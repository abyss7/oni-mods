// Check of the liquid-vapour equilibrium rules (sim/plugins/phase_rules.h) without the game: numbers against physics,
// convergence to saturation without oscillation, conservation of heat together with latent heat.
#include <stdio.h>

#include "../plugins/phase_rules.h"

static bool ok = true;

static void Check(bool condition, const char* what)
{
	if (!condition) { printf("  FAILED: %s\n", what); ok = false; }
}

int main()
{
	phase::Vapour steam, oxygen, carbon;
	steam.Set(372.5f, 18.015f);
	oxygen.Set(90.19f, 31.999f);
	carbon.Set(225.0f, 44.01f);

	// 1. Numbers. At the boiling point the vapour pressure equals one atmosphere; the table matches the formula.
	const float atBoil = steam.SaturationPressure(372.5f);
	float worst = 0.0f;
	for (float t = 250.0f; t < 600.0f; t += 0.37f)
	{
		const float exact = steam.scale.Mass(steam.SaturationPressureExact(t), 18.015f, t);
		const float error = fabsf(steam.SaturationMass(t) - exact) / exact;
		if (error > worst) worst = error;
	}
	printf("steam: pressure at the boiling point %.3f atm, table against the formula within %.3f%%, latent heat %.0f kJ/kg\n",
		atBoil, worst * 100.0f, steam.latent);
	printf("water boils at %.0f C under 0.1 atm, %.0f C under 2 atm, %.0f C under 5 atm; a cell holds %.2f kg of steam at 400 K, %.2f kg at 473 K, %.3f kg at 300 K\n",
		steam.BoilingPoint(0.1f) - 273.15f, steam.BoilingPoint(2.0f) - 273.15f,
		steam.BoilingPoint(5.0f) - 273.15f, steam.SaturationMass(400.0f), steam.SaturationMass(473.0f), steam.SaturationMass(300.0f));
	Check(fabsf(atBoil - 1.0f) < 0.002f, "one atmosphere at the boiling point");
	Check(worst < 0.005f, "table precision");
	Check(steam.latent > 1700.0f && steam.latent < 1900.0f, "latent heat of water");
	Check(phase::Condense(oxygen, 1000.0f, 300.0f, 1000.0f) == 0.0f, "oxygen above its critical temperature never condenses");
	Check(carbon.SaturationMass(300.0f) > 20.0f && carbon.SaturationMass(300.0f) < 30.0f, "carbon dioxide limit at 300 K");

	// 2. Condensation: the remainder is saturated at the new temperature, with no under- or overshoot above one percent.
	struct { const char* name; float mass, temperature, capacity; } cases[] = {
		{ "5 kg of steam alone at 400 K", 5.0f, 400.0f, 5.0f * 4.179f },
		{ "0.5 kg of steam in 1 kg of oxygen at 320 K", 0.5f, 320.0f, 0.5f * 4.179f + 1.005f },
		{ "100 kg of steam alone at 380 K", 100.0f, 380.0f, 100.0f * 4.179f },
		{ "0.1 kg of steam in 20 kg of oxygen at 290 K", 0.1f, 290.0f, 0.1f * 4.179f + 20.0f * 1.005f },
	};
	for (const auto& test : cases)
	{
		const float x = phase::Condense(steam, test.mass, test.temperature, test.capacity);
		const float warm = test.temperature + x * steam.latent / test.capacity;
		const float left = test.mass - x, limit = steam.SaturationMass(warm);
		printf("condensation, %s: %.4f kg condensed, cell warms to %.1f K, steam left %.4f kg against the limit %.4f kg\n", test.name, x, warm, left, limit);
		// A cell that latent heat warms up to the critical temperature cannot be saturated by vapour: the vapour stays in excess.
		Check(x > 0.0f && (warm > steam.critical - 0.5f ? left > 0.0f : fabsf(left - limit) < 0.01f * limit), "the rest is saturated at the new temperature");
	}

	// 2b. A drop is no hotter than the boiling point: the heat it did not carry away stays in the cell, and the total heat is the same.
	{
		const float capacity = 5.0f * 4.179f, x = phase::Condense(steam, 5.0f, 400.0f, capacity);
		const float warm = 400.0f + x * steam.latent / capacity;
		float leave = 0.0f;
		const float after = phase::KeepHeat(warm, capacity, x * 4.179f, 375.0f, &leave);
		printf("hot condensate: the cell would be at %.1f K, the drop leaves at %.1f K and the cell stays at %.1f K\n", warm, leave, after);
		Check(leave == 375.0f && after > warm && fabsf((capacity - x * 4.179f) * after + x * 4.179f * leave - capacity * warm) < 0.01f, "heat is kept when the drop leaves colder");
		Check(phase::KeepHeat(350.0f, capacity, 1.0f, 375.0f, &leave) == 350.0f && leave == 350.0f, "a drop within the liquid range leaves as it is");
	}

	// 3. A closed pair of cells: 10 kg of Water at 350 K under a kilogram of Oxygen at 300 K. The water evaporates and
	// cools, the gas warms and saturates; ordinary heat exchange runs between the cells. At the end the vapour is saturated at the common
	// temperature, the mass of water and vapour is unchanged, heat including latent heat is conserved, no oscillation.
	{
		const float heatWater = 4.179f, heatSteam = 4.179f, heatOxygen = 1.005f;
		float liquid = 10.0f, liquidT = 350.0f, vapour = 0.0f, gasT = 300.0f;
		const float oxygenMass = 1.0f;
		auto energy = [&] { return liquid * heatWater * liquidT + (vapour * heatSteam + oxygenMass * heatOxygen) * gasT + vapour * steam.latent; };
		const float energyStart = energy(), massStart = liquid + vapour;
		int reversals = 0;
		float lastVapour = 0.0f, lastChange = 0.0f;
		for (int step = 0; step < 4000; step++)
		{
			const float gasPressure = steam.scale.Pressure(vapour, vapour / 18.015f, gasT) + steam.scale.Pressure(oxygenMass, oxygenMass / 31.999f, gasT);
			const phase::Surface surface = phase::Evaporate(steam, liquid, liquidT, heatWater, vapour, gasT, gasPressure, 0.1f);
			if (surface.evaporated > 0.0f)
			{
				const float x = surface.evaporated;
				const float capacity = vapour * heatSteam + oxygenMass * heatOxygen;
				// Vapour leaves at the liquid's temperature; the remaining liquid gives up the latent heat.
				gasT = (capacity * gasT + x * heatSteam * liquidT) / (capacity + x * heatSteam);
				liquidT -= x * steam.latent / ((liquid - x) * heatWater);
				liquid -= x;
				vapour += x;
			}
			const float capacity = vapour * heatSteam + oxygenMass * heatOxygen;
			const float condensed = phase::Condense(steam, vapour, gasT, capacity);
			if (condensed > 0.0f)
			{
				gasT += condensed * steam.latent / capacity;
				vapour -= condensed;
				liquidT = (liquid * liquidT + condensed * gasT) / (liquid + condensed);
				liquid += condensed;
			}
			// Heat exchange between the cells.
			const float gasCapacity = vapour * heatSteam + oxygenMass * heatOxygen, liquidCapacity = liquid * heatWater;
			const float flow = 0.02f * (liquidT - gasT) * (gasCapacity < liquidCapacity ? gasCapacity : liquidCapacity);
			liquidT -= flow / liquidCapacity;
			gasT += flow / gasCapacity;
			const float change = vapour - lastVapour;
			if (step > 50 && change * lastChange < 0.0f && fabsf(change) > 1e-6f) reversals++;
			lastChange = change;
			lastVapour = vapour;
		}
		const float limit = steam.SaturationMass(liquidT) * liquidT / gasT;
		printf("closed pair: water %.4f kg at %.2f K, steam %.4f kg at %.2f K (balance %.4f kg), mass %.5f of %.5f kg, heat %.1f of %.1f kJ, reversals %d\n",
			liquid, liquidT, vapour, gasT, limit, liquid + vapour, massStart, energy(), energyStart, reversals);
		Check(fabsf(liquidT - gasT) < 0.5f, "temperatures meet");
		Check(fabsf(vapour - limit) < 0.02f * limit, "steam is saturated");
		Check(fabsf(liquid + vapour - massStart) < 1e-3f, "mass is kept");
		Check(fabsf(energy() - energyStart) < 0.002f * energyStart, "heat with the latent part is kept");
		Check(reversals < 5, "no swinging");
	}

	// 3b. Boiling by pressure. Water at 400 K boils under one atmosphere but not under five; under ten cells
	// of water (about one more atmosphere) the boiling point is higher. No more boils off than the heat above the boiling point carries away.
	{
		const float one = 1.0f;
		const float open = phase::Boil(steam, 1000.0f, 400.0f, 4.179f, one, 0.0f, 400.0f, 272.5f, 0.5f);
		const float deep = phase::Boil(steam, 1000.0f, 400.0f, 4.179f, one + 10000.0f * phase::WeightPressure, 0.0f, 400.0f, 272.5f, 0.5f);
		const float pressed = phase::Boil(steam, 1000.0f, 400.0f, 4.179f, 5.0f * one, 0.0f, 400.0f, 272.5f, 0.5f);
		const float cooled = 400.0f - open * steam.latent / ((1000.0f - open) * 4.179f);
		printf("boiling of 1000 kg of water at 400 K: under one atmosphere %.3f kg in a step (cools to %.2f K), under ten more cells of water %.3f kg, under five atmospheres %.3f kg\n",
			open, cooled, deep, pressed);
		Check(open > 0.0f && deep > 0.0f && deep <= open && pressed == 0.0f, "boiling depends on the pressure on the liquid");
		Check(cooled > steam.BoilingPoint(one) - 0.1f, "boiling does not cool the liquid below its boiling point");
	}

	// 3c. A hot liquid under cold gas does not cycle vapour round and round. 10 kg of Water at 390 K (boiling: its vapour
	// pressure is above the gas pressure) under a kilogram of Oxygen that a wall keeps cold (each step the gas
	// loses half of its superheat above 300 K). Boiling is limited by how much vapour the gas holds at its own
	// temperature, so there is nothing to supersaturate it with. Only what belongs to the real cycle condenses:
	// vapour cooled by the wall. Without the limit the liquid would bring the vapour pressure in the gas up to its own every step,
	// and many times more would fall back.
	{
		const float heatWater = 4.179f, heatSteam = 4.179f, heatOxygen = 1.005f, oxygenMass = 1.0f;
		float liquid = 10.0f, liquidT = 390.0f, vapour = 0.0f, gasT = 300.0f, boiledOff = 0.0f, rained = 0.0f, toWall = 0.0f;
		const float energyStart = liquid * heatWater * liquidT + oxygenMass * heatOxygen * gasT;
		int boilingSteps = 0;
		for (int step = 0; step < 400; step++)
		{
			const float gasPressure = steam.scale.Pressure(vapour, vapour / 18.015f, gasT) + steam.scale.Pressure(oxygenMass, oxygenMass / 31.999f, gasT);
			const phase::Surface surface = phase::Evaporate(steam, liquid, liquidT, heatWater, vapour, gasT, gasPressure, 0.1f);
			float x = surface.evaporated;
			if (surface.boiling)
			{
				const float boiling = phase::Boil(steam, liquid, liquidT, heatWater, gasPressure, vapour, gasT, 272.5f, 0.5f);
				if (boiling > x) x = boiling;
				boilingSteps++;
			}
			if (x > 0.0f)
			{
				const float capacity = vapour * heatSteam + oxygenMass * heatOxygen;
				gasT = (capacity * gasT + x * heatSteam * liquidT) / (capacity + x * heatSteam);
				liquidT -= x * steam.latent / ((liquid - x) * heatWater);
				liquid -= x;
				vapour += x;
				boiledOff += x;
			}
			const float capacity = vapour * heatSteam + oxygenMass * heatOxygen;
			const float condensed = phase::Condense(steam, vapour, gasT, capacity);
			if (condensed > 0.0f)
			{
				gasT += condensed * steam.latent / capacity;
				vapour -= condensed;
				liquidT = (liquid * liquidT + condensed * gasT) / (liquid + condensed);
				liquid += condensed;
				rained += condensed;
			}
			const float gasCapacity = vapour * heatSteam + oxygenMass * heatOxygen, liquidCapacity = liquid * heatWater;
			const float flow = 0.02f * (liquidT - gasT) * (gasCapacity < liquidCapacity ? gasCapacity : liquidCapacity);
			liquidT -= flow / liquidCapacity;
			gasT += flow / gasCapacity;
			// Cold wall.
			const float pulled = 0.5f * (gasT - 300.0f);
			gasT -= pulled;
			toWall += pulled * gasCapacity;
		}
		const float energy = liquid * heatWater * liquidT + (vapour * heatSteam + oxygenMass * heatOxygen) * gasT + vapour * steam.latent + toWall;
		printf("hot water under gas kept cold: boiled off %.3f kg in all, condensed back %.3f kg, boiling in %d steps of 400; water %.3f kg at %.1f K, steam %.4f kg at %.1f K, heat %.1f of %.1f kJ\n",
			boiledOff, rained, boilingSteps, liquid, liquidT, vapour, gasT, energy, energyStart);
		// Without the boiling limit 0.35 kg of the 0.41 kg boiled off falls back here.
		Check(boilingSteps > 0 && rained < 0.1f, "boiling does not push steam round in circles");
		Check(fabsf(energy - energyStart) < 0.002f * energyStart && fabsf(liquid + vapour - 10.0f) < 1e-3f, "mass and heat are kept");
	}

	// 4. Water next to vacuum: it boils, cools and loses a few percent of its mass before freezing, not all of it.
	{
		float liquid = 10.0f, liquidT = 300.0f, gone = 0.0f;
		int steps = 0;
		bool boiled = true;
		while (liquidT > 273.0f && steps < 100000)
		{
			// Vapour leaves the neighbouring cell every step (as into space): the pressure there is zero.
			const float amount = phase::Boil(steam, liquid, liquidT, 4.179f, 0.0f, 0.0f, 0.0f, 272.5f, 0.5f);
			boiled = boiled && amount > 0.0f;
			if (!(amount > 0.0f)) break;
			liquidT -= amount * steam.latent / ((liquid - amount) * 4.179f);
			liquid -= amount;
			gone += amount;
			steps++;
		}
		printf("water next to vacuum: boils, cools from 300 K to freezing in %d steps (%.0f s), %.2f kg of 10 evaporated\n", steps, steps * 0.2f, gone);
		Check(boiled && gone > 0.4f && gone < 0.9f && steps > 5, "water in vacuum freezes after losing a few percent");
	}

	// 5. Real substance data and pressure by mass. Water: heat of vaporisation 2257 kJ/kg, critical point
	// 647 K. Carbon Dioxide: at the game's boiling point (225 K) its vapour pressure is 7.3 atm, above 304 K it
	// does not liquefy at all. In "by mass" mode one atmosphere is 1.8 kg of any gas in a cell.
	{
		phase::Scale byMass;
		byMass.byMass = true;
		phase::Substance waterNumbers, carbonNumbers;
		waterNumbers.ratio = 13.1f; waterNumbers.critical = 647.1f; waterNumbers.latent = 2257.0f; waterNumbers.reference = 1.0f;
		carbonNumbers.ratio = 8.8f; carbonNumbers.critical = 304.1f; carbonNumbers.latent = 348.0f; carbonNumbers.reference = 7.3f;
		phase::Vapour water, waterMolecules, dioxide, dioxideMolecules;
		water.Set(372.5f, 18.015f, byMass, waterNumbers);
		waterMolecules.Set(372.5f, 18.015f, phase::Scale(), waterNumbers);
		dioxide.Set(225.0f, 44.01f, byMass, carbonNumbers);
		dioxideMolecules.Set(225.0f, 44.01f, phase::Scale(), carbonNumbers);
		printf("real water: vapour pressure %.3f atm at 300 K (0.035 in life), %.2f at 398 K (2.29), %.1f at 473 K (15.3); boils at %.0f C under 2 atm (120)\n",
			water.SaturationPressure(300.0f), water.SaturationPressure(398.0f), water.SaturationPressure(473.0f), water.BoilingPoint(2.0f) - 273.15f);
		printf("steam in a cell, by mass: %.3f kg at 300 K, %.2f at 383 K, %.2f at 398 K, %.1f at 473 K; by molecules: %.3f, %.2f, %.2f, %.1f\n",
			water.SaturationMass(300.0f), water.SaturationMass(383.0f), water.SaturationMass(398.0f), water.SaturationMass(473.0f),
			waterMolecules.SaturationMass(300.0f), waterMolecules.SaturationMass(383.0f), waterMolecules.SaturationMass(398.0f), waterMolecules.SaturationMass(473.0f));
		printf("carbon dioxide in a cell, by mass: %.2f kg at 180 K, %.1f at 225 K, %.0f at 300 K; by molecules: %.2f, %.1f, %.0f; above %.0f K it never condenses\n",
			dioxide.SaturationMass(180.0f), dioxide.SaturationMass(225.0f), dioxide.SaturationMass(300.0f),
			dioxideMolecules.SaturationMass(180.0f), dioxideMolecules.SaturationMass(225.0f), dioxideMolecules.SaturationMass(300.0f), dioxide.critical);
		Check(fabsf(water.SaturationPressure(372.5f) - 1.0f) < 0.003f && fabsf(water.SaturationMass(372.5f) - 1.8f) < 0.01f, "by mass, one atmosphere of steam at the boiling point is 1.8 kg");
		Check(fabsf(water.SaturationPressure(398.0f) - 2.29f) < 0.15f && fabsf(water.SaturationPressure(300.0f) - 0.035f) < 0.01f, "real water numbers");
		Check(fabsf(water.BoilingPoint(1.5f / 1.8f) - 368.0f) < 3.0f, "by mass, water under 1.5 kg of any gas boils a few degrees below 100 C");
		Check(dioxide.SaturationMass(300.0f) > 80.0f && dioxideMolecules.SaturationMass(300.0f) > 80.0f, "carbon dioxide at 300 K stays a gas up to about a hundred kilograms");
		Check(phase::Condense(dioxide, 1000.0f, 305.0f, 1000.0f) == 0.0f, "carbon dioxide above 304 K never condenses");
		Check(dioxide.SaturationMass(180.0f) < 2.0f, "cold carbon dioxide condenses at ordinary pressure only far below its game boiling point");
		// Saturation in "by mass" mode does not depend on gas temperature: water and air reach the same equilibrium.
		const float capacity = 0.5f * 4.179f + 1.005f;
		const float x = phase::Condense(water, 0.5f, 320.0f, capacity);
		const float warm = 320.0f + x * water.latent / capacity;
		Check(x > 0.0f && fabsf(0.5f - x - water.SaturationMass(warm)) < 0.01f * (0.5f - x), "condensation by mass leaves saturated steam");
		const phase::Surface surface = phase::Evaporate(water, 100.0f, 300.0f, 4.179f, 0.0f, 300.0f, 1.5f / 1.8f, 0.1f);
		Check(!surface.boiling && fabsf(surface.evaporated - 0.1f * water.SaturationMass(300.0f)) < 0.002f, "evaporation by mass goes a tenth of the way to saturation");
		Check(phase::Boil(water, 100.0f, 380.0f, 4.179f, 1.5f / 1.8f, 0.0f, 380.0f, 272.5f, 0.5f) > 0.0f && phase::Boil(water, 100.0f, 380.0f, 4.179f, 3.0f, 0.0f, 380.0f, 272.5f, 0.5f) == 0.0f,
			"by mass, water at 380 K boils under 1.5 kg of gas and does not under 5.4 kg");
	}

	// Liquid mixture (Raoult's law): vapour pressure half that of pure Water. Under one atmosphere such a liquid
	// boils where pure Water is at two atmospheres; at 380 K pure Water boils but the mixture does not; it evaporates up to half
	// as much vapour in the gas.
	{
		phase::Vapour water;
		water.Set(372.5f, 18.015f);
		const float pure = phase::Boil(water, 100.0f, 380.0f, 4.179f, 1.0f, 0.0f, 380.0f, 272.5f, 0.5f);
		const float mixed = phase::Boil(water, 100.0f, 380.0f, 4.179f, 1.0f, 0.0f, 380.0f, 272.5f, 0.5f, 0.5f);
		const float hot = water.BoilingPoint(2.0f) + 2.0f;
		const float mixedHot = phase::Boil(water, 100.0f, hot, 4.179f, 1.0f, 0.0f, hot, 272.5f, 0.5f, 0.5f);
		// Evaporation into a gas cell of the same temperature goes toward the liquid's vapour pressure: half as much for the mixture.
		float vapourPure = 0.0f, vapourMixed = 0.0f;
		for (int i = 0; i < 400; i++)
		{
			vapourPure += phase::Evaporate(water, 1000.0f, 330.0f, 4.179f, vapourPure, 330.0f, 1.0f, 0.1f).evaporated;
			vapourMixed += phase::Evaporate(water, 1000.0f, 330.0f, 4.179f, vapourMixed, 330.0f, 1.0f, 0.1f, 0.5f).evaporated;
		}
		printf("mixture with half the vapour pressure of water: boils at %.1f K under 1 atm (pure %.1f K); at 380 K pure water boils off %.3f kg, the mixture %.3f; at %.1f K the mixture %.3f; steam over it settles at %.4f kg against %.4f over pure water\n",
			water.BoilingPoint(2.0f), water.BoilingPoint(1.0f), pure, mixed, hot, mixedHot, vapourMixed, vapourPure);
		Check(pure > 0.0f && mixed == 0.0f && mixedHot > 0.0f, "a mixture with lower vapour pressure boils later");
		Check(fabsf(vapourMixed - 0.5f * vapourPure) < 0.02f * vapourPure, "evaporation stops at the vapour pressure of the mixture");
	}

	printf(ok ? "PHASE OK\n" : "PHASE FAILED\n");
	return ok ? 0 : 1;
}
