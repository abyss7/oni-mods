"""Model of the gas mixture layer on a single column of cells (0 = floor, N-1 = ceiling), step 0.2 s.
Used to pick rules and coefficients without the game: python3 sim/model/column.py

Rules:
  1+2. pressure and diffusion: flows between neighbouring cells are computed from the old state
       (the vector code does the same, see mixbench.cpp);
  3.   buoyancy: pairs of vertically adjacent cells exchange equal portions of mixture. The pairs are
       non-overlapping (even ones on one tick, odd ones on the next), so the exchange share can be
       more than half: cells can not only mix but also swap places. With a share below
       half the light gas only smears out and does not gather under the ceiling.
At the end, for comparison, there is a "gas separates from the mixture" rule: it stratifies even already mixed gas,
but holds the layer forever."""
# Molar masses from the game data (StreamingAssets/elements/gas.yaml): exactly what the layer gets in the game.
# For Hydrogen and Oxygen they are atomic masses, not molecular ones (real molecules would have 2 and 32).
MOLAR = {"H2": 1.00794, "O2": 15.9994, "CO2": 44.01}
TICK = 0.2


def total(c): return sum(c.values())
def heavy(c):
    n = sum(m / MOLAR[g] for g, m in c.items())
    return total(c) / n if n > 0 else 0.0


def flow_and_diffusion(cells, k_bulk, diffusion):
    n = len(cells)
    tot = [total(c) for c in cells]
    frac = [{g: (c.get(g, 0.0) / tot[i] if tot[i] > 0 else 0.0) for g in MOLAR} for i, c in enumerate(cells)]
    new = [dict(c) for c in cells]
    for low in range(n - 1):
        up = low + 1
        flow = max(-tot[up] / 8, min(tot[low] / 8, k_bulk * (tot[low] - tot[up])))
        mix = diffusion * min(tot[low], tot[up])
        for g in MOLAR:
            moved = max(flow, 0.0) * frac[low][g] - max(-flow, 0.0) * frac[up][g] + mix * (frac[low][g] - frac[up][g])
            new[low][g] = new[low].get(g, 0.0) - moved
            new[up][g] = new[up].get(g, 0.0) + moved
    return new


def buoyancy(cells, phase, strength, half, most):
    """Exchange in pairs (phase, phase+1), (phase+2, phase+3)...: the exchange share grows with the difference in mixture heaviness."""
    for low in range(phase, len(cells) - 1, 2):
        up = low + 1
        a, b = heavy(cells[low]), heavy(cells[up])
        if b <= a or a + b == 0:
            continue  # not heavier on top: stable
        contrast = (b - a) / (b + a)
        share = most * contrast / (contrast + half) * strength
        share = min(share, most)
        portion = share * min(total(cells[low]), total(cells[up]))
        lo, hi = cells[low], cells[up]
        tl, tu = total(lo), total(hi)
        for g in MOLAR:
            down = portion * hi.get(g, 0.0) / tu
            upw = portion * lo.get(g, 0.0) / tl
            lo[g] = lo.get(g, 0.0) - upw + down
            hi[g] = hi.get(g, 0.0) - down + upw


def unmix(cells, drift):
    """For comparison: in each pair, gas lighter than the pair average moves from the lower cell to the upper one, heavier gas from the upper to the lower."""
    for low in range(len(cells) - 1):
        up = low + 1
        n = sum((cells[low].get(g, 0.0) + cells[up].get(g, 0.0)) / MOLAR[g] for g in MOLAR)
        if n <= 0:
            continue
        mean = (total(cells[low]) + total(cells[up])) / n
        for g in MOLAR:
            rel = (mean - MOLAR[g]) / mean
            if rel > 0:
                moved = min(1.0, drift * rel) * cells[low].get(g, 0.0)
                cells[low][g] -= moved; cells[up][g] = cells[up].get(g, 0.0) + moved
            elif rel < 0:
                moved = min(1.0, -drift * rel) * cells[up].get(g, 0.0)
                cells[up][g] -= moved; cells[low][g] = cells[low].get(g, 0.0) + moved


def share(cell, gas):
    t = total(cell)
    return cell.get(gas, 0.0) / t if t else 0.0


def run(title, cells, seconds, marks, gas="H2", emit=None, emit_until=0, k_bulk=0.12, diffusion=0.0015, strength=1.0, half=0.1, most=0.95, both_phases=False, watch=-1, drift=0.0):
    print(f"\n{title}\n  diffusion={diffusion} buoyancy: strength={strength} half at contrast={half} exchange limit={most} {'two phases per tick' if both_phases else 'one phase per tick'}")
    main_at, best, start = None, 0.0, sum(c.get(gas, 0.0) for c in cells)
    for t in range(int(seconds / TICK) + 1):
        now = t * TICK
        s = share(cells[watch], gas)
        best = max(best, s)
        if main_at is None and s > 0.5:
            main_at = now
        if any(abs(now - m) < TICK / 2 for m in marks):
            print(f"  {now:7.1f} s  {gas} share by mass, ceiling to floor: " + " ".join(f"{share(c, gas) * 100:3.0f}" for c in reversed(cells)))
        if emit and now < emit_until:
            for cell, g, rate in emit:
                cells[cell][g] = cells[cell].get(g, 0.0) + rate * TICK
        cells = flow_and_diffusion(cells, k_bulk, diffusion)
        if both_phases:
            buoyancy(cells, 0, strength, half, most); buoyancy(cells, 1, strength, half, most)
        else:
            buoyancy(cells, t % 2, strength, half, most)
        if drift:
            unmix(cells, drift)
    where = "under the ceiling" if watch == -1 else "at the floor"
    print(f"  {gas} is the main gas {where} from {main_at} s; largest share {where} {best * 100:.0f}%")


marks = [0, 1, 2, 4, 8, 30, 60, 300, 600, 1200, 1800, 3600]
o2 = lambda m=1.8: {"H2": 0.0, "O2": m, "CO2": 0.0}
h2 = lambda: {"H2": 1.8, "O2": 0.0, "CO2": 0.0}
co2 = lambda: {"H2": 0.0, "O2": 0.0, "CO2": 1.8}
tuned = dict(half=0.02, most=0.97, diffusion=0.001)

run("1. Room of 10 cells, one cell of pure H2 at the floor", [h2()] + [o2() for _ in range(9)], 3600, marks, **tuned)
run("2. Room of 20 cells, one cell of pure H2 at the floor", [h2()] + [o2() for _ in range(19)], 3600, [0, 2, 4, 8, 60, 600, 1800, 3600], **tuned)
run("3. Room of 10 cells, three cells of pure H2 at the floor", [h2(), h2(), h2()] + [o2() for _ in range(7)], 3600, [0, 2, 4, 8, 60, 600, 1800, 3600], **tuned)
run("4. One cell of pure CO2 under the ceiling", [o2() for _ in range(9)] + [co2()], 3600, [0, 2, 4, 8, 60, 600, 1800, 3600], gas="CO2", watch=0, **tuned)
run("5. Electrolyzer for 60 s into one cell at the floor", [o2() for _ in range(10)], 1800, [0, 30, 60, 120, 600, 1800], emit=[(0, "O2", 0.888), (0, "H2", 0.112)], emit_until=60, **tuned)
run("6. Uniform mixture of 10% H2, with the 'gas separates from the mixture' rule added", [{"H2": 0.18, "O2": 1.62, "CO2": 0.0} for _ in range(10)], 3600, [0, 2, 8, 30, 60, 600, 1800, 3600], drift=0.05, **tuned)
run("7. The same separation, but weaker", [{"H2": 0.18, "O2": 1.62, "CO2": 0.0} for _ in range(10)], 3600, [0, 30, 60, 600, 1800, 3600], drift=0.005, **tuned)
