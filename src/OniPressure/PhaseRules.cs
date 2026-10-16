using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;

namespace OniPressure
{
	/// <summary>
	/// Boiling, evaporation and condensation by pressure, with latent heat (rules: sim/plugins/phase_rules.h).
	///
	/// Native code computes them on the simulation thread, and where exactly depends on whether the gas mixture mod is present:
	///   - no mixture mod: a cell holds one gas, and the pressure plugin computes the rules in its own pass over cells;
	///   - the mixture mod moves gases: the cells hold mixtures, and its layer computes the rules by partial pressures
	///     (the layer plugin gets the value gas-mix.phase set); the own pass is not enabled;
	///   - the mixture mod is loaded but does not move gases: this is its debug state, and it may turn movement on
	///     later, next to our own pass (together they would compute the rules twice). The rules are off.
	/// In the first two cases the pressure plugin forbids the simulation its own transitions between liquid and gas:
	/// otherwise it would convert a whole cell based on temperature alone.
	///
	/// The mode is chosen when the game loads: by then all mods are loaded, and the value gas-mix.move shows
	/// what the mixture mod does. The blocking hooks are installed earlier, when the mod loads, and change
	/// nothing until the mode is chosen.
	/// </summary>
	internal static class PhaseRules
	{
		private static bool cellsHooked;
		internal static string Mode { get; private set; } = "off: not started";

		// What share of the way to equilibrium a non-boiling liquid covers per pass; what share of the heat above the
		// boiling point a boiling liquid gives to vapour per pass; condensate portions smaller than this are not released (kg).
		private const float EvaporationRate = 0.1f, BoilRate = 0.5f, Portion = 0.01f;

		/// <summary>
		/// Real substance data instead of estimates from the boiling point (Trouton's rule, critical temperature 1.65
		/// times the boiling point): the ratio of molar heat of vaporisation to R * T0, critical temperature (K), heat
		/// of vaporisation (kJ/kg) and the pressure at which the substance boils at the game's boiling point (atm).
		/// Carbon Dioxide's boiling point in the game (225 K) is made up: under one atmosphere it is never liquid,
		/// and at 225 K its vapour pressure is 7.3 atm; above 304 K no pressure liquefies it. The heat is given per
		/// kilogram separately because the game's molar masses of Oxygen, Hydrogen and Chlorine are atomic, half
		/// the real ones.
		/// </summary>
		private static readonly (string gas, float ratio, float critical, float latent, float reference)[] Substances =
		{
			("Steam", 13.1f, 647.1f, 2257f, 1f),
			("CarbonDioxide", 8.8f, 304.1f, 348f, 7.3f),
			("Oxygen", 9.1f, 154.6f, 213f, 1f),
			("Hydrogen", 5.2f, 33.2f, 446f, 1f),
			("Methane", 8.8f, 190.6f, 510f, 1f),
			("ChlorineGas", 10.3f, 417f, 288f, 1f),
			("EthanolGas", 13.2f, 514f, 838f, 1f),
			("Propane", 9.8f, 369.8f, 428f, 1f),
			("Helium", 2.4f, 5.2f, 20.7f, 1f),
		};

		/// <summary>Phase rules settings block for the plugins (sim/plugins/element_table.h, PhaseConfigHeader and PhaseSubstance).</summary>
		private static byte[] Settings()
		{
			var known = new List<(ushort gas, float ratio, float critical, float latent, float reference)>();
			foreach ((string gas, float ratio, float critical, float latent, float reference) in Substances)
			{
				if (Enum.TryParse(gas, out SimHashes hash) && ElementLoader.FindElementByHash(hash) is Element element)
				{
					known.Add((element.idx, ratio, critical, latent, reference));
				}
			}
			// One atmosphere in "by mass" mode: the game's maximum gas mass per cell (1.8 kg).
			Element air = ElementLoader.FindElementByHash(SimHashes.Oxygen);
			using (MemoryStream stream = new MemoryStream())
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				writer.Write(Mod.Options.PressureByMass ? 1 : 0);
				writer.Write(air != null && air.maxMass > 0f ? air.maxMass : 1.8f);
				writer.Write(EvaporationRate);
				writer.Write(BoilRate);
				writer.Write(Portion);
				writer.Write(known.Count);
				foreach ((ushort gas, float ratio, float critical, float latent, float reference) in known)
				{
					writer.Write((int)gas);
					writer.Write(ratio);
					writer.Write(critical);
					writer.Write(latent);
					writer.Write(reference);
				}
				return stream.ToArray();
			}
		}

		internal static void Apply(Harmony harmony)
		{
			if (!SimHooks.Load())
			{
				Mode = "off: sim hook library is not available: " + SimHooks.Info;
				return;
			}
			if (!SimHooks.LoadPlugin("oni_pressure.bin", out string error) || !SimHooks.EnablePlugin("pressure", true, out error))
			{
				Mode = "off: pressure plugin: " + error;
				return;
			}
			Mode = "waiting for the game to load";
			harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(PhaseRules), nameof(GameSpawned)));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)), prefix: new HarmonyMethod(typeof(PhaseRules), nameof(LogStats)));
		}

		private static void GameSpawned(Game __instance)
		{
			try
			{
				Choose();
			}
			catch (Exception e)
			{
				SimHooks.SetValue("pressure.active", 0);
				Mode = "off: " + e.Message;
				Debug.LogWarning($"{ModContext.Tag} phase rules failed to start: {e}");
			}
			Debug.Log($"{ModContext.Tag} phase rules: {Mode}; {SimHooks.Info}");
			__instance.gameObject.AddOrGet<PhaseRulesLog>();
		}

		private static void Choose()
		{
			bool on = false;
			byte[] settings = Settings();
			string scale = Mod.Options.PressureByMass ? "pressure by gas mass" : "pressure by molecules";
			if (!SimHooks.SendData("pressure.elements", ElementRecords.Build(element => false)) || !SimHooks.SendData("pressure.phase-config", settings))
			{
				Mode = "off: the plugin rejected the element table or the settings";
			}
			else
			{
				// -1: the mixture layer plugin is not in the game; 0: loaded but does not move gases; 1: moves them.
				long mixture = SimHooks.GetValue("gas-mix.move");
				if (mixture == 1)
				{
					on = SimHooks.SendData("gas-mix.phase-config", settings) && SimHooks.SetValue("gas-mix.phase", 1);
					Mode = on ? $"on, computed by the gas mixture layer (partial pressures), {scale}" : "off: the gas mixture mod is too old for the phase rules";
				}
				else if (mixture == 0)
				{
					Mode = "off: the gas mixture mod is loaded but does not move gases";
				}
				else
				{
					if (!cellsHooked)
					{
						cellsHooked = SimHooks.EnablePlugin("pressure-cells", true, out string error);
						if (!cellsHooked)
						{
							Mode = "off: own pass: " + error;
						}
					}
					on = cellsHooked && SimHooks.SetValue("pressure.cells", 1);
					if (on)
					{
						Mode = $"on, own pass over cells with a single gas, {scale}";
					}
				}
			}
			SimHooks.SetValue("pressure.active", on ? 1 : 0);
		}

		internal static void LogStats()
		{
			if (!SimHooks.Available)
			{
				return;
			}
			string text = $"{ModContext.Tag} phase rules: {Mode}; the sim was not allowed to liquefy a whole cell of cold gas {SimHooks.GetValue("pressure.blocked-condensations")} times, " +
				$"to turn a whole cell of hot liquid into gas {SimHooks.GetValue("pressure.blocked-boilings")} times, to throw 5 kg of gas out of a liquid next to a hot cell {SimHooks.GetValue("pressure.blocked-flashes")} times";
			long passes = SimHooks.GetValue("pressure.passes");
			if (passes > 0)
			{
				text += $"; own pass: {passes} passes, {SimHooks.GetValue("pressure.pass-micros") / (double)passes:F0} us each; " +
					$"evaporated {SimHooks.GetValue("pressure.evaporated-grams") / 1000.0:F2} kg in {SimHooks.GetValue("pressure.evaporations")} portions (boiling in {SimHooks.GetValue("pressure.boilings")}, " +
					$"whole cells of liquid gone {SimHooks.GetValue("pressure.evaporated-whole")}, another gas pushed out for the steam {SimHooks.GetValue("pressure.displaced-for-steam")} times); " +
					$"condensed {SimHooks.GetValue("pressure.condensed-grams") / 1000.0:F2} kg in {SimHooks.GetValue("pressure.condensations")} portions " +
					$"(straight into the same liquid below {SimHooks.GetValue("pressure.pooled")}, falling drops {SimHooks.GetValue("pressure.drips")}, liquid right in the cell {SimHooks.GetValue("pressure.in-place")}, frost {SimHooks.GetValue("pressure.frost-grams") / 1000.0:F2} kg, " +
					$"solid residue and ore {SimHooks.GetValue("pressure.ore-grams") / 1000.0:F2} kg, lost {SimHooks.GetValue("pressure.lost-grams") / 1000.0:F3} kg)";
			}
			if (SimHooks.GetValue("gas-mix.phase") == 1)
			{
				text += $"; in the gas mixture layer: evaporated {SimHooks.GetValue("gas-mix.evaporated-grams") / 1000.0:F2} kg in {SimHooks.GetValue("gas-mix.evaporations")} portions " +
					$"(boiling in {SimHooks.GetValue("gas-mix.boilings")}, whole cells of liquid gone {SimHooks.GetValue("gas-mix.evaporated-whole")}, solid residue {SimHooks.GetValue("gas-mix.evaporated-ore-grams") / 1000.0:F2} kg); " +
					$"condensed {SimHooks.GetValue("gas-mix.condensed-grams") / 1000.0:F2} kg in {SimHooks.GetValue("gas-mix.condensed")} portions " +
					$"(straight into the same liquid below {SimHooks.GetValue("gas-mix.condensed-pooled")}, falling drops {SimHooks.GetValue("gas-mix.condensed-drips")}, liquid right in the cell {SimHooks.GetValue("gas-mix.condensed-in-place")}, frost {SimHooks.GetValue("gas-mix.frost-grams") / 1000.0:F2} kg, " +
					$"lost {SimHooks.GetValue("gas-mix.condensed-lost-grams") / 1000.0:F3} kg)";
			}
			Debug.Log(text);
			string elements = ByElement("pressure.phase-elements") + ByElement("gas-mix.phase-elements");
			if (elements.Length > 0)
			{
				Debug.Log($"{ModContext.Tag} phase rules by substance (kg since the game was loaded):{elements}");
			}
			long ownTicks = SimHooks.GetValue("pressure.ticks");
			if (ownTicks > 0)
			{
				Debug.Log($"{ModContext.Tag} sim tick with the own pass: {SimHooks.GetValue("pressure.tick-micros") / (double)ownTicks:F0} us ({ownTicks} ticks)");
			}
			LogDiagnostics();
		}

		private static string Name(int element)
		{
			return element >= 0 && element < ElementLoader.elements.Count ? ElementLoader.elements[element].id.ToString() : element.ToString();
		}

		private static string Place(int cell)
		{
			if (cell < 0 || !Grid.IsValidCell(cell))
			{
				return "?";
			}
			Grid.CellToXY(cell, out int x, out int y);
			return $"{x},{y}";
		}

		/// <summary>
		/// Survey of the mixture layer (query gas-mix.diagnostics): where and on which substance the layer loses gas, at what
		/// temperatures each substance condenses and evaporates and where the largest portion is, how many cells
		/// changed state. Everything since the start of the game.
		/// </summary>
		private static void LogDiagnostics()
		{
			const int Header = 8 * 16 + 4, EntrySize = 40;
			byte[] buffer = new byte[Header + EntrySize * 200];
			if (SimHooks.GetValue("gas-mix.phase") != 1 || SimHooks.Query("gas-mix.diagnostics", buffer) != 0)
			{
				return;
			}
			string[] states = { "vacuum", "gas", "liquid", "solid" };
			var changes = new List<string>();
			for (int was = 0; was < 4; was++)
			{
				for (int now = 0; now < 4; now++)
				{
					long count = BitConverter.ToInt64(buffer, 8 * (was * 4 + now));
					if (count > 0)
					{
						changes.Add($"{states[was]} to {states[now]} {count}");
					}
				}
			}
			var losses = new List<KeyValuePair<float, string>>();
			var phases = new List<KeyValuePair<float, string>>();
			var places = new List<KeyValuePair<float, int>>();
			var became = new List<KeyValuePair<float, string>>();
			int entries = BitConverter.ToInt32(buffer, 8 * 16);
			for (int i = 0; i < entries && i < 200; i++)
			{
				int at = Header + i * EntrySize;
				int kind = BitConverter.ToInt32(buffer, at), element = BitConverter.ToInt32(buffer, at + 4), count = BitConverter.ToInt32(buffer, at + 12), boiling = BitConverter.ToInt32(buffer, at + 16);
				float mass = BitConverter.ToSingle(buffer, at + 8), gasTemperature = BitConverter.ToSingle(buffer, at + 20), liquidTemperature = BitConverter.ToSingle(buffer, at + 24), biggest = BitConverter.ToSingle(buffer, at + 28);
				int cell = BitConverter.ToInt32(buffer, at + 32), other = BitConverter.ToInt32(buffer, at + 36);
				if (kind == 0)
				{
					losses.Add(new KeyValuePair<float, string>(mass, $" closed by {Name(element)}: {mass:F3} kg in {count} cells, the largest {biggest:F3} kg of {Name(other)} at {Place(cell)}"));
				}
				else if (kind == 1)
				{
					places.Add(new KeyValuePair<float, int>(mass, cell));
					phases.Add(new KeyValuePair<float, string>(mass, $" {Name(element)} condensed {mass:F1} kg in {count} portions, gas at {gasTemperature:F1} K on average, drops left at {liquidTemperature:F1} K, the largest {biggest:F3} kg at {Place(cell)}"));
				}
				else if (kind == 2)
				{
					places.Add(new KeyValuePair<float, int>(mass, cell));
					phases.Add(new KeyValuePair<float, string>(mass, $" {Name(element)} evaporated {mass:F1} kg in {count} portions ({boiling} boiling), liquid at {liquidTemperature:F1} K and gas at {gasTemperature:F1} K on average, the largest {biggest:F3} kg at {Place(cell)}"));
				}
				else
				{
					became.Add(new KeyValuePair<float, string>(count, $" {Name(element)} {count}"));
				}
			}
			losses.Sort((a, b) => b.Key.CompareTo(a.Key));
			phases.Sort((a, b) => b.Key.CompareTo(a.Key));
			became.Sort((a, b) => b.Key.CompareTo(a.Key));
			Debug.Log($"{ModContext.Tag} diagnostics, cells that changed state: {string.Join(", ", changes)}; became:{string.Join(",", became.GetRange(0, Math.Min(12, became.Count)).ConvertAll(line => line.Value))}");
			Debug.Log($"{ModContext.Tag} diagnostics, gas lost by the layer:{(losses.Count > 0 ? string.Join(";", losses.GetRange(0, Math.Min(8, losses.Count)).ConvertAll(line => line.Value)) : " nothing")}");
			Debug.Log($"{ModContext.Tag} diagnostics, phase rules:{string.Join(";", phases.GetRange(0, Math.Min(14, phases.Count)).ConvertAll(line => line.Value))}");
			// What lies around the places of the largest portions for the three most active transitions.
			places.Sort((a, b) => b.Key.CompareTo(a.Key));
			var shown = new HashSet<int>();
			foreach (KeyValuePair<float, int> place in places)
			{
				if (shown.Count >= 3)
				{
					break;
				}
				if (Grid.IsValidCell(place.Value) && shown.Add(place.Value))
				{
					Debug.Log($"{ModContext.Tag} diagnostics, around {Place(place.Value)}:{Around(place.Value)}");
				}
			}
		}

		/// <summary>
		/// Cells around the given one, in rows from top to bottom: element (first letters), mass in kilograms and temperature;
		/// for gas, the mass of the whole mixture. Buildings and world at the end.
		/// </summary>
		private static string Around(int center)
		{
			const int Reach = 3;
			Grid.CellToXY(center, out int centerX, out int centerY);
			var text = new System.Text.StringBuilder();
			var buildings = new HashSet<string>();
			for (int y = centerY + Reach; y >= centerY - Reach; y--)
			{
				text.Append($" [y={y}]");
				for (int x = centerX - Reach; x <= centerX + Reach; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell))
					{
						text.Append(" -");
						continue;
					}
					Element element = Grid.Element[cell];
					string name = element.id.ToString();
					text.Append($" {(name.Length > 6 ? name.Substring(0, 6) : name)}:{Grid.Mass[cell]:G3}@{Grid.Temperature[cell]:F0}");
					foreach (ObjectLayer layer in new[] { ObjectLayer.Building, ObjectLayer.FoundationTile })
					{
						UnityEngine.GameObject thing = Grid.Objects[cell, (int)layer];
						if (thing != null)
						{
							buildings.Add(thing.PrefabID().Name);
						}
					}
				}
			}
			return $"{text}; buildings: {(buildings.Count > 0 ? string.Join(", ", buildings) : "none")}; world {Grid.WorldIdx[center]}, the active one is {ClusterManager.Instance.activeWorldId}";
		}

		/// <summary>How much of each gas condensed and of each liquid evaporated, by descending mass.</summary>
		private static string ByElement(string query)
		{
			byte[] buffer = new byte[4 + 12 * 64];
			if (SimHooks.GetValue(query.StartsWith("gas-mix") ? "gas-mix.phase" : "pressure.cells") != 1 || SimHooks.Query(query, buffer) != 0)
			{
				return "";
			}
			int count = BitConverter.ToInt32(buffer, 0);
			var lines = new List<KeyValuePair<float, string>>();
			for (int i = 0; i < count && i < 64; i++)
			{
				int element = BitConverter.ToInt32(buffer, 4 + i * 12);
				float condensed = BitConverter.ToSingle(buffer, 8 + i * 12), evaporated = BitConverter.ToSingle(buffer, 12 + i * 12);
				string name = element >= 0 && element < ElementLoader.elements.Count ? ElementLoader.elements[element].id.ToString() : element.ToString();
				if (condensed > 0f)
				{
					lines.Add(new KeyValuePair<float, string>(condensed, $" {name} condensed {condensed:F2}"));
				}
				if (evaporated > 0f)
				{
					lines.Add(new KeyValuePair<float, string>(evaporated, $" {name} evaporated {evaporated:F2}"));
				}
			}
			lines.Sort((a, b) => b.Key.CompareTo(a.Key));
			return string.Join(",", lines.ConvertAll(line => line.Value));
		}
	}

	/// <summary>Logs the phase rules counters once per minute of game time.</summary>
	internal sealed class PhaseRulesLog : KMonoBehaviour, ISim4000ms
	{
		private int ticks;

		public void Sim4000ms(float dt)
		{
			if (++ticks % 15 == 0)
			{
				PhaseRules.LogStats();
			}
		}
	}
}
