using System;
using OniCommon;
using UnityEngine;

namespace OniLiquidMix
{
	/// <summary>
	/// Compositions of cells holding a liquid mixture, for the game logic on the main thread (sensors and other cell checks).
	/// The plugin takes them at the end of a tick only if the mod asked since the previous tick (query "liquid-mix.records"):
	/// while there are no checks, no snapshot is taken at all. Here the snapshot is fetched at most once per frame and only when it
	/// is new; a cell without a record is pure: it holds only its main liquid. Shares are by mass.
	/// </summary>
	internal static class LiquidMixture
	{
		/// <summary>From what share by mass a liquid counts as present in a cell, until the player sets their own threshold.</summary>
		internal const float PresenceShare = 0.1f;

		/// <summary>How many liquids a cell holds at most (MixSlots in sim/plugins/liquid_move.h).</summary>
		internal const int Slots = 10;

		/// <summary>Bytes of a "liquid-mix.cell" query: cell, number of liquids, the elements, the shares.</summary>
		internal const int CellQueryBytes = 8 + 8 * Slots;
		internal const int QueryElementAt = 8, QueryShareAt = 8 + 4 * Slots;

		// Header: tick (two ints), how many records fit, how many there are. Record: cell, number of liquids, the
		// elements, the shares.
		private const int Header = 4, RecordInts = 2 + 2 * Slots;

		private static int[] buffer = new int[Header + RecordInts * 256];
		private static int[] firstRecord;
		// Cells marked by the previous snapshot: the marks are cleared through them, the whole grid is not walked.
		private static int[] marked = new int[256];
		private static int count, markedCount, frame = -1;
		private static long tick = -1;
		private static bool failed;

		internal static long Fetches { get; private set; }
		internal static int Records => count;

		private static void Refresh()
		{
			if (frame == Time.frameCount || failed)
			{
				return;
			}
			frame = Time.frameCount;
			if (!LiquidSurvey.Mixing || Grid.CellCount <= 0)
			{
				count = 0;
				return;
			}
			try
			{
				if (firstRecord == null || firstRecord.Length != Grid.CellCount)
				{
					firstRecord = new int[Grid.CellCount];
					count = 0;
					markedCount = 0;
					tick = -1;
				}
				int result = Fetch();
				if (result == 4 || result == 2)
				{
					return;
				}
				if (result == 3)
				{
					// More records than fitted: the plugin reported their number, the buffer grows with headroom.
					buffer = new int[Header + RecordInts * (buffer[3] + buffer[3] / 2 + 256)];
					result = Fetch();
				}
				for (int i = 0; i < markedCount; i++)
				{
					firstRecord[marked[i]] = 0;
				}
				markedCount = 0;
				count = 0;
				if (result != 0)
				{
					tick = -1;
					return;
				}
				count = buffer[3];
				if (marked.Length < count)
				{
					marked = new int[count + count / 2];
				}
				for (int i = count - 1; i >= 0; i--)
				{
					int cell = buffer[Header + i * RecordInts];
					if ((uint)cell < (uint)firstRecord.Length)
					{
						if (firstRecord[cell] == 0)
						{
							marked[markedCount++] = cell;
						}
						firstRecord[cell] = i + 1;
					}
				}
				Fetches++;
			}
			catch (Exception e)
			{
				failed = true;
				count = 0;
				Debug.LogWarning($"{ModContext.Tag} LiquidMixture: no compositions from the plugin, cells count as their main liquid: {e}");
			}
		}

		private static unsafe int Fetch()
		{
			fixed (int* start = buffer)
			{
				*(long*)start = tick;
				start[2] = (buffer.Length - Header) / RecordInts;
				start[3] = 0;
				int result = SimHooks.Query("liquid-mix.records", (byte*)start, buffer.Length * sizeof(int));
				if (result == 0)
				{
					tick = *(long*)start;
				}
				return result;
			}
		}

		private static unsafe float Single(int bits)
		{
			return *(float*)&bits;
		}

		/// <summary>Share of liquid element by mass in the cell; a cell without a mixture: 1 for its substance, 0 for the rest.</summary>
		internal static float Share(int cell, ushort element)
		{
			if (!Grid.IsValidCell(cell))
			{
				return 0f;
			}
			Refresh();
			int record = firstRecord != null && count > 0 ? firstRecord[cell] - 1 : -1;
			if (record < 0)
			{
				return Grid.ElementIdx[cell] == element ? 1f : 0f;
			}
			int at = Header + record * RecordInts;
			int liquids = Math.Min(buffer[at + 1], Slots);
			for (int i = 0; i < liquids; i++)
			{
				if (buffer[at + 2 + i] == element)
				{
					return Single(buffer[at + 2 + Slots + i]);
				}
			}
			return 0f;
		}

		internal const string CompositionKey = "OniMods.LiquidComposition.1", CellsKey = "OniMods.LiquidMixtureCells.1";

		/// <summary>
		/// For other mods, without a reference to this one (application domain data): "OniMods.LiquidComposition.1" fills the
		/// cell's liquids and their masses (kg) and returns how many (a cell without a mixture gives its liquid);
		/// "OniMods.LiquidMixtureCells.1" fills the cells holding a mixture as far as the array goes and returns how many there
		/// are. Main thread.
		/// </summary>
		internal static void Publish()
		{
			AppDomain.CurrentDomain.SetData(CompositionKey, (Func<int, ushort[], float[], int>)Composition);
			AppDomain.CurrentDomain.SetData(CellsKey, (Func<int[], int>)MixtureCells);
		}

		private static readonly float[] shareBuffer = new float[Slots];

		private static int Composition(int cell, ushort[] elements, float[] masses)
		{
			if (!Grid.IsValidCell(cell) || !Grid.Element[cell].IsLiquid)
			{
				return 0;
			}
			int liquids = Liquids(cell, elements, masses.Length >= Slots ? masses : shareBuffer);
			float total = Grid.Mass[cell];
			float[] shares = masses.Length >= Slots ? masses : shareBuffer;
			for (int i = 0; i < liquids && i < masses.Length; i++)
			{
				masses[i] = shares[i] * total;
			}
			return Math.Min(liquids, masses.Length);
		}

		private static int MixtureCells(int[] into)
		{
			Refresh();
			for (int i = 0; i < count && i < into.Length; i++)
			{
				into[i] = buffer[Header + i * RecordInts];
			}
			return count;
		}

		/// <summary>Liquids of the cell with shares by mass (at most four); a cell without a mixture has one, its substance.</summary>
		internal static int Liquids(int cell, ushort[] elements, float[] shares)
		{
			if (!Grid.IsValidCell(cell))
			{
				return 0;
			}
			Refresh();
			int record = firstRecord != null && count > 0 ? firstRecord[cell] - 1 : -1;
			if (record < 0)
			{
				elements[0] = Grid.ElementIdx[cell];
				shares[0] = 1f;
				return 1;
			}
			int at = Header + record * RecordInts;
			int liquids = Math.Min(buffer[at + 1], Math.Min(Slots, elements.Length));
			for (int i = 0; i < liquids; i++)
			{
				elements[i] = (ushort)buffer[at + 2 + i];
				shares[i] = Single(buffer[at + 2 + Slots + i]);
			}
			return liquids;
		}
	}
}
