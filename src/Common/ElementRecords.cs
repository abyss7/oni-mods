using System;
using System.IO;

namespace OniCommon
{
	/// <summary>
	/// Properties of the game's elements for the native plugins (sim/plugins/element_table.h): the simulation has its own tables, but
	/// they are harder for the plugins to reach. The record is the same for the gas mixture layer plugin and the pressure plugin;
	/// each mod sends it to its own plugin.
	/// </summary>
	internal static class ElementRecords
	{
		/// <summary>
		/// 40 bytes per element, indexed by element number: molar mass, specific heat capacity, state, flags
		/// (breathable; emits gas), what the element turns into when cooled (element number or 0xFFFF) and below
		/// which temperature, what drops out as a separate substance when the gas condenses (number or 0xFFFF) and which
		/// share of the mass, thermal conductivity, what the element turns into when heated, what stays solid in that case,
		/// above which temperature and which share of the mass.
		/// </summary>
		internal static byte[] Build(Func<Element, bool> breathable)
		{
			var elements = ElementLoader.elements;
			using (MemoryStream stream = new MemoryStream(elements.Count * 40))
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				foreach (Element element in elements)
				{
					writer.Write(element.molarMass);
					writer.Write(element.specificHeatCapacity);
					writer.Write((byte)(element.state & Element.State.Solid));
					// Flags: 1 means "breathable", 2: a solid or liquid emits gas into the neighbouring cell
					// (the element has sublimateId set in the table; the game itself decides this the same way for the simulation).
					bool emitsGas = !element.IsGas && element.sublimateId != 0 && ElementLoader.FindElementByHash(element.sublimateId) != null;
					writer.Write((byte)((breathable(element) ? 1 : 0) | (emitsGas ? 2 : 0)));
					Element cold = element.lowTempTransition;
					bool condenses = element.IsGas && cold != null && cold.IsLiquid;
					writer.Write(cold != null ? cold.idx : ushort.MaxValue);
					writer.Write(element.lowTemp);
					// Transition "ore": for Sour Gas a third of the mass drops out as Sulfur.
					Element ore = condenses && element.lowTempTransitionOreID != SimHashes.Vacuum && element.lowTempTransitionOreID != 0 && element.lowTempTransitionOreMassConversion > 0f
						? ElementLoader.FindElementByHash(element.lowTempTransitionOreID) : null;
					writer.Write(ore != null ? ore.idx : ushort.MaxValue);
					writer.Write((ushort)0);
					writer.Write(ore != null ? element.lowTempTransitionOreMassConversion : 0f);
					writer.Write(element.thermalConductivity);
					// Transition when heated and its solid residue (Salt for Salt Water): for evaporation of liquids.
					Element hot = element.highTempTransition;
					Element residue = hot != null && element.highTempTransitionOreID != SimHashes.Vacuum && element.highTempTransitionOreID != 0 && element.highTempTransitionOreMassConversion > 0f
						? ElementLoader.FindElementByHash(element.highTempTransitionOreID) : null;
					writer.Write(hot != null ? hot.idx : ushort.MaxValue);
					writer.Write(residue != null ? residue.idx : ushort.MaxValue);
					writer.Write(element.highTemp);
					writer.Write(residue != null ? element.highTempTransitionOreMassConversion : 0f);
				}
				return stream.ToArray();
			}
		}
	}
}
