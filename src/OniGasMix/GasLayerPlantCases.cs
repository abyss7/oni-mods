using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniGasMix
{
	/// <summary>
	/// Self-check of plants in mixed air (GasLayerPlants): the file verify_plant_cases.txt in the mod folder. A few seconds
	/// after loading, the mod finds free space in the active world, builds a row of closed Abyssalite rooms, fills each with
	/// its air, plants one wild plant in it and after Wait seconds of game time checks its pressure, atmosphere, wilting and
	/// (Oxyfern) its intake. A line "PlantCases: name: ok" or "FAILED" with numbers per scene goes to the log, then a summary,
	/// and the game closes. The world changes: the check is meant for a copy of the save.
	/// </summary>
	internal static class GasLayerPlantCases
	{
		private const int RoomWidth = 3, RoomHeight = 4;
		private const float Warm = 300f, StartAfter = 5f, FillAfter = 0.5f, Wait = 40f;
		private const string Oxyfern = "Oxyfern", Mealwood = "BasicSingleHarvestPlant";

		private enum Expect { Normal, Low, Wrong }

		private sealed class Scene
		{
			internal string name, plant;
			internal (SimHashes gas, float kPa)[] air;
			internal Expect expect;
			internal float rateFrom = -1f, rateTo = -1f;
			internal GameObject go;
		}

		private static readonly Scene[] Scenes =
		{
			new Scene { name = "an Oxyfern in air with 0.5 kPa of CO2", plant = Oxyfern, air = new[] { (SimHashes.Oxygen, 100f), (SimHashes.CarbonDioxide, 0.5f) }, expect = Expect.Normal, rateFrom = 0.3f, rateTo = 0.6f },
			new Scene { name = "an Oxyfern in air with 2 kPa of CO2", plant = Oxyfern, air = new[] { (SimHashes.Oxygen, 100f), (SimHashes.CarbonDioxide, 2f) }, expect = Expect.Normal, rateFrom = 0.999f, rateTo = 1f },
			new Scene { name = "an Oxyfern in pure Oxygen", plant = Oxyfern, air = new[] { (SimHashes.Oxygen, 100f) }, expect = Expect.Normal, rateFrom = 0f, rateTo = 0f },
			new Scene { name = "an Oxyfern in thin CO2 (5 kPa)", plant = Oxyfern, air = new[] { (SimHashes.CarbonDioxide, 5f) }, expect = Expect.Low, rateFrom = 0.999f, rateTo = 1f },
			new Scene { name = "a Mealwood with 12 kPa of Oxygen in 60 kPa of Hydrogen", plant = Mealwood, air = new[] { (SimHashes.Hydrogen, 60f), (SimHashes.Oxygen, 12f) }, expect = Expect.Normal },
			new Scene { name = "a Mealwood in thin Oxygen (5 kPa)", plant = Mealwood, air = new[] { (SimHashes.Oxygen, 5f) }, expect = Expect.Low },
			new Scene { name = "a Mealwood in Chlorine", plant = Mealwood, air = new[] { (SimHashes.ChlorineGas, 100f) }, expect = Expect.Wrong },
		};

		private static int SiteWidth => 1 + Scenes.Length * (RoomWidth + 1);
		private const int SiteHeight = RoomHeight + 2;

		private static int phase, originX, originY;
		private static float phaseTime = -1f;

		internal static void Apply(Harmony harmony)
		{
			if (!File.Exists(Path.Combine(ModContext.Folder, "verify_plant_cases.txt")))
			{
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.LateUpdate)), postfix: new HarmonyMethod(typeof(GasLayerPlantCases), nameof(Frame)));
			Debug.Log("[OniGasMix] PlantCases: scenes will be built in the active world; the save is changed, use a copy");
		}

		private static void Frame()
		{
			if (phase > 4 || Game.Instance == null || GameClock.Instance == null || !GasLayer.Moving)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			if (phaseTime < 0f)
			{
				phaseTime = now;
				return;
			}
			try
			{
				if (phase == 0 && now >= phaseTime + StartAfter)
				{
					if (!FindSite())
					{
						Debug.LogWarning($"[OniGasMix] PlantCases: FAILED: no free {SiteWidth} x {SiteHeight} place in the active world");
						phase = 5;
						return;
					}
					BuildWalls();
					Next(now);
				}
				else if (phase == 1 && now >= phaseTime + FillAfter)
				{
					Fill(main: true);
					Next(now);
				}
				else if (phase == 2 && now >= phaseTime + FillAfter)
				{
					Fill(main: false);
					Next(now);
				}
				else if (phase == 3 && now >= phaseTime + FillAfter)
				{
					for (int i = 0; i < Scenes.Length; i++)
					{
						Scenes[i].go = Spawn(Scenes[i].plant, Cell(RoomLeft(i) + 1, 1));
					}
					Next(now);
				}
				else if (phase == 4 && now >= phaseTime + Wait)
				{
					Evaluate();
					phase = 5;
					App.Quit();
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniGasMix] PlantCases: FAILED with an exception in phase {phase}: {e}");
				phase = 5;
			}
		}

		private static void Next(float now)
		{
			phase++;
			phaseTime = now;
		}

		private static int Cell(int x, int y)
		{
			return Grid.XYToCell(originX + x, originY + y);
		}

		private static int RoomLeft(int room)
		{
			return 1 + room * (RoomWidth + 1);
		}

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
							Debug.Log($"[OniGasMix] PlantCases: site at {x},{y} ({(pass == 0 ? "in rock" : "in the open")}), {SiteWidth} x {SiteHeight} cells");
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

		private static void BuildWalls()
		{
			for (int y = 0; y < SiteHeight; y++)
			{
				for (int x = 0; x < SiteWidth; x++)
				{
					SimMessages.ReplaceElement(Cell(x, y), SimHashes.Katairite, CellEventLogger.Instance.DebugTool, 1000f, Warm, byte.MaxValue, 0);
				}
			}
		}

		/// <summary>Mass of a gas in a cell at Warm that gives this partial pressure, kg.</summary>
		private static float MassFor(SimHashes gas, float kPa)
		{
			Element element = ElementLoader.FindElementByHash(gas);
			float perKg = GasLayerPlants.Pressure(element, 1f, Warm);
			return perKg > 0f ? kPa / perKg : 0f;
		}

		/// <summary>The main gas (the first) replaces the room's rock; the others are added the way buildings add gas.</summary>
		private static void Fill(bool main)
		{
			for (int room = 0; room < Scenes.Length; room++)
			{
				(SimHashes gas, float kPa)[] air = Scenes[room].air;
				for (int dy = 0; dy < RoomHeight; dy++)
				{
					for (int dx = 0; dx < RoomWidth; dx++)
					{
						int cell = Cell(RoomLeft(room) + dx, 1 + dy);
						if (main)
						{
							SimMessages.ReplaceElement(cell, air[0].gas, CellEventLogger.Instance.DebugTool, MassFor(air[0].gas, air[0].kPa), Warm, byte.MaxValue, 0);
							continue;
						}
						for (int i = 1; i < air.Length; i++)
						{
							SimMessages.AddRemoveSubstance(cell, air[i].gas, CellEventLogger.Instance.ElementConsumerSimUpdate, MassFor(air[i].gas, air[i].kPa), Warm, byte.MaxValue, 0);
						}
					}
				}
			}
		}

		private static GameObject Spawn(string id, int cell)
		{
			GameObject prefab = Assets.GetPrefab(new Tag(id));
			if (prefab == null)
			{
				return null;
			}
			GameObject go = GameUtil.KInstantiate(prefab, Grid.CellToPosCBC(cell, Grid.SceneLayer.BuildingFront), Grid.SceneLayer.BuildingFront);
			go.SetActive(true);
			return go;
		}

		private static void Evaluate()
		{
			int passed = 0;
			var lines = new List<string>();
			for (int i = 0; i < Scenes.Length; i++)
			{
				Scene scene = Scenes[i];
				PressureVulnerable plant = scene.go != null ? scene.go.GetComponent<PressureVulnerable>() : null;
				if (plant == null)
				{
					Debug.LogWarning($"[OniGasMix] PlantCases: {scene.name}: FAILED: no plant");
					continue;
				}
				WiltCondition wilt = scene.go.GetComponent<WiltCondition>();
				OxyfernIntake intake = scene.go.GetComponent<OxyfernIntake>();
				bool wilting = wilt != null && wilt.IsWilting();
				PressureVulnerable.PressureState state = plant.ExternalPressureState;
				bool safe = plant.testAreaElementSafe;
				bool ok = scene.expect switch
				{
					Expect.Normal => !wilting && safe && state == PressureVulnerable.PressureState.Normal,
					Expect.Low => wilting && (state == PressureVulnerable.PressureState.WarningLow || state == PressureVulnerable.PressureState.LethalLow),
					_ => wilting && !safe,
				};
				if (scene.rateFrom >= 0f)
				{
					ok &= intake != null && intake.Rate >= scene.rateFrom - 1e-4f && intake.Rate <= scene.rateTo + 1e-4f;
				}
				float co2 = GasLayerPlants.PartialAround(scene.go, SimHashes.CarbonDioxide, out _);
				string numbers = $"pressure {GasLayerPlants.Format(plant.GetExternalPressure())} (needs {GasLayerPlants.Format(plant.pressureWarning_Low)}), state {state}, " +
					$"atmosphere {(safe ? "suits" : "does not suit")} ({plant.currentAtmoElement?.id}), {(wilting ? "wilting" : "not wilting")}" +
					(intake != null ? $", CO2 {co2:0.###} kPa, intake {intake.Rate * 100f:F0}% ({scene.go.GetComponent<ElementConsumer>().consumptionRate * 1000f:0.###} g/s)" : "") +
					(wilt != null && wilting ? $", wilt: {wilt.WiltCausesString().Replace("\n", " ")}" : "");
				if (ok)
				{
					passed++;
					Debug.Log($"[OniGasMix] PlantCases: {scene.name}: ok ({numbers})");
				}
				else
				{
					Debug.LogWarning($"[OniGasMix] PlantCases: {scene.name}: FAILED: {numbers}");
				}
			}
			// What the player reads: the first descriptor of an Oxyfern and of a Mealwood.
			foreach (int i in new[] { 0, 4 })
			{
				PressureVulnerable plant = Scenes[i].go != null ? Scenes[i].go.GetComponent<PressureVulnerable>() : null;
				List<Descriptor> descriptors = plant != null ? plant.GetDescriptors(Scenes[i].go) : null;
				var texts = new List<string>();
				descriptors?.ForEach(d => texts.Add(d.text));
				Debug.Log($"[OniGasMix] PlantCases: {Scenes[i].plant} requirements: {string.Join(" | ", texts)}");
			}
			Debug.Log($"[OniGasMix] PlantCases: {passed} of {Scenes.Length} passed");
		}
	}
}
