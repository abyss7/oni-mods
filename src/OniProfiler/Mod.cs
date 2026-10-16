using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KMod;
using OniCommon;
using UnityEngine;
using UnityEngine.LowLevel;

namespace OniProfiler
{
	public sealed class Mod : UserMod2
	{
		public override void OnLoad(Harmony harmony)
		{
			base.OnLoad(harmony);
			ModContext.Folder = path;
			ModContext.Name = nameof(OniProfiler);
			Report.Init(path);
			NativeProbe.Init(path);
			MethodTimer.PatchAll(harmony, Path.Combine(path, "profile_methods.txt"));
			LoopTimer.Install();
			Watchdog.Start(path);
			if (StagedCallbacks.Enabled)
			{
				HarmonyMethod remove = new HarmonyMethod(typeof(StagedCallbacks), nameof(StagedCallbacks.RemoveAll));
				harmony.Patch(AccessTools.Method(typeof(Game), "StopBE"), prefix: remove);
				harmony.Patch(AccessTools.Method(typeof(Game), "OnApplicationQuit"), prefix: remove);
			}
			// Simulation reconnaissance is switched on by a file in the mod folder and does not affect the profiler itself.
			if (File.Exists(SimProbes.ListFile))
			{
				try
				{
					SimProbes.Apply(harmony);
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[OniProfiler] {nameof(SimProbes)} failed to apply: {e}");
				}
			}
			GameObject go = new GameObject("OniProfiler");
			UnityEngine.Object.DontDestroyOnLoad(go);
			go.AddComponent<FrameSampler>();
			Debug.Log($"[OniProfiler] loaded, timed methods: {MethodTimer.Stats.Count}, player loop stages: {LoopTimer.Names.Length}, report: {Report.FilePath}");
		}
	}

	/// <summary>
	/// Breakdown of the whole main-thread frame by engine stage without a single patch: timestamps are inserted
	/// between neighbouring PlayerLoop stages. The sum over all stages equals the frame time.
	/// </summary>
	internal static class LoopTimer
	{
		internal static string[] Names = new string[0];
		internal static long[] Ticks = new long[0];
		// Longest stay in a stage within the window: shows which stage the hitches sit in.
		internal static long[] MaxTicks = new long[0];
		private static int current = -1;
		private static long stamp;

		internal static void Install()
		{
			PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
			PlayerLoopSystem[] phases = root.subSystemList;
			if (phases == null)
			{
				Debug.LogWarning("[OniProfiler] player loop has no phases, stage timing disabled");
				return;
			}
			List<string> names = new List<string>();
			for (int p = 0; p < phases.Length; p++)
			{
				PlayerLoopSystem phase = phases[p];
				string phaseName = phase.type != null ? phase.type.Name : "Phase" + p;
				PlayerLoopSystem[] stages = phase.subSystemList ?? new PlayerLoopSystem[0];
				List<PlayerLoopSystem> rebuilt = new List<PlayerLoopSystem>(stages.Length * 2 + 1);
				foreach (PlayerLoopSystem stage in stages)
				{
					rebuilt.Add(Marker(names.Count));
					names.Add(phaseName + "." + (stage.type != null ? stage.type.Name : "?"));
					rebuilt.Add(stage);
				}
				// Time from the end of a phase to the start of the next one: engine work between phases.
				rebuilt.Add(Marker(names.Count));
				names.Add(phaseName + ".<between phases>");
				phase.subSystemList = rebuilt.ToArray();
				phases[p] = phase;
			}
			root.subSystemList = phases;
			Names = names.ToArray();
			Ticks = new long[Names.Length];
			MaxTicks = new long[Names.Length];
			PlayerLoop.SetPlayerLoop(root);
		}

		private static long frameMaxTicks;
		private static int frameMaxSlot = -1;

		/// <summary>Longest stage since the previous call (called once per frame): where the hitch sat.</summary>
		internal static string TakeLongestStage()
		{
			string name = frameMaxSlot >= 0 && frameMaxSlot < Names.Length ? Names[frameMaxSlot] : "unknown";
			frameMaxTicks = 0;
			frameMaxSlot = -1;
			return name;
		}

		internal static string CurrentName => current >= 0 && current < Names.Length ? Names[current] : "none";

		private static PlayerLoopSystem Marker(int slot)
		{
			return new PlayerLoopSystem { type = typeof(LoopTimer), updateDelegate = () => Mark(slot) };
		}

		private static void Mark(int slot)
		{
			long now = Stopwatch.GetTimestamp();
			if (current >= 0)
			{
				long spent = now - stamp;
				Ticks[current] += spent;
				if (spent > frameMaxTicks)
				{
					frameMaxTicks = spent;
					frameMaxSlot = current;
				}
				if (spent > MaxTicks[current])
				{
					MaxTicks[current] = spent;
				}
			}
			current = slot;
			stamp = now;
			Watchdog.Beat();
		}

		internal static void Reset()
		{
			Array.Clear(Ticks, 0, Ticks.Length);
			Array.Clear(MaxTicks, 0, MaxTicks.Length);
		}
	}

	/// <summary>Times the methods from profile_methods.txt with one shared prefix/postfix pair.</summary>
	internal static class MethodTimer
	{
		internal sealed class Stat
		{
			public string name;
			public long ticks;
			public long calls;
			public long maxTicks;
		}

		// Methods that are too short are left alone: 12 bytes of IL was verified in game (GameScheduler.Update).
		private const int MinIlBytes = 12;

		// Filled once at startup and only read afterwards: safe from any thread.
		internal static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();

		internal static void PatchAll(Harmony harmony, string listFile)
		{
			if (!File.Exists(listFile))
			{
				Debug.LogWarning("[OniProfiler] no method list at " + listFile);
				return;
			}
			HarmonyMethod prefix = new HarmonyMethod(typeof(MethodTimer), nameof(Prefix)) { priority = Priority.First };
			HarmonyMethod postfix = new HarmonyMethod(typeof(MethodTimer), nameof(Postfix)) { priority = Priority.Last };
			foreach (string raw in File.ReadAllLines(listFile))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line[0] == '#')
				{
					continue;
				}
				if (line.StartsWith("@kanim-counters"))
				{
					Report.KAnimCounters = true;
					continue;
				}
				if (line.StartsWith("@native-probe"))
				{
					Report.NativeProbeOnce = true;
					continue;
				}
				if (line.StartsWith("@ui-callbacks"))
				{
					StagedCallbacks.Configure(45, onlyUi: true);
					continue;
				}
				if (line.StartsWith("@unity-callbacks"))
				{
					string[] words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
					StagedCallbacks.Configure(words.Length > 1 && int.TryParse(words[1], out int size) && size > 0 ? size : 45);
					continue;
				}
				string[] parts = line.Split(':');
				Type type = parts.Length == 2 ? AccessTools.TypeByName(parts[0]) : null;
				if (type == null)
				{
					Debug.LogWarning("[OniProfiler] type not found: " + line);
					continue;
				}
				List<MethodInfo> methods = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == parts[1]).ToList();
				if (methods.Count == 0)
				{
					Debug.LogWarning("[OniProfiler] method not found: " + line);
				}
				foreach (MethodInfo method in methods)
				{
					byte[] il = method.IsAbstract ? null : method.GetMethodBody()?.GetILAsByteArray();
					if (il == null || method.ContainsGenericParameters)
					{
						Debug.LogWarning("[OniProfiler] cannot patch (abstract/generic/extern): " + line);
						continue;
					}
					if (il.Length < MinIlBytes)
					{
						Debug.LogWarning("[OniProfiler] cannot patch (method body too small): " + line);
						continue;
					}
					try
					{
						string label = type.Name + "." + method.Name;
						if (methods.Count > 1)
						{
							label += "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";
						}
						Stats[method] = new Stat { name = label };
						harmony.Patch(method, prefix, postfix);
					}
					catch (Exception e)
					{
						Stats.Remove(method);
						Debug.LogWarning("[OniProfiler] patch failed: " + line + ": " + e.Message);
					}
				}
			}
		}

		private static void Prefix(out long __state)
		{
			__state = Stopwatch.GetTimestamp();
		}

		private static void Postfix(long __state, MethodBase __originalMethod)
		{
			long elapsed = Stopwatch.GetTimestamp() - __state;
			if (__originalMethod != null && Stats.TryGetValue(__originalMethod, out Stat stat))
			{
				System.Threading.Interlocked.Add(ref stat.ticks, elapsed);
				System.Threading.Interlocked.Increment(ref stat.calls);
				if (elapsed > stat.maxTicks)
				{
					stat.maxTicks = elapsed;
				}
			}
		}
	}

	/// <summary>
	/// All ISim*/IRender* components and state machine Update callbacks go through BucketGroup.AdvanceOneSubTick.
	/// We repeat its loop exactly, adding a measurement around each bucket.
	/// </summary>
	[HarmonyPatch(typeof(StateMachineUpdater.BucketGroup), nameof(StateMachineUpdater.BucketGroup.AdvanceOneSubTick))]
	internal static class BucketTimer
	{
		internal sealed class Stat
		{
			public string name;
			public long ticks;
			public long calls;
			public long maxTicks;
			public long entries;
		}

		internal static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>();
		private static readonly Dictionary<StateMachineUpdater.BaseUpdateBucket, Stat> byBucket = new Dictionary<StateMachineUpdater.BaseUpdateBucket, Stat>();

		private static readonly AccessTools.FieldRef<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>> bucketFrames =
			AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, List<List<StateMachineUpdater.BaseUpdateBucket>>>("bucketFrames");
		private static readonly AccessTools.FieldRef<StateMachineUpdater.BucketGroup, int> nextUpdateIndex =
			AccessTools.FieldRefAccess<StateMachineUpdater.BucketGroup, int>("nextUpdateIndex");

		private static bool Prefix(StateMachineUpdater.BucketGroup __instance, float dt)
		{
			List<List<StateMachineUpdater.BaseUpdateBucket>> frames = bucketFrames(__instance);
			ref int index = ref nextUpdateIndex(__instance);
			List<StateMachineUpdater.BaseUpdateBucket> list = frames[index];
			int count = list.Count;
			for (int i = 0; i < count; i++)
			{
				StateMachineUpdater.BaseUpdateBucket bucket = list[i];
				int entries = bucket.count;
				if (entries != 0)
				{
					Watchdog.Bucket = bucket.name;
					long start = Stopwatch.GetTimestamp();
					bucket.Update(dt);
					long elapsed = Stopwatch.GetTimestamp() - start;
					Watchdog.Bucket = null;
					if (!byBucket.TryGetValue(bucket, out Stat stat))
					{
						string key = __instance.updateRate + " " + bucket.name;
						if (!Stats.TryGetValue(key, out stat))
						{
							Stats[key] = stat = new Stat { name = key };
						}
						byBucket[bucket] = stat;
					}
					stat.ticks += elapsed;
					stat.calls++;
					stat.entries += entries;
					if (elapsed > stat.maxTicks)
					{
						stat.maxTicks = elapsed;
					}
				}
			}
			index = (index + 1) % frames.Count;
			return false;
		}

		internal static void Reset()
		{
			foreach (Stat stat in Stats.Values)
			{
				stat.ticks = stat.calls = stat.maxTicks = stat.entries = 0;
			}
		}

		/// <summary>Buckets are recreated when a save is loaded: the old references are no longer needed.</summary>
		internal static void Forget()
		{
			byBucket.Clear();
			Stats.Clear();
		}
	}

	[HarmonyPatch(typeof(StateMachineUpdater), nameof(StateMachineUpdater.Clear))]
	internal static class BucketTimerClear
	{
		private static void Postfix()
		{
			BucketTimer.Forget();
		}
	}

	internal sealed class FrameSampler : MonoBehaviour
	{
		private const float ReportIntervalSeconds = 30f;
		// After a save loads, the game spends several seconds finishing loading and recalculating everything: keep that out of the measurements.
		private const float WarmupSeconds = 15f;

		private readonly List<float> frameMs = new List<float>(4096);
		private float windowStart;
		private int gcStart;
		private bool wasInGame;
		private bool collecting;

		// On-screen run timer: seconds since game start. The same seconds appear in the report's hitch list
		// and in the "before and after" comparison lines, so a moment noticed by eye can be found in the logs.
		private GUIStyle timerStyle;
		private string timerText = "";
		private int timerSecond = -1, gcSeen;
		private long heapSeen;
		private bool timerShotTaken, gcPreviousFrame, skipFrame;

		private void Awake()
		{
			useGUILayout = false; // without layout OnGUI is called only for repaint and costs almost nothing
		}

		private void OnGUI()
		{
			if (!Watchdog.BenchRun || UnityEngine.Event.current.type != UnityEngine.EventType.Repaint)
			{
				return;
			}
			int second = (int)Time.realtimeSinceStartup;
			if (second != timerSecond)
			{
				timerSecond = second;
				timerText = "T+" + second;
			}
			float scale = Screen.height / 1080f;
			if (timerStyle == null)
			{
				timerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
				timerStyle.normal.textColor = Color.yellow;
			}
			timerStyle.fontSize = (int)(34 * scale);
			// Free space in the top bar, between the colony stats and the control buttons.
			Rect box = new Rect(Screen.width * 0.30f, 6 * scale, 170 * scale, 46 * scale);
			GUI.color = new Color(0f, 0f, 0f, 0.8f);
			GUI.DrawTexture(box, Texture2D.whiteTexture);
			GUI.color = Color.white;
			GUI.Label(box, timerText, timerStyle);
		}

		private void Update()
		{
			StagedCallbacks.StartFrame();
			bool inGame = Game.Instance != null && !KMonoBehaviour.isLoadingScene;
			Watchdog.Frame(inGame);
			float now = Time.realtimeSinceStartup;
			if (inGame != wasInGame)
			{
				wasInGame = inGame;
				collecting = false;
				windowStart = now + WarmupSeconds;
				return;
			}
			if (!inGame || now < windowStart)
			{
				return;
			}
			if (!collecting)
			{
				// The first frame of the window only resets the counters: its own duration belongs to the previous window.
				collecting = true;
				StagedCallbacks.ApplyNextBatch();
				ResetWindow(now);
				return;
			}
			float frame = Time.unscaledDeltaTime * 1000f;
			// Managed memory allocated during the frame: heap growth since the previous frame (after a collection the heap is smaller,
			// such a frame is not added to the sum). The total over the window shows how often the game will have to collect garbage.
			long heapNow = GC.GetTotalMemory(false);
			if (heapNow > heapSeen)
			{
				Report.AllocatedBytes += heapNow - heapSeen;
			}
			heapSeen = heapNow;
			string longest = LoopTimer.TakeLongestStage();
			int gcNow = GC.CollectionCount(0);
			// A collection may have run before or after this handler within its frame, so we look at two neighbouring frames.
			bool gcThisFrame = gcNow != gcSeen;
			bool gc = gcThisFrame || gcPreviousFrame;
			gcPreviousFrame = gcThisFrame;
			gcSeen = gcNow;
			if (skipFrame)
			{
				// The previous frame wrote the report: its duration belongs to the profiler, not to the game.
				skipFrame = false;
				return;
			}
			frameMs.Add(frame);
			if (frame >= Report.LongFrameMs)
			{
				Report.LongFrames.Add($"T+{now:F0} {frame:F0}ms {longest}{(gc ? " gc" : "")}");
			}
			if (Watchdog.BenchRun && !timerShotTaken && now - windowStart >= 5f)
			{
				// One screenshot per run: it shows that the timer is actually drawn and covers nothing.
				timerShotTaken = true;
				ScreenCapture.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(Report.FilePath), "timer_check.png"));
			}
			if (now - windowStart >= ReportIntervalSeconds)
			{
				Report.Write(frameMs, now - windowStart, GC.CollectionCount(0) - gcStart);
				StagedCallbacks.ApplyNextBatch();
				ResetWindow(now);
				skipFrame = true;
			}
		}

		private void OnApplicationQuit()
		{
			Watchdog.Frame(false);
			// An incomplete window on game exit is still useful if it holds at least a few seconds.
			float now = Time.realtimeSinceStartup;
			if (collecting && frameMs.Count > 0 && now - windowStart >= 5f)
			{
				Report.Write(frameMs, now - windowStart, GC.CollectionCount(0) - gcStart);
				collecting = false;
			}
		}

		private void ResetWindow(float now)
		{
			frameMs.Clear();
			windowStart = now;
			Report.AllocatedBytes = 0;
			heapSeen = GC.GetTotalMemory(false);
			gcStart = GC.CollectionCount(0);
			BucketTimer.Reset();
			LoopTimer.Reset();
			StagedCallbacks.ResetStats();
			foreach (MethodTimer.Stat stat in MethodTimer.Stats.Values)
			{
				stat.ticks = stat.calls = stat.maxTicks = 0;
			}
		}
	}

	internal static class Report
	{
		internal static string FilePath;
		private const int TopBuckets = 40;
		private const int TopStages = 25;
		internal const float HitchMs = 100f;
		// The long frame list also gets stutters shorter than a "hitch": they are noticeable by eye too.
		internal const float LongFrameMs = 80f;
		internal static readonly List<string> LongFrames = new List<string>();
		// The expensive report sections are switched on by the lines "@kanim-counters" and "@native-probe" in the method list.
		// The animation counters walk all scene objects and cost about half a second: when they were on by default
		// the profiler itself caused a hitch every 30 seconds.
		internal static bool KAnimCounters, NativeProbeOnce;
		private static readonly double msPerTick = 1000.0 / Stopwatch.Frequency;

		internal static void Init(string modPath)
		{
			FilePath = Path.Combine(modPath, "profile.log");
			File.WriteAllText(FilePath, $"# OniProfiler, session {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}, build {BuildWatermark.GetBuildText()}\n");
		}

		/// <summary>Managed memory allocated during the window (counted by FrameSampler).</summary>
		internal static long AllocatedBytes;

		internal static void Write(List<float> frameMs, float seconds, int gcCount)
		{
			try
			{
				long reportStart = Stopwatch.GetTimestamp();
				var sb = new System.Text.StringBuilder(16384);
				int frames = frameMs.Count;
				List<float> sorted = new List<float>(frameMs);
				sorted.Sort();
				double avgFrame = frameMs.Average();
				sb.AppendLine();
				sb.AppendLine($"===== {System.DateTime.Now:HH:mm:ss} window {seconds:F1}s =====");
				sb.AppendLine($"context: cycle={GameClock.Instance?.GetCycle()} speed={SpeedControlScreen.Instance?.GetSpeed()} timeScale={Time.timeScale:F2}" +
					$" dupes={Components.LiveMinionIdentities.Count} brains={Components.Brains.Count} buildings={Components.BuildingCompletes.Count} pickupables={Components.Pickupables.Count}" +
					$" cells={Grid.CellCount} worlds={ClusterManager.Instance?.WorldContainers.Count}");
				sb.AppendLine($"video: {Screen.width}x{Screen.height} vsync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate} refresh={Screen.currentResolution.refreshRateRatio.value:F0}Hz");
				sb.AppendLine($"frames: n={frames} fps={frames / seconds:F1} avg={avgFrame:F2}ms p50={Pct(sorted, 0.50):F2} p95={Pct(sorted, 0.95):F2} p99={Pct(sorted, 0.99):F2} max={sorted[frames - 1]:F2}" +
					$" gc={gcCount} heap={GC.GetTotalMemory(false) / 1048576}MB alloc={AllocatedBytes / 1048576.0 / Math.Max(seconds, 0.001f):F1}MB/s");
				List<float> hitches = sorted.Where(ms => ms >= HitchMs).ToList();
				sb.AppendLine($"hitches (>= {HitchMs:F0}ms): n={hitches.Count} total={hitches.Sum() / 1000f:F2}s");
				if (LongFrames.Count > 0)
				{
					// Time as on the on-screen run timer (seconds since game start), duration, the longest stage of the frame.
					sb.AppendLine($"long frames (>= {LongFrameMs:F0}ms): " + string.Join("; ", LongFrames));
					LongFrames.Clear();
				}

				double loopTotal = LoopTimer.Ticks.Sum() * msPerTick;
				sb.AppendLine();
				sb.AppendLine($"-- frame stages (stages do not overlap; sum {loopTotal / frames:F2} ms/frame of {avgFrame:F2})");
				sb.AppendLine($"{"ms/frame",9} {"%frame",6} {"max_ms",8}  stage");
				foreach (int i in Enumerable.Range(0, LoopTimer.Names.Length).Where(i => LoopTimer.Ticks[i] > 0).OrderByDescending(i => LoopTimer.Ticks[i]).Take(TopStages))
				{
					double ms = LoopTimer.Ticks[i] * msPerTick;
					sb.AppendLine($"{ms / frames,9:F3} {100.0 * ms / frames / avgFrame,6:F1} {LoopTimer.MaxTicks[i] * msPerTick,8:F2}  {LoopTimer.Names[i]}");
				}

				sb.AppendLine();
				sb.AppendLine("-- methods (inclusive time; nested in stages and in each other, do not sum)");
				sb.AppendLine($"{"ms/frame",9} {"%frame",6} {"calls",9} {"avg_us",9} {"max_ms",8}  name");
				foreach (MethodTimer.Stat s in MethodTimer.Stats.Values.Where(s => s.calls > 0).OrderByDescending(s => s.ticks))
				{
					double ms = s.ticks * msPerTick;
					sb.AppendLine($"{ms / frames,9:F3} {100.0 * ms / frames / avgFrame,6:F1} {s.calls,9} {1000.0 * ms / s.calls,9:F1} {s.maxTicks * msPerTick,8:F2}  {s.name}");
				}

				double bucketTotal = BucketTimer.Stats.Values.Sum(s => s.ticks) * msPerTick;
				sb.AppendLine();
				sb.AppendLine($"-- update buckets, top {TopBuckets} of {BucketTimer.Stats.Values.Count(s => s.calls > 0)}, total {bucketTotal / frames:F3} ms/frame ({100.0 * bucketTotal / frames / avgFrame:F1}% frame)");
				sb.AppendLine($"{"ms/frame",9} {"%frame",6} {"calls",9} {"avg_us",9} {"max_ms",8} {"entries",8}  name");
				foreach (BucketTimer.Stat s in BucketTimer.Stats.Values.Where(s => s.calls > 0).OrderByDescending(s => s.ticks).Take(TopBuckets))
				{
					double ms = s.ticks * msPerTick;
					sb.AppendLine($"{ms / frames,9:F3} {100.0 * ms / frames / avgFrame,6:F1} {s.calls,9} {1000.0 * ms / s.calls,9:F1} {s.maxTicks * msPerTick,8:F2} {s.entries / s.calls,8}  {s.name}");
				}
				StagedCallbacks.Append(sb, frames, avgFrame);
				if (KAnimCounters)
				{
					AppendKAnimCounters(sb);
				}
				if (NativeProbeOnce)
				{
					NativeProbe.AppendOnce(sb);
				}
				// Cost of the report itself: it is written on the main thread, and the frame it happens in is longer than usual.
				sb.AppendLine();
				sb.AppendLine($"report built in {(Stopwatch.GetTimestamp() - reportStart) * 1000.0 / Stopwatch.Frequency:F1} ms (this frame is left out of the next window)");
				File.AppendAllText(FilePath, sb.ToString());
			}
			catch (Exception e)
			{
				Debug.LogWarning("[OniProfiler] report failed: " + e);
			}
		}

		/// <summary>Snapshot of the animation system at report time: how much is walked and drawn per frame.</summary>
		private static void AppendKAnimCounters(System.Text.StringBuilder sb)
		{
			try
			{
				KAnimBatchManager manager = KAnimBatchManager.Instance();
				int sets = 0, batches = 0, drawn = 0, drawnWithoutVisible = 0, controllers = 0, visible = 0;
				foreach (BatchSet set in manager.activeBatchSets)
				{
					sets++;
					for (int i = 0; i < set.batchCount; i++)
					{
						KAnimBatch batch = set.GetBatch(i);
						batches++;
						int visibleHere = 0;
						foreach (KAnimConverter.IAnimConverter controller in batch.Controllers)
						{
							controllers++;
							if (controller != null && controller.IsVisible())
							{
								visibleHere++;
							}
						}
						visible += visibleHere;
						if (batch.size != 0 && batch.active && batch.materialType != KAnimBatchGroup.MaterialType.UI)
						{
							drawn++;
							if (visibleHere == 0)
							{
								drawnWithoutVisible++;
							}
						}
					}
				}
				KBatchedAnimUpdater updater = Singleton<KBatchedAnimUpdater>.Instance;
				KBatchedAnimTracker[] trackers = UnityEngine.Object.FindObjectsByType<KBatchedAnimTracker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
				KBatchedAnimCanvasRenderer[] canvasRenderers = UnityEngine.Object.FindObjectsByType<KBatchedAnimCanvasRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
				sb.AppendLine();
				sb.AppendLine("-- kanim counters (snapshot at report time)");
				sb.AppendLine($"batch sets: all={manager.batchSets.Count} active={sets}; batches in active sets={batches}, drawn={drawn}, drawn with no visible controller={drawnWithoutVisible}");
				sb.AppendLine($"controllers in active batches={controllers}, visible={visible}; update list={updater.updateList.Count}, always-update list={updater.alwaysUpdateList.Count}, moving={updater.movingControllerInfos.Count}");
				sb.AppendLine($"trackers: all={trackers.Length}, enabled={trackers.Count(t => t.isActiveAndEnabled)}; ui canvas renderers: all={canvasRenderers.Length}, enabled={canvasRenderers.Count(r => r.isActiveAndEnabled)}");
			}
			catch (Exception e)
			{
				sb.AppendLine("-- kanim counters failed: " + e.Message);
			}
		}

		private static float Pct(List<float> sorted, double p)
		{
			return sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
		}
	}
}
