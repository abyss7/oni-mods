using System;
using HarmonyLib;
using KMod;
using OniCommon;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;

namespace OniGasMix
{
	/// <summary>
	/// Gas mixture: a gas cell holds the mass of each gas separately. The bookkeeping is done by the native gas-mix plugin
	/// (sim/plugins/gas_mix.cpp) on the game's simulation thread; here is its hookup and everything the player sees.
	/// </summary>
	public sealed class Mod : UserMod2
	{
		internal static Options Options;

		public override void OnLoad(Harmony harmony)
		{
			ModContext.Folder = path;
			ModContext.Name = nameof(OniGasMix);
			PUtil.InitLibrary(false);
			new POptions().RegisterOptions(this, typeof(Options));
			Options = POptions.ReadSettings<Options>() ?? new Options();
			ModLocalization.Apply(harmony, typeof(ONIGASMIX));
			AbTest.Init(harmony);
			try
			{
				GasLayer.Apply(harmony);
				Debug.Log($"[OniGasMix] {assembly.GetName().Version}, the layer {(Options.MoveGases ? "moves gases" : "only follows gases")}; settings: {POptions.GetConfigFilePath(typeof(Options))}");
			}
			catch (Exception e)
			{
				// Without the hook libraries, or with a different game build, the mod changes nothing: the game runs as usual.
				Debug.LogWarning($"[OniGasMix] the gas mixture layer is off: {e}");
			}
		}
	}
}
