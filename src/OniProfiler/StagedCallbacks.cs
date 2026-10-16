using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace OniProfiler
{
	/// <summary>
	/// Measures the per-frame handlers (Update, LateUpdate, FixedUpdate) of all game components.
	///
	/// Wrapping them all at once when the mod loads is not possible: the game crashes on the transition to the main menu, the cause
	/// was not found. So the wrappers are installed once in game and in batches: a new batch with every report window.
	/// The names of each batch are written to the log before installation, so after a crash it is clear which batch caused it,
	/// and the reports for the previous batches are already on disk. The wrappers are removed on game exit.
	///
	/// Switched on by the line "@unity-callbacks N" in the method list, N: batch size.
	/// </summary>
	internal static class StagedCallbacks
	{
		private sealed class Stat
		{
			public string name;
			public string message;
			public int batch;
			public long ticks, topTicks, calls, maxTicks;
		}

		private const string HarmonyId = "abyss.OniProfiler.staged";
		private const int MinIlBytes = 12;
		private static readonly string[] messages = { "Update", "LateUpdate", "FixedUpdate" };
		private static readonly double msPerTick = 1000.0 / Stopwatch.Frequency;

		private static int batchSize;
		// "@ui-callbacks" mode: UI handlers only, as a single batch in the first window.
		private static bool uiOnly;
		private static Harmony harmony;
		private static List<List<KeyValuePair<MethodBase, Stat>>> batches;
		private static readonly Dictionary<MethodBase, Stat> stats = new Dictionary<MethodBase, Stat>();
		private static int applied;
		// Handlers are called only from the main thread. The depth is needed so that the time of a base Update
		// called from a subclass is not counted twice.
		private static int depth;

		internal static bool Enabled => batchSize > 0;

		internal static void Configure(int size, bool onlyUi = false)
		{
			batchSize = size;
			uiOnly = onlyUi;
		}

		internal static void StartFrame()
		{
			depth = 0;
		}

		private static void Prepare()
		{
			harmony = new Harmony(HarmonyId);
			List<KeyValuePair<MethodBase, Stat>> all = new List<KeyValuePair<MethodBase, Stat>>();
			foreach (Assembly assembly in uiOnly ? new Assembly[0] : new[] { typeof(Game).Assembly, typeof(KMonoBehaviour).Assembly })
			{
				foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
				{
					if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.ContainsGenericParameters)
					{
						continue;
					}
					foreach (string message in messages)
					{
						MethodInfo method = type.GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
						if (Patchable(method))
						{
							all.Add(new KeyValuePair<MethodBase, Stat>(method, new Stat { name = type.Name + "." + message, message = message }));
						}
					}
				}
			}
			all.Sort((a, b) => string.CompareOrdinal(a.Value.name, b.Value.name));
			batches = new List<List<KeyValuePair<MethodBase, Stat>>>();
			for (int i = 0; i < all.Count; i += batchSize)
			{
				batches.Add(all.GetRange(i, Math.Min(batchSize, all.Count - i)));
			}
			// Managed Unity UI code goes in the last batch: it is the most suspicious.
			List<KeyValuePair<MethodBase, Stat>> ui = new List<KeyValuePair<MethodBase, Stat>>();
			AddUi(ui, "UnityEngine.EventSystems.EventSystem", "Update");
			AddUi(ui, "UnityEngine.UI.CanvasUpdateRegistry", "PerformUpdate");
			if (uiOnly)
			{
				AddUi(ui, "MultiToggle", "Update");
				AddUi(ui, "KScrollRect", "LateUpdate");
				AddUi(ui, "KScreenManager", "Update");
				AddUi(ui, "InterfaceTool", "LateUpdate");
				AddUi(ui, "LineLayer", "Update");
			}
			if (ui.Count > 0)
			{
				batches.Add(ui);
			}
			for (int b = 0; b < batches.Count; b++)
			{
				foreach (KeyValuePair<MethodBase, Stat> pair in batches[b])
				{
					pair.Value.batch = b + 1;
				}
			}
		}

		private static void AddUi(List<KeyValuePair<MethodBase, Stat>> list, string typeName, string methodName)
		{
			Type type = AccessTools.TypeByName(typeName);
			MethodInfo method = type != null ? AccessTools.DeclaredMethod(type, methodName) : null;
			if (Patchable(method))
			{
				list.Add(new KeyValuePair<MethodBase, Stat>(method, new Stat { name = type.Name + "." + methodName, message = "UI" }));
			}
		}

		private static bool Patchable(MethodInfo method)
		{
			if (method == null || method.IsAbstract || method.ContainsGenericParameters)
			{
				return false;
			}
			byte[] il = method.GetMethodBody()?.GetILAsByteArray();
			return il != null && il.Length >= MinIlBytes;
		}

		/// <summary>Installs the next batch of wrappers. Called at the start of every report window.</summary>
		internal static void ApplyNextBatch()
		{
			if (!Enabled)
			{
				return;
			}
			if (batches == null)
			{
				Prepare();
			}
			if (applied >= batches.Count)
			{
				return;
			}
			List<KeyValuePair<MethodBase, Stat>> batch = batches[applied];
			Debug.Log($"[OniProfiler] staged callbacks: applying batch {applied + 1}/{batches.Count}: {string.Join(", ", batch.Select(p => p.Value.name))}");
			HarmonyMethod prefix = new HarmonyMethod(typeof(StagedCallbacks), nameof(Prefix)) { priority = Priority.First };
			HarmonyMethod postfix = new HarmonyMethod(typeof(StagedCallbacks), nameof(Postfix)) { priority = Priority.Last };
			foreach (KeyValuePair<MethodBase, Stat> pair in batch)
			{
				try
				{
					stats[pair.Key] = pair.Value;
					harmony.Patch(pair.Key, prefix, postfix);
				}
				catch (Exception e)
				{
					stats.Remove(pair.Key);
					Debug.LogWarning($"[OniProfiler] staged callbacks: patch failed: {pair.Value.name}: {e.Message}");
				}
			}
			applied++;
			Debug.Log($"[OniProfiler] staged callbacks: batch {applied}/{batches.Count} applied");
		}

		/// <summary>The scene is about to change: in the previous attempt the game crashed exactly on a scene change with wrapped handlers.</summary>
		internal static void RemoveAll()
		{
			if (harmony != null && applied > 0)
			{
				harmony.UnpatchAll(HarmonyId);
				stats.Clear();
				Debug.Log("[OniProfiler] staged callbacks: removed");
			}
		}

		private static void Prefix(out long __state)
		{
			depth++;
			__state = Stopwatch.GetTimestamp();
		}

		private static void Postfix(long __state, MethodBase __originalMethod)
		{
			long elapsed = Stopwatch.GetTimestamp() - __state;
			if (depth > 0)
			{
				depth--;
			}
			if (__originalMethod != null && stats.TryGetValue(__originalMethod, out Stat stat))
			{
				stat.ticks += elapsed;
				stat.calls++;
				if (depth == 0)
				{
					stat.topTicks += elapsed;
				}
				if (elapsed > stat.maxTicks)
				{
					stat.maxTicks = elapsed;
				}
			}
		}

		internal static void ResetStats()
		{
			foreach (Stat stat in stats.Values)
			{
				stat.ticks = stat.topTicks = stat.calls = stat.maxTicks = 0;
			}
		}

		internal static void Append(StringBuilder sb, int frames, double avgFrame)
		{
			if (!Enabled || batches == null)
			{
				return;
			}
			sb.AppendLine();
			sb.AppendLine($"-- unity callbacks, staged: batches applied {applied}/{batches.Count}, methods {stats.Count}");
			sb.AppendLine("top-level time: " + string.Join(", ", stats.Values.GroupBy(s => s.message).OrderByDescending(g => g.Sum(s => s.topTicks))
				.Select(g => $"{g.Key}={g.Sum(s => s.topTicks) * msPerTick / frames:F2}ms")));
			sb.AppendLine($"{"ms/frame",9} {"%frame",6} {"calls/fr",9} {"avg_us",9} {"max_ms",8} {"batch",5}  name");
			foreach (Stat s in stats.Values.Where(s => s.calls > 0).OrderByDescending(s => s.ticks).Take(40))
			{
				double ms = s.ticks * msPerTick;
				sb.AppendLine($"{ms / frames,9:F3} {100.0 * ms / frames / avgFrame,6:F1} {(double)s.calls / frames,9:F1} {1000.0 * ms / s.calls,9:F1} {s.maxTicks * msPerTick,8:F2} {s.batch,5}  {s.name}");
			}
		}
	}
}
