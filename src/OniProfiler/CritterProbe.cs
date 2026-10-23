using System;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace OniProfiler
{
	/// <summary>
	/// Where the critters of one kind stand and on what, a few times after loading (the file probe_critters.txt in the mod
	/// folder; its first line is the critter's prefab id, "Squirrel" by default): for each, its cell, how it moves (its
	/// navigation type), whether its navigation grid takes that cell as valid for it, whether it is falling, and the four
	/// cells around it (S solid, F foundation, C impassable for critters; the element; a building there). To compare a run
	/// with mods and one without.
	/// </summary>
	internal static class CritterProbe
	{
		private static readonly float[] At = { 3f, 15f, 45f, 90f };
		private static string id = "Squirrel";
		private static float start = -1f;
		private static int next;

		internal static void Apply(Harmony harmony, string folder)
		{
			string file = Path.Combine(folder, "probe_critters.txt");
			if (!File.Exists(file))
			{
				return;
			}
			string first = File.ReadAllText(file).Trim();
			if (first.Length > 0) id = first.Split('\n')[0].Trim();
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(CritterProbe), nameof(Frame)));
		}

		private static void Frame()
		{
			if (next >= At.Length || GameClock.Instance == null)
			{
				return;
			}
			if (start < 0f) start = Time.unscaledTime;
			if (Time.unscaledTime < start + At[next])
			{
				return;
			}
			try
			{
				Report(At[next]);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniProfiler] critter probe: {e}");
			}
			next++;
		}

		private static string Cell(int cell)
		{
			if (!Grid.IsValidCell(cell)) return "-";
			GameObject building = Grid.Objects[cell, (int)ObjectLayer.Building];
			return $"{(Grid.Solid[cell] ? "S" : ".")}{(Grid.Foundation[cell] ? "F" : "")}{(Grid.CritterImpassable[cell] ? "C" : "")}:{Grid.Element[cell].id}" +
				(building != null ? "[" + building.name + "]" : "");
		}

		private static void Report(float after)
		{
			var sb = new StringBuilder();
			Tag tag = new Tag(id);
			int count = 0, invalid = 0, falling = 0;
			foreach (KPrefabID critter in Components.Brains.Items.ConvertAll(b => b != null ? b.GetComponent<KPrefabID>() : null))
			{
				if (critter == null || critter.PrefabTag != tag) continue;
				count++;
				Navigator navigator = critter.GetComponent<Navigator>();
				int cell = Grid.PosToCell(critter);
				Grid.CellToXY(cell, out int x, out int y);
				NavType type = navigator != null ? navigator.CurrentNavType : NavType.NumNavTypes;
				bool valid = navigator != null && navigator.NavGrid != null && navigator.NavGrid.NavTable.IsValid(cell, type);
				bool fall = critter.HasTag(GameTags.Creatures.Falling);
				if (!valid) invalid++;
				if (fall) falling++;
				sb.AppendLine($"[OniProfiler] critter probe +{after:F0}s: {critter.GetProperName()} at {x},{y} {type}{(navigator != null && navigator.IsMoving() ? " moving" : "")}, valid {valid}, falling {fall}; " +
					$"below {Cell(Grid.CellBelow(cell))}, left {Cell(Grid.CellLeft(cell))}, right {Cell(Grid.CellRight(cell))}, above {Cell(Grid.CellAbove(cell))}, here {Cell(cell)}");
			}
			Debug.Log($"[OniProfiler] critter probe +{after:F0}s: {count} {id}, standing where their grid says they cannot {invalid}, falling {falling}\n{sb}");
		}
	}
}
