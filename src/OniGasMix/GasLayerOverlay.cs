using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using OniCommon;

namespace OniGasMix
{
	/// <summary>
	/// Oxygen overlay with gas selection. A list appears in the overlay legend: the first line is the game's normal view
	/// ("breathable gas": blue where one can breathe, red where one cannot), then one line for each gas
	/// present in the mixture layer. For the selected gas a separate picture is drawn: the colour of the gas itself (from the game's data),
	/// brightness by the gas's share in the cell (by number of molecules, like the percentages in the tooltip), the scale is linear: 100% is the full
	/// colour. The scale has a lower step (the author's suggestion of 2026-10-11): any noticeable amount of gas, even
	/// fractions of a percent, is drawn no darker than that step, and only a cell without the gas is dark; above the step brightness grows
	/// evenly, so 5% and 50% are still distinguishable. "Noticeable" means from 0.05% of molecules: the same threshold from
	/// which the tooltip names the gas. Without a threshold everything would glow: over time diffusion carries traces
	/// of every gas into every cell. Nearly empty cells fade out, so that traces of gas in vacuum do not glow like
	/// a full cell. The legend shows colour samples under the list for 100, 75, 50, 25% and for traces.
	/// The colours of different gases are not mixed: one picture is about one gas.
	///
	/// The first version coloured by partial pressure (number of gas molecules) with a square-root scale; in the game it turned out
	/// that 27% and 83% Hydrogen were almost indistinguishable (the author's remark of 2026-10-11): the pressure of the living area is above
	/// what was taken as "full brightness", and everything noticeable hit the ceiling of the scale.
	///
	/// The normal view (breathable or not) is coloured by the same rule as in the game, but by all the breathable gas
	/// of the cell: duplicants breathe by the cell's composition (GasLayerBreathing.cs), and the overlay shows the same thing.
	///
	/// How it works. The selection list is the standard mechanism of the game's overlays (the temperature one has the same): an array
	/// of switches on the mode. The game asks a "cell → colour" function for the overlay cell colour every frame
	/// for visible cells, on worker threads; here that function is replaced for the oxygen overlay and reads
	/// ready arrays: the mass of the selected gas and the number of moles of all gases per cell. Before each redraw they
	/// are filled by the layer with the request "gas-mix.field" for the visible rectangle (sim/plugins/gas_mix.cpp).
	/// Gas labels are taken from the game's strings (gas names, "Breathable Gas").
	///
	/// The overlay itself is now shared with the liquid mixtures mod (src/Common/MixtureUi.cs): the game's functions are patched by the mod that
	/// registered first, and this mod hands over its "Gases" group (list of gases, cell colour, legend) and the normal
	/// "breathable or not" view by the mixture's composition. The tooltip under the cursor and the selected-cell line are there too (GasLayerHover).
	/// </summary>
	internal static class GasLayerOverlay
	{
		private const int Header = 8;                  // request header: eight ints before the mass array
		private const string KeyPrefix = "ONIGASMIXGAS"; // names of the switches in the game's string table
		private const int MaxGases = 63;
		// A cell with less gas than this share of "a kilogram of Oxygen per cell" (by number of molecules) fades out.
		private const float FadeBelow = 0.1f;
		private const float TraceShare = 0.0005f;  // a smaller share of gas is not shown in the picture
		private const float FloorBrightness = 0.15f; // brightness of the smallest shown share
		private static readonly float[] LegendLevels = { 1f, 0.5f }; // plus traces: the colour itself is smooth
		private static readonly Color Empty = new Color(0.06f, 0.07f, 0.09f, 1f); // a cell without the selected gas

		private static float[] field;         // request header; mass of the selected gas and number of moles of all gases per cell
		private static int cellCount;         // length of each of the two arrays in field
		private static int selected = -1;     // element number of the selected gas; -1: the game's normal view
		private static Color gasColour;
		private static float perMolar;        // 1 / molar mass of the selected gas
		private static float fadeScale;       // multiplier "number of moles in the cell → 1 at the fade boundary"
		private static bool failed, applied;
		private static readonly Dictionary<string, int> gasByKey = new Dictionary<string, int>();

		/// <summary>Element hash of the selected gas for the save (UiSettings); 0 if the normal view is selected.</summary>
		internal static int SelectedGasHash => applied && !failed && selected >= 0 && selected < ElementLoader.elements.Count ? (int)ElementLoader.elements[selected].id : 0;

		/// <summary>Restores the gas selection from the save. Whether the gas exists in the colony is checked when the overlay is opened.</summary>
		internal static bool RestoreSelection(int hash)
		{
			Element gas = applied && !failed ? ElementLoader.FindElementByHash((SimHashes)hash) : null;
			if (gas == null || !gas.IsGas)
			{
				return false;
			}
			Select(gas.idx);
			return true;
		}

		// Self-check: the file verify_gas_overlay.txt in the mod folder. When the layer has run VerifyAfterTicks ticks,
		// the oxygen overlay turns on by itself, and the normal view and up to three gases are selected in turn (by clicks on the same
		// legend switches the player presses); for each view a screenshot is written to the mod folder,
		// verify_gas_overlay_<view>.png. The camera stays where it is.
		private const int VerifyAfterTicks = 500, VerifyFramesPerView = 40;
		private static readonly SimHashes[] VerifyFirst = { SimHashes.CarbonDioxide, SimHashes.Hydrogen, SimHashes.Oxygen };
		private static bool verify;
		private static int verifyFrame;
		private static readonly List<string> verifyViews = new List<string>();

		internal static void Apply(Harmony harmony)
		{
			applied = true;
			verify = File.Exists(Path.Combine(ModContext.Folder, "verify_gas_overlay.txt"));
			if (verify)
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"),
					postfix: new HarmonyMethod(typeof(GasLayerOverlay), nameof(VerifyStep)));
			}
			MixtureUi.Register(harmony, new Dictionary<string, object>
			{
				["id"] = "gas",
				["order"] = 0,
				["title"] = (Func<string>)(() => ONIGASMIX.GASMIXTURE.GROUP_TITLE),
				["overlayName"] = (Func<string>)(() => ONIGASMIX.GASMIXTURE.OVERLAY_NAME),
				["overlayButton"] = (Func<string>)(() => ONIGASMIX.GASMIXTURE.OVERLAY_BUTTON),
				["overlayTooltip"] = (Func<string>)(() => ONIGASMIX.GASMIXTURE.OVERLAY_TOOLTIP),
				["hover"] = (Func<int, List<string>>)GasLayerHover.HoverLinesFor,
				["panel"] = (Func<int, string>)GasLayerHover.PanelText,
				["choices"] = (Func<List<string[]>>)Choices,
				["textColour"] = "9FDCEB", // gases: light blue
				["current"] = (Func<string>)(() => selected >= 0 && !failed && selected < ElementLoader.elements.Count ? KeyOf(ElementLoader.elements[selected]) : null),
				["select"] = (Action<string>)(key => Select(key != null && gasByKey.TryGetValue(key, out int index) ? index : -1)),
				["refresh"] = (System.Action)(() => Refresh(wait: false)),
				["colour"] = (Func<int, Color>)Colour,
				["legend"] = (Func<List<LegendEntry>>)GasLegend,
				["base"] = (Func<SimDebugView, int, Color>)GasLayerBreathing.OverlayColour,
				["baseActive"] = (Func<bool>)(() => GasLayerBreathing.Active),
			});
		}

		private static string KeyOf(Element gas)
		{
			return KeyPrefix + gas.id.ToString().ToUpperInvariant();
		}

		// ---------- Picture ----------

		/// <summary>Called by the game from worker threads for each visible cell; while the redraw is in progress, the array does not change.</summary>
		private static Color Colour(int cell)
		{
			float[] masses = field;
			if (selected < 0 || masses == null)
			{
				return Empty;
			}
			if (Grid.Solid[cell] || Grid.IsLiquid(cell))
			{
				return Color.black;
			}
			float total = masses[Header + cellCount + cell];
			float amount = masses[Header + cell] * perMolar;
			if (!(amount > 0f) || !(total > 0f))
			{
				return Empty;
			}
			float share = Mathf.Min(amount / total, 1f);
			if (share < TraceShare)
			{
				return Empty;
			}
			return Shade(Brightness(share) * Mathf.Min(total * fadeScale, 1f));
		}

		/// <summary>Brightness by the gas's share: from the lower step for traces up to one for pure gas.</summary>
		private static float Brightness(float share)
		{
			return FloorBrightness + (1f - FloorBrightness) * share;
		}

		private static Color Shade(float brightness)
		{
			return new Color(
				Empty.r + (gasColour.r - Empty.r) * brightness,
				Empty.g + (gasColour.g - Empty.g) * brightness,
				Empty.b + (gasColour.b - Empty.b) * brightness,
				1f);
		}

		/// <summary>Asks the layer for the mass of the selected gas in the visible rectangle.</summary>
		private static unsafe void Refresh(bool wait)
		{
			try
			{
				if (field == null || cellCount != Grid.CellCount)
				{
					cellCount = Grid.CellCount;
					field = new float[Header + 2 * cellCount];
				}
				// A margin of one cell around the screen: the overlay texture is smoothed between neighbouring cells.
				Grid.GetVisibleExtents(out int minX, out int minY, out int maxX, out int maxY);
				fixed (float* start = field)
				{
					int* header = (int*)start;
					header[0] = selected;
					header[1] = wait ? 1 : 0;
					header[2] = minX - 1;
					header[3] = minY - 1;
					header[4] = maxX + 1;
					header[5] = maxY + 1;
					header[6] = 1; // together with the number of moles of all gases
				}
				int result = SimHooks.Query("gas-mix.field", field);
				if (result != 0 && result != 1)
				{
					// The layer does not move gas or the request did not fit: nothing to show.
					Array.Clear(field, Header, field.Length - Header);
				}
			}
			catch (Exception e)
			{
				failed = true;
				selected = -1;
				Debug.LogWarning($"[OniGasMix] GasLayerOverlay is switched off after an error: {e}");
			}
		}

		// ---------- Selection list in the legend ----------

		/// <summary>Gases currently present in the layer: {key, label, tooltip} for the common overlay list.</summary>
		private static List<string[]> Choices()
		{
			var choices = new List<string[]>();
			if (failed)
			{
				return choices;
			}
			byte[] present = new byte[4 * (1 + MaxGases)];
			gasByKey.Clear();
			if (SimHooks.Query("gas-mix.present", present) != 0)
			{
				return choices;
			}
			int count = Math.Min(BitConverter.ToInt32(present, 0), MaxGases);
			// The amount goes with each choice only when the list is to be ordered by it (the mod option).
			Dictionary<int, double> amounts = Mod.Options.SortByAmount ? WorldAmounts() : null;
			for (int i = 0; i < count; i++)
			{
				int index = BitConverter.ToInt32(present, 4 * (1 + i));
				if (index < 0 || index >= ElementLoader.elements.Count || !ElementLoader.elements[index].IsGas)
				{
					continue;
				}
				Element element = ElementLoader.elements[index];
				string key = KeyOf(element);
				gasByKey[key] = index;
				string name = STRINGS.UI.StripLinkFormatting(element.name);
				if (amounts != null)
				{
					amounts.TryGetValue(index, out double amount);
					choices.Add(new[] { key, name, element.description, amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture) });
				}
				else
				{
					choices.Add(new[] { key, name, element.description });
				}
			}
			return choices;
		}

		private static Dictionary<int, double> amountsCache;
		private static int amountsWorld = -1;
		private static float amountsTime;

		/// <summary>
		/// How much of each gas the active world holds, kg by element number (by the mixture's composition): the list is ordered
		/// by it. The world is walked at most once in a few seconds: the list is rebuilt several times when the overlay opens.
		/// </summary>
		private static Dictionary<int, double> WorldAmounts()
		{
			WorldContainer world = ClusterManager.Instance?.activeWorld;
			if (world == null)
			{
				return new Dictionary<int, double>();
			}
			if (amountsCache != null && amountsWorld == world.id && Time.unscaledTime - amountsTime < 5f)
			{
				return amountsCache;
			}
			var amounts = new Dictionary<int, double>();
			amountsCache = amounts;
			amountsWorld = world.id;
			amountsTime = Time.unscaledTime;
			for (int y = world.WorldOffset.y; y < world.WorldOffset.y + world.WorldSize.y; y++)
			{
				for (int x = world.WorldOffset.x; x < world.WorldOffset.x + world.WorldSize.x; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsGas)
					{
						continue;
					}
					GasMixture.Gases gases = GasMixture.Of(cell);
					while (gases.Next(out Element gas, out float mass))
					{
						amounts.TryGetValue(gas.idx, out double sum);
						amounts[gas.idx] = sum + mass;
					}
				}
			}
			return amounts;
		}

		private static void Select(int element)
		{
			selected = element;
			if (element < 0)
			{
				return;
			}
			Element gas = ElementLoader.elements[element];
			gasColour = MixtureUi.Bright(gas);
			float molar = MolarMasses.Of(gas);
			perMolar = molar > 0f ? 1f / molar : 1f;
			// A cell fades out when it has fewer molecules than FadeBelow kilograms of Oxygen.
			fadeScale = MolarMasses.Of(ElementLoader.FindElementByHash(SimHashes.Oxygen)) / FadeBelow;
			if (field != null)
			{
				Array.Clear(field, Header, field.Length - Header);
			}
			Refresh(wait: true);
		}

		// ---------- Legend ----------

		/// <summary>Scale samples: the colour in which a cell with such a share of the selected gas is drawn.</summary>
		private static List<LegendEntry> GasLegend()
		{
			var entries = new List<LegendEntry>();
			if (selected < 0 || failed)
			{
				return entries;
			}
			string name = STRINGS.UI.StripLinkFormatting(ElementLoader.elements[selected].name);
			foreach (float level in LegendLevels)
			{
				string percent = Mathf.RoundToInt(level * 100f) + (string)STRINGS.UI.UNITSUFFIXES.PERCENT;
				entries.Add(new LegendEntry(level == 1f ? percent + " " + name : percent, null, Shade(Brightness(level))));
			}
			entries.Add(new LegendEntry("<1" + (string)STRINGS.UI.UNITSUFFIXES.PERCENT, null, Shade(Brightness(0f))));
			return entries;
		}

		// ---------- Self-check ----------

		private static void VerifyStep()
		{
			if (!verify || failed || OverlayScreen.Instance == null || SimHooks.GetValue("gas-mix.ticks") < VerifyAfterTicks)
			{
				return;
			}
			try
			{
				int frame = verifyFrame++;
				if (frame == 0)
				{
					// The gases are in the mixtures overlay (the normal breathable view stays in the oxygen one).
					OverlayScreen.Instance.ToggleOverlay(MixtureUi.MixturesId, allowSound: false);
					// Views in order: gases from the preference list, then any others; up to three gases in total.
					verifyViews.Clear();
					foreach (SimHashes wanted in VerifyFirst)
					{
						string key = KeyPrefix + wanted.ToString().ToUpperInvariant();
						if (gasByKey.ContainsKey(key))
						{
							verifyViews.Add(key);
						}
					}
					foreach (string key in gasByKey.Keys)
					{
						if (verifyViews.Count < 4 && !verifyViews.Contains(key))
						{
							verifyViews.Add(key);
						}
					}
					return;
				}
				int view = frame / VerifyFramesPerView, phase = frame % VerifyFramesPerView;
				if (view >= verifyViews.Count)
				{
					OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID, allowSound: false);
					verify = false;
					return;
				}
				if (phase == 1 || phase == 3)
				{
					// The second click is for a closed group: the first one opens it.
					Click(verifyViews[view]);
				}
				else if (phase == VerifyFramesPerView - 5)
				{
					string name = verifyViews[view].Substring(KeyPrefix.Length).ToLowerInvariant();
					string file = Path.Combine(ModContext.Folder, $"verify_gas_overlay_{name}.png");
					ScreenCapture.CaptureScreenshot(file);
					Debug.Log($"[OniGasMix] GasLayerOverlay verify: view {name}, selected element {selected}, overlay {OverlayScreen.Instance.GetMode()}, choices {gasByKey.Count + 1}, file {file}");
				}
			}
			catch (Exception e)
			{
				verify = false;
				Debug.LogWarning($"[OniGasMix] GasLayerOverlay verify stopped after an error: {e}");
				// The overlay was turned on by the check: the check also turns it off.
				OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID, allowSound: false);
			}
		}

		/// <summary>Presses a legend switch the same way a player's click does.</summary>
		private static void Click(string key)
		{
			object menu = Traverse.Create(OverlayLegend.Instance).Field("filterMenu").GetValue();
			IDictionary widgets = menu == null ? null : Traverse.Create(menu).Field("widgets").GetValue() as IDictionary;
			if (widgets != null && !widgets.Contains(key) && widgets.Contains("ONIMIXTUREGROUPGAS"))
			{
				// The group's list is closed: its title opens it, and the menu is rebuilt on the next frame; the caller clicks again.
				key = "ONIMIXTUREGROUPGAS";
			}
			else if (widgets == null || !widgets.Contains(key))
			{
				throw new InvalidOperationException("no legend toggle named " + key);
			}
			GameObject widget = Traverse.Create(widgets[key]).Field("gameObject").GetValue<GameObject>();
			widget.GetComponentInChildren<MultiToggle>().onClick();
		}
	}
}
