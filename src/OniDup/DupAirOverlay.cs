using System;
using System.Collections.Generic;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniDup
{
	/// <summary>
	/// "Oxygen Pressure" view in the oxygen overlay (shared mixtures interface, src/Common/MixtureUi.cs): right under the game's
	/// "Breathable", it colours the air by the zones of DupAir with the same colour scale: from the best green at a normal
	/// Oxygen pressure down through thin air, lightheaded and gasping for air to unbreathable red, and darker red for a vacuum
	/// where body fluids boil. Each cell is shown by its own air (as if a Duplicant without a mask breathed it). High pressure
	/// is not shown yet.
	/// </summary>
	internal static class DupAirOverlay
	{
		private const string Key = "ONIDUPOXYGENPRESSURE";
		private static string current;

		internal static void Apply(Harmony harmony)
		{
			MixtureUi.Register(harmony, new Dictionary<string, object>
			{
				["id"] = "breathing",
				["order"] = -100,
				["flat"] = true,
				["title"] = (Func<string>)(() => ONIDUP.OVERLAY.NAME),
				["choices"] = (Func<List<string[]>>)(() => new List<string[]> { new[] { Key, (string)ONIDUP.OVERLAY.NAME, (string)ONIDUP.OVERLAY.TOOLTIP } }),
				["current"] = (Func<string>)(() => current),
				["select"] = (Action<string>)(key => current = key),
				["colour"] = (Func<int, Color>)Colour,
				["legend"] = (Func<List<LegendEntry>>)Legend,
			});
		}

		/// <summary>Called by the game from worker threads for each visible cell.</summary>
		private static Color Colour(int cell)
		{
			if (!Grid.IsValidCell(cell) || Grid.IsLiquid(cell) || Grid.Solid[cell])
			{
				return Color.black;
			}
			DupAir.Air air = DupAir.CellAir(cell);
			if (air.Pressure < DupAir.Vapour)
			{
				return VacuumColour();
			}
			float oxygen = DupAir.Lungs(air);
			return oxygen < DupAir.Severe ? (Color)SimDebugView.Instance.unbreathableColour : Scale(oxygen);
		}

		/// <summary>The game's breathable gradient from the lowest breathable zone (pO2 6 kPa) to normal air (15 kPa).</summary>
		private static Color Scale(float oxygen)
		{
			return SimDebugView.Instance.breathableGradient.Evaluate(Mathf.Clamp01((oxygen - DupAir.Severe) / (DupAir.Normal - DupAir.Severe)));
		}

		private static Color VacuumColour()
		{
			Color red = SimDebugView.Instance.unbreathableColour;
			return new Color(red.r * 0.55f, red.g * 0.55f, red.b * 0.55f, 1f);
		}

		private static List<LegendEntry> Legend()
		{
			// Each band shows the colour of its middle.
			return new List<LegendEntry>
			{
				new LegendEntry(ONIDUP.OVERLAY.NORMAL, ONIDUP.OVERLAY.NORMAL_TOOLTIP, Scale(DupAir.Normal)),
				new LegendEntry(ONIDUP.OVERLAY.THIN, ONIDUP.OVERLAY.THIN_TOOLTIP, Scale((DupAir.Normal + DupAir.Thin) / 2f)),
				new LegendEntry(ONIDUP.OVERLAY.LIGHTHEADED, ONIDUP.OVERLAY.LIGHTHEADED_TOOLTIP, Scale((DupAir.Thin + DupAir.Hypoxic) / 2f)),
				new LegendEntry(ONIDUP.OVERLAY.GASPING, ONIDUP.OVERLAY.GASPING_TOOLTIP, Scale((DupAir.Hypoxic + DupAir.Severe) / 2f)),
				new LegendEntry(ONIDUP.OVERLAY.UNBREATHABLE, ONIDUP.OVERLAY.UNBREATHABLE_TOOLTIP, SimDebugView.Instance.unbreathableColour),
				new LegendEntry(ONIDUP.OVERLAY.VACUUM, ONIDUP.OVERLAY.VACUUM_TOOLTIP, VacuumColour()),
			};
		}
	}
}
