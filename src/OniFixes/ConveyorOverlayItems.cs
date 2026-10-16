using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using OniCommon;

namespace OniFixes
{
	/// <summary>
	/// In the conveyor overlay each item on the rail is shown as a little box. A moving box has only its
	/// frame drawn, and the item is visible through it. For a standing one the game additionally draws an opaque background under the frame,
	/// and it lies closer to the camera than the item itself: as soon as an item stopped, it vanished and the box
	/// looked empty.
	///
	/// Here the background of standing boxes is not drawn: a standing box looks the same as a moving one,
	/// the item is always visible and stays under the frame. Outside the overlay nothing changes: there is no background there at all.
	/// </summary>
	internal static class ConveyorOverlayItems
	{
		// Turned off only by the self-check for the duration of the "as in the game" screenshot.
		private static bool skipBackground = true;

		// Self-check: file verify_conveyor.txt in the mod folder. A few seconds after loading, the camera
		// moves to a stretch of rail with standing items, the conveyor overlay is turned on and three screenshots
		// are written to the mod folder: verify_conveyor_vanilla.png (background drawn, as in the game), verify_conveyor_fixed.png
		// (with the fix) and verify_conveyor_normal.png (the same stretch without the overlay).
		private const int VerifyStartFrame = 300;
		private const float VerifyZoom = 4.5f;
		private const int ViewHalfWidth = 8, ViewHalfHeight = 4;
		private static bool verify;
		private static int verifyFrame;
		private static Vector3 verifyPos;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SolidConduitFlowVisualizer.ConduitFlowMesh), nameof(SolidConduitFlowVisualizer.ConduitFlowMesh.AddQuad)),
				prefix: new HarmonyMethod(typeof(ConveyorOverlayItems), nameof(Prefix)));
			verify = File.Exists(Path.Combine(ModContext.Folder, "verify_conveyor.txt"));
			if (verify)
			{
				harmony.Patch(AccessTools.Method(typeof(SolidConduitFlowVisualizer), nameof(SolidConduitFlowVisualizer.Render)),
					postfix: new HarmonyMethod(typeof(ConveyorOverlayItems), nameof(VerifyStep)));
			}
		}

		/// <param name="is_foreground">0 for the background, 1 for the frame.</param>
		private static bool Prefix(SolidConduitFlowVisualizer.ConduitFlowMesh __instance, float is_foreground)
		{
			if (is_foreground != 0f || !skipBackground)
			{
				return true;
			}
			Game game = Game.Instance;
			SolidConduitFlowVisualizer visualizer = game != null ? game.solidFlowVisualizer : null;
			return visualizer == null || !ReferenceEquals(__instance, visualizer.staticBallMesh);
		}

		private static void VerifyStep(SolidConduitFlowVisualizer __instance)
		{
			if (!verify || __instance != Game.Instance.solidFlowVisualizer)
			{
				return;
			}
			int step = verifyFrame++ - VerifyStartFrame;
			if (step < 0)
			{
				return;
			}
			SolidConduitFlow flow = Game.Instance.solidConduitFlow;
			if (step == 0)
			{
				if (!FindStandingItems(flow, out int cell))
				{
					Debug.LogWarning("[OniFixes] ConveyorOverlayItems verify: no standing items on conveyors, nothing to capture");
					verify = false;
					return;
				}
				verifyPos = Grid.CellToPosCCC(cell, Grid.SceneLayer.SolidConduitContents);
				ClusterManager.Instance.SetActiveWorld(Grid.WorldIdx[cell]);
				OverlayScreen.Instance.ToggleOverlay(OverlayModes.SolidConveyor.ID, allowSound: false);
			}
			// The camera is held in place even if someone moves it.
			CameraController.Instance.SnapTo(verifyPos, VerifyZoom);
			switch (step)
			{
				case 40:
					skipBackground = false;
					break;
				case 45:
					Capture(flow, "vanilla");
					break;
				case 50:
					skipBackground = true;
					break;
				case 55:
					Capture(flow, "fixed");
					break;
				case 60:
					OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID, allowSound: false);
					break;
				case 100:
					Capture(flow, "normal");
					verify = false;
					break;
			}
		}

		private static void Capture(SolidConduitFlow flow, string name)
		{
			CountItems(flow, Grid.PosToXY(verifyPos), out int standing, out int moving);
			string file = Path.Combine(ModContext.Folder, $"verify_conveyor_{name}.png");
			ScreenCapture.CaptureScreenshot(file);
			Debug.Log($"[OniFixes] ConveyorOverlayItems verify: {name}: standing={standing}, moving={moving}, overlay={OverlayScreen.Instance.GetMode()}, file={file}");
		}

		private static bool IsStanding(SolidConduitFlow flow, SolidConduitFlow.Conduit conduit)
		{
			return conduit.GetInitialContents(flow).pickupableHandle.IsValid() && conduit.GetLastFlowInfo(flow).direction == SolidConduitFlow.FlowDirection.None;
		}

		private static bool IsMoving(SolidConduitFlow flow, SolidConduitFlow.Conduit conduit)
		{
			return conduit.GetLastFlowInfo(flow).direction != SolidConduitFlow.FlowDirection.None;
		}

		/// <summary>Finds a standing item with the most other standing and travelling items next to it.</summary>
		private static bool FindStandingItems(SolidConduitFlow flow, out int bestCell)
		{
			SolidConduitFlow.SOAInfo conduits = flow.soaInfo;
			List<int> standing = new List<int>();
			for (int i = 0; i < conduits.NumEntries; i++)
			{
				if (IsStanding(flow, conduits.GetConduit(i)))
				{
					standing.Add(conduits.GetCell(i));
				}
			}
			bestCell = -1;
			int bestScore = -1;
			foreach (int cell in standing)
			{
				CountItems(flow, Grid.CellToXY(cell), out int nearStanding, out int nearMoving);
				int score = Mathf.Min(nearStanding, 6) + 2 * Mathf.Min(nearMoving, 6);
				if (score > bestScore)
				{
					bestScore = score;
					bestCell = cell;
				}
			}
			return bestCell != -1;
		}

		private static void CountItems(SolidConduitFlow flow, Vector2I center, out int standing, out int moving)
		{
			standing = 0;
			moving = 0;
			SolidConduitFlow.SOAInfo conduits = flow.soaInfo;
			for (int i = 0; i < conduits.NumEntries; i++)
			{
				Vector2I xy = Grid.CellToXY(conduits.GetCell(i));
				if (Mathf.Abs(xy.x - center.x) > ViewHalfWidth || Mathf.Abs(xy.y - center.y) > ViewHalfHeight)
				{
					continue;
				}
				SolidConduitFlow.Conduit conduit = conduits.GetConduit(i);
				if (IsStanding(flow, conduit))
				{
					standing++;
				}
				else if (IsMoving(flow, conduit))
				{
					moving++;
				}
			}
		}
	}
}
