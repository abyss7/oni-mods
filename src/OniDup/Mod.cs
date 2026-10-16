using HarmonyLib;
using KMod;
using OniCommon;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;

namespace OniDup
{
	/// <summary>
	/// A harsher life for duplicants. So far: breathing by the pressure of oxygen, hypoxia, vacuum and decompression, Carbon Dioxide and too
	/// much Oxygen (DupAir, DupAirOverlay); digging and building without the hiccups (DupWork);
	/// no escape from being buried (DupEntombed).
	/// </summary>
	public sealed class Mod : UserMod2
	{
		internal static Options Options = new Options();

		public override void OnLoad(Harmony harmony)
		{
			ModContext.Folder = path;
			ModContext.Name = nameof(OniDup);
			PUtil.InitLibrary(false);
			new POptions().RegisterOptions(this, typeof(Options));
			Options = POptions.ReadSettings<Options>() ?? new Options();
			ModLocalization.Apply(harmony, typeof(ONIDUP));
			DupAir.Apply(harmony);
			DupAirOverlay.Apply(harmony);
			DupWork.Apply(harmony);
			DupEntombed.Apply(harmony);
			Debug.Log($"[OniDup] {assembly.GetName().Version}, air: {DupAir.Mode}, work: {DupWork.Mode}, entombed: {DupEntombed.Mode}, buried face suffocates: {Options.BuriedFaceSuffocates}");
		}
	}
}
