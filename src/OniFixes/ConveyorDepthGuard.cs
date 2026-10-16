using HarmonyLib;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// An item travelling on a conveyor must be drawn at the depth of the "conveyor contents" layer.
	/// The depth of an animation is set by the batch it is registered in, and the batch is chosen by the z coordinate
	/// at the moment of registration. Sometimes an item on the rail ends up in a batch of a different depth and for the rest of the way
	/// is drawn on top of the rail. The cause has not been found in the code, so this is a check after the fact: after every
	/// conveyor step, the z coordinate and batch depth of all travelling items are compared; a mismatch
	/// is fixed by the stock re-registration and written to the log, to help find the source.
	/// </summary>
	internal static class ConveyorDepthGuard
	{
		private const int MaxLogged = 30;
		private static long checks, repaired;
		private static int logged;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.Sim200ms)),
				postfix: new HarmonyMethod(typeof(ConveyorDepthGuard), nameof(Postfix)));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)),
				prefix: new HarmonyMethod(typeof(ConveyorDepthGuard), nameof(LogStats)));
		}

		private static void Postfix(SolidConduitFlow __instance)
		{
			float expected = Grid.GetLayerZ(Grid.SceneLayer.SolidConduitContents);
			SolidConduitFlow.SOAInfo conduits = __instance.soaInfo;
			int count = conduits.NumEntries;
			for (int i = 0; i < count; i++)
			{
				SolidConduitFlow.ConduitContents contents = conduits.GetConduit(i).GetContents(__instance);
				if (!contents.pickupableHandle.IsValid())
				{
					continue;
				}
				KBatchedAnimController controller = __instance.conveyorPickupables.GetData(contents.pickupableHandle).kbac;
				if (controller == null)
				{
					continue;
				}
				checks++;
				float z = controller.transform.GetPosition().z;
				KAnimBatch batch = controller.batch;
				bool wrongZ = Mathf.Abs(z - expected) > 0.001f;
				bool wrongBatch = batch != null && Mathf.Abs(batch.batchset.key.z - expected) > 0.001f;
				if (!wrongZ && !wrongBatch)
				{
					continue;
				}
				repaired++;
				if (logged++ < MaxLogged)
				{
					Debug.LogWarning($"[OniFixes] ConveyorDepthGuard: {controller.name} cell={conduits.GetCell(i)} z={z:F2} batch z={(batch != null ? batch.batchset.key.z.ToString("F2") : "none")} " +
						$"expected={expected:F2} sceneLayer={controller.sceneLayer} unityLayer={LayerMask.LayerToName(controller.gameObject.layer)} " +
						$"active={controller.isActiveAndEnabled} parent={(controller.transform.parent != null ? controller.transform.parent.name : "none")}");
				}
				controller.SetSceneLayer(Grid.SceneLayer.SolidConduitContents);
			}
		}

		private static void LogStats()
		{
			if (checks > 0)
			{
				Debug.Log($"[OniFixes] ConveyorDepthGuard: item checks={checks}, repaired={repaired}");
			}
		}
	}
}
