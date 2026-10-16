namespace OniDup
{
	/// <summary>
	/// The mod's own strings shown to the player. The English text is here; the mod loads translations from
	/// translations/&lt;language code&gt;.po in the mod folder, if present. Key of a string in a translation file:
	/// STRINGS.ONIDUP.&lt;class&gt;.&lt;field&gt;.
	/// </summary>
	public static class ONIDUP
	{
		public static class EFFECTS
		{
			public static class HYPOXIA
			{
				public static LocString NAME = "Lightheaded";
				public static LocString TOOLTIP = "This Duplicant isn't getting enough <link=\"OXYGEN\">Oxygen</link> from the air here\n\nThe air needs more pressure or a richer share of <link=\"OXYGEN\">Oxygen</link>";
			}

			public static class SEVEREHYPOXIA
			{
				public static LocString NAME = "Gasping for Air";
				public static LocString TOOLTIP = "This Duplicant can barely draw any <link=\"OXYGEN\">Oxygen</link> from this thin air and will soon pass out";
			}

			public static class EBULLISM
			{
				public static LocString NAME = "Vacuum Exposure";
				public static LocString TOOLTIP = "The pressure here is so low that the moisture in this Duplicant's body is boiling away\n\nAn <link=\"OXYGENMASK\">Oxygen Mask</link> won't help here, only a sealed suit like the <link=\"ATMOSUIT\">Atmo Suit</link> will";
			}

			public static class BAROTRAUMA
			{
				public static LocString NAME = "Decompression Injury";
				public static LocString TOOLTIP = "A sudden drop in pressure hurt this Duplicant's ears and lungs\n\nA sealed suit like the <link=\"ATMOSUIT\">Atmo Suit</link> prevents this";
			}
		}

		/// <summary>Names of the changes to the breath bar, shown in its tooltip.</summary>
		public static class BREATH
		{
			public static LocString THINAIR = "Thin air in the lungs";
			public static LocString GASPING = "Gasping for Air";
			public static LocString VACUUM = "Vacuum Exposure";
		}

		public static class STATUSITEMS
		{
			public static class VACUUM
			{
				public static LocString NAME = "Exposed to Vacuum";
				public static LocString TOOLTIP = "This Duplicant is in a near-vacuum and will pass out within seconds\n\nOnly a sealed suit like the <link=\"ATMOSUIT\">Atmo Suit</link> protects against vacuum";
			}
		}

		public static class DEATHS
		{
			public static class DECOMPRESSION
			{
				public static LocString NAME = "Vacuum Exposure";
				public static LocString DESCRIPTION = "{Target} was exposed to the vacuum for too long.";
			}
		}
	}
}
