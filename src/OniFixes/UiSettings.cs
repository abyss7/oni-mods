using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using HarmonyLib;
using KSerialization;
using Newtonsoft.Json;
using OniCommon;

namespace OniFixes
{
	/// <summary>
	/// Tool and overlay settings in the game save. The game keeps them only in memory and after every load
	/// resets them to defaults, so "deconstruct only buildings" or "materials: gases" has to be chosen
	/// again. Here they are written to the save and restored after loading:
	/// - the mode lists of tools (deconstruct, cancel, dig, disable, empty pipe, priorities, harvest):
	///   which list rows are enabled;
	/// - the lists in overlay legends (materials, temperature mode, germs): the same;
	/// - the gas selected in the gas mixture overlay.
	/// The game saves the relative temperature slider position itself; only the overlay mode was lost, without
	/// which the slider is not shown.
	///
	/// Which overlay was open is not remembered (the author's decision): after loading, overlays are closed.
	///
	/// Stored the same way as the gas mixture layer state: a component on a save object with one string (JSON).
	/// A save opened without the mod loads as usual: the game skips the data of an unknown component.
	/// </summary>
	[SerializationConfig(KSerialization.MemberSerialization.OptIn)]
	public sealed class UiSettingsState : KMonoBehaviour
	{
		[Serialize]
		private string settings;

		[OnSerializing]
		private void BeforeSave()
		{
			// Failed to capture the settings: the save keeps the previous ones rather than nothing.
			settings = UiSettings.Capture() ?? settings;
		}

		public override void OnSpawn()
		{
			base.OnSpawn();
			UiSettings.Loaded(settings);
		}
	}

	internal static class UiSettings
	{
		private const int FramesBeforeRestore = 5; // tools and overlays are created in the first frames after loading

		private sealed class Data
		{
			public Dictionary<string, List<string>> tools = new Dictionary<string, List<string>>();
			public Dictionary<string, List<string>> overlays = new Dictionary<string, List<string>>();
		}

		private static string pending;
		private static bool restorePending;
		private static int framesReady;

		// Self-check: file verify_ui_settings.txt in the mod folder.
		// - with the word "set": VerifyFrame frames after loading, deconstruct is set to "only buildings", materials to
		//   "gases", the temperature overlay to relative mode, and the game is saved to
		//   save_files/OniUiCheck/OniUiCheck.sav;
		// - with any other content: after the same number of frames the settings are written to the log as they stand in the game
		//   (after loading that save they must match the recorded ones).
		private const int VerifyFrame = 600;
		private static string verify;
		private static int verifyFrames;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SaveGame), "OnPrefabInit"),
				postfix: new HarmonyMethod(typeof(UiSettings), nameof(AttachState)));
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"),
				postfix: new HarmonyMethod(typeof(UiSettings), nameof(RestoreWhenReady)));
			string marker = Path.Combine(ModContext.Folder, "verify_ui_settings.txt");
			verify = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
		}

		private static void VerifyStep()
		{
			if (verify == null || PlayerController.Instance == null || OverlayScreen.Instance == null || SaveLoader.Instance == null || ++verifyFrames < VerifyFrame)
			{
				return;
			}
			bool set = verify == "set";
			verify = null;
			try
			{
				if (!set)
				{
					Debug.Log($"[OniFixes] UiSettings verify: settings in the game now: {Capture()}");
					return;
				}
				List<string> done = new List<string>();
				foreach (InterfaceTool tool in PlayerController.Instance.tools)
				{
					if (tool is DeconstructTool && Set(ToolToggles(tool, forRestore: true), new List<string> { ToolParameterMenu.FILTERLAYERS.BUILDINGS }))
					{
						done.Add("DeconstructTool");
					}
				}
				foreach (OverlayModes.Mode mode in Modes())
				{
					string wanted = mode is OverlayModes.TileMode ? ToolParameterMenu.FILTERLAYERS.GAS
						: mode is OverlayModes.Temperature ? ToolParameterMenu.FILTERLAYERS.RELATIVETEMPERATURE : null;
					if (wanted != null && Set(mode.legendFilters, new List<string> { wanted }))
					{
						done.Add(mode.GetType().Name);
					}
				}
				string file = Path.Combine(SaveLoader.GetSavePrefix(), "OniUiCheck", "OniUiCheck.sav");
				SaveLoader.Instance.Save(file, isAutoSave: false, updateSavePointer: false);
				Debug.Log($"[OniFixes] UiSettings verify: changed {string.Join(", ", done)}; settings written to {file}: {Capture()}");
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniFixes] UiSettings verify failed: {e}");
			}
		}

		private static void AttachState(SaveGame __instance)
		{
			__instance.gameObject.AddOrGet<UiSettingsState>();
		}

		/// <summary>Current settings as a string for the save; null if they could not be captured.</summary>
		internal static string Capture()
		{
			try
			{
				if (PlayerController.Instance == null || OverlayScreen.Instance == null)
				{
					return null;
				}
				Data data = new Data();
				foreach (InterfaceTool tool in PlayerController.Instance.tools)
				{
					ToolParameterMenu.ToggleData[] toggles = ToolToggles(tool, forRestore: false);
					if (toggles != null)
					{
						data.tools[tool.GetType().Name] = Enabled(toggles);
					}
				}
				foreach (OverlayModes.Mode mode in Modes())
				{
					data.overlays[mode.GetType().Name] = Enabled(mode.legendFilters);
				}
				return JsonConvert.SerializeObject(data);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniFixes] UiSettings: the tool and overlay settings were not captured for the save: {e}");
				return null;
			}
		}

		/// <summary>The game has been loaded or started; saved == null: no settings in the save.</summary>
		internal static void Loaded(string saved)
		{
			pending = saved;
			restorePending = saved != null;
			framesReady = 0;
		}

		private static void RestoreWhenReady()
		{
			VerifyStep();
			if (!restorePending || PlayerController.Instance == null || OverlayScreen.Instance == null || ToolMenu.Instance == null
				|| ++framesReady < FramesBeforeRestore)
			{
				return;
			}
			restorePending = false;
			try
			{
				Data data = JsonConvert.DeserializeObject<Data>(pending);
				pending = null;
				if (data == null)
				{
					return;
				}
				List<string> restored = new List<string>();
				foreach (InterfaceTool tool in PlayerController.Instance.tools)
				{
					ToolParameterMenu.ToggleData[] toggles = ToolToggles(tool, forRestore: true);
					string name = tool.GetType().Name;
					if (toggles != null && data.tools != null && data.tools.TryGetValue(name, out List<string> enabled) && Set(toggles, enabled))
					{
						if (tool is FilteredDragTool filtered)
						{
							// The tool remembers the player's choice separately: it returns to it after an overlay that
							// imposes a mode itself (for example, the pipe overlay).
							filtered.userSelectedFilters = filtered.CloneFilters(filtered.currentFilters);
						}
						restored.Add($"{name}=[{string.Join(",", enabled)}]");
					}
				}
				foreach (OverlayModes.Mode mode in Modes())
				{
					// Only the toggle states: the overlay will apply the mode itself when it is opened (on opening, the legend
					// builds its list from these toggles and calls their change handler).
					string name = mode.GetType().Name;
					if (data.overlays != null && data.overlays.TryGetValue(name, out List<string> enabled) && Set(mode.legendFilters, enabled))
					{
						restored.Add($"{name}=[{string.Join(",", enabled)}]");
					}
				}
				Debug.Log($"[OniFixes] UiSettings: restored from the save: {(restored.Count > 0 ? string.Join("; ", restored) : "nothing")}");
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniFixes] UiSettings: the tool and overlay settings from the save were not restored: {e}");
			}
		}

		/// <summary>The tool's toggles that the player chooses; null if the tool has none.</summary>
		private static ToolParameterMenu.ToggleData[] ToolToggles(InterfaceTool tool, bool forRestore)
		{
			if (tool is FilteredDragTool filtered)
			{
				// While an overlay that imposes a mode on the tool is open, the player's choice is kept separately.
				return !forRestore && filtered.isOverlayDriven && filtered.userSelectedFilters != null ? filtered.userSelectedFilters : filtered.currentFilters;
			}
			if (tool is HarvestTool harvest)
			{
				return harvest.options;
			}
			return null;
		}

		/// <summary>Overlay modes with a selection list in the legend. The gas mixture overlay builds its list itself.</summary>
		private static IEnumerable<OverlayModes.Mode> Modes()
		{
			foreach (OverlayScreen.ModeInfo info in OverlayScreen.Instance.modeInfos.Values)
			{
				if (info.mode != null && info.mode.legendFilters != null && !(info.mode is OverlayModes.Oxygen))
				{
					yield return info.mode;
				}
			}
		}

		private static List<string> Enabled(ToolParameterMenu.ToggleData[] toggles)
		{
			List<string> enabled = new List<string>();
			foreach (ToolParameterMenu.ToggleData toggle in toggles)
			{
				if (toggle.IsOn)
				{
					enabled.Add(toggle.name);
				}
			}
			return enabled;
		}

		/// <summary>
		/// Enables exactly the listed toggles. Changes nothing and returns false if the list is empty
		/// or contains a name these toggles do not have (another game version): defaults are better than half a selection.
		/// </summary>
		private static bool Set(ToolParameterMenu.ToggleData[] toggles, List<string> enabled)
		{
			if (toggles == null || enabled == null || enabled.Count == 0)
			{
				return false;
			}
			foreach (string name in enabled)
			{
				if (Array.FindIndex(toggles, toggle => toggle.name == name && toggle.state != ToolParameterMenu.ToggleState.Disabled) < 0)
				{
					return false;
				}
			}
			foreach (ToolParameterMenu.ToggleData toggle in toggles)
			{
				if (toggle.state != ToolParameterMenu.ToggleState.Disabled)
				{
					toggle.state = enabled.Contains(toggle.name) ? ToolParameterMenu.ToggleState.On : ToolParameterMenu.ToggleState.Off;
				}
			}
			return true;
		}
	}
}
