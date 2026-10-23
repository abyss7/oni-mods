using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OniCommon;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// The Blackout mode, what the player sees: only what is lit. For each cell the mod keeps what the player sees in it
	/// (0 is darkness, below 1 lux; from a barely seen 4 at 1 lux the brightness grows with the logarithm of the light
	/// up to the game's own picture, 255, at 500 lux), and
	/// the game's fog of war shows no more than that: the fog darkens terrain, buildings and creatures alike, with the same
	/// gradient the game uses for the edge of the explored. (OniFixes, filling the fog natively, takes the values through
	/// the application domain data "OniMods.BlackoutShown.1".)
	///
	/// Which light the player may see:
	/// - of a source the colony made (lamps, the printing pod, rocket engines): while a Duplicant can walk into its light,
	///   checked for each such source about once a second; a light a Duplicant carries (the glow stick) and the glow in the water at the start always;
	/// - of the sun and of everything else (creatures, plants, glowing rocks, things found in the world): only in the cells
	///   a Duplicant sees at that moment (straight lines from their heads, where the animation has them, to any part of a
	///   cell, up to SightRadius cells, through anything but opaque solids), redone ten times a second; what they start to
	///   see shows over 0.3 s, what they stop seeing fades over 0.7 s.
	/// The light itself (for plants, work and everything else in the game) is not changed by this: only the player's view.
	///
	/// A cell the player sees nothing in is to the player as a cell not explored yet: the game takes it so for its tooltips
	/// ("Unknown"), selection, tools (no order of any kind can be given there; orders given while it was lit stay), health
	/// bars, status icons, pop-ups and overlays; besides, names are hidden there, no priorities show, nothing there is
	/// highlighted in an overlay (by the game's check), and nothing there can be selected.
	/// </summary>
	internal static unsafe class Darkness
	{
		private const float SightRadius = 32f, SightPeriod = 0.1f, SeenPeriod = 1f;
		private const int SourceCellsChecked = 48;

		private const int Margin = 24; // cells beyond the screen kept up to date, for the next frame's fog when the view moves
		private static byte* shown;
		private static float lastSight = -1f, seenQuota;
		private static readonly Queue<int> seenQueue = new Queue<int>();
		private static readonly int[] sourceCells = new int[SourceCellsChecked];
		private static float[] eyes = new float[128]; // x, y pairs, in cells
		private static readonly Vector3 HeadAboveFeet = new Vector3(0f, 1.6f), HeadAboveChest = new Vector3(0f, 0.6f);
		internal static long SightUpdates, SeenChecks, Views;
		internal const string SharedKey = "OniMods.BlackoutShown.1";

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(Darkness), nameof(Frame)) { priority = Priority.Low });
			harmony.Patch(AccessTools.Method(typeof(LightGridManager), nameof(LightGridManager.Shutdown)), prefix: new HarmonyMethod(typeof(Darkness), nameof(Shutdown)));
			harmony.Patch(AccessTools.Method(typeof(PropertyTextures), "UpdateFogOfWar"), prefix: new HarmonyMethod(typeof(Darkness), nameof(FogOfWar)));
			AppDomain.CurrentDomain.SetData(SharedKey, (Func<IntPtr>)(() => (IntPtr)shown));
			// What the player gets to know about dark cells: as of cells not explored yet. The game asks Grid.IsVisible for
			// its tooltips (it says "Unknown"), all its tools, selecting a cell, health bars, status icons, pop-ups, the
			// overlays' highlights and more; patched at loading, before anything that calls it is compiled.
			harmony.Patch(AccessTools.Method(typeof(Grid), nameof(Grid.IsVisible)), postfix: new HarmonyMethod(typeof(Darkness), nameof(NotVisibleInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(SelectTool), nameof(SelectTool.Select)), prefix: new HarmonyMethod(typeof(Darkness), nameof(NoSelectInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(NameDisplayScreen), "Culling"), postfix: new HarmonyMethod(typeof(Darkness), nameof(HideNamesInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(StatusItemRenderer.Entry), nameof(StatusItemRenderer.Entry.Render)), prefix: new HarmonyMethod(typeof(Darkness), nameof(NoStatusIconInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(PopFXManager), nameof(PopFXManager.SpawnFX),
				new[] { typeof(Sprite), typeof(Sprite), typeof(string), typeof(Transform), typeof(Vector3), typeof(float), typeof(bool), typeof(bool), typeof(bool) }),
				prefix: new HarmonyMethod(typeof(Darkness), nameof(NoPopUpInTheDark)));
			// Overlays: priorities, objects highlighted or brought forward, their icons, only where the player sees something.
			harmony.Patch(AccessTools.Method(typeof(PrioritizableRenderer), "renderEveryTickVisitHelper"), prefix: new HarmonyMethod(typeof(Darkness), nameof(NoPriorityInTheDark)));
			// Not patched: OverlayModes.Mode.AddTargetIfVisible<T> (it reads the explored state itself). A patch of a generic method
			// replaces the code all reference types share with one typed for a single T, and the other overlays then crash with
			// an invalid cast (the crop overlay did). Objects brought forward in an overlay are drawn under the fog anyway.
			// The icons of a building's ports in the overlays (pipes, wires, heat sources and sinks): not in the dark.
			harmony.Patch(AccessTools.Method(typeof(EntityCellVisualizer), nameof(EntityCellVisualizer.DrawIcons)), prefix: new HarmonyMethod(typeof(Darkness), nameof(NoPortIconsInTheDark)));
			// Orders only where there is some light.
			harmony.Patch(AccessTools.Method(typeof(DigTool), "OnDragTool"), prefix: new HarmonyMethod(typeof(Darkness), nameof(OrderOnlyInLight)));
			harmony.Patch(AccessTools.Method(typeof(BuildTool), "TryBuild"), prefix: new HarmonyMethod(typeof(Darkness), nameof(OrderOnlyInLight)));
		}

		/// <summary>What the player sees in a cell, 0-255 (0: nothing, or not computed yet).</summary>
		internal static byte ShownAt(int cell) => shown != null && Grid.IsValidCell(cell) ? shown[cell] : (byte)0;

		/// <summary>Whether the player sees nothing in a cell.</summary>
		internal static bool Dark(int cell) => shown != null && Grid.IsValidCell(cell) && shown[cell] == 0;

		private static bool Dark(Vector3 position) => Dark(Grid.PosToCell(position));

		private static bool NoSelectInTheDark(KSelectable new_selected)
		{
			return new_selected == null || !Dark(new_selected.transform.GetPosition());
		}

		private static void HideNamesInTheDark(NameDisplayScreen __instance)
		{
			foreach (NameDisplayScreen.Entry entry in __instance.entries)
			{
				if (entry.visible && entry.world_go != null && Dark(entry.world_go.transform.GetPosition()))
				{
					entry.display_go.SetActive(false);
					entry.visible = false;
				}
			}
		}

		private static bool NoStatusIconInTheDark(ref StatusItemRenderer.Entry __instance)
		{
			if (__instance.transform == null)
			{
				return true;
			}
			// Where the game puts the icons: over a building's middle, otherwise at the object.
			Vector3 at = __instance.isBuilding ? __instance.buildingPos : __instance.transform.GetPosition();
			if (__instance.isBuilding && __instance.building != null) at.x += (float)((__instance.building.Def.WidthInCells - 1) % 2) / 2f;
			return !Dark(at);
		}

		private static bool NoPopUpInTheDark(Transform target_transform, Vector3 offset, ref PopFX __result)
		{
			Vector3 position = offset + (target_transform != null ? target_transform.GetPosition() : Vector3.zero);
			if (!Dark(position))
			{
				return true;
			}
			__result = null;
			return false;
		}

		private static bool NoPriorityInTheDark(object obj, ref Util.IterationInstruction __result)
		{
			if (obj is Prioritizable prioritizable && prioritizable != null && Dark(prioritizable.transform.GetPosition()))
			{
				__result = Util.IterationInstruction.Continue;
				return false;
			}
			return true;
		}

		/// <summary>A cell the player sees nothing in counts as not explored yet.</summary>
		private static void NotVisibleInTheDark(int cell, ref bool __result)
		{
			if (__result && Dark(cell))
			{
				__result = false;
			}
		}

		/// <summary>Whether the player sees nothing in any cell of an object.</summary>
		internal static bool Hidden(GameObject go)
		{
			if (shown == null || go == null)
			{
				return false;
			}
			int origin = Grid.PosToCell(go);
			OccupyArea area = go.GetComponent<OccupyArea>();
			if (area == null || area.OccupiedCellsOffsets == null || area.OccupiedCellsOffsets.Length == 0)
			{
				return Dark(origin);
			}
			foreach (CellOffset offset in area.OccupiedCellsOffsets)
			{
				int cell = Grid.OffsetCell(origin, offset);
				if (Grid.IsValidCell(cell) && !Dark(cell)) return false;
			}
			return true;
		}

		// Which port icons were hidden for the dark when last drawn: drawn again when that changes (the game draws them
		// only when the overlay changes).
		private static readonly Dictionary<EntityCellVisualizer, bool> portsHidden = new Dictionary<EntityCellVisualizer, bool>();
		private static float lastPortCheck;

		private static void NoPortIconsInTheDark(EntityCellVisualizer __instance, ref HashedString mode)
		{
			if (Game.Instance != null && __instance == Game.Instance.previewVisualizer)
			{
				return;
			}
			bool hidden = Hidden(__instance.gameObject);
			portsHidden[__instance] = hidden;
			if (hidden)
			{
				mode = OverlayModes.None.ID;
			}
		}

		private static void RefreshPortIcons()
		{
			if (OverlayScreen.Instance == null || Time.unscaledTime < lastPortCheck + 0.5f)
			{
				return;
			}
			lastPortCheck = Time.unscaledTime;
			HashedString mode = OverlayScreen.Instance.GetMode();
			if (mode == OverlayModes.None.ID)
			{
				portsHidden.Clear();
				return;
			}
			foreach (EntityCellVisualizer visualizer in Components.EntityCellVisualizers.Items)
			{
				if (visualizer != null && portsHidden.TryGetValue(visualizer, out bool was) && was != Hidden(visualizer.gameObject))
				{
					visualizer.DrawIcons(mode);
				}
			}
		}

		private static bool OrderOnlyInLight(int cell)
		{
			return !Dark(cell);
		}

		private static void Shutdown()
		{
			seenQueue.Clear();
			lastSight = -1f;
			shown = null;
		}

		private static void Frame()
		{
			if (!LightGrid.Ready || ClusterManager.Instance == null)
			{
				return;
			}
			try
			{
				if (Time.unscaledTime >= lastSight + SightPeriod)
				{
					lastSight = Time.unscaledTime;
					UpdateSight();
				}
				UpdateSeen();
				UpdateShown();
				RefreshPortIcons();
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} darkness: {e}");
			}
		}

		/// <summary>The heads of the active world's Duplicants: what they see now.</summary>
		private static void UpdateSight()
		{
			int world = ClusterManager.Instance.activeWorldId, count = 0;
			foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
			{
				if (minion == null || minion.GetMyWorldId() != world)
				{
					continue;
				}
				if (!Grid.IsValidCell(Grid.PosToCell(minion)))
				{
					continue;
				}
				if (2 * count + 2 > eyes.Length)
				{
					Array.Resize(ref eyes, eyes.Length * 2);
				}
				Vector3 head = Head(minion);
				eyes[2 * count] = head.x;
				eyes[2 * count + 1] = head.y;
				count++;
			}
			LightNative.GridView view = LightGrid.View();
			fixed (float* e = eyes)
			{
				LightNative.Sight(&view, e, count, SightRadius);
			}
			SightUpdates++;
		}

		/// <summary>
		/// Where a Duplicant's eyes are (world position: a cell is a unit): above the chest as the animation has it (getting
		/// on a ladder, jumping, climbing: the body moves before the position does), otherwise above the feet; never inside
		/// a solid cell (then the head's cell).
		/// </summary>
		internal static Vector3 Head(MinionIdentity minion)
		{
			Vector3 position = minion.transform.GetPosition(), head = position + HeadAboveFeet;
			if (Body.ChestCentre(minion.GetComponent<KBatchedAnimController>(), out Vector3 chest))
			{
				head = chest + HeadAboveChest;
			}
			int cell = Grid.PosToCell(head);
			if (!Grid.IsValidCell(cell) || Grid.Solid[cell])
			{
				int feet = Grid.PosToCell(position);
				cell = Grid.IsValidCell(Grid.CellAbove(feet)) ? Grid.CellAbove(feet) : feet;
				head = Grid.CellToPosCCC(cell, Grid.SceneLayer.Move);
			}
			return head;
		}

		/// <summary>The colony's sources, a share per frame so that each is checked about every SeenPeriod seconds.</summary>
		private static void UpdateSeen()
		{
			int total = 0;
			foreach (int _ in LightGrid.ActiveIds) total++;
			if (total == 0)
			{
				return;
			}
			seenQuota += total * Time.unscaledDeltaTime / SeenPeriod;
			while (seenQuota >= 1f)
			{
				seenQuota -= 1f;
				if (seenQueue.Count == 0)
				{
					foreach (int id in LightGrid.ActiveIds) seenQueue.Enqueue(id);
				}
				int next = seenQueue.Dequeue();
				if (LightGrid.TryOwner(next, out LightGridManager.LightGridEmitter emitter) && !LightGrid.IsNatural(emitter))
				{
					LightNative.SetSeen(next, LightGrid.IsCarried(emitter) || Reachable(next, emitter) ? 1 : 0);
					SeenChecks++;
				}
			}
		}

		/// <summary>Whether a Duplicant of the source's world can walk into one of the cells it lights.</summary>
		internal static bool Reachable(int id, LightGridManager.LightGridEmitter emitter)
		{
			int origin = emitter.state.origin;
			if (!Grid.IsValidCell(origin))
			{
				return false;
			}
			int world = Grid.WorldIdx[origin], count;
			fixed (int* cells = sourceCells)
			{
				count = LightNative.SourceCells(id, cells, sourceCells.Length);
			}
			foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
			{
				if (minion == null || minion.GetMyWorldId() != world)
				{
					continue;
				}
				Navigator navigator = minion.GetComponent<Navigator>();
				if (navigator == null)
				{
					continue;
				}
				for (int i = 0; i < count; i++)
				{
					if (navigator.CanReach(sourceCells[i]))
					{
						return true;
					}
				}
			}
			return false;
		}

		/// <summary>What the player sees in the cells in view and a margin around them.</summary>
		private static void UpdateShown()
		{
			WorldContainer world = ClusterManager.Instance.activeWorld;
			if (world == null)
			{
				return;
			}
			Grid.GetVisibleExtents(out int minX, out int minY, out int maxX, out int maxY);
			minX = Math.Max(minX - Margin, world.WorldOffset.x);
			minY = Math.Max(minY - Margin, world.WorldOffset.y);
			maxX = Math.Min(maxX + Margin, world.WorldOffset.x + world.WorldSize.x - 1);
			maxY = Math.Min(maxY + Margin, world.WorldOffset.y + world.WorldSize.y - 1);
			if (maxX < minX || maxY < minY)
			{
				return;
			}
			LightNative.UpdateShown(minX, minY, maxX, maxY, Grid.exposedToSunlight, world.currentSunlightIntensity, Mathf.Min(Time.unscaledDeltaTime, 0.1f));
			shown = LightNative.Shown();
			Views++;
		}

		/// <summary>The game's fog of war (when OniFixes does not fill it natively): no more than the player sees.</summary>
		private static bool FogOfWar(TextureRegion region, int x0, int y0, int x1, int y1)
		{
			byte* seen = shown;
			if (seen == null)
			{
				return true;
			}
			byte[] visible = Grid.Visible;
			int top = Grid.HeightInCells;
			if (ClusterManager.Instance != null)
			{
				WorldContainer activeWorld = ClusterManager.Instance.activeWorld;
				top = activeWorld.WorldSize.y + activeWorld.WorldOffset.y - 1;
			}
			for (int y = y0; y <= y1; y++)
			{
				for (int x = x0; x <= x1; x++)
				{
					int cell = Grid.XYToCell(x, y);
					// As the game: a cell of another world shows the cell of the active world's top row in its column.
					int from = Grid.IsActiveWorld(cell) ? cell : Grid.XYToCell(x, top);
					byte value = Grid.IsValidCell(from) ? visible[from] : (byte)0;
					if (Grid.IsValidCell(from) && seen[from] < value) value = seen[from];
					region.SetBytes(x, y, value);
				}
			}
			return false;
		}
	}
}
