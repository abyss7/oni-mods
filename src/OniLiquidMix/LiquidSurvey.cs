using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using OniCommon;

namespace OniLiquidMix
{
	/// <summary>
	/// Inventory of the colony's liquids: every 30 seconds of game time the plugin counts liquid cells and pairs of neighbouring cells with different
	/// liquids (sim/plugins/liquid_rules.h, Survey), and once a minute and on exit the mod writes the totals to the log. They
	/// show what the mod's rules will change in this colony before they change anything: where liquids of one
	/// mixing group touch (mixing will happen there) and which layers ordering by density will swap.
	///
	/// Measurement (marker file liquid_measure.txt in the mod folder): how much time the simulation itself spends on liquid
	/// movement and how many liquid cells change per tick. This is the budget for our own liquid movement calculation,
	/// which will replace the game's. The measurement costs time itself (two hooks on every liquid cell every tick and
	/// a comparison of all cells with the previous tick), so it is off in normal play.
	///
	/// Shadow (marker file liquid_shadow.txt): our own flow calculation for a liquid cell (sim/plugins/liquid_move.h)
	/// is compared with the game's on every call; nothing changes in the game. The log gets match counts and examples
	/// of mismatches. It also costs time and is off in normal play.
	/// </summary>
	internal static class LiquidSurvey
	{
		private const int PairsInLog = 12;
		// Simulation functions whose calls are counted during the measurement: mass transfer between cells, the falling
		// drop, displacement of liquid out of a cell, liquid arriving from the game, a leak under pressure, cell swap,
		// layer swap (the last two are called for gases too).
		private static readonly string[] Counted =
		{
			"UpdateNeighbourLiquidMass", "SimEvents::SpawnFallingLiquid", "DisplaceLiquid", "AddLiquid", "DoPressureBreak", "CellSOA::SwapCells", "DoDensityDisplacement",
		};

		internal static bool Measuring { get; private set; }
		internal static bool Shadowing { get; private set; }
		internal static bool OwnMovement { get; private set; }
		internal static bool Mixing { get; private set; }
		internal static bool Density { get; private set; }
		internal static bool Boiling { get; private set; }

		internal static string Mode { get; private set; } = "off: not started";

		internal static void Apply(Harmony harmony)
		{
			if (!SimHooks.Load())
			{
				Mode = "off: sim hook library is not available: " + SimHooks.Info;
				return;
			}
			if (!SimHooks.LoadPlugin("oni_liquidmix.bin", out string error) || !SimHooks.EnablePlugin("liquid-mix", true, out error))
			{
				Mode = "off: liquid mixture plugin: " + error;
				return;
			}
			Measuring = File.Exists(Path.Combine(ModContext.Folder, "liquid_measure.txt"));
			if (Measuring)
			{
				var failed = new List<string>();
				if (!SimHooks.EnablePlugin("liquid-measure", true, out error))
				{
					failed.Add(error);
				}
				foreach (string function in Counted)
				{
					if (!SimHooks.AddProbe(function, out error))
					{
						failed.Add(function + ": " + error);
					}
				}
				if (failed.Count > 0)
				{
					Debug.LogWarning($"{ModContext.Tag} liquid measurement: {string.Join("; ", failed)}");
				}
			}
			Shadowing = File.Exists(Path.Combine(ModContext.Folder, "liquid_shadow.txt"));
			if (Shadowing && !SimHooks.EnablePlugin("liquid-shadow", true, out error))
			{
				Debug.LogWarning($"{ModContext.Tag} liquid shadow: {error}");
				Shadowing = false;
			}
			// Own cell flow instead of the game's: always together with mixtures (mod option), separately for
			// development by the marker file liquid_own.txt. Not switched on together with the shadow (it would have nothing to compare against).
			Options options = Mod.Options ?? new Options();
			OwnMovement = !Shadowing && (options.Mixing || File.Exists(Path.Combine(ModContext.Folder, "liquid_own.txt")));
			if (OwnMovement && !SimHooks.EnablePlugin("liquid-own", true, out error))
			{
				Debug.LogWarning($"{ModContext.Tag} own liquid movement: {error}");
				OwnMovement = false;
			}
			// Mixtures of liquids of one group (mod option): only together with own movement. Layers by density
			// (also an option) sit in the same hook set but work without mixtures too: they have their own flag in the plugin.
			Mixing = OwnMovement && options.Mixing;
			Density = options.DensityLayers;
			if ((Mixing || Density) && !SimHooks.EnablePlugin("liquid-mixing", true, out error))
			{
				Debug.LogWarning($"{ModContext.Tag} liquid mixing and layers: {error}");
				Mixing = false;
				Density = false;
			}
			// Drops by density in the game code; the tooltip, the selected-cell panel line and the "Liquids" group in the shared Mixtures Overlay
			// only with mixtures.
			if (Density)
			{
				LiquidDensity.Apply(harmony);
			}
			if (Mixing)
			{
				LiquidMixUi.Apply(harmony);
				LiquidMixLogic.Apply(harmony);
			}
			Mode = "waiting for the game to load";
			harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(LiquidSurvey), nameof(GameSpawned)));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)), prefix: new HarmonyMethod(typeof(LiquidSurvey), nameof(LogAtExit)));
		}

		private static void GameSpawned(Game __instance)
		{
			try
			{
				bool sent = SimHooks.SendData("liquid-mix.table", LiquidTable.Build());
				sent = sent && SimHooks.SendData("liquid-mix.water-anomaly", LiquidTable.WaterAnomaly());
				sent = sent && SimHooks.SendData("liquid-mix.decomposing", LiquidTable.Decomposing());
				sent = sent && SimHooks.SendData("liquid-mix.heat", LiquidTable.HeatCapacities());
				SimHooks.SetValue("liquid-mix.survey", sent ? 1 : 0);
				SimHooks.SetValue("liquid-mix.measure", sent && Measuring ? 1 : 0);
				SimHooks.SetValue("liquid-mix.shadow", sent && Shadowing ? 1 : 0);
				SimHooks.SetValue("liquid-mix.own", sent && OwnMovement ? 1 : 0);
				SimHooks.SetValue("liquid-mix.mixing", sent && Mixing ? 1 : 0);
				SimHooks.SetValue("liquid-mix.density", sent && Density ? 1 : 0);
				// This mod handles boiling of a mixture by composition only if the pressure mod is absent: with it, mixtures boil by the phase rules.
				Boiling = SimHooks.GetValue("pressure.active") < 0;
				SimHooks.SetValue("liquid-mix.boil", Boiling ? 1 : 0);
				Mode = sent ? "on" : "off: the plugin rejected the liquid table";
			}
			catch (Exception e)
			{
				SimHooks.SetValue("liquid-mix.survey", 0);
				Mode = "off: " + e.Message;
				Debug.LogWarning($"{ModContext.Tag} liquid survey failed to start: {e}");
			}
			Debug.Log($"{ModContext.Tag} liquid survey: {Mode}, measurement of the game's own liquid movement {(Measuring ? "on" : "off")}, shadow of our own movement {(Shadowing ? "on" : "off")}, our own movement instead of the game's {(OwnMovement ? "on" : "off")}, mixtures {(Mixing ? "on" : "off")}, layers by density {(Density ? "on" : "off")} (falling drops by density {LiquidDensity.Mode}){(Mixing ? (Boiling ? ", boiling by composition here" : ", boiling by the pressure mod's phase rules") : "")}; {SimHooks.Info}; mixing groups: {LiquidTable.Summary}");
			__instance.gameObject.AddOrGet<LiquidSurveyLog>();
		}

		private static string Name(ushort element)
		{
			return element < ElementLoader.elements.Count ? ElementLoader.elements[element].id.ToString() : element.ToString();
		}

		internal static unsafe void LogStats()
		{
			if (!SimHooks.Available)
			{
				return;
			}
			if (SimHooks.GetValue("liquid-mix.surveys") <= 0)
			{
				LogMeasurement();
				LogShadow(false);
				return;
			}
			// Plugin reply: number of records, then 12-byte records (two elements, pair kind, number of pairs).
			const int capacity = 64, record = 12;
			byte[] buffer = new byte[4 + capacity * record];
			BitConverter.GetBytes(capacity).CopyTo(buffer, 0);
			var lines = new[] { new List<string>(), new List<string>(), new List<string>() };
			if (SimHooks.Query("liquid-mix.survey-pairs", buffer) == 0)
			{
				int count = BitConverter.ToInt32(buffer, 0);
				for (int i = 0; i < count; i++)
				{
					int at = 4 + i * record;
					ushort first = BitConverter.ToUInt16(buffer, at), second = BitConverter.ToUInt16(buffer, at + 2);
					int kind = BitConverter.ToInt32(buffer, at + 4), pairs = BitConverter.ToInt32(buffer, at + 8);
					if (kind < 0 || kind > 2 || lines[kind].Count >= PairsInLog)
					{
						continue;
					}
					// For a "dense over light" pair the lower liquid is written first.
					lines[kind].Add(kind == 2 ? $"{Name(second)} over {Name(first)} {pairs}" : $"{Name(first)} and {Name(second)} {pairs}");
				}
			}
			long surveys = SimHooks.GetValue("liquid-mix.surveys");
			Debug.Log($"{ModContext.Tag} liquid survey: {SimHooks.GetValue("liquid-mix.cells")} liquid cells ({SimHooks.GetValue("liquid-mix.group-cells")} of liquids that can mix), " +
				$"{SimHooks.GetValue("liquid-mix.kilograms") / 1000f:F1} t; pairs of neighbouring cells: {SimHooks.GetValue("liquid-mix.fronts")} where liquids would mix [{string.Join(", ", lines[0])}], " +
				$"{SimHooks.GetValue("liquid-mix.foreign-contacts")} of liquids that never mix [{string.Join(", ", lines[1])}], " +
				$"{SimHooks.GetValue("liquid-mix.inversions")} with the denser liquid above (layers that ordering by density would swap) [{string.Join(", ", lines[2])}]; " +
				$"{surveys} surveys, {SimHooks.GetValue("liquid-mix.survey-micros") / (float)surveys / 1000f:F2} ms each, sim ticks {SimHooks.GetValue("liquid-mix.ticks")}");
			LogMeasurement();
			LogShadow(false);
			LogMixing();
			if (!Mixing)
			{
				Debug.Log($"{ModContext.Tag} liquids of the mixing groups (no mixtures): {GroupTotals()}");
			}
		}

		/// <summary>Mixtures: how many cells are mixed, what the game was kept from doing, how much of each group liquid there is.</summary>
		private static void LogMixing()
		{
			long ticks = SimHooks.GetValue("liquid-mix.ticks");
			if (!Mixing && Density)
			{
				Debug.Log($"{ModContext.Tag} layers by density (no mixtures): denser liquid moved below a lighter one {SimHooks.GetValue("liquid-mix.layers-swapped")} times; " +
					$"water convection by density with the 4 °C anomaly: mixed {SimHooks.GetValue("liquid-mix.convections")}, cold water kept on top {SimHooks.GetValue("liquid-mix.convections-stable")}");
			}
			if (!Mixing || ticks <= 0)
			{
				return;
			}
			Debug.Log($"{ModContext.Tag} liquid mixtures: {SimHooks.GetValue("liquid-mix.mixed-cells")} cells hold a mixture; " +
				$"exchanges between neighbours {SimHooks.GetValue("liquid-mix.exchanges") / (double)ticks:F1} per tick, mixing pass {SimHooks.GetValue("liquid-mix.mixing-micros") / 1000.0 / ticks:F3} ms per tick; " +
				$"drops split by composition {SimHooks.GetValue("liquid-mix.drop-pieces")}, lost {SimHooks.GetValue("liquid-mix.drop-lost-grams")} g; " +
				$"the game was kept from pushing a miscible liquid {SimHooks.GetValue("liquid-mix.pushes-refused")} times and from swapping miscible layers {SimHooks.GetValue("liquid-mix.layers-kept")} times; " +
				$"compositions moved with cells swapped by the game {SimHooks.GetValue("liquid-mix.swaps-followed")} times; " +
				$"liquid added by the game (drops, pipes, buildings) mixed into a cell of its group {SimHooks.GetValue("liquid-mix.adds-mixed")} times, the main liquid added to a mixed cell {SimHooks.GetValue("liquid-mix.adds-to-mixture")} times; " +
				$"pumps picked their liquid by composition {SimHooks.GetValue("liquid-mix.pump-picks")} times, a liquid that is not the main one in any cell around {SimHooks.GetValue("liquid-mix.pump-picks-minor")} times; " +
				$"taken from mixed cells {SimHooks.GetValue("liquid-mix.takes")} times, {SimHooks.GetValue("liquid-mix.taken-grams") / 1000.0:F1} kg; " +
				$"the game's own displacement carried compositions {SimHooks.GetValue("liquid-mix.displaced-mixed")} times, pushing into the cell beyond {SimHooks.GetValue("liquid-mix.pushed-mixed")} times, push-ups {SimHooks.GetValue("liquid-mix.push-ups")} times, wall break checks by mixture capacity {SimHooks.GetValue("liquid-mix.breaks-scaled")}; " +
				$"liquids of mixtures frozen out on their own {SimHooks.GetValue("liquid-mix.frozen-out")} times ({SimHooks.GetValue("liquid-mix.frozen-grams") / 1000.0:F1} kg), boiled out {SimHooks.GetValue("liquid-mix.boiled-out")} times ({SimHooks.GetValue("liquid-mix.boiled-grams") / 1000.0:F1} kg), turned into another liquid of their group when heated {SimHooks.GetValue("liquid-mix.converted-out")} times, boiling held for lack of room {SimHooks.GetValue("liquid-mix.transitions-held")}, checks for a 5 kg flash of vapour next to a hot cell skipped in mixtures {SimHooks.GetValue("liquid-mix.flashes-blocked")}; " +
				$"denser liquid moved below a lighter one {SimHooks.GetValue("liquid-mix.layers-swapped")} times; " +
				$"water convection by density with the 4 °C anomaly: mixed {SimHooks.GetValue("liquid-mix.convections")}, cold water kept on top {SimHooks.GetValue("liquid-mix.convections-stable")}; " +
				$"cells restored from the save {SimHooks.GetValue("liquid-mix.restored-cells")}; " +
				$"heat exchanges of mixed cells by the mixture's heat capacity {SimHooks.GetValue("liquid-mix.heat-scaled")}, liquids of cells converted by game logic {SimHooks.GetValue("liquid-mix.conversions")}; " +
				$"game logic by composition: {LiquidMixLogic.Stats()}; " +
				$"leftover compositions removed: when liquid came into an empty cell {SimHooks.GetValue("liquid-mix.fresh-fills")}, by the periodic pass {SimHooks.GetValue("liquid-mix.stale-cleared")}; liquids of the mixing groups: {GroupTotals()}");
		}

		/// <summary>How much of each mixing group liquid there is in all cells, by the plugin's latest count.</summary>
		private static string GroupTotals()
		{
			var totals = new List<string>();
			byte[] buffer = new byte[8 + 64 * 16];
			BitConverter.GetBytes(64).CopyTo(buffer, 0);
			if (SimHooks.Query("liquid-mix.group-totals", buffer) == 0)
			{
				int count = BitConverter.ToInt32(buffer, 0);
				for (int i = 0; i < count; i++)
				{
					int element = BitConverter.ToInt32(buffer, 8 + i * 16);
					double kilograms = BitConverter.ToDouble(buffer, 8 + i * 16 + 8);
					totals.Add($"{Name((ushort)element)} {kilograms / 1000.0:F3} t");
				}
			}
			return string.Join(", ", totals);
		}

		/// <summary>Comparison of our own calculation with the game's: numbers every time, mismatch examples on game exit.</summary>
		internal static void LogShadow(bool withSamples)
		{
			long calls = SimHooks.GetValue("liquid-mix.shadow-calls");
			if (!Shadowing || calls <= 0)
			{
				return;
			}
			long mismatch = SimHooks.GetValue("liquid-mix.shadow-mismatch"), withGas = SimHooks.GetValue("liquid-mix.shadow-with-gas");
			Debug.Log($"{ModContext.Tag} liquid shadow over {calls} calls of the game's liquid cell update: exact {SimHooks.GetValue("liquid-mix.shadow-exact")}, " +
				$"differing in the last digits only {SimHooks.GetValue("liquid-mix.shadow-close")}, mismatches {mismatch} ({mismatch * 100.0 / calls:F4}%); " +
				$"calls where liquid pushed gas out {withGas}, mismatches among them {SimHooks.GetValue("liquid-mix.shadow-mismatch-with-gas")}; " +
				$"mismatches where our requests (transfers, drops) differ from the game's {SimHooks.GetValue("liquid-mix.shadow-request-mismatch")}; " +
				$"transfers {SimHooks.GetValue("liquid-mix.shadow-transfers")}, falling drops {SimHooks.GetValue("liquid-mix.shadow-spawns")}; " +
				$"our update and the comparison took {SimHooks.GetValue("liquid-mix.shadow-micros") * 1000.0 / calls:F0} ns per call");
			if (!withSamples)
			{
				return;
			}
			byte[] buffer = new byte[32 * 1024];
			if (SimHooks.Query("liquid-mix.shadow-samples", buffer) != 0)
			{
				return;
			}
			int length = Array.IndexOf(buffer, (byte)0);
			string text = Encoding.ASCII.GetString(buffer, 0, length < 0 ? buffer.Length : length);
			foreach (string line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
			{
				// In the examples, elements are written as indices ("e12"): the log gets their names.
				Debug.Log($"{ModContext.Tag} liquid shadow sample: " + Regex.Replace(line, @"\be(\d+)\b", match => Name(ushort.Parse(match.Groups[1].Value))));
			}
		}

		private static void LogAtExit()
		{
			LogStats();
			LogShadow(true);
		}

		private static void LogMeasurement()
		{
			long ticks = SimHooks.GetValue("liquid-mix.measured-ticks");
			long cycles = SimHooks.GetValue("liquid-mix.tick-cycles");
			if (!Measuring || ticks <= 0 || cycles <= 0)
			{
				return;
			}
			// CPU cycles are converted to milliseconds using the time of the sim ticks themselves, taken both ways.
			double millisPerCycle = SimHooks.GetValue("liquid-mix.tick-micros") / 1000.0 / cycles;
			long updates = SimHooks.GetValue("liquid-mix.update-liquid-calls"), pushes = SimHooks.GetValue("liquid-mix.pressure-displacement-calls");
			double updateMillis = SimHooks.GetValue("liquid-mix.update-liquid-cycles") * millisPerCycle;
			double pushMillis = SimHooks.GetValue("liquid-mix.pressure-displacement-cycles") * millisPerCycle;
			var calls = new List<string>();
			foreach (KeyValuePair<string, long> probe in SimHooks.ProbeCalls())
			{
				if (Array.IndexOf(Counted, probe.Key) >= 0)
				{
					calls.Add($"{probe.Key} {probe.Value / (double)ticks:F1}");
				}
			}
			Debug.Log($"{ModContext.Tag} liquid measurement over {ticks} sim ticks: the sim tick itself {SimHooks.GetValue("liquid-mix.tick-micros") / 1000.0 / ticks:F2} ms; " +
				$"UpdateLiquid {updates / (double)ticks:F0} calls and {updateMillis / ticks:F3} ms per tick ({(updates > 0 ? updateMillis * 1e6 / updates : 0):F0} ns per call, timer included); " +
				$"DoLiquidPressureDisplacement {pushes / (double)ticks:F0} calls and {pushMillis / ticks:F3} ms per tick; " +
				$"liquid cells changed per tick {SimHooks.GetValue("liquid-mix.changed-cells") / (double)ticks:F0}, of them liquid appeared, vanished or was replaced {SimHooks.GetValue("liquid-mix.appeared-cells") / (double)ticks:F1}; " +
				$"calls per tick: {string.Join(", ", calls)}; cells moved by our own update {SimHooks.GetValue("liquid-mix.own-calls") / (double)ticks:F0} per tick; comparing the cells took {SimHooks.GetValue("liquid-mix.compare-micros") / 1000.0 / ticks:F2} ms per tick outside the tick time");
		}
	}

	/// <summary>Writes the inventory (and the measurement, if on) to the log once per minute of game time.</summary>
	internal sealed class LiquidSurveyLog : KMonoBehaviour, ISim4000ms
	{
		private int ticks;

		public void Sim4000ms(float dt)
		{
			if (++ticks % 15 == 0)
			{
				LiquidSurvey.LogStats();
			}
		}
	}
}
