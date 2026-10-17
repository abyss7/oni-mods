// Test of the module on plain Linux, without the game: the scalar and vector versions must give the same result.
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <vector>

#include "oni_native.cpp"
#include "oni_textures.cpp"

// Texture filling is compared against a direct transcription of the game logic: per cell, with a byte index, as in TextureRegion.SetBytes.
static bool TestTextures()
{
	const int width = 331, height = 257, cells = width * height, elements = 40;
	std::vector<uint16_t> elementIdx(cells), backwall(cells);
	std::vector<float> mass(cells), damage(cells);
	std::vector<uint8_t> properties(cells), worldIdx(cells), visible(cells), buildMasks(cells), state(elements);
	for (int i = 0; i < elements; i++) state[i] = (uint8_t)(rand() % 4);
	for (int i = 0; i < cells; i++)
	{
		elementIdx[i] = (uint16_t)(rand() % elements);
		backwall[i] = (rand() % 3 == 0) ? 0xFFFF : (uint16_t)(rand() % elements);
		mass[i] = (rand() % 6 == 0) ? 0.0f : (rand() % 7 == 0) ? 0.001f : (float)(rand() % 400000) / 100.0f;
		damage[i] = (float)(rand() % 1001) / 1000.0f;
		properties[i] = (uint8_t)(rand() % 256);
		worldIdx[i] = (uint8_t)(rand() % 3);
		visible[i] = (uint8_t)(rand() % 256);
		buildMasks[i] = (uint8_t)(rand() % 256);
	}
	OniGridView g = { width, height, 1, elements, elementIdx.data(), mass.data(), properties.data(), backwall.data(),
		worldIdx.data(), visible.data(), buildMasks.data(), damage.data(), state.data() };

	const int x0 = 17, y0 = 9, x1 = 300, y1 = 250, pageWidth = 512, pageHeight = 256, clampRow = 200;
	bool ok = true;
	const int kinds[] = { ONI_TEX_FOG_OF_WAR, ONI_TEX_SOLID_DIG_AMOUNT, ONI_TEX_SOLID_LIQUID_GAS_MASS, ONI_TEX_SOLID_LIQUID_GAS_MASS };
	const int bpps[] = { 1, 3, 4, 4 };
	const int params[] = { clampRow, 0, 1, 0 };
	for (int k = 0; k < 4; k++)
	{
		const int bpp = bpps[k];
		std::vector<uint8_t> expected(pageWidth * pageHeight * bpp, 0xAB), actual(expected);
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				const int cell = x + y * width;
				uint8_t* out = &expected[((x - x0) + (y - y0) * pageWidth) * bpp];
				const bool active = worldIdx[cell] == g.activeWorld;
				if (kinds[k] == ONI_TEX_FOG_OF_WAR)
				{
					const int other = x + clampRow * width;
					out[0] = active ? visible[cell] : (other < cells ? visible[other] : 0);
				}
				else if (kinds[k] == ONI_TEX_SOLID_DIG_AMOUNT)
				{
					const bool solid = buildMasks[cell] & 1;
					out[0] = solid ? 255 : 0;
					out[1] = solid ? (uint8_t)(255.0f * damage[cell]) : 0;
					out[2] = (backwall[cell] != 0xFFFF && state[backwall[cell]] == 3) ? 255 : 0;
				}
				else if (!active)
				{
					out[0] = out[1] = out[2] = out[3] = 0;
				}
				else
				{
					const uint8_t st = state[elementIdx[cell]];
					const bool impermeable = params[k] && (properties[cell] & 2);
					uint8_t b = 0, b2 = 0, b3 = 0;
					if (st == 3 || impermeable)
					{
						if (st == 3) b = 255;
						if (impermeable) { b = 250; if (st == 1 || st == 0) b = 230; }
					}
					else if (st == 2) b2 = 255;
					else b3 = 255;
					float amount = std::fmin(1.0f, mass[cell] / 2000.0f);
					if (mass[cell] > 0.0f) amount = std::fmax(0.003921569f, amount);
					out[0] = b; out[1] = b2; out[2] = b3; out[3] = (uint8_t)(amount * 255.0f);
				}
			}
		}
		OniTextureTarget t = { actual.data(), (int32_t)actual.size(), pageWidth, x0, y0, x0, y0, x1, y1 };
		const int result = oni_tex_fill(kinds[k], &g, &t, params[k]);
		const bool same = result == ONI_OK && actual == expected;
		printf("texture kind %d param %d: %s\n", kinds[k], params[k], same ? "matches" : "DIFFERS");
		ok = ok && same;
	}
	// A range that does not fit into the buffer or the grid must be rejected without writing.
	std::vector<uint8_t> small(64, 0xCD);
	OniTextureTarget tooSmall = { small.data(), (int32_t)small.size(), pageWidth, x0, y0, x0, y0, x1, y1 };
	OniTextureTarget outside = { small.data(), 1 << 30, pageWidth, x0, y0, x0, y0, width, y1 };
	const bool rejected = oni_tex_fill(ONI_TEX_FOG_OF_WAR, &g, &tooSmall, 0) == ONI_OUT_OF_BOUNDS
		&& oni_tex_fill(ONI_TEX_FOG_OF_WAR, &g, &outside, 0) == ONI_OUT_OF_BOUNDS
		&& oni_tex_fill(99, &g, &tooSmall, 0) == ONI_UNKNOWN_KIND
		&& small == std::vector<uint8_t>(64, 0xCD);
	printf("texture bounds checks: %s\n", rejected ? "reject bad ranges" : "FAILED");

	// Time per call for a screen-sized area at far zoom (284 x 242 cells).
	// Data for the measurement is coherent, as in the game: neighbouring cells are usually of the same world and the same element.
	for (int i = 0; i < cells; i++)
	{
		worldIdx[i] = (i / width) < 230 ? 1 : 2;
		elementIdx[i] = (uint16_t)((i / 48) % elements);
		backwall[i] = ((i / 96) % 3 == 0) ? 0xFFFF : (uint16_t)((i / 96) % elements);
		mass[i] = ((i / 48) % 5 == 0) ? 0.0f : 300.0f + (float)(i % 1000);
		buildMasks[i] = (uint8_t)((i / 48) % 2);
	}
	std::vector<uint8_t> page(pageWidth * pageHeight * 4);
	OniTextureTarget t = { page.data(), (int32_t)page.size(), pageWidth, x0, y0, x0, y0, x1, y1 };
	typedef int32_t (*FillFn)(int32_t, const OniGridView*, const OniTextureTarget*, int32_t);
	FillFn volatile fill = oni_tex_fill;
	for (int k = 0; k < 3; k++)
	{
		auto a = std::chrono::steady_clock::now();
		for (int r = 0; r < 2000; r++) fill(kinds[k], &g, &t, params[k]);
		auto b = std::chrono::steady_clock::now();
		printf("texture kind %d: %.1f us per call\n", kinds[k], std::chrono::duration<double, std::micro>(b - a).count() / 2000);
	}
	return ok && rejected;
}

int main()
{
	const int count = 198292 + 5; // grid size of a heavy colony plus a tail that is not a multiple of eight
	std::vector<float> mass(count), temperature(count);
	std::vector<uint16_t> element(count);
	srand(12345);
	for (int i = 0; i < count; i++)
	{
		mass[i] = (rand() % 5 == 0) ? 0.0f : (float)(rand() % 200000) / 100.0f;
		temperature[i] = 20.0f + (float)(rand() % 300000) / 100.0f;
		element[i] = (uint16_t)(rand() % 40);
	}

	GridStats a, b;
	oni_grid_stats_scalar(mass.data(), temperature.data(), count, 350.0f, &a);
	oni_grid_stats_avx(mass.data(), temperature.data(), count, 350.0f, &b);
	int reference = 0;
	for (int i = 0; i < count; i++) reference += element[i] == 7;
	int counted = oni_count_element(element.data(), count, 7);

	bool ok = std::fabs(a.totalMass - b.totalMass) <= 1e-6 * std::fabs(a.totalMass)
		&& std::fabs(a.heat - b.heat) <= 1e-6 * std::fabs(a.heat)
		&& a.maxTemperature == b.maxTemperature && a.minTemperature == b.minTemperature
		&& a.hotCells == b.hotCells && a.nonEmptyCells == b.nonEmptyCells && reference == counted;
	printf("scalar: mass=%.3f heat=%.3f max=%.2f min=%.2f hot=%d nonEmpty=%d\n", a.totalMass, a.heat, a.maxTemperature, a.minTemperature, a.hotCells, a.nonEmptyCells);
	printf("avx:    mass=%.3f heat=%.3f max=%.2f min=%.2f hot=%d nonEmpty=%d\n", b.totalMass, b.heat, b.maxTemperature, b.minTemperature, b.hotCells, b.nonEmptyCells);
	printf("element count: reference=%d native=%d\n", reference, counted);

	// Calls through volatile pointers: otherwise the compiler sees that the result does not change and throws the loop away.
	typedef void (*StatsFn)(const float*, const float*, int32_t, float, GridStats*);
	StatsFn volatile scalarFn = oni_grid_stats_scalar;
	StatsFn volatile avxFn = oni_grid_stats_avx;
	const int reps = 500;
	auto t0 = std::chrono::steady_clock::now();
	for (int r = 0; r < reps; r++) scalarFn(mass.data(), temperature.data(), count, 350.0f, &a);
	auto t1 = std::chrono::steady_clock::now();
	for (int r = 0; r < reps; r++) avxFn(mass.data(), temperature.data(), count, 350.0f, &b);
	auto t2 = std::chrono::steady_clock::now();
	printf("per call: scalar %.1f us, avx %.1f us\n",
		std::chrono::duration<double, std::micro>(t1 - t0).count() / reps,
		std::chrono::duration<double, std::micro>(t2 - t1).count() / reps);
	// The texture kernels with each variant: both must match the game's logic byte for byte.
	oni_native_limit_variant(0);
	printf("textures, SSE2:\n");
	ok = TestTextures() && ok;
	if (OniHasAvx())
	{
		oni_native_limit_variant(1);
		printf("textures, AVX:\n");
		ok = TestTextures() && ok;
	}
	printf("%s\n", ok ? "SELFTEST OK" : "SELFTEST FAILED");
	return ok ? 0 : 1;
}
