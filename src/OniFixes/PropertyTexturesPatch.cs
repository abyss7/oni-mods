using HarmonyLib;

namespace OniFixes
{
	/// <summary>
	/// Single hook point for PropertyTextures.UpdateProperty for all texture fixes:
	/// first we decide whether a recompute is needed at all, then how to do it (native kernel, in the background or stock).
	/// </summary>
	internal static class PropertyTexturesPatch
	{
		private static bool installed;

		internal static void Ensure(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			installed = true;
			harmony.Patch(AccessTools.Method(typeof(PropertyTextures), nameof(PropertyTextures.UpdateProperty)),
				prefix: new HarmonyMethod(typeof(PropertyTexturesPatch), nameof(UpdatePropertyPrefix)));
		}

		private static bool UpdatePropertyPrefix(PropertyTextures __instance, ref PropertyTextures.TextureProperties p, int x0, int y0, int x1, int y1)
		{
			if (ThrottlePropertyTextures.Applied && ThrottlePropertyTextures.ShouldSkip(ref p, x0, y0, x1, y1))
			{
				return false;
			}
			if (NativePropertyTextures.Applied && !NativePropertyTextures.UpdateProperty(__instance, ref p, x0, y0, x1, y1))
			{
				return false;
			}
			if (AsyncPropertyTextures.Applied)
			{
				return AsyncPropertyTextures.UpdateProperty(__instance, ref p, x0, y0, x1, y1);
			}
			return true;
		}
	}
}
