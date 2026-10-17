using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using OniCommon;

namespace OniGasMix
{
	/// <summary>
	/// Gas mixture composition in the interface. The game shows one gas per cell: with the mixture layer that is the cell's main gas
	/// and its mass. Here all gases of the cell are added to that, with their shares by number of molecules (the same shares pick
	/// the main gas; this is exactly the share in the pressure).
	///
	/// The game shows the cell mass itself, and it is now the mass of the whole mixture (see GasLayerBreathing.cs on pressure),
	/// so the mixture title carries no mass: there used to be an addition "+X" to the main gas's mass that
	/// the game showed.
	///
	/// The tooltip under the cursor, in the cell's substance box (without masses of individual gases: they jump from kilograms
	/// to micrograms and get in the way of reading):
	///
	///   – Mixture
	///        61% Oxygen
	///        33% Hydrogen
	///         6% Carbon Dioxide
	///
	/// The selected-cell panel, as a separate status line (masses are shown here):
	///
	///   Mixture
	///    61% Oxygen: 1.2 kg
	///    33% Hydrogen: 100 g
	///     6% Carbon Dioxide: 500 g
	///
	/// There are no own strings to translate: the words "Mixture" and "Miscellaneous", gas names, mass units and the percent sign
	/// are taken from the game's strings in its current language.
	///
	/// The composition is requested from the layer with "gas-mix.cell" (sim/plugins/gas_mix.cpp). The masses of non-main gases are unknown
	/// to the game; the main gas's mass is taken from the game's cell, so that the line matches the one the game draws itself.
	/// Works only when the layer moves gas itself. The lines are drawn and placed by the common mixtures interface (src/Common/MixtureUi.cs);
	/// it takes the gas group's lines from here.
	/// </summary>
	internal static class GasLayerHover
	{
		private const int MaxGases = 16;
		private const int Header = 16;           // cell, "may wait", number of gases, temperature
		private const int HoverLines = 6, PanelLines = 12;
		private const float MinShare = 0.0005f;  // gases with a share below 0.05% of molecules go into one "Miscellaneous" line
		private const int SampleCells = 2000;    // how many cells Sample() looks through: it is called from the main thread

		private struct Gas
		{
			public Element element;
			public float mass, molecules;
		}

		/// <summary>
		/// Composition of one cell, remembering the previous request. The tooltip and the cell panel have different cells, so
		/// each has its own instance.
		/// </summary>
		private sealed class Mixture
		{
			public readonly List<Gas> gases = new List<Gas>(); // by descending number of molecules
			public float totalMass, totalMolecules;
			public float otherMass;                             // mass of all gases except the main one (the one the game shows)
			public int version;                                 // grows when the composition has been reread

			private readonly byte[] buffer = new byte[Header + MaxGases * 8];
			private int cachedCell = -1;
			private long cachedTick = -1;
			private float cachedMass = -1f;

			/// <summary>How many leading gases make up a noticeable share; fewer than two means "the cell is not a mixture".</summary>
			public int Noticeable(int limit)
			{
				int count = 0;
				while (count < gases.Count && count < limit && gases[count].molecules >= MinShare * totalMolecules)
				{
					count++;
				}
				return count;
			}

			public void Read(int cell)
			{
				if (!Grid.IsValidCell(cell) || !(Grid.Element[cell].IsGas || Grid.Element[cell].IsVacuum))
				{
					if (gases.Count > 0 || cachedCell != -1)
					{
						gases.Clear();
						cachedCell = -1;
						version++;
					}
					return;
				}
				// The composition changes once per simulation tick, and the main gas's mass also between ticks: while the cell,
				// the tick and this mass are the same, there is nothing to reread.
				long tick = SimHooks.GetValue("gas-mix.ticks");
				float mainMass = GasLayerBreathing.MainMass(cell);
				if (cell == cachedCell && tick == cachedTick && mainMass == cachedMass)
				{
					return;
				}
				// The layer may be busy with a step (a few milliseconds). For a new cell we wait so the text does not flicker;
				// for the previous one we keep the old composition until next time.
				Array.Clear(buffer, 0, Header);
				buffer[0] = (byte)cell;
				buffer[1] = (byte)(cell >> 8);
				buffer[2] = (byte)(cell >> 16);
				buffer[3] = (byte)(cell >> 24);
				buffer[4] = (byte)(cell != cachedCell ? 1 : 0);
				int result = SimHooks.Query("gas-mix.cell", buffer);
				if (result == 1)
				{
					return;
				}
				cachedCell = cell;
				cachedTick = tick;
				cachedMass = mainMass;
				version++;
				gases.Clear();
				totalMass = totalMolecules = otherMass = 0f;
				if (result != 0)
				{
					return;
				}
				int count = Math.Min(BitConverter.ToInt32(buffer, 8), MaxGases);
				int mainIndex = Grid.ElementIdx[cell];
				for (int i = 0; i < count; i++)
				{
					int index = BitConverter.ToInt32(buffer, Header + i * 8);
					if (index < 0 || index >= ElementLoader.elements.Count)
					{
						continue;
					}
					Element element = ElementLoader.elements[index];
					float mass = index == mainIndex ? mainMass : BitConverter.ToSingle(buffer, Header + i * 8 + 4);
					float molar = MolarMasses.Of(element);
					if (!(mass > 0f) || !(molar > 0f))
					{
						continue;
					}
					gases.Add(new Gas { element = element, mass = mass, molecules = mass / molar });
					totalMass += mass;
					totalMolecules += mass / molar;
					if (index != mainIndex)
					{
						otherMass += mass;
					}
				}
				gases.Sort((a, b) => b.molecules.CompareTo(a.molecules));
			}
		}

		private static readonly Mixture hover = new Mixture(), panel = new Mixture();
		private static readonly List<string> hoverLines = new List<string>();
		private static int hoverVersion = -1, panelVersion = -1;
		private static string panelText = "";

		/// <summary>Tooltip lines about the cell's composition; empty if the cell is not a mixture. The list is shared and must not be modified.</summary>
		internal static List<string> HoverLinesFor(int cell)
		{
			hover.Read(cell);
			if (hover.version == hoverVersion)
			{
				return hoverLines;
			}
			hoverVersion = hover.version;
			hoverLines.Clear();
			int shown = hover.Noticeable(HoverLines);
			if (shown < 2)
			{
				// One gas (traces of the others): the game's box has already said everything.
				return hoverLines;
			}
			hoverLines.Add(Title(hover));
			float rest = 0f;
			for (int i = 0; i < hover.gases.Count; i++)
			{
				if (i < shown)
				{
					hoverLines.Add(Share(hover.gases[i].molecules / hover.totalMolecules) + " " + Name(hover.gases[i].element));
				}
				else
				{
					rest += hover.gases[i].molecules;
				}
			}
			if (rest > 0f)
			{
				hoverLines.Add(Share(rest / hover.totalMolecules) + " " + (string)STRINGS.MISC.TAGS.OTHER);
			}
			return hoverLines;
		}

		// ---------- Selected-cell panel ----------

		/// <summary>Status line text about the cell's composition; empty if the cell is not a mixture.</summary>
		internal static string PanelText(int cell)
		{
			panel.Read(cell);
			if (panel.version == panelVersion)
			{
				return panelText;
			}
			panelVersion = panel.version;
			int shown = panel.Noticeable(PanelLines);
			if (shown < 2)
			{
				return panelText = "";
			}
			StringBuilder text = new StringBuilder(Title(panel));
			float restMass = 0f, restMolecules = 0f;
			for (int i = 0; i < panel.gases.Count; i++)
			{
				Gas gas = panel.gases[i];
				if (i < shown)
				{
					text.Append('\n').Append(Share(gas.molecules / panel.totalMolecules)).Append(' ').Append(Name(gas.element))
						.Append(": ").Append(GameUtil.GetFormattedMass(gas.mass));
				}
				else
				{
					restMass += gas.mass;
					restMolecules += gas.molecules;
				}
			}
			if (restMass > 0f)
			{
				text.Append('\n').Append(Share(restMolecules / panel.totalMolecules)).Append(' ').Append((string)STRINGS.MISC.TAGS.OTHER)
					.Append(": ").Append(GameUtil.GetFormattedMass(restMass));
			}
			return panelText = text.ToString();
		}

		// ---------- Common pieces of text ----------

		private static string Title(Mixture mixture)
		{
			return STRINGS.MISC.TAGS.MIXTURE;
		}

		private static string Name(Element element)
		{
			return STRINGS.UI.StripLinkFormatting(element.name);
		}

		private static string Share(float share)
		{
			return MixtureUi.Share(share);
		}

		/// <summary>
		/// For the log: the tooltip and panel text for the first cell found where the mixture has at least three gases.
		/// </summary>
		internal static string Sample()
		{
			int step = Math.Max(1, Grid.CellCount / SampleCells);
			for (int cell = 0; cell < Grid.CellCount; cell += step)
			{
				if (!Grid.Element[cell].IsGas)
				{
					continue;
				}
				List<string> text = HoverLinesFor(cell);
				if (text.Count >= 4)
				{
					Grid.CellToXY(cell, out int x, out int y);
					return $"cell {cell} ({x}, {y}), shown by the game as {Grid.Element[cell].id} {Grid.Mass[cell]:F3} kg; hover: {string.Join(" | ", text)}; panel: {PanelText(cell).Replace("\n", " | ")}";
				}
			}
			return "no cell with three or more gases found";
		}
	}
}
