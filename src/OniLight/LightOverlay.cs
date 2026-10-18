using System.Collections.Generic;
using HarmonyLib;
using OniCommon;
using STRINGS;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// The light overlay by the game's own light ranges, in colours one can tell apart, with a legend of them. The game
	/// draws one pale yellow whose opacity grows with the square root of the light up to 80 000 lux: a room at 1000 lux is
	/// drawn at a tenth of it, and every lamp-lit place looks the same dim yellow. Here each range has its colour, and within
	/// a range the colour gets lighter with the logarithm of the light:
	/// Pitch Black (0), Very Dim (1-499), Dim (500-999), Well Lit (1000-9999), Bright (10 000-49 999), Brilliant
	/// (50 000-99 999, where sunburn starts at 72 000 the game's red shimmer stays) and Blinding (100 000 and more).
	/// The legend names the ranges as the game does in its tooltips and says what each does to Duplicants (with Blackout:
	/// its penalties).
	/// </summary>
	internal static class LightOverlay
	{
		private struct Band
		{
			public int from, to;
			public Color low, high;
			public string name;
			public System.Func<string> tooltip;
		}

		private const float Alpha = 0.8f;
		private static Band[] bands;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SimDebugView), nameof(SimDebugView.GetLightColour)), prefix: new HarmonyMethod(typeof(LightOverlay), nameof(Colour)));
			harmony.Patch(AccessTools.Method(typeof(OverlayLegend), "OnSpawn"), postfix: new HarmonyMethod(typeof(LightOverlay), nameof(Legend)));
		}

		private static Band[] Bands()
		{
			if (bands != null)
			{
				return bands;
			}
			var light = TUNING.DUPLICANTSTATS.STANDARD.Light;
			bool blackout = Mod.Options.Blackout;
			bands = new[]
			{
				new Band { from = 0, to = 0, low = new Color(0f, 0f, 0f, 0f), high = new Color(0f, 0f, 0f, 0f), name = UI.OVERLAYS.LIGHTING.RANGES.NO_LIGHT,
					tooltip = () => blackout ? ONILIGHT.OVERLAY.PITCHBLACK_BLACKOUT : ONILIGHT.OVERLAY.PITCHBLACK },
				new Band { from = 1, to = light.LOW_LIGHT - 1, low = new Color(0.10f, 0.16f, 0.45f), high = new Color(0.22f, 0.42f, 0.95f), name = UI.OVERLAYS.LIGHTING.RANGES.VERY_LOW_LIGHT,
					tooltip = () => blackout ? ONILIGHT.OVERLAY.VERYDIM_BLACKOUT : ONILIGHT.OVERLAY.VERYDIM },
				new Band { from = light.LOW_LIGHT, to = light.MEDIUM_LIGHT - 1, low = new Color(0.10f, 0.62f, 0.58f), high = new Color(0.20f, 0.80f, 0.70f), name = UI.OVERLAYS.LIGHTING.RANGES.LOW_LIGHT,
					tooltip = () => blackout ? ONILIGHT.OVERLAY.DIM_BLACKOUT : ONILIGHT.OVERLAY.DIM },
				new Band { from = light.MEDIUM_LIGHT, to = light.HIGH_LIGHT - 1, low = new Color(0.35f, 0.80f, 0.25f), high = new Color(0.75f, 0.92f, 0.25f), name = UI.OVERLAYS.LIGHTING.RANGES.MEDIUM_LIGHT,
					tooltip = () => ONILIGHT.OVERLAY.WELLLIT },
				new Band { from = light.HIGH_LIGHT, to = light.VERY_HIGH_LIGHT - 1, low = new Color(0.95f, 0.85f, 0.20f), high = new Color(1f, 0.95f, 0.55f), name = UI.OVERLAYS.LIGHTING.RANGES.HIGH_LIGHT,
					tooltip = () => ONILIGHT.OVERLAY.BRIGHT },
				new Band { from = light.VERY_HIGH_LIGHT, to = light.MAX_LIGHT - 1, low = new Color(1f, 0.62f, 0.20f), high = new Color(1f, 0.45f, 0.15f), name = UI.OVERLAYS.LIGHTING.RANGES.VERY_HIGH_LIGHT,
					tooltip = () => ONILIGHT.OVERLAY.BRILLIANT },
				new Band { from = light.MAX_LIGHT, to = int.MaxValue, low = new Color(1f, 0.92f, 0.88f), high = new Color(1f, 1f, 1f), name = UI.OVERLAYS.LIGHTING.RANGES.MAX_LIGHT,
					tooltip = () => ONILIGHT.OVERLAY.BLINDING },
			};
			return bands;
		}

		/// <summary>The colour of a cell for a light: its range's, lighter with the logarithm of the light within the range.</summary>
		internal static Color For(int lux)
		{
			foreach (Band band in Bands())
			{
				if (lux > band.to)
				{
					continue;
				}
				if (band.from == band.to)
				{
					return band.low;
				}
				float top = band.to == int.MaxValue ? band.from * 4f : band.to + 1f;
				float t = Mathf.Clamp01(Mathf.Log((float)lux / band.from) / Mathf.Log(top / band.from));
				Color c = Color.Lerp(band.low, band.high, t);
				c.a = Alpha;
				return c;
			}
			return Color.white;
		}

		private static bool Colour(SimDebugView instance, int cell, ref Color __result)
		{
			int lux = Grid.LightIntensity[cell] + LightGridManager.previewLux[cell];
			Color result = For(lux);
			int sunburn = TUNING.DUPLICANTSTATS.STANDARD.Light.LUX_SUNBURN;
			if (lux > sunburn)
			{
				// The game's red shimmer where Duplicants get sunburnt.
				float strength = (lux - sunburn) / (float)(80000 - sunburn) / 10f;
				Vector2 at = Grid.CellToPos2D(cell);
				result.r += Mathf.Min(0.1f, PerlinSimplexNoise.noise(at.x / 8f, at.y / 8f + instance.currentFrame / 32f) * strength);
				result.g *= 0.85f;
			}
			__result = result;
			return false;
		}

		private static void Legend(OverlayLegend __instance)
		{
			try
			{
				var infos = (List<OverlayLegend.OverlayInfo>)AccessTools.Field(typeof(OverlayLegend), "overlayInfoList").GetValue(__instance);
				OverlayLegend.OverlayInfo info = infos?.Find(i => i.mode == OverlayModes.Light.ID);
				if (info == null || info.infoUnits == null || info.infoUnits.Count == 0)
				{
					return;
				}
				OverlayLegend.OverlayInfoUnit template = info.infoUnits[0];
				var units = new List<OverlayLegend.OverlayInfoUnit>();
				foreach (Band band in Bands())
				{
					string range = band.from == band.to ? $"{band.from} lux" : band.to == int.MaxValue ? $"{band.from:N0}+ lux" : $"{band.from:N0}-{band.to:N0} lux";
					Color swatch = band.from == band.to ? new Color(0.05f, 0.05f, 0.08f, 1f) : Color.Lerp(band.low, band.high, 0.5f);
					swatch.a = 1f;
					units.Add(new OverlayLegend.OverlayInfoUnit(template.icon, $"{band.name} ({range})", swatch, template.fontColor, null, template.sliceIcon)
					{
						tooltip = $"<b>{band.name}</b>, {range}\n{band.tooltip()}",
					});
				}
				info.infoUnits = units;
			}
			catch (System.Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} light overlay legend: {e.Message}");
			}
		}
	}
}
