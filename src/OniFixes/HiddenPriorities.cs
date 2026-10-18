using System;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// A geyser (anything Uncoverable) still buried in rock shows as a "Buried Object": it cannot be selected and its name is
	/// not given until a cell of it is dug free. The priority tool still set its priority, and its errand (to analyse it)
	/// then gave the name away. Here the priority tool leaves such an object alone until it is uncovered.
	/// Self-check: the file verify_priorities.txt in the mod folder: a few seconds after loading, an uncoverable object of the
	/// active world is made buried again for a moment and given a priority by the tool, then uncovered and given it again;
	/// its own state and priority are restored after.
	/// </summary>
	internal static class HiddenPriorities
	{
		internal static int refused;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(PrioritizeTool), "TryPrioritizeGameObject"), prefix: new HarmonyMethod(typeof(HiddenPriorities), nameof(NotWhileBuried)));
			if (File.Exists(Path.Combine(ModContext.Folder, "verify_priorities.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(HiddenPriorities), nameof(Verify)));
			}
		}

		private static bool NotWhileBuried(GameObject target, ref bool __result)
		{
			Uncoverable uncoverable = target != null ? target.GetComponent<Uncoverable>() : null;
			if (uncoverable == null || uncoverable.IsUncovered)
			{
				return true;
			}
			refused++;
			__result = false;
			return false;
		}

		private static float verifyStart = -1f;
		private static bool verified;

		private static void Verify()
		{
			if (verified || GameClock.Instance == null || ClusterManager.Instance == null || PrioritizeTool.Instance == null)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			if (verifyStart < 0f) verifyStart = now;
			if (now < verifyStart + 8f)
			{
				return;
			}
			verified = true;
			try
			{
				int world = ClusterManager.Instance.activeWorldId;
				Uncoverable found = null;
				foreach (Uncoverable u in UnityEngine.Object.FindObjectsByType<Uncoverable>(FindObjectsSortMode.None))
				{
					Prioritizable p = u.GetComponent<Prioritizable>();
					if (p != null && p.showIcon && p.IsPrioritizable() && u.GetMyWorldId() == world && u.IsUncovered)
					{
						found = u;
						break;
					}
				}
				if (found == null)
				{
					Debug.Log($"{ModContext.Tag} priorities check: skipped (no uncovered object with a priority to try)");
					return;
				}
				var buried = AccessTools.Field(typeof(Uncoverable), "hasBeenUncovered");
				var tryPrioritize = AccessTools.Method(typeof(PrioritizeTool), "TryPrioritizeGameObject");
				Prioritizable prioritizable = found.GetComponent<Prioritizable>();
				PrioritySetting before = prioritizable.GetMasterPriority(), probe = new PrioritySetting(PriorityScreen.PriorityClass.basic, before.priority_value == 9 ? 1 : 9);
				buried.SetValue(found, false);
				bool whileBuried = (bool)tryPrioritize.Invoke(PrioritizeTool.Instance, new object[] { found.gameObject, probe });
				bool kept = prioritizable.GetMasterPriority().priority_value == before.priority_value;
				buried.SetValue(found, true);
				bool uncovered = (bool)tryPrioritize.Invoke(PrioritizeTool.Instance, new object[] { found.gameObject, probe });
				bool changed = prioritizable.GetMasterPriority().priority_value == probe.priority_value;
				prioritizable.SetMasterPriority(before);
				bool ok = !whileBuried && kept && uncovered && changed;
				Debug.Log($"{ModContext.Tag} priorities check: {(ok ? "ok" : "FAILED")} ({found.GetProperName()}: buried set {whileBuried}, kept {kept}; uncovered set {uncovered}, changed {changed})");
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} priorities check: FAILED with {e}");
			}
		}
	}
}
