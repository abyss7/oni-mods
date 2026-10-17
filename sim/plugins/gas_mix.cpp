// Plugin "gas-mix": the gas mixture layer. Every gas cell stores the mass of each gas separately;
// the simulation cell shows a projection of the layer: the "main" gas (the one with the most molecules) and its mass.
// Projecting only the main gas's mass (not the whole mixture) was chosen so that any simulation action
// on the cell as a whole (condensation, displacement, consumption) falls on exactly one gas of the layer.
//
// Two modes, switch "gas-mix.move":
//
//  0, mirror. The layer changes nothing in the game: the simulation moves gas, and after each of its ticks the layer absorbs
//     cell changes and checks that its projection matches the simulation cell.
//
//  1, the layer moves gas itself. Before a simulation tick the layer absorbs what the game and the simulation did to the cells
//     since last time, makes its own step (pressure, diffusion, gravity, heat transport: gas_mix_kernel.h)
//     and writes the projection into the simulation cells. The simulation's own gas movement is turned off meanwhile
//     (hooks at the end of the file), otherwise gas would be moved by two.
//
// Rules for absorbing changes (simulation cell versus the projection written last time):
//  - same gas: the simulation changed the main gas's mass (source, consumer, state transition);
//    the layer's mass of that gas becomes equal to the cell's mass;
//  - different gas: the simulation removed the main gas and put another; in the layer the old main is zeroed, the new one
//    is added, the cell's other gases stay;
//  - vacuum: the simulation removed the main gas entirely; the cell's other gases stay;
//  - liquid or solid: the simulation displaced or destroyed the main gas itself; the layer hands the cell's other gases
//    out to neighbouring cells, and if there is nowhere to hand them out, they are lost.
#include <windows.h>

#include <string.h>

#include <algorithm>
#include <cmath>
#include <mutex>
#include <vector>

#include <unordered_map>

#include "element_table.h"
#include "liquid_service.h"
#include "gas_mix_kernel.h"
#include "phase_rules.h"
#include "sim_types.h"
#include "simhook_plugin.h"

SIM_PLUGIN_MODULE();
// Structure layouts (sim_types.h) and the parameters of hooked functions were taken from this SimDLL build.
SIM_PLUGIN_BUILD("gas-mix", 0x6a67f9d8, 0x125000);

SIM_FLAG(moving, "gas-mix.move");
// Experiment without the layer: the simulation does not move gas itself, and nothing else changes (the mirror keeps watching).
// In the "layer moves gas itself" mode movement in the simulation is off even without this switch.
SIM_FLAG(simFrozen, "gas-mix.freeze-sim");
SIM_COUNTER(pressureCalls, "gas-mix.sim-pressure-calls");
SIM_COUNTER(pressureSkipped, "gas-mix.sim-pressure-skipped");
SIM_COUNTER(displacementCalls, "gas-mix.sim-displacement-calls");
SIM_COUNTER(displacementSkipped, "gas-mix.sim-displacement-skipped");
SIM_COUNTER(densityCalls, "gas-mix.sim-density-calls");
SIM_COUNTER(densitySkipped, "gas-mix.sim-density-skipped");
SIM_COUNTER(evaporationsBlocked, "gas-mix.sim-evaporations-blocked"); // how many times the simulation was not allowed to erase gas lighter than a gram
SIM_COUNTER(swapsBlocked, "gas-mix.sim-swaps-blocked");               // how many times the simulation was not allowed to swap two gas cells
// For comparing speed within one launch: with this switch the data frame for the game is not adjusted
// and no layer snapshot is made for the mod (the mod then breathes the old way, and the game sees the main gas's mass).
SIM_FLAG(pressureOff, "gas-mix.pressure-off");
// Pressure mode (the pressure mod's "by molecules", set by the mod together with the elements table, whose molar masses are
// then the real ones): the layer evens out pressure, not mass (gas_mix_kernel.h), and wherever the game and the simulation
// look at a gas cell's mass as pressure (buildings, sensors, vents, sources, the off-gassing limit) they are shown the cell's
// "equivalent mass": the mass of Oxygen at 20 degrees that would give the same pressure. Without it they see the mass of the
// whole mixture.
SIM_FLAG(byPressure, "gas-mix.by-pressure");
// Phase rules (sim/plugins/phase_rules.h): condensation by saturated vapour pressure, evaporation and boiling of liquid
// into the neighbouring gas cell, latent heat of transition. They are enabled by the pressure mod, which at the same time forbids
// the simulation its own transitions between liquid and gas; without it the layer condenses by temperature, like the game.
SIM_FLAG(phaseOn, "gas-mix.phase");
SIM_COUNTER(ticks, "gas-mix.ticks");
SIM_COUNTER(gasCells, "gas-mix.gas-cells");
SIM_COUNTER(speciesCount, "gas-mix.species");
SIM_COUNTER(mismatches, "gas-mix.projection-mismatches");
SIM_COUNTER(elementChanges, "gas-mix.element-changes");
SIM_COUNTER(leftGas, "gas-mix.cells-left-gas");
SIM_COUNTER(totalGrams, "gas-mix.total-grams");
SIM_COUNTER(absorbMicros, "gas-mix.absorb-micros");
SIM_COUNTER(skippedNoElements, "gas-mix.ticks-without-elements");
// Only in the "layer moves gas itself" mode.
SIM_COUNTER(layerGrams, "gas-mix.layer-grams");           // mass of all gases of the layer, including non-main ones
SIM_COUNTER(mixedCells, "gas-mix.mixed-cells");           // cells where the second gas makes up at least 1% of molecules
SIM_COUNTER(mainSwitched, "gas-mix.main-switched");       // how many times the layer changed a cell's main gas
SIM_COUNTER(mainVanished, "gas-mix.main-vanished");       // how many times the simulation removed the main gas entirely
SIM_COUNTER(pushedGrams, "gas-mix.pushed-grams");         // how much of the non-main gases was handed to neighbours when a cell closed
SIM_COUNTER(lostGrams, "gas-mix.lost-grams");             // how much was lost because there was nowhere to hand it out
SIM_COUNTER(voidGrams, "gas-mix.void-grams");             // how much gas was lost in cells of the Void element (grid edge)
SIM_COUNTER(spaceGrams, "gas-mix.space-grams");           // how much gas the layer itself removed in the space zone
SIM_COUNTER(activeCells, "gas-mix.active-cells");         // cells in the regions the simulation computes
SIM_COUNTER(idleGasCells, "gas-mix.idle-gas-cells");      // gas cells outside these regions (the layer does not move them)
// Why the simulation removed the main gas entirely (parts of the main-vanished counter):
SIM_COUNTER(vanishedSpace, "gas-mix.vanished-in-space");  // cell in the space zone: gas drains there at 20 g per tick
SIM_COUNTER(vanishedTiny, "gas-mix.vanished-tiny");       // there was less than a gram of gas: the simulation's small-mass rule
SIM_COUNTER(stepMicros, "gas-mix.step-micros");           // time of the whole layer pass per tick
SIM_COUNTER(kernelMicros, "gas-mix.kernel-micros");       // of it, the kernel step (pressure, diffusion, gravity)
SIM_COUNTER(kernelAvx, "gas-mix.kernel-avx");             // 1 if the kernel step uses the AVX variant
SIM_COUNTER(spreadCells, "gas-mix.spread-cells");         // in how many cells the step computed spreading (at ceilings and floors)
SIM_COUNTER(openCells, "gas-mix.open-cells");             // how many region cells are open to gas (gas or vacuum)
SIM_COUNTER(projectMicros, "gas-mix.project-micros");     // of it, writing the projection into the simulation cells
// Saving: what the layer picked up from the save at the last game load.
SIM_COUNTER(resets, "gas-mix.resets");                    // how many times the layer started from a clean slate (game load)
SIM_COUNTER(restoredEntries, "gas-mix.restored-entries"); // "gas in cell" entries accepted from the save
SIM_COUNTER(restoredGrams, "gas-mix.restored-grams");     // their mass
SIM_COUNTER(restoreSkipped, "gas-mix.restore-skipped");   // entries that did not fit (cell closed to gas, gas no longer in the game)
SIM_COUNTER(restoreMerged, "gas-mix.restore-merged");     // of the accepted: the gas became main in the cell, the mass added to the cell's mass
// Breathing: how much breathable gas was taken from cells' non-main gases at the mod's requests.
SIM_COUNTER(breathedGrams, "gas-mix.breathed-grams");
SIM_COUNTER(breathShortGrams, "gas-mix.breath-short-grams"); // requested, but by that moment it was no longer in the cell
// Gas sources and consumers in the simulation itself.
SIM_COUNTER(sourceMixedCount, "gas-mix.source-mixed");            // how many times released gas was added to the cell's mixture
SIM_COUNTER(sourceMixedGrams, "gas-mix.source-mixed-grams");
SIM_COUNTER(sourceGermsLost, "gas-mix.source-germs-lost");        // germs of the released gas that were lost in the process
SIM_COUNTER(emitterCalls, "gas-mix.emitter-calls");               // how many times a gas source (geyser) looked for a cell through the layer
SIM_COUNTER(emitterMixedCount, "gas-mix.emitter-mixed");          // how many times its gas was added to the mixture of a cell with a different main gas
SIM_COUNTER(emitterMixedGrams, "gas-mix.emitter-mixed-grams");
SIM_COUNTER(emitterHeld, "gas-mix.emitter-held");                 // how many times a release did not happen because of the mixture's pressure, though there was little main gas
SIM_COUNTER(emitterGermsLost, "gas-mix.emitter-germs-lost");
SIM_COUNTER(condensedCount, "gas-mix.condensed");                 // how many times a cell's non-main gas became liquid
SIM_COUNTER(condensedGrams, "gas-mix.condensed-grams");
SIM_COUNTER(condensedDrips, "gas-mix.condensed-drips");           // of them, handed to the game as a falling drop
SIM_COUNTER(condensedInPlace, "gas-mix.condensed-in-place");      // and added as liquid right into the cell
SIM_COUNTER(condensedLostGrams, "gas-mix.condensed-lost-grams");  // lost: simulation functions not found
SIM_COUNTER(condensedOreGrams, "gas-mix.condensed-ore-grams");    // of the condensed, fell out as ore
SIM_COUNTER(condensedGerms, "gas-mix.condensed-germs");           // cell germs that left together with the liquid and ore
SIM_COUNTER(sourceGermsKept, "gas-mix.source-germs-kept");        // germs of the released gas added to the cell when mixing
SIM_COUNTER(heatScaled, "gas-mix.heat-scaled");                   // how many times the cell temperature change from the simulation was rescaled to the whole mixture
SIM_COUNTER(heatPushed, "gas-mix.heat-pushed");                   // how many times heat of a closed cell's gases was passed to a neighbour
SIM_COUNTER(germKinds, "gas-mix.germ-kinds");                     // how many germ kinds the layer carried with gas in the last pass
SIM_COUNTER(germsInGas, "gas-mix.germs-in-gas");                  // how many germs are in gas cells after the last pass
SIM_COUNTER(germCellCount, "gas-mix.germ-cells");                 // in how many cells gas flows through there were germs in the last pass
SIM_COUNTER(germCellsChanged, "gas-mix.germ-cells-changed");      // in how many cells the layer changed the germ count, total
SIM_COUNTER(germFights, "gas-mix.germ-fights");                   // how many times two germ kinds met in a cell
SIM_COUNTER(germMicros, "gas-mix.germ-micros");                   // germ transport time, total
SIM_COUNTER(consumerGerms, "gas-mix.consumer-germs");             // germs that went to a consumer together with a non-main gas
SIM_COUNTER(breathGerms, "gas-mix.breath-germs");                 // germs that went to a duplicant together with a non-main breathable gas
SIM_COUNTER(emitterRaised, "gas-mix.emitter-raised");             // how many times a cell was shown the mass of the whole mixture for the duration of a source check
SIM_COUNTER(emitterUnrestored, "gas-mix.emitter-unrestored");     // how many such cells had to be restored somewhere other than where they should be (must be 0)
SIM_COUNTER(evaporatedGrams, "gas-mix.evaporated-grams");         // how much liquid became vapour by the phase rules, total
SIM_COUNTER(evaporations, "gas-mix.evaporations");                // how many times a liquid gave vapour to a neighbouring gas cell
SIM_COUNTER(boilings, "gas-mix.boilings");                        // of them, the liquid was boiling (its vapour pressure above the gas pressure)
SIM_COUNTER(evaporatedWhole, "gas-mix.evaporated-whole");         // how many liquid cells evaporated entirely
SIM_COUNTER(evaporatedOreGrams, "gas-mix.evaporated-ore-grams");  // how much remained as solid residue on evaporation (salt, dirt)
SIM_COUNTER(frostGrams, "gas-mix.frost-grams");                   // how much vapour fell straight out as solid (cell colder than the freezing point)
SIM_COUNTER(condensedPooled, "gas-mix.condensed-pooled");         // how many condensate portions went straight into the same liquid below the cell, without a drop
SIM_COUNTER(displacedByLayer, "gas-mix.displaced-by-layer");      // how many times a cell's main gas that the simulation could not displace went into the neighbours' mixtures
SIM_COUNTER(displacedByLayerGrams, "gas-mix.displaced-by-layer-grams");
SIM_COUNTER(displacedGermsLost, "gas-mix.displaced-germs-lost");  // germs of such a cell that could not be handed to a neighbour (it has a different kind)
SIM_COUNTER(phaseMicros, "gas-mix.phase-micros");                 // time of evaporation and boiling by the phase rules in the layer pass, total
SIM_COUNTER(tickMicros, "gas-mix.tick-micros");                   // time of the whole simulation tick together with the layer pass (from entering UpdateData to leaving), total
SIM_COUNTER(limitCellCount, "gas-mix.limit-cells");               // cells whose processing by the simulation is shown the mass of the whole mixture (in the last pass)
SIM_COUNTER(limitRaised, "gas-mix.limit-raised");                 // how many times a gas cell was shown the mass of the whole mixture for the duration of such processing
SIM_COUNTER(limitUnrestored, "gas-mix.limit-unrestored");         // how many times the mass could not be restored (the cell turned out to hold another substance; must be 0)
SIM_COUNTER(germGrowthCells, "gas-mix.germ-growth-cells");        // how many times a cell's germ growth and death were computed from the mixture composition
SIM_COUNTER(germGrowthMicros, "gas-mix.germ-growth-micros");
SIM_COUNTER(germDiffusionBlended, "gas-mix.germ-diffusion-blended"); // how many times the germs' own diffusion rate was taken from the mixture composition
SIM_COUNTER(arrivalsHidden, "gas-mix.arrivals");                  // how many times the simulation was not allowed to displace a cell's gas before writing a new substance
SIM_COUNTER(arrivalsMixedGrams, "gas-mix.arrivals-mixed-grams");  // how much gas written by the simulation into such a cell was added to its mixture
SIM_COUNTER(arrivalsClosed, "gas-mix.arrivals-closed");           // how many times something other than gas arrived in such a cell (the mixture handed to neighbours)
SIM_COUNTER(arrivalsGermsLost, "gas-mix.arrivals-germs-lost");
SIM_COUNTER(consumerMixedCount, "gas-mix.consumer-mixed");        // how many times a consumer took a gas that is not main in the cell
SIM_COUNTER(consumerMixedGrams, "gas-mix.consumer-mixed-grams");
SIM_COUNTER(pumpPicks, "gas-mix.pump-picks");                     // how many times the gas for an "any gas" consumer was chosen by the mixture composition
SIM_COUNTER(pumpPicksOther, "gas-mix.pump-picks-other");          // of them, the chosen gas is not main in any nearby cell
// Pressure: the game gets the mass of the whole mixture in a cell, not only of the main gas.
SIM_COUNTER(pressureCells, "gas-mix.pressure-cells");             // cells where the other gases were added to the mass for the game (over the last frame)
SIM_COUNTER(pressureGrams, "gas-mix.pressure-grams");             // how much was added (over the last frame)
SIM_COUNTER(frameMismatch, "gas-mix.frame-mismatch");             // frames where the data for the game had the wrong shape and were left untouched
SIM_COUNTER(framesPublished, "gas-mix.frames");                   // how many data frames were processed
SIM_COUNTER(frameMicros, "gas-mix.frame-micros");                 // time of this processing in the simulation thread, total

namespace
{
	// This much gas the simulation removes per tick from a cell in the space zone (see SimBase::UpdateData).
	const float SpaceDrain = 0.02f;
	// Gas lighter than this is erased by the simulation unconditionally (PostProcessCell); the layer removes such traces itself.
	const float TraceMass = 1e-9f;

	// State transition numbers from the simulation (DoStateTransition): the transition happens 3 degrees below the point, the cell
	// warms up by 1.5 degrees in the process, ore lighter than a gram does not drop.
	const float TransitionMargin = 3.0f;
	const float TransitionHeat = 1.5f;
	const float OreMinMass = 0.001f;

	std::atomic<ElementTable*> publishedElements{nullptr};
	// "Vapour and liquid" pairs for the phase rules: rebuilt when the element table or the rule settings arrive
	// from the pressure mod. Kept separately from the element table: a table change forces the layer to be rebuilt
	// from scratch, while rule settings do not require that.
	std::atomic<PhaseTables*> publishedPhases{nullptr};
	std::mutex phaseGuard;
	PhaseConfig phaseConfig;

	void RebuildPhases()
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements) return;
		PhaseTables* tables = new PhaseTables;
		tables->Build(elements->elements, phaseConfig);
		// Old tables are not freed: the simulation thread may be finishing reading them right now.
		publishedPhases.store(tables, std::memory_order_release);
	}

	typedef void (*ChangeSubstanceFn)(void* events, SimData* sim, uint64_t cell);

	// A portion of non-main gas that became liquid during a layer pass (see "Condensation" below).
	struct Condensed
	{
		int32_t cell;
		uint16_t liquid;
		uint16_t ore;      // NoElement if the transition has no ore
		float mass;        // the whole mass of gas that became liquid and ore
		float oreMass;
		float temperature;
		float share;       // what share of the cell mixture's mass this gas made up: the same share of the cell's germs leaves with it
	};
	// An entry without liquid (liquid == NoElement): a solid substance entirely in ore. This is how the residue of an evaporated
	// liquid (Salt) comes out, and vapour that fell out in a cell colder than its liquid's freezing point (frost).

	// Loss survey: how much gas was lost in cells closed by this substance, and the biggest case.
	struct LossStat
	{
		double mass = 0;
		int64_t count = 0;
		float biggest = 0.0f;
		int32_t cell = -1;       // cell of the biggest case
		uint16_t gas = NoElement; // which gas was lost in it
	};
	// Per-substance survey of the phase rules: mass, number of portions, temperatures (mass-weighted) and the biggest portion.
	struct PhaseStat
	{
		double mass = 0, gasHeat = 0, liquidHeat = 0; // gasHeat and liquidHeat: sum of "mass times temperature" of the gas and the liquid
		int64_t count = 0, boiling = 0;
		float biggest = 0.0f;
		int32_t cell = -1;

		void Add(float amount, float gasTemperature, float liquidTemperature, bool boil, size_t where)
		{
			mass += amount;
			gasHeat += (double)amount * gasTemperature;
			liquidHeat += (double)amount * liquidTemperature;
			count++;
			if (boil) boiling++;
			if (amount > biggest) { biggest = amount; cell = (int32_t)where; }
		}
	};

	// A cell whose mixture is temporarily hidden from the simulation (see "Gas the simulation writes over another gas").
	struct HiddenCell
	{
		int32_t cell;
		float temperature; // mixture temperature at the moment it was hidden
		uint8_t germ;      // cell germs at the same moment: the simulation erases them when writing a new substance
		int32_t germs;
	};

	// A portion of heat that came into a cell together with the gases of a closed neighbouring cell.
	struct HeatPush
	{
		size_t cell;
		float capacity;    // mass times heat capacity of the arriving gases
		float temperature; // their temperature
	};

	// Everything below belongs to the simulation thread.
	struct Layer
	{
		size_t cells = 0;
		const ElementTable* elements = nullptr;
		bool own = false;                        // masses are kept in the kernel ("layer moves gas itself" mode)
		std::vector<std::vector<float>> species; // mirror: by element index, empty until the gas has been seen
		std::vector<uint16_t> present;
		std::vector<uint16_t> projectedElement;  // what the layer wrote into the cell or considers its main gas
		std::vector<float> projectedMass;
		std::vector<float> lastTemperature;      // last non-zero cell temperature ("layer moves gas itself" mode)
		std::vector<float> otherMass;            // mass of all gases of the cell except the main one, after the last projection write
		std::vector<float> shownExtra;           // what is added to the main gas's mass when the cell is shown as pressure: otherMass,
		                                         // or in the pressure mode the equivalent mass minus the main gas's mass (may be negative)
		// By what factor the cell temperature change computed by the simulation from the main gas differs from
		// what the mixture should get (after the last projection write): the main gas's share of the mixture's heat capacity, multiplied
		// by the thermal conductivity correction. A cell with a single gas has 1.
		std::vector<float> mainShare;
		std::vector<HeatPush> heatPushes;        // heat of gases handed to neighbours in this pass
		std::vector<uint32_t> closing;           // cells that closed to gas in this pass: their gases are handed out after absorbing
		std::vector<float> germFraction;         // fractional part of the cell's germ count that did not fit into the simulation's integer
		std::vector<uint8_t> germFractionKind;   // which germ kind it belongs to
		float germStrength[GasMixKernel::MaxPassive] = { 1, 1, 1, 1, 1, 1, 1, 1 }; // "strength" of the kinds from the game data
		std::vector<uint32_t> germCells;         // cells with germs that gas flows through (for this pass)
		std::vector<uint32_t> germTouched;       // they and their neighbours: only there could the step change the germ count
		std::vector<uint32_t> germStamp;         // mark "cell already in germTouched" (pass number)
		std::vector<uint8_t> hidden;             // 1: the cell's mixture is hidden from the simulation until the next ResolveHidden
		std::vector<uint8_t> limitKind;          // pressure limit marks (LimitSelf, LimitAbove), set by the projection pass
		std::vector<uint32_t> limitCells;        // marked cells: marks are cleared by this list
		std::vector<HiddenCell> hiddenCells;
		uint32_t germPass = 0;
		std::vector<uint8_t> active;             // 1 if the cell lies in a region the simulation computes
		std::vector<GasMixRegion> regions;       // these regions; while they do not change, the layer walks only their cells
		int64_t activeCount = 0;
		int64_t idleGasCells = 0;                // gas outside the regions: the layer does not move it, counted when the regions change
		double idleGasMass = 0;
		GasMixKernel kernel;
		GasMixParams params;
		float condenseMass = 0.01f;              // non-main gas lighter than this does not condense in a cell (stays as "humidity")
		std::vector<Condensed> condensed;        // what condensed during the last pass: goes to the simulation after it
		float evaporationRate = 0.1f;            // what share of the way to equilibrium a non-boiling liquid covers per pass
		float boilRate = 0.5f;                   // what share of the heat above the boiling point a boiling liquid gives to vapour per pass
		std::unordered_map<uint32_t, float> residue; // solid residue of evaporation per liquid cell, while it is less than a portion
		std::vector<double> condensedBy, evaporatedBy; // phase rules by element index: how much gas condensed, how much liquid evaporated, kg
		// Survey (everything since the start of the game, under the layer lock). Losses: gases of a cell that had nowhere to hand them
		// out when closing, by the substance that closed the cell. Phase rules in detail: per gas (condensation) and per liquid
		// (evaporation). Cell state changes: [was][became] for vacuum, gas, liquid and solid, and by the substance
		// the cell became.
		std::vector<LossStat> lossBy;
		std::vector<PhaseStat> condensedDetail, evaporatedDetail;
		int64_t transitions[4][4] = {};
		std::vector<int64_t> transitionsTo;
		ChangeSubstanceFn changeSubstance = nullptr;
		bool lookedForChangeSubstance = false;
	};
	Layer layer;

	// Layer state in the game save. The simulation cells (and therefore the game save) hold only the main gas
	// of each cell; the layer gives the other gases of the mixture to the mod on saving and gets them back on loading.
	// Format (all numbers 4 bytes): header SavedHeader, then gasCount blocks: element index, number of
	// entries, SavedEntry entries. Cells are game cells, without the simulation frame: this way the record does not depend on its layout.
	// In the save the mod replaces element indices with their permanent hash names: indices may shift between game versions and
	// mod sets.
	const int32_t SavedMagic = 0x31584d47; // "GMX1"
	struct SavedHeader
	{
		int32_t magic;
		int32_t version;
		int32_t gameWidth, gameHeight;
		int32_t gasCount;
		int32_t bytes; // length of the whole block; if the buffer is too small, the required length is written here
	};
	struct SavedEntry
	{
		int32_t cell;
		float mass;
	};
	// A game was loaded: the layer must start over (state of a previous colony may have remained in the library's memory)
	// and pick up the saved gases. Changed under layerGuard.
	bool resetRequested = false;
	std::vector<uint8_t> pendingState;

	// Breathing. The game sees only the main gas in a cell, so the cell's breathable gases that are not main in it
	// (Oxygen in a cell where Carbon Dioxide became main) are learned about and taken by the mod through the layer.
	//  - After each pass the layer publishes an array "how much non-main breathable gas is in a game cell";
	//    the mod reads it with the query "gas-mix.breathable". Publishing and reading go under their own short lock
	//    (copying the array), not under the layer lock: the game's main thread must not wait for the layer step.
	//  - Requests to "take this much from the cell" ("gas-mix.consume") accumulate in a queue and are executed at the start
	//    of the next pass, like the game's own messages to the simulation.
	struct BreathRequest
	{
		int32_t cell; // game cell
		float mass;
		int32_t germs; // how many of the cell's germs the duplicant inhaled together with this gas (the mod has already given them to the duplicant)
	};
	std::mutex breathGuard;
	std::vector<BreathRequest> breathRequests;

	// Layer snapshot per game frame. The game reads cells from the data frame the simulation prepares at the end of its
	// frame (CopySimDataToGame), and reads it while the simulation is already computing the next one. So that the layer arrays the mod gets
	// refer to the same moment as the game's cells, they are taken in the same place, one snapshot per data frame
	// (the simulation alternates several such frames); the mod names the one it needs by the address of the mass array the game holds.
	//  - breathable: the cell's non-main breathable gas (for breathing);
	//  - others: mass of all gases of the cell except the main one. It is also added to the cell's mass in the data frame,
	//    so the game sees the pressure of the whole mixture, and the mod restores the main gas's mass from this array.
	// A cell's non-main gas in the frame snapshot. The layout is shared with the mod.
	struct FrameGas
	{
		int32_t cell; // game cell
		uint16_t element;
		uint16_t pad;
		float mass;
	};
	static_assert(sizeof(FrameGas) == 12, "frame gas record layout is shared with the mod");

	struct FrameSnapshot
	{
		const float* gameMass = nullptr;
		uint64_t made = 0;
		std::vector<float> breathable, others;
		// Composition of cells with a mixture: one record per non-main gas of the cell, in increasing cell index order.
		// From these the mod answers the game's questions "how much gas X is in the cell".
		std::vector<FrameGas> gases;
		// Row spans where the arrays may be non-zero (active regions): start in game cells and length.
		// Only these are copied to the mod: that is less than half of the grid.
		std::vector<std::pair<uint32_t, uint32_t>> rows;
	};
	FrameSnapshot snapshots[3];
	uint64_t snapshotsMade = 0;
	// The layer is changed by the simulation thread, while the cell composition for the tooltip is asked for by the game's main thread ("gas-mix.cell").
	// The lock is held for the duration of the layer pass; outside the pass it is free.
	std::mutex layerGuard;

	int64_t NowMicros()
	{
		static LARGE_INTEGER frequency;
		if (frequency.QuadPart == 0) QueryPerformanceFrequency(&frequency);
		LARGE_INTEGER now;
		QueryPerformanceCounter(&now);
		return now.QuadPart * 1000000 / frequency.QuadPart;
	}

	// ---------- Mirror ----------

	std::vector<float>& Species(uint16_t element)
	{
		std::vector<float>& masses = layer.species[element];
		if (masses.empty())
		{
			masses.assign(layer.cells, 0.0f);
			layer.present.push_back(element);
			speciesCount.store((int64_t)layer.present.size(), std::memory_order_relaxed);
		}
		return masses;
	}

	void ResetMirror(size_t cells, const ElementTable* elements)
	{
		layer.cells = cells;
		layer.elements = elements;
		layer.own = false;
		layer.species.assign(elements->elements.size(), std::vector<float>());
		layer.present.clear();
		layer.projectedElement.assign(cells, NoElement);
		layer.projectedMass.assign(cells, 0.0f);
		speciesCount.store(0, std::memory_order_relaxed);
	}

	// One pass over all cells after a simulation tick.
	void Mirror(const SimData* sim, const ElementTable* elements)
	{
		const int64_t start = NowMicros();
		const SimCells* source = sim->cells;
		size_t cells = source->elementIdx.size();
		if (source->mass.size() < cells) cells = source->mass.size();
		if (resetRequested || cells != layer.cells || elements != layer.elements || layer.own)
		{
			ResetMirror(cells, elements);
			if (resetRequested) resets.fetch_add(1, std::memory_order_relaxed);
			resetRequested = false;
			pendingState.clear(); // the mirror has nothing to pick up: a cell has one gas in it
		}

		const uint16_t* simElement = source->elementIdx.first;
		const float* simMass = source->mass.first;
		int64_t gas = 0, wrong = 0, changed = 0, left = 0;
		double total = 0;
		for (size_t cell = 0; cell < cells; cell++)
		{
			const uint16_t element = simElement[cell];
			const uint16_t before = layer.projectedElement[cell];
			if (!elements->IsGas(element))
			{
				if (before != NoElement)
				{
					for (uint16_t present : layer.present) layer.species[present][cell] = 0.0f;
					layer.projectedElement[cell] = NoElement;
					layer.projectedMass[cell] = 0.0f;
					left++;
				}
				continue;
			}
			const float mass = simMass[cell];
			if (element == before)
			{
				layer.species[element][cell] = mass;
			}
			else
			{
				if (before != NoElement)
				{
					layer.species[before][cell] = 0.0f;
					changed++;
				}
				Species(element)[cell] += mass;
			}

			// In the mirror a cell has one gas, so the "main" one here is simply the gas with the largest mass,
			// and it must match the simulation cell.
			uint16_t main = NoElement;
			float mainMass = -1.0f;
			for (uint16_t present : layer.present)
			{
				const float m = layer.species[present][cell];
				if (m > mainMass || (m == mainMass && present == element))
				{
					main = present;
					mainMass = m;
				}
			}
			if (main != element || mainMass != mass) wrong++;
			layer.projectedElement[cell] = main;
			layer.projectedMass[cell] = mainMass;
			total += mainMass;
			gas++;
		}
		gasCells.store(gas, std::memory_order_relaxed);
		totalGrams.store((int64_t)(total * 1000.0), std::memory_order_relaxed);
		mismatches.fetch_add(wrong, std::memory_order_relaxed);
		elementChanges.fetch_add(changed, std::memory_order_relaxed);
		leftGas.fetch_add(left, std::memory_order_relaxed);
		absorbMicros.fetch_add(NowMicros() - start, std::memory_order_relaxed);
		ticks.fetch_add(1, std::memory_order_relaxed);
	}

	// ---------- Survey: who in the simulation changes a cell's main gas ----------
	// The layer sees only the outcome ("a different gas in the cell" or "the gas vanished"), not the cause. Every time
	// the simulation tells the game about a substance change in a cell (SimEvents::ChangeSubstance), this records which
	// place in its code the call came from (return address relative to the start of SimDLL), if the layer had written
	// one gas into the cell and now it holds another gas or vacuum. Separately counted are the places that call cell clearing
	// (Evaporate) for gas cells: its own substance change call hides the real cause.
	// Functions are found by address in the function table.
	struct Changer
	{
		uint32_t rva;
		int32_t replaced;   // gas replaced by another gas
		int32_t vanished;   // gas replaced by vacuum
		int32_t evaporated; // gas cell clearing was called from here
	};
	std::mutex changersGuard;
	Changer changers[96];
	int changerCount = 0;

	void NoteChanger(void* caller, int kind)
	{
		static const uintptr_t base = (uintptr_t)GetModuleHandleA("SimDLL.dll");
		const uintptr_t address = (uintptr_t)caller;
		// Calls not from SimDLL (the layer itself) are not of interest.
		if (base == 0 || address < base || address - base > 0x4000000) return;
		const uint32_t rva = (uint32_t)(address - base);
		std::lock_guard<std::mutex> lock(changersGuard);
		int found = -1;
		for (int i = 0; i < changerCount && found < 0; i++) if (changers[i].rva == rva) found = i;
		if (found < 0)
		{
			if (changerCount >= (int)(sizeof(changers) / sizeof(changers[0]))) return;
			found = changerCount++;
			changers[found] = Changer{ rva, 0, 0, 0 };
		}
		if (kind == 0) changers[found].replaced++;
		else if (kind == 1) changers[found].vanished++;
		else changers[found].evaporated++;
	}

	// Called in simulation threads between layer passes: at that time nobody changes the written projection.
	void NoteSubstanceChange(const SimData* sim, uint64_t cell, void* caller)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements || moving.load(std::memory_order_relaxed) == 0 || !layer.own || elements != layer.elements) return;
		const SimCells* cells = sim->updatedCells;
		if (cell >= layer.cells || cell >= layer.projectedElement.size() || cell >= cells->elementIdx.size()) return;
		const uint16_t before = layer.projectedElement[cell], now = cells->elementIdx.first[cell];
		if (!elements->IsGas(before) || before == now) return;
		if (elements->IsGas(now)) NoteChanger(caller, 0);
		else if (now < elements->elements.size() && elements->elements[now].state == SIM_VACUUM) NoteChanger(caller, 1);
	}
}

// Earlier place in the chain than usual (simhook.h): the survey records the address of the calling game code.
SIM_HOOK_ORDERED("gas-mix", -100, void, "SimEvents::ChangeSubstance", ChangeSubstance, (void* events, SimData* sim, uint64_t cell))
{
	NoteSubstanceChange(sim, cell, __builtin_return_address(0));
	ChangeSubstance_original(events, sim, cell);
}

// Survey result for the mod: number of records, then Changer records.
SIM_DATA(Changers, "gas-mix.changers")
{
	if (size < (int32_t)sizeof(int32_t)) return -1;
	std::lock_guard<std::mutex> lock(changersGuard);
	int32_t count = (size - (int32_t)sizeof(int32_t)) / (int32_t)sizeof(Changer);
	if (count > changerCount) count = changerCount;
	memcpy((void*)data, &count, sizeof(count));
	memcpy((uint8_t*)data + sizeof(count), changers, (size_t)count * sizeof(Changer));
	return 0;
}

namespace
{
	// ---------- The layer moves gas itself ----------

	struct Cells
	{
		uint16_t* element;
		float* mass;
		float* temperature;
		const uint8_t* properties;
		size_t count;
		int32_t width;
	};

	// A cell gas can flow through: it holds gas or vacuum and is not marked as gas-impermeable.
	bool IsOpen(const Cells& c, const ElementTable* elements, size_t cell)
	{
		const uint16_t element = c.element[cell];
		if (element >= elements->elements.size()) return false;
		const uint8_t state = elements->elements[element].state;
		return (state == SIM_GAS || state == SIM_VACUUM) && (c.properties[cell] & 1) == 0;
	}

	void ResetOwn(const SimData* sim, const Cells& c, const ElementTable* elements)
	{
		layer.cells = c.count;
		layer.elements = elements;
		layer.own = true;
		std::vector<float> molar(elements->elements.size()), heat(elements->elements.size());
		for (size_t i = 0; i < molar.size(); i++)
		{
			molar[i] = elements->elements[i].molar > 0.0f ? elements->elements[i].molar : 1.0f;
			heat[i] = elements->elements[i].heat > 0.0f ? elements->elements[i].heat : 1.0f;
		}
		layer.kernel.Reset(sim->width, (int32_t)(c.count / sim->width), molar, heat);
		layer.projectedElement.assign(c.count, NoElement);
		layer.projectedMass.assign(c.count, 0.0f);
		layer.lastTemperature.assign(c.count, 0.0f);
		layer.otherMass.assign(c.count, 0.0f);
		layer.shownExtra.assign(c.count, 0.0f);
		layer.limitKind.assign(c.count, 0);
		layer.limitCells.clear();
		layer.residue.clear();
		layer.mainShare.assign(c.count, 1.0f);
		layer.germFraction.assign(c.count, 0.0f);
		layer.germFractionKind.assign(c.count, 0xFF);
		layer.germCells.clear();
		layer.germTouched.clear();
		layer.germStamp.assign(c.count, 0);
		layer.germPass = 0;
		layer.hidden.assign(c.count, 0);
		layer.hiddenCells.clear();
		layer.active.assign(c.count, 0);
		layer.regions.clear();
		layer.activeCount = -1; // the first pass after a reset must go over all cells
		// Initial layer state: every gas cell has one gas, the one currently in it.
		for (size_t cell = 0; cell < c.count; cell++)
		{
			const uint16_t element = c.element[cell];
			layer.projectedElement[cell] = element;
			if (elements->IsGas(element))
			{
				layer.kernel.Mass(element)[cell] = c.mass[cell];
				layer.projectedMass[cell] = c.mass[cell];
			}
		}
	}

	// Picks up gases from the saved state on top of the layer just assembled from the simulation cells.
	// A cell's main gas is always taken from the simulation (it lives in the game's own save); the others are taken
	// from the saved state. An entry is skipped if the cell is now closed to gas or if that gas no longer exists in the game.
	//
	// A special case: the gas from an entry turned out to be main in the cell. This happens when between the last layer
	// pass and saving the simulation itself changed the cell's gas (a source released another gas into it). In normal
	// operation the layer would add the cell's mass to its own mass of that gas on the next tick; the same is done
	// here. In the first run with loading there were 132 such entries out of 27.5 thousand, but they accounted for
	// 78 kg out of 4440: without this rule they were lost.
	void RestoreSaved(const Cells& c, const ElementTable* elements)
	{
		std::vector<uint8_t> state;
		state.swap(pendingState);
		int64_t taken = 0, skipped = 0, merged = 0;
		double mass = 0;
		const int32_t width = c.width, gameWidth = width - 2, gameHeight = (int32_t)(c.count / (size_t)width) - 2;
		SavedHeader header = {};
		if (state.size() >= sizeof(SavedHeader)) memcpy(&header, state.data(), sizeof(header));
		if (state.empty())
		{
			// New game or a save without layer state: nothing to pick up.
		}
		else if (header.magic != SavedMagic || header.version != 1 || header.gameWidth != gameWidth || header.gameHeight != gameHeight)
		{
			skipped = -1; // state from another grid or another format: does not fit as a whole
		}
		else
		{
			size_t at = sizeof(SavedHeader);
			for (int32_t g = 0; g < header.gasCount && at + 2 * sizeof(int32_t) <= state.size(); g++)
			{
				int32_t element, count;
				memcpy(&element, &state[at], sizeof(element));
				memcpy(&count, &state[at + sizeof(element)], sizeof(count));
				at += 2 * sizeof(int32_t);
				if (count < 0 || at + (size_t)count * sizeof(SavedEntry) > state.size()) break;
				const bool known = element >= 0 && element < 0xFFFF && elements->IsGas((uint16_t)element);
				float* masses = known ? layer.kernel.Mass(element) : nullptr;
				for (int32_t i = 0; i < count; i++, at += sizeof(SavedEntry))
				{
					SavedEntry entry;
					memcpy(&entry, &state[at], sizeof(entry));
					if (!known || entry.cell < 0 || entry.cell >= gameWidth * gameHeight || !(entry.mass > 0.0f) || !(entry.mass < 1e9f))
					{
						skipped++;
						continue;
					}
					const size_t cell = (size_t)(entry.cell / gameWidth + 1) * (size_t)width + (size_t)(entry.cell % gameWidth + 1);
					if (!IsOpen(c, elements, cell))
					{
						skipped++;
						continue;
					}
					if (c.element[cell] == (uint16_t)element)
					{
						// The cell's projection is forgotten: the absorbing pass that runs right after the reset will see
						// a "new" gas in the cell and add its mass to the one recorded here.
						layer.projectedElement[cell] = NoElement;
						layer.projectedMass[cell] = 0.0f;
						merged++;
					}
					masses[cell] = entry.mass;
					taken++;
					mass += entry.mass;
				}
			}
		}
		restoredEntries.store(taken, std::memory_order_relaxed);
		restoredGrams.store((int64_t)(mass * 1000.0), std::memory_order_relaxed);
		restoreSkipped.store(skipped, std::memory_order_relaxed);
		restoreMerged.store(merged, std::memory_order_relaxed);
	}

	// The cell closed to gas (liquid or solid came into it): we hand its gases out to open neighbours.
	// Their heat goes to the neighbours together with the gases: it is written to layer.heatPushes and blended with the neighbours'
	// temperature after all cells have been absorbed (ApplyHeatPushes).
	void PushOut(const Cells& c, const ElementTable* elements, size_t cell, double* pushed, double* lost)
	{
		size_t targets[4];
		int found = 0;
		const ptrdiff_t steps[4] = { -1, 1, -(ptrdiff_t)c.width, (ptrdiff_t)c.width };
		for (ptrdiff_t step : steps)
		{
			const ptrdiff_t n = (ptrdiff_t)cell + step;
			if (n >= 0 && (size_t)n < c.count && IsOpen(c, elements, (size_t)n)) targets[found++] = (size_t)n;
		}
		double capacity = 0;
		for (int32_t gas : layer.kernel.Present())
		{
			float* masses = layer.kernel.Mass(gas);
			const float amount = masses[cell];
			if (amount <= 0.0f) continue;
			masses[cell] = 0.0f;
			if (found == 0)
			{
				*lost += amount;
				const uint16_t closedBy = c.element[cell];
				if (layer.lossBy.size() <= closedBy) layer.lossBy.resize((size_t)closedBy + 1);
				LossStat& stat = layer.lossBy[closedBy];
				stat.mass += amount;
				stat.count++;
				if (amount > stat.biggest) { stat.biggest = amount; stat.cell = (int32_t)cell; stat.gas = (uint16_t)gas; }
				continue;
			}
			for (int i = 0; i < found; i++) masses[targets[i]] += amount / found;
			*pushed += amount;
			capacity += (double)amount * elements->elements[gas].heat;
		}
		// Temperature of the cell's gases: the last one the layer knew for it (the cell itself already has the incoming substance's temperature).
		const float temperature = layer.lastTemperature[cell];
		if (capacity > 0 && temperature > 0.0f)
		{
			for (int i = 0; i < found; i++) layer.heatPushes.push_back(HeatPush{ targets[i], (float)(capacity / found), temperature });
		}
	}

	// Blends the temperature of cells with the heat of gases handed to them by closed neighbours.
	void ApplyHeatPushes(const Cells& c, const ElementTable* elements)
	{
		for (const HeatPush& push : layer.heatPushes)
		{
			double capacity = 0;
			for (int32_t gas : layer.kernel.Present()) capacity += (double)layer.kernel.Mass(gas)[push.cell] * elements->elements[gas].heat;
			if (!(capacity > 0)) continue;
			// Before the portion arrived, the cell held the rest of the heat capacity at the cell temperature; in an empty cell
			// (the simulation keeps zero for vacuum) the temperature becomes the portion's temperature.
			const bool gasNow = elements->IsGas(c.element[push.cell]) && c.temperature[push.cell] > 0.0f;
			const double before = capacity > push.capacity ? capacity - push.capacity : 0.0;
			const float mixed = gasNow ? (float)((before * c.temperature[push.cell] + (double)push.capacity * push.temperature) / (before + push.capacity)) : push.temperature;
			layer.lastTemperature[push.cell] = mixed;
			if (gasNow) c.temperature[push.cell] = mixed;
			heatPushed.fetch_add(1, std::memory_order_relaxed);
		}
		layer.heatPushes.clear();
	}

	// ---------- Gas the simulation writes over another gas ----------
	// In several places the simulation adds gas to a cell itself, bypassing AddGas: evaporation from liquid (PostProcessCell),
	// sublimation of solids (DoSublimation), partial boil-off (DoPartialHeatTransition), emission on the game's
	// EmitMass message (ProcessMassEmission: plants, rockets, turbine). If the cell holds another gas, it first
	// displaces it to neighbours (DisplaceGas), and then writes the new gas into the cell entirely: element, mass, temperature.
	// With the layer the new gas should simply be added to the mixture. Therefore displacement from these places is not performed: the cell's
	// mixture is "hidden" (the main gas's mass stays with the layer, the cell is shown to the simulation as empty), the simulation
	// writes the new substance into it, and at the first opportunity (before handing the frame to the game and at the start of its
	// pass) the layer takes the written gas into the mixture and gives the cell back its previous main gas. If something other than gas was written,
	// the whole mixture is handed to neighbours when the cell is absorbed, as on an ordinary cell closing.
	bool HideForArrival(SimData* sim, const ElementTable* elements, int32_t cell);
	bool LayerReady(const ElementTable* elements, int32_t cell);

	// ---------- Pressure limit for gas emission ----------
	// A solid cell (Oxylite, Polluted Dirt) and a liquid surface (Polluted Water) emit gas into the neighbouring
	// gas cell while it holds less than 1.8 kg, and add no more than what is missing to 1.8 kg. The simulation reads the mass
	// from the cell, that is, it sees only the main gas: in a mixture emission would go up to 1.8 kg of main gas on top of
	// the others, and a gas that is not main in the cell would be emitted without limit (no main gas is added from it).
	// Both checks sit in cell processing (PostProcessCell): a gas cell looks at its solid neighbours
	// (DoSublimation), a liquid one at the cell above it. For the duration of processing a marked cell, the gas cell
	// is shown the mass of the whole mixture, afterwards the main gas's mass is restored; what the simulation added meanwhile
	// stays. Cells are marked by the projection pass: a gas cell with a mixture next to a gas-emitting solid
	// (LimitSelf), and a gas-emitting liquid under a gas cell with a mixture (LimitAbove). If the simulation
	// decides to write another gas into the cell, the mixture is hidden (HideForArrival), and the mass is restored before that.
	// The simulation calls PostProcessCell sequentially, in one thread, so there is only one substituted cell at any moment.
	const uint8_t LimitSelf = 1, LimitAbove = 2;
	struct LimitRaise
	{
		int32_t cell = -1;
		uint16_t element = 0;
		float real = 0.0f, shown = 0.0f; // mass of the main gas and the mass shown to the simulation
	};
	LimitRaise limitRaise; // under the layer lock

	void LowerAfterLimit(SimData* sim)
	{
		if (limitRaise.cell < 0) return;
		const LimitRaise raise = limitRaise;
		limitRaise.cell = -1;
		SimCells* cells = sim->updatedCells;
		if ((size_t)raise.cell >= cells->mass.size() || (size_t)raise.cell >= cells->elementIdx.size()) return;
		float& mass = cells->mass.first[raise.cell];
		if (cells->elementIdx.first[raise.cell] != raise.element)
		{
			limitUnrestored.fetch_add(1, std::memory_order_relaxed);
			return;
		}
		// Nothing was added: the mass is restored bit for bit.
		if (mass == raise.shown)
		{
			mass = raise.real;
			return;
		}
		const float changed = raise.real + (mass - raise.shown);
		mass = changed > 0.0f ? changed : 0.0f;
	}

	void RaiseForLimit(SimData* sim, const ElementTable* elements, int32_t cell)
	{
		SimCells* cells = sim->updatedCells;
		if (cell < 0 || (size_t)cell >= cells->mass.size() || (size_t)cell >= cells->elementIdx.size() || !LayerReady(elements, cell) || layer.hidden[cell]) return;
		const uint16_t main = cells->elementIdx.first[cell];
		const float extra = layer.shownExtra[cell];
		if (!elements->IsGas(main) || main != layer.projectedElement[cell] || extra == 0.0f) return;
		limitRaise.cell = cell;
		limitRaise.element = main;
		limitRaise.real = cells->mass.first[cell];
		limitRaise.shown = limitRaise.real + extra > 0.0f ? limitRaise.real + extra : 0.0f;
		cells->mass.first[cell] = limitRaise.shown;
		limitRaised.fetch_add(1, std::memory_order_relaxed);
	}

	// ---------- Gas displacement the simulation failed at ----------
	// Before writing liquid or solid into a gas cell, the simulation displaces its gas (DisplaceGas): it looks
	// nearby for a cell with the same gas or vacuum and moves the gas there. If there is no such cell, displacement fails,
	// and then, for example, a falling drop and the cell's gas destroy each other (AddLiquid: the smaller is subtracted
	// from the larger). The simulation sees only the main gas of neighbours, but with the layer the gas can also go to a neighbour with a different
	// main gas: the cell's main gas goes into the mixtures of the open neighbours in equal parts, with heat and germs, the cell
	// is cleared the same way the simulation would clear it, and the displacement counts as successful. The cell's other gases
	// are handed out by the layer itself when it absorbs the cell with the new substance. Without open neighbours everything stays as in the game.
	// The layer lock is held.
	bool DisplaceByLayer(SimData* sim, const ElementTable* elements, int32_t cell)
	{
		if (!LayerReady(elements, cell)) return false;
		if (limitRaise.cell == cell) LowerAfterLimit(sim);
		SimCells* cells = sim->updatedCells;
		const size_t count = cells->elementIdx.size();
		if ((size_t)cell >= count || cells->mass.size() < count || cells->temperature.size() < count || cells->properties.size() < count) return false;
		const uint16_t main = cells->elementIdx.first[cell];
		const float mass = cells->mass.first[cell], temperature = cells->temperature.first[cell];
		if (!elements->IsGas(main) || main != layer.projectedElement[cell] || layer.hidden[cell] || !(mass > 0.0f)) return false;
		int32_t targets[4];
		int found = 0;
		const int32_t steps[4] = { -1, 1, -sim->width, sim->width };
		for (int32_t step : steps)
		{
			const int32_t n = cell + step;
			if (n < 0 || (size_t)n >= count || !LayerReady(elements, n) || layer.hidden[n]) continue;
			const uint16_t theirs = cells->elementIdx.first[n];
			if (!elements->IsGas(theirs) || theirs != layer.projectedElement[n] || (cells->properties.first[n] & 1) != 0) continue;
			targets[found++] = n;
		}
		if (found == 0) return false;
		const float part = mass / found;
		const double partHeat = (double)part * elements->elements[main].heat;
		for (int i = 0; i < found; i++)
		{
			const int32_t n = targets[i];
			const uint16_t theirs = cells->elementIdx.first[n];
			// The neighbour's heat capacity before the portion arrives: main gas from the simulation, the rest from the layer.
			double capacity = (double)cells->mass.first[n] * elements->elements[theirs].heat;
			for (int32_t gas : layer.kernel.Present())
			{
				if ((uint16_t)gas != theirs) capacity += (double)layer.kernel.Mass(gas)[n] * elements->elements[gas].heat;
			}
			// The layer takes the neighbour's main gas mass from the simulation and keeps the other gases itself.
			if (theirs == main) cells->mass.first[n] += part;
			else layer.kernel.Mass(main)[n] += part;
			const float theirTemperature = cells->temperature.first[n];
			if (theirTemperature > 0.0f && temperature > 0.0f && capacity + partHeat > 0)
			{
				const float mixed = (float)((capacity * theirTemperature + partHeat * temperature) / (capacity + partHeat));
				cells->temperature.first[n] = mixed;
				layer.lastTemperature[n] = mixed;
			}
		}
		const bool germsReadable = cells->diseaseIdx.size() >= count && cells->diseaseCount.size() >= count;
		if (germsReadable && cells->diseaseIdx.first[cell] != 0xFF && cells->diseaseCount.first[cell] > 0)
		{
			typedef void (*AddDiseaseToCellFn)(void* diseases, SimCells* cells, int32_t cell, uint8_t diseaseIdx, int32_t diseaseCount);
			static const AddDiseaseToCellFn add = (AddDiseaseToCellFn)simhook_find("Disease::AddDiseaseToCell");
			const uint8_t kind = cells->diseaseIdx.first[cell];
			int32_t left = cells->diseaseCount.first[cell];
			// The function does not need the disease table (first parameter) when the cell has the same germ kind or none.
			for (int i = 0; i < found && left > 0 && add; i++)
			{
				const uint8_t theirs = cells->diseaseIdx.first[targets[i]];
				if (theirs != kind && theirs != 0xFF) continue;
				add(nullptr, cells, targets[i], kind, left);
				left = 0;
			}
			if (left > 0) displacedGermsLost.fetch_add(left, std::memory_order_relaxed);
		}
		cells->elementIdx.first[cell] = sim->vacuumElementIdx;
		cells->mass.first[cell] = 0.0f;
		cells->temperature.first[cell] = 0.0f;
		if (germsReadable)
		{
			cells->diseaseIdx.first[cell] = 0xFF;
			cells->diseaseCount.first[cell] = 0;
			if ((size_t)cell < cells->diseaseInfestationTickCount.size()) cells->diseaseInfestationTickCount.first[cell] = 0;
			if ((size_t)cell < cells->diseaseGrowthAccumulatedError.size()) cells->diseaseGrowthAccumulatedError.first[cell] = 0.0f;
		}
		layer.kernel.Mass(main)[cell] = 0.0f;
		layer.projectedMass[cell] = 0.0f;
		if (layer.changeSubstance) layer.changeSubstance(sim->simEvents, sim, (uint64_t)cell);
		displacedByLayer.fetch_add(1, std::memory_order_relaxed);
		displacedByLayerGrams.fetch_add((int64_t)(mass * 1000.0f), std::memory_order_relaxed);
		return true;
	}

	// What is done before processing a marked cell; the layer lock is held.
	void RaiseBeforePostProcess(SimData* sim, const ElementTable* elements, int32_t cell)
	{
		LowerAfterLimit(sim);
		if (!elements || !layer.own || resetRequested || elements != layer.elements || cell < 0 || (size_t)cell >= layer.limitKind.size()) return;
		const SimCells* cells = sim->updatedCells;
		if ((size_t)cell >= cells->elementIdx.size()) return;
		const uint8_t kind = layer.limitKind[cell];
		const uint16_t element = cells->elementIdx.first[cell];
		const uint8_t state = element < elements->elements.size() ? elements->elements[element].state : (uint8_t)SIM_SOLID;
		if ((kind & LimitSelf) != 0 && state == SIM_GAS) RaiseForLimit(sim, elements, cell);
		else if ((kind & LimitAbove) != 0 && state == SIM_LIQUID) RaiseForLimit(sim, elements, cell + sim->width);
	}

	void ResolveHidden(SimData* sim, const ElementTable* elements)
	{
		if (layer.hiddenCells.empty()) return;
		SimCells* cells = sim->updatedCells;
		const bool germsReadable = cells->diseaseIdx.size() >= layer.cells && cells->diseaseCount.size() >= layer.cells;
		for (const HiddenCell& hid : layer.hiddenCells)
		{
			const int32_t cell = hid.cell;
			const uint16_t now = cells->elementIdx.first[cell], main = layer.projectedElement[cell];
			if (now != sim->vacuumElementIdx && !elements->IsGas(now))
			{
				// Liquid or solid arrived: the cell will be handled by absorbing (the hidden mark stays until then).
				arrivalsClosed.fetch_add(1, std::memory_order_relaxed);
				continue;
			}
			layer.hidden[cell] = 0;
			float temperature = hid.temperature;
			const float arrived = elements->IsGas(now) ? cells->mass.first[cell] : 0.0f;
			if (arrived > 0.0f)
			{
				// Mixture temperature together with the arrived gas: by heat content.
				double capacity = 0;
				for (int32_t gas : layer.kernel.Present()) capacity += (double)layer.kernel.Mass(gas)[cell] * elements->elements[gas].heat;
				const double added = (double)arrived * elements->elements[now].heat;
				const float theirs = cells->temperature.first[cell];
				if (theirs > 0.0f && temperature > 0.0f && capacity + added > 0) temperature = (float)((capacity * temperature + added * theirs) / (capacity + added));
				else if (!(temperature > 0.0f)) temperature = theirs;
				layer.kernel.Mass(now)[cell] += arrived;
				arrivalsMixedGrams.fetch_add((int64_t)(arrived * 1000.0f), std::memory_order_relaxed);
			}
			// The cell again shows the simulation its previous main gas.
			const float mainMass = elements->IsGas(main) ? layer.kernel.Mass(main)[cell] : 0.0f;
			if (mainMass > 0.0f)
			{
				cells->elementIdx.first[cell] = main;
				cells->mass.first[cell] = mainMass;
				cells->temperature.first[cell] = temperature;
				layer.projectedMass[cell] = mainMass;
				layer.lastTemperature[cell] = temperature;
				if (now != main && layer.changeSubstance) layer.changeSubstance(sim->simEvents, sim, (uint64_t)cell);
			}
			// Germs that were in the cell before the write: the simulation erased them or replaced them with its own.
			if (hid.germs > 0 && germsReadable)
			{
				const uint8_t kind = cells->diseaseIdx.first[cell];
				if (kind == 0xFF || kind == hid.germ)
				{
					cells->diseaseIdx.first[cell] = hid.germ;
					cells->diseaseCount.first[cell] = (kind == 0xFF ? 0 : cells->diseaseCount.first[cell]) + hid.germs;
				}
				else arrivalsGermsLost.fetch_add(hid.germs, std::memory_order_relaxed);
			}
		}
		layer.hiddenCells.clear();
	}

	// Hides the cell's mixture before the simulation writes a new substance into it. true: the simulation can be told
	// that the gas was "displaced" from the cell. The layer lock is held.
	bool HideForArrival(SimData* sim, const ElementTable* elements, int32_t cell)
	{
		if (!LayerReady(elements, cell)) return false;
		SimCells* cells = sim->updatedCells;
		if ((size_t)cell >= cells->elementIdx.size() || (size_t)cell >= cells->mass.size() || (size_t)cell >= cells->temperature.size()) return false;
		// The cell was shown the mass of the whole mixture (pressure limit): from here on the real main gas mass is needed.
		if (limitRaise.cell == cell) LowerAfterLimit(sim);
		// A second write into the same cell within a tick: resolve the first one first.
		if (layer.hidden[cell]) ResolveHidden(sim, elements);
		const uint16_t main = cells->elementIdx.first[cell];
		if (!elements->IsGas(main) || main != layer.projectedElement[cell] || layer.hidden[cell]) return false;
		HiddenCell hid = { cell, cells->temperature.first[cell], 0xFF, 0 };
		// The layer takes the main gas's mass from the simulation: consumers may have changed it since the last pass.
		layer.kernel.Mass(main)[cell] = cells->mass.first[cell];
		cells->mass.first[cell] = 0.0f;
		if ((size_t)cell < cells->diseaseIdx.size() && (size_t)cell < cells->diseaseCount.size() && cells->diseaseIdx.first[cell] != 0xFF && cells->diseaseCount.first[cell] > 0)
		{
			hid.germ = cells->diseaseIdx.first[cell];
			hid.germs = cells->diseaseCount.first[cell];
			cells->diseaseIdx.first[cell] = 0xFF;
			cells->diseaseCount.first[cell] = 0;
		}
		layer.hidden[cell] = 1;
		layer.hiddenCells.push_back(hid);
		arrivalsHidden.fetch_add(1, std::memory_order_relaxed);
		return true;
	}

	void MoveStep(SimData* sim, const ElementTable* elements, bool sendEvents)
	{
		const int64_t start = NowMicros();
		SimCells* target = sim->updatedCells;
		Cells c;
		c.element = target->elementIdx.first;
		c.mass = target->mass.first;
		c.temperature = target->temperature.first;
		c.properties = target->properties.first;
		c.width = sim->width;
		c.count = target->elementIdx.size();
		if (target->mass.size() < c.count) c.count = target->mass.size();
		if (target->temperature.size() < c.count) c.count = target->temperature.size();
		if (target->properties.size() < c.count) c.count = target->properties.size();
		if (c.width <= 0 || c.count < (size_t)c.width * 3) return;
		c.count -= c.count % c.width;
		if (resetRequested || !layer.own || c.count != layer.cells || elements != layer.elements)
		{
			ResetOwn(sim, c, elements);
			if (resetRequested) resets.fetch_add(1, std::memory_order_relaxed);
			RestoreSaved(c, elements);
			resetRequested = false;
		}
		if (sendEvents && !layer.lookedForChangeSubstance)
		{
			layer.lookedForChangeSubstance = true;
			// If the survey hook is installed, we call the original directly; otherwise the simulation function itself.
			layer.changeSubstance = ChangeSubstance_original ? (ChangeSubstanceFn)ChangeSubstance_original
				: (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
		}

		// The simulation computes not the whole grid but only the active regions (open worlds). Outside them it does not move gas,
		// does not remove small masses and does not vent gas into space, so the layer does not move or write anything there either:
		// otherwise the gas of unopened worlds would spread unattended. While the regions are the same, the layer walks only their
		// cells; when they change (a new world is opened), one pass goes over the whole grid.
		GasMixKernel& kernel = layer.kernel;
		const int32_t rows = (int32_t)(c.count / (size_t)c.width);
		std::vector<GasMixRegion> regions;
		for (const SimActiveRegion* region = sim->activeRegions.first; region != sim->activeRegions.last; region++)
		{
			GasMixRegion r = { region->minX < 0 ? 0 : region->minX, region->minY < 0 ? 0 : region->minY,
				region->maxX > c.width ? c.width : region->maxX, region->maxY > rows ? rows : region->maxY };
			if (r.minX < r.maxX && r.minY < r.maxY) regions.push_back(r);
		}
		const bool everything = layer.activeCount < 0 || !(regions == layer.regions);
		if (everything)
		{
			layer.regions = regions;
			kernel.SetRegions(regions);
			layer.active.assign(c.count, 0);
			layer.activeCount = 0;
			kernel.ForSpans([&](size_t first, size_t last)
			{
				memset(&layer.active[first], 1, last - first);
				layer.activeCount += (int64_t)(last - first);
			});
		}
		const uint8_t* active = layer.active.data();
		const uint8_t* zones = sim->worldZones;
		// Walking cells: all of them (when regions change) or only the cells of active regions.
		auto forCells = [&](bool all, auto&& fn)
		{
			if (all)
			{
				for (size_t cell = 0; cell < c.count; cell++) fn(cell);
				return;
			}
			kernel.ForSpans([&](size_t first, size_t last)
			{
				for (size_t cell = first; cell < last; cell++) fn(cell);
			});
		};

		float* open = kernel.Open();
		const size_t numElements = elements->elements.size();
		int64_t replaced = 0, vanished = 0, closed = 0, inSpace = 0, tiny = 0, scaled = 0;
		double pushed = 0, lost = 0;
		layer.heatPushes.clear();
		ResolveHidden(sim, elements);

		// 1. Absorb what the game and the simulation did to the cells after our last write.
		forCells(everything, [&](size_t cell)
		{
			const uint16_t element = c.element[cell];
			const uint8_t state = element < numElements ? elements->elements[element].state : (uint8_t)SIM_SOLID;
			const uint16_t before = layer.projectedElement[cell];
			const bool beforeGas = elements->IsGas(before);
			if (before != element && before < numElements)
			{
				// Survey: the cell changed state since the last pass (one gas replaced by another does not count).
				const uint8_t was = elements->elements[before].state & 3;
				if (was != (state & 3))
				{
					layer.transitions[was][state & 3]++;
					if (layer.transitionsTo.size() < numElements) layer.transitionsTo.resize(numElements, 0);
					layer.transitionsTo[element]++;
				}
			}
			if (state == SIM_GAS)
			{
				if (element == before)
				{
					kernel.Mass(element)[cell] = c.mass[cell];
					// Heat between cells, with buildings and walls is computed by the simulation, and it computes as if the cell held
					// only the main gas: the temperature of a cell with a mixture would change faster than its heat capacity allows.
					// The temperature change per tick is reduced to the main gas's share of the mixture's heat capacity: then
					// the heat the simulation gave the cell is spread over the whole mixture.
					const float share = layer.mainShare[cell];
					if (share != 1.0f)
					{
						const float now = c.temperature[cell], last = layer.lastTemperature[cell];
						if (now > 0.0f && last > 0.0f && now != last)
						{
							c.temperature[cell] = last + (now - last) * share;
							scaled++;
						}
					}
				}
				else
				{
					if (beforeGas)
					{
						kernel.Mass(before)[cell] = 0.0f;
						replaced++;
					}
					kernel.Mass(element)[cell] += c.mass[cell];
				}
			}
			else if (state == SIM_VACUUM)
			{
				if (beforeGas && layer.projectedMass[cell] > 0.0f)
				{
					kernel.Mass(before)[cell] = 0.0f;
					vanished++;
					if (zones && zones[cell] == 0xFF) inSpace++;
					else if (layer.projectedMass[cell] < 0.001f) tiny++;
				}
			}
			else if (open[cell] != 0.0f || beforeGas)
			{
				// The simulation displaced or destroyed the main gas itself; we hand the rest to neighbours. If the mixture was
				// hidden from the simulation (it was not allowed to displace the main gas), the whole mixture goes to the neighbours.
				// Handing out is postponed until the end of absorbing: a neighbour the layer absorbs later than this cell would write
				// its main gas mass from the simulation over the handed-out gas, and the gas handed to it would be lost.
				if (beforeGas && !layer.hidden[cell]) kernel.Mass(before)[cell] = 0.0f;
				layer.hidden[cell] = 0;
				layer.closing.push_back((uint32_t)cell);
				closed++;
			}
			open[cell] = active[cell] && IsOpen(c, elements, cell) ? 1.0f : 0.0f;
			// For cells the layer does not touch, the projection is whatever is in the simulation.
			layer.projectedElement[cell] = element;
			layer.projectedMass[cell] = state == SIM_GAS ? c.mass[cell] : 0.0f;
		});
		for (uint32_t cell : layer.closing) PushOut(c, elements, cell, &pushed, &lost);
		layer.closing.clear();
		ApplyHeatPushes(c, elements);
		heatScaled.fetch_add(scaled, std::memory_order_relaxed);
		if (everything)
		{
			layer.idleGasCells = 0;
			layer.idleGasMass = 0;
			for (size_t cell = 0; cell < c.count; cell++)
			{
				if (active[cell] || !elements->IsGas(c.element[cell])) continue;
				layer.idleGasCells++;
				layer.idleGasMass += c.mass[cell];
			}
		}

		// Breathing: the mod's requests to take non-main breathable gas. The cell's main gas is known by this point
		// (it has just been absorbed); the mass is taken from the cell's other breathable gases in proportion to their shares.
		{
			std::vector<BreathRequest> requests;
			{
				std::lock_guard<std::mutex> lock(breathGuard);
				requests.swap(breathRequests);
			}
			const int32_t gameWidth = c.width - 2;
			const bool germsWritable = target->diseaseIdx.size() >= c.count && target->diseaseCount.size() >= c.count;
			double breathed = 0, shortage = 0;
			for (const BreathRequest& request : requests)
			{
				if (gameWidth <= 0 || request.cell < 0 || !(request.mass > 0.0f)) continue;
				const size_t cell = (size_t)(request.cell / gameWidth + 1) * (size_t)c.width + (size_t)(request.cell % gameWidth + 1);
				if (cell >= c.count) continue;
				float available = 0.0f;
				for (int32_t gas : kernel.Present())
				{
					if ((elements->elements[gas].flags & ElementBreathable) != 0 && layer.projectedElement[cell] != (uint16_t)gas) available += kernel.Mass(gas)[cell];
				}
				const float taken = request.mass < available ? request.mass : available;
				if (taken > 0.0f)
				{
					const float keep = 1.0f - taken / available;
					for (int32_t gas : kernel.Present())
					{
						if ((elements->elements[gas].flags & ElementBreathable) != 0 && layer.projectedElement[cell] != (uint16_t)gas) kernel.Mass(gas)[cell] *= keep;
					}
				}
				breathed += taken;
				shortage += request.mass - taken;
				if (request.germs > 0 && germsWritable && target->diseaseIdx.first[cell] != 0xFF)
				{
					int32_t& count = target->diseaseCount.first[cell];
					const int32_t leaving = request.germs < count ? request.germs : count;
					count -= leaving;
					if (count <= 0)
					{
						count = 0;
						target->diseaseIdx.first[cell] = 0xFF;
					}
					breathGerms.fetch_add(leaving, std::memory_order_relaxed);
				}
			}
			breathedGrams.fetch_add((int64_t)(breathed * 1000.0), std::memory_order_relaxed);
			breathShortGrams.fetch_add((int64_t)(shortage * 1000.0), std::memory_order_relaxed);
		}

		// 2. Layer step. Cell temperature is taken from the simulation: it computes heat between cells.
		// The simulation keeps zero for vacuum. If the layer still has gases in such a cell (the simulation removed only
		// the main one), they get back the last known cell temperature: otherwise the gas would be at zero
		// kelvin and the simulation would freeze it right away.
		// Along the way we collect cells with germs that gas flows through (see "Germs" below).
		const int MaxKinds = GasMixKernel::MaxPassive;
		const bool germsReadable = target->diseaseIdx.size() >= c.count && target->diseaseCount.size() >= c.count;
		uint8_t* germKind = target->diseaseIdx.first;
		int32_t* germCount = target->diseaseCount.first;
		bool kindPresent[GasMixKernel::MaxPassive] = {};
		// Passenger fields after the last pass are non-zero only where the layer touched them: that is where we clear them.
		float* germField[GasMixKernel::MaxPassive] = {};
		for (int kind = 0; kind < MaxKinds; kind++) germField[kind] = kernel.HasPassive(kind) ? kernel.Passive(kind) : nullptr;
		for (uint32_t cell : layer.germTouched) for (int kind = 0; kind < MaxKinds; kind++) if (germField[kind]) germField[kind][cell] = 0.0f;
		layer.germCells.clear();
		layer.germTouched.clear();
		float* heat = kernel.Temperature();
		forCells(false, [&](size_t cell)
		{
			const float t = c.temperature[cell];
			if (t > 0.0f) layer.lastTemperature[cell] = t;
			heat[cell] = t > 0.0f ? t : layer.lastTemperature[cell];
			if (germsReadable && germKind[cell] < MaxKinds && open[cell] != 0.0f && germCount[cell] > 0)
			{
				kindPresent[germKind[cell]] = true;
				layer.germCells.push_back((uint32_t)cell);
			}
		});

		// Germs. A simulation cell has one germ kind and one count; the simulation used to carry them with gas in
		// its pressure flow, which the layer has turned off. Now they are carried by the layer step: the count of each germ kind
		// travels as a passenger (GasMixKernel::AdvectPassive), that is, it leaves a cell with the same share as the cell's mass,
		// and is mixed by diffusion together with the gases. The simulation remains the owner of germs (its own
		// diffusion, growth and death work as before): each pass the layer reads the counts from cells and writes them back.
		// The count in a cell is an integer, so the fractional remainder is remembered and returned in the next pass.
		// Per step germs move no further than the neighbouring cell, so it is enough to read and write the cells with germs
		// and their neighbours, not all region cells.
		const int64_t germStart = NowMicros();
		int kinds = 0;
		for (int kind = 0; kind < MaxKinds; kind++)
		{
			if (!kindPresent[kind]) continue;
			kinds++;
			germField[kind] = kernel.Passive(kind);
		}
		if (kinds > 0)
		{
			if (++layer.germPass == 0)
			{
				std::fill(layer.germStamp.begin(), layer.germStamp.end(), 0u);
				layer.germPass = 1;
			}
			const ptrdiff_t around[5] = { 0, -1, 1, -(ptrdiff_t)c.width, (ptrdiff_t)c.width };
			for (uint32_t cell : layer.germCells)
			{
				const uint8_t kind = germKind[cell];
				germField[kind][cell] = (float)germCount[cell] + (layer.germFractionKind[cell] == kind ? layer.germFraction[cell] : 0.0f);
				for (ptrdiff_t step : around)
				{
					const ptrdiff_t n = (ptrdiff_t)cell + step;
					if (n < 0 || (size_t)n >= c.count || layer.germStamp[n] == layer.germPass) continue;
					layer.germStamp[n] = layer.germPass;
					layer.germTouched.push_back((uint32_t)n);
				}
			}
		}
		int64_t germTime = NowMicros() - germStart;
		const int64_t kernelStart = NowMicros();
		layer.params.byPressure = byPressure.load(std::memory_order_relaxed) != 0 ? 1 : 0;
		kernel.Step(layer.params);
		const int64_t projectStart = NowMicros();
		// Germs are usually few (tens of cells across the whole base), so their transport is not computed by the whole step,
		// but separately over a list of cells, with the same coefficients.
		for (int kind = 0; kind < MaxKinds; kind++)
		{
			if (kindPresent[kind]) kernel.AdvectPassive(kind, layer.germTouched.data(), layer.germTouched.size());
		}

		// Cells of the Void element (grid edge): gas flows into them as into vacuum and disappears. The simulation
		// does the same (UpdatePressure). Space above the surface works differently: there it is ordinary vacuum in the space zone,
		// and the simulation itself reduces the cell's mass by 20 g per tick; in the layer this falls on the main gas.
		const std::vector<int32_t>& present = kernel.Present();
		const uint16_t space = sim->voidElementIdx;
		double toVoid = 0;
		forCells(false, [&](size_t cell)
		{
			if (c.element[cell] != space) return;
			for (int32_t gas : present)
			{
				float* field = kernel.Mass(gas);
				toVoid += field[cell];
				field[cell] = 0.0f;
			}
		});

		// Space zone (cell without a back wall): the simulation reduces the cell's mass by 20 g per tick, and a cell lighter
		// than that is emptied entirely. From the projection it sees only the main gas, so the layer removes a mixture lighter than 20 g
		// itself: otherwise every tick the simulation would erase just the main gas, the rest would spread through space
		// as traces, and the cell would flicker between gas and vacuum. In cells heavier than 20 g the draining is left to the simulation
		// and falls on the main gas.
		double toSpace = 0;
		const SimBackwall* backwall = sim->backwall;
		if (zones && backwall && backwall->elementIdx.size() >= c.count)
		{
			const uint16_t* wall = backwall->elementIdx.first;
			const uint16_t noWall = backwall->vacuumElementIdx;
			forCells(false, [&](size_t cell)
			{
				if (zones[cell] != 0xFF || open[cell] == 0.0f || wall[cell] != noWall) return;
				float total = 0.0f;
				for (int32_t gas : present) total += kernel.Mass(gas)[cell];
				if (total <= 0.0f || total > SpaceDrain) return;
				for (int32_t gas : present) kernel.Mass(gas)[cell] = 0.0f;
				toSpace += total;
			});
		}

		// 2b. Evaporation and boiling (phase rules). Vapour enters the gas cell's mixture at the liquid's temperature,
		// the latent heat is given up by the liquid, which cools down.
		const PhaseTables* phases = publishedPhases.load(std::memory_order_acquire);
		const bool phaseRules = phaseOn.load(std::memory_order_relaxed) != 0 && phases && phases->gasVapour.size() == numElements;
		layer.condensed.clear();
		if (phaseRules)
		{
			const int64_t phaseStart = NowMicros();
			float* warmth = kernel.Temperature();
			int64_t times = 0, boiled = 0, whole = 0;
			double gone = 0, solid = 0;
			// A gas cell's mixture: pressure in atmospheres (by mass or by number of molecules, as selected) and heat capacity.
			auto gasState = [&](size_t cell, double* capacity, float* temperature)
			{
				double moles = 0, total = 0;
				*capacity = 0;
				for (int32_t gas : kernel.Present())
				{
					const float m = kernel.Mass(gas)[cell];
					if (!(m > 0.0f)) continue;
					total += m;
					moles += (double)m / elements->elements[gas].molar;
					*capacity += (double)m * elements->elements[gas].heat;
				}
				*temperature = *capacity > 0 ? warmth[cell] : 0.0f;
				return phases->scale.Pressure((float)total, (float)moles, *temperature);
			};
			// Vapours of a cell with a liquid mixture (service of the liquid mixture mod; absent without it): each vapour of the mixture has its own
			// equilibrium, against its own partial pressure in the gas. A cell without a mixture gives one vapour: its own.
			static const LiquidVapoursFn mixVapours = (LiquidVapoursFn)simhook_service("liquid-mix.vapours");
			auto vapoursOf = [&](size_t cell, LiquidVapourEntry* out) -> int32_t
			{
				const int32_t count = mixVapours ? mixVapours(sim, (int32_t)cell, c.temperature[cell], out) : 0;
				if (count > 0) return count;
				const uint16_t liquid = c.element[cell];
				out[0] = { elements->elements[liquid].highTransition, liquid, 1.0f };
				return 1;
			};
			// A portion x of the liquid of cell from becomes vapour in gas cell to. gas and source: which vapour leaves and by
			// which liquid to compute its latent heat (for a cell without a mixture, 0xFFFF and its substance).
			auto vaporize = [&](size_t from, size_t to, float x, double capacity, float gasTemperature, bool boiling, uint16_t gas, uint16_t source)
			{
				const uint16_t liquid = c.element[from];
				const ElementInfo& liquidInfo = elements->elements[liquid];
				const phase::Vapour& vapour = phases->liquidVapour[source < numElements ? source : liquid];
				const uint16_t gasElement = liquidInfo.highTransition;
				const float liquidMass = c.mass[from], liquidTemperature = c.temperature[from];
				bool all = liquidMass - x < 1e-4f;
				if (all) x = liquidMass;
				// The cell holds a liquid mixture (liquid mixture mod): the portion is taken from the liquids that give this vapour, by
				// Raoult's law; the gas goes into the cell as its own, the solid residue is released by the liquids plugin.
				static const LiquidEvaporateFn mixEvaporate = (LiquidEvaporateFn)simhook_service("liquid-mix.evaporate");
				LiquidVapourOut products[4];
				float taken = 0.0f;
				const int32_t mixed = mixEvaporate ? mixEvaporate(sim, (int32_t)from, x, liquidTemperature, gas, products, &taken) : 0;
				if (mixed > 0)
				{
					x = taken;
					all = liquidMass - x < 1e-4f;
					double heat = capacity;
					double energy = capacity * gasTemperature;
					for (int32_t i = 0; i < mixed; i++)
					{
						if (products[i].gas >= numElements || !(products[i].mass > 0.0f)) continue;
						const double added = (double)products[i].mass * elements->elements[products[i].gas].heat;
						heat += added;
						energy += added * liquidTemperature;
						kernel.Mass(products[i].gas)[to] += products[i].mass;
					}
					warmth[to] = heat > 0 ? (float)(energy / heat) : liquidTemperature;
				}
				const float share = mixed > 0 ? 0.0f : (liquidInfo.highOre < numElements && liquidInfo.highOreShare > 0.0f && liquidInfo.highOreShare < 1.0f ? liquidInfo.highOreShare : 0.0f);
				const float steam = mixed > 0 ? 0.0f : x * (1.0f - share);
				if (share > 0.0f)
				{
					float& kept = layer.residue[(uint32_t)from];
					kept += x * share;
					if (kept >= layer.condenseMass || all)
					{
						layer.condensed.push_back(Condensed{ (int32_t)from, NoElement, liquidInfo.highOre, kept, kept, liquidTemperature, 0.0f });
						solid += kept;
						layer.residue.erase((uint32_t)from);
					}
				}
				if (mixed == 0)
				{
					const double added = (double)steam * elements->elements[gasElement].heat;
					warmth[to] = capacity + added > 0 ? (float)((capacity * gasTemperature + added * liquidTemperature) / (capacity + added)) : liquidTemperature;
					kernel.Mass(gasElement)[to] += steam;
				}
				if (all)
				{
					c.element[from] = sim->vacuumElementIdx;
					c.mass[from] = 0.0f;
					c.temperature[from] = 0.0f;
					if (layer.changeSubstance) layer.changeSubstance(sim->simEvents, sim, from);
					whole++;
				}
				else
				{
					c.mass[from] = liquidMass - x;
					const float cooled = liquidTemperature - x * vapour.latent / ((liquidMass - x) * liquidInfo.heat);
					c.temperature[from] = cooled > 1.0f ? cooled : 1.0f;
				}
				times++;
				if (boiling) boiled++;
				gone += x;
				if (layer.evaporatedBy.size() < numElements) layer.evaporatedBy.resize(numElements, 0.0);
				layer.evaporatedBy[liquid] += x;
				if (layer.evaporatedDetail.size() < numElements) layer.evaporatedDetail.resize(numElements);
				layer.evaporatedDetail[liquid].Add(x, gasTemperature > 0.0f ? gasTemperature : liquidTemperature, liquidTemperature, boiling, from);
			};

			// Boiling, including deep in the liquid. Walk by columns top to bottom: the liquid is pressed by the gas of the open
			// cell above the column and by the weight of the liquid above. A liquid whose vapour pressure is higher boils, and the vapour goes
			// into that open cell (bubbles rise through any liquid above them). Under a solid ceiling
			// the vapour has nowhere to go, and the liquid there does not boil.
			// Cells are walked by rows (this is faster for memory), and the state of each column lives in its own record:
			// the open cell above the column, the weight of the liquid above and, when needed, the pressure and heat of its gas.
			struct Column
			{
				size_t outlet;
				double weight, capacity;
				float gasTemperature, gasPressure;
				bool known;
			};
			static std::vector<Column> columns; // simulation thread only, under the layer lock
			for (const GasMixRegion& region : layer.regions)
			{
				columns.assign((size_t)(region.maxX - region.minX), Column{ SIZE_MAX, 0, 0, 0.0f, 0.0f, false });
				for (int32_t y = region.maxY - 1; y >= region.minY; y--)
				{
					size_t cell = (size_t)y * c.width + region.minX;
					for (Column& column : columns)
					{
						const size_t here = cell++;
						if (open[here] != 0.0f)
						{
							column.outlet = c.element[here] == sim->voidElementIdx ? SIZE_MAX : here;
							column.known = false;
							column.weight = 0;
							continue;
						}
						const uint16_t liquid = c.element[here];
						if (liquid >= numElements || elements->elements[liquid].state != SIM_LIQUID)
						{
							column.outlet = SIZE_MAX;
							continue;
						}
						if (column.outlet == SIZE_MAX) continue;
						if (phases->liquidVapour[liquid].Valid() && c.mass[here] > 0.0f && c.temperature[here] > 0.0f)
						{
							const ElementInfo& liquidInfo = elements->elements[liquid];
							LiquidVapourEntry vapours[4];
							const int32_t kinds = vapoursOf(here, vapours);
							bool gone = false;
							for (int32_t k = 0; k < kinds && !gone; k++)
							{
								if (vapours[k].liquid >= numElements || vapours[k].gas >= numElements || !phases->liquidVapour[vapours[k].liquid].Valid()) continue;
								const phase::Vapour& vapour = phases->liquidVapour[vapours[k].liquid];
								const float factor = vapours[k].factor;
								// Cheap cut-off: under the weight of the liquid above alone it no longer boils.
								if (!(factor * vapour.SaturationPressure(c.temperature[here]) > (float)(column.weight * phase::WeightPressure) * phase::BoilPressureMargin)) continue;
								if (!column.known)
								{
									column.gasPressure = gasState(column.outlet, &column.capacity, &column.gasTemperature);
									column.known = true;
								}
								const float amount = phase::Boil(vapour, c.mass[here], c.temperature[here], liquidInfo.heat,
									column.gasPressure + (float)(column.weight * phase::WeightPressure), kernel.Mass(vapours[k].gas)[column.outlet],
									column.gasTemperature, liquidInfo.lowTemp, layer.boilRate, factor);
								if (amount > 1e-5f)
								{
									vaporize(here, column.outlet, amount, column.capacity, column.gasTemperature, true, kinds > 1 || mixVapours ? vapours[k].gas : (uint16_t)0xFFFF, vapours[k].liquid);
									column.known = false; // the open cell's mixture has changed
									gone = c.element[here] != liquid;
								}
							}
							if (gone) { column.outlet = SIZE_MAX; continue; }
						}
						column.weight += c.mass[here];
					}
				}
			}

			// Surface evaporation: a liquid that does not boil gives vapour to a neighbouring open cell while there is
			// less of it there than the liquid's vapour pressure holds and than the cell itself holds at its temperature.
			// The walk goes from gas cells: each one looks at its four neighbours. Boiling to the side of and below the liquid
			// (there is no open cell above it) is computed here as well.
			const ptrdiff_t sides[4] = { -(ptrdiff_t)c.width, -1, 1, (ptrdiff_t)c.width };
			forCells(false, [&](size_t cell)
			{
				if (open[cell] == 0.0f || c.element[cell] == sim->voidElementIdx) return;
				for (int side = 0; side < 4; side++)
				{
					const size_t beside = (size_t)((ptrdiff_t)cell + sides[side]);
					if (beside >= c.count || open[beside] != 0.0f) continue;
					const uint16_t liquid = c.element[beside];
					if (liquid >= numElements || !phases->liquidVapour[liquid].Valid()) continue;
					const ElementInfo& liquidInfo = elements->elements[liquid];
					LiquidVapourEntry vapours[4];
					const int32_t kinds = vapoursOf(beside, vapours);
					for (int32_t k = 0; k < kinds; k++)
					{
						if (c.element[beside] != liquid) break;
						if (vapours[k].liquid >= numElements || vapours[k].gas >= numElements || !phases->liquidVapour[vapours[k].liquid].Valid()) continue;
						const phase::Vapour& vapour = phases->liquidVapour[vapours[k].liquid];
						const float factor = vapours[k].factor;
						const float liquidMass = c.mass[beside], liquidTemperature = c.temperature[beside];
						if (!(liquidMass > 0.0f) || !(liquidTemperature > 0.0f)) break;
						double capacity = 0;
						float gasTemperature = 0.0f;
						const float pressure = gasState(cell, &capacity, &gasTemperature);
						const phase::Surface surface = phase::Evaporate(vapour, liquidMass, liquidTemperature, liquidInfo.heat,
							kernel.Mass(vapours[k].gas)[cell], gasTemperature, pressure, layer.evaporationRate, factor);
						float amount = surface.evaporated;
						// The liquid below this cell (side 0) has already given its share when boiling, in the column walk.
						if (surface.boiling && side != 0)
						{
							const float boilingOff = phase::Boil(vapour, liquidMass, liquidTemperature, liquidInfo.heat, pressure,
								kernel.Mass(vapours[k].gas)[cell], gasTemperature, liquidInfo.lowTemp, layer.boilRate, factor);
							if (boilingOff > amount) amount = boilingOff;
						}
						// Portions smaller than ten milligrams are not counted: equilibrium is almost reached.
						if (!(amount > 1e-5f)) continue;
						vaporize(beside, cell, amount, capacity, gasTemperature, surface.boiling, mixVapours ? vapours[k].gas : (uint16_t)0xFFFF, vapours[k].liquid);
					}
				}
			});
			evaporations.fetch_add(times, std::memory_order_relaxed);
			boilings.fetch_add(boiled, std::memory_order_relaxed);
			evaporatedWhole.fetch_add(whole, std::memory_order_relaxed);
			evaporatedGrams.fetch_add((int64_t)(gone * 1000.0), std::memory_order_relaxed);
			evaporatedOreGrams.fetch_add((int64_t)(solid * 1000.0), std::memory_order_relaxed);
			phaseMicros.fetch_add(NowMicros() - phaseStart, std::memory_order_relaxed);
		}

		// 3. Projection: the main gas by number of molecules and its mass, into the simulation cells.
		std::vector<const float*> masses(present.size());
		std::vector<float> perMolar(present.size()), equivalentPerKg(present.size(), 0.0f);
		const bool pressureMode = byPressure.load(std::memory_order_relaxed) != 0;
		for (size_t i = 0; i < present.size(); i++)
		{
			masses[i] = kernel.Mass(present[i]);
			perMolar[i] = 1.0f / elements->elements[present[i]].molar;
			if (pressureMode) equivalentPerKg[i] = kernel.EquivalentPerKg(present[i], layer.params);
		}
		// Condensation of non-main gases (described at ReleaseCondensate): below which temperature a gas becomes
		// liquid, and which one. Gases without such a transition have a zero threshold.
		std::vector<float> condenseBelow(present.size(), 0.0f), oreShare(present.size(), 0.0f), heatOf(present.size(), 0.0f), conductOf(present.size(), 0.0f);
		std::vector<uint16_t> condenseInto(present.size(), NoElement), oreInto(present.size(), NoElement);
		for (size_t i = 0; i < present.size(); i++)
		{
			const ElementInfo& info = elements->elements[present[i]];
			heatOf[i] = info.heat;
			conductOf[i] = info.conductivity;
			if (info.lowTransition >= elements->elements.size() || elements->elements[info.lowTransition].state != SIM_LIQUID) continue;
			condenseBelow[i] = info.lowTemp - TransitionMargin;
			condenseInto[i] = info.lowTransition;
			if (info.lowOre < elements->elements.size() && info.lowOreShare > 0.0f && info.lowOreShare < 1.0f)
			{
				oreInto[i] = info.lowOre;
				oreShare[i] = info.lowOreShare;
			}
		}
		// Phase rules: which gases of the mixture have a liquid and a saturated vapour pressure.
		std::vector<const phase::Vapour*> saturation(present.size(), nullptr);
		bool anySaturation = false;
		for (size_t i = 0; phaseRules && i < present.size(); i++)
		{
			if (!phases->gasVapour[present[i]].Valid()) continue;
			saturation[i] = &phases->gasVapour[present[i]];
			anySaturation = true;
		}
		double condensedMass = 0;
		float* temperature = kernel.Temperature();
		const uint16_t vacuum = sim->vacuumElementIdx;
		// Gas outside the active regions did not take part in the step and did not change in the simulation: its count and mass were counted
		// at the last region change.
		int64_t gas = layer.idleGasCells, mixed = 0, switched = 0;
		double mainTotal = layer.idleGasMass, layerTotal = layer.idleGasMass, traces = 0;
		// Pressure limit marks (described at the PostProcessCell hook) are set anew every pass.
		for (uint32_t marked : layer.limitCells) layer.limitKind[marked] = 0;
		layer.limitCells.clear();
		auto markLimit = [&](size_t marked, uint8_t kind)
		{
			if (layer.limitKind[marked] == 0) layer.limitCells.push_back((uint32_t)marked);
			layer.limitKind[marked] |= kind;
		};
		const ptrdiff_t limitSides[4] = { -(ptrdiff_t)c.width, -1, 1, (ptrdiff_t)c.width };
		forCells(false, [&](size_t cell)
		{
			layer.otherMass[cell] = 0.0f;
			layer.shownExtra[cell] = 0.0f;
			layer.mainShare[cell] = 1.0f;
			if (open[cell] == 0.0f)
			{
				// Closed gas cell (marked impermeable): its gas did not take part in the step.
				if (elements->IsGas(c.element[cell])) { gas++; mainTotal += c.mass[cell]; layerTotal += c.mass[cell]; }
				return;
			}
			if (c.element[cell] == space) return; // the Void cell stays itself
			const uint16_t before = c.element[cell];
			float best = 0.0f, second = 0.0f, keep = 0.0f, all = 0.0f, cellMass = 0.0f;
			size_t bestIndex = 0, keepIndex = present.size();
			for (size_t i = 0; i < present.size(); i++) cellMass += masses[i][cell];
			if (cellMass > 0.0f && cellMass < TraceMass)
			{
				// A trace of gas: the simulation would erase the main gas of such a cell anyway, and the others would flicker after it.
				for (int32_t gas : present) kernel.Mass(gas)[cell] = 0.0f;
				traces += cellMass;
			}
			// Condensation by the phase rules: any gas of the mixture, the main one too, of which the cell holds more than
			// the saturated vapour pressure holds at its temperature. Not all of the excess leaves: the latent heat warms the cell,
			// and the remainder becomes saturated at the new temperature.
			if (anySaturation && cellMass >= layer.condenseMass)
			{
				float warm = temperature[cell];
				if (!(warm > 0.0f)) warm = layer.lastTemperature[cell];
				for (size_t i = 0; i < present.size() && warm > 0.0f; i++)
				{
					const phase::Vapour* vapour = saturation[i];
					const float m = masses[i][cell];
					if (!vapour || !(m >= layer.condenseMass) || !(warm < vapour->critical) || m - vapour->SaturationMass(warm) < layer.condenseMass) continue;
					// The liquid must have somewhere to appear without trapping the gas: there is an open cell nearby (the drop falls
					// through open cells, and the gas of the cell where it lands will go to a neighbour) or the same liquid lies right below
					// the cell. In a lone gas cell with no exit vapour stays vapour.
					if (!(open[cell - 1] != 0.0f || open[cell + 1] != 0.0f || open[cell + c.width] != 0.0f
						|| (cell >= (size_t)c.width && (open[cell - c.width] != 0.0f || c.element[cell - c.width] == condenseInto[i])))) continue;
					double capacity = 0;
					for (size_t j = 0; j < present.size(); j++) capacity += (double)masses[j][cell] * heatOf[j];
					const float x = phase::Condense(*vapour, m, warm, (float)capacity);
					if (!(x >= layer.condenseMass)) continue;
					const float warmBefore = warm; // gas temperature before condensation, for the survey
					kernel.Mass(present[i])[cell] = m - x;
					// The drop leaves no hotter than its liquid's boiling point (otherwise the simulation will not let it fall);
					// the heat it did not carry away stays with the cell.
					float leave = warm;
					warm = phase::KeepHeat(warm + x * vapour->latent / (float)capacity, (float)capacity, x * heatOf[i],
						elements->elements[condenseInto[i]].highTemp + TransitionMargin - 0.5f, &leave);
					temperature[cell] = warm;
					layer.condensed.push_back(Condensed{ (int32_t)cell, condenseInto[i], oreInto[i], x, x * oreShare[i], leave, cellMass > 0.0f ? x / cellMass : 0.0f });
					cellMass -= x;
					condensedMass += x;
					if (layer.condensedBy.size() < numElements) layer.condensedBy.resize(numElements, 0.0);
					layer.condensedBy[present[i]] += x;
					if (layer.condensedDetail.size() < numElements) layer.condensedDetail.resize(numElements);
					layer.condensedDetail[present[i]].Add(x, warmBefore, leave, false, cell);
				}
			}
			for (size_t i = 0; i < present.size(); i++)
			{
				const float m = masses[i][cell];
				if (m <= 0.0f) continue;
				layerTotal += m;
				const float molecules = m * perMolar[i];
				all += molecules;
				if (molecules > best) { second = best; best = molecules; bestIndex = i; }
				else if (molecules > second) second = molecules;
				if ((uint16_t)present[i] == before) { keep = molecules; keepIndex = i; }
			}
			if (best <= 0.0f)
			{
				if (before != vacuum)
				{
					c.element[cell] = vacuum;
					c.mass[cell] = 0.0f;
					c.temperature[cell] = 0.0f;
					if (layer.changeSubstance) layer.changeSubstance(sim->simEvents, sim, cell);
					switched++;
				}
				layer.projectedElement[cell] = vacuum;
				layer.projectedMass[cell] = 0.0f;
				return;
			}
			// The previous main gas stays main until another one noticeably overtakes it: otherwise with nearly equal
			// shares the main gas would flicker from tick to tick.
			const size_t mainIndex = keepIndex < present.size() && keep >= 0.9f * best ? keepIndex : bestIndex;
			const uint16_t main = (uint16_t)present[mainIndex];
			const float mainMass = masses[mainIndex][cell];
			// The gas temperature must be positive. Zero is possible only if the cell never had a
			// temperature and gas was handed into it when a neighbouring cell closed; then we take room temperature.
			float warm = temperature[cell];
			if (!(warm > 0.0f)) warm = layer.lastTemperature[cell] > 0.0f ? layer.lastTemperature[cell] : 293.15f;
			layer.lastTemperature[cell] = warm;
			c.mass[cell] = mainMass;
			c.temperature[cell] = warm;
			if (main != before)
			{
				c.element[cell] = main;
				if (layer.changeSubstance) layer.changeSubstance(sim->simEvents, sim, cell);
				switched++;
			}
			layer.projectedElement[cell] = main;
			layer.projectedMass[cell] = mainMass;
			float others = cellMass > mainMass ? cellMass - mainMass : 0.0f;
			if (!phaseRules && others >= layer.condenseMass)
			{
				// left: mixture mass until the next gas is taken out of it; capacity and gone: heat capacity of the whole mixture
				// and of the part that left.
				float left = cellMass;
				double capacity = 0, gone = 0;
				for (size_t i = 0; i < present.size(); i++)
				{
					const float m = masses[i][cell];
					if (i == mainIndex || !(m >= layer.condenseMass) || !(warm < condenseBelow[i])) continue;
					if (gone == 0) for (size_t j = 0; j < present.size(); j++) capacity += (double)masses[j][cell] * heatOf[j];
					kernel.Mass(present[i])[cell] = 0.0f;
					// Liquid and ore leave already warmed, as in the simulation.
					layer.condensed.push_back(Condensed{ (int32_t)cell, condenseInto[i], oreInto[i], m, m * oreShare[i], warm + TransitionHeat, left > 0.0f ? m / left : 0.0f });
					left -= m;
					gone += (double)m * heatOf[i];
					others -= m;
					layerTotal -= m;
					condensedMass += m;
				}
				if (others < 0.0f) others = 0.0f;
				if (gone > 0 && capacity > 0)
				{
					// Latent heat: the simulation warms the cell by 1.5 degrees when all its gas condenses;
					// here part of the mixture left, and the cell warms by the same share (by heat capacity).
					warm += TransitionHeat * (float)(gone / capacity);
					layer.lastTemperature[cell] = warm;
					c.temperature[cell] = warm;
				}
			}
			layer.otherMass[cell] = others;
			if (pressureMode)
			{
				float equivalent = 0.0f;
				for (size_t i = 0; i < present.size(); i++) equivalent += masses[i][cell] * equivalentPerKg[i];
				layer.shownExtra[cell] = equivalent * warm - mainMass;
			}
			else
			{
				layer.shownExtra[cell] = others;
			}
			if (layer.shownExtra[cell] != 0.0f)
			{
				// A gas-emitting solid is nearby: the mixture mass must be shown to the processing of the cell itself. Below the cell
				// is a gas-emitting liquid: to the processing of that liquid.
				for (int side = 0; side < 4; side++)
				{
					const size_t beside = (size_t)((ptrdiff_t)cell + limitSides[side]);
					if (beside >= c.count || c.element[beside] >= numElements) continue;
					const ElementInfo& info = elements->elements[c.element[beside]];
					if ((info.flags & ElementEmitsGas) == 0) continue;
					if (info.state == SIM_SOLID) markLimit(cell, LimitSelf);
					else if (info.state == SIM_LIQUID && side == 0) markLimit(beside, LimitAbove);
				}
				// Heat between cells is computed by the simulation, from the main gas. The mixture has a different heat capacity (larger
				// by the mass of the other gases) and a different thermal conductivity (averaged by number of molecules). The amount of heat
				// between two cells the simulation takes proportional to the geometric mean of their conductivities
				// (UpdateTemperature), that is, to the square root of the cell's conductivity: the temperature change is corrected by that much.
				// The correction is clamped so that the cell does not overshoot its neighbours' temperature.
				double whole = 0, conducts = 0, molecules = 0;
				for (size_t i = 0; i < present.size(); i++)
				{
					const double m = masses[i][cell];
					whole += m * heatOf[i];
					conducts += m * perMolar[i] * conductOf[i];
					molecules += m * perMolar[i];
				}
				const double mainPart = (double)mainMass * heatOf[mainIndex];
				double factor = whole > mainPart && mainPart > 0 ? mainPart / whole : 1.0;
				if (molecules > 0 && conductOf[mainIndex] > 0.0f && conducts > 0) factor *= sqrt(conducts / molecules / conductOf[mainIndex]);
				layer.mainShare[cell] = (float)(factor < 0.05 ? 0.05 : factor > 1.5 ? 1.5 : factor);
			}
			gas++;
			mainTotal += mainMass;
			if (all - best >= 0.01f * all) mixed++;
		});

		// Germs after the step: each cell gas flows through keeps one kind. If two of them met,
		// the one with the larger "count times kind strength" wins, and loses as much as the loser "weighed".
		// In a cell with no gas left, no germs remain either.
		if (kinds > 0)
		{
			const int64_t writeStart = NowMicros();
			const bool hasTail = target->diseaseInfestationTickCount.size() >= c.count && target->diseaseGrowthAccumulatedError.size() >= c.count;
			int64_t total = 0, changed = 0, fights = 0;
			auto settle = [&](size_t cell)
			{
				if (open[cell] == 0.0f || !active[cell]) return;
				const uint8_t was = germKind[cell];
				if (was != 0xFF && was >= MaxKinds) return; // a kind the layer does not carry: leave it alone
				int best = -1, met = 0;
				float bestScore = 0.0f, allScore = 0.0f;
				for (int kind = 0; kind < MaxKinds; kind++)
				{
					if (!germField[kind] || !(germField[kind][cell] > 0.0f)) continue;
					const float score = germField[kind][cell] * layer.germStrength[kind];
					allScore += score;
					met++;
					if (score > bestScore) { bestScore = score; best = kind; }
				}
				float value = 0.0f;
				if (best >= 0 && elements->IsGas(c.element[cell]))
				{
					value = met > 1 ? (2.0f * bestScore - allScore) / layer.germStrength[best] : germField[best][cell];
					if (met > 1) fights++;
				}
				const int32_t count = value > 0.0f ? (int32_t)value : 0;
				layer.germFraction[cell] = value > 0.0f ? value - (float)count : 0.0f;
				layer.germFractionKind[cell] = count > 0 || layer.germFraction[cell] > 0.0f ? (uint8_t)best : (uint8_t)0xFF;
				total += count;
				if (count <= 0)
				{
					if (was == 0xFF && germCount[cell] == 0) return;
					germKind[cell] = 0xFF;
					germCount[cell] = 0;
					if (hasTail)
					{
						target->diseaseInfestationTickCount.first[cell] = 0;
						target->diseaseGrowthAccumulatedError.first[cell] = 0.0f;
					}
					changed++;
					return;
				}
				if (was == (uint8_t)best && germCount[cell] == count) return;
				if (was != (uint8_t)best)
				{
					germKind[cell] = (uint8_t)best;
					if (hasTail) target->diseaseInfestationTickCount.first[cell] = 0;
				}
				germCount[cell] = count;
				changed++;
			};
			for (uint32_t cell : layer.germTouched) settle(cell);
			germsInGas.store(total, std::memory_order_relaxed);
			germCellCount.store((int64_t)layer.germCells.size(), std::memory_order_relaxed);
			germCellsChanged.fetch_add(changed, std::memory_order_relaxed);
			germFights.fetch_add(fights, std::memory_order_relaxed);
			germTime += NowMicros() - writeStart;
		}
		else
		{
			germsInGas.store(0, std::memory_order_relaxed);
			germCellCount.store(0, std::memory_order_relaxed);
		}
		germKinds.store(kinds, std::memory_order_relaxed);
		germMicros.fetch_add(germTime, std::memory_order_relaxed);
		condensedCount.fetch_add((int64_t)layer.condensed.size(), std::memory_order_relaxed);
		condensedGrams.fetch_add((int64_t)(condensedMass * 1000.0), std::memory_order_relaxed);
		gasCells.store(gas, std::memory_order_relaxed);
		speciesCount.store((int64_t)present.size(), std::memory_order_relaxed);
		totalGrams.store((int64_t)(mainTotal * 1000.0), std::memory_order_relaxed);
		layerGrams.store((int64_t)(layerTotal * 1000.0), std::memory_order_relaxed);
		mixedCells.store(mixed, std::memory_order_relaxed);
		mainSwitched.fetch_add(switched, std::memory_order_relaxed);
		mainVanished.fetch_add(vanished, std::memory_order_relaxed);
		elementChanges.fetch_add(replaced, std::memory_order_relaxed);
		leftGas.fetch_add(closed, std::memory_order_relaxed);
		pushedGrams.fetch_add((int64_t)(pushed * 1000.0), std::memory_order_relaxed);
		lostGrams.fetch_add((int64_t)((lost + traces) * 1000.0), std::memory_order_relaxed);
		voidGrams.fetch_add((int64_t)(toVoid * 1000.0), std::memory_order_relaxed);
		spaceGrams.fetch_add((int64_t)(toSpace * 1000.0), std::memory_order_relaxed);
		activeCells.store(layer.activeCount, std::memory_order_relaxed);
		idleGasCells.store(layer.idleGasCells, std::memory_order_relaxed);
		vanishedSpace.fetch_add(inSpace, std::memory_order_relaxed);
		vanishedTiny.fetch_add(tiny, std::memory_order_relaxed);
		const int64_t end = NowMicros();
		kernelMicros.fetch_add(projectStart - kernelStart, std::memory_order_relaxed);
		kernelAvx.store(GasMixKernel::HasAvx() ? 1 : 0, std::memory_order_relaxed);
		spreadCells.store((int64_t)layer.kernel.SpreadCells(), std::memory_order_relaxed);
		limitCellCount.store((int64_t)layer.limitCells.size(), std::memory_order_relaxed);
		openCells.store((int64_t)layer.kernel.OpenCells(), std::memory_order_relaxed);
		projectMicros.fetch_add(end - projectStart, std::memory_order_relaxed);
		stepMicros.fetch_add(end - start, std::memory_order_relaxed);
		ticks.fetch_add(1, std::memory_order_relaxed);
	}
}

// "Strength" of germ kinds from the mod: one number per kind, by kind index (as in the game's disease table).
SIM_DATA(GermStrengths, "gas-mix.germs")
{
	if (size <= 0 || size % (int32_t)sizeof(float) != 0) return -1;
	const float* values = (const float*)data;
	std::lock_guard<std::mutex> lock(layerGuard);
	for (int kind = 0; kind < GasMixKernel::MaxPassive; kind++)
	{
		const float strength = kind < size / (int32_t)sizeof(float) ? values[kind] : 1.0f;
		layer.germStrength[kind] = strength > 0.0f ? strength : 1.0f;
	}
	return 0;
}

// Element table from the mod: one ElementInfo record per element, by element index.
SIM_DATA(Elements, "gas-mix.elements")
{
	if (size <= 0 || size % (int32_t)sizeof(ElementInfo) != 0) return -1;
	ElementTable* table = new ElementTable;
	const ElementInfo* first = (const ElementInfo*)data;
	table->elements.assign(first, first + size / sizeof(ElementInfo));
	// The old table is not freed: the simulation thread may be finishing reading it right now, and it changes once per game.
	publishedElements.store(table, std::memory_order_release);
	std::lock_guard<std::mutex> lock(phaseGuard);
	RebuildPhases();
	return 0;
}

// Layer step numbers: pressure, diffusion, gravity and, if a fourth is passed, spreading along a barrier.
// Change only while the simulation is not computing.
SIM_DATA(Params, "gas-mix.params")
{
	if (size < 3 * (int32_t)sizeof(float) || size > 5 * (int32_t)sizeof(float) || size % (int32_t)sizeof(float) != 0) return -1;
	const float* values = (const float*)data;
	layer.params.pressure = values[0];
	layer.params.diffusion = values[1];
	layer.params.gravity = values[2];
	if (size >= 4 * (int32_t)sizeof(float)) layer.params.spread = values[3];
	if (size >= 5 * (int32_t)sizeof(float)) layer.condenseMass = values[4];
	return 0;
}

// Phase rule settings from the pressure mod (element_table.h): how to compute pressure, evaporation and boiling rates,
// real substance numbers. The layer has its own portion threshold (gas-mix.params).
SIM_DATA(PhaseSettings, "gas-mix.phase-config")
{
	std::lock_guard<std::mutex> lock(phaseGuard);
	if (!phaseConfig.Read(data, size)) return -1;
	RebuildPhases();
	std::lock_guard<std::mutex> layerLock(layerGuard);
	layer.evaporationRate = phaseConfig.header.evaporationRate;
	layer.boilRate = phaseConfig.header.boilRate;
	return 0;
}

// Phase rules by substance: how much of each gas condensed and how much of each liquid evaporated since
// the start of the game (for the pressure mod's log). The buffer receives the number of records and the records: element index, how much
// of it condensed as a gas, how much of it evaporated as a liquid, kg.
SIM_DATA(PhaseElements, "gas-mix.phase-elements")
{
	struct Entry { int32_t element; float condensed; float evaporated; };
	if (size < (int32_t)sizeof(int32_t)) return -1;
	std::lock_guard<std::mutex> lock(layerGuard);
	int32_t* count = (int32_t*)data;
	Entry* out = (Entry*)((uint8_t*)data + sizeof(int32_t));
	const int32_t room = (size - (int32_t)sizeof(int32_t)) / (int32_t)sizeof(Entry);
	*count = 0;
	const size_t elements = layer.condensedBy.size() > layer.evaporatedBy.size() ? layer.condensedBy.size() : layer.evaporatedBy.size();
	for (size_t i = 0; i < elements && *count < room; i++)
	{
		const double condensed = i < layer.condensedBy.size() ? layer.condensedBy[i] : 0, evaporated = i < layer.evaporatedBy.size() ? layer.evaporatedBy[i] : 0;
		if (condensed > 0 || evaporated > 0) out[(*count)++] = Entry{ (int32_t)i, (float)condensed, (float)evaporated };
	}
	return 0;
}

// Survey for the log: gas losses, phase rule details and cell state changes (described at the layer fields).
// The buffer receives: sixteen state change counts [was][became] (8 bytes each), the number of records and the records.
// Record kind: 0 losses by the substance that closed the cell; 1 condensation by gas; 2 evaporation by liquid;
// 3 state changes by the substance the cell became. The cell in a record is a game cell (-1: none).
SIM_DATA(Diagnostics, "gas-mix.diagnostics")
{
	struct Entry { int32_t kind, element; float mass; int32_t count, boiling; float gasTemperature, liquidTemperature, biggest; int32_t cell, other; };
	const int32_t header = (int32_t)(sizeof(int64_t) * 16 + sizeof(int32_t));
	if (size < header) return -1;
	std::lock_guard<std::mutex> lock(layerGuard);
	memcpy((void*)data, layer.transitions, sizeof(int64_t) * 16);
	int32_t* count = (int32_t*)((uint8_t*)data + sizeof(int64_t) * 16);
	Entry* out = (Entry*)((uint8_t*)data + header);
	const int32_t room = (size - header) / (int32_t)sizeof(Entry);
	*count = 0;
	const int32_t width = layer.kernel.Width();
	auto gameCell = [&](int32_t cell) { return cell < 0 || width <= 2 ? -1 : (cell / width - 1) * (width - 2) + (cell % width - 1); };
	auto put = [&](const Entry& entry) { if (*count < room) out[(*count)++] = entry; };
	for (size_t i = 0; i < layer.lossBy.size(); i++)
	{
		const LossStat& stat = layer.lossBy[i];
		if (stat.count > 0) put(Entry{ 0, (int32_t)i, (float)stat.mass, (int32_t)stat.count, 0, 0.0f, 0.0f, stat.biggest, gameCell(stat.cell), (int32_t)stat.gas });
	}
	for (int kind = 1; kind <= 2; kind++)
	{
		const std::vector<PhaseStat>& stats = kind == 1 ? layer.condensedDetail : layer.evaporatedDetail;
		for (size_t i = 0; i < stats.size(); i++)
		{
			const PhaseStat& stat = stats[i];
			if (stat.count > 0) put(Entry{ kind, (int32_t)i, (float)stat.mass, (int32_t)stat.count, (int32_t)stat.boiling,
				(float)(stat.gasHeat / stat.mass), (float)(stat.liquidHeat / stat.mass), stat.biggest, gameCell(stat.cell), 0 });
		}
	}
	for (size_t i = 0; i < layer.transitionsTo.size(); i++)
	{
		if (layer.transitionsTo[i] > 0) put(Entry{ 3, (int32_t)i, 0.0f, (int32_t)layer.transitionsTo[i], 0, 0.0f, 0.0f, 0.0f, -1, 0 });
	}
	return 0;
}

// How much vapour of a gas fits in a cell at a temperature under the current phase rule settings (for the mod's
// self-check scenes). Record: gas index, temperature; in reply the mass, kg (a very large number: no limit).
// Returns 1 if the gas has no liquid of its own or the rules are not built yet.
SIM_DATA(SaturationQuery, "gas-mix.saturation")
{
	struct Query { int32_t gas; float temperature; float mass; };
	if (size != (int32_t)sizeof(Query)) return -1;
	Query* query = (Query*)data;
	const PhaseTables* phases = publishedPhases.load(std::memory_order_acquire);
	if (!phases || query->gas < 0 || (size_t)query->gas >= phases->gasVapour.size() || !phases->gasVapour[query->gas].Valid()) return 1;
	const float mass = phases->gasVapour[query->gas].SaturationMass(query->temperature);
	query->mass = mass < 1e30f ? mass : 1e30f;
	return 0;
}

// ---------- Gas sources and consumers ----------
// The game and the simulation add gas to a cell and take it away knowing only the cell's main gas.
//
// Source (AddGas: almost everything that releases gas into the world, from the electrolyzer to a duplicant's exhalation). If the cell
// holds another gas, the simulation tries to displace it to neighbours, and when there is nowhere to, it destroys as much of the old gas
// as it added of the new. With the layer the gas is simply added to the cell's mixture, together with its heat.
//
// Consumer (do_remove: scrubber, plants, the game's "take mass" request). The simulation walks the cells around
// the consumer and takes the needed gas where it is main. With the layer the consumer also takes it from cells
// where that gas is not main in the mixture.
//
// Both fixes work in the simulation thread between layer passes. Both do nothing while the layer is not moving gas,
// and until it has been rebuilt after a game load.
namespace
{
	bool LayerReady(const ElementTable* elements, int32_t cell)
	{
		return layer.own && !resetRequested && elements == layer.elements && cell >= 0 && (size_t)cell < layer.cells
			&& (size_t)cell < layer.active.size() && layer.active[cell] != 0;
	}

	// Adds a portion of gas to the mixture of a cell whose main gas is different; the layer lock is already held.
	// false: not our case (same gas, vacuum, liquid, solid, cell outside the layer).
	bool MixIntoCellLocked(const ElementTable* elements, SimData* sim, int32_t cell, uint16_t element, float mass, float temperature)
	{
		if (!LayerReady(elements, cell)) return false;
		SimCells* cells = sim->updatedCells;
		if ((size_t)cell >= cells->elementIdx.size() || (size_t)cell >= cells->mass.size() || (size_t)cell >= cells->temperature.size()) return false;
		const uint16_t main = cells->elementIdx.first[cell];
		// The simulation will add the same gas itself; it also adds to vacuum, liquid and solid in its own way.
		if (main == element || !elements->IsGas(main) || !elements->IsGas(element)) return false;
		// Common mixture temperature after adding the portion: by the heat content of all gases of the cell.
		double capacity = (double)cells->mass.first[cell] * elements->elements[main].heat;
		for (int32_t gas : layer.kernel.Present())
		{
			if ((uint16_t)gas != main) capacity += (double)layer.kernel.Mass(gas)[cell] * elements->elements[gas].heat;
		}
		const double added = (double)mass * elements->elements[element].heat;
		float before = cells->temperature.first[cell];
		// How much the simulation changed the cell temperature since the last layer pass is first rescaled
		// to the whole mixture (as when absorbing the cell), and only then is the portion blended in at that temperature.
		const float last = layer.lastTemperature[cell], share = layer.mainShare[cell];
		if (share != 1.0f && before > 0.0f && last > 0.0f && layer.projectedElement[cell] == main) before = last + (before - last) * share;
		if (before > 0.0f && temperature > 0.0f && capacity + added > 0.0)
		{
			float mixed = (float)((capacity * before + added * temperature) / (capacity + added));
			const float low = before < temperature ? before : temperature, high = before < temperature ? temperature : before;
			mixed = mixed < low ? low : mixed > high ? high : mixed;
			cells->temperature.first[cell] = mixed;
			layer.lastTemperature[cell] = mixed;
		}
		layer.kernel.Mass(element)[cell] += mass;
		return true;
	}

	// The germs of a gas portion added to the mixture go to the cell. A simulation cell always holds one germ kind;
	// when the kinds differ, the simulation decides who displaces whom by its disease table, which the layer cannot
	// reach: in that case the portion's germs are lost (false).
	typedef void (*AddDiseaseFn)(void* diseases, SimCells* cells, int32_t cell, uint8_t diseaseIdx, int32_t diseaseCount);

	bool AddGermsToCell(SimData* sim, int32_t cell, uint8_t diseaseIdx, int32_t diseaseCount)
	{
		if (diseaseIdx == 0xFF || diseaseCount <= 0) return true;
		static const AddDiseaseFn add = (AddDiseaseFn)simhook_find("Disease::AddDiseaseToCell");
		SimCells* cells = sim->updatedCells;
		if (!add || cell < 0 || (size_t)cell >= cells->diseaseIdx.size() || (size_t)cell >= cells->diseaseCount.size()) return false;
		const uint8_t now = cells->diseaseIdx.first[cell];
		if (now != diseaseIdx && now != 0xFF) return false;
		// The function does not need the disease table (first parameter) in these two cases.
		add(nullptr, cells, cell, diseaseIdx, diseaseCount);
		sourceGermsKept.fetch_add(diseaseCount, std::memory_order_relaxed);
		return true;
	}

	// Adds released gas to the cell's mixture. false: not our case, let the simulation do as usual.
	bool MixIntoCell(SimData* sim, int32_t cell, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements || !(mass > 0.0f) || moving.load(std::memory_order_relaxed) == 0) return false;
		std::lock_guard<std::mutex> lock(layerGuard);
		if (!MixIntoCellLocked(elements, sim, cell, element, mass, temperature)) return false;
		sourceMixedCount.fetch_add(1, std::memory_order_relaxed);
		sourceMixedGrams.fetch_add((int64_t)(mass * 1000.0f), std::memory_order_relaxed);
		if (!AddGermsToCell(sim, cell, diseaseIdx, diseaseCount)) sourceGermsLost.fetch_add(diseaseCount, std::memory_order_relaxed);
		return true;
	}

	// The consumer takes the needed gas from the mixture of a cell where that gas is not main.
	void TakeFromMixture(SimData* sim, float* wanted, uint16_t element, SimConsumedMass* taken, int32_t cell)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements || !wanted || !taken || !(*wanted > 0.0f) || moving.load(std::memory_order_relaxed) == 0 || !elements->IsGas(element)) return;
		std::lock_guard<std::mutex> lock(layerGuard);
		if (!LayerReady(elements, cell)) return;
		const SimCells* cells = sim->updatedCells;
		if ((size_t)cell >= cells->elementIdx.size() || cells->elementIdx.first[cell] == element) return;
		bool present = false;
		for (int32_t gas : layer.kernel.Present()) present = present || (uint16_t)gas == element;
		if (!present) return;
		float* masses = layer.kernel.Mass(element);
		const float amount = *wanted < masses[cell] ? *wanted : masses[cell];
		if (!(amount > 0.0f)) return;
		masses[cell] -= amount;
		*wanted -= amount;
		float temperature = (size_t)cell < cells->temperature.size() ? cells->temperature.first[cell] : 0.0f;
		if (!(temperature > 0.0f)) temperature = layer.lastTemperature[cell];
		const float total = taken->mass + amount;
		if (temperature > 0.0f && total > 0.0f)
		{
			taken->temperature = taken->mass > 0.0f ? (taken->mass * taken->temperature + amount * temperature) / total : temperature;
		}
		taken->mass = total;
		// The gas takes its share of the cell's germs with it (by mixture mass), if the consumer does not yet have germs of another kind.
		SimCells* germs = sim->updatedCells;
		if ((size_t)cell < germs->diseaseIdx.size() && (size_t)cell < germs->diseaseCount.size() && (size_t)cell < cells->mass.size())
		{
			const uint8_t kind = germs->diseaseIdx.first[cell];
			const int32_t count = germs->diseaseCount.first[cell];
			float mixture = cells->mass.first[cell] + amount;
			for (int32_t gas : layer.kernel.Present()) if ((uint16_t)gas != cells->elementIdx.first[cell]) mixture += layer.kernel.Mass(gas)[cell];
			if (kind != 0xFF && count > 0 && mixture > 0.0f && (taken->diseaseIdx == 0xFF || taken->diseaseIdx == kind))
			{
				const int32_t leaving = (int32_t)((float)count * (amount < mixture ? amount / mixture : 1.0f));
				if (leaving > 0)
				{
					taken->diseaseIdx = kind;
					taken->diseaseCount += leaving;
					germs->diseaseCount.first[cell] = count - leaving;
					if (count - leaving <= 0) germs->diseaseIdx.first[cell] = 0xFF;
					consumerGerms.fetch_add(leaving, std::memory_order_relaxed);
				}
			}
		}
		consumerMixedCount.fetch_add(1, std::memory_order_relaxed);
		consumerMixedGrams.fetch_add((int64_t)(amount * 1000.0f), std::memory_order_relaxed);
	}
}

// Consumer of "any gas" (pump and the like). Every tick the simulation takes for it the element of the first gas
// cell among those it reaches (the walk start shifts from tick to tick), and takes that gas.
// With a mixture this pumps out gas after gas, and an admixture that is not main anywhere nearby is never chosen.
// With the layer the gas is chosen at random weighted by mass across all reachable cells: over many ticks the pump
// takes gases in the same proportion as they lie around it. Then the intake goes the usual way
// (the cell walk and do_remove above, which takes gas also where it is not main).
namespace
{
	uint32_t pickState = 0x9e3779b9u;

	bool PickGasForConsumer(const SimData* sim, const int32_t* reachable, size_t count, uint16_t* chosen)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements || !reachable || !chosen || count == 0 || moving.load(std::memory_order_relaxed) == 0) return false;
		std::lock_guard<std::mutex> lock(layerGuard);
		if (!layer.own || resetRequested || elements != layer.elements) return false;
		const SimCells* cells = sim->updatedCells;
		const std::vector<int32_t>& present = layer.kernel.Present();
		if (present.empty() || present.size() > 64) return false;
		double weights[64] = {};
		bool main[64] = {};
		for (size_t i = 0; i < count; i++)
		{
			const int32_t cell = reachable[i];
			if (cell < 0 || (size_t)cell >= layer.cells || (size_t)cell >= cells->elementIdx.size() || (size_t)cell >= cells->mass.size() || !layer.active[cell]) continue;
			const uint16_t element = cells->elementIdx.first[cell];
			if (!elements->IsGas(element)) continue;
			for (size_t g = 0; g < present.size(); g++)
			{
				// The cell's main gas is taken from the simulation (its mass is fresher), the others from the layer.
				if ((uint16_t)present[g] == element) { weights[g] += cells->mass.first[cell]; main[g] = true; }
				else weights[g] += layer.kernel.Mass(present[g])[cell];
			}
		}
		double total = 0;
		for (size_t g = 0; g < present.size(); g++) total += weights[g];
		if (!(total > 0.0)) return false;
		pickState ^= pickState << 13; pickState ^= pickState >> 17; pickState ^= pickState << 5;
		double point = (pickState >> 8) * (1.0 / 16777216.0) * total;
		size_t pick = present.size() - 1;
		for (size_t g = 0; g < present.size(); g++)
		{
			if (point < weights[g]) { pick = g; break; }
			point -= weights[g];
		}
		// In case of rounding: the chosen gas must have mass.
		for (size_t g = 0; g < present.size() && !(weights[pick] > 0.0); g++) if (weights[g] > 0.0) pick = g;
		*chosen = (uint16_t)present[pick];
		pumpPicks.fetch_add(1, std::memory_order_relaxed);
		if (!main[pick]) pumpPicksOther.fetch_add(1, std::memory_order_relaxed);
		return true;
	}
}

SIM_HOOK("gas-mix", bool, "AnyInputCellHasState", AnyInputCellHasState,
	(SimData* sim, SimVector<int32_t>* reachable, uint16_t* element, int32_t state, int32_t offset))
{
	if (state == SIM_GAS && reachable && PickGasForConsumer(sim, reachable->first, reachable->size(), element)) return true;
	return AnyInputCellHasState_original(sim, reachable, element, state, offset);
}

// For checking without the game (hooktest): gas choice for a pump from a list of cells. Record: cells structure, number
// of cells, then their indices; in reply the chosen element or -1 is put into the first number of the list.
SIM_DATA(PickForTest, "gas-mix.pick-for-test")
{
	if (size < (int32_t)(sizeof(void*) + 2 * sizeof(int32_t))) return -1;
	SimData* sim;
	int32_t count;
	memcpy(&sim, data, sizeof(sim));
	memcpy(&count, (const uint8_t*)data + sizeof(sim), sizeof(count));
	int32_t* list = (int32_t*)((uint8_t*)data + sizeof(sim) + sizeof(count));
	if (count <= 0 || (size_t)size < sizeof(sim) + sizeof(count) + (size_t)count * sizeof(int32_t)) return -1;
	uint16_t chosen = 0;
	const bool picked = PickGasForConsumer(sim, list, (size_t)count, &chosen);
	list[0] = picked ? (int32_t)chosen : -1;
	return 0;
}

SIM_HOOK("gas-mix", void, "AddGas", AddGas,
	(int32_t cell, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount, SimData* sim))
{
	if (MixIntoCell(sim, cell, element, mass, temperature, diseaseIdx, diseaseCount)) return;
	AddGas_original(cell, element, mass, temperature, diseaseIdx, diseaseCount, sim);
}

SIM_HOOK("gas-mix", void, "do_remove", DoRemove, (SimData* sim, float* wanted, uint16_t element, SimConsumedMass* taken, int32_t cell))
{
	DoRemove_original(sim, wanted, element, taken, cell);
	TakeFromMixture(sim, wanted, element, taken, cell);
}

// ---------- The simulation's own gas sources (geysers) ----------
// ElementEmitter releases gas without game messages and bypassing AddGas: geysers, wells, buildings with constant
// emission. Once per its interval it walks the cells around itself (TryEmit) and releases a portion into the first one where
// the mass is below its maximum pressure; if it holds another gas, that gas is first displaced to neighbours (DisplaceGas).
// With the layer:
//  - the cell's pressure for it is the mass of the whole mixture, not of the single main gas (as for buildings and sensors);
//  - into a cell with a different main gas the portion is added to the mixture, displacing nobody;
//  - into a cell with its own gas or vacuum the simulation itself releases, as before (its Emit).
// Left untouched: the "source blocked by pressure" flag (geyser label and look) is computed by the simulation itself before calling
// TryEmit and from the single main gas. When there is little main gas but a lot of mixture, the geyser releases nothing
// but does not look blocked; such cases are counted by gas-mix.emitter-held.
// Liquid sources and sources without a set temperature go the old way.
namespace
{
	typedef SimEmittedMass* (*EmitFn)(void* self, SimEmittedMass* result, SimEmitterData* emitter, int32_t cell, float temperature, SimData* sim);
	typedef bool (*DisplaceGasFn)(SimData* sim, void* events, SimCells* cells, int32_t cell, uint16_t element);

	// true: the release is decided here (result holds what was released, or is empty); false: let the simulation decide.
	bool EmitThroughLayer(void* self, SimEmittedMass* result, SimEmitterData* emitter, SimVector<int32_t>* reachable, uint64_t offset, SimData* sim,
		EmitFn emit, DisplaceGasFn displace)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements || !result || !emitter || !reachable || !emit || !displace || moving.load(std::memory_order_relaxed) == 0) return false;
		if (!elements->IsGas(emitter->elemIdx) || !(emitter->emitMass > 0.0f) || !(emitter->emitTemperature > 0.0f)) return false;
		{
			std::lock_guard<std::mutex> lock(layerGuard);
			if (!layer.own || resetRequested || elements != layer.elements) return false;
		}
		emitterCalls.fetch_add(1, std::memory_order_relaxed);
		*result = SimEmittedMass{ NoElement, 0xFF, 0, 0.0f, 0.0f, 0 };
		const size_t count = reachable->size();
		bool roomByMainGas = false;
		for (size_t k = 0; k < count; k++)
		{
			const int32_t cell = reachable->first[(offset + k) % count];
			enum { Skip, Mixed, BySim, Displace } action = Skip;
			uint16_t main = NoElement;
			bool germsKept = true;
			{
				std::lock_guard<std::mutex> lock(layerGuard);
				const SimCells* cells = sim->updatedCells;
				if (cell < 0 || (size_t)cell >= cells->elementIdx.size() || (size_t)cell >= cells->mass.size()) continue;
				main = cells->elementIdx.first[cell];
				float pressure = cells->mass.first[cell];
				if (pressure < emitter->maxPressure) roomByMainGas = true;
				const bool mixture = elements->IsGas(main) && LayerReady(elements, cell);
				if (mixture && byPressure.load(std::memory_order_relaxed) != 0)
				{
					// Pressure mode: the equivalent mass from the last projection.
					pressure += layer.shownExtra[cell];
				}
				else if (mixture)
				{
					for (int32_t gas : layer.kernel.Present()) if ((uint16_t)gas != main) pressure += layer.kernel.Mass(gas)[cell];
				}
				if (!(pressure < emitter->maxPressure)) continue;
				if (main == emitter->elemIdx || main == sim->vacuumElementIdx) action = BySim;
				else if (mixture && MixIntoCellLocked(elements, sim, cell, emitter->elemIdx, emitter->emitMass, emitter->emitTemperature))
				{
					action = Mixed;
					germsKept = AddGermsToCell(sim, cell, emitter->diseaseIdx, emitter->emitDiseaseCount);
				}
				else action = Displace;
			}
			if (action == Mixed)
			{
				*result = SimEmittedMass{ emitter->elemIdx, 0xFF, 0, emitter->emitMass, emitter->emitTemperature, 0 };
				emitterMixedCount.fetch_add(1, std::memory_order_relaxed);
				emitterMixedGrams.fetch_add((int64_t)(emitter->emitMass * 1000.0f), std::memory_order_relaxed);
				if (!germsKept) emitterGermsLost.fetch_add(emitter->emitDiseaseCount, std::memory_order_relaxed);
				return true;
			}
			// Liquid or a cell outside the layer: as in the simulation, first displace what is in the cell.
			if (action == Displace && !displace(sim, sim->simEvents, sim->updatedCells, cell, main)) continue;
			emit(self, result, emitter, cell, emitter->emitTemperature, sim);
			return true;
		}
		if (roomByMainGas) emitterHeld.fetch_add(1, std::memory_order_relaxed);
		return true;
	}
}

// The "source blocked by pressure" flag (geyser label and look). The simulation computes it in ElementEmitter::Update:
// it collects the cells around the source (GetReachableCells), checks whether among them there is a cell with mass below the limit,
// sends the game a notification about the flag change and immediately calls TryEmit. The mass there is read straight from the cells, that is,
// only of the main gas. So that the flag is computed from the whole mixture, for the duration of this check the cells around the source
// are shown the mixture mass: after GetReachableCells called from the sources' Update, the mass of the gas cells from
// its lists is substituted, and at the start of TryEmit (it is always called next) it is restored bit for bit.
namespace
{
	struct RaisedCell
	{
		int32_t cell;
		float mass; // real mass of the main gas
	};
	std::mutex raisedGuard;
	std::vector<RaisedCell> raisedCells;

	// Gives the cells back the main gas's mass. true if there was something to restore.
	bool RestoreAfterEmitter(SimData* sim)
	{
		std::lock_guard<std::mutex> lock(raisedGuard);
		if (raisedCells.empty()) return false;
		SimCells* cells = sim->updatedCells;
		for (const RaisedCell& raised : raisedCells) if ((size_t)raised.cell < cells->mass.size()) cells->mass.first[raised.cell] = raised.mass;
		raisedCells.clear();
		return true;
	}

	void RaiseForEmitter(SimData* sim, const int32_t* list, size_t count)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements || !list || count == 0 || moving.load(std::memory_order_relaxed) == 0) return;
		std::lock_guard<std::mutex> lock(layerGuard);
		if (!layer.own || resetRequested || elements != layer.elements) return;
		std::lock_guard<std::mutex> raisedLock(raisedGuard);
		SimCells* cells = sim->updatedCells;
		for (size_t i = 0; i < count; i++)
		{
			const int32_t cell = list[i];
			if (cell < 0 || (size_t)cell >= cells->mass.size() || (size_t)cell >= cells->elementIdx.size() || !LayerReady(elements, cell)) continue;
			const uint16_t main = cells->elementIdx.first[cell];
			if (!elements->IsGas(main)) continue;
			bool already = false;
			for (const RaisedCell& raised : raisedCells) already = already || raised.cell == cell;
			if (already) continue;
			float others = 0.0f;
			if (byPressure.load(std::memory_order_relaxed) != 0)
			{
				// Pressure mode: the equivalent mass from the last projection.
				others = layer.shownExtra[cell];
				if (others == 0.0f) continue;
			}
			else
			{
				for (int32_t gas : layer.kernel.Present()) if ((uint16_t)gas != main) others += layer.kernel.Mass(gas)[cell];
				if (!(others > 0.0f)) continue;
			}
			raisedCells.push_back(RaisedCell{ cell, cells->mass.first[cell] });
			const float shown = cells->mass.first[cell] + others;
			cells->mass.first[cell] = shown > 0.0f ? shown : 0.0f;
			emitterRaised.fetch_add(1, std::memory_order_relaxed);
		}
	}
}

// SIMHOOK_DIRECT: the hook recognises a call from ElementEmitter::Update by the return address.
SIM_HOOK_ORDERED("gas-mix", SIMHOOK_DIRECT, void, "GetReachableCells", GetReachableCells,
	(SimData* sim, int32_t x, int32_t y, int32_t depth, SimVector<int32_t>* visited, void* queue, SimVector<int32_t>* reachable, bool flag))
{
	// If nobody restored the previous substitution (no TryEmit after GetReachableCells), we restore it now.
	if (RestoreAfterEmitter(sim)) emitterUnrestored.fetch_add(1, std::memory_order_relaxed);
	GetReachableCells_original(sim, x, y, depth, visited, queue, reachable, flag);
	static const uintptr_t update = (uintptr_t)simhook_find("ElementEmitter::Update");
	const uintptr_t updateSize = 925; // length of ElementEmitter::Update in this SimDLL build (the library is tied to the build)
	const uintptr_t caller = (uintptr_t)__builtin_return_address(0);
	if (update == 0 || caller < update || caller >= update + updateSize) return;
	// Which of the two lists Update examines next is not known: we substitute the cells of both.
	if (visited) RaiseForEmitter(sim, visited->first, visited->size());
	if (reachable) RaiseForEmitter(sim, reachable->first, reachable->size());
}

// For checking without the game (hooktest): substituting cell mass with the mixture mass and restoring. Record: cells structure,
// 1 to substitute or 0 to restore, number of cells, their indices.
SIM_DATA(RaiseForTest, "gas-mix.raise-for-test")
{
	struct Test { SimData* sim; int32_t raise; int32_t count; int32_t cells[8]; };
	if (size != (int32_t)sizeof(Test)) return -1;
	const Test* test = (const Test*)data;
	if (test->count < 0 || test->count > 8) return -1;
	if (test->raise) RaiseForEmitter(test->sim, test->cells, (size_t)test->count);
	else RestoreAfterEmitter(test->sim);
	return 0;
}

SIM_HOOK("gas-mix", SimEmittedMass*, "ElementEmitter::TryEmit", TryEmit,
	(void* self, SimEmittedMass* result, SimEmitterData* emitter, SimVector<int32_t>* reachable, uint64_t offset, SimData* sim))
{
	RestoreAfterEmitter(sim);
	static const EmitFn emit = (EmitFn)simhook_find("ElementEmitter::Emit");
	static const DisplaceGasFn displace = (DisplaceGasFn)simhook_find("DisplaceGas");
	if (EmitThroughLayer(self, result, emitter, reachable, offset, sim, emit, displace)) return result;
	return TryEmit_original(self, result, emitter, reachable, offset, sim);
}

// For checking without the game (hooktest): source release over a list of cells. Instead of simulation functions,
// stubs are used: "release by the simulation itself" remembers the cell, "displacement" always refuses.
namespace
{
	struct EmitterTest
	{
		SimData* sim;
		int32_t element;
		float mass;
		float temperature;
		float maxPressure;
		int32_t count;
		int32_t cells[8];
		int32_t handled;     // 1 if the release was decided by the layer
		float emitted;       // how much was released
		int32_t simCell;     // cell the simulation itself would have released into, or -1
		int32_t displaced;   // how many times displacement was requested
	};
	EmitterTest* emitterTest = nullptr;

	SimEmittedMass* EmitStub(void*, SimEmittedMass* result, SimEmitterData* emitter, int32_t cell, float temperature, SimData*)
	{
		emitterTest->simCell = cell;
		*result = SimEmittedMass{ emitter->elemIdx, 0xFF, 0, emitter->emitMass, temperature, 0 };
		return result;
	}

	bool DisplaceStub(SimData*, void*, SimCells*, int32_t, uint16_t)
	{
		emitterTest->displaced++;
		return false;
	}
}

SIM_DATA(EmitterForTest, "gas-mix.emit-for-test")
{
	if (size != (int32_t)sizeof(EmitterTest)) return -1;
	EmitterTest* test = (EmitterTest*)data;
	if (test->count <= 0 || test->count > 8) return -1;
	SimEmitterData emitter = {};
	emitter.emitMass = test->mass;
	emitter.emitTemperature = test->temperature;
	emitter.maxPressure = test->maxPressure;
	emitter.elemIdx = (uint16_t)test->element;
	emitter.diseaseIdx = 0xFF;
	SimVector<int32_t> reachable = { nullptr, test->cells, test->cells + test->count, test->cells + test->count };
	SimEmittedMass result = {};
	test->simCell = -1;
	test->displaced = 0;
	emitterTest = test;
	test->handled = EmitThroughLayer(nullptr, &result, &emitter, &reachable, 0, test->sim, EmitStub, DisplaceStub) ? 1 : 0;
	emitterTest = nullptr;
	test->emitted = result.mass;
	return 0;
}

// For checking without the game (hooktest): releasing gas into a cell and a consumer taking gas, as the hooks above see them,
// but without calling the simulation itself. Record: cells structure, cell, element, mass, temperature; in reply
// the result field gets 1 if the gas was added to the mixture (for release), and the taken mass with temperature (for intake).
namespace
{
	struct SourceTest
	{
		SimData* sim;
		int32_t cell;
		int32_t element;
		float mass;
		float temperature;
		int32_t result;
		float takenMass;
		float takenTemperature;
	};
}

SIM_DATA(SourceForTest, "gas-mix.add-gas-for-test")
{
	if (size != (int32_t)sizeof(SourceTest)) return -1;
	SourceTest* test = (SourceTest*)data;
	test->result = MixIntoCell(test->sim, test->cell, (uint16_t)test->element, test->mass, test->temperature, 0xFF, 0) ? 1 : 0;
	return 0;
}

SIM_DATA(ConsumerForTest, "gas-mix.remove-for-test")
{
	if (size != (int32_t)sizeof(SourceTest)) return -1;
	SourceTest* test = (SourceTest*)data;
	float wanted = test->mass;
	SimConsumedMass taken = {};
	TakeFromMixture(test->sim, &wanted, (uint16_t)test->element, &taken, test->cell);
	test->takenMass = taken.mass;
	test->takenTemperature = taken.temperature;
	test->mass = wanted;
	return 0;
}

// For checking without the game (hooktest): one layer pass over the passed structure, as around a simulation tick.
// Substance change notifications are not sent here: the fake structure has no event queue.
SIM_DATA(TickForTest, "gas-mix.tick-for-test")
{
	if (size != (int32_t)sizeof(SimData)) return -1;
	const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
	if (!elements) return -2;
	std::lock_guard<std::mutex> lock(layerGuard);
	if (moving.load(std::memory_order_relaxed)) MoveStep((SimData*)data, elements, false);
	else Mirror((const SimData*)data, elements);
	return 0;
}

// Composition of one cell for the mod (tooltip under the cursor). The data block here is both request and reply: the mod passes
// its buffer, the plugin appends the cell's gases to it. Returns 0 if the reply is ready; 1 if the layer is currently
// computing a step and the mod did not ask to wait; 2 if the layer is not moving gas (no composition).
namespace
{
	struct CellQuery
	{
		int32_t cell;      // on input: game cell index
		int32_t wait;      // on input: non-zero if it is fine to wait until the layer finishes the step
		int32_t count;     // on output: how many gases were written
		float temperature; // on output: mixture temperature
		struct Gas
		{
			int32_t element;
			float mass;
		} gases[1];        // on output: the cell's gases with non-zero mass, as many as fit in the buffer
	};
}

SIM_DATA(CellGases, "gas-mix.cell")
{
	const int32_t header = (int32_t)offsetof(CellQuery, gases);
	if (size < header) return -1;
	CellQuery* query = (CellQuery*)data;
	query->count = 0;
	std::unique_lock<std::mutex> lock(layerGuard, std::defer_lock);
	if (query->wait) lock.lock();
	else if (!lock.try_lock()) return 1;
	if (!layer.own || moving.load(std::memory_order_relaxed) == 0) return 2;
	// The simulation grid is wider than the game's by one cell on each side.
	const int32_t width = layer.kernel.Width(), gameWidth = width - 2;
	if (gameWidth <= 0 || query->cell < 0) return -2;
	const size_t cell = (size_t)(query->cell / gameWidth + 1) * (size_t)width + (size_t)(query->cell % gameWidth + 1);
	if (cell >= layer.cells) return -2;
	const int32_t capacity = (size - header) / (int32_t)sizeof(CellQuery::Gas);
	for (int32_t gas : layer.kernel.Present())
	{
		const float mass = layer.kernel.Mass(gas)[cell];
		if (mass <= 0.0f || query->count >= capacity) continue;
		query->gases[query->count].element = gas;
		query->gases[query->count].mass = mass;
		query->count++;
	}
	query->temperature = layer.lastTemperature[cell];
	return 0;
}

// Layer state for the save: all gases of the cells except the main one (format described at SavedHeader). The mod calls the query
// twice: with a buffer for just the header, to learn the required length (reply 3 and the length in the header), and with a buffer
// of that length. Returns 0 if everything fit; 2 if the layer is not moving gas (nothing to save).
SIM_DATA(SaveState, "gas-mix.save")
{
	if (size < (int32_t)sizeof(SavedHeader)) return -1;
	std::lock_guard<std::mutex> lock(layerGuard);
	if (!layer.own || moving.load(std::memory_order_relaxed) == 0) return 2;
	const int32_t width = layer.kernel.Width(), gameWidth = width - 2, gameHeight = layer.kernel.Height() - 2;
	if (gameWidth <= 0 || gameHeight <= 0) return -2;
	uint8_t* out = (uint8_t*)data;
	SavedHeader header = { SavedMagic, 1, gameWidth, gameHeight, 0, 0 };
	size_t at = sizeof(SavedHeader);
	for (int32_t gas : layer.kernel.Present())
	{
		const float* masses = layer.kernel.Mass(gas);
		const size_t blockAt = at;
		at += 2 * sizeof(int32_t);
		int32_t count = 0;
		for (int32_t y = 0; y < gameHeight; y++)
		{
			const size_t row = (size_t)(y + 1) * width + 1;
			for (int32_t x = 0; x < gameWidth; x++)
			{
				const float mass = masses[row + x];
				// Traces lighter than a microgram are not worth space in the save; the game saves the cell's main gas itself.
				if (!(mass >= TraceMass) || layer.projectedElement[row + x] == (uint16_t)gas) continue;
				if (at + sizeof(SavedEntry) <= (size_t)size)
				{
					const SavedEntry entry = { y * gameWidth + x, mass };
					memcpy(out + at, &entry, sizeof(entry));
				}
				at += sizeof(SavedEntry);
				count++;
			}
		}
		if (count == 0)
		{
			at = blockAt;
			continue;
		}
		if (blockAt + 2 * sizeof(int32_t) <= (size_t)size)
		{
			memcpy(out + blockAt, &gas, sizeof(gas));
			memcpy(out + blockAt + sizeof(gas), &count, sizeof(count));
		}
		header.gasCount++;
	}
	header.bytes = (int32_t)at;
	memcpy(out, &header, sizeof(header));
	return at <= (size_t)size ? 0 : 3;
}

// A game was loaded (or a new one started): the layer starts from a clean slate from the simulation cells and picks up the passed
// state; an empty block means "no state". The restoration itself happens in the simulation thread on the next tick.
SIM_DATA(RestoreState, "gas-mix.restore")
{
	std::lock_guard<std::mutex> lock(layerGuard);
	const uint8_t* bytes = (const uint8_t*)data;
	pendingState.assign(bytes, bytes + (size > 0 ? size : 0));
	resetRequested = true;
	// What was published for breathing and the request queue belong to the previous game.
	std::lock_guard<std::mutex> breath(breathGuard);
	for (FrameSnapshot& snapshot : snapshots) snapshot.gameMass = nullptr;
	breathRequests.clear();
	return 0;
}

// Layer snapshot for the data frame the game is reading now (see FrameSnapshot). In the mod's buffer: address of the game's mass
// array (8 bytes), number of game cells (4 bytes), reserve (4 bytes), then two float arrays of that length:
// non-main breathable gas and the mass of all gases except the main one. Returns 0 if the snapshot was found and handed over,
// and 2 if there is no snapshot for this frame (the layer is not moving gas or the game has just been loaded).
namespace
{
	struct FrameQuery
	{
		uint64_t gameMass;
		int32_t cells;
		int32_t reserved;
	};
}

SIM_DATA(FrameArrays, "gas-mix.frame")
{
	if (size < (int32_t)sizeof(FrameQuery)) return -1;
	const FrameQuery* query = (const FrameQuery*)data;
	std::lock_guard<std::mutex> lock(breathGuard);
	for (const FrameSnapshot& snapshot : snapshots)
	{
		if (snapshot.gameMass == nullptr || (uint64_t)(uintptr_t)snapshot.gameMass != query->gameMass) continue;
		const size_t cells = snapshot.others.size();
		if (query->cells < 0 || (size_t)query->cells != cells || (size_t)size < sizeof(FrameQuery) + 2 * cells * sizeof(float)) return -1;
		// Outside the spans the snapshot holds zeros; the mod keeps them at zero on its side itself (clears the buffer on game load).
		float* out = (float*)((uint8_t*)data + sizeof(FrameQuery));
		for (const std::pair<uint32_t, uint32_t>& row : snapshot.rows)
		{
			memcpy(out + row.first, snapshot.breathable.data() + row.first, row.second * sizeof(float));
			memcpy(out + cells + row.first, snapshot.others.data() + row.first, row.second * sizeof(float));
		}
		return 0;
	}
	return 2;
}

// Composition of cells with a mixture for the same frame: request as for "gas-mix.frame", room for FrameGas records after the header.
// In the cells field the mod passes how many records fit, and gets back how many there are in the snapshot (if they did not fit,
// nothing is copied and 3 is returned).
SIM_DATA(FrameGases, "gas-mix.frame-gases")
{
	if (size < (int32_t)sizeof(FrameQuery)) return -1;
	FrameQuery* query = (FrameQuery*)data;
	std::lock_guard<std::mutex> lock(breathGuard);
	for (const FrameSnapshot& snapshot : snapshots)
	{
		if (snapshot.gameMass == nullptr || (uint64_t)(uintptr_t)snapshot.gameMass != query->gameMass) continue;
		const size_t count = snapshot.gases.size();
		const bool fits = query->cells >= 0 && (size_t)query->cells >= count && (size_t)size >= sizeof(FrameQuery) + count * sizeof(FrameGas);
		query->cells = (int32_t)count;
		if (!fits) return 3;
		if (count > 0) memcpy((uint8_t*)data + sizeof(FrameQuery), snapshot.gases.data(), count * sizeof(FrameGas));
		return 0;
	}
	return 2;
}

// The data frame for the game is ready: the simulation has just copied the cells into it. The layer adds to the mass of each
// cell the mass of its non-main gases (the game shows and checks pressure by this mass) and takes a snapshot of its arrays
// for this frame. The simulation's own cells are not touched: they still hold only the main gas.
namespace
{
	void PublishFrame(const SimGameData* frame)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements || !frame || moving.load(std::memory_order_relaxed) == 0) return;
		const int64_t start = NowMicros();
		std::lock_guard<std::mutex> lock(layerGuard);
		if (!layer.own || resetRequested || elements != layer.elements) return;
		GasMixKernel& kernel = layer.kernel;
		const int32_t width = kernel.Width(), gameWidth = width - 2, gameHeight = kernel.Height() - 2;
		const SimCells* cells = frame->cells;
		if (gameWidth <= 0 || gameHeight <= 0 || !cells || frame->width != gameWidth || frame->height != gameHeight
			|| cells->mass.size() != (size_t)gameWidth * gameHeight || layer.otherMass.size() != layer.cells)
		{
			frameMismatch.fetch_add(1, std::memory_order_relaxed);
			return;
		}
		const size_t gameCells = (size_t)gameWidth * gameHeight;
		float* gameMass = cells->mass.first;
		std::lock_guard<std::mutex> publish(breathGuard);
		if (pressureOff.load(std::memory_order_relaxed) != 0)
		{
			// The frame is not adjusted; an old snapshot with the same mass array address no longer refers to it.
			for (FrameSnapshot& stale : snapshots) if (stale.gameMass == gameMass) stale.gameMass = nullptr;
			return;
		}
		// The snapshot of this data frame, if it already existed, otherwise the oldest one.
		FrameSnapshot* snapshot = &snapshots[0];
		for (FrameSnapshot& candidate : snapshots)
		{
			if (candidate.gameMass == gameMass) { snapshot = &candidate; break; }
			if (candidate.made < snapshot->made) snapshot = &candidate;
		}
		snapshot->gameMass = gameMass;
		snapshot->made = ++snapshotsMade;
		if (snapshot->others.size() != gameCells)
		{
			snapshot->others.assign(gameCells, 0.0f);
			snapshot->breathable.assign(gameCells, 0.0f);
		}
		// Metrics for the log are not counted on every frame: without them the loops below are simpler and faster.
		const bool count = (snapshotsMade & 31) == 1;
		int64_t patched = 0;
		double added = 0;
		snapshot->rows.clear();
		snapshot->gases.clear();
		const std::vector<int32_t>& present = kernel.Present();
		// Outside the active regions there are no non-main gases: zeros remain in the snapshot arrays there.
		kernel.ForSpans([&](size_t from, size_t to)
		{
			const int32_t y = (int32_t)(from / (size_t)width) - 1;
			if (y < 0 || y >= gameHeight) return;
			// The span lies within one row; the part without the simulation frame is taken from it.
			const size_t rowStart = (size_t)(y + 1) * (size_t)width;
			const size_t first = from > rowStart + 1 ? from : rowStart + 1;
			const size_t last = to < rowStart + 1 + (size_t)gameWidth ? to : rowStart + 1 + (size_t)gameWidth;
			if (first >= last) return;
			const size_t cellCount = last - first, game = (size_t)y * gameWidth + (first - (rowStart + 1));
			snapshot->rows.emplace_back((uint32_t)game, (uint32_t)cellCount);
			// The game sees the cell as pressure: the main gas's mass plus the extra (the other gases, or in the pressure mode
			// up to the equivalent mass); the mod gets the extra to restore the main gas's mass.
			const float* other = layer.otherMass.data() + first;
			const float* extra = layer.shownExtra.data() + first;
			float* others = snapshot->others.data() + game;
			float* mass = gameMass + game;
			memcpy(others, extra, cellCount * sizeof(float));
			for (size_t i = 0; i < cellCount; i++)
			{
				const float shown = mass[i] + extra[i];
				mass[i] = shown > 0.0f ? shown : 0.0f;
			}
			if (count)
			{
				for (size_t i = 0; i < cellCount; i++) if (extra[i] != 0.0f) { patched++; added += extra[i]; }
			}
			float* breathable = snapshot->breathable.data() + game;
			memset(breathable, 0, cellCount * sizeof(float));
			const uint16_t* projected = layer.projectedElement.data() + first;
			for (size_t i = 0; i < cellCount; i++)
			{
				if (!(other[i] > 0.0f)) continue;
				// Traces (less than a hundredth of a percent of the cell mass) do not go into the composition for the game: none of its checks
				// would notice them, and without them there are three times fewer records. Their mass remains in the cell mass.
				const float trace = mass[i] * 1e-4f;
				for (int32_t gas : present)
				{
					const float m = kernel.Mass(gas)[first + i];
					if (m > trace && projected[i] != (uint16_t)gas) snapshot->gases.push_back(FrameGas{ (int32_t)(game + i), (uint16_t)gas, 0, m });
				}
			}
			for (int32_t gas : kernel.Present())
			{
				if ((elements->elements[gas].flags & ElementBreathable) == 0) continue;
				const float* masses = kernel.Mass(gas) + first;
				for (size_t i = 0; i < cellCount; i++) breathable[i] += projected[i] != (uint16_t)gas ? masses[i] : 0.0f;
			}
		});
		if (count)
		{
			pressureCells.store(patched, std::memory_order_relaxed);
			pressureGrams.store((int64_t)(added * 1000.0), std::memory_order_relaxed);
		}
		framesPublished.fetch_add(1, std::memory_order_relaxed);
		frameMicros.fetch_add(NowMicros() - start, std::memory_order_relaxed);
	}
}

SIM_HOOK("gas-mix", void, "SimBase::CopySimDataToGame", CopyToGame, (void* self, SimData* sim, SimGameData* frame, SimGameData* previous, int32_t frames))
{
	// The game must not see a cell whose mixture is hidden from the simulation while a new gas is being written.
	if (const ElementTable* elements = publishedElements.load(std::memory_order_acquire))
	{
		std::lock_guard<std::mutex> lock(layerGuard);
		if (layer.own && !resetRequested && elements == layer.elements) ResolveHidden(sim, elements);
	}
	CopyToGame_original(self, sim, frame, previous, frames);
	PublishFrame(frame);
}

// For checking without the game (hooktest): the same as after copying cells for the game, over a fake data frame.
SIM_DATA(FrameForTest, "gas-mix.frame-for-test")
{
	if (size != (int32_t)sizeof(SimGameData)) return -1;
	PublishFrame((const SimGameData*)data);
	return 0;
}

// Breathing: requests to take non-main breathable gas, BreathRequest records in a row.
SIM_DATA(BreathConsume, "gas-mix.consume")
{
	if (size <= 0 || size % (int32_t)sizeof(BreathRequest) != 0) return -1;
	const BreathRequest* requests = (const BreathRequest*)data;
	std::lock_guard<std::mutex> lock(breathGuard);
	breathRequests.insert(breathRequests.end(), requests, requests + size / sizeof(BreathRequest));
	return 0;
}

// Which gases are in the layer: for the selection list in the overlay. In the mod's buffer: number of gases, then their element indices,
// as many as fit. Called rarely (when the overlay opens), so an ongoing layer step is waited out.
SIM_DATA(PresentGases, "gas-mix.present")
{
	if (size < (int32_t)sizeof(int32_t)) return -1;
	int32_t* out = (int32_t*)data;
	out[0] = 0;
	std::lock_guard<std::mutex> lock(layerGuard);
	if (!layer.own || moving.load(std::memory_order_relaxed) == 0) return 2;
	const int32_t capacity = size / (int32_t)sizeof(int32_t) - 1;
	for (int32_t gas : layer.kernel.Present())
	{
		if (out[0] < capacity) out[1 + out[0]++] = gas;
	}
	return 0;
}

// Mass of one gas per cell of a rectangle (the visible part of the world): for the overlay. After the header the mod's buffer
// holds a float array indexed by game cell over the whole grid; the plugin fills in only the rectangle's cells.
// If the mod also asks for the total, the first array is followed by a second identical one: the number of moles of all gases in the cell
// (mass divided by molar mass, over all gases); from it the mod computes the gas's share in the cell.
// Returns 0, 1 (the layer is busy with a step and waiting was not requested) or 2 (the layer is not moving gas), like "gas-mix.cell".
namespace
{
	struct FieldQuery
	{
		int32_t element;                // on input: element index of the gas
		int32_t wait;                   // on input: non-zero if it is fine to wait for the end of the layer step
		int32_t minX, minY, maxX, maxY; // on input: rectangle in game cells, borders included
		int32_t withTotals;             // on input: non-zero if the second array with the number of moles of all gases is needed
		int32_t reserved;
	};
}

SIM_DATA(GasField, "gas-mix.field")
{
	if (size < (int32_t)sizeof(FieldQuery)) return -1;
	const FieldQuery* query = (const FieldQuery*)data;
	float* out = (float*)((uint8_t*)data + sizeof(FieldQuery));
	std::unique_lock<std::mutex> lock(layerGuard, std::defer_lock);
	if (query->wait) lock.lock();
	else if (!lock.try_lock()) return 1;
	if (!layer.own || moving.load(std::memory_order_relaxed) == 0) return 2;
	// The simulation grid is wider and taller than the game's by one cell on each side.
	const int32_t width = layer.kernel.Width(), gameWidth = width - 2, gameHeight = layer.kernel.Height() - 2;
	if (gameWidth <= 0 || gameHeight <= 0) return -2;
	const int64_t gameCells = (int64_t)gameWidth * gameHeight;
	if ((int64_t)size < (int64_t)sizeof(FieldQuery) + gameCells * (query->withTotals ? 2 : 1) * (int64_t)sizeof(float)) return -1;
	const int32_t minX = query->minX < 0 ? 0 : query->minX, maxX = query->maxX >= gameWidth ? gameWidth - 1 : query->maxX;
	const int32_t minY = query->minY < 0 ? 0 : query->minY, maxY = query->maxY >= gameHeight ? gameHeight - 1 : query->maxY;
	if (minX > maxX || minY > maxY) return 0;
	bool present = false;
	for (int32_t gas : layer.kernel.Present()) present = present || gas == query->element;
	const float* masses = present ? layer.kernel.Mass(query->element) : nullptr;
	const size_t count = (size_t)(maxX - minX + 1);
	for (int32_t y = minY; y <= maxY; y++)
	{
		float* row = out + (size_t)y * gameWidth + minX;
		if (masses) memcpy(row, masses + (size_t)(y + 1) * width + (minX + 1), count * sizeof(float));
		else memset(row, 0, count * sizeof(float));
	}
	if (query->withTotals)
	{
		float* totals = out + gameCells;
		for (int32_t y = minY; y <= maxY; y++) memset(totals + (size_t)y * gameWidth + minX, 0, count * sizeof(float));
		for (int32_t gas : layer.kernel.Present())
		{
			const float* field = layer.kernel.Mass(gas);
			const float molar = layer.elements->elements[gas].molar;
			const float perMolar = molar > 0.0f ? 1.0f / molar : 1.0f;
			for (int32_t y = minY; y <= maxY; y++)
			{
				float* row = totals + (size_t)y * gameWidth + minX;
				const float* from = field + (size_t)(y + 1) * width + (minX + 1);
				for (size_t x = 0; x < count; x++) row[x] += from[x] * perMolar;
			}
		}
	}
	return 0;
}

// ---------- Condensation of non-main gases ----------
// The simulation performs state transitions only for a cell's main gas: a cold cell where vapour is main
// condenses entirely, while vapour making up less than half of a warm or cold cell stayed vapour forever.
// With the layer a cell's non-main gas becomes liquid when the cell is colder than its transition point by the same
// 3 degrees as in the simulation, and the cell holds at least condenseMass of it (traces of gas stay gas: this is "humidity",
// and without a threshold every cell of a cold room would drop microgram drops every tick).
// The mass is taken from the layer in the projection pass, and the liquid appears by the simulation's ordinary means right after
// it: as a falling drop, as with condensation in the game (SpawnFallingLiquid; the game draws the drop and adds the liquid
// where it lands), and if a drop is not possible (a liquid-impermeable building below the cell, a cell not revealed
// to the player, temperature outside the liquid's range), the liquid is added right into the cell (AddLiquid), as a fallen
// drop would. Above natural rock the drop appears as usual.
// The gas takes its share of the cell's germs with it (by mass), and the cell warms: when all the gas of a cell condenses
// the simulation warms it by 1.5 degrees, here the increase comes in the share of the departed heat capacity, and the liquid leaves already
// warmed. A transition with ore (Sour Gas: Liquid Methane and a third of the mass as Sulfur) drops the ore by the same means
// as the simulation (SpawnOre), if there is at least a gram of it. Not done: transitions of gas straight into solid (the game has none).
namespace
{
	typedef bool (*SpawnFallingLiquidFn)(void* events, SimData* sim, uint64_t cell, uint16_t element, float mass, float temperature,
		uint8_t diseaseIdx, int32_t diseaseCount, bool evenIfHidden);
	typedef void (*AddLiquidFn)(int32_t cell, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount, SimData* sim);

	typedef bool (*SpawnOreFn)(void* events, SimData* sim, uint64_t cell, uint16_t element, float mass, float temperature,
		uint8_t diseaseIdx, int32_t diseaseCount, bool evenIfHidden);
	typedef void (*ModifyDiseaseCountFn)(SimCellAccessor* cell, int32_t delta);

	// Called in the simulation thread without the layer lock: the simulation functions themselves send notifications the layer hooks.
	void ReleaseCondensate(SimData* sim, const std::vector<Condensed>& drops)
	{
		if (drops.empty()) return;
		static const SpawnFallingLiquidFn spawn = (SpawnFallingLiquidFn)simhook_find("SimEvents::SpawnFallingLiquid");
		static const AddLiquidFn add = (AddLiquidFn)simhook_find("AddLiquid");
		static const SpawnOreFn spawnOre = (SpawnOreFn)simhook_find("SimEvents::SpawnOre");
		static const ModifyDiseaseCountFn modifyGerms = (ModifyDiseaseCountFn)simhook_find("CellAccessor::ModifyDiseaseCount");
		SimCells* cells = sim->updatedCells;
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		for (const Condensed& drop : drops)
		{
			if (drop.liquid == NoElement)
			{
				// Solid residue of an evaporated liquid (Salt, Dirt): the record has no liquid.
				if (!(drop.ore != NoElement && drop.oreMass > 0.0f && spawnOre
					&& spawnOre(sim->simEvents, sim, (uint64_t)drop.cell, drop.ore, drop.oreMass, drop.temperature, 0xFF, 0, true)))
				{
					condensedLostGrams.fetch_add((int64_t)(drop.oreMass * 1000.0f), std::memory_order_relaxed);
				}
				continue;
			}
			// The share of the cell's germs that leaves with this gas.
			uint8_t germ = 0xFF;
			int32_t germs = 0;
			if (modifyGerms && (size_t)drop.cell < cells->diseaseIdx.size() && (size_t)drop.cell < cells->diseaseCount.size()
				&& cells->diseaseIdx.first[drop.cell] != 0xFF && cells->diseaseCount.first[drop.cell] > 0)
			{
				germs = (int32_t)((float)cells->diseaseCount.first[drop.cell] * (drop.share < 1.0f ? drop.share : 1.0f));
				if (germs > 0)
				{
					germ = cells->diseaseIdx.first[drop.cell];
					SimCellAccessor accessor = { cells, drop.cell };
					modifyGerms(&accessor, -germs);
					condensedGerms.fetch_add(germs, std::memory_order_relaxed);
				}
			}
			float liquidMass = drop.mass;
			int32_t liquidGerms = germs;
			if (drop.ore != NoElement && drop.oreMass > OreMinMass && drop.oreMass < drop.mass && spawnOre)
			{
				const int32_t oreGerms = (int32_t)((float)germs * (drop.oreMass / drop.mass));
				// The simulation drops ore even in a cell not revealed to the player.
				if (spawnOre(sim->simEvents, sim, (uint64_t)drop.cell, drop.ore, drop.oreMass, drop.temperature, oreGerms > 0 ? germ : (uint8_t)0xFF, oreGerms, true))
				{
					liquidMass -= drop.oreMass;
					liquidGerms -= oreGerms;
					condensedOreGrams.fetch_add((int64_t)(drop.oreMass * 1000.0f), std::memory_order_relaxed);
				}
			}
			const uint8_t liquidGerm = liquidGerms > 0 ? germ : (uint8_t)0xFF;
			// The cell is colder than the liquid's freezing point (by the phase rules vapour can exist there too): the vapour falls straight out
			// as a solid, a chunk of ice. Liquid in such a cell would immediately freeze into a whole cell of rock.
			if (elements && drop.liquid < elements->elements.size() && spawnOre)
			{
				const ElementInfo& liquid = elements->elements[drop.liquid];
				if (drop.temperature < liquid.lowTemp && liquid.lowTransition < elements->elements.size()
					&& elements->elements[liquid.lowTransition].state == SIM_SOLID
					&& spawnOre(sim->simEvents, sim, (uint64_t)drop.cell, liquid.lowTransition, liquidMass, drop.temperature, liquidGerm, liquidGerms, true))
				{
					frostGrams.fetch_add((int64_t)(liquidMass * 1000.0f), std::memory_order_relaxed);
					continue;
				}
			}
			// The same liquid already lies below the cell (through open cells): the portion is added straight to it.
			// The drop would land there too, but every drop is work for the game's main thread (particle, sound) and a puddle
			// on the way, and over a pool condensate comes every tick.
			const int32_t landing = elements ? LandingCell(sim, elements, drop.cell, drop.liquid) : drop.cell;
			if (add && landing != drop.cell && (size_t)landing < cells->elementIdx.size() && cells->elementIdx.first[landing] == drop.liquid)
			{
				add(landing, drop.liquid, liquidMass, drop.temperature, liquidGerm, liquidGerms, sim);
				condensedPooled.fetch_add(1, std::memory_order_relaxed);
				continue;
			}
			if (spawn && spawn(sim->simEvents, sim, (uint64_t)drop.cell, drop.liquid, liquidMass, drop.temperature, liquidGerm, liquidGerms, false))
			{
				condensedDrips.fetch_add(1, std::memory_order_relaxed);
			}
			else if (add)
			{
				add(landing, drop.liquid, liquidMass, drop.temperature, liquidGerm, liquidGerms, sim);
				condensedInPlace.fetch_add(1, std::memory_order_relaxed);
			}
			else condensedLostGrams.fetch_add((int64_t)(liquidMass * 1000.0f), std::memory_order_relaxed);
		}
	}
}

// ---------- Germ growth and death by mixture composition ----------
// The simulation computes a cell's germ growth and death (Disease::PostProcess, at once over a rectangle of cells) from its
// element and mass: each germ kind has its own numbers for each substance (half-life, "count per
// kilogram" limits, die-off rate). The cell's element is the main gas, and the mass is only its own too: 30% Chlorine in a mixture
// did not kill germs, and per-kilogram limits were computed from part of the gas.
// With the layer, for a cell with a mixture the simulation's computation is repeated for each gas of the mixture, as if the whole mixture were
// that gas (with its own function, one cell at a time), and the resulting change in germ count is the average weighted by the mass
// shares of the gases. Cells with a single gas are computed by the simulation itself, as before.
namespace
{
	typedef void (*GermGrowthFn)(void* diseases, SimData* sim, void* events, int32_t x0, int32_t x1, int32_t y0, int32_t y1);

	struct GermMixCell
	{
		int32_t cell;
		uint8_t kind, infestation;
		int32_t count;
		float error;
		uint16_t main;
		float mainMass;
		int gases;
		uint16_t gas[8];
		float mass[8];
	};

	// Cells of the rectangle (in simulation cells, the right and top borders not included) that have germs and a mixture.
	void CollectGermMixCells(SimData* sim, int32_t x0, int32_t x1, int32_t y0, int32_t y1, std::vector<GermMixCell>& found)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements || moving.load(std::memory_order_relaxed) == 0) return;
		std::lock_guard<std::mutex> lock(layerGuard);
		if (!layer.own || resetRequested || elements != layer.elements) return;
		SimCells* cells = sim->updatedCells;
		const size_t count = cells->elementIdx.size();
		if (cells->diseaseIdx.size() < count || cells->diseaseCount.size() < count || cells->diseaseInfestationTickCount.size() < count
			|| cells->diseaseGrowthAccumulatedError.size() < count || cells->mass.size() < count) return;
		for (int32_t y = y0; y < y1; y++)
		{
			for (int32_t x = x0; x < x1; x++)
			{
				const int32_t cell = y * sim->width + x;
				if (cell < 0 || (size_t)cell >= count || cells->diseaseIdx.first[cell] == 0xFF || cells->diseaseCount.first[cell] <= 0) continue;
				if (layer.otherMass[cell] <= 0.0f || !LayerReady(elements, cell)) continue;
				const uint16_t main = cells->elementIdx.first[cell];
				if (!elements->IsGas(main) || !(cells->mass.first[cell] > 0.0f)) continue;
				GermMixCell mix = {};
				mix.cell = cell;
				mix.kind = cells->diseaseIdx.first[cell];
				mix.infestation = cells->diseaseInfestationTickCount.first[cell];
				mix.count = cells->diseaseCount.first[cell];
				mix.error = cells->diseaseGrowthAccumulatedError.first[cell];
				mix.main = main;
				mix.mainMass = cells->mass.first[cell];
				mix.gas[0] = main;
				mix.mass[0] = mix.mainMass;
				mix.gases = 1;
				for (int32_t gas : layer.kernel.Present())
				{
					const float m = layer.kernel.Mass(gas)[cell];
					if ((uint16_t)gas == main || !(m > 0.0f) || mix.gases >= 8) continue;
					mix.gas[mix.gases] = (uint16_t)gas;
					mix.mass[mix.gases] = m;
					mix.gases++;
				}
				if (mix.gases > 1) found.push_back(mix);
			}
		}
	}

	// Recomputing cells with a mixture. grow: the simulation's computation for a rectangle of cells.
	template <class Grow> void BlendGermGrowth(SimData* sim, const std::vector<GermMixCell>& found, Grow&& grow)
	{
		SimCells* cells = sim->updatedCells;
		for (const GermMixCell& mix : found)
		{
			const int32_t x = mix.cell % sim->width, y = mix.cell / sim->width;
			float whole = 0.0f;
			for (int g = 0; g < mix.gases; g++) whole += mix.mass[g];
			double blended = 0;
			for (int g = 0; g < mix.gases; g++)
			{
				// The cell as before the computation, but the whole mixture is treated as this gas.
				cells->elementIdx.first[mix.cell] = mix.gas[g];
				cells->mass.first[mix.cell] = whole;
				cells->diseaseIdx.first[mix.cell] = mix.kind;
				cells->diseaseCount.first[mix.cell] = mix.count;
				cells->diseaseGrowthAccumulatedError.first[mix.cell] = mix.error;
				cells->diseaseInfestationTickCount.first[mix.cell] = mix.infestation;
				grow(x, x + 1, y, y + 1);
				// Change per tick: the integer part went into the germ count, the fractional part into the accumulated error.
				const double change = (double)(cells->diseaseCount.first[mix.cell] - mix.count) + cells->diseaseGrowthAccumulatedError.first[mix.cell];
				blended += change * (mix.mass[g] / whole);
			}
			cells->elementIdx.first[mix.cell] = mix.main;
			cells->mass.first[mix.cell] = mix.mainMass;
			const int32_t whole_change = (int32_t)blended;
			const int32_t count = mix.count + whole_change;
			if (count < 1)
			{
				cells->diseaseIdx.first[mix.cell] = 0xFF;
				cells->diseaseCount.first[mix.cell] = 0;
				cells->diseaseInfestationTickCount.first[mix.cell] = 0;
				cells->diseaseGrowthAccumulatedError.first[mix.cell] = 0.0f;
			}
			else
			{
				cells->diseaseIdx.first[mix.cell] = mix.kind;
				cells->diseaseCount.first[mix.cell] = count;
				cells->diseaseGrowthAccumulatedError.first[mix.cell] = (float)(blended - (double)whole_change);
				cells->diseaseInfestationTickCount.first[mix.cell] = mix.infestation < 0xFD ? (uint8_t)(mix.infestation + 1) : (uint8_t)0xFE;
			}
			germGrowthCells.fetch_add(1, std::memory_order_relaxed);
		}
	}
}

SIM_HOOK("gas-mix", void, "Disease::PostProcess", GermGrowth, (void* diseases, SimData* sim, void* events, int32_t x0, int32_t x1, int32_t y0, int32_t y1))
{
	static std::vector<GermMixCell> found;
	found.clear();
	CollectGermMixCells(sim, x0, x1, y0, y1, found);
	GermGrowth_original(diseases, sim, events, x0, x1, y0, y1);
	if (found.empty()) return;
	const int64_t start = NowMicros();
	BlendGermGrowth(sim, found, [&](int32_t fromX, int32_t toX, int32_t fromY, int32_t toY) { GermGrowth_original(diseases, sim, events, fromX, toX, fromY, toY); });
	germGrowthMicros.fetch_add(NowMicros() - start, std::memory_order_relaxed);
}

// The germs' own diffusion between neighbouring cells (Disease::UpdateCells): the simulation takes its rate from
// the germ kind's table by the cell's substance (GetDiffusionScale): zero while the cell holds fewer germs than the threshold for
// that substance or the infestation is younger than the threshold, otherwise the substance's multiplier. For a cell with a mixture the average
// weighted by the gases' mass shares is taken, as for growth and death.
SIM_HOOK("gas-mix", float, "Disease::GetDiffusionScale", GermDiffusionScale, (SimDiseaseTable* diseases, SimData* sim, int32_t cell))
{
	const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
	if (elements && diseases && moving.load(std::memory_order_relaxed) != 0)
	{
		std::lock_guard<std::mutex> lock(layerGuard);
		// Here the simulation reads the working copy of the cells (cells); the substance in it is the same as in the main one.
		const SimCells* cells = sim->cells;
		if (layer.own && !resetRequested && elements == layer.elements && LayerReady(elements, cell) && layer.otherMass[cell] > 0.0f
			&& (size_t)cell < cells->elementIdx.size() && (size_t)cell < cells->diseaseIdx.size() && (size_t)cell < cells->diseaseCount.size()
			&& (size_t)cell < cells->diseaseInfestationTickCount.size() && (size_t)cell < cells->mass.size())
		{
			const uint16_t main = cells->elementIdx.first[cell];
			const uint8_t kind = cells->diseaseIdx.first[cell];
			if (elements->IsGas(main) && kind < diseases->diseases.size())
			{
				const SimDiseaseInfo& info = diseases->diseases.first[kind];
				const int32_t count = cells->diseaseCount.first[cell];
				const uint8_t infested = cells->diseaseInfestationTickCount.first[cell];
				double weighted = 0, whole = 0;
				bool known = true;
				auto add = [&](uint16_t gas, float mass)
				{
					if (!(mass > 0.0f)) return;
					if (gas >= info.diffusionScale.size() || gas >= info.minDiffusionCount.size() || gas >= info.minDiffusionInfestationTickCount.size())
					{
						known = false;
						return;
					}
					whole += mass;
					if (count >= info.minDiffusionCount.first[gas] && infested >= info.minDiffusionInfestationTickCount.first[gas]) weighted += (double)mass * info.diffusionScale.first[gas];
				};
				add(main, cells->mass.first[cell]);
				for (int32_t gas : layer.kernel.Present()) if ((uint16_t)gas != main) add((uint16_t)gas, layer.kernel.Mass(gas)[cell]);
				if (known && whole > 0)
				{
					germDiffusionBlended.fetch_add(1, std::memory_order_relaxed);
					return (float)(weighted / whole);
				}
			}
		}
	}
	return GermDiffusionScale_original(diseases, sim, cell);
}

// For checking without the game (hooktest): the same recomputation with a fake rule instead of the simulation: in gas 1 germs
// do not change, in gas 2 a tenth die per tick, in the other gases half die.
SIM_DATA(GermGrowthForTest, "gas-mix.germ-growth-for-test")
{
	struct Test { SimData* sim; int32_t x0, x1, y0, y1; int32_t cells; };
	if (size != (int32_t)sizeof(Test)) return -1;
	Test* test = (Test*)data;
	SimData* sim = test->sim;
	std::vector<GermMixCell> found;
	CollectGermMixCells(sim, test->x0, test->x1, test->y0, test->y1, found);
	test->cells = (int32_t)found.size();
	BlendGermGrowth(sim, found, [&](int32_t fromX, int32_t, int32_t fromY, int32_t)
	{
		SimCells* cells = sim->updatedCells;
		const int32_t cell = fromY * sim->width + fromX;
		const uint16_t element = cells->elementIdx.first[cell];
		const float keep = element == 1 ? 1.0f : element == 2 ? 0.9f : 0.5f;
		const float total = (float)cells->diseaseCount.first[cell] * (keep - 1.0f) + cells->diseaseGrowthAccumulatedError.first[cell];
		cells->diseaseCount.first[cell] += (int32_t)total;
		cells->diseaseGrowthAccumulatedError.first[cell] = total - (float)(int32_t)total;
	});
	return 0;
}

// For checking without the game (hooktest): what condensed during the last layer pass. The passed buffer receives
// the number of portions and up to four Condensed records.
SIM_DATA(CondensedForTest, "gas-mix.condensed-for-test")
{
	if (size != (int32_t)(sizeof(int32_t) * 2 + 4 * sizeof(Condensed))) return -1;
	std::lock_guard<std::mutex> lock(layerGuard);
	int32_t* count = (int32_t*)data;
	Condensed* out = (Condensed*)((uint8_t*)data + sizeof(int32_t) * 2);
	*count = (int32_t)layer.condensed.size();
	for (size_t i = 0; i < layer.condensed.size() && i < 4; i++) out[i] = layer.condensed[i];
	return 0;
}

// Main pass of a simulation tick.
SIM_HOOK("gas-mix", void, "SimBase::UpdateData", UpdateData, (void* self, SimData* sim))
{
	const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
	if (!elements)
	{
		skippedNoElements.fetch_add(1, std::memory_order_relaxed);
		UpdateData_original(self, sim);
		return;
	}
	if (moving.load(std::memory_order_relaxed))
	{
		const int64_t tickStart = NowMicros();
		// The layer moves gas and writes the projection before the tick: the simulation immediately works with the new cell state.
		static std::vector<Condensed> drops;
		drops.clear();
		{
			std::lock_guard<std::mutex> lock(layerGuard);
			MoveStep(sim, elements, true);
			drops.swap(layer.condensed);
		}
		ReleaseCondensate(sim, drops);
		UpdateData_original(self, sim);
		tickMicros.fetch_add(NowMicros() - tickStart, std::memory_order_relaxed);
	}
	else
	{
		UpdateData_original(self, sim);
		std::lock_guard<std::mutex> lock(layerGuard);
		Mirror(sim, elements);
	}
}

// ---------- Gas movement in the simulation itself ----------
// The simulation moves gas with three functions . When the layer moves gas or the
// "gas-mix.freeze-sim" experiment is on, they return "nothing transferred"; everything else in the simulation goes as usual
// (heat, liquids, state transitions, sources and consumers).

namespace
{
	inline bool SimMovesGas()
	{
		return moving.load(std::memory_order_relaxed) == 0 && simFrozen.load(std::memory_order_relaxed) == 0;
	}

	inline bool IsGasOrVacuum(const ElementTable* elements, uint16_t element)
	{
		return element < elements->elements.size() && (elements->elements[element].state == SIM_GAS || elements->elements[element].state == SIM_VACUUM);
	}

	inline void Count(std::atomic<int64_t>& counter)
	{
		counter.fetch_add(1, std::memory_order_relaxed);
	}
}

// Mass equalisation between a gas cell and a neighbour (the same gas or vacuum). Returns the transferred mass.
SIM_HOOK("gas-mix", float, "UpdatePressure", UpdatePressure,
	(void* sim, void* events, void* cells, void* updatedCells, int32_t cell, uint16_t element, int8_t state, float flow, int32_t neighbour))
{
	Count(pressureCalls);
	if (!SimMovesGas())
	{
		Count(pressureSkipped);
		return 0.0f; // the calling code will add zero to the flow picture
	}
	return UpdatePressure_original(sim, events, cells, updatedCells, cell, element, state, flow, neighbour);
}

// Different gases push each other. Returns the transferred mass.
SIM_HOOK("gas-mix", float, "DoGasPressureDisplacement", GasDisplacement,
	(uint16_t element, int32_t cell, int32_t from, int32_t to, void* sim))
{
	Count(displacementCalls);
	if (!SimMovesGas())
	{
		Count(displacementSkipped);
		return 0.0f;
	}
	return GasDisplacement_original(element, cell, from, to, sim);
}

// Density stratification, shared by gases and liquids; only gas is stopped.
SIM_HOOK("gas-mix", bool, "DoDensityDisplacement", DensityDisplacement,
	(void* sim, void* events, int32_t cell, const SimElementPostProcess* element, float chance))
{
	Count(densityCalls);
	if (!SimMovesGas() && (element->state & 3) == SIM_GAS)
	{
		Count(densitySkipped);
		return false; // the cells did not swap places
	}
	return DensityDisplacement_original(sim, events, cell, element, chance);
}

// Random swapping of gas cells. At the end of a tick, for each gas cell untouched by density stratification,
// the simulation swaps it with a 10% chance with a neighbouring gas cell (left, right or below;
// the colder one is chosen, the lower one also by molar mass): this is its own gas mixing.
// Found by the survey: it was this that changed the main gas in cells about 60 times per tick while the layer was moving gas.
// The swapping sits right in the code of PostProcessCell and has no function of its own; the common cell swap function
// is hooked, and the swap is skipped only if called from PostProcessCell and both cells hold gas or vacuum
// (swapping gas with an empty cell is the same mixing). Liquids and everything else swap places as before.
// Gas displacement before writing a new gas into a cell (described at ResolveHidden). The places in the simulation code where
// gas is written into the same cell right after DisplaceGas; the other calls (arrival of liquid and solid) go as before.
// These places are recognised by the return address, hence SIMHOOK_DIRECT.
SIM_HOOK_ORDERED("gas-mix", SIMHOOK_DIRECT, bool, "DisplaceGas", DisplaceGas, (SimData* sim, void* events, SimCells* cells, int32_t cell, uint16_t element))
{
	if (!SimMovesGas())
	{
		// The cell was shown the mass of the whole mixture (pressure limit): the real main gas mass must be displaced.
		if (limitRaise.cell == cell)
		{
			std::lock_guard<std::mutex> lock(layerGuard);
			LowerAfterLimit(sim);
		}
		// Function lengths in this SimDLL build (the library is tied to the build).
		static const struct { const char* name; uintptr_t size; uintptr_t start; } writers[] =
		{
			{ "ProcessMassEmission", 1187, (uintptr_t)simhook_find("ProcessMassEmission") },
			{ "DoPartialHeatTransition", 2033, (uintptr_t)simhook_find("DoPartialHeatTransition") },
			{ "DoSublimation", 1718, (uintptr_t)simhook_find("DoSublimation") },
			{ "PostProcessCell", 4034, (uintptr_t)simhook_find("PostProcessCell") },
		};
		const uintptr_t caller = (uintptr_t)__builtin_return_address(0);
		bool writesGas = false;
		for (const auto& writer : writers) writesGas = writesGas || (writer.start != 0 && caller >= writer.start && caller < writer.start + writer.size);
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (writesGas && elements && elements->IsGas(element))
		{
			std::lock_guard<std::mutex> lock(layerGuard);
			if (layer.own && !resetRequested && elements == layer.elements && HideForArrival(sim, elements, cell)) return true;
		}
		if (DisplaceGas_original(sim, events, cells, cell, element)) return true;
		// The simulation has nowhere to displace to; the layer hands the cell's gas to neighbours with a different main gas.
		if (!elements || cells != sim->updatedCells) return false;
		std::lock_guard<std::mutex> lock(layerGuard);
		return layer.own && !resetRequested && elements == layer.elements && DisplaceByLayer(sim, elements, cell);
	}
	return DisplaceGas_original(sim, events, cells, cell, element);
}

// For checking without the game (hooktest): displacing a cell's gas by the layer's means, as after the simulation's refusal.
// Record: cells structure, cell index; in reply 1 or 0 is put into the cell index.
SIM_DATA(DisplaceForTest, "gas-mix.displace-for-test")
{
	struct Test { SimData* sim; int32_t cell; };
	if (size != (int32_t)sizeof(Test)) return -1;
	Test* test = (Test*)data;
	const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
	if (!elements) return -2;
	std::lock_guard<std::mutex> lock(layerGuard);
	test->cell = layer.own && DisplaceByLayer(test->sim, elements, test->cell) ? 1 : 0;
	return 0;
}

// For checking without the game (hooktest): hide a cell's mixture the same way the DisplaceGas hook does.
// Record: cells structure, cell index; in reply 1 or 0 is put into the cell index.
SIM_DATA(HideForTest, "gas-mix.hide-for-test")
{
	struct Test { SimData* sim; int32_t cell; };
	if (size != (int32_t)sizeof(Test)) return -1;
	Test* test = (Test*)data;
	const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
	if (!elements) return -2;
	std::lock_guard<std::mutex> lock(layerGuard);
	test->cell = layer.own && HideForArrival(test->sim, elements, test->cell) ? 1 : 0;
	return 0;
}

// SIMHOOK_DIRECT: a cell swap from PostProcessCell is recognised by the return address.
SIM_HOOK_ORDERED("gas-mix", SIMHOOK_DIRECT, void, "CellSOA::SwapCells", SwapCells, (SimCells* cells, int32_t first, int32_t second))
{
	if (!SimMovesGas())
	{
		static const uintptr_t postProcess = (uintptr_t)simhook_find("PostProcessCell");
		const uintptr_t postProcessSize = 4034; // length of PostProcessCell in this SimDLL build (the library is tied to the build)
		const uintptr_t caller = (uintptr_t)__builtin_return_address(0);
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (postProcess != 0 && caller >= postProcess && caller < postProcess + postProcessSize && elements
			&& first >= 0 && second >= 0 && (size_t)first < cells->elementIdx.size() && (size_t)second < cells->elementIdx.size()
			&& IsGasOrVacuum(elements, cells->elementIdx.first[first]) && IsGasOrVacuum(elements, cells->elementIdx.first[second]))
		{
			Count(swapsBlocked);
			return;
		}
	}
	SwapCells_original(cells, first, second);
}

// Pressure limit for gas emission (described at LimitRaise): processing of a marked cell is shown the mass
// of the whole mixture. The function is called for every cell every tick, so unmarked cells pass without the lock.
SIM_HOOK("gas-mix", void, "PostProcessCell", PostProcessCell, (SimData* sim, void* events, int32_t cell))
{
	// The marks are written by the layer pass in this same simulation thread.
	if (moving.load(std::memory_order_relaxed) == 0 || cell < 0 || (size_t)cell >= layer.limitKind.size() || layer.limitKind[cell] == 0)
	{
		PostProcessCell_original(sim, events, cell);
		return;
	}
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		std::lock_guard<std::mutex> lock(layerGuard);
		RaiseBeforePostProcess(sim, elements, cell);
	}
	PostProcessCell_original(sim, events, cell);
	std::lock_guard<std::mutex> lock(layerGuard);
	LowerAfterLimit(sim);
}

// For checking without the game (hooktest): the same as the hook does before and after processing a cell. Record: cells
// structure, cell index, 1 (before processing) or 0 (after); in reply the cell's mark is put into the cell index.
SIM_DATA(LimitForTest, "gas-mix.limit-for-test")
{
	struct Test { SimData* sim; int32_t cell; int32_t raise; };
	if (size != (int32_t)sizeof(Test)) return -1;
	Test* test = (Test*)data;
	const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
	if (!elements) return -2;
	std::lock_guard<std::mutex> lock(layerGuard);
	const uint8_t kind = test->cell >= 0 && (size_t)test->cell < layer.limitKind.size() ? layer.limitKind[test->cell] : 0;
	if (test->raise) RaiseBeforePostProcess(test->sim, elements, test->cell);
	else LowerAfterLimit(test->sim);
	test->cell = kind;
	return 0;
}

// Clearing a cell to vacuum. The simulation calls it from several places (small mass of gas or liquid, melting,
// sublimation, crumbling); for a gas cell this is the small-mass rule from PostProcessCell: gas lighter than 1e-9 kg
// is always erased, and gas lighter than a gram is erased if there is a gas cell of a kilogram or more nearby. The second is harmful
// to the layer: the cell shows only the main gas, and the simulation would erase, for example, Hydrogen that has the most molecules
// in the cell but weighs less than a gram. Therefore, while the layer moves gas, the simulation is not allowed to erase a gas
// cell with a mass of 1e-9 kg or more. The first stays: this is also how the simulation turns into vacuum a cell
// whose mass was drained to zero by space or a consumer.
// Earlier place in the chain than usual: the survey records the address of the calling game code.
SIM_HOOK_ORDERED("gas-mix", -100, void, "Evaporate", Evaporate, (int32_t cell, SimData* sim, void* events))
{
	if (moving.load(std::memory_order_relaxed) != 0)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		const SimCells* cells = sim->updatedCells;
		const bool gas = elements && cell >= 0 && (size_t)cell < cells->elementIdx.size() && (size_t)cell < cells->mass.size()
			&& elements->IsGas(cells->elementIdx.first[cell]);
		if (gas && cells->mass.first[cell] >= TraceMass)
		{
			Count(evaporationsBlocked);
			return;
		}
		if (gas) NoteChanger(__builtin_return_address(0), 2);
	}
	Evaporate_original(cell, sim, events);
}
