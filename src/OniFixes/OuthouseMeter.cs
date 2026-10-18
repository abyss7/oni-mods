using System;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// The fill meter of an outhouse stays full after cleaning until the next visit: when the cleaning ends, the game sets the
	/// meter by the number of uses before it clears that number (it clears it a moment later, as the outhouse goes back to
	/// empty), and nothing sets the meter again. Here the meter follows the cleared number on entering the empty state.
	/// Self-check: the file verify_outhouse.txt in the mod folder: a few seconds after loading, an outhouse is put in the
	/// active world with its meter full, sent to the empty state as after a cleaning, and its meter is read.
	/// </summary>
	internal static class OuthouseMeter
	{
		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Toilet.States), nameof(Toilet.States.InitializeStates)), postfix: new HarmonyMethod(typeof(OuthouseMeter), nameof(Postfix)));
			if (File.Exists(Path.Combine(ModContext.Folder, "verify_outhouse.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(OuthouseMeter), nameof(Verify)));
			}
		}

		private static int verifyStep;
		private static float verifyStart = -1f;
		private static Toilet test;

		private static void Verify()
		{
			if (verifyStep > 2 || GameClock.Instance == null || ClusterManager.Instance == null)
			{
				return;
			}
			try
			{
				float now = GameClock.Instance.GetTime();
				if (verifyStart < 0f)
				{
					verifyStart = now;
					return;
				}
				if (verifyStep == 0 && now > verifyStart + 6f)
				{
					// Over the colony's printing pod: an outhouse needs a floor; where it stands does not matter for the meter.
					Telepad pod = Components.Telepads.Items.Count > 0 ? Components.Telepads.Items[0] : null;
					int cell = pod != null ? Grid.CellAbove(Grid.CellAbove(Grid.CellAbove(Grid.PosToCell(pod)))) : Grid.PosToCell(CameraController.Instance.transform.GetPosition());
					GameObject go = Assets.GetBuildingDef("Outhouse").Build(cell, Orientation.Neutral, null, new[] { SimHashes.SandStone.CreateTag() }, 300f, playsound: false);
					test = go.GetComponent<Toilet>();
					verifyStep = 1;
				}
				else if (verifyStep == 1)
				{
					// The next frame: the outhouse has spawned and started its states. Full, as before a cleaning.
					test.FlushesUsed = test.maxFlushes;
					test.meter.SetPositionPercent(1f);
					verifyStep = 2;
				}
				else if (verifyStep == 2)
				{
					float before = test.meter.meterController.GetPositionPercent();
					test.smi.GoTo(test.smi.sm.empty);
					float after = test.meter.meterController.GetPositionPercent();
					bool ok = test.FlushesUsed == 0 && after < 0.01f;
					Debug.Log($"{ModContext.Tag} OuthouseMeter verify: {(ok ? "ok" : "FAILED")} (uses {test.FlushesUsed}, meter {before:F2} before the cleaning ends, {after:F2} after)");
					Util.KDestroyGameObject(test.gameObject);
					verifyStep = 3;
				}
			}
			catch (Exception e)
			{
				verifyStep = 3;
				Debug.LogWarning($"{ModContext.Tag} OuthouseMeter verify: FAILED: {e}");
			}
		}

		private static void Postfix(Toilet.States __instance)
		{
			__instance.empty.Enter("OniFixes.OuthouseMeter", smi =>
			{
				Toilet toilet = smi.master;
				if (toilet != null && toilet.meter != null && toilet.maxFlushes > 0)
				{
					toilet.meter.SetPositionPercent((float)toilet.FlushesUsed / toilet.maxFlushes);
				}
			});
		}
	}
}
