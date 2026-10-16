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
				public static LocString NAME = "Hypoxia";
				public static LocString TOOLTIP = "There is too little oxygen in what this Duplicant breathes: they tire quickly and think slowly";
			}

			public static class SEVEREHYPOXIA
			{
				public static LocString NAME = "Severe Hypoxia";
				public static LocString TOOLTIP = "This Duplicant can barely get any oxygen and will pass out soon";
			}

			public static class LOWPRESSURE
			{
				public static LocString NAME = "Low pressure";
			}

			public static class EBULLISM
			{
				public static LocString NAME = "Ebullism";
				public static LocString TOOLTIP = "The pressure here is so low that the water in this Duplicant's body is boiling. Only a sealed suit protects from it";
			}
		}
	}
}
