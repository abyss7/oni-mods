// Equilibrium of a liquid and its vapour: boiling, evaporation and condensation depending on pressure, with latent heat.
// Independent of the game and the simulation (check: sim/model/phase_check). Both plugins use the formulas: the gas
// mixture layer (cells with a mixture) and the pressure plugin (cells with a single gas, when there is no mixture mod).
//
// Physics. At temperature T a substance has a saturated vapour pressure:
//     ln(P_sat / P_ref) = k * (1 - T0 / T),   k = L / (R * T0),
// where T0 is the boiling point from the game data, P_ref the pressure at which the substance boils at it (one atmosphere
// unless stated otherwise), L the molar heat of vaporisation. More vapour than P_sat: the excess condenses. Less, with liquid
// nearby: it evaporates, and if P_sat is above the pressure on the liquid, it boils. Other gases do not affect the
// equilibrium: only the vapour's own pressure counts. Above the critical temperature no pressure liquefies the gas.
// The transition absorbs or releases the heat L.
//
// Substance data. The game data has no heat of vaporisation and no critical point. By default both are estimated from the
// boiling point: k about 10.5 (Trouton's rule), critical temperature 1.65 * T0. For substances whose real numbers are
// known (Water, Carbon Dioxide, Oxygen and others), the mod sends them separately (Substance).
//
// Pressure. In the game the pressure of a gas is its mass in the cell: a valve closes at 2 kg, gas emission
// stops at 1.8 kg, whatever gas fills the cell. In physics pressure is set by the number of molecules and the
// temperature. The rules can count both ways (Scale); the player chooses:
//   - by mass: one atmosphere is atmosphereMass kilograms of any gas in a cell (1.8 kg: the largest gas mass
//     in the game data); pressure does not depend on temperature or molar mass;
//   - by molecules: a cell is a cubic metre of ideal gas, P = (m / M) * R * T.
// The weight of liquid above a cell counts the same either way: a kilogram in the cell above adds 9.81 Pa.
//
// Game units: mass in kilograms, specific heat capacity in (DTU/g)/K, i.e. kJ/(kg*K); molar mass
// in g/mol. Latent heat in kJ/kg, and "mass * heat / cell heat capacity" gives degrees. Pressure here is
// in atmospheres throughout.
#pragma once

#include <float.h>
#include <math.h>
#include <stdint.h>

#include <vector>

namespace phase
{
	const float TroutonRatio = 10.5f;   // L / (R * T0) by default
	const float CriticalRatio = 1.65f;  // critical temperature to boiling point by default
	const float GasConstant = 8.314f;   // J/(mol*K)
	const float Atmosphere = 101325.0f; // Pa
	// Pressure of a liquid column: a kilogram of liquid in the cell above adds this many atmospheres (a cell is a
	// cubic metre, its floor a square metre). Ten cells of water give about one atmosphere.
	const float WeightPressure = 9.81f / Atmosphere;
	// A liquid boils when its vapour pressure exceeds the pressure on it by at least this share and it is hotter than the
	// boiling point at that pressure by at least this many degrees: without the margin boiling would flicker at the very boundary.
	const float BoilPressureMargin = 1.02f;
	const float BoilSuperheat = 0.5f;

	// How the mass of gas in a cell becomes pressure.
	struct Scale
	{
		bool byMass = false;
		float atmosphereMass = 1.8f; // how many kilograms of gas in a cell count as one atmosphere ("by mass" mode)

		// Pressure of gas of which the cell holds mass kilograms (moles: the same in kilomoles), atm.
		float Pressure(float mass, float moles, float temperature) const
		{
			return byMass ? mass / atmosphereMass : moles * 1000.0f * GasConstant * temperature / Atmosphere;
		}

		// How many kilograms of vapour with molar mass molar give pressure pressure (atm) in a cell at temperature T.
		float Mass(float pressure, float molar, float temperature) const
		{
			return byMass ? pressure * atmosphereMass : pressure * Atmosphere * molar * 1e-3f / (GasConstant * temperature);
		}
	};

	// Substance data the mod sends instead of estimates from the boiling point; zero: keep the estimate.
	struct Substance
	{
		float ratio = 0.0f;     // k = L / (R * T0)
		float critical = 0.0f;  // critical temperature
		float latent = 0.0f;    // heat of vaporisation, kJ/kg
		float reference = 0.0f; // pressure at which the substance boils at the game's boiling point, atm
	};

	// A "gas and its liquid" pair with a table of saturated vapour mass by temperature (one-degree step): an exponential
	// for every cell every tick would cost more than the rest of the calculation.
	class Vapour
	{
	public:
		float boil = 0.0f;      // boiling point from the game data
		float molar = 0.0f;     // molar mass of the vapour
		float ratio = 0.0f;     // k
		float reference = 1.0f; // pressure at which the substance boils at boil, atm
		float critical = 0.0f;  // above this temperature vapour does not condense and the liquid does not exist
		float latent = 0.0f;    // latent heat, kJ/kg
		Scale scale;

		void Set(float boilPoint, float molarMass, const Scale& pressureScale = Scale(), const Substance& known = Substance())
		{
			boil = boilPoint;
			molar = molarMass;
			scale = pressureScale;
			ratio = known.ratio > 0.0f ? known.ratio : TroutonRatio;
			reference = known.reference > 0.0f ? known.reference : 1.0f;
			critical = known.critical > 0.0f ? known.critical : boilPoint * CriticalRatio;
			latent = known.latent > 0.0f ? known.latent : ratio * GasConstant * boilPoint / molarMass;
			table_.assign((size_t)critical + 3, 0.0f);
			for (size_t t = 1; t < table_.size(); t++) table_[t] = scale.Mass(SaturationPressureExact((float)t), molar, (float)t);
		}

		bool Valid() const { return !table_.empty(); }

		// Saturated vapour pressure by the formula, atm.
		float SaturationPressureExact(float temperature) const
		{
			return reference * expf(ratio * (1.0f - boil / temperature));
		}

		// Vapour mass in a cell at which its pressure equals the saturated vapour pressure; above the critical
		// temperature there is no limit.
		float SaturationMass(float temperature) const
		{
			if (!(temperature < critical)) return FLT_MAX;
			if (!(temperature > 0.0f)) return 0.0f;
			const size_t at = (size_t)temperature;
			return table_[at] + (table_[at + 1] - table_[at]) * (temperature - (float)at);
		}

		// Saturated vapour pressure, atm (from the mass table, no exponential); above the critical temperature there is no limit.
		float SaturationPressure(float temperature) const
		{
			if (!(temperature < critical)) return FLT_MAX;
			const float mass = SaturationMass(temperature);
			return scale.Pressure(mass, mass / molar, temperature);
		}

		// How much the saturated vapour mass grows per degree.
		float Slope(float temperature) const
		{
			if (!(temperature < critical) || !(temperature > 0.0f)) return 0.0f;
			const size_t at = (size_t)temperature;
			return table_[at + 1] - table_[at];
		}

		// Temperature at which the liquid boils under pressure pressure (atm).
		float BoilingPoint(float pressure) const
		{
			if (!(pressure > 0.0f)) return 0.0f;
			const float denominator = 1.0f - logf(pressure / reference) / ratio;
			return denominator > 0.0f ? boil / denominator : FLT_MAX;
		}

	private:
		std::vector<float> table_;
	};

	// How much vapour condenses in a gas cell per step. mass: vapour mass in the cell; capacity: heat capacity
	// of the whole cell including this vapour, kJ/K. Latent heat warms the cell, and a warmer cell holds more vapour,
	// so not all of the excess condenses, only as much as leaves the remainder saturated at the new
	// temperature. It is found by bisection between the current one and the one the whole vapour would heat the cell to (but
	// not above critical: condensation stops there). Only computed for cells with an excess; there are few of them.
	// After the call: vapour mass - x, temperature T + x * latent / capacity.
	inline float Condense(const Vapour& vapour, float mass, float temperature, float capacity)
	{
		if (!(mass - vapour.SaturationMass(temperature) > 0.0f) || !(capacity > 0.0f)) return 0.0f;
		const float perDegree = capacity / vapour.latent; // kilograms of condensate per degree of cell warming
		float cold = temperature, warm = temperature + mass / perDegree;
		if (warm > vapour.critical) warm = vapour.critical;
		// Remaining vapour at temperature t minus the limit at that same t: decreases as t grows.
		auto surplus = [&](float t) { return mass - (t - temperature) * perDegree - vapour.SaturationMass(t); };
		if (surplus(warm) > 0.0f) cold = warm;
		else
		{
			for (int pass = 0; pass < 20; pass++)
			{
				const float middle = 0.5f * (cold + warm);
				if (surplus(middle) > 0.0f) cold = middle;
				else warm = middle;
			}
		}
		const float x = (cold - temperature) * perDegree;
		return x < 0.0f ? 0.0f : x > mass ? mass : x;
	}

	// How much liquid boils off per step. pressure: pressure on the liquid, atm (gas above it and the weight of liquid above);
	// outletVapour and outletTemperature: how much of its vapour is already in the gas cell the vapour goes to, and the
	// temperature of that cell (both zero for vacuum and for a cell whose foreign gas the vapour will displace); freeze: the
	// freezing point of the liquid. The heat above the boiling point at that pressure goes into vapour (share rate per step;
	// below the freezing point boiling does not cool the liquid), but no more than two limits. The first: how much vapour is
	// needed for the pressure in the receiving cell to equal the liquid's vapour pressure (no further boiling beyond that).
	// The second: how much vapour that cell holds at its own temperature. Without the second, a liquid hotter than the gas above
	// it would supersaturate it every step, the excess would condense at once, the drop would come back and boil again,
	// round and round until the temperatures equalise. With it, a hot liquid under cold saturated gas gives off heat by
	// ordinary heat exchange and boils as the gas warms up. Not boiling: 0.
	// factor: how many times this liquid's vapour pressure differs from that of the pure substance (a mixture of
	// liquids, Raoult's law; 1 for a pure liquid). The liquid boils when its vapour pressure in the mixture is above the pressure
	// over it, so the mixture's boiling point is where the pure substance's pressure is 1/factor times higher.
	inline float Boil(const Vapour& vapour, float liquidMass, float liquidTemperature, float liquidHeat, float pressure,
		float outletVapour, float outletTemperature, float freeze, float rate, float factor = 1.0f)
	{
		if (!(liquidMass > 0.0f) || !(liquidTemperature > 0.0f) || !(liquidHeat > 0.0f) || !(liquidTemperature < vapour.critical) || !(factor > 0.0f)) return 0.0f;
		const float saturated = factor * vapour.SaturationPressure(liquidTemperature);
		if (!(saturated > pressure * BoilPressureMargin)) return 0.0f;
		float point = vapour.BoilingPoint(pressure / factor);
		if (point < freeze) point = freeze;
		const float over = liquidTemperature - point;
		if (!(over > BoilSuperheat)) return 0.0f;
		const float byHeat = rate * liquidMass * liquidHeat * over / (vapour.latent + liquidHeat * over);
		const float outlet = outletTemperature > 0.0f ? outletTemperature : liquidTemperature;
		const float byPressure = vapour.scale.Mass(saturated - pressure, vapour.molar, outlet);
		const float room = vapour.SaturationMass(outlet) - outletVapour;
		float x = byHeat < byPressure ? byHeat : byPressure;
		if (x > room) x = room;
		return x < 0.0f ? 0.0f : x > liquidMass ? liquidMass : x;
	}

	// Condensate leaves the cell as a drop, and in the game a drop is never hotter than its liquid's boiling point. If
	// the cell is hotter after condensation (temperature), the drop leaves at temperature hottest, and the heat it did
	// not carry away stays in the cell. Returns the new cell temperature; leave: the drop temperature. capacity:
	// heat capacity of the cell before the portion leaves, portion: heat capacity of the portion itself (mass times specific heat).
	inline float KeepHeat(float temperature, float capacity, float portion, float hottest, float* leave)
	{
		*leave = temperature;
		if (!(temperature > hottest) || !(capacity - portion > 0.0f)) return temperature;
		*leave = hottest;
		return (capacity * temperature - portion * hottest) / (capacity - portion);
	}

	// What happens at the boundary of a liquid and the gas cell next to it.
	struct Surface
	{
		float evaporated; // how much liquid became vapour, kg
		bool boiling;     // saturated vapour pressure above the gas pressure: the liquid boils (use Boil)
	};

	// How much liquid evaporates from the surface into the adjacent gas cell per step when the liquid is not boiling.
	// liquidMass, liquidTemperature, liquidHeat: mass, temperature and specific heat of the liquid; vapourMass:
	// how much of its vapour is already in the gas cell; gasTemperature and gasPressure: temperature and total gas pressure
	// in it (both zero in vacuum). Evaporation cools the liquid and its vapour pressure drops: take as much as makes
	// the vapour in the gas cell equal the vapour pressure of the already cooled liquid. But no more than the gas cell
	// holds at its own temperature: otherwise a hot liquid would saturate cold gas beyond its limit every step,
	// the excess would condense at once, and so round and round. The share evaporationRate of that amount leaves per
	// step: in reality the evaporation rate is set by how fast vapour is carried away from the surface.
	// factor: as in Boil (vapour pressure of a liquid mixture relative to the pure substance). Vapour in the gas cell
	// is compared with the mixture's vapour pressure, while the gas cell's own limit (how much vapour it holds) does not depend on the mixture.
	inline Surface Evaporate(const Vapour& vapour, float liquidMass, float liquidTemperature, float liquidHeat,
		float vapourMass, float gasTemperature, float gasPressure, float evaporationRate, float factor = 1.0f)
	{
		Surface result = { 0.0f, false };
		if (!(liquidMass > 0.0f) || !(liquidTemperature > 0.0f) || !(liquidHeat > 0.0f) || !(factor > 0.0f)) return result;
		// Above the critical temperature there is no liquid: that is decided not by evaporation but by the whole cell's transition.
		if (!(liquidTemperature < vapour.critical)) return result;
		const float gasT = gasTemperature > 0.0f ? gasTemperature : liquidTemperature;
		const float saturated = factor * vapour.SaturationPressure(liquidTemperature);
		result.boiling = saturated > gasPressure * BoilPressureMargin;
		// Vapour mass in a gas cell whose pressure equals the liquid's vapour pressure; scale: how many times it
		// differs from the saturated vapour mass at the liquid's temperature (in "by mass" mode it does not).
		const float atLiquid = factor * vapour.SaturationMass(liquidTemperature);
		const float balance = vapour.scale.Mass(saturated, vapour.molar, gasT);
		const float scale = atLiquid > 0.0f ? balance / atLiquid : 1.0f;
		const float room = vapour.SaturationMass(gasT);
		const float deficit = (balance < room ? balance : room) - vapourMass;
		if (!(deficit > 0.0f)) return result;
		const float perKilogram = vapour.latent / (liquidMass * liquidHeat); // degrees of cooling per kilogram
		const float x = evaporationRate * deficit / (1.0f + factor * vapour.Slope(liquidTemperature) * scale * perKilogram);
		result.evaporated = x < 0.0f ? 0.0f : x > liquidMass ? liquidMass : x;
		return result;
	}
}
