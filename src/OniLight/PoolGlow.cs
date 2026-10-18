using System;
using System.Collections.Generic;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// The Blackout mode's help at the start (option): to find the water quickly. When the first Duplicants' New Hope
	/// begins, a glow is put at the bottom of the clean water pool nearest the printing pod, as if a glow stick had been
	/// dropped there, and it stays in that cell (whatever happens to the water). Like the Duplicants' sticks: 600 lux to 4
	/// cells, only the game's own picture of a light (the glow and rays, neon blue). Its light shows to the player whether or
	/// not a Duplicant can reach it (LightGrid: always seen). Once a Duplicant has seen it, it has done its job and fades out
	/// over FoundFade seconds; if nobody finds it, it fades with New Hope over its last half cycle. Then it is gone for good.
	///
	/// Its state (cell, when found, gone) is saved with the Duplicants' glow sticks (GlowStick), whose New Hope it follows.
	/// </summary>
	internal sealed class PoolGlow : MonoBehaviour
	{
		internal static readonly Color Colour = new Color(0.1f, 0.55f, 1f);
		internal const float FoundFade = 60f;
		private const float Period = 0.25f, SearchPeriod = 10f, PoolMass = 1000f;
		private const int MaxPoolCells = 4000;

		private static PoolGlow instance;
		private static float lastUpdate = -1000f, lastSearch = -1000f;
		private static Light2D light;
		private static float shown = -1f;
		internal static long Searches;

		/// <summary>The cell of the glow while it is there (invalid: none now).</summary>
		internal static int Cell => instance != null ? State().cell : Grid.InvalidCell;

		/// <summary>When a Duplicant first saw the glow, game time (-1: not yet).</summary>
		internal static float FoundAt => State().found;

		internal static bool Gone => State().gone;

		/// <summary>How much light the glow gives now, 0..1.</summary>
		internal static float Charge { get; private set; }

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(PoolGlow), nameof(Frame)) { priority = Priority.Low });
			harmony.Patch(AccessTools.Method(typeof(LightGridManager), nameof(LightGridManager.Shutdown)), prefix: new HarmonyMethod(typeof(PoolGlow), nameof(Shutdown)));
		}

		private static void Shutdown()
		{
			Remove();
			lastUpdate = lastSearch = -1000f;
			Charge = 0f;
		}

		/// <summary>The saved state, from the first living Duplicant with a glow stick that has one.</summary>
		private static (int cell, float found, bool gone) State()
		{
			foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
			{
				GlowStick stick = minion != null ? minion.GetComponent<GlowStick>() : null;
				if (stick != null && (stick.poolGone || stick.poolCell >= 0)) return (stick.poolCell, stick.poolFound, stick.poolGone);
			}
			return (-1, -1f, false);
		}

		private static void Save(int cell, float found, bool gone)
		{
			foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
			{
				GlowStick stick = minion != null ? minion.GetComponent<GlowStick>() : null;
				if (stick == null) continue;
				stick.poolCell = cell;
				stick.poolFound = found;
				stick.poolGone = gone;
			}
		}

		private static void Frame()
		{
			if (!LightGrid.Ready || GameClock.Instance == null || Time.unscaledTime < lastUpdate + Period)
			{
				return;
			}
			lastUpdate = Time.unscaledTime;
			try
			{
				(int cell, float found, bool gone) = State();
				float hope = GlowStick.ChargeFor(GlowStick.HopeLeft());
				if (gone || (hope <= 0f && cell < 0))
				{
					Remove();
					Charge = 0f;
					return;
				}
				if (cell < 0)
				{
					// Put once, when New Hope begins.
					if (Time.unscaledTime < lastSearch + SearchPeriod)
					{
						return;
					}
					lastSearch = Time.unscaledTime;
					cell = FindPool();
					if (!Grid.IsValidCell(cell))
					{
						return;
					}
					Save(cell, -1f, false);
				}
				float now = GameClock.Instance.GetTime();
				// Found: a Duplicant sees its cell (the sight of Blackout, through water too).
				if (found < 0f && instance != null && InSight(cell))
				{
					found = now;
					Save(cell, found, false);
					Grid.CellToXY(cell, out int fx, out int fy);
					Debug.Log($"{ModContext.Tag} pool glow: found at {fx},{fy}; fading out over {FoundFade:F0} s");
				}
				float charge = hope;
				if (found >= 0f)
				{
					float t = 1f - (now - found) / FoundFade;
					charge = Mathf.Min(charge, t <= 0f ? 0f : t >= 1f ? 1f : t * t * (3f - 2f * t));
				}
				Charge = charge;
				if (charge <= 0f)
				{
					Remove();
					Save(cell, found, true);
					Debug.Log($"{ModContext.Tag} pool glow: out for good");
					return;
				}
				if (instance == null)
				{
					Create(cell);
				}
				// The game's picture dims with it, the light follows in steps of 1%.
				light.IntensityAnimation = charge;
				light.overlayColour = new Color(Colour.r * charge, Colour.g * charge, Colour.b * charge, charge);
				if (Mathf.Abs(charge - shown) >= 0.01f)
				{
					shown = charge;
					light.Lux = Mathf.Max(1, Mathf.RoundToInt(GlowStick.Lux * charge));
					if (light.isSpawned && light.isActiveAndEnabled) light.FullRefresh();
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} pool glow: {e}");
				Remove();
			}
		}

		private static readonly float[] seenCell = new float[3];

		private static unsafe bool InSight(int cell)
		{
			fixed (float* v = seenCell)
			{
				LightNative.SeenCell(cell, v);
			}
			return seenCell[2] > 0f;
		}

		private static bool IsWater(int c) => Grid.IsValidCell(c) && Grid.Element[c].id == SimHashes.Water;
		private static void Create(int at)
		{
			var go = new GameObject("OniLightPoolGlow");
			go.SetActive(false);
			// On the very bottom: the centre of the glow at the floor's face, as if the stick lay on it (its cell for the light
			// grid is still the water's, a hair above the edge).
			go.transform.SetPosition(Grid.CellToPosCBC(at, Grid.SceneLayer.Liquid) + new Vector3(0f, 0.02f, 0f));
			instance = go.AddComponent<PoolGlow>();
			light = go.AddComponent<Light2D>();
			light.Lux = Mathf.RoundToInt(GlowStick.Lux);
			light.Range = GlowStick.Range;
			light.shape = LightShape.Circle;
			light.Offset = Vector2.zero;
			light.Color = Colour;
			light.overlayColour = Colour;
			light.drawOverlay = true;
			light.autoRespondToOperational = false;
			shown = -1f;
			go.SetActive(true);
			Grid.CellToXY(at, out int x, out int y);
			Debug.Log($"{ModContext.Tag} pool glow: at {x},{y} (search {Searches})");
		}

		private static void Remove()
		{
			if (instance != null)
			{
				UnityEngine.Object.Destroy(instance.gameObject);
			}
			instance = null;
			light = null;
			shown = -1f;
		}

		/// <summary>
		/// The bottom of the clean water pool nearest the printing pod (of the pod's world): pools are the connected cells of
		/// Water with at least PoolMass kg in all, nearest by their nearest cell; the bottom is the lowest cell, the one nearest
		/// the middle of the row.
		/// </summary>
		private static int FindPool()
		{
			Searches++;
			Telepad pod = Components.Telepads.Items.Count > 0 ? Components.Telepads.Items[0] : null;
			if (pod == null)
			{
				return Grid.InvalidCell;
			}
			int from = Grid.PosToCell(pod);
			WorldContainer world = pod.GetMyWorld();
			if (!Grid.IsValidCell(from) || world == null)
			{
				return Grid.InvalidCell;
			}
			Grid.CellToXY(from, out int px, out int py);
			var candidates = new List<(int distance, int cell)>();
			for (int y = world.WorldOffset.y; y < world.WorldOffset.y + world.WorldSize.y; y++)
			{
				for (int x = world.WorldOffset.x; x < world.WorldOffset.x + world.WorldSize.x; x++)
				{
					int c = Grid.XYToCell(x, y);
					if (IsWater(c)) candidates.Add(((x - px) * (x - px) + (y - py) * (y - py), c));
				}
			}
			candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
			var visited = new HashSet<int>();
			var pool = new List<int>();
			var queue = new Queue<int>();
			foreach ((int _, int seed) in candidates)
			{
				if (!visited.Add(seed))
				{
					continue;
				}
				pool.Clear();
				queue.Clear();
				queue.Enqueue(seed);
				float mass = 0f;
				while (queue.Count > 0 && pool.Count < MaxPoolCells)
				{
					int c = queue.Dequeue();
					pool.Add(c);
					mass += Grid.Mass[c];
					foreach (int next in new[] { Grid.CellLeft(c), Grid.CellRight(c), Grid.CellAbove(c), Grid.CellBelow(c) })
					{
						if (IsWater(next) && visited.Add(next)) queue.Enqueue(next);
					}
				}
				if (mass < PoolMass)
				{
					continue;
				}
				int lowest = int.MaxValue;
				long sumX = 0;
				int count = 0;
				foreach (int c in pool)
				{
					int y = Grid.CellRow(c);
					if (y < lowest) { lowest = y; sumX = 0; count = 0; }
					if (y == lowest) { sumX += Grid.CellColumn(c); count++; }
				}
				int middle = (int)(sumX / count), best = Grid.InvalidCell, bestOff = int.MaxValue;
				foreach (int c in pool)
				{
					if (Grid.CellRow(c) != lowest) continue;
					int off = Math.Abs(Grid.CellColumn(c) - middle);
					if (off < bestOff) { bestOff = off; best = c; }
				}
				return best;
			}
			return Grid.InvalidCell;
		}
	}
}
