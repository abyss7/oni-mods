using System;
using System.Collections.Generic;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniDup
{
	/// <summary>
	/// "Breathing Conditions" view in the oxygen overlay (shared mixtures interface, src/Common/MixtureUi.cs): right under the game's
	/// "Breathable", it colours the air by the zones of DupAir with the same colour scale: from the best green at a normal
	/// Oxygen pressure down through thin air, lightheaded and gasping for air to unbreathable red, and darker red for a vacuum
	/// where body fluids boil; Carbon Dioxide shows as the matching step down the scale, too much Oxygen in yellow and orange.
	/// Each cell is shown by its own air (as if a Duplicant without a mask breathed it).
	/// </summary>
	internal static class DupAirOverlay
	{
		private const string Key = "ONIDUPBREATHING";
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
			if (oxygen < DupAir.Severe || air.CarbonDioxide >= DupAir.PassingOut)
			{
				return SimDebugView.Instance.unbreathableColour;
			}
			if (oxygen >= DupAir.OxygenToxic)
			{
				return ToxicColour;
			}
			if (oxygen >= DupAir.OxygenRich)
			{
				return RichColour;
			}
			// Carbon Dioxide takes the colour of the matching step down the scale: stuffy air as thin air, a headache as
			// lightheaded, poisoning as gasping for air; the worse of the two shows.
			float level = Level(oxygen);
			float carbon = air.CarbonDioxide;
			if (carbon >= DupAir.Poisoning) level = Mathf.Min(level, Middle(DupAir.Hypoxic, DupAir.Severe));
			else if (carbon >= DupAir.Headache) level = Mathf.Min(level, Middle(DupAir.Thin, DupAir.Hypoxic));
			else if (carbon >= DupAir.Stuffy) level = Mathf.Min(level, Middle(DupAir.Normal, DupAir.Thin));
			return SimDebugView.Instance.breathableGradient.Evaluate(level);
		}

		// Above the normal pressure: the colours of the game's oxygen overlay legend for harmful air (yellow, then orange).
		private static readonly Color RichColour = new Color(0.95f, 0.85f, 0.3f, 1f), ToxicColour = new Color(0.95f, 0.5f, 0.15f, 1f);

		/// <summary>Place on the game's breathable gradient: from the lowest breathable zone (pO2 6 kPa) to normal air (15 kPa).</summary>
		private static float Level(float oxygen)
		{
			return Mathf.Clamp01((oxygen - DupAir.Severe) / (DupAir.Normal - DupAir.Severe));
		}

		private static float Middle(float high, float low)
		{
			return Level((high + low) / 2f);
		}

		private static Color Scale(float oxygen)
		{
			return SimDebugView.Instance.breathableGradient.Evaluate(Level(oxygen));
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
				new LegendEntry(ONIDUP.OVERLAY.TOXIC, ONIDUP.OVERLAY.TOXIC_TOOLTIP, ToxicColour),
				new LegendEntry(ONIDUP.OVERLAY.RICH, ONIDUP.OVERLAY.RICH_TOOLTIP, RichColour),
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
