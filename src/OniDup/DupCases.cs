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
	/// Scenes: normal air; Carbon Dioxide at 2, 4, 6.5 and 14 kPa (stuffy air, a headache, poisoning, passing out and death
	/// from Carbon Dioxide); thin air at the four steps of the Oxygen scale; a vacuum (exposure, passing out, damage, death from
	/// it); Oxygen at 3.3 atm (toxicity after half a cycle, seizures); a sharp drop of pressure (decompression injury);
	/// buried to the waist and over the head (stays in place, breathing with the option off and on, freed when dug out); a dig
	/// two cells away with a wall that goes up on the way or as the work starts (no work through the wall).
	/// </summary>
	internal static class DupCases
	{
		private const int Rooms = 16, RoomsPerRow = 6, RoomWidth = 5, RoomHeight = 3;
		private const int SiteWidth = 1 + RoomsPerRow * (RoomWidth + 1), SiteHeight = 1 + (Rooms + RoomsPerRow - 1) / RoomsPerRow * (RoomHeight + 1);
		private const float Warm = 300f, AirMass = 1.8f, StartAfter = 8f;
		private const int Normal = 0, Stuffy = 1, Headache = 2, Poisoning = 3, PassingOut = 4, Thin = 5, Lightheaded = 6, Gasping = 7, NoOxygen = 8,
			Vacuum = 9, Toxic = 10, Drop = 11, Waist = 12, Head = 13, DigOnTheWay = 14, DigAtWork = 15;
		private static readonly string[] Names =
		{
			"normal air", "stuffy air (CO2 2 kPa)", "a CO2 headache (4 kPa)", "CO2 poisoning (6.5 kPa)", "passing out and death from CO2 (14 kPa)",
			"thin air (pO2 13 kPa)", "lightheaded (pO2 9.5 kPa)", "gasping for air (pO2 7 kPa)", "no oxygen (pO2 3 kPa)", "vacuum exposure and death",
			"oxygen toxicity (3.3 atm)", "decompression injury", "buried to the waist", "buried over the head", "a wall on the way to a dig",
			"a wall as the dig starts",
		};
		// Carbon Dioxide in rooms 1-4, kPa; Oxygen in the lungs in rooms 5-8, kPa.
		// Passing out at 14 kPa, well above the 8 kPa limit: Carbon Dioxide sinks, and the Duplicant breathes the best cell around.
		private static readonly float[] CarbonDioxide = { 0f, 2f, 4f, 6.5f, 14f };
		private static readonly float[] Lungs = { 13f, 9.5f, 7f, 3f };
		private const float ToxicAtm = 3.3f, DropTo = 22.5f;

		private static int phase, originX, originY;
		private static float phaseTime, spawnTime;
		private static readonly GameObject[] dupes = new GameObject[Rooms];
		private static readonly bool[] done = new bool[Rooms];
		private static readonly Dictionary<string, float> notes = new Dictionary<string, float>();
		private static int passed, failed, seizures;
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
					if (r == Vacuum)
					{
						Set(cell, SimHashes.Vacuum, 0f);
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
		}

		/// <summary>Carbon Dioxide is added the way buildings add gas: with the gas mixture mod it joins the Oxygen of the cell.</summary>
		private static void AddCarbonDioxide()
		{
			for (int room = Stuffy; room <= PassingOut; room++)
			{
				float mass = MassFor(SimHashes.CarbonDioxide, CarbonDioxide[room]);
				ForRoom(room, (cell, dx, dy) => SimMessages.AddRemoveSubstance(cell, SimHashes.CarbonDioxide, CellEventLogger.Instance.ElementConsumerSimUpdate,
					mass, Warm, byte.MaxValue, 0));
			}
		}

		private static readonly string[] Spoilers = { "CantDig", "Narcolepsy", "MouthBreather", "DeeperDiversLungs" };

		private static void SpawnDupes()
		{
			GameObject prefab = Assets.GetPrefab(MinionConfig.ID);
			for (int room = 0; room < Rooms; room++)
			{
				int cell = RoomCell(room, room == DigOnTheWay || room == DigAtWork ? 4 : 3, 0);
				// Traits that would spoil a scene: no digging, falling asleep at random, breathing much more or less, bigger lungs.
				MinionStartingStats stats;
				do
				{
					stats = new MinionStartingStats(GameTags.Minions.Models.Standard, is_starter_minion: false);
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
			if (passed + failed == Rooms)
			{
				Debug.Log($"{ModContext.Tag} DupCases: {passed} of {Rooms} scenes passed; work fixes: moved while working {DupWork.escapes}, " +
					$"swings finished early {DupWork.swings}, started work resumed {DupWork.resumes}, turned on the way {DupWork.rerouted}, " +
					$"stopped working through a wall {DupWork.walls}");
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
			switch (room)
			{
				case Normal:
				case Stuffy:
				case Headache:
				case Poisoning:
					if (t >= 4f)
					{
						string[] effects = { null, DupAir.StuffyId, DupAir.HeadacheId, DupAir.PoisoningId };
						Expect(room, air.Zone == AirZone.Normal && air.CarbonDioxideLevel == room && OnlyEffect(dupe, effects[room]), dupe);
					}
					break;
				case PassingOut:
					if (t >= 4f && !notes.ContainsKey("co2-start"))
					{
						notes["co2-start"] = 1f;
						if (!(air.CarbonDioxideLevel == 4 && air.Blocked && air.DrainName == (string)ONIDUP.BREATH.CARBONDIOXIDE))
						{
							Fail(room, "not passing out: " + Describe(dupe));
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
						AirZone zone = zones[room - Thin];
						bool blocked = zone == AirZone.Severe || zone == AirZone.NoOxygen;
						bool lowOxygen = dupe.GetComponent<Effects>().HasEffect("LowOxygen");
						Expect(room, air.Zone == zone && air.Blocked == blocked && OnlyEffect(dupe, effects[room - Thin]) && lowOxygen == (zone == AirZone.Thin) &&
							Mathf.Abs(air.OxygenPressure - Lungs[room - Thin]) < 2.5f, dupe, $"game's Low Oxygen {lowOxygen}");
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
			}
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

		private static void ToxicScene(int room, float t, GameObject dupe, DupAirMonitor air)
		{
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
				Expect(room, dupe.GetComponent<Effects>().HasEffect(DupAir.BarotraumaId) && lost >= 9f, dupe, $"health lost {lost:F1}");
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
				// Where the Duplicant stands now: they may have walked about since they appeared.
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
			DupAir.PoisoningId, DupAir.ToxicityId, DupAir.SeizureId,
		};

		/// <summary>Of the mod's effects the Duplicant has exactly this one (or none).</summary>
		private static bool OnlyEffect(GameObject dupe, string id)
		{
			Effects effects = dupe.GetComponent<Effects>();
			return OwnEffects.All(own => effects.HasEffect(own) == (own == id));
		}

		private static Death DeathOf(GameObject dupe)
		{
			DeathMonitor.Instance smi = dupe.GetSMI<DeathMonitor.Instance>();
			return smi != null ? smi.sm.death.Get(smi) : null;
		}

		private static string Describe(GameObject dupe)
		{
			DupAirMonitor air = dupe.GetComponent<DupAirMonitor>();
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
