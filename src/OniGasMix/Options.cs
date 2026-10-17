using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace OniGasMix
{
	/// <summary>
	/// Mod settings. The settings window is drawn by PLib; they are stored in config.json in the mod folder and applied at game start.
	/// Names and tooltips live in the mod's strings (ModStrings.cs, class OPTIONS) and are translated together with them.
	/// </summary>
	[JsonObject(MemberSerialization.OptIn)]
	[ConfigFile(IndentOutput: true)]
	[RestartRequired]
	public sealed class Options
	{
		private const string Key = "STRINGS.ONIGASMIX.OPTIONS.";

		[Option(Key + "MOVEGASES.NAME", Key + "MOVEGASES.TOOLTIP")]
		[JsonProperty]
		public bool MoveGases { get; set; } = true;

		/// <summary>Order of the gases in the mixtures overlay list: off by name, on by the amount in the world.</summary>
		[Option(Key + "SORTBYAMOUNT.NAME", Key + "SORTBYAMOUNT.TOOLTIP")]
		[JsonProperty]
		public bool SortByAmount { get; set; }

		[Option(Key + "PRESENCEPERCENT.NAME", Key + "PRESENCEPERCENT.TOOLTIP")]
		[Limit(1, 50)]
		[JsonProperty]
		public int PresencePercent { get; set; } = 10;

		// Numbers of the gas movement rules (the layer kernel, sim/plugins/gas_mix_kernel.h). The defaults were tuned
		// on a model and checked in the game; the limits keep the computation from becoming unstable.
		[Option(Key + "PRESSURE.NAME", Key + "PRESSURE.TOOLTIP", Key + "CATEGORY.RULES", Format = "F2")]
		[Limit(0.05, 0.25)]
		[JsonProperty]
		public float Pressure { get; set; } = 0.2f;

		[Option(Key + "DIFFUSION.NAME", Key + "DIFFUSION.TOOLTIP", Key + "CATEGORY.RULES", Format = "F3")]
		[Limit(0.0, 0.05)]
		[JsonProperty]
		public float Diffusion { get; set; } = 0.004f;

		[Option(Key + "GRAVITY.NAME", Key + "GRAVITY.TOOLTIP", Key + "CATEGORY.RULES", Format = "F3")]
		[Limit(0.0, 0.2)]
		[JsonProperty]
		public float Gravity { get; set; } = 0.02f;

		[Option(Key + "SPREAD.NAME", Key + "SPREAD.TOOLTIP", Key + "CATEGORY.RULES", Format = "F2")]
		// A cell loses at most "spread x 2 + gravity" of its gas per step: in sum the limits stay below one.
		[Limit(0.0, 0.3)]
		[JsonProperty]
		public float Spread { get; set; } = 0.1f;

		[Option(Key + "CONDENSEGRAMS.NAME", Key + "CONDENSEGRAMS.TOOLTIP", Key + "CATEGORY.RULES")]
		[Limit(1, 1000)]
		[JsonProperty]
		public int CondenseGrams { get; set; } = 10;
	}
}
