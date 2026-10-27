namespace OniGasMix
{
	/// <summary>
	/// The mod's own strings that the player sees. The text here is English; translations are loaded from the mod folder,
	/// translations/<language code>.po (src/OniGasMix/translations in the sources), if present, for the game's language; none are shipped.
	/// String key in a translation file: STRINGS.ONIGASMIX.<class>.<field>. The strings are hooked into the game by common code
	/// (src/Common/ModLocalization.cs).
	/// </summary>
	public static class ONIGASMIX
	{
		public static class GASMIXTURE
		{
			// The overlay is shared with the liquid mixtures mod (src/Common/MixtureUi.cs): both mods use the same names.
			public static LocString OVERLAY_NAME = "MIXTURES OVERLAY";
			public static LocString OVERLAY_BUTTON = "Mixtures Overlay";
			public static LocString OVERLAY_TOOLTIP = "Displays the composition of mixtures of gases and liquids";
			/// <summary>Title of the gas group in the overlay list.</summary>
			public static LocString GROUP_TITLE = "Gases";
			// Legend of a selected gas: how much of the cell it makes up (by molecules, like the tooltip's percentages).
			public static LocString PURE = "Pure";
			public static LocString PURE_TOOLTIP = "The whole cell is {0}";
			public static LocString HALF = "Half the Mixture";
			public static LocString HALF_TOOLTIP = "{0} makes up half of the cell's gas molecules";
			public static LocString TRACES = "Traces";
			public static LocString TRACES_TOOLTIP = "A noticeable trace of {0}: less than 1% of the mixture";
			public static LocString ABSENT = "Absent";
			public static LocString ABSENT_TOOLTIP = "No {0} in this cell";
		}

		/// <summary>Gas sensor window: the threshold slider (GasSensorThreshold.cs).</summary>
		public static class SENSOR
		{
			public static LocString THRESHOLD_TITLE = "Share of the gas";
			public static LocString THRESHOLD_TOOLTIP = "Sends a <b><style=\"logic_on\">Green Signal</style></b> when the selected gas makes up at least <b>{0}%</b> of the molecules in the sensor's cell";
		}

		/// <summary>Plants and the air around them (GasLayerPlants.cs).</summary>
		public static class PLANTS
		{
			public static LocString OXYFERN_INTAKE_NAME = "Short of Carbon Dioxide: {rate}";
			public static LocString OXYFERN_INTAKE_TOOLTIP = "This plant takes in " + STRINGS.UI.PRE_KEYWORD + "Carbon Dioxide" + STRINGS.UI.PST_KEYWORD + " and gives " + STRINGS.UI.PRE_KEYWORD + "Oxygen" + STRINGS.UI.PST_KEYWORD + " at <b>{rate}</b> of its full rate: the air around it holds <b>{co2}</b> of Carbon Dioxide.\n\nIt works from {from} and at its full rate from {full}. It does not wilt for the lack of Carbon Dioxide.";
		}

		/// <summary>Mod settings (Options.cs): the settings window looks them up by the keys STRINGS.ONIGASMIX.OPTIONS.<setting>.NAME and .TOOLTIP.</summary>
		public static class OPTIONS
		{
			public static class MOVEGASES
			{
				public static LocString NAME = "The layer moves gases";
				public static LocString TOOLTIP = "On: the simulation's own gas movement is switched off; gases mix, spread by pressure and settle by weight under the layer's rules, and the cursor tooltip, the overlay, breathing, sources and consumers work with the composition of the mixture. Off: the layer only follows the gases and checks itself against the simulation without changing anything (for diagnostics).";
			}

			public static class SORTBYAMOUNT
			{
				public static LocString NAME = "Gases in the mixtures overlay by amount";
				public static LocString TOOLTIP = "On: the gases in the mixtures overlay list go from the most plentiful in the world to the least. Off: by name.";
			}

			public static class PRESENCEPERCENT
			{
				public static LocString NAME = "Share at which a gas counts as present, %";
				public static LocString TOOLTIP = "Checks that ask whether a certain gas is in a cell (the gas sensor, critters that need or eat a gas, sterile atmosphere for food, the mask station) treat the gas as present when its share of molecules in the mixture is at least this value. Lower values make such checks react to small admixtures.";
			}

			public static class CATEGORY
			{
				public static LocString RULES = "Gas movement rules";
			}

			public static class PRESSURE
			{
				public static LocString NAME = "Pressure equalization";
				public static LocString TOOLTIP = "The share of the mass difference between two neighbouring cells that flows per simulation tick. Higher values make gas rush into empty space faster.";
			}

			public static class DIFFUSION
			{
				public static LocString NAME = "Mixing of gases";
				public static LocString TOOLTIP = "How fast different gases blend into each other at equal pressure. At zero, gases mix only where pressure pushes them.";
			}

			public static class GRAVITY
			{
				public static LocString NAME = "Settling by weight";
				public static LocString TOOLTIP = "How fast a gas heavier than the mixture below it sinks. Higher values give sharper layers of light and heavy gases.";
			}

			public static class SPREAD
			{
				public static LocString NAME = "Spreading along ceilings and floors";
				public static LocString TOOLTIP = "How fast a light gas under a ceiling or a heavy gas on a floor flows sideways. This is what lets hydrogen find a gap in a ceiling.";
			}

			public static class CONDENSEGRAMS
			{
				public static LocString NAME = "Smallest amount that condenses, g";
				public static LocString TOOLTIP = "A gas that is not the main one in its cell turns into liquid below its condensation point only when the cell holds at least this much of it. Smaller amounts stay as vapour.";
			}
		}
	}
}
