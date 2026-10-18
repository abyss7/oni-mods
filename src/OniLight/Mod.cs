using HarmonyLib;
using KMod;
using OniCommon;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;

namespace OniLight
{
	/// <summary>
	/// Physical light (LightGrid, on the native engine LightNative): lamps light along rays that liquids dim and windows let
	/// through, with a smooth falloff; surfaces and back walls reflect light by their material (Reflectance), so it goes
	/// round corners. The Blackout mode (option): the player sees only what is lit and what Duplicants could see (Darkness).
	/// Self-check scenes: the file verify_light_cases.txt (LightCases).
	/// </summary>
	public sealed class Mod : UserMod2
	{
		internal static Options Options = new Options();

		public override void OnLoad(Harmony harmony)
		{
			ModContext.Folder = path;
			ModContext.Name = nameof(OniLight);
			PUtil.InitLibrary(false);
			new POptions().RegisterOptions(this, typeof(Options));
			Options = POptions.ReadSettings<Options>() ?? new Options();
			ModLocalization.Apply(harmony, typeof(ONILIGHT));
			Reflectance.Publish();
			if (!LightNative.Load())
			{
				Debug.LogWarning($"[OniLight] {assembly.GetName().Version}: the light engine is not available ({LightNative.Info}); the game's light stays");
				return;
			}
			LightGrid.Apply(harmony);
			if (Options.Blackout)
			{
				Darkness.Apply(harmony);
			}
			LightCases.Apply(harmony);
			Debug.Log($"[OniLight] {assembly.GetName().Version}, {LightNative.Info}; blackout {(Options.Blackout ? "on" : "off")}");
		}
	}
}
