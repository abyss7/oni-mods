namespace OniPressure
{
	/// <summary>
	/// The mod's own player-facing strings, in English. Translations are loaded from translations/<language code>.po
	/// in the mod folder, if present, to match the game language; none are shipped.
	/// A string's key in a translation file: STRINGS.ONIPRESSURE.<class>.<field>. Shared code hooks the strings
	/// into the game (src/Common/ModLocalization.cs).
	/// </summary>
	public static class ONIPRESSURE
	{
		/// <summary>Mod options (Options.cs): the options dialog looks them up by the keys STRINGS.ONIPRESSURE.OPTIONS.<option>.NAME and .TOOLTIP.</summary>
		public static class OPTIONS
		{
			public static class BYMASS
			{
				public static LocString NAME = "Pressure by gas mass";
				public static LocString TOOLTIP = "How the mass of gas in a cell becomes pressure for boiling and condensation. On: by mass, as everywhere else in the game: one atmosphere is 1.8 kg of any gas in a cell. Off: by molecules, as in physics: a kilogram of hydrogen presses far harder than a kilogram of carbon dioxide, and hot gas presses harder than cold.";
			}
		}
	}
}
