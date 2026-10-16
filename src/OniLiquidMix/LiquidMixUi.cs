using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniLiquidMix
{
	/// <summary>
	/// Liquid mixtures in the UI, as the "Liquids" group of the shared mixture UI (src/Common/MixtureUi.cs; it also draws
	/// the "Gases" group of the gas mixture mod, if that is enabled).
	///
	/// Tooltip under the cursor, in the cell substance box: composition of the mixture by mass (shares by mass, because liquid, unlike
	/// gas, is measured by mass), without the masses of the individual liquids:
	///   – Mixture
	///        61% Water
	///        39% Brine
	/// Selected-cell panel line: the same with masses. The cell mass in the game is the mass of the whole mixture, the cell substance its main
	/// liquid; the masses of the liquids are their shares of the cell mass.
	///
	/// Overlay: in the shared list, a group of liquids from the mixing groups currently present on the map. For the selected
	/// liquid a cell is painted in its colour (from the game data, raised to full brightness) with brightness by its share in the cell:
	/// a pure cell of this liquid is the brightest, a mixture dimmer, a cell of another liquid dark. Cells without liquid are dark,
	/// solid ones black. The plugin hands over the shares for the cells of the visible rectangle via the query "liquid-mix.field".
	/// </summary>
	internal static class LiquidMixUi
	{
		private const string KeyPrefix = "ONILIQUIDMIX";
		private const int HoverLines = 6, PanelLines = 12, MaxLiquids = LiquidMixture.Slots;
		private const float MinShare = 0.005f;       // a share below 0.5% by mass goes into the "Other" line
		private const float FloorBrightness = 0.15f; // brightness of the smallest shown share
		private const int Header = 8;                // header of the field query: eight ints
		private static readonly float[] LegendLevels = { 1f, 0.75f, 0.5f, 0.25f };
		private static readonly Color Empty = new Color(0.06f, 0.07f, 0.09f, 1f);

		private static int selected = -1;
		private static Color liquidColour;
		private static float[] field;
		private static int cellCount;
		private static bool failed;
		private static readonly Dictionary<string, int> liquidByKey = new Dictionary<string, int>();

		// Self-check: the file verify_liquid_overlay.txt in the mod folder. When the plugin has run VerifyAfterTicks ticks, the log
		// gets the tooltip and the selected-cell panel line for the first cell found with a mixture, then the Mixtures Overlay switches on by itself,
		// Water is selected (by a click on the same legend toggle the player presses), and the screenshot
		// verify_liquid_overlay_water.png is written to the mod folder; then the overlay returns to its normal view and closes. Later than the same
		// check for gases (500 ticks), so they do not get in each other's way, but before the end of a test run (about 800 ticks).
		private const int VerifyAfterTicks = 600, VerifyFrames = 80;
		private static bool verify;
		private static int verifyFrame;

		internal static void Apply(Harmony harmony)
		{
			verify = File.Exists(Path.Combine(ModContext.Folder, "verify_liquid_overlay.txt"));
			if (verify)
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(LiquidMixUi), nameof(VerifyStep)));
			}
			MixtureUi.Register(harmony, new Dictionary<string, object>
			{
				["id"] = "liquid",
				["order"] = 1,
				["title"] = (Func<string>)(() => ONILIQUIDMIX.MIXTURES.GROUP_TITLE),
				["overlayName"] = (Func<string>)(() => ONILIQUIDMIX.MIXTURES.OVERLAY_NAME),
				["overlayButton"] = (Func<string>)(() => ONILIQUIDMIX.MIXTURES.OVERLAY_BUTTON),
				["overlayTooltip"] = (Func<string>)(() => ONILIQUIDMIX.MIXTURES.OVERLAY_TOOLTIP),
				["hover"] = (Func<int, List<string>>)HoverLinesFor,
				["panel"] = (Func<int, string>)PanelText,
				["choices"] = (Func<List<string[]>>)Choices,
				["current"] = (Func<string>)(() => selected >= 0 && !failed && selected < ElementLoader.elements.Count ? KeyOf(ElementLoader.elements[selected]) : null),
				["select"] = (Action<string>)(key => Select(key != null && liquidByKey.TryGetValue(key, out int index) ? index : -1)),
				["refresh"] = (System.Action)(() => Refresh(wait: false)),
				["colour"] = (Func<int, Color>)Colour,
				["legend"] = (Func<List<LegendEntry>>)Legend,
			});
		}

		private static string KeyOf(Element liquid)
		{
			return KeyPrefix + liquid.id.ToString().ToUpperInvariant();
		}

		private static string Name(Element element)
		{
			return STRINGS.UI.StripLinkFormatting(element.name);
		}

		// ---------- Cell composition ----------

		private struct Part
		{
			public Element element;
			public float share;
		}

		/// <summary>Composition of one cell with memory of the previous query (the tooltip and the panel have their own instances).</summary>
		private sealed class Mixture
		{
			public readonly List<Part> parts = new List<Part>(); // by descending share
			public int version;
			private readonly byte[] buffer = new byte[LiquidMixture.CellQueryBytes];
			private int cachedCell = -1;
			private long cachedTick = -1;

			public void Read(int cell)
			{
				if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsLiquid)
				{
					if (parts.Count > 0 || cachedCell != -1)
					{
						parts.Clear();
						cachedCell = -1;
						version++;
					}
					return;
				}
				// The composition changes once per simulation tick.
				long tick = SimHooks.GetValue("liquid-mix.ticks");
				if (cell == cachedCell && tick == cachedTick)
				{
					return;
				}
				Array.Clear(buffer, 0, buffer.Length);
				BitConverter.GetBytes(cell).CopyTo(buffer, 0);
				int result = SimHooks.Query("liquid-mix.cell", buffer);
				if (result == 1)
				{
					// A tick is running: the previous composition stays until next time.
					return;
				}
				cachedCell = cell;
				cachedTick = tick;
				version++;
				parts.Clear();
				int count = result == 0 ? Math.Min(BitConverter.ToInt32(buffer, 4), MaxLiquids) : 0;
				for (int i = 0; i < count; i++)
				{
					int index = BitConverter.ToInt32(buffer, LiquidMixture.QueryElementAt + 4 * i);
					float share = BitConverter.ToSingle(buffer, LiquidMixture.QueryShareAt + 4 * i);
					if (index >= 0 && index < ElementLoader.elements.Count && share > 0f)
					{
						parts.Add(new Part { element = ElementLoader.elements[index], share = share });
					}
				}
				parts.Sort((a, b) => b.share.CompareTo(a.share));
			}

			public int Noticeable(int limit)
			{
				int count = 0;
				while (count < parts.Count && count < limit && parts[count].share >= MinShare)
				{
					count++;
				}
				return count;
			}
		}

		private static readonly Mixture hover = new Mixture(), panel = new Mixture();
		private static readonly List<string> hoverLines = new List<string>();
		private static int hoverVersion = -1, panelVersion = -1;
		private static string panelText = "";

		/// <summary>Tooltip lines about the cell composition; empty if the cell holds no mixture. The list is shared and must not be modified.</summary>
		private static List<string> HoverLinesFor(int cell)
		{
			hover.Read(cell);
			if (hover.version == hoverVersion)
			{
				return hoverLines;
			}
			hoverVersion = hover.version;
			hoverLines.Clear();
			int shown = hover.Noticeable(HoverLines);
			if (shown < 2)
			{
				return hoverLines;
			}
			hoverLines.Add(STRINGS.MISC.TAGS.MIXTURE);
			float rest = 0f;
			for (int i = 0; i < hover.parts.Count; i++)
			{
				if (i < shown)
				{
					hoverLines.Add(MixtureUi.Share(hover.parts[i].share) + " " + Name(hover.parts[i].element));
				}
				else
				{
					rest += hover.parts[i].share;
				}
			}
			if (rest > 0f)
			{
				hoverLines.Add(MixtureUi.Share(rest) + " " + (string)STRINGS.MISC.TAGS.OTHER);
			}
			return hoverLines;
		}

		/// <summary>Text of the selected-cell panel line; empty if the cell holds no mixture.</summary>
		private static string PanelText(int cell)
		{
			panel.Read(cell);
			if (panel.version == panelVersion)
			{
				return panelText;
			}
			panelVersion = panel.version;
			int shown = panel.Noticeable(PanelLines);
			if (shown < 2)
			{
				return panelText = "";
			}
			float mass = Grid.Mass[cell];
			var text = new StringBuilder(STRINGS.MISC.TAGS.MIXTURE);
			float rest = 0f;
			for (int i = 0; i < panel.parts.Count; i++)
			{
				Part part = panel.parts[i];
				if (i < shown)
				{
					text.Append('\n').Append(MixtureUi.Share(part.share)).Append(' ').Append(Name(part.element)).Append(": ").Append(GameUtil.GetFormattedMass(part.share * mass));
				}
				else
				{
					rest += part.share;
				}
			}
			if (rest > 0f)
			{
				text.Append('\n').Append(MixtureUi.Share(rest)).Append(' ').Append((string)STRINGS.MISC.TAGS.OTHER).Append(": ").Append(GameUtil.GetFormattedMass(rest * mass));
			}
			return panelText = text.ToString();
		}

		// ---------- Overlay ----------

		/// <summary>Liquids of the mixing groups currently present on the map.</summary>
		private static List<string[]> Choices()
		{
			var choices = new List<string[]>();
			liquidByKey.Clear();
			if (failed)
			{
				return choices;
			}
			byte[] buffer = new byte[8 + 64 * 16];
			BitConverter.GetBytes(64).CopyTo(buffer, 0);
			if (SimHooks.Query("liquid-mix.group-totals", buffer) != 0)
			{
				return choices;
			}
			int count = BitConverter.ToInt32(buffer, 0);
			for (int i = 0; i < count; i++)
			{
				int index = BitConverter.ToInt32(buffer, 8 + i * 16);
				if (index < 0 || index >= ElementLoader.elements.Count || !ElementLoader.elements[index].IsLiquid)
				{
					continue;
				}
				Element element = ElementLoader.elements[index];
				string key = KeyOf(element);
				liquidByKey[key] = index;
				choices.Add(new[] { key, Name(element), element.description });
			}
			return choices;
		}

		private static void Select(int element)
		{
			selected = element;
			if (element < 0)
			{
				return;
			}
			liquidColour = MixtureUi.Bright(ElementLoader.elements[element]);
			if (field != null)
			{
				Array.Clear(field, Header, field.Length - Header);
			}
			Refresh(wait: true);
		}

		/// <summary>Asks the plugin for the share of the selected liquid over the visible rectangle.</summary>
		private static unsafe void Refresh(bool wait)
		{
			try
			{
				if (selected < 0)
				{
					return;
				}
				if (field == null || cellCount != Grid.CellCount)
				{
					cellCount = Grid.CellCount;
					field = new float[Header + cellCount];
				}
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
				}
				// The field is offset by the header: the plugin writes the shares right after it, by game cell index.
				int result = SimHooks.Query("liquid-mix.field", field);
				if (result == 2)
				{
					Array.Clear(field, Header, field.Length - Header);
				}
			}
			catch (Exception e)
			{
				failed = true;
				selected = -1;
				Debug.LogWarning($"{ModContext.Tag} liquid overlay is switched off after an error: {e}");
			}
		}

		/// <summary>Called from the game's worker threads for every visible cell.</summary>
		private static Color Colour(int cell)
		{
			float[] shares = field;
			if (selected < 0 || shares == null || cell < 0 || Header + cell >= shares.Length)
			{
				return Empty;
			}
			if (Grid.Solid[cell])
			{
				return Color.black;
			}
			float share = shares[Header + cell];
			if (!(share > 0f))
			{
				return Empty;
			}
			return Shade(FloorBrightness + (1f - FloorBrightness) * Mathf.Min(share, 1f));
		}

		private static Color Shade(float brightness)
		{
			return new Color(
				Empty.r + (liquidColour.r - Empty.r) * brightness,
				Empty.g + (liquidColour.g - Empty.g) * brightness,
				Empty.b + (liquidColour.b - Empty.b) * brightness,
				1f);
		}

		private static List<LegendEntry> Legend()
		{
			var entries = new List<LegendEntry>();
			if (selected < 0 || failed)
			{
				return entries;
			}
			string name = Name(ElementLoader.elements[selected]);
			foreach (float level in LegendLevels)
			{
				string percent = Mathf.RoundToInt(level * 100f) + (string)STRINGS.UI.UNITSUFFIXES.PERCENT;
				entries.Add(new LegendEntry(level == 1f ? percent + " " + name : percent, null, Shade(FloorBrightness + (1f - FloorBrightness) * level)));
			}
			entries.Add(new LegendEntry("<1" + (string)STRINGS.UI.UNITSUFFIXES.PERCENT, null, Shade(FloorBrightness)));
			return entries;
		}


		// ---------- Self-check ----------

		private static void VerifyStep()
		{
			if (!verify || OverlayScreen.Instance == null || SimHooks.GetValue("liquid-mix.ticks") < VerifyAfterTicks)
			{
				return;
			}
			try
			{
				int frame = verifyFrame++;
				if (frame == 0)
				{
					Debug.Log($"{ModContext.Tag} liquid overlay verify: {Sample()}");
					OverlayScreen.Instance.ToggleOverlay(OverlayModes.Oxygen.ID, allowSound: false);
				}
				else if (frame == 10)
				{
					Click(KeyOf(ElementLoader.FindElementByHash(SimHashes.Water)));
				}
				else if (frame == VerifyFrames / 2)
				{
					string file = Path.Combine(ModContext.Folder, "verify_liquid_overlay_water.png");
					ScreenCapture.CaptureScreenshot(file);
					Debug.Log($"{ModContext.Tag} liquid overlay verify: water selected (element {selected}), overlay {OverlayScreen.Instance.GetMode()}, liquids to choose {liquidByKey.Count}, file {file}");
				}
				else if (frame >= VerifyFrames)
				{
					Click(MixtureUi.BaseKey);
					OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID, allowSound: false);
					verify = false;
				}
			}
			catch (Exception e)
			{
				verify = false;
				Debug.LogWarning($"{ModContext.Tag} liquid overlay verify stopped after an error: {e}");
				OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID, allowSound: false);
			}
		}

		/// <summary>Presses a legend toggle the same way a player's click does.</summary>
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

		/// <summary>For the log: the tooltip and the selected-cell panel line for the first cell found whose mixture holds at least two liquids.</summary>
		private static string Sample()
		{
			for (int cell = 0; cell < Grid.CellCount; cell++)
			{
				if (!Grid.Element[cell].IsLiquid)
				{
					continue;
				}
				List<string> text = HoverLinesFor(cell);
				if (text.Count >= 3)
				{
					Grid.CellToXY(cell, out int x, out int y);
					return $"cell {cell} ({x}, {y}), shown by the game as {Grid.Element[cell].id} {Grid.Mass[cell]:F1} kg; hover: {string.Join(" | ", text)}; panel: {PanelText(cell).Replace("\n", " | ")}";
				}
			}
			return "no cell with a mixture of liquids found";
		}
	}
}
