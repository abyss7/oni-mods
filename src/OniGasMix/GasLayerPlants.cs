using System;
using System.Collections.Generic;
using HarmonyLib;
using Klei.AI;
using STRINGS;
using UnityEngine;

namespace OniGasMix
{
	/// <summary>
	/// Plants and the air around them (the author's decision of 2026-10-27).
	///
	/// Pressure: a plant feels the pressure of all the gases around it, not only of the gases it needs, as real plants do
	/// (they grow down to about a tenth of an atmosphere whatever the air is made of). In the game it feels only the mass of
	/// its own gases: an Oxyfern among Duplicants, with a little Carbon Dioxide in the air, wilted from "low pressure" in a
	/// room at a full atmosphere. Here a gas cell gives the plant the sum of the partial pressures of its gases (kPa, by the
	/// pressure mod's scale; 1.8 kg is one atmosphere without it), a vacuum cell gives zero, and the plant takes the mean of its
	/// cells. A plant that the game makes sensitive to low pressure wilts below the game's threshold read as a pressure (the
	/// threshold's mass of the plant's only gas, or of Oxygen when it takes several or any, at room temperature), but never
	/// below MinimumPressure (10 kPa): the game's lower thresholds (25 g for the Oxyfern, the Nosh Sprout, the Dasha Saltvine,
	/// the Hydrocactus) were there because they counted only the plant's own gas; plants that wanted more air keep wanting it
	/// (the author's decision). The upper thresholds stay as they are.
	///
	/// Inside, the game keeps the pressure as a mass; here that mass is the mass of Oxygen at room temperature that gives the
	/// same pressure. In a room of pure Oxygen a plant sees what it saw in the game, and its upper thresholds keep their
	/// meaning. The player sees kPa: the plant's panel, its requirements and its statuses.
	///
	/// Atmosphere: a plant's gases still count as its atmosphere when one of them is the main gas, makes up the share any
	/// game logic takes as present, or is there with at least the plant's low-pressure threshold of partial pressure.
	///
	/// Oxyfern (OxyfernIntake): Carbon Dioxide stays its atmosphere, but it counts as there from OxyfernIntake.From (0.01 kPa,
	/// below that real leaves breathe out as much as they take in); with less in all its cells the fern wilts for the wrong
	/// atmosphere, as in the game. Its intake grows with the partial pressure of Carbon Dioxide around it, from nothing at
	/// 0.01 kPa to the full rate at 1 kPa (where Duplicants start to find the air stuffy). The Oxygen it gives follows the
	/// intake by the game's own ratio (50 times the mass of the Carbon Dioxide), and so do the Water and Dirt of a planted fern.
	/// A planted fern gives Oxygen of OxyfernIntake.OxygenPerWater (85%) of the mass of Water it drinks: less than an
	/// Electrolyzer (89%, with Hydrogen), and no more than the Oxygen atoms of that Water hold (the author's decision of
	/// 2026-10-27; the game's fern gave 99%, more Oxygen than its Water holds). Its intake of Carbon Dioxide is lowered to
	/// match, so the game's ratio and the Duplicants' breath (2 g of Carbon Dioxide per 100 g of Oxygen) still close the
	/// loop: Oxygen 26.9 g/s, Carbon Dioxide 0.54 g/s at the full rate, 3.7 ferns per Duplicant. A wild fern stays as in
	/// the game.
	/// </summary>
	internal static class GasLayerPlants
	{
		internal const float MinimumPressure = 10f;
		private const float RoomTemperature = 293.15f;

		private static Func<int, object, bool> previousPlantCell;
		private static Func<ushort, float, float, float> scale;
		private static bool lookedUp;
		private static float kPaPerKg;
		/// <summary>Plant cells whose gas counted as the plant's atmosphere by its pressure alone (below the share of a present gas).</summary>
		internal static long ByPressure;

		internal static void Apply(Harmony harmony)
		{
			// The game walks the plant's cells with a ready handler from a static field: it is replaced. The previous handler
			// stays for liquid cells (the liquid mixture mod chains its own the same way), so the order of mods does not matter.
			previousPlantCell = PressureVulnerable.testAreaCB;
			PressureVulnerable.testAreaCB = PlantCell;
			harmony.Patch(AccessTools.Method(typeof(PressureVulnerable), nameof(PressureVulnerable.Configure), new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(SimHashes[]) }),
				postfix: Method(nameof(Thresholds)));
			harmony.Patch(AccessTools.Method(typeof(PressureVulnerable), nameof(PressureVulnerable.GetDescriptors)), postfix: Method(nameof(Descriptors)));
			harmony.Patch(AccessTools.Method(typeof(MinionVitalsPanel), "GetAirPressureLabel"), prefix: Method(nameof(VitalsLabel)));
			harmony.Patch(AccessTools.Method(typeof(MinionVitalsPanel), "GetAirPressureTooltip"), prefix: Method(nameof(VitalsTooltip)));
			harmony.Patch(AccessTools.Method(typeof(Db), nameof(Db.Initialize)), postfix: Method(nameof(DbReady)));
			harmony.Patch(AccessTools.Method(typeof(OxyfernConfig), nameof(OxyfernConfig.CreatePrefab)), postfix: Method(nameof(OxyfernPrefab)));
		}

		private static HarmonyMethod Method(string name)
		{
			return new HarmonyMethod(typeof(GasLayerPlants), name);
		}

		// ---------- Pressure scale ----------

		private static void LookUp()
		{
			if (lookedUp)
			{
				return;
			}
			scale = AppDomain.CurrentDomain.GetData("OniMods.PressureScale.1") as Func<ushort, float, float, float>;
			lookedUp = true;
		}

		/// <summary>Partial pressure of a mass of gas in a cell, kPa: by the pressure mod's scale, or 1.8 kg per atmosphere.</summary>
		internal static float Pressure(Element gas, float mass, float temperature)
		{
			LookUp();
			return scale != null ? scale(gas.idx, mass, temperature) : mass / 1.8f * 101.325f;
		}

		/// <summary>The pressure of a kilogram of Oxygen at room temperature, kPa: the game's plant pressure (a mass) to kPa.</summary>
		internal static float KPaPerKg
		{
			get
			{
				if (kPaPerKg > 0f)
				{
					return kPaPerKg;
				}
				Element oxygen = ElementLoader.elements != null ? ElementLoader.FindElementByHash(SimHashes.Oxygen) : null;
				if (oxygen == null)
				{
					return 101.325f / 1.8f;
				}
				float value = Pressure(oxygen, 1f, RoomTemperature);
				kPaPerKg = value > 0f ? value : 101.325f / 1.8f;
				return kPaPerKg;
			}
		}

		internal static string Format(float kgEquivalent)
		{
			float kPa = kgEquivalent * KPaPerKg;
			return kPa < 9.95f ? $"{kPa:0.#} kPa" : $"{kPa:N0} kPa";
		}

		// ---------- The plant's cells ----------

		private static bool PlantCell(int cell, object data)
		{
			PressureVulnerable plant = (PressureVulnerable)data;
			if (Grid.IsSolidCell(cell))
			{
				return true;
			}
			Element main = Grid.Element[cell];
			if (!main.IsGas && !main.IsVacuum)
			{
				// A liquid: the previous handler (the game's, or another mod's).
				return previousPlantCell(cell, data);
			}
			float total = 0f, own = 0f, bestMass = 0f, temperature = Grid.Temperature[cell];
			Element best = null;
			if (main.IsGas)
			{
				GasMixture.Gases gases = GasMixture.Of(cell);
				while (gases.Next(out Element gas, out float mass))
				{
					float pressure = Pressure(gas, mass, temperature);
					total += pressure;
					if (!plant.IsSafeElement(gas))
					{
						continue;
					}
					own += pressure;
					if (mass > bestMass)
					{
						bestMass = mass;
						best = gas;
					}
				}
			}
			PressureVulnerable.testAreaPressure += total / KPaPerKg;
			PressureVulnerable.testAreaCount++;
			bool mainSafe = plant.IsSafeElement(main);
			bool byShare = best != null && GasMixture.Has(cell, best.idx);
			bool byPressure = best != null && (OxyfernIntake.Ferns.Contains(plant) ? own >= OxyfernIntake.From
				: plant.pressureWarning_Low > 0f && own / KPaPerKg >= plant.pressureWarning_Low);
			if (!mainSafe && !byShare && byPressure)
			{
				ByPressure++;
			}
			if (mainSafe || byShare || byPressure)
			{
				PressureVulnerable.testAreaSafeElementCount++;
				plant.currentAtmoElement = best ?? main;
			}
			if (plant.currentAtmoElement == null)
			{
				plant.currentAtmoElement = main;
			}
			return true;
		}

		private static void Thresholds(PressureVulnerable __instance, SimHashes[] safeAtmospheres)
		{
			if (__instance.pressureWarning_Low > 0f)
			{
				// The game's threshold is a mass of the plant's gas: of its only gas, otherwise of air (Oxygen).
				Element gas = safeAtmospheres != null && safeAtmospheres.Length == 1 ? ElementLoader.FindElementByHash(safeAtmospheres[0]) : null;
				if (gas == null || !gas.IsGas)
				{
					gas = ElementLoader.FindElementByHash(SimHashes.Oxygen);
				}
				float game = gas != null ? Pressure(gas, __instance.pressureWarning_Low, RoomTemperature) : __instance.pressureWarning_Low * KPaPerKg;
				__instance.pressureWarning_Low = Mathf.Max(MinimumPressure, game) / KPaPerKg;
			}
			if (__instance.pressureLethal_Low > __instance.pressureWarning_Low)
			{
				__instance.pressureLethal_Low = __instance.pressureWarning_Low;
			}
		}

		// ---------- What the player sees: kPa ----------

		private sealed class KPaFormatter : StandardAttributeFormatter
		{
			internal KPaFormatter() : base(GameUtil.UnitClass.Mass, GameUtil.TimeSlice.None)
			{
			}

			public override string GetFormattedValue(float value, GameUtil.TimeSlice timeSlice = GameUtil.TimeSlice.None)
			{
				return Format(value);
			}
		}

		private static void DbReady()
		{
			Db db = Db.Get();
			db.Amounts.AirPressure.SetDisplayer(new StandardAmountDisplayer(GameUtil.UnitClass.Mass, GameUtil.TimeSlice.None, new KPaFormatter()));
			db.CreatureStatusItems.AtmosphericPressureTooLow.resolveStringCallback = PressureStatus;
			db.CreatureStatusItems.AtmosphericPressureTooHigh.resolveStringCallback = PressureStatus;
		}

		private static string PressureStatus(string text, object data)
		{
			PressureVulnerable plant = (PressureVulnerable)data;
			return text.Replace("{low_mass}", Format(plant.pressureWarning_Low)).Replace("{high_mass}", Format(plant.pressureWarning_High));
		}

		private static void Descriptors(PressureVulnerable __instance, List<Descriptor> __result)
		{
			if (!__instance.pressure_sensitive || __result == null || __result.Count == 0)
			{
				return;
			}
			// The game's first descriptor is the low-pressure requirement.
			string low = Format(__instance.pressureWarning_Low);
			__result[0] = new Descriptor(string.Format(UI.GAMEOBJECTEFFECTS.REQUIRES_PRESSURE, low), string.Format(UI.GAMEOBJECTEFFECTS.TOOLTIPS.REQUIRES_PRESSURE, low), Descriptor.DescriptorType.Requirement);
		}

		private static bool VitalsLabel(GameObject go, ref string __result)
		{
			PressureVulnerable plant = go != null ? go.GetComponent<PressureVulnerable>() : null;
			if (plant == null)
			{
				return true;
			}
			string high = plant.pressureWarning_High < 1e6f ? Format(plant.pressureWarning_High) : "-";
			float lowKPa = plant.pressureWarning_Low * KPaPerKg;
			__result = $"{Db.Get().Amounts.AirPressure.Name}\n    • {(lowKPa < 9.95f ? lowKPa.ToString("0.#") : lowKPa.ToString("N0"))} - {high}";
			return false;
		}

		private static bool VitalsTooltip(GameObject go, ref string __result)
		{
			PressureVulnerable plant = go != null ? go.GetComponent<PressureVulnerable>() : null;
			if (plant == null)
			{
				return true;
			}
			__result = UI.TOOLTIPS.VITALS_CHECKBOX_PRESSURE.text.Replace("{pressure}", Format(plant.GetExternalPressure()));
			return false;
		}

		// ---------- Oxyfern ----------

		private static void OxyfernPrefab(GameObject __result)
		{
			__result.AddOrGet<OxyfernIntake>();
		}

		/// <summary>The partial pressure of a gas, kPa, averaged over a plant's open cells (zero where there is no gas).</summary>
		internal static float PartialAround(GameObject go, SimHashes wanted, out int cells)
		{
			cells = 0;
			OccupyArea area = go.GetComponent<OccupyArea>();
			int origin = Grid.PosToCell(go);
			float sum = 0f;
			CellOffset[] offsets = area != null ? area.OccupiedCellsOffsets : new[] { new CellOffset(0, 0) };
			foreach (CellOffset offset in offsets)
			{
				int cell = Grid.OffsetCell(origin, offset);
				if (!Grid.IsValidCell(cell) || Grid.IsSolidCell(cell))
				{
					continue;
				}
				cells++;
				if (!Grid.Element[cell].IsGas)
				{
					continue;
				}
				float temperature = Grid.Temperature[cell];
				GasMixture.Gases gases = GasMixture.Of(cell);
				while (gases.Next(out Element gas, out float mass))
				{
					if (gas.id == wanted)
					{
						sum += Pressure(gas, mass, temperature);
					}
				}
			}
			return cells > 0 ? sum / cells : 0f;
		}
	}

	/// <summary>
	/// The Oxyfern's work by the Carbon Dioxide around it (GasLayerPlants): every second the game's intake (a wild or a planted
	/// fern) times Rate, a share that grows linearly from 0 at From kPa of Carbon Dioxide to 1 at Full kPa. The Oxygen
	/// follows: the game's converter gives it in proportion to the Carbon Dioxide the fern has taken in, and its own statuses
	/// ("Using", "Emitting") show the real average rates. The Water and Dirt a planted fern takes follow the same share: a
	/// multiplier on the game's fertilizer usage attribute (the plant's panel shows the real rates by it), and the rates of
	/// the fern's absorbers, which the game fixes when they start, are set by it every second.
	/// </summary>
	internal sealed class OxyfernIntake : KMonoBehaviour, ISim1000ms
	{
		internal const float From = 0.01f, Full = 1f;
		/// <summary>A planted fern's Oxygen per mass of the Water it drinks.</summary>
		internal const float OxygenPerWater = 0.85f;
		/// <summary>The game's planted intake of Carbon Dioxide (Oxyfern.SetConsumptionRate) and its Water (OxyfernConfig), kg/s.</summary>
		private const float GameIntake = 0.00062500004f, GameWater = 0.031666666f;
		/// <summary>The ferns alive now: for them Carbon Dioxide counts as present from From kPa (GasLayerPlants.PlantCell).</summary>
		internal static readonly HashSet<PressureVulnerable> Ferns = new HashSet<PressureVulnerable>();
		private static readonly AccessTools.FieldRef<IrrigationMonitor.Instance, HandleVector<int>.Handle> IrrigationHandle =
			AccessTools.FieldRefAccess<IrrigationMonitor.Instance, HandleVector<int>.Handle>("absorberHandle");
		private static readonly AccessTools.FieldRef<FertilizationMonitor.Instance, HandleVector<int>.Handle> FertilizerHandle =
			AccessTools.FieldRefAccess<FertilizationMonitor.Instance, HandleVector<int>.Handle>("absorberHandle");
		private static readonly AccessTools.FieldRef<FertilizationMonitor.Instance, PlantElementAbsorber.ConsumeInfo[]> FertilizerElements =
			AccessTools.FieldRefAccess<FertilizationMonitor.Instance, PlantElementAbsorber.ConsumeInfo[]>("consumedElements");

#pragma warning disable CS0649
		[MyCmpReq] private Oxyfern fern;
		[MyCmpReq] private ReceptacleMonitor receptacle;
		[MyCmpReq] private ElementConsumer consumer;
		[MyCmpReq] private PressureVulnerable plant;
#pragma warning restore CS0649

		private float applied = -1f;
		private AttributeModifier usage;
		private AttributeInstance usageAttribute;

		/// <summary>The share of the game's rates the fern works at now, 0..1.</summary>
		internal float Rate { get; private set; } = 1f;

		/// <summary>Carbon Dioxide around the fern at the last update, kPa.</summary>
		internal float CarbonDioxide { get; private set; }

		public override void OnSpawn()
		{
			base.OnSpawn();
			Ferns.Add(plant);
			Klei.AI.Attribute attribute = Db.Get().PlantAttributes.FertilizerUsageMod;
			usageAttribute = gameObject.GetAttributes().Get(attribute);
			usage = new AttributeModifier(attribute.Id, 0f, ONIGASMIX.PLANTS.CARBON_DIOXIDE_MODIFIER, is_multiplier: true, is_readonly: false);
			usageAttribute?.Add(usage);
			Sim1000ms(0f);
		}

		public override void OnCleanUp()
		{
			Ferns.Remove(plant);
			usageAttribute?.Remove(usage);
			base.OnCleanUp();
		}

		public void Sim1000ms(float dt)
		{
			CarbonDioxide = GasLayerPlants.PartialAround(gameObject, SimHashes.CarbonDioxide, out _);
			Rate = Mathf.Clamp01((CarbonDioxide - From) / (Full - From));
			// The intake at the full rate: the game's for a wild fern, by the Water for a planted one; then this share of it.
			fern.SetConsumptionRate();
			if (receptacle.Replanted)
			{
				consumer.consumptionRate = PlantedIntake();
			}
			float rate = consumer.consumptionRate * Rate;
			consumer.consumptionRate = rate;
			if (Mathf.Abs(rate - applied) > applied * 0.01f + 1e-9f)
			{
				applied = rate;
				consumer.RefreshConsumptionRate();
			}
			// Water and Dirt: the attribute with this share, then the absorbers by it.
			usage?.SetValue(Rate - 1f);
			float total = usageAttribute != null ? usageAttribute.GetTotalValue() : Rate;
			IrrigationMonitor.Instance irrigation = gameObject.GetSMI<IrrigationMonitor.Instance>();
			if (irrigation != null)
			{
				Scale(IrrigationHandle(irrigation), irrigation.def.consumedElements, total);
			}
			FertilizationMonitor.Instance fertilizer = gameObject.GetSMI<FertilizationMonitor.Instance>();
			if (fertilizer != null && fertilizer.def.consumedElements != null)
			{
				PlantElementAbsorber.ConsumeInfo[] own = FertilizerElements(fertilizer);
				for (int i = 0; own != null && i < own.Length && i < fertilizer.def.consumedElements.Length; i++)
				{
					own[i].massConsumptionRate = fertilizer.def.consumedElements[i].massConsumptionRate * total;
				}
				Scale(FertilizerHandle(fertilizer), fertilizer.def.consumedElements, total);
			}
		}

		/// <summary>
		/// A planted fern's intake of Carbon Dioxide at the full rate, kg/s: as much as gives OxygenPerWater of its Water by
		/// the game's converter (OxyfernConfig.OXYGEN_RATE for GameIntake).
		/// </summary>
		internal float PlantedIntake()
		{
			IrrigationMonitor.Instance irrigation = gameObject.GetSMI<IrrigationMonitor.Instance>();
			float water = irrigation?.def.consumedElements != null && irrigation.def.consumedElements.Length > 0
				? irrigation.def.consumedElements[0].massConsumptionRate : GameWater;
			return OxygenPerWater * water * GameIntake / OxyfernConfig.OXYGEN_RATE;
		}

		private static void Scale(HandleVector<int>.Handle handle, PlantElementAbsorber.ConsumeInfo[] game, float total)
		{
			if (!handle.IsValid() || game == null || game.Length == 0)
			{
				return;
			}
			PlantElementAbsorbers absorbers = Game.Instance.plantElementAbsorbers;
			PlantElementAbsorber absorber = absorbers.GetData(handle);
			if (absorber.consumedElements == null)
			{
				absorber.localInfo.massConsumptionRate = game[0].massConsumptionRate * total;
			}
			else
			{
				for (int i = 0; i < absorber.consumedElements.Length && i < game.Length; i++)
				{
					absorber.consumedElements[i].massConsumptionRate = game[i].massConsumptionRate * total;
				}
			}
			absorbers.SetData(handle, absorber);
		}
	}
}
