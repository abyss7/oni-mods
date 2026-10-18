using System;
using System.Collections.Generic;
using HarmonyLib;
using OniCommon;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// The Blackout mode, what the player sees: only what is lit. For each cell the mod keeps what the player sees in it
	/// (0 is darkness, 255 the game's own picture; the brightness grows with the logarithm of the light up to 500 lux), and
	/// the game's fog of war shows no more than that: the fog darkens terrain, buildings and creatures alike, with the same
	/// gradient the game uses for the edge of the explored. (OniFixes, filling the fog natively, takes the values through
	/// the application domain data "OniMods.BlackoutShown.1".)
	///
	/// Which light the player may see:
	/// - of a source the colony made (lamps, the printing pod, rocket engines): while a Duplicant can walk into its light,
	///   checked for each such source about once a second; a light a Duplicant carries (the glow stick) and the glow in the water at the start always;
	/// - of the sun and of everything else (creatures, plants, glowing rocks, things found in the world): only in the cells
	///   a Duplicant sees at that moment (straight lines from their heads, up to SightRadius cells, through anything but
	///   opaque solids), redone several times a second.
	/// The light itself (for plants, work and everything else in the game) is not changed by this: only the player's view.
	///
	/// In a cell the player sees nothing in: the tooltip under the cursor is empty, nothing there can be selected, names,
	/// bars and status icons of things there are hidden, pop-ups are not shown; and dig and build orders can be placed only
	/// in cells with some light (once placed, they stay when the light goes).
	/// </summary>
	internal static unsafe class Darkness
	{
		private const float SightRadius = 32f, SightPeriod = 0.25f, SeenPeriod = 1f;
		private const int SourceCellsChecked = 48;

		private const int Margin = 24; // cells beyond the screen kept up to date, for the next frame's fog when the view moves
		private static byte* shown;
		private static float lastSight = -1f, seenQuota;
		private static readonly Queue<int> seenQueue = new Queue<int>();
		private static readonly int[] sourceCells = new int[SourceCellsChecked];
		private static int[] eyes = new int[64];
		internal static long SightUpdates, SeenChecks, Views;
		internal const string SharedKey = "OniMods.BlackoutShown.1";

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(Darkness), nameof(Frame)) { priority = Priority.Low });
			harmony.Patch(AccessTools.Method(typeof(LightGridManager), nameof(LightGridManager.Shutdown)), prefix: new HarmonyMethod(typeof(Darkness), nameof(Shutdown)));
			harmony.Patch(AccessTools.Method(typeof(PropertyTextures), "UpdateFogOfWar"), prefix: new HarmonyMethod(typeof(Darkness), nameof(FogOfWar)));
			AppDomain.CurrentDomain.SetData(SharedKey, (Func<IntPtr>)(() => (IntPtr)shown));
			// What the player gets to know about dark cells.
			harmony.Patch(AccessTools.Method(typeof(SelectToolHoverTextCard), nameof(SelectToolHoverTextCard.UpdateHoverElements)), prefix: new HarmonyMethod(typeof(Darkness), nameof(NoHoverInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(SelectTool), nameof(SelectTool.Select)), prefix: new HarmonyMethod(typeof(Darkness), nameof(NoSelectInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(NameDisplayScreen), "Culling"), postfix: new HarmonyMethod(typeof(Darkness), nameof(HideNamesInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(StatusItemRenderer.Entry), nameof(StatusItemRenderer.Entry.Render)), prefix: new HarmonyMethod(typeof(Darkness), nameof(NoStatusIconInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(PopFXManager), nameof(PopFXManager.SpawnFX),
				new[] { typeof(Sprite), typeof(Sprite), typeof(string), typeof(Transform), typeof(Vector3), typeof(float), typeof(bool), typeof(bool), typeof(bool) }),
				prefix: new HarmonyMethod(typeof(Darkness), nameof(NoPopUpInTheDark)));
			// Orders only where there is some light.
			harmony.Patch(AccessTools.Method(typeof(DigTool), "OnDragTool"), prefix: new HarmonyMethod(typeof(Darkness), nameof(OrderOnlyInLight)));
			harmony.Patch(AccessTools.Method(typeof(BuildTool), "TryBuild"), prefix: new HarmonyMethod(typeof(Darkness), nameof(OrderOnlyInLight)));
		}

		/// <summary>Whether the player sees nothing in a cell.</summary>
		internal static bool Dark(int cell) => shown != null && Grid.IsValidCell(cell) && shown[cell] == 0;

		private static bool Dark(Vector3 position) => Dark(Grid.PosToCell(position));

		private static bool NoHoverInTheDark()
		{
			int cell = Grid.PosToCell(Camera.main.ScreenToWorldPoint(KInputManager.GetMousePos()));
			if (!Grid.IsValidCell(cell) || !Dark(cell) || HoverTextScreen.Instance == null)
			{
				return true;
			}
			HoverTextScreen.Instance.BeginDrawing().EndDrawing();
			return false;
		}

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
			return __instance.transform == null || !Dark(__instance.transform.GetPosition());
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
				int feet = Grid.PosToCell(minion);
				if (!Grid.IsValidCell(feet))
				{
					continue;
				}
				if (count == eyes.Length)
				{
					Array.Resize(ref eyes, eyes.Length * 2);
				}
				eyes[count++] = Grid.IsValidCell(Grid.CellAbove(feet)) ? Grid.CellAbove(feet) : feet;
			}
			LightNative.GridView view = LightGrid.View();
			fixed (int* e = eyes)
			{
				LightNative.Sight(&view, e, count, SightRadius);
			}
			SightUpdates++;
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
		private static bool Reachable(int id, LightGridManager.LightGridEmitter emitter)
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
			LightNative.UpdateShown(minX, minY, maxX, maxY, Grid.exposedToSunlight, world.currentSunlightIntensity);
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
