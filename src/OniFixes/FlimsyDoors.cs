using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// The Pneumatic Door is a flimsy door that lets items, light and liquids through, yet the game makes it a floor to stand
	/// on like any door (its top row as it stands, all of it laid flat), open or closed. Here it is no floor at all: nobody
	/// stands or walks on it, Duplicants, critters and items fall through it; a ladder through a door laid flat still takes
	/// Duplicants up and down. The other doors are left as they are.
	/// The game adds a door's floor as it spawns and takes it away as it goes: here the Pneumatic Door's floor is taken away
	/// right after the game adds it and put back just before the game takes it away, so a floor another building gives the
	/// same cell is not touched.
	/// Self-check: the file verify_flimsy_doors.txt in the mod folder: a Pneumatic Door is built standing and laid flat near
	/// the printing pod, checked, and removed (the save is changed: use a copy).
	/// </summary>
	internal static class FlimsyDoors
	{
		internal static int doors;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Door), "OnSpawn"), postfix: new HarmonyMethod(typeof(FlimsyDoors), nameof(NoFloor)));
			harmony.Patch(AccessTools.Method(typeof(Door), "OnCleanUp"), prefix: new HarmonyMethod(typeof(FlimsyDoors), nameof(FloorBack)));
			if (File.Exists(Path.Combine(ModContext.Folder, "verify_flimsy_doors.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(FlimsyDoors), nameof(Verify)));
			}
		}

		private static bool IsFlimsy(Door door)
		{
			Building building = door != null ? door.GetComponent<Building>() : null;
			return building != null && building.Def != null && building.Def.PrefabID == DoorConfig.ID;
		}

		/// <summary>The cells the game makes a floor of (as Door.OnSpawn picks them).</summary>
		private static IEnumerable<int> FloorCells(Door door)
		{
			Building building = door.GetComponent<Building>();
			Rotatable rotatable = door.GetComponent<Rotatable>();
			bool standing = rotatable == null || rotatable.GetOrientation() == Orientation.Neutral;
			int first = standing ? building.Def.WidthInCells * (building.Def.HeightInCells - 1) : 0;
			int count = standing ? building.Def.WidthInCells : building.Def.HeightInCells;
			for (int i = 0; i < count; i++)
			{
				yield return building.PlacementCells[first + i];
			}
		}

		private static void NoFloor(Door __instance)
		{
			if (!IsFlimsy(__instance))
			{
				return;
			}
			foreach (int cell in FloorCells(__instance))
			{
				Grid.FakeFloor.Remove(cell);
				Pathfinding.Instance.AddDirtyNavGridCell(cell);
				Pathfinding.Instance.AddDirtyNavGridCell(Grid.CellAbove(cell));
			}
			doors++;
		}

		private static void FloorBack(Door __instance)
		{
			if (!IsFlimsy(__instance) || __instance.GetComponent<Building>()?.PlacementCells == null)
			{
				return;
			}
			foreach (int cell in FloorCells(__instance))
			{
				Grid.FakeFloor.Add(cell);
			}
		}

		// ---------- Self-check ----------

		private static float verifyStart = -1f;
		private static int step;
		private static readonly List<(GameObject door, string how, int top)> built = new List<(GameObject, string, int)>();

		private static int FreeCell(int from, int width, int height)
		{
			Grid.CellToXY(from, out int x0, out int y0);
			for (int dy = 0; dy < 12; dy++)
			{
				for (int dx = -15; dx <= 15; dx++)
				{
					bool free = true;
					for (int y = 0; y < height + 1 && free; y++)
					{
						for (int x = 0; x < width && free; x++)
						{
							int c = Grid.XYToCell(x0 + dx + x, y0 + dy + y);
							free = Grid.IsValidCell(c) && !Grid.Solid[c] && Grid.Element[c].IsGas && Grid.Objects[c, (int)ObjectLayer.Building] == null
								&& Grid.Objects[c, (int)ObjectLayer.FoundationTile] == null && !Grid.HasDoor[c];
						}
					}
					if (free) return Grid.XYToCell(x0 + dx, y0 + dy);
				}
			}
			return Grid.InvalidCell;
		}

		private static void Verify()
		{
			if (step > 2 || GameClock.Instance == null || ClusterManager.Instance == null || Components.Telepads.Items.Count == 0)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			if (verifyStart < 0f) verifyStart = now;
			try
			{
				if (step == 0 && now >= verifyStart + 8f)
				{
					step = 1;
					BuildingDef def = Assets.GetBuildingDef(DoorConfig.ID);
					int pod = Grid.PosToCell(Components.Telepads.Items[0]);
					int standing = FreeCell(Grid.OffsetCell(pod, 0, 3), 1, 2);
					if (Grid.IsValidCell(standing))
					{
						GameObject go = def.Build(standing, Orientation.Neutral, null, new[] { SimHashes.Copper.CreateTag() }, 293f, playsound: false, now);
						built.Add((go, "standing", Grid.CellAbove(standing)));
					}
					int flat = FreeCell(Grid.OffsetCell(pod, 0, 6), 2, 1);
					if (Grid.IsValidCell(flat))
					{
						GameObject go = def.Build(flat, Orientation.R90, null, new[] { SimHashes.Copper.CreateTag() }, 293f, playsound: false, now);
						built.Add((go, "laid flat", -1));
					}
				}
				else if (step == 1 && now >= verifyStart + 11f)
				{
					step = 2;
					var lines = new List<string>();
					bool ok = built.Count == 2;
					var cells = new List<int>();
					foreach ((GameObject go, string how, int top) in built)
					{
						Door door = go.GetComponent<Door>();
						int floors = 0, standable = 0;
						foreach (int cell in FloorCells(door))
						{
							cells.Add(cell);
							if (Grid.FakeFloor[cell]) floors++;
							if (Pathfinding.Instance.GetNavGrid(GameNavGrids.MINION).NavTable.IsValid(Grid.CellAbove(cell), NavType.Floor)) standable++;
						}
						ok &= floors == 0 && standable == 0;
						lines.Add($"{how}: floor cells {floors}, cells above to stand in {standable}");
					}
					foreach ((GameObject go, string how, int top) in built) Util.KDestroyGameObject(go);
					// After removing: the cells hold no floor and are not left below zero for others.
					int left = 0;
					foreach (int cell in cells) if (Grid.FakeFloor[cell]) left++;
					ok &= left == 0;
					Debug.Log($"{ModContext.Tag} flimsy doors check: {(ok ? "ok" : "FAILED")} ({string.Join("; ", lines)}; floors left after removing {left}; Pneumatic Doors without a floor {doors})");
				}
			}
			catch (Exception e)
			{
				step = 3;
				Debug.LogWarning($"{ModContext.Tag} flimsy doors check: FAILED with {e}");
			}
		}
	}
}
