// Filling of cell property textures: native copies of the loops from the game's PropertyTextures
// (UpdateFogOfWar, UpdateSolidDigAmount, UpdateSolidLiquidGasMass). The result must match
// the original byte for byte: the C# side can compare it with the original (verify_native_textures.txt).

#include <immintrin.h>
#include <string.h>

#include "oni_grid.h"

static inline uint8_t ToByte(float v)
{
	// Like a (byte) cast in C#: drops the fractional part.
	return (uint8_t)(int32_t)v;
}

// The loops are written without branches on cell data: choices are made with masks and tables.
// This way the time does not depend on how varied the picture on screen is, and the compiler can vectorize the simple parts.

static void FillFogOfWar(const OniGridView g, const OniTextureTarget t, int32_t clampRow)
{
	// A cell of another world shows the visibility of the given row of the active world in the same column;
	// if there is no such row in the grid, it shows 0.
	const bool clampValid = clampRow >= 0 && clampRow < g.height;
	const uint8_t* clamp = clampValid ? g.visible + clampRow * g.width : nullptr;
	const bool anyActive = g.activeWorld >= 0 && g.activeWorld <= 255;
	const uint8_t active = (uint8_t)g.activeWorld;
	const __m128i activeV = _mm_set1_epi8((char)active);
	const __m128i anyActiveV = _mm_set1_epi8(anyActive ? -1 : 0);
	for (int32_t y = t.y0; y <= t.y1; y++)
	{
		uint8_t* out = t.bytes + ((t.x0 - t.originX) + (y - t.originY) * t.pageWidth);
		const uint8_t* world = g.worldIdx + y * g.width;
		const uint8_t* visible = g.visible + y * g.width;
		int32_t x = t.x0;
		// 16 cells at a time: the "cell of own world" mask chooses between its own visibility and the visibility of the reference row.
		for (; x + 15 <= t.x1; x += 16, out += 16)
		{
			const __m128i own = _mm_and_si128(_mm_cmpeq_epi8(_mm_loadu_si128((const __m128i*)(world + x)), activeV), anyActiveV);
			const __m128i other = clampValid ? _mm_loadu_si128((const __m128i*)(clamp + x)) : _mm_setzero_si128();
			_mm_storeu_si128((__m128i*)out, _mm_blendv_epi8(other, _mm_loadu_si128((const __m128i*)(visible + x)), own));
		}
		for (; x <= t.x1; x++)
		{
			const uint8_t other = clampValid ? clamp[x] : 0;
			*out++ = (anyActive && world[x] == active) ? visible[x] : other;
		}
	}
}

static void FillSolidDigAmount(const OniGridView g, const OniTextureTarget t)
{
	for (int32_t y = t.y0; y <= t.y1; y++)
	{
		uint8_t* out = t.bytes + ((t.x0 - t.originX) + (y - t.originY) * t.pageWidth) * 3;
		const int32_t row = y * g.width;
		for (int32_t x = t.x0; x <= t.x1; x++, out += 3)
		{
			const int32_t cell = row + x;
			const uint8_t solid = (uint8_t)-(g.buildMasks[cell] & 1); // 0 or 255
			const uint16_t wall = g.backwallElementIdx[cell];
			// Index 0xFFFF means "no backwall"; it is certainly not less than the number of elements.
			const uint8_t wallState = wall < g.numElements ? g.elementState[wall] : ONI_VACUUM;
			out[0] = solid;
			out[1] = ToByte(255.0f * g.damage[cell]) & solid;
			out[2] = (uint8_t)-(wallState == ONI_SOLID);
		}
	}
}

// Share of the cell mass out of 2000 kg as a byte, for count consecutive cells. Any non-zero mass gives at least 1.
// Moved out of the main loop because the division is the most expensive thing in it, and separately it is computed 8 cells at once.
static void MassAmountBytes(const float* mass, uint8_t* out, int32_t count)
{
	const float Least = 0.003921569f; // 1/255, as written in the game
	int32_t i = 0;
	const __m256 full = _mm256_set1_ps(2000.0f), one = _mm256_set1_ps(1.0f), least = _mm256_set1_ps(Least), scale = _mm256_set1_ps(255.0f);
	for (; i + 8 <= count; i += 8)
	{
		const __m256 m = _mm256_loadu_ps(mass + i);
		// The operand order in min and max repeats Mathf.Min(1f, x) and Mathf.Max(least, x) from C#.
		const __m256 amount = _mm256_min_ps(one, _mm256_div_ps(m, full));
		const __m256 visible = _mm256_blendv_ps(amount, _mm256_max_ps(least, amount), _mm256_cmp_ps(m, _mm256_setzero_ps(), _CMP_GT_OQ));
		const __m256i bytes32 = _mm256_cvttps_epi32(_mm256_mul_ps(visible, scale));
		// AVX without AVX2: there are no 256-bit integer operations, so we pack in halves.
		const __m128i bytes16 = _mm_packus_epi32(_mm256_castsi256_si128(bytes32), _mm256_extractf128_si256(bytes32, 1));
		_mm_storel_epi64((__m128i*)(out + i), _mm_packus_epi16(bytes16, bytes16));
	}
	for (; i < count; i++)
	{
		const float m = mass[i];
		float amount = m / 2000.0f;
		amount = 1.0f < amount ? 1.0f : amount;
		if (m > 0.0f)
		{
			amount = Least > amount ? Least : amount;
		}
		out[i] = ToByte(amount * 255.0f);
	}
}

static void FillSolidLiquidGasMass(const OniGridView g, const OniTextureTarget t, bool markImpermeable)
{
	// The three low bytes of the pixel by element state (0..3) and the impermeability flag (+4):
	// byte 0 solid, byte 1 liquid, byte 2 gas or vacuum.
	static const uint32_t Colours[8] = {
		0x00FF0000, // vacuum
		0x00FF0000, // gas
		0x0000FF00, // liquid
		0x000000FF, // solid
		0x000000E6, // vacuum behind an impermeable cell: 230
		0x000000E6, // gas behind an impermeable cell: 230
		0x000000FA, // liquid behind an impermeable cell: 250
		0x000000FA, // solid impermeable cell: 250
	};
	enum { Chunk = 256 };
	uint8_t amounts[Chunk];
	const uint8_t impermeableBit = markImpermeable ? 2 : 0;
	const bool anyActive = g.activeWorld >= 0 && g.activeWorld <= 255;
	const uint8_t active = (uint8_t)g.activeWorld;
	for (int32_t y = t.y0; y <= t.y1; y++)
	{
		uint8_t* out = t.bytes + ((t.x0 - t.originX) + (y - t.originY) * t.pageWidth) * 4;
		const int32_t row = y * g.width;
		for (int32_t start = t.x0; start <= t.x1; start += Chunk)
		{
			const int32_t count = (t.x1 - start + 1) < Chunk ? (t.x1 - start + 1) : Chunk;
			MassAmountBytes(g.mass + row + start, amounts, count);
			for (int32_t i = 0; i < count; i++, out += 4)
			{
				const int32_t cell = row + start + i;
				const uint16_t element = g.elementIdx[cell];
				const uint32_t state = element < g.numElements ? g.elementState[element] : ONI_VACUUM;
				const uint32_t impermeable = (g.properties[cell] & impermeableBit) << 1; // 0 or 4
				const uint32_t pixel = Colours[state | impermeable] | ((uint32_t)amounts[i] << 24);
				const uint32_t own = (uint32_t)-(int32_t)(anyActive && g.worldIdx[cell] == active); // cells of other worlds are zero
				// Pixel bytes go from low to high, the way x86 stores them, so the pixel is written with a single store.
				const uint32_t value = pixel & own;
				memcpy(out, &value, 4);
			}
		}
	}
}

ONI_EXPORT int32_t oni_tex_fill(int32_t kind, const OniGridView* grid, const OniTextureTarget* target, int32_t param)
{
	// Copies on the stack, not references: the texture is written through uint8_t*, which by the language rules may
	// point to anything, and with references the compiler would re-read the loop bounds on every cell.
	const OniGridView g = *grid;
	const OniTextureTarget t = *target;
	int32_t bytesPerPixel;
	switch (kind)
	{
	case ONI_TEX_FOG_OF_WAR: bytesPerPixel = 1; break;
	case ONI_TEX_SOLID_DIG_AMOUNT: bytesPerPixel = 3; break;
	case ONI_TEX_SOLID_LIQUID_GAS_MASS: bytesPerPixel = 4; break;
	default: return ONI_UNKNOWN_KIND;
	}
	// An error in the bounds here means writing into foreign memory and a silent game crash, so we check explicitly.
	if (t.x0 < 0 || t.y0 < 0 || t.x1 >= g.width || t.y1 >= g.height || t.x0 > t.x1 || t.y0 > t.y1
		|| t.x0 < t.originX || t.y0 < t.originY || t.x1 - t.originX >= t.pageWidth)
	{
		return ONI_OUT_OF_BOUNDS;
	}
	const int64_t lastByte = ((int64_t)(t.x1 - t.originX) + (int64_t)(t.y1 - t.originY) * t.pageWidth + 1) * bytesPerPixel;
	if (lastByte > t.byteCount)
	{
		return ONI_OUT_OF_BOUNDS;
	}
	switch (kind)
	{
	case ONI_TEX_FOG_OF_WAR: FillFogOfWar(g, t, param); break;
	case ONI_TEX_SOLID_DIG_AMOUNT: FillSolidDigAmount(g, t); break;
	case ONI_TEX_SOLID_LIQUID_GAS_MASS: FillSolidLiquidGasMass(g, t, param != 0); break;
	}
	return ONI_OK;
}
