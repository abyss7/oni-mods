using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// Pips (and other wall crawlers) may hold on to a closed door as to a wall: a closed door is impassable for critters,
	/// and the game's walking grid takes such a cell as something to cling to. On loading, the walking grid is made before the
	/// doors mark their cells, and the game's note of the change is lost, so the cells beside doors are not places to cling
	/// to for a while: every critter that was on a door falls, and climbs back on later, when something nearby makes the
	/// game look at those cells again. Here, as each door spawns, the walking grids are redone at once around its cells;
	/// and for the first seconds after loading a critter next to a door does not fall (its door may not have spawned yet).
	/// </summary>
	internal static class DoorClimbers
	{
		private const float Grace = 3f; // s of game time after loading
		private static float loadedAt = -1f;
		internal static int refreshed, kept;
		private static bool reported;

		internal static void Apply(Harmony harmony)
		{
			// The Door class is patched only once the game's animations are loaded (DoorPatches).
			DoorPatches.Later(harmony, h =>
			{
				h.Patch(AccessTools.Method(typeof(Door), "OnSpawn"), postfix: new HarmonyMethod(typeof(DoorClimbers), nameof(DoorSpawned)));
			});
			harmony.Patch(AccessTools.Method(typeof(CreatureFallMonitor.Instance), nameof(CreatureFallMonitor.Instance.ShouldFall)), postfix: new HarmonyMethod(typeof(DoorClimbers), nameof(NotYet)));
			harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(DoorClimbers), nameof(Loaded)));
		}

		// Game time (it stands still while the save loads and while the game is paused).
		private static void Loaded()
		{
			loadedAt = GameClock.Instance != null ? GameClock.Instance.GetTime() : 0f;
			reported = false;
		}

		private static void DoorSpawned(Door __instance)
		{
			Building building = __instance.GetComponent<Building>();
			if (building == null || building.PlacementCells == null || Pathfinding.Instance == null)
			{
				return;
			}
			var area = new List<int>();
			var added = new HashSet<int>();
			foreach (NavGrid navGrid in Pathfinding.Instance.NavGrids)
			{
				area.Clear();
				added.Clear();
				foreach (int cell in building.PlacementCells)
				{
					Grid.CellToXY(cell, out int x, out int y);
					for (int j = Grid.ClampY(y - navGrid.updateRangeY); j <= Grid.ClampY(y + navGrid.updateRangeY); j++)
					{
						for (int i = Grid.ClampX(x - navGrid.updateRangeX); i <= Grid.ClampX(x + navGrid.updateRangeX); i++)
						{
							int around = Grid.XYToCell(i, j);
							if (added.Add(around)) area.Add(around);
						}
					}
				}
				navGrid.UpdateGraph(area);
			}
			refreshed++;
		}

		private static void NotYet(CreatureFallMonitor.Instance __instance, ref bool __result)
		{
			if (loadedAt < 0f || GameClock.Instance == null)
			{
				return;
			}
			if (GameClock.Instance.GetTime() > loadedAt + Grace)
			{
				if (!reported)
				{
					reported = true;
					Debug.Log($"[OniFixes] door climbers: walking grids redone around {refreshed} doors as they spawned; critters by a door kept from falling right after loading {kept} times");
				}
				return;
			}
			if (!__result)
			{
				return;
			}
			int cell = Grid.PosToCell(__instance.transform.GetPosition());
			foreach (int around in new[] { Grid.CellLeft(cell), Grid.CellRight(cell), Grid.CellAbove(cell), Grid.CellBelow(cell) })
			{
				if (Grid.IsValidCell(around) && Grid.HasDoor[around])
				{
					__result = false;
					kept++;
					return;
				}
			}
		}
	}
}
