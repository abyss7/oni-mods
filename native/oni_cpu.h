// What the processor can do, for choosing a variant of a kernel at run time.
//
// Our native libraries are built for any x86-64 processor (SSE2). A kernel where the time goes gets a second variant
// built for AVX (a function with __attribute__((target("avx")))), and the library calls it only if HasAvx(); both
// variants must compute the same result (no fused multiply-add: the libraries are built with -ffp-contract=off), and the
// self-checks compare them. Never build a whole library with -mavx: on a processor without AVX it would stop.
#pragma once

#include <cpuid.h>

#define ONI_INLINE __attribute__((always_inline)) inline

// Whether the processor has AVX and whether the system saves its registers on thread switches. Cached without a lock
// (two threads asking at once both compute the same answer), so the library needs no C++ runtime for it.
inline bool OniHasAvx()
{
	static int cached = -1;
	if (cached < 0)
	{
		unsigned int a = 0, b = 0, c = 0, d = 0;
		int has = 0;
		if (__get_cpuid(1, &a, &b, &c, &d) && (c & (1u << 27)) != 0 && (c & (1u << 28)) != 0) // OSXSAVE and AVX
		{
			unsigned int low = 0, high = 0;
			__asm__("xgetbv" : "=a"(low), "=d"(high) : "c"(0));
			has = (low & 6) == 6; // XMM and YMM registers
		}
		cached = has;
	}
	return cached != 0;
}
