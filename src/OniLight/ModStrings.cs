namespace OniLight
{
	/// <summary>
	/// The mod's own player-facing strings, in English. Translations are loaded from translations/<language code>.po
	/// in the mod folder, if present, to match the game language; none are shipped.
	/// A string's key in a translation file: STRINGS.ONILIGHT.<class>.<field>. Shared code hooks the strings
	/// into the game (src/Common/ModLocalization.cs).
	/// </summary>
	public static class ONILIGHT
	{
		/// <summary>Mod options (Options.cs): the options dialog looks them up by the keys STRINGS.ONILIGHT.OPTIONS.<option>.NAME and .TOOLTIP.</summary>
		public static class OPTIONS
		{
			public static class BLACKOUT
			{
				public static LocString NAME = "Blackout";
				public static LocString TOOLTIP = "You see only what is lit: where there is no light, nothing shows, and dim light shows dimly; very bright light dazzles. The light of the colony's own lamps shows while a Duplicant can walk into it; the light of the sun, creatures, plants and glowing rocks shows only where a Duplicant sees it at that moment.";
			}
		}
	}
}
