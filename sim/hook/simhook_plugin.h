// Included by plugins (sim/plugins/*.cpp). A plugin is a separate library: the mod loads it and calls
// simplugin_attach with the engine's function table; until then everything the plugin declared with the macros below
// sits in its own lists. One of the plugin's files must contain the line SIM_PLUGIN_MODULE();
//
// Example: hook a simulation function with parameters (object, cell number), count calls and call the original.
//
//   #include "simhook_plugin.h"
//   static std::atomic<int64_t> calls;
//   SIM_HOOK("my-plugin", void, "SimData::SomeFunction", SomeFunction, (void* self, int32_t cell))
//   {
//       calls.fetch_add(1, std::memory_order_relaxed);
//       SomeFunction_original(self, cell);
//   }
//
// SomeFunction_original is not necessarily the game's function itself: if another plugin hooked the same function,
// it is that plugin's version (the next link of the chain, see simhook.h). It may be called outside the hook too; for a
// disabled plugin it leads straight into the game function, and before the first enable it is nullptr.
//
// 64-bit Windows programs have a single calling convention, and class methods take the object pointer as the first
// parameter, so a method is described as a plain function with a self parameter.
// The parameter list must match the game function exactly: a mistake in it corrupts the simulation stack and crashes the game.
#pragma once

#include <atomic>

#include "simhook.h"

// The plugin's lists before attaching to the engine, and the engine's function table after.
inline SimHookEntry* simPluginHooks = nullptr;
inline SimHookValue* simPluginValues = nullptr;
inline SimHookData* simPluginData = nullptr;
inline SimHookBuild* simPluginBuilds = nullptr;
inline SimHookService* simPluginServices = nullptr;
inline const SimHookApi* simPluginApi = nullptr;

// Address of a simulation function by name from the symbol table: for plugins that need to call it themselves.
// nullptr if the plugin is not attached yet, the engine is not ready or the name is missing.
inline void* simhook_find(const char* name)
{
	return simPluginApi ? simPluginApi->find(name) : nullptr;
}

// Another plugin's service by name (simhook.h, SimHookService). Look it up once and keep it: the address does not change.
inline void* simhook_service(const char* name)
{
	return simPluginApi ? simPluginApi->find_service(name) : nullptr;
}

// Entry point of the plugin library. Returns 0, or SIMHOOK_WRONG_BUILD if the engine is a different version.
#define SIM_PLUGIN_MODULE() \
	SIMHOOK_EXPORT int32_t simplugin_attach(const SimHookApi* api) \
	{ \
		if (!api || api->abi != SIMHOOK_ABI) return SIMHOOK_WRONG_BUILD; \
		if (simPluginApi) return SIMHOOK_OK; \
		simPluginApi = api; \
		for (SimHookEntry* entry = simPluginHooks; entry;) { SimHookEntry* next = entry->next; api->register_hook(entry); entry = next; } \
		for (SimHookValue* value = simPluginValues; value;) { SimHookValue* next = value->next; api->register_value(value); value = next; } \
		for (SimHookData* data = simPluginData; data;) { SimHookData* next = data->next; api->register_data(data); data = next; } \
		for (SimHookBuild* build = simPluginBuilds; build;) { SimHookBuild* next = build->next; api->register_build(build); build = next; } \
		for (SimHookService* service = simPluginServices; service;) { SimHookService* next = service->next; api->register_service(service); service = next; } \
		return SIMHOOK_OK; \
	}

// The SimDLL build the plugin was written for (build time and image size from the library header). With a different build the engine will not enable the plugin's hooks.
//   SIM_PLUGIN_BUILD("gas-mix", 0x6a67f9d8, 0x125000);
struct SimHookBuildRegistrar
{
	explicit SimHookBuildRegistrar(SimHookBuild* build) { build->next = simPluginBuilds; simPluginBuilds = build; }
};
#define SIM_PLUGIN_BUILD_NAMED(ident, plugin, timestamp, sizeOfImage) \
	static SimHookBuild ident = { plugin, timestamp, sizeOfImage, nullptr }; \
	static SimHookBuildRegistrar ident##_registrar(&ident)
#define SIM_PLUGIN_BUILD_PASTE(line, plugin, timestamp, sizeOfImage) SIM_PLUGIN_BUILD_NAMED(simPluginBuild##line, plugin, timestamp, sizeOfImage)
#define SIM_PLUGIN_BUILD_LINE(line, plugin, timestamp, sizeOfImage) SIM_PLUGIN_BUILD_PASTE(line, plugin, timestamp, sizeOfImage)
#define SIM_PLUGIN_BUILD(plugin, timestamp, sizeOfImage) SIM_PLUGIN_BUILD_LINE(__LINE__, plugin, timestamp, sizeOfImage)

struct SimHookRegistrar
{
	explicit SimHookRegistrar(SimHookEntry* entry) { entry->next = simPluginHooks; simPluginHooks = entry; }
};

struct SimHookValueRegistrar
{
	explicit SimHookValueRegistrar(SimHookValue* value) { value->next = simPluginValues; simPluginValues = value; }
};

// A switch through which the mod controls the plugin on the fly, and a counter the mod reads.
// Both are std::atomic<int64_t>, because the plugin runs on simulation threads and the mod on the game's main thread.
//   SIM_FLAG(frozen, "gas-mix.freeze-sim");       if (frozen.load(std::memory_order_relaxed)) ...
//   SIM_COUNTER(skipped, "gas-mix.skipped");      skipped.fetch_add(1, std::memory_order_relaxed);
#define SIM_VALUE(ident, name) \
	static std::atomic<int64_t> ident{0}; \
	static SimHookValue ident##_value = { name, &ident, nullptr }; \
	static SimHookValueRegistrar ident##_value_registrar(&ident##_value)
#define SIM_FLAG(ident, name) SIM_VALUE(ident, name)
#define SIM_COUNTER(ident, name) SIM_VALUE(ident, name)

struct SimHookDataRegistrar
{
	explicit SimHookDataRegistrar(SimHookData* data) { data->next = simPluginData; simPluginData = data; }
};

// Receiver of a data block from the mod:
//   SIM_DATA(Elements, "gas-mix.elements") { ...data, size...; return 0; }
// The block lives in the mod's memory and is valid until the handler returns. The mod uses the same path to ask the plugin for data:
// the handler writes the answer straight into the passed buffer (example: "gas-mix.cell").
#define SIM_DATA(ident, name) \
	static int32_t ident##_handler(const void* data, int32_t size); \
	static SimHookData ident##_data = { name, ident##_handler, nullptr }; \
	static SimHookDataRegistrar ident##_data_registrar(&ident##_data); \
	static int32_t ident##_handler(const void* data, int32_t size)

// Hook with a place in the chain: needed when several plugins hook the same function and the order matters
// (chain description in simhook.h). order: smaller means closer to the game code; SIMHOOK_DIRECT for a hook
// that looks at the return address.
#define SIM_HOOK_ORDERED(plugin, order, result, symbol, ident, args) \
	static result (*ident##_original) args; \
	static result ident##_detour args; \
	static SimHookEntry ident##_entry = { plugin, symbol, (void*)ident##_detour, (void**)&ident##_original, nullptr, nullptr, order }; \
	static SimHookRegistrar ident##_registrar(&ident##_entry); \
	static result ident##_detour args

#define SIM_HOOK(plugin, result, symbol, ident, args) SIM_HOOK_ORDERED(plugin, 0, result, symbol, ident, args)

struct SimHookServiceRegistrar
{
	explicit SimHookServiceRegistrar(SimHookService* service) { service->next = simPluginServices; simPluginServices = service; }
};

// Service for other plugins: SIM_SERVICE(Evaporate, "liquid-mix.evaporate", EvaporateFunction);
#define SIM_SERVICE(ident, name, function) \
	static SimHookService ident##_service = { name, (void*)(function), nullptr }; \
	static SimHookServiceRegistrar ident##_service_registrar(&ident##_service)
