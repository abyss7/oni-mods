using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Profiling;

namespace OniProfiler
{
	/// <summary>
	/// What the game's memory is made of, once (the line "@memory" in the method list; written with the report after the
	/// first minute): Unity's own counters, the assets in memory by kind with the largest of them (sizes estimated from
	/// their dimensions and format: the release player does not give asset sizes), and the managed memory reachable from
	/// static fields, by root (an estimate: objects are walked a few levels deep, an object reached twice is counted once,
	/// long arrays by a sample).
	/// </summary>
	internal static class MemoryReport
	{
		internal static bool Enabled;
		private static bool done;
		private static int reports;
		private const int AfterReports = 2; // the first windows hold the loading
		private const int Depth = 6, ArraySample = 4096, TopRoots = 45, TopAssets = 15;
		private const long MaxObjects = 4000000;
		private const double MaxSeconds = 8.0; // under the watchdog's stall limit

		internal static void AppendOnce(System.Text.StringBuilder sb)
		{
			if (!Enabled || done || ++reports < AfterReports)
			{
				return;
			}
			done = true;
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			sb.AppendLine();
			sb.AppendLine("-- memory (once)");
			try
			{
				Unity(sb);
			}
			catch (Exception e)
			{
				sb.AppendLine("unity counters failed: " + e.Message);
			}
			try
			{
				Assets(sb);
			}
			catch (Exception e)
			{
				sb.AppendLine("assets failed: " + e.Message);
			}
			try
			{
				Statics(sb);
			}
			catch (Exception e)
			{
				sb.AppendLine("statics failed: " + e.Message);
			}
			sb.AppendLine($"memory section built in {(System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms");
		}

		private static string Mb(long bytes) => $"{bytes / 1048576.0,8:F1} MB";

		private static void Unity(System.Text.StringBuilder sb)
		{
			sb.AppendLine($"managed heap (GC): {Mb(GC.GetTotalMemory(false))}");
			sb.AppendLine($"unity: allocated {Mb(Profiler.GetTotalAllocatedMemoryLong())}, reserved {Mb(Profiler.GetTotalReservedMemoryLong())}, " +
				$"reserved unused {Mb(Profiler.GetTotalUnusedReservedMemoryLong())}, mono heap {Mb(Profiler.GetMonoHeapSizeLong())}, mono used {Mb(Profiler.GetMonoUsedSizeLong())}, " +
				$"graphics driver {Mb(Profiler.GetAllocatedMemoryForGraphicsDriver())}, temp allocator {Mb(Profiler.GetTempAllocatorSize())}");
			sb.AppendLine($"system memory {SystemInfo.systemMemorySize} MB, graphics memory {SystemInfo.graphicsMemorySize} MB ({SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsDeviceVersion})");
		}

		// ---------- Assets ----------

		private static void Assets(System.Text.StringBuilder sb)
		{
			var rows = new List<(string kind, string name, long bytes, string what)>();
			foreach (Texture2D t in Resources.FindObjectsOfTypeAll<Texture2D>())
			{
				rows.Add(("Texture2D", t.name, TextureBytes(t.width, t.height, t.format, t.mipmapCount), $"{t.width}x{t.height} {t.format} mips {t.mipmapCount}{(t.isReadable ? " readable" : "")}"));
			}
			foreach (RenderTexture t in Resources.FindObjectsOfTypeAll<RenderTexture>())
			{
				long bytes = (long)t.width * t.height * Math.Max(1, t.volumeDepth) * RenderBytes(t.format) + (t.depth > 0 ? (long)t.width * t.height * 4 : 0);
				rows.Add(("RenderTexture", t.name, bytes, $"{t.width}x{t.height} {t.format} depth {t.depth}{(t.IsCreated() ? "" : " not created")}"));
			}
			foreach (Texture2DArray t in Resources.FindObjectsOfTypeAll<Texture2DArray>())
			{
				rows.Add(("Texture2DArray", t.name, TextureBytes(t.width, t.height, t.format, t.mipmapCount) * t.depth, $"{t.width}x{t.height}x{t.depth} {t.format}"));
			}
			foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>())
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
				rows.Add(("Mesh", m.name, bytes, $"{m.vertexCount} vertices{(m.isReadable ? " readable" : "")}"));
			}
			// Sound is FMOD's (native, its banks): not Unity assets.
			foreach (string kind in new[] { "Material", "Shader", "Font", "GameObject", "Component", "ScriptableObject", "Sprite" })
			{
				int count = kind switch
				{
					"Material" => Resources.FindObjectsOfTypeAll<Material>().Length,
					"Shader" => Resources.FindObjectsOfTypeAll<Shader>().Length,
					"Font" => Resources.FindObjectsOfTypeAll<Font>().Length,
					"GameObject" => Resources.FindObjectsOfTypeAll<GameObject>().Length,
					"Component" => Resources.FindObjectsOfTypeAll<Component>().Length,
					"ScriptableObject" => Resources.FindObjectsOfTypeAll<ScriptableObject>().Length,
					_ => Resources.FindObjectsOfTypeAll<Sprite>().Length,
				};
				sb.AppendLine($"objects: {kind} {count}");
			}
			foreach (IGrouping<string, (string kind, string name, long bytes, string what)> group in rows.GroupBy(r => r.kind).OrderByDescending(g => g.Sum(r => r.bytes)))
			{
				sb.AppendLine($"assets: {group.Key}: {group.Count()}, {Mb(group.Sum(r => r.bytes))} (estimated from dimensions; a texture that is not readable lives on the GPU)");
				foreach ((string kind, string name, long bytes, string what) r in group.OrderByDescending(r => r.bytes).Take(TopAssets))
				{
					sb.AppendLine($"  {Mb(r.bytes)}  {r.name} ({r.what})");
				}
			}
			// The readable textures keep a copy in system memory.
			long readable = 0;
			foreach (Texture2D t in Resources.FindObjectsOfTypeAll<Texture2D>())
			{
				if (t.isReadable) readable += TextureBytes(t.width, t.height, t.format, t.mipmapCount);
			}
			sb.AppendLine($"assets: readable Texture2D (a copy in system memory): {Mb(readable)}");
		}

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

		// ---------- Managed memory from static fields ----------

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

		private static bool Stopped() => walked >= MaxObjects ||
			(System.Diagnostics.Stopwatch.GetTimestamp() - walkStart) / (double)System.Diagnostics.Stopwatch.Frequency >= MaxSeconds;

		private static void Statics(System.Text.StringBuilder sb)
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
			sb.AppendLine($"managed from static fields: {Mb(total)} in {roots.Count} roots of 1 MB and more ({walked} objects walked, {Depth} levels deep" +
				$"{(Stopped() ? ", STOPPED early at the limit: partial" : "")}; roots that could not be walked {failed}{(lastError != null ? ", the last: " + lastError : "")})");
			foreach ((string name, long bytes) r in roots.OrderByDescending(r => r.bytes).Take(TopRoots))
			{
				sb.AppendLine($"  {Mb(r.bytes)}  {r.name}");
			}
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
	}
}
