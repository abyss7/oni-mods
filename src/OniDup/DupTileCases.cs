using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniDup
{
	/// <summary>
	/// Self-check of building tiles (DupBuild): the file verify_dup_tiles.txt in the mod folder. A few seconds after loading,
	/// the mod builds two closed rooms of Abyssalite with air and new Duplicants: in the first a builder gets a row of tile
	/// orders along the floor and sandstone to build them with; in the second a builder gets one tile order in the cell of
	/// another Duplicant, who is held there for a while and then let go, and two tile orders one cell above the floor (a gap
	/// under them a Duplicant cannot stand in) while a third Duplicant is sent back and forth under them. Checks: no tile is
	/// finished by a builder standing inside it, all the tiles get built, nobody ends up inside a tile or steps into a cell
	/// with the head in a tile; the held tile is not finished while the other Duplicant is in it and is finished once they
	/// are gone. Then the game is closed. The world is changed: for a copy of the save.
	/// </summary>
	internal static class DupTileCases
	{
		private const int SiteWidth = 32, SiteHeight = 7, Row = 9;
		private const float StartAfter = 8f, Warm = 300f, HoldFor = 60f, AfterRelease = 240f;

		private static int phase, originX, originY, passed, failed, insideFinished, finished, headInside, walks;
		private static float phaseTime;
		private static readonly HashSet<GameObject> orders = new HashSet<GameObject>();
		private static GameObject builderA, builderB, occupant, released, heldOrder, walker;
		private static float lastWalk = -1000f;
		private static bool walkRight;
		private static int heldBefore;

		internal static void Apply(Harmony harmony)
		{
			if (!File.Exists(Path.Combine(ModContext.Folder, "verify_dup_tiles.txt")))
			{
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(DupTileCases), nameof(Frame)));
			harmony.Patch(AccessTools.Method(typeof(Constructable), "OnCompleteWork"), prefix: new HarmonyMethod(typeof(DupTileCases), nameof(Finishing)));
			Debug.Log($"{ModContext.Tag} DupTileCases: scenes will be built in the active world; the save is changed, use a copy");
		}

		private static int Cell(int x, int y) => Grid.XYToCell(originX + x, originY + y);

		private static void Set(int x, int y, SimHashes element, float mass)
		{
			SimMessages.ReplaceElement(Cell(x, y), element, CellEventLogger.Instance.DebugTool, mass, Warm, byte.MaxValue, 0);
		}

		private static void Next(float now)
		{
			phase++;
			phaseTime = now;
		}

		private static void Finishing(Constructable __instance, WorkerBase worker)
		{
			if (phase < 3 || phase > 6 || __instance == null || !orders.Contains(__instance.gameObject) || worker == null)
			{
				return;
			}
			finished++;
			int feet = Grid.PosToCell(worker), head = Grid.CellAbove(feet);
			int[] cells = __instance.GetComponent<Building>().PlacementCells;
			Debug.Log($"{ModContext.Tag} DupTileCases: tile at {Where(cells[0])} finished by {worker.GetProperName()} at {Where(feet)}; others: {Others()}");
			if (cells.Contains(feet) || cells.Contains(head))
			{
				insideFinished++;
				Debug.LogWarning($"{ModContext.Tag} DupTileCases: a tile finished by {worker.GetProperName()} standing inside it");
			}
		}

		private static void Frame()
		{
			if (phase > 6 || Game.Instance == null || GameClock.Instance == null || ClusterManager.Instance == null)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			try
			{
				if (phase >= 3)
				{
					Trace();
					Walk(now);
				}
				if (phase >= 3 && phase <= 4 && occupant != null)
				{
					// The other Duplicant is held in the cell of the tile order.
					occupant.GetComponent<Navigator>()?.Stop();
					occupant.transform.SetPosition(Grid.CellToPosCBC(Cell(24, 1), Grid.SceneLayer.Move));
				}
				switch (phase)
				{
					case 0:
						phaseTime = now;
						phase = 1;
						break;
					case 1 when now >= phaseTime + StartAfter:
						if (!FindSite())
						{
							Debug.LogWarning($"{ModContext.Tag} DupTileCases: FAILED: no free {SiteWidth} x {SiteHeight} place in the active world");
							phase = 7;
							return;
						}
						for (int y = 0; y < SiteHeight; y++)
						{
							for (int x = 0; x < SiteWidth; x++)
							{
								bool inside = y >= 1 && y <= 5 && ((x >= 1 && x <= 14) || (x >= 17 && x <= 30));
								Set(x, y, inside ? SimHashes.Oxygen : SimHashes.Katairite, inside ? 1.8f : 1000f);
							}
						}
						for (int y = -1; y <= SiteHeight; y++)
						{
							for (int x = -1; x <= SiteWidth; x++)
							{
								if (Grid.IsValidCell(Cell(x, y))) Grid.Reveal(Cell(x, y), byte.MaxValue, forceReveal: true);
							}
						}
						Next(now);
						break;
					case 2 when now >= phaseTime + 2f:
						builderA = Spawn(2, 1);
						builderB = Spawn(18, 1);
						occupant = Spawn(24, 1);
						walker = Spawn(20, 1);
						Sandstone(3, 1);
						Sandstone(19, 1);
						for (int x = 5; x < 5 + Row; x++)
						{
							Order(x, 1);
						}
						heldOrder = Order(24, 1);
						heldBefore = DupBuild.heldTicks;
						Next(now);
						break;
					case 3 when now >= phaseTime + HoldFor:
						{
							bool waiting = heldOrder != null && heldOrder.GetComponent<Constructable>() != null;
							int held = DupBuild.heldTicks - heldBefore;
							Expect("a tile is not finished while someone is in it", waiting && held > 0, $"the order still waiting: {waiting}, work held {held} ticks");
							// Let go: the other Duplicant moves out of the way.
							occupant.transform.SetPosition(Grid.CellToPosCBC(Cell(21, 1), Grid.SceneLayer.Move));
							released = occupant;
							occupant = null;
							released.GetComponent<Navigator>()?.Stop();
							// Tiles one cell above the floor, with a gap under them: ordered only now, so that the builder tries
							// the held tile first.
							Order(26, 2);
							Order(27, 2);
							Next(now);
						}
						break;
					case 4 when now >= phaseTime + AfterRelease:
						{
							int left = orders.Count(o => o != null && o.GetComponent<Constructable>() != null);
							Expect("the tiles get built", left == 0, $"{orders.Count - left} of {orders.Count} built, {left} left");
							Expect("no tile is finished from inside it", insideFinished == 0 && finished > 0, $"{finished} finished, {insideFinished} from inside");
							int buried = 0;
							foreach (GameObject dupe in new[] { builderA, builderB, released, walker })
							{
								if (dupe == null) continue;
								int feet = Grid.PosToCell(dupe);
								bool inside = Grid.Solid[feet] || Grid.Solid[Grid.CellAbove(feet)];
								if (inside) buried++;
								Debug.Log($"{ModContext.Tag} DupTileCases: {dupe.GetProperName()} at {Where(feet)}{(inside ? ", inside a tile" : "")}, errand {dupe.GetComponent<ChoreDriver>()?.GetCurrentChore()?.choreType?.Id ?? "none"}");
							}
							foreach (GameObject o in orders)
							{
								if (o != null && o.GetComponent<Constructable>() != null) Debug.Log($"{ModContext.Tag} DupTileCases: not built: {Where(Grid.PosToCell(o))}");
							}
							Expect("nobody ends up inside a tile", buried == 0, $"{buried} Duplicants inside a tile");
							Expect("nobody steps in with the head in a tile", headInside == 0, $"{headInside} times; walks under the gap {walks}");
							Debug.Log($"{ModContext.Tag} DupTileCases: DupBuild: work tables without the cells inside {DupBuild.filteredTables}, held ticks {DupBuild.heldTicks} ({DupBuild.stepping} for someone stepping in), made solid at once {DupBuild.earlySolid} cells, finishing held {DupBuild.heldCompletions} frames");
							Debug.Log($"{ModContext.Tag} DupTileCases: {passed} passed, {failed} failed; closing the game");
							phase = 7;
							App.Quit();
						}
						break;
				}
			}
			catch (Exception e)
			{
				phase = 7;
				Debug.LogWarning($"{ModContext.Tag} DupTileCases: FAILED: stopped after an error: {e}");
			}
		}

		/// <summary>The third Duplicant is sent to one end of the room and back, under the tiles above the gap, every 6 s.</summary>
		private static void Walk(float now)
		{
			if (walker == null || now < lastWalk + 6f)
			{
				return;
			}
			lastWalk = now;
			walkRight = !walkRight;
			int target = Cell(walkRight ? 30 : 20, 1);
			ChoreProvider provider = walker.GetComponent<ChoreProvider>();
			walker.GetComponent<ChoreDriver>()?.StopChore();
			new MoveChore(provider, Db.Get().ChoreTypes.MoveTo, smi => target);
			walks++;
		}

		private static readonly Dictionary<GameObject, int> lastCell = new Dictionary<GameObject, int>();

		/// <summary>Each move of a scene Duplicant to another cell, with what the game thinks of that cell.</summary>
		private static void Trace()
		{
			foreach (GameObject dupe in new[] { builderA, builderB, occupant ?? released, walker })
			{
				if (dupe == null) continue;
				int cell = Grid.PosToCell(dupe);
				if (lastCell.TryGetValue(dupe, out int last) && last == cell) continue;
				lastCell[dupe] = cell;
				if (Grid.Solid[Grid.CellAbove(cell)] && Grid.Objects[Grid.CellAbove(cell), (int)ObjectLayer.FoundationTile] != null)
				{
					headInside++;
					Debug.LogWarning($"{ModContext.Tag} DupTileCases: {dupe.GetProperName()} stepped to {Where(cell)} with the head in a tile");
				}
				Navigator navigator = dupe.GetComponent<Navigator>();
				string nav = navigator == null ? "" : $" {navigator.CurrentNavType}{(navigator.IsMoving() ? " moving" : "")}";
				Debug.Log($"{ModContext.Tag} DupTileCases: {dupe.GetProperName()} {Where(last)} -> {Where(cell)}: solid {Grid.Solid[cell]}, foundation {Grid.Foundation[cell]}, " +
					$"{Grid.Element[cell].id}, head solid {Grid.Solid[Grid.CellAbove(cell)]};{nav}, errand {dupe.GetComponent<ChoreDriver>()?.GetCurrentChore()?.choreType?.Id ?? "none"}, " +
					$"y {dupe.transform.GetPosition().y - originY:F3}");
			}
		}

		private static string Where(int cell)
		{
			if (!Grid.IsValidCell(cell)) return "-";
			Grid.CellToXY(cell, out int x, out int y);
			return $"({x - originX},{y - originY})";
		}

		private static string Others()
		{
			var parts = new List<string>();
			foreach (GameObject dupe in new[] { builderA, builderB, occupant ?? released, walker })
			{
				if (dupe == null) continue;
				Navigator navigator = dupe.GetComponent<Navigator>();
				string next = navigator != null && navigator.IsMoving() && navigator.path.IsValid() && navigator.path.nodes.Count > 1 ? " stepping to " + Where(navigator.path.nodes[1].cell) : "";
				parts.Add($"{dupe.GetProperName()} {Where(Grid.PosToCell(dupe))}{next}");
			}
			return string.Join(", ", parts);
		}

		private static void Expect(string name, bool ok, string numbers)
		{
			if (ok) passed++;
			else failed++;
			Debug.Log($"{ModContext.Tag} DupTileCases: {name}: {(ok ? "ok" : "FAILED")} ({numbers})");
		}

		private static GameObject Spawn(int x, int y)
		{
			GameObject prefab = Assets.GetPrefab(MinionConfig.ID);
			MinionStartingStats stats;
			do
			{
				stats = new MinionStartingStats(GameTags.Minions.Models.Standard, is_starter_minion: false);
			}
			while (stats.Traits.Any(trait => trait.Id == "Narcolepsy" || trait.Id == "CantBuild" || trait.Id == "CantDig"));
			GameObject dupe = Util.KInstantiate(prefab);
			dupe.name = prefab.name;
			Immigration.Instance.ApplyDefaultPersonalPriorities(dupe);
			dupe.transform.SetLocalPosition(Grid.CellToPosCBC(Cell(x, y), Grid.SceneLayer.Move));
			dupe.SetActive(true);
			stats.Apply(dupe);
			dupe.GetMyWorld().SetDupeVisited();
			return dupe;
		}

		private static void Sandstone(int x, int y)
		{
			Element sandstone = ElementLoader.FindElementByHash(SimHashes.SandStone);
			sandstone.substance.SpawnResource(Grid.CellToPosCBC(Cell(x, y), Grid.SceneLayer.Ore), 2500f, Warm, byte.MaxValue, 0);
		}

		private static GameObject Order(int x, int y)
		{
			BuildingDef def = Assets.GetBuildingDef(TileConfig.ID);
			GameObject order = def.TryPlace(null, Grid.CellToPosCBC(Cell(x, y), Grid.SceneLayer.Building), Orientation.Neutral, new[] { SimHashes.SandStone.CreateTag() });
			if (order != null)
			{
				orders.Add(order);
			}
			else
			{
				Debug.LogWarning($"{ModContext.Tag} DupTileCases: could not place a tile order at {x},{y}");
			}
			return order;
		}

		private static bool FindSite()
		{
			WorldContainer world = ClusterManager.Instance.activeWorld;
			int minX = world.WorldOffset.x + 2, minY = world.WorldOffset.y + 2;
			int maxX = world.WorldOffset.x + world.WorldSize.x - SiteWidth - 2, maxY = world.WorldOffset.y + world.WorldSize.y - SiteHeight - 2;
			for (int pass = 0; pass < 3; pass++)
			{
				for (int y = minY; y <= maxY; y += 2)
				{
					for (int x = minX; x <= maxX; x += 3)
					{
						if (SiteFree(x, y, pass))
						{
							originX = x;
							originY = y;
							Debug.Log($"{ModContext.Tag} DupTileCases: site at {x},{y}, {SiteWidth} x {SiteHeight} cells");
							return true;
						}
					}
				}
			}
			return false;
		}

		// Pass 0: all rock, nothing there; 1: anything natural, nothing there; 2: no buildings or tiles.
		private static bool SiteFree(int x0, int y0, int pass)
		{
			for (int y = y0 - 1; y < y0 + SiteHeight + 1; y++)
			{
				for (int x = x0 - 1; x < x0 + SiteWidth + 1; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell) || Grid.Foundation[cell] || (pass == 0 && !Grid.IsSolidCell(cell)) ||
						World.Instance.zoneRenderData.GetSubWorldZoneType(cell) == ProcGen.SubWorld.ZoneType.Space)
					{
						return false;
					}
					for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
					{
						if (Grid.Objects[cell, layer] != null && (pass < 2 || layer == (int)ObjectLayer.Building || layer == (int)ObjectLayer.FoundationTile
							|| layer == (int)ObjectLayer.Backwall || layer == (int)ObjectLayer.Minion))
						{
							return false;
						}
					}
				}
			}
			return true;
		}
	}
}
