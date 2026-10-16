// Check of the liquid mixture rules (sim/plugins/liquid_rules.h) without the game: mixing groups, the cell's main liquid
// and how it is kept, mixture density, the contact survey.
#include <stdio.h>
#include <string.h>

#include <chrono>
#include <vector>

#include "../plugins/liquid_move.h"
#include "../plugins/liquid_rules.h"

static bool ok = true;

static void Check(bool condition, const char* what)
{
	if (!condition) { printf("  FAILED: %s\n", what); ok = false; }
}

int main()
{
	using namespace liquid;
	// Elements: 1 Water, 2 Polluted Water, 3 Salt Water, 4 Brine, 5 Crude Oil, 6 Petroleum, 7 Mercury. Groups: water (0)
	// and hydrocarbons (1); Mercury mixes with nothing.
	// Element states: 0 vacuum, 1-7 liquids, 8 rock, 9 gas. Movement numbers as for Water in the game: per tick in
	// one direction up to 125 kg, smallest portion 10 g; each liquid has its own full-cell mass.
	struct Block { TableHeader header; Liquid liquids[7]; uint8_t states[12]; float strength[2]; } block =
	{
		{ 10, 7, 0.2f, 2 },
		{
			{ 1, 0, 1000.0f, 18.0f, 125.0f, 0.01f, 0.01f, 1000.0f }, { 2, 0, 1000.0f, 20.0f, 125.0f, 0.01f, 0.01f, 1000.0f },
			{ 3, 0, 1100.0f, 21.0f, 100.0f, 0.01f, 0.01f, 1100.0f }, { 4, 0, 1200.0f, 22.0f, 100.0f, 0.01f, 0.01f, 1200.0f },
			{ 5, 1, 870.0f, 500.0f, 50.0f, 0.1f, 0.1f, 870.0f }, { 6, 1, 740.0f, 82.2f, 50.0f, 0.1f, 0.1f, 740.0f },
			{ 7, NoGroup, 13534.0f, 200.6f, 140.0f, 0.01f, 0.01f, 1000.0f },
		},
		{ Vacuum, Fluid, Fluid, Fluid, Fluid, Fluid, Fluid, Fluid, Solid, Gas },
		{ 1.0f, 2.0f },
	};
	Table table;
	Check(table.Read(&block, sizeof(block)), "the table is accepted");
	Block broken = block;
	broken.liquids[2].element = 1;
	Table rejected;
	Check(!rejected.Read(&broken, sizeof(broken)) && !rejected.Read(&block, sizeof(block) - 4), "a table with a repeated element or a wrong size is rejected");
	Check(table.Mixes(1, 4) && table.Mixes(5, 6) && !table.Mixes(1, 5) && !table.Mixes(7, 7) && !table.Mixes(1, 0), "liquids mix only inside a group");

	// 1. Mixture density: volumes add up. 600 kg of Water and 400 kg of Brine take 0.933 m3.
	const uint16_t pair[2] = { 1, 4 };
	const float waterAndBrine[2] = { 600.0f, 400.0f };
	const float density = table.Density(pair, waterAndBrine, 2);
	printf("600 kg of water with 400 kg of brine: %.0f kg/m3\n", density);
	Check(fabsf(density - 1071.4f) < 0.5f, "density of a mixture");

	// 2. Main liquid: the one there is more of; the current one holds while the other is no more than 55.6%.
	auto main = [&](float brine, uint16_t current) { const float masses[2] = { 1000.0f * (1.0f - brine), 1000.0f * brine }; return table.Main(pair, masses, 2, current); };
	float up = 0.0f, down = 0.0f;
	for (float brine = 0.0f; brine < 1.0f && up == 0.0f; brine += 0.001f) if (main(brine, 1) == 4) up = brine;
	for (float brine = 1.0f; brine > 0.0f && down == 0.0f; brine -= 0.001f) if (main(brine, 4) == 1) down = brine;
	printf("water with brine: the cell becomes brine at %.1f%% of brine and water again at %.1f%%\n", up * 100.0f, down * 100.0f);
	Check(fabsf(up - 0.556f) < 0.002f && fabsf(down - 0.444f) < 0.002f, "the main liquid is kept near the border");
	Check(main(0.49f, NoGroup) == 1 && main(0.51f, NoGroup) == 4 && main(0.51f, 6) == 4, "without a current main liquid the larger one wins");
	const uint16_t three[3] = { 1, 3, 4 };
	const float thirds[3] = { 300.0f, 390.0f, 320.0f };
	Check(table.Main(three, thirds, 3, 1) == 3 && table.Main(three, thirds, 3, 4) == 4, "three liquids: the largest wins unless the current one is close to it");

	// 3. Survey. A 4 x 3 grid (bottom to top): Brine, Brine, Crude Oil, Mercury / Water, Salt Water, Petroleum, empty /
	// empty, empty, Water, empty.
	const uint16_t cells[12] = { 4, 4, 5, 7,  1, 3, 6, 0,  0, 0, 1, 0 };
	const float masses[12] = { 1200, 1200, 870, 1000,  1000, 1100, 740, 0,  0, 0, 500, 0 };
	Survey survey;
	survey.Run(table, cells, masses, 12, 4);
	printf("survey: %lld liquid cells (%lld of liquids that can mix), %.0f kg, fronts %lld, contacts of liquids that do not mix %lld, denser above lighter %lld; pairs:",
		(long long)survey.cells, (long long)survey.groupCells, survey.mass, (long long)survey.fronts, (long long)survey.foreign, (long long)survey.inversions);
	for (const Pair& pair : survey.pairs) printf(" %d:%d-%d x%d", pair.kind, pair.first, pair.second, pair.count);
	printf("\n");
	// Will mix: Brine with Water and with Salt Water (above it), Water with Salt Water, Crude Oil with Petroleum. Will not mix:
	// Brine with Crude Oil, Crude Oil with Mercury, Salt Water with Petroleum, Petroleum with the Water above it. Denser than the one below: Water
	// over Petroleum (1000 over 740).
	Check(survey.cells == 8 && survey.groupCells == 7 && survey.fronts == 4 && survey.foreign == 4 && survey.inversions == 1, "survey counts");
	bool found = false;
	for (const Pair& pair : survey.pairs) found = found || (pair.kind == Inversion && pair.first == 6 && pair.second == 1 && pair.count == 1);
	Check(found, "water over petroleum is reported as the pair to be reordered");

	// 4. Own movement calculation (liquid_move.h) on a small grid: a closed rock bowl 9 x 8 (7 x 6 inside).
	{
		enum { W = 9, H = 8, N = W * H };
		struct Cells { uint16_t element[N]; float temperature[N], mass[N]; uint8_t properties[N], diseaseIdx[N], infestation[N]; int32_t diseaseCount[N]; float growthError[N]; };
		static Cells main, shot;
		static float flows[N][4];
		auto wrap = [](Cells& cells) {
			SimCells result = {};
			result.elementIdx = { nullptr, cells.element, cells.element + N, cells.element + N };
			result.temperature = { nullptr, cells.temperature, cells.temperature + N, cells.temperature + N };
			result.mass = { nullptr, cells.mass, cells.mass + N, cells.mass + N };
			result.properties = { nullptr, cells.properties, cells.properties + N, cells.properties + N };
			result.diseaseIdx = { nullptr, cells.diseaseIdx, cells.diseaseIdx + N, cells.diseaseIdx + N };
			result.diseaseCount = { nullptr, cells.diseaseCount, cells.diseaseCount + N, cells.diseaseCount + N };
			return result;
		};
		SimCells mainCells = wrap(main), shotCells = wrap(shot);
		SimData sim = {};
		sim.width = W; sim.height = H; sim.vacuumElementIdx = 0; sim.voidElementIdx = 0xFFFE; sim.cells = &shotCells; sim.updatedCells = &mainCells;
		struct Grid
		{
			Cells& cells;
			float (*flows)[4];
			int drops = 0;
			uint16_t& element(int cell) { return cells.element[cell]; }
			float& mass(int cell) { return cells.mass[cell]; }
			float& temperature(int cell) { return cells.temperature[cell]; }
			uint8_t& diseaseIdx(int cell) { return cells.diseaseIdx[cell]; }
			int32_t& diseaseCount(int cell) { return cells.diseaseCount[cell]; }
			uint8_t& infestation(int cell) { return cells.infestation[cell]; }
			float& growthError(int cell) { return cells.growthError[cell]; }
			uint8_t properties(int cell) const { return cells.properties[cell]; }
			float* flow(int cell) { return flows[cell]; }
			bool displaceGas(int, uint16_t) { return false; }
			bool spawnFalling(int, int, uint16_t, float, float, uint8_t, int32_t) { drops++; return false; }
			Mix* store = nullptr; // nullptr: mixtures are not tracked
			const Table* table = nullptr;
			Mix* mix(int cell) { if (!store) return nullptr; if (store[cell].count >= 2 && !Contains(store[cell], cells.element[cell])) store[cell].count = 0; return &store[cell]; }
			bool mixes(uint16_t a, uint16_t b) const { return store && a != b && table->Mixes(a, b); }
			bool mixed(int cell) { const Mix* m = mix(cell); return m && m->count >= 2; }
			void noteMixed(int) {}
			void changed(int) {}
			void transferring(int, float, uint8_t, int32_t) {}
			void transferred(bool) {}
		} grid = { main, flows };
		grid.table = &table;
		auto reset = [&] {
			for (int i = 0; i < N; i++)
			{
				const int x = i % W, y = i / W;
				const bool wall = x == 0 || y == 0 || x == W - 1 || y == H - 1;
				main.element[i] = wall ? 8 : 0; main.mass[i] = wall ? 1000.0f : 0.0f; main.temperature[i] = wall ? 300.0f : 0.0f;
				main.properties[i] = 0; main.diseaseIdx[i] = NoDisease; main.diseaseCount[i] = 0; main.infestation[i] = 0; main.growthError[i] = 0.0f;
			}
		};
		// A tick as in the game: a snapshot, then the snapshot's liquid cells bottom to top, left to right; cells lighter than 10 g are erased.
		auto tick = [&] {
			shot = main;
			for (int cell = W; cell < N - W; cell++)
			{
				if (table.StateOf(shot.element[cell]) == Fluid && 0.0f < shot.mass[cell]) UpdateCell(grid, table, &sim, cell);
			}
			for (int cell = 0; cell < N; cell++)
			{
				if (table.StateOf(main.element[cell]) == Fluid && main.mass[cell] < 0.01f) ClearCell(grid, &sim, cell);
			}
		};
		auto total = [&](double* heat, long long* germs) {
			double mass = 0.0;
			*heat = 0.0; *germs = 0;
			for (int i = 0; i < N; i++)
			{
				if (table.StateOf(main.element[i]) != Fluid) continue;
				mass += main.mass[i]; *heat += (double)main.mass[i] * main.temperature[i]; *germs += main.diseaseCount[i];
			}
			return mass;
		};
		// 10 tonnes of Water as two cells at the ceiling: a hot one with germs and a cold one. The water falls and spreads: the bottom
		// row fills a full cell (1000 kg), the rest lies evenly as a second row.
		reset();
		const int hot = 6 * W + 2, cold = 6 * W + 6;
		main.element[hot] = 1; main.mass[hot] = 5000.0f; main.temperature[hot] = 350.0f; main.diseaseIdx[hot] = 1; main.diseaseCount[hot] = 100000;
		main.element[cold] = 1; main.mass[cold] = 5000.0f; main.temperature[cold] = 290.0f;
		for (int i = 0; i < 4000; i++) tick();
		double heat; long long germs;
		const double mass = total(&heat, &germs);
		float low = 1e9f, high = 0.0f, lowAbove = 1e9f, highAbove = 0.0f;
		for (int x = 1; x < W - 1; x++)
		{
			const float m = main.mass[W + x], above = main.mass[2 * W + x];
			if (m < low) low = m;
			if (m > high) high = m;
			if (above < lowAbove) lowAbove = above;
			if (above > highAbove) highAbove = above;
		}
		int liquidCells = 0;
		for (int i = 0; i < N; i++) liquidCells += table.StateOf(main.element[i]) == Fluid;
		printf("own movement, 10 t of water poured into a basin 7 wide: mass %.2f kg, heat %.0f of %.0f, germs %lld of 100000, %d liquid cells, bottom row %.1f-%.1f kg, second row %.1f-%.1f kg\n",
			mass, heat / 1000.0, (5000.0 * 350.0 + 5000.0 * 290.0) / 1000.0, germs, liquidCells, low, high, lowAbove, highAbove);
		Check(fabs(mass - 10000.0) < 0.5, "mass is kept");
		Check(fabs(heat - (5000.0 * 350.0 + 5000.0 * 290.0)) < 0.002 * heat, "heat is kept");
		Check(germs > 99000 && germs <= 100000, "germs travel with the liquid");
		Check(low > 999.0f && high < 1011.0f, "the bottom row holds a full cell");
		Check(highAbove - lowAbove < 1.0f && fabsf((lowAbove + highAbove) * 0.5f - (10000.0f - (low + high) * 3.5f) / 7.0f) < 2.0f, "the second row is level");
		Check(liquidCells == 14, "10 t take two rows of seven cells");
		// Crude Oil over Water does not flow into it and does not fall through it: no mass is moved into a foreign liquid.
		reset();
		for (int x = 1; x < W - 1; x++) { main.element[W + x] = 1; main.mass[W + x] = 1000.0f; main.temperature[W + x] = 300.0f; }
		main.element[5 * W + 4] = 5; main.mass[5 * W + 4] = 870.0f; main.temperature[5 * W + 4] = 300.0f;
		for (int i = 0; i < 500; i++) tick();
		double oil = 0.0, water = 0.0;
		bool layered = true;
		for (int i = 0; i < N; i++)
		{
			if (main.element[i] == 5) { oil += main.mass[i]; layered = layered && i / W == 2; }
			if (main.element[i] == 1) { water += main.mass[i]; layered = layered && i / W == 1; }
		}
		printf("own movement, oil over water: oil %.2f kg, water %.2f kg, oil lies in the row above the water: %s\n", oil, water, layered ? "yes" : "no");
		Check(fabs(oil - 870.0) < 0.1 && fabs(water - 7000.0) < 0.1 && layered, "different liquids stay apart");

		// Mixture: in the bowl Water on the left, Brine on the right, two rows each. Liquids of one group flow into each other and
		// mix; the mass of each is conserved, the composition evens out, and the pool comes to rest.
		static Mix store[N];
		for (int i = 0; i < N; i++) store[i].count = 0;
		grid.store = store;
		reset();
		for (int y = 1; y <= 2; y++)
			for (int x = 1; x < W - 1; x++)
			{
				const int cell = y * W + x;
				const bool brine = x >= 4;
				main.element[cell] = brine ? 4 : 1; main.mass[cell] = brine ? 1200.0f : 1000.0f; main.temperature[cell] = brine ? 280.0f : 320.0f;
			}
		auto perLiquid = [&](uint16_t element) {
			double sum = 0.0;
			for (int i = 0; i < N; i++)
			{
				if (table.StateOf(main.element[i]) != Fluid) continue;
				const Mix* m = grid.mix(i);
				if (m && m->count >= 2) { for (int k = 0; k < m->count; k++) if (m->element[k] == element) sum += (double)m->share[k] * main.mass[i]; }
				else if (main.element[i] == element) sum += main.mass[i];
			}
			return sum;
		};
		const double water0 = perLiquid(1), brine0 = perLiquid(4);
		int64_t exchanges = 0;
		float moving = 0.0f;
		for (int i = 0; i < 3000; i++)
		{
			float before[N];
			memcpy(before, main.mass, sizeof(before));
			tick();
			int64_t mixedNow = 0;
			uint8_t everyBlock[(N + 63) / 64] = { 1, 1 };
			Diffuse(grid, table, &sim, 0.05f, everyBlock, (N + 63) / 64, &exchanges, &mixedNow);
			moving = 0.0f;
			for (int k = 0; k < N; k++) moving = fmaxf(moving, fabsf(main.mass[k] - before[k]));
		}
		const double water1 = perLiquid(1), brine1 = perLiquid(4);
		float lowShare = 1.0f, highShare = 0.0f;
		int mixedCells = 0, cellsNow = 0;
		for (int i = 0; i < N; i++)
		{
			if (table.StateOf(main.element[i]) != Fluid) continue;
			cellsNow++;
			const Mix* m = grid.mix(i);
			float share = main.element[i] == 4 ? 1.0f : 0.0f;
			if (m && m->count >= 2) { mixedCells++; share = 0.0f; for (int k = 0; k < m->count; k++) if (m->element[k] == 4) share = m->share[k]; }
			lowShare = fminf(lowShare, share); highShare = fmaxf(highShare, share);
		}
		printf("mixing, water and brine side by side in the basin: water %.3f -> %.3f kg, brine %.3f -> %.3f kg, %d of %d cells mixed, brine share %.4f-%.4f, largest mass change in the last tick %.5f kg, %lld exchanges\n",
			water0, water1, brine0, brine1, mixedCells, cellsNow, lowShare, highShare, moving, (long long)exchanges);
		Check(fabs(water1 - water0) < 1e-3 * water0 && fabs(brine1 - brine0) < 1e-3 * brine0, "each liquid of the mixture keeps its mass");
		const float expected = (float)(brine0 / (water0 + brine0));
		Check(highShare - lowShare < 0.01f && fabsf(lowShare - expected) < 0.01f, "the mixture becomes even");
		Check(moving < 0.01f, "the basin comes to rest");
		grid.store = nullptr;
	}

	// 5. Ocean world: a 1116 x 178 grid (like a colony's), entirely filled with one and the same mixture of Water and Brine. The mixing
	// pass first walks all blocks (that is the worst-case cost), finds nothing to exchange, and drops them;
	// after that it costs nothing until the composition changes somewhere.
	{
		enum { W = 1116, H = 178, N = W * H, Blocks = (N + 63) / 64 };
		struct OceanGrid
		{
			std::vector<uint16_t> elements; std::vector<float> masses; std::vector<Mix> mixes; std::vector<uint8_t> flags;
			uint16_t& element(int cell) { return elements[cell]; }
			float& mass(int cell) { return masses[cell]; }
			Mix* mix(int cell) { return &mixes[cell]; }
			bool mixed(int cell) { return mixes[cell].count >= 2; }
			void noteMixed(int cell) { flags[(size_t)cell >> 6] = 1; }
			// Diffuse needs nothing else, but SetComposition calls the element change.
			void changed(int) {}
		} ocean;
		ocean.elements.assign(N, 1);
		ocean.masses.assign(N, 1100.0f);
		ocean.mixes.assign(N, Mix{ { 1, 4, 0, 0 }, { 0.5f, 0.5f, 0.0f, 0.0f }, 2 });
		ocean.flags.assign(Blocks, 1);
		SimCells oceanCells = {};
		oceanCells.elementIdx = { nullptr, ocean.elements.data(), ocean.elements.data() + N, ocean.elements.data() + N };
		SimData oceanSim = {};
		oceanSim.width = W; oceanSim.height = H; oceanSim.updatedCells = &oceanCells; oceanSim.cells = &oceanCells;
		auto pass = [&](int64_t* exchanged) {
			int64_t mixedCount = 0;
			const auto start = std::chrono::steady_clock::now();
			Diffuse(ocean, table, &oceanSim, 0.05f, ocean.flags.data(), Blocks, exchanged, &mixedCount);
			return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
		};
		int64_t first = 0, second = 0, third = 0;
		const double all = pass(&first), settled = pass(&second);
		int active = 0;
		for (uint8_t flag : ocean.flags) active += flag;
		// Somewhere in the middle the composition changes: the walk again touches only that place.
		const int poked = (H / 2) * W + W / 2;
		Portion fresh = { { 1, 4 }, { 0.8f, 0.2f }, 2 };
		SetComposition(ocean, table, poked, fresh);
		const double local = pass(&third);
		printf("ocean world of one mixture, %d cells: first pass over all blocks %.2f ms (%lld exchanges), then %.3f ms with %d blocks left; after a change in one cell %.3f ms (%lld exchanges)\n",
			(int)N, all, (long long)first, settled, active, local, (long long)third);
		Check(first == 0 && second == 0 && active == 0, "an even ocean needs no exchanges and drops every block");
		Check(third > 0 && third <= 200, "a change in one place is mixed only around it");
	}

	printf("%s\n", ok ? "LIQUID OK" : "LIQUID FAILED");
	return ok ? 0 : 1;
}
