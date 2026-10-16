using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniLiquidMix
{
	/// <summary>
	/// Self-check of liquid mixtures on scenes the save may not contain: the file verify_liquid_cases.txt in the mod
	/// folder. Mixture scenes are checked only if mixtures are on, the density layer and pond scenes only with layers
	/// by density on (mod options); the rest go to the log as skipped. A few dozen ticks after loading, the mod finds free space in
	/// the active world, builds a row of closed Abyssalite rooms, sets up its own scene in each, waits and
	/// compares the mass of each liquid in the room (with mixture shares); for each scene the log gets the line
	/// "LiquidCases: name: ok" or "FAILED" with numbers. The world is changed in the process: the check is meant for a test run
	/// on a copy of the save.
	///
	/// Scenes: Water and Brine side by side (they mix, the mass of each is conserved, the composition evens out); Crude Oil and
	/// Petroleum likewise (the second group); Water and Crude Oil (do not mix, stay in layers); a mixture flowing off a ledge
	/// (portions of the mixture fall and land in a puddle, the mass of each liquid is conserved); an overfilled mixture (into a full
	/// room of Water and Brine more Brine is added, the cell overfills by more than one and a half times, the game pushes
	/// half of it up and distributes the top cell to the neighbours: the composition must go along with the mass); a freezing mixture (Water with
	/// Brine at 258 K: Water drops out as Ice, Brine stays liquid); a boiling mixture (Water and Brine half and half at
	/// 385 K under Oxygen: by the pressure mod's phase rules the mixture evaporates by Raoult's law, Water faster than Brine, Salt
	/// drops out; the mass of liquids, Steam and Salt together is conserved); layers by density (Crude Oil under Water, as the game places it
	/// by molar mass, must float up: it is lighter than Water); a freezing pond (Water slightly warmer than 4 °C under an Ice ceiling:
	/// the cold water stays on top, the bottom holds near 4 °C, and the top row freezes; checked later than the rest);
	/// a pump on a mixture (a liquid consumer, like a pump's, on a half-and-half mixture of Water and Brine: takes both liquids by
	/// their shares, the mass of each is conserved together with what was pumped out); Water and Nectar, Petroleum and Phyto Oil (the new group members
	/// mix); a heated mixture of Water and Brackene (Brackene in the mixture turns into Brine and Milk Fat, as in the game).
	/// </summary>
	internal static class LiquidCases
	{
		// Rooms in rows of six: a single long strip of rooms does not find room in a save.
		private const int Rooms = 21, RoomsPerRow = 7, RoomWidth = 5, RoomHeight = 3;
		private const int SiteWidth = 1 + RoomsPerRow * (RoomWidth + 1), SiteHeight = 1 + (Rooms + RoomsPerRow - 1) / RoomsPerRow * (RoomHeight + 1);
		private const int StartTick = 60, BuildTicks = 4, FillTicks = 6, WaitTicks = 600;
		// The pond is checked twice: with the rest (has stratified) and later (the top row has turned to Ice: Water in the game
		// freezes at 272.5 K, the transition happens 3 degrees lower).
		private const int PondTicks = 1550, Pond = 8;
		private static bool late;
		// Rooms with a mixture by the ledge hold Oxygen: in vacuum Water would boil by the pressure mod's phase rules. Cold,
		// so that there is almost no Steam above the puddle.
		private const float Warm = 300f, Cool = 276f, Air = 1.8f;

		private static int phase, originX, originY;
		private static long frozenAtStart;
		private static long phaseTick;
		private static readonly Dictionary<string, double> start = new Dictionary<string, double>();

		internal static void Apply(Harmony harmony)
		{
			if (!File.Exists(Path.Combine(ModContext.Folder, "verify_liquid_cases.txt")))
			{
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.LateUpdate)), postfix: new HarmonyMethod(typeof(LiquidCases), nameof(Frame)));
			Debug.Log($"{ModContext.Tag} LiquidCases: scenes will be built in the active world; the save is changed, use a copy");
		}

		private static void Frame()
		{
			if (phase > 5 || Game.Instance == null || !(LiquidSurvey.Mixing || LiquidSurvey.Density))
			{
				return;
			}
			long tick = SimHooks.GetValue("liquid-mix.ticks");
			try
			{
				if (phase == 0 && tick >= StartTick)
				{
					if (!FindSite())
					{
						Debug.LogWarning($"{ModContext.Tag} LiquidCases: FAILED: no free {SiteWidth} x {SiteHeight} place without buildings and items in the active world");
						phase = 6;
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
					MixOnLedge();
					Remember();
					BuildSensors();
					StartPump();
					Next(tick);
				}
				else if (phase == 3 && tick >= phaseTick + WaitTicks)
				{
					StopPump();
					Evaluate(room => room < MopRoom);
					WorldActions();
					phase = 4;
				}
				else if (phase == 4 && tick >= phaseTick + WaitTicks + WorldTicks)
				{
					// Game logic acted on mixtures right after the main check: its results are looked at a few ticks later.
					Evaluate(room => room >= MopRoom);
					phase = 5;
				}
				else if (phase == 5 && tick >= phaseTick + PondTicks)
				{
					late = true;
					Evaluate(room => room == Pond);
					Debug.Log($"{ModContext.Tag} LiquidCases: {passed} of {Rooms + 1 - skipped} scenes passed{(skipped > 0 ? $", {skipped} skipped" : "")}");
					phase = 6;
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} LiquidCases: FAILED with an exception in phase {phase}: {e}");
				phase = 6;
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
							Debug.Log($"{ModContext.Tag} LiquidCases: site at {x},{y} ({(pass == 0 ? "in rock" : "in the open")}), {SiteWidth} x {SiteHeight} cells");
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

		private static int RoomCell(int room, int dx, int dy)
		{
			return Grid.XYToCell(originX + 1 + room % RoomsPerRow * (RoomWidth + 1) + dx, originY + 1 + room / RoomsPerRow * (RoomHeight + 1) + dy);
		}

		private static void Set(int cell, SimHashes element, float mass, float temperature)
		{
			SimMessages.ReplaceElement(cell, element, CellEventLogger.Instance.DebugTool, mass, temperature, byte.MaxValue, 0);
		}

		private static void BuildWalls()
		{
			for (int y = 0; y < SiteHeight; y++)
			{
				for (int x = 0; x < SiteWidth; x++)
				{
					Set(Grid.XYToCell(originX + x, originY + y), SimHashes.Katairite, 1000f, Warm);
				}
			}
		}

		private static void ForRoom(int room, Action<int, int, int> visit)
		{
			for (int dy = 0; dy < RoomHeight; dy++)
			{
				for (int dx = 0; dx < RoomWidth; dx++)
				{
					visit(RoomCell(room, dx, dy), dx, dy);
				}
			}
		}

		// ---------- Scenes ----------
		// 0 Water and Brine, 1 Crude Oil and Petroleum, 2 Water and Crude Oil, 3 mixture by a ledge, 4 overfilled mixture, 5 freezing mixture,
		// 6 boiling mixture, 7 layers by density, 8 freezing pond, 9 pump on a mixture, 10 Water and Nectar, 11 Petroleum and
		// Phyto Oil, 12 heated mixture with Brackene.

		private static readonly string[] Names = { "water and brine side by side", "crude oil and petroleum side by side", "water and crude oil do not mix", "a mixture flowing off a ledge", "an overfilled mixture", "a freezing mixture", "a boiling mixture", "layers by density", "a freezing pond", "a pump on a mixture",
			"water and nectar side by side", "petroleum and phyto oil side by side", "a heated mixture with brackene",
			"heat capacity of a mixture", "a body in a mixture", "five liquids in one pool",
			"a plant in a mixture with some of its liquid", "a plant in a mixture with a trace of its liquid",
			"mopping a mixture", "a liquid tepidizer in a mixture", "food in a mixture with Polluted Brine" };
		private static readonly SimHashes[][] Liquids =
		{
			new[] { SimHashes.Water, SimHashes.Brine },
			new[] { SimHashes.CrudeOil, SimHashes.Petroleum },
			new[] { SimHashes.Water, SimHashes.CrudeOil },
			new[] { SimHashes.Water, SimHashes.Brine },
			new[] { SimHashes.Water, SimHashes.Brine },
			new[] { SimHashes.Water, SimHashes.Brine },
			new[] { SimHashes.Water, SimHashes.Brine },
			new[] { SimHashes.Water, SimHashes.CrudeOil },
			new[] { SimHashes.Water, SimHashes.Ice },
			new[] { SimHashes.Water, SimHashes.Brine },
			new[] { SimHashes.Water, SimHashes.SugarWater },
			new[] { SimHashes.Petroleum, SimHashes.PhytoOil },
			new[] { SimHashes.Water, SimHashes.Milk },
			new[] { SimHashes.Water, SimHashes.Ethanol },
			new[] { SimHashes.Water, SimHashes.Brine },
			new[] { SimHashes.Water, SimHashes.Ethanol },
			new[] { SimHashes.Water, SimHashes.Ethanol },
			new[] { SimHashes.Water, SimHashes.Ethanol },
			new[] { SimHashes.Water, SimHashes.Brine },
			new[] { SimHashes.Water, SimHashes.Ethanol },
			new[] { SimHashes.Water, SimHashes.MurkyBrine },
		};
		// Pond: Water slightly warmer than 4 °C under a very cold Ice ceiling, so that the top row has time to freeze.
		private const float PondWater = 278.5f, PondIce = 20f;
		private const float Overfill = 1500f;
		// Freezing mixture: 40% Water and 60% Brine, 1100 kg per cell (slightly less than full), below the freezing point
		// of Water and above that of Brine.
		private const float Freezing = 258f, FreezingCell = 1100f, FreezingWater = 0.4f;
		private const float Boiling = 385f, BoilingCell = 900f;
		// Without the pressure mod a mixture boils at the points of its liquids (the game's transition, 3 degrees above the boiling point):
		// Water above 376.15 K, Brine above 378.9 K. The room is then filled with Brine at 377 K: pure Water would boil
		// away entirely before the composition is set, while Brine is still liquid at this temperature.
		private const float BoilingOwn = 377f;
		private static long oreAtStart, evaporatedAtStart, boiledAtStart;

		private static void FillRooms()
		{
			// Rooms 0-2, 4, 10 and 11 are full: two columns of one liquid and three of the other, every cell full (the mass of a full cell).
			foreach (int room in new[] { 0, 1, 2, 4, 10, 11 })
			{
				Element left = ElementLoader.FindElementByHash(Liquids[room][0]), right = ElementLoader.FindElementByHash(Liquids[room][1]);
				ForRoom(room, (cell, dx, dy) => Set(cell, dx < 2 ? left.id : right.id, dx < 2 ? left.maxMass : right.maxMass, Warm));
			}
			// Room 8: two rows of Water at 5.35 °C, an Ice ceiling at 20 K.
			ForRoom(8, (cell, dx, dy) => Set(cell, dy < 2 ? SimHashes.Water : SimHashes.Ice, 1000f, dy < 2 ? PondWater : PondIce));
			// Room 7: a row of Crude Oil at the bottom, a row of Water above it, Oxygen on top.
			ForRoom(7, (cell, dx, dy) => Set(cell, dy == 0 ? SimHashes.CrudeOil : dy == 1 ? SimHashes.Water : SimHashes.Oxygen, dy == 0 ? 870f : dy == 1 ? 1000f : Air, Warm));
			// Room 6: two bottom rows of Water (will become a half-and-half mixture with Brine), Oxygen on top; everything hot.
			SimHashes boilingLiquid = LiquidSurvey.Boiling ? SimHashes.Brine : SimHashes.Water;
			float boilingTemperature = LiquidSurvey.Boiling ? BoilingOwn : Boiling;
			ForRoom(6, (cell, dx, dy) => Set(cell, dy < 2 ? boilingLiquid : SimHashes.Oxygen, dy < 2 ? BoilingCell : Air, boilingTemperature));
			// Room 12: Water, will become a half-and-half mixture with Brackene; hotter than the Brackene conversion point (353.15 K + 3).
			ForRoom(Heated, (cell, dx, dy) => Set(cell, SimHashes.Water, 1000f, HeatedTemperature));
			// Room 13: a floor of hot Copper under two rows of a cold mixture of Water and Ethanol.
			ForRoom(HeatRoom, (cell, dx, dy) => Set(cell, dy == 0 ? SimHashes.Copper : SimHashes.Water, dy == 0 ? CopperMass : 1000f, dy == 0 ? CopperTemperature : MixtureTemperature));
			// Room 15: a column of each of five liquids of the water group, every cell full: they mix into one pool of five.
			ForRoom(PoolRoom, (cell, dx, dy) => { Element liquid = ElementLoader.FindElementByHash(PoolLiquids[dx]); Set(cell, liquid.id, liquid.maxMass, Warm); });
			// Rooms 16 and 17: full of Ethanol (16) or Water (17) that becomes a mixture of both; a plant grows on the floor.
			ForRoom(PlantRoom, (cell, dx, dy) => Set(cell, SimHashes.Ethanol, 1000f, Warm));
			ForRoom(TraceRoom, (cell, dx, dy) => Set(cell, SimHashes.Water, 1000f, Warm));
			// Rooms 18-20: Water that becomes mixtures with Brine (mopping), Ethanol (a tepidizer, under Oxygen) and Polluted Brine (food).
			ForRoom(MopRoom, (cell, dx, dy) => Set(cell, SimHashes.Water, 1000f, Warm));
			ForRoom(TepidRoom, (cell, dx, dy) => Set(cell, dy < 2 ? SimHashes.Water : SimHashes.Oxygen, dy < 2 ? 1000f : Air, TepidTemperature));
			ForRoom(FoodRoom, (cell, dx, dy) => Set(cell, SimHashes.Water, 1000f, Cool));
			// Room 14: full of Water that becomes a mixture with Brine; one cell gets a body in it.
			ForRoom(BodyRoom, (cell, dx, dy) => Set(cell, SimHashes.Water, 1000f, Warm));
			// Room 9: Brine, will become a half-and-half mixture with Water; the pump is placed in the next phase.
			ForRoom(9, (cell, dx, dy) => Set(cell, SimHashes.Brine, PumpCell, Warm));
			// Room 5: Brine; the compositions (Water with Brine) are set in the next phase.
			ForRoom(5, (cell, dx, dy) => Set(cell, SimHashes.Brine, FreezingCell, Freezing));
			// Room 3: Oxygen; an Abyssalite ledge in the left cell of the middle row; a puddle of Brine on the bottom.
			ForRoom(3, (cell, dx, dy) =>
			{
				if (dx == 0 && dy == 1)
				{
					Set(cell, SimHashes.Katairite, 1000f, Cool);
				}
				else if (dy == 0)
				{
					Set(cell, SimHashes.Brine, 200f, Cool);
				}
				else
				{
					Set(cell, SimHashes.Oxygen, Air, Cool);
				}
			});
			// On the ledge: a cell of Water that will become a half-and-half mixture of Water and Brine. Until the composition is set, there is a wall
			// to its right: otherwise the Water would start flowing off pure.
			Set(RoomCell(3, 0, 2), SimHashes.Water, 600f, Cool);
			Set(RoomCell(3, 1, 2), SimHashes.Katairite, 1000f, Cool);
		}

		/// <summary>The cell on the ledge gets a composition: half Water, half Brine (through the same path as restoring from a save).</summary>
		private static void MixOnLedge()
		{
			int water = ElementLoader.FindElementByHash(SimHashes.Water).idx, brine = ElementLoader.FindElementByHash(SimHashes.Brine).idx;
			int milk = ElementLoader.FindElementByHash(SimHashes.Milk).idx, ethanol = ElementLoader.FindElementByHash(SimHashes.Ethanol).idx;
			var cells = new List<(int cell, int first, int second, float firstShare)> { (RoomCell(3, 0, 2), water, brine, 0.5f) };
			ForRoom(Heated, (cell, dx, dy) => cells.Add((cell, water, milk, 0.5f)));
			ForRoom(5, (cell, dx, dy) => cells.Add((cell, water, brine, FreezingWater)));
			ForRoom(9, (cell, dx, dy) => cells.Add((cell, water, brine, 0.5f)));
			ForRoom(6, (cell, dx, dy) => { if (dy < 2) cells.Add((cell, water, brine, 0.5f)); });
			ForRoom(HeatRoom, (cell, dx, dy) => { if (dy > 0) cells.Add((cell, water, ethanol, 0.5f)); });
			ForRoom(BodyRoom, (cell, dx, dy) => cells.Add((cell, water, brine, 0.5f)));
			ForRoom(PlantRoom, (cell, dx, dy) => cells.Add((cell, water, ethanol, PlantWater)));
			ForRoom(TraceRoom, (cell, dx, dy) => cells.Add((cell, water, ethanol, TraceWater)));
			ForRoom(MopRoom, (cell, dx, dy) => cells.Add((cell, water, brine, 0.5f)));
			ForRoom(TepidRoom, (cell, dx, dy) => { if (dy < 2) cells.Add((cell, water, ethanol, 0.5f)); });
			ForRoom(FoodRoom, (cell, dx, dy) => cells.Add((cell, water, ElementLoader.FindElementByHash(SimHashes.MurkyBrine).idx, 0.7f)));
			using (MemoryStream stream = new MemoryStream())
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				writer.Write(0x58494d4c);
				writer.Write(1);
				writer.Write(Grid.WidthInCells);
				writer.Write(Grid.HeightInCells);
				writer.Write(cells.Count);
				writer.Write(24 + 40 * cells.Count);
				foreach ((int cell, int first, int second, float firstShare) in cells)
				{
					writer.Write(cell);
					writer.Write(2);
					writer.Write(first);
					writer.Write(second);
					writer.Write(0);
					writer.Write(0);
					writer.Write(firstShare);
					writer.Write(1f - firstShare);
					writer.Write(0f);
					writer.Write(0f);
				}
				SimHooks.SendData("liquid-mix.restore", stream.ToArray());
			}
			// A body in the Water of one mixed cell: only its Water becomes Polluted Water (applied after the composition is set).
			byte[] conversion = new byte[12];
			BitConverter.GetBytes(RoomCell(BodyRoom, 2, 0)).CopyTo(conversion, 0);
			BitConverter.GetBytes(water).CopyTo(conversion, 4);
			BitConverter.GetBytes((int)ElementLoader.FindElementByHash(SimHashes.DirtyWater).idx).CopyTo(conversion, 8);
			SimHooks.SendData("liquid-mix.convert", conversion);
			// The composition is set at the start of the next tick, before cells move; the wall on the right is removed.
			Set(RoomCell(3, 1, 2), SimHashes.Oxygen, Air, Cool);
			// Brine is added into the middle bottom cell of the full room with the mixture.
			SimMessages.AddRemoveSubstance(RoomCell(4, 2, 0), SimHashes.Brine, CellEventLogger.Instance.ElementConsumerSimUpdate, Overfill, Warm, byte.MaxValue, 0);
		}

		// ---------- Comparison ----------

		/// <summary>Mass of each liquid in the room with mixture shares; the number of mixed cells and the spread of the second liquid's share.</summary>
		private static Dictionary<SimHashes, double> RoomLiquids(int room, SimHashes second, out int mixed, out float lowShare, out float highShare)
		{
			var totals = new Dictionary<SimHashes, double>();
			int count = 0;
			float low = 1f, high = 0f;
			byte[] query = new byte[LiquidMixture.CellQueryBytes];
			ForRoom(room, (cell, dx, dy) =>
			{
				Element element = Grid.Element[cell];
				if (!element.IsLiquid)
				{
					return;
				}
				float mass = Grid.Mass[cell];
				Array.Clear(query, 0, query.Length);
				BitConverter.GetBytes(cell).CopyTo(query, 0);
				int liquids = SimHooks.Query("liquid-mix.cell", query) == 0 ? BitConverter.ToInt32(query, 4) : 0;
				float share = element.id == second ? 1f : 0f;
				if (liquids >= 2)
				{
					count++;
					share = 0f;
					for (int i = 0; i < liquids; i++)
					{
						Element part = ElementLoader.elements[BitConverter.ToInt32(query, LiquidMixture.QueryElementAt + 4 * i)];
						float part_share = BitConverter.ToSingle(query, LiquidMixture.QueryShareAt + 4 * i);
						totals[part.id] = (totals.TryGetValue(part.id, out double sum) ? sum : 0.0) + part_share * mass;
						if (part.id == second)
						{
							share = part_share;
						}
					}
				}
				else
				{
					totals[element.id] = (totals.TryGetValue(element.id, out double sum) ? sum : 0.0) + mass;
				}
				low = Math.Min(low, share);
				high = Math.Max(high, share);
			});
			mixed = count;
			lowShare = low;
			highShare = high;
			return totals;
		}

		// Liquid sensors in the room of Water with Brine: one for Water, the other for Brine. Once the room has mixed, both
		// must see their liquid (its share is above the threshold), although only one of them is the main liquid in the cell.
		private static LogicElementSensor waterSensor, brineSensor;

		private static void BuildSensors()
		{
			if (!LiquidSurvey.Mixing)
			{
				return;
			}
			BuildingDef def = Assets.GetBuildingDef("LogicElementSensorLiquid");
			var materials = new List<Tag> { SimHashes.Copper.CreateTag() };
			waterSensor = def.Build(RoomCell(0, 1, 0), Orientation.Neutral, null, materials, Warm, playsound: false).GetComponent<LogicElementSensor>();
			brineSensor = def.Build(RoomCell(0, 3, 0), Orientation.Neutral, null, materials, Warm, playsound: false).GetComponent<LogicElementSensor>();
			waterSensor.GetComponent<Filterable>().SelectedTag = SimHashes.Water.CreateTag();
			brineSensor.GetComponent<Filterable>().SelectedTag = SimHashes.Brine.CreateTag();
		}

		/// <summary>The sensors see both liquids of the mixture; the composition snapshot for the logic matches the composition from the plugin.</summary>
		private static bool CheckSensors(out string text)
		{
			if (waterSensor == null || brineSensor == null)
			{
				text = ", sensors were not built";
				return false;
			}
			ushort water = ElementLoader.FindElementByHash(SimHashes.Water).idx, brine = ElementLoader.FindElementByHash(SimHashes.Brine).idx;
			int waterCell = Grid.PosToCell(waterSensor), brineCell = Grid.PosToCell(brineSensor);
			float waterShare = LiquidMixture.Share(waterCell, water), brineShare = LiquidMixture.Share(brineCell, brine);
			bool on = waterSensor.IsSwitchedOn && brineSensor.IsSwitchedOn;
			text = $"; sensors: water {(waterSensor.IsSwitchedOn ? "on" : "off")} in a cell of {Grid.Element[waterCell].id} (water share {waterShare:F3}), " +
				$"brine {(brineSensor.IsSwitchedOn ? "on" : "off")} in a cell of {Grid.Element[brineCell].id} (brine share {brineShare:F3}); {LiquidMixLogic.Stats()}";
			return on && waterShare >= LiquidMixture.PresenceShare && brineShare >= LiquidMixture.PresenceShare;
		}

		// ---------- Pump on a mixture ----------
		// A liquid consumer like a pump's (ElementConsumer: any liquid within radius 2, 10 kg/s), without a building or
		// power: only requests to the simulation. What and how much it took is visible from each portion.
		private const int PumpRoom = 9;
		private const float PumpCell = 1100f;
		// Heated mixture with Brackene: when heated, Brackene in the game becomes Brine (90%) and Milk Fat (10%).
		private const int Heated = 12;
		private const float HeatedTemperature = 360f;
		private static long convertedAtStart;
		// Heat capacity of a mixture: a floor of hot Copper gives heat to a cold mixture of Water and Ethanol; the heat
		// content of the room (by the mixture's own heat capacity) must stay the same.
		private const int HeatRoom = 13, BodyRoom = 14, PoolRoom = 15, PlantRoom = 16, TraceRoom = 17;
		// Plants: a Thimble Reed (Water and Polluted Water suit it, Ethanol does not) in a mixture where Water is 40% and
		// Ethanol is main: it suits the plant; in a mixture where Water is main but only 5%: it does not.
		private const string PlantId = "BasicFabricPlant";
		private const float PlantWater = 0.4f, TraceWater = 0.05f;
		private static GameObject plant, tracePlant;
		// Five liquids of one group in one pool: each keeps its mass (no liquid turns into another for lack of slots).
		private static readonly SimHashes[] PoolLiquids = { SimHashes.Water, SimHashes.DirtyWater, SimHashes.SaltWater, SimHashes.Brine, SimHashes.Ethanol };
		private const float CopperMass = 2000f, CopperTemperature = 330f, MixtureTemperature = 280f;
		private static double heatAtStart;
		private static long conversionsAtStart;

		// Game logic on mixtures, acted on after the main check and looked at WorldTicks later: a mop takes every liquid of
		// a Water and Brine mixture by share; a Liquid Tepidizer with a portion of energy boils off the Ethanol of a Water
		// and Ethanol mixture (the liquid closest to boiling), not Water; food in Water with 30% Polluted Brine is contaminated.
		private const int MopRoom = 18, TepidRoom = 19, FoodRoom = 20, WorldTicks = 20;
		private const float MopAmount = 40f, TepidTemperature = 330f, TepidEnergy = 2400f;
		private static readonly Dictionary<SimHashes, float> mopped = new Dictionary<SimHashes, float>();
		private static GameObject heater, food;
		private static double expectedBoil;

		private static void WorldActions()
		{
			if (!LiquidSurvey.Mixing)
			{
				return;
			}
			Moppable.MopCell(RoomCell(MopRoom, 2, 1), MopAmount, (info, data) =>
			{
				if (info.mass > 0f)
				{
					SimHashes id = ElementLoader.elements[info.elemIdx].id;
					mopped[id] = (mopped.TryGetValue(id, out float sum) ? sum : 0f) + info.mass;
				}
			});
			LiquidHeaterBubbleEmitter emitter = heater != null ? heater.GetComponent<LiquidHeaterBubbleEmitter>() : null;
			if (emitter != null)
			{
				Element ethanol = ElementLoader.FindElementByHash(SimHashes.Ethanol);
				int cells = emitter.building.PlacementCells.Length;
				expectedBoil = 0.0;
				foreach (int cell in emitter.building.PlacementCells)
				{
					float gap = ethanol.highTemp + 3f - Grid.Temperature[cell];
					expectedBoil += gap > 0f ? Math.Min(TepidEnergy / cells / (ethanol.specificHeatCapacity * gap), Grid.Mass[cell] * LiquidMixture.Share(cell, ethanol.idx)) : 0.0;
				}
				emitter.accruedEnergyKJ = TepidEnergy;
				emitter.Sim1000ms(1f);
			}
			GameObject prefab = Assets.GetPrefab(new Tag("MushBar"));
			if (prefab != null)
			{
				food = GameUtil.KInstantiate(prefab, Grid.CellToPosCCC(RoomCell(FoodRoom, 2, 0), Grid.SceneLayer.Ore), Grid.SceneLayer.Ore);
				food.SetActive(true);
			}
		}

		private static GameObject SpawnPlant(int room)
		{
			GameObject prefab = Assets.GetPrefab(new Tag(PlantId));
			if (prefab == null)
			{
				return null;
			}
			GameObject go = GameUtil.KInstantiate(prefab, Grid.CellToPosCBC(RoomCell(room, 2, 0), Grid.SceneLayer.BuildingFront), Grid.SceneLayer.BuildingFront);
			go.SetActive(true);
			return go;
		}

		/// <summary>Whether the plant finds its surroundings suitable, and its pressure (recomputed now).</summary>
		private static string PlantState(GameObject go, out bool safe)
		{
			safe = false;
			PressureVulnerable vulnerable = go != null ? go.GetComponent<PressureVulnerable>() : null;
			if (vulnerable == null)
			{
				return "no plant";
			}
			float pressure = vulnerable.GetExternalPressure();
			safe = vulnerable.testAreaElementSafe;
			int cell = Grid.PosToCell(go);
			return $"plant {(safe ? "suited" : "not suited")}, pressure {pressure:F1} kg, cell of {Grid.Element[cell].id} with {LiquidMixture.Share(cell, ElementLoader.FindElementByHash(SimHashes.Water).idx):F3} Water";
		}

		/// <summary>Heat content of the heat room, kJ: Copper plus the liquid, by the mixture's heat capacity or by the main liquid's.</summary>
		private static double HeatContent(bool mainOnly, out double copperTemperature)
		{
			double total = 0.0, copper = 0.0;
			int copperCells = 0;
			byte[] query = new byte[LiquidMixture.CellQueryBytes];
			ForRoom(HeatRoom, (cell, dx, dy) =>
			{
				Element element = Grid.Element[cell];
				float mass = Grid.Mass[cell], temperature = Grid.Temperature[cell];
				if (dy == 0 && element.id == SimHashes.Copper)
				{
					copper += temperature;
					copperCells++;
				}
				if (!element.IsLiquid || mainOnly)
				{
					total += mass * element.specificHeatCapacity * temperature;
					return;
				}
				// The plugin answers 1 while a tick holds the compositions: wait for it, a wrong heat capacity would spoil the sum.
				int result = 1;
				for (int tries = 0; tries < 200 && result == 1; tries++)
				{
					Array.Clear(query, 0, query.Length);
					BitConverter.GetBytes(cell).CopyTo(query, 0);
					result = SimHooks.Query("liquid-mix.cell", query);
					if (result == 1)
					{
						System.Threading.Thread.Sleep(1);
					}
				}
				int count = result == 0 ? BitConverter.ToInt32(query, 4) : 0;
				if (count < 2)
				{
					total += mass * element.specificHeatCapacity * temperature;
					return;
				}
				double heat = 0.0;
				for (int i = 0; i < count; i++)
				{
					heat += BitConverter.ToSingle(query, LiquidMixture.QueryShareAt + i * 4) * ElementLoader.elements[BitConverter.ToInt32(query, LiquidMixture.QueryElementAt + i * 4)].specificHeatCapacity;
				}
				total += mass * heat * temperature;
			});
			copperTemperature = copperCells > 0 ? copper / copperCells : 0.0;
			return total;
		}

		private static GameObject pump;
		private static double pumpWater, pumpBrine;
		private static int pumpEvents, pumpSwitches, pumpLast = -1;

		private static void StartPump()
		{
			if (!LiquidSurvey.Mixing)
			{
				return;
			}
			ushort water = ElementLoader.FindElementByHash(SimHashes.Water).idx, brine = ElementLoader.FindElementByHash(SimHashes.Brine).idx;
			pump = new GameObject("OniLiquidMixPumpProbe");
			pump.SetActive(false);
			pump.transform.SetPosition(Grid.CellToPosCCC(RoomCell(PumpRoom, 2, 0), Grid.SceneLayer.Building));
			pump.AddComponent<KPrefabID>();
			ElementConsumer consumer = pump.AddComponent<ElementConsumer>();
			consumer.configuration = ElementConsumer.Configuration.AllLiquid;
			consumer.consumptionRate = 10f;
			consumer.consumptionRadius = 2;
			consumer.storeOnConsume = false;
			consumer.showInStatusPanel = false;
			consumer.showDescriptor = false;
			consumer.ignoreActiveChanged = true;
			consumer.OnElementConsumed += info =>
			{
				pumpEvents++;
				if (info.removedElemIdx == water) pumpWater += info.mass;
				else if (info.removedElemIdx == brine) pumpBrine += info.mass;
				if (pumpLast >= 0 && pumpLast != info.removedElemIdx) pumpSwitches++;
				pumpLast = info.removedElemIdx;
			};
			pump.SetActive(true);
			consumer.EnableConsumption(true);
		}

		private static void StopPump()
		{
			if (pump != null)
			{
				pump.GetComponent<ElementConsumer>().EnableConsumption(false);
				UnityEngine.Object.Destroy(pump);
				pump = null;
			}
		}

		private static void Remember()
		{
			// The starting mass is taken from what was placed: the game cells update only in the next frame.
			start["0 Water"] = 6 * 1000.0;
			start["0 Brine"] = 9 * 1200.0;
			start["1 CrudeOil"] = 6 * ElementLoader.FindElementByHash(SimHashes.CrudeOil).maxMass;
			start["1 Petroleum"] = 9 * ElementLoader.FindElementByHash(SimHashes.Petroleum).maxMass;
			start["2 Water"] = 6 * 1000.0;
			start["2 CrudeOil"] = 9 * ElementLoader.FindElementByHash(SimHashes.CrudeOil).maxMass;
			start["3 Water"] = 300.0;
			start["3 Brine"] = 300.0 + 5 * 200.0;
			start["4 Water"] = 6 * 1000.0;
			start["4 Brine"] = 9 * 1200.0 + Overfill;
			start["5 Water"] = 15 * FreezingCell * FreezingWater;
			start["5 Brine"] = 15 * FreezingCell * (1f - FreezingWater);
			frozenAtStart = SimHooks.GetValue("liquid-mix.frozen-grams");
			start["8 Water"] = 10 * 1000.0;
			start["8 Ice"] = 0.0;
			start["7 Water"] = 5 * 1000.0;
			start["7 CrudeOil"] = 5 * 870.0;
			start["9 Water"] = 15 * PumpCell * 0.5;
			start["9 Brine"] = 15 * PumpCell * 0.5;
			start["10 Water"] = 6 * 1000.0;
			start["10 SugarWater"] = 9 * ElementLoader.FindElementByHash(SimHashes.SugarWater).maxMass;
			start["11 Petroleum"] = 6 * ElementLoader.FindElementByHash(SimHashes.Petroleum).maxMass;
			start["11 PhytoOil"] = 9 * ElementLoader.FindElementByHash(SimHashes.PhytoOil).maxMass;
			start["12 Water"] = 15 * 1000.0 * 0.5;
			start["12 Milk"] = 15 * 1000.0 * 0.5;
			start["13 Water"] = 10 * 1000.0 * 0.5;
			start["13 Ethanol"] = 10 * 1000.0 * 0.5;
			start["14 Water"] = 15 * 1000.0 * 0.5;
			start["14 Brine"] = 15 * 1000.0 * 0.5;
			start["15 Water"] = RoomHeight * 1000.0;
			start["16 Water"] = RoomWidth * RoomHeight * 1000.0 * PlantWater;
			start["16 Ethanol"] = RoomWidth * RoomHeight * 1000.0 * (1.0 - PlantWater);
			start["17 Water"] = RoomWidth * RoomHeight * 1000.0 * TraceWater;
			start["17 Ethanol"] = RoomWidth * RoomHeight * 1000.0 * (1.0 - TraceWater);
			start["18 Water"] = RoomWidth * RoomHeight * 1000.0 * 0.5;
			start["18 Brine"] = RoomWidth * RoomHeight * 1000.0 * 0.5;
			start["19 Water"] = RoomWidth * 2 * 1000.0 * 0.5;
			start["19 Ethanol"] = RoomWidth * 2 * 1000.0 * 0.5;
			start["20 Water"] = RoomWidth * RoomHeight * 1000.0 * 0.7;
			start["20 MurkyBrine"] = RoomWidth * RoomHeight * 1000.0 * 0.3;
			BuildingDef heaterDef = Assets.GetBuildingDef("LiquidHeater");
			heater = heaterDef != null ? heaterDef.Build(RoomCell(TepidRoom, 2, 0), Orientation.Neutral, null, new List<Tag> { SimHashes.Copper.CreateTag() }, TepidTemperature, playsound: false) : null;
			plant = SpawnPlant(PlantRoom);
			tracePlant = SpawnPlant(TraceRoom);
			start["15 Ethanol"] = RoomHeight * (double)ElementLoader.FindElementByHash(SimHashes.Ethanol).maxMass;
			// The cells are updated only in the next frame: the starting heat is counted from what was set.
			float copperHeat = ElementLoader.FindElementByHash(SimHashes.Copper).specificHeatCapacity;
			float mixtureHeat = 0.5f * (ElementLoader.FindElementByHash(SimHashes.Water).specificHeatCapacity + ElementLoader.FindElementByHash(SimHashes.Ethanol).specificHeatCapacity);
			heatAtStart = RoomWidth * (double)CopperMass * copperHeat * CopperTemperature + 2 * RoomWidth * 1000.0 * mixtureHeat * MixtureTemperature;
			conversionsAtStart = SimHooks.GetValue("liquid-mix.conversions");
			convertedAtStart = SimHooks.GetValue("liquid-mix.converted-out");
			start["6 Water"] = 10 * BoilingCell * 0.5;
			start["6 Brine"] = 10 * BoilingCell * 0.5;
			oreAtStart = SimHooks.GetValue("liquid-mix.ore-released-grams");
			evaporatedAtStart = SimHooks.GetValue("liquid-mix.evaporated-mixed-grams");
			boiledAtStart = SimHooks.GetValue("liquid-mix.boiled-grams");
		}

		/// <summary>Mass of items of this substance lying in the room's cells (dropped ore, Ice).</summary>
		private static double ItemsInRoom(int room, SimHashes element)
		{
			double sum = 0.0;
			ForRoom(room, (cell, dx, dy) =>
			{
				GameObject first = Grid.Objects[cell, (int)ObjectLayer.Pickupables];
				for (ObjectLayerListItem item = first != null ? first.GetComponent<Pickupable>()?.objectLayerListItem : null; item != null; item = item.nextItem)
				{
					PrimaryElement primary = item.gameObject != null ? item.gameObject.GetComponent<PrimaryElement>() : null;
					if (primary != null && primary.ElementID == element)
					{
						sum += primary.Mass;
					}
				}
			});
			return sum;
		}

		private static void Report(string name, bool ok, string numbers)
		{
			string line = $"{ModContext.Tag} LiquidCases: {name}: {(ok ? "ok" : "FAILED")} ({numbers})";
			if (ok)
			{
				Debug.Log(line);
			}
			else
			{
				Debug.LogWarning(line);
			}
		}

		private static int passed, skipped;

		private static void Evaluate(Predicate<int> rooms)
		{
			for (int room = 0; room < Rooms; room++)
			{
				if (!rooms(room))
				{
					continue;
				}
				// Water and Crude Oil do not mix with any options; the layers and the pond are about density, the rest about mixtures.
				bool checkable = room == 2 || (room == 7 || room == 8 ? LiquidSurvey.Density : LiquidSurvey.Mixing);
				if (!checkable)
				{
					Debug.Log($"{ModContext.Tag} LiquidCases: {Names[room]}: skipped ({(room == 7 || room == 8 ? "layers by density" : "mixtures")} off in the settings)");
					skipped++;
					continue;
				}
				SimHashes first = Liquids[room][0], second = Liquids[room][1];
				Dictionary<SimHashes, double> totals = RoomLiquids(room, second, out int mixed, out float low, out float high);
				double a = totals.TryGetValue(first, out double x) ? x : 0.0, b = totals.TryGetValue(second, out double y) ? y : 0.0;
				double a0 = start[$"{room} {first}"], b0 = start[$"{room} {second}"];
				double other = 0.0;
				foreach (KeyValuePair<SimHashes, double> entry in totals)
				{
					if (entry.Key != first && entry.Key != second)
					{
						other += entry.Value;
					}
				}
				// Steam above the puddle in room 3 (phase rules of the pressure mod) is counted together with Water.
				float steam = 0f;
				ForRoom(room, (cell, dx, dy) => steam += Grid.Element[cell].id == SimHashes.Steam ? Grid.Mass[cell] : 0f);
				// In the freezing mixture Water leaves as Ice: none of it should remain liquid, and as much froze as there was.
				double frozen = (SimHooks.GetValue("liquid-mix.frozen-grams") - frozenAtStart) / 1000.0;
				// In the boiling mixture everything is counted together: the liquids, the vapour above them (room gas beyond the Oxygen) and the dropped Salt.
				float gas = 0f;
				ForRoom(room, (cell, dx, dy) => gas += Grid.Element[cell].IsGas ? Grid.Mass[cell] : 0f);
				// Salt is counted as pieces lying in the room itself: the plugin counter is shared by the whole game.
				double ore = ItemsInRoom(room, SimHashes.Salt);
				// Without the pressure mod a mixture does not evaporate by the phase rules but boils away at the points of its liquids (this mod).
				// There is no less Steam from Water than from Brine (Water has the higher vapour pressure). Brine meanwhile loses more liquid
				// than it gave Steam: part of it leaves as Salt, so this is not visible from the Brine share in the liquid.
				Element brineElement = ElementLoader.FindElementByHash(SimHashes.Brine);
				double steamOfBrine = 1.0 - brineElement.highTempTransitionOreMassConversion;
				double evaporated = (SimHooks.GetValue("liquid-mix.evaporated-mixed-grams") - evaporatedAtStart + SimHooks.GetValue("liquid-mix.boiled-grams") - boiledAtStart) / 1000.0;
				// Pond: frozen Water lies as Ice in the water rows (the Ice ceiling is above them).
				float pondIce = 0f;
				ForRoom(room, (cell, dx, dy) => pondIce += room == Pond && dy < 2 && Grid.Element[cell].id == SimHashes.Ice ? Grid.Mass[cell] : 0f);
				// Heated mixture with Brackene: Brackene went into Brine and Milk Fat, Water stayed.
				double heatedBrine = room == Heated && totals.TryGetValue(SimHashes.Brine, out double hb) ? hb : 0.0;
				double milkFat = room == Heated ? ItemsInRoom(room, SimHashes.MilkFat) : 0.0;
				float moppedWater = mopped.TryGetValue(SimHashes.Water, out float mw) ? mw : 0f, moppedBrine = mopped.TryGetValue(SimHashes.Brine, out float mb) ? mb : 0f;
				bool kept = room == PoolRoom || room == TepidRoom || room == FoodRoom ? true : room == MopRoom
					? Math.Abs(a + moppedWater - a0) <= 0.003 * a0 + 0.5 && Math.Abs(b + moppedBrine - b0) <= 0.003 * b0 + 0.5 && other < 0.5
					: room == PlantRoom || room == TraceRoom
					? Math.Abs(a - a0) <= 0.003 * a0 + 0.5 && Math.Abs(b - b0) <= 0.003 * b0 + 0.5 && other < 0.5
					: room == BodyRoom
					? Math.Abs(a + other - a0) <= 0.003 * a0 + 0.5 && Math.Abs(b - b0) <= 0.003 * b0 + 0.5 && other > 100.0
					: room == Heated
					? b < 0.01 * b0 && Math.Abs(a - a0) <= 0.003 * a0 + 0.5 && Math.Abs(heatedBrine - 0.9 * b0) <= 0.005 * b0 + 0.5
						&& Math.Abs(milkFat - 0.1 * b0) <= 0.005 * b0 + 0.5 && other - heatedBrine < 0.5
					: room == 5
					? a < 0.01 * a0 && Math.Abs(frozen - a0) <= 0.01 * a0 && Math.Abs(b - b0) <= 0.003 * b0 + 0.5 && other < 0.5
					: room == 6
					? Math.Abs(a + b + (gas - RoomWidth * Air) + ore - (a0 + b0)) <= 0.0005 * (a0 + b0) + 0.5 && evaporated > 0.0 && (a0 - a) >= steamOfBrine * (b0 - b) - 0.5 && other < 0.5
					: room == PumpRoom
					? Math.Abs(a + b + pumpWater + pumpBrine - (a0 + b0)) <= 0.003 * (a0 + b0) + 0.5 && other < 0.5
					: Math.Abs(a + (room == 3 || room == 7 ? steam : 0f) + pondIce - a0) <= 0.003 * a0 + 0.5 && Math.Abs(b - b0) <= 0.003 * b0 + 0.5 && other < 0.5;
				bool ok = kept;
				string extra = "";
				if (room == 0 || room == 1 || room == 10 || room == 11)
				{
					// Mixed and evened out: the share of the second liquid is the same throughout the room.
					float expected = (float)(b0 / (a0 + b0));
					ok = ok && mixed > 0 && high - low < 0.05f && Math.Abs((low + high) / 2f - expected) < 0.05f;
					extra = $", {mixed} cells mixed, share of {second} {low:F3}-{high:F3} (whole room {expected:F3})";
					if (room == 0)
					{
						ok = CheckSensors(out string sensors) && ok;
						extra += sensors;
					}
				}
				else if (room == 2)
				{
					ok = ok && mixed == 0;
					extra = $", {mixed} cells mixed";
				}
				else if (room == MopRoom)
				{
					// The mop took both liquids, about half each.
					float total = moppedWater + moppedBrine;
					ok = ok && total > 0.5f * MopAmount && moppedWater > 0f && moppedBrine > 0f && Math.Abs(moppedWater / total - 0.5f) < 0.15f;
					extra = $", mopped {moppedWater:F1} kg of Water and {moppedBrine:F1} kg of Brine (asked {MopAmount:F0} kg); {mixed} cells mixed";
				}
				else if (room == TepidRoom)
				{
					// The Ethanol boiled off, the Water stayed.
					double boiled = b0 - b;
					ok = heater != null && expectedBoil > 1.0 && boiled > 0.3 * expectedBoil && boiled < 1.2 * expectedBoil + 0.5 && Math.Abs(a - a0) <= 0.003 * a0 + 0.5;
					extra = $", {TepidEnergy:F0} kJ boiled off {boiled:F1} kg of Ethanol (expected {expectedBoil:F1} kg), Water changed by {a - a0:+0.0;-0.0} kg; {mixed} cells mixed";
				}
				else if (room == FoodRoom)
				{
					Rottable.Instance rot = food != null ? food.GetSMI<Rottable.Instance>() : null;
					Rottable.RotAtmosphereQuality quality = rot != null ? Rottable.AtmosphereQuality(rot) : Rottable.RotAtmosphereQuality.Normal;
					int cell = food != null ? Grid.PosToCell(food) : -1;
					ok = ok && rot != null && quality == Rottable.RotAtmosphereQuality.Contaminating;
					extra = $", food {(rot != null ? quality.ToString() : "not spawned")} in a cell of {(cell >= 0 ? Grid.Element[cell].id.ToString() : "-")}; {mixed} cells mixed";
				}
				else if (room == PlantRoom || room == TraceRoom)
				{
					// With 40% Water the plant is suited and its pressure is only the Water's part; with 5% it is not suited.
					GameObject subject = room == PlantRoom ? plant : tracePlant;
					string state = PlantState(subject, out bool suited);
					ok = ok && subject != null && (room == PlantRoom ? suited : !suited);
					extra = $", {state}; {mixed} cells mixed";
				}
				else if (room == PoolRoom)
				{
					// Every liquid of the pool keeps its mass, and some cells hold all five.
					int five = 0;
					byte[] query = new byte[LiquidMixture.CellQueryBytes];
					ForRoom(room, (cell, dx, dy) =>
					{
						Array.Clear(query, 0, query.Length);
						BitConverter.GetBytes(cell).CopyTo(query, 0);
						if (SimHooks.Query("liquid-mix.cell", query) == 0 && BitConverter.ToInt32(query, 4) >= PoolLiquids.Length) five++;
					});
					var parts = new List<string>();
					foreach (SimHashes id in PoolLiquids)
					{
						double expected = RoomHeight * (double)ElementLoader.FindElementByHash(id).maxMass;
						double now = totals.TryGetValue(id, out double value) ? value : 0.0;
						ok = ok && Math.Abs(now - expected) <= 0.003 * expected + 0.5;
						parts.Add($"{id} {expected:F0} -> {now:F1} kg");
					}
					ok = ok && five > 0;
					extra = $", {string.Join(", ", parts)}; {mixed} cells mixed, {five} with all five liquids";
				}
				else if (room == HeatRoom)
				{
					double heatEnd = HeatContent(false, out double copperTemperature), heatMain = HeatContent(true, out _);
					double given = RoomWidth * (double)CopperMass * ElementLoader.FindElementByHash(SimHashes.Copper).specificHeatCapacity * (CopperTemperature - copperTemperature);
					ok = ok && given > 0.0 && CopperTemperature - copperTemperature > 1.0 && Math.Abs(heatEnd - heatAtStart) <= 0.1 * given;
					extra = $", Copper {CopperTemperature:F1} -> {copperTemperature:F2} K, heat given by the Copper {given:F0} kJ; heat content of the room by the mixture's heat capacity " +
						$"{heatEnd - heatAtStart:+0;-0} kJ against the start, by the main liquid's {heatMain - heatAtStart:+0;-0} kJ; {mixed} cells mixed; " +
						$"heat exchanges of mixed cells rescaled in the whole game {SimHooks.GetValue("liquid-mix.heat-scaled")}";
				}
				else if (room == BodyRoom)
				{
					extra = $", Polluted Water {other:F1} kg, {mixed} cells mixed; liquids converted by game logic since the scene started {SimHooks.GetValue("liquid-mix.conversions") - conversionsAtStart}";
				}
				else if (room == Heated)
				{
					extra = $", brine {heatedBrine:F1} kg (expected {0.9 * b0:F1}), milk fat lying in the room {milkFat:F1} kg (expected {0.1 * b0:F1}), " +
						$"{mixed} cells mixed; liquids of mixtures turned into another liquid of their group since the scene started {SimHooks.GetValue("liquid-mix.converted-out") - convertedAtStart} times";
				}
				else if (room == PumpRoom)
				{
					// The pump takes the liquids of the mixture by their shares: Water and Brine roughly equally, and both a little at a time; the composition
					// of what remains does not drift from one half.
					double taken = pumpWater + pumpBrine;
					double waterShare = taken > 0.0 ? pumpWater / taken : 0.0;
					ok = ok && pumpEvents > 0 && taken > 100.0 && Math.Abs(waterShare - 0.5) < 0.1 && mixed > 0 && Math.Abs((low + high) / 2f - 0.5f) < 0.1f;
					extra = $", pumped {taken:F1} kg in {pumpEvents} portions: water {pumpWater:F1} kg (share {waterShare:F3}), brine {pumpBrine:F1} kg, " +
						$"the liquid changed between portions {pumpSwitches} times; {mixed} cells still mixed, share of brine left {low:F3}-{high:F3}";
				}
				else if (room == 8)
				{
					// Water at the bottom stays near 4 °C, colder above, and Ice over time: the pond is not mixed.
					float bottom = 0f, top = 0f;
					int bottomCells = 0, topWater = 0;
					ForRoom(room, (cell, dx, dy) =>
					{
						if (Grid.Element[cell].id != SimHashes.Water) return;
						if (dy == 0) { bottom += Grid.Temperature[cell]; bottomCells++; }
						if (dy == 1) { top += Grid.Temperature[cell]; topWater++; }
					});
					bottom = bottomCells > 0 ? bottom / bottomCells : 0f;
					top = topWater > 0 ? top / topWater : 0f;
					int frozenCells = 0;
					ForRoom(room, (cell, dx, dy) => frozenCells += dy == 1 && Grid.Element[cell].id == SimHashes.Ice ? 1 : 0);
					// First stratification (without the anomaly convection would even out the rows), later Ice on top with a liquid bottom.
					ok = ok && bottomCells == RoomWidth && (topWater == 0 || top < bottom - 0.5f) && (late ? frozenCells > 0 : bottom > 274f);
					extra = $", bottom row {bottom:F2} K, top row {(topWater > 0 ? $"{top:F2} K" : "frozen")} ({topWater} cells of water, {frozenCells} of ice; frozen {pondIce:F1} kg), " +
						$"water convection mixed {SimHooks.GetValue("liquid-mix.convections")} times, cold water kept on top {SimHooks.GetValue("liquid-mix.convections-stable")} times in the whole game";
				}
				else if (room == 7)
				{
					// Crude Oil must float up: almost all the Water is at the bottom, almost no Crude Oil there. By mass, not by cells: a drop of Water
					// may stay among the Crude Oil if the bottom is already full (this happens in the game too).
					float waterBottom = 0f, oilBottom = 0f;
					int lowestOil = int.MaxValue, highestWater = -1;
					ForRoom(room, (cell, dx, dy) =>
					{
						if (Grid.Element[cell].id == SimHashes.CrudeOil) { lowestOil = Math.Min(lowestOil, dy); oilBottom += dy == 0 ? Grid.Mass[cell] : 0f; }
						if (Grid.Element[cell].id == SimHashes.Water) { highestWater = Math.Max(highestWater, dy); waterBottom += dy == 0 ? Grid.Mass[cell] : 0f; }
					});
					ok = ok && mixed == 0 && waterBottom >= 0.9 * a && oilBottom <= 0.1 * b;
					extra = $", on the bottom row {waterBottom:F1} kg of water and {oilBottom:F1} kg of crude oil, crude oil from row {lowestOil}, water up to row {highestWater} (rows from the bottom), steam in the room {steam:F3} kg; denser liquid moved below a lighter one in the whole game {SimHooks.GetValue("liquid-mix.layers-swapped")} times";
				}
				else if (room == 6)
				{
					extra = $", {mixed} cells mixed, gas above the oxygen {gas - RoomWidth * Air:F2} kg, salt lying in the room {ore:F2} kg (released in the whole game {(SimHooks.GetValue("liquid-mix.ore-released-grams") - oreAtStart) / 1000.0:F2} kg), " +
						$"liquid + gas + salt {a + b + (gas - RoomWidth * Air) + ore:F2} of {a0 + b0:F2} kg, evaporated or boiled out of mixtures since the scene started {evaporated:F2} kg ({(LiquidSurvey.Boiling ? "boiling by composition here" : "phase rules of the pressure mod")}), " +
						$"steam from water {a0 - a:F2} kg, from brine {steamOfBrine * (b0 - b):F2} kg, brine share of the liquid {b / Math.Max(a + b, 1e-9):F4} (was 0.5)";
				}
				else if (room == 5)
				{
					extra = $", {mixed} cells still mixed, frozen out of mixtures in the whole game since the scene started {frozen:F1} kg ({SimHooks.GetValue("liquid-mix.frozen-out")} times)";
				}
				else if (room == 3)
				{
					extra = $", {mixed} cells mixed, steam in the room {steam:F3} kg, drops split by composition so far {SimHooks.GetValue("liquid-mix.drop-pieces")}";
				}
				else
				{
					ok = ok && mixed > 0;
					extra = $", {mixed} cells mixed; so far in the whole game: push-ups of a mixture {SimHooks.GetValue("liquid-mix.push-ups")}, cells displaced to neighbours with their composition {SimHooks.GetValue("liquid-mix.displaced-mixed")}, " +
						$"wall break checks by the capacity of a mixture {SimHooks.GetValue("liquid-mix.breaks-scaled")}";
				}
				Report(Names[room] + (room == Pond && late ? ", later" : ""), ok, $"{first} {a0:F1} -> {a:F1} kg, {second} {b0:F1} -> {b:F1} kg, other liquids {other:F1} kg{extra}");
				passed += ok ? 1 : 0;
			}
		}
	}
}
