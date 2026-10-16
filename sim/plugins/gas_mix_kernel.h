// Step of the gas mixture layer: exchange rules between neighbouring cells (description and tuning of the numbers: sim/model).
// Independent of the game and the simulation: works over its own arrays, so it is checked separately (sim/model/kernel_check).
//
//  1. Pressure: flow between neighbouring cells by the difference in total mass; the mixture flows in the composition
//     of the source cell; no more than one eighth of the source mass.
//  2. Diffusion: each gas moves to where its mass share is lower.
//  3. Gravity: a gas heavier than the mixture in the cell below it (averaged per molecule) partly falls down.
//  4. Spreading along a barrier: a gas that has nowhere to rise or fall can only flow sideways. Gas in a cell
//     under a ceiling (the cell above is closed) flows into the horizontally adjacent cell if it is lighter than that
//     cell's mixture; gas in a cell on a floor (the cell below is closed) flows into the neighbour if it is heavier than its mixture.
//     The share is computed by the same formula as for gravity. This way a layer of Hydrogen under a ceiling drains into a single
//     hole entirely, like water into a drain: Hydrogen at the hole flows into the cell below it (the mixture there is heavier), and
//     pressure pulls Hydrogen from behind into its place. In open space and for gases of equal weight the rule is silent.
//
// Rules 1 and 2 are computed in one pass over the state at the start of the step: from the total mass of the cells the
// edge coefficients, shared by all gases, are found, and then each gas is updated separately. The flow through an edge
// is computed by the same expression for both cells, so mass is conserved up to rounding.
// Rules 3 and 4 are computed in a second pass over the result of the first, both over the same state.
//
// Heat. A cell has one temperature for the whole mixture. Gas carries its heat with it: the cell's heat content is
// the sum of "gas mass x specific heat capacity x temperature", a portion of gas takes its share of that content, and the new
// cell temperature is the new heat content divided by the new sum of "mass x heat capacity". In the pressure and
// diffusion pass the share of departing mass is the same for all gases of a cell, so heat is carried as one more
// "gas" with the same coefficients; in the gravity and spreading pass the shares differ per gas, and heat is summed over gases.
//
// Passengers. Besides gases, the step can carry quantities that travel with the mixture without affecting it (germs):
// the field Passive(k) is carried by the pressure and diffusion pass with the same coefficients as heat, that is, it leaves
// a cell with the same share as the cell's mass. The gravity and spreading pass does not move passengers: in it gases
// swap places rather than all flowing together.
//
// Regions. The step does not compute the whole grid, only the given rectangles (SetRegions): the game's simulation also
// computes not everything but the open worlds. Cells outside the regions are neither read nor changed by the step; gas does not flow
// across a region's border, because the neighbouring cell outside must be closed (Open() == 0).
//
// Open cells. Inside regions the step does not walk all cells, only spans of open ones (BuildSpans): rock
// and liquid hold no gas, and in a player's base such cells are more than half. Close spans are merged, so the closed
// cells between them are computed too: for them everything comes out zero, as before. The passes read a cell's neighbours,
// which may lie outside the spans; such neighbours are always closed, and everything read from them (edge
// coefficients, downward flows) must be zero. Therefore these arrays are zeroed for cells that dropped out of the spans.
//
// Processor. The library is built for any x86-64, and the step in it comes in two variants: the plain one (SSE2) and one for
// processors with AVX. The variant is chosen at every step from the processor itself (HasAvx). Both are built from the same
// source: everything the step calls is marked GAS_MIX_INLINE and is inlined into each variant entirely. The numbers of the variants
// must match bit for bit (sim/model/kernel_check.py verifies this): only the vector width differs.
#pragma once

#include <stddef.h>
#include <stdint.h>
#include <string.h>

#include <vector>

#include <cpuid.h>
#include <xmmintrin.h>

#define GAS_MIX_INLINE __attribute__((always_inline)) inline

struct GasMixParams
{
	float pressure = 0.2f;    // share of the mass difference that flows per step (limit for computing from the old state)
	float diffusion = 0.004f; // mixing strength
	float gravity = 0.02f;    // gravity strength
	float spread = 0.1f;      // strength of spreading along ceilings and floors
};

// Rectangle of cells: the right and top borders are not included.
struct GasMixRegion
{
	int32_t minX, minY, maxX, maxY;
	bool operator==(const GasMixRegion& other) const { return minX == other.minX && minY == other.minY && maxX == other.maxX && maxY == other.maxY; }
};

// Per-cell array. All kernel arrays have the same size, and the system hands out their memory at the same offset
// within a page; then a write to one array and a read from another at the same cell index are taken by the processor
// as accesses to the same address, and it holds back the read (the low 12 address bits match). Therefore each
// array gets its own start offset.
class GasMixField
{
public:
	void assign(size_t count, float value)
	{
		static size_t created = 0;
		if (storage_.empty()) shift_ = (created++ % 25) * 40; // 160 bytes each, within a single page
		storage_.assign(count + shift_, value);
	}
	bool empty() const { return storage_.empty(); }
	float* data() { return storage_.data() + shift_; }
	const float* data() const { return storage_.data() + shift_; }

private:
	std::vector<float> storage_;
	size_t shift_ = 0;
};

class GasMixKernel
{
public:
	// molar: molar mass of each gas, heat: its specific heat capacity; gases are numbered consecutively from 0.
	void Reset(int32_t width, int32_t height, const std::vector<float>& molar, const std::vector<float>& heat = std::vector<float>())
	{
		width_ = width;
		height_ = height;
		cells_ = (size_t)width * height;
		molar_ = molar;
		heat_ = heat;
		heat_.resize(molar.size(), 1.0f);
		mass_.assign(molar.size(), GasMixField());
		present_.clear();
		// Arrays have one spare row on each side: neighbours above and below are read without edge checks.
		open_.assign(cells_ + 2 * width_, 0.0f);
		for (GasMixField* field : { &total_, &invTotal_, &heavy_, &aH_, &bH_, &aV_, &bV_, &down_, &side_, &next_, &temperature_, &capacity_, &energy_, &energyDown_, &energySide_ })
		{
			field->assign(cells_ + 2 * width_, 0.0f);
		}
		for (int index = 0; index < MaxPassive; index++)
		{
			passive_[index] = GasMixField();
			passiveUsed_[index] = false;
		}
		runs_.clear();
		spans_.clear();
		rows_.clear();
		for (int32_t y = 0; y < height_; y++) rows_.push_back(Span{ (ptrdiff_t)y * width_, (ptrdiff_t)(y + 1) * width_ });
	}

	// Which cells to compute: the whole grid by default. Regions may overlap: a cell is computed once.
	void SetRegions(const std::vector<GasMixRegion>& regions)
	{
		std::vector<uint8_t> inside(cells_, 0);
		for (const GasMixRegion& region : regions)
		{
			const int32_t minX = region.minX < 0 ? 0 : region.minX, maxX = region.maxX > width_ ? width_ : region.maxX;
			const int32_t minY = region.minY < 0 ? 0 : region.minY, maxY = region.maxY > height_ ? height_ : region.maxY;
			if (minX >= maxX) continue;
			for (int32_t y = minY; y < maxY; y++) memset(&inside[(size_t)y * width_ + minX], 1, (size_t)(maxX - minX));
		}
		// Spans per row: consecutive region cells within one row.
		rows_.clear();
		for (int32_t y = 0; y < height_; y++)
		{
			const ptrdiff_t row = (ptrdiff_t)y * width_;
			for (int32_t x = 0; x < width_; x++)
			{
				if (!inside[row + x]) continue;
				const int32_t from = x;
				while (x < width_ && inside[row + x]) x++;
				rows_.push_back(Span{ row + from, row + x });
			}
		}
		// Working arrays outside the regions must be zero: cells at a region's edge read their neighbours from there.
		runs_.clear();
		spans_.clear();
		for (GasMixField* field : { &total_, &invTotal_, &heavy_, &aH_, &bH_, &aV_, &bV_, &down_, &side_, &next_, &capacity_, &energy_, &energyDown_, &energySide_ })
		{
			field->assign(cells_ + 2 * width_, 0.0f);
		}
	}

	int32_t Width() const { return width_; }
	int32_t Height() const { return height_; }
	size_t Cells() const { return cells_; }
	const std::vector<int32_t>& Present() const { return present_; }

	// Per-cell array of gas mass; created on first access.
	float* Mass(int32_t gas)
	{
		GasMixField& field = mass_[gas];
		if (field.empty())
		{
			field.assign(cells_ + 2 * width_, 0.0f);
			present_.push_back(gas);
		}
		return field.data() + width_;
	}

	// 1 for a cell gas can flow through (gas or vacuum), 0 for the rest (liquid, solid, world edge).
	float* Open() { return open_.data() + width_; }

	// Per-cell field of a passenger; created on first access. Carried by the step only while enabled (UsePassive).
	static const int MaxPassive = 8;
	float* Passive(int index)
	{
		if (passive_[index].empty()) passive_[index].assign(cells_ + 2 * width_, 0.0f);
		return passive_[index].data() + width_;
	}
	bool HasPassive(int index) const { return !passive_[index].empty(); }
	void UsePassive(int index, bool use) { passiveUsed_[index] = use; }

	// The same passenger transport the step does, but only over a list of cells and after the step (the edge coefficients
	// from the step remain). For a sparse passenger: the list must contain every cell where it is present,
	// and their neighbours; in the other cells the field does not change. The numbers are the same as for transport inside the step.
	void AdvectPassive(int index, const uint32_t* cells, size_t count)
	{
		const ptrdiff_t w = width_;
		float* field = Passive(index);
		const float* ah = aH_.data() + w;
		const float* bh = bH_.data() + w;
		const float* av = aV_.data() + w;
		const float* bv = bV_.data() + w;
		sparse_.resize(count);
		for (size_t k = 0; k < count; k++)
		{
			const ptrdiff_t i = (ptrdiff_t)cells[k];
			const float right = ah[i] * field[i] - bh[i] * field[i + 1];
			const float left = ah[i - 1] * field[i - 1] - bh[i - 1] * field[i];
			const float up = av[i] * field[i] - bv[i] * field[i + w];
			const float below = av[i - w] * field[i - w] - bv[i - w] * field[i];
			sparse_[k] = field[i] - right + left - up + below;
		}
		for (size_t k = 0; k < count; k++) field[cells[k]] = sparse_[k];
	}

	// Calls fn(first cell, cell past the last) for each row span of the regions (all their cells,
	// open and closed).
	template <class Fn> void ForSpans(Fn&& fn) const
	{
		for (const Span& span : rows_) fn((size_t)span.first, (size_t)span.last);
	}

	// Cell temperature: before the step the game's temperature is put here, after the step the new one is here.
	// A cell without gas has temperature 0 after the step.
	float* Temperature() { return temperature_.data() + width_; }

	// Total cell mass after the last Totals() (after a step this is the mass before the gravity pass).
	const float* Total() const { return total_.data() + width_; }

	void Totals()
	{
		BuildSpans();
		Sum();
	}

	// Whether the processor has AVX and whether the system saves its registers on thread switches.
	static bool HasAvx()
	{
		static const bool has = []
		{
			unsigned int a = 0, b = 0, c = 0, d = 0;
			if (!__get_cpuid(1, &a, &b, &c, &d)) return false;
			if ((c & (1u << 27)) == 0 || (c & (1u << 28)) == 0) return false; // OSXSAVE and AVX
			unsigned int low = 0, high = 0;
			__asm__("xgetbv" : "=a"(low), "=d"(high) : "c"(0));
			return (low & 6) == 6; // XMM and YMM registers
		}();
		return has;
	}

	// For checks: compute with the plain variant of the step even if the processor has AVX.
	void ForcePlain(bool plain) { plain_ = plain; }

	// In how many cells the last step computed spreading (the other region cells do not need it).
	size_t SpreadCells() const { return spreadCells_; }

	// How many region cells were open to gas on the last step.
	size_t OpenCells() const { return openCells_; }

	// Which variant computes the step.
	const char* Variant() const { return !plain_ && HasAvx() ? "avx" : "sse2"; }

	void Step(const GasMixParams& params)
	{
		if (!plain_ && HasAvx()) StepAvx(params);
		else StepPlain(params);
	}

private:
	__attribute__((target("avx"), noinline)) void StepAvx(const GasMixParams& params) { StepBody(params); }
	__attribute__((noinline)) void StepPlain(const GasMixParams& params) { StepBody(params); }

	GAS_MIX_INLINE void StepBody(const GasMixParams& params)
	{
		// The tails of spreading gas reach denormal numbers, and arithmetic on them is orders of magnitude slower.
		// For the duration of the step the processor treats such numbers as zero; the mode is per thread and is restored after the step.
		const unsigned int savedMode = _mm_getcsr();
		_mm_setcsr(savedMode | 0x8040); // FTZ and DAZ
		BuildSpans();
		Sum();
		Edges(1, params, aH_, bH_);
		Edges(width_, params, aV_, bV_);
		// The right cell of a row and the left cell of the next one are adjacent in the array, but not in the world.
		for (int32_t y = 0; y < height_; y++)
		{
			const size_t last = (size_t)width_ + (size_t)y * width_ + width_ - 1;
			aH_.data()[last] = bH_.data()[last] = 0.0f;
		}
		// The cell's heat content is carried by the same coefficients as the gas masses.
		HeatToEnergy();
		FlowAndDiffusion(energy_);
		for (int index = 0; index < MaxPassive; index++) if (passiveUsed_[index] && !passive_[index].empty()) FlowAndDiffusion(passive_[index]);
		for (int32_t gas : present_) FlowAndDiffusion(mass_[gas]);
		Sum();
		EnergyToHeat();
		for (const Span& span : spans_) memset(energyDown_.data() + width_ + span.first, 0, (size_t)(span.last - span.first) * sizeof(float));
		FindBarriers();
		for (int32_t gas : present_) FallAndSpread(mass_[gas], molar_[gas], heat_[gas], params.gravity, params.spread);
		// Heat that fell and flowed sideways together with gas.
		{
			const ptrdiff_t w = width_;
			float* __restrict energy = energy_.data() + w;
			const float* __restrict fell = energyDown_.data() + w;
			const float* __restrict side = energySide_.data() + w;
			for (const Span& span : spans_)
			{
				for (ptrdiff_t i = span.first; i < span.last; i++) energy[i] = energy[i] - fell[i] + fell[i + w] - side[i] + side[i - 1];
			}
		}
		// Cell heat capacity after falling is already corrected in Fall(), there is no need to recompute the sums.
		EnergyToHeat();
		_mm_setcsr(savedMode);
	}

	// A row span of cells that the step computes: indices of the first cell and the cell past the last.
	struct Span
	{
		ptrdiff_t first, last;
	};

	// Total mass, heat capacity, inverse mass and per-molecule average weight of each cell's mixture.
	// The sums over gases are accumulated in registers, four cells at a time, and written to memory once: a
	// "gas after gas" pass accumulating into arrays is memory-bound and comes out several times slower.
	GAS_MIX_INLINE void Sum()
	{
		const size_t gases = present_.size();
		sumMass_.resize(gases);
		sumPerMolar_.resize(gases);
		sumHeat_.resize(gases);
		for (size_t g = 0; g < gases; g++)
		{
			sumMass_[g] = mass_[present_[g]].data() + width_;
			sumPerMolar_[g] = 1.0f / molar_[present_[g]];
			sumHeat_[g] = heat_[present_[g]];
		}
		for (const Span& span : spans_)
		{
			ptrdiff_t i = span.first;
			for (; i + 4 <= span.last; i += 4) SumFour(i);
			if (i == span.last) continue;
			// Span tail: the last group of four is computed with overlap, and a very short span cell by cell.
			if (span.last - span.first >= 4) SumFour(span.last - 4);
			else for (; i < span.last; i++) SumOne(i);
		}
	}

	GAS_MIX_INLINE void SumFour(ptrdiff_t i)
	{
		__m128 total = _mm_setzero_ps(), moles = _mm_setzero_ps(), capacity = _mm_setzero_ps();
		const size_t gases = sumMass_.size();
		for (size_t g = 0; g < gases; g++)
		{
			const __m128 m = _mm_loadu_ps(sumMass_[g] + i);
			total = _mm_add_ps(total, m);
			moles = _mm_add_ps(moles, _mm_mul_ps(m, _mm_set1_ps(sumPerMolar_[g])));
			capacity = _mm_add_ps(capacity, _mm_mul_ps(m, _mm_set1_ps(sumHeat_[g])));
		}
		const __m128 zero = _mm_setzero_ps();
		_mm_storeu_ps(total_.data() + width_ + i, total);
		_mm_storeu_ps(capacity_.data() + width_ + i, capacity);
		_mm_storeu_ps(invTotal_.data() + width_ + i, _mm_and_ps(_mm_cmpgt_ps(total, zero), _mm_div_ps(_mm_set1_ps(1.0f), total)));
		_mm_storeu_ps(heavy_.data() + width_ + i, _mm_and_ps(_mm_cmpgt_ps(moles, zero), _mm_div_ps(total, moles)));
	}

	GAS_MIX_INLINE void SumOne(ptrdiff_t i)
	{
		float total = 0.0f, moles = 0.0f, capacity = 0.0f;
		for (size_t g = 0; g < sumMass_.size(); g++)
		{
			const float m = sumMass_[g][i];
			total += m;
			moles += m * sumPerMolar_[g];
			capacity += m * sumHeat_[g];
		}
		(total_.data() + width_)[i] = total;
		(capacity_.data() + width_)[i] = capacity;
		(invTotal_.data() + width_)[i] = total > 0.0f ? 1.0f / total : 0.0f;
		(heavy_.data() + width_)[i] = moles > 0.0f ? total / moles : 0.0f;
	}

	GAS_MIX_INLINE void HeatToEnergy()
	{
		const float* __restrict t = temperature_.data() + width_;
		const float* __restrict c = capacity_.data() + width_;
		float* __restrict e = energy_.data() + width_;
		for (const Span& span : spans_)
		{
			for (ptrdiff_t i = span.first; i < span.last; i++) e[i] = c[i] * t[i];
		}
	}

	GAS_MIX_INLINE void EnergyToHeat()
	{
		float* __restrict t = temperature_.data() + width_;
		const float* __restrict c = capacity_.data() + width_;
		const float* __restrict e = energy_.data() + width_;
		for (const Span& span : spans_)
		{
			for (ptrdiff_t i = span.first; i < span.last; i++) t[i] = c[i] > 0.0f ? e[i] / c[i] : 0.0f;
		}
	}

	// Coefficients on the edge between a cell and its neighbour at offset step (1: right, width: above):
	// a: what share of the gas mass leaves the cell for the neighbour, b: what share of the neighbour's gas mass comes from it.
	GAS_MIX_INLINE void Edges(int32_t step, const GasMixParams& params, GasMixField& a, GasMixField& b)
	{
		const float* __restrict total = total_.data() + width_;
		const float* __restrict inv = invTotal_.data() + width_;
		const float* __restrict open = open_.data() + width_;
		float* __restrict pa = a.data() + width_;
		float* __restrict pb = b.data() + width_;
		const float pressure = params.pressure, diffusion = params.diffusion;
		// The neighbour of the last grid cells lies in the spare row: it is closed, and the coefficients come out zero.
		for (const Span& span : spans_)
		{
			for (ptrdiff_t i = span.first; i < span.last; i++)
			{
				const float here = total[i], there = total[i + step];
				float flow = pressure * (here - there);
				flow = flow > here * 0.125f ? here * 0.125f : flow;
				flow = flow < -there * 0.125f ? -there * 0.125f : flow;
				const float mix = diffusion * (here < there ? here : there);
				const float both = open[i] * open[i + step];
				pa[i] = both * ((flow > 0.0f ? flow : 0.0f) + mix) * inv[i];
				pb[i] = both * ((flow < 0.0f ? -flow : 0.0f) + mix) * inv[i + step];
			}
		}
	}

	GAS_MIX_INLINE void FlowAndDiffusion(GasMixField& field)
	{
		const ptrdiff_t w = width_;
		float* m = field.data() + w;
		float* out = next_.data() + w;
		const float* __restrict ah = aH_.data() + w;
		const float* __restrict bh = bH_.data() + w;
		const float* __restrict av = aV_.data() + w;
		const float* __restrict bv = bV_.data() + w;
		// Cell 0 reads its left neighbour at index -1: it lies in the spare row, its coefficients are zero.
		// Same with neighbours outside a region: their coefficients are zero because they are not computed outside regions.
		for (const Span& span : spans_)
		{
			const float* __restrict from = m;
			float* __restrict to = out;
			for (ptrdiff_t i = span.first; i < span.last; i++)
			{
				const float right = ah[i] * from[i] - bh[i] * from[i + 1];
				const float left = ah[i - 1] * from[i - 1] - bh[i - 1] * from[i];
				const float up = av[i] * from[i] - bv[i] * from[i + w];
				const float below = av[i - w] * from[i - w] - bv[i - w] * from[i];
				to[i] = from[i] - right + left - up + below;
			}
		}
		// The result is copied back into the gas array span by span: cells outside the regions stay as they were.
		for (const Span& span : spans_) memcpy(m + span.first, out + span.first, (size_t)(span.last - span.first) * sizeof(float));
	}

	// Spans of open cells inside the regions: only these are walked by the step. Spans closer than gap cells are merged.
	// Cells that were in the previous step's spans but did not get into the new ones lose their edge coefficients and
	// downward flows: they are read by neighbours that stayed in the spans, and there must be zero there.
	void BuildSpans()
	{
		const float* open = open_.data() + width_;
		const ptrdiff_t gap = 8;
		fresh_.clear();
		for (const Span& row : rows_)
		{
			ptrdiff_t first = -1, last = 0;
			for (ptrdiff_t i = row.first; i < row.last; i++)
			{
				if (open[i] == 0.0f) continue;
				if (first >= 0 && i - last <= gap)
				{
					last = i + 1;
					continue;
				}
				if (first >= 0) fresh_.push_back(Span{ first, last });
				first = i;
				last = i + 1;
			}
			if (first >= 0) fresh_.push_back(Span{ first, last });
		}
		if (fresh_.size() == spans_.size() && (fresh_.empty() || memcmp(fresh_.data(), spans_.data(), fresh_.size() * sizeof(Span)) == 0)) return;
		// Zeroing the parts of old spans not covered by new ones (both lists go in increasing cell index order).
		size_t next = 0;
		for (const Span& old : spans_)
		{
			ptrdiff_t from = old.first;
			while (next < fresh_.size() && fresh_[next].last <= from) next++;
			for (size_t k = next; from < old.last; k++)
			{
				const ptrdiff_t to = k < fresh_.size() && fresh_[k].first < old.last ? fresh_[k].first : old.last;
				if (to > from)
				{
					for (GasMixField* field : { &aH_, &bH_, &aV_, &bV_, &down_, &energyDown_ })
					{
						memset(field->data() + width_ + from, 0, (size_t)(to - from) * sizeof(float));
					}
				}
				if (k >= fresh_.size() || fresh_[k].first >= old.last) break;
				from = fresh_[k].last;
			}
		}
		spans_.swap(fresh_);
	}

	// Where to compute spreading. The rule works only in a cell whose right neighbour is open and where the cell itself or
	// that neighbour has a ceiling or a floor; in all other cells the sideways flow is zero for any gases.
	// Once per step such cells are collected into spans (runs_), and sideways flows are computed only over them, not over all
	// region cells. Close spans are merged: the extra cells give the same zeros, but the pass goes in long chunks.
	// The side and energySide arrays must be zero outside the spans: passes over all cells read them. Therefore
	// the old spans are zeroed before searching for new ones.
	GAS_MIX_INLINE void FindBarriers()
	{
		const ptrdiff_t w = width_;
		const float* __restrict open = open_.data() + w;
		for (const Span& run : runs_)
		{
			memset(side_.data() + w + run.first, 0, (size_t)(run.last - run.first) * sizeof(float));
			memset(energySide_.data() + w + run.first, 0, (size_t)(run.last - run.first) * sizeof(float));
		}
		runs_.clear();
		runStart_.clear();
		spreadCells_ = openCells_ = 0;
		const ptrdiff_t gap = 16; // spans closer than this are merged
		for (const Span& span : spans_)
		{
			runStart_.push_back(runs_.size());
			ptrdiff_t first = -1, last = 0;
			for (ptrdiff_t i = span.first; i < span.last; i++)
			{
				// Closed cells come in long chunks and are filtered out by the very first check.
				if (open[i] == 0.0f) continue;
				openCells_++;
				if (open[i + 1] == 0.0f) continue;
				// How many closed cells are above and below this cell and its right neighbour.
				const float barriers = 4.0f - open[i + w] - open[i - w] - open[i + 1 + w] - open[i + 1 - w];
				if (barriers == 0.0f) continue;
				if (first >= 0 && i - last <= gap)
				{
					last = i + 1;
					continue;
				}
				if (first >= 0)
				{
					runs_.push_back(Span{ first, last });
					spreadCells_ += (size_t)(last - first);
				}
				first = i;
				last = i + 1;
			}
			if (first >= 0)
			{
				runs_.push_back(Span{ first, last });
				spreadCells_ += (size_t)(last - first);
			}
		}
		runStart_.push_back(runs_.size());
	}

	GAS_MIX_INLINE void FallAndSpread(GasMixField& field, float molar, float heat, float gravity, float spread)
	{
		const ptrdiff_t w = width_;
		float* __restrict m = field.data() + w;
		float* __restrict down = down_.data() + w;
		float* __restrict side = side_.data() + w;
		const float* __restrict heavy = heavy_.data() + w;
		const float* __restrict open = open_.data() + w;
		const float* __restrict temperature = temperature_.data() + w;
		float* __restrict energyDown = energyDown_.data() + w;
		float* __restrict energySide = energySide_.data() + w;
		// How much lighter the gas is than the cell's mixture: from -1 (gas much heavier) to 1 (much lighter). Computed once
		// per cell and needed three times: for the cell below (falling) and for the right and left neighbours (spreading).
		// Zeros remain outside regions: everything is closed there anyway.
		float* __restrict lighter = next_.data() + w;
		const auto weigh = [&](const Span& span) __attribute__((always_inline))
		{
			for (ptrdiff_t i = span.first; i < span.last; i++) lighter[i] = (heavy[i] - molar) / (heavy[i] + molar);
		};
		// down: how much gas leaves the cell into the cell below it. Below the bottom grid row is the spare one, and below the bottom
		// row of a region are cells outside it: both are closed, nothing falls there.
		const auto falls = [&](const Span& span) __attribute__((always_inline))
		{
			for (ptrdiff_t i = span.first; i < span.last; i++)
			{
				const float excess = -lighter[i - w];
				float share = gravity * (excess > 0.0f ? excess : 0.0f);
				share = share > 1.0f ? 1.0f : share;
				down[i] = open[i] * open[i - w] * share * m[i];
				energyDown[i] += down[i] * heat * temperature[i];
			}
		};
		// side: how much gas crosses the cell's right edge along a barrier (signed: rightward minus leftward).
		// From a cell under a ceiling gas goes to the neighbour if it is lighter than the neighbour's mixture; from a cell on a floor, if heavier.
		// An empty neighbour has zero "weight": heavy gas flows into it along the floor, light gas under a ceiling does not
		// (pressure will fill the vacuum). Heat leaves with the gas. Computed only over the spreading spans.
		const auto sides = [&](const Span& run) __attribute__((always_inline))
		{
			for (ptrdiff_t i = run.first; i < run.last; i++)
			{
				// From here to the right: by the right neighbour's mixture; from the right to here: by this cell's mixture.
				const float there = lighter[i + 1], here = lighter[i];
				const float out = (1.0f - open[i + w]) * (there > 0.0f ? there : 0.0f) + (1.0f - open[i - w]) * (there < 0.0f ? -there : 0.0f);
				const float in = (1.0f - open[i + 1 + w]) * (here > 0.0f ? here : 0.0f) + (1.0f - open[i + 1 - w]) * (here < 0.0f ? -here : 0.0f);
				const float both = open[i] * open[i + 1] * spread;
				const float right = both * out * m[i], left = both * in * m[i + 1];
				side[i] = right - left;
				energySide[i] += heat * (right * temperature[i] - left * temperature[i + 1]);
			}
		};
		// Above the top row of a region the down array, and outside the spreading spans the side array, are not computed: there are zeros there.
		// Gas carries its heat capacity with it between cells.
		float* __restrict capacity = capacity_.data() + w;
		const auto apply = [&](const Span& span) __attribute__((always_inline))
		{
			for (ptrdiff_t i = span.first; i < span.last; i++)
			{
				const float moved = down[i + w] - down[i] - side[i] + side[i - 1];
				m[i] += moved;
				capacity[i] += moved * heat;
			}
		};
		// The three passes go over the spans as a wave, not each over the whole grid: a row just computed by one
		// pass is still in the processor's near memory when the next one reads it. The order inside the wave is such
		// that each pass sees exactly the same numbers as with separate passes over the whole grid: a span's flows are computed
		// when the weight is known for it and for the next span (the right neighbour of the last cell), and a span's
		// masses change when the flows of all cells above it have been computed.
		const size_t count = spans_.size();
		size_t applied = 0;
		if (count > 0) weigh(spans_[0]);
		for (size_t k = 0; k < count; k++)
		{
			if (k + 1 < count) weigh(spans_[k + 1]);
			falls(spans_[k]);
			for (size_t run = runStart_[k]; run < runStart_[k + 1]; run++) sides(runs_[run]);
			for (; applied <= k && (k + 1 == count || spans_[applied].last + w <= spans_[k + 1].first); applied++) apply(spans_[applied]);
		}
	}

	int32_t width_ = 0, height_ = 0;
	size_t cells_ = 0;
	std::vector<float> molar_, heat_;
	std::vector<GasMixField> mass_;
	std::vector<int32_t> present_;
	GasMixField open_, total_, invTotal_, heavy_, aH_, bH_, aV_, bV_, down_, side_, next_;
	GasMixField temperature_, capacity_, energy_, energyDown_, energySide_;
	GasMixField passive_[MaxPassive];
	std::vector<float> sparse_; // working array of AdvectPassive
	bool passiveUsed_[MaxPassive] = {};
	// rows_: row spans of the regions (all cells); spans_: spans of open cells inside them that the step computes
	// (BuildSpans); fresh_: working list for rebuilding them.
	std::vector<Span> rows_, spans_, fresh_;
	// Spreading spans of the last step (FindBarriers) and the index of the first of them for each span in spans_.
	std::vector<Span> runs_;
	std::vector<size_t> runStart_;
	size_t spreadCells_ = 0, openCells_ = 0;
	bool plain_ = false;
	// Working lists of Sum(): mass arrays and multipliers of the gases present in the layer.
	std::vector<const float*> sumMass_;
	std::vector<float> sumPerMolar_, sumHeat_;
};
