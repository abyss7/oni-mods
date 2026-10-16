namespace OniCommon
{
	/// <summary>
	/// The common code of our mods (folder src/Common) is compiled into each mod separately: the mods do not depend on each other
	/// and work in any combination. Here is what the common code needs to know about the mod it is compiled into:
	/// the mod folder (it holds the mod's marker files, libraries and translations) and the name for log lines.
	/// Filled in first thing in Mod.OnLoad.
	/// </summary>
	internal static class ModContext
	{
		internal static string Folder;
		internal static string Name = "Mod";

		/// <summary>The mod's tag in the game log: "[OniFixes]".</summary>
		internal static string Tag => "[" + Name + "]";
	}
}
