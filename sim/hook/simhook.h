// Hooking functions of the game's native simulation (SimDLL.dll) from our library.
//
// The library is loaded into the game process next to SimDLL, finds functions in it by name
// and replaces their start with a jump to our code (engine: MinHook). Two kinds of hooks:
//
//  - counter (probe): counts calls to the function and changes nothing. Does not need to know its parameters,
//    so it is good for scouting: which simulation functions run at all and how often;
//  - plugin: our version of the function with the same parameters, which may call the original, change
//    its behaviour or not call it at all. Plugins live in sim/plugins; each is built into its own library
//    and attaches to the engine on load (simplugin_attach), so a mod carries only its own plugins.
//
// A single function can carry any number of hooks from different plugins plus a counter: the engine links them into a
// chain. The function start holds one jump to the first link; the "original" of each link is the next link,
// and for the last one it is the real continuation of the function. So the cost for the game is one indirect call per plugin
// that hooked this function, and nothing beyond that: there is no intermediary between links, and a plugin that does not
// need the function takes no part in its chain. With one plugin on a function the path is the same as without chains.
// The order of links is set by the number order (SIM_HOOK_ORDERED): smaller means closer to the game code; on equal numbers
// the plugin with the alphabetically smaller name goes first, so the order does not depend on the mod load order.
// The counter is always first and sees every call, even those a plugin never passed on to the original.
// Everything a plugin does before calling the original runs in chain order, everything after, in reverse.
//
// There is one engine per game process. Its library is shipped in every mod that needs hooks, but it is loaded by
// the mod that asks first; the others find the already loaded one by file name and use it.
//
// Rules:
//  - Hooks are installed and removed only while the simulation is not computing: when the mod loads or between its frames.
//  - Hook code runs on simulation threads. It must not call C#; data shared with C# only
//    through atomic counters and flags.
//  - Function names and addresses are tied to the SimDLL build; on a mismatch nothing is hooked. A plugin additionally
//    names the build it was written for.
#pragma once

#include <stdint.h>

#if defined(_WIN32)
#define SIMHOOK_EXPORT extern "C" __declspec(dllexport)
#else
#define SIMHOOK_EXPORT extern "C"
#endif

// Changes on any edit of the exports: C# checks the number.
#define SIMHOOK_ABI 7

enum SimHookResult : int32_t
{
	SIMHOOK_OK = 0,
	SIMHOOK_NO_MODULE = -1,      // SimDLL is not loaded into the process
	SIMHOOK_NO_SYMBOLS = -2,     // symbol table file not found or unreadable
	SIMHOOK_WRONG_BUILD = -3,    // symbol table was taken from a different SimDLL build
	SIMHOOK_ENGINE_FAILED = -4,  // the hook engine failed to start
	SIMHOOK_UNKNOWN_SYMBOL = -5, // no such name in the table
	SIMHOOK_AMBIGUOUS = -6,      // several functions share the name: a qualifier like "name#2" is needed
	SIMHOOK_PATCH_FAILED = -7,   // the function start could not be patched
	SIMHOOK_NOT_READY = -8,      // simhook_init has not been called yet or it failed
	SIMHOOK_UNKNOWN_PLUGIN = -9,
	SIMHOOK_UNKNOWN_VALUE = -10,
};

// One plugin hook. Filled in by the SIM_HOOK macro (simhook_plugin.h).
struct SimHookEntry
{
	const char* plugin;   // plugin name: hooks with the same name are enabled and disabled together
	const char* symbol;   // simulation function name, as in the symbol table
	void* detour;         // our function
	void** original;      // receives the address through which the original is called: the next link of the chain
	SimHookEntry* next;
	void* target;         // function address in SimDLL once the hook is created
	int32_t order;        // place in this function's hook chain: smaller means closer to the game code
};

// A hook that looks at which place in the game code called it (return address): it must be first in the chain
// among plugins, otherwise it would see an address inside another plugin. There cannot be two of them on one function:
// the second plugin will not be enabled. (The counter sits even earlier, but it jumps rather than calls and does not change
// the return address.)
#define SIMHOOK_DIRECT (INT32_MIN + 1)


// A named plugin number available to the mod: a switch (the mod writes, the plugin reads) or a counter
// (the plugin writes, the mod reads). Name of the form "plugin.what". Filled in by the SIM_FLAG and SIM_COUNTER macros.
struct SimHookValue
{
	const char* name;
	void* atomic;      // std::atomic<int64_t>
	SimHookValue* next;
};


// A named plugin data receiver: the mod hands it a block of bytes (for example, the element property table).
// The handler is called on the mod's thread, not the simulation's: the data must be copied and published so
// that the simulation thread sees it whole (atomic pointer). Filled in by the SIM_DATA macro.
struct SimHookData
{
	const char* name;
	int32_t (*handler)(const void* data, int32_t size); // 0 on success
	SimHookData* next;
};


// The SimDLL build the plugin was written for: its hooks rely on that build's struct layouts and function
// parameters, so with a different build (after a game update) the plugin is not enabled. Filled in by the macro
// SIM_PLUGIN_BUILD (simhook_plugin.h).
struct SimHookBuild
{
	const char* plugin;
	uint32_t timestamp;   // build time from the SimDLL.dll header
	uint32_t sizeOfImage; // image size from the same place
	SimHookBuild* next;
};

// Plugin service: a function a plugin offers to other plugins (for example, the liquid plugin hands the cell composition
// to the phase rules). The call is direct, with no intermediary: the other plugin looks the address up by name once and then calls it itself.
// Name of the form "plugin.what"; the function parameters are defined by the plugin providing it. Filled in by the SIM_SERVICE macro.
struct SimHookService
{
	const char* name;
	void* function;
	SimHookService* next;
};

// Engine functions for a plugin in another library. The plugin receives this table on attach and does not
// depend on the engine library by name itself.
struct SimHookApi
{
	int32_t abi; // the engine's SIMHOOK_ABI: a plugin built for a different number does not attach
	void (*register_hook)(SimHookEntry* entry);
	void (*register_value)(SimHookValue* value);
	void (*register_data)(SimHookData* data);
	// Address of a simulation function by name from the symbol table: for plugins that need to call it themselves.
	// nullptr if the engine is not ready or the name is missing.
	void* (*find)(const char* name);
	void (*register_build)(SimHookBuild* build);
	void (*register_service)(SimHookService* service);
	// Address of another plugin's service by name; nullptr if no such plugin is in the game.
	void* (*find_service)(const char* name);
};
