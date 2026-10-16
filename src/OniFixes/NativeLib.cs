using System;
using System.IO;
using System.Runtime.InteropServices;
using OniCommon;

namespace OniFixes
{
	/// <summary>
	/// Native library with C++ kernels (sources in native/, built by scripts/build-native.sh).
	/// It sits in the mod folder as oni_native.bin: the game loads every *.dll there as a managed assembly
	/// and on a native library refuses to load the whole mod.
	///
	/// Rules for everything that uses the library:
	/// - if it is missing, failed to load or was built for another version of the structures, Available == false
	///   and the game's original code runs: native kernels speed things up but must not break anything;
	/// - kernels only read grid data and write into the buffer passed to them;
	/// - managed arrays are pinned (fixed) exactly for the duration of the call.
	/// </summary>
	internal static unsafe class NativeLib
	{
		// Must match ONI_NATIVE_ABI in native/oni_grid.h.
		private const int ExpectedAbi = 5;

		/// <summary>Layout matches OniGridView in native/oni_grid.h.</summary>
		[StructLayout(LayoutKind.Sequential)]
		internal struct GridView
		{
			public int width;
			public int height;
			public int activeWorld;
			public int numElements;
			public ushort* elementIdx;
			public float* mass;
			public byte* properties;
			public ushort* backwallElementIdx;
			public byte* worldIdx;
			public byte* visible;
			public byte* buildMasks;
			public float* damage;
			public byte* elementState;
		}

		/// <summary>Layout matches OniTextureTarget in native/oni_grid.h.</summary>
		[StructLayout(LayoutKind.Sequential)]
		internal struct TextureTarget
		{
			public byte* bytes;
			public int byteCount;
			public int pageWidth;
			public int originX;
			public int originY;
			public int x0, y0, x1, y1;
		}

		internal const int TexFogOfWar = 1;
		internal const int TexSolidDigAmount = 2;
		internal const int TexSolidLiquidGasMass = 3;

		[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr LoadLibrary(string path);

		[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int AbiFn();

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr InfoFn();

		/// <returns>0 on success; a negative number if the kernel refused (nothing written).</returns>
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		internal delegate int TexFillFn(int kind, GridView* grid, TextureTarget* target, int param);

		internal static bool Available { get; private set; }
		internal static string Info { get; private set; } = "not loaded";
		internal static TexFillFn TexFill { get; private set; }

		private static bool tried;

		/// <summary>Loads the library once per game launch. Any failure leaves Available == false.</summary>
		internal static bool Load()
		{
			if (tried)
			{
				return Available;
			}
			tried = true;
			string path = Path.Combine(ModContext.Folder, "oni_native.bin");
			try
			{
				if (!File.Exists(path))
				{
					Info = "no oni_native.bin in the mod folder";
					return false;
				}
				IntPtr module = LoadLibrary(path);
				if (module == IntPtr.Zero)
				{
					Info = $"LoadLibrary failed, error {Marshal.GetLastWin32Error()}";
					return false;
				}
				int abi = Bind<AbiFn>(module, "oni_native_abi")();
				if (abi != ExpectedAbi)
				{
					Info = $"library abi {abi}, mod expects {ExpectedAbi}";
					return false;
				}
				TexFill = Bind<TexFillFn>(module, "oni_tex_fill");
				Info = Marshal.PtrToStringAnsi(Bind<InfoFn>(module, "oni_native_info")());
				Available = true;
			}
			catch (Exception e)
			{
				Info = "load failed: " + e.Message;
				Available = false;
			}
			return Available;
		}

		private static byte[] elementStates = new byte[0];
		private static object elementStatesSource;

		/// <summary>
		/// State (0 vacuum, 1 gas, 2 liquid, 3 solid) by element index: Element objects are not available to the kernels.
		/// The table is rebuilt when the game has reloaded the element list.
		/// </summary>
		internal static byte[] ElementStates()
		{
			var elements = ElementLoader.elements;
			if (!ReferenceEquals(elementStatesSource, elements) || elementStates.Length != elements.Count)
			{
				byte[] table = new byte[elements.Count];
				for (int i = 0; i < table.Length; i++)
				{
					table[i] = (byte)(elements[i].state & Element.State.Solid);
				}
				elementStates = table;
				elementStatesSource = elements;
			}
			return elementStates;
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
