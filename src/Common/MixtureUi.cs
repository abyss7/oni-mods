using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace OniCommon
{
	/// <summary>
	/// Common mixtures interface for the gas mixture and liquid mixture mods: the tooltip under the cursor, the selected-cell line,
	/// the mixtures overlay (its own button next to the oxygen one) and the views of the whole air in the oxygen overlay. Each mod describes its own group (a "provider") and registers it here; the game's
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
	///   "flat"     bool                            optional: true puts the provider's choices into the oxygen overlay, right under
	///                                              its normal view (a view of the whole air rather than a group of substances);
	///                                              the other providers are groups of the mixtures overlay;
	///   "replace"  bool                            optional, with "flat": the provider's first choice replaces the oxygen overlay's
	///                                              normal view altogether: no selection list, its own legend only;
	///   "overlayName", "overlayButton", "overlayTooltip" Func&lt;string&gt;: the mixtures overlay's names (the same in meaning for
	///              all providers, taken from the first one).
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
			InstallOverlays(harmony);
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

		// ---------- Overlays ----------
		// Two overlays. The game's oxygen overlay keeps breathing: its normal "Breathable" view and the views of the whole air
		// ("flat" providers, like Breathing Conditions). A new "Mixtures" overlay right after it in the overlay bar, with its own
		// button and icon, shows the groups of substances (gases, liquids) and exists only if such a group is registered. Each
		// overlay has its own selection list in the legend and its own selected provider.

		/// <summary>The mixtures overlay's mode identifier.</summary>
		internal static readonly HashedString MixturesId = "OniMixtures";
		private const string IconName = "onimods_overlay_mixtures";

		/// <summary>The mixtures overlay: drawn like the oxygen one (cell colours from SimDebugView), with its own selection.</summary>
		internal sealed class MixturesMode : OverlayModes.Mode
		{
			public override HashedString ViewMode() => MixturesId;

			public override string GetSoundName() => "Oxygen";

			public override void Enable()
			{
				BuildChoice(this);
				base.Enable();
				SelectTool.Instance.SetLayerMask(SelectTool.Instance.GetDefaultLayerMask() | LayerMask.GetMask("MaskedOverlay"));
			}

			public override void Disable()
			{
				base.Disable();
				SelectTool.Instance.ClearLayerMask();
			}
		}

		private static Dictionary<string, object> activeMixtures; // provider of the selection in the mixtures overlay

		private static bool HasGroups => providers != null && providers.Exists(provider => !IsFlat(provider));

		private static bool IsMixtures(OverlayModes.Mode mode) => mode != null && mode.ViewMode() == MixturesId;

		private static void InstallOverlays(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SimDebugView), "OnPrefabInit"),
				postfix: new HarmonyMethod(typeof(MixtureUi), nameof(ColourFunctions)));
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
			harmony.Patch(AccessTools.Method(typeof(OverlayScreen), "RegisterModes"),
				postfix: new HarmonyMethod(typeof(MixtureUi), nameof(RegisterMixtures)));
			harmony.Patch(AccessTools.Method(typeof(OverlayMenu), "InitializeToggles"),
				postfix: new HarmonyMethod(typeof(MixtureUi), nameof(AddButton)));
			harmony.Patch(AccessTools.Method(typeof(SelectToolHoverTextCard), "OnSpawn"),
				postfix: new HarmonyMethod(typeof(MixtureUi), nameof(HoverFilter)));
			harmony.Patch(AccessTools.Method(typeof(CellSelectionObject), "OnSpawn"),
				postfix: new HarmonyMethod(typeof(MixtureUi), nameof(SelectionFilter)));
		}

		private static void RegisterMixtures(OverlayScreen __instance)
		{
			if (!HasGroups)
			{
				return;
			}
			try
			{
				__instance.RegisterMode(new MixturesMode());
				// Status items are not tied to this overlay; without the entry the game warns on every change.
				if (!StatusItem.overlayBitfieldMap.ContainsKey(MixturesId))
				{
					StatusItem.overlayBitfieldMap.Add(MixturesId, StatusItem.StatusItemOverlays.None);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the mixtures overlay could not be registered: {e}");
			}
		}

		/// <summary>The mixtures overlay's button, right after the oxygen one; the text and the tooltip come from the providers.</summary>
		private static void AddButton(OverlayMenu __instance)
		{
			if (!HasGroups)
			{
				return;
			}
			try
			{
				string button = null, tooltip = null;
				foreach (Dictionary<string, object> provider in providers)
				{
					if (button == null && Text(provider, "overlayButton") is string text)
					{
						button = text;
						tooltip = Text(provider, "overlayTooltip");
					}
				}
				button = button ?? "Mixtures Overlay";
				tooltip = (tooltip ?? button).Replace("{Hotkey}", "").TrimEnd();
				string icon = LoadIcon() ? IconName : "overlay_gasvent";
				var toggle = new OverlayMenu.OverlayToggleInfo(button, icon, MixturesId, "", Action.NumActions, tooltip, button);
				int oxygen = __instance.overlayToggleInfos.FindIndex(info => info is OverlayMenu.OverlayToggleInfo overlay && overlay.simView == OverlayModes.Oxygen.ID);
				__instance.overlayToggleInfos.Insert(oxygen + 1, toggle);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the mixtures overlay button could not be added: {e}");
			}
		}

		/// <summary>
		/// The button icon: overlay_mixtures.png (drawn by scripts/draw_overlay_icon.py in the style of the game's overlay icons),
		/// built into each mod that carries this code. Put among the game's sprites once.
		/// </summary>
		private static bool LoadIcon()
		{
			if (Assets.Sprites.ContainsKey(IconName))
			{
				return true;
			}
			try
			{
				byte[] png;
				using (System.IO.Stream stream = typeof(MixtureUi).Assembly.GetManifestResourceStream("OniCommon.overlay_mixtures.png"))
				{
					if (stream == null)
					{
						return false;
					}
					png = new byte[stream.Length];
					stream.Read(png, 0, png.Length);
				}
				// ImageConversion lives in a module the mods do not reference; the call goes through reflection.
				System.Reflection.MethodInfo load = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")
					?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
				var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, name = IconName };
				if (load == null || !(bool)load.Invoke(null, new object[] { texture, png, false }))
				{
					return false;
				}
				Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
				sprite.name = IconName;
				Assets.Sprites.Add(IconName, sprite);
				return true;
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the mixtures overlay icon could not be loaded: {e.Message}");
				return false;
			}
		}

		/// <summary>Under the cursor and in cell selection the mixtures overlay picks gas and liquid cells, as the oxygen one picks gas.</summary>
		private static void HoverFilter(SelectToolHoverTextCard __instance)
		{
			if (HasGroups && !__instance.overlayFilterMap.ContainsKey(MixturesId))
			{
				__instance.overlayFilterMap.Add(MixturesId, () =>
				{
					int cell = Grid.PosToCell(CameraController.Instance.baseCamera.ScreenToWorldPoint(KInputManager.GetMousePos()));
					return Grid.IsValidCell(cell) && (Grid.Element[cell].IsGas || Grid.Element[cell].IsLiquid);
				});
			}
		}

		private static void SelectionFilter(CellSelectionObject __instance)
		{
			if (HasGroups && !__instance.overlayFilterMap.ContainsKey(MixturesId))
			{
				__instance.overlayFilterMap.Add(MixturesId, () => Grid.IsValidCell(__instance.mouseCell)
					&& (Grid.Element[__instance.mouseCell].IsGas || Grid.Element[__instance.mouseCell].IsLiquid));
			}
		}

		private static void ColourFunctions(SimDebugView __instance)
		{
			Dictionary<HashedString, Func<SimDebugView, int, Color>> functions = __instance.getColourFuncs;
			if (functions == null || !functions.TryGetValue(OverlayModes.Oxygen.ID, out Func<SimDebugView, int, Color> current))
			{
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the oxygen overlay colour function was not found; the overlays are left as they are");
				overlayFailed = true;
				return;
			}
			original = current;
			functions[OverlayModes.Oxygen.ID] = Colour;
			// The mixtures overlay is drawn the same way as the oxygen one.
			functions[MixturesId] = MixturesColour;
			if (__instance.dataUpdateFuncs.TryGetValue(OverlayModes.Oxygen.ID, out Action<SimDebugView, Texture> update))
			{
				__instance.dataUpdateFuncs[MixturesId] = update;
			}
		}

		/// <summary>Oxygen overlay; called by the game from worker threads for each visible cell.</summary>
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

		/// <summary>Mixtures overlay; called from worker threads.</summary>
		private static Color MixturesColour(SimDebugView instance, int cell)
		{
			Dictionary<string, object> chosen = activeMixtures;
			return chosen != null && Get<Func<int, Color>>(chosen, "colour") is Func<int, Color> colour ? colour(cell) : Color.black;
		}

		private static void BeforeRedraw(HashedString viewMode)
		{
			if (overlayFailed)
			{
				return;
			}
			Dictionary<string, object> chosen = viewMode == OverlayModes.Oxygen.ID ? active : viewMode == MixturesId ? activeMixtures : null;
			if (chosen != null)
			{
				Get<System.Action>(chosen, "refresh")?.Invoke();
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
		/// Before an overlay is turned on, builds its selection list. Oxygen: the normal view and the views of the whole air
		/// (with none of those the overlay stays as in the game). Mixtures: the groups of substances.
		/// </summary>
		private static void BuildChoice(OverlayModes.Mode __instance)
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
					if (ReferenceEquals(OverlayLegend.Instance.currentMode, __instance) && OverlayLegend.Instance.filterMenu != null)
					{
						return;
					}
				}
				bool mixtures = IsMixtures(__instance);
				// A view that replaces the oxygen overlay's normal one: always on, no list; the legend is still built from code
				// (its own rows, without the game's).
				Dictionary<string, object> replacing = mixtures ? null : Replacing();
				ToolParameterMenu.ToggleData[] toggles = replacing != null ? new ToolParameterMenu.ToggleData[0] : mixtures ? MixturesToggles() : OxygenToggles();
				__instance.legendFilters = toggles.Length > (mixtures ? 0 : 1) ? toggles : null;
				// The legend builds a selection list only for overlays marked as "filled from code" (like the temperature one).
				List<OverlayLegend.OverlayInfo> infos = OverlayLegend.Instance?.overlayInfoList;
				if (infos == null)
				{
					return;
				}
				HashedString id = __instance.ViewMode();
				OverlayLegend.OverlayInfo info = infos.Find(candidate => candidate.mode == id);
				if (info == null && mixtures)
				{
					info = new OverlayLegend.OverlayInfo { mode = id, infoUnits = new List<OverlayLegend.OverlayInfoUnit>(), diagrams = new List<GameObject>() };
					infos.Add(info);
				}
				if (info != null)
				{
					info.isProgrammaticallyPopulated = __instance.legendFilters != null || replacing != null;
					if (replacing != null)
					{
						info.name = (Text(replacing, "title") ?? info.name).ToUpper();
					}
					if (mixtures)
					{
						info.name = providers.Select(provider => Text(provider, "overlayName")).FirstOrDefault(name => name != null) ?? "MIXTURES OVERLAY";
					}
				}
			}
			catch (Exception e)
			{
				overlayFailed = true;
				active = activeMixtures = null;
				__instance.legendFilters = null;
				Debug.LogWarning($"{ModContext.Tag} mixture interface: the overlay part is switched off after an error: {e}");
			}
		}

		private const string HeadingPrefix = "ONIMIXTUREGROUP";
		private static string expanded; // id of the group whose list is open in the mixtures overlay; one at a time
		private static readonly Dictionary<string, string> groupOfHeading = new Dictionary<string, string>();

		/// <summary>
		/// The view of the whole air that replaces the oxygen overlay's normal one ("replace"), selected; null if there is none.
		/// </summary>
		private static Dictionary<string, object> Replacing()
		{
			foreach (Dictionary<string, object> provider in providers.FindAll(IsFlat))
			{
				if (!(provider.TryGetValue("replace", out object replace) && replace is bool on && on))
				{
					continue;
				}
				List<string[]> choices = Get<Func<List<string[]>>>(provider, "choices")?.Invoke();
				if (choices == null || choices.Count == 0)
				{
					continue;
				}
				if (Get<Func<string>>(provider, "current")?.Invoke() != choices[0][0])
				{
					Get<Action<string>>(provider, "select")?.Invoke(choices[0][0]);
				}
				active = provider;
				return provider;
			}
			return null;
		}

		/// <summary>Oxygen overlay: the normal "Breathable" view, then the views of the whole air.</summary>
		private static ToolParameterMenu.ToggleData[] OxygenToggles()
		{
			var toggles = new List<ToolParameterMenu.ToggleData>();
			AddString(BaseKey, STRINGS.MISC.TAGS.BREATHABLE, STRINGS.UI.OVERLAYS.OXYGEN.TOOLTIPS.LEGEND2);
			Dictionary<string, object> stillActive = null;
			foreach (Dictionary<string, object> provider in providers.FindAll(IsFlat))
			{
				List<string[]> choices = Get<Func<List<string[]>>>(provider, "choices")?.Invoke();
				if (choices == null || choices.Count == 0)
				{
					continue;
				}
				string current = Get<Func<string>>(provider, "current")?.Invoke();
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
					Get<Action<string>>(provider, "select")?.Invoke(null);
				}
			}
			active = stillActive;
			toggles.Insert(0, new ToolParameterMenu.ToggleData(BaseKey, active == null ? ToolParameterMenu.ToggleState.On : ToolParameterMenu.ToggleState.Off));
			return toggles.ToArray();
		}

		/// <summary>
		/// Mixtures overlay: the groups of substances. A group shows only its title, "+ GASES", until it is clicked; then its
		/// substances open under it ("– GASES") and the other group closes. The group with the current selection is open. With no
		/// selection the first substance of the first group is selected: the overlay always shows something.
		/// </summary>
		private static ToolParameterMenu.ToggleData[] MixturesToggles()
		{
			var toggles = new List<ToolParameterMenu.ToggleData>();
			List<Dictionary<string, object>> groups = providers.FindAll(provider => !IsFlat(provider));
			bool anySelected = groups.Exists(provider => Get<Func<string>>(provider, "current")?.Invoke() != null);
			if (!anySelected)
			{
				foreach (Dictionary<string, object> provider in groups)
				{
					List<string[]> choices = Get<Func<List<string[]>>>(provider, "choices")?.Invoke();
					if (choices != null && choices.Count > 0)
					{
						choices.Sort((a, b) => string.Compare(a[1], b[1], StringComparison.CurrentCulture));
						Get<Action<string>>(provider, "select")?.Invoke(choices[0][0]);
						break;
					}
				}
			}
			Dictionary<string, object> stillActive = null;
			foreach (Dictionary<string, object> provider in groups)
			{
				List<string[]> choices = Get<Func<List<string[]>>>(provider, "choices")?.Invoke();
				if (choices == null || choices.Count == 0)
				{
					continue;
				}
				string id = (string)provider["id"];
				string current = Get<Func<string>>(provider, "current")?.Invoke();
				if (current != null && choices.Exists(choice => choice[0] == current))
				{
					expanded = id;
				}
				bool open = expanded == id;
				string heading = HeadingPrefix + id.ToUpperInvariant();
				groupOfHeading[heading] = id;
				// The legend list does not show bold font (checked with a screenshot): the title is marked with capitals.
				string title = Text(provider, "title") ?? id;
				AddString(heading, (open ? "– " : "+ ") + title.ToUpper(), title);
				toggles.Add(new ToolParameterMenu.ToggleData(heading, ToolParameterMenu.ToggleState.Off));
				choices.Sort((a, b) => string.Compare(a[1], b[1], StringComparison.CurrentCulture));
				bool found = false;
				foreach (string[] choice in choices)
				{
					AddString(choice[0], choice[1], choice[2]);
					bool on = choice[0] == current;
					found = found || on;
					if (open)
					{
						toggles.Add(new ToolParameterMenu.ToggleData(choice[0], on ? ToolParameterMenu.ToggleState.On : ToolParameterMenu.ToggleState.Off));
					}
				}
				if (found && stillActive == null)
				{
					stillActive = provider;
				}
				else if (current != null)
				{
					Get<Action<string>>(provider, "select")?.Invoke(null);
				}
			}
			activeMixtures = stillActive;
			return toggles.ToArray();
		}

		/// <summary>A group title was clicked: its list opens or closes; the selection stays as it was.</summary>
		private static void ToggleGroup(OverlayModes.Mode mode, string heading)
		{
			string id = groupOfHeading[heading];
			expanded = expanded == id ? null : id;
			mode.legendFilters = MixturesToggles();
			// The menu is rebuilt on the next frame, outside its own click handler.
			UIScheduler.Instance.Schedule("MixtureUi.ToggleGroup", 0f, _ =>
			{
				OverlayLegend legend = OverlayLegend.Instance;
				if (legend == null || !ReferenceEquals(legend.currentMode, mode) || legend.filterMenu == null)
				{
					return;
				}
				legend.filterMenu.PopulateMenu(mode.legendFilters);
				legend.ConfigureUIHeight();
			});
		}

		private static bool IsFlat(Dictionary<string, object> provider)
		{
			return provider.TryGetValue("flat", out object flat) && flat is bool on && on;
		}

		private static void ChoiceChanged(OverlayModes.Mode ___currentMode)
		{
			bool mixtures = IsMixtures(___currentMode);
			if (overlayFailed || providers == null || !(mixtures || ___currentMode is OverlayModes.Oxygen) || ___currentMode.legendFilters == null)
			{
				return;
			}
			string chosen = null;
			foreach (ToolParameterMenu.ToggleData toggle in ___currentMode.legendFilters)
			{
				if (mixtures && toggle.IsOn && toggle.name.StartsWith(HeadingPrefix) && groupOfHeading.ContainsKey(toggle.name))
				{
					ToggleGroup(___currentMode, toggle.name);
					return;
				}
				if (toggle.IsOn && toggle.name != BaseKey)
				{
					chosen = toggle.name;
				}
			}
			Dictionary<string, object> owner = null;
			foreach (Dictionary<string, object> provider in providers.FindAll(provider => IsFlat(provider) != mixtures))
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
			if (mixtures)
			{
				activeMixtures = owner;
			}
			else
			{
				active = owner;
			}
		}

		/// <summary>The "breathable / not breathable" notes belong to the oxygen overlay's normal view; a view of the whole air has its own legend.</summary>
		private static bool HideBreathingLegend(OverlayModes.Mode ___currentMode)
		{
			return !(active != null && !overlayFailed && ___currentMode is OverlayModes.Oxygen);
		}

		private static void Legend(OverlayModes.Mode __instance, ref List<LegendEntry> __result)
		{
			if (overlayFailed)
			{
				return;
			}
			Dictionary<string, object> chosen = __instance is OverlayModes.Oxygen ? active : IsMixtures(__instance) ? activeMixtures : null;
			if (chosen != null && Get<Func<List<LegendEntry>>>(chosen, "legend") is Func<List<LegendEntry>> legend)
			{
				__result = legend();
			}
			else if (__result == null && __instance is OverlayModes.Oxygen && __instance.legendFilters != null)
			{
				// The normal view with the selection list: the legend built "from code" hides the overlay's own rows
				// (Very Breathable ... Unbreathable, with their icons) when the mode gives no rows of its own. An empty list keeps them.
				__result = new List<LegendEntry>();
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
