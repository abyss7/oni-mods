using System;
using System.IO;
using HarmonyLib;
using Klei.AI;
using OniCommon;
using UnityEngine;

namespace OniDup
{
	/// <summary>
	/// The Manual Generator by the runner's strength (option "WheelByAthletics"). In the game it gives 400 W whoever runs it,
	/// and trains Machinery and the Technicals skills for running. Here:
	/// - power: 150 W + 12.5 W per point of Athletics (150 W untrained, 200 W for a usual starting Duplicant, 275 W at 10,
	///   the game's 400 W at 20), never below 50 W (a weakened or poisoned Duplicant still turns it slowly: from Athletics
	///   -8 down), times the game's generator bonuses (a tune-up); a Duplicant weakened by bad air (less Athletics) runs it
	///   weaker; the light does not matter;
	/// - training: Athletics only (running is not machine work), as fast as in the game;
	/// - food: running costs extra Calories, four times the power given (muscles turn about a quarter of food into work):
	///   +115 kcal for a whole cycle at 200 W, +230 kcal at 400 W.
	/// In the real world a person keeps up 75-100 W untrained, 250-300 W trained and about 400 W at the top for long.
	/// Self-check: the file verify_wheel.txt in the mod folder (a Manual Generator is built by the printing pod if the world
	/// has none; the save is changed: use a copy).
	/// </summary>
	internal static class DupWheel
	{
		internal const float BaseWatts = 150f, WattsPerAthletics = 12.5f, MinWatts = 50f, MuscleEfficiency = 0.25f;
		private const float JoulesPerCalorie = 4.184f; // the game counts Calories in calories (1000 kcal is 1 000 000)
		internal static double ExtraCalories;

		internal static void Apply(Harmony harmony)
		{
			if (!Mod.Options.WheelByAthletics)
			{
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(ManualGenerator), "OnPrefabInit"), postfix: new HarmonyMethod(typeof(DupWheel), nameof(OnlyAthletics)));
			harmony.Patch(AccessTools.PropertyGetter(typeof(Generator), nameof(Generator.WattageRating)), postfix: new HarmonyMethod(typeof(DupWheel), nameof(ByRunner)));
			harmony.Patch(AccessTools.Method(typeof(ManualGenerator), nameof(ManualGenerator.EnergySim200ms)), postfix: new HarmonyMethod(typeof(DupWheel), nameof(BurnWhileRunning)));
			if (File.Exists(Path.Combine(ModContext.Folder, "verify_wheel.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(DupWheel), nameof(Verify)));
			}
		}

		/// <summary>Running trains Athletics only (the game adds it itself); not Machinery, not the Technicals skills; light does not matter.</summary>
		private static void OnlyAthletics(ManualGenerator __instance)
		{
			__instance.attributeConverter = null;
			__instance.skillExperienceSkillGroup = null;
			__instance.lightEfficiencyBonus = false;
		}

		internal static float Watts(WorkerBase runner)
		{
			AttributeInstance athletics = runner != null ? Db.Get().Attributes.Athletics.Lookup(runner.gameObject) : null;
			return Mathf.Max(MinWatts, BaseWatts + WattsPerAthletics * (athletics != null ? athletics.GetTotalValue() : 0f));
		}

		private static void ByRunner(Generator __instance, ref float __result)
		{
			// Every generator's rating comes here (the power grid asks often): the wheel is told by its building first.
			if (__instance.building == null || __instance.building.Def == null || __instance.building.Def.PrefabID != ManualGeneratorConfig.ID)
			{
				return;
			}
			ManualGenerator wheel = __instance.GetComponent<ManualGenerator>();
			if (wheel == null || wheel.worker == null)
			{
				return;
			}
			// The game's generator bonuses (a tune-up) as a share of its own rating.
			float rated = __instance.building != null && __instance.building.Def != null ? __instance.building.Def.GeneratorWattageRating : 0f;
			float bonus = rated > 0f ? __result / rated : 1f;
			__result = Watts(wheel.worker) * bonus;
		}

		private static void BurnWhileRunning(ManualGenerator __instance, float dt)
		{
			WorkerBase runner = __instance.worker;
			if (runner == null || !__instance.GetComponent<Operational>().IsActive)
			{
				return;
			}
			Burn(runner, __instance.GetComponent<Generator>().WattageRating, dt);
		}

		internal static void Burn(WorkerBase runner, float watts, float dt)
		{
			AmountInstance calories = Db.Get().Amounts.Calories.Lookup(runner.gameObject);
			if (calories == null)
			{
				return;
			}
			float burnt = watts / MuscleEfficiency * dt / JoulesPerCalorie;
			calories.ApplyDelta(-burnt);
			ExtraCalories += burnt;
		}

		// ---------- Self-check ----------

		private static float verifyStart = -1f;
		private static bool verified;

		private static void Verify()
		{
			if (verified || GameClock.Instance == null || ClusterManager.Instance == null)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			if (verifyStart < 0f) verifyStart = now;
			if (now < verifyStart + 8f)
			{
				return;
			}
			verified = true;
			try
			{
				ManualGenerator wheel = null;
				foreach (Generator g in Components.Generators.Items)
				{
					ManualGenerator m = g != null ? g.GetComponent<ManualGenerator>() : null;
					if (m != null && m.GetMyWorldId() == ClusterManager.Instance.activeWorldId) { wheel = m; break; }
				}
				if (wheel == null && Components.Telepads.Items.Count > 0)
				{
					BuildingDef def = Assets.GetBuildingDef(ManualGeneratorConfig.ID);
					int cell = Grid.OffsetCell(Grid.PosToCell(Components.Telepads.Items[0]), 4, 0);
					wheel = def.Build(cell, Orientation.Neutral, null, new[] { SimHashes.Copper.CreateTag() }, 293f, playsound: false, now).GetComponent<ManualGenerator>();
				}
				MinionIdentity runner = Components.LiveMinionIdentities.Items.Find(m => m != null && m.GetMyWorldId() == ClusterManager.Instance.activeWorldId);
				if (wheel == null || runner == null)
				{
					Debug.Log($"{ModContext.Tag} wheel check: skipped (no wheel or no Duplicant)");
					return;
				}
				WorkerBase worker = runner.GetComponent<WorkerBase>();
				Generator generator = wheel.GetComponent<Generator>();
				AttributeInstance athletics = Db.Get().Attributes.Athletics.Lookup(runner.gameObject);
				var penalty = new AttributeModifier(Db.Get().Attributes.Athletics.Id, -athletics.GetTotalValue() + 10f, "wheel check");
				athletics.Add(penalty);
				float idle = generator.WattageRating;
				var workerProperty = AccessTools.Property(typeof(Workable), nameof(Workable.worker));
				workerProperty.SetValue(wheel, worker);
				float at10 = generator.WattageRating;
				var weaker = new AttributeModifier(Db.Get().Attributes.Athletics.Id, -30f, "wheel check");
				athletics.Add(weaker);
				float atMinus20 = generator.WattageRating;
				athletics.Remove(weaker);
				AmountInstance calories = Db.Get().Amounts.Calories.Lookup(runner.gameObject);
				float before = calories.value;
				Burn(worker, at10, 600f);
				float cycle = (before - calories.value) / 1000f;
				calories.SetValue(before);
				workerProperty.SetValue(wheel, null);
				athletics.Remove(penalty);
				bool ok = Mathf.Abs(at10 - 275f) < 0.5f && Mathf.Abs(atMinus20 - MinWatts) < 0.5f && Mathf.Abs(cycle - 275f / MuscleEfficiency * 600f / JoulesPerCalorie / 1000f) < 0.5f
					&& wheel.attributeConverter == null && wheel.skillExperienceSkillGroup == null;
				Debug.Log($"{ModContext.Tag} wheel check: {(ok ? "ok" : "FAILED")} (idle rating {idle:F0} W; {runner.GetProperName()} at Athletics 10: {at10:F1} W, at -20: {atMinus20:F1} W, " +
					$"a cycle of running costs {cycle:F0} kcal more; trains Machinery {wheel.attributeConverter != null}, Technicals skills {wheel.skillExperienceSkillGroup != null})");
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} wheel check: FAILED with {e}");
			}
		}
	}
}
