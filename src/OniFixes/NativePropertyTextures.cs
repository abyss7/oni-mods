using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using Unity.Collections.LowLevel.Unsafe;
using OniCommon;

namespace OniFixes
{
	/// <summary>
	/// Four cell property textures that the game recomputes every frame (fog of war, digging,
	/// mass and mass for light) are filled by a native C++ kernel instead of C# loops.
	///
	/// The original cuts the visible area into strips of 16 rows, hands them out to worker threads and waits.
	/// The kernel walks the same area on the main thread without handing out: for a screen-sized area this is faster
	/// than the handing out itself. Locking the texture page and uploading it to the GPU stay stock.
	///
	/// The result must match the original byte for byte. If the library did not load, grid data
	/// is not there yet or the kernel refused the range, the texture is computed by the original code.
	/// </summary>
	internal static unsafe class NativePropertyTextures
	{
		private const int PropertyCount = (int)PropertyTextures.Property.Num;

		private sealed class Stat
		{
			public long calls, cells, nativeTicks, originalTicks, verified, comparedBytes, differentBytes, fallbacks;
			public int maxDifference;
		}

		private static readonly Stat[] stats = new Stat[PropertyCount];

		// Self-check: file verify_native_textures.txt in the mod folder. After the kernel, the game's original code
		// computes the same area (on the main thread), the results are compared byte by byte; the original stays in the texture.
		// Number in the file: compare every N-th call, so that the self-check barely distorts the frame time measurement.
		private static bool verify;
		private static int verifyEvery = 1;
		private static byte[] verifyCopy = new byte[0];

		internal static bool Applied { get; private set; }

		internal static void Apply(Harmony harmony)
		{
			if (!NativeLib.Load())
			{
				throw new InvalidOperationException("native library is not available: " + NativeLib.Info);
			}
			string verifyFile = Path.Combine(ModContext.Folder, "verify_native_textures.txt");
			verify = File.Exists(verifyFile);
			if (verify && int.TryParse(File.ReadAllText(verifyFile).Trim(), out int every) && every > 1)
			{
				verifyEvery = every;
			}
			PropertyTexturesPatch.Ensure(harmony);
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)),
				prefix: new HarmonyMethod(typeof(NativePropertyTextures), nameof(LogStats)));
			Applied = true;
			Debug.Log($"[OniFixes] NativePropertyTextures: {NativeLib.Info}{(verify ? $", verify every {verifyEvery} call(s)" : "")}");
		}

		private static int KindOf(PropertyTextures.Property property)
		{
			switch (property)
			{
				case PropertyTextures.Property.FogOfWar: return NativeLib.TexFogOfWar;
				case PropertyTextures.Property.SolidDigAmount: return NativeLib.TexSolidDigAmount;
				case PropertyTextures.Property.SolidLiquidGasMass:
				case PropertyTextures.Property.SolidLiquidGasMassForLight: return NativeLib.TexSolidLiquidGasMass;
				default: return 0;
			}
		}

		/// <returns>true if the texture must be computed by the game's original code.</returns>
		internal static bool UpdateProperty(PropertyTextures instance, ref PropertyTextures.TextureProperties p, int x0, int y0, int x1, int y1)
		{
			int kind = p.updatedExternally ? 0 : KindOf(p.simProperty);
			if (kind == 0 || !AbTest.IsOn(nameof(NativePropertyTextures)))
			{
				return true;
			}
			if (Game.Instance == null || Game.Instance.IsLoading())
			{
				return false; // the original does nothing in this case either
			}
			if (Grid.elementIdx == null || Grid.mass == null || Grid.properties == null || BackwallManager.element_idx == null
				|| Grid.WorldIdx == null || Grid.Visible == null || Grid.BuildMasks == null || Grid.Damage == null)
			{
				return true;
			}
			int cells = Grid.CellCount;
			if (Grid.WorldIdx.Length < cells || Grid.Visible.Length < cells || Grid.BuildMasks.Length < cells || Grid.Damage.Length < cells)
			{
				return true;
			}
			byte[] elementState = NativeLib.ElementStates();

			int idx = (int)p.simProperty;
			Stat stat = stats[idx] ?? (stats[idx] = new Stat());
			TextureRegion region = instance.textureBuffers[idx].Lock(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
			int param = 0;
			NativeLib.GridView view = default;
			view.width = Grid.WidthInCells;
			view.height = Grid.HeightInCells;
			view.activeWorld = -1;
			view.numElements = elementState.Length;
			view.elementIdx = Grid.elementIdx;
			view.mass = Grid.mass;
			view.properties = Grid.properties;
			view.backwallElementIdx = BackwallManager.element_idx;
			view.shown = p.simProperty == PropertyTextures.Property.FogOfWar ? NativeLib.BlackoutShown() : null;
			if (ClusterManager.Instance != null)
			{
				view.activeWorld = ClusterManager.Instance.activeWorldId;
			}
			switch (p.simProperty)
			{
				case PropertyTextures.Property.FogOfWar:
					// Row from which visibility of cells of other worlds is taken: the top of the active world.
					param = Grid.HeightInCells;
					if (ClusterManager.Instance != null)
					{
						WorldContainer world = ClusterManager.Instance.activeWorld;
						param = world.WorldSize.y + world.WorldOffset.y - 1;
					}
					break;
				case PropertyTextures.Property.SolidLiquidGasMass:
					param = 1; // cells impermeable to liquid are marked separately
					break;
			}

			NativeLib.TextureTarget target = default;
			target.bytes = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(region.bytes);
			target.byteCount = region.bytes.Length;
			target.pageWidth = region.pageWidth;
			target.originX = region.x;
			target.originY = region.y;
			target.x0 = x0;
			target.y0 = y0;
			target.x1 = x1;
			target.y1 = y1;

			int result;
			long start = Stopwatch.GetTimestamp();
			fixed (byte* worldIdx = Grid.WorldIdx, visible = Grid.Visible, states = elementState)
			fixed (Grid.BuildFlags* masks = Grid.BuildMasks)
			fixed (float* damage = Grid.Damage)
			{
				view.worldIdx = worldIdx;
				view.visible = visible;
				view.buildMasks = (byte*)masks;
				view.damage = damage;
				view.elementState = states;
				result = NativeLib.TexFill(kind, &view, &target, param);
			}
			stat.nativeTicks += Stopwatch.GetTimestamp() - start;
			stat.calls++;
			stat.cells += (long)(x1 - x0 + 1) * (y1 - y0 + 1);

			if (result != 0)
			{
				// The kernel wrote nothing: the original fills the page.
				stat.fallbacks++;
				FillOriginal(p.simProperty, region, x0, y0, x1, y1);
			}
			else if (verify && stat.calls % verifyEvery == 0)
			{
				stat.verified++;
				Verify(stat, p.simProperty, region, ref target, x0, y0, x1, y1);
			}
			region.Unlock();
			return false;
		}

		private static void FillOriginal(PropertyTextures.Property property, TextureRegion region, int x0, int y0, int x1, int y1)
		{
			switch (property)
			{
				case PropertyTextures.Property.FogOfWar:
					PropertyTextures.UpdateFogOfWar(region, x0, y0, x1, y1);
					break;
				case PropertyTextures.Property.SolidDigAmount:
					PropertyTextures.UpdateSolidDigAmount(region, x0, y0, x1, y1);
					break;
				case PropertyTextures.Property.SolidLiquidGasMass:
					PropertyTextures.UpdateSolidLiquidGasMass(region, x0, y0, x1, y1);
					break;
				case PropertyTextures.Property.SolidLiquidGasMassForLight:
					PropertyTextures.UpdateSolidLiquidGasMassForLight(region, x0, y0, x1, y1);
					break;
			}
		}

		private static void Verify(Stat stat, PropertyTextures.Property property, TextureRegion region, ref NativeLib.TextureTarget target, int x0, int y0, int x1, int y1)
		{
			int rowBytes = (x1 - x0 + 1) * region.bytesPerPixel;
			int rows = y1 - y0 + 1;
			if (verifyCopy.Length < rowBytes * rows)
			{
				verifyCopy = new byte[rowBytes * rows];
			}
			int stride = target.pageWidth * region.bytesPerPixel;
			byte* first = target.bytes + ((x0 - target.originX) + (y0 - target.originY) * target.pageWidth) * region.bytesPerPixel;
			fixed (byte* copy = verifyCopy)
			{
				for (int row = 0; row < rows; row++)
				{
					Buffer.MemoryCopy(first + row * stride, copy + row * rowBytes, rowBytes, rowBytes);
				}
				long start = Stopwatch.GetTimestamp();
				FillOriginal(property, region, x0, y0, x1, y1);
				stat.originalTicks += Stopwatch.GetTimestamp() - start;
				for (int row = 0; row < rows; row++)
				{
					byte* original = first + row * stride;
					byte* native = copy + row * rowBytes;
					for (int i = 0; i < rowBytes; i++)
					{
						int difference = Math.Abs(original[i] - native[i]);
						if (difference != 0)
						{
							stat.differentBytes++;
							if (difference > stat.maxDifference)
							{
								stat.maxDifference = difference;
							}
						}
					}
				}
			}
			stat.comparedBytes += (long)rowBytes * rows;
		}

		private static void LogStats()
		{
			double usPerTick = 1e6 / Stopwatch.Frequency;
			for (int i = 0; i < PropertyCount; i++)
			{
				Stat s = stats[i];
				if (s == null || s.calls == 0)
				{
					continue;
				}
				string line = $"[OniFixes] NativePropertyTextures: {(PropertyTextures.Property)i}: calls={s.calls}, cells per call={s.cells / s.calls}, native {s.nativeTicks * usPerTick / s.calls:F1} us per call, fallbacks={s.fallbacks}";
				if (verify)
				{
					line += $"; verify: calls checked={s.verified}, original {s.originalTicks * usPerTick / Math.Max(1, s.verified):F1} us per call (single thread), bytes compared={s.comparedBytes}, different={s.differentBytes}, max difference={s.maxDifference}";
				}
				Debug.Log(line);
			}
		}
	}
}
