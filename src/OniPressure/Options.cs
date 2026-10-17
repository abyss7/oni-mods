using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace OniPressure
{
	/// <summary>
	/// Mod options. The options dialog is drawn by PLib; they are stored in config.json in the mod folder and applied at game start.
	/// Names and tooltips live in the mod's strings (ModStrings.cs, class OPTIONS) and are translated along with them.
	/// </summary>
	[JsonObject(MemberSerialization.OptIn)]
	[ConfigFile(IndentOutput: true)]
	[RestartRequired]
	public sealed class Options
	{
		private const string Key = "STRINGS.ONIPRESSURE.OPTIONS.";

		/// <summary>
		/// How the mass of gas in a cell becomes pressure for boiling and condensation (sim/plugins/phase_rules.h).
		/// By mass, as everywhere else in the game: one atmosphere is 1.8 kg of any gas. By molecules, as in physics (the
		/// default, the author's decision of 2026-10-17): pressure is set by the number of molecules and the temperature.
		/// </summary>
		[Option(Key + "BYMASS.NAME", Key + "BYMASS.TOOLTIP")]
		[JsonProperty]
		public bool PressureByMass { get; set; } = false;
	}
}
