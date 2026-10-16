using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Klei.AI;
using OniCommon;
using UnityEngine;

namespace OniLiquidMix
{
	/// <summary>
	/// The rest of the game logic that looks at the liquid in a cell: by the composition of
	/// a mixture instead of the main liquid. Applied together with LiquidMixLogic and counted in its statistics.
	/// </summary>
	internal static class LiquidMixWorld
	{
		private static readonly ushort[] elements = new ushort[LiquidMixture.Slots];
		private static readonly float[] shares = new float[LiquidMixture.Slots];

		internal static readonly (string name, Action<Harmony> apply)[] Patches =
		{
			("eye irritation", Irritation),
			("slipping", Slipping),
			("power bank in water", BankInWater),
			("power bank explosion", BankExplosion),
			("mopping", Mopping),
			("liquid tepidizer", Tepidizer),
			("food in liquid", FoodInLiquid),
			("pokeshell morphs", PokeshellMorphs),
			("germs on items", GermsOnItems),
			("tile leaks", TileLeaks),
			("rocket interior teardown", RocketTeardown),
			("radiation through liquid", Radiation),
		};

		private static HarmonyMethod Method(string name)
		{
			return new HarmonyMethod(typeof(LiquidMixWorld), name);
		}

		/// <summary>Liquids of a mixed liquid cell into the shared buffers; 0 if the cell is not a mixture.</summary>
		private static int Mixture(int cell)
		{
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsLiquid)
			{
				return 0;
			}
			int count = LiquidMixture.Liquids(cell, elements, shares);
			return count > 1 ? count : 0;
		}

		/// <summary>Share by mass of the liquids of a mixture that pass the test.</summary>
		private static float ShareWhere(int count, Func<Element, bool> test)
		{
			float share = 0f;
			for (int i = 0; i < count; i++)
			{
				if (test(ElementLoader.elements[elements[i]]))
				{
					share += shares[i];
				}
			}
			return share;
		}

		// ---------- Eye irritation: the rate of each liquid by its share ----------

		private static void Irritation(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(GasLiquidExposureMonitor), "UpdateExposure"), postfix: Method(nameof(ExposureByMixture)));
		}

		private static void ExposureByMixture(GasLiquidExposureMonitor __instance, GasLiquidExposureMonitor.Instance smi, float dt)
		{
			if (smi.isInAirtightEnvironment || smi.isImmuneToIrritability)
			{
				return;
			}
			int cell = Grid.CellAbove(Grid.PosToCell(smi.gameObject));
			int count = Mixture(cell);
			if (count == 0)
			{
				return;
			}
			// The game computed the main liquid's rate for the whole cell mass; the correct rate is the sum of each liquid's
			// rate by its share. What was accumulated in this step is corrected by the difference, as the gas mixture mod does.
			float byDefault = (!(Grid.Temperature[cell] >= -13657.5f) || !(Grid.Temperature[cell] <= 27315f)) ? 2f : 1f;
			float rate = 0f;
			for (int i = 0; i < count; i++)
			{
				SimHashes id = ElementLoader.elements[elements[i]].id;
				if (!smi.exposureRateOverride.TryGetValue(id, out float value) && !GasLiquidExposureMonitor.customExposureRates.TryGetValue(id, out value))
				{
					value = byDefault;
				}
				rate += value * shares[i];
			}
			rate *= Grid.Mass[cell] / 1000f;
			if (Mathf.Approximately(rate, smi.exposureRate))
			{
				return;
			}
			LiquidMixLogic.Differs("eye irritation");
			smi.exposure = MathUtil.Clamp(0f, 30f, smi.exposure + (rate - smi.exposureRate) * dt);
			smi.exposureRate = rate;
			__instance.ApplyEffects(smi);
		}

		// ---------- Slipping: a noticeable share of a slippery liquid ----------

		private static void Slipping(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SlipperyMonitor), "IsStandingOnASlipperyCell"), postfix: Method(nameof(SlipperyMixture)));
		}

		private static void SlipperyMixture(SlipperyMonitor.Instance smi, ref bool __result)
		{
			if (!smi.IsWalking)
			{
				return;
			}
			int cell = Grid.PosToCell(smi);
			int count = Mixture(cell);
			if (count == 0)
			{
				return;
			}
			int below = Grid.OffsetCell(cell, 0, -1);
			GameObject floor = Grid.IsValidCell(below) ? Grid.Objects[below, 9] : null;
			if (floor != null && floor.GetComponent<KPrefabID>().HasTag(GameTags.PreventsSlipping))
			{
				return;
			}
			bool slippery = ShareWhere(count, element => element.IsSlippery) >= LiquidMixture.PresenceShare
				|| (Grid.IsValidCell(below) && Grid.Element[below].IsSolid && Grid.Element[below].IsSlippery);
			if (slippery != __result)
			{
				LiquidMixLogic.Differs("slipping");
				__result = slippery;
			}
		}

		// ---------- Power banks: water damage by the Water share; an explosion keeps the mixture ----------

		private static void BankInWater(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Electrobank), "EvaluateWaterDamage"), prefix: Method(nameof(BankWaterDamage)));
		}

		private static bool BankWaterDamage(Electrobank __instance, float dt)
		{
			int count = Mixture(__instance.pickupable.cachedCell);
			if (count == 0)
			{
				return true;
			}
			bool water = ShareWhere(count, element => element.HasTag(GameTags.AnyWater)) >= LiquidMixture.PresenceShare;
			if (water != Grid.Element[__instance.pickupable.cachedCell].HasTag(GameTags.AnyWater))
			{
				LiquidMixLogic.Differs("power bank in water");
			}
			if (water && UnityEngine.Random.Range(1, 101) > 75)
			{
				PopFXManager.Instance.SpawnFX(PopFXManager.Instance.sprite_Negative, STRINGS.UI.GAMEOBJECTEFFECTS.DAMAGE_POPS.POWER_BANK_WATER_DAMAGE, __instance.transform);
				__instance.Damage(UnityEngine.Random.Range(0f, dt));
			}
			return false;
		}

		private static void BankExplosion(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Electrobank), nameof(Electrobank.Explode)), prefix: Method(nameof(ExplodeInMixture)));
		}

		/// <summary>The game heats the cell by replacing it with its main liquid; in a mixture the same heat goes in as energy.</summary>
		private static bool ExplodeInMixture(Electrobank __instance)
		{
			int cell = Grid.PosToCell(__instance.gameObject.transform.position);
			if (Mixture(cell) == 0)
			{
				return true;
			}
			SimMessages.ModifyEnergy(cell, __instance.charge, 9999f, SimMessages.EnergySourceID.Burner);
			Game.Instance.SpawnFX(SpawnFXHashes.MeteorImpactMetal, __instance.gameObject.transform.position, 0f);
			KFMOD.PlayOneShot(GlobalAssets.GetSound("Battery_explode"), __instance.gameObject.transform.position);
			if (__instance.rechargeable)
			{
				Electrobank.ReplaceEmptyWithGarbage(__instance.gameObject);
			}
			else
			{
				__instance.gameObject.DeleteObject();
			}
			LiquidMixLogic.Differs("power bank explosion");
			return false;
		}

		// ---------- Mopping: every liquid of the puddle, by share ----------

		private static void Mopping(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Moppable), nameof(Moppable.MopCell)), prefix: Method(nameof(MopMixture)));
		}

		private static bool MopMixture(int cell, float amount, Action<Sim.MassConsumedCallback, object> cb)
		{
			int count = Mixture(cell);
			if (count == 0)
			{
				return true;
			}
			for (int i = 0; i < count; i++)
			{
				int callbackIdx = cb != null ? Game.Instance.massConsumedCallbackManager.Add(cb, null, "Moppable").index : -1;
				SimMessages.ConsumeMass(cell, ElementLoader.elements[elements[i]].id, amount * shares[i], 1, callbackIdx);
			}
			LiquidMixLogic.Differs("mopping");
			return false;
		}

		// ---------- Liquid Tepidizer: the liquid of a mixture closest to boiling boils off ----------

		private static void Tepidizer(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(LiquidHeaterBubbleEmitter), nameof(LiquidHeaterBubbleEmitter.Sim1000ms)), prefix: Method(nameof(TepidizerMixture)));
		}

		/// <summary>
		/// The game's Sim1000ms for cells of one liquid; in a mixed cell the energy boils off the liquid that is closest to its
		/// boiling point, only its share is taken from the cell (ConsumeMass), and the bubble and byproduct are that liquid's.
		/// </summary>
		private static bool TepidizerMixture(LiquidHeaterBubbleEmitter __instance)
		{
			if (__instance.accruedEnergyKJ <= 0f)
			{
				return false;
			}
			int[] cells = __instance.building.PlacementCells;
			bool mixed = false;
			foreach (int cell in cells)
			{
				mixed = mixed || Mixture(cell) > 0;
			}
			if (!mixed)
			{
				return true;
			}
			float energy = __instance.accruedEnergyKJ / cells.Length;
			float spent = 0f;
			foreach (int cell in cells)
			{
				int count = Mixture(cell);
				Element element;
				float available;
				if (count == 0)
				{
					element = Grid.Element[cell];
					available = Grid.Mass[cell];
				}
				else
				{
					// The liquid that boils at the lowest temperature above the cell's temperature.
					element = null;
					available = 0f;
					for (int i = 0; i < count; i++)
					{
						Element liquid = ElementLoader.elements[elements[i]];
						if (liquid.HasTransitionUp && liquid.highTemp + 3f > Grid.Temperature[cell] && (element == null || liquid.highTemp < element.highTemp))
						{
							element = liquid;
							available = Grid.Mass[cell] * shares[i];
						}
					}
				}
				if (element == null || !element.IsLiquid || !element.HasTransitionUp || available <= 0f)
				{
					continue;
				}
				float temperature = Grid.Temperature[cell];
				float boiling = element.highTemp + 3f;
				float gap = boiling - temperature;
				if (gap <= 0f)
				{
					continue;
				}
				float perKg = element.specificHeatCapacity * gap;
				float boiled = energy / perKg;
				if (boiled < 0.0020000022f)
				{
					continue;
				}
				boiled = Mathf.Min(boiled, available);
				float bubbleMass = boiled;
				byte disease = Grid.DiseaseIdx[cell];
				float cellMass = Grid.Mass[cell];
				int germs = cellMass > 0f ? (int)(Grid.DiseaseCount[cell] * (boiled / cellMass)) : 0;
				int bubbleGerms = germs;
				Vector2 position = Grid.CellToPosCCC(cell, Grid.SceneLayer.Front);
				if (__instance.TryEmitOreByproduct(element, boiled, ref bubbleMass, disease, germs, ref bubbleGerms, cell, temperature, position) == LiquidHeaterBubbleEmitter.EmitResult.InsufficientMass)
				{
					continue;
				}
				spent += boiled * perKg;
				if (count == 0)
				{
					SimMessages.AddRemoveSubstance(cell, element.id, CellEventLogger.Instance.ElementEmitted, -boiled, temperature, disease, -bubbleGerms);
				}
				else
				{
					SimMessages.ConsumeMass(cell, element.id, boiled, 1, -1);
					LiquidMixLogic.Differs("liquid tepidizer");
				}
				BubbleManager.instance.SpawnBubble(element.highTempTransitionTarget, position, bubbleMass, boiling, new BubbleManager.Disease { Idx = disease, Count = bubbleGerms });
			}
			__instance.accruedEnergyKJ = Mathf.Max(__instance.accruedEnergyKJ - spent, 0f);
			return false;
		}

		// ---------- Food in a liquid: Polluted Brine in a mixture contaminates ----------

		private static void FoodInLiquid(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Rottable), nameof(Rottable.AtmosphereQuality)), postfix: Method(nameof(FoodMixture)));
		}

		private static void FoodMixture(IRottable rottable, ref Rottable.RotAtmosphereQuality __result)
		{
			if (__result == Rottable.RotAtmosphereQuality.Contaminating || rottable == null || rottable.gameObject == null)
			{
				return;
			}
			// The game takes the worse of the item's cell and the cell above; contaminating wins over everything.
			int cell = Grid.PosToCell(rottable.gameObject);
			foreach (int target in new[] { cell, Grid.IsValidCell(cell) ? Grid.CellAbove(cell) : -1 })
			{
				int count = Mixture(target);
				if (count == 0)
				{
					continue;
				}
				float contaminating = ShareWhere(count, element => Rottable.AtmosphereModifier.TryGetValue((int)element.id, out Rottable.RotAtmosphereQuality quality)
					&& quality == Rottable.RotAtmosphereQuality.Contaminating);
				if (contaminating >= LiquidMixture.PresenceShare)
				{
					LiquidMixLogic.Differs("food in liquid");
					__result = Rottable.RotAtmosphereQuality.Contaminating;
					return;
				}
			}
		}

		// ---------- Pokeshell morphs: living in Ethanol or Water by its share ----------

		private static void PokeshellMorphs(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(ModifierSet), nameof(ModifierSet.CreateFertilityModifier)), postfix: Method(nameof(MorphModifier)));
		}

		private static readonly Dictionary<string, SimHashes> MorphLiquids = new Dictionary<string, SimHashes>
		{
			{ "PokeLumber", SimHashes.Ethanol }, { "PokeFreshWater", SimHashes.Water },
		};

		/// <summary>
		/// The Oakshell and Sanishell eggs: the game adds the chance while the crab is in a substantial amount of Ethanol or
		/// Water as the main liquid and takes it away otherwise. Here the chance changes by the liquid's share in the cell:
		/// all of the rate in the pure liquid, minus all of it without the liquid, in between by the share.
		/// </summary>
		private static void MorphModifier(string id, FertilityModifier __result)
		{
			if (__result == null || !MorphLiquids.TryGetValue(id, out SimHashes liquid))
			{
				return;
			}
			const float rate = 0.00025f;
			__result.ApplyFunction = (inst, eggType) =>
			{
				CritterElementMonitor.Instance monitor = inst.gameObject.GetSMI<CritterElementMonitor.Instance>();
				if (monitor == null)
				{
					monitor = new CritterElementMonitor.Instance(inst.master);
					monitor.StartSM();
				}
				monitor.OnUpdateEggChances += dt =>
				{
					int cell = Grid.PosToCell(inst);
					if (!Grid.IsValidCell(cell))
					{
						return;
					}
					ushort element = ElementLoader.FindElementByHash(liquid).idx;
					float share = Grid.IsSubstantialLiquid(cell) ? LiquidMixture.Share(cell, element) : 0f;
					inst.AddBreedingChance(eggType, dt * rate * (2f * share - 1f));
				};
			};
		}

		// ---------- Germs on items in a liquid: the growth rules of each liquid by share ----------

		private static void GermsOnItems(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(DiseaseContainers), nameof(DiseaseContainers.CalculateDelta),
				new[] { typeof(int), typeof(ushort), typeof(float), typeof(int), typeof(float), typeof(float), typeof(Disease), typeof(float), typeof(bool) }),
				postfix: Method(nameof(ItemGermsByMixture)));
		}

		private static void ItemGermsByMixture(int disease_count, int environment_cell, Disease disease, float dt, ref float __result)
		{
			int count = Mixture(environment_cell);
			if (count == 0)
			{
				return;
			}
			float blended = 0f;
			for (int i = 0; i < count; i++)
			{
				blended += disease.elemExposureInfo[elements[i]].CalculateExposureDiseaseCountDelta(disease_count, dt) * shares[i];
			}
			float byMain = disease.elemExposureInfo[Grid.ElementIdx[environment_cell]].CalculateExposureDiseaseCountDelta(disease_count, dt);
			if (!Mathf.Approximately(blended, byMain))
			{
				LiquidMixLogic.Differs("germs on items");
				__result += blended - byMain;
			}
		}

		// ---------- Leaks through damaged tiles: the leaking liquid is picked by share ----------

		private static void TileLeaks(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(WorldDamage), "DelayedSpawnFX"), prefix: Method(nameof(LeakingLiquid)));
		}

		/// <summary>
		/// A leak takes 1 kg from the source cell (from all liquids of a mixture in proportion) and drops 1 kg of the main liquid
		/// on the other side. Here the dropped liquid is picked at random by share, so every liquid keeps its mass on average.
		/// </summary>
		private static void LeakingLiquid(int src_cell, ref Element elem, ref ushort idx)
		{
			int count = Mixture(src_cell);
			if (count == 0)
			{
				return;
			}
			float pick = UnityEngine.Random.value;
			for (int i = 0; i < count; i++)
			{
				pick -= shares[i];
				if (pick <= 0f || i == count - 1)
				{
					idx = elements[i];
					elem = ElementLoader.elements[idx];
					return;
				}
			}
		}

		// ---------- Rocket interior teardown: every liquid of a mixture becomes its own resource ----------

		private static void RocketTeardown(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(WorldContainer), "TransferLiquidsSolidsAndGases"), prefix: Method(nameof(TransferToResources)));
			harmony.Patch(AccessTools.Method(typeof(WorldContainer), "TransferLiquidsSolidsAndGasesToDebris"), prefix: Method(nameof(TransferToDebris)));
		}

		private static bool TransferToResources(WorldContainer __instance, Vector3 pos, HashSet<int> noRefundTiles)
		{
			for (int x = (int)__instance.minimumBounds.x; x <= __instance.maximumBounds.x; x++)
			{
				for (int y = (int)__instance.minimumBounds.y; y <= __instance.maximumBounds.y; y++)
				{
					int cell = Grid.XYToCell(x, y);
					if (noRefundTiles.Contains(cell))
					{
						continue;
					}
					Element element = Grid.Element[cell];
					if (element == null || element.IsVacuum)
					{
						continue;
					}
					int count = Mixture(cell);
					if (count == 0)
					{
						element.substance.SpawnResource(pos, Grid.Mass[cell], Grid.Temperature[cell], Grid.DiseaseIdx[cell], Grid.DiseaseCount[cell]);
						continue;
					}
					for (int i = 0; i < count; i++)
					{
						ElementLoader.elements[elements[i]].substance.SpawnResource(pos, Grid.Mass[cell] * shares[i], Grid.Temperature[cell], Grid.DiseaseIdx[cell], (int)(Grid.DiseaseCount[cell] * shares[i]));
					}
				}
			}
			return false;
		}

		private static bool TransferToDebris(WorldContainer __instance, ref List<Storage> debrisObjects, HashSet<int> noRefundTiles, SimHashes debrisContainerElement)
		{
			for (int x = (int)__instance.minimumBounds.x; x <= __instance.maximumBounds.x; x++)
			{
				for (int y = (int)__instance.minimumBounds.y; y <= __instance.maximumBounds.y; y++)
				{
					int cell = Grid.XYToCell(x, y);
					if (noRefundTiles.Contains(cell))
					{
						continue;
					}
					Element element = Grid.Element[cell];
					if (element == null || element.IsVacuum)
					{
						continue;
					}
					int count = Mixture(cell);
					int parts = count == 0 ? 1 : count;
					for (int i = 0; i < parts; i++)
					{
						SimHashes id = count == 0 ? element.id : ElementLoader.elements[elements[i]].id;
						float mass = Grid.Mass[cell] * 0.5f * (count == 0 ? 1f : shares[i]);
						if ((debrisObjects.Count == 0 || debrisObjects[debrisObjects.Count - 1].RemainingCapacity() == 0f) && mass > 0f)
						{
							debrisObjects.Add(CraftModuleInterface.SpawnRocketDebris(" from World Tiles", debrisContainerElement));
						}
						Storage storage = debrisObjects[debrisObjects.Count - 1];
						while (mass > 0f)
						{
							float portion = Mathf.Min(mass, storage.RemainingCapacity());
							mass -= portion;
							storage.AddOre(id, portion, Grid.Temperature[cell], Grid.DiseaseIdx[cell], Grid.DiseaseCount[cell]);
							if (mass > 0f)
							{
								storage = CraftModuleInterface.SpawnRocketDebris(" from World Tiles", debrisContainerElement);
								debrisObjects.Add(storage);
							}
						}
					}
				}
			}
			return false;
		}

		// ---------- Radiation through a liquid: by the composition of a mixture ----------

		private static void Radiation(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(RadiationGridEmitter), nameof(RadiationGridEmitter.Emit)), transpiler: Method(nameof(EmitThroughMixture)));
			harmony.Patch(AccessTools.Method(typeof(GameUtil), nameof(GameUtil.GetRadiationAbsorptionPercentage), new[] { typeof(int) }), postfix: Method(nameof(AbsorptionOfMixture)));
		}

		/// <summary>Replaces "Grid.Element[cell].molarMass" in the emitter's ray with the molar mass of the cell's mixture.</summary>
		private static IEnumerable<CodeInstruction> EmitThroughMixture(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> code = new List<CodeInstruction>(instructions);
			FieldInfo gridElement = AccessTools.Field(typeof(Grid), nameof(Grid.Element));
			FieldInfo molar = AccessTools.Field(typeof(Element), nameof(Element.molarMass));
			int replaced = 0;
			for (int i = 3; i < code.Count; i++)
			{
				if (code[i].LoadsField(molar) && code[i - 1].opcode == OpCodes.Ldelem_Ref && code[i - 3].LoadsField(gridElement))
				{
					// ldsfld Grid.Element; ldloc cell; ldelem.ref; ldfld molarMass -> ldloc cell; call MolarMass(cell)
					code[i - 3].opcode = OpCodes.Nop;
					code[i - 3].operand = null;
					code[i - 1].opcode = OpCodes.Nop;
					code[i - 1].operand = null;
					code[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LiquidMixWorld), nameof(MolarMass))).MoveLabelsFrom(code[i]);
					replaced++;
				}
			}
			if (replaced != 1)
			{
				throw new InvalidOperationException($"expected one read of the cell's molar mass, found {replaced}");
			}
			return code;
		}

		private static float MolarMass(int cell)
		{
			int count = Mixture(cell);
			if (count == 0)
			{
				return Grid.Element[cell].molarMass;
			}
			float molar = 0f;
			for (int i = 0; i < count; i++)
			{
				molar += ElementLoader.elements[elements[i]].molarMass * shares[i];
			}
			return molar;
		}

		private static void AbsorptionOfMixture(int cell, ref float __result)
		{
			int count = Mixture(cell);
			if (count == 0)
			{
				return;
			}
			float result = 0f;
			for (int i = 0; i < count; i++)
			{
				result += GameUtil.GetRadiationAbsorptionPercentage(ElementLoader.elements[elements[i]], Grid.Mass[cell], false) * shares[i];
			}
			__result = result;
		}
	}
}
