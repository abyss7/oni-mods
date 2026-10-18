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
				public static LocString TOOLTIP = "You see only what is lit: where there is no light, nothing shows, and dim light shows dimly. The light of the colony's own lamps shows while a Duplicant can walk into it; the light of the sun, creatures, plants and glowing rocks shows only where a Duplicant sees it at that moment. Dig and build orders need some light. Duplicants work slower in dim light and much slower in the dark, move slower in the dark, cannot do research, art, medicine, cooking or machine work in total darkness, and grow stressed if awake in it. The first Duplicants carry a glow stick while their New Hope lasts.";
			}

			public static class POOLGLOW
			{
				public static LocString NAME = "Glowing water at the start";
				public static LocString TOOLTIP = "With Blackout: at the start a glow lies at the bottom of the clean water pool nearest the Printing Pod, as if someone had dropped a glow stick there, so you can find the water. Its light shows whether or not a Duplicant can reach it. Once a Duplicant has seen it, it fades out within a minute; if nobody finds it, it fades with the first Duplicants' New Hope.";
			}
		}

		public static class EFFECTS
		{
			public static class INTHEDARK
			{
				public static LocString NAME = "In the Dark";
				public static LocString TOOLTIP = "This Duplicant has been awake in total darkness for over a minute and is growing stressed";
			}
		}

		public static class STATUSITEMS
		{
			public static class DARKWORKSPACE
			{
				public static LocString NAME = "Dark Workspace";
				public static LocString TOOLTIP = "This Duplicant works in total darkness: half as fast";
			}

			public static class DIMWORKSPACE
			{
				public static LocString NAME = "Dim Workspace";
				public static LocString TOOLTIP = "This Duplicant works in dim light (below 500 lux): 20% slower";
			}
		}

		public static class CHORES
		{
			public static LocString TOODARK = "Too dark to do this";
		}
	}
}
