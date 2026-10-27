using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// A door laid flat is a floor in the game whether it is open or closed. Here it is a floor only while closed: as it
	/// opens, its floor goes, and what stands on it falls through (Duplicants and critters by the game's own falling, items
	/// are dropped); as it closes, the floor is back. Climbing a ladder through it still works (a Duplicant in the door's
	/// cell holds on to the ladder below, not to the door); only stepping off the ladder onto an open door does not. Doors
	/// standing up keep their top as a floor (the door frame). The Pneumatic Door, no floor at all, is FlimsyDoors.
	/// Paths: a Duplicant's path is planned while the door is closed, but going up through a door opens it, and its top is
	/// no floor then; so a step that lands on a door laid flat and passes through that door (from its own cell, or with it
	/// among the cells the move goes through) is not offered to the path search. Up through such a door a Duplicant needs a
	/// ladder going on above it (and steps off it sideways); along the top of a closed door and down through it nothing
	/// changes.
	/// The game adds the floor as the door spawns and takes it away as it goes: here it is taken away while the door is open
	/// and put back before the game takes it away, so a floor another building gives the same cell is not touched.
	/// Self-check: the file verify_open_doors.txt in the mod folder: a Manual Airlock is laid flat near the printing pod,
	/// an item is dropped onto it, the door is opened and closed again, and removed (the save is changed: use a copy).
	/// </summary>
	internal static class OpenFlatDoors
	{
		private static readonly HashSet<Door> spawned = new HashSet<Door>(), open = new HashSet<Door>();
		internal static int opened, dropped;

		internal static void Apply(Harmony harmony)
		{
			// The Door class is patched only once the game's animations are loaded (DoorPatches).
			DoorPatches.Later(harmony, h =>
			{
				h.Patch(AccessTools.Method(typeof(Door), "OnSpawn"), postfix: new HarmonyMethod(typeof(OpenFlatDoors), nameof(Spawned)) { priority = Priority.Low });
				h.Patch(AccessTools.Method(typeof(Door), "SetPassableState"), postfix: new HarmonyMethod(typeof(OpenFlatDoors), nameof(StateChanged)));
				h.Patch(AccessTools.Method(typeof(Door), "OnCleanUp"), prefix: new HarmonyMethod(typeof(OpenFlatDoors), nameof(CleaningUp)));
			});
			harmony.Patch(AccessTools.Method(typeof(MinionPathFinderAbilities), nameof(MinionPathFinderAbilities.TraversePath)), postfix: new HarmonyMethod(typeof(OpenFlatDoors), nameof(NotUpOntoIt)));
			if (File.Exists(Path.Combine(ModContext.Folder, "verify_open_doors.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(OpenFlatDoors), nameof(Verify)));
			}
		}

		/// <summary>A door laid flat that has a floor of its own (not the Pneumatic Door when FlimsyDoors takes its floor away).</summary>
		private static bool Watched(Door door)
		{
			Building building = door != null ? door.GetComponent<Building>() : null;
			Rotatable rotatable = door != null ? door.GetComponent<Rotatable>() : null;
			if (building == null || building.Def == null || rotatable == null || rotatable.GetOrientation() == Orientation.Neutral)
			{
				return false;
			}
			return !(Mod.Options.FlimsyDoors && building.Def.PrefabID == DoorConfig.ID);
		}

		private static int[] Cells(Door door) => door.GetComponent<Building>().PlacementCells;

		private static void Spawned(Door __instance)
		{
			if (!Watched(__instance))
			{
				return;
			}
			spawned.Add(__instance);
			Mark(__instance, true);
			Sync(__instance);
		}

		private static void StateChanged(Door __instance)
		{
			if (spawned.Contains(__instance))
			{
				Sync(__instance);
			}
		}

		private static void Sync(Door door)
		{
			bool isOpen = door.IsOpen(), was = open.Contains(door);
			if (isOpen == was)
			{
				return;
			}
			foreach (int cell in Cells(door))
			{
				if (isOpen) Grid.FakeFloor.Remove(cell);
				else Grid.FakeFloor.Add(cell);
				Pathfinding.Instance.AddDirtyNavGridCell(cell);
				Pathfinding.Instance.AddDirtyNavGridCell(Grid.CellAbove(cell));
			}
			if (isOpen)
			{
				open.Add(door);
				opened++;
				DropWhatLies(door);
			}
			else
			{
				open.Remove(door);
			}
		}

		/// <summary>Items lying on the door fall (Duplicants and critters fall by themselves: their place to stand is gone).</summary>
		private static void DropWhatLies(Door door)
		{
			foreach (int cell in Cells(door))
			{
				foreach (int at in new[] { cell, Grid.CellAbove(cell) })
				{
					if (!Grid.IsValidCell(at)) continue;
					var items = new List<GameObject>();
					for (ObjectLayerListItem item = Grid.Objects[at, (int)ObjectLayer.Pickupables]?.GetComponent<Pickupable>()?.objectLayerListItem; item != null; item = item.nextItem)
					{
						if (item.gameObject != null) items.Add(item.gameObject);
					}
					foreach (GameObject go in items)
					{
						Pickupable pickupable = go.GetComponent<Pickupable>();
						if (pickupable == null || pickupable.storage != null || go.GetComponent<MinionIdentity>() != null || GameComps.Gravities.Has(go)) continue;
						GameComps.Gravities.Add(go, Vector2.zero);
						dropped++;
					}
				}
			}
		}

		private static void CleaningUp(Door __instance)
		{
			if (open.Remove(__instance))
			{
				foreach (int cell in Cells(__instance)) Grid.FakeFloor.Add(cell);
			}
			if (spawned.Remove(__instance)) Mark(__instance, false);
		}

		// The cells of doors laid flat with a floor of their own, by cell: read by the path search (on worker threads too;
		// written only as doors come and go, on the main thread).
		private static bool[] flat;

		private static void Mark(Door door, bool on)
		{
			if (flat == null || flat.Length != Grid.CellCount) flat = new bool[Grid.CellCount];
			foreach (int cell in Cells(door)) flat[cell] = on;
		}

		private static bool Flat(int cell) => flat != null && (uint)cell < (uint)flat.Length && flat[cell];

		/// <summary>Whether a step onto the top of a door laid flat goes through that door (and would open it under itself).</summary>
		internal static bool ThroughOntoIt(int to, NavType navType, int from, CellOffset[] voidOffsets)
		{
			if (navType != NavType.Floor || !Grid.IsValidCell(to))
			{
				return false;
			}
			int anchor = Grid.CellBelow(to);
			if (!Grid.IsValidCell(anchor) || !Grid.HasDoor[anchor] || !Flat(anchor))
			{
				return false;
			}
			if (Flat(from) && Grid.CellRow(from) == Grid.CellRow(anchor))
			{
				return true;
			}
			if (voidOffsets != null)
			{
				foreach (CellOffset offset in voidOffsets)
				{
					int cell = Grid.OffsetCell(from, offset);
					if (Flat(cell) && Grid.CellRow(cell) == Grid.CellRow(anchor)) return true;
				}
			}
			return false;
		}

		internal static long refusedSteps;

		private static void NotUpOntoIt(ref PathFinder.PotentialPath path, int from_cell, int transition_id, Navigator ___navigator, ref bool __result)
		{
			if (!__result || flat == null || path.navType != NavType.Floor)
			{
				return;
			}
			if (ThroughOntoIt(path.cell, path.navType, from_cell, ___navigator.NavGrid.transitions[transition_id].voidOffsets))
			{
				__result = false;
				refusedSteps++;
			}
		}

		// ---------- Self-check ----------

		private static float verifyStart = -1f, stepAt;
		private static int step;
		private static Door door;
		private static GameObject item;
		private static readonly List<string> notes = new List<string>();
		private static bool ok = true;

		/// <summary>A place for a door laid flat with two free cells under and above it: nearest the given cell, anywhere in its world.</summary>
		private static int FreeCell(int from)
		{
			Grid.CellToXY(from, out int x0, out int y0);
			for (int r = 0; r < 120; r++)
			for (int dy = -r; dy <= r; dy++)
			{
				for (int dx = -r; dx <= r; dx++)
				{
					if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
					bool free = true;
					for (int y = -2; y < 3 && free; y++)
					{
						for (int x = 0; x < 2 && free; x++)
						{
							int c = Grid.XYToCell(x0 + dx + x, y0 + dy + y);
							free = Grid.IsValidCell(c) && !Grid.Solid[c] && Grid.Element[c].IsGas && Grid.Objects[c, (int)ObjectLayer.Building] == null
								&& Grid.Objects[c, (int)ObjectLayer.FoundationTile] == null && !Grid.HasDoor[c] && !Grid.FakeFloor[c];
						}
					}
					if (free && Grid.WorldIdx[Grid.XYToCell(x0 + dx, y0 + dy)] == Grid.WorldIdx[from]) return Grid.XYToCell(x0 + dx, y0 + dy);
				}
			}
			return Grid.InvalidCell;
		}

		/// <summary>As the game's instant build mode does: the requested state at once, without an errand.</summary>
		private static void SetState(Door.ControlState state)
		{
			door.QueueStateChange(state);
			AccessTools.Method(typeof(Door), "ApplyRequestedControlState").Invoke(door, AccessTools.Method(typeof(Door), "ApplyRequestedControlState").GetParameters().Length == 0 ? null : new object[] { false });
		}

		private static bool Standable(int cell) => Pathfinding.Instance.GetNavGrid(GameNavGrids.MINION).NavTable.IsValid(Grid.CellAbove(cell), NavType.Floor);

		private static void Check(string what)
		{
			int cell = Cells(door)[0];
			bool floor = Grid.FakeFloor[cell], stand = Standable(cell);
			float itemY = item != null ? item.transform.GetPosition().y : float.NaN;
			Grid.CellToXY(cell, out _, out int doorY);
			bool itemOn = item != null && itemY >= doorY + 1f - 0.2f && itemY < doorY + 2f;
			bool expectFloor = what != "open";
			bool good = floor == expectFloor && stand == expectFloor && (what == "closed" ? itemOn : what == "open" ? !itemOn : true);
			ok &= good;
			notes.Add($"{what}: floor {floor}, a place to stand above {stand}, the item {(item == null ? "gone" : itemOn ? "on the door" : $"at y {itemY - doorY:F1} from the door")}{(good ? "" : " <- wrong")}");
		}

		private static void Verify()
		{
			if (step > 5 || GameClock.Instance == null || ClusterManager.Instance == null || Components.Telepads.Items.Count == 0)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			if (verifyStart < 0f) verifyStart = now;
			if (now < stepAt)
			{
				return;
			}
			try
			{
				switch (step)
				{
					case 0 when now >= verifyStart + 8f:
						int at = FreeCell(Grid.OffsetCell(Grid.PosToCell(Components.Telepads.Items[0]), 0, 4));
						if (!Grid.IsValidCell(at))
						{
							step = 6;
							Debug.Log($"{ModContext.Tag} open doors check: skipped (no free place)");
							return;
						}
						door = Assets.GetBuildingDef(ManualPressureDoorConfig.ID).Build(at, Orientation.R90, null, new[] { SimHashes.Copper.CreateTag() }, 293f, playsound: false, now).GetComponent<Door>();
						step = 1;
						stepAt = now + 2f;
						break;
					case 1:
						// An item dropped from above lands on the closed door.
						item = ElementLoader.FindElementByHash(SimHashes.SandStone).substance.SpawnResource(Grid.CellToPosCCC(Grid.OffsetCell(Cells(door)[0], 0, 2), Grid.SceneLayer.Ore), 10f, 293f, byte.MaxValue, 0);
						step = 2;
						stepAt = now + 4f;
						break;
					case 2:
						Check("closed");
						{
							int[] c = Cells(door);
							int top = Grid.CellAbove(c[0]), side = Grid.CellLeft(c[0]);
							bool upThrough = ThroughOntoIt(top, NavType.Floor, c[0], null);
							bool along = ThroughOntoIt(top, NavType.Floor, Grid.CellAbove(side), null);
							ok &= upThrough && !along;
							notes.Add($"a step up through the door onto it refused {upThrough}, a step along its top refused {along}");
						}
						SetState(Door.ControlState.Opened);
						step = 3;
						stepAt = now + 5f;
						break;
					case 3:
						Check("open");
						SetState(Door.ControlState.Locked);
						step = 4;
						stepAt = now + 5f;
						break;
					case 4:
						Check("closed again");
						int[] cells = (int[])Cells(door).Clone();
						Util.KDestroyGameObject(door.gameObject);
						if (item != null) Util.KDestroyGameObject(item);
						step = 5;
						stepAt = now + 1f;
						notes.Add($"cells: {string.Join(",", cells)}");
						break;
					case 5:
						step = 6;
						Debug.Log($"{ModContext.Tag} open doors check: {(ok ? "ok" : "FAILED")} ({string.Join("; ", notes)}; doors opened {opened}, items dropped {dropped}, steps refused to the path search {refusedSteps})");
						break;
				}
			}
			catch (Exception e)
			{
				step = 6;
				Debug.LogWarning($"{ModContext.Tag} open doors check: FAILED with {e}");
			}
		}
	}
}
