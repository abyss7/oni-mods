using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Klei.AI;
using UnityEngine;

namespace OniGasMix
{
	/// <summary>
	/// Game logic that asks "which gas is in the cell". In the game a cell has one substance; with the layer that is the main
	/// gas of the mixture, and everything that looks at it does not notice the mixture. Here such places are switched to the composition
	/// (the author's decision of 2026-10-12):
	///  - where a check has a quantity or a rate, the mass of exactly the needed gas is taken (partial mass):
	///    gas irritation, pressure for plants, Steam for the turbine, Oxygen for bionics, gas above water;
	///  - where a check is binary ("gas X is here"), the gas counts as present if its share by number of molecules
	///    is not below the presence threshold (mod setting, 10% by default): gas sensor, bees, dreckos, mask
	///    station, rest spots, critter diets, atmosphere for food.
	/// Each patch is installed separately: a failure of one does not affect the others or the layer itself. For the log it counts
	/// how many times the answer by composition differed from the answer by main gas.
	/// </summary>
	internal static class GasLayerLogic
	{
		private static readonly Dictionary<string, long> differs = new Dictionary<string, long>();
		private static readonly List<string> applied = new List<string>(), failed = new List<string>();

		internal static void Apply(Harmony harmony)
		{
			(string name, Action<Harmony> apply)[] patches =
			{
				("sensor", Sensor),
				("plants", GasLayerPlants.Apply),
				("critter diet", CritterDiet),
				("sublimation", Sublimation),
				("irritation", Irritation),
				("food atmosphere", FoodAtmosphere),
				("drecko scales", DreckoScales),
				("bee sleep", BeeSleep),
				("steam turbine", SteamTurbineSteam),
				("mask station", MaskStationOxygen),
				("mingle cells", MingleCells),
				("bionic oxygen", BionicOxygen),
				("surface air", SurfaceAir),
				("germs on items", GermsOnItems),
				("bionic place", BionicPlace),
			};
			foreach ((string name, Action<Harmony> apply) in patches)
			{
				try
				{
					apply(harmony);
					applied.Add(name);
				}
				catch (Exception e)
				{
					failed.Add(name);
					Debug.LogWarning($"[OniGasMix] GasLayerLogic: {name} failed to apply, this check stays by the main gas: {e}");
				}
			}
		}

		internal static string Stats()
		{
			List<string> parts = new List<string>();
			foreach (KeyValuePair<string, long> pair in differs)
			{
				parts.Add($"{pair.Key}={pair.Value}");
			}
			return $"applied: {string.Join(", ", applied)}{(failed.Count > 0 ? "; FAILED: " + string.Join(", ", failed) : "")}; " +
				$"answers that differ from the main gas: {(parts.Count > 0 ? string.Join(", ", parts) : "none")}; " +
				$"composition records in the last frame: {GasMixture.MixedRecords}, frames with composition: {GasMixture.Updates}, {GasMixture.FetchMicros:F0} us each on the main thread";
		}

		private static void Differs(string what)
		{
			differs.TryGetValue(what, out long count);
			differs[what] = count + 1;
		}

		private static HarmonyMethod Method(string name)
		{
			return new HarmonyMethod(typeof(GasLayerLogic), name);
		}

		// ---------- Gas sensor: the gas is present in the cell ----------

		private static void Sensor(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(LogicElementSensor), nameof(LogicElementSensor.Sim200ms)), prefix: Method(nameof(SensorSample)));
			// The gas sensor has its own presence threshold: the slider in the sensor's window (GasSensorThreshold).
			harmony.Patch(AccessTools.Method(typeof(LogicElementSensorGasConfig), nameof(LogicElementSensorGasConfig.DoPostConfigureComplete)), postfix: Method(nameof(SensorThresholdComponent)));
		}

		private static void SensorThresholdComponent(GameObject go)
		{
			go.AddOrGet<GasSensorThreshold>();
		}

		private static bool SensorSample(LogicElementSensor __instance)
		{
			// The game accumulates eight samples and switches when all eight are "yes". Only the sample itself is changed.
			if (__instance.desiredState != Element.State.Gas || __instance.sampleIdx >= 8)
			{
				return true;
			}
			int cell = Grid.PosToCell(__instance);
			bool main = Grid.ElementIdx[cell] == __instance.desiredElementIdx;
			GasSensorThreshold threshold = __instance.GetComponent<GasSensorThreshold>();
			bool present = GasMixture.Share(cell, __instance.desiredElementIdx) >= (threshold != null ? threshold.Share : GasMixture.PresenceShare);
			if (present != main)
			{
				Differs("sensor");
			}
			__instance.samples[__instance.sampleIdx] = present;
			__instance.sampleIdx++;
			return false;
		}

		// ---------- Critters that eat gas: diet by the mixture's gases ----------

		private static void CritterDiet(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(GasAndLiquidConsumerMonitor.Instance), nameof(GasAndLiquidConsumerMonitor.Instance.IsConsumableCell)),
				postfix: Method(nameof(CritterCell)));
		}

		private static void CritterCell(GasAndLiquidConsumerMonitor.Instance __instance, int cell, ref Element element, ref bool __result)
		{
			if (__result || !Grid.IsValidCell(cell) || !Grid.Element[cell].IsGas)
			{
				return;
			}
			// The main gas did not fit: will another gas of the mixture do, one that is noticeably present in the cell.
			GasAndLiquidConsumerMonitor.Def def = __instance.def;
			Element found = null;
			float foundMass = 0f;
			GasMixture.Gases gases = GasMixture.Of(cell);
			while (gases.Next(out Element gas, out float mass))
			{
				if (mass <= foundMass || !GasMixture.Has(cell, gas.idx))
				{
					continue;
				}
				if (def.consumableElementTag != Tag.Invalid && !gas.HasTag(def.consumableElementTag))
				{
					continue;
				}
				if (def.diet != null)
				{
					bool inDiet = false;
					foreach (Diet.Info info in def.diet.infos)
					{
						inDiet = inDiet || info.IsMatch(gas.tag);
					}
					if (!inDiet)
					{
						continue;
					}
				}
				found = gas;
				foundMass = mass;
			}
			if (found != null)
			{
				element = found;
				__result = true;
				Differs("critter diet");
			}
		}

		// ---------- Item sublimation: the gas is added to the mixture of its own cell ----------

		private static void Sublimation(Harmony harmony)
		{
			// The game does not release gas if the simulation could push it into a neighbouring overfilled cell.
			// With the layer the gas is always added to the mixture of the cell where the item lies.
			harmony.Patch(AccessTools.Method(typeof(Sublimates), nameof(Sublimates.SimMightOffcellOverpressure)), prefix: Method(nameof(NoOffcellEmission)));
			// The mass limit in the target cell is compared with the mass of the released gas itself, not of the whole mixture.
			harmony.Patch(AccessTools.Method(typeof(Sublimates), nameof(Sublimates.Sim200ms)), transpiler: Method(nameof(SublimationDestination)));
		}

		private static bool NoOffcellEmission(ref bool __result)
		{
			if (!GasLayerBreathing.Active)
			{
				return true;
			}
			__result = false;
			return false;
		}

		private static IEnumerable<CodeInstruction> SublimationDestination(IEnumerable<CodeInstruction> instructions)
		{
			// The only read of Grid.Mass[cell] in the method: ldsflda Grid.Mass; ldloc cell; call get_Item.
			List<CodeInstruction> code = new List<CodeInstruction>(instructions);
			System.Reflection.FieldInfo massField = AccessTools.Field(typeof(Grid), nameof(Grid.Mass));
			int replaced = 0;
			for (int i = 0; i + 2 < code.Count; i++)
			{
				if (code[i].opcode == OpCodes.Ldsflda && Equals(code[i].operand, massField) && code[i + 2].opcode == OpCodes.Call
					&& code[i + 2].operand is System.Reflection.MethodInfo getter && getter.Name == "get_Item")
				{
					code[i].opcode = OpCodes.Nop;
					code[i].operand = null;
					code[i + 2] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GasLayerLogic), nameof(SublimatedGasMass)));
					code.Insert(i + 2, new CodeInstruction(OpCodes.Ldarg_0));
					replaced++;
					i += 3;
				}
			}
			if (replaced != 1)
			{
				throw new InvalidOperationException($"Sublimates.Sim200ms: expected one read of the cell mass, found {replaced}");
			}
			return code;
		}

		private static float SublimatedGasMass(int cell, Sublimates item)
		{
			Element gas = ElementLoader.FindElementByHash(item.info.sublimatedElement);
			if (gas == null || !gas.IsGas || !Grid.Element[cell].IsGas)
			{
				return Grid.Mass[cell];
			}
			return GasMixture.Mass(cell, gas.idx);
		}

		// ---------- Gas irritation: by the mass of each gas of the mixture ----------

		private static void Irritation(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(GasLiquidExposureMonitor), nameof(GasLiquidExposureMonitor.UpdateExposure)), postfix: Method(nameof(ExposureByMixture)));
		}

		private static void ExposureByMixture(GasLiquidExposureMonitor __instance, GasLiquidExposureMonitor.Instance smi, float dt)
		{
			if (smi.isInAirtightEnvironment || smi.isImmuneToIrritability)
			{
				return;
			}
			int cell = Grid.CellAbove(Grid.PosToCell(smi.gameObject));
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsGas)
			{
				return;
			}
			// The game has already computed the rate as if the whole cell mass were the main gas. The correct rate:
			// the sum over the mixture's gases of "the gas's rate times its mass"; what was accumulated in this step is corrected by the difference.
			float byDefault = (!(Grid.Temperature[cell] >= -13657.5f) || !(Grid.Temperature[cell] <= 27315f)) ? 2f : 1f;
			float rate = 0f;
			GasMixture.Gases gases = GasMixture.Of(cell);
			while (gases.Next(out Element gas, out float mass))
			{
				if (!smi.exposureRateOverride.TryGetValue(gas.id, out float value) && !GasLiquidExposureMonitor.customExposureRates.TryGetValue(gas.id, out value))
				{
					value = byDefault;
				}
				rate += value * mass;
			}
			if (Mathf.Approximately(rate, smi.exposureRate))
			{
				return;
			}
			Differs("irritation");
			smi.exposure = MathUtil.Clamp(0f, 30f, smi.exposure + (rate - smi.exposureRate) * dt);
			smi.exposureRate = rate;
			__instance.ApplyEffects(smi);
		}

		// ---------- Food spoilage: the worst of the gases noticeably present in the cell ----------

		private static void FoodAtmosphere(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Rottable), nameof(Rottable.AtmosphereQuality)), prefix: Method(nameof(FoodAtmosphereByMixture)));
		}

		private static int Severity(Rottable.RotAtmosphereQuality quality)
		{
			return quality == Rottable.RotAtmosphereQuality.Contaminating ? 2 : quality == Rottable.RotAtmosphereQuality.Normal ? 1 : 0;
		}

		private static Rottable.RotAtmosphereQuality CellQuality(int cell, out bool known)
		{
			Element main = Grid.Element[cell];
			Rottable.RotAtmosphereQuality byMain = Rottable.RotAtmosphereQuality.Normal;
			known = Rottable.AtmosphereModifier.TryGetValue((int)main.id, out byMain);
			if (!main.IsGas)
			{
				return byMain;
			}
			// The atmosphere counts as sterile only while the cell has no noticeable share of normal or contaminated gas.
			int worst = -1;
			bool anyKnown = false;
			GasMixture.Gases gases = GasMixture.Of(cell);
			while (gases.Next(out Element gas, out float _))
			{
				if (!GasMixture.Has(cell, gas.idx))
				{
					continue;
				}
				bool has = Rottable.AtmosphereModifier.TryGetValue((int)gas.id, out Rottable.RotAtmosphereQuality quality);
				anyKnown = anyKnown || has;
				worst = Math.Max(worst, Severity(has ? quality : Rottable.RotAtmosphereQuality.Normal));
			}
			if (worst < 0)
			{
				return byMain;
			}
			known = anyKnown;
			Rottable.RotAtmosphereQuality result = worst == 2 ? Rottable.RotAtmosphereQuality.Contaminating : worst == 1 ? Rottable.RotAtmosphereQuality.Normal : Rottable.RotAtmosphereQuality.Sterilizing;
			if (result != byMain)
			{
				Differs("food atmosphere");
			}
			return result;
		}

		private static bool FoodAtmosphereByMixture(IRottable rottable, ref Rottable.RotAtmosphereQuality __result)
		{
			int cell = Grid.PosToCell(rottable.gameObject);
			if (!Grid.IsValidCell(cell))
			{
				return true; // outside the grid (space): as in the game
			}
			// As in the game: the item's cell and the cell above it, the worse of the two ratings is taken.
			Rottable.RotAtmosphereQuality here = CellQuality(cell, out _);
			Rottable.RotAtmosphereQuality above = here;
			int cellAbove = Grid.CellAbove(cell);
			if (Grid.IsValidCell(cellAbove))
			{
				Rottable.RotAtmosphereQuality value = CellQuality(cellAbove, out bool known);
				above = known ? value : here;
			}
			__result = Severity(here) >= Severity(above) ? here : above;
			return false;
		}

		// ---------- Binary checks "gas X is here" ----------

		private static void DreckoScales(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(ScaleGrowthMonitor), nameof(ScaleGrowthMonitor.IsInCorrectAtmosphere)), postfix: Method(nameof(ScalesAtmosphere)));
			// Scales grow in proportion to the share of the needed gas: the game adds growth at a constant rate while the drecko is
			// "in the right atmosphere"; once a second the excess for that second is taken off.
			harmony.Patch(AccessTools.Method(typeof(ScaleGrowthMonitor), nameof(ScaleGrowthMonitor.UpdateScales)), prefix: Method(nameof(ScalesByShare)));
		}

		private static void ScalesByShare(ScaleGrowthMonitor.Instance smi, float dt)
		{
			if (dt <= 0f || smi.def.targetAtmosphere == 0 || !smi.IsInsideState(smi.sm.growing.growing))
			{
				return;
			}
			int cell = Grid.PosToCell(smi);
			if (!Grid.IsValidCell(cell))
			{
				return;
			}
			float share = GasMixture.Share(cell, smi.def.targetAtmosphere);
			if (share >= 1f)
			{
				return;
			}
			Differs("drecko growth rate");
			smi.scaleGrowth.value = Mathf.Max(0f, smi.scaleGrowth.value - smi.def.defaultGrowthRate * 100f * (1f - share) * dt);
		}

		private static void ScalesAtmosphere(ScaleGrowthMonitor.Instance smi, ref bool __result)
		{
			if (__result || smi.def.targetAtmosphere == 0)
			{
				return;
			}
			int cell = Grid.PosToCell(smi);
			if (Grid.IsValidCell(cell) && GasMixture.Has(cell, smi.def.targetAtmosphere))
			{
				__result = true;
				Differs("drecko scales");
			}
		}

		private static void BeeSleep(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(BeeSleepMonitor), nameof(BeeSleepMonitor.IsInCO2)), postfix: Method(nameof(BeeInCarbonDioxide)));
		}

		private static void BeeInCarbonDioxide(BeeSleepMonitor.Instance smi, ref bool __result)
		{
			int cell = Grid.PosToCell(smi.gameObject);
			if (!Grid.IsValidCell(cell))
			{
				return;
			}
			bool present = GasMixture.Has(cell, SimHashes.CarbonDioxide);
			if (present != __result)
			{
				Differs("bee sleep");
			}
			__result = present;
		}

		private static void MaskStationOxygen(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(MaskStation), nameof(MaskStation.IsPumpable)), postfix: Method(nameof(MaskStationGas)));
		}

		private static void MaskStationGas(MaskStation __instance, ref bool __result)
		{
			if (__result)
			{
				return;
			}
			int origin = Grid.PosToCell(__instance.transform.GetPosition());
			foreach (ElementConsumer consumer in __instance.GetComponents<ElementConsumer>())
			{
				for (int y = 0; y < consumer.consumptionRadius; y++)
				{
					for (int x = 0; x < consumer.consumptionRadius; x++)
					{
						int cell = origin + x + Grid.WidthInCells * y;
						if (Grid.IsValidCell(cell) && Grid.Element[cell].IsGas && GasMixture.Has(cell, consumer.elementToConsume))
						{
							__result = true;
							Differs("mask station");
							return;
						}
					}
				}
			}
		}

		private static void MingleCells(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(MingleCellTracker), nameof(MingleCellTracker.Sim1000ms)), prefix: Method(nameof(MingleCellsByMixture)));
		}

		private static bool MingleCellsByMixture(MingleCellTracker __instance)
		{
			// The same walk over rest rooms as in the game; a cell is fit if it noticeably contains Oxygen.
			__instance.mingleCells.Clear();
			RoomProber roomProber = Game.Instance.roomProber;
			MinionGroupProber minionGroupProber = MinionGroupProber.Get();
			foreach (Room room in roomProber.rooms)
			{
				if (room.roomType != Db.Get().RoomTypes.RecRoom)
				{
					continue;
				}
				for (int y = room.cavity.minY; y <= room.cavity.maxY; y++)
				{
					for (int x = room.cavity.minX; x <= room.cavity.maxX; x++)
					{
						int cell = Grid.XYToCell(x, y);
						if (roomProber.GetCavityForCell(cell) == room.cavity && minionGroupProber.IsReachable(cell) && !Grid.HasLadder[cell] && !Grid.HasTube[cell]
							&& !Grid.IsLiquid(cell) && GasMixture.Has(cell, SimHashes.Oxygen))
						{
							__instance.mingleCells.Add(cell);
						}
					}
				}
			}
			return false;
		}

		// ---------- Mass of exactly the needed gas ----------

		private static void SteamTurbineSteam(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SteamTurbine.Instance), nameof(SteamTurbine.Instance.CanSteamFlow)), prefix: Method(nameof(TurbineSteam)));
		}

		private static bool TurbineSteam(SteamTurbine.Instance __instance, ref bool insufficient_mass, ref bool insufficient_temperature, ref bool __result)
		{
			// As in the game, but the Steam mass in the cell is the mass of the Steam itself, not of the whole mixture; Steam counts even
			// when it is not the main gas in the cell.
			SteamTurbine turbine = __instance.master;
			Element steam = ElementLoader.FindElementByHash(turbine.srcElem);
			if (steam == null)
			{
				return true;
			}
			float mass = 0f, temperature = 0f;
			foreach (int cell in turbine.srcCells)
			{
				float here = GasMixture.Mass(cell, steam.idx);
				if (here > 0f)
				{
					mass = Mathf.Max(mass, here);
					temperature = Mathf.Max(temperature, Grid.Temperature[cell]);
				}
			}
			insufficient_mass = mass < turbine.requiredMass;
			insufficient_temperature = temperature < turbine.minActiveTemperature;
			__result = !insufficient_mass && !insufficient_temperature;
			return false;
		}

		private static void BionicOxygen(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(BionicMassOxygenAbsorbChore), nameof(BionicMassOxygenAbsorbChore.GetBreathableMassInCell)), postfix: Method(nameof(BionicBreathable)));
		}

		private static void BionicBreathable(int cell, ref SimHashes elementID, ref float __result)
		{
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsGas)
			{
				return;
			}
			// The breathable gas of the cell with the largest mass, and exactly its mass (the game took the mass of the whole cell).
			Element best = null;
			float bestMass = 0f;
			GasMixture.Gases gases = GasMixture.Of(cell);
			while (gases.Next(out Element gas, out float mass))
			{
				if (mass > bestMass && gas.HasTag(GameTags.Breathable))
				{
					best = gas;
					bestMass = mass;
				}
			}
			if (best == null)
			{
				return;
			}
			if (best.id != elementID || !Mathf.Approximately(bestMass, __result))
			{
				Differs("bionic oxygen");
			}
			elementID = best.id;
			__result = bestMass;
		}

		// ---------- Germs on items: influence of the surrounding gas by mass shares ----------

		private static void GermsOnItems(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(DiseaseContainers), nameof(DiseaseContainers.CalculateDelta),
				new[] { typeof(int), typeof(ushort), typeof(float), typeof(int), typeof(float), typeof(float), typeof(Disease), typeof(float), typeof(bool) }),
				postfix: Method(nameof(ItemGermsByMixture)));
		}

		private static void ItemGermsByMixture(int disease_count, int environment_cell, Disease disease, float dt, ref float __result)
		{
			// The game added the change in germ count from the substance around the item, assuming a single main gas around.
			// With a mixture the average over the gases' mass shares is taken (as for the cell's own germs).
			if (!Grid.IsValidCell(environment_cell) || !Grid.Element[environment_cell].IsGas || GasMixture.Share(environment_cell, Grid.ElementIdx[environment_cell]) >= 1f)
			{
				return;
			}
			float total = GasMixture.RealTotal(environment_cell);
			if (!(total > 0f))
			{
				return;
			}
			float blended = 0f;
			GasMixture.Gases gases = GasMixture.Of(environment_cell);
			while (gases.Next(out Element gas, out float mass))
			{
				blended += disease.elemExposureInfo[gas.idx].CalculateExposureDiseaseCountDelta(disease_count, dt) * (mass / total);
			}
			float byMain = disease.elemExposureInfo[Grid.ElementIdx[environment_cell]].CalculateExposureDiseaseCountDelta(disease_count, dt);
			if (!Mathf.Approximately(blended, byMain))
			{
				Differs("germs on items");
				__result += blended - byMain;
			}
		}

		private static void BionicPlace(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(AbsorbCellQuery), nameof(AbsorbCellQuery.GetAbsorbOxygenFlags)), postfix: Method(nameof(BionicPlaceRatio)));
		}

		private static void BionicPlaceRatio(int cell, MinionBrain brain, ref float breathableCellRatioInSample)
		{
			// Share of surrounding cells with something to breathe: the game counted cells with a breathable main gas.
			if (brain == null || brain.OxygenBreather == null || !Grid.IsValidCell(cell))
			{
				return;
			}
			CellOffset[] offsets = GasBreatherFromWorldProvider.DEFAULT_BREATHABLE_OFFSETS;
			float ratio = 0f;
			foreach (CellOffset offset in offsets)
			{
				int near = Grid.OffsetCell(cell, offset);
				if (!Grid.IsValidCell(near) || !Grid.AreCellsInSameWorld(cell, near))
				{
					continue;
				}
				bool breathable = false;
				GasMixture.Gases gases = GasMixture.Of(near);
				while (!breathable && gases.Next(out Element gas, out float _))
				{
					breathable = gas.HasTag(GameTags.Breathable) && GasMixture.Has(near, gas.idx);
				}
				if (breathable)
				{
					ratio += 1f / offsets.Length;
				}
			}
			if (!Mathf.Approximately(ratio, breathableCellRatioInSample))
			{
				Differs("bionic place");
			}
			breathableCellRatioInSample = ratio;
		}

		private static void SurfaceAir(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SurfaceAirConsumerMonitor.Instance), nameof(SurfaceAirConsumerMonitor.Instance.IsSurfaceLiquidCell)), postfix: Method(nameof(SurfaceAirMass)));
		}

		private static void SurfaceAirMass(SurfaceAirConsumerMonitor.Instance __instance, int cell, ref bool __result)
		{
			// The other conditions (liquid in the cell and below it) as in the game; gas above the liquid: by the mass of the needed gas.
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsLiquid)
			{
				return;
			}
			int above = Grid.CellAbove(cell), below = Grid.CellBelow(cell);
			if (!Grid.IsValidCell(above) || !Grid.Element[above].IsGas || !Grid.IsValidCell(below) || !Grid.Element[below].IsLiquid)
			{
				return;
			}
			bool enough = GasMixture.Mass(above, __instance.def.element) >= __instance.def.minimumMassThreshold;
			if (enough != __result)
			{
				Differs("surface air");
			}
			__result = enough;
		}
	}
}
