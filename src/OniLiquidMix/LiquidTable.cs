using System;
using System.Collections.Generic;
using System.IO;

namespace OniLiquidMix
{
	/// <summary>
	/// Liquid table for the plugin (sim/plugins/liquid_rules.h: TableHeader, Liquid records, one state byte
	/// per game element, the strength of each germ type). The liquid movement numbers (how much a cell gives away per
	/// tick, the smallest portions, the mass of a full cell) are the same the game passes to its simulation.
	///
	/// A liquid has a mixing group: liquids of one group lie in a cell as a mixture (the cell stores the mass
	/// of each), the rest mix with nothing and lie in layers. The mod does not look into what the liquids are made of:
	/// a group is just a list of game liquids.
	///
	/// Density is needed for the layer order (instead of the molar mass the game orders them by) and for the
	/// density of a mixture. It is the mass of a full cell from the game data (a cell is a cubic metre), except for liquids
	/// where that is far from the real density of the substance: for those the real one is written.
	/// </summary>
	internal static class LiquidTable
	{
		private const ushort NoGroup = 0xFFFF;
		// The main liquid of a cell is kept as long as it is not smaller than the largest one by more than this share.
		private const float Keep = 0.2f;

		private static readonly (string name, string[] liquids)[] Groups =
		{
			// Ethanol mixes with Water in any proportion (the author's decision 2026-10-15); Nectar, Brackene and Ovolene are aqueous
			// solutions too, Phyto Oil and Biodiesel mix with petroleum products (the author's decisions 2026-10-16).
			("water", new[] { "Water", "DirtyWater", "SaltWater", "Brine", "MurkyBrine", "Ethanol", "SugarWater", "Milk", "FishMilk" }),
			("oil", new[] { "CrudeOil", "Petroleum", "Naphtha", "PhytoOil", "RefinedLipid" }),
		};

		/// <summary>Real densities of liquids (kg/m3) where the mass of a full cell in the game is far from them.</summary>
		private static readonly Dictionary<string, float> RealDensity = new Dictionary<string, float>
		{
			{ "Chlorine", 1560f }, { "LiquidCarbonDioxide", 1100f }, { "LiquidHelium", 125f }, { "LiquidHydrogen", 71f },
			{ "LiquidMethane", 423f }, { "LiquidOxygen", 1141f }, { "LiquidPhosphorus", 1740f }, { "LiquidPropane", 580f },
			{ "LiquidSulfur", 1800f }, { "Mercury", 13534f }, { "Ethanol", 789f }, { "Milk", 1030f }, { "SugarWater", 1390f },
			{ "Magma", 2650f }, { "MoltenGlass", 2400f }, { "MoltenSalt", 1556f },
			{ "MoltenAluminum", 2375f }, { "MoltenCopper", 8020f }, { "MoltenGold", 17310f }, { "MoltenIron", 6980f },
			{ "MoltenCobalt", 7750f }, { "MoltenLead", 10660f }, { "MoltenNiobium", 7830f }, { "MoltenSteel", 7000f },
			{ "MoltenTungsten", 17600f }, { "MoltenUranium", 17300f }, { "MoltenNickel", 7810f }, { "MoltenIridium", 19000f },
			{ "MoltenZinc", 6570f },
			// Vegetable oil and biodiesel; Ovolene like milk.
			{ "PhytoOil", 920f }, { "RefinedLipid", 880f }, { "FishMilk", 1030f },
			// The made-up gel and mucus: their full-cell mass is 100 kg, and they would float above oil; density as for
			// real gels and mucus, slightly above water (the author's decision 2026-10-16).
			{ "ViscoGel", 1100f }, { "Mucus", 1050f },
		};

		/// <summary>Liquid density by element index (0 for the rest); for ordering by density in the C# code.</summary>
		internal static float[] Densities { get; private set; }

		/// <summary>What went into the table, for the log: the groups and the number of other liquids.</summary>
		internal static string Summary { get; private set; } = "";

		private const ushort None = 0xFFFF;

		private static ushort Index(Element element)
		{
			return element != null ? element.idx : None;
		}

		private static ushort Index(SimHashes id)
		{
			return id == SimHashes.Vacuum ? None : Index(ElementLoader.FindElementByHash(id));
		}

		/// <summary>
		/// Dissolved share of aqueous liquids for the water density anomaly (sim/plugins/liquid_mix.cpp): it shifts the
		/// temperature at which the liquid is densest. 0 for Water, for salty ones by the salt share, 1 for alcohol.
		/// </summary>
		private static readonly (string id, float solute)[] Dissolved =
		{
			("Water", 0f), ("DirtyWater", 0.01f), ("SaltWater", 0.07f), ("Brine", 0.3f), ("MurkyBrine", 0.3f), ("Ethanol", 1f),
			("SugarWater", 0.5f), ("Milk", 0.13f), ("FishMilk", 0.13f),
		};

		internal static byte[] WaterAnomaly()
		{
			using (MemoryStream stream = new MemoryStream())
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				foreach ((string id, float solute) in Dissolved)
				{
					if (Enum.TryParse(id, out SimHashes hash) && ElementLoader.FindElementByHash(hash) is Element element)
					{
						writer.Write((int)element.idx);
						writer.Write(solute);
					}
				}
				return stream.ToArray();
			}
		}

		/// <summary>
		/// Liquids whose upward transition is not boiling into their own vapour but a conversion (into a liquid, like Brackene into Brine, or into a
		/// gas far from its condensation point, like Phyto Oil into Carbon Dioxide): element indices for the plugin. The plugin always
		/// handles such transitions of a mixture, not only without the pressure mod. The rule is the same as in the phase rules
		/// (sim/plugins/element_table.h: the boiling point and the gas condensation point differ by no more than 10%).
		/// </summary>
		/// <summary>Specific heat capacity of every liquid, for the heat capacity of mixtures in the plugin.</summary>
		internal static byte[] HeatCapacities()
		{
			using (MemoryStream stream = new MemoryStream())
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				foreach (Element element in ElementLoader.elements)
				{
					if (element.IsLiquid && element.specificHeatCapacity > 0f)
					{
						writer.Write((int)element.idx);
						writer.Write(element.specificHeatCapacity);
					}
				}
				return stream.ToArray();
			}
		}

		internal static byte[] Decomposing()
		{
			using (MemoryStream stream = new MemoryStream())
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				foreach (Element element in ElementLoader.elements)
				{
					Element product = element.highTempTransition;
					if (!element.IsLiquid || product == null)
					{
						continue;
					}
					bool vapour = product.IsGas && Math.Abs(element.highTemp - product.lowTemp) <= 0.1f * product.lowTemp;
					if (!vapour)
					{
						writer.Write((int)element.idx);
					}
				}
				return stream.ToArray();
			}
		}

		internal static byte[] Build()
		{
			var groupOf = new Dictionary<SimHashes, ushort>();
			var text = new List<string>();
			for (int index = 0; index < Groups.Length; index++)
			{
				var members = new List<string>();
				foreach (string id in Groups[index].liquids)
				{
					// Liquids from DLCs that are absent in this game are skipped.
					if (Enum.TryParse(id, out SimHashes hash) && ElementLoader.FindElementByHash(hash) is Element element && element.IsLiquid && !groupOf.ContainsKey(hash))
					{
						groupOf[hash] = (ushort)index;
						members.Add(id);
					}
				}
				text.Add($"{Groups[index].name}: {string.Join(", ", members)}");
			}
			int single = 0;
			using (MemoryStream stream = new MemoryStream())
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				var liquids = ElementLoader.elements.FindAll(element => element.IsLiquid);
				var densities = new float[ElementLoader.elements.Count];
				var diseases = Db.Get().Diseases;
				writer.Write(ElementLoader.elements.Count);
				writer.Write(liquids.Count);
				writer.Write(Keep);
				writer.Write(diseases.Count);
				foreach (Element element in liquids)
				{
					bool grouped = groupOf.TryGetValue(element.id, out ushort group);
					single += grouped ? 0 : 1;
					float density = RealDensity.TryGetValue(element.id.ToString(), out float real) ? real : element.maxMass;
					writer.Write(element.idx);
					writer.Write(grouped ? group : NoGroup);
					writer.Write(density > 0f ? density : 1000f);
					densities[element.idx] = density > 0f ? density : 1000f;
					writer.Write(element.molarMass);
					writer.Write(element.viscosity);
					writer.Write(element.minHorizontalFlow);
					writer.Write(element.minVerticalFlow);
					writer.Write(element.maxMass);
					writer.Write(element.lowTemp);
					writer.Write(element.highTemp);
					writer.Write(Index(element.lowTempTransition));
					writer.Write(Index(element.lowTempTransitionOreID));
					writer.Write(Index(element.highTempTransition));
					writer.Write(Index(element.highTempTransitionOreID));
					writer.Write(element.lowTempTransitionOreMassConversion);
					writer.Write(element.highTempTransitionOreMassConversion);
				}
				// State of each element (0 vacuum, 1 gas, 2 liquid, 3 solid), padded to four bytes.
				foreach (Element element in ElementLoader.elements)
				{
					writer.Write((byte)(element.state & Element.State.Solid));
				}
				for (int pad = ElementLoader.elements.Count; pad % 4 != 0; pad++)
				{
					writer.Write((byte)0);
				}
				for (int i = 0; i < diseases.Count; i++)
				{
					writer.Write(diseases[i].strength);
				}
				Summary = $"{string.Join("; ", text)}; {single} more liquids mix with nothing";
				Densities = densities;
				return stream.ToArray();
			}
		}
	}
}
