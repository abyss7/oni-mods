#!/usr/bin/env python3
"""Compares the C++ mixture layer kernel with the Python model (column_fall.py) on a column of 10 cells.

  python3 sim/model/kernel_check.py <path to the built kernel_check>

The kernel computes in float and over all cells at once, the model in double and pair by pair, so the match
is expected not bit for bit but within a tolerance."""
import importlib.util, io, contextlib, os, subprocess, sys

spec = importlib.util.spec_from_file_location("fall", "sim/model/column_fall.py")
fall = importlib.util.module_from_spec(spec)
argv = sys.argv; sys.argv = ["x", "none"]
with contextlib.redirect_stdout(io.StringIO()):
    spec.loader.exec_module(fall)
sys.argv = argv
col = fall.col

lines = subprocess.run([sys.argv[1]], capture_output=True, text=True, check=True).stdout.splitlines()
# The same calculation with the plain step variant (no AVX): the numbers must match to the last digit.
plain_lines = subprocess.run([sys.argv[1]], capture_output=True, text=True, check=True, env=dict(os.environ, GAS_MIX_PLAIN="1")).stdout.splitlines()
def numbers(text):
    return [line.split(" step_ms")[0] for line in text if not line.startswith("variant")]
def step_times(text):
    return {line.split()[0]: line.split()[-1] for line in text if " step_ms " in line}

variants = [line.split()[1] for line in lines + plain_lines if line.startswith("variant")]
same = numbers(lines) == numbers(plain_lines)
native = {}
grid = regions = leaked = heat = hole = room = passive = colony = None
for line in lines:
    parts = line.split()
    if parts[0] == "column":
        native[(int(parts[1]), int(parts[2]))] = [float(v) for v in parts[3:6]]
    elif parts[0] == "leaked":
        leaked = float(parts[1])
    elif parts[0] == "heat":
        heat = dict(zip(parts[1::2], map(float, parts[2::2])))
    elif parts[0] == "grid":
        grid = dict(zip(parts[1::2], parts[2::2]))
    elif parts[0] == "regions":
        regions = dict(zip(parts[1::2], parts[2::2]))
    elif parts[0] == "hole":
        hole = dict(zip(parts[1::2], map(float, parts[2::2])))
    elif parts[0] == "room":
        room = dict(zip(parts[1::2], map(float, parts[2::2])))
    elif parts[0] == "colony":
        colony = dict(zip(parts[1::2], parts[2::2]))
    elif parts[0] == "passive":
        passive = dict(zip(parts[1::2], map(float, parts[2::2])))

cells = [{"H2": 0.09, "O2": 1.11, "CO2": 0.6} for _ in range(10)]
worst = 0.0
for tick in range(9001):
    if tick in (0, 300, 3000, 9000):
        for y, cell in enumerate(cells):
            for value, gas in zip(native[(tick, y)], ("H2", "O2", "CO2")):
                worst = max(worst, abs(value - cell[gas]) / max(cell[gas], 1e-3))
    cells = fall.fall(col.flow_and_diffusion(cells, fall.PRESSURE, fall.DIFFUSION), fall.GRAVITY)

before, after = float(grid["mass_before"]), float(grid["mass_after"])
# Heat: the total is conserved, temperatures converged to a common one between the initial 290 and 350.
heat_ok = abs(heat["energy_after"] - heat["energy_before"]) / heat["energy_before"] < 1e-4 and 290 < heat["coldest"] and heat["hottest"] < 350 and heat["hottest"] - heat["coldest"] < 1.0
print(f"heat: total {heat['energy_before']:.3f} -> {heat['energy_after']:.3f}, temperatures after 60000 ticks from {heat['coldest']:.2f} to {heat['hottest']:.2f}")
ok = heat_ok and worst < 2e-3 and leaked == 0.0 and abs(after - before) / before < 1e-6 and float(grid["least"]) >= 0.0 and float(grid["in_walls"]) == 0.0
print(f"column: largest difference of the kernel from the model {worst * 100:.4f}% over 9000 ticks; leak through walls {leaked}")
print(f"colony grid, {grid['gases']} gases: mass {before:.3f} -> {after:.3f}, least mass {grid['least']}, in walls {grid['in_walls']}, step {grid['step_ms']} ms")
# Regions: mass inside is conserved, cells outside are untouched.
r_before, r_after = float(regions["mass_before"]), float(regions["mass_after"])
ok = ok and abs(r_after - r_before) / r_before < 1e-6 and float(regions["least"]) >= 0.0 and int(regions["touched_outside"]) == 0
print(f"regions only ({regions['active']} cells): mass {r_before:.3f} -> {r_after:.3f}, cells outside changed {regions['touched_outside']}, step {regions['step_ms']} ms")
# Spreading along an obstacle: Hydrogen under the ceiling leaves through the hole an order of magnitude faster than without spreading
# (without it half does not leave even in 150 minutes), mass is conserved; in a closed room the stratification is the same.
without = hole["half_without_min"]
ok = ok and 0 < hole["half_with_spread_min"] < 30 and (without < 0 or without > 5 * hole["half_with_spread_min"]) and abs(hole["mass_drift"]) < 1e-5
ok = ok and room["layering_difference"] < 1e-4 and room["horizontal_unevenness"] < 1e-4
print(f"spreading: half of the Hydrogen under the ceiling leaves through the hole in {hole['half_with_spread_min']:.1f} min, without spreading {'not gone in 200 min' if without < 0 else f'in {without:.1f} min'}; mass drift {hole['mass_drift']:.1e}")
print(f"closed room: stratification with and without spreading differs by no more than {room['layering_difference']:.1e} kg per cell, horizontal unevenness {room['horizontal_unevenness']:.1e} kg")
# A grid resembling a base (half the cells open, rooms close and open on the go): mass is conserved,
# closed cells hold no gas.
c_before, c_after = float(colony["mass_before"]), float(colony["mass_after"])
ok = ok and abs(c_after - c_before) / c_before < 1e-5 and float(colony["least"]) >= 0.0 and float(colony["in_walls"]) == 0.0
print(f"base-like grid ({colony['open']} of 80090 cells open): mass {c_before:.3f} -> {c_after:.3f}, in walls {colony['in_walls']}, step {colony['step_ms']} ms")
# Passenger (germs): the sum is conserved, none of it is in walls, a disabled field is not touched by the step. On the left there were 333 per
# kilogram of gas, on the right zero. Pressure evens out by flow from left to right, and the passenger rides along: on the right, its own
# 0.5 kg receives 1.25 kg carrying the passenger, i.e. 238 per kilogram. Then diffusion slowly smooths the difference:
# the least grows from 238, the most falls from 333.
ok = ok and abs(passive["sum_after"] - passive["sum_before"]) / passive["sum_before"] < 1e-4 and passive["in_walls"] == 0.0
ok = ok and 238.0 < passive["per_kg_least"] < passive["per_kg_most"] < 333.4 and passive["idle_changed"] == 0
# Transfer by a cell list after the step gives the same numbers as transfer inside the step.
ok = ok and passive["listed_different"] == 0
print(f"passenger: sum {passive['sum_before']:.1f} -> {passive['sum_after']:.1f}, per kilogram of gas from {passive['per_kg_least']:.2f} to {passive['per_kg_most']:.2f}, disabled field changed in {passive['idle_changed']:.0f} cells")
ok = ok and same and variants[1] == "sse2"
fast, slow = step_times(lines), step_times(plain_lines)
print(f"step variants: {variants[0]} and {variants[1]}, numbers {'matched' if same else 'DIVERGED'}; step over regions {fast['regions']} and {slow['regions']} ms, over the whole grid {fast['grid']} and {slow['grid']} ms")
print("KERNEL OK" if ok else "KERNEL FAILED")
sys.exit(0 if ok else 1)
