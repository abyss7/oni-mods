// Liquid mixture plugin (the OniLiquidMix mod). Model and rules: liquid_rules.h.
//
// The plugin receives from the mod a table of liquids with their mixing groups, moves
// liquids itself, keeps the composition of mixed cells and takes an inventory of the grid's liquids every SurveyEvery ticks.
// On the tick function it sits in one chain with the gas mixture layer and the pressure plugin (simhook.h).
//
// Measurement (hook set "liquid-measure", enabled by the mod via a marker file): how much time the simulation itself
// spends on liquid movement and how many liquid cells change per tick. This is the budget for our own movement computation,
// which will replace the game's.
//
// Shadow (hook set "liquid-shadow", also via a marker file): our own cell flow computation (liquid_move.h)
// is checked against the game's on every call. The game computes for real; our computation follows over a copy of the cell's
// neighbourhood taken before the call, and the result is compared with what the game produced. The shadow changes nothing in the simulation.
//
// Own movement (hook set "liquid-own"): our computation computes the flow of a liquid cell instead of the game's.
// Not enabled together with the shadow: the shadow would then have nothing to check against.
#include <windows.h>

#include <x86intrin.h>

#include <atomic>
#include <mutex>
#include <vector>

#include <math.h>
#include <stdarg.h>
#include <stdio.h>

#include "liquid_move.h"
#include "liquid_rules.h"
#include "liquid_service.h"
#include "sim_types.h"
#include "simhook_plugin.h"

SIM_PLUGIN_MODULE();
SIM_PLUGIN_BUILD("liquid-mix", 0x6a67f9d8, 0x125000);

// Liquid inventory once every SurveyEvery ticks (a simulation tick: 0.2 seconds of game time).
SIM_FLAG(surveyOn, "liquid-mix.survey");
const int64_t SurveyEvery = 150;

SIM_COUNTER(ticks, "liquid-mix.ticks");
SIM_COUNTER(surveys, "liquid-mix.surveys");
SIM_COUNTER(surveyMicros, "liquid-mix.survey-micros");         // time of all inventories, total
SIM_COUNTER(liquidCells, "liquid-mix.cells");                  // from here on: numbers of the last inventory
SIM_COUNTER(groupCells, "liquid-mix.group-cells");             // cells with liquids that have a mixing group
SIM_COUNTER(liquidKilograms, "liquid-mix.kilograms");
SIM_COUNTER(fronts, "liquid-mix.fronts");                      // pairs of neighbouring cells where the liquids will mix
SIM_COUNTER(foreignContacts, "liquid-mix.foreign-contacts");   // pairs of neighbouring cells with liquids that do not mix
SIM_COUNTER(inversions, "liquid-mix.inversions");              // pairs "upper denser than lower"

// Measurement of the game's liquid movement. Time inside functions is counted by the CPU cycle counter (two reads per
// call: about 15 ns, tens of thousands of calls per tick); the mod converts it to microseconds by the pair of numbers
// "CPU cycles and microseconds over all simulation ticks".
SIM_FLAG(measureOn, "liquid-mix.measure");
SIM_COUNTER(tickMicros, "liquid-mix.tick-micros");               // time of simulation ticks without the passes of our layers before and after
SIM_COUNTER(tickCycles, "liquid-mix.tick-cycles");               // the same in CPU cycles
SIM_COUNTER(measuredTicks, "liquid-mix.measured-ticks");
SIM_COUNTER(updateCalls, "liquid-mix.update-liquid-calls");      // UpdateLiquid: flow of one liquid cell
SIM_COUNTER(updateCycles, "liquid-mix.update-liquid-cycles");
SIM_COUNTER(displaceCalls, "liquid-mix.pressure-displacement-calls"); // DoLiquidPressureDisplacement: pushing out a foreign liquid
SIM_COUNTER(displaceCycles, "liquid-mix.pressure-displacement-cycles");
SIM_COUNTER(changedCells, "liquid-mix.changed-cells");           // cells where the liquid or its mass changed during a tick, total
SIM_COUNTER(appearedCells, "liquid-mix.appeared-cells");         // of them, cells where a liquid appeared, vanished or was replaced by another
SIM_COUNTER(compareMicros, "liquid-mix.compare-micros");         // time of the cell comparison itself (not included in the tick)

namespace
{
	// Table from the mod. The previous one is not freed: the simulation thread may be finishing reading it right now, and it changes
	// once per game.
	std::atomic<const liquid::Table*> publishedTable{nullptr};

	std::mutex surveyGuard;
	liquid::Survey lastSurvey;
	std::vector<double> groupTotals; // mass of each liquid from the mixing groups by element id, kg (with mixtures)

	int64_t NowMicros()
	{
		LARGE_INTEGER now, frequency;
		QueryPerformanceCounter(&now);
		QueryPerformanceFrequency(&frequency);
		return (int64_t)(now.QuadPart * 1000000 / frequency.QuadPart);
	}

	// Previous cell state for the measurement: which liquid cells change during a tick.
	std::vector<uint16_t> previousElements;
	std::vector<float> previousMasses;

	void CompareWithPrevious(const SimData* sim, const liquid::Table* table)
	{
		const int64_t start = NowMicros();
		const SimCells* cells = sim->updatedCells;
		size_t count = cells->elementIdx.size();
		if (cells->mass.size() < count) count = cells->mass.size();
		const uint16_t* elements = cells->elementIdx.first;
		const float* masses = cells->mass.first;
		if (previousElements.size() == count)
		{
			int64_t changed = 0, appeared = 0;
			for (size_t cell = 0; cell < count; cell++)
			{
				const bool was = table->Of(previousElements[cell]) != nullptr, now = table->Of(elements[cell]) != nullptr;
				if (!was && !now) continue;
				if (was != now || previousElements[cell] != elements[cell])
				{
					changed++;
					appeared++;
				}
				else if (previousMasses[cell] != masses[cell]) changed++;
			}
			changedCells.fetch_add(changed, std::memory_order_relaxed);
			appearedCells.fetch_add(appeared, std::memory_order_relaxed);
		}
		previousElements.assign(elements, elements + count);
		previousMasses.assign(masses, masses + count);
		compareMicros.fetch_add(NowMicros() - start, std::memory_order_relaxed);
	}

	void RunSurvey(const SimData* sim, const liquid::Table* table)
	{
		const int64_t start = NowMicros();
		const SimCells* cells = sim->updatedCells;
		size_t count = cells->elementIdx.size();
		if (cells->mass.size() < count) count = cells->mass.size();
		if (sim->width <= 0) return;
		std::lock_guard<std::mutex> lock(surveyGuard);
		lastSurvey.Run(*table, cells->elementIdx.first, cells->mass.first, count, (size_t)sim->width);
		liquidCells.store(lastSurvey.cells, std::memory_order_relaxed);
		groupCells.store(lastSurvey.groupCells, std::memory_order_relaxed);
		liquidKilograms.store((int64_t)lastSurvey.mass, std::memory_order_relaxed);
		fronts.store(lastSurvey.fronts, std::memory_order_relaxed);
		foreignContacts.store(lastSurvey.foreign, std::memory_order_relaxed);
		inversions.store(lastSurvey.inversions, std::memory_order_relaxed);
		surveys.fetch_add(1, std::memory_order_relaxed);
		surveyMicros.fetch_add(NowMicros() - start, std::memory_order_relaxed);
	}
}

// Liquid table from the mod (liquid_rules.h: TableHeader, then Liquid entries).
SIM_DATA(TableData, "liquid-mix.table")
{
	liquid::Table* table = new liquid::Table;
	if (!table->Read(data, size))
	{
		delete table;
		return -1;
	}
	publishedTable.store(table, std::memory_order_release);
	return 0;
}

// Pairs of the last inventory for the mod: the buffer holds the number of entries, then room for liquid::Pair entries; in reply the number
// is set to how many entries were filled (most frequent pairs first).
SIM_DATA(SurveyPairs, "liquid-mix.survey-pairs")
{
	if (size < (int32_t)sizeof(int32_t)) return -1;
	int32_t* count = (int32_t*)data;
	if (*count < 0 || (int64_t)sizeof(int32_t) + (int64_t)*count * (int64_t)sizeof(liquid::Pair) > size) return -1;
	liquid::Pair* out = (liquid::Pair*)((uint8_t*)data + sizeof(int32_t));
	std::lock_guard<std::mutex> lock(surveyGuard);
	int32_t filled = 0;
	for (; filled < *count && (size_t)filled < lastSurvey.pairs.size(); filled++) out[filled] = lastSurvey.pairs[filled];
	*count = filled;
	return 0;
}

// How much of each liquid from the mixing groups is in all cells (with mixtures enabled, by shares): the buffer holds the number
// of entries, then room for "element id (int32), mass (double)" entries; in reply the number is set to how many were filled.
SIM_DATA(GroupTotals, "liquid-mix.group-totals")
{
	struct Entry { int32_t element; int32_t pad; double kilograms; };
	if (size < 8) return -1;
	int32_t* count = (int32_t*)data;
	if (*count < 0 || 8 + (int64_t)*count * (int64_t)sizeof(Entry) > size) return -1;
	Entry* out = (Entry*)((uint8_t*)data + 8);
	std::lock_guard<std::mutex> lock(surveyGuard);
	int32_t filled = 0;
	for (size_t element = 0; element < groupTotals.size() && filled < *count; element++)
	{
		if (groupTotals[element] > 0.0) out[filled++] = { (int32_t)element, 0, groupTotals[element] };
	}
	*count = filled;
	return 0;
}

// For the game-free check (hooktest): the main liquid of a mixture and its density. Record: the current main liquid,
// the number of liquids in the cell (up to 8), their elements and masses; in reply the chosen one goes into the current main one, and the
// mixture density into the first mass.
SIM_DATA(MainData, "liquid-mix.main")
{
	struct Query { uint16_t current; uint16_t count; uint16_t elements[8]; float masses[8]; };
	if (size != (int32_t)sizeof(Query)) return -1;
	Query* query = (Query*)data;
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table || query->count > 8) return -2;
	const float density = table->Density(query->elements, query->masses, query->count);
	query->current = table->Main(query->elements, query->masses, query->count, query->current);
	query->masses[0] = density;
	return 0;
}

// Inventory right away, over the passed cell structure: for the game-free check.
SIM_DATA(SurveyForTest, "liquid-mix.survey-for-test")
{
	if (size != (int32_t)sizeof(SimData)) return -1;
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table) return -2;
	RunSurvey((const SimData*)data, table);
	return 0;
}

namespace { void MixBeforeTick(SimData* sim, const liquid::Table* table); void MixAfterTick(SimData* sim, const liquid::Table* table); void CountGroupLiquids(const SimData* sim, const liquid::Table* table); void PublishRecords(const SimData* sim, int64_t tick); void ApplyConversions(SimData* sim, const liquid::Table* table); }

namespace { void TickLock(bool take); }

SIM_HOOK("liquid-mix", void, "SimBase::UpdateData", UpdateData, (void* self, SimData* sim))
{
	TickLock(true);
	struct Release { ~Release() { TickLock(false); } } release;
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (table) MixBeforeTick(sim, table);
	if (table) ApplyConversions(sim, table);
	const bool measure = table && measureOn.load(std::memory_order_relaxed) != 0;
	if (measure)
	{
		const int64_t start = NowMicros();
		const uint64_t cycles = __rdtsc();
		UpdateData_original(self, sim);
		tickCycles.fetch_add((int64_t)(__rdtsc() - cycles), std::memory_order_relaxed);
		tickMicros.fetch_add(NowMicros() - start, std::memory_order_relaxed);
		measuredTicks.fetch_add(1, std::memory_order_relaxed);
		CompareWithPrevious(sim, table);
	}
	else UpdateData_original(self, sim);
	if (table) MixAfterTick(sim, table);
	const int64_t tick = ticks.fetch_add(1, std::memory_order_relaxed);
	if (table) PublishRecords(sim, tick);
	if (table && tick % SurveyEvery == 0) CountGroupLiquids(sim, table);
	if (!table || surveyOn.load(std::memory_order_relaxed) == 0 || tick % SurveyEvery != SurveyEvery / 2) return;
	RunSurvey(sim, table);
}

// ---------- Measurement of the game's liquid movement ----------

SIM_PLUGIN_BUILD("liquid-measure", 0x6a67f9d8, 0x125000);

SIM_HOOK("liquid-measure", void, "UpdateLiquid", UpdateLiquid, (SimData* sim, void* events, int32_t cell))
{
	const uint64_t start = __rdtsc();
	UpdateLiquid_original(sim, events, cell);
	updateCycles.fetch_add((int64_t)(__rdtsc() - start), std::memory_order_relaxed);
	updateCalls.fetch_add(1, std::memory_order_relaxed);
}

SIM_HOOK("liquid-measure", float, "DoLiquidPressureDisplacement", PressureDisplacement,
	(uint16_t element, int32_t cell, int32_t neighbour, int32_t beyond, SimData* sim))
{
	const uint64_t start = __rdtsc();
	const float result = PressureDisplacement_original(element, cell, neighbour, beyond, sim);
	displaceCycles.fetch_add((int64_t)(__rdtsc() - start), std::memory_order_relaxed);
	displaceCalls.fetch_add(1, std::memory_order_relaxed);
	return result;
}

// ---------- Shadow: checking our flow computation against the game's ----------
//
// On every UpdateLiquid call: before the call the cell's 5 x 5 neighbourhood is copied from the main state, along with its row
// of flows; the game computes; then our computation runs over the copy, and the copy is compared with what became of the game state.
// What our computation does not compute itself is taken from the game, from the records of nested hooks:
//   - a falling drop: whether the game agrees to accept it (depends on whether the cell is visible to the player);
//   - displacement of gas from the cell the liquid flows into: done by the game and the gas mixture layer; the record gives
//     whether it succeeded and what changed around, and the receiving cell is treated as cleared, as after displacement.
// The requests themselves are checked along the way: our computation must ask for the same transfers and drops with the same numbers.

SIM_PLUGIN_BUILD("liquid-shadow", 0x6a67f9d8, 0x125000);
SIM_FLAG(shadowOn, "liquid-mix.shadow");
SIM_COUNTER(shadowCalls, "liquid-mix.shadow-calls");
SIM_COUNTER(shadowExact, "liquid-mix.shadow-exact");               // matched to the bit
SIM_COUNTER(shadowClose, "liquid-mix.shadow-close");               // difference only in the last digits of numbers
SIM_COUNTER(shadowMismatch, "liquid-mix.shadow-mismatch");         // mismatch
SIM_COUNTER(shadowWithGas, "liquid-mix.shadow-with-gas");          // calls where the liquid displaced gas
SIM_COUNTER(shadowMismatchWithGas, "liquid-mix.shadow-mismatch-with-gas"); // mismatches among them
SIM_COUNTER(shadowRequests, "liquid-mix.shadow-request-mismatch"); // mismatches where the requests themselves differ (transfer, drop)
SIM_COUNTER(shadowTransfers, "liquid-mix.shadow-transfers");       // mass transfers in checked calls, total
SIM_COUNTER(shadowSpawns, "liquid-mix.shadow-spawns");             // falling drops
SIM_COUNTER(shadowMicros, "liquid-mix.shadow-micros");             // time of our computation and comparison, total

namespace
{
	const int ShadowRadius = 2, ShadowSide = 2 * ShadowRadius + 1, ShadowCells = ShadowSide * ShadowSide;

	struct CellCopy
	{
		uint16_t element;
		uint8_t diseaseIdx, infestation;
		float mass, temperature;
		int32_t diseaseCount;
		float growthError;
	};

	struct TransferRecord
	{
		int32_t nb;
		float mass;
		uint8_t diseaseIdx;
		int32_t diseaseCount;
		bool result, gas;
		uint32_t changed;             // which neighbourhood cells the call changed (on gas displacement)
		CellCopy after[ShadowCells];
	};

	struct SpawnRecord
	{
		int32_t at;
		uint16_t element;
		float mass, temperature;
		uint8_t diseaseIdx;
		int32_t diseaseCount;
		bool result;
	};

	// What the game did during one UpdateLiquid call. Calls come in a single simulation thread.
	struct ShadowCall
	{
		bool active = false;
		int cell = 0;
		TransferRecord transfers[4];
		int transferCount = 0, transfersMissed = 0;
		SpawnRecord spawns[4];
		int spawnCount = 0, spawnsMissed = 0;
	} gameCall;

	void Capture(const SimData* sim, int centre, CellCopy* out)
	{
		const SimCells* cells = sim->updatedCells;
		const int64_t count = (int64_t)cells->elementIdx.size();
		for (int dy = -ShadowRadius; dy <= ShadowRadius; dy++)
		{
			for (int dx = -ShadowRadius; dx <= ShadowRadius; dx++)
			{
				const int64_t cell = (int64_t)centre + (int64_t)dy * sim->width + dx;
				CellCopy& copy = out[(dy + ShadowRadius) * ShadowSide + dx + ShadowRadius];
				if (cell < 0 || cell >= count)
				{
					copy = {};
					continue;
				}
				copy.element = cells->elementIdx.first[cell];
				copy.mass = cells->mass.first[cell];
				copy.temperature = cells->temperature.first[cell];
				copy.diseaseIdx = cells->diseaseIdx.first[cell];
				copy.diseaseCount = cells->diseaseCount.first[cell];
				copy.infestation = cells->diseaseInfestationTickCount.first[cell];
				copy.growthError = cells->diseaseGrowthAccumulatedError.first[cell];
			}
		}
	}

	bool SameBits(float a, float b)
	{
		return memcmp(&a, &b, sizeof(a)) == 0;
	}

	bool Close(float a, float b)
	{
		const float larger = fabsf(a) > fabsf(b) ? fabsf(a) : fabsf(b);
		return fabsf(a - b) <= 1e-5f * larger + 1e-9f;
	}

	// 0: matched to the bit; 1: integer fields matched, numbers are close; 2: mismatch.
	int Compare(const CellCopy& ours, const CellCopy& game)
	{
		if (ours.element != game.element || ours.diseaseIdx != game.diseaseIdx || ours.diseaseCount != game.diseaseCount) return 2;
		if (SameBits(ours.mass, game.mass) && SameBits(ours.temperature, game.temperature)) return 0;
		return Close(ours.mass, game.mass) && Close(ours.temperature, game.temperature) ? 1 : 2;
	}

	// Grid for our computation: a copy of the neighbourhood instead of the real cells.
	struct ShadowGrid
	{
		const SimData* sim;
		int centre;
		CellCopy block[ShadowCells];
		float ownFlow[4], otherFlow[4];
		CellCopy spill;
		int outside = 0;
		// Requests of our computation and their check against the game's records.
		int transferIndex = 0, spawnIndex = 0, requestErrors = 0;
		bool usedGas = false;
		char requests[256];
		int requestsLength = 0;

		int Slot(int cell) const
		{
			const int dx = cell % sim->width - centre % sim->width, dy = cell / sim->width - centre / sim->width;
			return dx < -ShadowRadius || dx > ShadowRadius || dy < -ShadowRadius || dy > ShadowRadius ? -1 : (dy + ShadowRadius) * ShadowSide + dx + ShadowRadius;
		}
		CellCopy& At(int cell)
		{
			const int slot = Slot(cell);
			if (slot >= 0) return block[slot];
			outside++;
			spill = {};
			return spill;
		}
		void Note(const char* format, ...) __attribute__((format(printf, 2, 3)))
		{
			va_list args;
			va_start(args, format);
			const int written = vsnprintf(requests + requestsLength, sizeof(requests) - requestsLength, format, args);
			va_end(args);
			if (written > 0) requestsLength = requestsLength + written < (int)sizeof(requests) ? requestsLength + written : (int)sizeof(requests) - 1;
		}

		uint16_t& element(int cell) { return At(cell).element; }
		float& mass(int cell) { return At(cell).mass; }
		float& temperature(int cell) { return At(cell).temperature; }
		uint8_t& diseaseIdx(int cell) { return At(cell).diseaseIdx; }
		int32_t& diseaseCount(int cell) { return At(cell).diseaseCount; }
		uint8_t& infestation(int cell) { return At(cell).infestation; }
		float& growthError(int cell) { return At(cell).growthError; }
		uint8_t properties(int cell) const { return sim->updatedCells->properties.first[cell]; }
		float* flow(int cell) { return cell == centre ? ownFlow : otherFlow; }
		void changed(int) {}

		void transferring(int nb, float mass, uint8_t diseaseIdx, int32_t diseaseCount)
		{
			Note(" move(%+d %.9g germs %d)", nb - centre, mass, diseaseCount);
			if (transferIndex >= gameCall.transferCount)
			{
				requestErrors++;
				transferIndex++;
				return;
			}
			const TransferRecord& record = gameCall.transfers[transferIndex++];
			if (record.nb != nb || !SameBits(record.mass, mass) || record.diseaseIdx != diseaseIdx || record.diseaseCount != diseaseCount) requestErrors++;
		}
		void transferred(bool result)
		{
			Note(result ? "=ok" : "=refused");
			if (transferIndex >= 1 && transferIndex <= gameCall.transferCount && gameCall.transfers[transferIndex - 1].result != result) requestErrors++;
		}
		bool displaceGas(int cell, uint16_t)
		{
			usedGas = true;
			if (transferIndex < 1 || transferIndex > gameCall.transferCount) return false;
			const TransferRecord& record = gameCall.transfers[transferIndex - 1];
			if (!record.gas || record.nb != cell) return false;
			if (!record.result) return false;
			// Everything the displacement changed around is taken from the game; the cell itself is empty after displacement.
			const int target = Slot(cell);
			for (int slot = 0; slot < ShadowCells; slot++)
			{
				if (slot != target && (record.changed & (1u << slot)) != 0) block[slot] = record.after[slot];
			}
			liquid::ClearCell(*this, sim, cell);
			return true;
		}
		liquid::Mix* mix(int) { return nullptr; }
		bool mixed(int) const { return false; }
		void noteMixed(int) {}
		bool mixes(uint16_t, uint16_t) const { return false; }
		bool spawnFalling(int, int at, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount)
		{
			Note(" drop(%+d %.9g)", at - centre, mass);
			if (spawnIndex >= gameCall.spawnCount)
			{
				requestErrors++;
				spawnIndex++;
				return false;
			}
			const SpawnRecord& record = gameCall.spawns[spawnIndex++];
			if (record.at != at || record.element != element || !SameBits(record.mass, mass) || !SameBits(record.temperature, temperature)
				|| record.diseaseIdx != diseaseIdx || record.diseaseCount != diseaseCount) requestErrors++;
			Note(record.result ? "=ok" : "=refused");
			return record.result;
		}
	};

	// Mismatch examples for the log: no more than two per mismatch kind.
	const int SampleKinds = 8, SamplesPerKind = 2, SampleLength = 1500;
	std::mutex sampleGuard;
	char samples[SampleKinds * SamplesPerKind][SampleLength];
	int sampleCounts[SampleKinds];

	int Append(char* text, int length, const char* format, ...) __attribute__((format(printf, 3, 4)));
	int Append(char* text, int length, const char* format, ...)
	{
		va_list args;
		va_start(args, format);
		const int written = vsnprintf(text + length, SampleLength - length, format, args);
		va_end(args);
		return written > 0 ? (length + written < SampleLength ? length + written : SampleLength - 1) : length;
	}

	void AddSample(int kind, const SimData* sim, int cell, const CellCopy* before, const float* flowBefore, const ShadowGrid& grid, const CellCopy* game, const float* flowAfter)
	{
		std::lock_guard<std::mutex> lock(sampleGuard);
		if (sampleCounts[kind] >= SamplesPerKind) { sampleCounts[kind]++; return; }
		char* text = samples[kind * SamplesPerKind + sampleCounts[kind]++];
		const SimCells& snapshot = *sim->cells;
		const int width = sim->width;
		int length = Append(text, 0, "kind %d, cell %d (%d,%d) tick %u: snapshot e%u m=%.9g T=%.9g germs %d/%d;", kind, cell, cell % width - 1, cell / width - 1,
			(unsigned)sim->tickCount, (unsigned)snapshot.elementIdx.first[cell], snapshot.mass.first[cell], snapshot.temperature.first[cell],
			(int)snapshot.diseaseIdx.first[cell], snapshot.diseaseCount.first[cell]);
		static const struct { const char* name; int dx, dy; } around[] = { { "below", 0, -1 }, { "left", -1, 0 }, { "right", 1, 0 }, { "up", 0, 1 }, { "below-left", -1, -1 }, { "below-right", 1, -1 } };
		for (const auto& side : around)
		{
			const int nb = cell + side.dy * width + side.dx;
			const CellCopy& was = before[(side.dy + ShadowRadius) * ShadowSide + side.dx + ShadowRadius];
			length = Append(text, length, " %s: snapshot e%u m=%.9g closed %d, main e%u m=%.9g closed %d;", side.name, (unsigned)snapshot.elementIdx.first[nb], snapshot.mass.first[nb],
				(int)snapshot.properties.first[nb], (unsigned)was.element, was.mass, (int)sim->updatedCells->properties.first[nb]);
		}
		const CellCopy& own = before[ShadowCells / 2];
		length = Append(text, length, " main before e%u m=%.9g; game:", (unsigned)own.element, own.mass);
		for (int i = 0; i < gameCall.transferCount; i++)
		{
			const TransferRecord& record = gameCall.transfers[i];
			length = Append(text, length, " move(%+d %.9g germs %d)%s%s", record.nb - cell, record.mass, record.diseaseCount, record.result ? "=ok" : "=refused", record.gas ? " into gas" : "");
		}
		for (int i = 0; i < gameCall.spawnCount; i++)
		{
			length = Append(text, length, " drop(%+d %.9g)%s", gameCall.spawns[i].at - cell, gameCall.spawns[i].mass, gameCall.spawns[i].result ? "=ok" : "=refused");
		}
		length = Append(text, length, "; ours:%s; differences:", grid.requestsLength > 0 ? grid.requests : " nothing");
		for (int slot = 0; slot < ShadowCells; slot++)
		{
			if (Compare(grid.block[slot], game[slot]) == 0) continue;
			length = Append(text, length, " (%+d,%+d) ours e%u m=%.9g T=%.9g germs %d/%d, game e%u m=%.9g T=%.9g germs %d/%d;", slot % ShadowSide - ShadowRadius, slot / ShadowSide - ShadowRadius,
				(unsigned)grid.block[slot].element, grid.block[slot].mass, grid.block[slot].temperature, (int)grid.block[slot].diseaseIdx, grid.block[slot].diseaseCount,
				(unsigned)game[slot].element, game[slot].mass, game[slot].temperature, (int)game[slot].diseaseIdx, game[slot].diseaseCount);
		}
		if (flowAfter && memcmp(grid.ownFlow, flowAfter, sizeof(grid.ownFlow)) != 0)
		{
			length = Append(text, length, " flow before %.9g %.9g %.9g %.9g, ours %.9g %.9g %.9g %.9g, game %.9g %.9g %.9g %.9g;", flowBefore[0], flowBefore[1], flowBefore[2], flowBefore[3],
				grid.ownFlow[0], grid.ownFlow[1], grid.ownFlow[2], grid.ownFlow[3], flowAfter[0], flowAfter[1], flowAfter[2], flowAfter[3]);
		}
		if (grid.outside > 0) Append(text, length, " cells outside the copied block touched %d times;", grid.outside);
	}
}

// Mismatch examples as text, one line per example; the buffer is filled with as many as fit.
SIM_DATA(ShadowSamples, "liquid-mix.shadow-samples")
{
	if (size < 2) return -1;
	char* out = (char*)data;
	int length = 0;
	std::lock_guard<std::mutex> lock(sampleGuard);
	for (int kind = 0; kind < SampleKinds; kind++)
	{
		for (int i = 0; i < SamplesPerKind && i < sampleCounts[kind]; i++)
		{
			const int written = snprintf(out + length, (size_t)(size - length), "%s (kind %d seen %d times)\n", samples[kind * SamplesPerKind + i], kind, sampleCounts[kind]);
			if (written <= 0 || length + written >= size) { out[length] = 0; return 0; }
			length += written;
		}
	}
	out[length] = 0;
	return 0;
}

// Mass transfer between cells inside an UpdateLiquid call: a record of what the game did.
SIM_HOOK("liquid-shadow", bool, "UpdateNeighbourLiquidMass", ShadowTransfer,
	(SimData* sim, void* events, int32_t cell, int32_t element, int32_t nb, int32_t nbElement, float mass, uint8_t diseaseIdx, int32_t diseaseCount))
{
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!gameCall.active || !table) return ShadowTransfer_original(sim, events, cell, element, nb, nbElement, mass, diseaseIdx, diseaseCount);
	if (gameCall.transferCount >= 4)
	{
		gameCall.transfersMissed++;
		return ShadowTransfer_original(sim, events, cell, element, nb, nbElement, mass, diseaseIdx, diseaseCount);
	}
	TransferRecord& record = gameCall.transfers[gameCall.transferCount];
	record.nb = nb;
	record.mass = mass;
	record.diseaseIdx = diseaseIdx;
	record.diseaseCount = diseaseCount;
	record.changed = 0;
	const SimCells* cells = sim->updatedCells;
	record.gas = nb >= 0 && (size_t)nb < cells->elementIdx.size() && table->StateOf(cells->elementIdx.first[nb]) == liquid::Gas;
	CellCopy before[ShadowCells];
	if (record.gas) Capture(sim, gameCall.cell, before);
	record.result = ShadowTransfer_original(sim, events, cell, element, nb, nbElement, mass, diseaseIdx, diseaseCount);
	if (record.gas)
	{
		Capture(sim, gameCall.cell, record.after);
		for (int slot = 0; slot < ShadowCells; slot++)
		{
			if (memcmp(&before[slot], &record.after[slot], sizeof(CellCopy)) != 0) record.changed |= 1u << slot;
		}
	}
	gameCall.transferCount++;
	return record.result;
}

SIM_HOOK("liquid-shadow", bool, "SimEvents::SpawnFallingLiquid", ShadowSpawn,
	(void* events, SimData* sim, uint64_t cell, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount, bool force))
{
	const bool result = ShadowSpawn_original(events, sim, cell, element, mass, temperature, diseaseIdx, diseaseCount, force);
	if (!gameCall.active) return result;
	if (gameCall.spawnCount >= 4)
	{
		gameCall.spawnsMissed++;
		return result;
	}
	gameCall.spawns[gameCall.spawnCount++] = { (int32_t)cell, element, mass, temperature, diseaseIdx, diseaseCount, result };
	return result;
}

SIM_HOOK("liquid-shadow", void, "UpdateLiquid", ShadowUpdate, (SimData* sim, void* events, int32_t cell))
{
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	const SimCells* snapshot = sim->cells;
	const int64_t count = (int64_t)snapshot->elementIdx.size();
	// The game does not process cells at the very edge of the grid (there is a border there), but the neighbourhood must lie entirely inside the grid.
	if (!table || shadowOn.load(std::memory_order_relaxed) == 0 || sim->width < 8 || cell < 3 * sim->width || cell >= count - 3 * sim->width)
	{
		ShadowUpdate_original(sim, events, cell);
		return;
	}
	CellCopy before[ShadowCells];
	Capture(sim, cell, before);
	float (*flows)[4] = (float (*)[4])sim->flow;
	float flowBefore[4] = {};
	if (flows) memcpy(flowBefore, flows[cell], sizeof(flowBefore));
	gameCall.active = true;
	gameCall.cell = cell;
	gameCall.transferCount = gameCall.transfersMissed = gameCall.spawnCount = gameCall.spawnsMissed = 0;
	ShadowUpdate_original(sim, events, cell);
	gameCall.active = false;

	const int64_t start = NowMicros();
	ShadowGrid grid;
	grid.sim = sim;
	grid.centre = cell;
	memcpy(grid.block, before, sizeof(before));
	memcpy(grid.ownFlow, flowBefore, sizeof(flowBefore));
	memset(grid.otherFlow, 0, sizeof(grid.otherFlow));
	grid.requests[0] = 0;
	liquid::UpdateCell(grid, *table, sim, cell);
	CellCopy game[ShadowCells];
	Capture(sim, cell, game);
	int worst = 0;
	for (int slot = 0; slot < ShadowCells; slot++)
	{
		const int result = Compare(grid.block[slot], game[slot]);
		if (result > worst) worst = result;
	}
	const bool flowSame = !flows || memcmp(grid.ownFlow, flows[cell], sizeof(grid.ownFlow)) == 0;
	if (!flowSame && worst < 1) worst = Close(grid.ownFlow[0], flows[cell][0]) && Close(grid.ownFlow[1], flows[cell][1]) && Close(grid.ownFlow[2], flows[cell][2]) && Close(grid.ownFlow[3], flows[cell][3]) ? 1 : 2;
	const bool requestsDiffer = grid.requestErrors > 0 || grid.transferIndex != gameCall.transferCount || grid.spawnIndex != gameCall.spawnCount
		|| gameCall.transfersMissed > 0 || gameCall.spawnsMissed > 0 || grid.outside > 0;
	if (requestsDiffer) worst = 2;
	bool gas = grid.usedGas;
	for (int i = 0; i < gameCall.transferCount; i++) gas = gas || gameCall.transfers[i].gas;
	shadowCalls.fetch_add(1, std::memory_order_relaxed);
	shadowTransfers.fetch_add(gameCall.transferCount, std::memory_order_relaxed);
	shadowSpawns.fetch_add(gameCall.spawnCount, std::memory_order_relaxed);
	if (gas) shadowWithGas.fetch_add(1, std::memory_order_relaxed);
	if (worst == 0) shadowExact.fetch_add(1, std::memory_order_relaxed);
	else if (worst == 1) shadowClose.fetch_add(1, std::memory_order_relaxed);
	else
	{
		shadowMismatch.fetch_add(1, std::memory_order_relaxed);
		if (gas) shadowMismatchWithGas.fetch_add(1, std::memory_order_relaxed);
		if (requestsDiffer) shadowRequests.fetch_add(1, std::memory_order_relaxed);
	}
	if (worst > 0)
	{
		// Mismatch kind: 0-3 mismatch (same requests or not, with gas or without), 4-7 the same for close numbers.
		const int kind = (worst == 1 ? 4 : 0) + (requestsDiffer ? 2 : 0) + (gas ? 1 : 0);
		AddSample(kind, sim, cell, before, flowBefore, grid, game, flows ? flows[cell] : nullptr);
	}
	shadowMicros.fetch_add(NowMicros() - start, std::memory_order_relaxed);
}

// ---------- Own movement: our computation computes the cell flow ----------

SIM_PLUGIN_BUILD("liquid-own", 0x6a67f9d8, 0x125000);
SIM_FLAG(ownOn, "liquid-mix.own");
SIM_COUNTER(ownCalls, "liquid-mix.own-calls"); // how many cells were computed by our computation

// Mixtures of liquids of one group (liquid_move.h, Mix). Work only together with own movement.
SIM_FLAG(mixingOn, "liquid-mix.mixing");
SIM_COUNTER(mixedCells, "liquid-mix.mixed-cells");            // cells with a mixture at the end of the last tick
SIM_COUNTER(staleCleared, "liquid-mix.stale-cleared");        // compositions removed by the walk: the cell no longer holds that mixture
SIM_COUNTER(exchanges, "liquid-mix.exchanges");               // composition exchanges between neighbouring cells, total
SIM_COUNTER(mixingMicros, "liquid-mix.mixing-micros");        // time of mixing and counting mixtures, total
SIM_COUNTER(dropPieces, "liquid-mix.drop-pieces");            // drops from a mixture split by composition
SIM_COUNTER(dropLostGrams, "liquid-mix.drop-lost-grams");     // part of a drop the game accepted neither as its own liquid nor as the main one (must be 0)
SIM_COUNTER(pushesRefused, "liquid-mix.pushes-refused");      // the game was not allowed to push out a mixing liquid
SIM_COUNTER(layersKept, "liquid-mix.layers-kept");            // the game was not allowed to swap layers of mixing liquids
SIM_COUNTER(swapsFollowed, "liquid-mix.swaps-followed");      // cell swaps by the game that the composition followed
SIM_COUNTER(restoredCells, "liquid-mix.restored-cells");      // cells that got their composition from a save

namespace
{
	typedef bool (*DisplaceGasFn)(SimData* sim, void* events, SimCells* cells, int32_t cell, uint16_t element);
	typedef bool (*SpawnFallingFn)(void* events, SimData* sim, uint64_t cell, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount, bool force);
	typedef void (*ChangeSubstanceFn)(void* events, SimData* sim, uint64_t cell);

	bool MaybeMixed(int cell); // below, by the composition store
	void NoteMixed(int cell);

	// Grid for our computation in the game: the real cells. Gas displacement, falling drops and notifying the game of an element
	// change remain simulation functions (and of whoever hooked them: gas displacement is handled by the gas mixture layer).
	// The starts of the cell arrays are taken once per call: so the computation does not re-read them from the cell structure after
	// every write.
	struct LiveGrid
	{
		SimData* sim;
		void* events;
		SimCells* cells;
		float (*flows)[4];
		DisplaceGasFn displace;
		SpawnFallingFn spawn;
		ChangeSubstanceFn change;
		bool force;
		float noFlow[4];
		const liquid::Table* table;
		liquid::Mix* mixStore; // nullptr: mixtures disabled
		uint16_t* elements;
		float* masses;
		float* temperatures;
		uint8_t* diseases;
		int32_t* germs;
		uint8_t* infestations;
		float* growthErrors;
		const uint8_t* closed;

		LiveGrid(SimData* sim, void* events, float (*flows)[4], DisplaceGasFn displace, SpawnFallingFn spawn, ChangeSubstanceFn change, bool force,
			const liquid::Table* table, liquid::Mix* mixStore)
			: sim(sim), events(events), cells(sim->updatedCells), flows(flows), displace(displace), spawn(spawn), change(change), force(force), noFlow{},
			  table(table), mixStore(mixStore), elements(cells->elementIdx.first), masses(cells->mass.first), temperatures(cells->temperature.first), diseases(cells->diseaseIdx.first),
			  germs(cells->diseaseCount.first), infestations(cells->diseaseInfestationTickCount.first), growthErrors(cells->diseaseGrowthAccumulatedError.first),
			  closed(cells->properties.first) {}

		uint16_t& element(int cell) { return elements[cell]; }
		float& mass(int cell) { return masses[cell]; }
		float& temperature(int cell) { return temperatures[cell]; }
		uint8_t& diseaseIdx(int cell) { return diseases[cell]; }
		int32_t& diseaseCount(int cell) { return germs[cell]; }
		uint8_t& infestation(int cell) { return infestations[cell]; }
		float& growthError(int cell) { return growthErrors[cell]; }
		uint8_t properties(int cell) const { return closed[cell]; }
		float* flow(int cell) { return flows ? flows[cell] : noFlow; }
		bool displaceGas(int cell, uint16_t gas) { return displace(sim, events, cells, cell, gas); }
		// Cell composition; an entry left from a liquid no longer in the cell (foreign code removed it) is reset.
		liquid::Mix* mix(int cell)
		{
			if (!mixStore) return nullptr;
			liquid::Mix& mix = mixStore[cell];
			if (mix.count >= 2 && !liquid::Contains(mix, elements[cell])) mix.count = 0;
			return &mix;
		}
		bool mixes(uint16_t a, uint16_t b) const { return mixStore && a != b && table->Mixes(a, b); }
		bool mixed(int cell)
		{
			if (!mixStore || !MaybeMixed(cell)) return false;
			return mix(cell)->count >= 2;
		}
		void noteMixed(int cell) { NoteMixed(cell); }
		// A drop from a mixture cell is split by composition: the game knows only pure liquids. First the share of the main
		// liquid with all the germs: if the game did not accept it, the whole drop is not accepted, as with a pure liquid.
		// The other shares go as their own liquids, and if the game did not accept one (its temperature is outside the limits of that
		// liquid), as the main one.
		bool spawnFalling(int from, int at, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount)
		{
			const liquid::Mix* source = mix(from);
			if (!source || source->count < 2) return spawn(events, sim, (uint64_t)at, element, mass, temperature, diseaseIdx, diseaseCount, force);
			float mainShare = 0.0f;
			for (int i = 0; i < source->count; i++) if (source->element[i] == element) mainShare = source->share[i];
			if (!spawn(events, sim, (uint64_t)at, element, mass * mainShare, temperature, diseaseIdx, diseaseCount, force)) return false;
			for (int i = 0; i < source->count; i++)
			{
				if (source->element[i] == element) continue;
				const float piece = mass * source->share[i];
				dropPieces.store(dropPieces.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
				if (spawn(events, sim, (uint64_t)at, source->element[i], piece, temperature, liquid::NoDisease, 0, force)) continue;
				if (spawn(events, sim, (uint64_t)at, element, piece, temperature, liquid::NoDisease, 0, force)) continue;
				dropLostGrams.store(dropLostGrams.load(std::memory_order_relaxed) + (int64_t)(piece * 1000.0f), std::memory_order_relaxed);
			}
			return true;
		}
		// Without an event queue (game-free check) there is nowhere to report.
		void changed(int cell) { if (events) change(events, sim, (uint64_t)cell); }
		void transferring(int, float, uint8_t, int32_t) {}
		void transferred(bool) {}
	};

	// The "world is being edited in debug mode" flag in the simulation data: with it a drop appears even in an invisible cell.
	// Offset for the build the plugin is written for (right after the events pointer).
	bool DebugEditing(const SimData* sim)
	{
		return *((const uint8_t*)sim + 0xb8) != 0;
	}

	// The cell must lie in the grid together with its neighbours; the game itself checks the same (and terminates the process).
	bool Inside(const SimData* sim, int cell)
	{
		const int64_t count = (int64_t)sim->cells->elementIdx.size(), main = (int64_t)sim->updatedCells->elementIdx.size();
		return sim->width > 2 && cell > sim->width && cell + sim->width + 1 < count && cell + sim->width + 1 < main;
	}
}

namespace
{
	// Cell compositions, by simulation cell index. Reset when a new table arrives (game load) or
	// the grid size changes. The mod writes them to the save ("liquid-mix.save") and restores them after loading
	// ("liquid-mix.restore").
	std::vector<liquid::Mix> mixStore;
	std::vector<uint8_t> transitionPending; // mixture cells due to transition (see "Freezing and boiling")
	// One byte per 64 cells: whether there can be a mixture there. Hooks called for every cell look here
	// before looking at the cell composition: the 3 KB table stays in cache, the compositions (5 MB) do not. The byte is set when a mixture
	// appears in a cell, and is reset only on a full recount (once every SurveyEvery ticks).
	std::vector<uint8_t> blockMixed;
	// The same blocks for the mixing pass: something changed there, and neighbours may have something to exchange. The pass itself
	// drops a block in which there were no exchanges (liquid::Diffuse).
	std::vector<uint8_t> blockActive;
	void NoteMixed(int cell)
	{
		const size_t block = (size_t)cell >> 6;
		if (block < blockMixed.size()) blockMixed[block] = blockActive[block] = 1;
	}
	// Whether the cell holds a mixture (by composition; with a fast reject by the block byte). The cell index is checked here too.
	bool MaybeMixed(int cell)
	{
		const size_t block = (size_t)cell >> 6;
		return cell >= 0 && block < blockMixed.size() && blockMixed[block] && (size_t)cell < mixStore.size() && mixStore[cell].count >= 2;
	}
	std::atomic<int64_t> transitionsMarked{0}; // how many cells were marked during the tick: without marks the walk is not needed
	liquid::Mix* tickStore = nullptr;          // composition store for the current tick (nullptr: mixtures disabled)
	const liquid::Table* mixTable = nullptr;
	SimCells* mixCells = nullptr; // main cell state the compositions belong to

	// Compositions are changed only by the simulation thread; the game thread reads them for the save. The simulation thread holds the lock for the whole
	// tick (inTick), and outside the tick (game messages: liquid arriving, the mop) takes it for every call.
	std::mutex mixGuard;
	bool inTick = false;
	struct MixLock
	{
		std::unique_lock<std::mutex> lock;
		MixLock() { if (!inTick) lock = std::unique_lock<std::mutex>(mixGuard); }
	};

	// Compositions from a save wait for the next tick: by then the table and the grid are surely in place.
	struct SavedMixHeader { int32_t magic, version, width, height, count, bytes; };
	struct SavedMix { int32_t cell, count; int32_t element[liquid::MixSlots]; float share[liquid::MixSlots]; };
	// Version 1 of the format had four slots per cell; saves made with it are still read.
	struct SavedMixV1 { int32_t cell, count; int32_t element[4]; float share[4]; };
	const int32_t SavedMixVersion = 2;
	const int32_t SavedMixMagic = 0x58494d4c; // "LMIX"
	std::vector<SavedMix> pendingRestore;
	bool restorePending = false;

	int32_t lastWidth = 0;
	SimData* lastSim = nullptr; // simulation data structure from the last tick: for paths that are not given it

	liquid::Mix* MixStore(const SimData* sim, const liquid::Table* table)
	{
		lastWidth = sim->width;
		lastSim = (SimData*)sim;
		if (mixingOn.load(std::memory_order_relaxed) == 0 || ownOn.load(std::memory_order_relaxed) == 0) return nullptr;
		const size_t count = sim->updatedCells->elementIdx.size();
		if (mixTable != table || mixStore.size() != count || mixCells != sim->updatedCells)
		{
			mixStore.assign(count, liquid::Mix{});
			transitionPending.assign(count, 0);
			blockMixed.assign((count + 63) / 64, 0);
			blockActive.assign((count + 63) / 64, 0);
			mixTable = table;
			mixCells = sim->updatedCells;
		}
		return mixStore.data();
	}
}

namespace
{
	// Restores compositions from a save: a cell gets a composition only if its element is in it and it is a group liquid.
	void ApplyRestore(SimData* sim, const liquid::Table* table, liquid::Mix* store)
	{
		if (!restorePending) return;
		restorePending = false;
		const int32_t width = sim->width, gameWidth = width - 2;
		const SimCells* cells = sim->updatedCells;
		int64_t restored = 0;
		for (const SavedMix& saved : pendingRestore)
		{
			if (gameWidth <= 0 || saved.cell < 0 || saved.count < 2 || saved.count > liquid::MixSlots) continue;
			const size_t cell = (size_t)(saved.cell / gameWidth + 1) * (size_t)width + (size_t)(saved.cell % gameWidth + 1);
			if (cell >= mixStore.size()) continue;
			const uint16_t element = cells->elementIdx.first[cell];
			liquid::Mix mix = {};
			float sum = 0.0f;
			bool valid = true;
			for (int i = 0; i < saved.count; i++)
			{
				valid = valid && saved.element[i] >= 0 && table->Mixes((uint16_t)saved.element[i], element) && saved.share[i] > 0.0f;
				mix.element[i] = (uint16_t)saved.element[i];
				mix.share[i] = saved.share[i];
				sum += saved.share[i];
			}
			mix.count = (uint8_t)saved.count;
			if (!valid || !liquid::Contains(mix, element) || !(sum > 0.0f)) continue;
			for (int i = 0; i < saved.count; i++) mix.share[i] /= sum;
			store[cell] = mix;
			NoteMixed((int)cell);
			restored++;
		}
		pendingRestore.clear();
		restoredCells.store(restored, std::memory_order_relaxed);
	}
}

SIM_HOOK("liquid-own", void, "UpdateLiquid", OwnUpdate, (SimData* sim, void* events, int32_t cell))
{
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	static const DisplaceGasFn displace = (DisplaceGasFn)simhook_find("DisplaceGas");
	static const SpawnFallingFn spawn = (SpawnFallingFn)simhook_find("SimEvents::SpawnFallingLiquid");
	static const ChangeSubstanceFn change = (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
	if (!table || ownOn.load(std::memory_order_relaxed) == 0 || !displace || !spawn || !change || !Inside(sim, cell))
	{
		OwnUpdate_original(sim, events, cell);
		return;
	}
	LiveGrid grid(sim, events, (float (*)[4])sim->flow, displace, spawn, change, DebugEditing(sim), table, tickStore);
	liquid::UpdateCell(grid, *table, sim, cell);
	// No locked addition: calls come in a single simulation thread, and the mod reads the number only for the log.
	ownCalls.store(ownCalls.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
}

// For the game-free check (hooktest): flow of one cell by our computation over the passed cells. Gas is not
// displaced, drops are not accepted. Record: cell structure, cell index, room for its row of flows;
// in reply the cell index is set to the number of element change notifications.
namespace
{
	int testChanges;
	bool NoDisplace(SimData*, void*, SimCells*, int32_t, uint16_t) { return false; }
	bool NoSpawn(void*, SimData*, uint64_t, uint16_t, float, float, uint8_t, int32_t, bool) { return false; }
	void CountChange(void*, SimData*, uint64_t) { testChanges++; }
}

SIM_DATA(OwnForTest, "liquid-mix.own-for-test")
{
	struct Test { SimData* sim; int32_t cell; float flow[4]; };
	if (size != (int32_t)sizeof(Test)) return -1;
	Test* test = (Test*)data;
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table || !Inside(test->sim, test->cell)) return -2;
	testChanges = 0;
	// The event queue here is not real: element change notifications are only counted.
	LiveGrid grid(test->sim, &testChanges, nullptr, NoDisplace, NoSpawn, CountChange, false, table, nullptr);
	liquid::UpdateCell(grid, *table, test->sim, test->cell);
	memcpy(test->flow, grid.noFlow, sizeof(test->flow));
	test->cell = testChanges;
	return 0;
}

// ---------- Mixtures: mixing per tick and what the game does with cells itself ----------

SIM_PLUGIN_BUILD("liquid-mixing", 0x6a67f9d8, 0x125000);

namespace
{
	const float DiffusionRate = 0.05f; // share of the smaller of two masses that neighbouring cells exchange as composition per tick
	const int64_t FrontEvery = 10;     // how often (in ticks) to look for new boundaries of pure liquids of one group

	void RunTransitions(SimData* sim, const liquid::Table* table, LiveGrid& grid);
	void ReleaseOre(SimData* sim);

	// After the tick: transitions of marked cells, mixing of neighbouring cells and counting of mixture cells.
	void MixAfterTick(SimData* sim, const liquid::Table* table)
	{
		static const DisplaceGasFn displace = (DisplaceGasFn)simhook_find("DisplaceGas");
		static const SpawnFallingFn spawn = (SpawnFallingFn)simhook_find("SimEvents::SpawnFallingLiquid");
		static const ChangeSubstanceFn change = (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
		liquid::Mix* store = MixStore(sim, table);
		if (!store || !displace || !spawn || !change) return;
		const int64_t start = NowMicros();
		LiveGrid grid(sim, sim->simEvents, (float (*)[4])sim->flow, displace, spawn, change, DebugEditing(sim), table, store);
		RunTransitions(sim, table, grid);
		ReleaseOre(sim);
		// Once every FrontEvery ticks the active regions are scanned for new boundaries of two pure liquids of one group
		// (for example, Water and Brine poured next to each other): their blocks join the walk. Cost: one scan of the active regions per
		// FrontEvery ticks, the same on any map.
		if (ticks.load(std::memory_order_relaxed) % FrontEvery == FrontEvery / 2)
		{
			const int width = sim->width;
			const uint16_t* elements = grid.elements;
			const int count = (int)mixStore.size();
			for (const SimActiveRegion* region = sim->activeRegions.first; region != sim->activeRegions.last; region++)
			{
				for (int y = region->minY > 1 ? region->minY : 1; y < region->maxY && (y + 1) * width < count; y++)
				{
					for (int cell = y * width + region->minX; cell < y * width + region->maxX; cell++)
					{
						const uint16_t element = elements[cell];
						if (element >= table->groupOf.size() || table->groupOf[element] == liquid::NoGroup) continue;
						const uint16_t right = elements[cell + 1], up = elements[cell + width];
						if (right != element && table->Mixes(element, right)) { NoteMixed(cell); NoteMixed(cell + 1); }
						if (up != element && table->Mixes(element, up)) { NoteMixed(cell); NoteMixed(cell + width); }
					}
				}
			}
		}
		int64_t done = 0, mixed = 0;
		// Only blocks where something changed (blockActive). Mixture cells are counted in full by the full recount once every
		// SurveyEvery ticks (CountGroupLiquids).
		liquid::Diffuse(grid, *table, sim, DiffusionRate, blockActive.data(), blockActive.size(), &done, &mixed);

		exchanges.fetch_add(done, std::memory_order_relaxed);
		mixingMicros.fetch_add(NowMicros() - start, std::memory_order_relaxed);
	}

	bool MixingNow()
	{
		return mixingOn.load(std::memory_order_relaxed) != 0 && ownOn.load(std::memory_order_relaxed) != 0 && !mixStore.empty();
	}
}

// ---------- Pumps and consumers by composition ----------
//
// An "any liquid" consumer (a pump) picks one element every tick and takes it from the cells around it.
// The game takes the element of the first liquid cell among the reachable ones; with a mixture only the main liquid would be pumped that way,
// and with it (by mass) the impurities under its name. Here the element is picked at random weighted by mass across
// all reachable cells, with mixtures by composition: over many ticks the pump outputs packets of liquids in turn, in the
// proportion in which they lie around it. Taking from a mixture cell (do_remove) takes only the requested liquid and
// no more than the cell holds of it; the cell composition is recomputed.

SIM_COUNTER(pumpPicks, "liquid-mix.pump-picks");            // how many times the pump picked a liquid by composition
SIM_COUNTER(pumpPicksMinor, "liquid-mix.pump-picks-minor"); // of them, a liquid that is not the main one in any surrounding cell
SIM_COUNTER(takes, "liquid-mix.takes");                     // takes from mixture cells
SIM_COUNTER(takenGrams, "liquid-mix.taken-grams");

namespace
{
	uint32_t pickState = 0x9e3779b9u;

	bool PickLiquid(const SimData* sim, const liquid::Table* table, const int32_t* reachable, size_t count, uint16_t* chosen)
	{
		const SimCells* cells = sim->updatedCells;
		const size_t total = cells->elementIdx.size();
		uint16_t elements[32];
		double weights[32];
		bool main[32];
		int kinds = 0;
		auto add = [&](uint16_t element, double mass, bool isMain) {
			for (int i = 0; i < kinds; i++)
			{
				if (elements[i] == element) { weights[i] += mass; main[i] = main[i] || isMain; return; }
			}
			if (kinds < 32) { elements[kinds] = element; weights[kinds] = mass; main[kinds++] = isMain; }
		};
		for (size_t i = 0; i < count; i++)
		{
			const int32_t cell = reachable[i];
			if (cell < 0 || (size_t)cell >= total) continue;
			const uint16_t element = cells->elementIdx.first[cell];
			if (table->StateOf(element) != liquid::Fluid) continue;
			const float mass = cells->mass.first[cell];
			const liquid::Mix* mix = (size_t)cell < mixStore.size() ? &mixStore[cell] : nullptr;
			if (mix && mix->count >= 2 && liquid::Contains(*mix, element))
			{
				for (int k = 0; k < mix->count; k++) add(mix->element[k], (double)mix->share[k] * mass, mix->element[k] == element);
			}
			else add(element, mass, true);
		}
		double sum = 0.0;
		for (int i = 0; i < kinds; i++) sum += weights[i];
		if (!(sum > 0.0)) return false;
		pickState ^= pickState << 13; pickState ^= pickState >> 17; pickState ^= pickState << 5;
		double point = (pickState >> 8) * (1.0 / 16777216.0) * sum;
		int pick = kinds - 1;
		for (int i = 0; i < kinds; i++)
		{
			if (point < weights[i]) { pick = i; break; }
			point -= weights[i];
		}
		*chosen = elements[pick];
		pumpPicks.store(pumpPicks.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		if (!main[pick]) pumpPicksMinor.store(pumpPicksMinor.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		return true;
	}

	// Taking the requested liquid from a mixture cell. false: the cell is not a mixture with this liquid, let the game take it.
	bool TakeFromMix(SimData* sim, const liquid::Table* table, float* wanted, uint16_t element, SimConsumedMass* taken, int32_t cell)
	{
		static const DisplaceGasFn displace = (DisplaceGasFn)simhook_find("DisplaceGas");
		static const SpawnFallingFn spawn = (SpawnFallingFn)simhook_find("SimEvents::SpawnFallingLiquid");
		static const ChangeSubstanceFn change = (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
		if (!wanted || !taken || !(*wanted > 0.0f) || cell < 0 || (size_t)cell >= mixStore.size() || sim->updatedCells != mixCells) return false;
		LiveGrid grid(sim, sim->simEvents, (float (*)[4])sim->flow, displace, spawn, change, DebugEditing(sim), table, mixStore.data());
		liquid::Mix* mix = grid.mix(cell);
		if (mix->count < 2 || !liquid::Contains(*mix, element)) return false;
		liquid::Portion portion = liquid::CompositionOf(grid, cell);
		const float mass = grid.mass(cell);
		float masses[liquid::MixSlots];
		int index = -1;
		for (int i = 0; i < portion.count; i++)
		{
			masses[i] = portion.share[i] * mass;
			if (portion.element[i] == element) index = i;
		}
		const float amount = *wanted < masses[index] ? *wanted : masses[index];
		if (!(amount > 0.0f)) return true;
		// Temperature and germs of the portion, as in the game: the temperature mixes with what was already collected, germs leave as a share of the mass.
		const float temperature = grid.temperature(cell);
		const float before = taken->mass, combined = before + amount;
		float mixedTemperature = (temperature * amount + before * taken->temperature) / combined;
		const float high = taken->temperature < temperature ? temperature : taken->temperature, low = taken->temperature < temperature ? taken->temperature : temperature;
		if (before > 0.0f) mixedTemperature = mixedTemperature < low ? low : (mixedTemperature > high ? high : mixedTemperature);
		else mixedTemperature = temperature;
		taken->mass = combined;
		taken->temperature = mixedTemperature;
		const uint8_t kind = grid.diseaseIdx(cell);
		const int32_t count = grid.diseaseCount(cell);
		if (kind != liquid::NoDisease && count > 0 && (taken->diseaseIdx == 0xFF || taken->diseaseIdx == kind))
		{
			const int32_t leaving = (int32_t)((float)count * (amount / mass));
			if (leaving > 0)
			{
				taken->diseaseIdx = kind;
				taken->diseaseCount += leaving;
				liquid::ModifyDiseaseCount(grid, cell, -leaving);
			}
		}
		*wanted -= amount;
		masses[index] -= amount;
		const float left = mass - amount;
		grid.mass(cell) = left;
		takes.store(takes.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		takenGrams.store(takenGrams.load(std::memory_order_relaxed) + (int64_t)(amount * 1000.0f), std::memory_order_relaxed);
		if (!(left > 1.1754944e-38f))
		{
			liquid::ClearCell(grid, sim, cell);
			grid.changed(cell);
			return true;
		}
		for (int i = 0; i < portion.count; i++) portion.share[i] = masses[i] > 0.0f ? masses[i] / left : 0.0f;
		liquid::SetComposition(grid, *table, cell, portion);
		return true;
	}
}

SIM_HOOK("liquid-mixing", bool, "AnyInputCellHasState", MixPick,
	(SimData* sim, SimVector<int32_t>* reachable, uint16_t* element, int32_t state, int32_t offset))
{
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (state == liquid::Fluid && table && MixingNow() && reachable && element && sim->updatedCells == mixCells)
	{
		MixLock lock;
		if (PickLiquid(sim, table, reachable->first, reachable->size(), element)) return true;
	}
	return MixPick_original(sim, reachable, element, state, offset);
}

SIM_HOOK("liquid-mixing", void, "do_remove", MixTake, (SimData* sim, float* wanted, uint16_t element, SimConsumedMass* taken, int32_t cell))
{
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	{
		MixLock lock;
		if (table && MixingNow() && TakeFromMix(sim, table, wanted, element, taken, cell)) return;
	}
	MixTake_original(sim, wanted, element, taken, cell);
}

// Cells swapped by the game (falling, layering, displacement): the composition travels with the cell.
SIM_HOOK("liquid-mixing", void, "CellSOA::SwapCells", MixSwap, (SimCells* cells, int32_t first, int32_t second))
{
	if (MixingNow() && cells == mixCells && first >= 0 && second >= 0 && (size_t)first < mixStore.size() && (size_t)second < mixStore.size())
	{
		MixLock lock;
		std::swap(mixStore[first], mixStore[second]);
		if (mixStore[first].count >= 2) NoteMixed(first);
		if (mixStore[second].count >= 2) NoteMixed(second);
		swapsFollowed.store(swapsFollowed.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
	}
	MixSwap_original(cells, first, second);
}

// The game pushes a neighbouring liquid of another element into the cell beyond it. A liquid of the same group is not pushed out:
// it mixes in instead.
SIM_HOOK("liquid-mixing", float, "DoLiquidPressureDisplacement", MixPush, (uint16_t element, int32_t cell, int32_t neighbour, int32_t beyond, SimData* sim))
{
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (table && MixingNow() && neighbour >= 0 && (size_t)neighbour < sim->updatedCells->elementIdx.size())
	{
		const uint16_t other = sim->updatedCells->elementIdx.first[neighbour];
		if (other != element && table->Mixes(element, other))
		{
			pushesRefused.store(pushesRefused.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
			return 0.0f;
		}
	}
	return MixPush_original(element, cell, neighbour, beyond, sim);
}

// Order of liquid layers (DoDensityDisplacement from PostProcessCell). With a probability of about 0.7 per tick the game swaps
// a cell with the lower one if the lower one holds another liquid with a smaller molar mass: that is how Crude Oil (molar mass 500)
// sinks in Water, and Chlorine lies above liquid Carbon Dioxide. Here density is compared instead of molar mass
// (Table: the real density of the element or the mass of a full cell, for a mixture by composition; the author's decision). Layers of the same
// group are not swapped: they mix. The random number is consumed the same way as in the game, and only when below the cell there is
// another liquid. One and the same liquid (the game equalizes temperature there) is left to the game.
SIM_COUNTER(layersSwapped, "liquid-mix.layers-swapped"); // how many times the denser liquid went down
// Layers by density and the water anomaly (a mod setting). Works without mixtures too; without it the game stacks layers by molar mass.
SIM_FLAG(densityOn, "liquid-mix.density");
// So that liquids of almost equal density do not swap places back and forth.
const float DensityMargin = 1.001f;

// Water density anomaly. Water is densest at about 4 °C (277.13 K), and lighter when colder or warmer. The game mixes
// cells of one liquid by temperature: if the lower one is hotter, both get a common temperature (its convection). For water
// this is right only above 4 °C; below that cold water is lighter and stays on top, so a pond freezes from the surface, and
// about 4 °C holds at the bottom. Here, for water-group liquids, convection goes by density with temperature taken into account:
// density = density by composition * (1 - 6.8e-6 * (T - Tmax)^2), where Tmax is shifted by the dissolved matter (salt, alcohol):
// Tmax = 277.13 - 216 * dissolved share (0.216 degrees per per mille, as for seawater; for seawater Tmax is already
// below the freezing point, and there is no anomaly). The dissolved share of each liquid comes from the mod: 0 for Water, 0.07 for Salt
// Water, 0.3 for Brine, 1 for Ethanol; liquids without an entry have no anomaly, and the game handles their convection.
SIM_COUNTER(convectionsMixed, "liquid-mix.convections");        // how many times water-group cells equalized temperature by density
SIM_COUNTER(convectionsStable, "liquid-mix.convections-stable"); // how many times cold water stayed on top (the game would have mixed it)

namespace
{
	std::atomic<const std::vector<float>*> publishedSolute{nullptr}; // dissolved share by element id, < 0: no anomaly

	// Density of a water-group cell with temperature taken into account; 0 if the cell has no anomaly (then the game decides).
	float WaterDensity(LiveGrid& grid, const liquid::Table* table, const std::vector<float>& solute, int cell)
	{
		const uint16_t element = grid.element(cell);
		float dissolved = 0.0f, density = 0.0f;
		if (grid.mixed(cell))
		{
			const liquid::Mix* mix = grid.mix(cell);
			for (int i = 0; i < mix->count; i++)
			{
				const float own = mix->element[i] < solute.size() ? solute[mix->element[i]] : -1.0f;
				dissolved += mix->share[i] * (own >= 0.0f ? own : 1.0f);
			}
			density = table->Density(mix->element, mix->share, mix->count);
		}
		else
		{
			if (element >= solute.size() || solute[element] < 0.0f) return 0.0f;
			dissolved = solute[element];
			const liquid::Liquid* liquid = table->Of(element);
			density = liquid ? liquid->density : 0.0f;
		}
		const float densest = 277.13f - 216.0f * dissolved;
		const float off = grid.temperature(cell) - densest;
		return density * (1.0f - 6.8e-6f * off * off);
	}
}

// Dissolved shares for the water density anomaly: "element id (int32), share (float)" entries.
SIM_DATA(WaterAnomaly, "liquid-mix.water-anomaly")
{
	struct Entry { int32_t element; float solute; };
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table || size < 0 || size % (int32_t)sizeof(Entry) != 0) return -1;
	std::vector<float>* solute = new std::vector<float>(table->byElement.size(), -1.0f);
	const Entry* entries = (const Entry*)data;
	for (int32_t i = 0; i < size / (int32_t)sizeof(Entry); i++)
	{
		if (entries[i].element >= 0 && (size_t)entries[i].element < solute->size()) (*solute)[entries[i].element] = entries[i].solute;
	}
	// The previous table is not freed: the simulation thread may still be reading it (it changes once per game).
	publishedSolute.store(solute, std::memory_order_release);
	return 0;
}

// Liquids whose upward transition is not boiling into their own vapour but a conversion (Brackene into Brine, Phyto Oil and Biodiesel into
// Carbon Dioxide): one "element id (int32)" entry for each. For a mixture this plugin always handles such transitions, with
// the pressure mod too: the phase rules compute only boiling into the liquid's own vapour.
namespace
{
	std::atomic<const std::vector<uint8_t>*> publishedDecomposing{nullptr};
}

SIM_DATA(Decomposing, "liquid-mix.decomposing")
{
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table || size < 0 || size % (int32_t)sizeof(int32_t) != 0) return -1;
	std::vector<uint8_t>* marks = new std::vector<uint8_t>(table->byElement.size(), 0);
	const int32_t* entries = (const int32_t*)data;
	for (int32_t i = 0; i < size / (int32_t)sizeof(int32_t); i++)
	{
		if (entries[i] >= 0 && (size_t)entries[i] < marks->size()) (*marks)[entries[i]] = 1;
	}
	// The previous table is not freed: the simulation thread may still be reading it (it changes once per game).
	publishedDecomposing.store(marks, std::memory_order_release);
	return 0;
}

namespace
{
	typedef void (*SwapCellsFn)(SimCells* cells, int32_t first, int32_t second);

	float CellDensity(LiveGrid& grid, const liquid::Table* table, int cell)
	{
		const uint16_t element = grid.element(cell);
		if (grid.mixed(cell))
		{
			const liquid::Mix* mix = grid.mix(cell);
			return table->Density(mix->element, mix->share, mix->count);
		}
		const liquid::Liquid* liquid = table->Of(element);
		return liquid ? liquid->density : 0.0f;
	}
}

SIM_HOOK("liquid-mixing", bool, "DoDensityDisplacement", MixLayers, (SimData* sim, void* events, int32_t cell, const void* element, float chance))
{
	// Called for almost every cell every tick, for gases too: a gas goes straight to the game.
	if (!element || (((const SimElementPostProcess*)element)->state & 3) != liquid::Fluid) return MixLayers_original(sim, events, cell, element, chance);
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	const int64_t below = (int64_t)cell - sim->width;
	SimCells* cells = sim->updatedCells;
	const bool mixing = MixingNow() && cells == mixCells, density = densityOn.load(std::memory_order_relaxed) != 0;
	if (!table || (!mixing && !density) || below < 0 || (size_t)cell >= cells->elementIdx.size()) return MixLayers_original(sim, events, cell, element, chance);
	const uint16_t here = cells->elementIdx.first[cell], under = cells->elementIdx.first[below];
	if (table->StateOf(under) != liquid::Fluid) return MixLayers_original(sim, events, cell, element, chance);
	liquid::Mix* store = mixing ? mixStore.data() : nullptr;
	if (here == under)
	{
		if (!density) return MixLayers_original(sim, events, cell, element, chance);
		// One and the same liquid: convection. For water-group liquids by density with the anomaly, for the rest as in the game.
		const std::vector<float>* solute = publishedSolute.load(std::memory_order_acquire);
		if (!solute || here >= solute->size() || (*solute)[here] < 0.0f) return MixLayers_original(sim, events, cell, element, chance);
		static const DisplaceGasFn displaceWater = (DisplaceGasFn)simhook_find("DisplaceGas");
		static const SpawnFallingFn spawnWater = (SpawnFallingFn)simhook_find("SimEvents::SpawnFallingLiquid");
		static const ChangeSubstanceFn changeWater = (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
		if (!displaceWater || !spawnWater || !changeWater) return MixLayers_original(sim, events, cell, element, chance);
		const uint32_t seed = sim->randomSeed * 0x343fdu + 0x269ec3u;
		sim->randomSeed = seed;
		if ((float)((seed >> 16) & 0x7fff) * 3.0518509e-05f <= chance) return false;
		LiveGrid grid(sim, events, (float (*)[4])sim->flow, displaceWater, spawnWater, changeWater, DebugEditing(sim), table, store);
		const float upper = WaterDensity(grid, table, *solute, cell), lower = WaterDensity(grid, table, *solute, (int)below);
		const float tu = cells->temperature.first[cell], tl = cells->temperature.first[below];
		if (!(upper > 0.0f) || !(lower > 0.0f) || !(upper > lower))
		{
			// The game would have mixed them: warmer below, but the water on top is colder than 4 degrees and therefore lighter.
			if (tl > tu) convectionsStable.store(convectionsStable.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
			return false;
		}
		// The upper one is denser: both cells get a common temperature, as in the game.
		const float mu = cells->mass.first[cell], ml = cells->mass.first[below];
		float combined = (mu * tu + ml * tl) / (mu + ml);
		const float high = tu > tl ? tu : tl, low = tu > tl ? tl : tu;
		combined = combined > high ? high : combined < low ? low : combined;
		cells->temperature.first[cell] = combined;
		cells->temperature.first[below] = combined;
		convectionsMixed.store(convectionsMixed.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		return true;
	}
	if (mixing && table->Mixes(here, under))
	{
		layersKept.store(layersKept.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		return false;
	}
	if (!density) return MixLayers_original(sim, events, cell, element, chance);
	static const SwapCellsFn swap = (SwapCellsFn)simhook_find("CellSOA::SwapCells");
	static const ChangeSubstanceFn change = (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
	if (!swap || !change) return MixLayers_original(sim, events, cell, element, chance);
	// The game's random number: the same generator and the same threshold as in its function.
	const uint32_t seed = sim->randomSeed * 0x343fdu + 0x269ec3u;
	sim->randomSeed = seed;
	if ((float)((seed >> 16) & 0x7fff) * 3.0518509e-05f <= chance) return false;
	static const DisplaceGasFn displace = (DisplaceGasFn)simhook_find("DisplaceGas");
	static const SpawnFallingFn spawn = (SpawnFallingFn)simhook_find("SimEvents::SpawnFallingLiquid");
	if (!displace || !spawn) return false;
	LiveGrid grid(sim, events, (float (*)[4])sim->flow, displace, spawn, change, DebugEditing(sim), table, store);
	if (!(CellDensity(grid, table, cell) > CellDensity(grid, table, (int)below) * DensityMargin)) return false;
	// Swap through the game function: the cell composition follows (the swap hook above).
	swap(cells, cell, (int32_t)below);
	change(events, sim, (uint64_t)cell);
	change(events, sim, (uint64_t)below);
	layersSwapped.store(layersSwapped.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
	return true;
}

SIM_COUNTER(addsMixed, "liquid-mix.adds-mixed"); // liquid came from the game (drop, pipe, building) into a cell of its group and mixed

// The game adds liquid to a cell (a drop landed, something poured out of a pipe or a building). Into a cell with a liquid
// of its group it mixes, rather than displacing the owner or "extinguishing" the smaller mass with the larger, as in the game.
SIM_HOOK("liquid-mixing", void, "AddLiquid", MixAdd,
	(int32_t cell, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount, SimData* sim))
{
	static const DisplaceGasFn displace = (DisplaceGasFn)simhook_find("DisplaceGas");
	static const SpawnFallingFn spawn = (SpawnFallingFn)simhook_find("SimEvents::SpawnFallingLiquid");
	static const ChangeSubstanceFn change = (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (table && MixingNow() && displace && spawn && change && cell >= 0 && (size_t)cell < mixStore.size() && mass > 0.0f
		&& sim->updatedCells == mixCells && sim->updatedCells->elementIdx.first[cell] != element && table->Mixes(sim->updatedCells->elementIdx.first[cell], element))
	{
		MixLock lock;
		LiveGrid grid(sim, sim->simEvents, (float (*)[4])sim->flow, displace, spawn, change, DebugEditing(sim), table, mixStore.data());
		const float before = grid.mass(cell);
		liquid::Portion portion;
		portion.count = 1;
		portion.element[0] = element;
		portion.share[0] = 1.0f;
		liquid::AddMass(grid, *table, cell, mass, temperature, diseaseIdx, diseaseCount);
		liquid::Blend(grid, *table, cell, before, mass, portion);
		addsMixed.store(addsMixed.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		return;
	}
	MixAdd_original(cell, element, mass, temperature, diseaseIdx, diseaseCount, sim);
}

namespace
{
	// Composition lock for the whole tick (see MixLock). Taken only when mixtures are enabled.
	void TickLock(bool take)
	{
		if (take)
		{
			if (mixingOn.load(std::memory_order_relaxed) == 0) return;
			mixGuard.lock();
			inTick = true;
		}
		else if (inTick)
		{
			inTick = false;
			mixGuard.unlock();
		}
	}
}

// Cell compositions for the save (format SavedMixHeader and SavedMix entries; game cells, elements as element ids,
// the mod converts them to hashes). The mod calls the request twice: with a buffer for the header, to learn the length (reply 3), and with
// a buffer of that length. 0: everything fit; 2: mixtures disabled.
SIM_DATA(MixSave, "liquid-mix.save")
{
	if (size < (int32_t)sizeof(SavedMixHeader)) return -1;
	if (mixingOn.load(std::memory_order_relaxed) == 0) return 2;
	std::lock_guard<std::mutex> lock(mixGuard);
	if (!mixCells || mixStore.empty()) return 2;
	const size_t total = mixStore.size();
	// The simulation grid width is taken from the last tick.
	const int32_t width = lastWidth, gameWidth = width - 2;
	if (gameWidth <= 0) return -2;
	SavedMixHeader header = { SavedMixMagic, SavedMixVersion, gameWidth, (int32_t)(total / (size_t)width) - 2, 0, 0 };
	size_t at = sizeof(header);
	uint8_t* out = (uint8_t*)data;
	const uint16_t* elements = mixCells->elementIdx.first;
	for (size_t cell = 0; cell < total; cell++)
	{
		const liquid::Mix& mix = mixStore[cell];
		if (mix.count < 2 || !liquid::Contains(mix, elements[cell])) continue;
		const int32_t x = (int32_t)(cell % (size_t)width) - 1, y = (int32_t)(cell / (size_t)width) - 1;
		if (x < 0 || y < 0 || x >= gameWidth) continue;
		SavedMix saved = {};
		saved.cell = y * gameWidth + x;
		saved.count = mix.count;
		for (int i = 0; i < mix.count; i++) { saved.element[i] = mix.element[i]; saved.share[i] = mix.share[i]; }
		if (at + sizeof(saved) <= (size_t)size) memcpy(out + at, &saved, sizeof(saved));
		at += sizeof(saved);
		header.count++;
	}
	header.bytes = (int32_t)at;
	memcpy(out, &header, sizeof(header));
	return at <= (size_t)size ? 0 : 3;
}

// Compositions from a save (same format, elements as element ids of this game; -1: no such element). An empty block:
// nothing saved. Restored on the next tick.
SIM_DATA(MixRestore, "liquid-mix.restore")
{
	std::lock_guard<std::mutex> lock(mixGuard);
	pendingRestore.clear();
	restorePending = true;
	if (size < (int32_t)sizeof(SavedMixHeader)) return 0;
	SavedMixHeader header;
	memcpy(&header, data, sizeof(header));
	const size_t entry = header.version == 1 ? sizeof(SavedMixV1) : sizeof(SavedMix);
	if (header.magic != SavedMixMagic || (header.version != 1 && header.version != SavedMixVersion) || header.count < 0 || header.bytes != size
		|| (int64_t)sizeof(header) + (int64_t)header.count * (int64_t)entry != size) return -1;
	pendingRestore.resize(header.count);
	if (header.version == 1)
	{
		for (int32_t i = 0; i < header.count; i++)
		{
			SavedMixV1 old;
			memcpy(&old, (const uint8_t*)data + sizeof(header) + i * sizeof(SavedMixV1), sizeof(old));
			SavedMix& saved = pendingRestore[i];
			saved = SavedMix{};
			saved.cell = old.cell;
			saved.count = old.count > 4 ? 4 : old.count;
			for (int k = 0; k < saved.count; k++) { saved.element[k] = old.element[k]; saved.share[k] = old.share[k]; }
		}
	}
	else if (header.count) memcpy(pendingRestore.data(), (const uint8_t*)data + sizeof(header), header.count * sizeof(SavedMix));
	return 0;
}

// ---------- Game-free check of mixtures (hooktest) ----------

// Cell composition. Record: cell structure, cell index, number of liquids, their elements and shares.
SIM_DATA(MixForTest, "liquid-mix.mix-for-test")
{
	struct Test { SimData* sim; int32_t cell, count; uint16_t element[liquid::MixSlots]; float share[liquid::MixSlots]; };
	if (size != (int32_t)sizeof(Test)) return -1;
	const Test* test = (const Test*)data;
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table) return -2;
	std::lock_guard<std::mutex> lock(mixGuard);
	liquid::Mix* store = MixStore(test->sim, table);
	if (!store || test->cell < 0 || (size_t)test->cell >= mixStore.size() || test->count > liquid::MixSlots) return -3;
	liquid::Mix& mix = store[test->cell];
	NoteMixed(test->cell);
	mix.count = (uint8_t)test->count;
	for (int i = 0; i < test->count; i++) { mix.element[i] = test->element[i]; mix.share[i] = test->share[i]; }
	return 0;
}

// Taking liquid from a cell, as a pump does. Record: cell structure, cell index, element, how much is needed, amount collected.
SIM_DATA(TakeForTest, "liquid-mix.take-for-test")
{
	struct Test { SimData* sim; int32_t cell; uint16_t element; uint16_t pad; float wanted; SimConsumedMass taken; int32_t result; };
	if (size != (int32_t)sizeof(Test)) return -1;
	Test* test = (Test*)data;
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table) return -2;
	std::lock_guard<std::mutex> lock(mixGuard);
	test->result = TakeFromMix(test->sim, table, &test->wanted, test->element, &test->taken, test->cell) ? 1 : 0;
	return 0;
}

// Liquid pick by a pump: rounds times over one list of cells; in reply, counts gets for each of the elements 0..7 how many times it was picked.
SIM_DATA(PickForTest, "liquid-mix.pick-for-test")
{
	struct Test { SimData* sim; int32_t count, rounds; int32_t cells[8]; int32_t counts[8]; };
	if (size != (int32_t)sizeof(Test)) return -1;
	Test* test = (Test*)data;
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table || test->count < 1 || test->count > 8) return -2;
	std::lock_guard<std::mutex> lock(mixGuard);
	for (int i = 0; i < 8; i++) test->counts[i] = 0;
	for (int round = 0; round < test->rounds; round++)
	{
		uint16_t chosen = 0;
		if (PickLiquid(test->sim, table, test->cells, (size_t)test->count, &chosen) && chosen < 8) test->counts[chosen]++;
	}
	return 0;
}

// Restoring compositions from a save without a tick: the compositions are erased and restored from the block passed earlier.
SIM_DATA(RestoreForTest, "liquid-mix.restore-for-test")
{
	if (size != (int32_t)sizeof(SimData*)) return -1;
	SimData* sim;
	memcpy(&sim, data, sizeof(sim));
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table) return -2;
	std::lock_guard<std::mutex> lock(mixGuard);
	liquid::Mix* store = MixStore(sim, table);
	if (!store) return -3;
	for (liquid::Mix& mix : mixStore) mix.count = 0;
	ApplyRestore(sim, table, store);
	return 0;
}

namespace
{
	// Once every SurveyEvery ticks: how much of each liquid from the mixing groups is in all cells (with mixtures, by shares).
	// Counted without mixtures too: this shows how these masses change in the game itself (pumps, buildings), and gives something to
	// compare a run with mixtures against.
	void CountGroupLiquids(const SimData* sim, const liquid::Table* table)
	{
		const SimCells* cells = sim->updatedCells;
		const size_t count = cells->elementIdx.size() < cells->mass.size() ? cells->elementIdx.size() : cells->mass.size();
		const bool mixed = mixStore.size() == count && mixCells == cells && mixingOn.load(std::memory_order_relaxed) != 0;
		std::vector<double> totals(table->byElement.size(), 0.0);
		// The block bytes are recomputed along the way: mixtures that no longer exist stop slowing down the checks.
		if (mixed) std::fill(blockMixed.begin(), blockMixed.end(), 0);
		int64_t mixtures = 0, stale = 0;
		for (size_t cell = 0; cell < count; cell++)
		{
			const uint16_t element = cells->elementIdx.first[cell];
			const liquid::Liquid* liquid = table->Of(element);
			// Composition of a cell that no longer holds this mixture (the liquid left by a game path that does not touch the composition): removed,
			// so that a liquid arriving here later does not take it over.
			if (mixed && mixStore[cell].count != 0 && (!liquid || liquid->group == liquid::NoGroup || !liquid::Contains(mixStore[cell], element)))
			{
				mixStore[cell].count = 0;
				stale++;
			}
			if (!liquid || liquid->group == liquid::NoGroup) continue;
			const liquid::Mix* m = mixed ? &mixStore[cell] : nullptr;
			// Boundary of two different liquids of one group: the blocks of both cells join the mixing walk, otherwise
			// two calm pure puddles side by side would never start mixing.
			if (mixed)
			{
				const size_t right = cell + 1, up = cell + (size_t)sim->width;
				if (right < count && cells->elementIdx.first[right] != element && table->Mixes(element, cells->elementIdx.first[right])) { NoteMixed((int)cell); NoteMixed((int)right); }
				if (up < count && cells->elementIdx.first[up] != element && table->Mixes(element, cells->elementIdx.first[up])) { NoteMixed((int)cell); NoteMixed((int)up); }
			}
			if (m && m->count >= 2 && liquid::Contains(*m, element))
			{
				// Only the checks flag: a block joins the mixing walk when something changes in it.
				blockMixed[cell >> 6] = 1;
				mixtures++;
				for (int i = 0; i < m->count; i++) totals[m->element[i]] += (double)m->share[i] * cells->mass.first[cell];
			}
			else totals[element] += cells->mass.first[cell];
		}
		if (mixed) mixedCells.store(mixtures, std::memory_order_relaxed);
		staleCleared.fetch_add(stale, std::memory_order_relaxed);
		std::lock_guard<std::mutex> lock(surveyGuard);
		groupTotals.swap(totals);
	}
}

// Composition of a game cell for the mod (self-check scenes, later the tooltip). Record: game cell index, number
// of liquids, their elements and shares; number 0: the cell is pure (or mixtures are disabled).
SIM_DATA(MixCell, "liquid-mix.cell")
{
	struct Query { int32_t cell, count; int32_t element[liquid::MixSlots]; float share[liquid::MixSlots]; };
	if (size != (int32_t)sizeof(Query)) return -1;
	Query* query = (Query*)data;
	query->count = 0;
	if (mixingOn.load(std::memory_order_relaxed) == 0) return 0;
	// Called from the game's main thread every frame (tooltip under the cursor): waiting for the end of the tick is not allowed. Busy: 1,
	// let the mod show the previous answer.
	std::unique_lock<std::mutex> lock(mixGuard, std::try_to_lock);
	if (!lock.owns_lock()) return 1;
	const int32_t width = lastWidth, gameWidth = width - 2;
	if (!mixCells || gameWidth <= 0 || query->cell < 0) return 0;
	const size_t cell = (size_t)(query->cell / gameWidth + 1) * (size_t)width + (size_t)(query->cell % gameWidth + 1);
	if (cell >= mixStore.size()) return 0;
	const liquid::Mix& mix = mixStore[cell];
	if (mix.count < 2 || !liquid::Contains(mix, mixCells->elementIdx.first[cell])) return 0;
	query->count = mix.count;
	for (int i = 0; i < mix.count; i++) { query->element[i] = mix.element[i]; query->share[i] = mix.share[i]; }
	return 0;
}

// Compositions of all mixture cells for game logic (sensors and other cell checks in the mod). The main thread does not wait for
// the tick: the snapshot is made at the end of the tick if the mod asked for it since last time (without a request it is not made at all), and
// handed out from here under its own lock. The snapshot work grows with the number of blocks with mixtures, not with the map size.
// Request: a RecordsHeader header (capacity: how many records fit; in reply count and the snapshot tick), followed by
// CellRecord records (game cells). 0: everything copied; 3: did not fit (count given); 2: no snapshot yet;
// 4: the snapshot is the same one the mod has.
namespace
{
	struct RecordsHeader { int64_t tick; int32_t capacity, count; };
	struct CellRecord { int32_t cell, count; int32_t element[liquid::MixSlots]; float share[liquid::MixSlots]; };
	std::mutex recordsGuard;
	std::vector<CellRecord> records;
	int64_t recordsTick = -1;
	std::atomic<int> recordsWanted{ 0 };

	void PublishRecords(const SimData* sim, int64_t tick)
	{
		if (recordsWanted.exchange(0, std::memory_order_relaxed) == 0) return;
		std::vector<CellRecord> fresh;
		const int32_t width = sim->width, gameWidth = width - 2;
		if (MixingNow() && mixCells == sim->updatedCells && gameWidth > 0)
		{
			const uint16_t* elements = mixCells->elementIdx.first;
			for (size_t block = 0; block < blockMixed.size(); block++)
			{
				if (!blockMixed[block]) continue;
				const size_t last = (block + 1) * 64 < mixStore.size() ? (block + 1) * 64 : mixStore.size();
				for (size_t cell = block * 64; cell < last; cell++)
				{
					const liquid::Mix& mix = mixStore[cell];
					if (mix.count < 2 || !liquid::Contains(mix, elements[cell])) continue;
					const int32_t x = (int32_t)(cell % (size_t)width), y = (int32_t)(cell / (size_t)width);
					if (x < 1 || x > gameWidth || y < 1) continue;
					CellRecord record = {};
					record.cell = (y - 1) * gameWidth + (x - 1);
					record.count = mix.count;
					for (int i = 0; i < mix.count; i++) { record.element[i] = mix.element[i]; record.share[i] = mix.share[i]; }
					fresh.push_back(record);
				}
			}
		}
		std::lock_guard<std::mutex> lock(recordsGuard);
		records.swap(fresh);
		recordsTick = tick;
	}
}

SIM_DATA(MixRecords, "liquid-mix.records")
{
	if (size < (int32_t)sizeof(RecordsHeader)) return -1;
	RecordsHeader* header = (RecordsHeader*)data;
	recordsWanted.store(1, std::memory_order_relaxed);
	std::lock_guard<std::mutex> lock(recordsGuard);
	if (recordsTick < 0) return 2;
	// The mod already holds this snapshot (in tick it passes the tick of its own): no need to copy.
	if (header->tick == recordsTick) return 4;
	const size_t count = records.size();
	const bool fits = header->capacity >= 0 && (size_t)header->capacity >= count && (size_t)size >= sizeof(RecordsHeader) + count * sizeof(CellRecord);
	header->count = (int32_t)count;
	header->tick = recordsTick;
	if (!fits) return 3;
	if (count > 0) memcpy((uint8_t*)data + sizeof(RecordsHeader), records.data(), count * sizeof(CellRecord));
	return 0;
}

namespace
{
	// Before the tick: compositions from a save (or set by a scene) are put in place before cells start moving; the composition
	// store is taken for the whole tick (the flow of each cell does not look it up).
	void MixBeforeTick(SimData* sim, const liquid::Table* table)
	{
		tickStore = MixStore(sim, table);
		if (tickStore) ApplyRestore(sim, table, tickStore);
	}
}

SIM_COUNTER(addsToMixture, "liquid-mix.adds-to-mixture"); // the game added mass of the main liquid to a mixture cell
SIM_COUNTER(freshFills, "liquid-mix.fresh-fills");        // liquid came into an empty cell with an old composition: composition removed

// Adding mass to a cell. It is called by game paths that add to a cell the same element that lies in it
// (a drop of the same liquid, a pipe, a source); the function does not know the element: it is the cell element. In a mixture cell
// the addition belongs only to the main liquid, not to all equally: the composition is recomputed.
SIM_HOOK("liquid-mixing", void, "AddMassAndUpdateTemperature", MixAddMass,
	(SimCells* read, SimCells* write, int32_t cell, float added, float temperature, uint8_t diseaseIdx, int32_t diseaseCount))
{
	if (!MaybeMixed(cell) || !(added > 0.0f) || write != mixCells)
	{
		MixAddMass_original(read, write, cell, added, temperature, diseaseIdx, diseaseCount);
		return;
	}
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table || !MixingNow())
	{
		MixAddMass_original(read, write, cell, added, temperature, diseaseIdx, diseaseCount);
		return;
	}
	static const DisplaceGasFn displace = (DisplaceGasFn)simhook_find("DisplaceGas");
	static const SpawnFallingFn spawn = (SpawnFallingFn)simhook_find("SimEvents::SpawnFallingLiquid");
	static const ChangeSubstanceFn change = (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
	MixLock lock;
	const float before = write->mass.first[cell];
	MixAddMass_original(read, write, cell, added, temperature, diseaseIdx, diseaseCount);
	if (!(before > 0.0f))
	{
		// The cell was empty (the game put a liquid into it and adds mass): it now holds one liquid, but
		// the composition is left over from a mixture that left here bypassing our code. Otherwise the arriving liquid would become a mixture.
		if ((size_t)cell < mixStore.size() && mixStore[cell].count != 0)
		{
			mixStore[cell].count = 0;
			freshFills.store(freshFills.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		}
		return;
	}
	if (!displace || !spawn || !change) return;
	// The simulation needs its data structure; it is not here, but the cells are enough for recomputing the composition (events about a
	// main liquid change go through the last known structure).
	if (!lastSim) return;
	LiveGrid grid(lastSim, lastSim->simEvents, (float (*)[4])lastSim->flow, displace, spawn, change, DebugEditing(lastSim), table, mixStore.data());
	if (grid.cells != write) return;
	liquid::Portion portion;
	portion.count = 1;
	portion.element[0] = grid.element(cell);
	portion.share[0] = 1.0f;
	liquid::Blend(grid, *table, cell, before, added, portion);
	addsToMixture.store(addsToMixture.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
}

// ---------- Game paths that move liquid bypassing our movement ----------
//
// The game itself moves liquid between cells in several more places: it displaces a cell's liquid to the neighbours
// when another element arrives in it (DisplaceLiquidSimple), pushes a neighbouring liquid into the cell beyond it
// (DisplaceLiquidDirectional), and squeezes up half of a cell overfilled by more than one and a half times
// (right in the body of PostProcessCell). It does all this as with a pure liquid: a mixture would leave as the main liquid,
// and the impurities would become it. Here these paths carry the composition. The threshold for a wall breaking under liquid pressure (DoPressureBreak)
// is computed from how much a mixture cell holds when full, not from the main liquid.

SIM_COUNTER(displacedMixed, "liquid-mix.displaced-mixed"); // cell displacements to neighbours computed with the composition
SIM_COUNTER(pushedMixed, "liquid-mix.pushed-mixed");       // pushes into the cell beyond a neighbour computed with the composition
SIM_COUNTER(pushUps, "liquid-mix.push-ups");               // squeezes up from a mixture cell: the composition moved
SIM_COUNTER(breaksScaled, "liquid-mix.breaks-scaled");     // wall break checks by mixture capacity

namespace
{
	template <class F> bool WithGrid(SimData* sim, const liquid::Table* table, F work)
	{
		static const DisplaceGasFn displace = (DisplaceGasFn)simhook_find("DisplaceGas");
		static const SpawnFallingFn spawn = (SpawnFallingFn)simhook_find("SimEvents::SpawnFallingLiquid");
		static const ChangeSubstanceFn change = (ChangeSubstanceFn)simhook_find("SimEvents::ChangeSubstance");
		if (!displace || !spawn || !change || sim->updatedCells != mixCells || mixStore.empty()) return false;
		LiveGrid grid(sim, sim->simEvents, (float (*)[4])sim->flow, displace, spawn, change, DebugEditing(sim), table, mixStore.data());
		return work(grid);
	}

	bool Mixed(LiveGrid& grid, int cell)
	{
		return grid.mix(cell)->count >= 2;
	}
}

SIM_HOOK("liquid-mixing", bool, "DisplaceLiquidSimple", MixDisplace,
	(SimData* sim, void* events, int32_t cell, uint16_t element, int32_t count, int32_t* candidates))
{
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table || !MixingNow() || !candidates || count <= 0 || count > 8) return MixDisplace_original(sim, events, cell, element, count, candidates);
	MixLock lock;
	int handled = -1; // -1: let the game do it; 0 or 1: our answer
	WithGrid(sim, table, [&](LiveGrid& grid) {
		// Our own displacement is needed if there is a mixture in the cell itself or in one of its neighbours, or a liquid of its group nearby.
		bool ours = Mixed(grid, cell);
		for (int i = 0; i < count && !ours; i++)
		{
			const uint16_t there = grid.element(candidates[i]);
			ours = (there == element && Mixed(grid, candidates[i])) || grid.mixes(element, there);
		}
		if (!ours) return false;
		handled = 0;
		if (!(0.01f <= grid.mass(cell))) return true;
		int list[8], k = 0;
		for (int i = 0; i < count; i++)
		{
			const uint16_t there = grid.element(candidates[i]);
			if ((there == element || grid.mixes(element, there) || there == sim->vacuumElementIdx) && (grid.properties(candidates[i]) & liquid::ClosedForLiquid) == 0)
				list[k++] = candidates[i];
		}
		if (k == 0) return true;
		const liquid::Portion portion = liquid::CompositionOf(grid, cell);
		const float mass = grid.mass(cell), temperature = grid.temperature(cell);
		const uint8_t germs = grid.diseaseIdx(cell);
		const int32_t germCount = grid.diseaseCount(cell);
		for (int i = 0; i < k; i++)
		{
			const int target = list[i];
			float before = grid.mass(target);
			if (grid.element(target) == sim->vacuumElementIdx)
			{
				grid.element(target) = element;
				grid.mix(target)->count = 0;
				before = 0.0f;
			}
			liquid::AddMass(grid, *table, target, mass / (float)k, temperature, germs, (germCount - 1 + k) / k);
			liquid::Blend(grid, *table, target, before, mass / (float)k, portion);
			grid.changed(target);
		}
		liquid::ClearCell(grid, sim, cell);
		grid.changed(cell);
		handled = 1;
		displacedMixed.store(displacedMixed.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		return true;
	});
	if (handled >= 0) return handled == 1;
	return MixDisplace_original(sim, events, cell, element, count, candidates);
}

SIM_HOOK("liquid-mixing", bool, "DisplaceLiquidDirectional", MixPushInto,
	(SimData* sim, void* events, int32_t from, int32_t to, uint16_t element))
{
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table || !MixingNow()) return MixPushInto_original(sim, events, from, to, element);
	MixLock lock;
	int handled = -1;
	WithGrid(sim, table, [&](LiveGrid& grid) {
		const uint16_t own = grid.element(from), there = grid.element(to);
		if (table->StateOf(there) != liquid::Fluid || !(there == own || grid.mixes(own, there))) return false;
		if (!Mixed(grid, from) && !Mixed(grid, to) && there == own) return false;
		handled = 0;
		if (!(grid.mass(from) > 0.0f) || (grid.properties(to) & liquid::ClosedForLiquid) != 0) return true;
		const liquid::Portion portion = liquid::CompositionOf(grid, from);
		const float before = grid.mass(to), mass = grid.mass(from);
		liquid::AddMass(grid, *table, to, mass, grid.temperature(from), grid.diseaseIdx(from), grid.diseaseCount(from));
		liquid::Blend(grid, *table, to, before, mass, portion);
		liquid::ClearCell(grid, sim, from);
		grid.changed(from);
		handled = 1;
		pushedMixed.store(pushedMixed.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		return true;
	});
	if (handled >= 0) return handled == 1;
	return MixPushInto_original(sim, events, from, to, element);
}

// Squeezing up: PostProcessCell clears the upper cell (DisplaceLiquid) and puts half of the
// overfilled cell into it as its main liquid. The half carries the cell composition: as soon as the upper cell
// is cleared, it is given a copy of the lower cell's composition in advance, and the game's write (the lower cell's element) agrees with it.
// The call from PostProcessCell is recognized by the return address, hence SIMHOOK_DIRECT. We do not hook the processing of every cell
// for this: it runs tens of thousands of times per tick, and squeezing up is rare.
SIM_HOOK_ORDERED("liquid-mixing", SIMHOOK_DIRECT, bool, "DisplaceLiquid", MixDisplaceFromCell,
	(SimData* sim, void* events, SimCells* cells, int32_t cell, uint16_t element))
{
	const bool result = MixDisplaceFromCell_original(sim, events, cells, cell, element);
	if (!result || !MixingNow() || sim->updatedCells != mixCells) return result;
	static const uintptr_t postProcess = (uintptr_t)simhook_find("PostProcessCell");
	const uintptr_t postProcessSize = 4034; // length of PostProcessCell in this SimDLL build (the library is bound to the build)
	const uintptr_t caller = (uintptr_t)__builtin_return_address(0);
	if (postProcess == 0 || caller < postProcess || caller >= postProcess + postProcessSize) return result;
	MixLock lock;
	const int64_t below = (int64_t)cell - sim->width;
	if (below < 0 || (size_t)cell >= mixStore.size()) return result;
	const liquid::Mix& source = mixStore[below];
	if (source.count >= 2 && liquid::Contains(source, sim->updatedCells->elementIdx.first[below]))
	{
		mixStore[cell] = source;
		NoteMixed(cell);
		pushUps.store(pushUps.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
	}
	return result;
}

SIM_HOOK("liquid-mixing", bool, "DoPressureBreak", MixBreak, (SimData* sim, void* events, int32_t cell, float ratio, int32_t step))
{
	if (!MaybeMixed(cell)) return MixBreak_original(sim, events, cell, ratio, step);
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (table && MixingNow() && sim->updatedCells == mixCells)
	{
		MixLock lock;
		float scaled = ratio;
		WithGrid(sim, table, [&](LiveGrid& grid) {
			const float capacity = liquid::Capacity(grid, *table, cell, 0.0f);
			if (capacity > 0.0f) scaled = grid.mass(cell) / capacity;
			return true;
		});
		breaksScaled.store(breaksScaled.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		return MixBreak_original(sim, events, cell, scaled, step);
	}
	return MixBreak_original(sim, events, cell, ratio, step);
}

// ---------- Freezing and boiling of a mixture by composition ----------
//
// The game transitions a cell as a whole when its temperature goes 3 degrees past the transition point of its element
// (DoStateTransition): a mixture would freeze or boil away entirely by the main liquid. Here each liquid of the mixture
// transitions by its own points: on cooling the one with the higher point freezes (for Water with Brine, the Water: Ice drops out
// in chunks, the Brine stays liquid), on heating the one with the lower point boils away (the vapour goes into a gas cell nearby).
// The last liquid of the cell transitions as in the game, as a whole cell. Boiling here happens only without the pressure mod: with it
// the phase rules compute boiling and evaporation by pressure.
//
// The check runs in the temperature threads, so there the cell is only marked, and the transitions are done after the tick in
// the simulation thread.

SIM_FLAG(boilByComposition, "liquid-mix.boil"); // the mod sets 1 if there is no pressure mod (boiling stays with the game and this code)
SIM_COUNTER(frozenOut, "liquid-mix.frozen-out");        // how many times a mixture liquid froze separately
SIM_COUNTER(frozenGrams, "liquid-mix.frozen-grams");
SIM_COUNTER(boiledOut, "liquid-mix.boiled-out");        // how many times a mixture liquid boiled away separately
SIM_COUNTER(boiledGrams, "liquid-mix.boiled-grams");
SIM_COUNTER(convertedOut, "liquid-mix.converted-out");  // a mixture liquid turned on heating into another liquid of its group
SIM_COUNTER(transitionsHeld, "liquid-mix.transitions-held"); // boiling that has nowhere to release vapour (the liquid waits)

namespace
{
	const float TransitionMargin = 3.0f; // as in the game: transition 3 degrees past the point

	// Whether some liquid of the mixture awaits a transition: 1 freezing, 2 boiling, 0 none.
	// The plugin handles the upward transition of this liquid: boiling if there is no pressure mod, and conversion always.
	bool OwnHighTransition(uint16_t element, bool boil)
	{
		if (boil) return true;
		const std::vector<uint8_t>* marks = publishedDecomposing.load(std::memory_order_acquire);
		return marks && element < marks->size() && (*marks)[element] != 0;
	}

	int PendingTransition(const liquid::Table* table, const liquid::Mix& mix, float temperature, bool boil)
	{
		for (int i = 0; i < mix.count; i++)
		{
			const liquid::Liquid& liquid = table->move[mix.element[i]];
			if (liquid.lowTransition != 0xFFFF && temperature < liquid.lowTemp - TransitionMargin) return 1;
			if (liquid.highTransition != 0xFFFF && temperature > liquid.highTemp + TransitionMargin && OwnHighTransition(mix.element[i], boil)) return 2;
		}
		return 0;
	}

	typedef bool (*SpawnOreFn)(void* events, SimData* sim, uint64_t cell, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount, bool force);
	typedef void (*AddGasFn)(int32_t cell, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t diseaseCount, SimData* sim);

	// Transitions of marked cells (simulation thread, after the tick).
	void RunTransitions(SimData* sim, const liquid::Table* table, LiveGrid& grid)
	{
		static const SpawnOreFn spawnOre = (SpawnOreFn)simhook_find("SimEvents::SpawnOre");
		static const AddGasFn addGas = (AddGasFn)simhook_find("AddGas");
		if (transitionPending.size() != mixStore.size() || !spawnOre || !addGas) return;
		if (transitionsMarked.exchange(0, std::memory_order_relaxed) == 0) return;
		const bool boil = boilByComposition.load(std::memory_order_relaxed) != 0;
		// Marks occur only in mixture cells, and those lie in marked blocks.
		for (size_t index = 0; index < transitionPending.size(); index++)
		{
			if ((index & 63) == 0 && (index >> 6) < blockMixed.size() && !blockMixed[index >> 6]) { index += 63; continue; }
			if (!transitionPending[index]) continue;
			transitionPending[index] = 0;
			const int cell = (int)index;
			liquid::Mix* mix = grid.mix(cell);
			if (mix->count < 2) continue;
			const float temperature = grid.temperature(cell);
			liquid::Portion portion = liquid::CompositionOf(grid, cell);
			float mass = grid.mass(cell);
			float masses[liquid::MixSlots];
			for (int i = 0; i < portion.count; i++) masses[i] = portion.share[i] * mass;
			bool changed = false;
			for (int i = 0; i < portion.count; i++)
			{
				// The last liquid share transitions as a whole cell by the game's means (on the next tick).
				int liquidLeft = 0;
				for (int k = 0; k < portion.count; k++) liquidLeft += masses[k] > 0.0f;
				if (liquidLeft <= 1) break;
				if (!(masses[i] > 0.0f)) continue;
				const liquid::Liquid& liquid = table->move[portion.element[i]];
				const bool freezes = liquid.lowTransition != 0xFFFF && temperature < liquid.lowTemp - TransitionMargin;
				const bool boils = liquid.highTransition != 0xFFFF && temperature > liquid.highTemp + TransitionMargin && OwnHighTransition(portion.element[i], boil);
				if (!freezes && !boils) continue;
				const uint16_t product = freezes ? liquid.lowTransition : liquid.highTransition;
				const uint16_t ore = freezes ? liquid.lowOre : liquid.highOre;
				const float oreShare = freezes ? liquid.lowOreShare : liquid.highOreShare;
				const float oreMass = ore != 0xFFFF && oreShare > 0.0f ? masses[i] * oreShare : 0.0f;
				const float productMass = masses[i] - oreMass;
				const uint8_t productState = table->StateOf(product);
				if (boils && productState == liquid::Gas)
				{
					// The vapour goes into a gas cell or vacuum above the cell, otherwise to the side; nowhere: the liquid waits.
					const int around[3] = { cell + sim->width, cell - 1, cell + 1 };
					int target = -1;
					for (int nb : around)
					{
						const uint8_t state = table->StateOf(grid.element(nb));
						if ((state == liquid::Gas || state == liquid::Vacuum) && (grid.properties(nb) & liquid::ClosedForGas) == 0) { target = nb; break; }
					}
					if (target < 0)
					{
						transitionsHeld.store(transitionsHeld.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
						continue;
					}
					addGas(target, product, productMass, temperature, 0xFF, 0, sim);
					boiledOut.store(boiledOut.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
					boiledGrams.store(boiledGrams.load(std::memory_order_relaxed) + (int64_t)(masses[i] * 1000.0f), std::memory_order_relaxed);
				}
				else if (productState == liquid::Fluid && grid.mixes(portion.element[i], product))
				{
					// The liquid freezes (or converts on heating) into another liquid of its group (Salt Water into
					// Brine with Ice, Brackene into Brine): that one stays in the mixture.
					int k = -1;
					for (int j = 0; j < portion.count; j++) if (portion.element[j] == product) k = j;
					if (k >= 0) { masses[k] += productMass; masses[i] = 0.0f; }
					else { portion.element[i] = product; masses[i] = productMass; }
					if (oreMass > 0.0f) spawnOre(sim->simEvents, sim, (uint64_t)cell, ore, oreMass, temperature, 0xFF, 0, true);
					if (boils) convertedOut.store(convertedOut.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
					else
					{
						frozenOut.store(frozenOut.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
						frozenGrams.store(frozenGrams.load(std::memory_order_relaxed) + (int64_t)(oreMass * 1000.0f), std::memory_order_relaxed);
					}
					changed = true;
					continue;
				}
				else if (productState == liquid::Solid && freezes)
				{
					spawnOre(sim->simEvents, sim, (uint64_t)cell, product, productMass, temperature, 0xFF, 0, true);
					frozenOut.store(frozenOut.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
					frozenGrams.store(frozenGrams.load(std::memory_order_relaxed) + (int64_t)(masses[i] * 1000.0f), std::memory_order_relaxed);
				}
				else
				{
					transitionsHeld.store(transitionsHeld.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
					continue;
				}
				if (oreMass > 0.0f) spawnOre(sim->simEvents, sim, (uint64_t)cell, ore, oreMass, temperature, 0xFF, 0, true);
				masses[i] = 0.0f;
				changed = true;
			}
			if (!changed) continue;
			float left = 0.0f;
			for (int i = 0; i < portion.count; i++) left += masses[i];
			if (!(left > 0.0f))
			{
				liquid::ClearCell(grid, sim, cell);
				grid.changed(cell);
				continue;
			}
			// Germs stay in the liquid, their share by mass is not recomputed.
			grid.mass(cell) = left;
			for (int i = 0; i < portion.count; i++) portion.share[i] = masses[i] / left;
			liquid::SetComposition(grid, *table, cell, portion);
		}
	}
}

// Cell transition check (temperature threads). A mixture cell in which some liquid is due to transition
// is marked, and does not transition as a whole in this tick.
SIM_COUNTER(flashesBlocked, "liquid-mix.flashes-blocked"); // checks of the 5 kg vapour release skipped for mixture cells

// The game releases 5 kg of its gas from a liquid next to a hot neighbour (DoPartialHeatTransition): for a mixture this would be the gas
// of the main liquid out of the whole cell's mass, and the impurities (the salt of Brine) would vanish. Without the pressure mod a mixture boils by
// the points of its liquids (below), so the release is not done for a mixture cell; with the pressure mod that mod does not
// do it either (boiling from the surface by the phase rules).
SIM_HOOK("liquid-mixing", bool, "DoPartialHeatTransition", MixFlash, (SimData* sim, void* events, int32_t cell, int32_t neighbour))
{
	if (MaybeMixed(cell) && MixingNow() && sim->updatedCells == mixCells)
	{
		flashesBlocked.store(flashesBlocked.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		return false;
	}
	return MixFlash_original(sim, events, cell, neighbour);
}

SIM_HOOK("liquid-mixing", bool, "DoStateTransition", MixTransition, (SimData* sim, void* events, int32_t cell, void* elementData))
{
	// Called for every cell every tick: first the cheapest check, whether the cell holds a mixture.
	if (!MaybeMixed(cell)) return MixTransition_original(sim, events, cell, elementData);
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (table && MixingNow() && transitionPending.size() == mixStore.size() && sim->updatedCells == mixCells)
	{
		const liquid::Mix& mix = mixStore[cell];
		const uint16_t element = sim->updatedCells->elementIdx.first[cell];
		if (mix.count >= 2 && liquid::Contains(mix, element)
			&& PendingTransition(table, mix, sim->updatedCells->temperature.first[cell], boilByComposition.load(std::memory_order_relaxed) != 0) != 0)
		{
			if (!transitionPending[cell]) transitionsMarked.fetch_add(1, std::memory_order_relaxed);
			transitionPending[cell] = 1;
			return false;
		}
	}
	return MixTransition_original(sim, events, cell, elementData);
}

// ---------- Mixture evaporation by the phase rules (service for the gas mixture layer and the pressure plugin) ----------
//
// How much liquid leaves as vapour is computed by the phase rules from the cell's main liquid (its vapour pressure curve). Here this
// portion is split among the mixture liquids by Raoult's law: each one's share is proportional to its mole fraction in the mixture
// multiplied by its saturated vapour pressure at the cell temperature. The vapour pressure is taken from the phase rules formula with
// a Trouton ratio of 10.5 from the liquid's boiling point in the game data: the split needs only the pressure ratios of
// liquids of one group, and the water-group ones boil within 20 degrees of each other. A liquid that becomes a liquid when boiling
// (Polluted Brine into Polluted Water) does not leave as vapour. The solid residue accumulates per cell and is released
// in chunks no smaller than OrePortion.

SIM_COUNTER(evaporatedMixed, "liquid-mix.evaporated-mixed");     // how many times the phase rules evaporated a mixture by composition
SIM_COUNTER(evaporatedMixedGrams, "liquid-mix.evaporated-mixed-grams");
SIM_COUNTER(oreReleasedGrams, "liquid-mix.ore-released-grams");  // solid residue released in chunks

namespace
{
	const float OrePortion = 0.1f;
	struct OreKey { int32_t cell; uint16_t ore; };
	struct PendingOre { int32_t cell; uint16_t ore; float mass, temperature; };
	std::vector<PendingOre> pendingOre;

	int32_t EvaporateMixture(SimData* sim, int32_t cell, float mass, float temperature, uint16_t onlyGas, LiquidVapourOut* out, float* takenOut)
	{
		if (takenOut) *takenOut = 0.0f;
		if (!MaybeMixed(cell)) return 0;
		const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
		if (!table || !out || !(mass > 0.0f) || !MixingNow() || sim->updatedCells != mixCells) return 0;
		MixLock lock;
		int32_t count = 0;
		WithGrid(sim, table, [&](LiveGrid& grid) {
			liquid::Mix* mix = grid.mix(cell);
			if (mix->count < 2) return false;
			liquid::Portion portion = liquid::CompositionOf(grid, cell);
			const float total = grid.mass(cell);
			float masses[liquid::MixSlots], weights[liquid::MixSlots], taken[liquid::MixSlots] = {};
			double sum = 0.0;
			for (int i = 0; i < portion.count; i++)
			{
				masses[i] = portion.share[i] * total;
				const liquid::Liquid& liquid = table->move[portion.element[i]];
				const bool vapour = liquid.highTransition != 0xFFFF && table->StateOf(liquid.highTransition) == liquid::Gas
					&& (onlyGas == 0xFFFF || liquid.highTransition == onlyGas);
				// Mole fraction (in shares of mass over molar mass; the common factor cancels) times saturated vapour pressure.
				const double moles = liquid.molar > 0.0f ? masses[i] / liquid.molar : 0.0;
				const double pressure = vapour && temperature > 0.0f && liquid.highTemp > 0.0f ? exp(10.5 * (1.0 - liquid.highTemp / temperature)) : 0.0;
				weights[i] = (float)(moles * pressure);
				sum += weights[i];
			}
			if (!(sum > 0.0)) return false;
			// Shares by weights; a liquid that ran short gives everything, the remainder is split among the others.
			float left = mass < total ? mass : total;
			for (int round = 0; round < liquid::MixSlots && left > 1e-7f; round++)
			{
				double active = 0.0;
				for (int i = 0; i < portion.count; i++) if (masses[i] - taken[i] > 0.0f) active += weights[i];
				if (!(active > 0.0)) break;
				float spent = 0.0f;
				for (int i = 0; i < portion.count; i++)
				{
					const float available = masses[i] - taken[i];
					if (!(available > 0.0f) || !(weights[i] > 0.0f)) continue;
					float part = (float)(left * (weights[i] / active));
					if (part > available) part = available;
					taken[i] += part;
					spent += part;
				}
				left -= spent;
			}
			for (int i = 0; i < portion.count; i++)
			{
				if (!(taken[i] > 0.0f)) continue;
				const liquid::Liquid& liquid = table->move[portion.element[i]];
				const float oreShare = liquid.highOre != 0xFFFF && liquid.highOreShare > 0.0f && liquid.highOreShare < 1.0f ? liquid.highOreShare : 0.0f;
				const float gasMass = taken[i] * (1.0f - oreShare);
				int slot = -1;
				for (int k = 0; k < count; k++) if (out[k].gas == liquid.highTransition) slot = k;
				if (slot < 0 && count < 4) { slot = count++; out[slot] = { liquid.highTransition, 0, 0.0f }; }
				if (slot >= 0) out[slot].mass += gasMass;
				if (oreShare > 0.0f)
				{
					bool found = false;
					for (PendingOre& ore : pendingOre)
					{
						if (ore.cell == cell && ore.ore == liquid.highOre) { ore.mass += taken[i] * oreShare; ore.temperature = temperature; found = true; break; }
					}
					if (!found) pendingOre.push_back({ cell, liquid.highOre, taken[i] * oreShare, temperature });
				}
				masses[i] -= taken[i];
			}
			float spentTotal = 0.0f;
			for (int i = 0; i < portion.count; i++) spentTotal += taken[i];
			if (takenOut) *takenOut = spentTotal;
			const float remaining = total - spentTotal;
			if (remaining > 0.0f)
			{
				for (int i = 0; i < portion.count; i++) portion.share[i] = masses[i] > 0.0f ? masses[i] / remaining : 0.0f;
				// Shares are computed from the mass that will remain; the caller reduces the cell mass.
				const float now = grid.mass(cell);
				grid.mass(cell) = remaining;
				liquid::SetComposition(grid, *table, cell, portion);
				grid.mass(cell) = now;
			}
			evaporatedMixed.store(evaporatedMixed.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
			evaporatedMixedGrams.store(evaporatedMixedGrams.load(std::memory_order_relaxed) + (int64_t)(spentTotal * 1000.0f), std::memory_order_relaxed);
			return true;
		});
		return count;
	}

	// Release of the accumulated solid residue (simulation thread, after the tick).
	void ReleaseOre(SimData* sim)
	{
		static const SpawnOreFn spawnOre = (SpawnOreFn)simhook_find("SimEvents::SpawnOre");
		if (!spawnOre || pendingOre.empty()) return;
		const SimCells* cells = sim->updatedCells;
		for (size_t i = 0; i < pendingOre.size();)
		{
			PendingOre& ore = pendingOre[i];
			const bool gone = (size_t)ore.cell >= cells->elementIdx.size() || cells->elementIdx.first[ore.cell] == sim->vacuumElementIdx;
			if (ore.mass >= OrePortion || gone)
			{
				spawnOre(sim->simEvents, sim, (uint64_t)ore.cell, ore.ore, ore.mass, ore.temperature, 0xFF, 0, true);
				oreReleasedGrams.store(oreReleasedGrams.load(std::memory_order_relaxed) + (int64_t)(ore.mass * 1000.0f), std::memory_order_relaxed);
				ore = pendingOre.back();
				pendingOre.pop_back();
				continue;
			}
			i++;
		}
	}
}

SIM_SERVICE(Evaporate, "liquid-mix.evaporate", EvaporateMixture);

// Mixture vapour pressure relative to the main liquid (service for the phase rules, liquid_service.h).
SIM_COUNTER(vapourFactors, "liquid-mix.vapour-factors"); // how many times the phase rules asked for the correction for a mixture cell

namespace
{
	float VapourFactor(SimData* sim, int32_t cell, float temperature)
	{
		if (!MaybeMixed(cell)) return 1.0f;
		const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
		if (!table || !(temperature > 0.0f) || !MixingNow() || sim->updatedCells != mixCells) return 1.0f;
		const liquid::Mix& mix = mixStore[cell];
		const uint16_t main = sim->updatedCells->elementIdx.first[cell];
		if (mix.count < 2 || !liquid::Contains(mix, main) || main >= table->move.size()) return 1.0f;
		const liquid::Liquid& own = table->move[main];
		if (own.highTransition == 0xFFFF || !(own.highTemp > 0.0f)) return 1.0f;
		double moles = 0.0, pressure = 0.0;
		for (int i = 0; i < mix.count; i++)
		{
			const liquid::Liquid& liquid = table->move[mix.element[i]];
			const double n = liquid.molar > 0.0f ? mix.share[i] / liquid.molar : 0.0;
			moles += n;
			if (liquid.highTransition == own.highTransition && liquid.highTemp > 0.0f) pressure += n * exp(10.5 * (1.0 - liquid.highTemp / temperature));
		}
		const double reference = exp(10.5 * (1.0 - own.highTemp / temperature));
		vapourFactors.store(vapourFactors.load(std::memory_order_relaxed) + 1, std::memory_order_relaxed);
		return moles > 0.0 && reference > 0.0 ? (float)(pressure / moles / reference) : 1.0f;
	}
}

SIM_SERVICE(VapourFactor, "liquid-mix.vapour-factor", VapourFactor);

// Mixture vapours, each with its own equilibrium (service for the phase rules of the gas mixture layer, liquid_service.h).
namespace
{
	int32_t Vapours(SimData* sim, int32_t cell, float temperature, LiquidVapourEntry* out)
	{
		if (!MaybeMixed(cell) || !out) return 0;
		const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
		if (!table || !(temperature > 0.0f) || !MixingNow() || sim->updatedCells != mixCells) return 0;
		const liquid::Mix& mix = mixStore[cell];
		if (!liquid::Contains(mix, sim->updatedCells->elementIdx.first[cell])) return 0;
		double moles[liquid::MixSlots], total = 0.0;
		for (int i = 0; i < mix.count; i++)
		{
			const liquid::Liquid& liquid = table->move[mix.element[i]];
			moles[i] = liquid.molar > 0.0f ? mix.share[i] / liquid.molar : 0.0;
			total += moles[i];
		}
		if (!(total > 0.0)) return 0;
		struct Sum { uint16_t gas, liquid; double pressure, best, own; };
		Sum sums[liquid::MixSlots];
		int count = 0;
		for (int i = 0; i < mix.count; i++)
		{
			const liquid::Liquid& liquid = table->move[mix.element[i]];
			if (liquid.highTransition == 0xFFFF || table->StateOf(liquid.highTransition) != liquid::Gas || !(liquid.highTemp > 0.0f)) continue;
			const double own = exp(10.5 * (1.0 - liquid.highTemp / temperature));
			const double partial = moles[i] / total * own;
			int k = 0;
			while (k < count && sums[k].gas != liquid.highTransition) k++;
			if (k == count) sums[count++] = { liquid.highTransition, mix.element[i], 0.0, -1.0, own };
			sums[k].pressure += partial;
			if (partial > sums[k].best) { sums[k].best = partial; sums[k].liquid = mix.element[i]; sums[k].own = own; }
		}
		for (int k = 0; k < count; k++) out[k] = { sums[k].gas, sums[k].liquid, sums[k].own > 0.0 ? (float)(sums[k].pressure / sums[k].own) : 0.0f };
		return count;
	}
}

SIM_SERVICE(VapoursService, "liquid-mix.vapours", Vapours);

// For the game-free check (hooktest): whether the water-group cell cell is denser than the one below it (water anomaly). Record: cell
// structure, cell index; in reply the cell index is set to 1 (upper denser: convection), 0 (no) or -1 (not water-group).
SIM_DATA(ConvectionForTest, "liquid-mix.convection-for-test")
{
	struct Test { SimData* sim; int32_t cell; };
	if (size != (int32_t)sizeof(Test)) return -1;
	Test* test = (Test*)data;
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	const std::vector<float>* solute = publishedSolute.load(std::memory_order_acquire);
	if (!table || !solute) return -2;
	LiveGrid grid(test->sim, nullptr, nullptr, NoDisplace, NoSpawn, CountChange, false, table, nullptr);
	const float upper = WaterDensity(grid, table, *solute, test->cell), lower = WaterDensity(grid, table, *solute, test->cell - test->sim->width);
	test->cell = !(upper > 0.0f) || !(lower > 0.0f) ? -1 : upper > lower ? 1 : 0;
	return 0;
}

// Share of one liquid per cell of the visible rectangle for the Mixtures Overlay. Record: eight ints (element, whether to wait for
// the end of the tick, minX, minY, maxX, maxY in game cells, spare), then one float per game cell; into the cells
// of the rectangle the liquid's share by mass is written (1 or 0 for a pure cell), -1 for cells without liquid. 0: done;
// 1: a tick is running and waiting was not requested (array untouched); 2: mixtures disabled.
SIM_DATA(MixField, "liquid-mix.field")
{
	if (size < 32) return -1;
	int32_t* header = (int32_t*)data;
	const int32_t element = header[0], wait = header[1];
	if (mixingOn.load(std::memory_order_relaxed) == 0) return 2;
	std::unique_lock<std::mutex> lock(mixGuard, std::defer_lock);
	if (wait) lock.lock();
	else if (!lock.try_lock()) return 1;
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	const int32_t width = lastWidth, gameWidth = width - 2;
	if (!table || !mixCells || gameWidth <= 0) return 2;
	const int32_t gameCells = (size - 32) / (int32_t)sizeof(float), gameHeight = gameCells / gameWidth;
	float* out = (float*)((uint8_t*)data + 32);
	const int32_t minX = header[2] < 0 ? 0 : header[2], minY = header[3] < 0 ? 0 : header[3];
	const int32_t maxX = header[4] >= gameWidth ? gameWidth - 1 : header[4], maxY = header[5] >= gameHeight ? gameHeight - 1 : header[5];
	const uint16_t* elements = mixCells->elementIdx.first;
	const size_t total = mixStore.size();
	for (int32_t y = minY; y <= maxY; y++)
	{
		for (int32_t x = minX; x <= maxX; x++)
		{
			const int32_t gameCell = y * gameWidth + x;
			const size_t cell = (size_t)(y + 1) * (size_t)width + (size_t)(x + 1);
			if (cell >= total || gameCell >= gameCells) continue;
			const uint16_t here = elements[cell];
			if (table->StateOf(here) != liquid::Fluid) { out[gameCell] = -1.0f; continue; }
			float share = here == element ? 1.0f : 0.0f;
			const liquid::Mix& mix = mixStore[cell];
			if (mix.count >= 2 && liquid::Contains(mix, here))
			{
				share = 0.0f;
				for (int i = 0; i < mix.count; i++) if (mix.element[i] == element) share = mix.share[i];
			}
			out[gameCell] = share;
		}
	}
	return 0;
}

// ---------- Heat capacity of a mixture ----------
//
// The simulation exchanges heat between neighbouring cells (UpdateTemperature) with the heat capacity of the cell's
// element, that is of the main liquid for a mixture. Here the temperature change of a mixed cell from one exchange is
// rescaled by c(main) / c(mixture), where c(mixture) is the mass-weighted heat capacity of its liquids: the energy that
// went in or out stays the same, so the mixture heats and cools at its own rate. Thermal conductivity stays the main
// liquid's. Other energy paths (buildings, energy messages) still use the main liquid.

namespace
{
	std::atomic<const std::vector<float>*> publishedHeat{nullptr}; // specific heat capacity by element id, 0: unknown
}

SIM_COUNTER(heatScaled, "liquid-mix.heat-scaled"); // heat exchanges of mixed cells rescaled by the mixture's heat capacity

// Heat capacities of liquids: entries "element id (int32), specific heat capacity (float)".
SIM_DATA(HeatCapacities, "liquid-mix.heat")
{
	struct Entry { int32_t element; float heat; };
	const liquid::Table* table = publishedTable.load(std::memory_order_acquire);
	if (!table || size < 0 || size % (int32_t)sizeof(Entry) != 0) return -1;
	std::vector<float>* heat = new std::vector<float>(table->byElement.size(), 0.0f);
	const Entry* entries = (const Entry*)data;
	for (int32_t i = 0; i < size / (int32_t)sizeof(Entry); i++)
	{
		if (entries[i].element >= 0 && (size_t)entries[i].element < heat->size() && entries[i].heat > 0.0f) (*heat)[entries[i].element] = entries[i].heat;
	}
	// The previous table is not freed: simulation threads may still be reading it (it changes once per game).
	publishedHeat.store(heat, std::memory_order_release);
	return 0;
}

namespace
{
	// c(main) / c(mixture) for a mixed cell; 1 when anything is unknown.
	float HeatFactor(const std::vector<float>& heat, const liquid::Mix& mix, uint16_t main)
	{
		if (main >= heat.size() || !(heat[main] > 0.0f)) return 1.0f;
		float mixture = 0.0f;
		for (int i = 0; i < mix.count; i++)
		{
			const uint16_t element = mix.element[i];
			if (element >= heat.size() || !(heat[element] > 0.0f)) return 1.0f;
			mixture += mix.share[i] * heat[element];
		}
		return mixture > 0.0f ? heat[main] / mixture : 1.0f;
	}

	void RescaleHeat(SimCells* cells, const std::vector<float>& heat, int32_t cell, float before)
	{
		const liquid::Mix& mix = mixStore[cell];
		const uint16_t main = cells->elementIdx.first[cell];
		if (mix.count < 2 || !liquid::Contains(mix, main)) return;
		const float after = cells->temperature.first[cell];
		const float scaled = before + (after - before) * HeatFactor(heat, mix, main);
		if (scaled > 0.0f) cells->temperature.first[cell] = scaled;
		heatScaled.fetch_add(1, std::memory_order_relaxed);
	}
}

SIM_HOOK("liquid-mixing", void, "UpdateTemperature", MixHeat, (SimData* sim, void* events, int32_t a, int32_t b))
{
	// Called for every pair of neighbouring cells with a temperature difference: the cheap block check comes first.
	const bool mixedA = MaybeMixed(a), mixedB = MaybeMixed(b);
	if (!mixedA && !mixedB)
	{
		MixHeat_original(sim, events, a, b);
		return;
	}
	const std::vector<float>* heat = publishedHeat.load(std::memory_order_acquire);
	SimCells* cells = sim->updatedCells;
	if (!heat || !MixingNow() || cells != mixCells || (size_t)a >= cells->temperature.size() || (size_t)b >= cells->temperature.size())
	{
		MixHeat_original(sim, events, a, b);
		return;
	}
	const float beforeA = cells->temperature.first[a], beforeB = cells->temperature.first[b];
	MixHeat_original(sim, events, a, b);
	if (mixedA) RescaleHeat(cells, *heat, a, beforeA);
	if (mixedB) RescaleHeat(cells, *heat, b, beforeB);
}

// ---------- One liquid of a cell turned into another by game logic ----------
//
// Some game logic replaces a cell's whole element (a body in Water makes it Polluted Water). For a mixture that would
// turn every liquid of the cell into the new one; instead the mod sends the conversion here, and only the share of that
// liquid changes. Entries "game cell, from element, to element" (int32 each); applied before the next tick.

namespace
{
	struct Conversion { int32_t cell, from, to; };
	std::mutex conversionGuard;
	std::vector<Conversion> pendingConversions;
}

SIM_COUNTER(conversionsDone, "liquid-mix.conversions"); // liquids of a cell converted by game logic

SIM_DATA(Convert, "liquid-mix.convert")
{
	if (size < 0 || size % (int32_t)sizeof(Conversion) != 0) return -1;
	const Conversion* entries = (const Conversion*)data;
	std::lock_guard<std::mutex> lock(conversionGuard);
	for (int32_t i = 0; i < size / (int32_t)sizeof(Conversion); i++) pendingConversions.push_back(entries[i]);
	return 0;
}

namespace
{
	void ApplyConversions(SimData* sim, const liquid::Table* table)
	{
		std::vector<Conversion> work;
		{
			std::lock_guard<std::mutex> lock(conversionGuard);
			work.swap(pendingConversions);
		}
		if (work.empty() || !MixingNow() || sim->updatedCells != mixCells) return;
		const int32_t width = sim->width, gameWidth = width - 2;
		if (gameWidth <= 0) return;
		WithGrid(sim, table, [&](LiveGrid& grid) {
			for (const Conversion& conversion : work)
			{
				if (conversion.cell < 0 || conversion.from < 0 || conversion.to < 0) continue;
				const uint16_t from = (uint16_t)conversion.from, to = (uint16_t)conversion.to;
				const int cell = (conversion.cell / gameWidth + 1) * width + conversion.cell % gameWidth + 1;
				if ((size_t)cell >= mixStore.size() || !table->Mixes(from, to)) continue;
				liquid::Mix* mix = grid.mix(cell);
				if (mix->count >= 2 && liquid::Contains(*mix, grid.element(cell)) && liquid::Contains(*mix, from))
				{
					liquid::Portion portion = liquid::CompositionOf(grid, cell);
					int i = -1, j = -1;
					for (int k = 0; k < portion.count; k++)
					{
						if (portion.element[k] == from) i = k;
						if (portion.element[k] == to) j = k;
					}
					if (i < 0) continue;
					if (j >= 0)
					{
						portion.share[j] += portion.share[i];
						portion.element[i] = portion.element[portion.count - 1];
						portion.share[i] = portion.share[portion.count - 1];
						portion.count--;
					}
					else portion.element[i] = to;
					liquid::SetComposition(grid, *table, cell, portion);
				}
				else if (grid.element(cell) == from)
				{
					grid.element(cell) = to;
					grid.changed(cell);
				}
				else continue;
				conversionsDone.fetch_add(1, std::memory_order_relaxed);
			}
			return true;
		});
	}
}
