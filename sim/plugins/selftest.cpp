// Plugin for testing the engine (sim/hook/test/hooktest.cpp): hooks an exported SimDLL function
// that the test does not call, and a function of the test itself. Never enabled in the game.
#include <windows.h>

#include "simhook_plugin.h"

SIM_PLUGIN_MODULE();

SIM_HOOK("selftest", void, "kprofiler_ping", ProfilerPing, ())
{
	ProfilerPing_original();
}

// A plugin written "for another build" of SimDLL: the engine must refuse to enable it.
SIM_PLUGIN_BUILD("selftest-other-build", 1, 1);
SIM_HOOK("selftest-other-build", void, "kprofiler_ping", ProfilerPingOtherBuild, ())
{
	ProfilerPingOtherBuild_original();
}

// Chain: several plugins on one function. The test function returns its parameter; each link
// appends its digit to it and calls the next one, so the answer shows the order of the links. After the call
// each link also counts itself, so the compiler does not turn the call into a jump: otherwise the return address
// of the next link would remain the address of the test code, and the SIMHOOK_DIRECT requirement would check nothing.
SIM_COUNTER(chainCalls, "selftest.chain-calls");

#define CHAIN_LINK(plugin, order, ident, digit) \
	SIM_HOOK_ORDERED(plugin, order, int, "hooktest.chain", ident, (int value)) \
	{ \
		const int result = ident##_original(value * 10 + digit); \
		chainCalls.fetch_add(1, std::memory_order_relaxed); \
		return result; \
	}
CHAIN_LINK("selftest-a", 0, ChainA, 1)
CHAIN_LINK("selftest-b", 0, ChainB, 2)
CHAIN_LINK("selftest-early", -10, ChainEarly, 3)

// A link that needs the caller's address: digit 9 if it was called from outside this library, otherwise 8.
static int DirectDigit(void* caller)
{
	HMODULE from = nullptr, self = nullptr;
	GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)caller, &from);
	GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)DirectDigit, &self);
	return from != self ? 9 : 8;
}
SIM_HOOK_ORDERED("selftest-direct", SIMHOOK_DIRECT, int, "hooktest.chain", ChainDirect, (int value))
{
	const int result = ChainDirect_original(value * 10 + DirectDigit(__builtin_return_address(0)));
	chainCalls.fetch_add(1, std::memory_order_relaxed);
	return result;
}
SIM_HOOK_ORDERED("selftest-direct-too", SIMHOOK_DIRECT, int, "hooktest.chain", ChainDirectToo, (int value))
{
	const int result = ChainDirectToo_original(value * 10 + 7);
	chainCalls.fetch_add(1, std::memory_order_relaxed);
	return result;
}

// Calling its own "original" outside a hook: for a disabled link it leads past the others straight into the function.
SIM_DATA(ChainCall, "selftest.call-original-of-a")
{
	if (size != (int32_t)sizeof(int32_t) || !ChainA_original) return -1;
	int32_t* value = (int32_t*)data;
	*value = ChainA_original(*value);
	return 0;
}

// Service for testing the engine: another plugin (or the test) finds it by name and calls it directly.
static int32_t AddForTest(int32_t a, int32_t b) { return a + b; }
SIM_SERVICE(AddForTest, "selftest.add", AddForTest);
