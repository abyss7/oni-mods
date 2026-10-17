namespace OniFixes
{
	/// <summary>
	/// The mod's own strings that the player sees, in English. The mod loads translations from
	/// translations/<language code>.po in the mod folder if present (none are shipped) and picks the game language.
	/// Key of a string in a translation file: STRINGS.ONIFIXES.<class>.<field>, for example
	/// STRINGS.ONIFIXES.OPTIONS.CATEGORY.FIXES. To add a string, a field here is enough; a translation file
	/// without an entry for it keeps the English text. The strings are hooked into the game by shared code
	/// (src/Common/ModLocalization.cs).
	/// </summary>
	public static class ONIFIXES
	{
		/// <summary>
		/// Mod settings (Options.cs): the name and tooltip of each setting and the category names. The settings window
		/// looks them up in the game's string table by the keys STRINGS.ONIFIXES.OPTIONS.<setting>.NAME and .TOOLTIP.
		/// </summary>
		public static class OPTIONS
		{
			public static class CATEGORY
			{
				public static LocString RENDERING = "Rendering";
				public static LocString DUPLICANTS = "Duplicants";
				public static LocString FIXES = "Fixes";
			}

			public static class REMEMBERTOOLSETTINGS
			{
				public static LocString NAME = "Keep tool and overlay settings in the save";
				public static LocString TOOLTIP = "Tool modes (deconstruct only buildings and the like), the lists in overlay legends (materials, temperature mode, germs) are written to the save file and come back after loading. Which overlay was open is not kept. A save opened without the mod loads as usual.";
			}

			public static class QUICKPILLS
			{
				public static LocString NAME = "Quick pills";
				public static LocString TOOLTIP = "Duplicants take a pill in 2 seconds instead of 10: they sit down, swallow it and get up, instead of sitting frozen with puffed cheeks. The pill works the same.";
			}

			public static class REDRAWONANIMSTART
			{
				public static LocString NAME = "Redraw animations when they start";
				public static LocString TOOLTIP = "Fixes \"stuck\" frames on buildings: the game sometimes does not redraw a building after its animation changes until the cursor hovers over it (the water of a shower, for example).";
			}

			public static class CONVEYORDEPTHGUARD
			{
				public static LocString NAME = "Watch the depth of items on conveyors";
				public static LocString TOOLTIP = "If an item riding a conveyor ends up at the wrong depth and is drawn over the rail, it is put back in place and the case is written to the game log.";
			}

			public static class CONVEYOROVERLAYITEMS
			{
				public static LocString NAME = "Show items in standing conveyor boxes";
				public static LocString TOOLTIP = "In the conveyor overlay a stopped item disappeared and its box looked empty. With this setting the item is always visible and stays under the frame of the box.";
			}

			public static class CONVEYORARRIVALS
			{
				public static LocString NAME = "Items ride into the receptacle together with their box";
				public static LocString TOOLTIP = "A receptacle takes the item one step before its box arrives, so the box rode the last cell empty. With this setting the picture of the item arrives together with the box. Conveyor logic is not affected.";
			}

			public static class DEMANDDRIVENFETCH
			{
				public static LocString NAME = "Fast search for items to carry";
				public static LocString TOOLTIP = "A Duplicant looks only for the kinds of items its errands need instead of going through every item in the colony on each update. The choice stays the same: the nearest suitable item by path.";
			}

			public static class TRIPAWAREFETCH
			{
				public static LocString NAME = "Consider the delivery point when choosing an item";
				public static LocString TOOLTIP = "Works together with the fast search. An item is chosen by the length of the whole trip (to the item and from there to the delivery point), not by how close it is to the Duplicant. The errand itself is rated the same way when distance decides. Errand priorities do not change.";
			}

			public static class THROTTLEPROPERTYTEXTURES
			{
				public static LocString NAME = "Do not update cell textures without new data";
				public static LocString TOOLTIP = "Fog of war, liquids, mass and other service textures are updated only when the simulation has delivered new data, the camera has moved or the world has changed. On a heavy colony a frame is about 4.5% shorter.";
			}

			public static class PROPERTYTEXTURESMAXSTALEFRAMES
			{
				public static LocString NAME = "Texture lag limit, frames";
				public static LocString TOOLTIP = "After this many frames the computed textures (fog of war, digging, mass) are recalculated in any case. A lower value gives smoother fog reveal and digging progress but a smaller gain.";
			}

			public static class CACHEDSYMBOLOVERRIDES
			{
				public static LocString NAME = "Do not rebuild Duplicant clothes and hair every frame";
				public static LocString TOOLTIP = "The full layout of a Duplicant's symbol overrides is done only when its look or animation set has changed. The face (blinking, speech, head turns) is updated as before.";
			}

			public static class NATIVEPROPERTYTEXTURES
			{
				public static LocString NAME = "Compute cell textures with native code";
				public static LocString TOOLTIP = "Fog of war, digging and mass are filled by a C++ kernel instead of the game's C# code. The picture is the same. If the native library did not load, the game's code is used.";
			}

			public static class ASYNCPROPERTYTEXTURES
			{
				public static LocString NAME = "Compute cell textures in background threads (experiment)";
				public static LocString TOOLTIP = "Texture computation does not hold up the frame; the result is shown one frame later. On 4 cores the gain is about 1%; more cores were not tested.";
			}
		}
	}
}
