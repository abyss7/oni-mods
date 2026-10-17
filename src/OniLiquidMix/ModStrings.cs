namespace OniLiquidMix
{
	/// <summary>
	/// The mod's own player-facing strings. The English text is here; translations are loaded from translations/<language code>.po
	/// in the mod folder, if present, for the game language (none are shipped).
	/// Key of a string in a translation file: STRINGS.ONILIQUIDMIX.<class>.<field>. The strings are hooked into the game by shared code
	/// (src/Common/ModLocalization.cs).
	/// </summary>
	public static class ONILIQUIDMIX
	{
		/// <summary>Mod options (Options.cs): the options window looks them up by the keys STRINGS.ONILIQUIDMIX.OPTIONS.<option>.NAME and .TOOLTIP.</summary>
		public static class OPTIONS
		{
			public static class MIXING
			{
				public static LocString NAME = "Mixtures of liquids";
				public static LocString TOOLTIP = "Liquids of one group mix in a cell instead of pushing each other aside: water, polluted water, salt water, brine, polluted brine, ethanol, nectar, brackene and ovolene; crude oil, petroleum, naphtha, phyto oil and biodiesel. A mixture freezes and boils by its components, pumps take its liquids in proportion. Liquid flow is then calculated by the mod instead of the game.";
			}
			public static class DENSITY
			{
				public static LocString NAME = "Layers by density";
				public static LocString TOOLTIP = "Liquids settle and fall by density instead of molar mass: the denser liquid sinks. Water is densest near 4 °C, so a cooling pond freezes from the top. Works without mixtures too.";
			}
		}

		/// <summary>Threshold slider of the liquid sensor (LiquidSensorThreshold).</summary>
		public static class SENSOR
		{
			public static LocString THRESHOLD_TITLE = "Share of the liquid";
			public static LocString THRESHOLD_TOOLTIP = "Sends a <b><style=\"logic_on\">Green Signal</style></b> when the selected liquid makes up at least <b>{0}%</b> of the mass in the sensor's cell";
		}

		/// <summary>The shared Mixtures Overlay (src/Common/MixtureUi.cs): the names are the same as in the gas mixture mod.</summary>
		public static class MIXTURES
		{
			public static LocString OVERLAY_NAME = "MIXTURES OVERLAY";
			public static LocString OVERLAY_BUTTON = "Mixtures Overlay";
			public static LocString OVERLAY_TOOLTIP = "Displays the composition of mixtures of gases and liquids";
			/// <summary>Title of the liquids group in the overlay legend.</summary>
			public static LocString GROUP_TITLE = "Liquids";
		}
	}
}
