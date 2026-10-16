// Liquid mixture. Independent of the game and of the hook engine: this file is built both into the plugin (liquid_mix.cpp)
// and into the game-free check (sim/model/liquid_check.cpp).
//
// Only what mixes in real life mixes. A liquid has a mixing group (water: Water, Polluted
// Water, Salt Water, Brine; hydrocarbons: Crude Oil, Petroleum, Naphtha); liquids of one group lie in a cell
// as a mixture, like gases in the gas mixture mod: the cell stores the mass of each liquid. The mod does not go
// into what the liquids themselves are made of: Water with Brine stays Water with Brine, it does not become Salt Water. The cell element for
// the game and the simulation is the main liquid, the one with the largest mass in the cell; the cell mass in the simulation
// is the mass of the whole mixture (the simulation moves the liquid, and it computes by the full mass).
// Liquids of different groups and liquids without a group do not mix: they lie in different cells as layers and swap
// places, as in the game, only the layer order is set by density, not molar mass.
#pragma once

#include <math.h>
#include <stdint.h>
#include <string.h>

#include <algorithm>
#include <unordered_map>
#include <vector>

namespace liquid
{
	constexpr uint16_t NoGroup = 0xFFFF;

	// Data block from the mod: header, liquids, one state byte per game element (padded to
	// four bytes), and the strength of each germ type.
	struct TableHeader
	{
		int32_t elements;  // total number of elements in the game (element ids are below this number)
		int32_t liquids;
		float keep;        // retention of the cell's main liquid: see Table::Main
		int32_t diseases;  // number of germ types
	};
	// Element state, as the simulation stores it.
	enum State : uint8_t { Vacuum = 0, Gas = 1, Fluid = 2, Solid = 3 };
	// A game liquid. The block must contain all liquids: the plugin also learns from it that an element is liquid.
	struct Liquid
	{
		uint16_t element;
		uint16_t group;    // mixing group; NoGroup: mixes with nothing
		float density;     // kg/m3: layers are ordered by it
		float molar;       // molar mass: the game orders layers by it
		// Movement numbers from the game data: how many kilograms a cell gives in one direction per tick, the smallest
		// portions sideways and down, the mass of a full cell.
		float viscosity, minHorizontalFlow, minVerticalFlow, maxMass;
		// Transitions from the game data: below lowTemp the liquid becomes lowTransition, and the share lowOreShare of its mass
		// drops out as a separate element lowOre; above highTemp likewise. Id 0xFFFF: no transition (or ore).
		float lowTemp, highTemp;
		uint16_t lowTransition, lowOre, highTransition, highOre;
		float lowOreShare, highOreShare;
	};
	static_assert(sizeof(TableHeader) == 16 && sizeof(Liquid) == 52, "the liquid table layout is shared with the mod");

	struct Table
	{
		std::vector<Liquid> liquids;
		std::vector<int32_t> byElement; // index of the entry in liquids by element id; -1: not a liquid
		std::vector<uint8_t> states;    // state by element id (State)
		std::vector<Liquid> move;       // liquid entry by element id (zeros for the rest): for the movement computation
		std::vector<uint16_t> groupOf;  // mixing group by element id (NoGroup: not a liquid or mixes with nothing)
		std::vector<float> strength;    // strength of a germ type by its id
		float keep = 0.2f;

		// Element state; an id outside the table counts as solid (the liquid does nothing with such a cell).
		uint8_t StateOf(uint16_t element) const
		{
			return element < states.size() ? states[element] : (uint8_t)Solid;
		}

		bool Read(const void* data, int32_t size)
		{
			if (size < (int32_t)sizeof(TableHeader)) return false;
			TableHeader header;
			memcpy(&header, data, sizeof(header));
			if (header.elements <= 0 || header.elements > 0xFFFF || header.liquids < 0 || !(header.keep >= 0.0f && header.keep < 1.0f)
				|| header.diseases < 0 || header.diseases > 255) return false;
			const int64_t stateBytes = ((int64_t)header.elements + 3) / 4 * 4;
			if ((int64_t)sizeof(TableHeader) + (int64_t)header.liquids * (int64_t)sizeof(Liquid) + stateBytes + (int64_t)header.diseases * 4 != size) return false;
			const uint8_t* at = (const uint8_t*)data + sizeof(TableHeader);
			liquids.resize(header.liquids);
			if (header.liquids) memcpy(liquids.data(), at, header.liquids * sizeof(Liquid));
			at += header.liquids * sizeof(Liquid);
			states.assign(at, at + header.elements);
			at += stateBytes;
			strength.resize(header.diseases);
			if (header.diseases) memcpy(strength.data(), at, header.diseases * sizeof(float));
			keep = header.keep;
			byElement.assign(header.elements, -1);
			move.assign(header.elements, Liquid{});
			groupOf.assign(header.elements, NoGroup);
			for (size_t i = 0; i < liquids.size(); i++)
			{
				const Liquid& liquid = liquids[i];
				if (liquid.element >= header.elements || byElement[liquid.element] >= 0 || !(liquid.density > 0.0f)
					|| states[liquid.element] != Fluid) return false;
				byElement[liquid.element] = (int32_t)i;
				move[liquid.element] = liquid;
				groupOf[liquid.element] = liquid.group;
			}
			return true;
		}

		const Liquid* Of(uint16_t element) const
		{
			return element < byElement.size() && byElement[element] >= 0 ? &liquids[byElement[element]] : nullptr;
		}

		// Whether two liquids can lie in one cell as a mixture.
		bool Mixes(uint16_t first, uint16_t second) const
		{
			// Called from hot paths (flow, per-cell hooks): one table lookup by element id.
			if (first >= groupOf.size() || second >= groupOf.size()) return false;
			const uint16_t group = groupOf[first];
			return group != NoGroup && group == groupOf[second];
		}

		// Mixture density: liquid volumes add up. elements and masses: the cell's liquids and their masses.
		float Density(const uint16_t* elements, const float* masses, int count) const
		{
			float volume = 0.0f, mass = 0.0f;
			for (int i = 0; i < count; i++)
			{
				const Liquid* liquid = Of(elements[i]);
				if (!liquid || !(masses[i] > 0.0f)) continue;
				volume += masses[i] / liquid->density;
				mass += masses[i];
			}
			return volume > 0.0f ? mass / volume : 0.0f;
		}

		// Main liquid of the cell: the one with the largest mass. The current main one (current) is retained
		// while it is not smaller than the largest one by more than the share keep: without this a cell holding equal amounts of two liquids
		// would switch element back and forth with every portion. With keep = 0.2 and two liquids the main one
		// changes when the other exceeds 55.6%.
		uint16_t Main(const uint16_t* elements, const float* masses, int count, uint16_t current) const
		{
			int best = -1, now = -1;
			for (int i = 0; i < count; i++)
			{
				if (!(masses[i] > 0.0f)) continue;
				if (best < 0 || masses[i] > masses[best]) best = i;
				if (elements[i] == current) now = i;
			}
			if (best < 0) return current;
			if (now >= 0 && masses[now] >= masses[best] * (1.0f - keep)) return current;
			return elements[best];
		}
	};

	// Inventory of the grid's liquids: how many cells and where liquids touch. Needed before any mixing: it
	// shows how many cells the mod's rules will affect in a particular colony.
	enum PairKind : int32_t
	{
		Front = 0,     // neighbouring cells with different liquids of one group: mixing will happen here
		Foreign = 1,   // neighbouring cells with liquids that do not mix
		Inversion = 2, // cell above cell, the upper liquid denser than the lower: ordering by density will swap them
	};
	// For Front and Foreign the element with the smaller id comes first; for Inversion the first one lies lower.
	struct Pair
	{
		uint16_t first, second;
		int32_t kind;
		int32_t count;
	};
	static_assert(sizeof(Pair) == 12, "the survey record layout is shared with the mod");

	struct Survey
	{
		int64_t cells = 0, groupCells = 0, fronts = 0, foreign = 0, inversions = 0;
		double mass = 0.0;
		std::vector<Pair> pairs; // by descending count

		// elements and mass: simulation cell arrays; width: width of the simulation grid.
		void Run(const Table& table, const uint16_t* elements, const float* masses, size_t count, size_t width)
		{
			*this = Survey();
			std::unordered_map<uint64_t, int32_t> found;
			for (size_t cell = 0; cell < count; cell++)
			{
				const Liquid* liquid = table.Of(elements[cell]);
				if (!liquid) continue;
				cells++;
				mass += masses[cell];
				if (liquid->group != NoGroup) groupCells++;
				const size_t beside[2] = { cell % width + 1 < width ? cell + 1 : count, cell + width };
				for (int side = 0; side < 2; side++)
				{
					const Liquid* other = beside[side] < count ? table.Of(elements[beside[side]]) : nullptr;
					if (!other || other == liquid) continue;
					const bool mixes = liquid->group != NoGroup && liquid->group == other->group;
					(mixes ? fronts : foreign)++;
					const uint64_t low = liquid->element < other->element ? liquid->element : other->element;
					const uint64_t high = liquid->element < other->element ? other->element : liquid->element;
					found[((uint64_t)(mixes ? Front : Foreign) << 32) | (low << 16) | high]++;
					if (side == 1 && other->density > liquid->density * 1.001f)
					{
						inversions++;
						found[((uint64_t)Inversion << 32) | ((uint64_t)liquid->element << 16) | other->element]++;
					}
				}
			}
			for (const auto& entry : found)
			{
				pairs.push_back({ (uint16_t)(entry.first >> 16), (uint16_t)entry.first, (int32_t)(entry.first >> 32), entry.second });
			}
			std::sort(pairs.begin(), pairs.end(), [](const Pair& a, const Pair& b)
			{
				return a.count != b.count ? a.count > b.count : (a.kind != b.kind ? a.kind < b.kind : (a.first != b.first ? a.first < b.first : a.second < b.second));
			});
		}
	};
}
