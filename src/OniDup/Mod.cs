using HarmonyLib;
using KMod;
using OniCommon;

namespace OniDup
{
	/// <summary>
	/// A harsher life for duplicants. So far: breathing by the pressure of oxygen, hypoxia, vacuum and decompression (DupAir).
	/// </summary>
	public sealed class Mod : UserMod2
	{
		public override void OnLoad(Harmony harmony)
		{
			ModContext.Folder = path;
			ModContext.Name = nameof(OniDup);
			ModLocalization.Apply(harmony, typeof(ONIDUP));
			DupAir.Apply(harmony);
			DupAirOverlay.Apply(harmony);
			Debug.Log($"[OniDup] {assembly.GetName().Version}, air: {DupAir.Mode}");
		}
	}
}
