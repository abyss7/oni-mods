// Light for the Blackout mode, the game library (oni_light.bin, for OniLight): the engine of oni_light.h behind plain C
// functions for C# (src/OniLight/LightNative.cs). One engine for the game's one grid; all calls come from the game's
// main thread.
//
// Build: scripts/build-native.sh (cross-compiled for Windows x64, the game runs under Proton).
// Built for any x86-64 processor (SSE2); the transport also has an AVX variant chosen at run time (oni_light_variant).

#include "oni_grid.h"
#include "oni_light.h"

// Changes on any edit of the structures and signatures: C# compares the number and leaves the game's light alone on a mismatch.
#define ONI_LIGHT_ABI 6

#define ONI_STRINGIFY2(x) #x
#define ONI_STRINGIFY(x) ONI_STRINGIFY2(x)

static onilight::Engine engine;

ONI_EXPORT int32_t oni_light_abi()
{
	return ONI_LIGHT_ABI;
}

ONI_EXPORT const char* oni_light_info()
{
	return "oni_light abi " ONI_STRINGIFY(ONI_LIGHT_ABI) ", " __VERSION__ ", SSE2 with AVX chosen at run time";
}

// The variant of the transport this processor runs: "avx" or "sse2".
ONI_EXPORT const char* oni_light_variant()
{
	return engine.Variant();
}

// A new grid (a game loaded): every source is dropped.
ONI_EXPORT void oni_light_init(int32_t width, int32_t height)
{
	engine.Init(width, height);
}

ONI_EXPORT void oni_light_elements(const onilight::ElementOptics* table, int32_t count)
{
	engine.SetElements(table, count);
}

// Adds a source (or replaces one with the same id) and puts its light into the light grid. Returns the cells lit directly.
ONI_EXPORT int32_t oni_light_add(const onilight::GridView* grid, int32_t id, const onilight::SourceDesc* source)
{
	return engine.Add(*grid, id, *source);
}

// Takes a source's light out of the light grid; keep != 0 keeps it for an add with another brightness only.
ONI_EXPORT void oni_light_remove(const onilight::GridView* grid, int32_t id, int32_t keep)
{
	engine.Remove(*grid, id, keep != 0);
}

// Whether there is liquid, a gas that dims light or a mixture in a source's area (its light changes as they move, with no
// event from the game).
ONI_EXPORT int32_t oni_light_wet(int32_t id)
{
	return engine.Wet(id) ? 1 : 0;
}

// Drops what is kept for a source taken out with keep.
ONI_EXPORT void oni_light_free(int32_t id)
{
	engine.Free(id);
}

// The direct light of a source that is not added: cells and lux, at most capacity. Returns the number written.
ONI_EXPORT int32_t oni_light_preview(const onilight::GridView* grid, const onilight::SourceDesc* source, int32_t* cells, int32_t* lux, int32_t capacity)
{
	return engine.Preview(*grid, *source, cells, lux, capacity);
}

// out: direct light, scattered light, what the cell lets through (as last read).
ONI_EXPORT void oni_light_cell(int32_t cell, float* out)
{
	if (cell < 0 || cell >= engine.Width() * engine.Height())
	{
		out[0] = out[1] = out[2] = 0.0f;
		return;
	}
	out[0] = engine.Direct(cell);
	out[1] = engine.Scattered(cell);
	out[2] = engine.Through(cell);
}

// Self-check against the grid as it is now, over a rectangle of cells (inclusive): see Engine::Check. out: 6 numbers.
ONI_EXPORT void oni_light_check(const onilight::GridView* grid, int32_t x0, int32_t y0, int32_t x1, int32_t y1, float* out)
{
	engine.Check(*grid, { x0, y0, x1, y1 }, out);
}

// Counters since the grid was made: traces, rescales, removes, traced offsets, optics cells, transport cell-steps, written cells.
ONI_EXPORT void oni_light_stats(int64_t* out)
{
	const onilight::Counters& c = engine.Stats();
	out[0] = c.traces;
	out[1] = c.rescales;
	out[2] = c.removes;
	out[3] = c.tracedOffsets;
	out[4] = c.opticsCells;
	out[5] = c.transportCells;
	out[6] = c.writtenCells;
}

// ---------- What the player sees ----------

// Whether a duplicant can walk into the light of a source made by the colony: the player sees its light only then.
ONI_EXPORT void oni_light_set_seen(int32_t id, int32_t seen)
{
	engine.SetSeen(id, seen != 0);
}

// Cells a source lights directly, nearest first, at most capacity. Returns the number written.
ONI_EXPORT int32_t oni_light_source_cells(int32_t id, int32_t* cells, int32_t capacity)
{
	return engine.SourceCells(id, cells, capacity);
}

// What duplicants see now, from their eyes (x, y pairs in cells, where the heads are), up to radius cells.
ONI_EXPORT void oni_light_sight(const onilight::GridView* grid, const float* eyes, int32_t count, float radius)
{
	engine.Sight(*grid, eyes, count, radius);
}

// What the player sees in cells x0..x1, y0..y1, kept by cell (see Engine::UpdateShown).
ONI_EXPORT void oni_light_update_shown(int32_t x0, int32_t y0, int32_t x1, int32_t y1, const uint8_t* exposed, float sunLux, float dt)
{
	engine.UpdateShown({ x0, y0, x1, y1 }, exposed, sunLux, dt);
}

// What the player sees by cell: 0 darkness .. 255 the game's own picture. Valid until the next oni_light_init.
ONI_EXPORT const uint8_t* oni_light_shown()
{
	return engine.Shown();
}

// out: light the player sees of the colony's sources, light of the others, whether duplicants see the cell.
ONI_EXPORT void oni_light_seen_cell(int32_t cell, float* out)
{
	if (cell < 0 || cell >= engine.Width() * engine.Height())
	{
		out[0] = out[1] = out[2] = 0.0f;
		return;
	}
	out[0] = engine.SeenLux(cell);
	out[1] = engine.NaturalLux(cell);
	out[2] = engine.InSight(cell) ? 1.0f : 0.0f;
}

// How far the light of a source can reach from its cell, in cells (what the game must watch for changes).
ONI_EXPORT int32_t oni_light_reach(float range)
{
	return (int32_t)ceilf(1.5f * std::min(range, onilight::MaxRange)) + 1 + onilight::Steps;
}
