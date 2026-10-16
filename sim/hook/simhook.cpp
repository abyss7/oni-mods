#include <windows.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <algorithm>
#include <atomic>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "MinHook.h"
#include "simhook.h"
#include "pdb_functions.h"

namespace
{
	struct Symbol
	{
		std::string name;
		uint32_t rva;
	};

	struct Site;

	// A link in the hook chain of one function: a plugin hook or a call counter.
	struct Link
	{
		Site* site;
		void* detour;       // where the call arrives
		void* original;     // where the next link's address is written (for the counter it is inside the stub, unaligned)
		int32_t order;
		const char* owner;  // plugin name; empty string for the counter
		bool enabled;
	};

	// A simulation function carrying hooks.
	struct Site
	{
		std::string name;
		void* target = nullptr;
		void* head = nullptr;       // the link the jump at the function start currently leads to; nullptr: the function is untouched
		void* trampoline = nullptr; // continuation of the original while the jump is in place
		std::vector<Link*> links;
	};

	struct Probe
	{
		std::string name;
		void* target;
		uint8_t* stub; // see MakeProbeStub
		Link* link;
	};

	struct State
	{
		std::mutex mutex;
		bool ready = false;
		HMODULE module = nullptr;
		uint32_t moduleSize = 0, moduleTimestamp = 0;
		std::vector<Symbol> symbols;
		std::unordered_multimap<std::string, uint32_t> byName; // name -> index in symbols
		std::vector<Probe> probes;
		std::unordered_map<void*, Site*> sites;               // by function address
		std::unordered_map<SimHookEntry*, Link*> pluginLinks; // links of plugin hooks
		std::unordered_map<std::string, void*> ownSymbols;    // see simhook_symbol_add_address
		uint8_t* stubPage = nullptr;
		size_t stubUsed = 0, stubSize = 0;
		std::string lastError = "simhook_init was not called";
	};

	// Not destroyed on unload: hooks may fire even while the process is shutting down.
	State& state = *new State;
	SimHookEntry* pluginHooks = nullptr;
	SimHookValue* pluginValues = nullptr;
	SimHookData* pluginData = nullptr;
	SimHookBuild* pluginBuilds = nullptr;
	SimHookService* pluginServices = nullptr;

	std::atomic<int64_t>* FindValue(const char* name)
	{
		for (SimHookValue* value = pluginValues; value; value = value->next)
		{
			if (strcmp(value->name, name) == 0) return (std::atomic<int64_t>*)value->atomic;
		}
		return nullptr;
	}

	int32_t Fail(int32_t code, const std::string& message)
	{
		state.lastError = message;
		return code;
	}

	// PE header of a loaded module: the SimDLL build is checked by build time and image size.
	bool ModuleIdentity(HMODULE module, uint32_t* timestamp, uint32_t* sizeOfImage)
	{
		const uint8_t* base = (const uint8_t*)module;
		const IMAGE_DOS_HEADER* dos = (const IMAGE_DOS_HEADER*)base;
		if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
		const IMAGE_NT_HEADERS64* nt = (const IMAGE_NT_HEADERS64*)(base + dos->e_lfanew);
		if (nt->Signature != IMAGE_NT_SIGNATURE) return false;
		*timestamp = nt->FileHeader.TimeDateStamp;
		*sizeOfImage = nt->OptionalHeader.SizeOfImage;
		return true;
	}

	// Function table: a text file of function names and addresses.
	//   # module=SimDLL.dll timestamp=0x........ size_of_image=0x........
	//   <rva in hex><TAB><function name>
	// Functions with the same name (overloads) are consecutive in ascending address order; refer to them as "name#1", "name#2".
	int32_t LoadSymbols(const char* path, uint32_t timestamp, uint32_t sizeOfImage)
	{
		FILE* file = fopen(path, "rb");
		if (!file) return Fail(SIMHOOK_NO_SYMBOLS, std::string("cannot open symbols file: ") + path);
		char line[4096];
		bool identityChecked = false;
		while (fgets(line, sizeof(line), file))
		{
			size_t length = strlen(line);
			while (length > 0 && (line[length - 1] == '\n' || line[length - 1] == '\r')) line[--length] = 0;
			if (length == 0) continue;
			if (line[0] == '#')
			{
				const char* ts = strstr(line, "timestamp=");
				const char* size = strstr(line, "size_of_image=");
				if (ts && size)
				{
					const uint32_t fileTimestamp = (uint32_t)strtoul(ts + 10, nullptr, 16);
					const uint32_t fileSize = (uint32_t)strtoul(size + 14, nullptr, 16);
					if (fileTimestamp != timestamp || fileSize != sizeOfImage)
					{
						fclose(file);
						char message[256];
						snprintf(message, sizeof(message), "symbols are for another SimDLL build: file timestamp=0x%08x size=0x%x, loaded timestamp=0x%08x size=0x%x",
							fileTimestamp, fileSize, timestamp, sizeOfImage);
						return Fail(SIMHOOK_WRONG_BUILD, message);
					}
					identityChecked = true;
				}
				continue;
			}
			char* tab = strchr(line, '\t');
			if (!tab) continue;
			*tab = 0;
			const uint32_t rva = (uint32_t)strtoul(line, nullptr, 16);
			if (rva == 0 || rva >= sizeOfImage) continue;
			state.byName.emplace(tab + 1, (uint32_t)state.symbols.size());
			state.symbols.push_back({ tab + 1, rva });
		}
		fclose(file);
		if (!identityChecked) return Fail(SIMHOOK_WRONG_BUILD, "symbols file has no build identity line");
		return SIMHOOK_OK;
	}

	// The game's own file next to the library: functions are read straight from it.
	// The file must belong to this very library: its identifier (GUID) is recorded in the library's own debug directory.
	int32_t LoadPdbSymbols(const char* path, uint32_t sizeOfImage)
	{
		std::vector<PdbFunction> functions;
		PdbIdentity identity = {};
		std::string error;
		if (!ReadPdbFunctions(path, functions, &identity, error)) return Fail(SIMHOOK_NO_SYMBOLS, error);
		const uint8_t* base = (const uint8_t*)state.module;
		const IMAGE_NT_HEADERS64* nt = (const IMAGE_NT_HEADERS64*)(base + ((const IMAGE_DOS_HEADER*)base)->e_lfanew);
		const IMAGE_DATA_DIRECTORY& debug = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
		bool same = false;
		for (DWORD at = 0; at + sizeof(IMAGE_DEBUG_DIRECTORY) <= debug.Size && debug.VirtualAddress != 0; at += sizeof(IMAGE_DEBUG_DIRECTORY))
		{
			const IMAGE_DEBUG_DIRECTORY* entry = (const IMAGE_DEBUG_DIRECTORY*)(base + debug.VirtualAddress + at);
			if (entry->Type != IMAGE_DEBUG_TYPE_CODEVIEW || entry->SizeOfData < 24 || entry->AddressOfRawData == 0) continue;
			const uint8_t* record = base + entry->AddressOfRawData;
			same = same || (memcmp(record, "RSDS", 4) == 0 && memcmp(record + 4, identity.guid, 16) == 0);
		}
		if (!same) return Fail(SIMHOOK_WRONG_BUILD, std::string("the function file is not from the loaded SimDLL: ") + path);
		for (const PdbFunction& function : functions)
		{
			if (function.rva == 0 || function.rva >= sizeOfImage) continue;
			state.byName.emplace(function.name, (uint32_t)state.symbols.size());
			state.symbols.push_back({ function.name, function.rva });
		}
		return SIMHOOK_OK;
	}

	bool EndsWith(const char* text, const char* tail)
	{
		const size_t length = strlen(text), tailLength = strlen(tail);
		return length >= tailLength && _stricmp(text + length - tailLength, tail) == 0;
	}

	// Function address by name: first the symbol table, then SimDLL's own exports.
	int32_t Resolve(const char* name, void** address)
	{
		const auto own = state.ownSymbols.find(name);
		if (own != state.ownSymbols.end())
		{
			*address = own->second;
			return SIMHOOK_OK;
		}
		std::string plain = name;
		int ordinal = 0;
		const size_t hash = plain.rfind('#');
		if (hash != std::string::npos && hash + 1 < plain.size() && isdigit((unsigned char)plain[hash + 1]))
		{
			ordinal = atoi(plain.c_str() + hash + 1);
			plain.resize(hash);
		}
		const auto range = state.byName.equal_range(plain);
		std::vector<uint32_t> rvas;
		for (auto it = range.first; it != range.second; ++it) rvas.push_back(state.symbols[it->second].rva);
		if (rvas.empty())
		{
			if (void* exported = (void*)GetProcAddress(state.module, name))
			{
				*address = exported;
				return SIMHOOK_OK;
			}
			return Fail(SIMHOOK_UNKNOWN_SYMBOL, std::string("unknown symbol: ") + name);
		}
		if (rvas.size() > 1)
		{
			if (ordinal < 1 || ordinal > (int)rvas.size())
			{
				return Fail(SIMHOOK_AMBIGUOUS, plain + " names " + std::to_string(rvas.size()) + " functions: use " + plain + "#1.." + std::to_string(rvas.size()));
			}
			// Order in unordered_multimap is undefined, hence by address.
			for (size_t i = 0; i < rvas.size(); i++)
				for (size_t j = i + 1; j < rvas.size(); j++)
					if (rvas[j] < rvas[i]) { const uint32_t swap = rvas[i]; rvas[i] = rvas[j]; rvas[j] = swap; }
			*address = (uint8_t*)state.module + rvas[ordinal - 1];
			return SIMHOOK_OK;
		}
		*address = (uint8_t*)state.module + rvas[0];
		return SIMHOOK_OK;
	}

	// Counter stub, 32 bytes: without knowing the function's parameters it counts the call and jumps to the original.
	//   +0   lock inc qword ptr [rip+16]   ; counter at +24
	//   +8   jmp  qword ptr [rip+0]        ; address at +14
	//   +14  address of the original's continuation (MinHook trampoline)
	//   +24  call counter
	// Parameter registers are left untouched; flags on function entry carry no meaning.
	uint8_t* MakeProbeStub()
	{
		const size_t stubBytes = 32;
		if (!state.stubPage || state.stubUsed + stubBytes > state.stubSize)
		{
			state.stubSize = 64 * 1024;
			state.stubPage = (uint8_t*)VirtualAlloc(nullptr, state.stubSize, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
			state.stubUsed = 0;
			if (!state.stubPage) return nullptr;
		}
		uint8_t* stub = state.stubPage + state.stubUsed;
		state.stubUsed += stubBytes;
		static const uint8_t code[14] = { 0xF0, 0x48, 0xFF, 0x05, 0x10, 0x00, 0x00, 0x00, 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
		memcpy(stub, code, sizeof(code));
		memset(stub + 14, 0, stubBytes - 14);
		return stub;
	}

	Site* SiteAt(void* target, const char* name)
	{
		Site*& site = state.sites[target];
		if (!site)
		{
			site = new Site;
			site->name = name;
			site->target = target;
		}
		return site;
	}

	void SetNext(Link* link, void* next)
	{
		memcpy(link->original, &next, sizeof(next));
	}

	// Rebuilds the function's chain from the enabled links. The jump at the function start leads to the first link,
	// so it is re-pointed only when the first link changed; for the rest it is enough to rewrite the addresses of
	// the next ones. Disabled links also get a working address (the original's continuation past the others): a plugin
	// may call its "original" from outside the hook too.
	int32_t Rebuild(Site& site)
	{
		std::vector<Link*> chain;
		for (Link* link : site.links)
		{
			if (link->enabled) chain.push_back(link);
		}
		std::stable_sort(chain.begin(), chain.end(), [](const Link* a, const Link* b)
		{
			return a->order != b->order ? a->order < b->order : strcmp(a->owner, b->owner) < 0;
		});
		for (size_t i = 0; i + 1 < chain.size(); i++)
		{
			if (chain[i]->order != SIMHOOK_DIRECT || chain[i + 1]->order != SIMHOOK_DIRECT) continue;
			return Fail(SIMHOOK_PATCH_FAILED, site.name + ": plugins " + chain[i]->owner + " and " + chain[i + 1]->owner
				+ " both look at the address of the calling code and cannot share this function");
		}
		void* head = chain.empty() ? nullptr : chain[0]->detour;
		int32_t result = SIMHOOK_OK;
		bool created = false;
		if (head != site.head)
		{
			if (site.head)
			{
				const MH_STATUS removed = MH_RemoveHook(site.target);
				if (removed != MH_OK) return Fail(SIMHOOK_PATCH_FAILED, site.name + ": " + MH_StatusToString(removed));
				site.head = nullptr;
				site.trampoline = nullptr;
			}
			if (head)
			{
				const MH_STATUS status = MH_CreateHook(site.target, head, &site.trampoline);
				if (status == MH_OK)
				{
					site.head = head;
					created = true;
				}
				else
				{
					result = Fail(SIMHOOK_PATCH_FAILED, site.name + ": " + MH_StatusToString(status));
					site.trampoline = nullptr;
					chain.clear();
				}
			}
		}
		void* rest = site.head ? site.trampoline : site.target;
		for (Link* link : site.links)
		{
			if (!link->enabled || chain.empty()) SetNext(link, rest);
		}
		for (size_t i = chain.size(); i-- > 0;) SetNext(chain[i], i + 1 < chain.size() ? chain[i + 1]->detour : rest);
		if (created)
		{
			const MH_STATUS status = MH_EnableHook(site.target);
			if (status != MH_OK)
			{
				MH_RemoveHook(site.target);
				site.head = nullptr;
				site.trampoline = nullptr;
				for (Link* link : site.links) SetNext(link, site.target);
				return Fail(SIMHOOK_PATCH_FAILED, site.name + ": " + MH_StatusToString(status));
			}
		}
		return result;
	}

	void RemoveLink(Link* link)
	{
		std::vector<Link*>& links = link->site->links;
		links.erase(std::remove(links.begin(), links.end(), link), links.end());
	}

	int32_t AddProbeAt(void* target, const char* label)
	{
		for (size_t i = 0; i < state.probes.size(); i++)
		{
			if (state.probes[i].target == target) return (int32_t)i;
		}
		uint8_t* stub = MakeProbeStub();
		if (!stub) return Fail(SIMHOOK_ENGINE_FAILED, "cannot allocate memory for a probe");
		// The counter is first in the chain: it counts all calls, not only those the plugins passed on to the original.
		Site* site = SiteAt(target, label);
		Link* link = new Link{ site, stub, stub + 14, INT32_MIN, "", true };
		site->links.push_back(link);
		const int32_t built = Rebuild(*site);
		if (built != SIMHOOK_OK)
		{
			RemoveLink(link);
			delete link;
			const std::string error = state.lastError;
			Rebuild(*site);
			return Fail(built, error);
		}
		state.probes.push_back({ label, target, stub, link });
		return (int32_t)state.probes.size() - 1;
	}
}

// Plugin attachment: their hooks, numbers and data receivers join the engine's shared lists.
static void RegisterHook(SimHookEntry* entry)
{
	entry->next = pluginHooks;
	pluginHooks = entry;
}

static void* FindSymbol(const char* name)
{
	std::lock_guard<std::mutex> lock(state.mutex);
	void* address = nullptr;
	return state.ready && Resolve(name, &address) == SIMHOOK_OK ? address : nullptr;
}

static void RegisterValue(SimHookValue* value)
{
	value->next = pluginValues;
	pluginValues = value;
}

static void RegisterData(SimHookData* data)
{
	data->next = pluginData;
	pluginData = data;
}

static void RegisterBuild(SimHookBuild* build)
{
	build->next = pluginBuilds;
	pluginBuilds = build;
}

static void RegisterService(SimHookService* service)
{
	service->next = pluginServices;
	pluginServices = service;
}

static void* FindService(const char* name)
{
	for (SimHookService* service = pluginServices; service; service = service->next)
	{
		if (strcmp(service->name, name) == 0) return service->function;
	}
	return nullptr;
}

// The engine's function table for plugins: the mod passes it to a plugin when loading it (simplugin_attach).
SIMHOOK_EXPORT const SimHookApi* simhook_api()
{
	static const SimHookApi api = { SIMHOOK_ABI, RegisterHook, RegisterValue, RegisterData, FindSymbol, RegisterBuild, RegisterService, FindService };
	return &api;
}

// Passes a data block to a plugin. Returns what its handler returned (0 on success).
SIMHOOK_EXPORT int32_t simhook_data_send(const char* name, const void* data, int32_t size)
{
	for (SimHookData* entry = pluginData; entry; entry = entry->next)
	{
		if (strcmp(entry->name, name) == 0) return entry->handler(data, size);
	}
	return SIMHOOK_UNKNOWN_VALUE;
}

// Writes a plugin switch. May be called at any time: it is a single atomic number.
SIMHOOK_EXPORT int32_t simhook_value_set(const char* name, int64_t value)
{
	std::atomic<int64_t>* found = FindValue(name);
	if (!found) return SIMHOOK_UNKNOWN_VALUE;
	found->store(value, std::memory_order_relaxed);
	return SIMHOOK_OK;
}

// Reads a plugin switch or counter; returns -1 for an unknown name.
SIMHOOK_EXPORT int64_t simhook_value_get(const char* name)
{
	std::atomic<int64_t>* found = FindValue(name);
	return found ? found->load(std::memory_order_relaxed) : -1;
}

SIMHOOK_EXPORT int32_t simhook_abi()
{
	return SIMHOOK_ABI;
}

SIMHOOK_EXPORT const char* simhook_last_error()
{
	return state.lastError.c_str();
}

// moduleName: name of the loaded simulation library ("SimDLL.dll"). symbolsPath may be empty:
// then only exported functions are available.
SIMHOOK_EXPORT int32_t simhook_init(const char* moduleName, const char* symbolsPath)
{
	std::lock_guard<std::mutex> lock(state.mutex);
	if (state.ready) return SIMHOOK_OK;
	state.module = GetModuleHandleA(moduleName);
	if (!state.module) return Fail(SIMHOOK_NO_MODULE, std::string(moduleName) + " is not loaded");
	uint32_t timestamp = 0;
	if (!ModuleIdentity(state.module, &timestamp, &state.moduleSize)) return Fail(SIMHOOK_NO_MODULE, "module header is not readable");
	state.moduleTimestamp = timestamp;
	state.symbols.clear();
	state.byName.clear();
	if (symbolsPath && symbolsPath[0])
	{
		const int32_t loaded = EndsWith(symbolsPath, ".pdb") ? LoadPdbSymbols(symbolsPath, state.moduleSize) : LoadSymbols(symbolsPath, timestamp, state.moduleSize);
		if (loaded != SIMHOOK_OK)
		{
			state.symbols.clear();
			state.byName.clear();
			return loaded;
		}
	}
	const MH_STATUS status = MH_Initialize();
	if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED) return Fail(SIMHOOK_ENGINE_FAILED, MH_StatusToString(status));
	state.ready = true;
	state.lastError = "";
	return SIMHOOK_OK;
}

SIMHOOK_EXPORT int32_t simhook_symbol_count()
{
	return (int32_t)state.symbols.size();
}

SIMHOOK_EXPORT const char* simhook_symbol_name(int32_t index)
{
	return index >= 0 && index < (int32_t)state.symbols.size() ? state.symbols[index].name.c_str() : nullptr;
}

SIMHOOK_EXPORT void* simhook_resolve(const char* name)
{
	std::lock_guard<std::mutex> lock(state.mutex);
	void* address = nullptr;
	return state.ready && Resolve(name, &address) == SIMHOOK_OK ? address : nullptr;
}

// Installs a call counter on a simulation function. Returns the counter number (from 0) or a negative error code.
SIMHOOK_EXPORT int32_t simhook_probe_add(const char* name)
{
	std::lock_guard<std::mutex> lock(state.mutex);
	if (!state.ready) return SIMHOOK_NOT_READY;
	void* target = nullptr;
	const int32_t resolved = Resolve(name, &target);
	return resolved == SIMHOOK_OK ? AddProbeAt(target, name) : resolved;
}

// Same, by address: for engine checks on our own functions.
SIMHOOK_EXPORT int32_t simhook_probe_add_address(void* target, const char* label)
{
	std::lock_guard<std::mutex> lock(state.mutex);
	return state.ready ? AddProbeAt(target, label) : (int32_t)SIMHOOK_NOT_READY;
}

// Gives a name to a function outside SimDLL so a plugin can hook it by that name: for engine checks on our own
// functions (sim/hook/test/hooktest.cpp).
SIMHOOK_EXPORT int32_t simhook_symbol_add_address(const char* name, void* address)
{
	std::lock_guard<std::mutex> lock(state.mutex);
	if (!state.ready) return SIMHOOK_NOT_READY;
	state.ownSymbols[name] = address;
	return SIMHOOK_OK;
}

SIMHOOK_EXPORT int32_t simhook_probe_count()
{
	return (int32_t)state.probes.size();
}

SIMHOOK_EXPORT const char* simhook_probe_name(int32_t index)
{
	return index >= 0 && index < (int32_t)state.probes.size() ? state.probes[index].name.c_str() : nullptr;
}

SIMHOOK_EXPORT int64_t simhook_probe_calls(int32_t index)
{
	if (index < 0 || index >= (int32_t)state.probes.size()) return -1;
	int64_t calls;
	memcpy(&calls, state.probes[index].stub + 24, sizeof(calls));
	return calls;
}

// Removes all counters. A function that has nothing else on it is restored to its original form.
SIMHOOK_EXPORT int32_t simhook_probe_clear()
{
	std::lock_guard<std::mutex> lock(state.mutex);
	int32_t failed = 0;
	for (Probe& probe : state.probes)
	{
		Site* site = probe.link->site;
		RemoveLink(probe.link);
		delete probe.link;
		if (Rebuild(*site) != SIMHOOK_OK) failed++;
	}
	state.probes.clear();
	return failed;
}

SIMHOOK_EXPORT int32_t simhook_plugin_hook_count(const char* plugin)
{
	int32_t count = 0;
	for (SimHookEntry* entry = pluginHooks; entry; entry = entry->next) count += strcmp(entry->plugin, plugin) == 0;
	return count;
}

// Enables or disables all of a plugin's hooks: its links join or leave the chains of their functions.
// Returns the number of hooks or a negative error code; on error the hooks of this plugin already enabled
// are disabled again.
SIMHOOK_EXPORT int32_t simhook_plugin_enable(const char* plugin, int32_t enable)
{
	std::lock_guard<std::mutex> lock(state.mutex);
	if (!state.ready) return SIMHOOK_NOT_READY;
	// A plugin is written for a specific SimDLL build: with a different one its hooks would corrupt the simulation.
	for (SimHookBuild* build = pluginBuilds; build && enable; build = build->next)
	{
		if (strcmp(build->plugin, plugin) != 0 || (build->timestamp == state.moduleTimestamp && build->sizeOfImage == state.moduleSize)) continue;
		char message[256];
		snprintf(message, sizeof(message), "plugin %s is for SimDLL build timestamp=0x%08x size=0x%x, loaded timestamp=0x%08x size=0x%x",
			plugin, build->timestamp, build->sizeOfImage, state.moduleTimestamp, state.moduleSize);
		return Fail(SIMHOOK_WRONG_BUILD, message);
	}
	int32_t count = 0, error = SIMHOOK_OK;
	for (SimHookEntry* entry = pluginHooks; entry && error == SIMHOOK_OK; entry = entry->next)
	{
		if (strcmp(entry->plugin, plugin) != 0) continue;
		count++;
		Link*& link = state.pluginLinks[entry];
		if (!link)
		{
			void* target = nullptr;
			error = Resolve(entry->symbol, &target);
			if (error != SIMHOOK_OK)
			{
				state.pluginLinks.erase(entry);
				break;
			}
			entry->target = target;
			Site* site = SiteAt(target, entry->symbol);
			link = new Link{ site, entry->detour, entry->original, entry->order, entry->plugin, false };
			site->links.push_back(link);
		}
		if (link->enabled == (enable != 0)) continue;
		link->enabled = enable != 0;
		error = Rebuild(*link->site);
	}
	if (count == 0) return Fail(SIMHOOK_UNKNOWN_PLUGIN, std::string("no hooks registered for plugin: ") + plugin);
	if (error != SIMHOOK_OK)
	{
		// A plugin is enabled entirely or not at all: whatever got installed is removed.
		const std::string message = state.lastError;
		for (SimHookEntry* entry = pluginHooks; entry; entry = entry->next)
		{
			const auto found = strcmp(entry->plugin, plugin) == 0 ? state.pluginLinks.find(entry) : state.pluginLinks.end();
			if (found == state.pluginLinks.end() || !found->second->enabled) continue;
			found->second->enabled = false;
			Rebuild(*found->second->site);
		}
		return Fail(error, message);
	}
	return count;
}

// Disables the hooks of all plugins at once; counters stay.
SIMHOOK_EXPORT void simhook_disable_all()
{
	std::lock_guard<std::mutex> lock(state.mutex);
	if (!state.ready) return;
	for (const auto& entry : state.pluginLinks)
	{
		if (!entry.second->enabled) continue;
		entry.second->enabled = false;
		Rebuild(*entry.second->site);
	}
}
