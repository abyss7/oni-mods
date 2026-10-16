// Own liquid movement computation: what the game simulation does as the flow of one liquid cell per tick.
// Independent of the hook engine. For pure liquids it is an exact repeat of the game computation (checked against the game
// call by call, the "shadow" in liquid_mix.cpp); on top of it, mixtures of liquids of one group (Mix).
//
// Mixture. A mixture cell stores the shares of its liquids by mass; the mixture mass is the cell mass, the cell element
// is the main liquid (Table::Main). A portion leaving a cell carries its composition; an arriving one mixes with what
// is in the cell. Taking and adding pure mass by foreign code (pumps, buildings) does not change the shares. Liquids of one
// group count as one liquid for flow: mass goes from a Water cell into a Brine cell just as it would
// into a Water cell. How much a cell holds is computed from its composition (volumes add up), so a pool
// with an even composition comes to rest like a pure one.
//
// How a tick works in the game. Cells have two states: a snapshot taken
// before the liquid phase of the tick, and the main one. A cell decides how much to give and where by the snapshot (its own mass and
// element, the masses and elements of its neighbours), but writes into the main state; so the result does not depend on the order
// in which cells are visited almost anywhere, except for cells swapping places. The steps of one cell, strictly in order:
//   1. down: into the same liquid (while the lower cell is lighter than this one with a 1% compression margin), or a fall into gas
//      or vacuum (cells swap or the whole cell leaves as a falling drop), or vanishing above a Void cell;
//   2. left and 3. right: a quarter of the mass difference, no less than the smallest portion; from a ledge edge the portion leaves as a drop;
//   4. up: half of the excess over what the cell can hold under the upper one;
//   5. exchange with gas diagonally down (gas comes out from under an overhang).
//
// The templates below work over a "grid" G: access to the main state of cells and to what the computation does not
// compute itself. In the shadow the grid writes into a copy of the cell's neighbourhood, in the game into the real cells. The grid provides:
//   uint16_t& element(int), float& mass(int), float& temperature(int), uint8_t& diseaseIdx(int),
//   int32_t& diseaseCount(int), uint8_t& infestation(int), float& growthError(int)   main state;
//   uint8_t properties(int)                      cell flags in the main state (bit 1: closed to gas,
//                                                bit 2: closed to liquid);
//   float* flow(int)                             four numbers of the cell: given left, right, down, up;
//   bool displaceGas(int cell, uint16_t gas)     displace gas from the cell (done by the game and the gas mixture layer);
//   bool spawnFalling(int from, int at, uint16_t element, float mass, float temperature, uint8_t diseaseIdx, int32_t count)
//                                                hand a portion of cell from to the game as a falling drop in cell at;
//                                                false: the game refused (a grid with mixtures splits the drop by composition);
//   void changed(int cell)                       tell the game that the cell element changed.
//   void transferring(int nb, float mass, uint8_t diseaseIdx, int32_t count), void transferred(bool result)
//                                                start and outcome of moving a portion into cell nb: for checking in the shadow;
//   Mix* mix(int cell)                           cell composition (nullptr: the grid does not track mixtures, as in the shadow);
//   bool mixed(int cell)                         whether the cell holds a mixture (cheaper than reading the composition: for checks
//                                                on every cell);
//   void noteMixed(int cell)                     a mixture appeared in the cell (for the grid's fast checks);
//   bool mixes(uint16_t a, uint16_t b)           whether two different liquids mix (always false in the shadow).
#pragma once

#include "liquid_rules.h"
#include "sim_types.h"

namespace liquid
{
	const uint8_t NoDisease = 0xFF;
	const uint8_t ClosedForGas = 1, ClosedForLiquid = 2;

	// How many liquids one cell holds: more than the largest mixing group (9 liquids), so a liquid of the group always finds
	// a slot. Only if a group ever grows past this, a liquid arriving in a full cell goes to the largest one.
	constexpr int MixSlots = 10;
	// A liquid with less than a gram in the cell goes to the main one: otherwise traces would spread over the whole pool.
	constexpr float TraceMass = 0.001f;

	// Cell composition. count < 2: the cell is pure, its liquid is the cell element, the other fields are not read.
	struct Mix
	{
		uint16_t element[MixSlots];
		float share[MixSlots];
		uint8_t count;
	};

	// Composition of a portion or a cell: shares by mass, summing to 1.
	struct Portion
	{
		uint16_t element[MixSlots];
		float share[MixSlots];
		int count;
	};

	inline bool Contains(const Mix& mix, uint16_t element)
	{
		for (int i = 0; i < mix.count && i < MixSlots; i++)
		{
			if (mix.element[i] == element) return true;
		}
		return false;
	}

	template <class G> Portion CompositionOf(G& g, int cell)
	{
		Portion portion;
		const Mix* mix = g.mixed(cell) ? g.mix(cell) : nullptr;
		if (mix && mix->count >= 2)
		{
			portion.count = mix->count;
			for (int i = 0; i < mix->count; i++) { portion.element[i] = mix->element[i]; portion.share[i] = mix->share[i]; }
			return portion;
		}
		portion.count = 1;
		portion.element[0] = g.element(cell);
		portion.share[0] = 1.0f;
		return portion;
	}

	// How much a cell with this composition weighs when full: liquid volumes add up.
	inline float CapacityOf(const Table& table, const Portion& portion)
	{
		float volume = 0.0f;
		for (int i = 0; i < portion.count; i++)
		{
			const float full = portion.element[i] < table.move.size() ? table.move[portion.element[i]].maxMass : 0.0f;
			if (full > 0.0f) volume += portion.share[i] / full;
		}
		return volume > 0.0f ? 1.0f / volume : 0.0f;
	}

	// How much a cell weighs when full: for a pure one this is its liquid's number, for a mixture by composition.
	template <class G> float Capacity(G& g, const Table& table, int cell, float own)
	{
		if (!g.mixed(cell)) return own;
		const float capacity = CapacityOf(table, CompositionOf(g, cell));
		return capacity > 0.0f ? capacity : own;
	}

	// Writes the cell composition and sets its main liquid. Traces (less than a gram) go to the largest share.
	template <class G> void SetComposition(G& g, const Table& table, int cell, Portion portion)
	{
		Mix* mix = g.mix(cell);
		if (!mix) return;
		const float mass = g.mass(cell);
		int largest = 0;
		for (int i = 1; i < portion.count; i++) if (portion.share[i] > portion.share[largest]) largest = i;
		for (int i = 0; i < portion.count;)
		{
			if (portion.count > 1 && i != largest && portion.share[i] * mass < TraceMass)
			{
				portion.share[largest] += portion.share[i];
				portion.element[i] = portion.element[portion.count - 1];
				portion.share[i] = portion.share[portion.count - 1];
				if (largest == portion.count - 1) largest = i;
				portion.count--;
				continue;
			}
			i++;
		}
		uint16_t main = portion.element[0];
		if (portion.count >= 2)
		{
			mix->count = (uint8_t)portion.count;
			g.noteMixed(cell);
			float sum = 0.0f;
			for (int i = 0; i < portion.count; i++) sum += portion.share[i];
			for (int i = 0; i < portion.count; i++) { mix->element[i] = portion.element[i]; mix->share[i] = portion.share[i] / sum; }
			main = table.Main(mix->element, mix->share, mix->count, g.element(cell));
		}
		else mix->count = 0;
		if (main != g.element(cell))
		{
			g.element(cell) = main;
			g.changed(cell);
		}
	}

	// A cell that held before kg received added kg of composition portion (the cell mass is already added).
	template <class G> void Blend(G& g, const Table& table, int cell, float before, float added, const Portion& portion)
	{
		if (!g.mix(cell)) return;
		const float total = before + added;
		if (!(total > 0.0f)) return;
		Portion own;
		own.count = 0;
		if (before > 0.0f) own = CompositionOf(g, cell);
		// A pure portion into the same pure liquid: nothing to do.
		if (own.count == 1 && portion.count == 1 && own.element[0] == portion.element[0]) return;
		Portion result;
		result.count = 0;
		auto add = [&](uint16_t element, float share) {
			for (int i = 0; i < result.count; i++)
			{
				if (result.element[i] == element) { result.share[i] += share; return; }
			}
			if (result.count < MixSlots) { result.element[result.count] = element; result.share[result.count++] = share; return; }
			int smallest = 0;
			for (int i = 1; i < result.count; i++) if (result.share[i] < result.share[smallest]) smallest = i;
			if (share <= result.share[smallest])
			{
				int largest = 0;
				for (int i = 1; i < result.count; i++) if (result.share[i] > result.share[largest]) largest = i;
				result.share[largest] += share;
				return;
			}
			int largest = 0;
			for (int i = 1; i < result.count; i++) if (i != smallest && (largest == smallest || result.share[i] > result.share[largest])) largest = i;
			result.share[largest] += result.share[smallest];
			result.element[smallest] = element;
			result.share[smallest] = share;
		};
		for (int i = 0; i < own.count; i++) add(own.element[i], own.share[i] * (before / total));
		for (int i = 0; i < portion.count; i++) add(portion.element[i], portion.share[i] * (added / total));
		SetComposition(g, table, cell, result);
	}

	// Composition exchange between two cells of one group without moving mass: exchange kg of each one's composition
	// goes into the other (mixing in a liquid at rest).
	template <class G> void Exchange(G& g, const Table& table, int a, int b, float exchange)
	{
		const float ma = g.mass(a), mb = g.mass(b);
		if (!(exchange > 0.0f) || !(ma > exchange) || !(mb > exchange)) return;
		const Portion pa = CompositionOf(g, a), pb = CompositionOf(g, b);
		// Cell a keeps ma - exchange of its composition and receives exchange of b's composition; likewise the other way.
		Blend(g, table, a, ma - exchange, exchange, pb);
		Blend(g, table, b, mb - exchange, exchange, pa);
	}

	template <class G> void ClearDisease(G& g, int cell)
	{
		g.diseaseIdx(cell) = NoDisease;
		g.diseaseCount(cell) = 0;
		g.infestation(cell) = 0;
		g.growthError(cell) = 0.0f;
	}

	template <class G> void ModifyDiseaseCount(G& g, int cell, int32_t delta)
	{
		g.diseaseCount(cell) += delta;
		if (g.diseaseCount(cell) < 1) ClearDisease(g, cell);
	}

	// Germs arrive in a cell. Of the same type they add up; different types compete by strength: count times type strength.
	// The branch for two different types repeats the game literally, including its oddities (the weaker arriving ones vanish).
	template <class G> void AddDisease(G& g, const Table& table, int cell, uint8_t idx, int32_t count)
	{
		const uint8_t current = g.diseaseIdx(cell);
		const int32_t have = g.diseaseCount(cell);
		uint8_t newIdx;
		int32_t newCount;
		if (current == idx) { newIdx = current; newCount = count + have; }
		else if (current == NoDisease) { newIdx = idx; newCount = count; }
		else if (idx == NoDisease) { newIdx = current; newCount = have; }
		else
		{
			const float a = (float)have * (current < table.strength.size() ? table.strength[current] : 0.0f);
			const float b = (float)count * (idx < table.strength.size() ? table.strength[idx] : 0.0f);
			if (a > b)
			{
				const int32_t t = (int32_t)((float)count - (a / b) * (float)have);
				newIdx = t < 0 ? current : idx;
				newCount = t < 0 ? -t : t;
			}
			else if (have < 0) { newIdx = idx; newCount = -have; }
			else { newIdx = current; newCount = have; }
		}
		g.diseaseIdx(cell) = newIdx;
		g.diseaseCount(cell) = newCount;
		if (newCount > 0)
		{
			if (newIdx != current) g.infestation(cell) = 0;
		}
		else ClearDisease(g, cell);
	}

	// Mass with temperature and germs arrives in a cell: the mixture temperature by masses, clamped between the two
	// source temperatures (protection against rounding errors).
	template <class G> void AddMass(G& g, const Table& table, int cell, float added, float temperature, uint8_t diseaseIdx, int32_t diseaseCount)
	{
		const float before = g.mass(cell);
		const float after = before + added;
		if (after <= 0.0f)
		{
			g.temperature(cell) = 0.0f;
			ClearDisease(g, cell);
			return;
		}
		const float own = g.temperature(cell);
		const float mixed = (before * own + temperature * added) / after;
		float high, low;
		if (own <= temperature) { high = temperature; low = own; }
		else { high = own; low = temperature; }
		float result = high;
		if (mixed <= high) result = low <= mixed ? mixed : low;
		g.mass(cell) = after;
		g.temperature(cell) = result;
		AddDisease(g, table, cell, diseaseIdx, diseaseCount);
	}

	template <class G> void SwapCells(G& g, int a, int b)
	{
		Mix* ma = g.mix(a);
		Mix* mb = g.mix(b);
		if (ma && mb) { const Mix t = *ma; *ma = *mb; *mb = t; }
		{ const uint16_t t = g.element(a); g.element(a) = g.element(b); g.element(b) = t; }
		{ const float t = g.mass(a); g.mass(a) = g.mass(b); g.mass(b) = t; }
		{ const float t = g.temperature(a); g.temperature(a) = g.temperature(b); g.temperature(b) = t; }
		{ const uint8_t t = g.diseaseIdx(a); g.diseaseIdx(a) = g.diseaseIdx(b); g.diseaseIdx(b) = t; }
		{ const int32_t t = g.diseaseCount(a); g.diseaseCount(a) = g.diseaseCount(b); g.diseaseCount(b) = t; }
		{ const uint8_t t = g.infestation(a); g.infestation(a) = g.infestation(b); g.infestation(b) = t; }
		{ const float t = g.growthError(a); g.growthError(a) = g.growthError(b); g.growthError(b) = t; }
	}

	template <class G> void ClearCell(G& g, const SimData* sim, int cell)
	{
		if (Mix* mix = g.mix(cell)) mix->count = 0;
		g.element(cell) = sim->vacuumElementIdx;
		g.mass(cell) = 0.0f;
		g.temperature(cell) = 0.0f;
		ClearDisease(g, cell);
	}

	// "Open" for falling: gas or vacuum without the "closed to liquid" flag.
	template <class G> bool Permeable(G& g, const Table& table, int cell)
	{
		return (g.properties(cell) & ClosedForLiquid) == 0 && table.StateOf(g.element(cell)) <= Gas;
	}

	// Moves a portion from cell cell into the neighbouring nb. Does not touch the source: the caller subtracts the mass and germs
	// from it. false: the receiver holds another liquid, or gas that has nowhere to be displaced.
	template <class G> bool TransferBody(G& g, const Table& table, const SimData* sim, int cell, uint16_t element, int nb, float mass, uint8_t diseaseIdx, int32_t diseaseCount)
	{
		const float temperature = sim->cells->temperature.first[cell];
		const uint16_t there = g.element(nb);
		const bool blend = g.mix(nb) != nullptr;
		if (element == there || g.mixes(element, there))
		{
			Portion portion;
			float before = 0.0f;
			if (blend) { portion = CompositionOf(g, cell); before = g.mass(nb); }
			AddMass(g, table, nb, mass, temperature, diseaseIdx, diseaseCount);
			if (blend) Blend(g, table, nb, before, mass, portion);
			return true;
		}
		if (there == sim->voidElementIdx)
		{
			g.mass(nb) = 0.0f;
			return true;
		}
		const uint8_t state = table.StateOf(there);
		if (state == Fluid) return false;
		if (state == Gas && !g.displaceGas(nb, there)) return false;
		g.element(nb) = element;
		if (blend) g.mix(nb)->count = 0;
		AddMass(g, table, nb, mass, temperature, diseaseIdx, diseaseCount);
		if (blend) Blend(g, table, nb, 0.0f, mass, CompositionOf(g, cell));
		g.changed(nb);
		return true;
	}

	template <class G> bool Transfer(G& g, const Table& table, const SimData* sim, int cell, uint16_t element, int nb, float mass, uint8_t diseaseIdx, int32_t diseaseCount)
	{
		g.transferring(nb, mass, diseaseIdx, diseaseCount);
		const bool result = TransferBody(g, table, sim, cell, element, nb, mass, diseaseIdx, diseaseCount);
		g.transferred(result);
		return result;
	}

	// Sideways step (left: side = -1, right: side = +1). m and dc: how much mass and germs the cell still has left.
	template <class G> void MoveSideways(G& g, const Table& table, const SimData* sim, int cell, int side, uint16_t element, const Liquid& own, float& m, int32_t& dc)
	{
		const SimCells& snapshot = *sim->cells;
		const int nb = cell + side;
		const uint16_t there = snapshot.elementIdx.first[nb];
		if (!((element == there || g.mixes(element, there) || table.StateOf(there) < Fluid) && table.StateOf(g.element(nb)) != Solid
			&& (snapshot.properties.first[nb] & ClosedForLiquid) == 0)) return;
		const float beside = table.StateOf(there) == Fluid ? snapshot.mass.first[nb] : 0.0f;
		const float quarter = (m - beside) * 0.25f;
		float move = m <= own.viscosity ? m : own.viscosity;
		if (quarter <= move) move = quarter;
		if (!(own.minHorizontalFlow <= move && 0.0f < move)) return;
		const int width = sim->width;
		const int32_t germs = (int32_t)((move / m) * (float)dc);
		const uint8_t diseaseIdx = snapshot.diseaseIdx.first[cell];
		bool moved = false;
		// The cell rests on support, and below the neighbouring cell to the side it is empty: the portion falls off the ledge as a drop.
		const int under = cell - width;
		const bool onLedge = (table.StateOf(snapshot.elementIdx.first[under]) == Solid || (snapshot.properties.first[under] & ClosedForLiquid) != 0)
			&& Permeable(g, table, nb - width);
		if (onLedge) moved = g.spawnFalling(cell, nb, snapshot.elementIdx.first[cell], move, snapshot.temperature.first[cell], diseaseIdx, germs);
		if (!moved) moved = Transfer(g, table, sim, cell, element, nb, move, diseaseIdx, germs);
		if (!moved) return;
		dc -= germs;
		if (g.diseaseIdx(cell) == diseaseIdx) ModifyDiseaseCount(g, cell, -germs);
		m -= move;
		g.mass(cell) = g.mass(cell) - move;
		float* flow = g.flow(cell);
		flow[side < 0 ? 0 : 1] = move + flow[side < 0 ? 0 : 1];
	}

	// Flow of one cell per tick. Call for a cell that is liquid in the snapshot and has mass above zero.
	template <class G> void UpdateCell(G& g, const Table& table, const SimData* sim, int cell)
	{
		const SimCells& snapshot = *sim->cells;
		const uint16_t element = snapshot.elementIdx.first[cell];
		if (element >= table.move.size()) return;
		const Liquid& own = table.move[element];
		float m = snapshot.mass.first[cell];
		int32_t dc = snapshot.diseaseCount.first[cell];
		const uint8_t diseaseIdx = snapshot.diseaseIdx.first[cell];
		const int below = cell - sim->width;
		const uint16_t belowElement = snapshot.elementIdx.first[below];

		// 1. Down.
		if (table.StateOf(g.element(below)) != Solid && (snapshot.properties.first[below] & ClosedForLiquid) == 0)
		{
			const uint8_t state = table.StateOf(belowElement);
			if (element == belowElement || g.mixes(element, belowElement))
			{
				const float under = state == Fluid ? snapshot.mass.first[below] : 0.0f;
				// Whether the lower cell is full is decided by its own composition.
				const float full = Capacity(g, table, below, element == belowElement ? own.maxMass : table.move[belowElement].maxMass);
				float capacity = m * 1.01f;
				if (capacity <= full) capacity = full;
				float room = capacity - under;
				if (room <= 0.0f) room = 0.0f;
				float move = m <= own.viscosity ? m : own.viscosity;
				if (room * 0.5f <= move) move = room * 0.5f;
				if ((m <= own.minVerticalFlow || own.minVerticalFlow <= move) && 0.0f < move)
				{
					const int32_t germs = (int32_t)((move / m) * (float)dc);
					if (Transfer(g, table, sim, cell, element, below, move, diseaseIdx, germs))
					{
						dc -= germs;
						if (g.diseaseIdx(cell) == diseaseIdx) ModifyDiseaseCount(g, cell, -germs);
						m -= move;
						g.mass(cell) = g.mass(cell) - move;
						g.flow(cell)[2] = move + g.flow(cell)[2];
					}
				}
			}
			else if (state < Fluid)
			{
				if (belowElement == sim->voidElementIdx)
				{
					if (Mix* mix = g.mix(cell)) mix->count = 0;
					g.element(cell) = sim->vacuumElementIdx;
					g.mass(cell) = 0.0f;
					g.temperature(cell) = 0.0f;
					ClearDisease(g, cell);
					g.changed(cell);
					return;
				}
				// Open to the side, but closed diagonally below that side: the whole cell leaves as a falling drop.
				if ((Permeable(g, table, cell - 1) && !Permeable(g, table, below - 1)) || (Permeable(g, table, cell + 1) && !Permeable(g, table, below + 1)))
				{
					if (!g.spawnFalling(cell, cell, element, m, snapshot.temperature.first[cell], diseaseIdx, dc)) return;
					g.element(cell) = sim->vacuumElementIdx;
					g.temperature(cell) = 0.0f;
					g.mass(cell) = 0.0f;
					ClearDisease(g, cell);
					if (Mix* mix = g.mix(cell)) mix->count = 0;
					g.changed(cell);
					return;
				}
				SwapCells(g, cell, below);
				g.changed(cell);
				g.changed(below);
				return;
			}
		}

		// 2 and 3. Left, then right.
		if (m <= 0.0f) return;
		MoveSideways(g, table, sim, cell, -1, element, own, m, dc);
		if (m <= 0.0f) return;
		MoveSideways(g, table, sim, cell, +1, element, own, m, dc);

		// 4. Up: the cell is compressed more than it can hold under the upper one.
		if (m <= 0.0f) return;
		{
			const int up = cell + sim->width;
			const uint16_t above = snapshot.elementIdx.first[up];
			if ((element == above || g.mixes(element, above) || table.StateOf(above) < Fluid) && table.StateOf(g.element(up)) != Solid
				&& (snapshot.properties.first[up] & ClosedForLiquid) == 0)
			{
				const float over = table.StateOf(above) == Fluid ? snapshot.mass.first[up] : 0.0f;
				const float full = Capacity(g, table, cell, own.maxMass);
				float capacity = over * 1.01f;
				if (capacity <= full) capacity = full;
				float excess = m - capacity;
				if (excess <= 0.0f) excess = 0.0f;
				excess = excess * 0.5f;
				float move = excess;
				if (m < capacity + capacity)
				{
					const float limit = m <= own.viscosity ? m : own.viscosity;
					move = excess <= limit ? excess : limit;
				}
				if (0.01f < move)
				{
					const int32_t germs = (int32_t)((move / m) * (float)dc);
					if (Transfer(g, table, sim, cell, element, up, move, diseaseIdx, germs))
					{
						if (g.diseaseIdx(cell) == diseaseIdx) ModifyDiseaseCount(g, cell, -germs);
						m -= move;
						g.mass(cell) = g.mass(cell) - move;
						g.flow(cell)[3] = move + g.flow(cell)[3];
					}
				}
			}
		}

		// 5. Exchange with gas diagonally down: right side first, then left. Everything by the main state.
		if (m <= 0.0f) return;
		for (int side = +1; side >= -1; side -= 2)
		{
			if (table.StateOf(g.element(below + side)) != Gas) continue;
			if (table.StateOf(g.element(cell + side)) != Solid && (g.properties(cell + side) & ClosedForGas) == 0) continue;
			if (table.StateOf(g.element(below)) == Solid) continue;
			if ((g.properties(cell) & ClosedForGas) != 0) continue;
			if ((g.properties(below + side) & ClosedForLiquid) != 0) continue;
			if ((g.properties(below) & (ClosedForGas | ClosedForLiquid)) != 0) continue;
			SwapCells(g, cell, below + side);
			g.changed(cell);
			g.changed(below + side);
			return;
		}
	}
}

namespace liquid
{
	// Whether two cells have the same composition (within SameShare per share): such cells have nothing to exchange.
	constexpr float SameShare = 1e-4f;
	template <class G> bool SameComposition(G& g, int a, int b)
	{
		const bool mixedA = g.mixed(a), mixedB = g.mixed(b);
		if (!mixedA || !mixedB) return !mixedA && !mixedB && g.element(a) == g.element(b);
		const Mix* ma = g.mix(a);
		const Mix* mb = g.mix(b);
		if (ma->count != mb->count) return false;
		for (int i = 0; i < ma->count; i++)
		{
			bool found = false;
			for (int k = 0; k < mb->count; k++)
			{
				if (mb->element[k] != ma->element[i]) continue;
				found = fabsf(mb->share[k] - ma->share[i]) < SameShare;
				break;
			}
			if (!found) return false;
		}
		return true;
	}

	// Mixing in a liquid at rest: neighbouring cells of one group with different compositions exchange the share
	// rate of the smaller of the two masses; neighbours whose composition is already equal are skipped. Mixture cells are counted along the way.
	//
	// Only blocks of 64 cells (cell index shifted by 6) marked in flags are visited: the composition recently changed there
	// (g.noteMixed) or a boundary between two liquids of one group was found. A block in which not a single exchange
	// happened during a pass is dropped from the walk until it is touched again. So the cost of a pass grows with what happens
	// in the liquids, not with the map size and not with the number of mixed cells: a couple hundred mixtures cost a couple hundred cells,
	// and an ocean world of an already even mixture almost nothing; the worst case is walking all blocks, that is the whole grid.
	// Each pair of neighbours is counted once: from the cell on the left or below, or, if its block is not marked, from the neighbour.
	template <class G> void Diffuse(G& g, const Table& table, const SimData* sim, float rate, uint8_t* flags, size_t blocks, int64_t* exchanges, int64_t* mixed)
	{
		const int width = sim->width;
		const int count = (int)sim->updatedCells->elementIdx.size();
		const uint16_t* groups = table.groupOf.data();
		const size_t kinds = table.groupOf.size();
		auto flagged = [&](int cell) { const size_t block = (size_t)cell >> 6; return block < blocks && flags[block] != 0; };
		for (size_t block = 0; block < blocks; block++)
		{
			if (!flags[block]) continue;
			int first = (int)(block << 6), last = first + 64;
			if (first < width) first = width;
			if (last > count - width) last = count - width;
			int64_t here = 0;
			for (int cell = first; cell < last; cell++)
			{
				const uint16_t element = g.element(cell);
				if (element >= kinds || groups[element] == NoGroup) continue;
				const bool ownMixed = g.mixed(cell);
				if (ownMixed) (*mixed)++;
				// Right and up always; left and down only if that block is not marked (otherwise it counts the pair itself).
				const int beside[4] = { cell + 1, cell + width, cell - 1, cell - width };
				for (int side = 0; side < 4; side++)
				{
					const int nb = beside[side];
					if (side >= 2 && flagged(nb)) continue;
					const uint16_t other = g.element(nb);
					if (other >= kinds || groups[other] != groups[element]) continue;
					if (other == element && !ownMixed && !g.mixed(nb)) continue;
					if (SameComposition(g, cell, nb)) continue;
					const float ma = g.mass(cell), mb = g.mass(nb);
					Exchange(g, table, cell, nb, rate * (ma < mb ? ma : mb));
					(*exchanges)++;
					here++;
				}
			}
			if (here == 0) flags[block] = 0;
		}
	}
}
