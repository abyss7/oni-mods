using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniPressure
{
	/// <summary>
	/// Survey: what pressure the walls hold right now. Enabled by the file pressure_survey.txt in the mod folder.
	/// 12 seconds of game time after loading and then once a minute it walks all solid cells and logs how many
	/// of them stand at which pressure: buildings (tiles, doors) and natural rock separately, gas and liquid
	/// separately. These numbers are used to pick the thresholds for the "wall breaks under pressure" rule: they
	/// show what is normal in an ordinary base and what is a plainly impossible storage.
	///
	/// Two quantities are counted for gas. Load: the largest gas mass in a cell next to the wall. Difference:
	/// the difference of gas masses on the two sides of a one-cell-thick wall (left and right, or below and above);
	/// if the other side is neither gas nor vacuum, no difference is counted. For liquid: the largest compression of
	/// an adjacent liquid (cell mass to the element's maximum mass); the game's own leak rule uses this same number.
	/// </summary>
	internal sealed class PressureSurvey : KMonoBehaviour, ISim4000ms
	{
		private const int FirstTick = 3, EveryTicks = 15, TopCells = 5;
		private static readonly float[] GasEdges = { 1.8f, 3f, 5f, 10f, 20f, 50f, 200f };
		private static readonly float[] LiquidEdges = { 1f, 1.05f, 1.2f, 2f, 5f };

		internal static bool Enabled;
		private int ticks;

		internal static void Apply(Harmony harmony)
		{
			Enabled = File.Exists(Path.Combine(ModContext.Folder, "pressure_survey.txt"));
			if (Enabled)
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "OnSpawn"), postfix: new HarmonyMethod(typeof(PressureSurvey), nameof(GameSpawned)));
			}
		}

		private static void GameSpawned(Game __instance)
		{
			__instance.gameObject.AddOrGet<PressureSurvey>();
		}

		public void Sim4000ms(float dt)
		{
			ticks++;
			if (ticks == FirstTick || (ticks > FirstTick && (ticks - FirstTick) % EveryTicks == 0))
			{
				Run();
			}
		}

		/// <summary>One quantity for one kind of wall: how many cells fall in each range, and the most loaded ones.</summary>
		private sealed class Tally
		{
			private readonly float[] edges;
			private readonly int[] counts;
			private readonly List<KeyValuePair<float, int>> top = new List<KeyValuePair<float, int>>();

			internal Tally(float[] edges)
			{
				this.edges = edges;
				counts = new int[edges.Length + 1];
			}

			internal void Add(float value, int cell)
			{
				int bin = 0;
				while (bin < edges.Length && value > edges[bin])
				{
					bin++;
				}
				counts[bin]++;
				if (top.Count < TopCells || value > top[top.Count - 1].Key)
				{
					int at = top.Count;
					while (at > 0 && top[at - 1].Key < value)
					{
						at--;
					}
					top.Insert(at, new KeyValuePair<float, int>(value, cell));
					if (top.Count > TopCells)
					{
						top.RemoveAt(TopCells);
					}
				}
			}

			internal string Text(string unit)
			{
				var text = new StringBuilder();
				for (int bin = 0; bin < counts.Length; bin++)
				{
					string range = bin == 0 ? $"up to {edges[0]}" : bin == edges.Length ? $"over {edges[bin - 1]}" : $"{edges[bin - 1]}-{edges[bin]}";
					text.Append(bin == 0 ? "" : ", ").Append(range).Append(' ').Append(unit).Append(": ").Append(counts[bin]);
				}
				text.Append("; top:");
				foreach (KeyValuePair<float, int> entry in top)
				{
					Grid.CellToXY(entry.Value, out int x, out int y);
					text.Append($" {entry.Key:F2} at {x},{y} ({Grid.Element[entry.Value].id})");
				}
				return text.ToString();
			}
		}

		private static bool Open(int cell)
		{
			return Grid.IsValidCell(cell) && !Grid.Element[cell].IsSolid && !Grid.Element[cell].IsLiquid;
		}

		private static void Run()
		{
			float started = Time.realtimeSinceStartup;
			// First index: 0 buildings, 1 natural rock.
			var gasLoad = new[] { new Tally(GasEdges), new Tally(GasEdges) };
			var gasDrop = new[] { new Tally(GasEdges), new Tally(GasEdges) };
			var liquid = new[] { new Tally(LiquidEdges), new Tally(LiquidEdges) };
			var walls = new int[2];
			int width = Grid.WidthInCells;
			for (int cell = 0; cell < Grid.CellCount; cell++)
			{
				if (Grid.WorldIdx[cell] == byte.MaxValue || !Grid.Element[cell].IsSolid)
				{
					continue;
				}
				int kind = Grid.Foundation[cell] ? 0 : 1;
				int left = cell - 1, right = cell + 1, below = cell - width, above = cell + width;
				float load = 0f, squeeze = 0f;
				bool touchesGas = false;
				foreach (int beside in new[] { left, right, below, above })
				{
					if (!Grid.IsValidCell(beside))
					{
						continue;
					}
					if (Grid.Element[beside].IsGas)
					{
						touchesGas = true;
						load = Mathf.Max(load, CellPressure.Gas(beside));
					}
					squeeze = Mathf.Max(squeeze, CellPressure.Liquid(beside));
				}
				if (!touchesGas && squeeze <= 0f)
				{
					continue;
				}
				walls[kind]++;
				if (touchesGas)
				{
					gasLoad[kind].Add(load, cell);
					float drop = -1f;
					if (Open(left) && Open(right))
					{
						drop = Mathf.Abs(CellPressure.Gas(left) - CellPressure.Gas(right));
					}
					if (Open(below) && Open(above))
					{
						drop = Mathf.Max(drop, Mathf.Abs(CellPressure.Gas(below) - CellPressure.Gas(above)));
					}
					if (drop >= 0f)
					{
						gasDrop[kind].Add(drop, cell);
					}
				}
				if (squeeze > 0f)
				{
					liquid[kind].Add(squeeze, cell);
				}
			}
			string[] names = { "built", "natural" };
			Debug.Log($"{ModContext.Tag} survey: gas mixture mod {(CellPressure.GasMixPresent() ? "present" : "absent")}, walls next to gas or liquid: built {walls[0]}, natural {walls[1]}; " +
				$"took {(Time.realtimeSinceStartup - started) * 1000f:F1} ms");
			for (int kind = 0; kind < 2; kind++)
			{
				Debug.Log($"{ModContext.Tag} survey: {names[kind]}, largest gas mass beside the wall: {gasLoad[kind].Text("kg")}");
				Debug.Log($"{ModContext.Tag} survey: {names[kind]}, gas mass difference across a wall one cell thick: {gasDrop[kind].Text("kg")}");
				Debug.Log($"{ModContext.Tag} survey: {names[kind]}, largest liquid mass beside the wall relative to its full cell: {liquid[kind].Text("x")}");
			}
		}
	}
}
