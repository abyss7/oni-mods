// Services the liquid mixture plugin offers to other plugins (simhook.h, SimHookService). They plug into the phase rules (gas
// mixture layer and the pressure plugin): the liquid of a mixture cell evaporates by its own liquids, not by the main one.
#pragma once

#include <stdint.h>

#include "sim_types.h"

// Gas produced by evaporation of a mixture liquid.
struct LiquidVapourOut
{
	uint16_t gas;
	uint16_t pad;
	float mass;
};

// "liquid-mix.evaporate": cell loses up to mass kg of liquid by evaporation at temperature temperature. If the cell
// holds a mixture, the loss is split among its liquids by Raoult's law (each one's share by its vapour pressure in the mixture), the
// cell composition is recomputed, and the gases of the portion are written to out (up to 4 entries; identical gases summed). gas: only
// liquids that produce this gas evaporate (0xFFFF: all). The solid residue (salt of Brine, dirt of Polluted Water) the liquid plugin
// releases itself. taken receives how much liquid actually left (less than mass if those liquids ran short).
// Returns the number of gases in out; 0: the cell is not a mixture, compute as before. The caller reduces the cell mass itself, after
// the call, by taken.
typedef int32_t (*LiquidEvaporateFn)(SimData* sim, int32_t cell, float mass, float temperature, uint16_t gas, LiquidVapourOut* out, float* taken);

// Mixture vapour: the gas, the liquid whose vapour pressure curve it is computed by, and the correction to that curve.
struct LiquidVapourEntry
{
	uint16_t gas;
	uint16_t liquid;
	float factor;
};

// "liquid-mix.vapours": which vapours the liquid of cell cell gives at temperature temperature, each with its own equilibrium.
// For each gas the mixture liquids boil into: the liquid whose vapour pressure curve is taken (the one that gives the most of this
// vapour), and the correction: the sum of mole fractions times vapour pressures of the liquids giving this gas, divided by the vapour
// pressure of the chosen one (Raoult's law). The phase rules compute evaporation and boiling of each vapour separately, against its own
// partial pressure in the gas. Up to 4 entries; 0: the cell is not a mixture (compute by its element).
typedef int32_t (*LiquidVapoursFn)(SimData* sim, int32_t cell, float temperature, LiquidVapourEntry* out);

// "liquid-mix.vapour-factor": how many times the vapour pressure of the liquid of cell cell at temperature temperature differs
// from the vapour pressure of its main liquid (Raoult's law: the sum over the mixture liquids giving the same vapour of mole fraction
// times saturated vapour pressure, divided by the vapour pressure of the main one). 1 if the cell is not a mixture. The phase rules multiply
// the liquid vapour pressure by this number (phase::Boil, phase::Evaporate): water with salt boils later than pure water.
typedef float (*LiquidVapourFactorFn)(SimData* sim, int32_t cell, float temperature);
