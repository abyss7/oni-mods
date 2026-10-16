using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace OniDup
{
	/// <summary>
	/// Mod options. The options window is drawn by PLib; they are stored in config.json in the mod folder and applied at game start.
	/// Names and tooltips live in the mod strings (ModStrings.cs, class OPTIONS).
	/// </summary>
	[JsonObject(MemberSerialization.OptIn)]
	[ConfigFile(IndentOutput: true)]
	[RestartRequired]
	public sealed class Options
	{
		private const string Key = "STRINGS.ONIDUP.OPTIONS.";

		/// <summary>
		/// A Duplicant buried over the head cannot breathe (holds their breath, then the game's suffocation). Off: they take air
		/// from the free cells next to their head, as in the game.
		/// </summary>
		[Option(Key + "BURIEDBREATH.NAME", Key + "BURIEDBREATH.TOOLTIP")]
		[JsonProperty]
		public bool BuriedFaceSuffocates { get; set; }
	}
}
