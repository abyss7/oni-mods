// Pressure plugin: boiling, evaporation and condensation depend on pressure, and a transition absorbs and releases latent heat.
// The rules and their physics: phase_rules.h. The plugin does two things.
//
// 1. Forbids the simulation its own transitions between liquid and gas (hook set "pressure").
//    The simulation changes a cell's state by temperature alone: gas 3 degrees colder than the transition point
//    becomes liquid entirely, hotter liquid becomes gas entirely (DoStateTransition), and liquid heavier than 5 kg
//    next to a hot neighbour throws out 5 kg of gas (DoPartialHeatTransition). Under the phase rules gas colder than the
//    boiling point stays gas while there is less of it than the saturated vapour pressure holds, and liquid hotter than
//    the boiling point stays liquid while gas presses on it. Other transitions (melting, freezing) work as
//    in the game. Liquid hotter than the critical temperature is turned into gas by the simulation itself: no liquid exists there.
//
// 2. Computes the phase rules itself (set "pressure-cells") when there is no gas mixture mod: a cell holds one gas, and
//    no layer is needed. Gas in excess of saturation condenses and the cell warms up. Liquid gives vapour to the
//    neighbouring cell if it holds vacuum or the liquid's own vapour; vapour goes into a cell with another gas only when the liquid
//    boils (its vapour pressure is above that gas's pressure): the gas is then displaced, as with boiling in the game.
//    Here vapour has nowhere to mix with a foreign gas, so in this mode there is no quiet evaporation of water into air.
//
// With the gas mixture mod the second set is not enabled: the cells hold mixtures, and the mixture layer computes the same
// rules by partial pressures (value gas-mix.phase); from this plugin it only needs the transition ban.
#include <windows.h>

#include <atomic>
#include <mutex>
#include <unordered_map>
#include <vector>

#include "element_table.h"
#include "liquid_service.h"
#include "simhook_plugin.h"

SIM_PLUGIN_MODULE();
SIM_PLUGIN_BUILD("pressure", 0x6a67f9d8, 0x125000);
SIM_PLUGIN_BUILD("pressure-cells", 0x6a67f9d8, 0x125000);

// Ban on the simulation's transitions between liquid and gas. Enable only together with computing the phase rules (own pass
// or the mixture layer): otherwise liquid would stop boiling altogether.
SIM_FLAG(active, "pressure.active");
// Own pass over cells (mode without the gas mixture mod).
SIM_FLAG(cellsOn, "pressure.cells");

SIM_COUNTER(blockedCondensations, "pressure.blocked-condensations"); // times the simulation was not allowed to liquefy a whole cell of cold gas
SIM_COUNTER(blockedBoilings, "pressure.blocked-boilings");           // times it was not allowed to turn a whole cell of hot liquid into gas
SIM_COUNTER(blockedFlashes, "pressure.blocked-flashes");             // times it was not allowed to throw 5 kg of gas out of a liquid next to a hot neighbour
SIM_COUNTER(passes, "pressure.passes");                              // passes over cells done
SIM_COUNTER(passMicros, "pressure.pass-micros");                     // their time, total
SIM_COUNTER(tickMicros, "pressure.tick-micros");                     // time of the whole simulation tick including the own pass, total
SIM_COUNTER(tickCount, "pressure.ticks");
SIM_COUNTER(evaporations, "pressure.evaporations");                  // times a liquid gave vapour to a neighbouring cell
SIM_COUNTER(boilings, "pressure.boilings");                          // of these, the liquid was boiling
SIM_COUNTER(evaporatedGrams, "pressure.evaporated-grams");
SIM_COUNTER(evaporatedWhole, "pressure.evaporated-whole");           // cells of liquid that evaporated entirely
SIM_COUNTER(displacedForSteam, "pressure.displaced-for-steam");      // times a boiling liquid displaced a foreign gas from the neighbouring cell
SIM_COUNTER(condensations, "pressure.condensations");                // portions of gas that became liquid
SIM_COUNTER(condensedGrams, "pressure.condensed-grams");
SIM_COUNTER(drips, "pressure.drips");                                // of these, fell as a drop
SIM_COUNTER(pooled, "pressure.pooled");                              // went straight into the same liquid below the cell, without a drop
SIM_COUNTER(inPlace, "pressure.in-place");                           // appeared as liquid right in the cell
SIM_COUNTER(frostGrams, "pressure.frost-grams");                     // came out solid right away (the cell is colder than the liquid's freezing point)
SIM_COUNTER(oreGrams, "pressure.ore-grams");                         // came out as a separate solid element (sulfur, salt)
SIM_COUNTER(lostGrams, "pressure.lost-grams");                       // could be released neither as a drop nor into the cell (should be 0)

namespace
{
	// Transition numbers from the simulation (DoStateTransition): the transition happens 3 degrees past the point.
	const float TransitionMargin = 3.0f;
	const float OreMinMass = 0.001f;

	std::atomic<ElementTable*> publishedElements{nullptr};
	// "Vapour and liquid" pairs: rebuilt when the element table or the rule settings arrive.
	std::atomic<PhaseTables*> publishedPhases{nullptr};
	std::mutex phaseGuard;
	PhaseConfig phaseConfig;

	void RebuildPhases()
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		if (!elements) return;
		PhaseTables* tables = new PhaseTables;
		tables->Build(elements->elements, phaseConfig);
		// Old tables are not freed: simulation threads may be reading them right now.
		publishedPhases.store(tables, std::memory_order_release);
	}

	// What left the cells during the pass and is spawned by the simulation's own means after it: liquid (a drop or right
	// into the cell) and solid (transition ore, evaporation residue, frost). liquid == NoElement: only the solid in ore.
	struct Released
	{
		int32_t cell;
		uint16_t liquid;
		uint16_t ore;
		float mass;
		float oreMass;
		float temperature;
	};

	struct State
	{
		std::mutex guard;
		float evaporationRate = 0.1f; // what share of the way to equilibrium a non-boiling liquid covers per pass
		float boilRate = 0.5f;        // what share of the heat above the boiling point a boiling liquid gives to vapour per pass
		float portion = 0.01f;        // condensate and residue portions smaller than this are not released
		std::unordered_map<uint32_t, float> residue; // solid evaporation residue per liquid cell, while it is below one portion
		std::vector<Released> released;
		std::vector<double> condensedBy, evaporatedBy; // by element index: how much gas condensed, how much liquid evaporated, kg
	} state;

	typedef bool (*DisplaceGasFn)(SimData* sim, void* events, SimCells* cells, int32_t cell, uint16_t element);
	typedef void (*ChangeSubstanceFn)(void* events, SimData* sim, uint64_t cell);

	int64_t NowMicros()
	{
		LARGE_INTEGER now, frequency;
		QueryPerformanceCounter(&now);
		QueryPerformanceFrequency(&frequency);
		return (int64_t)(now.QuadPart * 1000000 / frequency.QuadPart);
	}

	// 1: the simulation would liquefy this cell's gas entirely; 2: would turn its liquid into gas; 0: not our transition.
	int BlockedTransition(const SimData* sim, const ElementTable* elements, const PhaseTables* phases, int32_t cell)
	{
		const SimCells* cells = sim->updatedCells;
		if (cell < 0 || (size_t)cell >= cells->elementIdx.size() || (size_t)cell >= cells->temperature.size()) return 0;
		const uint16_t element = cells->elementIdx.first[cell];
		// Called for every cell every tick: for most cells (rock, vacuum, gas without liquid) the answer
		// is visible from a single byte.
		if (element >= phases->kind.size() || element >= elements->elements.size()) return 0;
		const uint8_t kind = phases->kind[element];
		if (kind == 0) return 0;
		const ElementInfo& info = elements->elements[element];
		const float temperature = cells->temperature.first[cell];
		if (kind == 1) return temperature < info.lowTemp - TransitionMargin ? 1 : 0;
		// The simulation checks freezing first, and that stays with it.
		if (info.lowTransition != NoElement && temperature < info.lowTemp - TransitionMargin) return 0;
		return temperature > info.highTemp + TransitionMargin && temperature < phases->liquidVapour[element].critical ? 2 : 0;
	}

	// Phase rules pass over the simulation's cells (one substance per cell). displace and changed may be empty
	// (check without the game): then foreign gas is not displaced and no element-change notifications are sent.
	void Pass(SimData* sim, const ElementTable* elements, const PhaseTables* phases, DisplaceGasFn displace, ChangeSubstanceFn changed)
	{
		const int64_t start = NowMicros();
		SimCells* cells = sim->updatedCells;
		size_t count = cells->elementIdx.size();
		if (cells->mass.size() < count) count = cells->mass.size();
		if (cells->temperature.size() < count) count = cells->temperature.size();
		if (cells->properties.size() < count) count = cells->properties.size();
		const int32_t width = sim->width;
		if (width <= 0 || count < (size_t)width * 3) return;
		const int32_t rows = (int32_t)(count / (size_t)width);
		uint16_t* element = cells->elementIdx.first;
		float* mass = cells->mass.first;
		float* temperature = cells->temperature.first;
		const uint8_t* properties = cells->properties.first;
		const size_t numElements = elements->elements.size();
		const ptrdiff_t sides[4] = { (ptrdiff_t)width, -1, 1, -(ptrdiff_t)width };
		int64_t times = 0, boiled = 0, whole = 0, pushed = 0, portions = 0;
		double gone = 0, wet = 0;
		auto notify = [&](size_t cell) { if (changed) changed(sim->simEvents, sim, (uint64_t)cell); };

		// The simulation does not compute the whole grid, only the active regions (open worlds): nothing outside them is touched.
		for (const SimActiveRegion* region = sim->activeRegions.first; region != sim->activeRegions.last; region++)
		{
			const int32_t minX = region->minX < 1 ? 1 : region->minX, maxX = region->maxX > width - 1 ? width - 1 : region->maxX;
			const int32_t minY = region->minY < 1 ? 1 : region->minY, maxY = region->maxY > rows - 1 ? rows - 1 : region->maxY;
			for (int32_t y = minY; y < maxY; y++)
			{
				for (size_t cell = (size_t)y * width + minX, last = (size_t)y * width + maxX; cell < last; cell++)
				{
					const uint16_t own = element[cell];
					if (own >= numElements || phases->kind[own] != 1 || !(mass[cell] > 0.0f) || !(temperature[cell] > 0.0f)) continue;
					const ElementInfo& info = elements->elements[own];
					{
						// Condensation: the cell has more gas than the saturated vapour pressure holds.
						const phase::Vapour& vapour = phases->gasVapour[own];
						if (!vapour.Valid() || !(temperature[cell] < vapour.critical) || mass[cell] - vapour.SaturationMass(temperature[cell]) < state.portion) continue;
						// The liquid needs somewhere to appear: a neighbouring cell with the same gas or vacuum (the drop falls
						// through them, and the gas of the cell where it lands is displaced there) or the same liquid lying right
						// below the cell. In a lone gas cell with no way out the vapour stays vapour.
						bool outlet = cell >= (size_t)width && element[cell - width] == info.lowTransition;
						for (int side = 0; side < 4 && !outlet; side++)
						{
							const size_t beside = (size_t)((ptrdiff_t)cell + sides[side]);
							outlet = beside < count && (properties[beside] & 1) == 0 && (element[beside] == own || element[beside] == sim->vacuumElementIdx);
						}
						if (!outlet) continue;
						const float capacity = mass[cell] * info.heat;
						const float x = phase::Condense(vapour, mass[cell], temperature[cell], capacity);
						if (!(x >= state.portion)) continue;
						// The drop leaves no hotter than its liquid's boiling point (otherwise the simulation would not let it fall);
						// the heat it did not carry away stays in the cell.
						float leave = 0.0f;
						temperature[cell] = phase::KeepHeat(temperature[cell] + x * vapour.latent / capacity, capacity, x * info.heat,
							elements->elements[info.lowTransition].highTemp + TransitionMargin - 0.5f, &leave);
						mass[cell] -= x;
						const float share = info.lowOre < numElements && info.lowOreShare > 0.0f && info.lowOreShare < 1.0f ? info.lowOreShare : 0.0f;
						state.released.push_back(Released{ (int32_t)cell, info.lowTransition, share > 0.0f ? info.lowOre : NoElement, x, x * share, leave });
						portions++;
						wet += x;
						if (state.condensedBy.size() < numElements) state.condensedBy.resize(numElements, 0.0);
						state.condensedBy[own] += x;
						continue;
					}
				}
			}
		}

		// A portion x of the liquid in cell from becomes vapour in cell to: it holds vacuum, its own vapour or (only when
		// boiling) a foreign gas, which gets displaced. false: the vapour cannot go there.
		// Correction to the vapour pressure of a cell with a liquid mixture (service of the liquid mixture mod; absent without it).
		static const LiquidVapourFactorFn mixFactor = (LiquidVapourFactorFn)simhook_service("liquid-mix.vapour-factor");
		auto vaporize = [&](size_t from, size_t to, float x, bool boiling)
		{
			const uint16_t own = element[from];
			const ElementInfo& info = elements->elements[own];
			const phase::Vapour& vapour = phases->liquidVapour[own];
			const uint16_t gasElement = info.highTransition;
			const uint16_t theirs = element[to];
			const bool empty = theirs == sim->vacuumElementIdx || !(mass[to] > 0.0f);
			const bool same = !empty && theirs == gasElement;
			if (!empty && !same)
			{
				if (!boiling || !displace || !displace(sim, sim->simEvents, cells, (int32_t)to, theirs) || element[to] != sim->vacuumElementIdx) return false;
				pushed++;
			}
			const float liquidMass = mass[from], liquidTemperature = temperature[from];
			const bool all = liquidMass - x < 1e-4f;
			if (all) x = liquidMass;
			// The cell holds a liquid mixture (liquid mixture mod): the portion is split among its liquids, and the liquid plugin
			// releases the solid residue. The gas cell holds a single gas: if the mixture's liquids give different gases (for
			// water-based ones it is always Steam), they go into the cell as the main liquid's gas.
			static const LiquidEvaporateFn mixEvaporate = (LiquidEvaporateFn)simhook_service("liquid-mix.evaporate");
			LiquidVapourOut products[4];
			float taken = 0.0f;
			const int32_t mixed = mixEvaporate ? mixEvaporate(sim, (int32_t)from, x, liquidTemperature, 0xFFFF, products, &taken) : 0;
			if (mixed > 0 && taken < x) x = taken;
			float mixedSteam = 0.0f;
			for (int32_t i = 0; i < mixed; i++) mixedSteam += products[i].mass;
			const float share = mixed > 0 ? 0.0f : (info.highOre < numElements && info.highOreShare > 0.0f && info.highOreShare < 1.0f ? info.highOreShare : 0.0f);
			const float steam = mixed > 0 ? mixedSteam : x * (1.0f - share);
			if (share > 0.0f)
			{
				float& kept = state.residue[(uint32_t)from];
				kept += x * share;
				if (kept >= state.portion || all)
				{
					state.released.push_back(Released{ (int32_t)from, NoElement, info.highOre, kept, kept, liquidTemperature });
					state.residue.erase((uint32_t)from);
				}
			}
			if (same)
			{
				const float there = mass[to];
				temperature[to] = (there * temperature[to] + steam * liquidTemperature) / (there + steam);
				mass[to] = there + steam;
			}
			else
			{
				element[to] = gasElement;
				mass[to] = steam;
				temperature[to] = liquidTemperature;
				notify(to);
			}
			if (all)
			{
				element[from] = sim->vacuumElementIdx;
				mass[from] = 0.0f;
				temperature[from] = 0.0f;
				notify(from);
				whole++;
			}
			else
			{
				mass[from] = liquidMass - x;
				const float cooled = liquidTemperature - x * vapour.latent / ((liquidMass - x) * info.heat);
				temperature[from] = cooled > 1.0f ? cooled : 1.0f;
			}
			times++;
			if (boiling) boiled++;
			gone += x;
			if (state.evaporatedBy.size() < numElements) state.evaporatedBy.resize(numElements, 0.0);
			state.evaporatedBy[own] += x;
			return true;
		};
		// Whether a cell is open to vapour: gas or vacuum with no ban on gas; returns the pressure and temperature of the gas in it.
		auto openFor = [&](size_t cell, float* pressure, float* gasTemperature)
		{
			*pressure = 0.0f;
			*gasTemperature = 0.0f;
			if (cell >= count || (properties[cell] & 1) != 0) return false;
			const uint16_t theirs = element[cell];
			if (theirs >= numElements || theirs == sim->voidElementIdx) return false;
			if (theirs == sim->vacuumElementIdx) return true;
			if (elements->elements[theirs].state != SIM_GAS) return false;
			if (mass[cell] > 0.0f)
			{
				*gasTemperature = temperature[cell];
				*pressure = phases->scale.Pressure(mass[cell], mass[cell] / elements->elements[theirs].molar, temperature[cell]);
			}
			return true;
		};

		for (const SimActiveRegion* region = sim->activeRegions.first; region != sim->activeRegions.last; region++)
		{
			const int32_t minX = region->minX < 1 ? 1 : region->minX, maxX = region->maxX > width - 1 ? width - 1 : region->maxX;
			const int32_t minY = region->minY < 1 ? 1 : region->minY, maxY = region->maxY > rows - 1 ? rows - 1 : region->maxY;
			// Boiling, including inside the bulk of a liquid. Columns are walked top to bottom: the liquid is pressed by the gas of the open
			// cell above the column and the weight of the liquid above. Liquid whose vapour pressure is higher boils, and the vapour goes
			// into that open cell (bubbles rise through any liquid above them). Under a solid ceiling the vapour has
			// nowhere to go, and the liquid there does not boil.
			// Cells are walked by rows (faster for memory), and each column's state lives in its own record.
			struct Column
			{
				size_t outlet;
				double weight;
				float gasPressure, gasTemperature;
			};
			static std::vector<Column> columns; // simulation thread only, under the state lock
			columns.assign((size_t)(maxX > minX ? maxX - minX : 0), Column{ SIZE_MAX, 0, 0.0f, 0.0f });
			for (int32_t y = maxY - 1; y >= minY; y--)
			{
				size_t cell = (size_t)y * width + minX;
				for (Column& column : columns)
				{
					const size_t here = cell++;
					float pressureHere = 0.0f, temperatureHere = 0.0f;
					if (openFor(here, &pressureHere, &temperatureHere))
					{
						column.outlet = here;
						column.gasPressure = pressureHere;
						column.gasTemperature = temperatureHere;
						column.weight = 0;
						continue;
					}
					const uint16_t own = element[here];
					if (own >= numElements || elements->elements[own].state != SIM_LIQUID)
					{
						column.outlet = SIZE_MAX;
						continue;
					}
					if (column.outlet == SIZE_MAX) continue;
					const phase::Vapour& vapour = phases->liquidVapour[own];
					if (vapour.Valid() && mass[here] > 0.0f && temperature[here] > 0.0f)
					{
						const ElementInfo& info = elements->elements[own];
						// Vapour is already in the cell above the column only if that cell holds the same vapour; boiling will displace a foreign gas.
						const bool sameAbove = element[column.outlet] == info.highTransition && mass[column.outlet] > 0.0f;
						const float factor = mixFactor ? mixFactor(sim, (int32_t)here, temperature[here]) : 1.0f;
						const float amount = phase::Boil(vapour, mass[here], temperature[here], info.heat,
							column.gasPressure + (float)(column.weight * phase::WeightPressure), sameAbove ? mass[column.outlet] : 0.0f,
							sameAbove ? column.gasTemperature : 0.0f, info.lowTemp, state.boilRate, factor);
						if (amount > 1e-5f && vaporize(here, column.outlet, amount, true))
						{
							openFor(column.outlet, &column.gasPressure, &column.gasTemperature);
							if (element[here] != own) { column.outlet = SIZE_MAX; continue; }
						}
					}
					column.weight += mass[here];
				}
			}
			// Surface evaporation: a liquid that is not boiling gives vapour to a neighbouring cell with vacuum or its own
			// vapour while there is less of it there than the liquid's vapour pressure holds. Boiling to the side of and below the liquid
			// (no open cell above it) is computed here too.
			for (int32_t y = minY; y < maxY; y++)
			{
				for (size_t cell = (size_t)y * width + minX, last = (size_t)y * width + maxX; cell < last; cell++)
				{
					const uint16_t own = element[cell];
					if (own >= numElements || phases->kind[own] != 2) continue;
					const ElementInfo& info = elements->elements[own];
					const phase::Vapour& vapour = phases->liquidVapour[own];
					for (int side = 0; side < 4; side++)
					{
						if (element[cell] != own || !(mass[cell] > 0.0f) || !(temperature[cell] > 0.0f)) break;
						const size_t beside = (size_t)((ptrdiff_t)cell + sides[side]);
						float pressure = 0.0f, gasTemperature = 0.0f;
						if (!openFor(beside, &pressure, &gasTemperature)) continue;
						const bool same = element[beside] == info.highTransition && mass[beside] > 0.0f;
						const float factor = mixFactor ? mixFactor(sim, (int32_t)cell, temperature[cell]) : 1.0f;
						const phase::Surface surface = phase::Evaporate(vapour, mass[cell], temperature[cell], info.heat,
							same ? mass[beside] : 0.0f, gasTemperature, pressure, state.evaporationRate, factor);
						float amount = surface.evaporated;
						// The cell above the liquid (side 0) already got its share during the column walk when boiling.
						if (surface.boiling && side != 0)
						{
							const float boilingOff = phase::Boil(vapour, mass[cell], temperature[cell], info.heat, pressure,
								same ? mass[beside] : 0.0f, same ? gasTemperature : 0.0f, info.lowTemp, state.boilRate, factor);
							if (boilingOff > amount) amount = boilingOff;
						}
						if (!(amount > 1e-5f)) continue;
						vaporize(cell, beside, amount, surface.boiling);
					}
				}
			}
		}
		evaporations.fetch_add(times, std::memory_order_relaxed);
		boilings.fetch_add(boiled, std::memory_order_relaxed);
		evaporatedWhole.fetch_add(whole, std::memory_order_relaxed);
		displacedForSteam.fetch_add(pushed, std::memory_order_relaxed);
		evaporatedGrams.fetch_add((int64_t)(gone * 1000.0), std::memory_order_relaxed);
		condensations.fetch_add(portions, std::memory_order_relaxed);
		condensedGrams.fetch_add((int64_t)(wet * 1000.0), std::memory_order_relaxed);
		passes.fetch_add(1, std::memory_order_relaxed);
		passMicros.fetch_add(NowMicros() - start, std::memory_order_relaxed);
	}

	typedef bool (*SpawnFn)(void* events, SimData* sim, uint64_t cell, uint16_t element, float mass, float temperature,
		uint8_t diseaseIdx, int32_t diseaseCount, bool evenIfHidden);
	typedef void (*AddLiquidFn)(int32_t cell, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount, SimData* sim);

	// Releases what the pass collected by the simulation's own means: ore and frost as chunks, liquid as a falling drop, and if
	// a drop is not possible (temperature outside the liquid range, cell not revealed to the player), right into the cell.
	void Release(SimData* sim, const ElementTable* elements, const std::vector<Released>& released)
	{
		if (released.empty()) return;
		static const SpawnFn spawnLiquid = (SpawnFn)simhook_find("SimEvents::SpawnFallingLiquid");
		static const SpawnFn spawnOre = (SpawnFn)simhook_find("SimEvents::SpawnOre");
		static const AddLiquidFn addLiquid = (AddLiquidFn)simhook_find("AddLiquid");
		for (const Released& out : released)
		{
			float liquidMass = out.mass;
			if (out.ore != NoElement && out.oreMass > 0.0f && (out.liquid == NoElement || out.oreMass > OreMinMass) && spawnOre
				&& spawnOre(sim->simEvents, sim, (uint64_t)out.cell, out.ore, out.oreMass, out.temperature, 0xFF, 0, true))
			{
				liquidMass -= out.oreMass;
				oreGrams.fetch_add((int64_t)(out.oreMass * 1000.0f), std::memory_order_relaxed);
			}
			if (out.liquid == NoElement || !(liquidMass > 0.0f)) continue;
			if (out.liquid < elements->elements.size() && spawnOre)
			{
				// The cell is colder than the liquid's freezing point: the vapour comes out solid right away.
				const ElementInfo& liquid = elements->elements[out.liquid];
				if (out.temperature < liquid.lowTemp && liquid.lowTransition < elements->elements.size()
					&& elements->elements[liquid.lowTransition].state == SIM_SOLID
					&& spawnOre(sim->simEvents, sim, (uint64_t)out.cell, liquid.lowTransition, liquidMass, out.temperature, 0xFF, 0, true))
				{
					frostGrams.fetch_add((int64_t)(liquidMass * 1000.0f), std::memory_order_relaxed);
					continue;
				}
			}
			// Below the cell (through gas and vacuum) the same liquid already lies: the portion is added straight to it, without a drop.
			const SimCells* cells = sim->updatedCells;
			const int32_t landing = LandingCell(sim, elements, out.cell, out.liquid);
			if (addLiquid && landing != out.cell && (size_t)landing < cells->elementIdx.size() && cells->elementIdx.first[landing] == out.liquid)
			{
				addLiquid(landing, out.liquid, liquidMass, out.temperature, 0xFF, 0, sim);
				pooled.fetch_add(1, std::memory_order_relaxed);
				continue;
			}
			if (spawnLiquid && spawnLiquid(sim->simEvents, sim, (uint64_t)out.cell, out.liquid, liquidMass, out.temperature, 0xFF, 0, false))
			{
				drips.fetch_add(1, std::memory_order_relaxed);
			}
			else if (addLiquid)
			{
				addLiquid(landing, out.liquid, liquidMass, out.temperature, 0xFF, 0, sim);
				inPlace.fetch_add(1, std::memory_order_relaxed);
			}
			else lostGrams.fetch_add((int64_t)(liquidMass * 1000.0f), std::memory_order_relaxed);
		}
	}
}

// Element properties from the mod: the same record as the mixture layer plugin's (element_table.h).
SIM_DATA(Elements, "pressure.elements")
{
	if (size <= 0 || size % (int32_t)sizeof(ElementInfo) != 0) return -1;
	ElementTable* table = new ElementTable;
	const ElementInfo* first = (const ElementInfo*)data;
	table->elements.assign(first, first + size / sizeof(ElementInfo));
	// The old table is not freed: simulation threads may be reading it right now, and it changes once per game.
	publishedElements.store(table, std::memory_order_release);
	{
		std::lock_guard<std::mutex> lock(phaseGuard);
		RebuildPhases();
	}
	// The table arrives on every game load: unreleased evaporation residue belonged to the previous world.
	std::lock_guard<std::mutex> lock(state.guard);
	state.residue.clear();
	return 0;
}

// Phase rule settings from the mod (element_table.h): how to count pressure, evaporation and boiling rates,
// the smallest condensate portion, real substance data.
SIM_DATA(PhaseSettings, "pressure.phase-config")
{
	std::lock_guard<std::mutex> lock(phaseGuard);
	if (!phaseConfig.Read(data, size)) return -1;
	RebuildPhases();
	std::lock_guard<std::mutex> stateLock(state.guard);
	state.evaporationRate = phaseConfig.header.evaporationRate;
	state.boilRate = phaseConfig.header.boilRate;
	state.portion = phaseConfig.header.portion;
	return 0;
}

// Phase rules by substance: how much of each gas condensed and how much of each liquid evaporated since
// the start of the game (for the pressure mod's log). The buffer receives the record count and the records: element index, how much
// of it condensed as gas, how much of it evaporated as liquid, kg.
SIM_DATA(PhaseElements, "pressure.phase-elements")
{
	struct Entry { int32_t element; float condensed; float evaporated; };
	if (size < (int32_t)sizeof(int32_t)) return -1;
	std::lock_guard<std::mutex> lock(state.guard);
	int32_t* count = (int32_t*)data;
	Entry* out = (Entry*)((uint8_t*)data + sizeof(int32_t));
	const int32_t room = (size - (int32_t)sizeof(int32_t)) / (int32_t)sizeof(Entry);
	*count = 0;
	const size_t elements = state.condensedBy.size() > state.evaporatedBy.size() ? state.condensedBy.size() : state.evaporatedBy.size();
	for (size_t i = 0; i < elements && *count < room; i++)
	{
		const double condensed = i < state.condensedBy.size() ? state.condensedBy[i] : 0, evaporated = i < state.evaporatedBy.size() ? state.evaporatedBy[i] : 0;
		if (condensed > 0 || evaporated > 0) out[(*count)++] = Entry{ (int32_t)i, (float)condensed, (float)evaporated };
	}
	return 0;
}

// Called from the temperature threads for every cell every tick, hence no locks.
SIM_HOOK("pressure", bool, "DoStateTransition", StateTransition, (SimData* sim, void* events, int32_t cell, void* elementData))
{
	if (active.load(std::memory_order_relaxed) != 0)
	{
		const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
		const PhaseTables* phases = publishedPhases.load(std::memory_order_acquire);
		if (elements && phases)
		{
			const int blocked = BlockedTransition(sim, elements, phases, cell);
			if (blocked == 1) { blockedCondensations.fetch_add(1, std::memory_order_relaxed); return false; }
			if (blocked == 2) { blockedBoilings.fetch_add(1, std::memory_order_relaxed); return false; }
		}
	}
	return StateTransition_original(sim, events, cell, elementData);
}

// Throwing 5 kg of gas out of a liquid next to a hot neighbour: under the phase rules the liquid warms from the neighbour by ordinary
// heat exchange and boils from the surface.
SIM_HOOK("pressure", bool, "DoPartialHeatTransition", PartialHeatTransition, (SimData* sim, void* events, int32_t cell, int32_t neighbour))
{
	if (active.load(std::memory_order_relaxed) != 0)
	{
		const PhaseTables* phases = publishedPhases.load(std::memory_order_acquire);
		const SimCells* cells = sim->updatedCells;
		if (phases && cell >= 0 && (size_t)cell < cells->elementIdx.size())
		{
			const uint16_t element = cells->elementIdx.first[cell];
			if (element < phases->kind.size() && phases->kind[element] == 2)
			{
				// Counted only for liquid the simulation would do this to at all (5 kg and more).
				if ((size_t)cell < cells->mass.size() && cells->mass.first[cell] >= 5.0f) blockedFlashes.fetch_add(1, std::memory_order_relaxed);
				return false;
			}
		}
	}
	return PartialHeatTransition_original(sim, events, cell, neighbour);
}

// Own pass before the simulation tick: the simulation then works with the new cell state right away.
SIM_HOOK("pressure-cells", void, "SimBase::UpdateData", UpdateData, (void* self, SimData* sim))
{
	const int64_t tickStart = NowMicros();
	const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
	const PhaseTables* phases = publishedPhases.load(std::memory_order_acquire);
	if (elements && phases && phases->gasVapour.size() == elements->elements.size()
		&& cellsOn.load(std::memory_order_relaxed) != 0 && active.load(std::memory_order_relaxed) != 0)
	{
		static const DisplaceGasFn displace = (DisplaceGasFn)simhook_find("DisplaceGas");
		static const ChangeSubstanceFn changed = (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
		static std::vector<Released> released;
		released.clear();
		{
			std::lock_guard<std::mutex> lock(state.guard);
			state.released.clear();
			Pass(sim, elements, phases, displace, changed);
			released.swap(state.released);
		}
		Release(sim, elements, released);
	}
	UpdateData_original(self, sim);
	tickMicros.fetch_add(NowMicros() - tickStart, std::memory_order_relaxed);
	tickCount.fetch_add(1, std::memory_order_relaxed);
}

// ---------- Check without the game (hooktest) ----------

// One pass over the given cell structure. A foreign gas is "displaced" by clearing the cell; what was released stays
// in the list for "pressure.released-for-test".
namespace
{
	bool ClearForTest(SimData* sim, void*, SimCells* cells, int32_t cell, uint16_t)
	{
		cells->elementIdx.first[cell] = sim->vacuumElementIdx;
		cells->mass.first[cell] = 0.0f;
		cells->temperature.first[cell] = 0.0f;
		return true;
	}
}

SIM_DATA(TickForTest, "pressure.tick-for-test")
{
	if (size != (int32_t)sizeof(SimData)) return -1;
	const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
	const PhaseTables* phases = publishedPhases.load(std::memory_order_acquire);
	if (!elements || !phases) return -2;
	std::lock_guard<std::mutex> lock(state.guard);
	state.released.clear();
	Pass((SimData*)data, elements, phases, ClearForTest, nullptr);
	return 0;
}

// Whether the plugin would forbid the simulation a transition in this cell: the cell number is overwritten with 0, 1 (condensation) or 2 (boiling).
SIM_DATA(BlockedForTest, "pressure.blocked-for-test")
{
	struct Test { SimData* sim; int32_t cell; };
	if (size != (int32_t)sizeof(Test)) return -1;
	Test* test = (Test*)data;
	const ElementTable* elements = publishedElements.load(std::memory_order_acquire);
	const PhaseTables* phases = publishedPhases.load(std::memory_order_acquire);
	if (!elements || !phases) return -2;
	test->cell = BlockedTransition(test->sim, elements, phases, test->cell);
	return 0;
}

// What left the cells during the last pass: the record count and up to four Released records.
SIM_DATA(ReleasedForTest, "pressure.released-for-test")
{
	if (size != (int32_t)(sizeof(int32_t) * 2 + 4 * sizeof(Released))) return -1;
	std::lock_guard<std::mutex> lock(state.guard);
	int32_t* count = (int32_t*)data;
	Released* out = (Released*)((uint8_t*)data + sizeof(int32_t) * 2);
	*count = (int32_t)state.released.size();
	for (size_t i = 0; i < state.released.size() && i < 4; i++) out[i] = state.released[i];
	return 0;
}
