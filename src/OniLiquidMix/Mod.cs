using HarmonyLib;
using KMod;
using OniCommon;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;

namespace OniLiquidMix
{
	/// <summary>
	/// Liquid mixing: liquids of one mixing group lie in a cell as a mixture, like gases in the gas mixture mod
	/// (rules: sim/plugins/liquid_rules.h). The mod passes the plugin the liquid table with groups (LiquidTable), turns on its
	/// parts by the settings (LiquidSurvey), keeps compositions in the save (LiquidMixSave) and moves the game logic that looks
	/// at a cell's liquid to the composition (LiquidMixLogic, LiquidMixWorld, LiquidMixUi).
	/// </summary>
	public sealed class Mod : UserMod2
	{
		internal static Options Options;

		public override void OnLoad(Harmony harmony)
		{
			ModContext.Folder = path;
			ModContext.Name = nameof(OniLiquidMix);
			PUtil.InitLibrary(false);
			new POptions().RegisterOptions(this, typeof(Options));
			Options = POptions.ReadSettings<Options>() ?? new Options();
			ModLocalization.Apply(harmony, typeof(ONILIQUIDMIX));
			LiquidSurvey.Apply(harmony);
			LiquidMixSave.Apply(harmony);
			LiquidCases.Apply(harmony);
			LiquidMixture.Publish();
			Debug.Log($"[OniLiquidMix] {assembly.GetName().Version}, liquid survey: {LiquidSurvey.Mode}");
		}
	}
}
