// Prototype of light for the Blackout mode, against the game's own light (LightGridManager, DiscreteShadowCaster), on a
// grid like a player's base: rooms dug in rock, pools of liquid, windows. Measures the cost of building all lights and of
// updating one, and writes the light of a piece of the grid for pictures (light_model.py).
//
//  game: the game's law. Visibility by recursive shadow casting within the radius (straight-line distance); solid cells
//        block and are not lit themselves; lux = Lux / max(1, round(0.5 * d)), d the distance "by cells" (steps), 0 past
//        the radius. As in the game, each lit cell is checked against the list already found (a linear search).
//  new:  light along straight rays from the source: each cell takes the light of the cell before it on the ray from the
//        source times what that cell lets through (gas 1, liquid 0.6, a window 0.85, solid 0); a diagonal step between two
//        cells takes the better of the two side cells, so light does not slip through a diagonal of blocks. The ray tree
//        (which cell comes before which) is computed once for all sources. Lux = Lux / max(1, 0.5 * r), r the straight
//        distance, smoothly faded to 0 between R and 1.5 R instead of the cut. Solid cells take the light that reaches
//        their face (lit walls). Then scattered light over the grid (Scatter): a share of the direct light walks a few
//        cells through open cells, so a doorway's shade and the bend of a corridor are in half-light rather than black.
//
//   g++ -O2 -march=native -std=c++20 -o light_model sim/model/light_model.cpp && ./light_model out_dir
//   python3 sim/model/light_model.py out_dir
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

enum Kind : uint8_t { Rock, Gas, Liquid, Window };

struct World
{
	int w = 0, h = 0;
	std::vector<uint8_t> kind;
	bool Solid(int x, int y) const { return x < 0 || y < 0 || x >= w || y >= h || kind[y * w + x] == Rock || kind[y * w + x] == Window; }
	bool Opaque(int x, int y) const { return x < 0 || y < 0 || x >= w || y >= h || kind[y * w + x] == Rock; }
	float Through(int x, int y) const
	{
		if (x < 0 || y < 0 || x >= w || y >= h) return 0.0f;
		switch (kind[y * w + x]) { case Gas: return 1.0f; case Liquid: return 0.6f; case Window: return 0.85f; default: return 0.0f; }
	}
};

// A base: rock with rooms 20 x 8 in a lattice, one-cell walls, doorways, a window in some walls, a pool of liquid in a quarter of
// the rooms, a staircase of blocks in some.
static World MakeBase(int w, int h, unsigned seed)
{
	World world;
	world.w = w;
	world.h = h;
	world.kind.assign((size_t)w * h, Rock);
	srand(seed);
	for (int ry = 4; ry + 9 < h; ry += 10)
	{
		for (int rx = 4; rx + 21 < w; rx += 22)
		{
			if (rand() % 5 == 0) continue; // undug rock
			for (int y = ry; y < ry + 8; y++) for (int x = rx; x < rx + 20; x++) world.kind[y * w + x] = Gas;
			if (rand() % 4 == 0) for (int y = ry; y < ry + 2; y++) for (int x = rx; x < rx + 20; x++) world.kind[y * w + x] = Liquid;
			if (rand() % 3 == 0) for (int k = 0; k < 4; k++) world.kind[(ry + k) * w + rx + 10 + k] = Rock;
			// Doorway to the right room and a window above it.
			for (int y = ry; y < ry + 2; y++) world.kind[y * w + rx + 20] = world.kind[y * w + rx + 21] = Gas;
			if (rand() % 2 == 0) world.kind[(ry + 5) * w + rx + 20] = world.kind[(ry + 5) * w + rx + 21] = Window;
		}
	}
	return world;
}

struct Source { int x, y, lux; float range; };

// ---------- The game's light ----------

namespace game
{
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
				bool solid = world.Solid(x, y) && !(x >= 0 && y >= 0 && x < world.w && y < world.h && world.kind[y * world.w + x] == Window);
				if (dx * dx + dy * dy <= range * range && x >= 0 && y >= 0 && x < world.w && y < world.h && !solid)
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
		for (int oct = 0; oct < 8; oct++) Scan(world, s.x, s.y, (int)s.range, 1, 1.0, 0.0, Mult[0][oct], Mult[1][oct], Mult[2][oct], Mult[3][oct], lit.cells);
		lit.lux.resize(lit.cells.size());
		for (size_t k = 0; k < lit.cells.size(); k++)
		{
			int cell = lit.cells[k], x = cell % world.w, y = cell / world.w;
			int d = std::abs(x - s.x) + std::abs(y - s.y);
			int falloff = std::max(1, (int)std::nearbyint(0.5f * std::max(d, 1))); // the game's RoundToInt: halves to even
			lit.lux[k] = s.lux / falloff;
			grid[cell] += lit.lux[k];
		}
	}

	static void Remove(const Lit& lit, std::vector<int>& grid)
	{
		for (size_t k = 0; k < lit.cells.size(); k++) grid[lit.cells[k]] -= lit.lux[k];
	}
}

// ---------- The new light ----------

namespace light
{
	// The ray tree for one radius: offsets in order of distance; each has the offset before it on the straight ray from the
	// source (its parent) and, for a diagonal step, the two side cells; and the weight of the falloff.
	struct Tree
	{
		std::vector<int16_t> dx, dy;
		std::vector<int32_t> parent;
		std::vector<uint8_t> diagonal;
		std::vector<float> weight;
	};

	static float Fade(float r, float range)
	{
		const float end = 1.5f * range;
		if (r <= range) return 1.0f;
		if (r >= end) return 0.0f;
		const float t = (r - range) / (end - range);
		return 1.0f - t * t * (3.0f - 2.0f * t);
	}

	static Tree Build(float range)
	{
		Tree tree;
		const int reach = (int)std::ceil(1.5f * range);
		struct Off { int dx, dy; float r; };
		std::vector<Off> offs;
		for (int dy = -reach; dy <= reach; dy++)
			for (int dx = -reach; dx <= reach; dx++)
			{
				const float r = std::sqrt((float)(dx * dx + dy * dy));
				if (r <= 1.5f * range) offs.push_back({ dx, dy, r });
			}
		std::sort(offs.begin(), offs.end(), [](const Off& a, const Off& b) { return a.r < b.r || (a.r == b.r && (a.dy < b.dy || (a.dy == b.dy && a.dx < b.dx))); });
		std::vector<int> index((2 * reach + 1) * (2 * reach + 1), -1);
		auto at = [&](int dx, int dy) -> int& { return index[(dy + reach) * (2 * reach + 1) + dx + reach]; };
		for (size_t i = 0; i < offs.size(); i++) at(offs[i].dx, offs[i].dy) = (int)i;
		for (const Off& o : offs)
		{
			tree.dx.push_back((int16_t)o.dx);
			tree.dy.push_back((int16_t)o.dy);
			// The cell before this one on the straight ray: the last cell of the ray from the source before reaching it.
			int px = 0, py = 0;
			if (o.dx != 0 || o.dy != 0)
			{
				const int steps = std::max(std::abs(o.dx), std::abs(o.dy));
				const float sx = (float)o.dx / steps, sy = (float)o.dy / steps;
				px = (int)std::lround(sx * (steps - 1));
				py = (int)std::lround(sy * (steps - 1));
			}
			tree.parent.push_back(o.dx == 0 && o.dy == 0 ? -1 : at(px, py));
			tree.diagonal.push_back((uint8_t)(px != o.dx && py != o.dy));
			tree.weight.push_back(Fade(o.r, range) / std::max(1.0f, 0.5f * o.r));
		}
		return tree;
	}

	struct Lit { std::vector<int> cells; std::vector<float> lux; };

	// Light of one source, along the ray tree.
	static void Add(const World& world, const Tree& tree, const Source& s, Lit& lit, std::vector<float>& grid, std::vector<float>& reach)
	{
		lit.cells.clear();
		lit.lux.clear();
		const size_t n = tree.dx.size();
		reach.resize(n);
		for (size_t i = 0; i < n; i++)
		{
			const int x = s.x + tree.dx[i], y = s.y + tree.dy[i];
			float t = 1.0f;
			const int p = tree.parent[i];
			if (p >= 0)
			{
				const int px = s.x + tree.dx[p], py = s.y + tree.dy[p];
				t = reach[p] * (p == 0 ? 1.0f : world.Through(px, py));
				if (tree.diagonal[i]) t *= std::max(world.Through(x, py), world.Through(px, y));
			}
			reach[i] = t; // light arriving at this cell (before what the cell itself lets through)
			if (t <= 0.0f || x < 0 || y < 0 || x >= world.w || y >= world.h) continue;
			const float lux = s.lux * tree.weight[i] * t;
			if (lux < 1.0f) continue;
			lit.cells.push_back(y * world.w + x);
			lit.lux.push_back(lux);
		}
		for (size_t k = 0; k < lit.cells.size(); k++) grid[lit.cells[k]] += lit.lux[k];
	}

	static constexpr float Albedo = 0.3f; // how much of the direct light comes back as scattered light, in an open room
	static constexpr int Steps = 6;       // how far the scattered light goes from the direct light, in cells

	// Scattered light over the grid, from the sum of the direct light: the direct light of the open cells walks a few steps
	// to the neighbours (each step a quarter to each, times what the cell lets through), and a share of the walk is the
	// scattered light. It goes round corners and into a doorway's shade and fades along a corridor; it does not cross solid
	// cells, so a lit wall does not light the far side. Linear in the direct light, so it can be redone for a part of the
	// grid (the changed lights' area widened by Steps).
	static void Scatter(const World& world, const std::vector<float>& direct, std::vector<float>& scattered, std::vector<float>& a, std::vector<float>& b)
	{
		const int w = world.w, h = world.h;
		a.assign(direct.size(), 0.0f);
		b.assign(direct.size(), 0.0f);
		scattered.assign(direct.size(), 0.0f);
		for (size_t c = 0; c < direct.size(); c++) a[c] = world.kind[c] == Rock ? 0.0f : direct[c];
		for (int step = 0; step < Steps; step++)
		{
			for (int y = 0; y < h; y++)
				for (int x = 0; x < w; x++)
				{
					const int c = y * w + x;
					const float through = world.Through(x, y);
					if (through <= 0.0f) { b[c] = 0.0f; continue; }
					const float sum = (x > 0 ? a[c - 1] : 0.0f) + (x + 1 < w ? a[c + 1] : 0.0f) + (y > 0 ? a[c - w] : 0.0f) + (y + 1 < h ? a[c + w] : 0.0f);
					b[c] = 0.25f * through * sum;
					scattered[c] += b[c];
				}
			std::swap(a, b);
		}
		for (float& v : scattered) v *= Albedo / Steps;
	}

	static void Remove(const Lit& lit, std::vector<float>& grid)
	{
		for (size_t k = 0; k < lit.cells.size(); k++) grid[lit.cells[k]] -= lit.lux[k];
	}
}

// A hand-made scene for the pictures: # rock, . gas, ~ liquid, | window; L a floor lamp (1000 lux, radius 4), C a ceiling
// light (1800 lux, radius 8) in a gas cell.
static const char* const Scene[] = {
	"#####################################################################",
	"#.........C.........................#################.......C.......#",
	"#...................................#################...............#",
	"#.......#....#......................................................#",
	"#.......#....#......................................................#",
	"#.......#....#.....................#################................#",
	"#..........................#..#....#################................#",
	"#.....................L...#..#.....#################................#",
	"#........................#..#......#################.......##.......#",
	"#...................###.............................##.....##.......#",
	"#..................#####............................##..............#",
	"#.................#######...........................................#",
	"#####################################################################",
	"#####################################################################",
	"#.......................C....#......................#################",
	"#............................#......................#################",
	"#............................|......................#################",
	"#............................|......................................#",
	"#............................|......................#################",
	"#.............L..............#..................C...#...............#",
	"#............................#......................#...............#",
	"#.........~~~~~~~~~~~~~~.....#......................................#",
	"#.........~~~~~~~~~~~~~~............................................#",
	"#.........~~~~~~~~~~~~~~............................................#",
	"#####################################################################",
};

static void WriteScene(const std::string& out, const light::Tree& tree4, const light::Tree& tree8)
{
	const int h = (int)(sizeof(Scene) / sizeof(Scene[0])), w = 69;
	World world;
	world.w = w;
	world.h = h;
	world.kind.assign((size_t)w * h, Rock);
	std::vector<Source> sources;
	for (int row = 0; row < h; row++)
		for (int x = 0; x < w; x++)
		{
			const char c = Scene[row][x];
			const int y = h - 1 - row;
			world.kind[y * w + x] = c == '#' ? Rock : c == '~' ? Liquid : c == '|' ? Window : Gas;
			if (c == 'L') sources.push_back({ x, y, 1000, 4.0f });
			if (c == 'C') sources.push_back({ x, y, 1800, 8.0f });
		}
	std::vector<int> g(w * h, 0);
	std::vector<float> nw(w * h, 0.0f), reach;
	for (const Source& s : sources)
	{
		game::Lit gl;
		light::Lit nl;
		game::Add(world, s, gl, g);
		light::Add(world, s.range > 5 ? tree8 : tree4, s, nl, nw, reach);
	}
	std::vector<float> scattered, a, b;
	light::Scatter(world, nw, scattered, a, b);
	FILE* f = fopen((out + "/light_piece.txt").c_str(), "w");
	fprintf(f, "%d %d\n", w, h);
	for (int y = h - 1; y >= 0; y--)
		for (int x = 0; x < w; x++)
		{
			const int c = y * w + x;
			fprintf(f, "%d %d %.1f %.1f\n", (int)world.kind[c], g[c], nw[c], nw[c] + scattered[c]);
		}
	for (const Source& s : sources) fprintf(f, "source %d %d\n", s.x, h - 1 - s.y);
	fclose(f);
}

static double Now()
{
	return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

int main(int argc, char** argv)
{
	const std::string out = argc > 1 ? argv[1] : ".";
	const World world = MakeBase(256, 384, 7);
	std::vector<int> open;
	for (int i = 0; i < world.w * world.h; i++) if (world.kind[i] == Gas) open.push_back(i);
	const light::Tree tree4 = light::Build(4.0f), tree8 = light::Build(8.0f);
	printf("grid %d x %d, open cells %zu; ray tree: radius 4 %zu offsets, radius 8 %zu offsets\n", world.w, world.h, open.size(), tree4.dx.size(), tree8.dx.size());
	for (int count : { 200, 1000, 5000 })
	{
		srand(11);
		std::vector<Source> sources;
		for (int k = 0; k < count; k++)
		{
			const int cell = open[rand() % open.size()];
			const bool big = rand() % 2 == 0; // lamps (1000 lux, radius 4) and ceiling lights (1800 lux, radius 8)
			sources.push_back({ cell % world.w, cell / world.w, big ? 1800 : 1000, big ? 8.0f : 4.0f });
		}
		std::vector<int> gameGrid(world.w * world.h, 0);
		std::vector<game::Lit> gameLit(count);
		double t0 = Now();
		for (int k = 0; k < count; k++) game::Add(world, sources[k], gameLit[k], gameGrid);
		const double gameBuild = Now() - t0;
		std::vector<float> newGrid(world.w * world.h, 0.0f), reach;
		std::vector<light::Lit> newLit(count);
		t0 = Now();
		for (int k = 0; k < count; k++) light::Add(world, sources[k].range > 5 ? tree8 : tree4, sources[k], newLit[k], newGrid, reach);
		const double newBuild = Now() - t0;
		// Updating one light (remove and add again), as when a light bug moves or a wall is built next to a lamp.
		const int updates = 2000;
		t0 = Now();
		for (int u = 0; u < updates; u++)
		{
			const int k = u % count;
			game::Remove(gameLit[k], gameGrid);
			game::Add(world, sources[k], gameLit[k], gameGrid);
		}
		const double gameUpdate = (Now() - t0) / updates * 1000.0;
		t0 = Now();
		for (int u = 0; u < updates; u++)
		{
			const int k = u % count;
			light::Remove(newLit[k], newGrid);
			light::Add(world, sources[k].range > 5 ? tree8 : tree4, sources[k], newLit[k], newGrid, reach);
		}
		const double newUpdate = (Now() - t0) / updates * 1000.0;
		size_t gameCells = 0, newCells = 0;
		for (auto& l : gameLit) gameCells += l.cells.size();
		for (auto& l : newLit) newCells += l.cells.size();
		int gameLitCells = 0, newLitCells = 0;
		for (int i = 0; i < world.w * world.h; i++) { gameLitCells += gameGrid[i] > 0; newLitCells += newGrid[i] >= 1.0f; }
		std::vector<float> scattered, a, b;
		t0 = Now();
		light::Scatter(world, newGrid, scattered, a, b);
		const double scatter = Now() - t0;
		printf("%d lights: scattered light over the whole grid %.2f ms\n", count, scatter);
		printf("%d lights: build all  game %.2f ms, new %.2f ms;  update one  game %.1f us, new %.1f us;  cells per light  game %.0f, new %.0f;  "
			"lit cells  game %d, new %d;  memory for the lights  game %.1f MB, new %.1f MB\n",
			count, gameBuild, newBuild, gameUpdate, newUpdate, (double)gameCells / count, (double)newCells / count, gameLitCells, newLitCells,
			gameCells * 8.0 / 1e6, newCells * 8.0 / 1e6);
	}
	// Cost of one light by its radius: the game's light grows with the lit area, the new one with the area to 1.5 R.
	for (float range : { 4.0f, 8.0f, 16.0f })
	{
		const light::Tree tree = light::Build(range);
		srand(13);
		std::vector<int> gameGrid(world.w * world.h, 0);
		std::vector<float> newGrid(world.w * world.h, 0.0f), reach;
		game::Lit gl;
		light::Lit nl;
		const int count = 2000;
		double gameTime = 0, newTime = 0;
		for (int k = 0; k < count; k++)
		{
			const int cell = open[rand() % open.size()];
			const Source s{ cell % world.w, cell / world.w, 1800, range };
			double t0 = Now();
			game::Add(world, s, gl, gameGrid);
			gameTime += Now() - t0;
			t0 = Now();
			light::Add(world, tree, s, nl, newGrid, reach);
			newTime += Now() - t0;
		}
		printf("radius %.0f: one light  game %.1f us, new %.1f us (ray tree %zu offsets)\n", range, gameTime / count * 1000.0, newTime / count * 1000.0, tree.dx.size());
	}
	WriteScene(out, tree4, tree8);
	return 0;
}
