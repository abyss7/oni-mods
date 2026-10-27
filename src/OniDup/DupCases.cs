using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Klei.AI;
using OniCommon;
using UnityEngine;

namespace OniDup
{
	/// <summary>
	/// Self-check of the mod on scenes the save may not contain: the file verify_dup_cases.txt in the mod folder. A few seconds
	/// after loading, the mod finds free space (in rock if it can) in the active world, builds a block of closed Abyssalite rooms, fills each
	/// with its own air, puts a new Duplicant in each and checks what happens to them; for each scene the log gets the line
	/// "DupCases: name: ok" or "FAILED" with numbers. The world is changed and Duplicants are added (some of them die): the check
	/// is meant for a test run on a copy of the save.
	///
	/// Scenes: normal air; Carbon Dioxide at 2, 5, 8 and 16 kPa (stuffy air, a headache, poisoning, passing out and death
	/// from Carbon Dioxide); thin air at the four steps of the Oxygen scale; a vacuum (exposure, passing out, damage, death from
	/// it); Oxygen at 3.3 atm (toxicity after half a cycle, seizures); a sharp drop of pressure (decompression injury, about 6 damage);
	/// buried to the waist and over the head (stays in place, breathing with the option off and on, freed when dug out); a dig
	/// two cells away with a wall that goes up on the way or as the work starts (no work through the wall); an oxygen mask in
	/// air poor in oxygen, in Carbon Dioxide and in a vacuum (helps in the first two only); Atmo, Jet and Lead suits taken
	/// into a vacuum (nothing happens); the head under water (left to the game); a building's air supply (like a telescope's)
	/// in a vacuum (nothing happens); a bionic Duplicant in Carbon Dioxide (not affected yet); a dig beside oneself in a narrow
	/// tunnel (no injury) and into a vacuum pocket (an injury). Scenes whose equipment or
	/// Duplicant model the game lacks (a DLC is off) are skipped.
	/// </summary>
	internal static class DupCases
	{
		private const int Rooms = 30, RoomsPerRow = 6, RoomWidth = 5, RoomHeight = 3;
		private const int SiteWidth = 1 + RoomsPerRow * (RoomWidth + 1), SiteHeight = 1 + (Rooms + RoomsPerRow - 1) / RoomsPerRow * (RoomHeight + 1);
		private const float Warm = 300f, StartAfter = 8f;
		/// <summary>Oxygen at one atmosphere, by the pressure scale in use (with the pressure mod 1.8 kg is not 101 kPa).</summary>
		private static float AirMass => MassFor(SimHashes.Oxygen, 101.325f);
		private const int Normal = 0, Stuffy = 1, Headache = 2, Poisoning = 3, PassingOut = 4, Thin = 5, Lightheaded = 6, Gasping = 7, NoOxygen = 8,
			Vacuum = 9, Toxic = 10, Drop = 11, Waist = 12, Head = 13, DigOnTheWay = 14, DigAtWork = 15,
			MaskPoorAir = 16, MaskCarbon = 17, MaskVacuum = 18, AtmoSuit = 19, JetSuit = 20, LeadSuit = 21, UnderWater = 22, AirSupply = 23, Bionic = 24,
			Pill = 25, PillWithHat = 26, PillColonist = 27, TunnelDig = 28, VacuumBreach = 29;
		private static readonly string[] Names =
		{
			"normal air", "stuffy air (CO2 2 kPa)", "a CO2 headache (5 kPa)", "CO2 poisoning (8 kPa)", "passing out and death from CO2 (16 kPa)",
			"thin air (pO2 13 kPa)", "lightheaded (pO2 9.5 kPa)", "gasping for air (pO2 7 kPa)", "no oxygen (pO2 3 kPa)", "vacuum exposure and death",
			"oxygen toxicity (3.3 atm)", "decompression injury", "buried to the waist", "buried over the head", "a wall on the way to a dig",
			"a wall as the dig starts",
			"an oxygen mask in air poor in oxygen", "an oxygen mask in carbon dioxide", "an oxygen mask in a vacuum", "an atmo suit into a vacuum",
			"a jet suit into a vacuum", "a lead suit into a vacuum", "the head under water", "a building's air supply in a vacuum",
			"a bionic duplicant in carbon dioxide", "taking a pill", "taking a pill in a hat", "taking a pill, a Duplicant of the colony",
			"a dig in a narrow tunnel (no injury)", "a dig into a vacuum pocket (an injury)",
		};
		// Equipment of the scenes, by room.
		private static readonly Dictionary<int, string> Outfits = new Dictionary<int, string>
		{
			{ MaskPoorAir, OxygenMaskConfig.ID }, { MaskCarbon, OxygenMaskConfig.ID }, { MaskVacuum, OxygenMaskConfig.ID },
			{ AtmoSuit, AtmoSuitConfig.ID }, { JetSuit, JetSuitConfig.ID }, { LeadSuit, LeadSuitConfig.ID },
		};
		// Air poor in oxygen: Hydrogen with a little Oxygen (kPa); without a mask the lungs get about 10 kPa of Oxygen.
		private const float PoorHydrogen = 90f, PoorOxygen = 11f;
		// Carbon Dioxide in rooms 1-4, kPa; Oxygen in the lungs in rooms 5-8, kPa.
		// Passing out at 16 kPa, well above the 10 kPa limit: Carbon Dioxide sinks, and the Duplicant breathes the best cell around.
		private static readonly float[] CarbonDioxide = { 0f, 2f, 5f, 8f, 16f };
		private static readonly float[] Lungs = { 13f, 9.5f, 7f, 3f };
		private const float ToxicAtm = 3.3f, DropTo = 22.5f;

		private static int phase, originX, originY;
		private static float phaseTime, spawnTime;
		private static readonly GameObject[] dupes = new GameObject[Rooms];
		private static readonly bool[] done = new bool[Rooms];
		private static readonly Dictionary<string, float> notes = new Dictionary<string, float>();
		private static int passed, failed, skipped, seizures;
		private static bool seizing, originalOption;
		// Dig scenes: the cell to dig, when the wall went up and the last cell the work was done from after it.
		private static readonly int[] wallAt = new int[Rooms];
		private static readonly float[] wallTime = new float[Rooms];
		private static readonly float[] throughWall = new float[Rooms];

		internal static void Apply(Harmony harmony)
		{
			if (!File.Exists(Path.Combine(ModContext.Folder, "verify_dup_cases.txt")))
			{
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(DupCases), nameof(Frame)));
			Debug.Log($"{ModContext.Tag} DupCases: scenes will be built in the active world; the save is changed, use a copy");
		}

		private static void Frame()
		{
			if (phase > 5 || Game.Instance == null || GameClock.Instance == null || ClusterManager.Instance == null)
			{
				return;
			}
			if (phase == 5)
			{
				Watch();
				return;
			}
			float now = GameClock.Instance.GetTime();
			try
			{
				if (phase == 0)
				{
					phaseTime = now;
					phase = 1;
				}
				else if (phase == 1 && now >= phaseTime + StartAfter)
				{
					if (!FindSite())
					{
						Debug.LogWarning($"{ModContext.Tag} DupCases: FAILED: no free {SiteWidth} x {SiteHeight} place in the active world");
						phase = 7;
						return;
					}
					BuildWalls();
					Next(now);
				}
				else if (phase == 2 && now >= phaseTime + 1f)
				{
					FillRooms();
					Next(now);
				}
				else if (phase == 3 && now >= phaseTime + 1f)
				{
					AddCarbonDioxide();
					Next(now);
				}
				else if (phase == 4 && now >= phaseTime + 2f)
				{
					SpawnDupes();
					spawnTime = now;
					originalOption = Mod.Options.BuriedFaceSuffocates;
					phase = 5;
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} DupCases: FAILED with an exception in phase {phase}: {e}");
				phase = 6;
			}
		}

		private static void Next(float now)
		{
			phase++;
			phaseTime = now;
		}

		// ---------- Site, walls and air ----------

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
							Debug.Log($"{ModContext.Tag} DupCases: site at {x},{y} ({(pass == 0 ? "in rock" : "in the open")}), {SiteWidth} x {SiteHeight} cells");
							return true;
						}
					}
				}
			}
			return false;
		}

		private static bool SiteFree(int x0, int y0, bool rockOnly)
		{
			for (int y = y0 - 1; y < y0 + SiteHeight + 1; y++)
			{
				for (int x = x0 - 1; x < x0 + SiteWidth + 1; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell) || Grid.Foundation[cell] || (rockOnly && !Grid.IsSolidCell(cell)) ||
						World.Instance.zoneRenderData.GetSubWorldZoneType(cell) == ProcGen.SubWorld.ZoneType.Space)
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

		private static void Set(int cell, SimHashes element, float mass, float temperature = Warm)
		{
			SimMessages.ReplaceElement(cell, element, CellEventLogger.Instance.DebugTool, mass, temperature, byte.MaxValue, 0);
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

		private static void BuildWalls()
		{
			for (int y = 0; y < SiteHeight; y++)
			{
				for (int x = 0; x < SiteWidth; x++)
				{
					Set(Grid.XYToCell(originX + x, originY + y), SimHashes.Katairite, 1000f);
				}
			}
		}

		/// <summary>Mass of a gas in a cell for a pressure, kPa, by the scale DupAir uses (the pressure mod's, if installed).</summary>
		private static float MassFor(SimHashes gas, float kPa)
		{
			float perKg = DupAir.Partial(ElementLoader.FindElementByHash(gas), 1f, Warm);
			return kPa / perKg;
		}

		private static void FillRooms()
		{
			for (int room = 0; room < Rooms; room++)
			{
				float oxygen = AirMass;
				if (room >= Thin && room <= NoOxygen)
				{
					oxygen = MassFor(SimHashes.Oxygen, Lungs[room - Thin] + DupAir.Vapour);
				}
				else if (room == Toxic)
				{
					oxygen = MassFor(SimHashes.Oxygen, ToxicAtm * 101.325f);
				}
				int r = room;
				ForRoom(room, (cell, dx, dy) =>
				{
					if (r == Vacuum || r == MaskVacuum || r == AirSupply)
					{
						Set(cell, SimHashes.Vacuum, 0f);
					}
					else if (r == MaskPoorAir)
					{
						Set(cell, SimHashes.Hydrogen, MassFor(SimHashes.Hydrogen, PoorHydrogen));
					}
					else if (r == UnderWater && dy < 2)
					{
						Set(cell, SimHashes.Water, 1000f);
					}
					else
					{
						Set(cell, SimHashes.Oxygen, oxygen);
					}
				});
			}
			// The rock to dig: the bottom left cell of the dig rooms; a Duplicant digs it from two cells away over the free cell
			// between, or diagonally from above that cell once it is a wall.
			foreach (int room in new[] { DigOnTheWay, DigAtWork })
			{
				Set(RoomCell(room, 0, 0), SimHashes.IgneousRock, 1000f);
			}
			// A narrow tunnel: air only where the Duplicant stands (the right column, two cells), rock to dig beside them. A
			// vacuum pocket: the same, with a vacuum behind the rock beside their feet.
			foreach (int room in new[] { TunnelDig, VacuumBreach })
			{
				int r = room;
				ForRoom(room, (cell, dx, dy) =>
				{
					if (dx == 4 && dy < 2) return;
					if (r == VacuumBreach && dx < 3) Set(cell, SimHashes.Vacuum, 0f);
					else Set(cell, SimHashes.IgneousRock, 1000f);
				});
			}
		}

		/// <summary>Carbon Dioxide is added the way buildings add gas: with the gas mixture mod it joins the Oxygen of the cell.</summary>
		private static void AddCarbonDioxide()
		{
			foreach (int room in new[] { Stuffy, Headache, Poisoning, PassingOut, MaskCarbon, Bionic })
			{
				float mass = MassFor(SimHashes.CarbonDioxide, CarbonDioxide[room <= PassingOut ? room : PassingOut]);
				ForRoom(room, (cell, dx, dy) => Add(cell, SimHashes.CarbonDioxide, mass));
			}
			float poorOxygen = MassFor(SimHashes.Oxygen, PoorOxygen);
			ForRoom(MaskPoorAir, (cell, dx, dy) => Add(cell, SimHashes.Oxygen, poorOxygen));
		}

		private static void Add(int cell, SimHashes element, float mass)
		{
			SimMessages.AddRemoveSubstance(cell, element, CellEventLogger.Instance.ElementConsumerSimUpdate, mass, Warm, byte.MaxValue, 0);
		}

		private static readonly string[] Spoilers = { "CantDig", "Narcolepsy", "MouthBreather", "DeeperDiversLungs" };

		private static void SpawnDupes()
		{
			for (int room = 0; room < Rooms; room++)
			{
				if (room == PillColonist)
				{
					// A Duplicant of the colony, with their clothes, hat and history, is brought into the room.
					MinionIdentity colonist = Components.LiveMinionIdentities.Items.FirstOrDefault(m => m != null && !dupes.Contains(m.gameObject) &&
						m.model == GameTags.Minions.Models.Standard);
					if (colonist == null)
					{
						Skip(room, "no Duplicant in the colony");
						continue;
					}
					colonist.GetComponent<ChoreDriver>()?.StopChore();
					colonist.GetComponent<Navigator>()?.Stop();
					colonist.transform.SetPosition(Grid.CellToPosCBC(RoomCell(room, 3, 0), Grid.SceneLayer.Move));
					dupes[room] = colonist.gameObject;
					Debug.Log($"{ModContext.Tag} DupCases: {Names[room]}: {colonist.GetProperName()}, hat {colonist.GetComponent<MinionResume>()?.CurrentHat ?? "none"}");
					continue;
				}
				Tag model = room == Bionic ? GameTags.Minions.Models.Bionic : GameTags.Minions.Models.Standard;
				GameObject prefab = Assets.TryGetPrefab(room == Bionic ? (Tag)BionicMinionConfig.ID : (Tag)MinionConfig.ID);
				if (prefab == null || (Outfits.TryGetValue(room, out string outfit) && Assets.TryGetPrefab(outfit) == null))
				{
					Skip(room, "not in this game (a DLC is off)");
					continue;
				}
				int cell = RoomCell(room, room == DigOnTheWay || room == DigAtWork || room == TunnelDig || room == VacuumBreach ? 4 : 3, 0);
				// Traits that would spoil a scene: no digging, falling asleep at random, breathing much more or less, bigger lungs.
				MinionStartingStats stats;
				do
				{
					stats = new MinionStartingStats(model, is_starter_minion: false);
				}
				while (stats.Traits.Any(trait => Spoilers.Contains(trait.Id) || trait.Id.StartsWith("GrantSkill_Swimming")));
				GameObject dupe = Util.KInstantiate(prefab);
				dupe.name = prefab.name;
				Immigration.Instance.ApplyDefaultPersonalPriorities(dupe);
				dupe.transform.SetLocalPosition(Grid.CellToPosCBC(cell, Grid.SceneLayer.Move));
				dupe.SetActive(true);
				stats.Apply(dupe);
				dupe.GetMyWorld().SetDupeVisited();
				dupes[room] = dupe;
			}
			foreach (int room in new[] { DigOnTheWay, DigAtWork })
			{
				DigTool.PlaceDig(RoomCell(room, 0, 0));
				wallAt[room] = RoomCell(room, 1, 0);
				wallTime[room] = -1f;
				throughWall[room] = -1f;
			}
			DigTool.PlaceDig(RoomCell(TunnelDig, 3, 0));
			DigTool.PlaceDig(RoomCell(TunnelDig, 3, 1));
			DigTool.PlaceDig(RoomCell(VacuumBreach, 3, 0));
			Debug.Log($"{ModContext.Tag} DupCases: {Rooms} Duplicants placed; air: {DupAir.Sources}; " +
				$"Oxygen {DupAir.Partial(ElementLoader.FindElementByHash(SimHashes.Oxygen), 1f, Warm):F1} kPa per kg");
		}

		// ---------- Watching and checks ----------

		/// <summary>Called every frame after the Duplicants are placed.</summary>
		private static void Watch()
		{
			float t = GameClock.Instance.GetTime() - spawnTime;
			for (int room = 0; room < Rooms; room++)
			{
				if (done[room])
				{
					continue;
				}
				try
				{
					Scene(room, t);
				}
				catch (Exception e)
				{
					Fail(room, $"exception: {e}");
				}
			}
			if (passed + failed + skipped == Rooms)
			{
				Debug.Log($"{ModContext.Tag} DupCases: {passed} of {Rooms - skipped} scenes passed{(skipped > 0 ? $", {skipped} skipped" : "")}; work fixes: moved while working {DupWork.escapes}, " +
					$"swings finished early {DupWork.swings}, started work resumed {DupWork.resumes}, turned on the way {DupWork.rerouted}, " +
					$"stopped working through a wall {DupWork.walls}");
				// Done: the game is closed (a self-check run need not wait for its frames).
				App.Quit();
				phase = 6;
			}
		}

		private static void Scene(int room, float t)
		{
			GameObject dupe = dupes[room];
			if (dupe == null)
			{
				Fail(room, "the Duplicant is gone");
				return;
			}
			DupAirMonitor air = dupe.GetComponent<DupAirMonitor>();
			if (t >= 1f && !notes.ContainsKey("dressed" + room))
			{
				notes["dressed" + room] = 1f;
				if (Outfits.ContainsKey(room))
				{
					items[room] = MakeOutfit(room, dupe);
				}
				else if (room == AirSupply)
				{
					dupe.GetComponent<OxygenBreather>().AddGasProvider(new TestAirSupply());
				}
			}
			// The outfit is put on a second after it is made: an item spawns on the next frame, and one already equipped by then
			// would equip itself again on spawning.
			if (t >= 2f && items.TryGetValue(room, out GameObject outfit))
			{
				items.Remove(room);
				Equip(outfit, dupe);
			}
			switch (room)
			{
				case Normal:
				case Stuffy:
				case Headache:
				case Poisoning:
					if (t >= 4f)
					{
						string[] effects = { null, DupAir.StuffyId, DupAir.HeadacheId, DupAir.PoisoningId };
						StatusItem[] statuses = { null, DupAir.StuffyStatus, DupAir.HeadacheStatus, DupAir.PoisoningStatus };
						Expect(room, air.Zone == AirZone.Normal && air.CarbonDioxideLevel == room && OnlyEffect(dupe, effects[room]) && OnlyStatus(dupe, statuses[room]), dupe,
							AirStatusText(dupe));
					}
					break;
				case PassingOut:
					if (t >= 4f && !notes.ContainsKey("co2-start"))
					{
						notes["co2-start"] = 1f;
						if (!(air.CarbonDioxideLevel == 4 && air.Blocked && air.DrainName == (string)ONIDUP.BREATH.CARBONDIOXIDE && OnlyStatus(dupe, DupAir.CarbonDioxideOutStatus)))
						{
							Fail(room, $"not passing out ({AirStatusText(dupe)}): " + Describe(dupe));
						}
					}
					else if (t >= 75f && !notes.ContainsKey("co2-out"))
					{
						notes["co2-out"] = 1f;
						if (!dupe.HasTag(GameTags.SuffocatingIncapacitated) && !dupe.HasTag(GameTags.Dead))
						{
							Fail(room, "has not passed out after 75 s: " + Describe(dupe));
						}
					}
					else if (t >= 210f)
					{
						Expect(room, DeathOf(dupe) == DupAir.CarbonDioxide, dupe, $"death: {DeathOf(dupe)?.Id ?? "none"}");
					}
					break;
				case Thin:
				case Lightheaded:
				case Gasping:
				case NoOxygen:
					// Breathing takes Oxygen from the cell at the head faster than it evens out: the check comes early, and the
					// zone is judged by the Oxygen the Duplicant actually has, which must stay near the room's.
					if (t >= 2.5f)
					{
						AirZone[] zones = { AirZone.Thin, AirZone.Hypoxia, AirZone.Severe, AirZone.NoOxygen };
						string[] effects = { null, DupAir.HypoxiaId, DupAir.SevereHypoxiaId, null };
						StatusItem[] statuses = { null, DupAir.HypoxiaStatus, DupAir.GaspingStatus, DupAir.NoOxygenStatus };
						AirZone zone = zones[room - Thin];
						bool blocked = zone == AirZone.Severe || zone == AirZone.NoOxygen;
						bool lowOxygen = dupe.GetComponent<Effects>().HasEffect("LowOxygen");
						Expect(room, air.Zone == zone && air.Blocked == blocked && OnlyEffect(dupe, effects[room - Thin]) && lowOxygen == (zone == AirZone.Thin) &&
							OnlyStatus(dupe, statuses[room - Thin]) && Mathf.Abs(air.OxygenPressure - Lungs[room - Thin]) < 2.5f, dupe,
							$"game's Low Oxygen {lowOxygen}, {AirStatusText(dupe)}");
					}
					break;
				case Vacuum:
					VacuumScene(room, t, dupe, air);
					break;
				case Toxic:
					ToxicScene(room, t, dupe, air);
					break;
				case Drop:
					DropScene(room, t, dupe);
					break;
				case Waist:
				case Head:
					BuriedScene(room, t, dupe, air);
					break;
				case DigOnTheWay:
				case DigAtWork:
					DigScene(room, t, dupe);
					break;
				case TunnelDig:
				case VacuumBreach:
					BreachScene(room, t, dupe);
					break;
				case MaskPoorAir:
					if (t >= 4f)
					{
						// Without the mask the same air would leave the lungs short of Oxygen.
						float bare = DupAir.Lungs(DupAir.CellAir(Grid.CellAbove(Grid.PosToCell(dupe))));
						Expect(room, Masked(dupe) && air.Zone == AirZone.Normal && !air.Blocked && OnlyEffect(dupe, null) && bare < DupAir.Thin, dupe,
							$"without the mask pO2 would be {bare:F1} kPa");
					}
					break;
				case MaskCarbon:
					if (t >= 4f)
					{
						Expect(room, Masked(dupe) && air.Zone == AirZone.Normal && air.CarbonDioxideLevel == 0 && !air.Blocked && OnlyEffect(dupe, null), dupe);
					}
					break;
				case MaskVacuum:
					if (t >= 4f)
					{
						Expect(room, Masked(dupe) && air.Zone == AirZone.Vacuum && dupe.GetComponent<Effects>().HasEffect(DupAir.EbullismId), dupe);
					}
					break;
				case AtmoSuit:
				case JetSuit:
				case LeadSuit:
					SuitScene(room, t, dupe, air);
					break;
				case UnderWater:
					if (t >= 4f)
					{
						Expect(room, air.Zone == AirZone.Protected && OnlyEffect(dupe, null), dupe, $"head in {Grid.Element[Grid.CellAbove(Grid.PosToCell(dupe))].id}");
					}
					break;
				case AirSupply:
					if (t >= 4f && !notes.ContainsKey("supply"))
					{
						notes["supply"] = dupe.GetComponent<Health>().hitPoints;
						if (!(air.Zone == AirZone.Protected && OnlyEffect(dupe, null)))
						{
							Fail(room, "exposed: " + Describe(dupe));
						}
					}
					else if (t >= 25f)
					{
						Health health = dupe.GetComponent<Health>();
						Expect(room, !dupe.HasTag(GameTags.SuffocatingIncapacitated) && health.hitPoints >= notes["supply"] - 0.5f && OnlyEffect(dupe, null) &&
							!dupe.GetComponent<KSelectable>().HasStatusItem(DupAir.VacuumStatus), dupe, $"health {health.hitPoints:F1}");
					}
					break;
				case Pill:
				case PillWithHat:
				case PillColonist:
					PillScene(room, t, dupe);
					break;
				case Bionic:
					if (t >= 10f)
					{
						Expect(room, air == null && OnlyEffect(dupe, null), dupe, "not watched by the mod");
					}
					break;
			}
		}

		// Taking a pill: the Duplicant's animation is followed while they take it; it must not stand still.
		private static readonly Dictionary<int, string> pillAnim = new Dictionary<int, string>();
		private static readonly Dictionary<int, float> pillStill = new Dictionary<int, float>(), pillStart = new Dictionary<int, float>(), pillLast = new Dictionary<int, float>();
		private static readonly Dictionary<int, GameObject> pills = new Dictionary<int, GameObject>();

		private static void PillScene(int room, float t, GameObject dupe)
		{
			if (t >= 1f && !pills.ContainsKey(room))
			{
				if (room == PillWithHat)
				{
					MinionResume resume = dupe.GetComponent<MinionResume>();
					resume.SetHats(null, "hat_role_mining1");
					resume.ApplyTargetHat();
				}
				GameObject pill = Util.KInstantiate(Assets.GetPrefab(BasicBoosterConfig.ID));
				pill.transform.SetPosition(Grid.CellToPosCBC(RoomCell(room, 0, 0), Grid.SceneLayer.Ore));
				pill.SetActive(true);
				pills[room] = pill;
				return;
			}
			if (!pills.ContainsKey(room))
			{
				return;
			}
			KBatchedAnimController anim = dupe.GetComponent<KBatchedAnimController>();
			bool taking = dupe.HasTag(GameTags.TakingMedicine);
			if (taking)
			{
				if (!pillStart.ContainsKey(room))
				{
					pillStart[room] = t;
					pillStill[room] = 0f;
					pillLast[room] = t;
				}
				string name = $"{anim.GetCurrentAnim()?.name ?? "none"} ({anim.PlayMode}{(anim.IsStopped() ? ", stopped" : "")})";
				float elapsed = anim.GetElapsedTime();
				if (anim.IsStopped())
				{
					pillStill[room] += t - pillLast[room];
				}
				pillLast[room] = t;
				if (!pillAnim.TryGetValue(room, out string last) || last != name)
				{
					pillAnim[room] = name;
					Debug.Log($"{ModContext.Tag} DupCases: {Names[room]}: at {t - pillStart[room]:F1} s the animation is {name}, elapsed {elapsed:F2} s, " +
						$"overrides [{string.Join(", ", anim.OverrideAnimFiles.Select(data => data.file != null ? data.file.name : "?"))}]");
				}
			}
			bool taken = pills[room] == null;
			if ((taken && !taking) || t >= 90f)
			{
				float still = pillStill.TryGetValue(room, out float value) ? value : -1f;
				float took = pillStart.ContainsKey(room) ? pillLast[room] - pillStart[room] : -1f;
				Expect(room, taken && pillStart.ContainsKey(room) && still < 1f, dupe, $"taken {taken} in {took:F1} s, standing still {still:F1} s while taking it");
			}
		}

		/// <summary>A sealed suit: in normal air, then the room becomes a vacuum; nothing must happen to the Duplicant.</summary>
		private static void SuitScene(int room, float t, GameObject dupe, DupAirMonitor air)
		{
			string key = "suit" + room;
			Health health = dupe.GetComponent<Health>();
			if (t >= 4f && !notes.ContainsKey(key))
			{
				notes[key] = health.hitPoints;
				if (dupe.GetComponent<SuitEquipper>().IsWearingAirtightSuit() == null)
				{
					Fail(room, "the suit is not on");
					return;
				}
				ForRoom(room, (cell, dx, dy) => Set(cell, SimHashes.Vacuum, 0f));
			}
			else if (t >= 8f && !notes.ContainsKey(key + "-vacuum"))
			{
				notes[key + "-vacuum"] = 1f;
				if (!(air.Zone == AirZone.Protected && OnlyEffect(dupe, null)))
				{
					Fail(room, "exposed in the suit: " + Describe(dupe));
				}
			}
			else if (t >= 25f)
			{
				Expect(room, !dupe.HasTag(GameTags.SuffocatingIncapacitated) && health.hitPoints >= notes[key] - 0.5f && OnlyEffect(dupe, null), dupe,
					$"health {health.hitPoints:F1}");
			}
		}

		private static readonly Dictionary<int, GameObject> items = new Dictionary<int, GameObject>();

		private static GameObject MakeOutfit(int room, GameObject dupe)
		{
			GameObject item = Util.KInstantiate(Assets.GetPrefab(Outfits[room]));
			item.transform.SetPosition(dupe.transform.GetPosition());
			item.SetActive(true);
			return item;
		}

		private static void Equip(GameObject item, GameObject dupe)
		{
			SuitTank tank = item.GetComponent<SuitTank>();
			tank.storage.AddGasChunk(SimHashes.Oxygen, tank.capacity, Warm, byte.MaxValue, 0, keep_zero_mass: false);
			Equippable equippable = item.GetComponent<Equippable>();
			Equipment equipment = dupe.GetComponent<MinionIdentity>().GetEquipment();
			// As a suit locker does it: the errand to go and put it on, created by the assignment, is cancelled.
			equippable.Assign(equipment.GetComponent<IAssignableIdentity>());
			item.GetComponent<EquippableWorkable>().CancelChore("Manual equip");
			equipment.Equip(equippable);
		}

		private static bool Masked(GameObject dupe)
		{
			return dupe.GetComponent<OxygenBreather>().GetCurrentGasProvider() is SuitTank tank && !tank.IsEmpty() &&
				!tank.GetComponent<KPrefabID>().HasTag(GameTags.AirtightSuit);
		}

		/// <summary>Air from a building, like a telescope gives the Duplicant working in it.</summary>
		private sealed class TestAirSupply : OxygenBreather.IGasProvider
		{
			public void OnSetOxygenBreather(OxygenBreather oxygen_breather) { }
			public void OnClearOxygenBreather(OxygenBreather oxygen_breather) { }
			public bool ConsumeGas(OxygenBreather oxygen_breather, float amount) => true;
			public bool ShouldEmitCO2() => false;
			public bool ShouldStoreCO2() => false;
			public bool IsLowOxygen() => false;
			public bool HasOxygen() => true;
			public bool IsBlocked() => false;
		}

		private static void VacuumScene(int room, float t, GameObject dupe, DupAirMonitor air)
		{
			if (t >= 4f && !notes.ContainsKey("vacuum-start"))
			{
				notes["vacuum-start"] = 1f;
				bool status = dupe.GetComponent<KSelectable>().HasStatusItem(DupAir.VacuumStatus);
				if (!(air.Zone == AirZone.Vacuum && dupe.GetComponent<Effects>().HasEffect(DupAir.EbullismId) && status))
				{
					Fail(room, $"not exposed (status {status}): " + Describe(dupe));
				}
			}
			else if (t >= 22f && !notes.ContainsKey("vacuum-out"))
			{
				notes["vacuum-out"] = 1f;
				Health health = dupe.GetComponent<Health>();
				if (!dupe.HasTag(GameTags.SuffocatingIncapacitated) || health.hitPoints >= health.maxHitPoints)
				{
					Fail(room, $"after 22 s: incapacitated {dupe.HasTag(GameTags.SuffocatingIncapacitated)}, health {health.hitPoints:F1}: " + Describe(dupe));
				}
			}
			else if (t >= 160f)
			{
				Expect(room, DeathOf(dupe) == DupAir.Decompression, dupe, $"death: {DeathOf(dupe)?.Id ?? "none"}");
			}
		}

		private static readonly Dictionary<int, float> topUps = new Dictionary<int, float>();

		/// <summary>
		/// A small closed room loses the air a Duplicant breathes (100 g/s): over minutes that is a large share of it. The
		/// room's Oxygen is put back to its mass every few seconds while a scene needs the air to stay as it was.
		/// </summary>
		private static void KeepAir(int room, float t, float every, IEnumerable<int> cells, float mass)
		{
			if (topUps.TryGetValue(room, out float last) && t < last + every)
			{
				return;
			}
			topUps[room] = t;
			foreach (int cell in cells)
			{
				if (Grid.IsValidCell(cell) && Grid.Element[cell].IsGas && Grid.Mass[cell] < mass) Set(cell, SimHashes.Oxygen, mass);
			}
		}

		private static IEnumerable<int> RoomGasCells(int room)
		{
			var cells = new List<int>();
			ForRoom(room, (cell, dx, dy) => cells.Add(cell));
			return cells;
		}

		private static void ToxicScene(int room, float t, GameObject dupe, DupAirMonitor air)
		{
			KeepAir(room, t, 5f, RoomGasCells(room), MassFor(SimHashes.Oxygen, ToxicAtm * 101.325f));
			bool now = dupe.GetComponent<Effects>().HasEffect(DupAir.SeizureId);
			if (now && !seizing)
			{
				seizures++;
				Debug.Log($"{ModContext.Tag} DupCases: {Names[room]}: a seizure at {t:F0} s, pO2 {air.OxygenPressure:F0} kPa");
			}
			seizing = now;
			if (t >= 4f && !notes.ContainsKey("toxic-start"))
			{
				notes["toxic-start"] = 1f;
				if (!(air.Zone == AirZone.Normal && air.OxygenPressure >= DupAir.OxygenToxic && !dupe.GetComponent<Effects>().HasEffect(DupAir.ToxicityId)))
				{
					Fail(room, "at the start: " + Describe(dupe));
				}
			}
			else if (t >= 310f)
			{
				// Seizures are random (20% a minute): their count goes to the log but does not decide the check.
				Expect(room, dupe.GetComponent<Effects>().HasEffect(DupAir.ToxicityId), dupe, $"seizures {seizures}");
			}
		}

		private static void DropScene(int room, float t, GameObject dupe)
		{
			Health health = dupe.GetComponent<Health>();
			if (t >= 4f && !notes.ContainsKey("drop"))
			{
				notes["drop"] = health.hitPoints;
				float oxygen = MassFor(SimHashes.Oxygen, DropTo);
				ForRoom(room, (cell, dx, dy) => Set(cell, SimHashes.Oxygen, oxygen));
			}
			else if (t >= 8f)
			{
				float lost = notes["drop"] - health.hitPoints;
				// The room's air at the game's pressure scale down to 22.5 kPa: over 4 times, an injury; the damage by the ratio
				// (5 at 4 times, 10 at 8), as recorded.
				DupAirMonitor air = dupe.GetComponent<DupAirMonitor>();
				float ratio = air.DecompressionFrom / Mathf.Max(air.DecompressionTo, 0.1f);
				float expected = Mathf.Clamp(5f * Mathf.Log(ratio / 2f, 2f), 5f, 20f);
				Expect(room, dupe.GetComponent<Effects>().HasEffect(DupAir.BarotraumaId) && ratio >= 4f && Mathf.Abs(lost - expected) < 0.6f, dupe,
					$"{air.DecompressionFrom:F0} -> {air.DecompressionTo:F0} kPa ({ratio:F1} times), health lost {lost:F1}, expected {expected:F1}");
			}
		}

		/// <summary>
		/// Digging beside oneself: in a narrow tunnel of air no injury (the cells just dug are empty for a moment); into a vacuum
		/// pocket the air runs out, about 6 times thinner: an injury.
		/// </summary>
		private static void BreachScene(int room, float t, GameObject dupe)
		{
			Health health = dupe.GetComponent<Health>();
			if (!notes.ContainsKey("breach-health" + room))
			{
				notes["breach-health" + room] = health.hitPoints;
			}
			bool dug = room == TunnelDig ? !Grid.Solid[RoomCell(room, 3, 0)] && !Grid.Solid[RoomCell(room, 3, 1)] : !Grid.Solid[RoomCell(room, 3, 0)];
			// Two cells of air last a breathing Duplicant half a minute: kept until the dig opens the wall (the scene is
			// about the dig, not about running out of air), then left alone.
			if (!dug)
			{
				KeepAir(room, t, 1f, new[] { RoomCell(room, 4, 0), RoomCell(room, 4, 1) }, AirMass);
			}
			if (dug && !notes.ContainsKey("breach-dug" + room))
			{
				notes["breach-dug" + room] = t;
				Debug.Log($"{ModContext.Tag} DupCases: {Names[room]}: dug at {t:F1} s");
			}
			if (!(notes.TryGetValue("breach-dug" + room, out float at) && t >= at + 8f) && t < 120f)
			{
				return;
			}
			Effects effects = dupe.GetComponent<Effects>();
			float lost = notes["breach-health" + room] - health.hitPoints;
			bool injured = effects.HasEffect(DupAir.BarotraumaId), ears = effects.HasEffect(DupAir.PoppedEarsId);
			string what = $"dug {dug}, injury {injured}, popped ears {ears}, health lost {lost:F1}, {AirStatusText(dupe)}";
			if (room == TunnelDig)
			{
				Expect(room, dug && !injured && lost < 0.5f, dupe, what);
			}
			else
			{
				Expect(room, dug && injured && lost >= 4.5f && lost <= 20f && dupe.GetComponent<KSelectable>().HasStatusItem(DupAir.DecompressionStatus), dupe, what);
			}
		}

		private static void BuriedScene(int room, float t, GameObject dupe, DupAirMonitor air)
		{
			string key = room == Waist ? "waist" : "head";
			FallMonitor.Instance fall = dupe.GetSMI<FallMonitor.Instance>();
			AmountInstance breath = Db.Get().Amounts.Breath.Lookup(dupe);
			int cell = Grid.PosToCell(dupe);
			if (t >= 4f && !notes.ContainsKey(key))
			{
				// Where the Duplicant stands now: they may have walked about since they appeared; held there for the burying (at
				// the game's highest speed a step may be under way between two checks).
				dupe.GetComponent<Navigator>()?.Stop();
				dupe.transform.SetPosition(Grid.CellToPosCBC(cell, Grid.SceneLayer.Move));
				notes[key] = cell;
				Set(room == Waist ? cell : Grid.CellAbove(cell), SimHashes.Katairite, 1000f);
				return;
			}
			if (!notes.ContainsKey(key))
			{
				return;
			}
			int home = (int)notes[key], buried = room == Waist ? home : Grid.CellAbove(home);
			if (t >= 7f && !notes.ContainsKey(key + "-stuck"))
			{
				notes[key + "-stuck"] = breath.value;
				if (!(fall.IsInsideState(fall.sm.entombed.stuck) && cell == home))
				{
					Fail(room, $"not entombed in place: state {fall.GetCurrentState()?.name}, moved {cell != home}: " + Describe(dupe));
				}
				else if (room == Waist)
				{
					Set(buried, SimHashes.Oxygen, AirMass);
					notes[key + "-freed"] = t;
				}
			}
			else if (room == Waist && t >= 11f)
			{
				Expect(room, !fall.IsInsideState(fall.sm.entombed) && cell == home, dupe, $"state {fall.GetCurrentState()?.name}");
			}
			else if (room == Head && t >= 27f && !notes.ContainsKey("head-off"))
			{
				// With the option off a buried face breathes from the cells around it.
				notes["head-off"] = breath.value;
				float before = notes["head-stuck"];
				if (!(air.Zone == AirZone.Protected && breath.value >= before - 2f))
				{
					Fail(room, $"option off: breath {before:F1} -> {breath.value:F1}: " + Describe(dupe));
				}
				Mod.Options.BuriedFaceSuffocates = true;
			}
			else if (room == Head && t >= 47f && !notes.ContainsKey("head-on"))
			{
				float before = notes["head-off"];
				notes["head-on"] = breath.value;
				Mod.Options.BuriedFaceSuffocates = originalOption;
				if (!(air.Zone == AirZone.Buried && breath.value <= before - 8f))
				{
					Fail(room, $"option on: breath {before:F1} -> {breath.value:F1}, drain {air.DrainName}: " + Describe(dupe));
				}
				Set(buried, SimHashes.Oxygen, AirMass);
			}
			else if (room == Head && t >= 51f)
			{
				Expect(room, !fall.IsInsideState(fall.sm.entombed) && air.Zone != AirZone.Buried && cell == home, dupe, $"state {fall.GetCurrentState()?.name}");
			}
		}

		private static void DigScene(int room, float t, GameObject dupe)
		{
			int rock = RoomCell(room, 0, 0), far = RoomCell(room, 2, 0);
			WorkerBase worker = dupe.GetComponent<WorkerBase>();
			Navigator navigator = dupe.GetComponent<Navigator>();
			Workable workable = worker.GetWorkable();
			bool digging = worker.GetState() == WorkerBase.State.Working && workable is Diggable && Grid.PosToCell(workable) == rock;
			int cell = Grid.PosToCell(dupe);
			if (wallTime[room] < 0f)
			{
				bool goingToDig = navigator.IsMoving() && navigator.target != null && Grid.PosToCell(navigator.target) == rock && navigator.reservedCell == far;
				if (room == DigOnTheWay ? goingToDig && cell != far : digging && cell == far)
				{
					Set(wallAt[room], SimHashes.Katairite, 1000f);
					wallTime[room] = t;
					Debug.Log($"{ModContext.Tag} DupCases: {Names[room]}: wall up at {t:F1} s");
				}
			}
			else if (digging && cell == far && t > wallTime[room] + 0.6f)
			{
				throughWall[room] = t;
			}
			if (!Grid.Solid[rock] || t >= 120f)
			{
				bool dug = !Grid.Solid[rock];
				Expect(room, dug && wallTime[room] >= 0f && throughWall[room] < 0f, dupe,
					$"dug {dug}, wall at {wallTime[room]:F1} s, worked through the wall at {throughWall[room]:F1} s, finished at {t:F1} s");
			}
		}

		// ---------- Helpers ----------

		private static readonly string[] OwnEffects =
		{
			DupAir.HypoxiaId, DupAir.SevereHypoxiaId, DupAir.EbullismId, DupAir.BarotraumaId, DupAir.StuffyId, DupAir.HeadacheId,
			DupAir.PoisoningId, DupAir.ToxicityId, DupAir.SeizureId, DupAir.PoppedEarsId,
		};

		/// <summary>Of the mod's effects the Duplicant has exactly this one (or none).</summary>
		private static bool OnlyEffect(GameObject dupe, string id)
		{
			Effects effects = dupe.GetComponent<Effects>();
			return OwnEffects.All(own => effects.HasEffect(own) == (own == id));
		}

		private static StatusItem[] AirStatuses => new[] { DupAir.VacuumStatus, DupAir.CarbonDioxideOutStatus, DupAir.NoOxygenStatus, DupAir.BuriedStatus,
			DupAir.GaspingStatus, DupAir.PoisoningStatus, DupAir.HypoxiaStatus, DupAir.HeadacheStatus, DupAir.ToxicityStatus, DupAir.StuffyStatus };

		/// <summary>Of the air's status items only this one (or none) is shown, and its tooltip has all its numbers.</summary>
		private static bool OnlyStatus(GameObject dupe, StatusItem item)
		{
			KSelectable selectable = dupe.GetComponent<KSelectable>();
			DupAirMonitor air = dupe.GetComponent<DupAirMonitor>();
			return AirStatuses.All(own => selectable.HasStatusItem(own) == (own == item))
				&& (item == null || !air.Explain(item.tooltipText).Contains("{"));
		}

		private static string AirStatusText(GameObject dupe)
		{
			KSelectable selectable = dupe.GetComponent<KSelectable>();
			DupAirMonitor air = dupe.GetComponent<DupAirMonitor>();
			var shownAll = AirStatuses.Where(selectable.HasStatusItem).ToList();
			if (shownAll.Count == 0) return "status none";
			StatusItem shown = shownAll[0];
			return $"status {string.Join(" + ", shownAll.Select(x => x.Id))}: {air.Explain(shown.tooltipText).Replace("\n", " ")}";
		}

		private static Death DeathOf(GameObject dupe)
		{
			DeathMonitor.Instance smi = dupe.GetSMI<DeathMonitor.Instance>();
			return smi != null ? smi.sm.death.Get(smi) : null;
		}

		private static string Describe(GameObject dupe)
		{
			DupAirMonitor air = dupe.GetComponent<DupAirMonitor>();
			if (air == null)
			{
				return $"not watched by the mod, effects [{string.Join(",", OwnEffects.Where(dupe.GetComponent<Effects>().HasEffect))}]";
			}
			Effects effects = dupe.GetComponent<Effects>();
			string own = string.Join(",", OwnEffects.Where(effects.HasEffect));
			AmountInstance breath = Db.Get().Amounts.Breath.Lookup(dupe);
			string traits = string.Join(",", dupe.GetComponent<Traits>()?.TraitList.Select(trait => trait.Id) ?? Enumerable.Empty<string>());
			return $"zone {air.Zone}, pO2 {air.OxygenPressure:F1} kPa, P {air.Pressure:F1}, pCO2 {air.CarbonDioxide:F2} (level {air.CarbonDioxideLevel}), " +
				$"blocked {air.Blocked}, drain {air.DrainName ?? "none"}, breath {breath?.value:F1}, effects [{own}], traits [{traits}]";
		}

		private static void Expect(int room, bool ok, GameObject dupe, string extra = null)
		{
			if (ok)
			{
				done[room] = true;
				passed++;
				Debug.Log($"{ModContext.Tag} DupCases: {Names[room]}: ok{(extra != null ? " (" + extra + ")" : "")}");
			}
			else
			{
				Fail(room, (extra != null ? extra + "; " : "") + Describe(dupe));
			}
		}

		private static void Skip(int room, string why)
		{
			done[room] = true;
			skipped++;
			Debug.Log($"{ModContext.Tag} DupCases: {Names[room]}: skipped: {why}");
		}

		private static void Fail(int room, string why)
		{
			if (done[room])
			{
				return;
			}
			done[room] = true;
			failed++;
			Debug.LogWarning($"{ModContext.Tag} DupCases: {Names[room]}: FAILED: {why}");
		}
	}
}
