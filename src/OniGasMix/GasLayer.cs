using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;

namespace OniGasMix
{
	/// <summary>
	/// Gas mixture layer (in development): every gas cell stores the mass of each gas separately, and the simulation
	/// cell shows the layer's projection, i.e. the main gas (by number of molecules) and its mass. The layer itself lives
	/// in the native hook library, inside the simulation tick (sim/plugins/gas_mix.cpp); here is its enabling,
	/// passing of element properties and output of figures.
	///
	/// Modes:
	/// - mirror (setting "The layer moves gases" off): the layer changes nothing in the game, it only follows the gases
	///   and checks its projection against the simulation cell;
	/// - the layer moves gas itself (setting "The layer moves gases" on): the simulation's own gas movement
	///   is switched off, the layer moves gas by four rules (pressure, diffusion, gravity, spreading
	///   along ceilings and floors) and writes
	///   the projection into the cells. Duplicants breathe by the cell's composition (GasLayerBreathing.cs); emitted gas
	///   is added to the cell's mixture, and consumers of a specific gas (scrubber, plants) take it from the mixture too
	///   (hooks in gas_mix.cpp). The cell mass the game sees is the mass of the whole mixture (pressure for
	///   buildings and sensors). Pumps pick gas by the composition of the mixture around them. Geysers still look at the cell's
	///   main gas; only the main gas condenses and freezes.
	/// </summary>
	internal static class GasLayer
	{
		private const int LogEveryTicks = 150;

		private static bool elementsSent, move;
		private static int ticksSinceLog;
		private static int ticksAfterLoad = -1; // ticks after the game was loaded; -1: the early log entry has already been made
		private static bool pressureOn = true;   // mixture pressure for the game is on (switched off only by an AbTest comparison)

		/// <summary>The layer moves gas itself (rather than only following it).</summary>
		internal static bool Moving => move;

		/// <summary>
		/// The game is loaded or started anew. Its element table may have changed, so it is sent to the layer again;
		/// a few ticks later the layer's figures are written to the log (including what it restored from the save).
		/// </summary>
		internal static void GameLoaded()
		{
			elementsSent = false;
			ticksAfterLoad = 0;
			ticksSinceLog = 0;
			GasLayerBreathing.GameLoaded();
			GasMixture.GameLoaded();
			GasLayerCases.GameLoaded();
		}

		internal static void Apply(Harmony harmony)
		{
			// Hooks are installed now, while the mod loads: the simulation is not computing yet.
			if (!SimHooks.Load())
			{
				throw new InvalidOperationException("sim hook library is not available: " + SimHooks.Info);
			}
			// The layer plugin lives in its own library: the mod carries only it and the hook engine shared by our mods.
			if (!SimHooks.LoadPlugin("oni_gasmix.bin", out string error) || !SimHooks.EnablePlugin("gas-mix", true, out error))
			{
				throw new InvalidOperationException("gas-mix plugin: " + error);
			}
			// Numbers of the movement rules from the mod settings: five 4-byte numbers.
			Options options = Mod.Options;
			float[] rules = { options.Pressure, options.Diffusion, options.Gravity, options.Spread, Math.Max(1, options.CondenseGrams) / 1000f };
			byte[] packed = new byte[rules.Length * sizeof(float)];
			Buffer.BlockCopy(rules, 0, packed, 0, packed.Length);
			if (!SimHooks.SendData("gas-mix.params", packed))
			{
				Debug.LogWarning("[OniGasMix] GasLayer: the plugin rejected the movement rules from the settings, its defaults are used");
			}
			move = Mod.Options.MoveGases;
			GasMixture.PresenceShare = Math.Max(1, Math.Min(50, Mod.Options.PresencePercent)) / 100f;
			if (move)
			{
				// Only one party may move gas: with this switch the plugin itself turns off gas movement in the simulation.
				SimHooks.SetValue("gas-mix.move", 1);
				// The game shows only the cell's main gas. The mixture composition is added to the tooltip under the cursor
				// and to the selected-cell panel, and the oxygen overlay gets a gas selection. A failure in these
				// interface patches must not get in the way of the layer itself.
				GasMixture.Publish();
				foreach ((string name, Action<Harmony> apply) in new (string, Action<Harmony>)[] { (nameof(GasLayerOverlay), GasLayerOverlay.Apply), (nameof(GasLayerBreathing), GasLayerBreathing.Apply), (nameof(GasLayerLogic), GasLayerLogic.Apply), (nameof(GasLayerCases), GasLayerCases.Apply), (nameof(GasLayerPlantCases), GasLayerPlantCases.Apply) })
				{
					try
					{
						apply(harmony);
					}
					catch (Exception e)
					{
						Debug.LogWarning($"[OniGasMix] {name} failed to apply: {e}");
					}
				}
			}
			harmony.Patch(AccessTools.Method(typeof(Sim), nameof(Sim.HandleMessage)),
				postfix: new HarmonyMethod(typeof(GasLayer), nameof(OnSimMessage)));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)),
				prefix: new HarmonyMethod(typeof(GasLayer), nameof(LogStats)));
			// The layer's state in the game's save, and the layer reset on every game load.
			GasLayerSave.Apply(harmony);
			Debug.Log($"[OniGasMix] GasLayer: {(move ? "layer moves gas" : "mirror mode")}, {SimHooks.Info}");
		}

		private static unsafe void OnSimMessage(SimMessageHashes sim_msg_id, IntPtr __result)
		{
			if (sim_msg_id != SimMessageHashes.PrepareGameData || __result == IntPtr.Zero)
			{
				return;
			}
			Sim.GameDataUpdate* update = (Sim.GameDataUpdate*)__result;
			if (move)
			{
				// The game has received a new data frame and is about to switch its cell arrays to it (including
				// when the simulation ran no tick in it). The layer's arrays are taken for this same frame: it is identified
				// by the address of the mass array from the simulation's response, not by what the game held until now.
				// The speed comparison within one launch (ab_test.txt, name GasLayerPressure) temporarily turns off
				// the data frame patch and the layer snapshot: the game then sees the main gas's mass, breathing works as in the game.
				bool wanted = AbTest.IsOn("GasLayerPressure");
				if (wanted != pressureOn)
				{
					pressureOn = wanted;
					SimHooks.SetValue("gas-mix.pressure-off", wanted ? 0 : 1);
				}
				GasLayerBreathing.Tick((ulong)update->mass);
				GasMixture.Tick((ulong)update->mass);
			}
			if (update->numFramesProcessed <= 0)
			{
				return;
			}
			if (!elementsSent)
			{
				// The layer needs element properties. The table is ready only in-game, so it is sent on the first tick;
				// until then the layer skips ticks.
				// By molecules (the pressure mod's setting) the layer gets the molecules' molar masses and evens out pressure,
				// not mass (sim/plugins/gas_mix_kernel.h).
				bool byPressure = MolarMasses.ByMolecules;
				SimHooks.SetValue("gas-mix.by-pressure", byPressure ? 1 : 0);
				elementsSent = SimHooks.SendData("gas-mix.elements", ElementRecords.Build(GasLayerBreathing.IsBreathable, MolarMasses.Of));
				if (elementsSent)
				{
					Debug.Log($"[OniGasMix] GasLayer: the layer evens out {(byPressure ? "pressure (by molecules, the pressure mod's setting)" : "mass")}");
				}
				// "Strength" of germ types: the layer uses it to decide which type stays in a cell where two meet.
				SimHooks.SendData("gas-mix.germs", GermStrengths());
			}
			if (ticksAfterLoad >= 0 && ++ticksAfterLoad >= 3)
			{
				ticksAfterLoad = -1;
				LogStats();
			}
			if (++ticksSinceLog >= LogEveryTicks)
			{
				ticksSinceLog = 0;
				LogStats();
			}
		}

		/// <summary>4 bytes per germ type, by type number: its "strength" from the game's disease table.</summary>
		private static byte[] GermStrengths()
		{
			var diseases = Db.Get().Diseases;
			using (MemoryStream stream = new MemoryStream(diseases.Count * 4))
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				for (int i = 0; i < diseases.Count; i++)
				{
					writer.Write(diseases[i].strength);
				}
				return stream.ToArray();
			}
		}

		/// <summary>Layer exploration: from which places of the simulation code the cell's main gas was changed, by descending number of cases.</summary>
		private static string Changers()
		{
			byte[] buffer = new byte[4 + 96 * 16];
			if (SimHooks.Query("gas-mix.changers", buffer) != 0)
			{
				return "no data";
			}
			int count = Math.Min(BitConverter.ToInt32(buffer, 0), 96);
			List<(uint rva, int replaced, int vanished, int evaporated)> entries = new List<(uint, int, int, int)>();
			for (int i = 0; i < count; i++)
			{
				int at = 4 + i * 16;
				entries.Add((BitConverter.ToUInt32(buffer, at), BitConverter.ToInt32(buffer, at + 4), BitConverter.ToInt32(buffer, at + 8), BitConverter.ToInt32(buffer, at + 12)));
			}
			entries.Sort((a, b) => (b.replaced + b.vanished + b.evaporated).CompareTo(a.replaced + a.vanished + a.evaporated));
			List<string> text = new List<string>();
			foreach ((uint rva, int replaced, int vanished, int evaporated) in entries)
			{
				text.Add($"0x{rva:x}: {replaced}/{vanished}/{evaporated}");
			}
			return text.Count > 0 ? string.Join(", ", text) : "none";
		}

		internal static void LogStats()
		{
			long ticks = SimHooks.GetValue("gas-mix.ticks");
			if (ticks <= 0)
			{
				return;
			}
			string head = $"[OniGasMix] GasLayer: T+{UnityEngine.Time.realtimeSinceStartup:F0} sim ticks={ticks}, gas cells={SimHooks.GetValue("gas-mix.gas-cells")}, " +
				$"gases={SimHooks.GetValue("gas-mix.species")}, mass of main gases={SimHooks.GetValue("gas-mix.total-grams") / 1000.0:F1} kg, ";
			if (move)
			{
				Debug.Log(head + $"mass of all gases in the layer={SimHooks.GetValue("gas-mix.layer-grams") / 1000.0:F1} kg, cells with a mixture={SimHooks.GetValue("gas-mix.mixed-cells")}, " +
					$"main gas switched by the layer={SimHooks.GetValue("gas-mix.main-switched")}, replaced by the sim={SimHooks.GetValue("gas-mix.element-changes")}, " +
					$"removed by the sim={SimHooks.GetValue("gas-mix.main-vanished")}, cells closed for gas={SimHooks.GetValue("gas-mix.cells-left-gas")}, " +
					$"other gases pushed out={SimHooks.GetValue("gas-mix.pushed-grams") / 1000.0:F2} kg, lost={SimHooks.GetValue("gas-mix.lost-grams") / 1000.0:F3} kg, vanished in Void cells={SimHooks.GetValue("gas-mix.void-grams") / 1000.0:F1} kg, removed by the layer in the space zone={SimHooks.GetValue("gas-mix.space-grams") / 1000.0:F2} kg, " +
					$"cells in active regions={SimHooks.GetValue("gas-mix.active-cells")} (open for gas {SimHooks.GetValue("gas-mix.open-cells")}, spreading computed in {SimHooks.GetValue("gas-mix.spread-cells")}), gas cells outside them={SimHooks.GetValue("gas-mix.idle-gas-cells")}, " +
					$"of the removed: in the space zone={SimHooks.GetValue("gas-mix.vanished-in-space")}, less than a gram={SimHooks.GetValue("gas-mix.vanished-tiny")}, " +
					$"whole sim tick {SimHooks.GetValue("gas-mix.tick-micros") / (double)ticks:F0} us, of it layer pass {SimHooks.GetValue("gas-mix.step-micros") / (double)ticks:F0} us per tick (evaporation and boiling {SimHooks.GetValue("gas-mix.phase-micros") / (double)ticks:F0}, kernel {SimHooks.GetValue("gas-mix.kernel-micros") / (double)ticks:F0} {(SimHooks.GetValue("gas-mix.kernel-avx") != 0 ? "avx" : "sse2")}, projection {SimHooks.GetValue("gas-mix.project-micros") / (double)ticks:F0}); " +
					$"sim gas calls skipped: pressure {SimHooks.GetValue("gas-mix.sim-pressure-skipped")}, displacement {SimHooks.GetValue("gas-mix.sim-displacement-skipped")}, density {SimHooks.GetValue("gas-mix.sim-density-skipped")}, " +
					$"removals of gas under a gram blocked={SimHooks.GetValue("gas-mix.sim-evaporations-blocked")}, random swaps of gas cells blocked={SimHooks.GetValue("gas-mix.sim-swaps-blocked")}; " +
					$"game loads={SimHooks.GetValue("gas-mix.resets")}, raised from the save: {SimHooks.GetValue("gas-mix.restored-entries")} cell entries, {SimHooks.GetValue("gas-mix.restored-grams") / 1000.0:F1} kg (of them merged with a new main gas: {SimHooks.GetValue("gas-mix.restore-merged")}), not fitting={SimHooks.GetValue("gas-mix.restore-skipped")}");
				Debug.Log($"[OniGasMix] GasLayer: pressure: the game sees the whole mixture in {SimHooks.GetValue("gas-mix.pressure-cells")} cells, " +
					$"{SimHooks.GetValue("gas-mix.pressure-grams") / 1000.0:F1} kg above the main gases; frames left untouched={SimHooks.GetValue("gas-mix.frame-mismatch")}; " +
					$"this work in the sim thread: {SimHooks.GetValue("gas-mix.frames")} frames, {(SimHooks.GetValue("gas-mix.frames") > 0 ? SimHooks.GetValue("gas-mix.frame-micros") / (double)SimHooks.GetValue("gas-mix.frames") : 0):F0} us each");
				Debug.Log("[OniGasMix] GasLayer: sim code that changed the main gas of a cell (address in SimDLL: replaced by another gas / removed / cleared as gas): " + Changers());
				Debug.Log("[OniGasMix] GasLayer: breathing: " + GasLayerBreathing.Stats());
				Debug.Log("[OniGasMix] GasLayer: game logic by composition: " + GasLayerLogic.Stats());
				Debug.Log($"[OniGasMix] GasLayer: sources and consumers: gas mixed into a cell instead of pushing the other gas out {SimHooks.GetValue("gas-mix.source-mixed")} times, " +
					$"{SimHooks.GetValue("gas-mix.source-mixed-grams") / 1000.0:F2} kg (germs lost with it: {SimHooks.GetValue("gas-mix.source-germs-lost")}); " +
					$"consumers took a gas that is not the main one {SimHooks.GetValue("gas-mix.consumer-mixed")} times, {SimHooks.GetValue("gas-mix.consumer-mixed-grams") / 1000.0:F2} kg; " +
					$"pumps chose their gas by the mixture {SimHooks.GetValue("gas-mix.pump-picks")} times, of them a gas that is not the main one in any cell around: {SimHooks.GetValue("gas-mix.pump-picks-other")}; " +
					$"sim emitters (geysers) of gas went through the layer {SimHooks.GetValue("gas-mix.emitter-calls")} times, mixed into a cell with another main gas {SimHooks.GetValue("gas-mix.emitter-mixed")} times, " +
					$"{SimHooks.GetValue("gas-mix.emitter-mixed-grams") / 1000.0:F2} kg (germs lost with it: {SimHooks.GetValue("gas-mix.emitter-germs-lost")}), held back by the pressure of the mixture {SimHooks.GetValue("gas-mix.emitter-held")} times; " +
					$"gases that are not the main one condensed {SimHooks.GetValue("gas-mix.condensed")} times, {SimHooks.GetValue("gas-mix.condensed-grams") / 1000.0:F2} kg " +
					$"(as falling drops {SimHooks.GetValue("gas-mix.condensed-drips")}, as liquid right in the cell {SimHooks.GetValue("gas-mix.condensed-in-place")}, lost {SimHooks.GetValue("gas-mix.condensed-lost-grams") / 1000.0:F3} kg; of it fell out as ore {SimHooks.GetValue("gas-mix.condensed-ore-grams") / 1000.0:F2} kg; germs of the cell that left with it {SimHooks.GetValue("gas-mix.condensed-germs")}); " +
					$"germs of gas mixed in by sources that the cell kept: {SimHooks.GetValue("gas-mix.source-germs-kept")}; " +
					$"heat: temperature changes made by the sim rescaled to the whole mixture {SimHooks.GetValue("gas-mix.heat-scaled")} times, heat of gases pushed out of a closed cell given to neighbours {SimHooks.GetValue("gas-mix.heat-pushed")} times; " +
					$"germs: kinds carried with the gas {SimHooks.GetValue("gas-mix.germ-kinds")}, germs in gas cells {SimHooks.GetValue("gas-mix.germs-in-gas")}, cells where the layer changed the count {SimHooks.GetValue("gas-mix.germ-cells-changed")}, " +
					$"two kinds met {SimHooks.GetValue("gas-mix.germ-fights")} times, taken by consumers with a gas that is not the main one {SimHooks.GetValue("gas-mix.consumer-germs")}, " +
					$"{(ticks > 0 ? SimHooks.GetValue("gas-mix.germ-micros") / (double)ticks : 0):F0} us per tick, cells with germs {SimHooks.GetValue("gas-mix.germ-cells")}; " +
					$"growth and death of germs computed by the mixture {SimHooks.GetValue("gas-mix.germ-growth-cells")} times, {(ticks > 0 ? SimHooks.GetValue("gas-mix.germ-growth-micros") / (double)ticks : 0):F0} us per tick, " +
					$"their own diffusion rate taken by the mixture {SimHooks.GetValue("gas-mix.germ-diffusion-blended")} times; " +
					$"emitters: cells shown with the mass of the whole mixture for the blocked check {SimHooks.GetValue("gas-mix.emitter-raised")} times, restored out of place {SimHooks.GetValue("gas-mix.emitter-unrestored")}; " +
					$"off-gassing limit: cells whose processing sees the mass of the whole mixture {SimHooks.GetValue("gas-mix.limit-cells")}, shown {SimHooks.GetValue("gas-mix.limit-raised")} times, not restored {SimHooks.GetValue("gas-mix.limit-unrestored")}; " +
					$"gas of a cell that the sim could not displace taken into the mixtures around {SimHooks.GetValue("gas-mix.displaced-by-layer")} times, {SimHooks.GetValue("gas-mix.displaced-by-layer-grams") / 1000.0:F2} kg (germs lost with it: {SimHooks.GetValue("gas-mix.displaced-germs-lost")}); " +
					$"gas written by the sim over another gas: the mixture was kept {SimHooks.GetValue("gas-mix.arrivals")} times, {SimHooks.GetValue("gas-mix.arrivals-mixed-grams") / 1000.0:F2} kg joined it, " +
					$"not a gas arrived {SimHooks.GetValue("gas-mix.arrivals-closed")} times, germs lost {SimHooks.GetValue("gas-mix.arrivals-germs-lost")}");
				// Tooltip check without a cursor: the same lines it would draw, for one cell with a mixture.
				if (Grid.CellCount > 0)
				{
					Debug.Log("[OniGasMix] GasLayer: hover lines sample: " + GasLayerHover.Sample());
				}
			}
			else
			{
				Debug.Log(head + $"projection mismatches={SimHooks.GetValue("gas-mix.projection-mismatches")}, main gas replaced={SimHooks.GetValue("gas-mix.element-changes")}, " +
					$"cells that stopped being gas={SimHooks.GetValue("gas-mix.cells-left-gas")}, pass {SimHooks.GetValue("gas-mix.absorb-micros") / (double)ticks:F0} us per tick, " +
					$"ticks before the element table arrived={SimHooks.GetValue("gas-mix.ticks-without-elements")}");
			}
		}
	}
}
