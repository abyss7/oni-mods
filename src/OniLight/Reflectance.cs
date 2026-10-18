using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OniCommon;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// Reflectance of visible light by element: how much of the light falling on a surface or a back wall of the element
	/// comes back, 0..1. Values are those of the real material or the nearest real one (rough and slightly tarnished for
	/// metals: the model reflects diffusely), to about 0.1.
	///
	/// Other mods give values for their own elements (or change ours) in either of two ways, and those win over this table:
	/// - a file light_reflectance.yaml in the mod's folder, a line per element: "ElementId: 0.35";
	/// - from code, without referencing this mod: the dictionary in AppDomain data "OniMods.LightReflectance.1"
	///   (Dictionary&lt;string, float&gt; by element id; create it if it is not there yet, add to it otherwise), filled
	///   before a game is loaded.
	/// An element nobody gives a value for takes one by its material category.
	/// </summary>
	internal static class Reflectance
	{
		internal const string SharedKey = "OniMods.LightReflectance.1";
		internal const string FileName = "light_reflectance.yaml";

		private static readonly Dictionary<string, float> Table = new Dictionary<string, float>
		{
			// Very dark
			{ "Unobtanium", 0f }, { "Fullerene", 0.03f }, { "Carbon", 0.04f }, { "Bitumen", 0.04f }, { "RefinedCarbon", 0.05f }, { "Peat", 0.05f },
			{ "Corium", 0.05f }, { "Rubber", 0.05f }, { "SolidCrudeOil", 0.05f }, { "FrozenInk", 0.05f }, { "CarbonFibre", 0.05f },
			{ "Graphite", 0.06f }, { "UraniumOre", 0.06f }, { "Wolframite", 0.07f }, { "Obsidian", 0.07f }, { "Katairite", 0.08f },
			{ "ToxicMud", 0.08f }, { "Basalt", 0.08f }, { "Glass", 0.08f },
			// Dark
			{ "IronOre", 0.10f }, { "Cuprite", 0.10f }, { "MaficRock", 0.10f }, { "Mud", 0.10f }, { "Algae", 0.10f }, { "SlimeMold", 0.10f },
			{ "Gunk", 0.10f }, { "Regolith", 0.12f }, { "Shale", 0.12f }, { "IgneousRock", 0.15f }, { "Dirt", 0.15f }, { "ToxicSand", 0.15f },
			{ "Rust", 0.15f }, { "Fertilizer", 0.15f }, { "NickelOre", 0.15f }, { "ZincOre", 0.15f }, { "SolidPetroleum", 0.15f },
			{ "Cobaltite", 0.20f }, { "Cinnabar", 0.20f }, { "DepletedUranium", 0.20f }, { "SolidNaphtha", 0.20f }, { "SolidNuclearWaste", 0.20f },
			{ "EnrichedUranium", 0.20f }, { "SiltStone", 0.20f },
			// Middle
			{ "Granite", 0.25f }, { "SedimentaryRock", 0.25f }, { "Phosphorite", 0.25f }, { "Galena", 0.25f }, { "OxyRock", 0.25f },
			{ "AluminumOre", 0.30f }, { "FoolsGold", 0.30f }, { "Brick", 0.30f }, { "CrushedRock", 0.30f }, { "DirtyIce", 0.30f },
			{ "SolidResin", 0.30f }, { "NaturalSolidResin", 0.30f }, { "Amber", 0.30f }, { "Isoresin", 0.30f }, { "PhosphateNodules", 0.30f },
			{ "Iron", 0.35f }, { "Lead", 0.35f }, { "Cement", 0.35f }, { "CementMix", 0.35f }, { "Slabs", 0.35f }, { "SandCement", 0.35f },
			{ "Clay", 0.35f }, { "PalmWood", 0.35f }, { "SolidMucus", 0.35f }, { "MurkyBrineIce", 0.35f },
			{ "SandStone", 0.40f }, { "Sand", 0.40f }, { "Electrum", 0.40f }, { "Steel", 0.40f }, { "Tungsten", 0.40f }, { "Niobium", 0.40f },
			{ "Ice", 0.40f }, { "TempConductorSolid", 0.40f }, { "FrozenPhytoOil", 0.40f }, { "WoodLog", 0.40f }, { "FabricatedWood", 0.40f },
			// Light
			{ "Copper", 0.45f }, { "Cobalt", 0.45f }, { "Nickel", 0.45f }, { "Zinc", 0.45f }, { "GoldAmalgam", 0.45f }, { "Fossil", 0.45f },
			{ "Diamond", 0.45f }, { "Yellowcake", 0.45f }, { "Corallium", 0.50f }, { "Coquina", 0.50f }, { "BrineIce", 0.50f },
			{ "SolidChlorine", 0.50f }, { "Phosphorus", 0.50f }, { "SuperInsulator", 0.50f }, { "SolidSuperCoolant", 0.50f },
			{ "SolidViscoGel", 0.50f }, { "Radium", 0.50f }, { "Iridium", 0.55f }, { "Aluminum", 0.60f }, { "Gold", 0.60f },
			{ "SolidMercury", 0.60f }, { "BleachStone", 0.60f }, { "Sulfur", 0.60f }, { "SolidMethane", 0.60f }, { "SolidPropane", 0.60f },
			{ "SolidHydrogen", 0.60f }, { "SolidOxygen", 0.60f }, { "SolidEthanol", 0.60f }, { "SolidSyngas", 0.60f }, { "CrushedIce", 0.60f },
			// White
			{ "Polypropylene", 0.70f }, { "Ceramic", 0.70f }, { "Sucrose", 0.70f }, { "MilkFat", 0.70f }, { "Tallow", 0.70f }, { "Pearl", 0.70f },
			{ "FishMilkIce", 0.70f }, { "Salt", 0.75f }, { "Lime", 0.75f }, { "HardPolypropylene", 0.75f }, { "SolidCarbonDioxide", 0.80f },
			{ "MilkIce", 0.80f }, { "StableSnow", 0.80f }, { "Aerogel", 0.80f }, { "Snow", 0.85f },
		};

		// By material category, for elements nobody gives a value for.
		private static readonly Dictionary<string, float> ByCategory = new Dictionary<string, float>
		{
			{ "Metal", 0.15f }, { "RefinedMetal", 0.45f }, { "BuildableRaw", 0.25f }, { "BuildableProcessed", 0.35f }, { "ConsumableOre", 0.30f },
			{ "Farmable", 0.20f }, { "Filter", 0.35f }, { "Liquifiable", 0.50f }, { "Organics", 0.25f }, { "Agriculture", 0.20f },
			{ "ManufacturedMaterial", 0.45f }, { "RareMaterials", 0.30f }, { "Sublimating", 0.50f }, { "Other", 0.30f }, { "Special", 0.10f },
		};

		private const float Default = 0.30f;

		// Gases that dim visible light, per kg of real mass (a cell lets through exp(-absorb x mass)). The others are clear: the
		// molecules of air, hydrogen, carbon dioxide, methane, steam and the rest barely absorb visible light, and scattering
		// by molecules shows only over kilometres. Chlorine is yellow-green: a cubic metre at one atmosphere (2.95 kg at 293 K)
		// takes about a fifth of the light. Fallout (dust) and carbon vapour (soot) are dark: a kilogram in a cell takes about
		// 70%.
		private static readonly Dictionary<string, float> GasAbsorb = new Dictionary<string, float>
		{
			{ "ChlorineGas", 0.2231f / 2.95f }, { "Fallout", 1.2f }, { "CarbonGas", 1.2f },
		};

		private const float ReferenceMolar = 31.998f; // the equivalent mass of the gas mixture mod by pressure: of Oxygen

		private static Dictionary<string, float> fromMods;
		internal static int FromFiles { get; private set; }
		internal static int FromCode { get; private set; }
		internal static int ByCategoryCount { get; private set; }

		/// <summary>The shared dictionary for other mods' code: made at load so that mods loaded later find it.</summary>
		internal static void Publish()
		{
			if (!(AppDomain.CurrentDomain.GetData(SharedKey) is Dictionary<string, float>))
			{
				AppDomain.CurrentDomain.SetData(SharedKey, new Dictionary<string, float>());
			}
		}

		/// <summary>Values from other mods: their files (read once) and the shared dictionary (read at each game load).</summary>
		private static Dictionary<string, float> FromMods()
		{
			var values = new Dictionary<string, float>();
			if (fromMods == null)
			{
				fromMods = new Dictionary<string, float>();
				FromFiles = 0;
				try
				{
					foreach (KMod.Mod mod in Global.Instance.modManager.mods)
					{
						if (!mod.IsEnabledForActiveDlc() || string.IsNullOrEmpty(mod.ContentPath))
						{
							continue;
						}
						string file = Path.Combine(mod.ContentPath, FileName);
						if (File.Exists(file))
						{
							FromFiles += ReadFile(file, fromMods);
						}
					}
				}
				catch (Exception e)
				{
					Debug.LogWarning($"{ModContext.Tag} reading {FileName} of other mods: {e.Message}");
				}
			}
			foreach (var pair in fromMods)
			{
				values[pair.Key] = pair.Value;
			}
			FromCode = 0;
			if (AppDomain.CurrentDomain.GetData(SharedKey) is Dictionary<string, float> shared)
			{
				foreach (var pair in shared)
				{
					values[pair.Key] = Mathf.Clamp01(pair.Value);
					FromCode++;
				}
			}
			return values;
		}

		private static int ReadFile(string file, Dictionary<string, float> into)
		{
			int count = 0;
			foreach (string raw in File.ReadAllLines(file))
			{
				string line = raw;
				int comment = line.IndexOf('#');
				if (comment >= 0)
				{
					line = line.Substring(0, comment);
				}
				int colon = line.IndexOf(':');
				if (colon <= 0)
				{
					continue;
				}
				string id = line.Substring(0, colon).Trim();
				if (float.TryParse(line.Substring(colon + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
				{
					into[id] = Mathf.Clamp01(value);
					count++;
				}
			}
			return count;
		}

		/// <summary>The engine's table by element index: reflectance, absorption of liquids (from the game's own factor), state.</summary>
		internal static LightNative.ElementOptics[] Optics()
		{
			Dictionary<string, float> mods = FromMods();
			List<Element> elements = ElementLoader.elements;
			// With the gas mixture mod evening out pressure, the game sees a gas cell's mass as the equivalent mass of Oxygen.
			bool equivalent = AppDomain.CurrentDomain.GetData("OniMods.GasComposition.1") != null && MolarMasses.ByMolecules;
			var table = new LightNative.ElementOptics[elements.Count];
			ByCategoryCount = 0;
			for (int i = 0; i < elements.Count; i++)
			{
				Element e = elements[i];
				string id = e.id.ToString();
				if (!mods.TryGetValue(id, out float reflectance) && !Table.TryGetValue(id, out reflectance))
				{
					reflectance = e.IsSolid && ByCategory.TryGetValue(e.materialCategory.Name ?? "", out float byCategory) ? byCategory : Default;
					if (e.IsSolid)
					{
						ByCategoryCount++;
					}
				}
				// The share a full cell lets through is the game's own (1 - lightAbsorptionFactor, as for sunlight); for any
				// mass, exp(-absorb * mass).
				float absorb = 0f, molarRatio = 0f;
				if (e.IsLiquid && e.defaultValues.mass > 0f)
				{
					absorb = -Mathf.Log(1f - Mathf.Min(e.lightAbsorptionFactor, 0.99f)) / e.defaultValues.mass;
				}
				else if (e.IsGas && GasAbsorb.TryGetValue(id, out float gasAbsorb))
				{
					absorb = gasAbsorb;
					molarRatio = equivalent ? MolarMasses.Of(e) / ReferenceMolar : 0f;
				}
				table[i] = new LightNative.ElementOptics
				{
					reflectance = reflectance,
					absorb = absorb,
					state = (byte)(e.state & Element.State.Solid),
					molarRatio = molarRatio,
				};
			}
			return table;
		}
	}
}
