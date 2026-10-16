using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OniProfiler
{
	/// <summary>
	/// Experiment: can the mod compute over simulation data with native C++ code.
	///
	/// If oni_native.bin lies next to the mod (built by scripts/build-native.sh), it is loaded,
	/// gets pointers to the grid arrays the simulation shares with the game, and computes a summary over them.
	/// The same is computed by a C# loop; results and timings are compared and written to the report once per launch.
	/// </summary>
	internal static unsafe class NativeProbe
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct GridStats
		{
			public double totalMass;
			public double heat;
			public float maxTemperature;
			public float minTemperature;
			public int hotCells;
			public int nonEmptyCells;
		}

		[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr LoadLibrary(string path);

		[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int AbiFn();

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr InfoFn();

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate void StatsFn(float* mass, float* temperature, int count, float hotThreshold, GridStats* result);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int CountFn(ushort* elementIdx, int count, ushort element);

		private const float HotThreshold = 350f; // about 77 degrees Celsius
		private const int Repeats = 200;

		private static string libraryPath;
		private static bool done;

		internal static void Init(string modPath)
		{
			// The extension is not .dll: the game loads every *.dll in the mod folder as a managed assembly.
			libraryPath = Path.Combine(modPath, "oni_native.bin");
		}

		internal static void AppendOnce(StringBuilder sb)
		{
			if (done || libraryPath == null || !File.Exists(libraryPath))
			{
				return;
			}
			done = true;
			try
			{
				Run(sb);
			}
			catch (Exception e)
			{
				sb.AppendLine();
				sb.AppendLine("-- native probe failed: " + e);
			}
		}

		private static T Bind<T>(IntPtr module, string name) where T : Delegate
		{
			IntPtr address = GetProcAddress(module, name);
			if (address == IntPtr.Zero)
			{
				throw new EntryPointNotFoundException(name);
			}
			return Marshal.GetDelegateForFunctionPointer<T>(address);
		}

		private static void Run(StringBuilder sb)
		{
			float* mass = Grid.mass;
			float* temperature = Grid.temperature;
			ushort* elementIdx = Grid.elementIdx;
			int count = Grid.CellCount;
			if (mass == null || temperature == null || elementIdx == null || count <= 0)
			{
				done = false; // the simulation has not handed over its data yet, try again in the next report
				return;
			}
			IntPtr module = LoadLibrary(libraryPath);
			if (module == IntPtr.Zero)
			{
				throw new DllNotFoundException($"{libraryPath}, error {Marshal.GetLastWin32Error()}");
			}
			int abi = Bind<AbiFn>(module, "oni_native_abi")();
			string info = Marshal.PtrToStringAnsi(Bind<InfoFn>(module, "oni_native_info")());
			StatsFn scalar = Bind<StatsFn>(module, "oni_grid_stats_scalar");
			StatsFn avx = Bind<StatsFn>(module, "oni_grid_stats_avx");
			CountFn countElement = Bind<CountFn>(module, "oni_count_element");
			ushort oxygen = ElementLoader.GetElementIndex(SimHashes.Oxygen);

			GridStats managed = default, nativeScalar = default, nativeAvx = default;
			int managedOxygen = 0, nativeOxygen = 0;
			double tick = 1e6 / Stopwatch.Frequency;

			long t0 = Stopwatch.GetTimestamp();
			for (int r = 0; r < Repeats; r++)
			{
				managed = ManagedStats(mass, temperature, count);
			}
			long t1 = Stopwatch.GetTimestamp();
			for (int r = 0; r < Repeats; r++)
			{
				scalar(mass, temperature, count, HotThreshold, &nativeScalar);
			}
			long t2 = Stopwatch.GetTimestamp();
			for (int r = 0; r < Repeats; r++)
			{
				avx(mass, temperature, count, HotThreshold, &nativeAvx);
			}
			long t3 = Stopwatch.GetTimestamp();
			for (int r = 0; r < Repeats; r++)
			{
				managedOxygen = ManagedCount(elementIdx, count, oxygen);
			}
			long t4 = Stopwatch.GetTimestamp();
			for (int r = 0; r < Repeats; r++)
			{
				nativeOxygen = countElement(elementIdx, count, oxygen);
			}
			long t5 = Stopwatch.GetTimestamp();

			bool same = Close(managed.totalMass, nativeAvx.totalMass) && Close(managed.heat, nativeAvx.heat)
				&& managed.maxTemperature == nativeAvx.maxTemperature && managed.minTemperature == nativeAvx.minTemperature
				&& managed.hotCells == nativeAvx.hotCells && managed.nonEmptyCells == nativeAvx.nonEmptyCells
				&& Close(managed.totalMass, nativeScalar.totalMass) && managed.hotCells == nativeScalar.hotCells
				&& managedOxygen == nativeOxygen;

			sb.AppendLine();
			sb.AppendLine("-- native probe (C++ reads the simulation arrays directly)");
			sb.AppendLine($"library: {info}, abi={abi}, cells={count}");
			sb.AppendLine($"grid: mass={nativeAvx.totalMass / 1000.0:F1} t, mean temperature={nativeAvx.heat / nativeAvx.totalMass - 273.15:F1} C, " +
				$"min={nativeAvx.minTemperature - 273.15f:F1} C, max={nativeAvx.maxTemperature - 273.15f:F1} C, " +
				$"non-empty cells={nativeAvx.nonEmptyCells}, hotter than {HotThreshold - 273.15f:F0} C={nativeAvx.hotCells}, oxygen cells={nativeOxygen}");
			sb.AppendLine($"grid stats per call: C# {(t1 - t0) * tick / Repeats:F0} us, C++ scalar {(t2 - t1) * tick / Repeats:F0} us, C++ AVX {(t3 - t2) * tick / Repeats:F0} us");
			sb.AppendLine($"element count per call: C# {(t4 - t3) * tick / Repeats:F0} us, C++ SSE2 {(t5 - t4) * tick / Repeats:F0} us");
			sb.AppendLine(same ? "results: C# and C++ agree" : $"results: MISMATCH, C# mass={managed.totalMass} hot={managed.hotCells} oxygen={managedOxygen}");
			Debug.Log($"[OniProfiler] native probe: {info}; agree={same}; stats C# {(t1 - t0) * tick / Repeats:F0} us vs C++ AVX {(t3 - t2) * tick / Repeats:F0} us");
		}

		private static bool Close(double a, double b)
		{
			return Math.Abs(a - b) <= 1e-6 * Math.Max(1.0, Math.Abs(a));
		}

		private static GridStats ManagedStats(float* mass, float* temperature, int count)
		{
			GridStats stats = new GridStats { maxTemperature = -1e30f, minTemperature = 1e30f };
			for (int i = 0; i < count; i++)
			{
				float m = mass[i];
				float t = temperature[i];
				stats.totalMass += m;
				stats.heat += (double)m * t;
				if (m > 0f)
				{
					stats.nonEmptyCells++;
					if (t > stats.maxTemperature)
					{
						stats.maxTemperature = t;
					}
					if (t < stats.minTemperature)
					{
						stats.minTemperature = t;
					}
					if (t > HotThreshold)
					{
						stats.hotCells++;
					}
				}
			}
			return stats;
		}

		private static int ManagedCount(ushort* elementIdx, int count, ushort element)
		{
			int result = 0;
			for (int i = 0; i < count; i++)
			{
				if (elementIdx[i] == element)
				{
					result++;
				}
			}
			return result;
		}
	}
}
