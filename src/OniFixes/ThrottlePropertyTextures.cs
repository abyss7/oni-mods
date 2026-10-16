using System;
using System.IO;
using HarmonyLib;
using UnityEngine;
using OniCommon;

namespace OniFixes
{
	/// <summary>
	/// The game updates nine cell property textures every frame, even though their source data changes
	/// much less often: the simulation hands over a new state once every 0.2 game seconds.
	///
	/// Five textures come entirely from the simulation (flow, liquids, materials, sunlight).
	/// We re-upload them to the GPU only after the simulation has handed over new data: no loss of accuracy.
	///
	/// Four textures are computed from the grid (fog of war, digging, mass for rendering and for light).
	/// We recompute them when simulation data has arrived, the camera has moved, the world has changed or
	/// the number of frames set in the options has passed. The last one catches changes from the game side (fog reveal, damage to
	/// cells) with a lag of no more than a few frames. Game logic does not read these textures.
	/// </summary>
	internal static class ThrottlePropertyTextures
	{
		private static int maxStaleFrames = 4;
		private const int PropertyCount = (int)PropertyTextures.Property.Num;

		private struct State
		{
			public bool primed;
			public int simVersion, frame, world, x0, y0, x1, y1;
			public ulong checksum;
		}

		private static bool applied;
		private static readonly State[] states = new State[PropertyCount];
		private static int simVersion = 1;
		private static long externalSeen, externalSkipped, computedSeen, computedSkipped;

		// Self-check: file verify_property_textures.txt in the mod folder enables comparing checksums
		// of the simulation textures on every skipped frame. A mismatch means the data changed
		// without a signal from the simulation, i.e. skipping would have been a mistake.
		private static bool verify;
		private static long verifyChecks, verifyMismatches;

		internal static bool Applied => applied;

		/// <summary>
		/// The decision is always computed, but applied only when the fix is enabled: this way in comparison
		/// mode statistics and the self-check run the same in both states.
		/// </summary>
		internal static bool ShouldSkip(ref PropertyTextures.TextureProperties p, int x0, int y0, int x1, int y1)
		{
			return !ShouldUpdate(ref p, x0, y0, x1, y1) && AbTest.IsOn(nameof(ThrottlePropertyTextures));
		}

		internal static void Apply(Harmony harmony)
		{
			maxStaleFrames = Math.Max(1, Mod.Options.PropertyTexturesMaxStaleFrames);
			verify = File.Exists(Path.Combine(ModContext.Folder, "verify_property_textures.txt"));
			PropertyTexturesPatch.Ensure(harmony);
			harmony.Patch(AccessTools.Method(typeof(Sim), nameof(Sim.HandleMessage)),
				postfix: new HarmonyMethod(typeof(ThrottlePropertyTextures), nameof(OnSimMessage)));
			HarmonyMethod reset = new HarmonyMethod(typeof(ThrottlePropertyTextures), nameof(Reset));
			harmony.Patch(AccessTools.Method(typeof(PropertyTextures), nameof(PropertyTextures.OnReset)), prefix: reset);
			harmony.Patch(AccessTools.Method(typeof(PropertyTextures), nameof(PropertyTextures.DestroyInstance)), prefix: reset);
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)),
				prefix: new HarmonyMethod(typeof(ThrottlePropertyTextures), nameof(LogStats)));
			applied = true;
		}

		/// <summary>The simulation hands the game a new state only in response to PrepareGameData and only if it has processed at least one frame of its own.</summary>
		private static unsafe void OnSimMessage(SimMessageHashes sim_msg_id, IntPtr __result)
		{
			if (sim_msg_id == SimMessageHashes.PrepareGameData && __result != IntPtr.Zero && ((Sim.GameDataUpdate*)__result)->numFramesProcessed > 0)
			{
				simVersion++;
			}
		}

		private static void Reset()
		{
			Array.Clear(states, 0, states.Length);
		}

		private static bool ShouldUpdate(ref PropertyTextures.TextureProperties p, int x0, int y0, int x1, int y1)
		{
			// The game already updates the other eight textures one per frame.
			if (!p.updateEveryFrame)
			{
				return true;
			}
			if (Game.Instance == null || Game.Instance.IsLoading() || GameUtil.IsCapturingTimeLapse())
			{
				return true;
			}
			int idx = (int)p.simProperty;
			ref State s = ref states[idx];
			int frame = Time.frameCount;
			bool update = !s.primed || s.simVersion != simVersion;
			if (p.updatedExternally)
			{
				externalSeen++;
				if (verify)
				{
					ulong checksum = Checksum(p.simProperty);
					if (!update)
					{
						verifyChecks++;
						if (checksum != s.checksum)
						{
							verifyMismatches++;
							update = true;
						}
					}
					s.checksum = checksum;
				}
				if (!update)
				{
					externalSkipped++;
					return false;
				}
			}
			else
			{
				computedSeen++;
				int world = ClusterManager.Instance != null ? ClusterManager.Instance.activeWorldId : -1;
				update = update || s.world != world || s.x0 != x0 || s.y0 != y0 || s.x1 != x1 || s.y1 != y1 || frame - s.frame >= maxStaleFrames;
				if (!update)
				{
					computedSkipped++;
					return false;
				}
				s.world = world;
				s.x0 = x0;
				s.y0 = y0;
				s.x1 = x1;
				s.y1 = y1;
			}
			s.primed = true;
			s.simVersion = simVersion;
			s.frame = frame;
			return true;
		}

		private static unsafe ulong Checksum(PropertyTextures.Property property)
		{
			int cells = Grid.WidthInCells * Grid.HeightInCells;
			IntPtr data;
			int bytes;
			switch (property)
			{
			case PropertyTextures.Property.Flow: data = PropertyTextures.externalFlowTex; bytes = 8 * cells; break;
			case PropertyTextures.Property.Liquid: data = PropertyTextures.externalLiquidTex; bytes = 4 * cells; break;
			case PropertyTextures.Property.LiquidData: data = PropertyTextures.externalLiquidDataTex; bytes = 4 * cells; break;
			case PropertyTextures.Property.MaterialData: data = PropertyTextures.externalMaterialDataTex; bytes = 4 * cells; break;
			case PropertyTextures.Property.ExposedToSunlight: data = PropertyTextures.externalExposedToSunlight; bytes = cells; break;
			default: return 0;
			}
			if (data == IntPtr.Zero)
			{
				return 0;
			}
			ulong hash = 1469598103934665603UL;
			ulong* words = (ulong*)data;
			int count = bytes / 8;
			for (int i = 0; i < count; i++)
			{
				hash = (hash ^ words[i]) * 1099511628211UL;
			}
			byte* tail = (byte*)data + count * 8;
			for (int i = 0; i < bytes - count * 8; i++)
			{
				hash = (hash ^ tail[i]) * 1099511628211UL;
			}
			return hash;
		}

		private static void LogStats()
		{
			if (externalSeen + computedSeen == 0)
			{
				return;
			}
			string text = $"[OniFixes] ThrottlePropertyTextures: sim textures skipped {externalSkipped}/{externalSeen} ({Percent(externalSkipped, externalSeen)}%), " +
				$"computed textures skipped {computedSkipped}/{computedSeen} ({Percent(computedSkipped, computedSeen)}%)";
			if (verify)
			{
				text += $"; verify: {verifyMismatches} mismatches in {verifyChecks} checks";
			}
			Debug.Log(text);
		}

		private static long Percent(long part, long whole)
		{
			return whole > 0 ? 100 * part / whole : 0;
		}
	}
}
