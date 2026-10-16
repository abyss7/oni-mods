// Shared description of the game grid for the native kernels.
//
// The game stores the world as flat arrays with one entry per cell: cell (x, y) lives at index x + y * width.
// Some arrays belong to the native simulation (element index, mass, properties, backwall),
// some live in C# managed memory (world index, visibility, building flags, damage).
// C# passes pointers here without copying; managed arrays are pinned for the duration of the call.
// Kernels only read these arrays.
#pragma once

#include <stdint.h>

#if defined(_WIN32)
#define ONI_EXPORT extern "C" __declspec(dllexport)
#else
#define ONI_EXPORT extern "C" __attribute__((visibility("default")))
#endif

// Changes on any edit of the structures and signatures: C# compares the number and does not use the kernels on a mismatch.
#define ONI_NATIVE_ABI 5

// Layout must match the GridView struct in src/OniFixes/NativeLib.cs.
struct OniGridView
{
	int32_t width;
	int32_t height;
	int32_t activeWorld;                // index of the active world; -1 if there are no worlds (then there are no active cells)
	int32_t numElements;                // length of elementState
	const uint16_t* elementIdx;         // simulation: element index of the cell
	const float* mass;                  // simulation: cell mass, kg
	const uint8_t* properties;          // simulation: cell flags, bit 2 = impermeable to liquid
	const uint16_t* backwallElementIdx; // simulation: backwall element, 0xFFFF = none
	const uint8_t* worldIdx;            // C#: world index of the cell
	const uint8_t* visible;             // C#: cell visibility for fog of war, 0..255
	const uint8_t* buildMasks;          // C#: building flags, bit 1 = solid cell
	const float* damage;                // C#: cell damage, 0..1
	const uint8_t* elementState;        // C#: by element index, 0 vacuum, 1 gas, 2 liquid, 3 solid
};

enum OniElementState : uint8_t
{
	ONI_VACUUM = 0,
	ONI_GAS = 1,
	ONI_LIQUID = 2,
	ONI_SOLID = 3,
};

// Texture rectangle that the kernel fills. Grid point (x, y) is written to
// bytes[((x - originX) + (y - originY) * pageWidth) * bytesPerPixel].
// Layout must match the TextureTarget struct in src/OniFixes/NativeLib.cs.
struct OniTextureTarget
{
	uint8_t* bytes;
	int32_t byteCount;
	int32_t pageWidth;
	int32_t originX;
	int32_t originY;
	int32_t x0, y0, x1, y1; // cell range to fill, inclusive
};

enum OniTextureKind : int32_t
{
	ONI_TEX_FOG_OF_WAR = 1,           // 1 byte; param = the row from which visibility is taken for cells of other worlds
	ONI_TEX_SOLID_DIG_AMOUNT = 2,     // 3 bytes; param is unused
	ONI_TEX_SOLID_LIQUID_GAS_MASS = 3 // 4 bytes; param = 1 if impermeable cells are marked separately
};

enum OniResult : int32_t
{
	ONI_OK = 0,
	ONI_UNKNOWN_KIND = -1,
	ONI_OUT_OF_BOUNDS = -2, // range does not fit into the grid or the texture buffer: nothing written
};
