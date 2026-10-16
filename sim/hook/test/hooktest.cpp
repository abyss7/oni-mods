// Check of the hook engine without the game. Runs under Wine:
//   hooktest.exe <path to SimDLL.dll> [symbol table]
// Loads the real SimDLL (it computes nothing meanwhile), installs and removes hooks.
// Simulation functions are not called: outside the game they have no data.
#include <windows.h>

#include <stdint.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "../simhook.h"
#include "../../plugins/liquid_rules.h"
#include "../../plugins/phase_rules.h"

typedef int32_t (*InitFn)(const char*, const char*);
typedef const char* (*TextFn)();
typedef int32_t (*CountFn)();
typedef const char* (*NameFn)(int32_t);
typedef int32_t (*ProbeAddFn)(const char*);
typedef int32_t (*ProbeAddAddressFn)(void*, const char*);
typedef int64_t (*ProbeCallsFn)(int32_t);
typedef int32_t (*PluginEnableFn)(const char*, int32_t);
typedef int32_t (*ValueSetFn)(const char*, int64_t);
typedef int64_t (*ValueGetFn)(const char*);
typedef int32_t (*DataSendFn)(const char*, const void*, int32_t);

// Fake simulation cells in the same layout the layer reads (sim/plugins/sim_types.h).
template <class T> struct FakeVector { void* proxy; T* first; T* last; T* end; };
struct FakeCells
{
	FakeVector<uint16_t> elementIdx; FakeVector<float> temperature; FakeVector<float> mass; FakeVector<uint8_t> properties;
	FakeVector<uint8_t> insulation; FakeVector<uint8_t> strengthInfo; FakeVector<uint8_t> diseaseIdx; FakeVector<int32_t> diseaseCount;
	FakeVector<uint8_t> infestation; FakeVector<float> growthError;
};
struct FakeSim
{
	int32_t width, height, numGameCells; uint32_t seed; int32_t iterate, displace;
	uint8_t options; uint16_t voidElement, vacuumElement, unobtanium;
	FakeCells* cells; FakeCells* updatedCells;
	void* tail[16]; // remaining fields of the real struct; of these the layer needs the world zones (tail[1]) and active regions (tail[9..12])
};
struct FakeRegion { int32_t minX, minY, maxX, maxY; float sunlight, cosmic; };
#pragma pack(push, 4)
struct FakeBackwall { uint16_t none; int16_t pad; FakeVector<uint16_t> elementIdx; };
#pragma pack(pop)
struct FakeElement { float molar; float heat; uint8_t state; uint8_t flags; uint16_t lowTransition; float lowTemp; uint16_t lowOre; uint16_t pad; float lowOreShare; float conductivity;
	uint16_t highTransition; uint16_t highOre; float highTemp; float highOreShare; };

static __attribute__((noinline)) int Twice(int value)
{
	// The body is long enough for a jump to fit at the function start.
	volatile int a = value, b = value;
	return a + b;
}

// Function for checking the hook chain: the selftest plugin hooks it under the name "hooktest.chain".
static volatile int chainRuns;
static __attribute__((noinline)) int Chain(int value)
{
	chainRuns = chainRuns + 1;
	volatile int kept = value;
	return kept;
}

int main(int argc, char** argv)
{
	// No accumulation: when a check fails, it is visible where it stopped.
	setvbuf(stdout, nullptr, _IONBF, 0);
	if (argc < 2)
	{
		printf("usage: hooktest.exe <SimDLL.dll> [symbols]\n");
		return 2;
	}
	HMODULE sim = LoadLibraryExA(argv[1], nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
	if (!sim)
	{
		printf("cannot load %s, error %lu\n", argv[1], GetLastError());
		return 1;
	}
	char path[MAX_PATH];
	GetModuleFileNameA(nullptr, path, sizeof(path));
	strcpy(strrchr(path, '\\') + 1, "oni_simhook.bin");
	HMODULE hook = LoadLibraryA(path);
	if (!hook)
	{
		printf("cannot load %s, error %lu\n", path, GetLastError());
		return 1;
	}
	InitFn init = (InitFn)GetProcAddress(hook, "simhook_init");
	TextFn lastError = (TextFn)GetProcAddress(hook, "simhook_last_error");
	CountFn symbolCount = (CountFn)GetProcAddress(hook, "simhook_symbol_count");
	NameFn symbolName = (NameFn)GetProcAddress(hook, "simhook_symbol_name");
	ProbeAddFn probeAdd = (ProbeAddFn)GetProcAddress(hook, "simhook_probe_add");
	ProbeAddAddressFn probeAddAddress = (ProbeAddAddressFn)GetProcAddress(hook, "simhook_probe_add_address");
	ProbeCallsFn probeCalls = (ProbeCallsFn)GetProcAddress(hook, "simhook_probe_calls");
	CountFn probeClear = (CountFn)GetProcAddress(hook, "simhook_probe_clear");
	typedef const char* (*ProbeNameFn)(int32_t);
	ProbeNameFn probeName = (ProbeNameFn)GetProcAddress(hook, "simhook_probe_name");
	PluginEnableFn pluginEnable = (PluginEnableFn)GetProcAddress(hook, "simhook_plugin_enable");
	typedef int32_t (*SymbolAddFn)(const char*, void*);
	SymbolAddFn symbolAdd = (SymbolAddFn)GetProcAddress(hook, "simhook_symbol_add_address");

	ValueSetFn valueSet = (ValueSetFn)GetProcAddress(hook, "simhook_value_set");
	ValueGetFn valueGet = (ValueGetFn)GetProcAddress(hook, "simhook_value_get");

	DataSendFn dataSend = (DataSendFn)GetProcAddress(hook, "simhook_data_send");

	// Plugins live in their own libraries and attach to the engine through its function table. The second time the engine
	// is found by file name as already loaded (that is how the second mod in the game finds it), rather than loaded again.
	typedef const void* (*ApiFn)();
	typedef int32_t (*AttachFn)(const void* api);
	ApiFn engineApi = (ApiFn)GetProcAddress(hook, "simhook_api");
	const bool sameEngine = GetModuleHandleA("oni_simhook.bin") == hook;
	bool attached = engineApi != nullptr && sameEngine;
	const char* pluginFiles[4] = { "oni_selftest.bin", "oni_gasmix.bin", "oni_pressure.bin", "oni_liquidmix.bin" };
	for (const char* plugin : pluginFiles)
	{
		strcpy(strrchr(path, '\\') + 1, plugin);
		HMODULE module = LoadLibraryA(path);
		AttachFn attach = module ? (AttachFn)GetProcAddress(module, "simplugin_attach") : nullptr;
		const int32_t result = attach && engineApi ? attach(engineApi()) : -100;
		// Attaching again changes nothing.
		const int32_t again = attach && engineApi ? attach(engineApi()) : -100;
		if (result != 0 || again != 0) printf("plugin library %s: load %s, attach %d, again %d\n", plugin, module ? "ok" : "FAILED", result, again);
		attached = attached && result == 0 && again == 0;
	}
	printf("plugin libraries attached to the engine found by its file name: %s\n", attached ? "ok" : "FAILED");
	// Plugin service: address by name and a direct call; an unknown name gives nullptr.
	{
		const SimHookApi* api = engineApi ? (const SimHookApi*)engineApi() : nullptr;
		typedef int32_t (*AddFn)(int32_t, int32_t);
		AddFn add = api ? (AddFn)api->find_service("selftest.add") : nullptr;
		const bool service = add && add(2, 3) == 5 && api->find_service("no-such.service") == nullptr;
		printf("plugin service found by name and called directly: %s\n", service ? "ok" : "FAILED");
		attached = attached && service;
	}

	bool ok = attached;
	const char* moduleName = strrchr(argv[1], '\\') ? strrchr(argv[1], '\\') + 1 : (strrchr(argv[1], '/') ? strrchr(argv[1], '/') + 1 : argv[1]);
	const int32_t initialized = init(moduleName, argc > 2 ? argv[2] : "");
	printf("init: %d %s, symbols: %d\n", initialized, lastError(), symbolCount());
	if (initialized != 0) return 1;

	// 1. Counter on our own function: calls are counted, the result does not change.
	int (*volatile twice)(int) = Twice;
	const int32_t own = probeAddAddress((void*)Twice, "hooktest Twice");
	int sum = 0;
	for (int i = 0; i < 1000; i++) sum += twice(i);
	const bool counted = own >= 0 && probeCalls(own) == 1000 && sum == 999000;
	printf("probe on own function: index %d, calls %lld, sum %d: %s\n", own, (long long)probeCalls(own), sum, counted ? "ok" : "FAILED");
	ok = ok && counted;

	// 2. Plugin: a hook on an exported SimDLL function is enabled and disabled. Before that a call counter is put on the same
	// function: they sit on the function together, and the counter's name does not change.
	const int32_t underPlugin = probeAdd("kprofiler_ping");
	const int32_t enabled = pluginEnable("selftest", 1);
	const int32_t disabled = pluginEnable("selftest", 0);
	const bool together = underPlugin >= 0 && probeName(underPlugin) && strcmp(probeName(underPlugin), "kprofiler_ping") == 0;
	printf("probe and plugin hook on one function: index %d, name %s: %s\n", underPlugin, underPlugin >= 0 && probeName(underPlugin) ? probeName(underPlugin) : "?", together ? "ok" : "FAILED");
	ok = ok && together;

	// 3. Chain: several plugins and a counter on one function. Each link appends its own digit to the number
	// (a: 1, b: 2, early: 3, direct: 9 when called straight from the check code and 8 when called from another link),
	// so the answer shows the order: it is set by the number order and the plugin name, not by the enable order.
	{
		int (*volatile chain)(int) = Chain;
		const int32_t named = symbolAdd ? symbolAdd("hooktest.chain", (void*)Chain) : -100;
		const int32_t probe = probeAddAddress((void*)Chain, "hooktest Chain");
		const int plain = chain(0);
		const int32_t onB = pluginEnable("selftest-b", 1);
		const int withB = chain(0);
		const int32_t onA = pluginEnable("selftest-a", 1);
		const int withAB = chain(0);
		const int32_t onEarly = pluginEnable("selftest-early", 1);
		const int withEarly = chain(0);
		const int32_t onDirect = pluginEnable("selftest-direct", 1);
		const int withDirect = chain(0);
		// A second hook that needs the caller's address does not get installed on the same function; the chain stays intact.
		const int32_t onDirectToo = pluginEnable("selftest-direct-too", 1);
		char refusal[200];
		strncpy(refusal, lastError(), sizeof(refusal) - 1);
		refusal[sizeof(refusal) - 1] = 0;
		const int afterRefusal = chain(0);
		// A disabled link drops out of the chain, and its "original" leads past the others straight into the function.
		pluginEnable("selftest-a", 0);
		const int withoutA = chain(0);
		int32_t direct = 5;
		const int32_t called = dataSend("selftest.call-original-of-a", &direct, sizeof(direct));
		pluginEnable("selftest-direct", 0);
		pluginEnable("selftest-early", 0);
		const int onlyB = chain(0);
		pluginEnable("selftest-b", 0);
		const int runsBefore = chainRuns;
		const int none = chain(4);
		const bool order = named == 0 && probe >= 0 && plain == 0 && onB == 1 && withB == 2 && onA == 1 && withAB == 12 && onEarly == 1 && withEarly == 312
			&& onDirect == 1 && withDirect == 9312 && onDirectToo == -7 && afterRefusal == 9312 && withoutA == 932 && called == 0 && direct == 5
			&& onlyB == 2 && none == 4 && chainRuns == runsBefore + 1;
		// The counter is first and counts every call to the function, however many links stand behind it; a call to the
		// "original" bypassing the chain it does not see. The links ran 1 + 2 + 3 + 4 + 4 + 3 + 1 times.
		const bool counts = probe >= 0 && probeCalls(probe) == 9 && valueGet("selftest.chain-calls") == 18;
		printf("chain of hooks on one function: alone %d, b %d, a+b %d, early first %d, direct first %d, second direct hook refused (%d: %s), without a %d, only b %d, none %d; "
			"probe calls %lld, link calls %lld: %s\n", plain, withB, withAB, withEarly, withDirect, onDirectToo, refusal, withoutA, onlyB, none,
			probe >= 0 ? (long long)probeCalls(probe) : -1LL, (long long)valueGet("selftest.chain-calls"), order && counts ? "ok" : "FAILED");
		ok = ok && order && counts;
	}
	const int32_t unknown = pluginEnable("no-such-plugin", 1);
	// A plugin that named a different SimDLL build is not enabled (code -3: wrong build).
	const int32_t otherBuild = pluginEnable("selftest-other-build", 1);
	printf("plugin selftest: enable %d, disable %d; unknown plugin %d; plugin for another SimDLL build %d: %s\n", enabled, disabled, unknown, otherBuild,
		(enabled == 1 && disabled == 1 && unknown < 0 && otherBuild == -3) ? "ok" : "FAILED");
	ok = ok && enabled == 1 && disabled == 1 && unknown < 0 && otherBuild == -3;

	// Gas mixture layer on fake cells.
	// Elements: 0 vacuum, 1 Oxygen, 2 Carbon Dioxide, 3 Water, 4 rock, 5 Hydrogen.
	// 6: the Void element (the grid edge in the game: also vacuum by state, but gas vanishes in it).
	// Oxygen has the "breathable" flag in the first byte after the state.
	// Carbon Dioxide (2) below 250 degrees becomes a liquid (3), and a quarter of its mass drops out as a solid (4);
	// the others have no transitions.
	// Thermal conductivity is the same for all: the mixture heat exchange correction then equals the main gas's share of the heat capacity.
	// Liquid 3 when heated above 250 degrees becomes gas 2, a tenth of the mass remains as solid 4:
	// this pair is used by the phase rules (off by default).
	const FakeElement table[8] = {
		{ 1, 1, 0, 0, 0xFFFF, 0, 0xFFFF, 0, 0, 1, 0xFFFF, 0xFFFF, 0, 0 },
		{ 32, 1.005f, 1, 1, 0xFFFF, 0, 0xFFFF, 0, 0, 1, 0xFFFF, 0xFFFF, 0, 0 },
		{ 44, 0.846f, 1, 0, 3, 250.0f, 4, 0, 0.25f, 1, 0xFFFF, 0xFFFF, 0, 0 },
		{ 18, 4.18f, 2, 2, 0xFFFF, 0, 0xFFFF, 0, 0, 1, 2, 4, 250.0f, 0.1f },
		{ 50, 1.0f, 3, 0, 0xFFFF, 0, 0xFFFF, 0, 0, 1, 0xFFFF, 0xFFFF, 0, 0 },
		{ 2, 14.3f, 1, 0, 0xFFFF, 0, 0xFFFF, 0, 0, 1, 0xFFFF, 0xFFFF, 0, 0 },
		{ 1, 1, 0, 0, 0xFFFF, 0, 0xFFFF, 0, 0, 1, 0xFFFF, 0xFFFF, 0, 0 },
		{ 60, 1.0f, 3, 2, 0xFFFF, 0, 0xFFFF, 0, 0, 1, 0xFFFF, 0xFFFF, 0, 0 } };
	{
		// Mirror: three ticks, the layer counters are checked after each.
		uint16_t element[6] = { 1, 1, 2, 3, 4, 0 };
		float mass[6] = { 1.5f, 2.0f, 0.25f, 1000.0f, 500.0f, 0.0f };
		float temperature[6] = { 300, 300, 300, 300, 300, 0 };
		uint8_t properties[6] = { 0, 0, 0, 0, 0, 0 };
		FakeCells cells = { { nullptr, element, element + 6, element + 6 }, { nullptr, temperature, temperature + 6, temperature + 6 }, { nullptr, mass, mass + 6, mass + 6 }, { nullptr, properties, properties + 6, properties + 6 } };
		FakeSim sim = {};
		sim.width = 3; sim.height = 2; sim.cells = &cells; sim.updatedCells = &cells;
		bool layer = sizeof(FakeSim) >= 0xb0 && dataSend("gas-mix.elements", table, sizeof(table)) == 0 && dataSend("gas-mix.no-such-data", table, 12) < 0;
		// Tick 1: three gas cells, two gases, 3.75 kg.
		layer = layer && dataSend("gas-mix.tick-for-test", &sim, sizeof(sim)) == 0
			&& valueGet("gas-mix.gas-cells") == 3 && valueGet("gas-mix.species") == 2 && valueGet("gas-mix.total-grams") == 3750
			&& valueGet("gas-mix.projection-mismatches") == 0;
		// Tick 2: Oxygen flowed (mass changed), Carbon Dioxide was replaced by Oxygen, the vacuum filled up.
		mass[0] = 1.0f; mass[1] = 2.5f; element[2] = 1; mass[2] = 0.5f; element[5] = 2; mass[5] = 0.125f;
		layer = layer && dataSend("gas-mix.tick-for-test", &sim, sizeof(sim)) == 0
			&& valueGet("gas-mix.gas-cells") == 4 && valueGet("gas-mix.total-grams") == 4125
			&& valueGet("gas-mix.element-changes") == 1 && valueGet("gas-mix.projection-mismatches") == 0;
		// Tick 3: Water arrived in a gas cell, another became vacuum.
		element[0] = 3; mass[0] = 800.0f; element[5] = 0; mass[5] = 0.0f;
		layer = layer && dataSend("gas-mix.tick-for-test", &sim, sizeof(sim)) == 0
			&& valueGet("gas-mix.gas-cells") == 2 && valueGet("gas-mix.total-grams") == 3000
			&& valueGet("gas-mix.cells-left-gas") == 2 && valueGet("gas-mix.projection-mismatches") == 0 && valueGet("gas-mix.ticks") == 3;
		printf("gas-mix mirror on fake cells: gas cells %lld, grams %lld, mismatches %lld, ticks %lld: %s\n", (long long)valueGet("gas-mix.gas-cells"),
			(long long)valueGet("gas-mix.total-grams"), (long long)valueGet("gas-mix.projection-mismatches"), (long long)valueGet("gas-mix.ticks"), layer ? "ok" : "FAILED");
		ok = ok && layer;
	}
	{
		// The layer moves gas itself. A 5 x 6 grid with a rock frame, inside a 3 x 4 room: Hydrogen in the bottom left corner,
		// Oxygen in the other cells, vacuum in the top right corner.
		enum { W = 5, H = 6, N = W * H };
		uint16_t element[N]; float mass[N], temperature[N]; uint8_t properties[N] = {};
		for (int i = 0; i < N; i++)
		{
			const int x = i % W, y = i / W;
			const bool room = x >= 1 && x <= 3 && y >= 1 && y <= 4;
			element[i] = room ? 1 : 4; mass[i] = room ? 1.0f : 500.0f; temperature[i] = 300.0f;
		}
		const int hydrogen = 1 * W + 1, empty = 4 * W + 3, middle = 2 * W + 2, left = 2 * W + 1;
		element[hydrogen] = 5; temperature[hydrogen] = 350.0f;
		element[empty] = 0; mass[empty] = 0.0f; temperature[empty] = 0.0f;
		// Cell germs: type (0xFF: none) and count, as in the simulation.
		uint8_t germKind[N]; int32_t germCount[N] = {}; uint8_t infestation[N] = {}; float growthError[N] = {};
		for (int i = 0; i < N; i++) germKind[i] = 0xFF;
		FakeCells cells = { { nullptr, element, element + N, element + N }, { nullptr, temperature, temperature + N, temperature + N }, { nullptr, mass, mass + N, mass + N }, { nullptr, properties, properties + N, properties + N },
			{}, {}, { nullptr, germKind, germKind + N, germKind + N }, { nullptr, germCount, germCount + N, germCount + N },
			{ nullptr, infestation, infestation + N, infestation + N }, { nullptr, growthError, growthError + N, growthError + N } };
		FakeSim sim = {};
		sim.width = W; sim.height = H; sim.vacuumElement = 0; sim.voidElement = 6; sim.cells = &cells; sim.updatedCells = &cells;
		bool moved = valueSet("gas-mix.move", 1) == 0;
		// What is checked here is the layer's plumbing (absorption, projection, queries, saving), not the movement rules: those
		// are checked by kernel_check. Spreading along an obstacle in this 3 x 4 room would act in every cell
		// and quickly make Hydrogen the main gas everywhere, while below we need cells with different main gases; so
		// it is disabled here (the fourth number).
		const float rules[4] = { 0.2f, 0.004f, 0.02f, 0.0f };
		moved = moved && dataSend("gas-mix.params", rules, sizeof(rules)) == 0;
		auto tick = [&] { return dataSend("gas-mix.tick-for-test", &sim, sizeof(sim)) == 0; };
		auto grams = [&] { return valueGet("gas-mix.layer-grams"); };
		auto about = [](long long a, long long b) { return a - b <= 2 && b - a <= 2; };

		// Space zone: one cell of the room (needed below, when the simulation removes its main gas).
		uint8_t zones[N] = {};
		zones[left] = 0xFF;
		sim.tail[1] = zones;
		// While there are no active regions, the simulation does not compute the grid, and the layer does not move gas: an empty cell stays empty.
		moved = moved && tick() && about(grams(), 11000) && element[empty] == 0 && mass[empty] == 0.0f
			&& valueGet("gas-mix.active-cells") == 0 && valueGet("gas-mix.idle-gas-cells") == 11;
		// From here on the whole grid is active.
		FakeRegion regions[1] = { { 0, 0, W, H, 0.0f, 0.0f } };
		sim.tail[10] = regions; sim.tail[11] = regions + 1; sim.tail[12] = regions + 1;

		// Tick 1: 11 kg of gas; Oxygen flows into the empty cell with the neighbours' temperature, the rock is untouched.
		moved = moved && tick() && about(grams(), 11000) && element[empty] == 1 && mass[empty] > 0.0f
			&& temperature[empty] > 299.0f && temperature[empty] < 301.0f && element[0] == 4 && mass[0] == 500.0f
			&& valueGet("gas-mix.active-cells") == N && valueGet("gas-mix.idle-gas-cells") == 0;
		// Another 200 ticks: the mass is the same, Hydrogen has spread through the room, in the cell above it it is the main gas by molecules,
		// although by mass there is little of it there; temperatures lie between the initial 300 and 350.
		for (int i = 0; i < 200 && moved; i++) moved = tick();
		const int above = 4 * W + 1;
		bool sane = true;
		for (int i = 0; i < N; i++) if (element[i] != 4) sane = sane && mass[i] >= 0.0f && temperature[i] > 299.0f && temperature[i] < 350.5f;
		moved = moved && about(grams(), 11000) && sane && valueGet("gas-mix.mixed-cells") >= 10 && element[above] == 5 && mass[above] < 0.5f;
		const long long switched = valueGet("gas-mix.main-switched");
		// Cell composition for the tooltip: the mod asks by game cell number (game grid 3 x 4, without the frame).
		// The cell above the Hydrogen holds two gases, their total mass is positive, the temperature is the room's.
		struct { int32_t cell, wait, count; float temperature; struct { int32_t element; float mass; } gases[8]; } query = {};
		query.cell = (4 - 1) * (W - 2) + (1 - 1);
		query.wait = 1;
		const bool asked = dataSend("gas-mix.cell", &query, sizeof(query)) == 0;
		float asksum = 0.0f; bool hasHydrogen = false, hasOxygen = false;
		for (int i = 0; i < query.count && i < 8; i++)
		{
			asksum += query.gases[i].mass;
			hasHydrogen = hasHydrogen || query.gases[i].element == 5;
			hasOxygen = hasOxygen || query.gases[i].element == 1;
		}
		moved = moved && asked && query.count == 2 && hasHydrogen && hasOxygen && asksum > mass[above] && query.temperature > 299.0f && query.temperature < 350.5f;
		// For the overlay: the list of layer gases and the mass of one gas over a rectangle of game cells. Hydrogen in the cell
		// above itself is the same as in the cell answer; the plugin does not touch cells outside the rectangle.
		int32_t listed[8] = {};
		const bool list = dataSend("gas-mix.present", listed, sizeof(listed)) == 0 && listed[0] == 2;
		struct { int32_t element, wait, minX, minY, maxX, maxY, withTotals, reserved; float cells[(W - 2) * (H - 2)]; float totals[(W - 2) * (H - 2)]; } field = {};
		for (float& value : field.cells) value = -1.0f;
		for (float& value : field.totals) value = -1.0f;
		field.element = 5; field.wait = 1; field.minX = 0; field.minY = 1; field.maxX = 1; field.maxY = 3; field.withTotals = 1;
		const bool fielded = dataSend("gas-mix.field", &field, sizeof(field)) == 0;
		// Cell total: number of moles of Hydrogen (molar mass 2) and Oxygen (32) together.
		float molesAbove = 0.0f;
		for (int i = 0; i < query.count && i < 8; i++) molesAbove += query.gases[i].mass / (query.gases[i].element == 5 ? 2.0f : 32.0f);
		const float totalAbove = field.totals[query.cell];
		moved = moved && totalAbove > molesAbove * 0.999f && totalAbove < molesAbove * 1.001f && field.totals[0] == -1.0f;
		float hydrogenAbove = 0.0f;
		for (int i = 0; i < query.count && i < 8; i++) if (query.gases[i].element == 5) hydrogenAbove = query.gases[i].mass;
		moved = moved && list && fielded && field.cells[query.cell] == hydrogenAbove && hydrogenAbove > 0.0f
			&& field.cells[0] == -1.0f && field.cells[1 * (W - 2) + 2] == -1.0f && field.cells[1 * (W - 2) + 1] >= 0.0f;
		// Data frame for the game. The simulation copies the cells into it without the frame; after that the layer adds to the cell's
		// mass the mass of its non-main gases (the game sees the pressure of the whole mixture) and takes two arrays for this frame:
		// the non-main breathable gas and all non-main gases. Above the Hydrogen the main gas is Hydrogen, so the non-main one
		// there is Oxygen, which is also all of the cell's breathable gas; in a cell where Oxygen is main there is no breathable gas beyond it.
		enum { G = (W - 2) * (H - 2) };
		uint16_t gameElement[G]; float gameMass[G], gameTemperature[G]; uint8_t gameProperties[G] = {};
		for (int g = 0; g < G; g++)
		{
			const int from = (g / (W - 2) + 1) * W + (g % (W - 2) + 1);
			gameElement[g] = element[from]; gameMass[g] = mass[from]; gameTemperature[g] = temperature[from];
		}
		FakeCells gameCells = { { nullptr, gameElement, gameElement + G, gameElement + G }, { nullptr, gameTemperature, gameTemperature + G, gameTemperature + G },
			{ nullptr, gameMass, gameMass + G, gameMass + G }, { nullptr, gameProperties, gameProperties + G, gameProperties + G } };
		struct { int32_t width, height, frames, pad; uint8_t events[29 * 0x20]; FakeCells* cells; } frame = { W - 2, H - 2, 1, 0, {}, &gameCells };
		const bool framed = dataSend("gas-mix.frame-for-test", &frame, sizeof(frame)) == 0;
		struct { uint64_t gameMass; int32_t cells, reserved; float breathable[G]; float others[G]; } frameQuery = {};
		frameQuery.gameMass = (uint64_t)(uintptr_t)gameMass; frameQuery.cells = G;
		const bool published = framed && dataSend("gas-mix.frame", &frameQuery, sizeof(frameQuery)) == 0;
		const float* breathable = frameQuery.breathable;
		float oxygenAbove = 0.0f;
		for (int i = 0; i < query.count && i < 8; i++) if (query.gases[i].element == 1) oxygenAbove = query.gases[i].mass;
		int oxygenMain = -1;
		for (int i = 0; i < N && oxygenMain < 0; i++) if (element[i] == 1) oxygenMain = (i / W - 1) * (W - 2) + (i % W - 1);
		moved = moved && published && oxygenAbove > 0.2f && breathable[query.cell] == oxygenAbove && oxygenMain >= 0 && breathable[oxygenMain] == 0.0f
			&& frameQuery.others[query.cell] == oxygenAbove && gameMass[query.cell] == mass[above] + oxygenAbove
			&& valueGet("gas-mix.pressure-cells") > 0 && valueGet("gas-mix.frame-mismatch") == 0;
		// Composition of mixture cells for the same frame: the cell above the Hydrogen has an Oxygen record (non-main gas)
		// with the same mass; into a buffer the records do not fit in nothing is written, and their count is reported.
		struct { uint64_t gameMass; int32_t cells, reserved; struct { int32_t cell; uint16_t element, pad; float mass; } gases[32]; } composition = {};
		composition.gameMass = frameQuery.gameMass; composition.cells = 32;
		const bool composed = dataSend("gas-mix.frame-gases", &composition, sizeof(composition)) == 0 && composition.cells > 0 && composition.cells <= 32;
		bool oxygenListed = false, sorted = true;
		for (int i = 0; composed && i < composition.cells; i++)
		{
			oxygenListed = oxygenListed || (composition.gases[i].cell == query.cell && composition.gases[i].element == 1 && composition.gases[i].mass == oxygenAbove);
			sorted = sorted && (i == 0 || composition.gases[i - 1].cell <= composition.gases[i].cell);
		}
		const int32_t composedCount = composition.cells;
		composition.cells = 0;
		const bool tooSmall = dataSend("gas-mix.frame-gases", &composition, sizeof(composition)) == 3 && composition.cells == composedCount;
		moved = moved && composed && oxygenListed && sorted && tooSmall;
		// A frame the layer did not prepare is not found by address.
		frameQuery.gameMass += 4;
		moved = moved && dataSend("gas-mix.frame", &frameQuery, sizeof(frameQuery)) == 2;
		// A request to take 100 g is fulfilled on the next pass; a request to a cell without that gas goes into the shortfall.
		const long long beforeBreath = grams();
		const struct { int32_t cell; float mass; int32_t germs; } breaths[2] = { { query.cell, 0.1f, 0 }, { oxygenMain, 0.05f, 0 } };
		const bool breathed = dataSend("gas-mix.consume", breaths, sizeof(breaths)) == 0 && tick();
		moved = moved && breathed && about(grams(), beforeBreath - 100) && about(valueGet("gas-mix.breathed-grams"), 100) && about(valueGet("gas-mix.breath-short-grams"), 50);
		query.count = 0;
		moved = moved && dataSend("gas-mix.cell", &query, sizeof(query)) == 0;

		// Saving: the layer hands out the cells' non-main gases, and after "loading the game" (a layer reset from the simulation's
		// cells and this same state back) the mass and composition are as before. First a request with a buffer for the header
		// reports the required length.
		struct SavedHead { int32_t magic, version, gameWidth, gameHeight, gasCount, bytes; };
		uint8_t saved[2048] = {};
		SavedHead head = {};
		const bool sized = dataSend("gas-mix.save", saved, sizeof(SavedHead)) == 3;
		memcpy(&head, saved, sizeof(head));
		const int32_t savedBytes = head.bytes;
		const bool savedOk = sized && savedBytes > (int32_t)sizeof(SavedHead) && savedBytes <= (int32_t)sizeof(saved)
			&& dataSend("gas-mix.save", saved, sizeof(saved)) == 0;
		memcpy(&head, saved, sizeof(head));
		const long long beforeLoad = grams(), mixedBeforeLoad = valueGet("gas-mix.mixed-cells");
		const bool reloaded = dataSend("gas-mix.restore", saved, savedBytes) == 0 && tick();
		query.count = 0;
		const bool askedAgain = dataSend("gas-mix.cell", &query, sizeof(query)) == 0;
		float hydrogenAfterLoad = 0.0f, oxygenAfterLoad = 0.0f;
		for (int i = 0; i < query.count && i < 8; i++)
		{
			if (query.gases[i].element == 5) hydrogenAfterLoad = query.gases[i].mass;
			if (query.gases[i].element == 1) oxygenAfterLoad = query.gases[i].mass;
		}
		moved = moved && savedOk && head.gameWidth == W - 2 && head.gameHeight == H - 2 && head.gasCount == 2 && head.bytes == savedBytes
			&& reloaded && about(grams(), beforeLoad) && valueGet("gas-mix.mixed-cells") == mixedBeforeLoad
			&& valueGet("gas-mix.resets") == 1 && valueGet("gas-mix.restored-entries") > 0 && valueGet("gas-mix.restore-skipped") == 0
			&& askedAgain && query.count == 2 && hydrogenAfterLoad > 0.0f && oxygenAfterLoad > 0.0f;
		// Empty state (a save without the layer, or the game loaded with the layer off and then turned on): after
		// the reset the layer holds only the cells' main gases. In this room the main gas by molecule count is Hydrogen almost everywhere,
		// but by mass there is more Oxygen, so less than half of the mass remains.
		const bool emptied = dataSend("gas-mix.restore", saved, 0) == 0 && tick();
		moved = moved && emptied && valueGet("gas-mix.restored-entries") == 0 && valueGet("gas-mix.resets") == 2 && grams() < beforeLoad / 2;
		// The previous state is restored for the checks below. Gases that managed to mix during the tick without the state
		// are lost in this reset (they existed only in the layer), so the mass is slightly below the original.
		const bool back = dataSend("gas-mix.restore", saved, savedBytes) == 0 && tick();
		moved = moved && back && valueGet("gas-mix.restored-entries") == 12 && grams() <= beforeLoad + 2 && grams() > beforeLoad - 600;
		// A gas from the saved state became the cell's main gas: after saving, the simulation replaced the cell's Hydrogen (main)
		// with 0.3 kg of Oxygen, some of which the cell already held as non-main. After loading, the cell's Oxygen
		// is the sum of both, and the cell's Hydrogen is gone together with what the simulation removed.
		uint8_t savedLater[2048] = {};
		const bool savedAgain = dataSend("gas-mix.save", savedLater, sizeof(savedLater)) == 0;
		memcpy(&head, savedLater, sizeof(head));
		const long long beforeMerge = grams();
		const float hydrogenReplaced = mass[above];
		element[above] = 1; mass[above] = 0.3f;
		const bool mergedLoad = dataSend("gas-mix.restore", savedLater, head.bytes) == 0 && tick();
		moved = moved && savedAgain && mergedLoad && valueGet("gas-mix.restore-merged") == 1 && valueGet("gas-mix.restore-skipped") == 0
			&& about(grams(), beforeMerge - (long long)(hydrogenReplaced * 1000.0f) + 300);

		// Source and consumer. 200 g of hot Carbon Dioxide is emitted into a mixture cell that has no Carbon
		// Dioxide: it is added to the mixture, the cell warms up, but not above the portion. The same gas as the main one, the layer
		// leaves to the simulation. A Carbon Dioxide consumer takes 50 g from this mixture and gets it with the
		// cell temperature; the cell's main gas it does not take from the layer (the simulation takes that).
		struct { void* sim; int32_t cell, element; float mass, temperature; int32_t result; float takenMass, takenTemperature; } source = {};
		const int simAbove = 4 * W + 1;
		const long long beforeSource = grams();
		const float coolBefore = temperature[simAbove];
		source.sim = &sim; source.cell = simAbove; source.element = 2; source.mass = 0.2f; source.temperature = 400.0f;
		const bool emitted = dataSend("gas-mix.add-gas-for-test", &source, sizeof(source)) == 0 && source.result == 1
			&& temperature[simAbove] > coolBefore && temperature[simAbove] < 400.0f;
		source.element = element[simAbove]; source.mass = 0.1f; source.temperature = 300.0f;
		const bool sameGasLeft = dataSend("gas-mix.add-gas-for-test", &source, sizeof(source)) == 0 && source.result == 0;
		source.element = 2; source.mass = 0.05f;
		const bool consumed = dataSend("gas-mix.remove-for-test", &source, sizeof(source)) == 0 && source.takenMass == 0.05f && source.mass == 0.0f
			&& source.takenTemperature == temperature[simAbove];
		source.element = element[simAbove]; source.mass = 0.05f;
		const bool mainLeft = dataSend("gas-mix.remove-for-test", &source, sizeof(source)) == 0 && source.takenMass == 0.0f && source.mass == 0.05f;
		moved = moved && emitted && sameGasLeft && consumed && mainLeft && tick() && about(grams(), beforeSource + 150)
			&& valueGet("gas-mix.source-mixed") == 1 && about(valueGet("gas-mix.source-mixed-grams"), 200) && about(valueGet("gas-mix.consumer-mixed-grams"), 50);
		query.count = 0;
		bool hasCarbonDioxide = false;
		moved = moved && dataSend("gas-mix.cell", &query, sizeof(query)) == 0;
		for (int i = 0; i < query.count && i < 8; i++) hasCarbonDioxide = hasCarbonDioxide || query.gases[i].element == 2;
		moved = moved && hasCarbonDioxide;

		// Pump: the gas for an "any gas" consumer is picked by mass across all cells it reaches.
		// Over the whole room, 2000 picks yield all three gases, most often Oxygen (the most of it by mass),
		// and at least once Carbon Dioxide, of which there is little and which is main almost nowhere.
		int picks[8] = {};
		bool pickedAll = true;
		for (int round = 0; round < 2000 && pickedAll; round++)
		{
			struct { void* sim; int32_t count; int32_t cells[N]; } pick = {};
			pick.sim = &sim; pick.count = 0;
			for (int i = 0; i < N; i++) if (element[i] == 1 || element[i] == 2 || element[i] == 5) pick.cells[pick.count++] = i;
			pickedAll = dataSend("gas-mix.pick-for-test", &pick, sizeof(pick)) == 0 && pick.cells[0] >= 0 && pick.cells[0] < 8;
			if (pickedAll) picks[pick.cells[0]]++;
		}
		moved = moved && pickedAll && picks[1] > picks[5] && picks[1] > picks[2] && picks[5] > 0 && picks[2] > 0 && picks[1] + picks[2] + picks[5] == 2000
			&& valueGet("gas-mix.pump-picks") == 2000;

		// The simulation's own source (geyser). Carbon Dioxide into a cell where the main gas is different: the portion is added
		// to the mixture, the simulation is asked neither to emit nor to displace. The pressure limit is slightly above the main gas mass
		// but below the mixture mass: no emission (pressure is counted over the whole mixture). The same gas as the cell's main gas:
		// the simulation emits it itself. The layer does not touch a liquid source.
		struct { void* sim; int32_t element; float mass, temperature, maxPressure; int32_t count; int32_t cells[8]; int32_t handled; float emitted; int32_t simCell, displaced; } geyser = {};
		const long long beforeGeyser = grams();
		geyser.sim = &sim; geyser.element = 2; geyser.mass = 0.1f; geyser.temperature = 350.0f; geyser.maxPressure = 1000.0f; geyser.count = 1; geyser.cells[0] = simAbove;
		const bool geyserMixed = dataSend("gas-mix.emit-for-test", &geyser, sizeof(geyser)) == 0 && geyser.handled == 1 && geyser.emitted == 0.1f
			&& geyser.simCell == -1 && geyser.displaced == 0 && valueGet("gas-mix.emitter-mixed") == 1 && about(valueGet("gas-mix.emitter-mixed-grams"), 100);
		geyser.maxPressure = mass[simAbove] + 0.001f;
		const bool geyserHeld = dataSend("gas-mix.emit-for-test", &geyser, sizeof(geyser)) == 0 && geyser.handled == 1 && geyser.emitted == 0.0f
			&& geyser.simCell == -1 && valueGet("gas-mix.emitter-held") == 1 && valueGet("gas-mix.emitter-mixed") == 1;
		geyser.element = element[simAbove]; geyser.maxPressure = 1000.0f;
		const bool geyserBySim = dataSend("gas-mix.emit-for-test", &geyser, sizeof(geyser)) == 0 && geyser.handled == 1 && geyser.emitted == 0.1f
			&& geyser.simCell == simAbove && valueGet("gas-mix.emitter-mixed") == 1;
		geyser.element = 3;
		const bool geyserLiquid = dataSend("gas-mix.emit-for-test", &geyser, sizeof(geyser)) == 0 && geyser.handled == 0;
		moved = moved && geyserMixed && geyserHeld && geyserBySim && geyserLiquid && valueGet("gas-mix.emitter-calls") == 3 && tick() && about(grams(), beforeGeyser + 100);
		// The geyser portion is taken back by a consumer so that further checks run with the previous masses.
		source.element = 2; source.mass = 0.1f;
		moved = moved && dataSend("gas-mix.remove-for-test", &source, sizeof(source)) == 0 && source.takenMass == 0.1f && tick() && about(grams(), beforeGeyser);

		// Heat from the simulation. The simulation (impersonated here by the check) heated a mixture cell by 10 degrees,
		// assuming the cell holds only the main gas. The layer keeps the main gas's share of the mixture heat capacity of that
		// (slightly less: during the same tick the heated cell manages to give part of the heat to its neighbours along with gas).
		query.count = 0;
		moved = moved && dataSend("gas-mix.cell", &query, sizeof(query)) == 0;
		const float heats[8] = { 0, 1.005f, 0.846f, 4.18f, 1.0f, 14.3f, 0, 0 };
		float mainPart = 0.0f, whole = 0.0f;
		for (int i = 0; i < query.count && i < 8; i++)
		{
			const float part = query.gases[i].mass * heats[query.gases[i].element & 7];
			whole += part;
			if (query.gases[i].element == element[simAbove]) mainPart = part;
		}
		const float mainShare = whole > 0.0f ? mainPart / whole : 1.0f, heatBefore = temperature[simAbove];
		temperature[simAbove] += 10.0f;
		const bool heatScaled = tick() && valueGet("gas-mix.heat-scaled") >= 1 && mainShare > 0.0f && mainShare < 0.98f
			&& temperature[simAbove] - heatBefore > 10.0f * mainShare - 1.5f && temperature[simAbove] - heatBefore < 10.0f * mainShare + 0.2f;
		temperature[simAbove] = heatBefore;
		moved = moved && heatScaled && tick();

		// The "source blocked by pressure" flag: while the source is checked, a mixture cell is shown the mass of
		// the whole mixture, then the main gas mass is restored bit for bit. A cell with a single gas does not change.
		struct { void* sim; int32_t raise, count; int32_t cells[8]; } shown = {};
		query.count = 0;
		moved = moved && dataSend("gas-mix.cell", &query, sizeof(query)) == 0;
		float mixtureMass = 0.0f;
		for (int i = 0; i < query.count && i < 8; i++) mixtureMass += query.gases[i].mass;
		const float mainOnly = mass[simAbove];
		shown.sim = &sim; shown.raise = 1; shown.count = 2; shown.cells[0] = simAbove; shown.cells[1] = simAbove;
		const bool raised = dataSend("gas-mix.raise-for-test", &shown, sizeof(shown)) == 0 && mass[simAbove] > mainOnly + 0.01f
			&& mass[simAbove] > mixtureMass * 0.999f && mass[simAbove] < mixtureMass * 1.001f && valueGet("gas-mix.emitter-raised") == 1;
		shown.raise = 0;
		const bool lowered = dataSend("gas-mix.raise-for-test", &shown, sizeof(shown)) == 0 && mass[simAbove] == mainOnly;
		moved = moved && raised && lowered;

		// Germ growth and death by mixture composition. Fake rule: in Oxygen (1) germs do not change,
		// in Carbon Dioxide (2) a tenth dies per tick, in other gases half. In a mixture cell the share that dies is
		// the average weighted by the gases' mass shares; the main gas and its mass are unchanged after the calculation.
		float keepShare = 0.0f;
		for (int i = 0; i < query.count && i < 8; i++)
		{
			const int gas = query.gases[i].element;
			keepShare += (gas == 1 ? 1.0f : gas == 2 ? 0.9f : 0.5f) * query.gases[i].mass / mixtureMass;
		}
		const uint16_t growMain = element[simAbove];
		germKind[simAbove] = 0; germCount[simAbove] = 100000; infestation[simAbove] = 7; growthError[simAbove] = 0.0f;
		struct { void* sim; int32_t x0, x1, y0, y1, cells; } growth = { &sim, 0, W, 0, H, 0 };
		const bool grown = dataSend("gas-mix.germ-growth-for-test", &growth, sizeof(growth)) == 0 && growth.cells == 1
			&& germCount[simAbove] > (int)(100000.0f * keepShare) - 3 && germCount[simAbove] < (int)(100000.0f * keepShare) + 3 && keepShare < 0.99f
			&& germKind[simAbove] == 0 && infestation[simAbove] == 8 && element[simAbove] == growMain && mass[simAbove] == mainOnly
			&& valueGet("gas-mix.germ-growth-cells") == 1;
		germKind[simAbove] = 0xFF; germCount[simAbove] = 0; infestation[simAbove] = 0; growthError[simAbove] = 0.0f;
		moved = moved && grown;

		// Gas the simulation writes over another gas (evaporation, sublimation, emission on a game message).
		// Before the write the cell's mixture is hidden: the simulation is shown an empty cell, it has nothing to displace.
		// The simulation (impersonated here by the check) writes 300 g of hot Carbon Dioxide into the cell in full.
		// On the next pass the layer adds it to the mixture and gives the cell back its previous main gas: the layer mass
		// grew by exactly 300 g, the main gas is the same, the cell warmed up, but not to the portion's temperature.
		struct { void* sim; int32_t cell; } hide = { &sim, simAbove };
		const long long beforeArrival = grams();
		const uint16_t hiddenMain = element[simAbove];
		const float hiddenMass = mass[simAbove], hiddenTemperature = temperature[simAbove];
		const bool hiddenNow = dataSend("gas-mix.hide-for-test", &hide, sizeof(hide)) == 0 && hide.cell == 1 && mass[simAbove] == 0.0f && element[simAbove] == hiddenMain;
		element[simAbove] = 2; mass[simAbove] = 0.3f; temperature[simAbove] = 400.0f;
		const bool arrived = tick() && about(grams(), beforeArrival + 300) && valueGet("gas-mix.arrivals") == 1 && about(valueGet("gas-mix.arrivals-mixed-grams"), 300)
			&& element[simAbove] == hiddenMain && mass[simAbove] > hiddenMass * 0.7f && mass[simAbove] < hiddenMass * 1.3f
			&& temperature[simAbove] > hiddenTemperature + 1.0f && temperature[simAbove] < 399.0f;
		// The portion is taken back by a consumer, the temperature is restored: further checks run with the previous cell.
		source.element = 2; source.mass = 0.3f;
		moved = moved && hiddenNow && arrived && dataSend("gas-mix.remove-for-test", &source, sizeof(source)) == 0 && source.takenMass == 0.3f;
		temperature[simAbove] = hiddenTemperature;
		moved = moved && tick() && about(grams(), beforeArrival);

		// Pressure limit for gas emission. The wall to the left of the mixture cell becomes a solid substance
		// that emits gas (7): the layer pass marks the cell, and before the simulation processes it the cell
		// is shown the mass of the whole mixture. The simulation (impersonated here by the check) adds 50 g of the same gas:
		// after processing the cell holds the main gas mass plus 50 g. Without the addition the mass is restored bit for bit.
		// If the mixture gets hidden during the showing (the simulation writes another gas into the cell), the layer remembers the real
		// main gas mass, not the shown one: the layer mass grows only by the same 50 g.
		const int wall = simAbove - 1;
		const uint16_t wallBefore = element[wall];
		element[wall] = 7;
		bool limited = tick();
		const long long beforeLimit = grams();
		query.count = 0;
		limited = limited && dataSend("gas-mix.cell", &query, sizeof(query)) == 0;
		float limitMixture = 0.0f;
		for (int i = 0; i < query.count && i < 8; i++) limitMixture += query.gases[i].mass;
		const float limitMain = mass[simAbove];
		struct { void* sim; int32_t cell, raise; } limit = { &sim, simAbove, 1 };
		auto limitSend = [&](int cell, int raise) { limit.cell = cell; limit.raise = raise; return dataSend("gas-mix.limit-for-test", &limit, sizeof(limit)) == 0; };
		limited = limited && valueGet("gas-mix.limit-cells") >= 1 && limitSend(simAbove, 1) && limit.cell == 1 && mass[simAbove] > limitMain + 0.01f
			&& mass[simAbove] > limitMixture * 0.999f && mass[simAbove] < limitMixture * 1.001f && valueGet("gas-mix.limit-raised") == 1;
		mass[simAbove] += 0.05f;
		limited = limited && limitSend(simAbove, 0) && mass[simAbove] > limitMain + 0.0499f && mass[simAbove] < limitMain + 0.0501f;
		const float limitAdded = mass[simAbove];
		limited = limited && limitSend(simAbove, 1) && mass[simAbove] > limitAdded + 0.01f && limitSend(simAbove, 0) && mass[simAbove] == limitAdded;
		// An unmarked cell (there is no emitting substance at the right wall of the room) does not change.
		const int unmarked = 4 * W + 2;
		const float unmarkedMass = mass[unmarked];
		limited = limited && limitSend(unmarked, 1) && limit.cell == 0 && mass[unmarked] == unmarkedMass && limitSend(unmarked, 0);
		hide.cell = simAbove;
		limited = limited && limitSend(simAbove, 1) && dataSend("gas-mix.hide-for-test", &hide, sizeof(hide)) == 0 && hide.cell == 1 && mass[simAbove] == 0.0f
			&& limitSend(simAbove, 0) && mass[simAbove] == 0.0f && tick() && about(grams(), beforeLimit + 50) && element[simAbove] == hiddenMain
			&& valueGet("gas-mix.limit-unrestored") == 0 && valueGet("gas-mix.arrivals") == 2;
		// The wall and the cell mass are restored: further checks run as before.
		element[wall] = wallBefore;
		mass[simAbove] -= 0.05f;
		limited = limited && tick() && about(grams(), beforeLimit) && valueGet("gas-mix.limit-cells") == 0;
		if (!limited) printf("gas-mix off-gassing limit: FAILED (main %f, mixture %f, cell now %f, raised %lld, unrestored %lld, layer %lld of %lld)\n",
			limitMain, limitMixture, mass[simAbove], valueGet("gas-mix.limit-raised"), valueGet("gas-mix.limit-unrestored"), grams(), beforeLimit);
		moved = moved && limited;

		// Condensation of a non-main gas. The mixture cell cooled below the Carbon Dioxide transition point (250 - 3):
		// the Carbon Dioxide leaves the layer entirely (it becomes a liquid by the simulation's own means, which are absent
		// here), the cell's main gas stays. The portion leaves warmed by 1.5 degrees, a quarter of its mass
		// is marked as ore, its share in the mixture is between 0 and 1; the cell itself warms by less than 1.5 degrees (part of the
		// mixture left). In a cell warmer than that point nothing happens.
		const long long beforeCold = grams();
		const float warmBefore = temperature[simAbove];
		const uint16_t mainBefore = element[simAbove];
		temperature[simAbove] = 248.0f;
		const bool notYet = tick() && valueGet("gas-mix.condensed") == 0 && about(grams(), beforeCold);
		temperature[simAbove] = 200.0f;
		const bool condensed = tick() && valueGet("gas-mix.condensed") == 1 && valueGet("gas-mix.condensed-grams") > 10
			&& about(grams(), beforeCold - valueGet("gas-mix.condensed-grams")) && element[simAbove] == mainBefore && mainBefore != 2;
		struct { int32_t count, reserved; struct { int32_t cell; uint16_t liquid, ore; float mass, oreMass, temperature, share; } drops[4]; } dropped = {};
		const bool drop = dataSend("gas-mix.condensed-for-test", &dropped, sizeof(dropped)) == 0 && dropped.count == 1 && dropped.drops[0].cell == simAbove
			&& dropped.drops[0].liquid == 3 && dropped.drops[0].ore == 4 && about((long long)(dropped.drops[0].mass * 1000.0f), valueGet("gas-mix.condensed-grams"))
			&& dropped.drops[0].oreMass == dropped.drops[0].mass * 0.25f && dropped.drops[0].share > 0.0f && dropped.drops[0].share < 1.0f
			&& temperature[simAbove] > dropped.drops[0].temperature - 1.5f && temperature[simAbove] < dropped.drops[0].temperature;
		query.count = 0;
		bool carbonDioxideLeft = false;
		moved = moved && dataSend("gas-mix.cell", &query, sizeof(query)) == 0;
		for (int i = 0; i < query.count && i < 8; i++) carbonDioxideLeft = carbonDioxideLeft || query.gases[i].element == 2;
		temperature[simAbove] = warmBefore;
		moved = moved && notYet && condensed && drop && !carbonDioxideLeft;

		// Source: the simulation added 0.5 kg of the main gas to the middle cell.
		// Germs as well: the middle cell holds 100,000 of type 0. Gas from it spreads to the neighbours by pressure and
		// carries germs with it: they appear at the neighbours, the middle cell has fewer of them, and the total
		// over the room stays the same (the per-cell count is an integer: less than one germ per cell can be lost).
		float strengths[2] = { 1.0f, 5.0f };
		moved = moved && dataSend("gas-mix.germs", strengths, sizeof(strengths)) == 0;
		germKind[middle] = 0; germCount[middle] = 100000;
		const long long before = grams();
		mass[middle] += 0.5f;
		moved = moved && tick() && about(grams(), before + 500);
		long long germsAll = 0, germsAround = 0;
		bool oneKind = true;
		for (int i = 0; i < N; i++)
		{
			germsAll += germCount[i];
			if (i != middle) germsAround += germCount[i];
			oneKind = oneKind && (germCount[i] > 0 ? germKind[i] == 0 : germKind[i] == 0xFF);
		}
		moved = moved && oneKind && germsAround > 100 && germCount[middle] < 100000 && germCount[middle] > 50000 && germsAll <= 100000 && germsAll >= 100000 - N
			&& valueGet("gas-mix.germ-kinds") == 1 && valueGet("gas-mix.germs-in-gas") == germsAll;
		// A second germ type, five times stronger, at the left neighbour: in the cell where the types met, a single type remains.
		const int32_t leftBefore = germCount[left];
		germKind[left] = 1; germCount[left] = 100000;
		moved = moved && tick() && valueGet("gas-mix.germ-kinds") == 2 && valueGet("gas-mix.germ-fights") > 0 && germKind[left] == 1 && germCount[left] > 50000;
		bool germsSane = leftBefore >= 0;
		for (int i = 0; i < N; i++) germsSane = germsSane && germCount[i] >= 0 && (germCount[i] > 0 ? germKind[i] <= 1 : germKind[i] == 0xFF);
		moved = moved && germsSane;
		// Germs are not needed further on: remove them so the remaining checks run as before.
		for (int i = 0; i < N; i++) { germKind[i] = 0xFF; germCount[i] = 0; }
		// Water arrived in the middle cell: the simulation removed its main gas itself, the layer handed the rest to the neighbours.
		// Before that the cell was 100 degrees hotter than its neighbours: the gases handed out bring their heat to the neighbours.
		temperature[middle] += 100.0f;
		moved = moved && tick();
		const int around[4] = { middle - 1, middle + 1, middle - W, middle + W };
		float aroundBefore[4];
		for (int i = 0; i < 4; i++) aroundBefore[i] = temperature[around[i]];
		const float mainGone = mass[middle];
		const long long beforeWater = grams();
		element[middle] = 3; mass[middle] = 1000.0f; temperature[middle] = 300.0f;
		moved = moved && tick() && about(grams(), beforeWater - (long long)(mainGone * 1000.0f)) && valueGet("gas-mix.pushed-grams") > 0
			&& valueGet("gas-mix.lost-grams") == 0 && element[middle] == 3 && mass[middle] == 1000.0f;
		// In this check Water emits gas (element flag): the liquid cell below the gas mixture cell
		// is marked, and while it is processed the cell above it is shown the mass of the whole mixture.
		const float overWater = mass[middle + W];
		struct { void* sim; int32_t cell, raise; } surface = { &sim, middle, 1 };
		const bool surfaceRaised = dataSend("gas-mix.limit-for-test", &surface, sizeof(surface)) == 0 && surface.cell == 2 && mass[middle + W] > overWater + 0.001f && mass[middle] == 1000.0f;
		surface.cell = middle; surface.raise = 0;
		const bool surfaceLowered = dataSend("gas-mix.limit-for-test", &surface, sizeof(surface)) == 0 && mass[middle + W] == overWater;
		if (!(surfaceRaised && surfaceLowered)) printf("gas-mix off-gassing limit over a liquid: FAILED (mark %d, above %f, was %f)\n", surface.cell, mass[middle + W], overWater);
		moved = moved && surfaceRaised && surfaceLowered;
		float warmed = 0.0f;
		for (int i = 0; i < 4; i++) if ((element[around[i]] == 1 || element[around[i]] == 2 || element[around[i]] == 5) && temperature[around[i]] - aroundBefore[i] > warmed) warmed = temperature[around[i]] - aroundBefore[i];
		moved = moved && valueGet("gas-mix.heat-pushed") >= 1 && warmed > 0.5f;
		// The simulation removed the cell's main gas entirely (vacuum): the other gases remained, the cell has a new main gas
		// and its previous temperature, not zero.
		const long long beforeVanish = grams();
		const float vanished = mass[left];
		element[left] = 0; mass[left] = 0.0f; temperature[left] = 0.0f;
		moved = moved && tick() && about(grams(), beforeVanish - (long long)(vanished * 1000.0f)) && valueGet("gas-mix.main-vanished") == 1
			&& valueGet("gas-mix.vanished-in-space") == 1 && valueGet("gas-mix.vanished-tiny") == 0 && element[left] != 0 && mass[left] > 0.0f && temperature[left] > 299.0f;
		// A cell of the Void element next to gas: the gas flows into it and vanishes, the cell itself stays as it is.
		const int outer = 4 * W + 3;
		const long long beforeSpace = grams();
		element[outer] = 6; mass[outer] = 0.0f; temperature[outer] = 0.0f;
		for (int i = 0; i < 20 && moved; i++) moved = tick();
		moved = moved && element[outer] == 6 && mass[outer] == 0.0f && valueGet("gas-mix.void-grams") > 100 && grams() < beforeSpace - 100;
		// Space zone without a back wall in the whole room. The simulation (impersonated here by the check) reduces each cell's main
		// gas by 20 g per tick; a mixture lighter than 20 g the layer removes itself. In the end no gas remains.
		const long long beforeDrain = grams();
		uint16_t walls[N] = {};
		FakeBackwall backwall = { 0, 0, { nullptr, walls, walls + N, walls + N } };
		sim.tail[0] = &backwall;
		for (int i = 0; i < N; i++) zones[i] = 0xFF;
		for (int t = 0; t < 400 && moved; t++)
		{
			moved = tick();
			for (int i = 0; i < N; i++)
			{
				if (element[i] != 1 && element[i] != 2 && element[i] != 5) continue;
				mass[i] -= 0.02f;
				if (mass[i] <= 0.0f) { element[i] = 0; mass[i] = 0.0f; temperature[i] = 0.0f; }
			}
		}
		const long long drainedByLayer = valueGet("gas-mix.space-grams");
		moved = moved && tick() && beforeDrain > 1000 && grams() == 0 && drainedByLayer > 0 && drainedByLayer < beforeDrain;
		printf("gas-mix moving on fake cells: layer grams %lld, main gas switched %lld times, pushed %lld g, lost %lld g; then all of it left through the space zone, %lld g removed by the layer itself: %s\n",
			beforeDrain, switched, (long long)valueGet("gas-mix.pushed-grams"), (long long)valueGet("gas-mix.lost-grams"), drainedByLayer, moved ? "ok" : "FAILED");
		ok = ok && moved;
		valueSet("gas-mix.move", 0);
	}
	{
		// The gases of a cell that closed up are not lost at a neighbour the layer absorbs after it. A row of three cells in a
		// rock frame: Carbon Dioxide left and right, in the middle Oxygen with an admixture of Carbon Dioxide (300 g).
		// Gas movement is off (all rules are zero). Water arrives in the middle cell: the simulation removed its Oxygen
		// itself, and the layer hands the Carbon Dioxide to the neighbours equally, and for both it is the main gas.
		enum { W = 5, H = 3, N = W * H };
		uint16_t element[N]; float mass[N], temperature[N]; uint8_t properties[N] = {}, zones[N] = {};
		for (int i = 0; i < N; i++) { element[i] = 4; mass[i] = 500.0f; temperature[i] = 300.0f; }
		const int a = W + 1, b = W + 2, c = W + 3;
		element[a] = 2; element[b] = 1; element[c] = 2;
		mass[a] = mass[b] = mass[c] = 1.0f;
		uint8_t germKind[N]; int32_t germCount[N] = {}; uint8_t infestation[N] = {}; float growthError[N] = {};
		for (int i = 0; i < N; i++) germKind[i] = 0xFF;
		FakeCells cells = { { nullptr, element, element + N, element + N }, { nullptr, temperature, temperature + N, temperature + N }, { nullptr, mass, mass + N, mass + N }, { nullptr, properties, properties + N, properties + N },
			{}, {}, { nullptr, germKind, germKind + N, germKind + N }, { nullptr, germCount, germCount + N, germCount + N },
			{ nullptr, infestation, infestation + N, infestation + N }, { nullptr, growthError, growthError + N, growthError + N } };
		FakeSim sim = {};
		sim.width = W; sim.height = H; sim.vacuumElement = 0; sim.voidElement = 6; sim.cells = &cells; sim.updatedCells = &cells;
		sim.tail[1] = zones;
		FakeRegion regions[1] = { { 0, 0, W, H, 0.0f, 0.0f } };
		sim.tail[10] = regions; sim.tail[11] = regions + 1; sim.tail[12] = regions + 1;
		const float still[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
		auto tick = [&] { return dataSend("gas-mix.tick-for-test", &sim, sizeof(sim)) == 0; };
		auto grams = [&] { return valueGet("gas-mix.layer-grams"); };
		struct { void* sim; int32_t cell, element; float mass, temperature; int32_t result; float takenMass, takenTemperature; } source = { &sim, b, 2, 0.3f, 300.0f, 0, 0.0f, 0.0f };
		bool pushed = valueSet("gas-mix.move", 1) == 0 && dataSend("gas-mix.params", still, sizeof(still)) == 0 && tick() && grams() == 3000
			&& dataSend("gas-mix.add-gas-for-test", &source, sizeof(source)) == 0 && source.result == 1 && tick() && grams() == 3300 && element[b] == 1;
		const long long lostBefore = valueGet("gas-mix.lost-grams");
		element[b] = 3; mass[b] = 1000.0f;
		pushed = pushed && tick() && grams() >= 2298 && grams() <= 2302 && valueGet("gas-mix.lost-grams") == lostBefore
			&& mass[a] > 1.149f && mass[a] < 1.151f && mass[c] > 1.149f && mass[c] < 1.151f && element[a] == 2 && element[c] == 2;
		printf("gas-mix gases of a closed cell reach both neighbours: layer grams %lld of 2300, left %.3f kg, right %.3f kg: %s\n", (long long)grams(), mass[a], mass[c], pushed ? "ok" : "FAILED");
		ok = ok && pushed;
		// Displacement the simulation could not manage. The middle cell now holds Hydrogen (500 g), the neighbours Carbon
		// Dioxide: for the simulation the Hydrogen has nowhere to go, and a drop falling into the cell would destroy it along with itself.
		// The layer hands the Hydrogen into the neighbours' mixtures, the cell becomes empty, as after displacement by the simulation, and
		// Water can be written into it: the layer mass is unchanged, the neighbours have 250 g of Hydrogen each (by molecule count it is now main there).
		element[b] = 5; mass[b] = 0.5f; temperature[b] = 350.0f;
		struct { void* sim; int32_t cell; } displace = { &sim, b };
		bool displaced = tick() && grams() >= 2798 && grams() <= 2802 && element[b] == 5
			&& dataSend("gas-mix.displace-for-test", &displace, sizeof(displace)) == 0 && displace.cell == 1 && element[b] == 0 && mass[b] == 0.0f && temperature[b] == 0.0f
			&& temperature[a] > 300.5f && temperature[a] < 349.0f && valueGet("gas-mix.displaced-by-layer") == 1 && valueGet("gas-mix.displaced-by-layer-grams") == 500;
		element[b] = 3; mass[b] = 1000.0f; temperature[b] = 300.0f;
		displaced = displaced && tick() && grams() >= 2798 && grams() <= 2802 && valueGet("gas-mix.lost-grams") == lostBefore
			&& element[a] == 5 && element[c] == 5 && mass[a] > 0.249f && mass[a] < 0.251f && mass[c] > 0.249f && mass[c] < 0.251f;
		const long long gramsDisplaced = grams();
		const float hydrogenLeft = mass[a], hydrogenRight = mass[c];
		// A cell without open neighbours: the layer refuses too, and everything goes on as in the game.
		element[a] = 3; mass[a] = 1000.0f;
		displace.cell = c;
		const bool alone = tick() && (element[b] = 4, mass[b] = 500.0f, true) && tick()
			&& dataSend("gas-mix.displace-for-test", &displace, sizeof(displace)) == 0 && displace.cell == 0 && element[c] != 0 && mass[c] > 0.0f;
		printf("gas-mix gas that the sim cannot displace goes into the mixtures around: layer grams %lld of 2800, hydrogen left %.3f kg, right %.3f kg; refused without open neighbours %s: %s\n",
			gramsDisplaced, hydrogenLeft, hydrogenRight, alone ? "yes" : "NO", displaced && alone ? "ok" : "FAILED");
		ok = ok && displaced && alone;
		valueSet("gas-mix.move", 0);
	}
	{
		// Phase rules (enabled by the pressure mod). A 5 x 5 grid: in the bottom row one gas cell and to its right
		// room for liquid; the right column is needed below for boiling in the bulk. Gas movement is off.
		enum { W = 5, H = 5, N = W * H };
		uint16_t element[N]; float mass[N], temperature[N]; uint8_t properties[N] = {}, zones[N] = {};
		for (int i = 0; i < N; i++) { element[i] = 4; mass[i] = 500.0f; temperature[i] = 300.0f; }
		const int a = W + 1, b = W + 2;
		element[a] = 2; mass[a] = 20.0f;
		uint8_t germKind[N]; int32_t germCount[N] = {}; uint8_t infestation[N] = {}; float growthError[N] = {};
		for (int i = 0; i < N; i++) germKind[i] = 0xFF;
		FakeCells cells = { { nullptr, element, element + N, element + N }, { nullptr, temperature, temperature + N, temperature + N }, { nullptr, mass, mass + N, mass + N }, { nullptr, properties, properties + N, properties + N },
			{}, {}, { nullptr, germKind, germKind + N, germKind + N }, { nullptr, germCount, germCount + N, germCount + N },
			{ nullptr, infestation, infestation + N, infestation + N }, { nullptr, growthError, growthError + N, growthError + N } };
		FakeSim sim = {};
		sim.width = W; sim.height = H; sim.vacuumElement = 0; sim.voidElement = 6; sim.cells = &cells; sim.updatedCells = &cells;
		sim.tail[1] = zones;
		FakeRegion regions[1] = { { 0, 0, W, H, 0.0f, 0.0f } };
		sim.tail[10] = regions; sim.tail[11] = regions + 1; sim.tail[12] = regions + 1;
		const float still[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
		auto tick = [&] { return dataSend("gas-mix.tick-for-test", &sim, sizeof(sim)) == 0; };
		auto grams = [&] { return valueGet("gas-mix.layer-grams"); };
		struct { int32_t count, reserved; struct { int32_t cell; uint16_t liquid, ore; float mass, oreMass, temperature, share; } drops[4]; } dropped = {};
		phase::Vapour vapour;
		vapour.Set(250.0f, 44.0f);

		// Condensation by saturated vapour pressure. The cell holds 20 kg of gas 2 at 300 degrees, while at this temperature
		// about 10 kg of its vapour fits. Without the phase rules nothing happens (the cell is warmer than the transition point).
		// With them not all of the excess leaves: the heat of transition warms the cell, and the remainder is saturated at the new temperature.
		bool phases = valueSet("gas-mix.move", 1) == 0 && dataSend("gas-mix.params", still, sizeof(still)) == 0 && tick() && mass[a] == 20.0f && temperature[a] == 300.0f
			&& valueSet("gas-mix.phase", 1) == 0 && tick();
		// While the cell is closed on all sides, the liquid has nowhere to appear, and the vapour stays vapour. An empty cell
		// opens next to it: condensation proceeds.
		phases = phases && mass[a] == 20.0f && dataSend("gas-mix.condensed-for-test", &dropped, sizeof(dropped)) == 0 && dropped.count == 0;
		element[b] = 0; mass[b] = 0.0f; temperature[b] = 0.0f;
		phases = phases && tick();
		// The drop leaves no hotter than the liquid's boiling point (250 + 2.5): the heat it did not carry away stayed in the
		// cell, and the total heat together with the heat of transition is the same.
		const float left = mass[a], warmed = temperature[a];
		phases = phases && dataSend("gas-mix.condensed-for-test", &dropped, sizeof(dropped)) == 0 && dropped.count == 1 && dropped.drops[0].cell == a
			&& dropped.drops[0].liquid == 3 && dropped.drops[0].ore == 4 && dropped.drops[0].oreMass == dropped.drops[0].mass * 0.25f
			&& fabsf(left + dropped.drops[0].mass - 20.0f) < 1e-3f && dropped.drops[0].mass > 0.5f && dropped.drops[0].mass < 2.0f
			&& dropped.drops[0].temperature == 252.5f;
		const float gone20 = dropped.drops[0].mass, whole20 = 20.0f * 0.846f, saturatedAt = 300.0f + gone20 * vapour.latent / whole20;
		phases = phases && warmed > saturatedAt && fabsf(left - vapour.SaturationMass(saturatedAt)) < 0.01f * left
			&& fabsf((whole20 - gone20 * 0.846f) * warmed + gone20 * 0.846f * 252.5f - (whole20 * 300.0f + gone20 * vapour.latent)) < 0.02f * gone20 * vapour.latent;
		// A cell with no more vapour than the limit does not change further.
		phases = phases && tick() && mass[a] == left && dataSend("gas-mix.condensed-for-test", &dropped, sizeof(dropped)) == 0 && dropped.count == 0;
		printf("gas-mix phases, condensation by saturation: %.3f kg of 20 left (limit %.3f kg at %.1f K), the drop left at %.1f K, the cell is at %.1f K: %s\n",
			left, vapour.SaturationMass(saturatedAt), saturatedAt, dropped.drops[0].temperature, warmed, phases ? "ok" : "FAILED");

		// Evaporation. The gas cell holds a kilogram of Oxygen at 300 degrees, next to it 100 kg of liquid 3 at 240
		// (below the boiling point): its vapour pressure is lower than the Oxygen pressure, it does not boil and gives off vapour little by little.
		// The liquid cools by the heat of transition, the vapour enters the mixture as a non-main gas, a tenth of the departed
		// mass remains as a solid residue.
		element[a] = 1; mass[a] = 1.0f; temperature[a] = 300.0f;
		element[b] = 3; mass[b] = 100.0f; temperature[b] = 240.0f;
		struct { int32_t cell, wait, count; float temperature; struct { int32_t element; float mass; } gases[8]; } query = {};
		auto vapourIn = [&] {
			query.cell = 0; query.wait = 1; query.count = 0;
			if (dataSend("gas-mix.cell", &query, sizeof(query)) != 0) return -1.0f;
			float found = 0.0f;
			for (int i = 0; i < query.count && i < 8; i++) if (query.gases[i].element == 2) found += query.gases[i].mass;
			return found;
		};
		bool evaporates = tick();
		const float gone = 100.0f - mass[b], steam = vapourIn();
		evaporates = evaporates && gone > 0.05f && gone < 0.2f && fabsf(steam - 0.9f * gone) < 1e-4f && element[a] == 1 && mass[a] == 1.0f
			&& fabsf((240.0f - temperature[b]) * mass[b] * 4.18f - gone * vapour.latent) < 0.02f * gone * vapour.latent
			&& temperature[a] < 300.0f && temperature[a] > 290.0f && valueGet("gas-mix.evaporations") == 1 && valueGet("gas-mix.boilings") == 0
			&& dataSend("gas-mix.condensed-for-test", &dropped, sizeof(dropped)) == 0 && dropped.count == 1 && dropped.drops[0].cell == b
			&& dropped.drops[0].liquid == 0xFFFF && dropped.drops[0].ore == 4 && fabsf(dropped.drops[0].mass - 0.1f * gone) < 1e-4f
			&& grams() >= (long long)(1000.0f + 900.0f * gone) - 2 && grams() <= (long long)(1000.0f + 900.0f * gone) + 2;
		// The same liquid at 300 degrees: its vapour pressure is above the gas pressure, it boils and per pass gives off
		// tens of times more.
		temperature[b] = 300.0f;
		const float beforeBoil = mass[b];
		evaporates = evaporates && tick() && valueGet("gas-mix.boilings") == 1 && beforeBoil - mass[b] > 5.0f * gone && temperature[b] < 300.0f;
		printf("gas-mix phases, evaporation: %.4f kg left the liquid without boiling (steam in the cell %.4f kg), %.3f kg when it boils, liquid cooled to %.2f K: %s\n",
			gone, steam, beforeBoil - mass[b], temperature[b], evaporates ? "ok" : "FAILED");

		// Boiling in the bulk. In the column from top to bottom: a cell with Oxygen, cold liquid (its vapour pressure is below
		// the Oxygen pressure, it does not boil), hot liquid. The hot one has no open neighbours, but its vapour pressure
		// is above the pressure over the column: it boils, the vapour passes through the liquid above it into the gas cell, and it
		// cools down. The cold liquid does not change.
		const int top = 3 * W + 3, cover = 2 * W + 3, deep = W + 3;
		element[top] = 1; mass[top] = 1.0f; temperature[top] = 300.0f;
		element[cover] = 3; mass[cover] = 100.0f; temperature[cover] = 240.0f;
		element[deep] = 3; mass[deep] = 100.0f; temperature[deep] = 300.0f;
		const long long boilingsBefore = valueGet("gas-mix.boilings");
		bool bulk = tick();
		const float bubbled = 100.0f - mass[deep];
		bulk = bulk && bubbled > 5.0f && bubbled < 15.0f && temperature[deep] < 290.0f && mass[cover] == 100.0f && temperature[cover] == 240.0f
			&& element[top] == 2 && fabsf(mass[top] - 0.9f * bubbled) < 1e-3f && valueGet("gas-mix.boilings") == boilingsBefore + 1;
		printf("gas-mix phases, boiling under another liquid: %.3f kg left the deep cell (cooled to %.1f K), %.3f kg of steam above the column, the liquid in between kept %.3f kg: %s\n",
			bubbled, temperature[deep], mass[top], mass[cover], bulk ? "ok" : "FAILED");
		ok = ok && phases && evaporates && bulk;
		valueSet("gas-mix.phase", 0);
		valueSet("gas-mix.move", 0);
	}
	{
		// The pressure plugin on single-substance cells (mode without the gas mixture mod). The same 5 x 5 grid.
		enum { W = 5, H = 5, N = W * H };
		uint16_t element[N]; float mass[N], temperature[N]; uint8_t properties[N] = {};
		for (int i = 0; i < N; i++) { element[i] = 4; mass[i] = 500.0f; temperature[i] = 300.0f; }
		const int a = W + 1, b = W + 2;
		FakeCells cells = { { nullptr, element, element + N, element + N }, { nullptr, temperature, temperature + N, temperature + N }, { nullptr, mass, mass + N, mass + N }, { nullptr, properties, properties + N, properties + N },
			{}, {}, {}, {}, {}, {} };
		FakeSim sim = {};
		sim.width = W; sim.height = H; sim.vacuumElement = 0; sim.voidElement = 6; sim.cells = &cells; sim.updatedCells = &cells;
		FakeRegion regions[1] = { { 0, 0, W, H, 0.0f, 0.0f } };
		sim.tail[10] = regions; sim.tail[11] = regions + 1; sim.tail[12] = regions + 1;
		auto tick = [&] { return dataSend("pressure.tick-for-test", &sim, sizeof(sim)) == 0; };
		struct { void* sim; int32_t cell; } blocked = { &sim, a };
		auto blocks = [&](uint16_t what, float warm) {
			element[a] = what; mass[a] = 1.0f; temperature[a] = warm;
			blocked.cell = a;
			return dataSend("pressure.blocked-for-test", &blocked, sizeof(blocked)) == 0 ? blocked.cell : -1;
		};
		struct { int32_t count, reserved; struct { int32_t cell; uint16_t liquid, ore; float mass, oreMass, temperature; } out[4]; } released = {};
		phase::Vapour vapour;
		vapour.Set(250.0f, 44.0f);

		// Blocking the simulation's transitions: cold gas does not liquefy entirely, hot liquid does not turn into gas
		// entirely; liquid hotter than the critical temperature and substances without a "vapour and liquid" pair are left to the simulation.
		bool pressure = dataSend("pressure.elements", table, sizeof(table)) == 0
			&& blocks(2, 240.0f) == 1 && blocks(2, 300.0f) == 0 && blocks(3, 300.0f) == 2 && blocks(3, 240.0f) == 0 && blocks(3, 420.0f) == 0
			&& blocks(1, 50.0f) == 0 && blocks(4, 5000.0f) == 0;

		// Condensation: 20 kg of gas 2 at 300 degrees; the remainder is saturated at the new temperature, the cell is heated
		// by the heat of transition, a quarter of the condensed mass is marked as ore.
		element[a] = 2; mass[a] = 20.0f; temperature[a] = 300.0f;
		// While the cell is closed on all sides, the liquid has nowhere to appear, and the vapour stays vapour.
		pressure = pressure && tick() && mass[a] == 20.0f && dataSend("pressure.released-for-test", &released, sizeof(released)) == 0 && released.count == 0;
		element[b] = 0; mass[b] = 0.0f; temperature[b] = 0.0f;
		pressure = pressure && tick() && dataSend("pressure.released-for-test", &released, sizeof(released)) == 0 && released.count == 1
			&& released.out[0].cell == a && released.out[0].liquid == 3 && released.out[0].ore == 4 && released.out[0].oreMass == released.out[0].mass * 0.25f
			&& fabsf(mass[a] + released.out[0].mass - 20.0f) < 1e-3f && released.out[0].temperature == 252.5f
			&& fabsf(mass[a] - vapour.SaturationMass(300.0f + released.out[0].mass * vapour.latent / (20.0f * 0.846f))) < 0.01f * mass[a]
			&& fabsf(mass[a] * 0.846f * temperature[a] + released.out[0].mass * 0.846f * 252.5f - (20.0f * 0.846f * 300.0f + released.out[0].mass * vapour.latent)) < 0.02f * released.out[0].mass * vapour.latent;
		const float condensedTo = mass[a], condensedAt = temperature[a];

		// Liquid next to vacuum boils: its vapour appears in the empty cell with the liquid's temperature, the liquid cools
		// by the heat of transition, a tenth of the departed mass remains as a solid residue.
		element[a] = 0; mass[a] = 0.0f; temperature[a] = 0.0f;
		element[b] = 3; mass[b] = 100.0f; temperature[b] = 240.0f;
		pressure = pressure && tick();
		const float gone = 100.0f - mass[b];
		pressure = pressure && gone > 0.5f && element[a] == 2 && fabsf(mass[a] - 0.9f * gone) < 1e-4f && temperature[a] == 240.0f
			&& fabsf((240.0f - temperature[b]) * mass[b] * 4.18f - gone * vapour.latent) < 0.02f * gone * vapour.latent && valueGet("pressure.boilings") == 1
			&& dataSend("pressure.released-for-test", &released, sizeof(released)) == 0 && released.count == 1 && released.out[0].liquid == 0xFFFF
			&& released.out[0].ore == 4 && fabsf(released.out[0].mass - 0.1f * gone) < 1e-4f;
		// After that the vapour above the liquid comes to equilibrium: the liquid's vapour pressure and the vapour in the cell converge.
		for (int i = 0; i < 200 && pressure; i++) pressure = tick();
		const float balance = vapour.SaturationMass(temperature[b]) * temperature[b] / temperature[a];
		pressure = pressure && fabsf(mass[a] - balance) < 0.02f * balance;
		const float settled = mass[a];

		// A foreign gas above the liquid. While the liquid does not boil (its vapour pressure is below the gas pressure), nothing
		// changes: the vapour has nothing to mix with. A boiling liquid displaces the gas and fills the cell with its vapour.
		element[a] = 1; mass[a] = 1.0f; temperature[a] = 300.0f;
		mass[b] = 100.0f; temperature[b] = 240.0f;
		pressure = pressure && tick() && element[a] == 1 && mass[a] == 1.0f && mass[b] == 100.0f && valueGet("pressure.displaced-for-steam") == 0;
		temperature[b] = 300.0f;
		pressure = pressure && tick() && element[a] == 2 && mass[a] > 1.0f && mass[b] < 99.0f && temperature[b] < 300.0f && valueGet("pressure.displaced-for-steam") == 1;
		// Boiling in the bulk: a cell with Oxygen, cold liquid below it, hot liquid below that. The hot one's vapour passes
		// upwards and displaces the Oxygen (in this mode they cannot mix).
		const int top = 3 * W + 3, cover = 2 * W + 3, deep = W + 3;
		element[top] = 1; mass[top] = 1.0f; temperature[top] = 300.0f;
		element[cover] = 3; mass[cover] = 100.0f; temperature[cover] = 240.0f;
		element[deep] = 3; mass[deep] = 100.0f; temperature[deep] = 300.0f;
		pressure = pressure && tick();
		const float bubbled = 100.0f - mass[deep];
		pressure = pressure && bubbled > 5.0f && bubbled < 15.0f && temperature[deep] < 290.0f && mass[cover] == 100.0f && temperature[cover] == 240.0f
			&& element[top] == 2 && fabsf(mass[top] - 0.9f * bubbled) < 1e-3f;
		printf("pressure plugin, boiling under another liquid: %.3f kg left the deep cell (cooled to %.1f K), %.3f kg of steam above the column: %s\n",
			bubbled, temperature[deep], mass[top], pressure ? "ok" : "FAILED");
		// Phase rule settings: pressure by mass (an atmosphere is 1.8 kg) and real numbers for gas 2 (at the boiling point
		// from the table its vapour pressure is 7.3 atm, above 304 degrees it does not liquefy). 20 kg of gas at 300
		// degrees is then below the limit (about 57 kg) and does not condense, at 250 degrees the limit is about 13 kg, and
		// the excess leaves; at 310 degrees even 500 kg does not condense.
		struct { int32_t byMass; float atmosphereMass, evaporationRate, boilRate, portion; int32_t substances; struct { int32_t gas; float ratio, critical, latent, reference; } list[1]; }
			settings = { 1, 1.8f, 0.1f, 0.5f, 0.01f, 1, { { 2, 8.8f, 304.0f, 348.0f, 7.3f } } };
		for (int i = 0; i < N; i++) { element[i] = 4; mass[i] = 500.0f; temperature[i] = 300.0f; }
		bool configured = dataSend("pressure.phase-config", &settings, sizeof(settings)) == 0 && dataSend("pressure.phase-config", &settings, sizeof(settings) - 4) < 0
			&& dataSend("gas-mix.phase-config", &settings, sizeof(settings)) == 0;
		element[b] = 0; mass[b] = 0.0f; temperature[b] = 0.0f;
		element[a] = 2; mass[a] = 20.0f; temperature[a] = 300.0f;
		configured = configured && tick() && mass[a] == 20.0f && dataSend("pressure.released-for-test", &released, sizeof(released)) == 0 && released.count == 0;
		mass[a] = 500.0f; temperature[a] = 310.0f;
		configured = configured && tick() && mass[a] == 500.0f && blocks(2, 240.0f) == 1;
		element[a] = 2; mass[a] = 20.0f; temperature[a] = 250.0f;
		configured = configured && tick() && mass[a] < 19.5f && mass[a] > 13.0f && temperature[a] > 250.0f
			&& dataSend("pressure.released-for-test", &released, sizeof(released)) == 0 && released.count == 1 && released.out[0].liquid == 3;
		printf("pressure plugin, settings: by mass with real numbers for the gas, 20 kg at 250 K leave %.2f kg at %.1f K: %s\n", mass[a], temperature[a], configured ? "ok" : "FAILED");
		// The settings are reset to the plugins' defaults.
		settings.byMass = 0; settings.substances = 0;
		configured = configured && dataSend("pressure.phase-config", &settings, 24) == 0 && dataSend("gas-mix.phase-config", &settings, 24) == 0;
		pressure = pressure && configured;
		printf("pressure plugin on single-substance cells: condensation leaves %.3f kg of 20 at %.1f K; liquid next to vacuum gives %.3f kg at once, steam settles at %.3f kg (balance %.3f); boiling pushes the other gas out: %s\n",
			condensedTo, condensedAt, gone, settled, balance, pressure ? "ok" : "FAILED");
		ok = ok && pressure;
	}

	{
		// Liquid mixture plugin: liquid table with mixing groups, the main liquid of a mixture, inventory of
		// liquids on a 4 x 3 grid. Elements: 1 Water, 2 Salt Water, 3 Brine, 4 Crude Oil, 5 Petroleum, 6 mercury.
		using namespace liquid;
		// Element 8: Steam (gas), Water (with no residue) and Brine (30% remains as solid 7) boil into it. Element 9: another gas,
		// liquid 2 boils into it at 351.5 K (like Ethanol: a mixture with Water gives two different vapours).
		const uint16_t none = 0xFFFF;
		struct { TableHeader header; Liquid liquids[6]; uint8_t states[12]; float strength[1]; } table =
		{
			{ 10, 6, 0.2f, 1 },
			{
				{ 1, 0, 1000.0f, 18.0f, 125.0f, 0.01f, 0.01f, 1000.0f, 272.5f, 372.5f, none, none, 8, none, 0.0f, 0.0f },
				{ 2, 0, 1100.0f, 21.0f, 100.0f, 0.01f, 0.01f, 1100.0f, 265.6f, 351.5f, none, none, 9, none, 0.0f, 0.0f },
				{ 3, 0, 1200.0f, 22.0f, 100.0f, 0.01f, 0.01f, 1200.0f, 250.6f, 375.9f, none, none, 8, 7, 0.0f, 0.3f },
				{ 4, 1, 870.0f, 500.0f, 50.0f, 0.1f, 0.1f, 870.0f, 233.0f, 673.0f, none, none, none, none, 0.0f, 0.0f },
				{ 5, 1, 740.0f, 82.2f, 50.0f, 0.1f, 0.1f, 740.0f, 216.0f, 812.0f, none, none, none, none, 0.0f, 0.0f },
				{ 6, NoGroup, 13534.0f, 200.6f, 140.0f, 0.01f, 0.01f, 1000.0f, 234.3f, 629.9f, none, none, none, none, 0.0f, 0.0f },
			},
			{ Vacuum, Fluid, Fluid, Fluid, Fluid, Fluid, Fluid, Solid, Gas, Gas },
			{ 1.0f },
		};
		bool liquids = dataSend("liquid-mix.table", &table, sizeof(table)) == 0 && dataSend("liquid-mix.table", &table, sizeof(table) - 4) < 0;
		// 450 kg of Water and 550 kg of Brine: Water stays main (Brine below 55.6%); at 400 and 600 Brine
		// becomes main. Mixture density by volumes.
		struct { uint16_t current, count; uint16_t elements[8]; float masses[8]; } query = { 1, 2, { 1, 3 }, { 450.0f, 550.0f } };
		liquids = liquids && dataSend("liquid-mix.main", &query, sizeof(query)) == 0 && query.current == 1 && fabsf(query.masses[0] - 1101.0f) < 0.5f;
		const float density = query.masses[0];
		query.masses[0] = 400.0f; query.masses[1] = 600.0f;
		liquids = liquids && dataSend("liquid-mix.main", &query, sizeof(query)) == 0 && query.current == 3;
		enum { W = 4, H = 3, N = W * H };
		// Bottom to top: Brine, Brine, Crude Oil, mercury / Water, Salt Water, Petroleum, empty / empty, empty, Water, empty.
		uint16_t element[N] = { 3, 3, 4, 6,  1, 2, 5, 0,  0, 0, 1, 0 };
		float mass[N] = { 1200, 1200, 870, 1000,  1000, 1100, 740, 0,  0, 0, 500, 0 }, temperature[N];
		uint8_t properties[N] = {};
		for (int i = 0; i < N; i++) temperature[i] = 300.0f;
		FakeCells cells = { { nullptr, element, element + N, element + N }, { nullptr, temperature, temperature + N, temperature + N }, { nullptr, mass, mass + N, mass + N }, { nullptr, properties, properties + N, properties + N },
			{}, {}, {}, {}, {}, {} };
		FakeSim sim = {};
		sim.width = W; sim.height = H; sim.cells = &cells; sim.updatedCells = &cells;
		struct { int32_t count; Pair pairs[16]; } pairs = { 16, {} };
		liquids = liquids && dataSend("liquid-mix.survey-for-test", &sim, sizeof(sim)) == 0 && dataSend("liquid-mix.survey-pairs", &pairs, sizeof(pairs)) == 0
			&& valueGet("liquid-mix.cells") == 8 && valueGet("liquid-mix.group-cells") == 7 && valueGet("liquid-mix.kilograms") == 7610
			&& valueGet("liquid-mix.fronts") == 4 && valueGet("liquid-mix.foreign-contacts") == 4 && valueGet("liquid-mix.inversions") == 1 && pairs.count == 9;
		bool waterOverPetroleum = false;
		for (int i = 0; i < pairs.count && i < 16; i++) waterOverPetroleum = waterOverPetroleum || (pairs.pairs[i].kind == Inversion && pairs.pairs[i].first == 5 && pairs.pairs[i].second == 1);
		liquids = liquids && waterOverPetroleum;
		// The cell's own movement on the real cell layout: 2000 kg of Water on a rock floor, empty on the sides.
		// Per tick the cell gives at most 125 kg to each side: left, right and up (the cell holds twice the mass of a
		// full one, the excess goes up); 1625 remains. Germs leave in mass shares, 1000 of 16,000 each.
		{
			enum { W2 = 5, N2 = 25 };
			uint16_t e2[N2]; float m2[N2], t2[N2], growth[N2] = {}; uint8_t p2[N2] = {}, idx[N2], infested[N2] = {}; int32_t germs[N2] = {};
			for (int i = 0; i < N2; i++) { e2[i] = i < 2 * W2 ? 7 : 0; m2[i] = i < 2 * W2 ? 500.0f : 0.0f; t2[i] = i < 2 * W2 ? 300.0f : 0.0f; idx[i] = 0xFF; }
			const int centre = 2 * W2 + 2;
			e2[centre] = 1; m2[centre] = 2000.0f; t2[centre] = 320.0f; idx[centre] = 0; germs[centre] = 16000;
			FakeCells own = { { nullptr, e2, e2 + N2, e2 + N2 }, { nullptr, t2, t2 + N2, t2 + N2 }, { nullptr, m2, m2 + N2, m2 + N2 }, { nullptr, p2, p2 + N2, p2 + N2 },
				{}, {}, { nullptr, idx, idx + N2, idx + N2 }, { nullptr, germs, germs + N2, germs + N2 }, { nullptr, infested, infested + N2, infested + N2 }, { nullptr, growth, growth + N2, growth + N2 } };
			FakeSim ownSim = {};
			ownSim.width = W2; ownSim.height = 5; ownSim.vacuumElement = 0; ownSim.voidElement = 0xFFF0; ownSim.cells = &own; ownSim.updatedCells = &own;
			struct { void* sim; int32_t cell; float flow[4]; } step = { &ownSim, centre, {} };
			const bool moved = dataSend("liquid-mix.own-for-test", &step, sizeof(step)) == 0 && step.cell == 3
				&& m2[centre] == 1625.0f && m2[centre - 1] == 125.0f && m2[centre + 1] == 125.0f && m2[centre + W2] == 125.0f
				&& e2[centre - 1] == 1 && e2[centre + 1] == 1 && e2[centre + W2] == 1 && t2[centre - 1] == 320.0f
				&& germs[centre - 1] == 1000 && germs[centre + 1] == 1000 && germs[centre + W2] == 1000 && germs[centre] == 13000 && idx[centre + 1] == 0
				&& step.flow[0] == 125.0f && step.flow[1] == 125.0f && step.flow[2] == 0.0f && step.flow[3] == 125.0f;
			printf("liquid-mix plugin, own movement of one cell: 2000 kg on a floor give %.0f kg left, %.0f kg right and %.0f kg up, %.0f kg stay, germs %d + %d + %d + %d: %s\n",
				m2[centre - 1], m2[centre + 1], m2[centre + W2], m2[centre], germs[centre - 1], germs[centre + 1], germs[centre + W2], germs[centre], moved ? "ok" : "FAILED");
			liquids = liquids && moved;
		}
		// Mixtures: pump and intake by composition, saving compositions. A 1200 kg cell: 75% Water and 25% Brine (Water is main),
		// next to it a cell of pure Brine, 1000 kg. The pump picks a liquid weighted by mass: Water 900 kg, Brine 1300 kg.
		{
			enum { W3 = 6, N3 = 36 };
			uint16_t e3[N3]; float m3[N3], t3[N3], growth3[N3] = {}; uint8_t p3[N3] = {}, idx3[N3], inf3[N3] = {}; int32_t germs3[N3] = {};
			for (int i = 0; i < N3; i++) { e3[i] = 7; m3[i] = 500.0f; t3[i] = 300.0f; idx3[i] = 0xFF; }
			const int mixed = 2 * W3 + 2, pure = mixed + 1;
			e3[mixed] = 1; m3[mixed] = 1200.0f; t3[mixed] = 310.0f; idx3[mixed] = 0; germs3[mixed] = 12000;
			e3[pure] = 3; m3[pure] = 1000.0f; t3[pure] = 290.0f;
			FakeCells cells3 = { { nullptr, e3, e3 + N3, e3 + N3 }, { nullptr, t3, t3 + N3, t3 + N3 }, { nullptr, m3, m3 + N3, m3 + N3 }, { nullptr, p3, p3 + N3, p3 + N3 },
				{}, {}, { nullptr, idx3, idx3 + N3, idx3 + N3 }, { nullptr, germs3, germs3 + N3, germs3 + N3 }, { nullptr, inf3, inf3 + N3, inf3 + N3 }, { nullptr, growth3, growth3 + N3, growth3 + N3 } };
			FakeSim sim3 = {};
			sim3.width = W3; sim3.height = 6; sim3.vacuumElement = 0; sim3.voidElement = 0xFFF0; sim3.cells = &cells3; sim3.updatedCells = &cells3;
			const bool flags = valueSet("liquid-mix.own", 1) == 0 && valueSet("liquid-mix.mixing", 1) == 0;
			struct { void* sim; int32_t cell, count; uint16_t element[10]; float share[10]; } setMix = { &sim3, mixed, 2, { 1, 3 }, { 0.75f, 0.25f } };
			bool mixing = flags && dataSend("liquid-mix.mix-for-test", &setMix, sizeof(setMix)) == 0;
			struct { void* sim; int32_t count, rounds; int32_t cells[8]; int32_t counts[8]; } pick = { &sim3, 2, 22000, { mixed, pure }, {} };
			mixing = mixing && dataSend("liquid-mix.pick-for-test", &pick, sizeof(pick)) == 0;
			const float waterPicked = pick.counts[1] / 22000.0f;
			mixing = mixing && fabsf(waterPicked - 900.0f / 2200.0f) < 0.02f && pick.counts[1] + pick.counts[3] == 22000;
			// The pump asks for 400 kg of Brine from the mixed cell: there is only 300 there, all of it is taken; the cell becomes pure
			// Water, 900 kg (the substance does not change), germs leave in proportion to mass (300 of 1200: 3000 of 12,000).
			struct { void* sim; int32_t cell; uint16_t element, pad; float wanted; struct { int32_t handle; uint16_t element; uint8_t idx, pad; float mass, temperature; int32_t count; } taken; int32_t result; }
				take = { &sim3, mixed, 3, 0, 400.0f, { -1, 3, 0xFF, 0, 0.0f, 0.0f, 0 }, 0 };
			mixing = mixing && dataSend("liquid-mix.take-for-test", &take, sizeof(take)) == 0 && take.result == 1 && fabsf(take.wanted - 100.0f) < 1e-3f
				&& fabsf(take.taken.mass - 300.0f) < 1e-3f && take.taken.temperature == 310.0f && take.taken.idx == 0 && take.taken.count == 3000
				&& fabsf(m3[mixed] - 900.0f) < 1e-3f && e3[mixed] == 1 && germs3[mixed] == 9000;
			// The same cell is already pure: taking Brine from it is not ours (the game takes it).
			take.wanted = 50.0f;
			mixing = mixing && dataSend("liquid-mix.take-for-test", &take, sizeof(take)) == 0 && take.result == 0;
			// Saving and restoring: a 60/40 mixture of Water and Brine in a pure Brine cell (Brine is main).
			struct { void* sim; int32_t cell, count; uint16_t element[10]; float share[10]; } second = { &sim3, pure, 2, { 3, 1 }, { 0.6f, 0.4f } };
			mixing = mixing && dataSend("liquid-mix.mix-for-test", &second, sizeof(second)) == 0;
			uint8_t saved[4096];
			const int32_t saveResult = dataSend("liquid-mix.save", saved, sizeof(saved));
			int32_t savedCount = 0, savedCell = -1;
			memcpy(&savedCount, saved + 16, 4);
			memcpy(&savedCell, saved + 24, 4);
			int32_t savedBytes = 0;
			memcpy(&savedBytes, saved + 20, 4);
			void* simPointer = &sim3;
			mixing = mixing && saveResult == 0 && savedCount == 1 && savedCell == (pure / W3 - 1) * (W3 - 2) + pure % W3 - 1
				&& dataSend("liquid-mix.restore", saved, savedBytes) == 0 && dataSend("liquid-mix.restore-for-test", &simPointer, sizeof(simPointer)) == 0;
			// After restoring the cell is a mixture again: taking Brine gets 60% of 1000 kg.
			take.cell = pure; take.wanted = 1000.0f; take.taken = { -1, 3, 0xFF, 0, 0.0f, 0.0f, 0 };
			mixing = mixing && dataSend("liquid-mix.take-for-test", &take, sizeof(take)) == 0 && take.result == 1 && fabsf(take.taken.mass - 600.0f) < 1e-2f
				&& e3[pure] == 1 && fabsf(m3[pure] - 400.0f) < 1e-2f;
			// Evaporation of a mixture by the phase rules (a service for other plugins): a cell of 500 kg Water and 500 kg Brine at 380 K
			// gives off 10 kg. By Raoult's law 5.74 kg of Water leaves and 4.26 kg of Brine; of the Brine 30% remains as solid, so
			// the vapour is 8.72 kg, and the Brine share in the cell rises to 0.5007.
			{
				const SimHookApi* api = engineApi ? (const SimHookApi*)engineApi() : nullptr;
				typedef int32_t (*EvaporateFn)(void* sim, int32_t cell, float mass, float temperature, uint16_t gas, void* out, float* taken);
				EvaporateFn evaporate = api ? (EvaporateFn)api->find_service("liquid-mix.evaporate") : nullptr;
				e3[mixed] = 1; m3[mixed] = 1000.0f; t3[mixed] = 380.0f;
				struct { void* sim; int32_t cell, count; uint16_t element[10]; float share[10]; } half = { &sim3, mixed, 2, { 1, 3 }, { 0.5f, 0.5f } };
				struct { uint16_t gas, pad; float mass; } out[4] = {};
				const bool set = dataSend("liquid-mix.mix-for-test", &half, sizeof(half)) == 0;
				float taken = 0.0f;
				const int32_t gases = set && evaporate ? evaporate(&sim3, mixed, 10.0f, 380.0f, 0xFFFF, out, &taken) : -1;
				m3[mixed] -= 10.0f;
				struct { int32_t cell, count; int32_t element[10]; float share[10]; } query = { (mixed / W3 - 1) * (W3 - 2) + mixed % W3 - 1, 0, {}, {} };
				// The composition query goes by game cell, and the grid width is taken from the last composition request.
				const bool asked = dataSend("liquid-mix.cell", &query, sizeof(query)) == 0;
				float brineShare = 0.0f;
				for (int i = 0; i < query.count; i++) if (query.element[i] == 3) brineShare = query.share[i];
				const bool raoult = gases == 1 && out[0].gas == 8 && fabsf(out[0].mass - 8.722f) < 0.01f && fabsf(taken - 10.0f) < 1e-4f && asked && fabsf(brineShare - 0.50074f) < 0.0002f;
				printf("liquid-mix plugin, evaporation of a mixture for the phase rules: %d gases, steam %.3f kg of 10 kg (Raoult split), brine share in the cell %.5f: %s\n",
					gases, gases > 0 ? out[0].mass : 0.0f, brineShare, raoult ? "ok" : "FAILED");
				mixing = mixing && raoult;
				// Vapour pressure correction for the same cell (Water is main, Brine about half): Brine boils later than Water,
				// so the mixture has less vapour than pure Water, about 0.96 of it.
				typedef float (*FactorFn)(void* sim, int32_t cell, float temperature);
				FactorFn factor = api ? (FactorFn)api->find_service("liquid-mix.vapour-factor") : nullptr;
				const float f = factor ? factor(&sim3, mixed, 380.0f) : -1.0f, outside = factor ? factor(&sim3, mixed + 1, 380.0f) : -1.0f;
				// Vapours of the same cell: both liquids give vapour 8, so there is one record; count along the Water curve (it has more vapour),
				// with the same correction.
				typedef int32_t (*VapoursFn)(void* sim, int32_t cell, float temperature, void* out);
				VapoursFn vapours = api ? (VapoursFn)api->find_service("liquid-mix.vapours") : nullptr;
				struct { uint16_t gas, liquid; float factor; } entries[4] = {};
				const int32_t kinds = vapours ? vapours(&sim3, mixed, 380.0f, entries) : -1;
				const bool lower = f > 0.95f && f < 0.97f && outside == 1.0f && kinds == 1 && entries[0].gas == 8 && entries[0].liquid == 1 && fabsf(entries[0].factor - f) < 1e-4f;
				printf("liquid-mix plugin, vapour pressure of the mixture against pure water at 380 K: %.4f (a cell without a mixture %.1f); vapours of the cell %d, steam by the curve of liquid %d with %.4f: %s\n",
					f, outside, kinds, kinds > 0 ? entries[0].liquid : -1, kinds > 0 ? entries[0].factor : 0.0f, lower ? "ok" : "FAILED");
				mixing = mixing && lower;
				// A half-and-half mixture of Water (vapour 8) and liquid 2 (vapour 9) at 340 K: two vapours, each with its own correction. Evaporating
				// only vapour 9 takes mass only from liquid 2.
				struct { void* sim; int32_t cell, count; uint16_t element[10]; float share[10]; } two = { &sim3, mixed, 2, { 1, 2 }, { 0.5f, 0.5f } };
				const bool setTwo = dataSend("liquid-mix.mix-for-test", &two, sizeof(two)) == 0;
				const int32_t pairKinds = setTwo && vapours ? vapours(&sim3, mixed, 340.0f, entries) : -1;
				bool bothGases = pairKinds == 2;
				for (int32_t k = 0; k < pairKinds && k < 4; k++) bothGases = bothGases && ((entries[k].gas == 8 && entries[k].liquid == 1) || (entries[k].gas == 9 && entries[k].liquid == 2));
				const float before = m3[mixed];
				taken = 0.0f;
				const int32_t onlyNine = evaporate ? evaporate(&sim3, mixed, 5.0f, 340.0f, 9, out, &taken) : -1;
				m3[mixed] -= taken;
				struct { int32_t cell, count; int32_t element[10]; float share[10]; } after = { query.cell, 0, {}, {} };
				dataSend("liquid-mix.cell", &after, sizeof(after));
				float secondShare = 0.0f;
				for (int i = 0; i < after.count; i++) if (after.element[i] == 2) secondShare = after.share[i];
				const float expected = (before * 0.5f - 5.0f) / (before - 5.0f);
				const bool separate = bothGases && onlyNine == 1 && out[0].gas == 9 && fabsf(out[0].mass - 5.0f) < 1e-3f && fabsf(taken - 5.0f) < 1e-4f && fabsf(secondShare - expected) < 1e-4f;
				printf("liquid-mix plugin, a mixture with two vapours: %d vapours; evaporating only the second takes %.3f kg from its liquid alone (share %.5f, expected %.5f): %s\n",
					pairKinds, taken, secondShare, expected, separate ? "ok" : "FAILED");
				mixing = mixing && separate;
			}
			// Water density anomaly: Water is densest at about 4 °C. Above Water at 278 K, Water at 275 K is lighter (does not
			// sink), at 280 K above 290 K it is denser (convection, as in the game); for Brine (point of maximum density below
			// freezing) cold on top is always denser.
			{
				struct { int32_t element; float solute; } anomaly[2] = { { 1, 0.0f }, { 3, 0.3f } };
				const bool sent = dataSend("liquid-mix.water-anomaly", anomaly, sizeof(anomaly)) == 0;
				const int upperCell = 3 * W3 + 3, lowerCell = 2 * W3 + 3;
				auto decide = [&](uint16_t element, float upper, float lower) {
					e3[upperCell] = element; e3[lowerCell] = element; m3[upperCell] = 1000.0f; m3[lowerCell] = 1000.0f;
					t3[upperCell] = upper; t3[lowerCell] = lower;
					struct { void* sim; int32_t cell; } test = { &sim3, upperCell };
					return dataSend("liquid-mix.convection-for-test", &test, sizeof(test)) == 0 ? test.cell : -9;
				};
				const int cold = decide(1, 275.0f, 278.0f), warm = decide(1, 280.0f, 290.0f), brine = decide(3, 275.0f, 278.0f), other = decide(5, 275.0f, 278.0f);
				const bool anomalyOk = sent && cold == 0 && warm == 1 && brine == 1 && other == -1;
				printf("liquid-mix plugin, water density anomaly: water at 275 K over 278 K sinks %d, at 280 K over 290 K %d; brine at 275 K over 278 K %d; another liquid %d: %s\n",
					cold, warm, brine, other, anomalyOk ? "ok" : "FAILED");
				mixing = mixing && anomalyOk;
			}
			valueSet("liquid-mix.own", 0);
			valueSet("liquid-mix.mixing", 0);
			printf("liquid-mix plugin, mixtures: a pump picks water %.3f of the time (share of mass 0.409); it takes %.1f kg of brine of 400 asked from the mixed cell, germs %d; "
				"a saved composition comes back after loading (cell of the save %d, brine taken %.1f kg): %s\n", waterPicked, 300.0f, 3000, savedCell, take.taken.mass, mixing ? "ok" : "FAILED");
			liquids = liquids && mixing;
		}
		printf("liquid-mix plugin: 450 kg of water with 550 kg of brine stay water of %.0f kg/m3; survey of fake cells: %lld liquid cells, %lld pairs that would mix, %lld that would not, %lld with the denser liquid above: %s\n",
			density, (long long)valueGet("liquid-mix.cells"), (long long)valueGet("liquid-mix.fronts"), (long long)valueGet("liquid-mix.foreign-contacts"),
			(long long)valueGet("liquid-mix.inversions"), liquids ? "ok" : "FAILED");
		ok = ok && liquids;
	}

	// 3. With the symbol table: how many simulation functions can be hooked at all.
	const int32_t symbols = symbolCount();
	if (symbols > 0)
	{
		// The working plugin: its functions are found by name, hooks are created and removed, the switch and counters
		// are available. The functions themselves are not called here, so the counters stay at zero.
		// The plugin has seventeen hooks: all are enabled and disabled at once.
		const int32_t mixOn = pluginEnable("gas-mix", 1);
		const bool values = valueSet("gas-mix.freeze-sim", 1) == 0 && valueGet("gas-mix.freeze-sim") == 1 && valueSet("gas-mix.freeze-sim", 0) == 0
			&& valueGet("gas-mix.sim-pressure-calls") == 0 && valueSet("gas-mix.no-such-value", 1) < 0 && valueGet("no-such-value") == -1;
		const int32_t mixOff = pluginEnable("gas-mix", 0);
		const bool mix = mixOn == 17 && mixOff == 17 && values;
		// The pressure plugin next to the mixture layer: transition blocking (two hooks) and its own pass over the cells,
		// which sits on the same tick function as the layer. The engine puts both into a chain; whether to skip its own
		// pass next to the layer is decided by the pressure mod (the phase rules are then computed by the layer).
		const int32_t mixAgain = pluginEnable("gas-mix", 1);
		const int32_t phaseOn = pluginEnable("pressure", 1), phaseOff = pluginEnable("pressure", 0);
		// The liquid mixture plugin joins as the third on the same tick function.
		const int32_t cellsBeside = pluginEnable("pressure-cells", 1), liquidBeside = pluginEnable("liquid-mix", 1);
		const int32_t cellsOff = pluginEnable("pressure-cells", 0), liquidOff = pluginEnable("liquid-mix", 0);
		// Measurement of the game's liquid movement: two hooks with timing.
		const int32_t measureOn = pluginEnable("liquid-measure", 1), measureOff = pluginEnable("liquid-measure", 0);
		// Shadow: comparing our own flow calculation with the game's, three hooks (one on the same function as the measurement).
		const int32_t shadowOn = pluginEnable("liquid-measure", 1) == 2 ? pluginEnable("liquid-shadow", 1) : -100;
		const int32_t shadowOff = pluginEnable("liquid-shadow", 0);
		pluginEnable("liquid-measure", 0);
		const int32_t ownOn = pluginEnable("liquid-own", 1), ownOff = pluginEnable("liquid-own", 0);
		const int32_t mixingOn = pluginEnable("liquid-mixing", 1), mixingOff = pluginEnable("liquid-mixing", 0);
		pluginEnable("gas-mix", 0);
		const bool pressureHooks = mixAgain == 17 && phaseOn == 2 && phaseOff == 2 && cellsBeside == 1 && cellsOff == 1 && liquidBeside == 1 && liquidOff == 1
			&& measureOn == 2 && measureOff == 2 && shadowOn == 3 && shadowOff == 3 && ownOn == 1 && ownOff == 1 && mixingOn == 14 && mixingOff == 14;
		printf("plugins next to the gas mixture layer: pressure transition hooks %d, on the same tick function its own pass %d and the liquid mixture plugin %d: %s\n",
			phaseOn, cellsBeside, liquidBeside, pressureHooks ? "ok" : "FAILED");
		ok = ok && pressureHooks;
		printf("plugin gas-mix: enable %d (%s), switch and counters %s, disable %d: %s\n", mixOn, mixOn < 0 ? lastError() : "hooks", values ? "ok" : "FAILED", mixOff, mix ? "ok" : "FAILED");
		ok = ok && mix;

		int hooked = 0, failed = 0, ambiguous = 0;
		char firstFailures[1024] = "";
		for (int32_t i = 0; i < symbols; i++)
		{
			const int32_t result = probeAdd(symbolName(i));
			if (result >= 0) hooked++;
			else if (result == -6) ambiguous++;
			else
			{
				failed++;
				if (strlen(firstFailures) < 800)
				{
					strcat(firstFailures, "\n    ");
					strncat(firstFailures, lastError(), 150);
				}
			}
		}
		const int32_t notRemoved = probeClear();
		printf("probes over the symbol table: %d symbols, hooked %d, same-name skipped %d, failed %d, not removed %d%s\n",
			symbols, hooked, ambiguous, failed, notRemoved, firstFailures);
		ok = ok && notRemoved == 0 && hooked > 0;
	}
	printf("%s\n", ok ? "HOOKTEST OK" : "HOOKTEST FAILED");
	return ok ? 0 : 1;
}
