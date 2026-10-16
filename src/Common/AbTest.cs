using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using HarmonyLib;

namespace OniCommon
{
	/// <summary>
	/// A "before and after" comparison within a single game launch. The file ab_test.txt in the mod folder with the line
	/// "FixName period_in_seconds" makes that fix switch on and off in turns,
	/// while frame times are accumulated separately for each state and written to the log on exit.
	/// Several fixes joined with "+" are switched together: this measures their combined effect.
	/// This way the scene and the load are the same for both states, which two separate launches cannot achieve.
	///
	/// Pick a period that is not a multiple of 15 seconds (for example, 11): garbage collection and other stalls happen
	/// about every half minute, and with a period of 15 they hit the same phase time after time.
	/// </summary>
	internal static class AbTest
	{
		private static string target;
		private static readonly HashSet<string> targets = new HashSet<string>();
		private static bool on = true;
		private static long periodTicks, switchAt, lastStamp;
		private static bool lastFrameInGame;
		private static readonly List<float>[] frameMs = { new List<float>(8192), new List<float>(8192) };

		private struct AbTestFrame
		{
		}

		/// <summary>A fix that is not under comparison right now is always on.</summary>
		internal static bool IsOn(string fix)
		{
			return on || !targets.Contains(fix);
		}

		internal static void Init(Harmony harmony)
		{
			string file = Path.Combine(ModContext.Folder, "ab_test.txt");
			if (!File.Exists(file))
			{
				return;
			}
			string[] parts = File.ReadAllText(file).Split(new[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
			{
				return;
			}
			target = parts[0];
			targets.UnionWith(target.Split('+'));
			float seconds = 2f;
			if (parts.Length > 1)
			{
				float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
			}
			periodTicks = (long)((seconds > 0f ? seconds : 2f) * Stopwatch.Frequency);
			PlayerLoopUtil.InsertAtFrameStart(typeof(AbTestFrame), FrameStart);
			HarmonyMethod log = new HarmonyMethod(typeof(AbTest), nameof(LogResult));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)), prefix: log);
			Debug.Log($"{ModContext.Tag} A/B test of {target}, switching every {seconds} s");
		}

		private static void FrameStart()
		{
			long now = Stopwatch.GetTimestamp();
			if (lastFrameInGame && lastStamp != 0)
			{
				frameMs[on ? 1 : 0].Add((float)((now - lastStamp) * 1000.0 / Stopwatch.Frequency));
			}
			lastFrameInGame = Game.Instance != null && !Game.Instance.IsLoading() && !KMonoBehaviour.isLoadingScene;
			lastStamp = now;
			if (now >= switchAt)
			{
				on = !on;
				switchAt = now + periodTicks;
				if (lastFrameInGame)
				{
					// The same seconds as on the run's on-screen timer (the OniProfiler mod).
					Debug.Log($"{ModContext.Tag} A/B: T+{UnityEngine.Time.realtimeSinceStartup:F0} {(on ? "on" : "off")}");
				}
			}
		}

		private static void LogResult()
		{
			Debug.Log($"{ModContext.Tag} A/B {target}: off: {Summarize(frameMs[0])} | on: {Summarize(frameMs[1])}");
		}

		/// <summary>Median and mean without stalls (frames longer than 100 ms are almost always garbage collection).</summary>
		private static string Summarize(List<float> samples)
		{
			if (samples.Count == 0)
			{
				return "n=0";
			}
			List<float> sorted = new List<float>(samples);
			sorted.Sort();
			double sum = 0;
			int kept = 0;
			foreach (float ms in sorted)
			{
				if (ms < 100f)
				{
					sum += ms;
					kept++;
				}
			}
			// Long frames by buckets: shows whether the fix adds hitches that fall short of a "stall".
			int[] bins = new int[4];
			foreach (float ms in sorted)
			{
				if (ms >= 500f) bins[3]++;
				else if (ms >= 250f) bins[2]++;
				else if (ms >= 100f) bins[1]++;
				else if (ms >= 50f) bins[0]++;
			}
			return $"n={sorted.Count} median={sorted[sorted.Count / 2]:F2}ms p25={sorted[sorted.Count / 4]:F2} p75={sorted[sorted.Count * 3 / 4]:F2} mean<100ms={(kept > 0 ? sum / kept : 0):F2}ms hitches={sorted.Count - kept} " +
				$"frames 50-100ms={bins[0]} 100-250ms={bins[1]} 250-500ms={bins[2]} 500ms+={bins[3]}";
		}
	}
}
