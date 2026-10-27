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
	///   water vapour in the lungs. An oxygen mask gives pure oxygen at the pressure around: pO2 = P - 6.3. A sealed suit (Atmo,
	///   Jet, Lead), a transit tube and a building that supplies air while working in it (a telescope) protect from
	///   everything here.
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
	/// A sharp drop of the pressure around the head (by Boyle's law what hurts is the ratio, not the difference), lasting at
	/// least 1.2 s (a passing dip as gas runs into a cell just dug is not one), by more than 30 kPa within about 3 s:
	/// - 2-4 times, or more but staying above 25 kPa: "Popped Ears", no damage (stress +5% per cycle for a quarter cycle, the
	///   game's popped eardrums reaction);
	/// - 4 times and more, down below 25 kPa (a breach into a vacuum, an aircraft window blown out): "Decompression Injury",
	///   5 damage at 4 times, 10 at 8, 15 at 16, 20 at 32 and more (stress +20% per cycle for half a cycle).
	/// Cells that were solid or liquid in the last 2 s are left out of the pressure around: a cell just dug is empty until the
	/// gas around runs into it, the air around has not got thinner.
	///
	/// Carbon Dioxide, by its partial pressure in the air the duplicant breathes (only while breathing: in pure Carbon Dioxide a
	/// duplicant holds their breath and is not poisoned; a mask or a suit protects): 1-4 kPa stuffy air (a little stress, learning -2), 4-6 a
	/// headache, 6-10 poisoning, 10 and more passing out within a minute, then the game's suffocation (death: Carbon Dioxide Poisoning).
	///
	/// Too much Oxygen: the game's air is pure Oxygen (1.8 kg in a cell is normal air), so the limits of the real world are moved
	/// up: pO2 of 2 atm and more for half a cycle in a row gives Oxygen Toxicity, which goes after half a cycle at a normal
	/// pressure; at 3 atm and more there is a 20% chance of a seizure every minute (knocked off their feet for about 10 s).
	/// With the mod option "Buried Duplicants suffocate" a duplicant buried over the head (DupEntombed) holds their breath for the
	/// game's 110 s, then suffocates; a mask or a suit gives air.
	/// Bionic duplicants are not affected yet.
	/// </summary>
	internal static class DupAir
	{
		internal const float KiloPascalPerKg = 101.325f / 1.8f;
		internal const float Vapour = 6.3f;
		internal const float Normal = 15f, Thin = 11f, Hypoxic = 8f, Severe = 6f;
		internal const string HypoxiaId = "OniDupHypoxia", SevereHypoxiaId = "OniDupSevereHypoxia", EbullismId = "OniDupEbullism", BarotraumaId = "OniDupBarotrauma", PoppedEarsId = "OniDupPoppedEars";
		// kPa of Carbon Dioxide, as for people: drowsy from about 1%, a headache from about 4%, dizzy and confused from about
		// 6%, unconscious within minutes from about 10% (the author's choice of 2026-10-27; it was 1, 3, 5, 8).
		internal const float Stuffy = 1f, Headache = 4f, Poisoning = 6f, PassingOut = 10f;
		internal const float OxygenRich = 2f * 101.325f, OxygenToxic = 3f * 101.325f;     // kPa of Oxygen in the lungs
		internal const string StuffyId = "OniDupStuffyAir", HeadacheId = "OniDupCarbonDioxideHeadache", PoisoningId = "OniDupCarbonDioxidePoisoning",
			ToxicityId = "OniDupOxygenToxicity", SeizureId = "OniDupOxygenSeizure";

		internal static string Mode { get; private set; } = "off";
		internal static StatusItem VacuumStatus { get; private set; }
		/// <summary>The air's status items, the worst first: one of them is shown on a duplicant, with the numbers.</summary>
		internal static StatusItem CarbonDioxideOutStatus, NoOxygenStatus, BuriedStatus, GaspingStatus, PoisoningStatus, HypoxiaStatus, HeadacheStatus,
			ToxicityStatus, StuffyStatus, DecompressionStatus, PoppedEarsStatus;
		internal static Death Decompression { get; private set; }
		internal static Death CarbonDioxide { get; private set; }

		internal static void Apply(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(MinionConfig), nameof(MinionConfig.CreatePrefab)), postfix: new HarmonyMethod(typeof(DupAir), nameof(AddMonitor)));
				harmony.Patch(AccessTools.Method(typeof(Db), nameof(Db.Initialize)), postfix: new HarmonyMethod(typeof(DupAir), nameof(AddEffects)));
				harmony.Patch(AccessTools.Method(typeof(OxygenBreather), nameof(OxygenBreather.IsLowOxygen)), postfix: new HarmonyMethod(typeof(DupAir), nameof(LowOxygen)));
				harmony.Patch(AccessTools.Method(typeof(OxygenBreather), nameof(OxygenBreather.Sim200ms)), prefix: new HarmonyMethod(typeof(DupAir), nameof(Breathe)));
				harmony.Patch(AccessTools.Method(typeof(SuffocationMonitor.Instance), nameof(SuffocationMonitor.Instance.CanBreath)), postfix: new HarmonyMethod(typeof(DupAir), nameof(CanBreathe)));
				harmony.Patch(AccessTools.Method(typeof(GasBreatherFromWorldProvider), nameof(GasBreatherFromWorldProvider.GetBestBreathableCellAroundSpecificCell),
					new[] { typeof(int), typeof(CellOffset[]), typeof(OxygenBreather), typeof(float).MakeByRefType() }), postfix: new HarmonyMethod(typeof(DupAir), nameof(BreathableAround)));
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

		internal static float Partial(Element gas, float mass, float temperature)
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
			name = ONIDUP.EFFECTS.POPPEDEARS.NAME;
			var ears = new Effect(PoppedEarsId, name, ONIDUP.EFFECTS.POPPEDEARS.TOOLTIP, 150f, true, true, true, "anim_react_popped_ears_kanim", -1f);
			ears.Add(new AttributeModifier(stress, 5f / 600f, name));
			Db.Get().effects.Add(ears);

			VacuumStatus = AirStatus("OniDupVacuum", ONIDUP.STATUSITEMS.VACUUM.NAME, ONIDUP.STATUSITEMS.VACUUM.TOOLTIP, NotificationType.DuplicantThreatening);
			CarbonDioxideOutStatus = AirStatus("OniDupCarbonDioxideOut", ONIDUP.STATUSITEMS.CO2PASSINGOUT.NAME, ONIDUP.STATUSITEMS.CO2PASSINGOUT.TOOLTIP, NotificationType.DuplicantThreatening);
			NoOxygenStatus = AirStatus("OniDupNoOxygen", ONIDUP.STATUSITEMS.NOOXYGEN.NAME, ONIDUP.STATUSITEMS.NOOXYGEN.TOOLTIP, NotificationType.DuplicantThreatening);
			BuriedStatus = AirStatus("OniDupBuriedHead", ONIDUP.STATUSITEMS.BURIED.NAME, ONIDUP.STATUSITEMS.BURIED.TOOLTIP, NotificationType.DuplicantThreatening);
			GaspingStatus = AirStatus("OniDupGasping", ONIDUP.STATUSITEMS.GASPING.NAME, ONIDUP.STATUSITEMS.GASPING.TOOLTIP, NotificationType.DuplicantThreatening);
			PoisoningStatus = AirStatus("OniDupCarbonDioxidePoisoning", ONIDUP.STATUSITEMS.CO2POISONING.NAME, ONIDUP.STATUSITEMS.CO2POISONING.TOOLTIP, NotificationType.Bad);
			HypoxiaStatus = AirStatus("OniDupHypoxia", ONIDUP.STATUSITEMS.HYPOXIA.NAME, ONIDUP.STATUSITEMS.HYPOXIA.TOOLTIP, NotificationType.Bad);
			HeadacheStatus = AirStatus("OniDupCarbonDioxideHeadache", ONIDUP.STATUSITEMS.CO2HEADACHE.NAME, ONIDUP.STATUSITEMS.CO2HEADACHE.TOOLTIP, NotificationType.BadMinor);
			ToxicityStatus = AirStatus("OniDupOxygenToxicity", ONIDUP.STATUSITEMS.OXYGENTOXICITY.NAME, ONIDUP.STATUSITEMS.OXYGENTOXICITY.TOOLTIP, NotificationType.Bad);
			StuffyStatus = AirStatus("OniDupStuffyAir", ONIDUP.STATUSITEMS.STUFFYAIR.NAME, ONIDUP.STATUSITEMS.STUFFYAIR.TOOLTIP, NotificationType.BadMinor);
			DecompressionStatus = AirStatus("OniDupDecompression", ONIDUP.STATUSITEMS.DECOMPRESSION.NAME, ONIDUP.STATUSITEMS.DECOMPRESSION.TOOLTIP, NotificationType.Bad);
			PoppedEarsStatus = AirStatus("OniDupPoppedEars", ONIDUP.STATUSITEMS.POPPEDEARS.NAME, ONIDUP.STATUSITEMS.POPPEDEARS.TOOLTIP, NotificationType.BadMinor);
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
		/// A status item of the air with the numbers of the duplicant's air in its tooltip; an icon over the head only for what
		/// threatens their life.
		/// </summary>
		private static StatusItem AirStatus(string id, string name, string tooltip, NotificationType type)
		{
			bool threat = type == NotificationType.DuplicantThreatening;
			return new StatusItem(id, name, tooltip, "", threat ? StatusItem.IconType.Exclamation : StatusItem.IconType.Info, type, false, OverlayModes.None.ID,
				showWorldIcon: threat)
			{
				resolveTooltipCallback = (text, data) => data is DupAirMonitor monitor ? monitor.Explain(text) : text,
			};
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

		/// <summary>
		/// A duplicant who cannot breathe here holds their breath: does not inhale (nor exhale Carbon Dioxide), and the game sees
		/// no air (its flag behind HasOxygen; the getters themselves are too small to patch, the JIT inlines them), so its own
		/// suffocation runs. When breathing is possible again the game's own breathing finds air and turns the flag back.
		/// </summary>
		private static bool Breathe(OxygenBreather __instance)
		{
			DupAirMonitor monitor = DupAirMonitor.Of(__instance);
			if (monitor == null || !monitor.Blocked)
			{
				return true;
			}
			HoldBreath(__instance);
			return false;
		}

		/// <summary>
		/// The game lets a duplicant "catch their breath" wherever it finds Oxygen by its own measure; in air full of Carbon
		/// Dioxide or too thin to breathe that would refill the breath. A duplicant who cannot breathe here cannot catch it either.
		/// </summary>
		private static void CanBreathe(SuffocationMonitor.Instance __instance, ref bool __result)
		{
			if (__result)
			{
				DupAirMonitor monitor = DupAirMonitor.Of(__instance.oxygenBreather);
				if (monitor != null && monitor.Blocked)
				{
					__result = false;
				}
			}
		}

		/// <summary>
		/// Where the game looks for air to breathe or to catch the breath (around a cell): only where a duplicant could breathe
		/// by this mod's scale, not in air too thin or too full of Carbon Dioxide. So a duplicant out of breath goes to look for
		/// good air instead of "catching their breath" in bad air.
		/// </summary>
		private static void BreathableAround(int theSpecificCell, CellOffset[] breathRange, OxygenBreather breather, ref GasBreatherFromWorldProvider.BreathableCellData __result)
		{
			if (!__result.IsBreathable || breather == null || DupAirMonitor.Of(breather) == null)
			{
				return;
			}
			foreach (CellOffset offset in breathRange ?? GasBreatherFromWorldProvider.DEFAULT_BREATHABLE_OFFSETS)
			{
				int cell = Grid.OffsetCell(theSpecificCell, offset);
				if (Grid.IsValidCell(cell) && Grid.Element[cell].IsGas)
				{
					Air air = CellAir(cell);
					if (Lungs(air) >= Hypoxic && air.CarbonDioxide < PassingOut)
					{
						return;
					}
				}
			}
			__result.IsBreathable = false;
		}

		internal static void HoldBreath(OxygenBreather breather)
		{
			breather.hasAirTimer.Stop();
			if (breather.hasAir)
			{
				breather.hasAir = false;
				breather.Trigger((int)GameHashes.OxygenBreatherHasAirChanged, BoxedBools.False);
			}
		}
	}

	internal enum AirZone
	{
		Protected, // a suit, a transit tube, a building's air supply, the head in a liquid or a solid: the game's own logic
		Normal,
		Thin,
		Hypoxia,
		Severe,
		NoOxygen,
		Vacuum,
		Buried, // the head buried in solid ground, with the mod option on: holds their breath
	}

	/// <summary>Per-duplicant state of DupAir: the zone, the breath drain and the pressure history for decompression.</summary>
	internal sealed class DupAirMonitor : KMonoBehaviour, ISim200ms
	{
		// The pressure around, every 0.2 s: the last 1.2 s is "now" (the highest of it: a drop must last), the 3 s before it
		// "before" (the highest of it).
		private const int HistorySamples = 21, RecentSamples = 6;
		private const float DecompressionDrop = 30f, DecompressionCooldown = 10f, EbullismDamagePerSecond = 0.5f;
		private const float EarsRatio = 2f, InjuryRatio = 4f, InjuryBelow = 25f, OpenedFor = 2f;
		// Ebullism damage comes in portions: every hit plays the game's flinch, every 0.2 s it would never stop.
		private const float EbullismDamageEvery = 5f;
		private const float VacuumBreathSeconds = 15f, SevereBreathSeconds = 180f, CarbonDioxideBreathSeconds = 60f;
		private const float ToxicityAfter = 300f, ToxicityRecovery = 300f, SeizureEvery = 60f, SeizureChance = 0.2f, SeizureStress = 5f;
		private const int SeizureShakes = 4; // the game's electric shock reaction, about 10 s

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
		internal string DrainName => drain != null ? drainName : null;
		private float richTime, normalTime, seizureTime;

		internal AirZone Zone { get; private set; } = AirZone.Protected;
		internal bool Watching => Zone != AirZone.Protected;
		internal bool OxygenBlocked => Zone == AirZone.Severe || Zone == AirZone.NoOxygen || Zone == AirZone.Vacuum || Zone == AirZone.Buried;
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
			// Carbon Dioxide is inhaled only while breathing.
			bool breathing = Zone == AirZone.Normal || Zone == AirZone.Thin || Zone == AirZone.Hypoxia;
			float carbon = breathing ? CarbonDioxide : 0f;
			CarbonDioxideLevel = carbon >= DupAir.PassingOut ? 4 : carbon >= DupAir.Poisoning ? 3 : carbon >= DupAir.Headache ? 2 : carbon >= DupAir.Stuffy ? 1 : 0;
			SetEffect(DupAir.StuffyId, CarbonDioxideLevel == 1);
			SetEffect(DupAir.HeadacheId, CarbonDioxideLevel == 2);
			SetEffect(DupAir.PoisoningId, CarbonDioxideLevel >= 3);
			OxygenExcess(dt);
			SetStatus(ref airStatus, ref airShown, WorstAir());
			SetStatus(ref injuryStatus, ref injuryShown, effects == null ? null : effects.HasEffect(DupAir.BarotraumaId) ? DupAir.DecompressionStatus
				: effects.HasEffect(DupAir.PoppedEarsId) ? DupAir.PoppedEarsStatus : null);
			if (Blocked && !wasBlocked)
			{
				DupAir.HoldBreath(breather);
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
			Decompression(Zone == AirZone.Protected || Zone == AirZone.Buried);
		}

		private AirZone Measure(out float headPressure, out bool sealedSuit)
		{
			headPressure = 0f;
			sealedSuit = prefabID.HasTag(GameTags.HasSuitTank) && suitEquipper != null && suitEquipper.IsWearingAirtightSuit() != null;
			OxygenBreather.IGasProvider provider = breather.GetCurrentGasProvider();
			// A building that supplies air while the duplicant works in it (a telescope): they breathe its air, not the room's.
			if (provider != null && !(provider is GasBreatherFromWorldProvider) && !(provider is SuitTank))
			{
				OxygenPressure = 0f;
				CarbonDioxide = 0f;
				return AirZone.Protected;
			}
			// An oxygen mask counts only while its tank has oxygen; an empty mask is left to the game (no air).
			bool mask = !sealedSuit && provider is SuitTank tank && !tank.IsEmpty();
			int cell = Grid.PosToCell(this);
			int head = Grid.IsValidCell(cell) ? Grid.CellAbove(cell) : -1;
			if (!Grid.IsValidCell(head))
			{
				return AirZone.Protected;
			}
			Element headElement = Grid.Element[head];
			headPressure = DupAir.CellAir(head).Pressure;
			if (Mod.Options.BuriedFaceSuffocates && !sealedSuit && !mask && !prefabID.HasTag(GameTags.InTransitTube) && DupEntombed.Buried(head))
			{
				OxygenPressure = 0f;
				CarbonDioxide = 0f;
				return AirZone.Buried;
			}
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

		private System.Guid airStatus, injuryStatus;
		private StatusItem airShown, injuryShown;
		private float decompressionFrom, decompressionTo, decompressionDamage;
		/// <summary>The last decompression: the pressure before and after (kPa) and the damage done (for the self-check).</summary>
		internal float DecompressionFrom => decompressionFrom;
		internal float DecompressionTo => decompressionTo;
		internal float DecompressionDamage => decompressionDamage;

		/// <summary>The worst of the duplicant's air, for the status list (the effects hold the penalties).</summary>
		private StatusItem WorstAir()
		{
			switch (Zone)
			{
				case AirZone.Vacuum: return DupAir.VacuumStatus;
				case AirZone.Buried: return DupAir.BuriedStatus;
				case AirZone.Protected: return null;
			}
			if (CarbonDioxideLevel >= 4) return DupAir.CarbonDioxideOutStatus;
			if (Zone == AirZone.NoOxygen) return DupAir.NoOxygenStatus;
			if (Zone == AirZone.Severe) return DupAir.GaspingStatus;
			if (CarbonDioxideLevel == 3) return DupAir.PoisoningStatus;
			if (Zone == AirZone.Hypoxia) return DupAir.HypoxiaStatus;
			if (CarbonDioxideLevel == 2) return DupAir.HeadacheStatus;
			if (effects != null && effects.HasEffect(DupAir.ToxicityId)) return DupAir.ToxicityStatus;
			if (CarbonDioxideLevel == 1) return DupAir.StuffyStatus;
			return null;
		}

		private void SetStatus(ref System.Guid handle, ref StatusItem shown, StatusItem wanted)
		{
			if (selectable == null || shown == wanted)
			{
				return;
			}
			if (handle != System.Guid.Empty)
			{
				selectable.RemoveStatusItem(handle);
				handle = System.Guid.Empty;
			}
			shown = wanted;
			if (wanted != null)
			{
				handle = selectable.AddStatusItem(wanted, this);
			}
		}

		/// <summary>A status item's tooltip with this duplicant's numbers.</summary>
		internal string Explain(string text)
		{
			float hold = VacuumBreathSeconds + 95f * Mathf.Min(1f, Pressure / 101.325f);
			return text.Replace("{P}", Pressure.ToString("F1")).Replace("{O2}", OxygenPressure.ToString("F1")).Replace("{CO2}", CarbonDioxide.ToString("F1"))
				.Replace("{HOLD}", hold.ToString("F0")).Replace("{FROM}", decompressionFrom.ToString("F0")).Replace("{TO}", decompressionTo.ToString("F0"))
				.Replace("{RATIO}", (decompressionFrom / Mathf.Max(decompressionTo, 0.1f)).ToString("F1")).Replace("{DAMAGE}", decompressionDamage.ToString("F0"));
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
					// Buried: the breath held in normal air before, the game's 110 s.
					seconds = Zone == AirZone.Vacuum ? VacuumBreathSeconds : Zone == AirZone.Severe ? SevereBreathSeconds
						: Zone == AirZone.Buried ? VacuumBreathSeconds + 95f : VacuumBreathSeconds + 95f * Mathf.Min(1f, Pressure / 101.325f);
					name = Zone == AirZone.Severe ? (string)ONIDUP.BREATH.GASPING : Zone == AirZone.Vacuum ? (string)ONIDUP.BREATH.VACUUM
						: Zone == AirZone.Buried ? (string)ONIDUP.BREATH.BURIED : (string)ONIDUP.BREATH.THINAIR;
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
			float oxygen = Zone == AirZone.Protected || Zone == AirZone.Buried ? 0f : OxygenPressure;
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

		// Cells around the head last seen solid or liquid, and when (a cell just dug is not yet part of the air around).
		private readonly System.Collections.Generic.Dictionary<int, float> closedAt = new System.Collections.Generic.Dictionary<int, float>();

		/// <summary>
		/// Pressure around the head: the average over the gas and vacuum cells of the 3x3 square around it. Neighbouring cells of
		/// the game's gas often differ a lot in pressure (a thin cell by a door), while real air evens out at once; a single
		/// thin cell on the way is not a decompression, leaving a pressurised area is. A cell that was solid or liquid within
		/// OpenedFor seconds counts only once it holds gas.
		/// </summary>
		private float AmbientPressure(float now)
		{
			int head = Grid.CellAbove(Grid.PosToCell(this));
			float sum = 0f;
			int count = 0;
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					int around = Grid.OffsetCell(head, dx, dy);
					if (!Grid.IsValidCell(around))
					{
						continue;
					}
					Element element = Grid.Element[around];
					if (element.IsGas)
					{
						sum += DupAir.CellAir(around).Pressure;
						count++;
					}
					else if (element.IsVacuum)
					{
						if (!closedAt.TryGetValue(around, out float closed) || now - closed >= OpenedFor)
						{
							count++;
						}
					}
					else
					{
						closedAt[around] = now;
					}
				}
			}
			if (closedAt.Count > 32)
			{
				var old = new System.Collections.Generic.List<int>();
				foreach (var entry in closedAt)
				{
					if (now - entry.Value >= OpenedFor) old.Add(entry.Key);
				}
				foreach (int cell in old) closedAt.Remove(cell);
			}
			return count == 0 ? 0f : sum / count;
		}

		/// <summary>
		/// A sharp drop of pressure around the duplicant that lasts: Popped Ears, or Decompression Injury with damage by how many
		/// times the pressure fell.
		/// </summary>
		private void Decompression(bool protectedHead)
		{
			// In a sealed suit, a transit tube or with the head in a liquid (a liquid airlock) the air around tells nothing:
			// the history starts over, so leaving the liquid is not taken for a drop of pressure to zero.
			if (protectedHead)
			{
				historyCount = 0;
				return;
			}
			float now = GameClock.Instance.GetTime();
			history[historyAt] = AmbientPressure(now);
			historyAt = (historyAt + 1) % HistorySamples;
			historyCount = Math.Min(historyCount + 1, HistorySamples);
			if (historyCount <= RecentSamples || now - lastDecompression < DecompressionCooldown)
			{
				return;
			}
			// Newest first: the recent samples, then the ones before.
			float current = 0f, before = 0f;
			for (int i = 0; i < historyCount; i++)
			{
				float sample = history[(historyAt - 1 - i + HistorySamples) % HistorySamples];
				if (i < RecentSamples) current = Mathf.Max(current, sample);
				else before = Mathf.Max(before, sample);
			}
			if (before - current < DecompressionDrop || current * EarsRatio > before)
			{
				return;
			}
			float ratio = before / Mathf.Max(current, 0.1f);
			bool injury = ratio >= InjuryRatio && current < InjuryBelow;
			lastDecompression = now;
			historyCount = 0;
			decompressionFrom = before;
			decompressionTo = current;
			decompressionDamage = injury ? Mathf.Clamp(5f * Mathf.Log(ratio / 2f, 2f), 5f, 20f) : 0f;
			Debug.Log($"{ModContext.Tag} air: {gameObject.GetProperName()} at {Grid.PosToXY(transform.GetPosition())}: pressure {before:F0} -> {current:F0} kPa " +
				$"({ratio:F1} times), {(injury ? $"decompression injury, {decompressionDamage:F1} damage" : "popped ears")}");
			if (injury)
			{
				effects?.Add(DupAir.BarotraumaId, true);
				health?.Damage(decompressionDamage);
			}
			else
			{
				effects?.Add(DupAir.PoppedEarsId, true);
			}
		}
	}
}
