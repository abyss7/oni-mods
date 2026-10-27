using System.IO;
using HarmonyLib;
using UnityEngine;

namespace OniProfiler
{
	/// <summary>
	/// Self-check runs at the game's highest speed (the file run_fast.txt in the mod folder, put there by the bench script's
	/// option -s): once the game is running, its speed is set to the fastest and kept there (unpaused). Checks that wait for
	/// game time take a third of the real time. Not for measuring frames.
	/// </summary>
	internal static class RunFast
	{
		internal static void Apply(Harmony harmony, string folder)
		{
			if (File.Exists(Path.Combine(folder, "run_fast.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(RunFast), nameof(Frame)));
				Debug.Log("[OniProfiler] run fast: the game runs at its highest speed");
			}
		}

		private static void Frame()
		{
			SpeedControlScreen speed = SpeedControlScreen.Instance;
			if (speed == null)
			{
				return;
			}
			if (speed.IsPaused) speed.Unpause(playSound: false);
			if (speed.GetSpeed() != 2) speed.SetSpeed(2);
		}
	}
}
