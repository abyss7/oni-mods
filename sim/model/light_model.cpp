// Light for the Blackout mode (native/oni_light.h) against the game's own light (LightGridManager, DiscreteShadowCaster),
// on a grid like a player's base: rooms dug in rock, pools of water, windows. Measures the cost of building all lights,
// of updating one (with redoing the light grid around it), by radius, and of the reflected light over the whole grid;
// checks that the updates give what a full computation gives; writes the light of a scene for pictures (light_model.py).
//
// The game's law (ported here for the timings and the pictures): visibility by recursive shadow casting within the
// radius; solid cells block and are not lit themselves; lux = Lux / max(1, round(0.5 * d)), d the distance "by cells"
// (steps), 0 past the radius; each lit cell is checked against the list already found (a linear search), as in the game.
//
//   g++ -O3 -ffp-contract=off -std=c++20 -o light_model sim/model/light_model.cpp && ./light_model out_dir [sse2]
// (sse2: the transport's SSE2 variant even if the processor has AVX)
//   python3 sim/model/light_model.py out_dir
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>

#include "../../native/oni_light.h"

using onilight::ElementOptics;
using onilight::Steps;

// Elements of the model, with reflectances from the mod's table and absorption from the game's data.
enum Element : uint16_t { Oxygen, Water, PollutedWater, Granite, SandStone, Katairite, Snow, Glass, ElementCount };

static float Absorb(float lightAbsorptionFactor, float defaultMass)
{
	return -logf(1.0f - std::min(lightAbsorptionFactor, 0.99f)) / defaultMass;
}

static std::vector<ElementOptics> Elements()
{
	std::vector<ElementOptics> t(ElementCount);
	t[Oxygen] = { 0.0f, 0.0f, onilight::Gas, {} };
	t[Water] = { 0.0f, Absorb(0.25f, 1000.0f), onilight::Liquid, {} };
	t[PollutedWater] = { 0.0f, Absorb(0.7f, 1000.0f), onilight::Liquid, {} };
	t[Granite] = { 0.25f, 0.0f, onilight::Solid, {} };
	t[SandStone] = { 0.40f, 0.0f, onilight::Solid, {} };
	t[Katairite] = { 0.08f, 0.0f, onilight::Solid, {} };
	t[Snow] = { 0.85f, 0.0f, onilight::Solid, {} };
	t[Glass] = { 0.08f, 0.0f, onilight::Solid, {} };
	return t;
}

struct World
{
	int w = 0, h = 0;
	std::vector<uint16_t> element, backwall, built;
	std::vector<float> mass;
	std::vector<uint8_t> properties, buildMasks;
	std::vector<int32_t> lightCount;

	void Init(int width, int height)
	{
		w = width;
		h = height;
		const size_t n = (size_t)w * h;
		element.assign(n, Granite);
		backwall.assign(n, Granite);
		built.assign(n, 0xFFFF);
		mass.assign(n, 1000.0f);
		properties.assign(n, 0);
		buildMasks.assign(n, 1);
		lightCount.assign(n, 0);
	}
	void Set(int x, int y, Element e, uint16_t back = 0xFFFF)
	{
		const size_t c = (size_t)y * w + x;
		element[c] = e;
		backwall[c] = back;
		mass[c] = e == Oxygen ? 1.8f : 1000.0f;
		properties[c] = e == Glass ? 0x10 : 0;
		buildMasks[c] = e == Granite || e == SandStone || e == Katairite || e == Snow || e == Glass ? 1 : 0;
	}
	bool In(int x, int y) const { return x >= 0 && y >= 0 && x < w && y < h; }
	// What blocks the game's light: solid and not transparent.
	bool Opaque(int x, int y) const { return !In(x, y) || ((buildMasks[(size_t)y * w + x] & 1) && !(properties[(size_t)y * w + x] & 0x10)); }
	onilight::GridView View()
	{
		return { w, h, element.data(), mass.data(), properties.data(), backwall.data(), buildMasks.data(), built.data(), lightCount.data() };
	}
};

// A base: granite with rooms 20 x 8 in a lattice, one-cell walls, doorways, a window in some walls, a pool of water in a
// quarter of the rooms, a staircase of blocks in some; granite back walls.
static void MakeBase(World& world, int w, int h, unsigned seed)
{
	world.Init(w, h);
	srand(seed);
	for (int ry = 4; ry + 9 < h; ry += 10)
	{
		for (int rx = 4; rx + 21 < w; rx += 22)
		{
			if (rand() % 5 == 0) continue; // undug rock
			for (int y = ry; y < ry + 8; y++) for (int x = rx; x < rx + 20; x++) world.Set(x, y, Oxygen, Granite);
			if (rand() % 4 == 0) for (int y = ry; y < ry + 2; y++) for (int x = rx; x < rx + 20; x++) world.Set(x, y, Water, Granite);
			if (rand() % 3 == 0) for (int k = 0; k < 4; k++) world.Set(rx + 10 + k, ry + k, Granite);
			// Doorway to the right room and a window above it.
			for (int y = ry; y < ry + 2; y++) { world.Set(rx + 20, y, Oxygen, Granite); world.Set(rx + 21, y, Oxygen, Granite); }
			if (rand() % 2 == 0) { world.Set(rx + 20, ry + 5, Glass); world.Set(rx + 21, ry + 5, Glass); }
		}
	}
}

// ---------- The game's light ----------

namespace game
{
	struct Source { int x, y, lux, range; bool cone; };

	// Recursive shadow casting over eight octants (the same method as the game's DiscreteShadowCaster).
	static void Scan(const World& world, int ox, int oy, int range, int row, double start, double end, int xx, int xy, int yx, int yy, std::vector<int>& out)
	{
		if (start < end) return;
		double nextStart = start;
		for (int i = row; i <= range; i++)
		{
			bool blocked = false;
			for (int dx = -i, dy = -i; dx <= 0; dx++)
			{
				double l = (dx - 0.5) / (dy + 0.5), r = (dx + 0.5) / (dy - 0.5);
				if (start < r) continue;
				if (end > l) break;
				int x = ox + dx * xx + dy * xy, y = oy + dx * yx + dy * yy;
				bool solid = world.Opaque(x, y);
				if (dx * dx + dy * dy <= range * range && world.In(x, y) && !solid)
				{
					int cell = y * world.w + x;
					// As in the game: the found cells are a list, each new one is looked for in it.
					if (std::find(out.begin(), out.end(), cell) == out.end()) out.push_back(cell);
				}
				if (blocked)
				{
					if (solid) { nextStart = r; continue; }
					blocked = false;
					start = nextStart;
				}
				else if (solid && i < range)
				{
					blocked = true;
					Scan(world, ox, oy, range, i + 1, start, l, xx, xy, yx, yy, out);
					nextStart = r;
				}
			}
			if (blocked) break;
		}
	}

	static const int Mult[4][8] = { { 1, 0, 0, -1, -1, 0, 0, 1 }, { 0, 1, -1, 0, 0, -1, 1, 0 }, { 0, 1, 1, 0, 0, -1, -1, 0 }, { 1, 0, 0, 1, -1, 0, 0, -1 } };

	struct Lit { std::vector<int> cells; std::vector<int> lux; };

	static void Add(const World& world, const Source& s, Lit& lit, std::vector<int>& grid)
	{
		lit.cells.clear();
		lit.cells.push_back(s.y * world.w + s.x);
		for (int oct = 0; oct < 8; oct++) Scan(world, s.x, s.y, s.range, 1, 1.0, 0.0, Mult[0][oct], Mult[1][oct], Mult[2][oct], Mult[3][oct], lit.cells);
		if (s.cone) // the game's cone: the quarter below the source (|dx| <= |dy|, dy < 0)
			lit.cells.erase(std::remove_if(lit.cells.begin() + 1, lit.cells.end(), [&](int c) { int dx = c % world.w - s.x, dy = c / world.w - s.y; return !(dy < 0 && abs(dx) <= -dy); }), lit.cells.end());
		lit.lux.resize(lit.cells.size());
		for (size_t k = 0; k < lit.cells.size(); k++)
		{
			int cell = lit.cells[k], x = cell % world.w, y = cell / world.w;
			int d = abs(x - s.x) + abs(y - s.y);
			int falloff = std::max(1, (int)nearbyintf(0.5f * std::max(d, 1))); // the game's RoundToInt: halves to even
			lit.lux[k] = s.lux / falloff;
			grid[cell] += lit.lux[k];
		}
	}

	static void Remove(const Lit& lit, std::vector<int>& grid)
	{
		for (size_t k = 0; k < lit.cells.size(); k++) grid[lit.cells[k]] -= lit.lux[k];
	}
}

static onilight::SourceDesc Desc(int x, int y, int lux, float range, bool cone)
{
	return { x, y, (float)lux, range, 0.5f, cone ? onilight::Cone : onilight::Circle, onilight::South, 0 };
}

// A scene for the pictures: four equal rooms under a ceiling light (C, 1800 lux, radius 8, lighting the half below) with
// a floor lamp (L, 1000 lux, radius 4) by a doorway that leads round a bend; a pillar; a pool. Sandstone (S), abyssalite
// (A), snow (W), each with a back wall of the same; granite (G) with no back wall. ~ water, % polluted water.
static const char* const Scene[] = {
	"SSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
	"SS..............C.............SSSSSSSSSSAA..............C.............AAAAAAAAAA",
	"SS............................SSSS...SSSAA............................AAAA...AAA",
	"SS............................SSSS...SSSAA............................AAAA...AAA",
	"SS.........S..................SSSS...SSSAA.........A..................AAAA...AAA",
	"SS.........S..................SSSS...SSSAA.........A..................AAAA...AAA",
	"SS.........S..................SSSS...SSSAA.........A..................AAAA...AAA",
	"SS............................SSSS...SSSAA............................AAAA...AAA",
	"SS............................SSSS...SSSAA............................AAAA...AAA",
	"SS..........~~~~~~~~.................SSSAA..........~~~~~~~~.................AAA",
	"SS..........~~~~~~~~......L..........SSSAA..........~~~~~~~~......L..........AAA",
	"SS..........~~~~~~~~.................SSSAA..........~~~~~~~~.................AAA",
	"SSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
	"SSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
	"WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG",
	"WW..............C.............WWWWWWWWWWGG..............C.............GGGGGGGGGG",
	"WW............................WWWW...WWWGG............................GGGG...GGG",
	"WW............................WWWW...WWWGG............................GGGG...GGG",
	"WW.........W..................WWWW...WWWGG.........G..................GGGG...GGG",
	"WW.........W..................WWWW...WWWGG.........G..................GGGG...GGG",
	"WW.........W..................WWWW...WWWGG.........G..................GGGG...GGG",
	"WW............................WWWW...WWWGG............................GGGG...GGG",
	"WW............................WWWW...WWWGG............................GGGG...GGG",
	"WW..........%%%%%%%%.................WWWGG..........%%%%%%%%.................GGG",
	"WW..........%%%%%%%%......L..........WWWGG..........%%%%%%%%......L..........GGG",
	"WW..........%%%%%%%%.................WWWGG..........%%%%%%%%.................GGG",
	"WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG",
	"WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG",
};

static Element Material(char m)
{
	switch (m)
	{
	case 'S': return SandStone;
	case 'A': return Katairite;
	case 'W': return Snow;
	default: return Granite;
	}
}

static void WriteScene(const std::string& out)
{
	const int h = (int)(sizeof(Scene) / sizeof(Scene[0])), w = 80;
	World world;
	world.Init(w, h);
	std::vector<game::Source> sources;
	for (int row = 0; row < h; row++)
		for (int x = 0; x < w; x++)
		{
			const char c = Scene[row][x];
			const int y = h - 1 - row;
			const char room = Scene[row < 14 ? 0 : 14][x < 40 ? 0 : 40];
			const uint16_t back = room == 'G' ? 0xFFFF : Material(room); // the granite room has no back wall
			if (c == '~') world.Set(x, y, Water, back);
			else if (c == '%') world.Set(x, y, PollutedWater, back);
			else if (c == '.' || c == 'C' || c == 'L') world.Set(x, y, Oxygen, back);
			else world.Set(x, y, Material(c));
			if (c == 'L') sources.push_back({ x, y, 1000, 4, false });
			if (c == 'C') sources.push_back({ x, y, 1800, 8, true });
		}
	onilight::Engine engine;
	engine.Init(w, h);
	const std::vector<ElementOptics> elements = Elements();
	engine.SetElements(elements.data(), (int)elements.size());
	onilight::GridView view = world.View();
	std::vector<int> g(w * h, 0);
	for (size_t k = 0; k < sources.size(); k++)
	{
		game::Lit gl;
		game::Add(world, sources[k], gl, g);
		const game::Source& s = sources[k];
		engine.Add(view, (int)k, Desc(s.x, s.y, s.lux, (float)s.range, s.cone));
	}
	FILE* f = fopen((out + "/light_piece.txt").c_str(), "w");
	fprintf(f, "%d %d\n", w, h);
	for (int y = h - 1; y >= 0; y--)
		for (int x = 0; x < w; x++)
		{
			const int c = y * w + x;
			fprintf(f, "%d %c %d %.1f %.1f\n", Scene[h - 1 - y][x] == '~' || Scene[h - 1 - y][x] == '%' ? 2 : world.Opaque(x, y) ? 0 : 1,
				Scene[h - 1 - y][x], g[c], engine.Direct(c), (float)world.lightCount[c]);
		}
	for (const game::Source& s : sources) fprintf(f, "source %d %d\n", s.x, h - 1 - s.y);
	fclose(f);
	// Mean light of each room (open cells), and of the shaft up the bend, for the report.
	for (int q = 0; q < 4; q++)
	{
		const int x0 = q % 2 * 40, row0 = q / 2 * 14;
		double sumG = 0, sumD = 0, sumT = 0, bendD = 0, bendT = 0;
		int cells = 0, bend = 0;
		for (int row = row0 + 1; row < row0 + 12; row++)
			for (int x = x0 + 2; x < x0 + 37; x++)
			{
				const int c = (h - 1 - row) * w + x;
				if (world.Opaque(x, h - 1 - row)) continue;
				if (x >= x0 + 34 && row <= row0 + 8) { bendD += engine.Direct(c); bendT += world.lightCount[c]; bend++; continue; }
				sumG += g[c]; sumD += engine.Direct(c); sumT += world.lightCount[c]; cells++;
			}
		printf("room %c: mean lux  game %.0f, direct %.0f, with reflected %.0f;  shaft up the bend  direct %.0f, with reflected %.1f\n",
			Scene[row0][x0], sumG / cells, sumD / cells, sumT / cells, bendD / bend, bendT / bend);
	}
	// The corridor and the shaft, cell by cell (with reflected light): from the doorway right, then up.
	for (int q = 0; q < 4; q++)
	{
		const int x0 = q % 2 * 40, row0 = q / 2 * 14;
		printf("room %c corridor:", Scene[row0][x0]);
		for (int x = x0 + 30; x <= x0 + 35; x++) printf(" %d", world.lightCount[(h - 1 - (row0 + 10)) * w + x]);
		printf("  | up:");
		for (int row = row0 + 9; row >= row0 + 2; row--) printf(" %d", world.lightCount[(h - 1 - row) * w + x0 + 35]);
		printf("\n");
	}
}

static double Now()
{
	return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

int main(int argc, char** argv)
{
	const std::string out = argc > 1 ? argv[1] : ".";
	const std::string limit = argc > 2 ? argv[2] : "";
	const int variant = limit == "sse2" ? 0 : 1;
	World world;
	MakeBase(world, 256, 384, 7);
	std::vector<int> open;
	for (int i = 0; i < world.w * world.h; i++) if (world.element[i] == Oxygen) open.push_back(i);
	const std::vector<ElementOptics> elements = Elements();
	printf("grid %d x %d, open cells %zu\n", world.w, world.h, open.size());
	onilight::GridView view = world.View();
	for (int count : { 200, 1000, 5000 })
	{
		srand(11);
		std::vector<game::Source> sources;
		for (int k = 0; k < count; k++)
		{
			const int cell = open[rand() % open.size()];
			const bool big = rand() % 2 == 0; // lamps (1000 lux, radius 4) and ceiling lights (1800 lux, radius 8, the half below)
			sources.push_back({ cell % world.w, cell / world.w, big ? 1800 : 1000, big ? 8 : 4, big });
		}
		std::vector<int> gameGrid(world.w * world.h, 0);
		std::vector<game::Lit> gameLit(count);
		double t0 = Now();
		for (int k = 0; k < count; k++) game::Add(world, sources[k], gameLit[k], gameGrid);
		const double gameBuild = Now() - t0;
		onilight::Engine engine;
		engine.Init(world.w, world.h);
		engine.SetElements(elements.data(), (int)elements.size());
		engine.LimitVariant(variant);
		if (count == 200) printf("transport: %s\n", engine.Variant());
		std::fill(world.lightCount.begin(), world.lightCount.end(), 0);
		t0 = Now();
		for (int k = 0; k < count; k++) engine.Add(view, k, Desc(sources[k].x, sources[k].y, sources[k].lux, (float)sources[k].range, sources[k].cone));
		const double newAdd = Now() - t0;
		// Updating one light (remove and add again), as when a light bug moves or a wall is built next to a lamp.
		const int updates = 2000;
		t0 = Now();
		for (int u = 0; u < updates; u++)
		{
			const int k = u * 7919 % count;
			game::Remove(gameLit[k], gameGrid);
			game::Add(world, sources[k], gameLit[k], gameGrid);
		}
		const double gameUpdate = (Now() - t0) / updates * 1000.0;
		t0 = Now();
		for (int u = 0; u < updates; u++)
		{
			const int k = u * 7919 % count;
			engine.Remove(view, k, false);
			engine.Add(view, k, Desc(sources[k].x, sources[k].y, sources[k].lux, (float)sources[k].range, sources[k].cone));
		}
		const double newUpdate = (Now() - t0) / updates * 1000.0;
		// A change of brightness alone (a light bug dimming): taken out with keep and put back rescaled.
		t0 = Now();
		for (int u = 0; u < updates; u++)
		{
			const int k = u * 7919 % count;
			engine.Remove(view, k, true);
			engine.Add(view, k, Desc(sources[k].x, sources[k].y, sources[k].lux - (u & 1), (float)sources[k].range, sources[k].cone));
		}
		const double newDim = (Now() - t0) / updates * 1000.0;
		t0 = Now();
		for (int u = 0; u < updates; u++)
		{
			const int k = u * 7919 % count;
			engine.Remove(view, k, true);
			engine.Add(view, k, Desc(sources[k].x, sources[k].y, sources[k].lux, (float)sources[k].range, sources[k].cone));
		}
		float check[6];
		engine.Check(view, { 0, 0, world.w - 1, world.h - 1 }, check);
		const onilight::Counters& st = engine.Stats();
		printf("%d lights: build all  game %.2f ms, new %.2f ms;  update one  game %.1f us, new %.1f us, brightness only %.2f us;  "
			"vs computing again: direct %.4f, light grid %.0f lux, vs all reflected light at once %.0f lux;  transport %.0f cell-steps per trace\n",
			count, gameBuild, newAdd, gameUpdate, newUpdate, newDim, check[0], check[1], check[2], (double)st.transportCells / st.traces);
	}
	// Cost of one light by its radius.
	for (float range : { 4.0f, 8.0f, 16.0f })
	{
		onilight::Engine engine;
		engine.Init(world.w, world.h);
		engine.SetElements(elements.data(), (int)elements.size());
		engine.LimitVariant(variant);
		srand(13);
		std::vector<int> gameGrid(world.w * world.h, 0);
		game::Lit gl;
		const int count = 2000;
		double gameTime = 0, newTime = 0;
		for (int k = 0; k < count; k++)
		{
			const int cell = open[rand() % open.size()];
			const game::Source s{ cell % world.w, cell / world.w, 1800, (int)range, false };
			double t0 = Now();
			game::Add(world, s, gl, gameGrid);
			gameTime += Now() - t0;
			t0 = Now();
			engine.Add(view, 0, Desc(s.x, s.y, s.lux, range, false));
			newTime += Now() - t0;
		}
		printf("radius %.0f: one light  game %.1f us, new %.1f us\n", range, gameTime / count * 1000.0, newTime / count * 1000.0);
	}
	WriteScene(out);
	return 0;
}
