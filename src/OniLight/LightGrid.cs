using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// Puts the game's lights through the light engine (LightNative) instead of the game's shadow casting: every
	/// LightGridEmitter becomes a source of the engine, which writes the game's light grid (Grid.LightCount) itself, so
	/// everything that reads light (plants, Duplicants' work bonus, the light overlay) sees the new light.
	///
	/// - AddToGrid adds the emitter's source; RemoveFromGrid takes it out, keeping its light until the end of the frame, so
	///   that a light bug that only changes brightness (removed and added again in one frame) is rescaled, not traced.
	///   UpdateLitCells (which the game runs in worker threads for light bugs) does nothing: the engine traces at add.
	/// - A light watches the cells it can reach (the game refreshes it when one of them changes): its area is widened to
	///   what the new light reaches, and a refresh asked for by changed cells is done once per frame, tracing it again.
	/// - Built back walls (drywall and the like) are not in the game's grid: their elements are kept here, and the lights
	///   around one are refreshed when it is built or removed.
	/// - The placement preview of a lamp shows the new direct light.
	/// - Liquid moves without telling the lights (a cell's mass changes, its element does not), and so do gases that dim light
	///   and mixtures (Mixtures): a light with any of them in its area is traced again every WetPeriod seconds, a few such
	///   lights per frame; and every light is traced again every SweepPeriod seconds, for whatever else changed unannounced.
	/// </summary>
	internal static unsafe class LightGrid
	{
		private static bool ready;
		private static readonly Dictionary<LightGridManager.LightGridEmitter, int> ids = new Dictionary<LightGridManager.LightGridEmitter, int>();
		private static readonly Dictionary<int, LightGridManager.LightGridEmitter> owners = new Dictionary<int, LightGridManager.LightGridEmitter>();
		private static readonly HashSet<int> active = new HashSet<int>();
		private static readonly Stack<int> freeIds = new Stack<int>();
		private static readonly List<int> takenOut = new List<int>();
		private static readonly List<LightGridManager.LightGridEmitter> waiting = new List<LightGridManager.LightGridEmitter>();
		private static readonly HashSet<Light2D> toRefresh = new HashSet<Light2D>();
		private static readonly HashSet<int> wet = new HashSet<int>();
		private static readonly Queue<int> wetQueue = new Queue<int>();
		private const float WetPeriod = 2f, SweepPeriod = 10f;
		private static float wetQuota, sweepQuota;
		private static readonly Queue<int> sweepQueue = new Queue<int>();
		private static int nextId;
		private static ushort[] builtBackwall = new ushort[0];
		private static readonly int[] previewCells = new int[1 << 16], previewLux = new int[1 << 16];

		// Time spent in the engine (main thread), for the log and the measurements.
		internal static long AddTicks, RemoveTicks, FreeTicks, PreviewTicks, Adds, Removes, Frees, Previews, Refreshes, WetRetraces;
		private static float lastReport;

		internal static void Apply(Harmony harmony)
		{
			Type emitter = typeof(LightGridManager.LightGridEmitter);
			harmony.Patch(AccessTools.Method(typeof(LightGridManager), nameof(LightGridManager.Initialise)), postfix: new HarmonyMethod(typeof(LightGrid), nameof(Initialise)));
			harmony.Patch(AccessTools.Method(typeof(LightGridManager), nameof(LightGridManager.Shutdown)), prefix: new HarmonyMethod(typeof(LightGrid), nameof(Shutdown)));
			harmony.Patch(AccessTools.Method(emitter, nameof(LightGridManager.LightGridEmitter.AddToGrid)), prefix: new HarmonyMethod(typeof(LightGrid), nameof(AddToGrid)));
			harmony.Patch(AccessTools.Method(emitter, nameof(LightGridManager.LightGridEmitter.RemoveFromGrid)), prefix: new HarmonyMethod(typeof(LightGrid), nameof(RemoveFromGrid)));
			harmony.Patch(AccessTools.Method(emitter, nameof(LightGridManager.LightGridEmitter.UpdateLitCells)), prefix: new HarmonyMethod(typeof(LightGrid), nameof(UpdateLitCells)));
			harmony.Patch(AccessTools.Method(typeof(LightGridManager), nameof(LightGridManager.CreatePreview),
				new[] { typeof(int), typeof(float), typeof(LightShape), typeof(int), typeof(int), typeof(DiscreteShadowCaster.Direction) }),
				prefix: new HarmonyMethod(typeof(LightGrid), nameof(CreatePreview)));
			harmony.Patch(AccessTools.DeclaredMethod(typeof(Light2D), "ComputeExtents"), postfix: new HarmonyMethod(typeof(LightGrid), nameof(WidenExtents)));
			harmony.Patch(AccessTools.DeclaredMethod(typeof(Light2D), "OnWorldChanged"), prefix: new HarmonyMethod(typeof(LightGrid), nameof(OnWorldChanged)));
			harmony.Patch(AccessTools.DeclaredMethod(typeof(BuildingComplete), "OnSpawn"), postfix: new HarmonyMethod(typeof(LightGrid), nameof(BuildingSpawned)));
			harmony.Patch(AccessTools.DeclaredMethod(typeof(BuildingComplete), "OnCleanUp"), prefix: new HarmonyMethod(typeof(LightGrid), nameof(BuildingRemoved)));
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(LightGrid), nameof(Frame)));
		}

		internal static bool Ready => ready && Grid.elementIdx != null && Grid.mass != null && Grid.properties != null && Grid.LightCount != null;

		// The managed arrays the engine reads and writes, pinned while a game is loaded.
		private static GCHandle pinnedMasks, pinnedBuilt, pinnedLight, pinnedMix;
		private static object pinnedFor;

		private static void Pin()
		{
			Unpin();
			pinnedMasks = GCHandle.Alloc(Grid.BuildMasks, GCHandleType.Pinned);
			pinnedBuilt = GCHandle.Alloc(builtBackwall, GCHandleType.Pinned);
			pinnedLight = GCHandle.Alloc(Grid.LightCount, GCHandleType.Pinned);
			if (Mixtures.Through != null) pinnedMix = GCHandle.Alloc(Mixtures.Through, GCHandleType.Pinned);
			pinnedFor = Grid.LightCount;
		}

		private static void Unpin()
		{
			if (pinnedMasks.IsAllocated) pinnedMasks.Free();
			if (pinnedBuilt.IsAllocated) pinnedBuilt.Free();
			if (pinnedLight.IsAllocated) pinnedLight.Free();
			if (pinnedMix.IsAllocated) pinnedMix.Free();
			pinnedFor = null;
		}

		/// <summary>The game's grid for the engine.</summary>
		internal static LightNative.GridView View()
		{
			if (!ReferenceEquals(pinnedFor, Grid.LightCount))
			{
				Pin(); // the game made new arrays
			}
			return new LightNative.GridView
			{
				width = Grid.WidthInCells,
				height = Grid.HeightInCells,
				elementIdx = Grid.elementIdx,
				mass = Grid.mass,
				properties = Grid.properties,
				backwallElementIdx = BackwallManager.element_idx,
				buildMasks = (byte*)pinnedMasks.AddrOfPinnedObject(),
				builtBackwall = (ushort*)pinnedBuilt.AddrOfPinnedObject(),
				lightCount = (int*)pinnedLight.AddrOfPinnedObject(),
				temperature = Grid.temperature,
				mixThrough = pinnedMix.IsAllocated ? (float*)pinnedMix.AddrOfPinnedObject() : null,
			};
		}

		private static void Initialise()
		{
			ids.Clear();
			owners.Clear();
			active.Clear();
			freeIds.Clear();
			takenOut.Clear();
			waiting.Clear();
			toRefresh.Clear();
			naturalOf.Clear();
			wet.Clear();
			wetQueue.Clear();
			wetQuota = sweepQuota = 0f;
			sweepQueue.Clear();
			nextId = 0;
			AddTicks = RemoveTicks = FreeTicks = PreviewTicks = Adds = Removes = Frees = Previews = Refreshes = WetRetraces = 0;
			builtBackwall = new ushort[Grid.CellCount];
			for (int i = 0; i < builtBackwall.Length; i++)
			{
				builtBackwall[i] = ushort.MaxValue;
			}
			LightNative.Init(Grid.WidthInCells, Grid.HeightInCells);
			LightNative.ElementOptics[] optics = Reflectance.Optics();
			Mixtures.Initialise(optics);
			Pin();
			fixed (LightNative.ElementOptics* table = optics)
			{
				LightNative.Elements(table, optics.Length);
			}
			ready = true;
			Debug.Log($"{ModContext.Tag} light grid {Grid.WidthInCells} x {Grid.HeightInCells}, {optics.Length} elements; reflectance from other mods: " +
				$"{Reflectance.FromFiles} by file, {Reflectance.FromCode} by code; {Reflectance.ByCategoryCount} solids by category; mixtures from the mods: {Mixtures.Sources}");
		}

		private static void Shutdown()
		{
			if (ready)
			{
				Report("at the end");
			}
			ready = false;
			Unpin();
		}

		private static LightNative.SourceDesc Desc(LightGridManager.LightGridEmitter.State state, bool natural = false)
		{
			Grid.CellToXY(state.origin, out int x, out int y);
			return new LightNative.SourceDesc
			{
				x = x,
				y = y,
				lux = state.intensity,
				range = state.radius,
				falloff = state.falloffRate,
				shape = (int)state.shape,
				direction = (int)state.direction,
				width = state.width,
				natural = natural ? 1 : 0,
			};
		}

		// Whether a light is not made by the colony: the player sees its light only where a Duplicant sees (Darkness).
		private static readonly Dictionary<LightGridManager.LightGridEmitter, bool> naturalOf = new Dictionary<LightGridManager.LightGridEmitter, bool>();

		internal static bool IsNatural(LightGridManager.LightGridEmitter emitter)
		{
			if (naturalOf.TryGetValue(emitter, out bool natural))
			{
				return natural;
			}
			Light2D owner = null;
			foreach (Light2D light in Components.Light2Ds.Items)
			{
				if (light != null && light.emitter == emitter)
				{
					owner = light;
					break;
				}
			}
			natural = !MadeByColony(owner);
			if (owner != null)
			{
				naturalOf[emitter] = natural;
			}
			return natural;
		}

		/// <summary>A building the colony builds (from the build menu, a rocket module) or the printing pod.</summary>
		private static bool MadeByColony(Light2D light)
		{
			if (light == null)
			{
				return false;
			}
			BuildingComplete building = light.GetComponent<BuildingComplete>();
			if (building == null || building.Def == null)
			{
				return false; // creatures, plants, items
			}
			return building.Def.ShowInBuildMenu || light.GetComponent<Telepad>() != null || light.GetComponent<RocketModuleCluster>() != null;
		}

		internal static IEnumerable<int> ActiveIds => active;

		internal static bool TryOwner(int id, out LightGridManager.LightGridEmitter emitter) => owners.TryGetValue(id, out emitter);

		private static int IdFor(LightGridManager.LightGridEmitter emitter)
		{
			if (ids.TryGetValue(emitter, out int id))
			{
				return id;
			}
			id = freeIds.Count > 0 ? freeIds.Pop() : nextId++;
			ids[emitter] = id;
			owners[id] = emitter;
			return id;
		}

		private static bool AddToGrid(LightGridManager.LightGridEmitter __instance)
		{
			if (!ready)
			{
				return true;
			}
			if (!Ready)
			{
				// The grid's data is not there yet (an early spawn while loading): added on the first frame.
				if (!waiting.Contains(__instance))
				{
					waiting.Add(__instance);
				}
				return false;
			}
			__instance.litCells.Clear();
			LightGridManager.LightGridEmitter.State state = __instance.state;
			if (!Grid.IsValidCell(state.origin))
			{
				return false;
			}
			int id = IdFor(__instance);
			LightNative.SourceDesc desc = Desc(state, IsNatural(__instance));
			LightNative.GridView view = View();
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			LightNative.Add(&view, id, &desc);
			AddTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
			Adds++;
			active.Add(id);
			if (LightNative.Wet(id) != 0) wet.Add(id);
			else wet.Remove(id);
			return false;
		}

		private static bool RemoveFromGrid(LightGridManager.LightGridEmitter __instance)
		{
			if (!ready)
			{
				return true;
			}
			waiting.Remove(__instance);
			__instance.litCells.Clear();
			if (ids.TryGetValue(__instance, out int id) && active.Remove(id) && Ready)
			{
				LightNative.GridView view = View();
				long start = System.Diagnostics.Stopwatch.GetTimestamp();
				LightNative.Remove(&view, id, 1);
				RemoveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
				Removes++;
				takenOut.Add(id);
			}
			return false;
		}

		private static bool UpdateLitCells()
		{
			return !ready;
		}

		private static bool CreatePreview(int origin_cell, float radius, LightShape shape, int lux, int width, DiscreteShadowCaster.Direction direction)
		{
			if (!Ready)
			{
				return !ready;
			}
			LightGridManager.previewLightCells.Clear();
			if (!Grid.IsValidCell(origin_cell))
			{
				return false;
			}
			LightNative.SourceDesc desc = Desc(new LightGridManager.LightGridEmitter.State
			{
				origin = origin_cell,
				shape = shape,
				width = width,
				direction = direction,
				radius = radius,
				intensity = lux,
				falloffRate = 0.5f,
			});
			int count;
			LightNative.GridView view = View();
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			fixed (int* cells = previewCells)
			fixed (int* values = previewLux)
			{
				count = LightNative.Preview(&view, &desc, cells, values, previewCells.Length);
			}
			PreviewTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
			Previews++;
			for (int i = 0; i < count; i++)
			{
				LightGridManager.previewLightCells.Add(new Tuple<int, int>(previewCells[i], previewLux[i]));
				LightGridManager.previewLux[previewCells[i]] = previewLux[i];
			}
			return false;
		}

		/// <summary>How far from its cell a light's area must reach: its light and the light it makes reflect.</summary>
		internal static int ReachOf(float range, int width)
		{
			return LightNative.Reach(range) + Mathf.Max(0, width) / 2 + 1;
		}

		private static void WidenExtents(Light2D __instance, ref Extents __result)
		{
			if (!ready || !Grid.IsValidCell(__instance.origin))
			{
				return;
			}
			int reach = ReachOf(__instance.Range, __instance.shape == LightShape.Quad ? __instance.Width : 0);
			Grid.CellToXY(__instance.origin, out int x, out int y);
			__result = new Extents(x - reach, y - reach, 2 * reach + 1, 2 * reach + 1);
		}

		private static bool OnWorldChanged(Light2D __instance)
		{
			if (!ready)
			{
				return true;
			}
			toRefresh.Add(__instance);
			return false;
		}

		private static bool IsBackwall(BuildingComplete building)
		{
			return building != null && building.Def != null && building.Def.ObjectLayer == ObjectLayer.Backwall;
		}

		private static void BuildingSpawned(BuildingComplete __instance)
		{
			if (!ready || !IsBackwall(__instance))
			{
				return;
			}
			PrimaryElement element = __instance.GetComponent<PrimaryElement>();
			ushort index = element == null ? ushort.MaxValue : (ushort)ElementLoader.GetElementIndex(element.ElementID);
			SetBackwall(__instance, index);
		}

		private static void BuildingRemoved(BuildingComplete __instance)
		{
			if (ready && IsBackwall(__instance))
			{
				SetBackwall(__instance, ushort.MaxValue);
			}
		}

		private static void SetBackwall(BuildingComplete building, ushort element)
		{
			int[] cells = building.PlacementCells;
			if (cells == null)
			{
				return;
			}
			foreach (int cell in cells)
			{
				if (cell >= 0 && cell < builtBackwall.Length)
				{
					builtBackwall[cell] = element;
					RefreshAround(cell);
				}
			}
		}

		/// <summary>Refreshes (this frame) the lights whose light may reach a cell.</summary>
		internal static void RefreshAround(int cell)
		{
			Grid.CellToXY(cell, out int cx, out int cy);
			foreach (Light2D light in Components.Light2Ds.Items)
			{
				if (light == null || !Grid.IsValidCell(light.origin))
				{
					continue;
				}
				Grid.CellToXY(light.origin, out int x, out int y);
				int reach = ReachOf(light.Range, light.shape == LightShape.Quad ? light.Width : 0);
				if (Math.Abs(x - cx) <= reach && Math.Abs(y - cy) <= reach)
				{
					toRefresh.Add(light);
				}
			}
		}

		private static void Frame()
		{
			if (!ready || !Ready)
			{
				return;
			}
			if (waiting.Count > 0)
			{
				var list = new List<LightGridManager.LightGridEmitter>(waiting);
				waiting.Clear();
				foreach (LightGridManager.LightGridEmitter emitter in list)
				{
					AddToGrid(emitter);
				}
			}
			if (toRefresh.Count > 0)
			{
				var list = new List<Light2D>(toRefresh);
				toRefresh.Clear();
				foreach (Light2D light in list)
				{
					if (light != null && light.isSpawned && light.isActiveAndEnabled)
					{
						// The cells changed: its light is traced again (not kept for a rescale, which assumes they did not).
						if (ids.TryGetValue(light.emitter, out int id) && active.Remove(id))
						{
							LightNative.GridView view = View();
							long start = System.Diagnostics.Stopwatch.GetTimestamp();
							LightNative.Remove(&view, id, 0);
							RemoveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
							Removes++;
							takenOut.Add(id);
						}
						light.FullRefresh();
						Refreshes++;
					}
				}
			}
			Mixtures.Update();
			// Lights with liquid in their area, a share per frame so that each is traced again every WetPeriod seconds.
			if (wet.Count > 0)
			{
				wetQuota += wet.Count * Time.unscaledDeltaTime / WetPeriod;
				while (wetQuota >= 1f)
				{
					if (wetQueue.Count == 0)
					{
						foreach (int id in wet) wetQueue.Enqueue(id);
					}
					wetQuota -= 1f;
					Retrace(wetQueue.Dequeue());
				}
			}
			// Every light, slowly: whatever changed without telling the lights (gas drifting in, a natural back wall) is caught
			// within SweepPeriod seconds.
			if (active.Count > 0)
			{
				sweepQuota += active.Count * Time.unscaledDeltaTime / SweepPeriod;
				while (sweepQuota >= 1f)
				{
					if (sweepQueue.Count == 0)
					{
						foreach (int id in active) sweepQueue.Enqueue(id);
					}
					sweepQuota -= 1f;
					Retrace(sweepQueue.Dequeue());
				}
			}
			// What was taken out this frame and not put back is dropped.
			if (takenOut.Count > 0)
			{
				long start = System.Diagnostics.Stopwatch.GetTimestamp();
				foreach (int id in takenOut)
				{
					if (active.Contains(id) || !owners.TryGetValue(id, out LightGridManager.LightGridEmitter emitter))
					{
						continue;
					}
					LightNative.Free(id);
					wet.Remove(id);
					owners.Remove(id);
					naturalOf.Remove(emitter);
					ids.Remove(emitter);
					freeIds.Push(id);
					Frees++;
				}
				takenOut.Clear();
				FreeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
			}
			if (Time.unscaledTime > lastReport + 300f)
			{
				lastReport = Time.unscaledTime;
				Report("so far");
			}
		}

		/// <summary>Traces a source again from the grid as it is now (its emitter's current state).</summary>
		private static void Retrace(int id)
		{
			if (!active.Contains(id) || !owners.TryGetValue(id, out LightGridManager.LightGridEmitter emitter) || !Grid.IsValidCell(emitter.state.origin))
			{
				return;
			}
			LightNative.SourceDesc desc = Desc(emitter.state, IsNatural(emitter));
			LightNative.GridView view = View();
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			LightNative.Remove(&view, id, 0);
			LightNative.Add(&view, id, &desc);
			AddTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
			WetRetraces++;
			if (LightNative.Wet(id) == 0) wet.Remove(id);
			else wet.Add(id);
		}

		/// <summary>Traces every source again (for the self-check).</summary>
		internal static void RetraceAll()
		{
			foreach (int id in new List<int>(active))
			{
				Retrace(id);
			}
		}

		internal static void Report(string when)
		{
			long* s = stackalloc long[7];
			LightNative.Stats(s);
			double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			Debug.Log($"{ModContext.Tag} light {when}: {active.Count} sources; adds {Adds} ({AddTicks * ms:F1} ms, {(Adds > 0 ? AddTicks * ms * 1000.0 / Adds : 0):F1} us each), " +
				$"removes {Removes} ({RemoveTicks * ms:F1} ms), frees {Frees} ({FreeTicks * ms:F1} ms), previews {Previews} ({PreviewTicks * ms:F1} ms), " +
				$"refreshes asked by cells {Refreshes}, retraces for liquid and the sweep {WetRetraces} ({wet.Count} lights with liquid or gas that dims); mixtures {Mixtures.GasCells} gas and {Mixtures.LiquidCells} liquid cells dimming light (table redone {Mixtures.Updates} times); engine: traces {s[0]}, rescales {s[1]}, transport cell-steps {s[5]}");
		}
	}
}
