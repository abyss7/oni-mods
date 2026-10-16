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
	/// A drop of pressure around the duplicant by more than half and more than 30 kPa within 2 s pops the eardrums (the game's
	/// "Popped Eardrums") and does 10 damage. Bionic duplicants are not affected yet.
	/// </summary>
	internal static class DupAir
	{
		internal const float KiloPascalPerKg = 101.325f / 1.8f;
		internal const float Vapour = 6.3f;
		internal const float Normal = 15f, Thin = 11f, Hypoxic = 8f, Severe = 6f;
		internal const string HypoxiaId = "OniDupHypoxia", SevereHypoxiaId = "OniDupSevereHypoxia", EbullismId = "OniDupEbullism";

		internal static string Mode { get; private set; } = "off";

		internal static void Apply(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(MinionConfig), nameof(MinionConfig.CreatePrefab)), postfix: new HarmonyMethod(typeof(DupAir), nameof(AddMonitor)));
				harmony.Patch(AccessTools.Method(typeof(Db), nameof(Db.Initialize)), postfix: new HarmonyMethod(typeof(DupAir), nameof(AddEffects)));
				harmony.Patch(AccessTools.Method(typeof(OxygenBreather), nameof(OxygenBreather.IsLowOxygen)), postfix: new HarmonyMethod(typeof(DupAir), nameof(LowOxygen)));
				harmony.Patch(AccessTools.PropertyGetter(typeof(OxygenBreather), nameof(OxygenBreather.HasOxygen)), postfix: new HarmonyMethod(typeof(DupAir), nameof(HasOxygen)));
				harmony.Patch(AccessTools.PropertyGetter(typeof(OxygenBreather), nameof(OxygenBreather.IsOutOfOxygen)), postfix: new HarmonyMethod(typeof(DupAir), nameof(OutOfOxygen)));
				Mode = "on";
			}
			catch (Exception e)
			{
				Mode = "off: " + e.Message;
				Debug.LogWarning($"{ModContext.Tag} DupAir failed to apply: {e}");
			}
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
			var hypoxia = new Effect(HypoxiaId, name, ONIDUP.EFFECTS.HYPOXIA.TOOLTIP, 0f, true, true, true);
			hypoxia.Add(new AttributeModifier(stress, 15f / 600f, name));
			hypoxia.Add(new AttributeModifier(attributes.Athletics.Id, -4f, name));
			hypoxia.Add(new AttributeModifier(attributes.Learning.Id, -3f, name));
			foreach (Klei.AI.Attribute attribute in work)
			{
				hypoxia.Add(new AttributeModifier(attribute.Id, -2f, name));
			}
			Db.Get().effects.Add(hypoxia);

			name = ONIDUP.EFFECTS.SEVEREHYPOXIA.NAME;
			var severe = new Effect(SevereHypoxiaId, name, ONIDUP.EFFECTS.SEVEREHYPOXIA.TOOLTIP, 0f, true, true, true, Db.Get().Emotes.Minion.Cough, 20f);
			severe.Add(new AttributeModifier(stress, 30f / 600f, name));
			severe.Add(new AttributeModifier(attributes.Athletics.Id, -8f, name));
			severe.Add(new AttributeModifier(attributes.Learning.Id, -4f, name));
			foreach (Klei.AI.Attribute attribute in work)
			{
				severe.Add(new AttributeModifier(attribute.Id, -4f, name));
			}
			Db.Get().effects.Add(severe);

			Db.Get().effects.Add(new Effect(EbullismId, ONIDUP.EFFECTS.EBULLISM.NAME, ONIDUP.EFFECTS.EBULLISM.TOOLTIP, 0f, true, true, true));
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
		private const float VacuumBreathSeconds = 15f, SevereBreathSeconds = 180f;
		private const int AirChangedEvent = -933153513; // GameHashes.OxygenBreatherHasAirChanged

		private static readonly System.Collections.Generic.Dictionary<OxygenBreather, DupAirMonitor> byBreather =
			new System.Collections.Generic.Dictionary<OxygenBreather, DupAirMonitor>();

#pragma warning disable CS0649 // filled by the game ([MyCmpGet])
		[MyCmpGet] private OxygenBreather breather;
		[MyCmpGet] private Effects effects;
		[MyCmpGet] private Health health;
		[MyCmpGet] private KPrefabID prefabID;
		[MyCmpGet] private SuitEquipper suitEquipper;
#pragma warning restore CS0649

		private readonly float[] history = new float[HistorySamples];
		private int historyCount, historyAt;
		private float lastDecompression = -1000f;
		private AttributeModifier drain;

		internal AirZone Zone { get; private set; } = AirZone.Protected;
		internal bool Watching => Zone != AirZone.Protected;
		internal bool Blocked => Zone == AirZone.Severe || Zone == AirZone.NoOxygen || Zone == AirZone.Vacuum;
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
				breather.noOxygenThreshold = (Severe + Vapour) / DupAir.KiloPascalPerKg;
				breather.lowOxygenThreshold = (DupAir.Normal + Vapour) / DupAir.KiloPascalPerKg;
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
			Zone = Measure(out float pressure, out bool sealedSuit);
			Pressure = pressure;
			SetEffect(DupAir.HypoxiaId, Zone == AirZone.Hypoxia);
			SetEffect(DupAir.SevereHypoxiaId, Zone == AirZone.Severe);
			SetEffect(DupAir.EbullismId, Zone == AirZone.Vacuum);
			if (Blocked != wasBlocked)
			{
				Trigger(AirChangedEvent, Blocked ? BoxedBools.False : BoxedBools.True);
			}
			UpdateDrain();
			if (Zone == AirZone.Vacuum && health != null)
			{
				health.Damage(EbullismDamagePerSecond * dt);
			}
			Decompression(pressure, sealedSuit);
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
			headPressure = headElement.IsGas ? Grid.Mass[head] * DupAir.KiloPascalPerKg : 0f;
			if (sealedSuit || prefabID.HasTag(GameTags.InTransitTube) || !(headElement.IsGas || headElement.IsVacuum))
			{
				return AirZone.Protected;
			}
			if (headPressure < Vapour)
			{
				return AirZone.Vacuum;
			}
			// The best cell around the head, as the game picks it for breathing.
			float best = 0f;
			foreach (CellOffset offset in GasBreatherFromWorldProvider.DEFAULT_BREATHABLE_OFFSETS)
			{
				int around = Grid.OffsetCell(cell, offset);
				if (!Grid.IsValidCell(around) || !Grid.Element[around].IsGas)
				{
					continue;
				}
				float total = Grid.Mass[around];
				if (!(total > 0f))
				{
					continue;
				}
				float pressure = total * DupAir.KiloPascalPerKg - Vapour;
				float share = mask ? 1f : Mathf.Clamp01(GasBreatherFromWorldProvider.GetBreathableCellMass(around, out _) / total);
				best = Mathf.Max(best, pressure * share);
			}
			OxygenPressure = best;
			if (best >= DupAir.Normal) return AirZone.Normal;
			if (best >= DupAir.Thin) return AirZone.Thin;
			if (best >= DupAir.Hypoxic) return AirZone.Hypoxia;
			if (best >= DupAir.Severe) return AirZone.Severe;
			return AirZone.NoOxygen;
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
			if (Blocked)
			{
				float seconds = Zone == AirZone.Vacuum ? VacuumBreathSeconds : Zone == AirZone.Severe ? SevereBreathSeconds
					: VacuumBreathSeconds + 95f * Mathf.Min(1f, Pressure / 101.325f);
				float rate = DUPLICANTSTATS.STANDARD.Breath.BREATH_RATE;
				target = rate - DUPLICANTSTATS.STANDARD.Breath.BREATH_BAR_TOTAL_AMOUNT / seconds;
			}
			if (drain != null && Mathf.Abs(drain.Value - target) < 0.01f)
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
				drain = new AttributeModifier(Db.Get().Amounts.Breath.deltaAttribute.Id, target, ONIDUP.EFFECTS.LOWPRESSURE.NAME);
				attributes.Add(drain);
			}
		}

		/// <summary>A sharp drop of pressure around the duplicant: barotrauma (the game's "Popped Eardrums") and damage.</summary>
		private void Decompression(float pressure, bool sealedSuit)
		{
			float highest = pressure;
			for (int i = 0; i < historyCount; i++)
			{
				highest = Mathf.Max(highest, history[i]);
			}
			history[historyAt] = pressure;
			historyAt = (historyAt + 1) % HistorySamples;
			historyCount = Math.Min(historyCount + 1, HistorySamples);
			if (sealedSuit || highest - pressure < DecompressionDrop || pressure > 0.5f * highest || GameClock.Instance.GetTime() - lastDecompression < DecompressionCooldown)
			{
				return;
			}
			lastDecompression = GameClock.Instance.GetTime();
			historyCount = 0;
			effects?.Add("PoppedEarDrums", true);
			health?.Damage(DecompressionDamage);
		}
	}
}
