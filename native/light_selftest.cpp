// Self-check of the light engine (oni_light.h) on plain Linux, without the game: the law of the direct light, walls,
// liquids and windows, the shapes, reflection by material, light round a corner, and that updates (adding, removing,
// rescaling) leave the light grid as a full computation would.
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <vector>

#include "oni_light.h"

using namespace onilight;

namespace
{
	enum Element : uint16_t { Oxygen, Water, Granite, Katairite, Snow, Glass, Chlorine, Count };

	struct Grid
	{
		int w, h;
		std::vector<uint16_t> element, backwall, built;
		std::vector<float> mass;
		std::vector<uint8_t> properties, buildMasks;
		std::vector<int32_t> light;
		std::vector<float> temperature, mix;

		Grid(int width, int height, Element fill, uint16_t back = 0xFFFF) : w(width), h(height)
		{
			const size_t n = (size_t)w * h;
			element.assign(n, Oxygen);
			backwall.assign(n, back);
			built.assign(n, 0xFFFF);
			mass.assign(n, 1.8f);
			properties.assign(n, 0);
			buildMasks.assign(n, 0);
			light.assign(n, 0);
			temperature.assign(n, 293.15f);
			mix.assign(n, NAN);
			for (int y = 0; y < h; y++)
				for (int x = 0; x < w; x++) Set(x, y, fill, back);
		}
		void Set(int x, int y, Element e, uint16_t back = 0xFFFF)
		{
			const size_t c = (size_t)y * w + x;
			element[c] = e;
			backwall[c] = back;
			mass[c] = e == Oxygen ? 1.8f : 1000.0f;
			properties[c] = e == Glass ? 0x10 : 0;
			buildMasks[c] = e == Granite || e == Katairite || e == Snow || e == Glass ? 1 : 0;
		}
		int Cell(int x, int y) const { return y * w + x; }
		GridView View()
		{
			return { w, h, element.data(), mass.data(), properties.data(), backwall.data(), buildMasks.data(), built.data(), light.data(), temperature.data(), mix.data() };
		}
	};

	std::vector<ElementOptics> Elements()
	{
		std::vector<ElementOptics> t(Count);
		t[Oxygen] = { 0.0f, 0.0f, Gas, {} };
		t[Water] = { 0.0f, -logf(0.75f) / 1000.0f, Liquid, {} };
		t[Granite] = { 0.25f, 0.0f, Solid, {} };
		t[Katairite] = { 0.08f, 0.0f, Solid, {} };
		t[Snow] = { 0.85f, 0.0f, Solid, {} };
		t[Glass] = { 0.08f, 0.0f, Solid, {} };
		// Chlorine: a fifth of the light per cubic metre at one atmosphere (2.95 kg at 293 K); shown as the equivalent mass
		// of the reference gas (molar 70.906 over 31.998).
		t[Chlorine] = { 0.0f, -logf(0.8f) / 2.95f, Gas, {}, 70.906f / 31.998f };
		return t;
	}

	struct Fixture
	{
		Grid grid;
		Engine engine;
		std::vector<ElementOptics> elements = Elements();
		explicit Fixture(Grid g) : grid(std::move(g))
		{
			engine.Init(grid.w, grid.h);
			engine.SetElements(elements.data(), (int)elements.size());
		}
		GridView View() { return grid.View(); }
	};

	SourceDesc Lamp(int x, int y, float lux = 1000.0f, float range = 4.0f, Shape shape = Circle, int width = 0)
	{
		return { x, y, lux, range, 0.5f, shape, South, width };
	}

	int failures = 0;

	void Expect(bool ok, const char* what, double a = 0, double b = 0)
	{
		printf("%s: %s", ok ? "ok" : "FAILED", what);
		if (a != 0 || b != 0) printf(" (%.4g, %.4g)", a, b);
		printf("\n");
		if (!ok) failures++;
	}

	bool Near(double a, double b, double tolerance) { return fabs(a - b) <= tolerance; }

	// The law in the open, with nothing to reflect: Lux / max(1, r / 2), faded between R and 1.5 R.
	void OpenField()
	{
		Fixture f(Grid(61, 61, Oxygen));
		GridView g = f.View();
		f.engine.Add(g, 0, Lamp(30, 30));
		Expect(Near(f.engine.Direct(f.grid.Cell(32, 30)), 1000.0, 0.01), "open field: 1000 lux 2 cells away", f.engine.Direct(f.grid.Cell(32, 30)), 1000);
		Expect(Near(f.engine.Direct(f.grid.Cell(34, 30)), 500.0, 0.01), "open field: 500 lux 4 cells away", f.engine.Direct(f.grid.Cell(34, 30)), 500);
		Expect(Near(f.engine.Direct(f.grid.Cell(35, 30)), 200.0, 0.01), "open field: faded to 200 lux 5 cells away", f.engine.Direct(f.grid.Cell(35, 30)), 200);
		Expect(f.engine.Direct(f.grid.Cell(36, 30)) == 0.0f, "open field: nothing 6 cells away (1.5 R)");
		Expect(Near(f.engine.Direct(f.grid.Cell(33, 34)), 200.0, 0.01), "open field: by the straight distance ((3, 4) as (5, 0))", f.engine.Direct(f.grid.Cell(33, 34)), 200);
		double scattered = 0;
		for (int c = 0; c < 61 * 61; c++) scattered += f.engine.Scattered(c);
		Expect(scattered == 0.0, "open field without a back wall: no scattered light", scattered, 0);
	}

	// A wall, and a diagonal of blocks touching at corners, stop the light (and the reflected light) entirely.
	void Walls()
	{
		Fixture f(Grid(41, 41, Oxygen, Granite));
		for (int y = 0; y < 41; y++) f.grid.Set(25, y, Granite);
		for (int k = 0; k < 41; k++)
			if (k < 41) f.grid.Set(k, 40 - k < 0 ? 0 : 40 - k, Granite); // a diagonal from top left down to the right
		GridView g = f.View();
		f.engine.Add(g, 0, Lamp(20, 10, 1800, 8));
		Expect(f.grid.light[f.grid.Cell(26, 10)] == 0 && f.grid.light[f.grid.Cell(27, 12)] == 0, "a wall: nothing on the far side");
		Expect(f.grid.light[f.grid.Cell(25, 10)] > 0, "a wall: its face is lit", f.grid.light[f.grid.Cell(25, 10)], 0);
		// Across the diagonal (x + y > 40 at the lamp's side is x + y = 30: the other side is x + y > 40).
		int leaked = 0;
		for (int y = 0; y < 41; y++)
			for (int x = 0; x < 25; x++)
				if (x + y > 40 && f.grid.light[f.grid.Cell(x, y)] > 0) leaked++;
		Expect(leaked == 0, "a diagonal of blocks: no light through its corners", leaked, 0);
	}

	// Water lets through a quarter less per full cell; glass a tenth less.
	void LiquidAndGlass()
	{
		Fixture f(Grid(41, 41, Oxygen));
		for (int y = 0; y < 15; y++)
			for (int x = 0; x < 41; x++) f.grid.Set(x, y, Water);
		GridView g = f.View();
		f.engine.Add(g, 0, Lamp(20, 16, 1000, 8));
		// Straight down: in water each cell takes 0.75 of the one above, times the falloff.
		const double a = f.engine.Direct(f.grid.Cell(20, 12)), b = f.engine.Direct(f.grid.Cell(20, 11));
		const double expected = 0.75 * (0.5 * 4) / (0.5 * 5);
		Expect(Near(b / a, expected, 1e-3), "water: 0.75 per full cell", b / a, expected);
		Expect(Near(f.engine.Through(f.grid.Cell(20, 12)), 0.75, 1e-4), "water: what a full cell lets through", f.engine.Through(f.grid.Cell(20, 12)), 0.75);

		Fixture w(Grid(41, 41, Oxygen));
		Fixture o(Grid(41, 41, Oxygen));
		for (int y = 0; y < 41; y++) w.grid.Set(24, y, Glass);
		GridView gw = w.View(), go = o.View();
		w.engine.Add(gw, 0, Lamp(20, 20, 1000, 8));
		o.engine.Add(go, 0, Lamp(20, 20, 1000, 8));
		const double through = w.engine.Direct(w.grid.Cell(27, 20)) / o.engine.Direct(o.grid.Cell(27, 20));
		Expect(Near(through, WindowThrough, 1e-4), "a window lets through 0.9", through, WindowThrough);
	}

	// A gas that dims light, by its real mass (the shown mass is the equivalent of the reference gas); a mixture by what
	// the mod gives for the cell.
	void Gases()
	{
		Fixture f(Grid(41, 41, Oxygen));
		const float shown = 2.95f * 31.998f / 70.906f; // one atmosphere of chlorine at 293.15 K, as the gas mixture mod shows it
		for (int y = 0; y < 41; y++)
		{
			f.grid.Set(25, y, Chlorine);
			f.grid.mass[f.grid.Cell(25, y)] = shown;
			f.grid.Set(26, y, Chlorine);
			f.grid.mass[f.grid.Cell(26, y)] = shown;
			f.grid.temperature[f.grid.Cell(26, y)] = 2.0f * 293.15f; // as hot, the same shown mass is half as much chlorine
			f.grid.mix[f.grid.Cell(27, y)] = 0.5f;                  // a mixture cell: what the mod gave
		}
		GridView g = f.View();
		f.engine.Add(g, 0, Lamp(20, 20, 1000, 8));
		Expect(Near(f.engine.Through(f.grid.Cell(25, 20)), 0.8, 1e-4), "chlorine: a fifth of the light per cell at one atmosphere", f.engine.Through(f.grid.Cell(25, 20)), 0.8);
		Expect(Near(f.engine.Through(f.grid.Cell(26, 20)), sqrt(0.8), 1e-4), "chlorine: hot, the same shown mass is less gas", f.engine.Through(f.grid.Cell(26, 20)), sqrt(0.8));
		Expect(Near(f.engine.Through(f.grid.Cell(27, 20)), 0.5, 1e-6), "a mixture: what the mod gives for the cell", f.engine.Through(f.grid.Cell(27, 20)), 0.5);
		Expect(Near(f.engine.Through(f.grid.Cell(24, 20)), 1.0, 1e-6), "oxygen: clear", f.engine.Through(f.grid.Cell(24, 20)), 1.0);
		Expect(f.engine.Wet(0), "a light by a gas that dims light changes by itself (traced again by the mod)");
	}

	// What the player sees: a colony light only once marked as seen, a natural light only where a duplicant sees; the
	// darkness of the view by the logarithm of the light; a wall blocks the sight.
	void PlayerView()
	{
		Fixture f(Grid(61, 41, Oxygen));
		for (int y = 0; y < 41; y++) f.grid.Set(30, y, Granite);
		GridView g = f.View();
		f.engine.Add(g, 0, Lamp(20, 20));          // the colony's
		SourceDesc bug = Lamp(40, 20);
		bug.natural = 1;                          // a light bug behind the wall
		f.engine.Add(g, 1, bug);
		const int lampCell = f.grid.Cell(22, 20), bugCell = f.grid.Cell(42, 20);
		Expect(f.engine.SeenLux(lampCell) == 0.0f, "the player's view: a colony light not yet seen is dark");
		f.engine.SetSeen(0, true);
		Expect(Near(f.engine.SeenLux(lampCell), f.engine.Direct(lampCell), 1e-3), "the player's view: a seen colony light shows", f.engine.SeenLux(lampCell), f.engine.Direct(lampCell));
		const int32_t eye = f.grid.Cell(20, 21);
		f.engine.Sight(g, &eye, 1, 32.0f);
		Expect(f.engine.InSight(f.grid.Cell(25, 21)) && !f.engine.InSight(bugCell), "the player's view: the sight stops at a wall");
		const int32_t eye2 = f.grid.Cell(45, 21);
		f.engine.Sight(g, &eye2, 1, 32.0f);
		Expect(f.engine.InSight(bugCell) && f.engine.NaturalLux(bugCell) > 0.0f, "the player's view: a natural light shows where a duplicant sees");
		f.engine.UpdateShown({ 0, 0, 60, 40 }, nullptr, 0.0f);
		const float lux = f.engine.SeenLux(lampCell), expected = std::min(255.0f, logf(1.0f + lux) * 255.0f / logf(1.0f + FullLux) + 0.5f);
		const uint8_t* shown = f.engine.Shown();
		Expect(shown[lampCell] == (uint8_t)expected && shown[f.grid.Cell(5, 20)] == 0, "the player's view: by the logarithm of the light, 0 in the dark", shown[lampCell], expected);
		// Traced again (as the mod does for lights by liquid and gas, and every light now and then): still seen.
		f.engine.Remove(g, 0, false);
		f.engine.Add(g, 0, Lamp(20, 20));
		Expect(Near(f.engine.SeenLux(lampCell), f.engine.Direct(lampCell), 1e-3), "the player's view: a colony light traced again stays seen", f.engine.SeenLux(lampCell), f.engine.Direct(lampCell));
		f.engine.Add(g, 0, Lamp(20, 20));
		Expect(Near(f.engine.SeenLux(lampCell), f.engine.Direct(lampCell), 1e-3), "the player's view: a colony light replaced stays seen", f.engine.SeenLux(lampCell), f.engine.Direct(lampCell));
		f.engine.Remove(g, 0, true);
		f.engine.Free(0);
		f.engine.Add(g, 0, Lamp(20, 20));
		Expect(f.engine.SeenLux(lampCell) == 0.0f, "the player's view: a new light under a freed id is not seen yet");
		f.engine.Remove(g, 0, false);
		f.engine.Remove(g, 1, false);
		double left = 0;
		for (int c = 0; c < 61 * 41; c++) left = std::max(left, (double)fabsf(f.engine.SeenLux(c)) + fabsf(f.engine.NaturalLux(c)));
		Expect(left < 1e-3, "the player's view: nothing left after removing", left, 0);
	}

	// A ceiling light in a room 12 x 4 of Granite: the ceiling and the tops of the walls, out of its direct light, get the
	// light scattered from the floor.
	void CeilingRoom()
	{
		Grid room(16, 10, Granite);
		for (int y = 2; y <= 5; y++)
			for (int x = 2; x <= 13; x++) room.Set(x, y, Oxygen);
		Fixture f(room);
		GridView g = f.View();
		f.engine.Add(g, 0, Lamp(7, 5, 1800, 8, Cone));
		const int ceiling = f.grid.Cell(4, 6), wallTop = f.grid.Cell(1, 5), corner = f.grid.Cell(13, 6);
		Expect(f.engine.Direct(ceiling) == 0.0f && f.engine.Direct(wallTop) == 0.0f, "a ceiling light: no direct light on the ceiling and the tops of the walls");
		Expect(f.engine.Scattered(ceiling) >= 1.0f && f.engine.Scattered(wallTop) >= 1.0f && f.engine.Scattered(corner) >= 1.0f,
			"a ceiling light: the ceiling and the walls lit from the floor", f.engine.Scattered(ceiling), f.engine.Scattered(wallTop));
		Expect(f.engine.Scattered(f.grid.Cell(4, 8)) == 0.0f && f.engine.Scattered(f.grid.Cell(0, 3)) == 0.0f, "a ceiling light: nothing inside the rock behind the faces");
	}

	// A tunnel 2 high with a light at a Duplicant's head: straight lines reach the ceiling only right above the light (the
	// lines to farther cells of it cut through the ceiling itself); the light reflected in the tunnel lights the rest of it.
	void Tunnel()
	{
		Grid tunnel(24, 7, Granite);
		for (int y = 2; y <= 3; y++)
			for (int x = 1; x <= 20; x++) tunnel.Set(x, y, Oxygen);
		for (int x = 8; x <= 14; x++) tunnel.Set(x, 2, Water), tunnel.mass[tunnel.Cell(x, 2)] = 150.0f;
		Fixture f(tunnel);
		GridView g = f.View();
		f.engine.Add(g, 0, Lamp(6, 3, 600, 4));
		int lit = 0;
		for (int x = 1; x <= 20; x++) lit += f.engine.Direct(f.grid.Cell(x, 4)) + f.engine.Scattered(f.grid.Cell(x, 4)) >= 1.0f;
		Expect(f.engine.Direct(f.grid.Cell(9, 4)) == 0.0f && lit >= 12, "a tunnel: its ceiling lit by the light in it, over a puddle too", lit, 12);
	}

	// A ceiling light lights the half below it; a beam is even on both sides of its axis.
	void Shapes()
	{
		Fixture f(Grid(61, 61, Oxygen));
		GridView g = f.View();
		f.engine.Add(g, 0, Lamp(30, 40, 1800, 8, Cone));
		double above = 0;
		for (int y = 40; y < 61; y++)
			for (int x = 0; x < 61; x++)
				if (!(x == 30 && y == 40)) above += f.engine.Direct(f.grid.Cell(x, y));
		Expect(above == 0.0, "a ceiling light: nothing at or above its row", above, 0);
		Expect(Near(f.engine.Direct(f.grid.Cell(30, 36)), 900.0, 0.01), "a ceiling light: full light straight down", f.engine.Direct(f.grid.Cell(30, 36)), 900);

		Fixture q(Grid(61, 61, Oxygen));
		GridView gq = q.View();
		q.engine.Add(gq, 0, Lamp(30, 50, 60000, 8, Quad, 3));
		double worst = 0;
		for (int y = 30; y < 50; y++)
			for (int k = 1; k < 12; k++) worst = std::max(worst, fabs((double)q.engine.Direct(q.grid.Cell(30 - k, y)) - q.engine.Direct(q.grid.Cell(30 + k, y))));
		Expect(worst < 0.01, "a beam: even on both sides", worst, 0);
		Expect(q.engine.Direct(q.grid.Cell(30, 44)) > q.engine.Direct(q.grid.Cell(36, 44)) * 4, "a beam: narrow",
			q.engine.Direct(q.grid.Cell(30, 44)), q.engine.Direct(q.grid.Cell(36, 44)));
	}

	// The same room and lamp: snow is lighter than abyssalite; with no back wall and no walls nearby less again.
	double RoomLight(Element walls, uint16_t back, double* bend)
	{
		Fixture f(Grid(60, 40, walls));
		for (int y = 5; y < 15; y++)
			for (int x = 5; x < 35; x++) f.grid.Set(x, y, Oxygen, back);
		// A corridor from the room's right side, then up a shaft.
		for (int y = 5; y < 8; y++)
			for (int x = 35; x < 45; x++) f.grid.Set(x, y, Oxygen, back);
		for (int y = 8; y < 25; y++)
			for (int x = 42; x < 45; x++) f.grid.Set(x, y, Oxygen, back);
		GridView g = f.View();
		f.engine.Add(g, 0, Lamp(20, 14, 1800, 8, Cone));
		f.engine.Add(g, 1, Lamp(37, 6, 1000, 4));
		double sum = 0;
		int n = 0;
		for (int y = 5; y < 15; y++)
			for (int x = 5; x < 35; x++, n++) sum += f.grid.light[f.grid.Cell(x, y)];
		*bend = 0;
		for (int y = 12; y < 16; y++)
			for (int x = 42; x < 45; x++) *bend += f.grid.light[f.grid.Cell(x, y)];
		return sum / n;
	}

	void Reflection()
	{
		double bendSnow, bendDark, bendNone;
		const double snow = RoomLight(Snow, Snow, &bendSnow), dark = RoomLight(Katairite, Katairite, &bendDark), none = RoomLight(Granite, 0xFFFF, &bendNone);
		printf("   room light: snow %.0f, abyssalite %.0f, granite without a back wall %.0f; up the shaft: %.0f, %.0f, %.0f\n", snow, dark, none, bendSnow, bendDark, bendNone);
		Expect(snow > dark * 1.5, "reflection: a snow room is much lighter than an abyssalite one", snow, dark);
		Expect(bendSnow > bendDark * 3 && bendSnow > 0, "round the corner: a snow shaft gets light, an abyssalite one little", bendSnow, bendDark);
	}

	// Many updates in a base-like grid against a full computation; removing everything leaves nothing. With a variant
	// below the widest, only the light grid is returned (for comparing the variants of the transport).
	std::vector<int32_t> Updates(int variant)
	{
		Fixture f(Grid(200, 160, Granite, Granite));
		f.engine.LimitVariant(variant);
		const bool narrower = variant < 1;
		srand(3);
		for (int ry = 4; ry + 12 < 160; ry += 13)
			for (int rx = 4; rx + 22 < 200; rx += 24)
			{
				for (int y = ry; y < ry + 10; y++)
					for (int x = rx; x < rx + 20; x++) f.grid.Set(x, y, rand() % 7 == 0 && y < ry + 2 ? Water : Oxygen, rand() % 3 ? Granite : Snow);
				for (int y = ry; y < ry + 3; y++) f.grid.Set(rx + 20, y, Oxygen, Granite), f.grid.Set(rx + 21, y, Oxygen, Granite);
			}
		GridView g = f.View();
		std::vector<SourceDesc> lamps;
		for (int k = 0; k < 300; k++)
		{
			int x, y;
			do { x = rand() % 200; y = rand() % 160; } while (f.grid.buildMasks[f.grid.Cell(x, y)] & 1);
			const int kind = rand() % 4;
			lamps.push_back(kind == 0 ? Lamp(x, y) : kind == 1 ? Lamp(x, y, 1800, 8, Cone) : kind == 2 ? Lamp(x, y, 1800, 5) : Lamp(x, y, 60000, 8, Quad, 3));
			f.engine.Add(g, k, lamps.back());
		}
		for (int u = 0; u < 3000; u++)
		{
			const int k = rand() % 300, what = rand() % 4;
			if (what == 0) f.engine.Remove(g, k, false);
			else if (what == 1) f.engine.Add(g, k, lamps[k]);
			else if (what == 2) { f.engine.Remove(g, k, true); SourceDesc d = lamps[k]; d.lux *= 0.5f + (rand() % 100) / 100.0f; f.engine.Add(g, k, d); lamps[k] = d; }
			else
			{
				SourceDesc d = lamps[k];
				d.x = std::min(199, std::max(0, d.x + rand() % 3 - 1));
				if (!(f.grid.buildMasks[f.grid.Cell(d.x, d.y)] & 1)) { lamps[k] = d; f.engine.Add(g, k, d); }
			}
		}
		const std::vector<int32_t> result = f.grid.light;
		if (narrower) return result;
		float check[6];
		f.engine.Check(g, { 0, 0, f.grid.w - 1, f.grid.h - 1 }, check);
		Expect(check[0] < 0.05f, "updates: direct light as computed again", check[0], 0);
		Expect(check[1] <= 1.0f, "updates: the light grid as computed again (lux)", check[1], 1);
		Expect(check[2] <= 2.0f, "updates: the light grid as all reflected light carried at once (lux)", check[2], 2);

		// The preview is the direct light of an add.
		std::vector<int32_t> cells(4096), lux(4096);
		const int n = f.engine.Preview(g, lamps[7], cells.data(), lux.data(), 4096);
		Fixture solo(f.grid);
		GridView gs = solo.View();
		std::fill(solo.grid.light.begin(), solo.grid.light.end(), 0);
		solo.engine.Add(gs, 0, lamps[7]);
		int worst = 0;
		for (int i = 0; i < n; i++) worst = std::max(worst, abs(lux[i] - (int)(solo.engine.Direct(cells[i]) + 0.5f)));
		Expect(n > 0 && worst <= 1, "the preview: as the direct light of an add", n, worst);

		for (int k = 0; k < 300; k++) f.engine.Remove(g, k, k % 2 == 0);
		int lit = 0;
		double left = 0;
		for (int c = 0; c < 200 * 160; c++)
		{
			lit += f.grid.light[c] != 0;
			left = std::max(left, (double)fabsf(f.engine.Direct(c)) + fabsf(f.engine.Scattered(c)));
		}
		Expect(lit == 0 && left < 0.05, "removing everything: nothing left", lit, left);
		return result;
	}

	// The SSE2 and the AVX variants of the transport give the same light grid.
	void Variants()
	{
		const std::vector<int32_t> widest = Updates(1);
		if (!HasAvx())
		{
			printf("skipped: the transport's variants (no AVX here)\n");
			return;
		}
		const std::vector<int32_t> plain = Updates(0);
		int differ = 0;
		for (size_t c = 0; c < widest.size(); c++) differ += widest[c] != plain[c];
		Expect(differ == 0, "the transport: SSE2 gives the same light grid as AVX", differ, 0);
	}

	// A change of brightness alone gives what tracing again gives.
	void Rescale()
	{
		Fixture a(Grid(60, 40, Granite, Snow)), b(Grid(60, 40, Granite, Snow));
		for (Fixture* f : { &a, &b })
			for (int y = 5; y < 30; y++)
				for (int x = 5; x < 50; x++) f->grid.Set(x, y, Oxygen, Snow);
		GridView ga = a.View(), gb = b.View();
		a.engine.Add(ga, 0, Lamp(20, 15, 1800, 5));
		a.engine.Remove(ga, 0, true);
		a.engine.Add(ga, 0, Lamp(20, 15, 700, 5));
		b.engine.Add(gb, 0, Lamp(20, 15, 700, 5));
		int worst = 0;
		for (int c = 0; c < 60 * 40; c++) worst = std::max(worst, abs(a.grid.light[c] - b.grid.light[c]));
		Expect(worst <= 1 && a.engine.Stats().rescales == 1, "a change of brightness: rescaled, as traced again", worst, (double)a.engine.Stats().rescales);
	}
}

int main()
{
	OpenField();
	Walls();
	LiquidAndGlass();
	Gases();
	PlayerView();
	Shapes();
	CeilingRoom();
	Tunnel();
	Reflection();
	Variants();
	Rescale();
	if (failures)
	{
		printf("LIGHT SELFTEST FAILED: %d\n", failures);
		return 1;
	}
	printf("LIGHT SELFTEST OK\n");
	return 0;
}
