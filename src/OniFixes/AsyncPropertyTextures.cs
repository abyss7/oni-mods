using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using UnityEngine.LowLevel;
using OniCommon;

namespace OniFixes
{
	/// <summary>
	/// The game recomputes the cell property textures (fog of war, mass, temperature and others) in
	/// PropertyTextures.LateUpdate on worker threads, while the main thread stands and waits for the result.
	/// Here the computation moves to the background: frame N starts it and carries on (rendering, UI),
	/// frame N+1 uploads the finished result to the GPU. The image of these textures lags one frame behind;
	/// game logic does not read them.
	///
	/// The background threads read grid data that the simulation swaps during synchronisation,
	/// so the computation always finishes before the next frame begins and before any
	/// call into the simulation through Sim.HandleMessage.
	/// </summary>
	internal static class AsyncPropertyTextures
	{
		private delegate void Fill(TextureRegion region, int x0, int y0, int x1, int y1);

		private struct Job
		{
			public Fill fill;
			public TextureRegion region;
			public int x0, y0, x1, y1;
		}

		private const int WorkerCount = 2;
		private const int StripRows = 16;
		private const int PropertyCount = (int)PropertyTextures.Property.Num;

		private static readonly Fill[] fillers = new Fill[PropertyCount];
		// The first computation of each texture goes the stock way, so that it is not left empty for a frame.
		private static readonly bool[] primed = new bool[PropertyCount];

		// All queue state changes only under queueLock.
		private static readonly object queueLock = new object();
		private static Job[] jobs = new Job[256];
		private static int jobCount;
		private static int nextJob;
		private static int remaining;
		private static Exception jobError;

		// Main thread only.
		private static readonly List<TextureRegion> pending = new List<TextureRegion>(PropertyCount);
		private static int collecting;
		private static bool syncThisFrame;
		private static long frames, jobsTotal, frameStartWaits, simWaits, waitTicks;

		internal static bool Applied;

		private struct PropertyTexturesJoin
		{
		}

		internal static void Apply(Harmony harmony)
		{
			fillers[(int)PropertyTextures.Property.StateChange] = PropertyTextures.UpdateStateChange;
			fillers[(int)PropertyTextures.Property.GasPressure] = PropertyTextures.UpdatePressure;
			fillers[(int)PropertyTextures.Property.GasColour] = PropertyTextures.UpdateGasColour;
			fillers[(int)PropertyTextures.Property.GasDanger] = PropertyTextures.UpdateDanger;
			fillers[(int)PropertyTextures.Property.FogOfWar] = PropertyTextures.UpdateFogOfWar;
			fillers[(int)PropertyTextures.Property.SolidDigAmount] = PropertyTextures.UpdateSolidDigAmount;
			fillers[(int)PropertyTextures.Property.SolidLiquidGasMass] = PropertyTextures.UpdateSolidLiquidGasMass;
			fillers[(int)PropertyTextures.Property.SolidLiquidGasMassForLight] = PropertyTextures.UpdateSolidLiquidGasMassForLight;
			fillers[(int)PropertyTextures.Property.WorldLight] = PropertyTextures.UpdateWorldLight;
			fillers[(int)PropertyTextures.Property.Temperature] = PropertyTextures.UpdateTemperature;
			fillers[(int)PropertyTextures.Property.FallingSolid] = PropertyTextures.UpdateFallingSolidChange;
			fillers[(int)PropertyTextures.Property.Radiation] = PropertyTextures.UpdateRadiation;

			for (int i = 0; i < WorkerCount; i++)
			{
				new Thread(WorkerMain) { IsBackground = true, Name = "OniFixes.PropTex" + i }.Start();
			}
			PlayerLoopUtil.InsertAtFrameStart(typeof(PropertyTexturesJoin), JoinAtFrameStart);
			PropertyTexturesPatch.Ensure(harmony);

			harmony.Patch(AccessTools.Method(typeof(PropertyTextures), nameof(PropertyTextures.LateUpdate)),
				prefix: new HarmonyMethod(typeof(AsyncPropertyTextures), nameof(BeginFrame)),
				postfix: new HarmonyMethod(typeof(AsyncPropertyTextures), nameof(EndFrame)));
			harmony.Patch(AccessTools.Method(typeof(Sim), nameof(Sim.HandleMessage)),
				prefix: new HarmonyMethod(typeof(AsyncPropertyTextures), nameof(JoinBeforeSim)));
			HarmonyMethod discard = new HarmonyMethod(typeof(AsyncPropertyTextures), nameof(Discard));
			harmony.Patch(AccessTools.Method(typeof(PropertyTextures), nameof(PropertyTextures.OnReset)), prefix: discard);
			harmony.Patch(AccessTools.Method(typeof(PropertyTextures), nameof(PropertyTextures.DestroyInstance)), prefix: discard);
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.StopBE)), prefix: discard);
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)), prefix: discard);
			Applied = true;
		}

		private static void WorkerMain()
		{
			while (true)
			{
				int index;
				lock (queueLock)
				{
					while (nextJob >= jobCount)
					{
						Monitor.Wait(queueLock);
					}
					index = nextJob++;
				}
				RunJob(index);
			}
		}

		private static void RunJob(int index)
		{
			try
			{
				Job job = jobs[index];
				job.fill(job.region, job.x0, job.y0, job.x1, job.y1);
			}
			catch (Exception e)
			{
				jobError = jobError ?? e;
			}
			lock (queueLock)
			{
				if (--remaining == 0)
				{
					Monitor.PulseAll(queueLock);
				}
			}
		}

		/// <summary>Waits for the background computation to finish, helping to run the remaining tasks. Returns true if it had to wait.</summary>
		private static bool WaitAll()
		{
			bool waited = false;
			long start = 0;
			while (true)
			{
				int index;
				lock (queueLock)
				{
					if (remaining == 0)
					{
						break;
					}
					if (!waited)
					{
						waited = true;
						start = Stopwatch.GetTimestamp();
					}
					if (nextJob >= jobCount)
					{
						Monitor.Wait(queueLock);
						continue;
					}
					index = nextJob++;
				}
				RunJob(index);
			}
			if (waited)
			{
				waitTicks += Stopwatch.GetTimestamp() - start;
			}
			return waited;
		}

		/// <summary>As the first stage of the frame we wait for the background computation: after that the game may change and free grid data.</summary>
		private static void JoinAtFrameStart()
		{
			if (WaitAll())
			{
				frameStartWaits++;
			}
		}

		private static void JoinBeforeSim()
		{
			if (WaitAll())
			{
				simWaits++;
			}
		}

		/// <summary>The scene or texture buffers are being recreated: the results of the unfinished frame have nowhere to be uploaded.</summary>
		private static void Discard()
		{
			WaitAll();
			if (frames > 0)
			{
				LogStats();
			}
			pending.Clear();
			Array.Clear(primed, 0, primed.Length);
			lock (queueLock)
			{
				jobCount = 0;
				nextJob = 0;
			}
			collecting = 0;
		}

		private static void BeginFrame()
		{
			WaitAll();
			if (jobError != null)
			{
				Debug.LogWarning("[OniFixes] AsyncPropertyTextures: fill failed in background: " + jobError);
				jobError = null;
			}
			// Uploading to the GPU is allowed only from the main thread.
			for (int i = 0; i < pending.Count; i++)
			{
				pending[i].Unlock();
			}
			pending.Clear();
			lock (queueLock)
			{
				jobCount = 0;
				nextJob = 0;
			}
			// When capturing a timelapse the game updates all textures at once and immediately takes the shot: no lag allowed.
			syncThisFrame = !AbTest.IsOn(nameof(AsyncPropertyTextures)) || GameUtil.IsCapturingTimeLapse();
			collecting = 0;
			if (++frames % 3600 == 0)
			{
				LogStats();
			}
		}

		internal static bool UpdateProperty(PropertyTextures instance, ref PropertyTextures.TextureProperties p, int x0, int y0, int x1, int y1)
		{
			if (p.updatedExternally)
			{
				return true;
			}
			int idx = (int)p.simProperty;
			Fill fill = fillers[idx];
			if (fill == null || syncThisFrame || !primed[idx])
			{
				primed[idx] = true;
				return true;
			}
			if (Game.Instance == null || Game.Instance.IsLoading())
			{
				return false;
			}
			TextureRegion region = instance.textureBuffers[idx].Lock(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
			pending.Add(region);
			for (int y = y0; y <= y1; y += StripRows)
			{
				if (collecting == jobs.Length)
				{
					Array.Resize(ref jobs, jobs.Length * 2);
				}
				jobs[collecting++] = new Job { fill = fill, region = region, x0 = x0, y0 = y, x1 = x1, y1 = Math.Min(y + StripRows - 1, y1) };
			}
			return false;
		}

		private static void EndFrame()
		{
			if (collecting == 0)
			{
				return;
			}
			jobsTotal += collecting;
			lock (queueLock)
			{
				jobCount = collecting;
				nextJob = 0;
				remaining = collecting;
				Monitor.PulseAll(queueLock);
			}
			collecting = 0;
		}

		private static void LogStats()
		{
			double waitMs = waitTicks * 1000.0 / Stopwatch.Frequency;
			Debug.Log($"[OniFixes] AsyncPropertyTextures: frames={frames}, jobs/frame={(double)jobsTotal / frames:F1}, " +
				$"waited at frame start {frameStartWaits} times, before sim {simWaits} times, total wait {waitMs:F1} ms ({waitMs / frames:F3} ms/frame)");
		}
	}
}
