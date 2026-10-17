// Prototype of light for the Blackout mode, against the game's own light (LightGridManager, DiscreteShadowCaster), on a
// grid like a player's base: rooms dug in rock, pools of liquid, windows. Measures the cost of building all lights and of
// updating one, and writes the light of a scene for pictures (light_model.py).
//
//  game: the game's law. Visibility by recursive shadow casting within the radius (straight-line distance); solid cells
//        block and are not lit themselves; lux = Lux / max(1, round(0.5 * d)), d the distance "by cells" (steps), 0 past
//        the radius. As in the game, each lit cell is checked against the list already found (a linear search).
//  new:  light along straight rays from the source: each cell takes the light of the cell before it on the ray from the
//        source times what that cell lets through; a diagonal step between two cells takes the better of the two side
//        cells, so light does not slip through a diagonal of blocks. The ray tree (which cell comes before which) is
//        computed once for all sources. Lux = Lux / max(1, 0.5 * r), r the straight distance, smoothly faded to 0 between R
//        and 1.5 R instead of the cut. Solid cells take the light that reaches their face (lit walls).
//        What a cell lets through: gas 1; a liquid by its element, by the law the game's sunlight uses for the share of a
//        full cell (1 - lightAbsorptionFactor), and as exp(-a * mass / defaultMass) for any mass (Beer-Lambert); a window
//        0.9; solid 0.
//        Reflected light: a lit solid face sends lux x reflectance of its material back into the open cell the ray came
//        from (so it never leaks through the wall); each lit open cell sends lux x reflectance of its back wall (none
//        reflects nothing). The reflected light then walks a few cells through open cells (Scatter), standing for the
//        many bounces after the first: the shade of a doorway and the bend of a corridor are in half-light rather than
//        black, and a pale room is brighter than a dark one under the same lamp.
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

// Reflectance of visible light: a few materials of the full table (which lives with the mod).
namespace reflectance
{
	constexpr float Granite = 0.25f, SandStone = 0.40f, Katairite = 0.08f, Snow = 0.85f;
}

// Share of light a full cell of a liquid lets through, from the game's lightAbsorptionFactor, for any mass (Beer-Lambert).
static float LiquidThrough(float absorption, float mass, float defaultMass)
{
	const float a = -std::log(1.0f - std::min(absorption, 0.99f));
	return std::exp(-a * mass / defaultMass);
}

struct World
{
	int w = 0, h = 0;
	std::vector<uint8_t> kind;
	std::vector<float> through; // share of light the cell lets through
	std::vector<float> albedo;  // a solid cell: reflectance of its material; an open one: of its back wall (0 without one)
	std::vector<float> walls;   // an open cell: a quarter of the reflectance of each solid side, what scattered light keeps on the walls

	void Init(int width, int height)
	{
		w = width;
		h = height;
		kind.assign((size_t)w * h, Rock);
		through.assign((size_t)w * h, 0.0f);
		albedo.assign((size_t)w * h, reflectance::Granite);
	}
	// After the cells are set (and again around a cell that changes).
	void Finish()
	{
		walls.assign((size_t)w * h, 0.0f);
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++)
			{
				if (through[y * w + x] <= 0.0f) continue;
				float sum = 0.0f;
				const int nx[4] = { x - 1, x + 1, x, x }, ny[4] = { y, y, y - 1, y + 1 };
				for (int j = 0; j < 4; j++)
					if (In(nx[j], ny[j]) && through[ny[j] * w + nx[j]] <= 0.0f) sum += albedo[ny[j] * w + nx[j]];
				walls[y * w + x] = 0.25f * sum;
			}
	}
	void Set(int x, int y, Kind k, float t, float a)
	{
		const int c = y * w + x;
		kind[c] = k;
		through[c] = t;
		albedo[c] = a;
	}
	bool In(int x, int y) const { return x >= 0 && y >= 0 && x < w && y < h; }
	bool Solid(int x, int y) const { return !In(x, y) || kind[y * w + x] == Rock || kind[y * w + x] == Window; }
	bool Opaque(int x, int y) const { return !In(x, y) || kind[y * w + x] == Rock; }
	float Through(int x, int y) const { return In(x, y) ? through[y * w + x] : 0.0f; }
};

static const float Water = LiquidThrough(0.25f, 1000.0f, 1000.0f), PollutedWater = LiquidThrough(0.7f, 1000.0f, 1000.0f);

// A base: granite with rooms 20 x 8 in a lattice, one-cell walls, doorways, a window in some walls, a pool of water in a
// quarter of the rooms, a staircase of blocks in some; granite back walls.
static World MakeBase(int w, int h, unsigned seed)
{
	World world;
	world.Init(w, h);
	srand(seed);
	const float back = reflectance::Granite;
	for (int ry = 4; ry + 9 < h; ry += 10)
	{
		for (int rx = 4; rx + 21 < w; rx += 22)
		{
			if (rand() % 5 == 0) continue; // undug rock
			for (int y = ry; y < ry + 8; y++) for (int x = rx; x < rx + 20; x++) world.Set(x, y, Gas, 1.0f, back);
			if (rand() % 4 == 0) for (int y = ry; y < ry + 2; y++) for (int x = rx; x < rx + 20; x++) world.Set(x, y, Liquid, Water, back);
			if (rand() % 3 == 0) for (int k = 0; k < 4; k++) world.Set(rx + 10 + k, ry + k, Rock, 0.0f, reflectance::Granite);
			// Doorway to the right room and a window above it.
			for (int y = ry; y < ry + 2; y++) { world.Set(rx + 20, y, Gas, 1.0f, back); world.Set(rx + 21, y, Gas, 1.0f, back); }
			if (rand() % 2 == 0) { world.Set(rx + 20, ry + 5, Window, 0.9f, 0.0f); world.Set(rx + 21, ry + 5, Window, 0.9f, 0.0f); }
		}
	}
	world.Finish();
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
				bool solid = world.Opaque(x, y); // the game lets light through transparent tiles (windows) and any liquid
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
	// source (its parent), whether the step from the parent is diagonal, and the weight of the falloff.
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

	// What one source adds: direct light by cell, and reflected light (to be scattered) by cell.
	struct Lit { std::vector<int> cells; std::vector<float> lux; std::vector<int> bounceCells; std::vector<float> bounce; };

	static void Add(const World& world, const Tree& tree, const Source& s, Lit& lit, std::vector<float>& grid, std::vector<float>& bounceGrid, std::vector<float>& reach)
	{
		lit.cells.clear();
		lit.lux.clear();
		lit.bounceCells.clear();
		lit.bounce.clear();
		const size_t n = tree.dx.size();
		reach.resize(n);
		for (size_t i = 0; i < n; i++)
		{
			const int x = s.x + tree.dx[i], y = s.y + tree.dy[i];
			float t = 1.0f;
			const int p = tree.parent[i];
			int from = -1;
			if (p >= 0)
			{
				const int px = s.x + tree.dx[p], py = s.y + tree.dy[p];
				t = reach[p] * (p == 0 ? 1.0f : world.Through(px, py));
				if (tree.diagonal[i]) t *= std::max(world.Through(x, py), world.Through(px, y));
				from = py * world.w + px;
			}
			reach[i] = t; // light arriving at this cell (before what the cell itself lets through)
			if (t <= 0.0f || !world.In(x, y)) continue;
			const float lux = s.lux * tree.weight[i] * t;
			if (lux < 1.0f) continue;
			const int cell = y * world.w + x;
			lit.cells.push_back(cell);
			lit.lux.push_back(lux);
			// Reflected: a solid face back into the cell the ray came from, an open cell by its back wall.
			const float back = lux * world.albedo[cell];
			if (back < 1.0f) continue;
			lit.bounceCells.push_back(world.through[cell] > 0.0f || from < 0 ? cell : from);
			lit.bounce.push_back(back);
		}
		for (size_t k = 0; k < lit.cells.size(); k++) grid[lit.cells[k]] += lit.lux[k];
		for (size_t k = 0; k < lit.bounceCells.size(); k++) bounceGrid[lit.bounceCells[k]] += lit.bounce[k];
	}

	static void Remove(const Lit& lit, std::vector<float>& grid, std::vector<float>& bounceGrid)
	{
		for (size_t k = 0; k < lit.cells.size(); k++) grid[lit.cells[k]] -= lit.lux[k];
		for (size_t k = 0; k < lit.bounceCells.size(); k++) bounceGrid[lit.bounceCells[k]] -= lit.bounce[k];
	}

	static constexpr int Steps = 8; // how far the reflected light goes from where it was reflected, in cells

	// The reflected light walks through the open cells, standing for the bounces after the first: each step a quarter to
	// each neighbour, times what the cell lets through; the quarter that goes into a solid side comes back by that side's
	// reflectance. So in a snow corridor light goes far and in an abyssalite one it dies within a few cells. The scattered
	// light of a cell is the mean over the steps, so in an open room it is about the reflected light around it. Linear in
	// the reflected light, so it can be redone for a part of the grid (the changed lights' area widened by Steps).
	static void Scatter(const World& world, const std::vector<float>& bounce, std::vector<float>& scattered, std::vector<float>& a, std::vector<float>& b)
	{
		const int w = world.w, h = world.h;
		a = bounce;
		b.assign(bounce.size(), 0.0f);
		scattered = bounce;
		for (int step = 0; step < Steps; step++)
		{
			for (int y = 0; y < h; y++)
			{
				const float* row = &a[(size_t)y * w];
				const float* up = y + 1 < h ? row + w : nullptr;
				const float* down = y > 0 ? row - w : nullptr;
				const float* through = &world.through[(size_t)y * w];
				const float* walls = &world.walls[(size_t)y * w];
				float* out = &b[(size_t)y * w];
				float* sum = &scattered[(size_t)y * w];
				for (int x = 0; x < w; x++)
				{
					const float t = through[x];
					if (t <= 0.0f) { out[x] = 0.0f; continue; }
					const float around = (x > 0 ? row[x - 1] : 0.0f) + (x + 1 < w ? row[x + 1] : 0.0f) + (up ? up[x] : 0.0f) + (down ? down[x] : 0.0f);
					out[x] = t * (0.25f * around + walls[x] * row[x]);
					sum[x] += out[x];
				}
			}
			std::swap(a, b);
		}
		for (float& v : scattered) v *= 1.0f / (Steps + 1);
	}
}

// A scene for the pictures: four equal rooms under a ceiling light (C, 1800 lux, radius 8) with a floor lamp (L, 1000 lux,
// radius 4) by a doorway that leads round a bend; a pillar; a pool. Sandstone (S), abyssalite (A), snow (W), each with a back
// wall of the same; granite (G) with no back wall. ~ water, % polluted water.
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

static float Reflectance(char m)
{
	switch (m)
	{
	case 'S': return reflectance::SandStone;
	case 'A': return reflectance::Katairite;
	case 'W': return reflectance::Snow;
	default: return reflectance::Granite;
	}
}

static void WriteScene(const std::string& out, const light::Tree& tree4, const light::Tree& tree8)
{
	const int h = (int)(sizeof(Scene) / sizeof(Scene[0])), w = 80;
	World world;
	world.Init(w, h);
	std::vector<Source> sources;
	for (int row = 0; row < h; row++)
		for (int x = 0; x < w; x++)
		{
			const char c = Scene[row][x];
			const int y = h - 1 - row;
			const char room = Scene[row < 14 ? 0 : 14][x < 40 ? 0 : 40];
			const float back = room == 'G' ? 0.0f : Reflectance(room); // the granite room has no back wall
			if (c == '~') world.Set(x, y, Liquid, Water, back);
			else if (c == '%') world.Set(x, y, Liquid, PollutedWater, back);
			else if (c == '.' || c == 'C' || c == 'L') world.Set(x, y, Gas, 1.0f, back);
			else world.Set(x, y, Rock, 0.0f, Reflectance(c));
			if (c == 'L') sources.push_back({ x, y, 1000, 4.0f });
			if (c == 'C') sources.push_back({ x, y, 1800, 8.0f });
		}
	world.Finish();
	std::vector<int> g(w * h, 0);
	std::vector<float> direct(w * h, 0.0f), bounce(w * h, 0.0f), reach;
	for (const Source& s : sources)
	{
		game::Lit gl;
		light::Lit nl;
		game::Add(world, s, gl, g);
		light::Add(world, s.range > 5 ? tree8 : tree4, s, nl, direct, bounce, reach);
	}
	std::vector<float> scattered, a, b;
	light::Scatter(world, bounce, scattered, a, b);
	FILE* f = fopen((out + "/light_piece.txt").c_str(), "w");
	fprintf(f, "%d %d\n", w, h);
	for (int y = h - 1; y >= 0; y--)
		for (int x = 0; x < w; x++)
		{
			const int c = y * w + x;
			fprintf(f, "%d %c %d %.1f %.1f\n", (int)world.kind[c], Scene[h - 1 - y][x], g[c], direct[c], direct[c] + scattered[c]);
		}
	for (const Source& s : sources) fprintf(f, "source %d %d\n", s.x, h - 1 - s.y);
	fclose(f);
	// Mean light of each room (open cells), and of its far bend, for the report.
	for (int q = 0; q < 4; q++)
	{
		const int x0 = q % 2 * 40, row0 = q / 2 * 14;
		double sumG = 0, sumD = 0, sumT = 0, bendD = 0, bendT = 0;
		int cells = 0, bend = 0;
		for (int row = row0 + 1; row < row0 + 12; row++)
			for (int x = x0 + 2; x < x0 + 37; x++)
			{
				const int c = (h - 1 - row) * w + x;
				if (world.kind[c] == Rock) continue;
				const bool inBend = x >= x0 + 34 && row <= row0 + 8;
				if (inBend) { bendD += direct[c]; bendT += direct[c] + scattered[c]; bend++; continue; }
				sumG += g[c]; sumD += direct[c]; sumT += direct[c] + scattered[c]; cells++;
			}
		printf("room %c: mean lux  game %.0f, direct %.0f, with reflected %.0f;  up the bend  direct %.0f, with reflected %.0f\n",
			Scene[row0][x0], sumG / cells, sumD / cells, sumT / cells, bendD / bend, bendT / bend);
	}
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
		std::vector<float> newGrid(world.w * world.h, 0.0f), bounceGrid(world.w * world.h, 0.0f), reach;
		std::vector<light::Lit> newLit(count);
		t0 = Now();
		for (int k = 0; k < count; k++) light::Add(world, sources[k].range > 5 ? tree8 : tree4, sources[k], newLit[k], newGrid, bounceGrid, reach);
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
			light::Remove(newLit[k], newGrid, bounceGrid);
			light::Add(world, sources[k].range > 5 ? tree8 : tree4, sources[k], newLit[k], newGrid, bounceGrid, reach);
		}
		const double newUpdate = (Now() - t0) / updates * 1000.0;
		std::vector<float> scattered, a, b;
		t0 = Now();
		light::Scatter(world, bounceGrid, scattered, a, b);
		const double scatter = Now() - t0;
		size_t gameCells = 0, newCells = 0;
		for (auto& l : gameLit) gameCells += l.cells.size();
		for (auto& l : newLit) newCells += l.cells.size() + l.bounceCells.size();
		printf("%d lights: build all  game %.2f ms, new %.2f ms;  update one  game %.1f us, new %.1f us;  scattered light over the whole grid %.2f ms;  "
			"memory for the lights  game %.1f MB, new %.1f MB\n",
			count, gameBuild, newBuild, gameUpdate, newUpdate, scatter, gameCells * 8.0 / 1e6, newCells * 8.0 / 1e6);
	}
	// Cost of one light by its radius: the game's light grows with the lit area, the new one with the area to 1.5 R.
	for (float range : { 4.0f, 8.0f, 16.0f })
	{
		const light::Tree tree = light::Build(range);
		srand(13);
		std::vector<int> gameGrid(world.w * world.h, 0);
		std::vector<float> newGrid(world.w * world.h, 0.0f), bounceGrid(world.w * world.h, 0.0f), reach;
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
			light::Add(world, tree, s, nl, newGrid, bounceGrid, reach);
			newTime += Now() - t0;
		}
		printf("radius %.0f: one light  game %.1f us, new %.1f us (ray tree %zu offsets)\n", range, gameTime / count * 1000.0, newTime / count * 1000.0, tree.dx.size());
	}
	WriteScene(out, tree4, tree8);
	return 0;
}
