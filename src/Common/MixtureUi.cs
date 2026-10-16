using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace OniCommon
{
	/// <summary>
	/// Common mixtures interface for the gas mixture and liquid mixture mods: the tooltip under the cursor, the selected-cell line and
	/// the mixtures overlay (formerly the oxygen overlay). Each mod describes its own group (a "provider") and registers it here; the game's
	/// functions are patched only by the mod that registered first, and the others just add their group. This way the mods do not
	/// compete for the same game functions and work together or alone: the overlay and the tooltip show gases, liquids or
	/// both, each group in its own block.
	///
	/// The common code is compiled into each mod separately, and each has its own copies of these types. So the common list of providers
	/// lives in the application domain data under one key and consists only of types shared by everyone: a "name → value" dictionary
	/// with delegates from the standard library, Unity and game types. A provider (Dictionary&lt;string, object&gt;):
	///   "id"       string                          the group name, also the prefix of its switch names in the legend;
	///   "order"    int                             group order (lower is higher);
	///   "title"    Func&lt;string&gt;                   group title in the overlay legend (strings are taken at display time:
	///                                              the game hooks in mod translations after the mods are loaded);
	///   "hover"    Func&lt;int, List&lt;string&gt;&gt;          tooltip lines for a cell (the first: the title); empty: nothing to show;
	///   "panel"    Func&lt;int, string&gt;                selected-cell line text; empty: nothing to show;
	///   "choices"  Func&lt;List&lt;string[]&gt;&gt;             what can be chosen in the overlay: {key, label, tooltip};
	///   "current"  Func&lt;string&gt;                     the selected key of this group or null;
	///   "select"   Action&lt;string&gt;                   selection of a key of this group (null: the normal view or another group is selected);
	///   "refresh"  System.Action                         before the overlay is redrawn, while a key of this group is selected;
	///   "colour"   Func&lt;int, Color&gt;                 cell colour while a key of this group is selected (called from worker threads);
	///   "legend"   Func&lt;List&lt;LegendEntry&gt;&gt;          legend while a key of this group is selected;
	///   "base"     Func&lt;SimDebugView, int, Color&gt;   optional: colour of the normal view (breathable or not), while "baseActive";
	///   "baseActive" Func&lt;bool&gt;;
	///   "overlayName", "overlayButton", "overlayTooltip" Func&lt;string&gt;: overlay names (the same in meaning for all providers,
	///              taken from the first one).
	/// </summary>
	internal static class MixtureUi
	{
		private const string RegistryKey = "OniMods.MixtureUi.1";
		internal const string BaseKey = "ONIMIXTUREBASE"; // normal overlay view: breathable or not
		private const int IndentWidth = 20;

		private static List<Dictionary<string, object>> providers;
		private static Func<SimDebugView, int, Color> original;
		private static Dictionary<string, object> active; // provider of the selected key; null: normal view
		private static bool overlayFailed, hoverFailed, panelFailed;
		private static readonly Dictionary<string, StatusItem> panelItems = new Dictionary<string, StatusItem>();

		/// <summary>Registers a group. The first mod to register installs the patches on the game's functions.</summary>
		internal static void Register(Harmony harmony, Dictionary<string, object> provider)
		{
			var registry = AppDomain.CurrentDomain.GetData(RegistryKey) as List<Dictionary<string, object>>;
			if (registry == null)
			{
				registry = new List<Dictionary<string, object>>();
				AppDomain.CurrentDomain.SetData(RegistryKey, registry);
				providers = registry;
				Install(harmony);
				Debug.Log($"{ModContext.Tag} mixture interface: installed by this mod");
			}
			registry.Add(provider);
			registry.Sort((a, b) => ((int)a["order"]).CompareTo((int)b["order"]));
		}

		private static T Get<T>(Dictionary<string, object> provider, string name) where T : class
		{
			return provider.TryGetValue(name, out object value) ? value as T : null;
		}

		/// <summary>A provider string: a function (called now, already in the game's language) or a ready string.</summary>
		private static string Text(Dictionary<string, object> provider, string name)
		{
			if (!provider.TryGetValue(name, out object value))
			{
				return null;
			}
			return value is Func<string> text ? text() : value as string;
		}

		private static void Install(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SelectToolHoverTextCard), nameof(SelectToolHoverTextCard.UpdateHoverElements)),
				transpiler: new HarmonyMethod(typeof(MixtureUi), nameof(AddHoverLines)));
			harmony.Patch(AccessTools.Method(typeof(CellSelectionObject), "UpdateStatusItem"),
				postfix: new HarmonyMethod(typeof(MixtureUi), nameof(UpdatePanel)));
			harmony.Patch(AccessTools.Method(typeof(SimDebugView), "OnPrefabInit"),
				postfix: new HarmonyMethod(typeof(MixtureUi), nameof(ReplaceColourFunction)));
			harmony.Patch(AccessTools.Method(typeof(SimDebugView), nameof(SimDebugView.UpdateData)),
				prefix: new HarmonyMethod(typeof(MixtureUi), nameof(BeforeRedraw)));
			harmony.Patch(AccessTools.Method(typeof(OverlayModes.Oxygen), nameof(OverlayModes.Oxygen.Enable)),
				prefix: new HarmonyMethod(typeof(MixtureUi), nameof(BuildChoice)));
			// Before the game's method: it immediately rebuilds the legend, and the selection in it must already be the new one.
			harmony.Patch(AccessTools.Method(typeof(OverlayLegend), "OnFiltersChanged"),
				prefix: new HarmonyMethod(typeof(MixtureUi), nameof(ChoiceChanged)));
			harmony.Patch(AccessTools.Method(typeof(OverlayLegend), "PopulateOverlayInfoUnits"),
				prefix: new HarmonyMethod(typeof(MixtureUi), nameof(HideBreathingLegend)));
			harmony.Patch(AccessTools.Method(typeof(OverlayModes.Mode), nameof(OverlayModes.Mode.GetCustomLegendData)),
				postfix: new HarmonyMethod(typeof(MixtureUi), nameof(Legend)));
			harmony.Patch(AccessTools.Method(typeof(OverlayMenu), "InitializeToggles"),
				prefix: new HarmonyMethod(typeof(MixtureUi), nameof(RenameButton)));
		}

		// ---------- Tooltip under the cursor ----------

		/// <summary>
		/// Inserts a call to <see cref="Draw"/> into the cell's substance box: after the mass and temperature lines, before the
		/// "exposed to space" line. The place is found by the only call to IsExposedToSpace in the method.
		/// </summary>
		private static IEnumerable<CodeInstruction> AddHoverLines(IEnumerable<CodeInstruction> instructions)
		{
			var code = new List<CodeInstruction>(instructions);
			MethodInfo anchor = AccessTools.Method(typeof(CellSelectionObject), nameof(CellSelectionObject.IsExposedToSpace));
			int at = code.FindIndex(instruction => instruction.Calls(anchor));
			if (at < 1 || !code[at - 1].IsLdloc())
			{
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the place for mixture lines was not found in the hover card; the card is left as is");
				return code;
			}
			CodeInstruction loadCell = code[at - 1];
			var first = new CodeInstruction(OpCodes.Ldarg_0);
			// Branches may lead to the load of the cell number: now they must arrive at the insertion.
			first.labels.AddRange(loadCell.labels);
			loadCell.labels.Clear();
			code.InsertRange(at - 1, new[]
			{
				first,
				new CodeInstruction(loadCell.opcode, loadCell.operand),
				new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MixtureUi), nameof(Draw))),
			});
			return code;
		}

		public static void Draw(SelectToolHoverTextCard card, int cell)
		{
			if (hoverFailed || providers == null)
			{
				return;
			}
			try
			{
				foreach (Dictionary<string, object> provider in providers)
				{
					List<string> text = Get<Func<int, List<string>>>(provider, "hover")?.Invoke(cell);
					if (text == null || text.Count == 0)
					{
						continue;
					}
					HoverTextDrawer drawer = HoverTextScreen.Instance.drawer;
					TextStyleSetting style = card.Styles_BodyText.Standard;
					drawer.NewLine();
					drawer.DrawIcon(card.iconDash);
					drawer.DrawText(text[0], style);
					for (int i = 1; i < text.Count; i++)
					{
						drawer.NewLine(20);
						drawer.AddIndent(IndentWidth);
						drawer.DrawText(text[i], style);
					}
				}
			}
			catch (Exception e)
			{
				// The tooltip is drawn every frame: one log entry is enough.
				hoverFailed = true;
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the hover card part is switched off after an error: {e}");
			}
		}

		// ---------- Selected-cell line ----------

		/// <summary>
		/// The game places the status lines of the selected cell on selection and twice a second, and removes them when
		/// the selection is cleared. Here each group's line is added or removed the same way.
		/// </summary>
		private static void UpdatePanel(CellSelectionObject __instance)
		{
			if (panelFailed || providers == null)
			{
				return;
			}
			try
			{
				bool selected = SelectTool.Instance != null && SelectTool.Instance.selected == __instance.Selectable;
				foreach (Dictionary<string, object> provider in providers)
				{
					string id = (string)provider["id"];
					Func<int, string> panel = Get<Func<int, string>>(provider, "panel");
					if (panel == null)
					{
						continue;
					}
					if (!panelItems.TryGetValue(id, out StatusItem item))
					{
						// The provider builds the whole line text; it has no tooltip.
						item = new StatusItem("OniMixture" + id, "{Mixture}", "", "", StatusItem.IconType.Info, NotificationType.Neutral, false, OverlayModes.None.ID);
						item.resolveStringCallback = (text, data) => panel(((CellSelectionObject)data).SelectedCell);
						panelItems[id] = item;
					}
					__instance.Selectable.ToggleStatusItem(item, selected && panel(__instance.SelectedCell).Length > 0, __instance);
				}
			}
			catch (Exception e)
			{
				panelFailed = true;
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the selected cell panel part is switched off after an error: {e}");
			}
		}

		// ---------- Overlay ----------

		/// <summary>
		/// With gas and liquid selection the overlay is no longer only about oxygen: the button and the tooltip in the overlay list are taken from
		/// the providers (from the first one that has them).
		/// </summary>
		private static void RenameButton()
		{
			foreach (Dictionary<string, object> provider in providers ?? new List<Dictionary<string, object>>())
			{
				if (Text(provider, "overlayButton") is string button)
				{
					STRINGS.UI.OVERLAYS.OXYGEN.BUTTON = button;
					STRINGS.UI.TOOLTIPS.OXYGENOVERLAYSTRING = Text(provider, "overlayTooltip") ?? button;
					return;
				}
			}
		}

		private static void ReplaceColourFunction(SimDebugView __instance)
		{
			Dictionary<HashedString, Func<SimDebugView, int, Color>> functions =
				Traverse.Create(__instance).Field<Dictionary<HashedString, Func<SimDebugView, int, Color>>>("getColourFuncs").Value;
			if (functions == null || !functions.TryGetValue(OverlayModes.Oxygen.ID, out Func<SimDebugView, int, Color> current))
			{
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the oxygen overlay colour function was not found; the overlay is left as is");
				overlayFailed = true;
				return;
			}
			original = current;
			functions[OverlayModes.Oxygen.ID] = Colour;
		}

		/// <summary>Called by the game from worker threads for each visible cell.</summary>
		private static Color Colour(SimDebugView instance, int cell)
		{
			Dictionary<string, object> chosen = active;
			if (chosen != null && Get<Func<int, Color>>(chosen, "colour") is Func<int, Color> colour)
			{
				return colour(cell);
			}
			foreach (Dictionary<string, object> provider in providers)
			{
				if (Get<Func<bool>>(provider, "baseActive")?.Invoke() == true && Get<Func<SimDebugView, int, Color>>(provider, "base") is Func<SimDebugView, int, Color> baseColour)
				{
					return baseColour(instance, cell);
				}
			}
			return original(instance, cell);
		}

		private static void BeforeRedraw(HashedString viewMode)
		{
			if (!overlayFailed && active != null && viewMode == OverlayModes.Oxygen.ID)
			{
				Get<System.Action>(active, "refresh")?.Invoke();
			}
		}

		private static string FilterKey(string name, string part)
		{
			return "STRINGS.UI.TOOLS.FILTERLAYERS." + name + "." + part;
		}

		/// <summary>The menu switch's label and tooltip are looked up in the game's string table by its name.</summary>
		private static void AddString(string name, string text, string tooltip)
		{
			Strings.Add(FilterKey(name, "NAME"), text);
			Strings.Add(FilterKey(name, "TOOLTIP"), tooltip ?? text);
		}

		/// <summary>
		/// Before the overlay is turned on, builds the selection list: the normal view, then the providers' groups. Each group has its own
		/// title: a switch that cannot be pressed. With no selection in any group the overlay stays as in the game.
		/// </summary>
		private static void BuildChoice(OverlayModes.Oxygen __instance)
		{
			if (overlayFailed || providers == null)
			{
				return;
			}
			try
			{
				// The mode may be turned on again while it is already on; the game then does not rebuild the legend.
				if (__instance.legendFilters != null && OverlayLegend.Instance != null)
				{
					Traverse legend = Traverse.Create(OverlayLegend.Instance);
					if (ReferenceEquals(legend.Field("currentMode").GetValue(), __instance) && legend.Field("filterMenu").GetValue() as UnityEngine.Object != null)
					{
						return;
					}
				}
				var toggles = new List<ToolParameterMenu.ToggleData>();
				AddString(BaseKey, STRINGS.MISC.TAGS.BREATHABLE, STRINGS.UI.OVERLAYS.OXYGEN.TOOLTIPS.LEGEND2);
				Dictionary<string, object> stillActive = null;
				foreach (Dictionary<string, object> provider in providers)
				{
					List<string[]> choices = Get<Func<List<string[]>>>(provider, "choices")?.Invoke();
					if (choices == null || choices.Count == 0)
					{
						continue;
					}
					string id = (string)provider["id"];
					string current = Get<Func<string>>(provider, "current")?.Invoke();
					string heading = "ONIMIXTUREGROUP" + id.ToUpperInvariant();
					// The legend list does not show bold font (checked with a screenshot): the title is marked with capitals and dashes.
					string title = Text(provider, "title") ?? id;
					AddString(heading, "— " + title.ToUpper() + " —", title);
					toggles.Add(new ToolParameterMenu.ToggleData(heading, ToolParameterMenu.ToggleState.Disabled));
					choices.Sort((a, b) => string.Compare(a[1], b[1], StringComparison.CurrentCulture));
					bool found = false;
					foreach (string[] choice in choices)
					{
						AddString(choice[0], choice[1], choice[2]);
						bool on = choice[0] == current;
						found = found || on;
						toggles.Add(new ToolParameterMenu.ToggleData(choice[0], on ? ToolParameterMenu.ToggleState.On : ToolParameterMenu.ToggleState.Off));
					}
					if (found && stillActive == null)
					{
						stillActive = provider;
					}
					else if (current != null)
					{
						// The group's selection is gone (or something is selected in another group): the group returns to the normal view.
						Get<Action<string>>(provider, "select")?.Invoke(null);
					}
				}
				active = stillActive;
				toggles.Insert(0, new ToolParameterMenu.ToggleData(BaseKey, active == null ? ToolParameterMenu.ToggleState.On : ToolParameterMenu.ToggleState.Off));
				__instance.legendFilters = toggles.Count > 1 ? toggles.ToArray() : null;
				// The legend builds a selection list only for overlays marked as "filled from code" (like the temperature one).
				List<OverlayLegend.OverlayInfo> infos = OverlayLegend.Instance == null ? null
					: Traverse.Create(OverlayLegend.Instance).Field<List<OverlayLegend.OverlayInfo>>("overlayInfoList").Value;
				OverlayLegend.OverlayInfo info = infos?.Find(candidate => candidate.mode == OverlayModes.Oxygen.ID);
				if (info != null)
				{
					info.isProgrammaticallyPopulated = __instance.legendFilters != null;
					foreach (Dictionary<string, object> provider in providers)
					{
						if (Text(provider, "overlayName") is string name)
						{
							info.name = name;
							break;
						}
					}
				}
			}
			catch (Exception e)
			{
				overlayFailed = true;
				active = null;
				__instance.legendFilters = null;
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the overlay part is switched off after an error: {e}");
			}
		}

		private static void ChoiceChanged(OverlayModes.Mode ___currentMode)
		{
			if (overlayFailed || providers == null || !(___currentMode is OverlayModes.Oxygen) || ___currentMode.legendFilters == null)
			{
				return;
			}
			string chosen = null;
			foreach (ToolParameterMenu.ToggleData toggle in ___currentMode.legendFilters)
			{
				if (toggle.IsOn && toggle.name != BaseKey)
				{
					chosen = toggle.name;
				}
			}
			Dictionary<string, object> owner = null;
			foreach (Dictionary<string, object> provider in providers)
			{
				List<string[]> choices = Get<Func<List<string[]>>>(provider, "choices")?.Invoke();
				bool mine = chosen != null && choices != null && choices.Exists(choice => choice[0] == chosen);
				if (mine)
				{
					owner = provider;
				}
				string current = Get<Func<string>>(provider, "current")?.Invoke();
				if (current != (mine ? chosen : null))
				{
					Get<Action<string>>(provider, "select")?.Invoke(mine ? chosen : null);
				}
			}
			active = owner;
		}

		/// <summary>The "breathable / not breathable" notes belong to the normal view; the picture of a selected substance has none.</summary>
		private static bool HideBreathingLegend(OverlayModes.Mode ___currentMode)
		{
			return !(active != null && !overlayFailed && ___currentMode is OverlayModes.Oxygen);
		}

		private static void Legend(OverlayModes.Mode __instance, ref List<LegendEntry> __result)
		{
			if (active != null && !overlayFailed && __instance is OverlayModes.Oxygen && Get<Func<List<LegendEntry>>>(active, "legend") is Func<List<LegendEntry>> legend)
			{
				__result = legend();
			}
		}

		// ---------- Common pieces of text for providers ----------

		/// <summary>Percentage at the start of a line, right-aligned with equal-width characters.</summary>
		internal static string Share(float share)
		{
			int percent = (int)Math.Round(share * 100f);
			string number = percent < 1 ? "<1" : percent.ToString();
			return $"<mspace=0.55em>{number,3}</mspace>{(string)STRINGS.UI.UNITSUFFIXES.PERCENT}";
		}

		/// <summary>Substance colour brought to full brightness: dark substances are otherwise invisible on the dark background.</summary>
		internal static Color Bright(Element element)
		{
			Color colour = element.substance.uiColour;
			float brightest = Mathf.Max(colour.r, Mathf.Max(colour.g, colour.b));
			return brightest > 0f ? new Color(colour.r / brightest, colour.g / brightest, colour.b / brightest, 1f) : Color.white;
		}
	}
}
