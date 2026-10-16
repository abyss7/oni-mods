using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace OniProfiler
{
	/// <summary>
	/// Hang watchdog. The main thread checks in at every frame stage; if in game there are no check-ins for longer
	/// than the threshold, a background thread writes to hang.txt (in the mod folder) and to the log which stage and which update
	/// bucket the frame got stuck in, plus the stacks of all managed threads.
	///
	/// The file watchdog.txt in the mod folder (placed by an automated test run) additionally allows killing
	/// the hung game: in an automated run it would otherwise hang full screen until the timeout. In normal play the watchdog
	/// only writes the report.
	/// </summary>
	internal static class Watchdog
	{
		private const double DefaultStallSeconds = 20;
		// The watchdog is not armed before this many frames in game: loading a save produces no frames.
		private const int ArmAfterFrames = 60;

		internal static volatile string Bucket;
		private static long lastBeat;
		private static volatile int framesInGame;
		private static string modPath;
		private static bool killOnHang;

		/// <summary>The game was started by an automated run (watchdog.txt is present): the run timer may be drawn.</summary>
		internal static bool BenchRun => killOnHang;
		private static double stallSeconds = DefaultStallSeconds;

		internal static void Start(string path)
		{
			modPath = path;
			string marker = Path.Combine(path, "watchdog.txt");
			if (File.Exists(marker))
			{
				killOnHang = true;
				if (double.TryParse(File.ReadAllText(marker).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds >= 5)
				{
					stallSeconds = seconds;
				}
			}
			new Thread(Watch) { IsBackground = true, Name = "OniProfiler.Watchdog" }.Start();
		}

		/// <summary>Main thread check-in: called at every frame stage.</summary>
		internal static void Beat()
		{
			Volatile.Write(ref lastBeat, Stopwatch.GetTimestamp());
		}

		/// <summary>Once per frame: whether we are in game. Outside the game (menu, loading) the watchdog is disarmed.</summary>
		internal static void Frame(bool inGame)
		{
			framesInGame = inGame ? framesInGame + 1 : 0;
		}

		private static void Watch()
		{
			while (true)
			{
				Thread.Sleep(1000);
				if (framesInGame < ArmAfterFrames)
				{
					continue;
				}
				double stalled = (Stopwatch.GetTimestamp() - Volatile.Read(ref lastBeat)) / (double)Stopwatch.Frequency;
				if (stalled < stallSeconds)
				{
					continue;
				}
				Report(stalled);
				return;
			}
		}

		private static void Report(double stalled)
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine($"[OniProfiler] HANG: main thread made no progress for {stalled:F0} s");
			sb.AppendLine($"frame stage: {LoopTimer.CurrentName}");
			sb.AppendLine($"update bucket: {Bucket ?? "none"}");
			string file = Path.Combine(modPath, "hang.txt");
			File.WriteAllText(file, sb.ToString());
			if (killOnHang)
			{
				// Taking stacks stops all threads and may itself never return: the game is killed in any case.
				new Thread(() =>
				{
					Thread.Sleep(10000);
					Process.GetCurrentProcess().Kill();
				}) { IsBackground = true }.Start();
			}
			try
			{
				MethodInfo method = typeof(Thread).GetMethod("Mono_GetStackTraces", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
				if (method == null)
				{
					sb.AppendLine("stack traces: Thread.Mono_GetStackTraces is not available");
				}
				else
				{
					foreach (DictionaryEntry entry in (IDictionary)method.Invoke(null, null))
					{
						Thread thread = (Thread)entry.Key;
						string trace = entry.Value.ToString();
						if (trace.Trim().Length == 0)
						{
							continue;
						}
						sb.AppendLine($"--- thread {thread.ManagedThreadId} \"{thread.Name}\"");
						sb.AppendLine(trace);
					}
				}
			}
			catch (Exception e)
			{
				sb.AppendLine($"stack traces failed: {e}");
			}
			File.WriteAllText(file, sb.ToString());
			UnityEngine.Debug.LogWarning(sb.ToString());
			if (killOnHang)
			{
				Thread.Sleep(1000);
				Process.GetCurrentProcess().Kill();
			}
		}
	}
}
