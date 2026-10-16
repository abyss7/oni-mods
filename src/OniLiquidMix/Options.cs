using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace OniLiquidMix
{
	/// <summary>
	/// Mod options. The options window is drawn by PLib; they are stored in config.json in the mod folder and applied at game start.
	/// Names and tooltips live in the mod strings (ModStrings.cs, class OPTIONS) and are translated together with them.
	/// </summary>
	[JsonObject(MemberSerialization.OptIn)]
	[ConfigFile(IndentOutput: true)]
	[RestartRequired]
	public sealed class Options
	{
		private const string Key = "STRINGS.ONILIQUIDMIX.OPTIONS.";

		/// <summary>
		/// Liquids of one mixing group lie in a cell as a mixture (sim/plugins/liquid_move.h). Also switches on own
		/// flow of liquids instead of the game's: a mixture has to be moved together with its composition.
		/// </summary>
		[Option(Key + "MIXING.NAME", Key + "MIXING.TOOLTIP")]
		[JsonProperty]
		public bool Mixing { get; set; } = true;

		/// <summary>
		/// Liquid layers and falling drops by density rather than molar mass; water and aqueous solutions have a density
		/// anomaly (highest near 4 °C, a pond freezes from the top). Works without mixtures too.
		/// </summary>
		[Option(Key + "DENSITY.NAME", Key + "DENSITY.TOOLTIP")]
		[JsonProperty]
		public bool DensityLayers { get; set; } = true;
	}
}
