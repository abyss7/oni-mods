using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using OniCommon;

namespace OniLiquidMix
{
	/// <summary>
	/// Order of liquids by density in the game's C# code. A falling drop (FallingWater.UpdateParticles) passes through
	/// a liquid if its molar mass is greater than that liquid's, and settles on top if it is smaller. Here, instead of
	/// molar mass, the density from the mod's table is compared (the real density of the substance or the mass of a full cell),
	/// as in the layer order in the simulation (sim/plugins/liquid_mix.cpp, DoDensityDisplacement): a drop of Petroleum settles on
	/// Water instead of sinking in it.
	/// </summary>
	internal static class LiquidDensity
	{
		internal static string Mode { get; private set; } = "off";

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(FallingWater), nameof(FallingWater.UpdateParticles)), transpiler: new HarmonyMethod(typeof(LiquidDensity), nameof(UseDensity)));
		}

		/// <summary>Liquid density for comparison; for a substance not in the table, the molar mass, as in the game.</summary>
		public static float Of(Element element)
		{
			float[] densities = LiquidTable.Densities;
			return densities != null && element.idx < densities.Length && densities[element.idx] > 0f ? densities[element.idx] : element.molarMass;
		}

		private static IEnumerable<CodeInstruction> UseDensity(IEnumerable<CodeInstruction> instructions)
		{
			var molarMass = AccessTools.Field(typeof(Element), nameof(Element.molarMass));
			var of = AccessTools.Method(typeof(LiquidDensity), nameof(Of));
			int replaced = 0;
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.LoadsField(molarMass))
				{
					replaced++;
					yield return new CodeInstruction(OpCodes.Call, of).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
					continue;
				}
				yield return instruction;
			}
			// In this game build the molar mass is read there exactly twice: for the drop and for the liquid below it.
			Mode = replaced == 2 ? "on" : $"unexpected: {replaced} reads of molarMass replaced";
			if (replaced != 2)
			{
				Debug.LogWarning($"{ModContext.Tag} falling drops by density: expected 2 reads of molarMass in FallingWater.UpdateParticles, replaced {replaced}");
			}
		}
	}
}
