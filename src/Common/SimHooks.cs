using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace OniCommon
{
	/// <summary>
	/// Hooks on functions of the native simulation (SimDLL) from a mod. The hooking itself is done by the engine oni_simhook.bin
	/// (sources and rules in sim/, build with sim/build.sh); here is its loading and access to it from C#.
	///
	/// Two kinds of hooks:
	/// - a call counter on any simulation function by name (AddProbe): changes nothing, good for exploration;
	/// - a plugin (LoadPlugin, EnablePlugin): our native version of a function in a separate library from sim/plugins.
	///   A mod carries only its own plugins.
	///
	/// There is one engine in the game process for all our mods. Its library ships in every mod that needs
	/// hooks, but it is loaded by whoever asks first; the others find the already loaded one by file name.
	/// Two engine instances would install hooks on the same functions on top of each other.
	///
	/// Hooks are installed when the mod loads, while the simulation is not computing yet. If the library or the function table
	/// is missing, or the table belongs to a different SimDLL build, nothing is hooked and the game runs as usual.
	/// </summary>
	internal static class SimHooks
	{
		// Must match SIMHOOK_ABI in sim/hook/simhook.h.
		private const int ExpectedAbi = 7;
		private const string EngineFile = "oni_simhook.bin";

		[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr LoadLibrary(string path);

		[DllImport("kernel32", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandle(string name);

		[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr ApiFn();

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int AttachFn(IntPtr api);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int IntFn();

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr TextFn();

		[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		private delegate int InitFn(string moduleName, string symbolsPath);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		private delegate int NameFn(string name);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate long CallsFn(int index);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		private delegate int PluginFn(string name, int enable);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		private delegate int ValueSetFn(string name, long value);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		private delegate long ValueGetFn(string name);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		private unsafe delegate int DataSendFn(string name, byte* data, int size);

		private static DataSendFn dataSend;
		private static ValueSetFn valueSet;
		private static ValueGetFn valueGet;
		private static TextFn lastError;
		private static NameFn probeAdd;
		private static CallsFn probeCalls;
		private static PluginFn pluginEnable;
		private static ApiFn engineApi;
		private static readonly List<string> probes = new List<string>();
		private static bool tried;

		internal static bool Available { get; private set; }
		internal static string Info { get; private set; } = "not loaded";

		private static string LastError => lastError != null ? Marshal.PtrToStringAnsi(lastError()) : Info;

		/// <summary>Loads the library once per game launch. Any failure leaves Available == false.</summary>
		internal static bool Load()
		{
			if (tried)
			{
				return Available;
			}
			tried = true;
			try
			{
				// Where the engine finds the simulation's functions: next to SimDLL.dll, or a table in the mod folder.
				string plugins = Path.Combine(UnityEngine.Application.dataPath, "Plugins", "x86_64");
				string symbols = Path.Combine(plugins, "SimDLL.pdb");
				if (!File.Exists(symbols))
				{
					symbols = Path.Combine(ModContext.Folder, "sim_symbols.tsv");
				}
				// The engine may already have been loaded by another of our mods: then we use its instance.
				IntPtr module = GetModuleHandle(EngineFile);
				bool shared = module != IntPtr.Zero;
				if (!shared)
				{
					string library = Path.Combine(ModContext.Folder, EngineFile);
					if (!File.Exists(library) || !File.Exists(symbols))
					{
						Info = $"no {EngineFile} in the mod folder, or no function table for SimDLL";
						return false;
					}
					// The game loads SimDLL on the first access to the simulation, i.e. after the mods are loaded.
					// We load it ourselves by the same path: the hooks must be in place before the first tick.
					string sim = Path.Combine(plugins, "SimDLL.dll");
					if (LoadLibrary(sim) == IntPtr.Zero)
					{
						Info = $"cannot load {sim}, error {Marshal.GetLastWin32Error()}";
						return false;
					}
					module = LoadLibrary(library);
					if (module == IntPtr.Zero)
					{
						Info = $"LoadLibrary failed, error {Marshal.GetLastWin32Error()}";
						return false;
					}
				}
				int abi = Bind<IntFn>(module, "simhook_abi")();
				if (abi != ExpectedAbi)
				{
					Info = $"library abi {abi}, mod expects {ExpectedAbi}";
					return false;
				}
				lastError = Bind<TextFn>(module, "simhook_last_error");
				probeAdd = Bind<NameFn>(module, "simhook_probe_add");
				probeCalls = Bind<CallsFn>(module, "simhook_probe_calls");
				pluginEnable = Bind<PluginFn>(module, "simhook_plugin_enable");
				valueSet = Bind<ValueSetFn>(module, "simhook_value_set");
				valueGet = Bind<ValueGetFn>(module, "simhook_value_get");
				dataSend = Bind<DataSendFn>(module, "simhook_data_send");
				engineApi = Bind<ApiFn>(module, "simhook_api");
				int result = Bind<InitFn>(module, "simhook_init")("SimDLL.dll", symbols);
				if (result != 0)
				{
					Info = $"init failed ({result}): {LastError}";
					return false;
				}
				Info = $"ready, symbols: {Bind<IntFn>(module, "simhook_symbol_count")()} from {Path.GetFileName(symbols)}{(shared ? ", engine loaded by another mod" : "")}";
				Available = true;
			}
			catch (Exception e)
			{
				Info = "load failed: " + e.Message;
				Available = false;
			}
			return Available;
		}

		/// <summary>Installs a call counter on a simulation function. Functions with the same name are told apart as "name#1", "name#2".</summary>
		internal static bool AddProbe(string function, out string error)
		{
			error = null;
			int index = probeAdd(function);
			if (index < 0)
			{
				error = LastError;
				return false;
			}
			while (probes.Count <= index)
			{
				probes.Add(null);
			}
			probes[index] = function;
			return true;
		}

		/// <summary>Call counts for all installed counter hooks.</summary>
		internal static IEnumerable<KeyValuePair<string, long>> ProbeCalls()
		{
			for (int i = 0; i < probes.Count; i++)
			{
				if (probes[i] != null)
				{
					yield return new KeyValuePair<string, long>(probes[i], probeCalls(i));
				}
			}
		}

		/// <summary>
		/// Loads a plugin library from the mod folder and attaches it to the engine: after that the plugin's hooks
		/// can be enabled by its name (EnablePlugin), and its numbers and data are available by names "plugin.what".
		/// </summary>
		internal static bool LoadPlugin(string file, out string error)
		{
			error = null;
			string library = Path.Combine(ModContext.Folder, file);
			if (!File.Exists(library))
			{
				error = "no " + file + " in the mod folder";
				return false;
			}
			IntPtr module = LoadLibrary(library);
			if (module == IntPtr.Zero)
			{
				error = $"LoadLibrary {file} failed, error {Marshal.GetLastWin32Error()}";
				return false;
			}
			IntPtr attach = GetProcAddress(module, "simplugin_attach");
			if (attach == IntPtr.Zero)
			{
				error = file + " is not a plugin library";
				return false;
			}
			int result = Marshal.GetDelegateForFunctionPointer<AttachFn>(attach)(engineApi());
			if (result != 0)
			{
				error = $"{file} was built for another engine version ({result})";
				return false;
			}
			return true;
		}

		/// <summary>Enables or disables all hooks of a plugin. Call only while the simulation is not computing.</summary>
		internal static bool EnablePlugin(string plugin, bool enable, out string error)
		{
			error = null;
			if (pluginEnable(plugin, enable ? 1 : 0) < 0)
			{
				error = LastError;
				return false;
			}
			return true;
		}

		/// <summary>A plugin switch (name of the form "plugin.what"). Can be changed at any moment.</summary>
		internal static bool SetValue(string name, long value)
		{
			return valueSet(name, value) == 0;
		}

		/// <summary>Passes a block of data to the plugin (name of the form "plugin.what"); the plugin copies it for itself.</summary>
		internal static unsafe bool SendData(string name, byte[] data)
		{
			fixed (byte* pinned = data)
			{
				return dataSend(name, pinned, data.Length) == 0;
			}
		}

		/// <summary>
		/// Asks the plugin for data: the same channel as <see cref="SendData"/>, but the plugin writes its answer
		/// into the given buffer. Returns what the plugin's handler returned (0 on success).
		/// </summary>
		internal static unsafe int Query(string name, byte[] buffer)
		{
			fixed (byte* pinned = buffer)
			{
				return dataSend(name, pinned, buffer.Length);
			}
		}

		/// <summary>The same for an already pinned buffer.</summary>
		internal static unsafe int Query(string name, byte* buffer, int size)
		{
			return dataSend(name, buffer, size);
		}

		/// <summary>The same for a buffer of numbers: a request header and an array of per-cell values.</summary>
		internal static unsafe int Query(string name, float[] buffer)
		{
			fixed (float* pinned = buffer)
			{
				return dataSend(name, (byte*)pinned, buffer.Length * sizeof(float));
			}
		}

		/// <summary>A plugin switch or counter; -1 for an unknown name.</summary>
		internal static long GetValue(string name)
		{
			return valueGet(name);
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
