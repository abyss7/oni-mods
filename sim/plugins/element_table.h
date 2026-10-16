// Properties of the game's elements that the mod sends to the plugins (the simulation has its own tables, but they are harder to reach),
// and the "vapour and its liquid" pairs built from them for the phase rules. The record is shared by the gas mixture layer plugin and
// the pressure plugin: the mods send it the same way, each to its own plugin.
#pragma once

#include <stdint.h>
#include <string.h>

#include <vector>

#include "phase_rules.h"
#include "sim_types.h"

const uint16_t NoElement = 0xFFFF;

struct ElementInfo
{
	float molar; // molar mass
	float heat;  // specific heat capacity
	uint8_t state;
	uint8_t flags; // bit 1: the gas is breathable; bit 2: the solid or liquid emits gas into the neighbouring cell
	uint16_t lowTransition; // what the element turns into when cooled below lowTemp (element number), 0xFFFF: nothing
	float lowTemp;
	uint16_t lowOre;        // what drops out as a separate substance in that case ("ore": Sulfur for Sour Gas), 0xFFFF: nothing
	uint16_t pad;
	float lowOreShare;      // what share of the mass goes into the ore
	float conductivity;     // thermal conductivity
	uint16_t highTransition; // what the element turns into when heated above highTemp, 0xFFFF: nothing
	uint16_t highOre;        // what remains as a separate substance in that case (Salt for Salt Water), 0xFFFF: nothing
	float highTemp;
	float highOreShare;
};
// By what share a liquid's transition point into gas may differ from that gas's condensation point for the transition
// to count as boiling (5% for Polluted Water, 66% for latex and Carbon Dioxide).
const float BoilingMatch = 0.1f;
const uint8_t ElementBreathable = 1;
const uint8_t ElementEmitsGas = 2;
static_assert(sizeof(ElementInfo) == 40, "element record layout is shared with the mods");

struct ElementTable;
// Where to put liquid that cannot appear as a falling drop (it is hotter than the simulation allows a drop to be,
// or the cell is not revealed to the player): into the cell where the drop would have landed. From the cell downwards while there is gas or vacuum below;
// if the same liquid lies below, the portion is added to it. This way the condensate of a whole room collects at its bottom
// instead of occupying gas cells where it appeared (the gas would then have nowhere to be displaced to).
inline int32_t LandingCell(const SimData* sim, const ElementTable* elements, int32_t cell, uint16_t liquid);

struct ElementTable
{
	std::vector<ElementInfo> elements;
	bool IsGas(uint16_t element) const { return element < elements.size() && elements[element].state == SIM_GAS; }
};

// Phase rule settings the pressure mod sends to the plugin as one block: a header followed by substance records.
struct PhaseConfigHeader
{
	int32_t byMass;        // pressure by gas mass (1) or by number of molecules (0)
	float atmosphereMass;  // how many kilograms of gas in a cell count as one atmosphere in "by mass" mode
	float evaporationRate; // what share of the way to equilibrium a non-boiling liquid covers per pass
	float boilRate;        // what share of the heat above the boiling point a boiling liquid gives to the vapour per pass
	float portion;         // condensate portions smaller than this are not released, kg (the mixture layer has its own threshold)
	int32_t substances;    // how many PhaseSubstance records follow
};

// Real numbers of a substance instead of estimates from the boiling point (phase::Substance), by the number of its gas.
struct PhaseSubstance
{
	int32_t gas;
	float ratio;
	float critical;
	float latent;
	float reference;
};
static_assert(sizeof(PhaseConfigHeader) == 24 && sizeof(PhaseSubstance) == 20, "phase config layout is shared with the mods");

struct PhaseConfig
{
	PhaseConfigHeader header = { 0, 1.8f, 0.1f, 0.5f, 0.01f, 0 };
	std::vector<PhaseSubstance> substances;

	// Parses the block from the mod; false: the block has the wrong shape or numbers are out of range.
	bool Read(const void* data, int32_t size)
	{
		if (size < (int32_t)sizeof(PhaseConfigHeader)) return false;
		PhaseConfigHeader read;
		memcpy(&read, data, sizeof(read));
		if (read.substances < 0 || size != (int32_t)(sizeof(PhaseConfigHeader) + (size_t)read.substances * sizeof(PhaseSubstance))) return false;
		if (!(read.atmosphereMass > 0.0f) || !(read.evaporationRate >= 0.0f) || read.evaporationRate > 1.0f
			|| !(read.boilRate > 0.0f) || read.boilRate > 1.0f || !(read.portion > 0.0f)) return false;
		header = read;
		const PhaseSubstance* first = (const PhaseSubstance*)((const uint8_t*)data + sizeof(PhaseConfigHeader));
		substances.assign(first, first + read.substances);
		return true;
	}
};

// "Vapour and its liquid" pairs for the phase rules, built from element properties and settings. By gas number: its
// condensation (boiling point from the gas data). By liquid number: its evaporation (boiling point from the
// liquid data, molar mass and its vapour's numbers). A liquid counts as boiling only if its transition point is close
// to the condensation point of the resulting gas (Water and Steam, Salt Water and Polluted Water and Steam). For the others the transition to gas
// is decomposition, not boiling (latex and oil into Carbon Dioxide at 100-200 degrees, Petroleum into Sour Gas at
// 540): it does not depend on pressure and is left to the simulation, as in the game.
struct PhaseTables
{
	phase::Scale scale;
	std::vector<phase::Vapour> gasVapour, liquidVapour;
	// Short form by element number, for places called for every cell: 0 the phase rules do not concern the element,
	// 1 gas with its own liquid, 2 liquid with its own vapour.
	std::vector<uint8_t> kind;

	void Build(const std::vector<ElementInfo>& elements, const PhaseConfig& config)
	{
		scale.byMass = config.header.byMass != 0;
		scale.atmosphereMass = config.header.atmosphereMass;
		std::vector<phase::Substance> known(elements.size());
		for (const PhaseSubstance& substance : config.substances)
		{
			if (substance.gas < 0 || (size_t)substance.gas >= elements.size()) continue;
			phase::Substance& numbers = known[substance.gas];
			numbers.ratio = substance.ratio;
			numbers.critical = substance.critical;
			numbers.latent = substance.latent;
			numbers.reference = substance.reference;
		}
		gasVapour.assign(elements.size(), phase::Vapour());
		liquidVapour.assign(elements.size(), phase::Vapour());
		kind.assign(elements.size(), 0);
		for (size_t i = 0; i < elements.size(); i++)
		{
			const ElementInfo& info = elements[i];
			if (info.state == SIM_GAS && info.lowTransition < elements.size() && elements[info.lowTransition].state == SIM_LIQUID
				&& info.lowTemp > 1.0f && info.molar > 0.0f)
			{
				gasVapour[i].Set(info.lowTemp, info.molar, scale, known[i]);
				kind[i] = 1;
			}
			if (info.state == SIM_LIQUID && info.highTransition < elements.size() && elements[info.highTransition].state == SIM_GAS
				&& info.highTemp > 1.0f && elements[info.highTransition].molar > 0.0f
				&& fabsf(info.highTemp - elements[info.highTransition].lowTemp) <= BoilingMatch * elements[info.highTransition].lowTemp)
			{
				liquidVapour[i].Set(info.highTemp, elements[info.highTransition].molar, scale, known[info.highTransition]);
				kind[i] = 2;
			}
		}
	}
};

inline int32_t LandingCell(const SimData* sim, const ElementTable* elements, int32_t cell, uint16_t liquid)
{
	const SimCells* cells = sim->updatedCells;
	const size_t count = cells->elementIdx.size() < cells->properties.size() ? cells->elementIdx.size() : cells->properties.size();
	int32_t at = cell;
	while (at - sim->width >= 0 && (size_t)(at - sim->width) < count)
	{
		const int32_t below = at - sim->width;
		const uint16_t element = cells->elementIdx.first[below];
		if (element == liquid) return below;
		// Bit 2 of the cell properties: a building impermeable to liquid.
		const bool open = element == sim->vacuumElementIdx || (element < elements->elements.size() && elements->elements[element].state == SIM_GAS);
		if (!open || (cells->properties.first[below] & 2) != 0) break;
		at = below;
	}
	return at;
}
