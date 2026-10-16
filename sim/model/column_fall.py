"""The gravity rule adopted for the mixture layer (proposed by the project's author), on the same column of cells as column.py.

Each gas has its own heaviness (molar mass), and a cell has the mean heaviness of its mixture by molecule count. Gas in the upper
cell that is heavier than the mixture of the lower cell partly falls down; the shared pressure then evens out the cell masses.
Light gases rise on their own: pressure pushes them up. The layers are permanent, with a blurred boundary; its width
is set by the ratio of gravity to mixing.

  python3 sim/model/column_fall.py three      three gases in one room, the layer's working numbers
  python3 sim/model/column_fall.py            scenarios with two gases
  python3 sim/model/column_fall.py mass       the same, but the mean is by mass shares (for comparison)
  python3 sim/model/column_fall.py own        each gas has its own pressure (for comparison: the layers almost vanish)
"""
import importlib.util, io, contextlib, sys
spec = importlib.util.spec_from_file_location("column", "sim/model/column.py")
col = importlib.util.module_from_spec(spec)
with contextlib.redirect_stdout(io.StringIO()):
    spec.loader.exec_module(col)
M = col.MOLAR


BY_MOLECULES = True
# The layer's working numbers (decided 2026-10-11): stratification of three gases in a column of 10 cells covers 90% of the way
# in 8 minutes (3.5 in the game); cell mass deviates from the mean by no more than 12% along the way and 7% at the end.
PRESSURE, DIFFUSION, GRAVITY = 0.2, 0.004, 0.02


def mean_by_mass(c):
    if BY_MOLECULES:
        return col.heavy(c)  # mean by molecule count: cell mass divided by the number of moles
    t = col.total(c)
    return sum(m * M[g] for g, m in c.items()) / t if t > 0 else 0.0


def fall(cells, gravity, counterflow=False):
    """All pairs are computed from the same state; gas falls only one cell down.
    counterflow: instead of the fallen gas, the same amount of the lower cell's mixture goes up right away, so gravity does not change
    the cell masses. Without it pressure returns the mass, and with strong gravity it cannot keep up."""
    means = [mean_by_mass(c) for c in cells]
    new = [dict(c) for c in cells]
    for low in range(len(cells) - 1):
        up = low + 1
        fell = 0.0
        for g in M:
            have = cells[up].get(g, 0.0)
            if have <= 0:
                continue
            below = means[low]
            excess = (M[g] - below) / (M[g] + below) if below > 0 else 1.0  # 0..1: how much heavier the gas is than the mixture below it
            if excess <= 0:
                continue
            moved = min(1.0, gravity * excess) * have
            new[up][g] -= moved
            new[low][g] = new[low].get(g, 0.0) + moved
            fell += moved
        if counterflow and fell > 0:
            weight = col.total(cells[low])
            back = min(fell, 0.5 * weight)
            for g in M:
                moved = back * cells[low].get(g, 0.0) / weight
                new[low][g] -= moved
                new[up][g] = new[up].get(g, 0.0) + moved
    return new


def independent_pressure(cells, k):
    new = [dict(c) for c in cells]
    for low in range(len(cells) - 1):
        up = low + 1
        for g in M:
            a, b = cells[low].get(g, 0.0), cells[up].get(g, 0.0)
            moved = max(-b / 8, min(a / 8, k * (a - b)))
            new[low][g] -= moved; new[up][g] += moved
    return new


def run(title, cells, gas, marks, gravity, diffusion=0.001, own_pressure=False, emit=None, emit_until=0):
    print(f"\n{title}  [gravity {gravity}, diffusion {diffusion}, pressure {'per gas' if own_pressure else 'shared'}]")
    for t in range(int(max(marks) / 0.2) + 1):
        now = t * 0.2
        if any(abs(now - m) < 0.1 for m in marks):
            masses = [col.total(c) for c in cells]
            print(f"  {now:6.0f} s  {gas} share, ceiling to floor: " + " ".join(f"{col.share(c, gas) * 100:3.0f}" for c in reversed(cells)) + f"   cell mass {min(masses):.2f}..{max(masses):.2f}")
        if emit and now < emit_until:
            for cell, g, rate in emit:
                cells[cell][g] = cells[cell].get(g, 0.0) + rate * 0.2
        cells = independent_pressure(cells, 0.12) if own_pressure else col.flow_and_diffusion(cells, 0.12, diffusion)
        cells = fall(cells, gravity)


o2 = lambda: {"H2": 0.0, "O2": 1.8, "CO2": 0.0}
half = lambda: {"H2": 0.0, "O2": 0.9, "CO2": 0.9}
h2 = lambda: {"H2": 1.8, "O2": 0.0, "CO2": 0.0}
co2 = lambda: {"H2": 0.0, "O2": 0.0, "CO2": 1.8}
long = [0, 2, 10, 30, 60, 300, 600, 1800, 3600]

def molecules(cell, gas):
    n = sum(m / M[g] for g, m in cell.items())
    return cell.get(gas, 0.0) / M[gas] / n if n else 0.0


which = sys.argv[1] if len(sys.argv) > 1 else "all"
if which == "three":
    cells = [{"H2": 0.09, "O2": 1.11, "CO2": 0.6} for _ in range(10)]
    print("Three gases mixed evenly over the height; working numbers: pressure 0.2, diffusion 0.004, gravity 0.02. Share by molecule count, ceiling to floor.")
    for t in range(int(3 * 3600 / 0.2) + 1):
        if t in (0, 150, 300, 600, 1500, 3000, 9000, 54000):
            print(f"  {t * 0.2 / 60:5.0f} min")
            for g in M:
                print(f"    {g:3}  " + " ".join(f"{molecules(c, g) * 100:3.0f}" for c in reversed(cells)))
        cells = fall(col.flow_and_diffusion(cells, PRESSURE, DIFFUSION), GRAVITY)
    sys.exit(0)
if which == "mass":
    BY_MOLECULES = False
    which = "all"
if which == "molecules":
    for g in (0.05, 0.3, 1.0):
        run("Mean by molecules. A cell of pure H2 at the floor", [h2()] + [o2() for _ in range(9)], "H2", [0, 2, 5, 10, 30, 60, 300, 1800, 3600], gravity=g)
    run("Mean by molecules. Electrolyzer for 60 s into a cell at the floor", [o2() for _ in range(10)], "H2", [0, 30, 60, 300, 600, 1800, 3600], gravity=0.3, emit=[(0, "O2", 0.888), (0, "H2", 0.112)], emit_until=60)
    run("Mean by molecules. The author's example: CO2 and O2 half and half", [half() for _ in range(10)], "CO2", [0, 60, 300, 600, 1800, 3600], gravity=0.05)
if which in ("all", "example"):
    for g in (0.01, 0.05, 0.3):
        run("The author's example: 10 cells of CO2 and O2 half and half", [half() for _ in range(10)], "CO2", long, gravity=g)
if which in ("all", "h2"):
    for g in (0.05, 0.3):
        run("A cell of pure H2 at the floor, Oxygen above", [h2()] + [o2() for _ in range(9)], "H2", long, gravity=g)
if which in ("all", "premix"):
    run("Electrolyzer for 60 s into a cell at the floor", [o2() for _ in range(10)], "H2", [0, 30, 60, 300, 600, 1800, 3600], gravity=0.3, emit=[(0, "O2", 0.888), (0, "H2", 0.112)], emit_until=60)
    run("Electrolyzer for 60 s into a cell at the floor", [o2() for _ in range(10)], "H2", [0, 30, 60, 300, 600, 1800, 3600], gravity=0.01, emit=[(0, "O2", 0.888), (0, "H2", 0.112)], emit_until=60)
if which in ("all", "own"):
    run("The author's example, but each gas has its own pressure", [half() for _ in range(10)], "CO2", [0, 10, 60, 600, 3600], gravity=0.05, own_pressure=True)
    run("A cell of pure H2 at the floor, each gas has its own pressure", [h2()] + [o2() for _ in range(9)], "H2", [0, 2, 10, 60, 600, 3600], gravity=0.3, own_pressure=True)
