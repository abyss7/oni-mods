using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniDup
{
	/// <summary>
	/// "Breathing Conditions" view in the oxygen overlay (shared mixtures interface, src/Common/MixtureUi.cs): right under the game's
	/// "Breathable", it colours the air by the zones of DupAir: Oxygen on the game's colour scale, from the best green at a
	/// normal pressure down through thin air, lightheaded and gasping for air to unbreathable red; a vacuum where body fluids
	/// boil in deep blue; too much Oxygen in yellow and orange; Carbon Dioxide on its own purple scale, from stuffy air to a
	/// blackout, wherever it harms at least as much as the Oxygen there.
	/// Each cell is shown by its own air (as if a Duplicant without a mask breathed it).
	/// The same band, named and coloured, is the note after a gas cell's mass in the cursor tooltip, instead of the game's
	/// "(Very Breathable)" and the like.
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
			// After every other postfix: the gas mixture mod writes its own note there.
			harmony.Patch(AccessTools.Method(typeof(HoverTextHelper), nameof(HoverTextHelper.MassStringsReadOnly)),
				postfix: new HarmonyMethod(typeof(DupAirOverlay), nameof(HoverNote)) { priority = Priority.Last });
			if (File.Exists(Path.Combine(ModContext.Folder, "verify_dup_overlay.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(DupAirOverlay), nameof(VerifyStep)));
			}
		}

		// ---------- Self-check: the file verify_dup_overlay.txt in the mod folder ----------
		// A while after loading, the overlays are opened over the colony and pictured: the oxygen overlay in its normal view
		// and in this one, then the mixtures overlay as it opens (the first gas selected) and with the liquids group opened.

		private static int verifyFrame = -1;
		private static float verifyStart = -1f;

		private static void VerifyStep()
		{
			if (verifyFrame > 200 || OverlayScreen.Instance == null || GameClock.Instance == null)
			{
				return;
			}
			if (verifyStart < 0f)
			{
				verifyStart = GameClock.Instance.GetTime();
				return;
			}
			if (GameClock.Instance.GetTime() < verifyStart + 20f)
			{
				return;
			}
			try
			{
				int frame = ++verifyFrame;
				// A picture is taken at the end of its frame: whatever is switched after it waits for the next frame.
				if (frame == 0)
				{
					// The "mod differences" dialog of a test run darkens the whole screen: it is closed for the pictures.
					foreach (ConfirmDialogScreen dialog in UnityEngine.Object.FindObjectsOfType<ConfirmDialogScreen>())
					{
						dialog.Deactivate();
					}
					CameraController.Instance.SnapTo(ColonyCentre(), 14f);
					OverlayScreen.Instance.ToggleOverlay(OverlayModes.Oxygen.ID, allowSound: false);
				}
				else if (frame == 30)
				{
					Picture("breathable");
				}
				else if (frame == 31)
				{
					Click(Key);
				}
				else if (frame == 60)
				{
					Picture("breathing");
				}
				else if (frame == 61)
				{
					Click(MixtureUi.BaseKey);
					OverlayScreen.Instance.ToggleOverlay(MixtureUi.MixturesId, allowSound: false);
				}
				else if (frame == 90)
				{
					Picture("mixtures");
				}
				else if (frame == 91)
				{
					Click("ONIMIXTUREGROUPLIQUID");
				}
				else if (frame == 120)
				{
					Picture("liquids");
				}
				else if (frame == 121)
				{
					OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID, allowSound: false);
					verifyFrame = 1000;
				}
			}
			catch (Exception e)
			{
				verifyFrame = 1000;
				Debug.LogWarning($"{ModContext.Tag} overlay verify stopped after an error: {e}");
			}
		}

		private static Vector3 ColonyCentre()
		{
			Vector3 sum = Vector3.zero;
			int count = 0;
			foreach (MinionIdentity dupe in Components.LiveMinionIdentities.Items)
			{
				if (dupe != null && dupe.GetMyWorldId() == ClusterManager.Instance.activeWorldId)
				{
					sum += dupe.transform.GetPosition();
					count++;
				}
			}
			return count > 0 ? sum / count : CameraController.Instance.transform.GetPosition();
		}

		private static void Picture(string name)
		{
			if (name == "breathing")
			{
				// The real colours of the game's breathable scale, for choosing the colours of this view.
				var samples = new System.Text.StringBuilder();
				foreach (float level in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
				{
					Color colour = SimDebugView.Instance.breathableGradient.Evaluate(level);
					samples.Append($" {level:F2}: {colour.r:F2} {colour.g:F2} {colour.b:F2};");
				}
				Debug.Log($"{ModContext.Tag} overlay verify: the game's breathable scale{samples}, unbreathable {SimDebugView.Instance.unbreathableColour}");
			}
			string file = Path.Combine(ModContext.Folder, $"verify_dup_overlay_{name}.png");
			ScreenCapture.CaptureScreenshot(file);
			object menu = Traverse.Create(OverlayLegend.Instance).Field("filterMenu").GetValue();
			IDictionary widgets = menu == null ? null : Traverse.Create(menu).Field("widgets").GetValue() as IDictionary;
			Debug.Log($"{ModContext.Tag} overlay verify: {name}, switches in the list {widgets?.Count ?? 0}, view {current ?? "normal"}, file {file}");
		}

		private static void Click(string key)
		{
			object menu = Traverse.Create(OverlayLegend.Instance).Field("filterMenu").GetValue();
			IDictionary widgets = menu == null ? null : Traverse.Create(menu).Field("widgets").GetValue() as IDictionary;
			if (widgets == null || !widgets.Contains(key))
			{
				throw new InvalidOperationException("no legend toggle named " + key);
			}
			GameObject widget = Traverse.Create(widgets[key]).Field("gameObject").GetValue<GameObject>();
			widget.GetComponentInChildren<MultiToggle>().onClick();
		}

		/// <summary>Breathing conditions of a cell's air, in the order of the legend.</summary>
		private enum Band { None, Toxic, Rich, Normal, Thin, Lightheaded, Gasping, Unbreathable, Vacuum, Stuffy, Headache, Poisoning, Blackout }

		/// <summary>The cell's band and its colour on the map; None for a liquid, a solid or an invalid cell. Safe on worker threads.</summary>
		private static Band Classify(int cell, out Color colour)
		{
			colour = Color.black;
			if (!Grid.IsValidCell(cell) || Grid.IsLiquid(cell) || Grid.Solid[cell])
			{
				return Band.None;
			}
			DupAir.Air air = DupAir.CellAir(cell);
			if (air.Pressure < DupAir.Vapour)
			{
				colour = VacuumColour;
				return Band.Vacuum;
			}
			float oxygen = DupAir.Lungs(air);
			if (oxygen < DupAir.Severe)
			{
				colour = SimDebugView.Instance.unbreathableColour; // the breath is held: Carbon Dioxide does not matter
				return Band.Unbreathable;
			}
			// Oxygen and Carbon Dioxide on a common scale of harm; the worse shows, Carbon Dioxide when they are equal.
			int oxygenHarm = oxygen >= DupAir.OxygenToxic ? 3 : oxygen >= DupAir.OxygenRich ? 1
				: oxygen >= DupAir.Normal ? 0 : oxygen >= DupAir.Thin ? 1 : oxygen >= DupAir.Hypoxic ? 2 : 3;
			int carbon = CarbonLevel(air.CarbonDioxide);
			if (carbon > 0 && carbon >= oxygenHarm)
			{
				colour = CarbonColours[carbon - 1];
				return Band.Stuffy + (carbon - 1);
			}
			if (oxygen >= DupAir.OxygenToxic)
			{
				colour = ToxicColour;
				return Band.Toxic;
			}
			if (oxygen >= DupAir.OxygenRich)
			{
				colour = RichColour;
				return Band.Rich;
			}
			colour = Scale(oxygen);
			return oxygen >= DupAir.Normal ? Band.Normal : oxygen >= DupAir.Thin ? Band.Thin : oxygen >= DupAir.Hypoxic ? Band.Lightheaded : Band.Gasping;
		}

		/// <summary>Called by the game from worker threads for each visible cell.</summary>
		private static Color Colour(int cell)
		{
			Classify(cell, out Color colour);
			return colour;
		}

		/// <summary>
		/// The note after a gas cell's mass under the cursor ("(Very Breathable)" in the game, by the mass of breathable gas): the
		/// cell's breathing band by this mod's scale, in its colour (the darker map colours lightened to read on the dark card).
		/// After the gas mixture mod's own note, which this replaces.
		/// </summary>
		private static void HoverNote(int cell, string[] __result)
		{
			if (__result == null || __result.Length < 4 || !Grid.IsValidCell(cell) || !(Grid.Element[cell].IsGas || Grid.Element[cell].IsVacuum))
			{
				return;
			}
			Band band = Classify(cell, out Color _);
			string name = BandName(band);
			// The colour of the band's row in the legend (for the Oxygen steps the middle of the step), not of the exact point
			// on the map's scale: the note and the legend match.
			Color colour = BandColour(band);
			if (name == null)
			{
				return;
			}
			if (band != lastBand)
			{
				lastBand = band;
				// The deep colours (vacuum, the last steps of Carbon Dioxide) are lightened more, keeping the order of the steps.
				float lighten = band == Band.Vacuum ? 0.45f : band == Band.Blackout ? 0.35f : band == Band.Poisoning ? 0.2f : 0.1f;
				Color text = Color.Lerp(colour, Color.white, lighten);
				lastNote = " " + string.Format(STRINGS.ELEMENTS.BREATHABLEDESC, text.ToHexString(), name);
			}
			__result[3] = lastNote;
		}

		/// <summary>The colour of a band's row in the legend.</summary>
		private static Color BandColour(Band band)
		{
			switch (band)
			{
				case Band.Toxic: return ToxicColour;
				case Band.Rich: return RichColour;
				case Band.Normal: return Scale(DupAir.Normal);
				case Band.Thin: return Scale((DupAir.Normal + DupAir.Thin) / 2f);
				case Band.Lightheaded: return Scale((DupAir.Thin + DupAir.Hypoxic) / 2f);
				case Band.Gasping: return Scale((DupAir.Hypoxic + DupAir.Severe) / 2f);
				case Band.Unbreathable: return SimDebugView.Instance.unbreathableColour;
				case Band.Vacuum: return VacuumColour;
				case Band.Stuffy: case Band.Headache: case Band.Poisoning: case Band.Blackout: return CarbonColours[band - Band.Stuffy];
				default: return Color.white;
			}
		}

		private static Band lastBand = Band.None;
		private static string lastNote = "";

		private static string BandName(Band band)
		{
			switch (band)
			{
				case Band.Toxic: return ONIDUP.OVERLAY.TOXIC;
				case Band.Rich: return ONIDUP.OVERLAY.RICH;
				case Band.Normal: return ONIDUP.OVERLAY.NORMAL;
				case Band.Thin: return ONIDUP.OVERLAY.THIN;
				case Band.Lightheaded: return ONIDUP.OVERLAY.LIGHTHEADED;
				case Band.Gasping: return ONIDUP.OVERLAY.GASPING;
				case Band.Unbreathable: return ONIDUP.OVERLAY.UNBREATHABLE;
				case Band.Vacuum: return ONIDUP.OVERLAY.VACUUM;
				case Band.Stuffy: return ONIDUP.OVERLAY.STUFFY;
				case Band.Headache: return ONIDUP.OVERLAY.HEADACHE;
				case Band.Poisoning: return ONIDUP.OVERLAY.POISONING;
				case Band.Blackout: return ONIDUP.OVERLAY.BLACKOUT;
				default: return null;
			}
		}

		private static int CarbonLevel(float kPa)
		{
			return kPa >= DupAir.PassingOut ? 4 : kPa >= DupAir.Poisoning ? 3 : kPa >= DupAir.Headache ? 2 : kPa >= DupAir.Stuffy ? 1 : 0;
		}

		// Above the normal pressure: the colours of the game's oxygen overlay legend for harmful air (yellow, then orange).
		private static readonly Color RichColour = new Color(0.95f, 0.85f, 0.3f, 1f), ToxicColour = new Color(0.95f, 0.5f, 0.15f, 1f);
		// A vacuum: the deep blue of space, apart from the red of air without Oxygen.
		private static readonly Color VacuumColour = new Color(0.12f, 0.18f, 0.45f, 1f);
		// Carbon Dioxide on its own scale, from light lavender (stuffy air) to deep purple (a blackout).
		private static readonly Color[] CarbonColours =
		{
			new Color(0.78f, 0.66f, 0.95f, 1f), new Color(0.66f, 0.45f, 0.92f, 1f), new Color(0.52f, 0.26f, 0.80f, 1f), new Color(0.36f, 0.10f, 0.58f, 1f),
		};

		/// <summary>Place on the game's breathable gradient: from the lowest breathable zone (pO2 6 kPa) to normal air (15 kPa).</summary>
		private static float Level(float oxygen)
		{
			return Mathf.Clamp01((oxygen - DupAir.Severe) / (DupAir.Normal - DupAir.Severe));
		}

		private static Color Scale(float oxygen)
		{
			return SimDebugView.Instance.breathableGradient.Evaluate(Level(oxygen));
		}

		private static List<LegendEntry> Legend()
		{
			// Each band of the Oxygen scale shows the colour of its middle.
			return new List<LegendEntry>
			{
				new LegendEntry(ONIDUP.OVERLAY.TOXIC, ONIDUP.OVERLAY.TOXIC_TOOLTIP, ToxicColour),
				new LegendEntry(ONIDUP.OVERLAY.RICH, ONIDUP.OVERLAY.RICH_TOOLTIP, RichColour),
				new LegendEntry(ONIDUP.OVERLAY.NORMAL, ONIDUP.OVERLAY.NORMAL_TOOLTIP, BandColour(Band.Normal)),
				new LegendEntry(ONIDUP.OVERLAY.THIN, ONIDUP.OVERLAY.THIN_TOOLTIP, BandColour(Band.Thin)),
				new LegendEntry(ONIDUP.OVERLAY.LIGHTHEADED, ONIDUP.OVERLAY.LIGHTHEADED_TOOLTIP, BandColour(Band.Lightheaded)),
				new LegendEntry(ONIDUP.OVERLAY.GASPING, ONIDUP.OVERLAY.GASPING_TOOLTIP, BandColour(Band.Gasping)),
				new LegendEntry(ONIDUP.OVERLAY.UNBREATHABLE, ONIDUP.OVERLAY.UNBREATHABLE_TOOLTIP, SimDebugView.Instance.unbreathableColour),
				new LegendEntry(ONIDUP.OVERLAY.VACUUM, ONIDUP.OVERLAY.VACUUM_TOOLTIP, VacuumColour),
				new LegendEntry(ONIDUP.OVERLAY.STUFFY, ONIDUP.OVERLAY.STUFFY_TOOLTIP, CarbonColours[0]),
				new LegendEntry(ONIDUP.OVERLAY.HEADACHE, ONIDUP.OVERLAY.HEADACHE_TOOLTIP, CarbonColours[1]),
				new LegendEntry(ONIDUP.OVERLAY.POISONING, ONIDUP.OVERLAY.POISONING_TOOLTIP, CarbonColours[2]),
				new LegendEntry(ONIDUP.OVERLAY.BLACKOUT, ONIDUP.OVERLAY.BLACKOUT_TOOLTIP, CarbonColours[3]),
			};
		}
	}
}
