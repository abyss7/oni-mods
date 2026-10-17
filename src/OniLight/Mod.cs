using HarmonyLib;
using KMod;
using OniCommon;

namespace OniLight
{
	/// <summary>
	/// Physical light (LightGrid, on the native engine LightNative): lamps light along rays that liquids dim and windows let
	/// through, with a smooth falloff; surfaces and back walls reflect light by their material (Reflectance), so it goes
	/// round corners. The groundwork for the Blackout mode. Self-check scenes: the file verify_light_cases.txt (LightCases).
	/// </summary>
	public sealed class Mod : UserMod2
	{
		public override void OnLoad(Harmony harmony)
		{
			ModContext.Folder = path;
			ModContext.Name = nameof(OniLight);
			Reflectance.Publish();
			if (!LightNative.Load())
			{
				Debug.LogWarning($"[OniLight] {assembly.GetName().Version}: the light engine is not available ({LightNative.Info}); the game's light stays");
				return;
			}
			LightGrid.Apply(harmony);
			LightCases.Apply(harmony);
			Debug.Log($"[OniLight] {assembly.GetName().Version}, {LightNative.Info}");
		}
	}
}
