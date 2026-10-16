using System;
using System.Collections.Generic;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniLiquidMix
{
	/// <summary>
	/// Game logic by the composition of a liquid mixture, not only by the cell's main liquid (modelled on GasLayerLogic of the gas
	/// mixture mod). Compositions come from the plugin's snapshot (LiquidMixture); a liquid counts as present from a share
	/// by mass of LiquidMixture.PresenceShare, for the sensor by its slider. Done:
	/// - liquid sensor: sees its liquid inside a mixture too (threshold by slider, like the gas sensor);
	/// - bionics: a wet floor does damage if the mixture underfoot holds a noticeable amount of Water, not only if Water is the main liquid;
	/// - algae in water (oxygen emission): Water in a mixture counts if there is a noticeable amount of it;
	/// - crab molting: crabs do not molt in a cell with a noticeable share of Ethanol;
	/// - the Pitcher Pump lists every liquid of the mixtures it reaches, with its share of the mass (the simulation itself
	///   takes the selected liquid out of the mixture);
	/// - a body in water pollutes only the Water of a mixture, not its other liquids (the plugin converts the share);
	/// - plants: the part of a mixture made of the plant's liquids suits it, and only that part counts as its pressure
	///   (like gases in the gas mixture mod; the other liquids of the mixture do not count but do no harm).
	/// The log (LiquidSurvey) gets what was applied and how many times the answer by composition differed from the game's answer.
	/// </summary>
	internal static class LiquidMixLogic
	{
		private static readonly Dictionary<string, long> differs = new Dictionary<string, long>();
		private static readonly List<string> applied = new List<string>(), failed = new List<string>();
		private static readonly ushort[] elements = new ushort[LiquidMixture.Slots];
		private static readonly float[] shares = new float[LiquidMixture.Slots];

		internal static void Apply(Harmony harmony)
		{
			(string name, System.Action<Harmony> apply)[] patches =
			{
				("sensor", Sensor),
				("bionic wet floor", BionicWetFloor),
				("algae in water", AlgaeInWater),
				("molting", Molting),
				("pitcher pump", PitcherPump),
				("plants", Plants),
				("body in water", BodyInWater),
			};
			var all = new List<(string name, System.Action<Harmony> apply)>(patches);
			all.AddRange(LiquidMixWorld.Patches);
			foreach ((string name, System.Action<Harmony> apply) in all)
			{
				try
				{
					apply(harmony);
					applied.Add(name);
				}
				catch (Exception e)
				{
					failed.Add(name);
					Debug.LogWarning($"{ModContext.Tag} LiquidMixLogic: {name} failed to apply, this check stays by the main liquid: {e}");
				}
			}
		}

		internal static string Stats()
		{
			var parts = new List<string>();
			foreach (KeyValuePair<string, long> pair in differs)
			{
				parts.Add($"{pair.Key}={pair.Value}");
			}
			return $"applied: {string.Join(", ", applied)}{(failed.Count > 0 ? "; FAILED: " + string.Join(", ", failed) : "")}; " +
				$"answers that differ from the main liquid: {(parts.Count > 0 ? string.Join(", ", parts) : "none")}; " +
				$"composition snapshots taken {LiquidMixture.Fetches}, mixed cells in the last one {LiquidMixture.Records}";
		}

		internal static void Differs(string what)
		{
			differs.TryGetValue(what, out long count);
			differs[what] = count + 1;
		}

		private static HarmonyMethod Method(string name)
		{
			return new HarmonyMethod(typeof(LiquidMixLogic), name);
		}

		/// <summary>Whether the cell holds a noticeable amount of liquid with the tag (by composition).</summary>
		private static bool HasTagged(int cell, Tag tag)
		{
			int count = LiquidMixture.Liquids(cell, elements, shares);
			for (int i = 0; i < count; i++)
			{
				Element element = ElementLoader.elements[elements[i]];
				if (element.IsLiquid && element.HasTag(tag) && shares[i] >= LiquidMixture.PresenceShare)
				{
					return true;
				}
			}
			return false;
		}

		// ---------- Liquid sensor: the liquid is present in the cell ----------

		private static void Sensor(Harmony harmony)
		{
			// The gas sensor (same class) stays with the gas mixture mod: only the "liquid" state is handled here.
			harmony.Patch(AccessTools.Method(typeof(LogicElementSensor), nameof(LogicElementSensor.Sim200ms)), prefix: Method(nameof(SensorSample)));
			harmony.Patch(AccessTools.Method(typeof(LogicElementSensorLiquidConfig), nameof(LogicElementSensorLiquidConfig.DoPostConfigureComplete)), postfix: Method(nameof(SensorThresholdComponent)));
		}

		private static void SensorThresholdComponent(GameObject go)
		{
			go.AddOrGet<LiquidSensorThreshold>();
		}

		private static bool SensorSample(LogicElementSensor __instance)
		{
			// The game accumulates eight samples and switches when all eight say "yes". Only the sample itself changes.
			if (__instance.desiredState != Element.State.Liquid || __instance.sampleIdx >= 8)
			{
				return true;
			}
			int cell = Grid.PosToCell(__instance);
			bool main = Grid.ElementIdx[cell] == __instance.desiredElementIdx;
			LiquidSensorThreshold threshold = __instance.GetComponent<LiquidSensorThreshold>();
			bool present = LiquidMixture.Share(cell, __instance.desiredElementIdx) >= (threshold != null ? threshold.Share : LiquidMixture.PresenceShare);
			if (present != main)
			{
				Differs("sensor");
			}
			__instance.samples[__instance.sampleIdx] = present;
			__instance.sampleIdx++;
			return false;
		}

		// ---------- Bionics: wet floor from Water ----------

		private static void BionicWetFloor(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(BionicWaterDamageMonitor), "IsFloorWetWithIntolerantSubstance"), postfix: Method(nameof(BionicFloor)));
		}

		private static void BionicFloor(BionicWaterDamageMonitor.Instance smi, ref bool __result)
		{
			if (smi == null || smi.master == null || smi.master.gameObject.HasTag(GameTags.InTransitTube))
			{
				return;
			}
			int cell = Grid.PosToCell(smi);
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsLiquid || smi.kpid.HasTag(GameTags.HasAirtightSuit))
			{
				return;
			}
			bool water = HasTagged(cell, GameTags.AnyWater);
			bool main = Grid.Element[cell].HasTag(GameTags.AnyWater);
			if (water == main)
			{
				return;
			}
			// Feet and waist protection: as in the game, by the liquid depth in the cell.
			bool wet = water && (!smi.kpid.HasTag(GameTags.FeetProtection) || Grid.IsSubstantialLiquid(cell, 0.1f))
				&& (!smi.kpid.HasTag(GameTags.FeetAndWaistProtection) || Grid.IsSubstantialLiquid(cell));
			if (wet != __result)
			{
				__result = wet;
				Differs("bionic wet floor");
			}
		}

		// ---------- Algae in water ----------

		private static void AlgaeInWater(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(DissolvingAlgae), "EvaluateEmissionCondition"), prefix: Method(nameof(AlgaeCondition)));
		}

		private static bool AlgaeCondition(DissolvingAlgae __instance)
		{
			int cell = Grid.PosToCell(__instance);
			if (!Grid.IsValidCell(cell) || !Grid.IsLiquid(cell))
			{
				return true;
			}
			bool water = HasTagged(cell, GameTags.AnyWater);
			if (water == Grid.Element[cell].HasTag(GameTags.AnyWater))
			{
				return true;
			}
			bool on = water && Grid.LightIntensity[cell] > 500;
			if (__instance.enableEmitter != on)
			{
				Differs("algae in water");
				__instance.SetEnable(on);
				__instance.UpdateStatusItem();
				if (on)
				{
					__instance.SpawnVisualFX();
				}
			}
			return false;
		}

		// ---------- Molting in Ethanol ----------

		private static void Molting(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(CrabTuning), nameof(CrabTuning.IsValidDropCell)), postfix: Method(nameof(MoltCell)));
		}

		private static void MoltCell(MoltDropperMonitor.Instance smi, ref bool __result)
		{
			int cell = Grid.PosToCell(smi.transform.GetPosition());
			if (!__result || !Grid.IsValidCell(cell) || !Grid.Element[cell].IsLiquid)
			{
				return;
			}
			ushort ethanol = ElementLoader.FindElementByHash(SimHashes.Ethanol).idx;
			if (LiquidMixture.Share(cell, ethanol) >= LiquidMixture.PresenceShare)
			{
				__result = false;
				Differs("molting");
			}
		}
	
		// ---------- Pitcher Pump: liquids of mixtures ----------

		private static void PitcherPump(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(LiquidPumpingStation), nameof(LiquidPumpingStation.Sim200ms)), prefix: Method(nameof(PumpLiquids)));
		}

		/// <summary>
		/// The game's Sim200ms with one change: the amounts per liquid are summed by composition, so a liquid that is not the main
		/// one in any reachable cell can be chosen too. The rest (bottle sources, status) is as in the game.
		/// </summary>
		private static bool PumpLiquids(LiquidPumpingStation __instance)
		{
			if (__instance.session != null)
			{
				return false;
			}
			LiquidPumpingStation.LiquidInfo[] infos = __instance.infos;
			int before = __instance.infoCount;
			for (int i = 0; i < __instance.infoCount; i++)
			{
				infos[i].amount = 0f;
			}
			bool mixed = false;
			if (__instance.GetComponent<Operational>().IsOperational)
			{
				int cell = Grid.PosToCell(__instance);
				foreach (CellOffset offset in LiquidPumpingStation.liquidOffsets)
				{
					if (__instance.depthAvailable < Math.Abs(offset.y))
					{
						continue;
					}
					int target = Grid.OffsetCell(cell, offset);
					if (!Grid.IsValidCell(target) || !Grid.Element[target].IsLiquid)
					{
						continue;
					}
					int count = LiquidMixture.Liquids(target, elements, shares);
					mixed = mixed || count > 1;
					float mass = Grid.Mass[target];
					for (int k = 0; k < count; k++)
					{
						Element element = ElementLoader.elements[elements[k]];
						int found = -1;
						for (int n = 0; n < __instance.infoCount; n++)
						{
							if (infos[n].element == element)
							{
								found = n;
								break;
							}
						}
						if (found >= 0)
						{
							infos[found].amount += mass * shares[k];
						}
						else if (__instance.infoCount < infos.Length)
						{
							infos[__instance.infoCount].amount = mass * shares[k];
							infos[__instance.infoCount].element = element;
							__instance.infoCount++;
						}
					}
				}
			}
			if (mixed)
			{
				Differs("pitcher pump");
			}
			for (int index = __instance.infoCount - 1; index >= 0; index--)
			{
				LiquidPumpingStation.LiquidInfo info = infos[index];
				if (info.amount <= 1f)
				{
					if (info.source != null)
					{
						info.source.DeleteObject();
					}
					int last = __instance.infoCount - 1;
					Util.Swap(infos, in index, in last);
					__instance.infoCount--;
					continue;
				}
				if (info.source == null)
				{
					info.source = __instance.GetComponent<Storage>().AddLiquid(info.element.id, info.amount, info.element.defaultValues.temperature, byte.MaxValue, 0).GetComponent<SubstanceChunk>();
					Pickupable pickupable = info.source.GetComponent<Pickupable>();
					pickupable.KPrefabID.AddTag(GameTags.LiquidSource);
					pickupable.SetOffsets(new CellOffset[1] { new CellOffset(0, 1) });
					pickupable.targetWorkable = __instance;
					pickupable.OnReservationsChanged = (System.Action<Pickupable, bool, Pickupable.Reservation>)Delegate.Combine(pickupable.OnReservationsChanged,
						new System.Action<Pickupable, bool, Pickupable.Reservation>(__instance.OnReservationsChanged));
				}
				info.source.GetComponent<Pickupable>().TotalAmount = info.amount;
				infos[index] = info;
			}
			if (before != __instance.infoCount)
			{
				__instance.RefreshStatusItem();
			}
			return false;
		}

		// ---------- A body in water: only the Water of a mixture becomes Polluted Water ----------

		private static void BodyInWater(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(DecompositionMonitor.Instance), nameof(DecompositionMonitor.Instance.DirtyWater)), prefix: Method(nameof(PolluteWater)));
		}

		/// <summary>
		/// The game turns the body's cell into Polluted Water if it is Water, or else, if it is Polluted Water, one Water cell
		/// nearby. Here a cell counts as Water if it holds a noticeable share of it, and a mixed cell gets only its Water
		/// converted (by the plugin, before the next tick); pure cells go through the game's own replacement.
		/// </summary>
		private static bool PolluteWater(DecompositionMonitor.Instance __instance, int maxCellRange)
		{
			int cell = Grid.PosToCell(__instance.master.transform.GetPosition());
			if (!Grid.IsValidCell(cell))
			{
				return true;
			}
			ushort water = ElementLoader.FindElementByHash(SimHashes.Water).idx, polluted = ElementLoader.FindElementByHash(SimHashes.DirtyWater).idx;
			int liquids = LiquidMixture.Liquids(cell, elements, shares);
			bool mixedHere = liquids > 1;
			if (!mixedHere && !NeighboursMixed(cell, maxCellRange))
			{
				return true;
			}
			if (LiquidMixture.Share(cell, water) >= LiquidMixture.PresenceShare)
			{
				Pollute(cell, water, polluted);
				return false;
			}
			if (LiquidMixture.Share(cell, polluted) < LiquidMixture.PresenceShare)
			{
				return false;
			}
			int[] around = new int[4];
			for (int i = 0; i < maxCellRange; i++)
			{
				for (int j = 0; j < maxCellRange; j++)
				{
					around[0] = Grid.OffsetCell(cell, new CellOffset(-i, j));
					around[1] = Grid.OffsetCell(cell, new CellOffset(i, j));
					around[2] = Grid.OffsetCell(cell, new CellOffset(-i, -j));
					around[3] = Grid.OffsetCell(cell, new CellOffset(i, -j));
					around.Shuffle();
					foreach (int other in around)
					{
						if (Grid.GetCellDistance(cell, other) < maxCellRange - 1 && Grid.IsValidCell(other) && LiquidMixture.Share(other, water) >= LiquidMixture.PresenceShare)
						{
							Pollute(other, water, polluted);
							return false;
						}
					}
				}
			}
			return false;
		}

		private static bool NeighboursMixed(int cell, int range)
		{
			for (int dx = -range; dx <= range; dx++)
			{
				for (int dy = -range; dy <= range; dy++)
				{
					int other = Grid.OffsetCell(cell, new CellOffset(dx, dy));
					if (Grid.IsValidCell(other) && LiquidMixture.Liquids(other, elements, shares) > 1)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static void Pollute(int cell, ushort water, ushort polluted)
		{
			if (LiquidMixture.Liquids(cell, elements, shares) > 1)
			{
				byte[] entry = new byte[12];
				BitConverter.GetBytes(cell).CopyTo(entry, 0);
				BitConverter.GetBytes((int)water).CopyTo(entry, 4);
				BitConverter.GetBytes((int)polluted).CopyTo(entry, 8);
				SimHooks.SendData("liquid-mix.convert", entry);
				Differs("body in water");
				return;
			}
			SimMessages.ReplaceElement(cell, SimHashes.DirtyWater, CellEventLogger.Instance.DecompositionDirtyWater, Grid.Mass[cell], Grid.Temperature[cell], Grid.DiseaseIdx[cell], Grid.DiseaseCount[cell]);
		}

		// ---------- Plants: the liquids of a mixture that suit the plant ----------

		private static Func<int, object, bool> previousPlantCell;

		private static void Plants(Harmony harmony)
		{
			// The game walks the plant's cells with a ready handler from a static field. The gas mixture mod replaces it
			// too: each mod keeps the previous handler for the cells that are not its own, so the order of mods does not matter.
			previousPlantCell = PressureVulnerable.testAreaCB;
			PressureVulnerable.testAreaCB = PlantCell;
		}

		private static bool PlantCell(int cell, object data)
		{
			if (!Grid.IsValidCell(cell) || Grid.IsSolidCell(cell) || !Grid.Element[cell].IsLiquid)
			{
				return previousPlantCell(cell, data);
			}
			int count = LiquidMixture.Liquids(cell, elements, shares);
			if (count < 2)
			{
				return previousPlantCell(cell, data);
			}
			PressureVulnerable plant = (PressureVulnerable)data;
			Element main = Grid.Element[cell];
			float safeShare = 0f, bestShare = 0f;
			Element best = null;
			for (int i = 0; i < count; i++)
			{
				Element liquid = ElementLoader.elements[elements[i]];
				if (!plant.IsSafeElement(liquid))
				{
					continue;
				}
				safeShare += shares[i];
				if (shares[i] > bestShare)
				{
					bestShare = shares[i];
					best = liquid;
				}
			}
			bool safe = best != null && bestShare >= LiquidMixture.PresenceShare;
			if (safe != plant.IsSafeElement(main))
			{
				Differs("plants");
			}
			if (safe)
			{
				PressureVulnerable.testAreaPressure += Grid.Mass[cell] * safeShare;
				PressureVulnerable.testAreaCount++;
				PressureVulnerable.testAreaSafeElementCount++;
				plant.currentAtmoElement = best;
			}
			if (plant.currentAtmoElement == null)
			{
				plant.currentAtmoElement = main;
			}
			return true;
		}
	}
}
