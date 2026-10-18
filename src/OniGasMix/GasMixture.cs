using System;
using OniCommon;

namespace OniGasMix
{
	/// <summary>
	/// Composition of a gas cell for the game's logic. The game knows one substance and one mass per cell; with the layer the substance
	/// is the main gas, and the mass is shared by the whole mixture. Everything in the game that asked "which gas is in the cell" asks through this
	/// class "how much of gas X is in the cell" or "is it here".
	///
	/// The data comes from the layer's snapshot for the same simulation data frame as the game's cells (like the breathing arrays):
	/// for cells with a mixture the layer returns a list of the non-main gases with their masses; the mass of the main gas is obtained
	/// by subtraction from the cell mass. While the layer is not running, a cell counts as a single gas, as in the game.
	/// </summary>
	internal static class GasMixture
	{
		private const int Header = 4;      // request header, in 4-byte numbers: address of the mass array, counter, spare
		private const int RecordInts = 3;  // record: cell, element, mass

		private static int[] buffer = new int[Header + RecordInts * 16384];
		private static int count;          // records in the snapshot
		private static int[] firstRecord;  // per cell: index of its first record + 1; 0: the cell holds a single gas
		private static bool active;
		private static float[] perMolar;   // 1 / molar mass by element number

		/// <summary>Share of molecules at which a gas counts as present in a cell (mod setting).</summary>
		internal static float PresenceShare = 0.1f;

		private static long fetchTicks;

		internal static long Updates { get; private set; }

		/// <summary>Average cost of fetching the composition per data frame, in microseconds of the main thread.</summary>
		internal static double FetchMicros => Updates > 0 ? fetchTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / Updates : 0;
		internal static int MixedRecords => active ? count : 0;

		internal static void GameLoaded()
		{
			active = false;
			count = 0;
			firstRecord = null;
		}

		/// <summary>On every data frame from the simulation; gameMass identifies the frame, as in GasLayerBreathing.Tick.</summary>
		internal static unsafe void Tick(ulong gameMass)
		{
			try
			{
				int cells = Grid.CellCount;
				if (firstRecord == null || firstRecord.Length != cells)
				{
					firstRecord = new int[cells];
					count = 0;
				}
				if (perMolar == null || perMolar.Length != ElementLoader.elements.Count)
				{
					perMolar = new float[ElementLoader.elements.Count];
					for (int i = 0; i < perMolar.Length; i++)
					{
						float molar = MolarMasses.Of(ElementLoader.elements[i]);
						perMolar[i] = molar > 0f ? 1f / molar : 0f;
					}
				}
				// Marks of the previous frame are cleared by its own records: the whole grid is not walked.
				for (int i = 0; i < count; i++)
				{
					int cell = buffer[Header + i * RecordInts];
					if ((uint)cell < (uint)cells)
					{
						firstRecord[cell] = 0;
					}
				}
				count = 0;
				long began = System.Diagnostics.Stopwatch.GetTimestamp();
				int result = Fetch(gameMass);
				if (result == 3)
				{
					// More records than fit: the layer reported their number, the buffer grows with headroom.
					buffer = new int[Header + RecordInts * (buffer[2] + buffer[2] / 2 + 1024)];
					result = Fetch(gameMass);
				}
				active = result == 0;
				if (!active)
				{
					return;
				}
				count = buffer[2];
				for (int i = count - 1; i >= 0; i--)
				{
					int cell = buffer[Header + i * RecordInts];
					if ((uint)cell < (uint)cells)
					{
						firstRecord[cell] = i + 1;
					}
				}
				Updates++;
				fetchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
			}
			catch (Exception e)
			{
				active = false;
				count = 0;
				Debug.LogWarning($"[OniGasMix] GasMixture: no composition from the layer, cells count as one gas: {e}");
			}
		}

		private static unsafe int Fetch(ulong gameMass)
		{
			fixed (int* start = buffer)
			{
				*(ulong*)start = gameMass;
				start[2] = (buffer.Length - Header) / RecordInts;
				start[3] = 0;
				return SimHooks.Query("gas-mix.frame-gases", (byte*)start, buffer.Length * sizeof(int));
			}
		}

		private static unsafe float RecordMass(int record)
		{
			fixed (int* start = buffer)
			{
				return *(float*)(start + Header + record * RecordInts + 2);
			}
		}

		/// <summary>Mass of the gas with number element in the cell (partial mass). 0 for a non-gas or a foreign substance.</summary>
		internal static float Mass(int cell, ushort element)
		{
			if (!Grid.IsValidCell(cell))
			{
				return 0f;
			}
			ushort main = Grid.ElementIdx[cell];
			int first = active && firstRecord != null ? firstRecord[cell] : 0;
			if (first == 0)
			{
				return main == element ? GasLayerBreathing.MainMass(cell) : 0f;
			}
			float others = 0f, found = 0f;
			for (int i = first - 1; i < count && buffer[Header + i * RecordInts] == cell; i++)
			{
				float mass = RecordMass(i);
				others += mass;
				if ((ushort)buffer[Header + i * RecordInts + 1] == element)
				{
					found = mass;
				}
			}
			return main == element ? GasLayerBreathing.MainMass(cell) : found;
		}

		/// <summary>
		/// Real mass of all the cell's gas. The game sees a gas cell's mass as pressure: the mass of the whole mixture, or by
		/// molecules the mass of Oxygen that would give the same pressure (sim/plugins/gas_mix.cpp, "pressure mode").
		/// </summary>
		internal static float RealTotal(int cell)
		{
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsGas)
			{
				return Grid.IsValidCell(cell) ? Grid.Mass[cell] : 0f;
			}
			float total = 0f;
			Gases gases = Of(cell);
			while (gases.Next(out Element _, out float mass))
			{
				total += mass;
			}
			return total;
		}

		internal static float Mass(int cell, SimHashes gas)
		{
			Element element = ElementLoader.FindElementByHash(gas);
			return element != null ? Mass(cell, element.idx) : 0f;
		}

		/// <summary>
		/// Iterates over the cell's gases without allocating memory: first the non-main gases of the mixture, then the main one.
		/// <code>var gases = GasMixture.Of(cell); while (gases.Next(out Element gas, out float mass)) { ... }</code>
		/// Yields nothing for a cell without gas.
		/// </summary>
		internal struct Gases
		{
			private int cell, record;
			private float others;
			private bool mainLeft;

			internal Gases(int cell)
			{
				this.cell = cell;
				others = 0f;
				mainLeft = Grid.IsValidCell(cell) && Grid.Element[cell].IsGas;
				int first = mainLeft && active && firstRecord != null ? firstRecord[cell] : 0;
				record = first != 0 ? first - 1 : -1;
			}

			internal bool Next(out Element gas, out float mass)
			{
				while (record >= 0 && record < count && buffer[Header + record * RecordInts] == cell)
				{
					int element = buffer[Header + record * RecordInts + 1] & 0xFFFF;
					mass = RecordMass(record);
					record++;
					others += mass;
					if (element < ElementLoader.elements.Count && mass > 0f)
					{
						gas = ElementLoader.elements[element];
						return true;
					}
				}
				record = -1;
				if (mainLeft)
				{
					mainLeft = false;
					gas = Grid.Element[cell];
					mass = GasLayerBreathing.MainMass(cell);
					return mass > 0f;
				}
				gas = null;
				mass = 0f;
				return false;
			}
		}

		/// <summary>
		/// Composition of a cell for other mods, without a reference to this one (they find it in the application domain data):
		/// fills the gases and their masses, returns how many (at most the arrays' length). A cell without a mixture gives its
		/// main gas. Safe to call from worker threads, like the overlay does.
		/// </summary>
		internal const string SharedKey = "OniMods.GasComposition.1";

		internal const string CellsKey = "OniMods.GasMixtureCells.1";

		internal static void Publish()
		{
			AppDomain.CurrentDomain.SetData(SharedKey, (Func<int, ushort[], float[], int>)Composition);
			AppDomain.CurrentDomain.SetData(CellsKey, (Func<int[], int>)MixtureCells);
		}

		/// <summary>
		/// The cells holding a mixture in the current snapshot, for other mods (application domain data "OniMods.GasMixtureCells.1"):
		/// fills the array as far as it goes, returns how many there are (more than the array's length: call again with a
		/// larger one). Main thread.
		/// </summary>
		private static int MixtureCells(int[] into)
		{
			if (!active)
			{
				return 0;
			}
			int found = 0, previous = -1;
			for (int i = 0; i < count; i++)
			{
				int cell = buffer[Header + i * RecordInts];
				if (cell == previous)
				{
					continue;
				}
				previous = cell;
				if (found < into.Length)
				{
					into[found] = cell;
				}
				found++;
			}
			return found;
		}

		private static int Composition(int cell, ushort[] elements, float[] masses)
		{
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsGas)
			{
				return 0;
			}
			int count = 0;
			Gases gases = Of(cell);
			while (count < elements.Length && gases.Next(out Element gas, out float mass))
			{
				elements[count] = gas.idx;
				masses[count] = mass;
				count++;
			}
			return count;
		}

		internal static Gases Of(int cell)
		{
			return new Gases(cell);
		}

		/// <summary>Share of the gas in the cell by number of molecules (the way the layer picks the main gas), from 0 to 1.</summary>
		internal static float Share(int cell, ushort element)
		{
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsGas || perMolar == null)
			{
				return 0f;
			}
			int first = active && firstRecord != null ? firstRecord[cell] : 0;
			if (first == 0)
			{
				return Grid.ElementIdx[cell] == element ? 1f : 0f;
			}
			ushort main = Grid.ElementIdx[cell];
			float others = 0f, molecules = 0f, wanted = 0f;
			for (int i = first - 1; i < count && buffer[Header + i * RecordInts] == cell; i++)
			{
				float mass = RecordMass(i);
				int gas = buffer[Header + i * RecordInts + 1] & 0xFFFF;
				float part = gas < perMolar.Length ? mass * perMolar[gas] : 0f;
				others += mass;
				molecules += part;
				if (gas == element)
				{
					wanted = part;
				}
			}
			float mainPart = GasLayerBreathing.MainMass(cell) * perMolar[main];
			molecules += mainPart;
			if (main == element)
			{
				wanted = mainPart;
			}
			return molecules > 0f ? wanted / molecules : 0f;
		}

		internal static float Share(int cell, SimHashes gas)
		{
			Element element = ElementLoader.FindElementByHash(gas);
			return element != null ? Share(cell, element.idx) : 0f;
		}

		/// <summary>The gas is present in the cell: its share is not below the presence threshold.</summary>
		internal static bool Has(int cell, ushort element)
		{
			return Share(cell, element) >= PresenceShare;
		}

		internal static bool Has(int cell, SimHashes gas)
		{
			return Share(cell, gas) >= PresenceShare;
		}
	}
}
