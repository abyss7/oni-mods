using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace OniLight
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
		private const string Key = "STRINGS.ONILIGHT.OPTIONS.";

		/// <summary>The Blackout mode: the player sees only what is lit and what Duplicants could see (Darkness).</summary>
		[Option(Key + "BLACKOUT.NAME", Key + "BLACKOUT.TOOLTIP")]
		[JsonProperty]
		public bool Blackout { get; set; } = true;

		/// <summary>With Blackout: a glow at the bottom of the clean water pool nearest the printing pod while New Hope lasts.</summary>
		[Option(Key + "POOLGLOW.NAME", Key + "POOLGLOW.TOOLTIP")]
		[JsonProperty]
		public bool PoolGlow { get; set; } = true;
	}
}
