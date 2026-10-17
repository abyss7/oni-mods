using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniGasMix
{
	/// <summary>
	/// Self-check of the layer on scenes that may not exist in the save: the file verify_gas_cases.txt in the mod folder.
	/// A few dozen ticks after loading, the mod finds free space in the active world, builds a row of
	/// closed Abyssalite rooms there, sets up its own scene in each, waits and checks the outcome; for each scene a line
	/// "GasCases: name: ok" or "FAILED" with numbers goes to the log. The world changes in the process, so the check
	/// is meant for a test run on a copy of the save.
	///
	/// The scenes cover what an ordinary colony may not show during a run: conservation of each gas's mass when
	/// mixing, gas emission from a solid cell and evaporation from a liquid into a foreign gas, a simulation source in a foreign
	/// gas and its blocking by the mixture's pressure, condensation of a non-main gas with ore, two germ types meeting,
	/// a light gas escaping through a hole in the ceiling, a liquid arriving in a cell whose gas the simulation cannot displace.
	///
	/// For a scene whose outcome did not match, an inventory of the room (Describe) is appended to the log: what lies in it, what
	/// temperature it has, and where on the scene site the substances expected in the room turned up.
	/// </summary>
	internal static class GasLayerCases
	{
		// The rooms stand in two tiers of five columns. Lower tier: scenes 0-3 and the lower chamber of scene 7; upper:
		// scenes 4-6, scene 8 (above scene 3; only two cells are open from its room) and the upper chamber of scene 7
		// (above its lower one). The sixth column holds the phase rules scenes (9 and 10): they are set up only when the rules are enabled
		// by the pressure mod; scene 5 is then skipped (it checks condensation by a single temperature, as in the game).
		private const int Columns = 7, RoomWidth = 5, RoomHeight = 3;
		private const int SiteWidth = 1 + Columns * (RoomWidth + 1), SiteHeight = 2 * (RoomHeight + 1) + 1;
		// The seventh column holds the pressure scenes (11 and 12): set up only when the pressure is counted by molecules.
		private static readonly int[] RoomColumn = { 0, 1, 2, 3, 0, 1, 2, 4, 3, 5, 5, 6, 6 };
		private static readonly bool[] RoomUpper = { false, false, false, false, true, true, true, false, true, false, true, false, true };
		private static bool byPressure;
		// Pressure scenes: Hydrogen beside Oxygen; Oxygen warm below and cold above.
		private const float SideHydrogen = 0.4f, WarmBelow = 380f, ColdAbove = 260f;
		private const int StartTick = 60, BuildTicks = 4, FillTicks = 6, WaitTicks = 650;
		// After how many ticks from the start of waiting the inventory of the condensation room goes to the log: by then the drops
		// have fallen, and one can see where the liquid went before anything gets a chance to happen to it.
		private const int EarlyLookTicks = 40;
		private const float Warm = 300f, Cold = 100f;
		// Simulation limit: gas from a solid cell and from a liquid is emitted while the gas cell holds less than this.
		private const float OffGasLimit = 1.8f, OxygenOverWater = 1.7f;
		private const float BesidePocket = 20f, PocketWater = 5f;
		// Hydrogen of the pocket. By molecules the two cells must be at one pressure, otherwise the Hydrogen flows into the
		// Carbon Dioxide cell before the water comes: as much Hydrogen as gives the pressure of 20 kg of Carbon Dioxide.
		private static float PocketGas => byPressure ? BesidePocket * MolarMasses.Of(ElementLoader.FindElementByHash(SimHashes.Hydrogen))
			/ MolarMasses.Of(ElementLoader.FindElementByHash(SimHashes.CarbonDioxide)) : 2f;

		private static int phase, originX, originY;
		private static long phaseTick;
		private static readonly Dictionary<string, long> before = new Dictionary<string, long>();
		private static readonly Dictionary<string, float> start = new Dictionary<string, float>();
		private static readonly int[] emitterHandle = { -1, -1 };
		private static bool emitterBlocked, lookedEarly, phases;
		private static readonly SimHashes[] Moisture = { SimHashes.Steam, SimHashes.Water, SimHashes.Ice };
		private const float PoolWater = 200f, DenseSteam = 15f, SteamStart = 400f, DenseCarbon = 120f, CarbonStart = 250f;
		private static readonly SimHashes[] Carbon = { SimHashes.CarbonDioxide, SimHashes.LiquidCarbonDioxide, SimHashes.SolidCarbonDioxide };
		// What to watch around the condensation room: Sour Gas and everything its methane can become.
		private static readonly SimHashes[] Condensate = { SimHashes.SourGas, SimHashes.Methane, SimHashes.LiquidMethane, SimHashes.SolidMethane };

		private static readonly string[] Counters =
		{
			"gas-mix.emitter-mixed", "gas-mix.emitter-held", "gas-mix.arrivals", "gas-mix.condensed", "gas-mix.condensed-in-place", "gas-mix.condensed-drips",
			"gas-mix.condensed-ore-grams", "gas-mix.germ-fights", "gas-mix.lost-grams", "gas-mix.limit-raised", "gas-mix.limit-unrestored", "gas-mix.displaced-by-layer", "gas-mix.evaporated-grams",
		};

		internal static void Apply(Harmony harmony)
		{
			if (!File.Exists(Path.Combine(ModContext.Folder, "verify_gas_cases.txt")))
			{
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.LateUpdate)), postfix: new HarmonyMethod(typeof(GasLayerCases), nameof(Frame)));
			Debug.Log("[OniGasMix] GasCases: scenes will be built in the active world; the save is changed, use a copy");
		}

		internal static void GameLoaded()
		{
			phase = 0;
			phaseTick = 0;
			lookedEarly = false;
		}

		private static void Frame()
		{
			if (phase > 3 || Game.Instance == null || !GasLayer.Moving)
			{
				return;
			}
			long tick = SimHooks.GetValue("gas-mix.ticks");
			try
			{
				if (phase == 0 && tick >= StartTick)
				{
					if (!FindSite())
					{
						Debug.LogWarning($"[OniGasMix] GasCases: FAILED: no free {SiteWidth} x {SiteHeight} place without buildings and items in the active world");
						phase = 4;
						return;
					}
					BuildWalls();
					Next(tick);
				}
				else if (phase == 1 && tick >= phaseTick + BuildTicks)
				{
					FillRooms();
					Next(tick);
				}
				else if (phase == 2 && tick >= phaseTick + FillTicks)
				{
					AddDetails();
					Next(tick);
				}
				else if (phase == 3 && !lookedEarly && tick >= phaseTick + EarlyLookTicks)
				{
					lookedEarly = true;
					if (byPressure)
					{
						LookAtWarmRoom();
					}
					Debug.Log(phases ? $"[OniGasMix] GasCases: dense steam, {EarlyLookTicks} ticks in: {Describe(10, Moisture)}"
						: $"[OniGasMix] GasCases: condensation, {EarlyLookTicks} ticks in: {Describe(5, Condensate)}");
				}
				else if (phase == 3 && tick >= phaseTick + WaitTicks)
				{
					Evaluate();
					phase = 4;
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniGasMix] GasCases: FAILED with an exception in phase {phase}: {e}");
				phase = 4;
			}
		}

		private static void Next(long tick)
		{
			phase++;
			phaseTick = tick;
		}

		// ---------- Site and walls ----------

		private static bool FindSite()
		{
			WorldContainer world = ClusterManager.Instance.activeWorld;
			int minX = world.WorldOffset.x + 2, minY = world.WorldOffset.y + 2;
			int maxX = world.WorldOffset.x + world.WorldSize.x - SiteWidth - 2, maxY = world.WorldOffset.y + world.WorldSize.y - SiteHeight - 2;
			// First a site entirely in rock is searched for (nothing there can be harmed), then any site without buildings and items.
			for (int pass = 0; pass < 2; pass++)
			{
				for (int y = minY; y <= maxY; y += 2)
				{
					for (int x = minX; x <= maxX; x += 3)
					{
						if (SiteFree(x, y, rockOnly: pass == 0))
						{
							originX = x;
							originY = y;
							Debug.Log($"[OniGasMix] GasCases: site at {x},{y} ({(pass == 0 ? "in rock" : "in the open")}), {SiteWidth} x {SiteHeight} cells");
							return true;
						}
					}
				}
			}
			return false;
		}

		private static bool SiteFree(int x0, int y0, bool rockOnly)
		{
			for (int y = y0; y < y0 + SiteHeight; y++)
			{
				for (int x = x0; x < x0 + SiteWidth; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell) || Grid.Foundation[cell] || (rockOnly && !Grid.IsSolidCell(cell)))
					{
						return false;
					}
					// In the space zone the simulation itself reduces a cell's gas by 20 g per tick: the scenes there would empty out.
					if (World.Instance.zoneRenderData.GetSubWorldZoneType(cell) == ProcGen.SubWorld.ZoneType.Space)
					{
						return false;
					}
					for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
					{
						if (Grid.Objects[cell, layer] != null)
						{
							return false;
						}
					}
				}
			}
			return true;
		}

		private static int Cell(int x, int y)
		{
			return Grid.XYToCell(originX + x, originY + y);
		}

		/// <summary>
		/// A cell of a scene room: dx from 0 to RoomWidth - 1, dy from 0 to RoomHeight - 1; above: the chamber above the room
		/// (only scene 7 has one).
		/// </summary>
		private static int RoomCell(int room, int dx, int dy, bool above = false)
		{
			return Cell(RoomLeft(room) + dx, 1 + dy + (RoomUpper[room] || above ? RoomHeight + 1 : 0));
		}

		/// <summary>Grid column of the site where a scene's room begins.</summary>
		private static int RoomLeft(int room)
		{
			return 1 + RoomColumn[room] * (RoomWidth + 1);
		}

		private static void Set(int cell, SimHashes element, float mass, float temperature, byte germ = byte.MaxValue, int germs = 0)
		{
			SimMessages.ReplaceElement(cell, element, CellEventLogger.Instance.DebugTool, mass, temperature, germ, germs);
		}

		private static void Add(int cell, SimHashes element, float mass, float temperature)
		{
			SimMessages.AddRemoveSubstance(cell, element, CellEventLogger.Instance.ElementConsumerSimUpdate, mass, temperature, byte.MaxValue, 0);
		}

		private static void BuildWalls()
		{
			for (int y = 0; y < SiteHeight; y++)
			{
				for (int x = 0; x < SiteWidth; x++)
				{
					Set(Cell(x, y), SimHashes.Katairite, 1000f, Warm);
				}
			}
		}

		/// <summary>Wall cells around a scene room: a ring one cell thick.</summary>
		private static void ForRing(int room, Action<int> visit)
		{
			int left = RoomLeft(room) - 1, right = left + RoomWidth + 1;
			int bottom = RoomUpper[room] ? RoomHeight + 1 : 0, top = bottom + RoomHeight + 1;
			for (int y = bottom; y <= top; y++)
			{
				for (int x = left; x <= right; x++)
				{
					if (x == left || x == right || y == bottom || y == top)
					{
						visit(Cell(x, y));
					}
				}
			}
		}

		private static void ForRoom(int room, bool above, Action<int, int, int> visit)
		{
			for (int dy = 0; dy < RoomHeight; dy++)
			{
				for (int dx = 0; dx < RoomWidth; dx++)
				{
					visit(RoomCell(room, dx, dy, above), dx, dy);
				}
			}
		}

		// ---------- Scenes ----------
		// 0 mixing, 1 gas from a solid cell, 2 evaporation from a liquid, 3 source in a foreign gas,
		// 4 source blocked by the mixture's pressure, 5 condensation with ore, 6 two germ types, 7 hole in the ceiling,
		// 8 liquid in a cell whose gas has nowhere to go.

		private static void FillRooms()
		{
			byte slime = Db.Get().Diseases.GetIndex(Db.Get().Diseases.SlimeGerms.Id), food = Db.Get().Diseases.GetIndex(Db.Get().Diseases.FoodGerms.Id);
			phases = SimHooks.GetValue("gas-mix.phase") == 1;
			byPressure = MolarMasses.ByMolecules;
			if (byPressure)
			{
				// 11. Hydrogen in the left two columns, Oxygen in the right three, at one temperature. 12. Oxygen, 1.8 kg per cell
				// everywhere, the bottom row warm, the upper two cold. The walls of room 12 are insulated.
				ForRoom(11, false, (cell, dx, dy) => Set(cell, dx < 2 ? SimHashes.Hydrogen : SimHashes.Oxygen, dx < 2 ? SideHydrogen : 1.8f, Warm));
				ForRing(12, cell => SimMessages.SetInsulation(cell, 0.01f));
				ForRoom(12, false, (cell, dx, dy) => Set(cell, SimHashes.Oxygen, 1.8f, dy == 0 ? WarmBelow : ColdAbove));
			}
			if (phases)
			{
				// 9. Water under Oxygen, all at one temperature: the water evaporates while its vapour in the air is less
				// than the saturated vapour pressure holds. 10. Several times more Steam than fits at its
				// temperature: the excess condenses, the transition heat warms the room. The walls of both rooms are insulated.
				ForRing(9, cell => SimMessages.SetInsulation(cell, 0.01f));
				ForRing(10, cell => SimMessages.SetInsulation(cell, 0.01f));
				ForRoom(9, false, (cell, dx, dy) => Set(cell, dy == 0 ? SimHashes.Water : SimHashes.Oxygen, dy == 0 ? PoolWater : 1f, Warm));
				ForRoom(10, false, (cell, dx, dy) => Set(cell, SimHashes.Steam, DenseSteam, SteamStart));
			}
			ForRoom(0, false, (cell, dx, dy) => Set(cell, dx < 2 ? SimHashes.Oxygen : SimHashes.CarbonDioxide, dx < 2 ? 2f : 0.5f, Warm));
			// The simulation emits gas from a solid cell only for substances that have it recorded in the element table
			// (Oxylite, Polluted Dirt, Mud, Corium). Bleach Stone does not fit here: Chlorine is emitted not by a cell
			// but by a lying chunk, and that is done by the game, not the simulation.
			ForRoom(1, false, (cell, dx, dy) => Set(cell, SimHashes.CarbonDioxide, 1f, Warm));
			Set(Cell(RoomLeft(1) + 2, 0), SimHashes.OxyRock, 500f, Warm); // floor cell under the middle of the room
			// Oxygen above the Polluted Water at 1.7 kg each: up to the 1.8 kg limit, after which the water stops emitting gas,
			// 100 g per cell remain. The limit is counted over the whole mixture, and the Polluted Oxygen in the room accumulates
			// to about a kilogram; by the main gas alone it would accumulate about 6.5 kg (portions of 200 g).
			ForRoom(2, false, (cell, dx, dy) => Set(cell, dy == 0 ? SimHashes.DirtyWater : SimHashes.Oxygen, dy == 0 ? 200f : OxygenOverWater, Warm));
			ForRoom(3, false, (cell, dx, dy) => Set(cell, SimHashes.Oxygen, 1f, Warm));
			ForRoom(4, false, (cell, dx, dy) => Set(cell, SimHashes.Hydrogen, 0.3f, Warm));
			// The walls of the cold room get insulation, like Insulated Tile cells. Without it the Liquid Methane
			// boiled off before it could be counted: the simulation computes heat between cells by the geometric
			// mean of their thermal conductivities, between gas and solid also 25 times more, and a kilogram of Hydrogen
			// next to an Abyssalite wall that is 200 degrees warmer heats up by half a degree per tick.
			ForRing(5, cell => SimMessages.SetInsulation(cell, 0.01f));
			if (phases)
			{
				// With phase rules this room holds dense cold Carbon Dioxide: there is more of it than fits at
				// its temperature, the excess liquefies, and the transition heat warms the room.
				ForRoom(5, false, (cell, dx, dy) => Set(cell, SimHashes.CarbonDioxide, DenseCarbon, CarbonStart));
			}
			else
			{
				ForRoom(5, false, (cell, dx, dy) => Set(cell, SimHashes.Hydrogen, 1f, Cold));
			}
			ForRoom(6, false, (cell, dx, dy) => Set(cell, SimHashes.Oxygen, dx < 2 ? 2f : 0.5f, Warm, dx < 2 ? slime : food, 100000));
			// Lower chamber: Hydrogen under the ceiling, Oxygen below; one hole in the ceiling at the right wall; Oxygen above.
			ForRoom(7, false, (cell, dx, dy) => Set(cell, dy == RoomHeight - 1 ? SimHashes.Hydrogen : SimHashes.Oxygen, dy == RoomHeight - 1 ? 0.2f : 1.5f, Warm));
			ForRoom(7, true, (cell, dx, dy) => Set(cell, SimHashes.Oxygen, 1.5f, Warm));
			Set(Cell(RoomLeft(7) + RoomWidth - 1, RoomHeight + 1), SimHashes.Oxygen, 1.5f, Warm);
			// Two open cells in the wall: one with 2 kg of Hydrogen, the neighbouring one with 20 kg of Carbon Dioxide. With these
			// masses the main gas of both does not change while the scene waits: the simulation sees a foreign gas next to the Hydrogen.
			Set(RoomCell(8, 2, 1), SimHashes.Hydrogen, PocketGas, Warm);
			Set(RoomCell(8, 1, 1), SimHashes.CarbonDioxide, BesidePocket, Warm);
		}

		private static void AddDetails()
		{
			// Non-main gases are added the same way the game's buildings add them (with the "add substance" message).
			Add(RoomCell(0, 0, 1), SimHashes.Hydrogen, 0.1f, Warm);
			Add(RoomCell(0, 4, 1), SimHashes.Hydrogen, 0.1f, Warm);
			ForRoom(4, false, (cell, dx, dy) => Add(cell, SimHashes.Oxygen, 1.6f, Warm));
			if (!phases)
			{
				ForRoom(5, false, (cell, dx, dy) => Add(cell, SimHashes.SourGas, 0.2f, Cold));
			}
			// Water arrives in the Hydrogen cell the same way a falling drop adds it.
			Add(RoomCell(8, 2, 1), SimHashes.Water, PocketWater, Warm);
			// Simulation sources (like a geyser's): Hydrogen, 5 g every 0.2 s. In room 3 the pressure has room to grow,
			// in room 4 the limit is 1 kg: Hydrogen (the main gas) is 0.3 kg per cell, and the whole mixture 1.9 kg per cell.
			Emitter(0, RoomCell(3, 2, 1), 5f);
			Emitter(1, RoomCell(4, 2, 1), 1f);
			foreach (string counter in Counters)
			{
				before[counter] = SimHooks.GetValue(counter);
			}
			start["sour"] = 0.2f * RoomWidth * RoomHeight;
			start["hydrogen below"] = 0.2f * RoomWidth;
		}

		private static void Emitter(int index, int cell, float maxPressure)
		{
			int blocked = -1, unblocked = -1;
			if (index == 1)
			{
				blocked = Game.Instance.callbackManager.Add(new Game.CallbackInfo(() => emitterBlocked = true, manually_release: true)).index;
				unblocked = Game.Instance.callbackManager.Add(new Game.CallbackInfo(() => emitterBlocked = false, manually_release: true)).index;
			}
			int registered = Game.Instance.simComponentCallbackManager.Add((handle, data) =>
			{
				emitterHandle[index] = handle;
				SimMessages.ModifyElementEmitter(handle, cell, 1, SimHashes.Hydrogen, 0.2f, 0.005f, 320f, maxPressure, byte.MaxValue, 0);
			}, null, "GasCases").index;
			SimMessages.AddElementEmitter(maxPressure, registered, blocked, unblocked);
		}

		// ---------- Outcome ----------

		private static float RoomMass(int room, SimHashes element, bool above = false)
		{
			Element wanted = ElementLoader.FindElementByHash(element);
			float sum = 0f;
			ForRoom(room, above, (cell, dx, dy) =>
			{
				sum += wanted.IsGas ? GasMixture.Mass(cell, wanted.idx) : (Grid.ElementIdx[cell] == wanted.idx ? Grid.Mass[cell] : 0f);
			});
			return sum;
		}

		/// <summary>Average temperature of the room's cells: only gas cells or all non-empty ones.</summary>
		private static float RoomTemperature(int room, bool gasOnly)
		{
			float sum = 0f;
			int count = 0;
			ForRoom(room, false, (cell, dx, dy) =>
			{
				Element element = Grid.Element[cell];
				if (element.IsVacuum || (gasOnly && !element.IsGas))
				{
					return;
				}
				sum += Grid.Temperature[cell];
				count++;
			});
			return count > 0 ? sum / count : 0f;
		}

		/// <summary>
		/// How much Steam fits in a cell at temperature T by the phase rules: the mass at which the vapour pressure
		/// equals the saturated vapour pressure. Asked from the plugin: the numbers depend on the pressure mod's settings
		/// (pressure by mass or by molecules, substance numbers). -1 if the plugin did not answer.
		/// </summary>
		private static float Saturation(SimHashes gas, float temperature)
		{
			byte[] query = new byte[12];
			BitConverter.GetBytes((int)ElementLoader.FindElementByHash(gas).idx).CopyTo(query, 0);
			BitConverter.GetBytes(temperature).CopyTo(query, 4);
			return SimHooks.Query("gas-mix.saturation", query) == 0 ? BitConverter.ToSingle(query, 8) : -1f;
		}

		/// <summary>
		/// Kilograms of Oxygen at 20 degrees that give the pressure of this mass of gas at the scene temperature; by mass the
		/// mass itself.
		/// </summary>
		private static float Equivalent(SimHashes gas, float mass)
		{
			return byPressure ? mass * MolarMasses.Of(ElementLoader.FindElementByHash(SimHashes.Oxygen)) / MolarMasses.Of(ElementLoader.FindElementByHash(gas)) * Warm / 293.15f : mass;
		}

		private static float earlyBottom, earlyTop, earlyBottomWarm, earlyTopWarm, earlyLow, earlyHigh;

		/// <summary>Room 12 a few seconds in: the rows' masses and temperatures and the spread of pressure.</summary>
		private static void LookAtWarmRoom()
		{
			earlyBottom = earlyTop = earlyBottomWarm = earlyTopWarm = earlyHigh = 0f;
			earlyLow = float.MaxValue;
			ForRoom(12, false, (cell, dx, dy) =>
			{
				float mass = GasMixture.Mass(cell, SimHashes.Oxygen), equivalent = CellEquivalent(cell);
				earlyLow = Mathf.Min(earlyLow, equivalent);
				earlyHigh = Mathf.Max(earlyHigh, equivalent);
				if (dy == 0) { earlyBottom += mass; earlyBottomWarm += Grid.Temperature[cell] / RoomWidth; }
				if (dy == RoomHeight - 1) { earlyTop += mass; earlyTopWarm += Grid.Temperature[cell] / RoomWidth; }
			});
		}

		/// <summary>The cell's pressure as kilograms of Oxygen at 20 degrees, by its composition and temperature.</summary>
		private static float CellEquivalent(int cell)
		{
			float sum = 0f, oxygen = MolarMasses.Of(ElementLoader.FindElementByHash(SimHashes.Oxygen));
			GasMixture.Gases gases = GasMixture.Of(cell);
			while (gases.Next(out Element gas, out float mass))
			{
				sum += mass * oxygen / MolarMasses.Of(gas);
			}
			return sum * Grid.Temperature[cell] / 293.15f;
		}

		/// <summary>Mass of all gases of the cell by the mixture's composition.</summary>
		private static float CellGas(int cell)
		{
			float sum = 0f;
			GasMixture.Gases gases = GasMixture.Of(cell);
			while (gases.Next(out Element gas, out float mass))
			{
				sum += mass;
			}
			return sum;
		}

		private static long Delta(string counter)
		{
			return SimHooks.GetValue(counter) - before[counter];
		}

		private static bool Near(float value, float expected, float tolerance)
		{
			return Mathf.Abs(value - expected) <= expected * tolerance;
		}

		private static void Report(string name, bool ok, string numbers, int room = -1, params SimHashes[] watch)
		{
			Debug.Log($"[OniGasMix] GasCases: {name}: {(ok ? "ok" : "FAILED")} ({numbers})");
			if (!ok && room >= 0)
			{
				Debug.Log($"[OniGasMix] GasCases: {name}: {Describe(room, watch)}");
			}
		}

		/// <summary>How much substance the inventory has collected: mass, number of cells (or items, drops) and rows of the scene site.</summary>
		private sealed class Pile
		{
			internal float mass;
			internal int count, low = int.MaxValue, high = int.MinValue;
		}

		private static void Tally(SortedDictionary<string, Pile> piles, string name, float mass, int row)
		{
			if (!piles.TryGetValue(name, out Pile pile))
			{
				piles[name] = pile = new Pile();
			}
			pile.mass += mass;
			pile.count++;
			pile.low = Math.Min(pile.low, row);
			pile.high = Math.Max(pile.high, row);
		}

		private static string List(SortedDictionary<string, Pile> piles, string unit)
		{
			if (piles.Count == 0)
			{
				return "nothing";
			}
			var parts = new List<string>();
			foreach (KeyValuePair<string, Pile> pile in piles)
			{
				parts.Add($"{pile.Key} {pile.Value.mass:F4} kg in {pile.Value.count} {unit}, rows {pile.Value.low}..{pile.Value.high}");
			}
			return string.Join(", ", parts);
		}

		/// <summary>
		/// Inventory of a scene room and the site around it. In the room: all substances (gases by mixture composition) and the temperature
		/// range. Outside the room, on the whole scene site and three rows above it: only substances from watch (what
		/// was expected in the room could end up behind a wall or on the roof). Then items on the scene site and drops
		/// of substances from watch that are still falling. Rows are counted from the bottom row of the scene site.
		/// </summary>
		private static string Describe(int room, SimHashes[] watch)
		{
			var inside = new SortedDictionary<string, Pile>();
			var roomCells = new HashSet<int>();
			float coldest = float.MaxValue, warmest = 0f;
			ForRoom(room, false, (cell, dx, dy) =>
			{
				roomCells.Add(cell);
				int row = Grid.CellRow(cell) - originY;
				Element element = Grid.Element[cell];
				if (element.IsGas)
				{
					GasMixture.Gases gases = GasMixture.Of(cell);
					while (gases.Next(out Element gas, out float mass))
					{
						Tally(inside, gas.id.ToString(), mass, row);
					}
				}
				else if (!element.IsVacuum && element.id != SimHashes.Katairite)
				{
					Tally(inside, element.id.ToString(), Grid.Mass[cell], row);
				}
				if (!element.IsVacuum && element.id != SimHashes.Katairite)
				{
					coldest = Mathf.Min(coldest, Grid.Temperature[cell]);
					warmest = Mathf.Max(warmest, Grid.Temperature[cell]);
				}
			});

			var outside = new SortedDictionary<string, Pile>();
			const int Margin = 3;
			for (int y = -1; y < SiteHeight + Margin; y++)
			{
				for (int x = -1; x <= SiteWidth; x++)
				{
					int cell = Cell(x, y);
					if (!Grid.IsValidCell(cell) || roomCells.Contains(cell))
					{
						continue;
					}
					foreach (SimHashes hash in watch)
					{
						Element wanted = ElementLoader.FindElementByHash(hash);
						float mass = wanted.IsGas ? GasMixture.Mass(cell, wanted.idx) : (Grid.ElementIdx[cell] == wanted.idx ? Grid.Mass[cell] : 0f);
						if (mass > 0f)
						{
							Tally(outside, hash.ToString(), mass, y);
						}
					}
				}
			}

			var items = new SortedDictionary<string, Pile>();
			foreach (Pickupable item in Components.Pickupables.Items)
			{
				if (item == null || item.PrimaryElement == null)
				{
					continue;
				}
				Grid.PosToXY(item.transform.GetPosition(), out int x, out int y);
				if (x >= originX && x < originX + SiteWidth && y >= originY && y < originY + SiteHeight + Margin)
				{
					Tally(items, item.PrimaryElement.ElementID.ToString(), item.PrimaryElement.Mass, y - originY);
				}
			}

			var drops = new SortedDictionary<string, Pile>();
			FallingWater falling = FallingWater.instance;
			if (falling != null)
			{
				for (int i = 0; i < falling.physics.Count && i < falling.particleProperties.Count; i++)
				{
					Grid.PosToXY(falling.physics[i].position, out int x, out int y);
					if (x < originX - 1 || x > originX + SiteWidth || y < originY - 1 || y >= originY + SiteHeight + Margin)
					{
						continue;
					}
					Element element = ElementLoader.elements[falling.particleProperties[i].elementIdx];
					if (Array.IndexOf(watch, element.id) >= 0)
					{
						Tally(drops, element.id.ToString(), falling.particleProperties[i].mass, y - originY);
					}
				}
			}
			string temperature = warmest > 0f ? $"{coldest:F1}..{warmest:F1} K" : "none";
			return $"in the room: {List(inside, "cells")}; temperature {temperature}; outside the room: {List(outside, "cells")}; " +
				$"items on the site: {List(items, "items")}; drops in flight: {List(drops, "drops")}";
		}

		private static void Evaluate()
		{
			int cells = RoomWidth * RoomHeight;
			// 0. Mixing: the masses of the three gases are the same as placed; Oxygen has reached the right wall.
			float oxygen = RoomMass(0, SimHashes.Oxygen), carbon = RoomMass(0, SimHashes.CarbonDioxide), hydrogen = RoomMass(0, SimHashes.Hydrogen);
			float oxygenFar = GasMixture.Share(RoomCell(0, RoomWidth - 1, 1), SimHashes.Oxygen);
			Report("mixing keeps the mass of each gas", Near(oxygen, 2f * 2 * RoomHeight, 0.005f) && Near(carbon, 0.5f * 3 * RoomHeight, 0.005f) && Near(hydrogen, 0.2f, 0.005f) && oxygenFar > 0.05f,
				$"oxygen {oxygen:F4} of {2f * 2 * RoomHeight:F1} kg, carbon dioxide {carbon:F4} of {0.5f * 3 * RoomHeight:F1}, hydrogen {hydrogen:F4} of 0.2, oxygen share at the far wall {oxygenFar:P0}", 0);

			// 1. Oxylite in the floor releases Oxygen into a room with Carbon Dioxide: Oxygen has appeared, Carbon Dioxide is
			// in place. The 1.8 kg limit is counted over the whole mixture: Oxygen accumulates no more than what is missing up to the limit
			// (0.8 kg per cell), although the main gas of the cell above the Oxylite stays below the limit all the time.
			// Without this the Oxylite would have emitted 26 kg during the wait.
			oxygen = RoomMass(1, SimHashes.Oxygen);
			carbon = RoomMass(1, SimHashes.CarbonDioxide);
			float overSolid = CellGas(RoomCell(1, 2, 0));
			// By molecules the limit is the pressure: the room the Carbon Dioxide leaves, in kilograms of Oxygen.
			float oxygenRoom = (OffGasLimit - Equivalent(SimHashes.CarbonDioxide, 1f)) / Equivalent(SimHashes.Oxygen, 1f) * cells;
			Report("off-gassing from a solid cell into another gas, up to the pressure limit", oxygen > 1f && oxygen < oxygenRoom * 1.15f && Near(carbon, cells, 0.005f)
				&& Delta("gas-mix.limit-raised") > 0 && Delta("gas-mix.limit-unrestored") == 0,
				$"oxygen {oxygen:F3} kg (the limit leaves room for {oxygenRoom:F1}), carbon dioxide {carbon:F4} of {cells} kg, all gases in the cell over the solid {overSolid:F3} kg; " +
				$"the mass of the whole mixture shown {Delta("gas-mix.limit-raised")} times in all scenes, not restored {Delta("gas-mix.limit-unrestored")}", 1);

			// 2. Evaporation from Polluted Water: Polluted Oxygen has appeared, Oxygen is in place, and the emission stopped
			// at the limit over the whole mixture (margin above the limit: one 200 g portion per cell above the water).
			int overWater = RoomWidth * (RoomHeight - 1);
			oxygen = RoomMass(2, SimHashes.Oxygen);
			float polluted = RoomMass(2, SimHashes.ContaminatedOxygen);
			float pollutedRoom = (OffGasLimit - Equivalent(SimHashes.Oxygen, OxygenOverWater)) / Equivalent(SimHashes.ContaminatedOxygen, 1f) * overWater;
			Report("off-gassing from a liquid into another gas, up to the pressure limit", polluted > 0f && polluted < pollutedRoom + 0.2f * RoomWidth + 0.5f && Near(oxygen, OxygenOverWater * overWater, 0.005f),
				$"polluted oxygen {polluted:F3} kg (the limit leaves room for {pollutedRoom:F1}), oxygen {oxygen:F4} of {OxygenOverWater * overWater:F1} kg; arrivals kept the mixture {Delta("gas-mix.arrivals")} times in all scenes", 2);

			// 3. Simulation source in a foreign gas: Hydrogen is mixed in, Oxygen is in place.
			oxygen = RoomMass(3, SimHashes.Oxygen);
			hydrogen = RoomMass(3, SimHashes.Hydrogen);
			Report("sim emitter into another gas", hydrogen > 0.5f && Near(oxygen, cells, 0.005f) && Delta("gas-mix.emitter-mixed") > 0,
				$"hydrogen {hydrogen:F3} kg, oxygen {oxygen:F4} of {cells} kg, mixed into another gas {Delta("gas-mix.emitter-mixed")} times", 3);

			// 4. Source blocked by the mixture's pressure: no Hydrogen was added, the game received the "blocked" notification.
			hydrogen = RoomMass(4, SimHashes.Hydrogen);
			Report("sim emitter blocked by the pressure of the mixture", Near(hydrogen, 0.3f * cells, 0.005f) && emitterBlocked,
				$"hydrogen {hydrogen:F4} of {0.3f * cells:F1} kg, blocked callback {(emitterBlocked ? "came" : "DID NOT COME")}, held back {Delta("gas-mix.emitter-held")} times", 4);

			// 5. Condensation of a non-main gas with ore: no Sour Gas is left, Liquid Methane has appeared (two thirds
			// of the mass; some drops may fall outside the room, hence the lower threshold), Sulfur has dropped out.
			if (phases)
			{
				Debug.Log("[OniGasMix] GasCases: condensation of a gas that is not the main one, with ore: skipped (the phase rules are on: see the steam scenes)");
				// 5 with phase rules. 120 kg of Carbon Dioxide per cell at 250 K: above the limit with any settings.
				// The excess liquefies and collects at the bottom, and the room warms up: the drop leaves cold, and the transition
				// heat stays with the gas. The gas is either saturated at the new temperature, or has warmed up to the critical one
				// (304 K), above which it does not liquefy at all: then more than the limit remains, and there is nothing to compare
				// with the limit.
				float carbonGas = RoomMass(5, SimHashes.CarbonDioxide), carbonLiquid = RoomMass(5, SimHashes.LiquidCarbonDioxide), carbonWarm = RoomTemperature(5, gasOnly: true);
				int carbonCells = 0;
				ForRoom(5, false, (cell, dx, dy) => carbonCells += Grid.Element[cell].IsGas ? 1 : 0);
				float carbonAll = DenseCarbon * cells, carbonHolds = Saturation(SimHashes.CarbonDioxide, carbonWarm) * carbonCells;
				bool atCritical = Saturation(SimHashes.CarbonDioxide, carbonWarm + 0.5f) > 1e20f;
				Report("dense cold carbon dioxide liquefies and warms the room", carbonLiquid > 0.03f * carbonAll && Near(carbonGas + carbonLiquid, carbonAll, 0.01f) && carbonWarm > CarbonStart + 10f
					&& (atCritical || (carbonGas < 1.1f * carbonHolds && carbonGas > 0.6f * carbonHolds)) && Saturation(SimHashes.CarbonDioxide, 310f) > 1e20f,
					$"gas {carbonGas:F1} kg in {carbonCells} cells ({(atCritical ? "at the critical temperature, no limit" : $"they hold {carbonHolds:F1} kg")} at {carbonWarm:F1} K), liquid {carbonLiquid:F1} kg, together {carbonGas + carbonLiquid:F1} of {carbonAll:F0} kg, " +
					$"started at {CarbonStart:F0} K; at 300 K a cell holds {Saturation(SimHashes.CarbonDioxide, 300f):F0} kg, at 310 K there is no limit", 5, Carbon);
			}
			else
			{
				float sour = RoomMass(5, SimHashes.SourGas), methane = RoomMass(5, SimHashes.LiquidMethane);
				long ore = Delta("gas-mix.condensed-ore-grams");
				Report("condensation of a gas that is not the main one, with ore", sour < 0.05f * start["sour"] && methane > 0.3f * start["sour"] && Near(ore, 330f * start["sour"], 0.1f),
					$"sour gas left {sour * 1000f:F1} g of {start["sour"] * 1000f:F0}, liquid methane in cells {methane:F3} of {0.67f * start["sour"]:F3} kg, sulfur {ore} g; portions {Delta("gas-mix.condensed")}, " +
					$"as drops {Delta("gas-mix.condensed-drips")}, right in the cell {Delta("gas-mix.condensed-in-place")}", 5, Condensate);
			}

			// 6. Two germ types: they met in the cells, a single type remained in each.
			Report("two kinds of germs meet", Delta("gas-mix.germ-fights") > 0, $"met {Delta("gas-mix.germ-fights")} times");

			// 7. Hole in the ceiling: part of the Hydrogen went into the upper chamber.
			float below = RoomMass(7, SimHashes.Hydrogen), above = RoomMass(7, SimHashes.Hydrogen, above: true);
			Report("light gas leaves through a gap in the ceiling", above > 0.03f * start["hydrogen below"] && Near(below + above + GasMixture.Mass(Cell(RoomLeft(7) + RoomWidth - 1, RoomHeight + 1), SimHashes.Hydrogen), start["hydrogen below"], 0.01f),
				$"hydrogen below {below * 1000f:F1} g, above {above * 1000f:F1} g of {start["hydrogen below"] * 1000f:F0}", 7);

			// 8. Water arrived in a Hydrogen cell whose only neighbour is a Carbon Dioxide cell. The simulation
			// cannot displace the Hydrogen (it needs a neighbour with the same gas or vacuum) and would destroy it together with
			// the same mass of water. With the layer the Hydrogen goes into the neighbouring cell's mixture, and all the water stays.
			int pocket = RoomCell(8, 2, 1), beside = RoomCell(8, 1, 1);
			float water = Grid.Element[pocket].id == SimHashes.Water ? Grid.Mass[pocket] : 0f;
			float hydrogenBeside = GasMixture.Mass(beside, SimHashes.Hydrogen), carbonBeside = GasMixture.Mass(beside, SimHashes.CarbonDioxide);
			// With phase rules some of the water manages to evaporate into the neighbouring cell: the tolerance is wider.
			Report("liquid enters a cell whose gas the sim cannot displace", Near(water, PocketWater, phases ? 0.03f : 0.01f) && Near(hydrogenBeside, PocketGas, 0.01f) && Near(carbonBeside, BesidePocket, 0.01f)
				&& Delta("gas-mix.displaced-by-layer") > 0,
				$"water {water:F3} of {PocketWater:F1} kg, in the cell beside: hydrogen {hydrogenBeside:F3} of {PocketGas:F3} kg, carbon dioxide {carbonBeside:F3} of {BesidePocket:F1} kg; " +
				$"gas taken by the layer {Delta("gas-mix.displaced-by-layer")} times in all scenes", 8);

			if (phases)
			{
				// 9. Water evaporates into the air until saturation. The mass of water and vapour together is unchanged; there is no more vapour in the air
				// than fits at the room temperature, and during the wait at least half of that
				// has accumulated.
				int air = RoomWidth * (RoomHeight - 1);
				float pool = RoomMass(9, SimHashes.Water), vapour = RoomMass(9, SimHashes.Steam), roomWarm = RoomTemperature(9, gasOnly: true);
				float limit = Saturation(SimHashes.Steam, roomWarm) * air;
				Report("water evaporates into the air up to saturation", vapour > 0.5f * limit && vapour < 1.15f * limit && Mathf.Abs(pool + vapour - PoolWater * RoomWidth) < 0.05f
					&& Delta("gas-mix.evaporated-grams") > 0,
					$"steam in the air {vapour:F4} kg (the air holds {limit:F4} kg at {roomWarm:F1} K), water {pool:F3} kg, together {pool + vapour:F3} of {PoolWater * RoomWidth:F0} kg; " +
					$"evaporated in all scenes {Delta("gas-mix.evaporated-grams") / 1000f:F3} kg", 9, Moisture);

				// 10. Dense Steam: the excess condenses, the room is warmed by the transition heat, the water collects at the bottom.
				// Without phase rules nothing happens to Steam at 400 K. How much Steam remains depends on the settings
				// of the pressure mod, so it is compared with the limit at the resulting temperature: the Steam in gas cells
				// is no more than they hold, and not much less (the water at the bottom is still warming up).
				float dense = RoomMass(10, SimHashes.Steam), rain = RoomMass(10, SimHashes.Water), hot = RoomTemperature(10, gasOnly: true);
				float all = DenseSteam * cells, holds = Saturation(SimHashes.Steam, hot) * RoomWidth * (RoomHeight - 1);
				Report("steam above saturation condenses and heats the room", dense < 0.97f * all && rain > 0.03f * all && Near(dense + rain, all, 0.01f) && hot > SteamStart + 20f
					&& dense < 1.1f * holds && dense > 0.6f * holds,
					$"steam {dense:F2} kg (the room holds {holds:F2} kg at {hot:F1} K), water {rain:F2} kg, together {dense + rain:F2} of {all:F0} kg, started at {SteamStart:F0} K; " +
					$"portions {Delta("gas-mix.condensed")}, as drops {Delta("gas-mix.condensed-drips")}, right in the cell {Delta("gas-mix.condensed-in-place")}", 10, Moisture);
			}

			if (byPressure)
			{
				// 11. Pressure evens out: the mass of each gas is unchanged; the cells' pressures (equivalent masses by the
				// composition) differ little, the game sees each cell's pressure as its mass, and the Hydrogen, spread over the
				// room, takes far less mass per cell than the Oxygen.
				float side = RoomMass(11, SimHashes.Hydrogen), rest = RoomMass(11, SimHashes.Oxygen);
				float least = float.MaxValue, most = 0f, worstShown = 0f;
				ForRoom(11, false, (cell, dx, dy) =>
				{
					float equivalent = CellEquivalent(cell);
					least = Mathf.Min(least, equivalent);
					most = Mathf.Max(most, equivalent);
					worstShown = Mathf.Max(worstShown, Mathf.Abs(Grid.Mass[cell] - equivalent) / equivalent);
				});
				Report("by molecules the layer evens out pressure, not mass", Near(side, SideHydrogen * 2 * RoomHeight, 0.005f) && Near(rest, 1.8f * 3 * RoomHeight, 0.005f)
					&& most / least < 1.25f && worstShown < 0.02f,
					$"hydrogen {side:F4} of {SideHydrogen * 2 * RoomHeight:F1} kg, oxygen {rest:F3} of {1.8f * 3 * RoomHeight:F1}; pressure across the room from {least:F3} to {most:F3} kg of oxygen; " +
					$"the game's cell mass differs from the pressure by up to {worstShown:P1}", 11);

				// 12. Warm below, cold above. Early, while the rows still differ in temperature, at one pressure the warm bottom
				// row held less mass than the cold top one (LookAtWarmRoom). By now heat has evened out (the simulation's own
				// conduction between cells takes part too), the rows hold equal masses and the pressures are equal.
				float bottom = 0f, top = 0f, warmBottom = 0f, coldTop = 0f, low = float.MaxValue, high = 0f;
				ForRoom(12, false, (cell, dx, dy) =>
				{
					float mass = GasMixture.Mass(cell, SimHashes.Oxygen), equivalent = CellEquivalent(cell);
					low = Mathf.Min(low, equivalent);
					high = Mathf.Max(high, equivalent);
					if (dy == 0) { bottom += mass; warmBottom += Grid.Temperature[cell]; }
					if (dy == RoomHeight - 1) { top += mass; coldTop += Grid.Temperature[cell]; }
				});
				warmBottom /= RoomWidth;
				coldTop /= RoomWidth;
				Report("by molecules warm gas holds less mass", earlyBottom < 0.97f * earlyTop && earlyHigh / earlyLow < 1.25f && high / low < 1.25f && Mathf.Abs(warmBottom - coldTop) < 20f
					&& Near(RoomMass(12, SimHashes.Oxygen), 1.8f * cells, 0.005f),
					$"{EarlyLookTicks} ticks in: bottom row {earlyBottom:F3} kg at {earlyBottomWarm:F1} K, top row {earlyTop:F3} kg at {earlyTopWarm:F1} K, pressure from {earlyLow:F3} to {earlyHigh:F3} kg of oxygen; " +
					$"at the end: bottom row {bottom:F3} kg at {warmBottom:F1} K (started at {WarmBelow:F0}), top row {top:F3} kg at {coldTop:F1} K (started at {ColdAbove:F0}), pressure from {low:F3} to {high:F3}", 12);
			}

			Report("nothing was lost by the layer", Delta("gas-mix.lost-grams") == 0, $"lost {Delta("gas-mix.lost-grams")} g");
			foreach (int handle in emitterHandle)
			{
				if (handle != -1 && Sim.IsValidHandle(handle))
				{
					SimMessages.RemoveElementEmitter(-1, handle);
				}
			}
		}
	}
}
