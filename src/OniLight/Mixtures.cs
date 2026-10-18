using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// What cells holding a mixture let through, by their composition: the gas mixture mod (OniGasMix) and the liquid mixture
	/// mod (OniLiquidMix) keep compositions the game does not know (for the game a cell is its main substance and the whole
	/// mass). Each part of a mixture dims light by its own absorption and mass, so a cell lets through exp(-sum of absorb x
	/// mass). The mods give compositions and the list of mixed cells through the application domain data (no reference to
	/// them); without them nothing is done here. A gas mixture with nothing in it that dims light is left alone (clear as
	/// any gas). The table is redone every Period seconds; the lights with such cells in their area are traced again just as
	/// often (LightGrid), so they take the new table.
	/// </summary>
	internal static class Mixtures
	{
		internal const float Period = 1f;

		private static Func<int, ushort[], float[], int> gasComposition, liquidComposition;
		private static Func<int[], int> gasCells, liquidCells;
		private static float[] absorb = new float[0], molarRatio = new float[0];
		private static readonly List<int> set = new List<int>();
		private static int[] cells = new int[4096];
		private static readonly ushort[] elements = new ushort[16];
		private static readonly float[] masses = new float[16];
		private static float last = -1f;

		/// <summary>By cell: what a cell holding a mixture lets through, NaN for other cells. Null without the mixture mods.</summary>
		internal static float[] Through { get; private set; }
		internal static int GasCells { get; private set; }
		internal static int LiquidCells { get; private set; }
		internal static long Updates { get; private set; }

		internal static void Initialise(LightNative.ElementOptics[] optics)
		{
			gasComposition = AppDomain.CurrentDomain.GetData("OniMods.GasComposition.1") as Func<int, ushort[], float[], int>;
			gasCells = AppDomain.CurrentDomain.GetData("OniMods.GasMixtureCells.1") as Func<int[], int>;
			liquidComposition = AppDomain.CurrentDomain.GetData("OniMods.LiquidComposition.1") as Func<int, ushort[], float[], int>;
			liquidCells = AppDomain.CurrentDomain.GetData("OniMods.LiquidMixtureCells.1") as Func<int[], int>;
			if (gasCells == null || gasComposition == null)
			{
				gasCells = null;
			}
			if (liquidCells == null || liquidComposition == null)
			{
				liquidCells = null;
			}
			set.Clear();
			last = -1f;
			Updates = 0;
			GasCells = LiquidCells = 0;
			absorb = new float[optics.Length];
			molarRatio = new float[optics.Length];
			for (int i = 0; i < optics.Length; i++)
			{
				absorb[i] = optics[i].absorb;
				molarRatio[i] = optics[i].molarRatio;
			}
			if (gasCells == null && liquidCells == null)
			{
				Through = null;
				return;
			}
			Through = new float[Grid.CellCount];
			for (int i = 0; i < Through.Length; i++)
			{
				Through[i] = float.NaN;
			}
		}

		internal static string Sources => (gasCells != null ? "gases" : "") + (gasCells != null && liquidCells != null ? " and " : "") +
			(liquidCells != null ? "liquids" : "") + (gasCells == null && liquidCells == null ? "none" : "");

		/// <summary>Redoes the table if Period has passed (or always, with force).</summary>
		internal static void Update(bool force = false)
		{
			if (Through == null || (!force && last >= 0f && Time.unscaledTime < last + Period))
			{
				return;
			}
			last = Time.unscaledTime;
			Updates++;
			foreach (int cell in set)
			{
				Through[cell] = float.NaN;
			}
			set.Clear();
			GasCells = Fill(gasCells, gasComposition, gas: true);
			LiquidCells = Fill(liquidCells, liquidComposition, gas: false);
		}

		/// <summary>
		/// For the self-check: what a gas or liquid cell should let through as it is now, from its composition if a mixture
		/// mod gives one, otherwise from its element and real mass.
		/// </summary>
		internal static float Expected(int cell)
		{
			Element element = Grid.Element[cell];
			Func<int, ushort[], float[], int> composition = element.IsGas ? gasComposition : element.IsLiquid ? liquidComposition : null;
			float depth = 0f;
			if (composition != null)
			{
				int parts = composition(cell, elements, masses);
				for (int k = 0; k < parts; k++)
				{
					depth += (elements[k] < absorb.Length ? absorb[elements[k]] : 0f) * masses[k];
				}
				return Mathf.Exp(-depth);
			}
			int idx = Grid.ElementIdx[cell];
			float mass = Grid.Mass[cell];
			if (element.IsGas && idx < molarRatio.Length && molarRatio[idx] > 0f)
			{
				mass *= molarRatio[idx] * 293.15f / Mathf.Max(Grid.Temperature[cell], 1f);
			}
			return Mathf.Exp(-(idx < absorb.Length ? absorb[idx] : 0f) * mass);
		}

		private static int Fill(Func<int[], int> list, Func<int, ushort[], float[], int> composition, bool gas)
		{
			if (list == null)
			{
				return 0;
			}
			int count = list(cells);
			if (count > cells.Length)
			{
				cells = new int[count + count / 2];
				count = Math.Min(list(cells), cells.Length);
			}
			int written = 0;
			for (int i = 0; i < count; i++)
			{
				int cell = cells[i];
				if ((uint)cell >= (uint)Through.Length)
				{
					continue;
				}
				int parts = composition(cell, elements, masses);
				float depth = 0f;
				bool dims = false;
				for (int k = 0; k < parts; k++)
				{
					float a = elements[k] < absorb.Length ? absorb[elements[k]] : 0f;
					if (a > 0f)
					{
						depth += a * masses[k];
						dims = true;
					}
				}
				if (parts > 0 && (dims || !gas))
				{
					Through[cell] = Mathf.Exp(-depth);
					set.Add(cell);
					written++;
				}
			}
			return written;
		}
	}
}
