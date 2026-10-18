// Light for the Blackout mode: direct light along rays from each source, and light reflected by surfaces carried
// through the open cells. The engine keeps what each source adds, so a source is added and removed on its own; the
// reflected light is redone only around what changed. Used by the game library (oni_light.cpp), its self-check
// (light_selftest.cpp) and the model (sim/model/light_model.cpp).
//
// Direct light. From a source, light goes along straight rays: each cell takes the light of the cell before it on the
// ray times what that cell lets through (gas 1; a liquid exp(-absorb * mass), absorb from the element; a transparent
// tile WindowThrough; a solid cell 0). A diagonal step between two cells takes the better of the two side cells, so light
// does not slip through a diagonal of blocks. Which cell comes before which is computed once per radius (the ray tree).
// Lux = Lux / max(1, falloff * r), r the straight distance, smoothly faded to 0 between R and 1.5 R. A ceiling light
// (Cone) lights the half below it by the cosine of the angle from straight down; a beam (Quad) is a row of `width`
// emitters, each with a share of the light and a narrow profile (cosine to the fourth power) along its direction.
// Solid cells take the light that reaches their face (lit walls).
//
// Reflected light. A lit solid face sends lux x reflectance of its material back into the open cell the ray came from
// (so it never leaks through the wall); a lit open cell sends lux x reflectance of its back wall (a built one, or the
// natural one; none reflects nothing). The reflected light is then carried for Steps cells by a transport with four
// directions: in each cell the light moving east, west, north and south goes one cell on per step; a share turns into
// the other directions (the spread of a beam); a share leaves the plane, coming back by the back wall's reflectance;
// light meeting a solid side comes back by that side's reflectance (half straight back, a quarter to each side). So it
// goes round corners and along corridors, far in a snow corridor and briefly in an abyssalite one, and a pale room is
// brighter than a dark one under the same lamp. The scattered light of a cell is the sum over the steps, scaled so that
// in an open room without walls it equals the reflected light around it.
//
// Updates. Each source keeps its direct and its scattered light by cell (the transport is linear, so the scattered light
// of the grid is the sum over the sources, each carried over its box widened by Steps), and puts them into the game's
// light grid (direct + scattered, rounded) at once when added and takes them out when removed. A source whose brightness
// alone changes (a light bug dimming) is rescaled rather than traced again. Changes of the cells around a source reach it
// through the game, which refreshes a light when a cell in its area changes (the area is widened to what the light can
// reach).
#pragma once

#include <math.h>
#include <stdint.h>
#include <string.h>

#include <algorithm>
#include <vector>

#include "oni_cpu.h"

#define ONI_LIGHT_INLINE ONI_INLINE

namespace onilight
{
	constexpr int Steps = 12;                 // how far the reflected light is carried, in cells
	constexpr float Spread = 0.3f;            // share of the scattered light in a cell that turns into the other directions per step
	constexpr float Escape = 0.15f;           // share of the scattered light that leaves the plane per cell (back wall, viewer)
	constexpr float WindowThrough = 0.9f;     // a transparent tile
	constexpr float BuildingReflectance = 0.35f; // a solid building cell whose element is not solid (a closed door)
	constexpr float MinLux = 0.5f;            // less direct or reflected light than this is not kept
	constexpr float MinScattered = 0.05f;     // less scattered light than this is not kept
	constexpr float MaxRange = 64.0f;         // a larger radius (another mod's light) is taken as this
	constexpr float FullLux = 500.0f;         // the player sees a cell at the game's own brightness from this light
	constexpr float SightIn = 0.3f, SightOut = 0.7f; // s: what duplicants start or stop seeing shows or fades for the player

	enum State : uint8_t { Vacuum = 0, Gas = 1, Liquid = 2, Solid = 3 };
	enum Shape : int32_t { Circle = 0, Cone = 1, Quad = 2 };
	enum Direction : int32_t { North = 0, East = 1, South = 2, West = 3 };

	// By element index. Layout must match LightNative.ElementOptics in src/OniLight/LightNative.cs.
	struct ElementOptics
	{
		float reflectance; // of a solid surface or a back wall of this element, 0..1
		float absorb;      // a liquid or a gas lets through exp(-absorb * mass), mass in kg (0: the gas is clear)
		uint8_t state;     // State
		uint8_t pad[3];
		float molarRatio;  // a gas whose shown mass is the equivalent of the reference gas (the gas mixture mod by pressure):
		                   // its molar mass over the reference one; 0 when the shown mass is the real one
	};

	constexpr float ReferenceTemperature = 293.15f; // of the equivalent mass (sim/plugins/gas_mix_kernel.h)

	// The game's grid, passed on each call (the managed arrays are pinned only for the call).
	// Layout must match LightNative.GridView in src/OniLight/LightNative.cs.
	struct GridView
	{
		int32_t width;
		int32_t height;
		const uint16_t* elementIdx;         // simulation: element index of the cell
		const float* mass;                  // simulation: cell mass, kg
		const uint8_t* properties;          // simulation: cell flags, 0x10 = transparent
		const uint16_t* backwallElementIdx; // simulation: natural back wall element, 0xFFFF = none
		const uint8_t* buildMasks;          // C#: building flags, bit 1 = solid cell
		const uint16_t* builtBackwall;      // C#: element of a built back wall, 0xFFFF = none
		int32_t* lightCount;                // C#: the game's light grid, written as sources are added and removed
		const float* temperature;           // simulation: cell temperature, K (for the real mass of a gas shown as equivalent)
		const float* mixThrough;            // C#: what a cell holding a mixture lets through (by its composition); NaN = none
	};

	// Layout must match LightNative.SourceDesc in src/OniLight/LightNative.cs.
	struct SourceDesc
	{
		int32_t x, y;
		float lux, range, falloff;
		int32_t shape, direction, width;
		int32_t natural; // not made by the colony (a creature, a plant, a natural rock): the player sees its light only where a duplicant sees
	};

	struct Rect
	{
		int x0, y0, x1, y1; // inclusive
		bool Empty() const { return x1 < x0 || y1 < y0; }
		Rect Grow(int n) const { return { x0 - n, y0 - n, x1 + n, y1 + n }; }
		bool Touches(const Rect& o) const { return x0 <= o.x1 + 1 && o.x0 <= x1 + 1 && y0 <= o.y1 + 1 && o.y0 <= y1 + 1; }
		void Join(const Rect& o) { x0 = std::min(x0, o.x0); y0 = std::min(y0, o.y0); x1 = std::max(x1, o.x1); y1 = std::max(y1, o.y1); }
		int Cells() const { return Empty() ? 0 : (x1 - x0 + 1) * (y1 - y0 + 1); }
	};

	// Offsets within 1.5 R in order of distance; for each, the offset before it on the straight ray from the source, whether
	// the step from it is diagonal, the distance and the fade.
	struct Tree
	{
		float range = 0.0f;
		int reach = 0;
		std::vector<int16_t> dx, dy;
		std::vector<int32_t> parent;
		std::vector<uint8_t> diagonal;
		std::vector<float> r, fade;

		void Build(float radius)
		{
			range = radius;
			const float end = 1.5f * radius;
			reach = (int)ceilf(end);
			struct Off { int dx, dy; float r; };
			std::vector<Off> offs;
			for (int y = -reach; y <= reach; y++)
				for (int x = -reach; x <= reach; x++)
				{
					const float d = sqrtf((float)(x * x + y * y));
					if (d <= end) offs.push_back({ x, y, d });
				}
			std::sort(offs.begin(), offs.end(), [](const Off& a, const Off& b) { return a.r < b.r || (a.r == b.r && (a.dy < b.dy || (a.dy == b.dy && a.dx < b.dx))); });
			const int side = 2 * reach + 1;
			std::vector<int32_t> index((size_t)side * side, -1);
			for (size_t i = 0; i < offs.size(); i++) index[(size_t)(offs[i].dy + reach) * side + offs[i].dx + reach] = (int32_t)i;
			for (const Off& o : offs)
			{
				int px = 0, py = 0;
				const int steps = std::max(abs(o.dx), abs(o.dy));
				if (steps > 0)
				{
					px = (int)lroundf((float)o.dx / steps * (steps - 1));
					py = (int)lroundf((float)o.dy / steps * (steps - 1));
				}
				dx.push_back((int16_t)o.dx);
				dy.push_back((int16_t)o.dy);
				parent.push_back(steps == 0 ? -1 : index[(size_t)(py + reach) * side + px + reach]);
				diagonal.push_back((uint8_t)(steps > 0 && px != o.dx && py != o.dy));
				r.push_back(o.r);
				float f = 1.0f;
				if (o.r > radius)
				{
					const float t = (o.r - radius) / (end - radius);
					f = 1.0f - t * t * (3.0f - 2.0f * t);
				}
				fade.push_back(f);
			}
		}
	};

	struct Entry
	{
		uint32_t cell;
		float value;
	};

	struct Source
	{
		bool active = false; // its light is in the grid
		bool kept = false;   // taken out of the grid, its light kept to be put back with another brightness
		SourceDesc desc{};
		float traced = 0.0f; // the brightness its light was traced with (the entries are for it)
		float scale = 1.0f;  // the share of it in the grid (a light bug dimmed)
		bool wet = false;    // there is liquid, a gas that dims light or a mixture in its area: its light changes as they move
		bool seen = false;   // made by the colony and a duplicant can walk into its light: the player sees it
		std::vector<Entry> direct, scattered;
	};

	struct Counters
	{
		int64_t traces, rescales, removes, tracedOffsets, opticsCells, transportCells, writtenCells;
	};

	// The library is built for any x86-64 processor (SSE2); the transport, where nearly all the time goes, has a second
	// variant built for AVX, chosen at run time (oni_cpu.h). Both compute the same numbers bit for bit (no fused
	// multiply-add): only the vector width differs. (An AVX-512 variant gave 8% on a Zen 4 against AVX: the rows are short
	// and the rest is not vector work.)
	inline bool HasAvx() { return OniHasAvx(); }

	// One step of the transport, in two passes over plain arrays (so the compiler vectorises them).
	// Spreading: a share of the light of each cell turns evenly into the four directions, and the cell keeps r of it.
	ONI_LIGHT_INLINE void SpreadStep(size_t n, const float* __restrict r, const float* __restrict fE, const float* __restrict fW, const float* __restrict fN,
		const float* __restrict fS, float* __restrict gE, float* __restrict gW, float* __restrict gN, float* __restrict gS)
	{
		const float keep = 1.0f - Spread, turn = Spread * 0.25f;
		for (size_t i = 0; i < n; i++)
		{
			const float e = fE[i], w = fW[i], no = fN[i], so = fS[i];
			const float spread = turn * (e + w + no + so);
			gE[i] = r[i] * (keep * e + spread);
			gW[i] = r[i] * (keep * w + spread);
			gN[i] = r[i] * (keep * no + spread);
			gS[i] = r[i] * (keep * so + spread);
		}
	}

	// Moving one row of cells on by a cell: light comes in from the neighbour behind (times what the cell lets through);
	// light that met a solid side comes back by its reflectance, half straight back and a quarter to each side. The cell's
	// sum gathers what is in it after the step. gNb: the row below (light moving north), gSa: the row above.
	ONI_LIGHT_INLINE void MoveRow(int count, const float* __restrict gE, const float* __restrict gW, const float* __restrict gN, const float* __restrict gS,
		const float* __restrict gNb, const float* __restrict gSa, const float* __restrict t, const float* __restrict aE, const float* __restrict aW,
		const float* __restrict aN, const float* __restrict aS, float* __restrict fE, float* __restrict fW, float* __restrict fN, float* __restrict fS,
		float* __restrict sum)
	{
		for (int x = 0; x < count; x++)
		{
			const float rE = gE[x] * aE[x], rW = gW[x] * aW[x], rN = gN[x] * aN[x], rS = gS[x] * aS[x];
			const float sideH = 0.25f * (rN + rS), sideV = 0.25f * (rE + rW);
			const float tt = t[x];
			const float ve = tt * gE[x - 1] + 0.5f * rW + sideH;
			const float vw = tt * gW[x + 1] + 0.5f * rE + sideH;
			const float vn = tt * gNb[x] + 0.5f * rS + sideV;
			const float vs = tt * gSa[x] + 0.5f * rN + sideV;
			fE[x] = ve;
			fW[x] = vw;
			fN[x] = vn;
			fS[x] = vs;
			sum[x] += ve + vw + vn + vs;
		}
	}

	// The transport's coefficients of a row: what each cell lets through, and what it keeps of the light that does not
	// leave the plane (open cells only; solid cells keep nothing).
	ONI_LIGHT_INLINE void SetupRow(int count, const float* __restrict t, const float* __restrict albedo, float* __restrict T, float* __restrict R)
	{
		for (int x = 0; x < count; x++)
		{
			const float tt = t[x];
			T[x] = tt;
			R[x] = tt > 0.0f ? 1.0f - Escape * (1.0f - albedo[x]) : 0.0f;
		}
	}

	// What comes back from one side of each cell of a row: the side's reflectance if the cell is open and the side solid.
	ONI_LIGHT_INLINE void SideRow(int count, const float* __restrict t, const float* __restrict side, const float* __restrict sideAlbedo, float* __restrict out)
	{
		for (int x = 0; x < count; x++) out[x] = t[x] > 0.0f && side[x] <= 0.0f ? sideAlbedo[x] : 0.0f;
	}

	class Engine
	{
	public:
		void Init(int width, int height)
		{
			w_ = width;
			h_ = height;
			const size_t n = (size_t)width * height;
			direct_.assign(n, 0.0f);
			scattered_.assign(n, 0.0f);
			seen_.assign(n, 0.0f);
			natural_.assign(n, 0.0f);
			sight_.assign(n, 0);
			sightLevel_.assign(n, 0.0f);
			sightCells_.clear();
			shown_.assign(n, 0);
			through_.assign(n, 1.0f);
			albedo_.assign(n, 0.0f);
			sources_.clear();
			counters_ = {};
		}

		void SetElements(const ElementOptics* table, int count) { elements_.assign(table, table + count); }

		// For checks and measurements: the widest variant of the transport allowed (0 SSE2, 1 AVX); the processor may
		// allow less.
		void LimitVariant(int widest) { variant_ = widest; }
		const char* Variant() const { return variant_ >= 1 && HasAvx() ? "avx" : "sse2"; }

		int Width() const { return w_; }
		int Height() const { return h_; }
		const Counters& Stats() const { return counters_; }
		float Direct(int cell) const { return direct_[cell]; }
		float Scattered(int cell) const { return scattered_[cell]; }
		float Through(int cell) const { return through_[cell]; }

		// Adds a source under an id (replacing it if it is in the grid) and puts its light into the light grid. A source
		// taken out with keep and added again with only another brightness, not above the one it was traced with, is
		// rescaled (exactly: light dropped as too faint when traced stays too faint). Returns the cells lit directly.
		int Add(const GridView& g, int id, const SourceDesc& d)
		{
			if (id < 0 || w_ == 0) return 0;
			if ((size_t)id >= sources_.size()) sources_.resize((size_t)id + 1);
			Source& s = sources_[id];
			if (s.active) Remove(g, id, false);
			if (s.kept && SameShape(s.desc, d) && s.traced > 0.0f && d.lux >= 0.0f && d.lux <= s.traced)
			{
				s.scale = d.lux / s.traced;
				counters_.rescales++;
			}
			else
			{
				s.direct.clear();
				s.scattered.clear();
				s.wet = d.lux > 0.0f && d.range > 0.0f && Compute(g, d, s.direct, s.scattered) > 0;
				s.traced = d.lux;
				s.scale = 1.0f;
				counters_.traces++;
			}
			s.desc = d;
			s.kept = false;
			s.active = true;
			Apply(g, s, 1.0f);
			return (int)s.direct.size();
		}

		// Takes a source's light out of the light grid. With keep, its light is kept for an Add with another brightness
		// (Free drops it); without, it is dropped. Either way the source stays seen or not (a source traced again does not
		// go dark to the player until its next check): only Free, when the id is given up, forgets it.
		void Remove(const GridView& g, int id, bool keep)
		{
			if (id < 0 || (size_t)id >= sources_.size() || !sources_[id].active) return;
			Source& s = sources_[id];
			Apply(g, s, -1.0f);
			s.active = false;
			counters_.removes++;
			if (keep) s.kept = true;
			else Drop(s);
		}

		bool Wet(int id) const { return id >= 0 && (size_t)id < sources_.size() && sources_[id].active && sources_[id].wet; }

		// ---------- What the player sees ----------

		// Whether a duplicant can walk into the light of a source made by the colony (the mod decides): the player sees its
		// light only then.
		void SetSeen(int id, bool seen)
		{
			if (id < 0 || (size_t)id >= sources_.size()) return;
			Source& s = sources_[id];
			if (s.seen == seen) return;
			if (s.active && !s.desc.natural) Seen(s, seen_, (seen ? 1.0f : -1.0f) * s.scale);
			s.seen = seen;
		}

		// Cells a source lights directly, nearest first, at most capacity (for the mod's check of who can walk into it).
		int SourceCells(int id, int32_t* cells, int capacity) const
		{
			if (id < 0 || (size_t)id >= sources_.size() || !sources_[id].active) return 0;
			const Source& s = sources_[id];
			int n = 0;
			for (size_t i = 0; i < s.direct.size() && n < capacity; i++) cells[n++] = (int32_t)s.direct[i].cell;
			return n;
		}

		// What duplicants see now. An eye is a point (x, y in cells: cell (x, y) spans [x, x + 1) x [y, y + 1)), where the
		// head is in the animation, not a cell's centre. A cell is seen if a straight line from an eye reaches some part of
		// it through open and transparent cells only (gas, liquid, windows), and its centre is within `radius`; a solid cell
		// seen is its face. Exact by angles: four quadrants (east, west, north, south, each to 45 degrees off its axis) are
		// swept row by row away from the eye, keeping the intervals of directions (slopes) not yet shadowed; a cell is seen
		// if its interval overlaps one of them, and an opaque cell takes its interval out for the rows behind it. Walking
		// towards a gap, the cone seen through it widens cell by cell. The light of things not made by the colony and of the
		// sun shows to the player only in these cells.
		void Sight(const GridView& g, const float* eyes, int count, float radius)
		{
			for (int32_t c : sightCells_) sight_[c] = 0;
			sightCells_.clear();
			if (count <= 0 || w_ == 0) return;
			for (int k = 0; k < count; k++)
			{
				const float ex = eyes[2 * k], ey = eyes[2 * k + 1];
				if (!(ex >= 0.0f && ey >= 0.0f && ex < (float)w_ && ey < (float)h_)) continue;
				MarkSeen((int)ex, (int)ey);
				for (int q = 0; q < 4; q++) SightQuadrant(g, ex, ey, q, radius);
			}
		}

		void MarkSeen(int x, int y)
		{
			if (x < 0 || y < 0 || x >= w_ || y >= h_) return;
			const int cell = y * w_ + x;
			if (!sight_[cell])
			{
				sight_[cell] = 1;
				sightCells_.push_back(cell);
			}
		}

		// One quadrant: q 0 east (+x), 1 west (-x), 2 north (+y), 3 south (-y); p the axis away from the eye, s across it.
		void SightQuadrant(const GridView& g, float ex, float ey, int q, float radius)
		{
			const bool alongX = q < 2;
			const int dir = q == 0 || q == 2 ? 1 : -1;
			const float ep = alongX ? ex : ey, es = alongX ? ey : ex;
			const int ip = (int)ep;
			const float eps = 1e-5f, r2 = radius * radius;
			float open[2 * 64];
			int n = 1;
			open[0] = -1.0f;
			open[1] = 1.0f;
			float shade[2 * 160];
			const int rows = (int)radius + 1;
			for (int k = 1; k <= rows && n > 0; k++)
			{
				const int cp = ip + dir * k; // the row's cell index along p
				// Distance along p from the eye to the row's near and far edges.
				const float a = std::max(dir > 0 ? (float)cp - ep : ep - (float)(cp + 1), eps), b = a + 1.0f;
				if (a * a > r2) break;
				const int j0 = (int)floorf(es - b), j1 = (int)floorf(es + b);
				int shades = 0;
				for (int j = j0; j <= j1; j++)
				{
					const float lo = (float)j - es, hi = lo + 1.0f;
					const float slo = std::min(lo / a, lo / b), shi = std::max(hi / a, hi / b);
					if (shi <= -1.0f || slo >= 1.0f) continue;
					const float cs = lo + 0.5f, cpd = a + 0.5f;
					bool seen = false;
					for (int i = 0; i < n && !seen; i++) seen = slo < open[2 * i + 1] - eps && shi > open[2 * i] + eps;
					const int x = alongX ? cp : j, y = alongX ? j : cp;
					if (seen && cs * cs + cpd * cpd <= r2) MarkSeen(x, y);
					if (Clear(g, x, y) <= 0.0f && shades < 160)
					{
						shade[2 * shades] = slo;
						shade[2 * shades + 1] = shi;
						shades++;
					}
				}
				// The opaque cells of the row shadow the rows behind it.
				for (int t = 0; t < shades; t++) n = Subtract(open, n, shade[2 * t], shade[2 * t + 1], eps);
			}
		}

		// Takes [lo, hi] out of the sorted open intervals; drops what is narrower than eps. Returns their number.
		static int Subtract(float* open, int n, float lo, float hi, float eps)
		{
			float out[2 * 64];
			int m = 0;
			for (int i = 0; i < n; i++)
			{
				const float u = open[2 * i], v = open[2 * i + 1];
				if (hi <= u || lo >= v)
				{
					if (m < 64) { out[2 * m] = u; out[2 * m + 1] = v; m++; }
					continue;
				}
				if (lo > u + eps && m < 64) { out[2 * m] = u; out[2 * m + 1] = lo; m++; }
				if (hi < v - eps && m < 64) { out[2 * m] = hi; out[2 * m + 1] = v; m++; }
			}
			for (int i = 0; i < 2 * m; i++) open[i] = out[i];
			return m;
		}

		// What the player sees in each cell of a rectangle, kept by cell (Shown): 0 is darkness, 255 the game's own picture;
		// the brightness grows with the logarithm of the light up to full at FullLux. The light seen: of the colony's
		// sources a duplicant can walk into, and, in the cells duplicants see, of the others and of the sun (exposed
		// 0..255 times sunLux).
		// What duplicants see comes and goes smoothly for the player: a cell they start to see shows over SightIn seconds,
		// one they stop seeing fades over SightOut (`dt`: the time since the last call, s).
		void UpdateShown(Rect r, const uint8_t* exposed, float sunLux, float dt)
		{
			r = Clip(r);
			const float scale = 255.0f / logf(1.0f + FullLux);
			const float up = std::min(1.0f, std::max(dt, 0.0f) / SightIn), down = std::min(1.0f, std::max(dt, 0.0f) / SightOut);
			for (int y = r.y0; y <= r.y1; y++)
			{
				const size_t row = (size_t)y * w_;
				for (int x = r.x0; x <= r.x1; x++)
				{
					const size_t c = row + x;
					float lux = seen_[c];
					float& level = sightLevel_[c];
					level = sight_[c] ? std::min(1.0f, level + up) : std::max(0.0f, level - down);
					if (level > 0.0f) lux += level * (natural_[c] + (exposed ? exposed[c] * (sunLux / 255.0f) : 0.0f));
					shown_[c] = lux <= 0.0f ? 0 : (uint8_t)std::min(255.0f, std::max(1.0f, logf(1.0f + lux) * scale + 0.5f));
				}
			}
		}

		// What the player sees by cell (see UpdateShown); valid until the next Init.
		const uint8_t* Shown() const { return shown_.data(); }

		float SeenLux(int cell) const { return seen_[cell]; }
		float NaturalLux(int cell) const { return natural_[cell]; }
		bool InSight(int cell) const { return sight_[cell] != 0; }

		// The id is given up (the next source under it is new to the player too).
		void Free(int id)
		{
			if (id < 0 || (size_t)id >= sources_.size() || sources_[id].active) return;
			Source& s = sources_[id];
			Drop(s);
			s.seen = false;
		}

		static void Drop(Source& s)
		{
			s.kept = false;
			std::vector<Entry>().swap(s.direct);
			std::vector<Entry>().swap(s.scattered);
		}

		// Direct light of a source that is not added (the placement preview of a building). Returns the number of cells.
		int Preview(const GridView& g, const SourceDesc& d, int32_t* cells, int32_t* lux, int capacity)
		{
			if (!(d.lux > 0.0f) || !(d.range > 0.0f) || w_ == 0) return 0;
			std::vector<Entry> direct, bounce;
			const Rect box = Box(d);
			if (box.Empty()) return 0;
			RefreshOptics(g, box);
			Trace(d, direct, bounce);
			// One emitter's cells are distinct, but a beam's emitters overlap: sum by cell.
			int n = 0;
			for (const Entry& e : direct)
			{
				int k = 0;
				while (k < n && cells[k] != (int32_t)e.cell) k++;
				if (k == n)
				{
					if (n == capacity) continue;
					cells[n] = (int32_t)e.cell;
					lux[n] = 0;
					n++;
				}
				lux[k] += (int32_t)(e.value + 0.5f);
			}
			return n;
		}

		// Self-check against the grid as it is now, over the cells of `area`. out: [0] the largest difference of the direct
		// light from tracing every source again; [1] of the light grid from computing every source again (lux); [2] of the
		// light grid from carrying the reflected light of all sources at once over the whole grid (lux; differs by the light
		// below MinScattered that each source drops); [3] the number of cells compared; [4] the cell of the largest
		// difference of the direct light, [5] of the light grid from computing every source again.
		void Check(const GridView& g, Rect area, float* out)
		{
			area = Clip(area);
			const size_t n = (size_t)w_ * h_;
			std::vector<float> direct(n, 0.0f), scattered(n, 0.0f), all(n, 0.0f);
			std::vector<Entry> d, sc, bounce, bounceAll;
			for (const Source& s : sources_)
			{
				if (!s.active || !(s.traced > 0.0f) || !(s.desc.range > 0.0f)) continue;
				SourceDesc traced = s.desc;
				traced.lux = s.traced;
				d.clear();
				sc.clear();
				Compute(g, traced, d, sc);
				for (const Entry& e : d) direct[e.cell] += s.scale * e.value;
				for (const Entry& e : sc) scattered[e.cell] += s.scale * e.value;
				bounce.clear();
				d.clear();
				Trace(traced, d, bounce);
				for (Entry& e : bounce) e.value *= s.scale;
				bounceAll.insert(bounceAll.end(), bounce.begin(), bounce.end());
			}
			const Rect grid{ 0, 0, w_ - 1, h_ - 1 };
			RefreshOptics(g, grid);
			sc.clear();
			Transport(grid, bounceAll, sc);
			for (const Entry& e : sc) all[e.cell] += e.value;
			float dd = 0.0f, d1 = 0.0f, d2 = 0.0f;
			size_t worstDirect = 0, worstGrid = 0;
			for (int y = area.y0; y <= area.y1; y++)
				for (int x = area.x0; x <= area.x1; x++)
				{
					const size_t c = (size_t)y * w_ + x;
					const float a = fabsf(direct[c] - direct_[c]), b = fabsf((float)(Round(direct[c] + scattered[c]) - g.lightCount[c]));
					if (a > dd) { dd = a; worstDirect = c; }
					if (b > d1) { d1 = b; worstGrid = c; }
					d2 = std::max(d2, fabsf((float)(Round(direct[c] + all[c]) - g.lightCount[c])));
				}
			out[0] = dd;
			out[1] = d1;
			out[2] = d2;
			out[3] = (float)area.Cells();
			out[4] = (float)worstDirect;
			out[5] = (float)worstGrid;
		}

	private:
		int w_ = 0, h_ = 0;
		std::vector<ElementOptics> elements_;
		std::vector<float> direct_, scattered_, through_, albedo_, seen_, natural_;
		std::vector<uint8_t> sight_, shown_;
		std::vector<float> sightLevel_;
		std::vector<int32_t> sightCells_;

		// Whether a duplicant sees through a cell (anything but an opaque solid), from the grid as it is now.
		float Clear(const GridView& g, int x, int y) const
		{
			if (x < 0 || y < 0 || x >= w_ || y >= h_) return 0.0f;
			const size_t c = (size_t)y * w_ + x;
			const ElementOptics& e = Element(g.elementIdx[c]);
			const bool solid = e.state == Solid || (g.buildMasks[c] & 1) != 0;
			return !solid || (g.properties[c] & 0x10) != 0 ? 1.0f : 0.0f;
		}
		std::vector<Source> sources_;
		std::vector<Tree> trees_;
		std::vector<float> reach_, work_;
		std::vector<Entry> bounce_;
		Counters counters_{};

		static int32_t Round(float v) { return v > 0.5f ? (int32_t)(v + 0.5f) : 0; }

		static bool SameShape(const SourceDesc& a, const SourceDesc& b)
		{
			return a.x == b.x && a.y == b.y && a.range == b.range && a.falloff == b.falloff && a.shape == b.shape && a.direction == b.direction && a.width == b.width;
		}

		Rect Clip(Rect r) const { return { std::max(r.x0, 0), std::max(r.y0, 0), std::min(r.x1, w_ - 1), std::min(r.y1, h_ - 1) }; }

		void Apply(const GridView& g, Source& s, float sign)
		{
			sign *= s.scale;
			for (const Entry& e : s.direct)
			{
				direct_[e.cell] += sign * e.value;
				g.lightCount[e.cell] = Round(direct_[e.cell] + scattered_[e.cell]);
			}
			for (const Entry& e : s.scattered)
			{
				scattered_[e.cell] += sign * e.value;
				g.lightCount[e.cell] = Round(direct_[e.cell] + scattered_[e.cell]);
			}
			counters_.writtenCells += (int64_t)(s.direct.size() + s.scattered.size());
			// What the player may see of it.
			if (s.desc.natural) Seen(s, natural_, sign);
			else if (s.seen) Seen(s, seen_, sign);
		}

		static void Seen(const Source& s, std::vector<float>& grid, float amount)
		{
			for (const Entry& e : s.direct) grid[e.cell] += amount * e.value;
			for (const Entry& e : s.scattered) grid[e.cell] += amount * e.value;
		}

		// Trees by radius, rounded to a quarter of a cell (a light bug's radius shrinks smoothly as it dims); kept for the
		// last 64 radii.
		const Tree& TreeFor(float range)
		{
			range = std::min(std::max(roundf(range * 4.0f) * 0.25f, 0.25f), MaxRange);
			for (const Tree& t : trees_)
				if (t.range == range) return t;
			if (trees_.size() >= 64) trees_.clear();
			trees_.emplace_back();
			trees_.back().Build(range);
			return trees_.back();
		}

		const ElementOptics& Element(uint16_t idx) const
		{
			static const ElementOptics none = { 0.0f, 0.0f, Gas, { 0, 0, 0 } };
			return idx < elements_.size() ? elements_[idx] : none;
		}

		float BackReflectance(const GridView& g, size_t c) const
		{
			const uint16_t built = g.builtBackwall ? g.builtBackwall[c] : 0xFFFF;
			if (built != 0xFFFF) return Element(built).reflectance;
			const uint16_t natural = g.backwallElementIdx ? g.backwallElementIdx[c] : 0xFFFF;
			return natural != 0xFFFF ? Element(natural).reflectance : 0.0f;
		}

		// What the cells of a rectangle let through and reflect, from the game's grid as it is now. Returns the number of
		// cells whose light changes without an event from the game: liquids, gases that dim light, mixtures.
		int RefreshOptics(const GridView& g, Rect r)
		{
			r = Clip(r);
			counters_.opticsCells += r.Cells();
			int liquid = 0;
			for (int y = r.y0; y <= r.y1; y++)
			{
				const size_t row = (size_t)y * w_;
				for (int x = r.x0; x <= r.x1; x++)
				{
					const size_t c = row + x;
					const ElementOptics& e = Element(g.elementIdx[c]);
					const bool solid = e.state == Solid || (g.buildMasks[c] & 1) != 0;
					const bool transparent = (g.properties[c] & 0x10) != 0;
					if (solid && !transparent)
					{
						through_[c] = 0.0f;
						albedo_[c] = e.state == Solid ? e.reflectance : BuildingReflectance;
					}
					else
					{
						float t = 1.0f;
						const float mix = g.mixThrough ? g.mixThrough[c] : NAN;
						if (solid) t = WindowThrough;
						else if (mix == mix) t = mix, liquid++;
						else if (e.state == Liquid) t = expf(-e.absorb * g.mass[c]), liquid++;
						else if (e.absorb > 0.0f)
						{
							float mass = g.mass[c];
							if (e.molarRatio > 0.0f && g.temperature) mass *= e.molarRatio * ReferenceTemperature / std::max(g.temperature[c], 1.0f);
							t = expf(-e.absorb * mass);
							liquid++;
						}
						through_[c] = t;
						albedo_[c] = BackReflectance(g, c);
					}
				}
			}
			return liquid;
		}

		float At(int x, int y) const { return x >= 0 && y >= 0 && x < w_ && y < h_ ? through_[(size_t)y * w_ + x] : 0.0f; }

		struct Emitter { int x, y; float lux; };

		// The emitters of a source (a beam is a row of them) and the direction of a beam.
		int Emitters(const SourceDesc& d, Emitter* out, int& ax, int& ay) const
		{
			ax = 0;
			ay = -1;
			if (d.direction == North) { ax = 0; ay = 1; }
			else if (d.direction == East) { ax = 1; ay = 0; }
			else if (d.direction == West) { ax = -1; ay = 0; }
			if (d.shape != Quad || d.width <= 1)
			{
				out[0] = { d.x, d.y, d.lux };
				return 1;
			}
			const int width = std::min(d.width, 64);
			const int px = ay != 0 ? 1 : 0, py = ax != 0 ? 1 : 0; // across the beam
			const int back = width % 2 == 0 ? width / 2 - 1 : (width - 1) / 2;
			for (int i = 0; i < width; i++) out[i] = { d.x + (i - back) * px, d.y + (i - back) * py, d.lux / width };
			return width;
		}

		// The cells a source may light directly.
		Rect Box(const SourceDesc& d)
		{
			Emitter em[64];
			int ax, ay;
			const int count = Emitters(d, em, ax, ay);
			const int reach = (int)ceilf(1.5f * std::min(d.range, MaxRange)) + 1;
			return Clip({ std::min(em[0].x, em[count - 1].x) - reach, std::min(em[0].y, em[count - 1].y) - reach,
				std::max(em[0].x, em[count - 1].x) + reach, std::max(em[0].y, em[count - 1].y) + reach });
		}

		// Direct and scattered light of a source, from the grid as it is now. Returns the number of liquid cells in its area.
		int Compute(const GridView& g, const SourceDesc& d, std::vector<Entry>& direct, std::vector<Entry>& scattered)
		{
			const Rect box = Box(d);
			if (box.Empty()) return 0;
			const Rect region = Clip(box.Grow(Steps));
			const int liquid = RefreshOptics(g, region.Grow(1));
			bounce_.clear();
			Trace(d, direct, bounce_);
			if (!bounce_.empty()) Transport(region, bounce_, scattered);
			return liquid;
		}

		// Direct and reflected light of a source; the optics of its box must be fresh.
		void Trace(const SourceDesc& d, std::vector<Entry>& direct, std::vector<Entry>& bounce)
		{
			const Tree& tree = TreeFor(d.range);
			Emitter emitters[64];
			int ax, ay;
			const int count = Emitters(d, emitters, ax, ay);
			const size_t n = tree.dx.size();
			reach_.resize(n);
			float* reach = reach_.data();
			const float falloff = d.falloff > 0.0f ? d.falloff : 0.5f;
			for (int k = 0; k < count; k++)
			{
				const Emitter& em = emitters[k];
				counters_.tracedOffsets += (int64_t)n;
				for (size_t i = 0; i < n; i++)
				{
					const int x = em.x + tree.dx[i], y = em.y + tree.dy[i];
					const int p = tree.parent[i];
					float t = 1.0f;
					int from = -1;
					if (p >= 0)
					{
						const float rp = reach[p];
						if (rp <= 0.0f) { reach[i] = 0.0f; continue; }
						const int px = em.x + tree.dx[p], py = em.y + tree.dy[p];
						t = p == 0 ? rp : rp * At(px, py);
						if (tree.diagonal[i]) t *= std::max(At(x, py), At(px, y));
						from = py * w_ + px;
					}
					reach[i] = t;
					if (t <= 0.0f || x < 0 || y < 0 || x >= w_ || y >= h_) continue;
					const float r = tree.r[i];
					float shape = 1.0f;
					if (r > 0.0f)
					{
						if (d.shape == Cone) shape = tree.dy[i] < 0 ? -tree.dy[i] / r : 0.0f;
						else if (d.shape == Quad)
						{
							const float along = (tree.dx[i] * ax + tree.dy[i] * ay) / r;
							shape = along > 0.0f ? along * along * along * along : 0.0f;
						}
					}
					const float lux = em.lux * shape * tree.fade[i] * t / std::max(1.0f, falloff * r);
					if (lux < MinLux) continue;
					const uint32_t cell = (uint32_t)(y * w_ + x);
					direct.push_back({ cell, lux });
					const float back = lux * albedo_[cell];
					if (back < MinLux) continue;
					// A solid face reflects into the cell the ray came from, an open cell by its back wall.
					bounce.push_back({ through_[cell] > 0.0f || from < 0 ? cell : (uint32_t)from, back });
				}
			}
		}

		int variant_ = 1; // the widest allowed: 0 SSE2, 1 AVX

		__attribute__((target("avx"), noinline)) void TransportAvx(const Rect& region, const std::vector<Entry>& bounce, std::vector<Entry>& out)
		{
			TransportBody(region, bounce, out);
		}

		__attribute__((noinline)) void TransportPlain(const Rect& region, const std::vector<Entry>& bounce, std::vector<Entry>& out)
		{
			TransportBody(region, bounce, out);
		}

		// Scattered light carried from reflected light (cells inside `region`, at least Steps away from its edges unless
		// at the grid's edge) over `region`; the optics of `region` widened by 1 must be fresh.
		void Transport(const Rect& region, const std::vector<Entry>& bounce, std::vector<Entry>& out)
		{
			if (variant_ >= 1 && HasAvx()) TransportAvx(region, bounce, out);
			else TransportPlain(region, bounce, out);
		}

		ONI_LIGHT_INLINE void TransportBody(const Rect& region, const std::vector<Entry>& bounce, std::vector<Entry>& out)
		{
			// Local arrays with a border of one empty cell.
			const int lw = region.x1 - region.x0 + 3, lh = region.y1 - region.y0 + 3;
			const size_t n = (size_t)lw * lh;
			work_.resize(n * 15);
			float* T = work_.data();
			float* R = T + n;
			float* aE = R + n;
			float* aW = aE + n;
			float* aN = aW + n;
			float* aS = aN + n;
			float* F[4] = { aS + n, aS + 2 * n, aS + 3 * n, aS + 4 * n }; // moving east, west, north, south
			float* A = aS + 5 * n;
			float* G[4] = { A + n, A + 2 * n, A + 3 * n, A + 4 * n }; // the same after spreading, before moving
			std::fill(T, A + n, 0.0f); // the light, the border, and the sides of cells at the grid's edge facing out of it
			const int cols = region.x1 - region.x0 + 1;
			for (int ly = 1; ly < lh - 1; ly++)
			{
				const int y = region.y0 + ly - 1;
				const size_t c = (size_t)y * w_ + region.x0, i = (size_t)ly * lw + 1;
				const float* __restrict t = &through_[c];
				const float* __restrict al = &albedo_[c];
				// The neighbours' optics; outside the grid nothing comes back (such a side keeps 0).
				const bool east = region.x1 + 1 < w_, west = region.x0 > 0, north = y + 1 < h_, south = y > 0;
				const int wf = west ? 0 : 1;
				SetupRow(cols, t, al, T + i, R + i);
				SideRow(east ? cols : cols - 1, t, t + 1, al + 1, aE + i);
				SideRow(cols - wf, t + wf, t + wf - 1, al + wf - 1, aW + i + wf);
				if (north) SideRow(cols, t, t + w_, al + w_, aN + i);
				if (south) SideRow(cols, t, t - w_, al - w_, aS + i);
			}
			// Reflected light leaves its cell evenly through the cell's open sides.
			int by0 = lh, by1 = -1; // the rows where it starts
			for (const Entry& e : bounce)
			{
				const int lx = (int)(e.cell % (uint32_t)w_) - region.x0 + 1, ly = (int)(e.cell / (uint32_t)w_) - region.y0 + 1;
				const size_t i = (size_t)ly * lw + lx;
				if (T[i] <= 0.0f) continue;
				const bool oe = T[i + 1] > 0.0f, ow = T[i - 1] > 0.0f, on = T[i + lw] > 0.0f, os = T[i - lw] > 0.0f;
				const int open = oe + ow + on + os;
				if (open == 0) continue;
				const float share = e.value / open;
				F[0][i] += oe ? share : 0.0f;
				F[1][i] += ow ? share : 0.0f;
				F[2][i] += on ? share : 0.0f;
				F[3][i] += os ? share : 0.0f;
				A[i] += e.value;
				by0 = std::min(by0, ly);
				by1 = std::max(by1, ly);
			}
			// Light goes one cell per step, so step k only needs the rows of the start widened by k + 1. Each pass runs over
			// whole rows as one array (the border cells keep nothing, so nothing crosses from one row to the next).
			for (int step = 0; step < Steps && by1 >= 0; step++)
			{
				const int y0 = std::max(1, by0 - step - 1), y1 = std::min(lh - 2, by1 + step + 1);
				const size_t first = (size_t)(y0 - 1) * lw, spread = (size_t)(y1 - y0 + 3) * lw, at = (size_t)y0 * lw, count = (size_t)(y1 - y0 + 1) * lw;
				counters_.transportCells += (int64_t)count;
				SpreadStep(spread, R + first, F[0] + first, F[1] + first, F[2] + first, F[3] + first, G[0] + first, G[1] + first, G[2] + first, G[3] + first);
				MoveRow((int)count - 2, G[0] + at + 1, G[1] + at + 1, G[2] + at + 1, G[3] + at + 1, G[2] + at + 1 - lw, G[3] + at + 1 + lw,
					T + at + 1, aE + at + 1, aW + at + 1, aN + at + 1, aS + at + 1, F[0] + at + 1, F[1] + at + 1, F[2] + at + 1, F[3] + at + 1, A + at + 1);
			}
			const float q = 1.0f - Escape;
			const float norm = (1.0f - q) / (1.0f - powf(q, (float)(Steps + 1)));
			for (int ly = 1; ly < lh - 1; ly++)
			{
				const uint32_t row = (uint32_t)((region.y0 + ly - 1) * w_ + region.x0 - 1);
				const size_t i0 = (size_t)ly * lw;
				const float* a = A + i0;
				const float* t = T + i0;
				for (int lx = 1; lx < lw - 1; lx++)
				{
					// A solid face is lit by the scattered light in front of it (the mean of its open sides: linear, so the
					// lights add up): the ceiling over a light that shines down, the walls by the floor. Solid cells carry no
					// light of their own.
					float v;
					if (t[lx] > 0.0f) v = a[lx] * norm;
					else
					{
						const int open = (t[lx - 1] > 0.0f) + (t[lx + 1] > 0.0f) + (t[lx - lw] > 0.0f) + (t[lx + lw] > 0.0f);
						v = open == 0 ? 0.0f : (a[lx - 1] + a[lx + 1] + a[lx - lw] + a[lx + lw]) * norm / (float)open;
					}
					if (v >= MinScattered) out.push_back({ row + (uint32_t)lx, v });
				}
			}
		}
	};
}
