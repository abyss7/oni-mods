using System;

namespace OniPressure
{
	/// <summary>
	/// Pressure in a cell as the game understands it: for a gas, the mass of gas in the cell; for a liquid, the ratio of
	/// the mass to the element's maximum mass per cell (above one: the liquid is compressed by the weight of the column above).
	///
	/// Gas mass is read from the game's ordinary Grid.Mass array, and that is enough in both modes. Without the gas
	/// mixture mod a cell holds one gas, and this is its mass. With the gas mixture mod the game is given the mass of
	/// the cell's whole mixture (the mod adds the other gases to the main gas's mass in the simulation data frame),
	/// while the cell's element stays the main gas. So pressure does not need to know whether the other mod is on.
	///
	/// What this array cannot tell: how much of each gas is in the cell (only the mixture mod knows) and what the
	/// simulation itself sees (its cells still hold only the main gas; native code has no access to the total mass).
	/// </summary>
	internal static class CellPressure
	{
		/// <summary>Mass of the cell's gas in kilograms; zero for vacuum. Not valid for liquids and solids.</summary>
		internal static float Gas(int cell)
		{
			return Grid.Element[cell].IsGas ? Grid.Mass[cell] : 0f;
		}

		/// <summary>How many times the cell's liquid exceeds its maximum mass; zero for anything that is not a liquid.</summary>
		internal static float Liquid(int cell)
		{
			Element element = Grid.Element[cell];
			return element.IsLiquid && element.maxMass > 0f ? Grid.Mass[cell] / element.maxMass : 0f;
		}

		/// <summary>Whether the gas mixture mod is running alongside: only for the log line, does not affect the calculation.</summary>
		internal static bool GasMixPresent()
		{
			foreach (System.Reflection.Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (loaded.GetName().Name == "OniGasMix")
				{
					return true;
				}
			}
			return false;
		}
	}
}
