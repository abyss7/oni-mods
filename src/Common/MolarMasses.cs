using System;
using System.Collections.Generic;

namespace OniCommon
{
	/// <summary>
	/// Molar masses for the pressure by molecules. The game's data give Oxygen, Polluted Oxygen, Hydrogen and Chlorine the
	/// masses of their atoms, half those of their molecules; with them a kilogram of Oxygen would give twice its real pressure,
	/// and Oxygen would weigh as much as Natural Gas. In the pressure mod's "by molecules" mode the mods take the molecules'
	/// masses from here; in the "by mass" mode, and without the pressure mod, the game's data stay as they are.
	///
	/// The mode is shared through the application domain data (each mod has its own copy of this code): the pressure mod
	/// sets it when loading, the others read it in-game.
	/// </summary>
	internal static class MolarMasses
	{
		private const string SharedKey = "OniMods.PressureByMolecules.1";

		private static readonly Dictionary<SimHashes, float> Molecules = new Dictionary<SimHashes, float>
		{
			{ SimHashes.Oxygen, 31.998f },
			{ SimHashes.ContaminatedOxygen, 31.998f },
			{ SimHashes.Hydrogen, 2.016f },
			{ SimHashes.ChlorineGas, 70.906f },
		};

		/// <summary>Called by the pressure mod with its setting.</summary>
		internal static void PublishMode(bool byMolecules)
		{
			AppDomain.CurrentDomain.SetData(SharedKey, byMolecules);
		}

		/// <summary>Whether the pressure is counted by molecules (the pressure mod is on with that setting).</summary>
		internal static bool ByMolecules => AppDomain.CurrentDomain.GetData(SharedKey) is bool on && on;

		/// <summary>The molecule's molar mass: from the table, otherwise from the game's data.</summary>
		internal static float Real(Element element)
		{
			return Molecules.TryGetValue(element.id, out float molar) ? molar : element.molarMass;
		}

		/// <summary>The molar mass in the current mode: the molecule's by molecules, the game's otherwise.</summary>
		internal static float Of(Element element)
		{
			return ByMolecules ? Real(element) : element.molarMass;
		}
	}
}
