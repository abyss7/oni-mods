using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// A look at what the player sees around the printing pod, without changing the world: the file verify_light_probe.txt
	/// in the mod folder. Twenty seconds after loading (the camera put on the pod), the log gets a map of the cells around the pod (what the player
	/// sees in each, 0-255, and what kind of cell it is), the light of each cell the player sees something in (of the
	/// colony's sources, of the others, whether a Duplicant sees the cell), and the sources that light those cells directly:
	/// what they are, made by the colony or not, whether a Duplicant can walk into their light.
	/// </summary>
	internal static unsafe class LightProbe
	{
		private const int Left = 14, Right = 14, Up = 6, Down = 14;
		private static float start = -1f;
		private static bool done;

		internal static void Apply(Harmony harmony)
		{
			if (File.Exists(Path.Combine(ModContext.Folder, "verify_light_probe.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(LightProbe), nameof(Frame)));
			}
		}

		private static void Frame()
		{
			if (done || GameClock.Instance == null || !LightGrid.Ready || Components.Telepads.Items.Count == 0)
			{
				return;
			}
			if (start < 0f)
			{
				// What the player sees is kept up to date around the view: look at the pod.
				start = Time.unscaledTime;
				CameraController.Instance?.SnapTo(Components.Telepads.Items[0].transform.GetPosition(), 12f);
			}
			if (Time.unscaledTime < start + 20f)
			{
				return;
			}
			done = true;
			try
			{
				Report();
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} probe failed: {e}");
			}
		}

		private static void Report()
		{
			Telepad pod = Components.Telepads.Items[0];
			Grid.CellToXY(Grid.PosToCell(pod), out int px, out int py);
			// The map: shown 0-255 as a digit 0-9 ('.' nothing), '#' solid and unseen, '~' liquid and unseen.
			var sb = new StringBuilder();
			sb.AppendLine($"{ModContext.Tag} probe: around the printing pod at {px},{py}; digits: what the player sees (0-9 of 255), '#' solid, '~' liquid, '.' gas, unseen");
			float* v = stackalloc float[3];
			var lit = new List<int>();
			for (int y = py + Up; y >= py - Down; y--)
			{
				sb.Append($"{y,4} ");
				for (int x = px - Left; x <= px + Right; x++)
				{
					int cell = Grid.XYToCell(x, y);
					if (!Grid.IsValidCell(cell)) { sb.Append(' '); continue; }
					byte shown = Darkness.ShownAt(cell);
					if (shown > 0)
					{
						sb.Append((char)('0' + Math.Min(9, shown * 10 / 256)));
						lit.Add(cell);
					}
					else sb.Append(Grid.Solid[cell] ? '#' : Grid.Element[cell].IsLiquid ? '~' : '.');
				}
				sb.AppendLine(y == py ? "   <- the pod's row" : "");
			}
			Debug.Log(sb.ToString());
			// The cells seen below the pod's floor: their light.
			foreach (int cell in lit)
			{
				Grid.CellToXY(cell, out int x, out int y);
				if (y >= py) continue;
				LightNative.SeenCell(cell, v);
				Debug.Log($"{ModContext.Tag} probe: {x},{y} ({x - px:+0;-0},{y - py:+0;-0}) {Grid.Element[cell].id}: shown {Darkness.ShownAt(cell)}, light {Grid.LightIntensity[cell]} lux, of the colony's seen {v[0]:F1}, of the others {v[1]:F1}, in sight {v[2] > 0f}");
			}
			// The sources lighting any of them directly.
			var below = new HashSet<int>();
			foreach (int cell in lit) { Grid.CellToXY(cell, out _, out int y); if (y < py) below.Add(cell); }
			int[] cells = new int[8192];
			foreach (int id in LightGrid.ActiveIds)
			{
				if (!LightGrid.TryOwner(id, out LightGridManager.LightGridEmitter emitter)) continue;
				int n;
				fixed (int* c = cells) n = LightNative.SourceCells(id, c, cells.Length);
				int hits = 0;
				for (int i = 0; i < n; i++) if (below.Contains(cells[i])) hits++;
				if (hits == 0) continue;
				Light2D owner = null;
				foreach (Light2D l in Components.Light2Ds.Items) if (l != null && l.emitter == emitter) { owner = l; break; }
				Grid.CellToXY(emitter.state.origin, out int ox, out int oy);
				bool natural = LightGrid.IsNatural(emitter);
				Debug.Log($"{ModContext.Tag} probe: source {id} {(owner != null ? owner.gameObject.GetProperName() + " (" + owner.name + ")" : "?")} at {ox},{oy}, {emitter.state.intensity} lux R {emitter.state.radius}: " +
					$"{(natural ? "not the colony's" : LightGrid.IsCarried(emitter) ? "carried or the glow in the water (always seen)" : "the colony's")}, a Duplicant can walk into its light {(!natural && Darkness.Reachable(id, emitter))}, lights {hits} of the seen cells below the pod directly");
			}
		}
	}
}
