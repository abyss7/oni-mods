// Measurement: what a tick of the mixture layer costs in vector form on a colony grid (1114 x 178), single-threaded.
//   tools/zig/zig c++ -O3 -mavx -fno-exceptions -fno-rtti -o /tmp/mixbench sim/model/mixbench.cpp && /tmp/mixbench 8
// Parameters: the number of gases and, optionally, the word fall: gravity by the rule "gas heavier than the mixture below it falls"
// instead of exchanging pairs of rows. The whole grid is walked, not only gas cells.
//
// All three rules are reduced to coefficients on cell boundaries, shared by all gases; per gas there remains
// one pass "new mass = old minus flows through four boundaries". The flow through a boundary is computed by one
// and the same expression for both cells, so mass is conserved up to the rounding of additions.
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <vector>

typedef std::vector<float> Field;
static const int W = 1114, H = 178, N = W * H;

static double Ms(std::chrono::steady_clock::time_point a) { return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - a).count(); }

// Preparation: total mass, mole count, inverse mass, mixture "heaviness".
static void Prepare(const std::vector<Field>& mass, const float* invMolar, int S, Field& total, Field& invTotal, Field& heavy)
{
	Field moles(N, 0.0f);
	for (int i = 0; i < N; i++) total[i] = 0.0f;
	for (int s = 0; s < S; s++)
	{
		const float* __restrict m = mass[s].data(); float* __restrict t = total.data(); float* __restrict n = moles.data();
		const float k = invMolar[s];
		for (int i = 0; i < N; i++) { t[i] += m[i]; n[i] += m[i] * k; }
	}
	for (int i = 0; i < N; i++)
	{
		invTotal[i] = total[i] > 0.0f ? 1.0f / total[i] : 0.0f;
		heavy[i] = moles[i] > 0.0f ? total[i] / moles[i] : 0.0f;
	}
}

// Coefficients on the boundary of a cell with its neighbour at step (1: right, W: upper): pressure and diffusion.
static void Edges(const Field& total, const Field& invTotal, const Field& open, int step, float kBulk, float diffusion, Field& a, Field& b)
{
	const float* __restrict t = total.data(); const float* __restrict it = invTotal.data(); const float* __restrict o = open.data();
	float* __restrict pa = a.data(); float* __restrict pb = b.data();
	for (int i = 0; i < N - step; i++)
	{
		const float here = t[i], there = t[i + step];
		float flow = kBulk * (here - there);
		flow = flow > here * 0.125f ? here * 0.125f : flow;
		flow = flow < -there * 0.125f ? -there * 0.125f : flow;
		const float mix = diffusion * (here < there ? here : there);
		const float both = o[i] * o[i + step];
		pa[i] = both * ((flow > 0.0f ? flow : 0.0f) + mix) * it[i];
		pb[i] = both * ((flow < 0.0f ? -flow : 0.0f) + mix) * it[i + step];
	}
}

// One gas: the old state is read, the new one written; rows are independent.
static void Species(const Field& old, Field& next, const Field& keep, const Field& aH, const Field& bH, const Field& aV, const Field& bV)
{
	const float* __restrict m = old.data(); float* __restrict out = next.data(); const float* __restrict k = keep.data();
	const float* __restrict ah = aH.data(); const float* __restrict bh = bH.data(); const float* __restrict av = aV.data(); const float* __restrict bv = bV.data();
	// Through flows on boundaries: the flow through a boundary is computed by one and the same expression for both cells,
	// so what left one is bit for bit equal to what arrived in the other.
	(void)k;
	for (int i = W; i < N - W; i++)
	{
		const float right = ah[i] * m[i] - bh[i] * m[i + 1];
		const float left = ah[i - 1] * m[i - 1] - bh[i - 1] * m[i];
		const float up = av[i] * m[i] - bv[i] * m[i + W];
		const float down = av[i - W] * m[i - W] - bv[i - W] * m[i];
		out[i] = m[i] - right + left - up + down;
	}
}

// Buoyancy: pairs of rows (lower, upper) do not overlap; the exchange share is common to all gases of a cell.
static void BuoyancyShares(const Field& total, const Field& invTotal, const Field& heavy, const Field& open, int phase, float half, float most, Field& fromLow, Field& fromHigh)
{
	for (int y = 1 + phase; y < H - 2; y += 2)
	{
		const int lo = y * W, hi = lo + W;
		for (int x = 0; x < W; x++)
		{
			const float a = heavy[lo + x], b = heavy[hi + x];
			const float contrast = b > a ? (b - a) / (b + a) : 0.0f;
			const float least = total[lo + x] < total[hi + x] ? total[lo + x] : total[hi + x];
			const float portion = open[lo + x] * open[hi + x] * most * contrast / (contrast + half) * least;
			fromLow[lo + x] = portion * invTotal[lo + x];
			fromHigh[lo + x] = portion * invTotal[hi + x];
		}
	}
}

static void BuoyancySwap(Field& mass, int phase, const Field& fromLow, const Field& fromHigh)
{
	for (int y = 1 + phase; y < H - 2; y += 2)
	{
		float* __restrict lo = mass.data() + y * W; float* __restrict hi = lo + W;
		const float* __restrict fl = fromLow.data() + y * W; const float* __restrict fh = fromHigh.data() + y * W;
		for (int x = 0; x < W; x++)
		{
			const float up = fl[x] * lo[x], down = fh[x] * hi[x];
			lo[x] = lo[x] - up + down;
			hi[x] = hi[x] - down + up;
		}
	}
}

// Gravity by the rule "gas heavier than the mixture below it falls": a separate pass for each gas.
// heavy: the mean heaviness of the cell's mixture; which mean (by molecules or by mass) is decided only by the preparation,
// and it costs the same: in both cases it is one weighted sum over the gases.
static void Fall(const Field& old, Field& next, Field& down, const Field& heavy, const Field& open, float molar, float gravity)
{
	const float* __restrict m = old.data(); float* __restrict out = next.data(); float* __restrict d = down.data();
	const float* __restrict h = heavy.data(); const float* __restrict o = open.data();
	// How much gas leaves the cell for the cell below it.
	for (int i = W; i < N; i++)
	{
		const float below = h[i - W];
		const float excess = (molar - below) / (molar + below);
		d[i] = o[i] * o[i - W] * gravity * (excess > 0.0f ? excess : 0.0f) * m[i];
	}
	for (int i = W; i < N - W; i++) out[i] = m[i] - d[i] + d[i + W];
}

int main(int argc, char** argv)
{
	const int S = argc > 1 ? atoi(argv[1]) : 8;
	const float invMolar[16] = { 1 / 32.f, 1 / 44.f, 1 / 2.f, 1 / 71.f, 1 / 18.f, 1 / 16.f, 1 / 34.f, 1 / 28.f, 1 / 30.f, 1 / 40.f, 1 / 20.f, 1 / 48.f, 1 / 17.f, 1 / 64.f, 1 / 4.f, 1 / 36.f };
	std::vector<Field> mass(S, Field(N, 0.0f)), next(S, Field(N, 0.0f));
	Field open(N, 0.0f), total(N), invTotal(N), heavy(N), aH(N, 0), bH(N, 0), aV(N, 0), bV(N, 0), keep(N, 0), fromLow(N, 0), fromHigh(N, 0);
	srand(7);
	for (int i = 0; i < N; i++)
	{
		const int y = i / W, x = i % W;
		// The grid edge and some cells inside are not gas cells: nothing flows through them.
		open[i] = (y == 0 || y == H - 1 || x == 0 || x == W - 1 || (i / 97) % 7 == 0) ? 0.0f : 1.0f;
		if (open[i] == 0.0f) continue;
		for (int s = 0; s < S; s++) mass[s][i] = ((i / 53 + s) % S < 3) ? 0.2f + (float)((i * 13 + s * 7) % 100) * 0.02f : 0.0f;
	}
	double before = 0; for (int s = 0; s < S; s++) for (int i = 0; i < N; i++) before += mass[s][i];

	const bool fallRule = argc > 2; // second parameter: the "heavy gas falls" rule instead of exchanging pairs of rows
	const float molar[16] = { 32, 44, 2, 71, 18, 16, 34, 28, 30, 40, 20, 48, 17, 64, 4, 36 };
	Field down(N, 0.0f);
	const int ticks = 200;
	double tPrep = 0, tEdges = 0, tSpecies = 0, tBuoy = 0;
	for (int tick = 0; tick < ticks; tick++)
	{
		auto t0 = std::chrono::steady_clock::now();
		Prepare(mass, invMolar, S, total, invTotal, heavy);
		tPrep += Ms(t0); t0 = std::chrono::steady_clock::now();
		Edges(total, invTotal, open, 1, 0.12f, 0.001f, aH, bH);
		Edges(total, invTotal, open, W, 0.12f, 0.001f, aV, bV);
		for (int i = W; i < N - W; i++) keep[i] = 1.0f - aH[i] - bH[i - 1] - aV[i] - bV[i - W];
		tEdges += Ms(t0); t0 = std::chrono::steady_clock::now();
		for (int s = 0; s < S; s++) { Species(mass[s], next[s], keep, aH, bH, aV, bV); mass[s].swap(next[s]); }
		tSpecies += Ms(t0); t0 = std::chrono::steady_clock::now();
		Prepare(mass, invMolar, S, total, invTotal, heavy);
		if (fallRule)
		{
			for (int s = 0; s < S; s++) { Fall(mass[s], next[s], down, heavy, open, molar[s], 0.01f); mass[s].swap(next[s]); }
		}
		else
		{
			BuoyancyShares(total, invTotal, heavy, open, tick % 2, 0.02f, 0.97f, fromLow, fromHigh);
			for (int s = 0; s < S; s++) BuoyancySwap(mass[s], tick % 2, fromLow, fromHigh);
		}
		tBuoy += Ms(t0);
	}
	double after = 0, least = 1e9; for (int s = 0; s < S; s++) for (int i = W; i < N - W; i++) { after += mass[s][i]; if (mass[s][i] < least) least = mass[s][i]; }
	double edge = 0; for (int s = 0; s < S; s++) { for (int i = 0; i < W; i++) edge += mass[s][i]; for (int i = N - W; i < N; i++) edge += mass[s][i]; }
	printf("%d gases, %d cells: preparation %.2f ms, boundaries %.2f ms, gases %.2f ms (%.2f per gas), gravity %.2f ms; total %.2f ms per tick\n",
		S, N, tPrep / ticks, tEdges / ticks, tSpecies / ticks, tSpecies / ticks / S, tBuoy / ticks, (tPrep + tEdges + tSpecies + tBuoy) / ticks);
	printf("  mass before %.3f, after %.3f (without the edge rows, which the measurement does not update: %.3f); least gas mass in a cell %.6f\n", before, after + edge, edge, least);
	return 0;
}
