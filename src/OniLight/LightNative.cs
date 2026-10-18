using System;
using System.IO;
using System.Runtime.InteropServices;
using OniCommon;

namespace OniLight
{
	/// <summary>
	/// The light engine (sources in native/oni_light.h and native/oni_light.cpp, built by scripts/build-native.sh). It sits
	/// in the mod folder as oni_light.bin: the game loads every *.dll there as a managed assembly and on a native library
	/// refuses to load the whole mod. If it is missing, failed to load or was built for other structures, Available is false
	/// and the game's own light stays.
	/// </summary>
	internal static unsafe class LightNative
	{
		// Must match ONI_LIGHT_ABI in native/oni_light.cpp.
		private const int ExpectedAbi = 3;

		/// <summary>Layout matches onilight::ElementOptics.</summary>
		[StructLayout(LayoutKind.Sequential)]
		internal struct ElementOptics
		{
			public float reflectance;
			public float absorb;
			public byte state;
			public byte pad0, pad1, pad2;
			public float molarRatio;
		}

		/// <summary>Layout matches onilight::GridView.</summary>
		[StructLayout(LayoutKind.Sequential)]
		internal struct GridView
		{
			public int width;
			public int height;
			public ushort* elementIdx;
			public float* mass;
			public byte* properties;
			public ushort* backwallElementIdx;
			public byte* buildMasks;
			public ushort* builtBackwall;
			public int* lightCount;
			public float* temperature;
			public float* mixThrough;
		}

		/// <summary>Layout matches onilight::SourceDesc.</summary>
		[StructLayout(LayoutKind.Sequential)]
		internal struct SourceDesc
		{
			public int x, y;
			public float lux, range, falloff;
			public int shape, direction, width;
		}

		[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr LoadLibrary(string path);

		[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AbiFn();
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr InfoFn();
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void InitFn(int width, int height);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ElementsFn(ElementOptics* table, int count);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AddFn(GridView* grid, int id, SourceDesc* source);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void RemoveFn(GridView* grid, int id, int keep);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeFn(int id);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PreviewFn(GridView* grid, SourceDesc* source, int* cells, int* lux, int capacity);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CellFn(int cell, float* out3);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CheckFn(GridView* grid, int x0, int y0, int x1, int y1, float* out6);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WetFn(int id);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void StatsFn(long* out7);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReachFn(float range);

		internal static bool Available { get; private set; }
		internal static string Info { get; private set; } = "not loaded";
		internal static InitFn Init;
		internal static ElementsFn Elements;
		internal static AddFn Add;
		internal static RemoveFn Remove;
		internal static FreeFn Free;
		internal static PreviewFn Preview;
		internal static CellFn Cell;
		internal static CheckFn Check;
		internal static StatsFn Stats;
		internal static ReachFn Reach;
		internal static WetFn Wet;

		internal static bool Load()
		{
			string path = Path.Combine(ModContext.Folder, "oni_light.bin");
			try
			{
				if (!File.Exists(path))
				{
					Info = "no oni_light.bin in the mod folder";
					return false;
				}
				IntPtr module = LoadLibrary(path);
				if (module == IntPtr.Zero)
				{
					Info = $"LoadLibrary failed, error {Marshal.GetLastWin32Error()}";
					return false;
				}
				int abi = Bind<AbiFn>(module, "oni_light_abi")();
				if (abi != ExpectedAbi)
				{
					Info = $"library abi {abi}, mod expects {ExpectedAbi}";
					return false;
				}
				Init = Bind<InitFn>(module, "oni_light_init");
				Elements = Bind<ElementsFn>(module, "oni_light_elements");
				Add = Bind<AddFn>(module, "oni_light_add");
				Remove = Bind<RemoveFn>(module, "oni_light_remove");
				Free = Bind<FreeFn>(module, "oni_light_free");
				Preview = Bind<PreviewFn>(module, "oni_light_preview");
				Cell = Bind<CellFn>(module, "oni_light_cell");
				Check = Bind<CheckFn>(module, "oni_light_check");
				Stats = Bind<StatsFn>(module, "oni_light_stats");
				Reach = Bind<ReachFn>(module, "oni_light_reach");
				Wet = Bind<WetFn>(module, "oni_light_wet");
				Info = Marshal.PtrToStringAnsi(Bind<InfoFn>(module, "oni_light_info")()) + "; this processor: " +
					Marshal.PtrToStringAnsi(Bind<InfoFn>(module, "oni_light_variant")());
				Available = true;
			}
			catch (Exception e)
			{
				Info = "load failed: " + e.Message;
				Available = false;
			}
			return Available;
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
	}
}
