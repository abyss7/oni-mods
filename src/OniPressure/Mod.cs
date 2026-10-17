using HarmonyLib;
using KMod;
using OniCommon;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;

namespace OniPressure
{
	/// <summary>
	/// Pressures: boiling, evaporation and condensation depend on pressure and carry latent heat (PhaseRules);
	/// pressure on walls is only measured for now (PressureSurvey). The mod works both with the gas mixture mod
	/// (OniGasMix) and without it.
	/// </summary>
	public sealed class Mod : UserMod2
	{
		internal static Options Options;

		public override void OnLoad(Harmony harmony)
		{
			ModContext.Folder = path;
			ModContext.Name = nameof(OniPressure);
			PUtil.InitLibrary(false);
			new POptions().RegisterOptions(this, typeof(Options));
			Options = POptions.ReadSettings<Options>() ?? new Options();
			ModLocalization.Apply(harmony, typeof(ONIPRESSURE));
			MolarMasses.PublishMode(!Options.PressureByMass);
			PressureSurvey.Apply(harmony);
			PhaseRules.Apply(harmony);
			CellPressure.Publish(Options.PressureByMass);
			Debug.Log($"[OniPressure] {assembly.GetName().Version}, phase rules: {PhaseRules.Mode}, survey {(PressureSurvey.Enabled ? "on" : "off")}");
		}
	}
}
