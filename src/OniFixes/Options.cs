using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace OniFixes
{
	/// <summary>
	/// Mod settings. The settings window is drawn by PLib: a button appears in the mod's row in the mod list
	/// (including in Mod Manager). Stored in config.json in the mod folder, applied at game launch.
	/// </summary>
	[JsonObject(MemberSerialization.OptIn)]
	[ConfigFile(IndentOutput: true)]
	[RestartRequired]
	public sealed class Options
	{
		// Names, tooltips and categories live in the mod strings (ModStrings.cs, class OPTIONS) and are translated together
		// with them; here are only the keys by which the settings window finds the text in the game's string table.
		private const string Key = "STRINGS.ONIFIXES.OPTIONS.";

		[Option(Key + "REMEMBERTOOLSETTINGS.NAME", Key + "REMEMBERTOOLSETTINGS.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool RememberToolSettings { get; set; } = true;

		[Option(Key + "REDRAWONANIMSTART.NAME", Key + "REDRAWONANIMSTART.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool RedrawOnAnimStart { get; set; } = true;

		[Option(Key + "CONVEYORDEPTHGUARD.NAME", Key + "CONVEYORDEPTHGUARD.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool ConveyorDepthGuard { get; set; } = true;

		[Option(Key + "CONVEYOROVERLAYITEMS.NAME", Key + "CONVEYOROVERLAYITEMS.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool ConveyorOverlayItems { get; set; } = true;

		[Option(Key + "CONVEYORARRIVALS.NAME", Key + "CONVEYORARRIVALS.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool ConveyorArrivals { get; set; } = true;

		[Option(Key + "QUICKPILLS.NAME", Key + "QUICKPILLS.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool QuickPills { get; set; } = true;

		[Option(Key + "OUTHOUSEMETER.NAME", Key + "OUTHOUSEMETER.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool OuthouseMeter { get; set; } = true;

		[Option(Key + "HIDDENPRIORITIES.NAME", Key + "HIDDENPRIORITIES.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool HiddenPriorities { get; set; } = true;

		[Option(Key + "SMOOTHSLOWMOVES.NAME", Key + "SMOOTHSLOWMOVES.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool SmoothSlowMoves { get; set; } = true;

		[Option(Key + "DOORCLIMBERS.NAME", Key + "DOORCLIMBERS.TOOLTIP", Key + "CATEGORY.FIXES")]
		[JsonProperty]
		public bool DoorClimbers { get; set; } = true;

		[Option(Key + "DEMANDDRIVENFETCH.NAME", Key + "DEMANDDRIVENFETCH.TOOLTIP", Key + "CATEGORY.DUPLICANTS")]
		[JsonProperty]
		public bool DemandDrivenFetch { get; set; } = true;

		[Option(Key + "TRIPAWAREFETCH.NAME", Key + "TRIPAWAREFETCH.TOOLTIP", Key + "CATEGORY.DUPLICANTS")]
		[JsonProperty]
		public bool TripAwareFetch { get; set; } = true;

		[Option(Key + "THROTTLEPROPERTYTEXTURES.NAME", Key + "THROTTLEPROPERTYTEXTURES.TOOLTIP", Key + "CATEGORY.RENDERING")]
		[JsonProperty]
		public bool ThrottlePropertyTextures { get; set; } = true;

		[Option(Key + "PROPERTYTEXTURESMAXSTALEFRAMES.NAME", Key + "PROPERTYTEXTURESMAXSTALEFRAMES.TOOLTIP", Key + "CATEGORY.RENDERING")]
		[Limit(1, 12)]
		[JsonProperty]
		public int PropertyTexturesMaxStaleFrames { get; set; } = 4;

		[Option(Key + "CACHEDSYMBOLOVERRIDES.NAME", Key + "CACHEDSYMBOLOVERRIDES.TOOLTIP", Key + "CATEGORY.RENDERING")]
		[JsonProperty]
		public bool CachedSymbolOverrides { get; set; } = true;

		[Option(Key + "NATIVEPROPERTYTEXTURES.NAME", Key + "NATIVEPROPERTYTEXTURES.TOOLTIP", Key + "CATEGORY.RENDERING")]
		[JsonProperty]
		public bool NativePropertyTextures { get; set; } = true;

		[Option(Key + "ASYNCPROPERTYTEXTURES.NAME", Key + "ASYNCPROPERTYTEXTURES.TOOLTIP", Key + "CATEGORY.RENDERING")]
		[JsonProperty]
		public bool AsyncPropertyTextures { get; set; } = false;
	}
}
