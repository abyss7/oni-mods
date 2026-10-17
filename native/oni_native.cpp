// Trial native module: checks that C++ code can read simulation data directly
// from game memory and compute over it faster than C#.
//
// The game exposes the grid as flat arrays with one entry per cell (mass, temperature, element index).
// The C# mod passes pointers to these arrays here without copying.
//
// Build: scripts/build-native.sh (cross-compiled for Windows x64, the game runs under Proton).
// Built for any x86-64 processor (SSE2); kernels with an AVX variant choose it at run time (oni_cpu.h).

#include <immintrin.h>

#include "oni_cpu.h"
#include "oni_grid.h"

// Layout must match the GridStats struct in NativeProbe.cs.
struct GridStats
{
	double totalMass;      // sum of the masses of all cells, kg
	double heat;           // sum of mass * temperature: for the mass-weighted mean temperature
	float maxTemperature;  // among non-empty cells
	float minTemperature;  // among non-empty cells
	int32_t hotCells;      // non-empty cells hotter than the threshold
	int32_t nonEmptyCells; // cells with mass above zero
};

#define ONI_STRINGIFY2(x) #x
#define ONI_STRINGIFY(x) ONI_STRINGIFY2(x)

ONI_EXPORT int32_t oni_native_abi()
{
	return ONI_NATIVE_ABI;
}

ONI_EXPORT const char* oni_native_info()
{
	return "oni_native abi " ONI_STRINGIFY(ONI_NATIVE_ABI) ", " __VERSION__ ", SSE2 with AVX chosen at run time";
}

// The variant the kernels run on this processor: "avx" or "sse2".
ONI_EXPORT const char* oni_native_variant()
{
	return OniHasAvx() ? "avx" : "sse2";
}

ONI_EXPORT void oni_grid_stats_scalar(const float* mass, const float* temperature, int32_t count, float hotThreshold, GridStats* out)
{
	GridStats stats = { 0.0, 0.0, -1e30f, 1e30f, 0, 0 };
	for (int32_t i = 0; i < count; i++)
	{
		float m = mass[i];
		float t = temperature[i];
		stats.totalMass += m;
		stats.heat += (double)m * (double)t;
		if (m > 0.0f)
		{
			stats.nonEmptyCells++;
			if (t > stats.maxTemperature) stats.maxTemperature = t;
			if (t < stats.minTemperature) stats.minTemperature = t;
			if (t > hotThreshold) stats.hotCells++;
		}
	}
	*out = stats;
}

__attribute__((target("avx"))) static inline double horizontal_sum(__m256d v)
{
	__m128d low = _mm256_castpd256_pd128(v);
	__m128d high = _mm256_extractf128_pd(v, 1);
	__m128d sum = _mm_add_pd(low, high);
	return _mm_cvtsd_f64(_mm_add_sd(sum, _mm_unpackhi_pd(sum, sum)));
}

__attribute__((target("avx"), noinline)) static void GridStatsAvx(const float* mass, const float* temperature, int32_t count, float hotThreshold, GridStats* out)
{
	const __m256 zero = _mm256_setzero_ps();
	const __m256 threshold = _mm256_set1_ps(hotThreshold);
	__m256d massSum = _mm256_setzero_pd();
	__m256d heatSum = _mm256_setzero_pd();
	__m256 maxT = _mm256_set1_ps(-1e30f);
	__m256 minT = _mm256_set1_ps(1e30f);
	int32_t hot = 0;
	int32_t nonEmpty = 0;

	int32_t i = 0;
	for (; i + 8 <= count; i += 8)
	{
		__m256 m = _mm256_loadu_ps(mass + i);
		__m256 t = _mm256_loadu_ps(temperature + i);

		// Sums accumulate in double: float lacks precision over hundreds of thousands of cells.
		__m256d mLow = _mm256_cvtps_pd(_mm256_castps256_ps128(m));
		__m256d mHigh = _mm256_cvtps_pd(_mm256_extractf128_ps(m, 1));
		__m256d tLow = _mm256_cvtps_pd(_mm256_castps256_ps128(t));
		__m256d tHigh = _mm256_cvtps_pd(_mm256_extractf128_ps(t, 1));
		massSum = _mm256_add_pd(massSum, _mm256_add_pd(mLow, mHigh));
		heatSum = _mm256_add_pd(heatSum, _mm256_add_pd(_mm256_mul_pd(mLow, tLow), _mm256_mul_pd(mHigh, tHigh)));

		// Mask of non-empty cells: empty ones do not take part in the minimum, maximum and counters.
		__m256 occupied = _mm256_cmp_ps(m, zero, _CMP_GT_OQ);
		maxT = _mm256_max_ps(maxT, _mm256_blendv_ps(maxT, t, occupied));
		minT = _mm256_min_ps(minT, _mm256_blendv_ps(minT, t, occupied));
		nonEmpty += __builtin_popcount((unsigned)_mm256_movemask_ps(occupied));
		hot += __builtin_popcount((unsigned)_mm256_movemask_ps(_mm256_and_ps(occupied, _mm256_cmp_ps(t, threshold, _CMP_GT_OQ))));
	}

	GridStats stats;
	stats.totalMass = horizontal_sum(massSum);
	stats.heat = horizontal_sum(heatSum);
	float maxLanes[8], minLanes[8];
	_mm256_storeu_ps(maxLanes, maxT);
	_mm256_storeu_ps(minLanes, minT);
	stats.maxTemperature = maxLanes[0];
	stats.minTemperature = minLanes[0];
	for (int lane = 1; lane < 8; lane++)
	{
		if (maxLanes[lane] > stats.maxTemperature) stats.maxTemperature = maxLanes[lane];
		if (minLanes[lane] < stats.minTemperature) stats.minTemperature = minLanes[lane];
	}
	stats.hotCells = hot;
	stats.nonEmptyCells = nonEmpty;

	// Tail that is not a multiple of eight.
	for (; i < count; i++)
	{
		float m = mass[i];
		float t = temperature[i];
		stats.totalMass += m;
		stats.heat += (double)m * (double)t;
		if (m > 0.0f)
		{
			stats.nonEmptyCells++;
			if (t > stats.maxTemperature) stats.maxTemperature = t;
			if (t < stats.minTemperature) stats.minTemperature = t;
			if (t > hotThreshold) stats.hotCells++;
		}
	}
	*out = stats;
}

// The same with AVX, 8 cells at a time; without AVX the scalar one (the sums may differ in the last digits: AVX adds in
// four lanes).
ONI_EXPORT void oni_grid_stats_avx(const float* mass, const float* temperature, int32_t count, float hotThreshold, GridStats* out)
{
	if (OniHasAvx()) GridStatsAvx(mass, temperature, count, hotThreshold, out);
	else oni_grid_stats_scalar(mass, temperature, count, hotThreshold, out);
}

// How many cells are occupied by the given element. Element indices are 16-bit; compared 8 at a time (SSE2).
ONI_EXPORT int32_t oni_count_element(const uint16_t* elementIdx, int32_t count, uint16_t element)
{
	const __m128i wanted = _mm_set1_epi16((short)element);
	int32_t result = 0;
	int32_t i = 0;
	for (; i + 8 <= count; i += 8)
	{
		__m128i equal = _mm_cmpeq_epi16(_mm_loadu_si128((const __m128i*)(elementIdx + i)), wanted);
		// The mask has two bits for each matching 16-bit value.
		result += __builtin_popcount((unsigned)_mm_movemask_epi8(equal)) >> 1;
	}
	for (; i < count; i++)
	{
		if (elementIdx[i] == element) result++;
	}
	return result;
}
