using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using OniCommon;

namespace OniFixes
{
	/// <summary>
	/// A receiver takes an item off the rail in the same conveyor step in which the item entered its cell.
	/// The item itself disappears at once (it goes into storage), while the little box travels on empty for a whole step.
	/// Same with an item that was standing in front of the receiver: the box is drawn until the end of the step, the item is already gone.
	///
	/// The conveyor logic is not changed here: the item is taken at the same moment as before. For the remaining
	/// step a copy of its image is drawn in its place, which travels with the box and disappears together with it.
	/// The copy repeats the animation files, current animation, tint and hidden symbols; symbol overrides are not copied.
	/// </summary>
	internal static class ConveyorArrivals
	{
		private sealed class Ghost
		{
			public KBatchedAnimController anim;
			public Vector3 from, to;
			public float tick;
			public bool overlay;
		}

		private static readonly SolidConduitFlow.FlowDirection[] Directions =
		{
			SolidConduitFlow.FlowDirection.Left, SolidConduitFlow.FlowDirection.Right, SolidConduitFlow.FlowDirection.Up, SolidConduitFlow.FlowDirection.Down,
		};

		private static readonly List<Ghost> ghosts = new List<Ghost>();
		private static SolidConduitFlow owner;
		private static bool inConsumer;
		private static long taken, created;

		// Self-check: file verify_conveyor_arrival.txt in the mod folder. The camera moves to a receiver that
		// items are entering, and in the middle of a step screenshots are written: verify_arrival_vanilla.png (copies off),
		// verify_arrival_fixed.png (with the fix, conveyor overlay) and verify_arrival_normal.png (no overlay).
		private const int VerifyStartFrame = 300;
		private const float VerifyZoom = 3f;
		private static bool verify, ghostsEnabled = true, arrivedAtTarget;
		private static int verifyFrame, verifyPhase, verifyResumeFrame;
		private static Vector3 verifyPos;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SolidConduitConsumer), nameof(SolidConduitConsumer.ConduitUpdate)),
				prefix: new HarmonyMethod(typeof(ConveyorArrivals), nameof(EnterConsumer)),
				finalizer: new HarmonyMethod(typeof(ConveyorArrivals), nameof(LeaveConsumer)));
			harmony.Patch(AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.RemovePickupable)),
				prefix: new HarmonyMethod(typeof(ConveyorArrivals), nameof(BeforeRemove)));
			harmony.Patch(AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.Sim200ms)),
				postfix: new HarmonyMethod(typeof(ConveyorArrivals), nameof(DropExpired)));
			harmony.Patch(AccessTools.Method(typeof(SolidConduitFlow), nameof(SolidConduitFlow.RenderEveryTick)),
				postfix: new HarmonyMethod(typeof(ConveyorArrivals), nameof(MoveGhosts)));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)),
				prefix: new HarmonyMethod(typeof(ConveyorArrivals), nameof(LogStats)));
			verify = File.Exists(Path.Combine(ModContext.Folder, "verify_conveyor_arrival.txt"));
		}

		private static void EnterConsumer()
		{
			inConsumer = true;
		}

		private static void LeaveConsumer()
		{
			inConsumer = false;
		}

		private static void BeforeRemove(SolidConduitFlow __instance, int cell_idx)
		{
			if (!inConsumer)
			{
				return;
			}
			SolidConduitFlow.Conduit conduit = __instance.GetConduit(cell_idx);
			if (conduit.idx == -1)
			{
				return;
			}
			SolidConduitFlow.ConduitContents contents = conduit.GetContents(__instance);
			if (!contents.pickupableHandle.IsValid())
			{
				return;
			}
			taken++;
			KBatchedAnimController source = __instance.conveyorPickupables.GetData(contents.pickupableHandle).kbac;
			if (source == null || source.CurrentAnim == null || !source.HasBatchInstanceData)
			{
				return;
			}
			// The item either entered this cell on this step (then the box travels from the neighbouring one), or was standing in it.
			Vector3 to = Grid.CellToPosCCC(cell_idx, Grid.SceneLayer.SolidConduitContents);
			Vector3 from = to;
			foreach (SolidConduitFlow.FlowDirection direction in Directions)
			{
				int neighbour = SolidConduitFlow.GetCellFromDirection(cell_idx, direction);
				if (!Grid.IsValidCell(neighbour))
				{
					continue;
				}
				SolidConduitFlow.Conduit other = __instance.GetConduit(neighbour);
				if (other.idx != -1 && other.GetLastFlowInfo(__instance).direction == SolidConduitFlow.InverseFlow(direction))
				{
					from = Grid.CellToPosCCC(neighbour, Grid.SceneLayer.SolidConduitContents);
					break;
				}
			}
			if (verify && from != to)
			{
				VerifyArrival(to);
			}
			if (!ghostsEnabled)
			{
				return;
			}
			if (owner != __instance)
			{
				owner = __instance;
				ghosts.Clear();
			}
			ghosts.Add(new Ghost
			{
				anim = CreateCopy(source, Vector3.Lerp(from, to, __instance.ContinuousLerpPercent)),
				from = from,
				to = to,
				tick = __instance.lastUpdateTime,
				overlay = __instance.viewingConduits,
			});
			created++;
		}

		private static KBatchedAnimController CreateCopy(KBatchedAnimController source, Vector3 position)
		{
			GameObject go = GameUtil.KInstantiate(Assets.GetPrefab(EffectConfigs.EffectTemplateId), position, Grid.SceneLayer.SolidConduitContents, "ConveyorArrival");
			KBatchedAnimController copy = go.GetComponent<KBatchedAnimController>();
			copy.AnimFiles = source.AnimFiles;
			copy.materialType = source.materialType;
			copy.animScale = source.animScale;
			copy.animWidth = source.animWidth;
			copy.animHeight = source.animHeight;
			copy.sceneLayer = Grid.SceneLayer.SolidConduitContents;
			go.transform.localScale = source.transform.localScale;
			go.SetActive(true);
			copy.Play(source.currentAnim, source.PlayMode);
			copy.SetPositionPercent(source.GetPositionPercent());
			copy.FlipX = source.FlipX;
			copy.FlipY = source.FlipY;
			copy.Rotation = source.Rotation;
			copy.Offset = source.Offset;
			foreach (KAnimHashedString symbol in source.hiddenSymbolsSet)
			{
				copy.SetSymbolVisiblity(symbol, false);
			}
			copy.SetLayer(source.gameObject.layer);
			copy.TintColour = source.TintColour;
			return copy;
		}

		/// <summary>The copy lives until the next conveyor step: with it, its box also stops being drawn.</summary>
		private static void DropExpired(SolidConduitFlow __instance)
		{
			for (int i = ghosts.Count - 1; i >= 0; i--)
			{
				Ghost ghost = ghosts[i];
				if (owner == __instance && ghost.anim != null && ghost.tick == __instance.lastUpdateTime)
				{
					continue;
				}
				if (ghost.anim != null)
				{
					Util.KDestroyGameObject(ghost.anim.gameObject);
				}
				ghosts.RemoveAt(i);
			}
		}

		private static void MoveGhosts(SolidConduitFlow __instance)
		{
			if (verify)
			{
				VerifyStep(__instance);
			}
			if (ghosts.Count == 0)
			{
				return;
			}
			DropExpired(__instance);
			float lerp = __instance.ContinuousLerpPercent;
			bool overlay = __instance.viewingConduits;
			foreach (Ghost ghost in ghosts)
			{
				ghost.anim.transform.SetPosition(Vector3.Lerp(ghost.from, ghost.to, lerp));
				if (ghost.overlay != overlay)
				{
					// This is how the game recolours items on the rail when the overlay changes.
					ghost.overlay = overlay;
					ghost.anim.SetLayer(overlay ? __instance.maskedOverlayLayer : Game.PickupableLayer);
					ghost.anim.TintColour = overlay ? SolidConduitFlow.OverlayColour : SolidConduitFlow.NormalColour;
				}
			}
		}

		private static void LogStats()
		{
			Debug.Log($"[OniFixes] ConveyorArrivals: items taken off the belt by consumers={taken}, copies drawn={created}");
		}

		/// <summary>Called from the conveyor step, so it only records the event: VerifyStep handles the camera and the overlay.</summary>
		private static void VerifyArrival(Vector3 to)
		{
			if (verifyPhase == 0 && verifyFrame >= VerifyStartFrame)
			{
				verifyPos = to;
				ghostsEnabled = false;
				verifyPhase = -1;
			}
			else if (verifyPhase > 0 && to == verifyPos && verifyFrame >= verifyResumeFrame)
			{
				arrivedAtTarget = true;
			}
		}

		private static void VerifyStep(SolidConduitFlow flow)
		{
			verifyFrame++;
			if (verifyPhase == 0)
			{
				return;
			}
			if (verifyPhase == -1)
			{
				int cell = Grid.PosToCell(verifyPos);
				Debug.Log($"[OniFixes] ConveyorArrivals verify: target cell={cell} world={Grid.WorldIdx[cell]} active world={ClusterManager.Instance.activeWorldId}, frame={verifyFrame}, copies so far={created}");
				foreach (ConfirmDialogScreen dialog in Object.FindObjectsByType<ConfirmDialogScreen>(FindObjectsSortMode.None))
				{
					dialog.Deactivate();
				}
				ClusterManager.Instance.SetActiveWorld(Grid.WorldIdx[cell]);
				OverlayScreen.Instance.ToggleOverlay(OverlayModes.SolidConveyor.ID, allowSound: false);
				verifyPhase = 1;
				verifyResumeFrame = verifyFrame + 60;
				Debug.Log("[OniFixes] ConveyorArrivals verify: camera and overlay set");
			}
			// The camera is held in place even if someone moves it.
			CameraController.Instance.SnapTo(verifyPos, VerifyZoom);
			if (verifyPhase == 3 && verifyFrame == verifyResumeFrame)
			{
				// Not in the screenshot frame: the screenshot is written at the end of the frame and must catch the overlay.
				OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID, allowSound: false);
			}
			if (!arrivedAtTarget || flow.ContinuousLerpPercent < 0.5f)
			{
				return;
			}
			arrivedAtTarget = false;
			verifyResumeFrame = verifyFrame + 3;
			string name = verifyPhase == 1 ? "vanilla" : verifyPhase == 2 ? "fixed" : "normal";
			string file = Path.Combine(ModContext.Folder, $"verify_arrival_{name}.png");
			ScreenCapture.CaptureScreenshot(file);
			Debug.Log($"[OniFixes] ConveyorArrivals verify: {name}: step progress={flow.ContinuousLerpPercent:F2}, copies={ghosts.Count}, overlay={OverlayScreen.Instance.GetMode()}, file={file}");
			verifyPhase++;
			ghostsEnabled = true;
			if (verifyPhase == 4)
			{
				verify = false;
			}
		}
	}
}
