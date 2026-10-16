using System;
using HarmonyLib;
using Klei.AI;
using OniCommon;
using TUNING;
using UnityEngine;

namespace OniDup
{
	/// <summary>
	/// Breathing by the pressure of oxygen, hypoxia, vacuum and decompression.
	///
	/// Two numbers decide everything, recomputed for each duplicant every 0.2 s:
	/// - P, the total gas pressure at the head, on the game's mass scale (1 atm = 101.3 kPa = 1.8 kg of gas in a cell);
	/// - pO2, the pressure of oxygen that reaches the lungs: (P - 6.3) times the share of breathable gas (Oxygen and Polluted
	///   Oxygen, by the gas mixture mod's composition if it is installed) in the best cell around the head; 6.3 kPa is the
	///   water vapour in the lungs. An oxygen mask gives pure oxygen at the pressure around: pO2 = P - 6.3. A sealed suit and
	///   a transit tube protect from everything here.
	///
	/// Zones by pO2 (sea level is about 20 kPa):
	/// - 15 and more: normal;
	/// - 11-15: thin air, the game's "Low Oxygen" effect;
	/// - 8-11: hypoxia (athletics -4, learning -3, work attributes -2, stress +15% per cycle);
	/// - 6-8: severe hypoxia (athletics -8, learning and work attributes -4, stress +30% per cycle); the duplicant cannot
	///   breathe, but the breath runs out slowly: passes out in about 3 minutes;
	/// - below 6: no oxygen; breath is held for 15 + 95 * min(1, P / 101.3) seconds (the game's 110 s at 1 atm);
	/// - P below 6.3 kPa (the Armstrong limit): body fluids boil, a mask does not help: passes out in 15 s, takes 0.5 damage
	///   per second ("Ebullism").
	/// After passing out the game's own suffocation goes on: 120 s to death, other duplicants can resuscitate.
	/// A drop of pressure around the duplicant by more than half and more than 30 kPa within 2 s gives "Barotrauma" (the numbers
	/// and reaction of the game's "Popped Eardrums", which is about over-pressure) and does 10 damage.
	///
	/// Carbon Dioxide, by its partial pressure in the air the duplicant breathes (only while breathing: in pure Carbon Dioxide a
	/// duplicant holds their breath and is not poisoned; a mask or a suit protects): 1-3 kPa stuffy air, 3-5 a headache, 5-8
	/// poisoning, 8 and more passing out within a minute, then the game's suffocation (death: Carbon Dioxide Poisoning).
	///
	/// Too much Oxygen: the game's air is pure Oxygen (1.8 kg in a cell is normal air), so the limits of the real world are moved
	/// up: pO2 of 2 atm and more for half a cycle in a row gives Oxygen Toxicity, which goes after half a cycle at a normal
	/// pressure; at 3 atm and more there is a 20% chance of a seizure every minute (knocked off their feet for about 10 s).
	/// Bionic duplicants are not affected yet.
	/// </summary>
	internal static class DupAir
	{
		internal const float KiloPascalPerKg = 101.325f / 1.8f;
		internal const float Vapour = 6.3f;
		internal const float Normal = 15f, Thin = 11f, Hypoxic = 8f, Severe = 6f;
		internal const string HypoxiaId = "OniDupHypoxia", SevereHypoxiaId = "OniDupSevereHypoxia", EbullismId = "OniDupEbullism", BarotraumaId = "OniDupBarotrauma";
		internal const float Stuffy = 1f, Headache = 3f, Poisoning = 5f, PassingOut = 8f; // kPa of Carbon Dioxide
		internal const float OxygenRich = 2f * 101.325f, OxygenToxic = 3f * 101.325f;     // kPa of Oxygen in the lungs
		internal const string StuffyId = "OniDupStuffyAir", HeadacheId = "OniDupCarbonDioxideHeadache", PoisoningId = "OniDupCarbonDioxidePoisoning",
			ToxicityId = "OniDupOxygenToxicity", SeizureId = "OniDupOxygenSeizure";

		internal static string Mode { get; private set; } = "off";
		internal static StatusItem VacuumStatus { get; private set; }
		internal static Death Decompression { get; private set; }
		internal static Death CarbonDioxide { get; private set; }

		internal static void Apply(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(MinionConfig), nameof(MinionConfig.CreatePrefab)), postfix: new HarmonyMethod(typeof(DupAir), nameof(AddMonitor)));
				harmony.Patch(AccessTools.Method(typeof(Db), nameof(Db.Initialize)), postfix: new HarmonyMethod(typeof(DupAir), nameof(AddEffects)));
				harmony.Patch(AccessTools.Method(typeof(OxygenBreather), nameof(OxygenBreather.IsLowOxygen)), postfix: new HarmonyMethod(typeof(DupAir), nameof(LowOxygen)));
				harmony.Patch(AccessTools.PropertyGetter(typeof(OxygenBreather), nameof(OxygenBreather.HasOxygen)), postfix: new HarmonyMethod(typeof(DupAir), nameof(HasOxygen)));
				harmony.Patch(AccessTools.PropertyGetter(typeof(OxygenBreather), nameof(OxygenBreather.IsOutOfOxygen)), postfix: new HarmonyMethod(typeof(DupAir), nameof(OutOfOxygen)));
				harmony.Patch(AccessTools.Method(typeof(SuffocationMonitor.Instance), nameof(SuffocationMonitor.Instance.Kill)), prefix: new HarmonyMethod(typeof(DupAir), nameof(DeathInVacuum)));
				Mode = "on";
			}
			catch (Exception e)
			{
				Mode = "off: " + e.Message;
				Debug.LogWarning($"{ModContext.Tag} DupAir failed to apply: {e}");
			}
		}

		/// <summary>Partial pressures of a cell's air, kPa.</summary>
		internal struct Air
		{
			internal float Pressure, Oxygen, CarbonDioxide;
		}

		// Shared by the other mods through the application domain data (no reference between the mods): the pressure mod's
		// scale (by mass or by molecules, as its phase rules count) and the gas mixture mod's composition of a cell.
		private static Func<ushort, float, float, float> scale;
		private static Func<int, ushort[], float[], int> composition;
		private static bool lookedUp;
		[ThreadStatic] private static ushort[] gasElements;
		[ThreadStatic] private static float[] gasMasses;

		internal static string Sources => $"pressure scale {(scale != null ? "of the pressure mod" : "by mass")}, composition {(composition != null ? "of the gas mixture mod" : "by the main gas")}";

		private static void LookUp()
		{
			if (lookedUp)
			{
				return;
			}
			scale = AppDomain.CurrentDomain.GetData("OniMods.PressureScale.1") as Func<ushort, float, float, float>;
			composition = AppDomain.CurrentDomain.GetData("OniMods.GasComposition.1") as Func<int, ushort[], float[], int>;
			lookedUp = true;
			Debug.Log($"{ModContext.Tag} air: {Sources}");
		}

		private static float Partial(Element gas, float mass, float temperature)
		{
			return scale != null ? scale(gas.idx, mass, temperature) : mass * KiloPascalPerKg;
		}

		/// <summary>
		/// Pressure of a cell's air and the partial pressures of breathable gas (Oxygen, Polluted Oxygen) and Carbon Dioxide, kPa.
		/// Zero for a vacuum, a liquid or a solid. Called from worker threads by the overlay.
		/// </summary>
		internal static Air CellAir(int cell)
		{
			LookUp();
			Air air = default;
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsGas)
			{
				return air;
			}
			float temperature = Grid.Temperature[cell];
			int count = 0;
			if (composition != null)
			{
				if (gasElements == null)
				{
					gasElements = new ushort[32];
					gasMasses = new float[32];
				}
				count = composition(cell, gasElements, gasMasses);
			}
			if (count == 0)
			{
				Add(ref air, Grid.Element[cell], Grid.Mass[cell], temperature);
				return air;
			}
			for (int i = 0; i < count; i++)
			{
				Add(ref air, ElementLoader.elements[gasElements[i]], gasMasses[i], temperature);
			}
			return air;
		}

		private static void Add(ref Air air, Element gas, float mass, float temperature)
		{
			float pressure = Partial(gas, mass, temperature);
			air.Pressure += pressure;
			if (gas.HasTag(GameTags.Breathable))
			{
				air.Oxygen += pressure;
			}
			if (gas.id == SimHashes.CarbonDioxide)
			{
				air.CarbonDioxide += pressure;
			}
		}

		/// <summary>Mass of pure Oxygen in a cell at room temperature that gives this pressure, kg (on the scale in use).</summary>
		internal static float OxygenMassFor(float pressure)
		{
			LookUp();
			Element oxygen = ElementLoader.FindElementByHash(SimHashes.Oxygen);
			float perKg = oxygen != null ? Partial(oxygen, 1f, 293.15f) : KiloPascalPerKg;
			return perKg > 0f ? pressure / perKg : pressure / KiloPascalPerKg;
		}

		/// <summary>
		/// Pressure of Oxygen reaching the lungs from this air, kPa: (P - 6.3) times the share of breathable gas in the pressure,
		/// or P - 6.3 with an oxygen mask (pure Oxygen at the pressure around).
		/// </summary>
		internal static float Lungs(Air air, bool mask = false)
		{
			if (!(air.Pressure > Vapour))
			{
				return 0f;
			}
			return (air.Pressure - Vapour) * (mask ? 1f : air.Oxygen / air.Pressure);
		}

		private static void AddMonitor(GameObject __result)
		{
			__result.AddOrGet<DupAirMonitor>();
		}

		private static void AddEffects()
		{
			Database.Attributes attributes = Db.Get().Attributes;
			Klei.AI.Attribute[] work = { attributes.Construction, attributes.Digging, attributes.Machinery, attributes.Cooking, attributes.Caring,
				attributes.Strength, attributes.Art, attributes.Botanist, attributes.Ranching };
			string stress = Db.Get().Amounts.Stress.deltaAttribute.Id;

			string name = ONIDUP.EFFECTS.HYPOXIA.NAME;
			// No floating text: at a zone boundary the effect can come and go as the duplicant walks.
			var hypoxia = new Effect(HypoxiaId, name, ONIDUP.EFFECTS.HYPOXIA.TOOLTIP, 0f, true, false, true);
			hypoxia.Add(new AttributeModifier(stress, 15f / 600f, name));
			hypoxia.Add(new AttributeModifier(attributes.Athletics.Id, -4f, name));
			hypoxia.Add(new AttributeModifier(attributes.Learning.Id, -3f, name));
			foreach (Klei.AI.Attribute attribute in work)
			{
				hypoxia.Add(new AttributeModifier(attribute.Id, -2f, name));
			}
			Db.Get().effects.Add(hypoxia);

			name = ONIDUP.EFFECTS.SEVEREHYPOXIA.NAME;
			var severe = new Effect(SevereHypoxiaId, name, ONIDUP.EFFECTS.SEVEREHYPOXIA.TOOLTIP, 0f, true, false, true, Db.Get().Emotes.Minion.Cough, 20f);
			severe.Add(new AttributeModifier(stress, 30f / 600f, name));
			severe.Add(new AttributeModifier(attributes.Athletics.Id, -8f, name));
			severe.Add(new AttributeModifier(attributes.Learning.Id, -4f, name));
			foreach (Klei.AI.Attribute attribute in work)
			{
				severe.Add(new AttributeModifier(attribute.Id, -4f, name));
			}
			Db.Get().effects.Add(severe);

			Db.Get().effects.Add(new Effect(EbullismId, ONIDUP.EFFECTS.EBULLISM.NAME, ONIDUP.EFFECTS.EBULLISM.TOOLTIP, 0f, true, false, true));

			// Barotrauma: the game's "Popped Eardrums" is about over-pressure; the same numbers and reaction, with its own text.
			name = ONIDUP.EFFECTS.BAROTRAUMA.NAME;
			var barotrauma = new Effect(BarotraumaId, name, ONIDUP.EFFECTS.BAROTRAUMA.TOOLTIP, 300f, true, true, true, "anim_react_popped_ears_kanim", -1f);
			barotrauma.Add(new AttributeModifier(stress, 20f / 600f, name));
			Db.Get().effects.Add(barotrauma);

			VacuumStatus = new StatusItem("OniDupVacuum", ONIDUP.STATUSITEMS.VACUUM.NAME, ONIDUP.STATUSITEMS.VACUUM.TOOLTIP, "",
				StatusItem.IconType.Exclamation, NotificationType.DuplicantThreatening, false, OverlayModes.None.ID);
			Decompression = new Death("OniDupDecompression", Db.Get().Deaths, ONIDUP.DEATHS.DECOMPRESSION.NAME, ONIDUP.DEATHS.DECOMPRESSION.DESCRIPTION,
				"death_suffocation", "dead_on_back");
			CarbonDioxide = new Death("OniDupCarbonDioxide", Db.Get().Deaths, ONIDUP.DEATHS.CARBONDIOXIDE.NAME, ONIDUP.DEATHS.CARBONDIOXIDE.DESCRIPTION,
				"death_suffocation", "dead_on_back");

			// Carbon Dioxide: stuffy air, a headache, poisoning.
			name = ONIDUP.EFFECTS.STUFFYAIR.NAME;
			var stuffy = new Effect(StuffyId, name, ONIDUP.EFFECTS.STUFFYAIR.TOOLTIP, 0f, true, false, true);
			stuffy.Add(new AttributeModifier(stress, 5f / 600f, name));
			stuffy.Add(new AttributeModifier(attributes.Learning.Id, -2f, name));
			Db.Get().effects.Add(stuffy);

			name = ONIDUP.EFFECTS.CO2HEADACHE.NAME;
			var headache = new Effect(HeadacheId, name, ONIDUP.EFFECTS.CO2HEADACHE.TOOLTIP, 0f, true, false, true);
			headache.Add(new AttributeModifier(stress, 15f / 600f, name));
			headache.Add(new AttributeModifier(attributes.Learning.Id, -3f, name));
			foreach (Klei.AI.Attribute attribute in work)
			{
				headache.Add(new AttributeModifier(attribute.Id, -2f, name));
			}
			Db.Get().effects.Add(headache);

			name = ONIDUP.EFFECTS.CO2POISONING.NAME;
			var poisoning = new Effect(PoisoningId, name, ONIDUP.EFFECTS.CO2POISONING.TOOLTIP, 0f, true, false, true, Db.Get().Emotes.Minion.Cough, 20f);
			poisoning.Add(new AttributeModifier(stress, 25f / 600f, name));
			poisoning.Add(new AttributeModifier(attributes.Athletics.Id, -5f, name));
			poisoning.Add(new AttributeModifier(attributes.Learning.Id, -4f, name));
			foreach (Klei.AI.Attribute attribute in work)
			{
				poisoning.Add(new AttributeModifier(attribute.Id, -4f, name));
			}
			Db.Get().effects.Add(poisoning);

			// Too much Oxygen: toxicity after half a cycle, seizures at the highest pressures.
			name = ONIDUP.EFFECTS.OXYGENTOXICITY.NAME;
			var toxicity = new Effect(ToxicityId, name, ONIDUP.EFFECTS.OXYGENTOXICITY.TOOLTIP, 0f, true, true, true);
			toxicity.Add(new AttributeModifier(stress, 10f / 600f, name));
			toxicity.Add(new AttributeModifier(attributes.Athletics.Id, -3f, name));
			Db.Get().effects.Add(toxicity);

			name = ONIDUP.EFFECTS.OXYGENSEIZURE.NAME;
			var seizure = new Effect(SeizureId, name, ONIDUP.EFFECTS.OXYGENSEIZURE.TOOLTIP, 120f, true, true, true);
			seizure.Add(new AttributeModifier(stress, 20f / 600f, name));
			Db.Get().effects.Add(seizure);
		}

		/// <summary>
		/// A duplicant who passed out in a vacuum or from Carbon Dioxide and was not rescued dies of that, not of plain suffocation.
		/// </summary>
		private static bool DeathInVacuum(SuffocationMonitor.Instance __instance)
		{
			DupAirMonitor monitor = DupAirMonitor.Of(__instance.oxygenBreather);
			Death death = monitor == null ? null : monitor.Zone == AirZone.Vacuum ? Decompression
				: monitor.CarbonDioxideLevel >= 4 && !monitor.OxygenBlocked ? CarbonDioxide : null;
			if (death == null)
			{
				return true;
			}
			__instance.gameObject.GetSMI<DeathMonitor.Instance>().Kill(death);
			return false;
		}

		// The game's checks of a duplicant's air go through these: our zones decide for duplicants the mod watches.

		private static void LowOxygen(OxygenBreather __instance, ref bool __result)
		{
			DupAirMonitor monitor = DupAirMonitor.Of(__instance);
			if (monitor != null && monitor.Watching)
			{
				__result = monitor.Zone == AirZone.Thin;
			}
		}

		private static void HasOxygen(OxygenBreather __instance, ref bool __result)
		{
			DupAirMonitor monitor = DupAirMonitor.Of(__instance);
			if (monitor != null && monitor.Blocked)
			{
				__result = false;
			}
		}

		private static void OutOfOxygen(OxygenBreather __instance, ref bool __result)
		{
			DupAirMonitor monitor = DupAirMonitor.Of(__instance);
			if (monitor != null && monitor.Blocked)
			{
				__result = true;
			}
		}
	}

	internal enum AirZone
	{
		Protected, // a suit, a transit tube, the head in a liquid or a solid: the game's own logic
		Normal,
		Thin,
		Hypoxia,
		Severe,
		NoOxygen,
		Vacuum,
	}

	/// <summary>Per-duplicant state of DupAir: the zone, the breath drain and the pressure history for decompression.</summary>
	internal sealed class DupAirMonitor : KMonoBehaviour, ISim200ms
	{
		private const int HistorySamples = 10; // 2 s at 0.2 s
		private const float DecompressionDrop = 30f, DecompressionCooldown = 10f, DecompressionDamage = 10f, EbullismDamagePerSecond = 0.5f;
		// Ebullism damage comes in portions: every hit plays the game's flinch, every 0.2 s it would never stop.
		private const float EbullismDamageEvery = 5f;
		private const float VacuumBreathSeconds = 15f, SevereBreathSeconds = 180f, CarbonDioxideBreathSeconds = 60f;
		private const float ToxicityAfter = 300f, ToxicityRecovery = 300f, SeizureEvery = 60f, SeizureChance = 0.2f, SeizureStress = 5f;
		private const int SeizureShakes = 4; // the game's electric shock reaction, about 10 s
		private const int AirChangedEvent = -933153513; // GameHashes.OxygenBreatherHasAirChanged

		private static readonly System.Collections.Generic.Dictionary<OxygenBreather, DupAirMonitor> byBreather =
			new System.Collections.Generic.Dictionary<OxygenBreather, DupAirMonitor>();

#pragma warning disable CS0649 // filled by the game ([MyCmpGet])
		[MyCmpGet] private OxygenBreather breather;
		[MyCmpGet] private Effects effects;
		[MyCmpGet] private Health health;
		[MyCmpGet] private KPrefabID prefabID;
		[MyCmpGet] private SuitEquipper suitEquipper;
		[MyCmpGet] private KSelectable selectable;
#pragma warning restore CS0649

		private readonly float[] history = new float[HistorySamples];
		private int historyCount, historyAt;
		private float lastDecompression = -1000f;
		private float ebullismTime = EbullismDamageEvery;
		private AttributeModifier drain;
		private string drainName;
		private float richTime, normalTime, seizureTime;

		internal AirZone Zone { get; private set; } = AirZone.Protected;
		internal bool Watching => Zone != AirZone.Protected;
		internal bool OxygenBlocked => Zone == AirZone.Severe || Zone == AirZone.NoOxygen || Zone == AirZone.Vacuum;
		internal bool Blocked => OxygenBlocked || CarbonDioxideLevel >= 4;
		/// <summary>0: none, 1: stuffy air, 2: headache, 3: poisoning, 4: passing out.</summary>
		internal int CarbonDioxideLevel { get; private set; }
		internal float CarbonDioxide { get; private set; }
		internal float Pressure { get; private set; }
		internal float OxygenPressure { get; private set; }

		internal static DupAirMonitor Of(OxygenBreather breather)
		{
			return breather != null && byBreather.TryGetValue(breather, out DupAirMonitor monitor) ? monitor : null;
		}

		public override void OnSpawn()
		{
			base.OnSpawn();
			if (breather != null)
			{
				byBreather[breather] = this;
				// The game's own "where can one breathe" checks (safe cells, where to catch breath) by the mass of breathable gas
				// in a cell: for pure Oxygen the same limits as the zones (pO2 6 and 15 kPa).
				breather.noOxygenThreshold = DupAir.OxygenMassFor(Severe + Vapour);
				breather.lowOxygenThreshold = DupAir.OxygenMassFor(DupAir.Normal + Vapour);
			}
		}

		public override void OnCleanUp()
		{
			if (breather != null)
			{
				byBreather.Remove(breather);
			}
			base.OnCleanUp();
		}

		private const float Severe = DupAir.Severe, Vapour = DupAir.Vapour;

		public void Sim200ms(float dt)
		{
			if (breather == null || prefabID == null || prefabID.HasTag(GameTags.Dead))
			{
				return;
			}
			AirZone before = Zone;
			bool wasBlocked = Blocked;
			Zone = Measure(out float pressure, out _);
			Pressure = pressure;
			SetEffect(DupAir.HypoxiaId, Zone == AirZone.Hypoxia);
			SetEffect(DupAir.SevereHypoxiaId, Zone == AirZone.Severe);
			SetEffect(DupAir.EbullismId, Zone == AirZone.Vacuum);
			SetVacuumStatus(Zone == AirZone.Vacuum);
			// Carbon Dioxide is inhaled only while breathing.
			bool breathing = Zone == AirZone.Normal || Zone == AirZone.Thin || Zone == AirZone.Hypoxia;
			float carbon = breathing ? CarbonDioxide : 0f;
			CarbonDioxideLevel = carbon >= DupAir.PassingOut ? 4 : carbon >= DupAir.Poisoning ? 3 : carbon >= DupAir.Headache ? 2 : carbon >= DupAir.Stuffy ? 1 : 0;
			SetEffect(DupAir.StuffyId, CarbonDioxideLevel == 1);
			SetEffect(DupAir.HeadacheId, CarbonDioxideLevel == 2);
			SetEffect(DupAir.PoisoningId, CarbonDioxideLevel >= 3);
			OxygenExcess(dt);
			if (Blocked != wasBlocked)
			{
				Trigger(AirChangedEvent, Blocked ? BoxedBools.False : BoxedBools.True);
			}
			UpdateDrain();
			if (Zone == AirZone.Vacuum && health != null)
			{
				ebullismTime += dt;
				if (ebullismTime >= EbullismDamageEvery)
				{
					health.Damage(EbullismDamagePerSecond * ebullismTime);
					ebullismTime = 0f;
				}
			}
			else
			{
				ebullismTime = EbullismDamageEvery;
			}
			Decompression(pressure, Zone == AirZone.Protected);
		}

		private AirZone Measure(out float headPressure, out bool sealedSuit)
		{
			headPressure = 0f;
			sealedSuit = prefabID.HasTag(GameTags.HasSuitTank) && suitEquipper != null && suitEquipper.IsWearingAirtightSuit() != null;
			// An oxygen mask counts only while its tank has oxygen; an empty mask is left to the game (no air).
			bool mask = !sealedSuit && breather.GetCurrentGasProvider() is SuitTank tank && !tank.IsEmpty();
			int cell = Grid.PosToCell(this);
			int head = Grid.IsValidCell(cell) ? Grid.CellAbove(cell) : -1;
			if (!Grid.IsValidCell(head))
			{
				return AirZone.Protected;
			}
			Element headElement = Grid.Element[head];
			headPressure = DupAir.CellAir(head).Pressure;
			if (sealedSuit || prefabID.HasTag(GameTags.InTransitTube) || !(headElement.IsGas || headElement.IsVacuum))
			{
				OxygenPressure = 0f;
				CarbonDioxide = 0f;
				return AirZone.Protected;
			}
			if (headPressure < Vapour)
			{
				return AirZone.Vacuum;
			}
			// The best cell around the head, as the game picks it for breathing.
			float best = 0f, bestCarbon = 0f;
			foreach (CellOffset offset in GasBreatherFromWorldProvider.DEFAULT_BREATHABLE_OFFSETS)
			{
				int around = Grid.OffsetCell(cell, offset);
				if (!Grid.IsValidCell(around) || !Grid.Element[around].IsGas)
				{
					continue;
				}
				DupAir.Air air = DupAir.CellAir(around);
				float lungs = DupAir.Lungs(air, mask);
				if (lungs > best)
				{
					best = lungs;
					bestCarbon = air.CarbonDioxide;
				}
			}
			OxygenPressure = best;
			CarbonDioxide = mask ? 0f : bestCarbon;
			if (best >= DupAir.Normal) return AirZone.Normal;
			if (best >= DupAir.Thin) return AirZone.Thin;
			if (best >= DupAir.Hypoxic) return AirZone.Hypoxia;
			if (best >= DupAir.Severe) return AirZone.Severe;
			return AirZone.NoOxygen;
		}

		private System.Guid vacuumStatus;

		private void SetVacuumStatus(bool on)
		{
			if (selectable == null || DupAir.VacuumStatus == null)
			{
				return;
			}
			if (on && vacuumStatus == System.Guid.Empty)
			{
				vacuumStatus = selectable.AddStatusItem(DupAir.VacuumStatus, this);
			}
			else if (!on && vacuumStatus != System.Guid.Empty)
			{
				selectable.RemoveStatusItem(vacuumStatus);
				vacuumStatus = System.Guid.Empty;
			}
		}

		private void SetEffect(string id, bool on)
		{
			if (effects == null)
			{
				return;
			}
			bool has = effects.HasEffect(id);
			if (on && !has)
			{
				effects.Add(id, false);
			}
			else if (!on && has)
			{
				effects.Remove(id);
			}
		}

		/// <summary>
		/// While the duplicant cannot breathe, the game drains the breath bar in 110 s. Here the time depends on the zone: severe
		/// hypoxia 180 s, no oxygen 15 + 95 * min(1, P / 101.3) s (lungs hold air at the pressure around), vacuum 15 s.
		/// </summary>
		private void UpdateDrain()
		{
			float target = 0f;
			string name = null;
			if (Blocked)
			{
				// The fastest of the reasons not to breathe wins.
				float seconds = float.MaxValue;
				if (OxygenBlocked)
				{
					seconds = Zone == AirZone.Vacuum ? VacuumBreathSeconds : Zone == AirZone.Severe ? SevereBreathSeconds
						: VacuumBreathSeconds + 95f * Mathf.Min(1f, Pressure / 101.325f);
					name = Zone == AirZone.Severe ? (string)ONIDUP.BREATH.GASPING : Zone == AirZone.Vacuum ? (string)ONIDUP.BREATH.VACUUM : (string)ONIDUP.BREATH.THINAIR;
				}
				if (CarbonDioxideLevel >= 4 && CarbonDioxideBreathSeconds < seconds)
				{
					seconds = CarbonDioxideBreathSeconds;
					name = ONIDUP.BREATH.CARBONDIOXIDE;
				}
				target = DUPLICANTSTATS.STANDARD.Breath.BREATH_RATE - DUPLICANTSTATS.STANDARD.Breath.BREATH_BAR_TOTAL_AMOUNT / seconds;
			}
			if (drain != null && Mathf.Abs(drain.Value - target) < 0.01f && drainName == name)
			{
				return;
			}
			Attributes attributes = gameObject.GetAttributes();
			if (drain != null)
			{
				attributes.Remove(drain);
				drain = null;
			}
			if (target != 0f)
			{
				drain = new AttributeModifier(Db.Get().Amounts.Breath.deltaAttribute.Id, target, name);
				attributes.Add(drain);
				drainName = name;
			}
		}

		/// <summary>
		/// Too much Oxygen in the lungs: Oxygen Toxicity after half a cycle in a row at 2 atm and more, gone after half a cycle below;
		/// at 3 atm and more a seizure every minute with a 20% chance. Protected duplicants breathe a normal pressure.
		/// </summary>
		private void OxygenExcess(float dt)
		{
			float oxygen = Zone == AirZone.Protected ? 0f : OxygenPressure;
			if (oxygen >= DupAir.OxygenRich)
			{
				richTime += dt;
				normalTime = 0f;
				if (richTime >= ToxicityAfter)
				{
					SetEffect(DupAir.ToxicityId, true);
				}
			}
			else
			{
				richTime = 0f;
				normalTime += dt;
				if (normalTime >= ToxicityRecovery)
				{
					SetEffect(DupAir.ToxicityId, false);
				}
			}
			if (oxygen < DupAir.OxygenToxic)
			{
				seizureTime = 0f;
				return;
			}
			seizureTime += dt;
			if (seizureTime < SeizureEvery)
			{
				return;
			}
			seizureTime = 0f;
			if (UnityEngine.Random.value >= SeizureChance)
			{
				return;
			}
			// The game's electric shock reaction, repeated: the duplicant shakes and drops what they were doing.
			ChoreProvider provider = GetComponent<ChoreProvider>();
			if (provider != null)
			{
				new EmoteChore(provider, Db.Get().ChoreTypes.EmoteHighPriority, Db.Get().Emotes.Minion.Shock, SeizureShakes);
			}
			effects?.Add(DupAir.SeizureId, true);
			Db.Get().Amounts.Stress.Lookup(gameObject)?.ApplyDelta(SeizureStress);
		}

		/// <summary>A sharp drop of pressure around the duplicant: Decompression Injury and damage.</summary>
		private void Decompression(float pressure, bool protectedHead)
		{
			// In a sealed suit, a transit tube or with the head in a liquid (a liquid airlock) the air around tells nothing:
			// the history starts over, so leaving the liquid is not taken for a drop of pressure to zero.
			if (protectedHead)
			{
				historyCount = 0;
				return;
			}
			float highest = pressure;
			for (int i = 0; i < historyCount; i++)
			{
				highest = Mathf.Max(highest, history[i]);
			}
			history[historyAt] = pressure;
			historyAt = (historyAt + 1) % HistorySamples;
			historyCount = Math.Min(historyCount + 1, HistorySamples);
			if (highest - pressure < DecompressionDrop || pressure > 0.5f * highest || GameClock.Instance.GetTime() - lastDecompression < DecompressionCooldown)
			{
				return;
			}
			lastDecompression = GameClock.Instance.GetTime();
			historyCount = 0;
			effects?.Add(DupAir.BarotraumaId, true);
			health?.Damage(DecompressionDamage);
		}
	}
}
