// Layout of the simulation structs the plugins access. Only the fields that are needed, and only their beginning.
// Layout of the simulation's data for the build the plugins are written for. The hook library
// refuses to work with a different SimDLL build, so these offsets are applied only to the build they were taken from.
#pragma once

#include <stddef.h>
#include <stdint.h>

// std::vector in the simulation build takes 32 bytes: a bookkeeping pointer, then begin, end of data and end of storage.
template <class T>
struct SimVector
{
	void* proxy;
	T* first;
	T* last;
	T* end;

	size_t size() const { return (size_t)(last - first); }
};

// Cells are stored by field: a separate array for each property.
struct SimCells
{
	SimVector<uint16_t> elementIdx;
	SimVector<float> temperature;
	SimVector<float> mass;
	SimVector<uint8_t> properties; // bit 1: the cell is gas-impermeable
	SimVector<uint8_t> insulation;
	SimVector<uint8_t> strengthInfo;
	SimVector<uint8_t> diseaseIdx; // germ type of the cell, 0xFF: no germs (a cell always holds a single type)
	SimVector<int32_t> diseaseCount;
	SimVector<uint8_t> diseaseInfestationTickCount; // how many ticks the cell has been infected: its own germ diffusion does not start right away
	SimVector<float> diseaseGrowthAccumulatedError;
};

// A germ type in the simulation's disease table (DiseaseInfo): only the per-substance rules are needed. Each rule has
// an array indexed by element number.
struct SimDiseaseInfo
{
	uint32_t hashId;
	float strength;
	float ranges[16]; // limits and half-lives by temperature and pressure
	SimVector<float> underPopulationDeathRate;
	SimVector<float> populationHalfLife;
	SimVector<float> overPopulationHalfLife;
	SimVector<float> diffusionScale;
	SimVector<float> minCountPerKG;
	SimVector<float> maxCountPerKG;
	SimVector<int32_t> minDiffusionCount;
	SimVector<uint8_t> minDiffusionInfestationTickCount;
	float radiationKillRate;
	uint32_t padding;
};
static_assert(sizeof(SimDiseaseInfo) == 0x150 && offsetof(SimDiseaseInfo, diffusionScale) == 0xa8 && offsetof(SimDiseaseInfo, minDiffusionCount) == 0x108, "disease info layout");

// The simulation's disease table (Disease).
struct SimDiseaseTable
{
	SimVector<SimDiseaseInfo> diseases;
};

// A cell for simulation functions that take a "cell accessor" (CellAccessor).
struct SimCellAccessor
{
	SimCells* cells;
	int32_t cellIdx;
};

// Cell back walls. In the simulation build this struct is packed tighter than usual: the array starts
// at the fourth byte (the data start lies at offset 0x0c).
#pragma pack(push, 4)
struct SimBackwall
{
	uint16_t vacuumElementIdx; // this number in the array means "no wall"
	int16_t padding;
	SimVector<uint16_t> elementIdx;
};
#pragma pack(pop)

// The grid area the simulation computes this tick (in the game with the expansion these are the revealed worlds: each world
// occupies its own rectangle of the shared grid, and the simulation does not touch unrevealed worlds at all). Simulation
// cell coordinates; the right and top edges are not part of the area.
struct SimActiveRegion
{
	int32_t minX, minY;
	int32_t maxX, maxY;
	float sunlight;
	float cosmicRadiation;
};

struct SimData
{
	int32_t width;
	int32_t height;
	int32_t numGameCells;
	uint32_t randomSeed;
	int32_t iterateDirection;
	int32_t displacementDirection;
	uint8_t savedOptions;
	uint16_t voidElementIdx;
	uint16_t vacuumElementIdx;
	uint16_t unobtaniumElementIdx;
	SimCells* cells;        // working copy: a tick takes it from updatedCells several times
	SimCells* updatedCells; // main state: both the tick and messages from the game write here
	SimBackwall* backwall;
	uint8_t* worldZones;    // one byte per cell: 0xFF is the space zone (gas and liquid decay there on their own)
	SimVector<float> cosmicRadiationOcclusion;
	void* accumulatedFlow;
	void* flow;
	uint8_t* timers;        // one byte per cell: a change of substance "wakes" the cell
	SimVector<SimActiveRegion> activeRegions;
	void* visibleGrid;
	uint16_t tickCount;
	void* simEvents;        // event queue for the game (including substance changes in a cell)
};

// The simulation grid is one cell wider than the game grid on each side: simulation cell (x, y) is game cell (x - 1, y - 1).

static_assert(sizeof(SimVector<float>) == 0x20, "vector layout");
static_assert(offsetof(SimCells, temperature) == 0x20 && offsetof(SimCells, mass) == 0x40, "cell arrays layout");
static_assert(offsetof(SimCells, properties) == 0x60, "cell properties layout");
static_assert(offsetof(SimCells, diseaseIdx) == 0xc0 && offsetof(SimCells, diseaseCount) == 0xe0, "cell disease layout");
static_assert(offsetof(SimCells, diseaseInfestationTickCount) == 0x100 && offsetof(SimCells, diseaseGrowthAccumulatedError) == 0x120, "cell disease tail layout");
static_assert(offsetof(SimData, voidElementIdx) == 0x1a && offsetof(SimData, cells) == 0x20 && offsetof(SimData, updatedCells) == 0x28, "sim data layout");
static_assert(offsetof(SimData, timers) == 0x70 && offsetof(SimData, simEvents) == 0xa8, "sim data tail layout");
static_assert(offsetof(SimData, worldZones) == 0x38 && offsetof(SimData, activeRegions) == 0x78 && sizeof(SimActiveRegion) == 0x18, "active regions layout");
static_assert(offsetof(SimData, backwall) == 0x30 && offsetof(SimBackwall, elementIdx) + offsetof(SimVector<uint16_t>, first) == 0x0c, "backwall layout");

// Start of the ElementPostProcessData record (PostProcessCell uses it to decide what to do with a cell): only the state is needed.
struct SimElementPostProcess
{
	uint16_t sublimateIndex;
	uint16_t convertIndex;
	uint8_t state; // lowest two bits: SimState
};

// How much substance a consumer took (ElementConsumer or the game's "take mass" message): accumulates
// while the walk over the cells around it removes mass, and goes to the game.
struct SimConsumedMass
{
	int32_t simHandle;
	uint16_t removedElemIdx;
	uint8_t diseaseIdx;
	uint8_t pad;
	float mass;
	float temperature;
	int32_t diseaseCount;
};
static_assert(sizeof(SimConsumedMass) == 0x14, "consumed mass layout");

// A substance source (ElementEmitter: geysers and buildings that emit gas or liquid on their own, without game
// messages). Once every emitInterval seconds it emits emitMass into one of the cells around it if the mass there is below maxPressure.
struct SimEmitterData
{
	float elapsedTime;
	float emitInterval;
	float emitMass;
	float emitTemperature;
	float maxPressure;
	int32_t emitDiseaseCount;
	uint16_t cellX, cellY;
	uint16_t elemIdx;
	uint8_t maxDepth;
	uint8_t offsetIdx;
	uint8_t blockedState;
	uint8_t diseaseIdx;
	uint32_t blockedCallback;
	uint32_t unblockedCallback;
};
static_assert(sizeof(SimEmitterData) == 0x2c && offsetof(SimEmitterData, elemIdx) == 0x1c, "emitter layout");

// What the source emitted per call (goes to the game).
struct SimEmittedMass
{
	uint16_t elemIdx;
	uint8_t diseaseIdx;
	uint8_t pad;
	float mass;
	float temperature;
	int32_t diseaseCount;
};
static_assert(sizeof(SimEmittedMass) == 0x10, "emitted mass layout");

// The data frame the simulation prepares for the game (GameData): the frame's events and a copy of the cells without the border.
// The game reads the cell arrays straight from it (Grid.Mass and the rest). The cell copy is preceded by 29 event lists.
struct SimGameData
{
	int32_t width;  // game grid dimensions
	int32_t height;
	int32_t numFramesProcessed;
	int32_t padding;
	uint8_t events[29 * 0x20];
	SimCells* cells;
};
static_assert(offsetof(SimGameData, cells) == 0x3b0, "game data layout");

// Element state, as encoded by the simulation and the game (lowest two bits).
enum SimState : uint8_t
{
	SIM_VACUUM = 0,
	SIM_GAS = 1,
	SIM_LIQUID = 2,
	SIM_SOLID = 3,
};
