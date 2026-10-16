using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;

namespace OniProfiler
{
	/// <summary>
	/// Simulation reconnaissance: call counters on its functions. Switched on by the file sim_probes.txt in the mod folder, which holds
	/// function names (one per line, "#" at the start of a line: comment). The totals are written to the log on game exit.
	/// The counters are set by the hook engine shared by our mods (src/Common/SimHooks.cs); they do not change the simulation itself.
	/// </summary>
	internal static class SimProbes
	{
		internal static string ListFile => Path.Combine(ModContext.Folder, "sim_probes.txt");

		internal static void Apply(Harmony harmony)
		{
			if (!SimHooks.Load())
			{
				throw new InvalidOperationException("sim hook library is not available: " + SimHooks.Info);
			}
			string list = ListFile;
			int added = 0, failed = 0;
			if (File.Exists(list))
			{
				foreach (string raw in File.ReadAllLines(list))
				{
					string name = raw.Trim();
					if (name.Length == 0 || name[0] == '#')
					{
						continue;
					}
					if (SimHooks.AddProbe(name, out string error))
					{
						added++;
					}
					else
					{
						failed++;
						Debug.LogWarning($"{ModContext.Tag} SimProbes: {name}: {error}");
					}
				}
			}
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)),
				prefix: new HarmonyMethod(typeof(SimProbes), nameof(LogStats)));
			Debug.Log($"{ModContext.Tag} SimProbes: {SimHooks.Info}; probes set: {added}, failed: {failed}");
		}

		private static void LogStats()
		{
			foreach (KeyValuePair<string, long> probe in SimHooks.ProbeCalls())
			{
				Debug.Log($"{ModContext.Tag} SimProbes: {probe.Key}: calls={probe.Value}");
			}
		}
	}
}
