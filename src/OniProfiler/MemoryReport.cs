using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using OniCommon;
using UnityEngine;
using UnityEngine.Profiling;

namespace OniProfiler
{
	/// <summary>
	/// What the game's memory is made of. The switches are lines in the method list (profile_methods.txt):
	///
	/// - "@memory" writes one full section once, after the first minute of the game;
	/// - "@memory-watch [minutes]" (minutes is optional, 10 by default) watches a long session: every report window
	///   gets one cheap "mem:" line with the memory counters (a timeline in the report file), and every N minutes
	///   the full breakdown is written again with the difference to the previous one: which roots, textures and
	///   object counts grew. A snapshot costs one long frame (the walk of the static fields alone can take seconds)
	///   and is followed by a garbage collection. "process private" that grows while the managed counters stay flat
	///   means the memory goes to native code (the simulation plugins of the mixture mods, FMOD, the game's own
	///   buffers): see "outside unity's reservations".
	///
	/// The file memory_now.txt in the mod folder forces a snapshot in the next report window (the file is deleted
	/// afterwards), so a moment noticed by eye can also be caught.
	///
	/// When the process private bytes come close to the system memory (85 percent), a snapshot is taken at once,
	/// regardless of the schedule, but a cheap one: without the walk of the static fields, whose "seen" set itself
	/// costs memory exactly when there is none left. This is the snapshot most likely to hold the state right
	/// before an OOM kills the game; it is taken at most once in 5 minutes.
	/// </summary>
	internal static class MemoryReport
	{
		internal static bool Enabled;
		private static bool done;
		private static int reports;
		private const int AfterReports = 2; // the first windows hold the loading
		private const int Depth = 6, ArraySample = 4096, TopRoots = 45, TopAssets = 15;
		private const double MaxSeconds = 8.0; // under the watchdog's stall limit

		// The watch (the line "@memory-watch" in the method list).
		internal static bool Watch;
		private static int snapshotMinutes = 10;
		private static float lastSnapshotAt = float.NegativeInfinity;
		private static int snapshotIndex;
		// A cheap snapshot at once when the process comes close to the system memory: 0 = unknown system memory.
		private static long pressureLimitBytes;
		private static float lastPressureAt = float.NegativeInfinity;
		private const float PressureCooldownSeconds = 300f;
		private static Snapshot previous;
		private static long milestonePrivate; // the private bytes of the last milestone line in the game log
		private const long MilestoneBytes = 512L << 20;
		// The walk's "seen" set itself costs memory (hundreds of MB at the limit): under the watch it is allowed
		// fewer objects, because the session it watches is the one short of memory.
		private static long maxWalkObjects = 4000000;

		private static readonly System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
		// GC.GetGCMemoryInfo exists in the runtime (netstandard 2.1) but not in the net48 reference: read by reflection.
		private static readonly MethodInfo gcMemoryInfo = typeof(GC).GetMethod("GetGCMemoryInfo", Type.EmptyTypes);

		private sealed class Counters
		{
			public long gcHeap, monoHeap, monoUsed, unityAlloc, unityReserved, unityUnused, graphicsDriver, tempAllocator, privateBytes, workingSet, virtualBytes;
		}

		private sealed class Snapshot
		{
			public float atSeconds;
			public string cycleText = "?";
			public Counters counters;
			public readonly List<(string section, string label, long value, bool bytes)> rows = new List<(string, string, long, bool)>(512);
			public long walked;
			public readonly List<string> notes = new List<string>();
		}

		// ---------- one-shot section ("@memory"), as before ----------

		internal static void AppendOnce(System.Text.StringBuilder sb)
		{
			if (!Enabled || done || ++reports < AfterReports)
			{
				return;
			}
			done = true;
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			Snapshot snap = Take(walkStatics: true);
			Render(sb, snap, previous, $"-- memory (once, T+{snap.atSeconds:F0})");
			sb.AppendLine($"memory section built in {ElapsedMs(start):F0} ms");
		}

		// ---------- the watch ("@memory-watch") ----------

		internal static void ConfigureWatch(int minutes)
		{
			Watch = true;
			snapshotMinutes = minutes;
			maxWalkObjects = 2000000;
			if (SystemInfo.systemMemorySize > 0)
			{
				pressureLimitBytes = (long)SystemInfo.systemMemorySize * 1048576L * 85 / 100;
			}
		}

		/// <summary>Called from Report.Write in every report window: the timeline line, and a full snapshot when due.</summary>
		internal static void AppendWatch(System.Text.StringBuilder sb)
		{
			if (!Watch)
			{
				return;
			}
			Counters counters = null;
			try
			{
				counters = WindowLine(sb);
			}
			catch (Exception e)
			{
				sb.AppendLine("mem: counters failed: " + e.Message);
			}
			try
			{
				SnapshotIfDue(sb, counters);
			}
			catch (Exception e)
			{
				sb.AppendLine("mem: snapshot failed: " + e);
			}
		}

		/// <summary>Appends the timeline line and returns the counters it was built from (for the pressure check).</summary>
		private static Counters WindowLine(System.Text.StringBuilder sb)
		{
			float now = Time.realtimeSinceStartup;
			Counters c = Read();
			sb.AppendLine($"mem: T+{now:F0} cycle={CycleText()} private={c.privateBytes / 1048576}MB working={c.workingSet / 1048576}MB " +
				$"gc_heap={c.gcHeap / 1048576}MB mono={c.monoUsed / 1048576}/{c.monoHeap / 1048576}MB " +
				$"unity={c.unityAlloc / 1048576}/{c.unityReserved / 1048576}MB gfx={c.graphicsDriver / 1048576}MB " +
				$"gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} pickupables={PickupablesCount()}");
			// A milestone line in the game log: the growth is visible there even without the report file.
			if (milestonePrivate == 0 || c.privateBytes < milestonePrivate)
			{
				milestonePrivate = c.privateBytes;
			}
			if (c.privateBytes - milestonePrivate >= MilestoneBytes)
			{
				Debug.Log($"[OniProfiler] memory: private {c.privateBytes / 1048576} MB (+{(c.privateBytes - milestonePrivate) / 1048576} MB since the last milestone), " +
					$"gc heap {c.gcHeap / 1048576} MB, unity {c.unityAlloc / 1048576} MB, graphics driver {c.graphicsDriver / 1048576} MB, T+{now:F0}");
				milestonePrivate = c.privateBytes;
			}
			return c;
		}

		private static void SnapshotIfDue(System.Text.StringBuilder sb, Counters counters)
		{
			float now = Time.realtimeSinceStartup;
			bool force = TakeMarker();
			// Close to the end of the system memory: take a snapshot now, a cheap one (without the statics walk).
			bool pressure = counters != null && pressureLimitBytes > 0 && counters.privateBytes >= pressureLimitBytes &&
				now - lastPressureAt >= PressureCooldownSeconds;
			if (pressure)
			{
				lastPressureAt = now;
			}
			if (!force && !pressure && now - lastSnapshotAt < snapshotMinutes * 60f)
			{
				return;
			}
			lastSnapshotAt = now;
			snapshotIndex++;
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			Snapshot snap = Take(walkStatics: !pressure);
			Render(sb, snap, previous, $"-- memory (watch #{snapshotIndex}{(pressure ? ", MEMORY PRESSURE: cheap, without the statics walk" : "")}, " +
				$"T+{snap.atSeconds:F0} s, difference to " +
				(previous != null ? $"#{snapshotIndex - 1}, {snap.atSeconds - previous.atSeconds:F0} s ago" : "nothing yet") + ")");
			previous = snap;
			// The walk's "seen" set is garbage now and the watched session is short of memory: give it back at once.
			// A full collection of a multi-gigabyte heap stalls the game for a moment (about 0.7 s at 2.1 GB).
			GC.Collect();
			Debug.Log($"[OniProfiler] memory snapshot #{snapshotIndex}: private {snap.counters.privateBytes / 1048576} MB, " +
				$"gc heap {snap.counters.gcHeap / 1048576} MB, walked {snap.walked} objects in {ElapsedMs(start):F0} ms, T+{now:F0}");
		}

		private static bool TakeMarker()
		{
			try
			{
				string marker = Path.Combine(ModContext.Folder, "memory_now.txt");
				if (!File.Exists(marker))
				{
					return false;
				}
				File.Delete(marker);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		// ---------- building a snapshot ----------

		/// <param name="walkStatics">false for the cheap snapshot under memory pressure: the walk's "seen" set itself costs memory.</param>
		private static Snapshot Take(bool walkStatics)
		{
			var snap = new Snapshot { atSeconds = Time.realtimeSinceStartup, cycleText = CycleText(), counters = Read() };
			Section(snap, "counters", AddCounters);
			Section(snap, "objects and assets", AddObjectsAndAssets);
			if (walkStatics)
			{
				Section(snap, "static fields", AddStatics);
			}
			else
			{
				snap.notes.Add("static fields: skipped in this cheap snapshot (the walk itself costs memory)");
			}
			return snap;
		}

		private static void Section(Snapshot snap, string what, Action<Snapshot> add)
		{
			try
			{
				add(snap);
			}
			catch (Exception e)
			{
				snap.notes.Add($"{what} failed: {e.Message}");
			}
		}

		private static Counters Read()
		{
			process.Refresh();
			var c = new Counters
			{
				gcHeap = GC.GetTotalMemory(false),
				monoHeap = Profiler.GetMonoHeapSizeLong(),
				monoUsed = Profiler.GetMonoUsedSizeLong(),
				unityAlloc = Profiler.GetTotalAllocatedMemoryLong(),
				unityReserved = Profiler.GetTotalReservedMemoryLong(),
				unityUnused = Profiler.GetTotalUnusedReservedMemoryLong(),
				graphicsDriver = Profiler.GetAllocatedMemoryForGraphicsDriver(),
				tempAllocator = Profiler.GetTempAllocatorSize(),
				privateBytes = process.PrivateMemorySize64,
				workingSet = process.WorkingSet64,
				virtualBytes = process.VirtualMemorySize64,
			};
			if (c.privateBytes == 0)
			{
				// Under Proton/Wine the performance counters behind System.Diagnostics.Process are stubbed and
				// return zeros. Wine maps the root filesystem as drive Z:, and the game process is the Linux
				// process itself: its own /proc/self/status holds the real numbers (also what spills into swap).
				ReadProcStatus(c);
			}
			return c;
		}

		private static void ReadProcStatus(Counters c)
		{
			try
			{
				long rss = 0, swap = 0;
				foreach (string line in File.ReadAllLines(@"Z:\proc\self\status"))
				{
					if (TryKb(line, "VmRSS:", out long kb))
					{
						rss = kb;
					}
					else if (TryKb(line, "VmSwap:", out kb))
					{
						swap = kb;
					}
					else if (TryKb(line, "VmSize:", out kb))
					{
						c.virtualBytes = kb;
					}
				}
				c.workingSet = rss;
				c.privateBytes = rss + swap;
			}
			catch (Exception)
			{
			}
		}

		private static bool TryKb(string line, string prefix, out long kb)
		{
			kb = 0;
			if (!line.StartsWith(prefix))
			{
				return false;
			}
			string[] parts = line.Substring(prefix.Length).Trim().Split(' ');
			return parts.Length > 0 && long.TryParse(parts[0], out kb);
		}

		private static string CycleText()
		{
			try
			{
				return GameClock.Instance != null ? GameClock.Instance.GetCycle().ToString() : "?";
			}
			catch (Exception)
			{
				return "?";
			}
		}

		private static int PickupablesCount()
		{
			try
			{
				return Components.Pickupables.Count;
			}
			catch (Exception)
			{
				return -1;
			}
		}

		private static void AddCounters(Snapshot snap)
		{
			Counters c = snap.counters;
			snap.rows.Add(("counters", "gc heap used", c.gcHeap, true));
			snap.rows.Add(("counters", "mono heap size", c.monoHeap, true));
			snap.rows.Add(("counters", "mono used", c.monoUsed, true));
			snap.rows.Add(("counters", "unity allocated", c.unityAlloc, true));
			snap.rows.Add(("counters", "unity reserved", c.unityReserved, true));
			snap.rows.Add(("counters", "unity reserved, unused", c.unityUnused, true));
			snap.rows.Add(("counters", "graphics driver", c.graphicsDriver, true));
			snap.rows.Add(("counters", "temp allocator", c.tempAllocator, true));
			snap.rows.Add(("counters", "process private", c.privateBytes, true));
			snap.rows.Add(("counters", "process working set", c.workingSet, true));
			snap.rows.Add(("counters", "process virtual", c.virtualBytes, true));
			// Unity's reservations include the mono heap; what is left of the process is native code.
			snap.rows.Add(("counters", "outside unity's reservations (native code, an estimate)", Math.Max(0, c.privateBytes - c.unityReserved), true));
			object info = null;
			try
			{
				info = gcMemoryInfo?.Invoke(null, null);
			}
			catch (Exception)
			{
			}
			if (info != null)
			{
				AddGcInfoRow(snap, info, "gc info: heap size", "HeapSizeBytes");
				AddGcInfoRow(snap, info, "gc info: fragmented", "FragmentedBytes");
				AddGcInfoRow(snap, info, "gc info: memory load", "MemoryLoadBytes");
			}
		}

		private static void AddGcInfoRow(Snapshot snap, object info, string label, string property)
		{
			try
			{
				object value = info.GetType().GetProperty(property)?.GetValue(info, null);
				if (value is long longValue)
				{
					snap.rows.Add(("counters", label, longValue, true));
				}
				else if (value is ulong ulongValue)
				{
					snap.rows.Add(("counters", label, (long)Math.Min(ulongValue, long.MaxValue), true));
				}
			}
			catch (Exception)
			{
			}
		}

		private static void AddObjectsAndAssets(Snapshot snap)
		{
			Texture2D[] textures = Resources.FindObjectsOfTypeAll<Texture2D>();
			RenderTexture[] renderTextures = Resources.FindObjectsOfTypeAll<RenderTexture>();
			Texture2DArray[] textureArrays = Resources.FindObjectsOfTypeAll<Texture2DArray>();
			Mesh[] meshes = Resources.FindObjectsOfTypeAll<Mesh>();
			// Counts show leaks of objects themselves, even where the bytes are only estimated.
			snap.rows.Add(("objects", "Texture2D", textures.Length, false));
			snap.rows.Add(("objects", "RenderTexture", renderTextures.Length, false));
			snap.rows.Add(("objects", "Texture2DArray", textureArrays.Length, false));
			snap.rows.Add(("objects", "Mesh", meshes.Length, false));
			snap.rows.Add(("objects", "Material", Resources.FindObjectsOfTypeAll<Material>().Length, false));
			snap.rows.Add(("objects", "Shader", Resources.FindObjectsOfTypeAll<Shader>().Length, false));
			snap.rows.Add(("objects", "Font", Resources.FindObjectsOfTypeAll<Font>().Length, false));
			snap.rows.Add(("objects", "GameObject", Resources.FindObjectsOfTypeAll<GameObject>().Length, false));
			snap.rows.Add(("objects", "Component", Resources.FindObjectsOfTypeAll<Component>().Length, false));
			snap.rows.Add(("objects", "ScriptableObject", Resources.FindObjectsOfTypeAll<ScriptableObject>().Length, false));
			snap.rows.Add(("objects", "Sprite", Resources.FindObjectsOfTypeAll<Sprite>().Length, false));
			snap.rows.Add(("objects", "pickupables (debris and other things)", PickupablesCount(), false));

			// Sizes estimated from dimensions and format: the release player does not give asset sizes.
			var top = new List<(string kind, string name, long bytes, string what)>();
			long textureTotal = 0, readableTotal = 0;
			foreach (Texture2D t in textures)
			{
				long bytes = TextureBytes(t.width, t.height, t.format, t.mipmapCount);
				textureTotal += bytes;
				if (t.isReadable)
				{
					readableTotal += bytes;
				}
				top.Add(("Texture2D", t.name, bytes, $"{t.width}x{t.height} {t.format} mips {t.mipmapCount}{(t.isReadable ? " readable" : "")}"));
			}
			long renderTotal = 0;
			foreach (RenderTexture t in renderTextures)
			{
				long bytes = (long)t.width * t.height * Math.Max(1, t.volumeDepth) * RenderBytes(t.format) + (t.depth > 0 ? (long)t.width * t.height * 4 : 0);
				renderTotal += bytes;
				top.Add(("RenderTexture", t.name, bytes, $"{t.width}x{t.height} {t.format} depth {t.depth}{(t.IsCreated() ? "" : " not created")}"));
			}
			long arrayTotal = 0;
			foreach (Texture2DArray t in textureArrays)
			{
				long bytes = TextureBytes(t.width, t.height, t.format, t.mipmapCount) * t.depth;
				arrayTotal += bytes;
				top.Add(("Texture2DArray", t.name, bytes, $"{t.width}x{t.height}x{t.depth} {t.format}"));
			}
			long meshTotal = 0;
			foreach (Mesh m in meshes)
			{
				long bytes = 0;
				try
				{
					for (int s = 0; s < m.vertexBufferCount; s++) bytes += (long)m.vertexCount * m.GetVertexBufferStride(s);
					for (int s = 0; s < m.subMeshCount; s++) bytes += (long)m.GetIndexCount(s) * (m.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32 ? 4 : 2);
				}
				catch (Exception)
				{
				}
				meshTotal += bytes;
				top.Add(("Mesh", m.name, bytes, $"{m.vertexCount} vertices{(m.isReadable ? " readable" : "")}"));
			}
			// Sound is FMOD's (native, its banks): not Unity assets.
			snap.rows.Add(("assets", "Texture2D, total", textureTotal, true));
			snap.rows.Add(("assets", "Texture2D readable copy (a second copy in system memory)", readableTotal, true));
			snap.rows.Add(("assets", "RenderTexture, total", renderTotal, true));
			snap.rows.Add(("assets", "Texture2DArray, total", arrayTotal, true));
			snap.rows.Add(("assets", "Mesh, total", meshTotal, true));
			foreach ((string kind, string name, long bytes, string what) r in top.OrderByDescending(r => r.bytes).Take(TopAssets))
			{
				snap.rows.Add(("assets", $"{r.kind}: {r.name} ({r.what})", r.bytes, true));
			}
		}

		// ---------- managed memory from static fields ----------

		private sealed class ByReference : IEqualityComparer<object>
		{
			public new bool Equals(object a, object b) => ReferenceEquals(a, b);
			public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
		}

		private static HashSet<object> seen;
		private static readonly Dictionary<Type, FieldInfo[]> fieldsOf = new Dictionary<Type, FieldInfo[]>();
		private static long walked, walkStart;
		private static int failed;
		private static string lastError;

		private static bool Stopped() => walked >= maxWalkObjects ||
			(System.Diagnostics.Stopwatch.GetTimestamp() - walkStart) / (double)System.Diagnostics.Stopwatch.Frequency >= MaxSeconds;

		private static void AddStatics(Snapshot snap)
		{
			seen = new HashSet<object>(new ByReference());
			walked = 0;
			failed = 0;
			lastError = null;
			walkStart = System.Diagnostics.Stopwatch.GetTimestamp();
			var roots = new List<(string name, long bytes)>();
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				string an = assembly.GetName().Name;
				if (an.StartsWith("System") || an.StartsWith("mscorlib") || an.StartsWith("Mono.") || an.StartsWith("netstandard") || an.StartsWith("Unity."))
				{
					continue;
				}
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException e)
				{
					types = e.Types.Where(t => t != null).ToArray();
				}
				foreach (Type type in types)
				{
					if (type.ContainsGenericParameters)
					{
						continue;
					}
					FieldInfo[] fields;
					try
					{
						fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
					}
					catch (Exception)
					{
						continue;
					}
					foreach (FieldInfo field in fields)
					{
						if (field.IsLiteral || field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType.IsPointer || field.FieldType.IsByRef)
						{
							continue;
						}
						object value;
						try
						{
							value = field.GetValue(null);
						}
						catch (Exception)
						{
							continue;
						}
						if (value == null || (value is UnityEngine.Object o && o == null))
						{
							continue;
						}
						if (Stopped())
						{
							break;
						}
						long bytes;
						try
						{
							bytes = Size(value, Depth);
						}
						catch (Exception e)
						{
							failed++;
							lastError = $"{type.FullName}.{field.Name}: {e.GetType().Name} {e.Message}";
							continue;
						}
						if (bytes >= 1 << 20) roots.Add(($"{an}: {type.FullName}.{field.Name}", bytes));
					}
				}
			}
			long total = roots.Sum(r => r.bytes);
			snap.walked = walked;
			snap.notes.Add($"static fields: {Mb(total)} in {roots.Count} roots of 1 MB and more ({walked} objects walked, {Depth} levels deep" +
				$"{(Stopped() ? ", STOPPED early at the limit: partial" : "")}; roots that could not be walked {failed}{(lastError != null ? ", the last: " + lastError : "")})");
			foreach ((string name, long bytes) r in roots.OrderByDescending(r => r.bytes).Take(TopRoots))
			{
				snap.rows.Add(("statics", r.name, r.bytes, true));
			}
			snap.rows.Add(("statics", "all roots of 1 MB and more, together", total, true));
			seen = null;
		}

		private static FieldInfo[] InstanceFields(Type type)
		{
			if (fieldsOf.TryGetValue(type, out FieldInfo[] fields))
			{
				return fields;
			}
			var list = new List<FieldInfo>();
			for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
			{
				foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					list.Add(f);
				}
			}
			fields = list.ToArray();
			fieldsOf[type] = fields;
			return fields;
		}

		private static int ValueSize(Type type)
		{
			if (!type.IsValueType) return 8;
			if (type.IsPrimitive)
			{
				if (type == typeof(long) || type == typeof(ulong) || type == typeof(double) || type == typeof(IntPtr) || type == typeof(UIntPtr)) return 8;
				if (type == typeof(short) || type == typeof(ushort) || type == typeof(char)) return 2;
				if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(bool)) return 1;
				return 4;
			}
			if (type.IsEnum) return ValueSize(Enum.GetUnderlyingType(type));
			try
			{
				return Math.Max(1, System.Runtime.InteropServices.Marshal.SizeOf(type));
			}
			catch (Exception)
			{
				int sum = 0;
				foreach (FieldInfo f in InstanceFields(type)) sum += ValueSize(f.FieldType);
				return Math.Max(1, sum);
			}
		}

		/// <summary>Bytes of an object and what it holds (not counted before), `depth` levels down.</summary>
		private static long Size(object o, int depth)
		{
			if (o == null || depth < 0 || Stopped() || !seen.Add(o))
			{
				return 0;
			}
			walked++;
			Type type = o.GetType();
			if (o is string s)
			{
				return 20 + 2L * s.Length;
			}
			if (o is Array array)
			{
				Type element = type.GetElementType();
				if (element.IsPointer)
				{
					return 32 + array.LongLength * 8;
				}
				long length = array.LongLength;
				long bytes = 32 + length * ValueSize(element);
				// Unity objects in an array are references into the scene: not walked.
				if (depth > 0 && !element.IsPrimitive && !element.IsEnum && array.Rank == 1 && !typeof(UnityEngine.Object).IsAssignableFrom(element))
				{
					// The elements (or the reference fields of struct elements), by a sample of a long array.
					bool refs = !element.IsValueType;
					FieldInfo[] structRefs = refs ? null : InstanceFields(element).Where(f => !f.FieldType.IsValueType && !f.FieldType.IsPointer).ToArray();
					if (refs || structRefs.Length > 0)
					{
						long step = Math.Max(1, length / ArraySample), sampled = 0, sum = 0;
						for (long i = 0; i < length; i += step)
						{
							object item = array.GetValue(i);
							sampled++;
							if (refs) sum += Size(item, depth - 1);
							else foreach (FieldInfo f in structRefs) sum += Size(f.GetValue(item), depth - 1);
						}
						bytes += sampled > 0 ? sum * (length / sampled) : 0;
					}
				}
				return bytes;
			}
			long size = 16;
			foreach (FieldInfo f in InstanceFields(type))
			{
				if (f.FieldType.IsValueType || f.FieldType.IsPointer)
				{
					size += f.FieldType.IsPointer ? 8 : ValueSize(f.FieldType);
					continue;
				}
				size += 8;
				if (depth > 0)
				{
					object value;
					try
					{
						value = f.GetValue(o);
					}
					catch (Exception)
					{
						continue;
					}
					// Not walked: code, and references from one Unity object to another (the scene graph; a singleton
					// reached from a static field is walked with its own data).
					if (value is Delegate || value is Type || value is MemberInfo || value is Assembly || value is UnityEngine.Object) continue;
					size += Size(value, depth - 1);
				}
			}
			return size;
		}

		// ---------- rendering with the difference to the previous snapshot ----------

		private static void Render(System.Text.StringBuilder sb, Snapshot snap, Snapshot prev, string title)
		{
			sb.AppendLine();
			sb.AppendLine(title);
			sb.AppendLine($"system memory {SystemInfo.systemMemorySize} MB, graphics memory {SystemInfo.graphicsMemorySize} MB ({SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsDeviceVersion})");
			var before = new Dictionary<string, long>();
			if (prev != null)
			{
				foreach ((string section, string label, long value, bool bytes) r in prev.rows)
				{
					before[r.label] = r.value;
				}
			}
			string section = null;
			foreach ((string section, string label, long value, bool bytes) r in snap.rows)
			{
				if (r.section != section)
				{
					section = r.section;
					sb.AppendLine();
					sb.AppendLine($"[{section}]");
				}
				string change = "";
				if (before.TryGetValue(r.label, out long was) && was != r.value)
				{
					long delta = r.value - was;
					change = r.bytes ? $" ({delta / 1048576.0:+0.0;-0.0} MB)" : $" ({delta:+#,##0;-#,##0})";
				}
				sb.AppendLine($"{(r.bytes ? Mb(r.value) : $"n={r.value,12}")}  {r.label}{change}");
			}
			foreach (string note in snap.notes)
			{
				sb.AppendLine("note: " + note);
			}
			if (prev == null)
			{
				return;
			}
			var changes = new List<(string label, long delta, bool bytes)>();
			foreach ((string sectionName, string label, long value, bool bytes) r in snap.rows)
			{
				if (before.TryGetValue(r.label, out long was) && r.value != was)
				{
					changes.Add((r.label, r.value - was, r.bytes));
				}
			}
			sb.AppendLine();
			sb.AppendLine($"[grew since the previous snapshot, {snap.atSeconds - prev.atSeconds:F0} s ago]");
			foreach ((string label, long delta, bool bytes) change in changes.Where(ch => ch.bytes && ch.delta >= 1 << 20).OrderByDescending(ch => ch.delta).Take(10))
			{
				sb.AppendLine($"  +{change.delta / 1048576.0:F1} MB  {change.label}");
			}
			foreach ((string label, long delta, bool bytes) change in changes.Where(ch => !ch.bytes && ch.delta > 0).OrderByDescending(ch => ch.delta).Take(6))
			{
				sb.AppendLine($"  +{change.delta}  {change.label}");
			}
			foreach ((string label, long delta, bool bytes) change in changes.Where(ch => ch.bytes && ch.delta <= -(1 << 20)).OrderBy(ch => ch.delta).Take(5))
			{
				sb.AppendLine($"  {change.delta / 1048576.0:F1} MB  {change.label}");
			}
		}

		private static string Mb(long bytes) => $"{bytes / 1048576.0,8:F1} MB";

		private static long TextureBytes(int w, int h, TextureFormat format, int mips)
		{
			double bpp = format switch
			{
				TextureFormat.Alpha8 or TextureFormat.R8 => 1,
				TextureFormat.RGB24 => 3,
				TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.BGRA32 or TextureFormat.RFloat or TextureFormat.RGHalf => 4,
				TextureFormat.RGBAHalf or TextureFormat.RGFloat => 8,
				TextureFormat.RGBAFloat => 16,
				TextureFormat.RHalf or TextureFormat.R16 or TextureFormat.RG16 or TextureFormat.RGB565 or TextureFormat.RGBA4444 or TextureFormat.ARGB4444 => 2,
				TextureFormat.DXT1 or TextureFormat.BC4 => 0.5,
				TextureFormat.DXT5 or TextureFormat.BC5 or TextureFormat.BC7 or TextureFormat.BC6H => 1,
				_ => 4,
			};
			double bytes = (double)w * h * bpp;
			return (long)(mips > 1 ? bytes * 4.0 / 3.0 : bytes);
		}

		private static int RenderBytes(RenderTextureFormat format) => format switch
		{
			RenderTextureFormat.ARGBHalf or RenderTextureFormat.RGFloat => 8,
			RenderTextureFormat.ARGBFloat => 16,
			RenderTextureFormat.R8 => 1,
			RenderTextureFormat.RHalf or RenderTextureFormat.R16 or RenderTextureFormat.RG16 => 2,
			_ => 4,
		};

		private static double ElapsedMs(long start) => (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
	}
}
