using System;
using System.Collections.Generic;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// The Blackout mode's help at the start (option): while the first Duplicants' New Hope lasts, a glow lies at the bottom of
	/// the clean water pool nearest the printing pod, as if a glow stick had been dropped there. Like the Duplicants' sticks:
	/// 600 lux to 4 cells, only the game's own picture of a light (the glow and rays, neon blue), fading with New Hope over its
	/// last half cycle. Its light shows to the player whether or not a Duplicant can reach it (LightGrid: always seen).
	///
	/// Nothing is saved: the glow follows the New Hope of the living Duplicants, and the pool is looked for again on loading
	/// and whenever the water at the glow is gone.
	/// </summary>
	internal sealed class PoolGlow : MonoBehaviour
	{
		internal static readonly Color Colour = new Color(0.1f, 0.55f, 1f);
		private const float Period = 0.5f, SearchPeriod = 10f, PoolMass = 1000f;
		private const int MaxPoolCells = 4000;

		private static PoolGlow instance;
		private static float lastUpdate = -1000f, lastSearch = -1000f;
		private static int cell = Grid.InvalidCell;
		private static Light2D light;
		private static float shown = -1f;
		internal static long Searches;

		/// <summary>The cell of the glow (invalid: none now).</summary>
		internal static int Cell => instance != null ? cell : Grid.InvalidCell;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(PoolGlow), nameof(Frame)) { priority = Priority.Low });
			harmony.Patch(AccessTools.Method(typeof(LightGridManager), nameof(LightGridManager.Shutdown)), prefix: new HarmonyMethod(typeof(PoolGlow), nameof(Shutdown)));
		}

		private static void Shutdown()
		{
			Remove();
			cell = Grid.InvalidCell;
			lastUpdate = lastSearch = -1000f;
		}

		private static void Frame()
		{
			if (!LightGrid.Ready || Time.unscaledTime < lastUpdate + Period)
			{
				return;
			}
			lastUpdate = Time.unscaledTime;
			try
			{
				float charge = GlowStick.ChargeFor(GlowStick.HopeLeft());
				if (charge <= 0f)
				{
					Remove();
					return;
				}
				if (instance != null && !IsWater(cell))
				{
					Remove();
				}
				if (instance == null)
				{
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

		private static bool IsWater(int c) => Grid.IsValidCell(c) && Grid.Element[c].id == SimHashes.Water;

		private static void Create(int at)
		{
			var go = new GameObject("OniLightPoolGlow");
			go.SetActive(false);
			go.transform.SetPosition(Grid.CellToPosCCC(at, Grid.SceneLayer.Liquid));
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
