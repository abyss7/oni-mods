// Check of the mixture layer kernel (sim/plugins/gas_mix_kernel.h) without the game: the same column of 10 cells as in the model
// column_fall.py. Prints gas masses per cell at given ticks; kernel_check.py compares them with the model.
// Along the way it checks mass conservation and non-negativity on a large grid and measures the step time.
// With the environment variable GAS_MIX_PLAIN it computes with the plain step variant, without AVX.
#include <chrono>
#include <cstdio>
#include <cstdlib>

#include "../plugins/gas_mix_kernel.h"

// Bit-exact digest of all layer masses: the step variants are compared by it.
static unsigned long long Bits(GasMixKernel& kernel, int gases)
{
	unsigned long long hash = 1469598103934665603ull;
	for (int g = 0; g < gases; g++)
	{
		const unsigned char* bytes = (const unsigned char*)kernel.Mass(g);
		for (size_t i = 0; i < kernel.Cells() * sizeof(float); i++) hash = (hash ^ bytes[i]) * 1099511628211ull;
	}
	return hash;
}

int main(int argc, char** argv)
{
	const std::vector<float> molar = { 1.00794f, 15.9994f, 44.01f }; // H2, O2, CO2: as in the game data and in column.py
	GasMixParams params;
	// GAS_MIX_PLAIN=1: compute with the plain step variant (no AVX), to compare the variants with each other.
	const bool plain = getenv("GAS_MIX_PLAIN") != nullptr;
	{
		GasMixKernel kernel;
		kernel.ForcePlain(plain);
		printf("variant %s\n", kernel.Variant());
	}
	{
		// Column: width 3 (only the middle is open), height 12 (rows 1..10 are open).
		GasMixKernel kernel;
		kernel.Reset(3, 12, molar);
		kernel.ForcePlain(plain);
		const float start[3] = { 0.09f, 1.11f, 0.6f };
		for (int y = 1; y <= 10; y++)
		{
			kernel.Open()[y * 3 + 1] = 1.0f;
			for (int g = 0; g < 3; g++) kernel.Mass(g)[y * 3 + 1] = start[g];
		}
		for (int tick = 0; tick <= 9000; tick++)
		{
			if (tick == 0 || tick == 300 || tick == 3000 || tick == 9000)
			{
				for (int y = 1; y <= 10; y++)
				{
					printf("column %d %d %.7g %.7g %.7g\n", tick, y - 1, kernel.Mass(0)[y * 3 + 1], kernel.Mass(1)[y * 3 + 1], kernel.Mass(2)[y * 3 + 1]);
				}
			}
			kernel.Step(params);
		}
		// Nothing should have leaked through closed cells.
		double leaked = 0;
		for (int g = 0; g < 3; g++) for (int i = 0; i < 36; i++) if (kernel.Open()[i] == 0.0f) leaked += kernel.Mass(g)[i];
		printf("leaked %.9g\n", leaked);
	}
	{
		// Heat: two gases with different heat capacities, cold on top, hot below. The heat total is conserved,
		// temperatures converge to a common one lying between the initial ones.
		const std::vector<float> two = { 32.0f, 44.0f }, heat = { 1.005f, 0.846f };
		GasMixKernel kernel;
		kernel.Reset(3, 12, two, heat);
		kernel.ForcePlain(plain);
		double before = 0;
		for (int y = 1; y <= 10; y++)
		{
			const int i = y * 3 + 1, g = y <= 5 ? 1 : 0;
			kernel.Open()[i] = 1.0f;
			kernel.Mass(g)[i] = 1.8f;
			kernel.Temperature()[i] = y <= 5 ? 350.0f : 290.0f;
			before += 1.8 * heat[g] * kernel.Temperature()[i];
		}
		kernel.Mass(0); kernel.Mass(1);
		for (int tick = 0; tick < 60000; tick++) kernel.Step(params);
		double after = 0; float coldest = 1e9f, hottest = 0;
		for (int y = 1; y <= 10; y++)
		{
			const int i = y * 3 + 1;
			const float t = kernel.Temperature()[i];
			after += (kernel.Mass(0)[i] * heat[0] + kernel.Mass(1)[i] * heat[1]) * t;
			if (t < coldest) coldest = t;
			if (t > hottest) hottest = t;
		}
		printf("heat energy_before %.6f energy_after %.6f coldest %.4f hottest %.4f\n", before, after, coldest, hottest);
	}
	{
		// Colony grid: random gases and walls; mass is conserved, no negative masses.
		const int W = 1114, H = 178, S = argc > 1 ? atoi(argv[1]) : 8;
		std::vector<float> many(S);
		for (int g = 0; g < S; g++) many[g] = 2.0f + (float)((g * 37) % 70);
		GasMixKernel kernel;
		kernel.Reset(W, H, many);
		kernel.ForcePlain(plain);
		srand(11);
		for (int i = 0; i < W * H; i++)
		{
			const int x = i % W, y = i / W;
			kernel.Open()[i] = (x == 0 || y == 0 || x == W - 1 || y == H - 1 || (i / 97) % 7 == 0) ? 0.0f : 1.0f;
		}
		for (int g = 0; g < S; g++)
		{
			float* m = kernel.Mass(g);
			for (int i = 0; i < W * H; i++) m[i] = (kernel.Open()[i] != 0.0f && (i / 53 + g) % S < 3) ? 0.2f + (float)((i * 13 + g * 7) % 100) * 0.02f : 0.0f;
		}
		double before = 0;
		for (int g = 0; g < S; g++) for (int i = 0; i < W * H; i++) before += kernel.Mass(g)[i];
		const int ticks = 200;
		const auto t0 = std::chrono::steady_clock::now();
		for (int tick = 0; tick < ticks; tick++) kernel.Step(params);
		const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / ticks;
		double after = 0, least = 1e30, inWalls = 0;
		for (int g = 0; g < S; g++)
		{
			const float* m = kernel.Mass(g);
			for (int i = 0; i < W * H; i++)
			{
				after += m[i];
				if (m[i] < least) least = m[i];
				if (kernel.Open()[i] == 0.0f) inWalls += m[i];
			}
		}
		printf("grid gases %d mass_before %.6f mass_after %.6f least %.9g in_walls %.9g bits %016llx spread_cells %zu step_ms %.2f\n", S, before, after, least, inWalls, Bits(kernel, S), kernel.SpreadCells(), ms);
	}
	{
		// The same grid, but only regions are computed (like open worlds in the game: about 40% of cells). The step does not
		// touch cells outside the regions: their mass stays bit for bit.
		const int W = 1114, H = 178, S = argc > 1 ? atoi(argv[1]) : 8;
		std::vector<float> many(S);
		for (int g = 0; g < S; g++) many[g] = 2.0f + (float)((g * 37) % 70);
		GasMixKernel kernel;
		kernel.Reset(W, H, many);
		kernel.ForcePlain(plain);
		const std::vector<GasMixRegion> regions = { { 1, 1, 301, 177 }, { 480, 20, 640, 150 }, { 900, 1, 1010, 60 } };
		std::vector<char> inside(W * H, 0);
		size_t active = 0;
		for (const GasMixRegion& r : regions) for (int y = r.minY; y < r.maxY; y++) for (int x = r.minX; x < r.maxX; x++) { inside[y * W + x] = 1; active++; }
		for (int i = 0; i < W * H; i++) kernel.Open()[i] = (inside[i] && (i / 97) % 7 != 0) ? 1.0f : 0.0f;
		for (int g = 0; g < S; g++)
		{
			float* m = kernel.Mass(g);
			for (int i = 0; i < W * H; i++) m[i] = ((i / 97) % 7 != 0 && (i / 53 + g) % S < 3) ? 0.2f + (float)((i * 13 + g * 7) % 100) * 0.02f : 0.0f;
		}
		kernel.SetRegions(regions);
		std::vector<std::vector<float>> start(S);
		double before = 0;
		for (int g = 0; g < S; g++)
		{
			start[g].assign(kernel.Mass(g), kernel.Mass(g) + W * H);
			for (int i = 0; i < W * H; i++) if (inside[i]) before += start[g][i];
		}
		const int ticks = 200;
		const auto t0 = std::chrono::steady_clock::now();
		for (int tick = 0; tick < ticks; tick++) kernel.Step(params);
		const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / ticks;
		double after = 0, least = 1e30;
		long touchedOutside = 0;
		for (int g = 0; g < S; g++)
		{
			const float* m = kernel.Mass(g);
			for (int i = 0; i < W * H; i++)
			{
				if (inside[i]) { after += m[i]; if (m[i] < least) least = m[i]; }
				else if (m[i] != start[g][i]) touchedOutside++;
			}
		}
		printf("regions gases %d active %zu mass_before %.6f mass_after %.6f least %.9g touched_outside %ld bits %016llx spread_cells %zu step_ms %.2f\n", S, active, before, after, least, touchedOutside, Bits(kernel, S), kernel.SpreadCells(), ms);
	}
	{
		// A grid resembling a player's base: the same regions, but about half the cells are open, in 60 x 18 chunks
		// (dug-out areas within rock). Mass is conserved; the step time shows what closed cells cost.
		// With small chunks (12 x 6) skipping closed cells does not change the time: between them there is not a single
		// processor memory line (64 bytes) that could be left untouched.
		const int W = 1114, H = 178, S = argc > 1 ? atoi(argv[1]) : 8;
		std::vector<float> many(S);
		for (int g = 0; g < S; g++) many[g] = 2.0f + (float)((g * 37) % 70);
		GasMixKernel kernel;
		kernel.Reset(W, H, many);
		kernel.ForcePlain(plain);
		const std::vector<GasMixRegion> regions = { { 1, 1, 301, 177 }, { 480, 20, 640, 150 }, { 900, 1, 1010, 60 } };
		std::vector<char> inside(W * H, 0);
		for (const GasMixRegion& r : regions) for (int y = r.minY; y < r.maxY; y++) for (int x = r.minX; x < r.maxX; x++) inside[y * W + x] = 1;
		size_t openCells = 0;
		for (int i = 0; i < W * H; i++)
		{
			const unsigned block = (unsigned)((i % W) / 60) * 2654435761u + (unsigned)((i / W) / 18) * 40503u;
			const bool open = inside[i] && ((block >> 7) % 100) < 46;
			kernel.Open()[i] = open ? 1.0f : 0.0f;
			if (open) openCells++;
		}
		for (int g = 0; g < S; g++)
		{
			float* m = kernel.Mass(g);
			for (int i = 0; i < W * H; i++) m[i] = (kernel.Open()[i] != 0.0f && (i / 53 + g) % S < 3) ? 0.2f + (float)((i * 13 + g * 7) % 100) * 0.02f : 0.0f;
		}
		kernel.SetRegions(regions);
		double before = 0;
		for (int g = 0; g < S; g++) for (int i = 0; i < W * H; i++) before += kernel.Mass(g)[i];
		const int ticks = 200;
		const auto t0 = std::chrono::steady_clock::now();
		for (int tick = 0; tick < ticks; tick++)
		{
			// Every tenth tick one room closes and a neighbouring one opens, as with building and digging:
			// the spans of open cells are rebuilt, and the closed room's gas is removed beforehand.
			if (tick % 10 == 5)
			{
				const int y0 = 18 * (1 + (tick / 10) % 7), x0 = 60 * ((tick / 10) % 4) + 1;
				for (int y = y0; y < y0 + 18; y++) for (int x = x0; x < x0 + 60; x++)
				{
					const int i = y * W + x;
					if (!inside[i]) continue;
					const bool wasOpen = kernel.Open()[i] != 0.0f;
					if (wasOpen) for (int g = 0; g < S; g++) { before -= kernel.Mass(g)[i]; kernel.Mass(g)[i] = 0.0f; }
					kernel.Open()[i] = wasOpen ? 0.0f : 1.0f;
				}
			}
			kernel.Step(params);
		}
		const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / ticks;
		double after = 0, least = 1e30, inWalls = 0;
		for (int g = 0; g < S; g++)
		{
			const float* m = kernel.Mass(g);
			for (int i = 0; i < W * H; i++)
			{
				after += m[i];
				if (m[i] < least) least = m[i];
				if (kernel.Open()[i] == 0.0f) inWalls += m[i];
			}
		}
		printf("colony gases %d open %zu mass_before %.6f mass_after %.6f least %.9g in_walls %.9g bits %016llx step_ms %.2f\n", S, openCells, before, after, least, inWalls, Bits(kernel, S), ms);
	}
	{
		// Passenger (germs). A 12 x 6 room: dense gas with the passenger on the left, thin gas without it on the right. The passenger
		// rides along with the gas flow: its sum is conserved, it does not pass through walls, and the "per kilogram
		// of gas" difference between the halves after pressure evens out is smoothed by diffusion. Without enabling, the step does not touch the field.
		const int W = 14, H = 8;
		const std::vector<float> one = { 15.9994f };
		GasMixKernel kernel;
		kernel.Reset(W, H, one);
		kernel.ForcePlain(plain);
		float* riders = kernel.Passive(0);
		float* idle = kernel.Passive(1);
		double before = 0;
		for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
		{
			const int i = y * W + x;
			kernel.Open()[i] = 1.0f;
			kernel.Mass(0)[i] = x <= 6 ? 3.0f : 0.5f;
			kernel.Temperature()[i] = 300.0f;
			riders[i] = x <= 6 ? 1000.0f : 0.0f;
			idle[i] = 7.0f;
			before += riders[i];
		}
		kernel.UsePassive(0, true);
		for (int tick = 0; tick < 3000; tick++)
		{
			for (int i = 0; i < W * H; i++) kernel.Temperature()[i] = 300.0f;
			kernel.Step(params);
		}
		double after = 0, inWalls = 0, least = 1e30, most = 0, idleChanged = 0;
		for (int i = 0; i < W * H; i++)
		{
			after += riders[i];
			if (kernel.Open()[i] == 0.0f) { inWalls += riders[i]; continue; }
			const double perKilogram = riders[i] / kernel.Mass(0)[i];
			if (perKilogram < least) least = perKilogram;
			if (perKilogram > most) most = perKilogram;
			if (idle[i] != 7.0f) idleChanged++;
		}
		// The same calculation, but the passenger is transferred after the step by a cell list (that is how the plugin carries it): the numbers
		// must match bit for bit.
		GasMixKernel listed;
		listed.Reset(W, H, one);
		listed.ForcePlain(plain);
		std::vector<uint32_t> all;
		float* ridersListed = listed.Passive(0);
		for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
		{
			const int i = y * W + x;
			listed.Open()[i] = 1.0f;
			listed.Mass(0)[i] = x <= 6 ? 3.0f : 0.5f;
			ridersListed[i] = x <= 6 ? 1000.0f : 0.0f;
			all.push_back((uint32_t)i);
		}
		for (int tick = 0; tick < 3000; tick++)
		{
			for (int i = 0; i < W * H; i++) listed.Temperature()[i] = 300.0f;
			listed.Step(params);
			listed.AdvectPassive(0, all.data(), all.size());
		}
		int sparseDifferent = 0;
		for (int i = 0; i < W * H; i++) if (memcmp(&riders[i], &ridersListed[i], sizeof(float)) != 0) sparseDifferent++;
		printf("passive sum_before %.3f sum_after %.3f in_walls %.9g per_kg_least %.4f per_kg_most %.4f idle_changed %.0f listed_different %d\n", before, after, inWalls, least, most, idleChanged, sparseDifferent);
	}
	{
		// Spreading along an obstacle. A room 22 cells wide: a row of Hydrogen under the ceiling, Oxygen below and in the room
		// above the ceiling, one hole in the ceiling in the far corner. Measures how long it takes half the Hydrogen to leave upwards,
		// with and without spreading; mass is conserved.
		const int W = 24, H = 16, ceiling = 8, hole = 21;
		const std::vector<float> two = { 15.9994f, 1.00794f };
		double halfMinutes[2] = { -1, -1 }, drift = 0;
		for (int variant = 0; variant < 2; variant++)
		{
			GasMixKernel kernel;
			kernel.Reset(W, H, two);
			kernel.ForcePlain(plain);
			double hydrogen = 0, before = 0;
			for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
			{
				const int i = y * W + x;
				const bool open = !(x == 0 || y == 0 || x == W - 1 || y == H - 1 || (y == ceiling && x != hole));
				kernel.Open()[i] = open ? 1.0f : 0.0f;
				kernel.Temperature()[i] = 300.0f;
				if (!open) continue;
				kernel.Mass(y == ceiling - 1 ? 1 : 0)[i] = 1.8f;
				before += 1.8;
				if (y == ceiling - 1) hydrogen += 1.8;
			}
			kernel.Mass(0); kernel.Mass(1);
			GasMixParams local = params;
			if (variant == 1) local.spread = 0.0f;
			const int limit = variant == 0 ? 30000 : 60000; // 100 and 200 minutes of game time
			for (int tick = 1; tick <= limit && halfMinutes[variant] < 0; tick++)
			{
				for (int i = 0; i < W * H; i++) kernel.Temperature()[i] = 300.0f;
				kernel.Step(local);
				if (tick % 50) continue;
				double below = 0;
				for (int y = 1; y < ceiling; y++) for (int x = 1; x < W - 1; x++) below += kernel.Mass(1)[y * W + x];
				if (below <= hydrogen * 0.5) halfMinutes[variant] = tick * 0.2 / 60.0;
			}
			double after = 0;
			for (int g = 0; g < 2; g++) for (int i = 0; i < W * H; i++) after += kernel.Mass(g)[i];
			if (variant == 0) drift = (after - before) / before;
		}
		printf("hole half_with_spread_min %.2f half_without_min %.2f mass_drift %.3g\n", halfMinutes[0], halfMinutes[1], drift);
	}
	{
		// A closed room without holes: three gases mixed evenly over the whole room stratify. Spreading
		// only works where there is a difference between horizontal neighbours, and there is none here: stratification
		// by height with and without spreading must match, and horizontally everything stays even.
		const int W = 14, H = 12;
		const std::vector<float> three = { 1.00794f, 15.9994f, 44.01f };
		std::vector<float> profile[2];
		double uneven = 0;
		for (int variant = 0; variant < 2; variant++)
		{
			GasMixKernel kernel;
			kernel.Reset(W, H, three);
			kernel.ForcePlain(plain);
			for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
			{
				const int i = y * W + x;
				kernel.Open()[i] = 1.0f;
				kernel.Mass(0)[i] = 0.09f; kernel.Mass(1)[i] = 1.11f; kernel.Mass(2)[i] = 0.6f;
			}
			GasMixParams local = params;
			if (variant == 1) local.spread = 0.0f;
			for (int tick = 0; tick < 6000; tick++)
			{
				for (int i = 0; i < W * H; i++) kernel.Temperature()[i] = 300.0f;
				kernel.Step(local);
			}
			for (int g = 0; g < 3; g++) for (int y = 1; y < H - 1; y++)
			{
				const float* m = kernel.Mass(g);
				profile[variant].push_back(m[y * W + 1]);
				if (variant == 0) for (int x = 2; x < W - 1; x++)
				{
					const double d = m[y * W + x] - m[y * W + 1];
					if (d > uneven) uneven = d; else if (-d > uneven) uneven = -d;
				}
			}
		}
		double worst = 0;
		for (size_t i = 0; i < profile[0].size(); i++)
		{
			const double d = profile[0][i] - profile[1][i];
			if (d > worst) worst = d; else if (-d > worst) worst = -d;
		}
		printf("room layering_difference %.3g horizontal_unevenness %.3g\n", worst, uneven);
	}
	{
		// Pressure mode (the pressure mod's "by molecules"), with the real molar masses of Hydrogen and Oxygen. A closed room
		// 12 x 6: Hydrogen on the left, 0.5 kg per cell (about 8 times the pressure of the Oxygen at the reference
		// temperature), Oxygen on the right, 1.8 kg per cell. By mass the cells end with equal masses and very different
		// pressures; by pressure the pressures even out (up to the weight of the gas column), and the Hydrogen takes far less
		// mass per cell. Mass is conserved in both modes.
		const std::vector<float> real = { 2.016f, 31.998f, 44.01f };
		const int W = 14, H = 8;
		double spread[2] = { 0, 0 }, drift[2] = { 0, 0 }, hydrogenShare[2] = { 0, 0 };
		unsigned long long bits[2] = { 0, 0 };
		for (int mode = 0; mode < 2; mode++)
		{
			GasMixKernel kernel;
			kernel.Reset(W, H, real);
			kernel.ForcePlain(plain);
			GasMixParams local = params;
			local.byPressure = mode;
			double before = 0;
			for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
			{
				const int i = y * W + x;
				kernel.Open()[i] = 1.0f;
				if (x < W / 2) kernel.Mass(0)[i] = 0.5f; else kernel.Mass(1)[i] = 1.8f;
				before += kernel.Mass(0)[i] + kernel.Mass(1)[i];
			}
			for (int tick = 0; tick < 6000; tick++)
			{
				for (int i = 0; i < W * H; i++) kernel.Temperature()[i] = 293.15f;
				kernel.Step(local);
			}
			double after = 0, least = 1e30, most = 0, hydrogenTop = 0, hydrogen = 0;
			for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
			{
				const int i = y * W + x;
				after += kernel.Mass(0)[i] + kernel.Mass(1)[i];
				const double eq = kernel.Mass(0)[i] * (31.998 / 2.016) + kernel.Mass(1)[i];
				least = eq < least ? eq : least;
				most = eq > most ? eq : most;
				hydrogen += kernel.Mass(0)[i];
				if (y == H - 2) hydrogenTop += kernel.Mass(0)[i];
			}
			spread[mode] = most / least;
			drift[mode] = (after - before) / before;
			hydrogenShare[mode] = hydrogenTop / hydrogen;
			bits[mode] = Bits(kernel, 2);
		}
		printf("pressure spread_by_mass %.4f spread_by_pressure %.4f drift_by_mass %.3g drift_by_pressure %.3g hydrogen_top_by_pressure %.4f bits %016llx %016llx\n",
			spread[0], spread[1], drift[0], drift[1], hydrogenShare[1], bits[0], bits[1]);
	}
	{
		// Pressure mode: convection. A closed room 6 x 12 of Oxygen, 1.8 kg per cell; the bottom half at 400 K, the top at 250 K.
		// By mass the gas does not know warm from cold: heat spreads only with diffusion. By pressure warm gas is lighter:
		// the warm bottom expands upwards and the cold top sinks into it, so the layers mix faster. A cell has one temperature,
		// so a sinking cold portion mixes at once; the room comes to an even temperature rather than warm over cold.
		// Heat is conserved in both modes.
		const std::vector<float> real = { 2.016f, 31.998f, 44.01f };
		const int W = 8, H = 14;
		double top[2] = { 0, 0 }, bottom[2] = { 0, 0 }, heatDrift[2] = { 0, 0 };
		for (int mode = 0; mode < 2; mode++)
		{
			GasMixKernel kernel;
			kernel.Reset(W, H, real);
			kernel.ForcePlain(plain);
			GasMixParams local = params;
			local.byPressure = mode;
			double energyBefore = 0;
			for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
			{
				const int i = y * W + x;
				kernel.Open()[i] = 1.0f;
				kernel.Mass(1)[i] = 1.8f;
				kernel.Temperature()[i] = y < H / 2 ? 400.0f : 250.0f;
				energyBefore += 1.8 * kernel.Temperature()[i];
			}
			for (int tick = 0; tick < 3000; tick++) kernel.Step(local);
			double energyAfter = 0, topSum = 0, topCount = 0, bottomSum = 0, bottomCount = 0;
			for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
			{
				const int i = y * W + x;
				const double t = kernel.Temperature()[i];
				energyAfter += kernel.Mass(1)[i] * t;
				if (y >= H - 3) { topSum += t; topCount++; }
				if (y <= 2) { bottomSum += t; bottomCount++; }
			}
			top[mode] = topSum / topCount;
			bottom[mode] = bottomSum / bottomCount;
			heatDrift[mode] = (energyAfter - energyBefore) / energyBefore;
		}
		printf("convection top_by_mass %.2f bottom_by_mass %.2f top_by_pressure %.2f bottom_by_pressure %.2f heat_drift_by_mass %.3g heat_drift_by_pressure %.3g\n",
			top[0], bottom[0], top[1], bottom[1], heatDrift[0], heatDrift[1]);
	}
	return 0;
}
