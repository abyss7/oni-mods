using System;
using System.Collections.Generic;
using HarmonyLib;
using OniCommon;

namespace OniDup
{
	/// <summary>
	/// Building tiles without walling oneself in. The game pushes a duplicant out of a tile built where they stand; with that
	/// gone (DupEntombed) a tile built into a duplicant buries them, so:
	///
	/// - A tile is not built from a cell inside it: the cells a duplicant may work from leave out those where the body (feet
	///   or head) would be inside the tile.
	/// - A tile is not finished while a duplicant is inside it (walking through, standing, sleeping) or stepping into it:
	///   the work holds until they are gone. That holds after the last swing too: the game finishes the tile only when the
	///   builder's closing animation ends, and someone may step in meanwhile.
	/// - A finished tile is solid for walking at once. The game makes the cell solid only when the simulation has put the
	///   tile's material there, a tick later; until then duplicants still walked into the cell as if it were open and were
	///   buried when it closed. The walking graph around it is redone at once too: the cell under a tile built one cell above
	///   the floor stops being a place to stand before anyone walks in with the head in the tile.
	/// </summary>
	internal static class DupBuild
	{
		internal static string Mode { get; private set; } = "off";
		internal static int filteredTables, heldTicks, earlySolid, stepping, heldCompletions;

		internal static void Apply(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(Workable), nameof(Workable.SetOffsetTable)), prefix: new HarmonyMethod(typeof(DupBuild), nameof(NotFromInside)));
				harmony.Patch(AccessTools.Method(typeof(Workable), nameof(Workable.WorkTick)), prefix: new HarmonyMethod(typeof(DupBuild), nameof(HoldWhileOccupied)));
				harmony.Patch(AccessTools.Method(typeof(StandardWorker), nameof(StandardWorker.Work)), prefix: new HarmonyMethod(typeof(DupBuild), nameof(HoldCompletion)));
				harmony.Patch(AccessTools.DeclaredMethod(typeof(SimCellOccupier), "OnSpawn"), postfix: new HarmonyMethod(typeof(DupBuild), nameof(SolidAtOnce)));
				Mode = "on";
			}
			catch (Exception e)
			{
				Mode = "off: " + e.Message;
				Debug.LogWarning($"{ModContext.Tag} DupBuild failed to apply: {e}");
			}
		}

		/// <summary>A building under construction that will be solid for walking once built (a tile of any kind).</summary>
		private static bool BuildsSolid(Workable workable, out Building building)
		{
			building = null;
			if (!(workable is Constructable constructable))
			{
				return false;
			}
			building = constructable.GetComponent<Building>();
			return building != null && building.Def != null && building.Def.BuildingComplete != null
				&& building.Def.BuildingComplete.GetComponent<SimCellOccupier>() != null;
		}

		private static void NotFromInside(Workable __instance, ref CellOffset[][] offset_table)
		{
			if (offset_table == null || !BuildsSolid(__instance, out Building building))
			{
				return;
			}
			// The tile's cells relative to the building's cell (the table's offsets are from it too).
			int origin = Grid.PosToCell(__instance);
			var inside = new HashSet<CellOffset>();
			foreach (int cell in building.PlacementCells)
			{
				CellOffset offset = Grid.GetOffset(origin, cell);
				inside.Add(offset);
				inside.Add(new CellOffset(offset.x, offset.y - 1)); // standing below with the head inside
			}
			var kept = new List<CellOffset[]>(offset_table.Length);
			foreach (CellOffset[] entry in offset_table)
			{
				if (entry == null || entry.Length == 0 || !inside.Contains(entry[0]))
				{
					kept.Add(entry);
				}
			}
			if (kept.Count != offset_table.Length)
			{
				offset_table = kept.ToArray();
				filteredTables++;
			}
		}

		private static bool HoldWhileOccupied(Workable __instance, ref bool __result)
		{
			if (!BuildsSolid(__instance, out Building building) || !Occupied(building))
			{
				return true;
			}
			return Hold(ref __result);
		}

		/// <summary>The work is done and the closing animation plays: the tile is not finished while someone is in it.</summary>
		private static bool HoldCompletion(StandardWorker __instance, ref WorkerBase.WorkResult __result)
		{
			if ((__instance.state != WorkerBase.State.PendingCompletion && __instance.state != WorkerBase.State.Completing) || !__instance.successFullyCompleted
				|| !BuildsSolid(__instance.GetWorkable(), out Building building) || !Occupied(building))
			{
				return true;
			}
			heldCompletions++;
			__result = WorkerBase.WorkResult.InProgress;
			return false;
		}

		/// <summary>A duplicant is in the tile's cells or stepping into them (or below them with the head inside).</summary>
		private static bool Occupied(Building building)
		{
			int[] cells = building.PlacementCells;
			foreach (int cell in cells)
			{
				// A duplicant takes the cells of their feet and head on this layer.
				if (Grid.IsValidCell(cell) && Grid.Objects[cell, (int)ObjectLayer.Minion] != null)
				{
					return true;
				}
			}
			// A duplicant in the middle of a step into the tile is still registered in the cell they left.
			foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
			{
				Navigator navigator = minion != null ? minion.GetComponent<Navigator>() : null;
				if (navigator == null || !navigator.IsMoving() || !navigator.path.IsValid() || navigator.path.nodes.Count < 2)
				{
					continue;
				}
				int next = navigator.path.nodes[1].cell;
				if (Array.IndexOf(cells, next) >= 0 || (Grid.IsValidCell(next) && Array.IndexOf(cells, Grid.CellAbove(next)) >= 0))
				{
					stepping++;
					return true;
				}
			}
			return false;
		}

		private static bool Hold(ref bool result)
		{
			heldTicks++;
			result = false;
			return false;
		}

		private static void SolidAtOnce(SimCellOccupier __instance)
		{
			if (!__instance.doReplaceElement)
			{
				return; // the game sets these cells itself at once (ForceSetGameCellData)
			}
			Building building = __instance.GetComponent<Building>();
			if (building == null || building.PlacementCells == null)
			{
				return;
			}
			var changed = new List<int>();
			foreach (int cell in building.PlacementCells)
			{
				if (Grid.IsValidCell(cell) && !Grid.Solid[cell])
				{
					Grid.SetSolid(cell, solid: true, CellEventLogger.Instance.SimCellOccupierForceSolid);
					changed.Add(cell);
					earlySolid++;
				}
			}
			RefreshAround(changed);
		}

		/// <summary>
		/// Redoes the walking graph at once around cells that became solid, as far as each nav grid's update reaches (the game
		/// does it at its next update of the dirty cells). Not only the cell itself: the cell below it, where a duplicant would
		/// stand with the head in the tile, stops being a place to walk too, before anyone steps in.
		/// </summary>
		private static void RefreshAround(List<int> cells)
		{
			if (cells.Count == 0 || Pathfinding.Instance == null)
			{
				return;
			}
			var area = new List<int>();
			var added = new HashSet<int>();
			foreach (NavGrid navGrid in Pathfinding.Instance.NavGrids)
			{
				area.Clear();
				added.Clear();
				foreach (int cell in cells)
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
		}
	}
}
