using System;
using System.IO;
using System.Text;
using HarmonyLib;
using Klei.AI;
using UnityEngine;

namespace OniProfiler
{
	/// <summary>
	/// How the plants of one kind see their atmosphere, a few times after loading (the file probe_plants.txt in the mod
	/// folder; its first line is the plant's prefab id, "Oxyfern" by default): for each, its cells, the pressure state and
	/// whether its gas counts as safe, the pressure it sees and the gas it takes for its atmosphere, why it wilts, its status
	/// lines, and for each of its cells the main gas, its mass, and the composition by the gas mixture mod if there is one.
	/// </summary>
	internal static class PlantProbe
	{
		private static readonly float[] At = { 30f, 90f };
		private static string id = "Oxyfern";
		private static float start = -1f;
		private static int next;
		private static Func<int, ushort[], float[], int> composition;
		private static readonly ushort[] elements = new ushort[16];
		private static readonly float[] masses = new float[16];

		internal static void Apply(Harmony harmony, string folder)
		{
			string file = Path.Combine(folder, "probe_plants.txt");
			if (!File.Exists(file))
			{
				return;
			}
			string first = File.ReadAllText(file).Trim();
			if (first.Length > 0) id = first.Split('\n')[0].Trim();
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(PlantProbe), nameof(Frame)));
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
				composition = AppDomain.CurrentDomain.GetData("OniMods.GasComposition.1") as Func<int, ushort[], float[], int>;
				Report(At[next]);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniProfiler] plant probe: {e}");
			}
			next++;
		}

		private static string CellText(int cell)
		{
			if (!Grid.IsValidCell(cell)) return "-";
			Grid.CellToXY(cell, out int x, out int y);
			var sb = new StringBuilder($"{x},{y} {Grid.Element[cell].id} {Grid.Mass[cell] * 1000f:F0} g, {Grid.Temperature[cell] - 273.15f:F1} C");
			if (composition != null && Grid.Element[cell].IsGas)
			{
				int parts = composition(cell, elements, masses);
				if (parts > 0)
				{
					sb.Append(" [");
					for (int k = 0; k < parts; k++)
					{
						Element e = ElementLoader.elements[elements[k]];
						sb.Append($"{(k > 0 ? ", " : "")}{e.id} {masses[k] * 1000f:F1} g");
					}
					sb.Append("]");
				}
			}
			return sb.ToString();
		}

		private static void Report(float after)
		{
			var sb = new StringBuilder();
			Tag tag = new Tag(id);
			int count = 0, complaining = 0;
			foreach (KPrefabID plant in Components.Uprootables.Items.ConvertAll(u => u != null ? u.GetComponent<KPrefabID>() : null))
			{
				if (plant == null || plant.PrefabTag != tag) continue;
				count++;
				int origin = Grid.PosToCell(plant);
				PressureVulnerable pv = plant.GetComponent<PressureVulnerable>();
				WiltCondition wilt = plant.GetComponent<WiltCondition>();
				string wilting = "";
				if (wilt != null)
				{
					foreach (WiltCondition.Condition c in wilt.CurrentWiltSources()) wilting += (wilting.Length > 0 ? ", " : "") + c;
				}
				string statuses = "";
				KSelectable selectable = plant.GetComponent<KSelectable>();
				if (selectable != null && selectable.GetStatusItemGroup() != null)
				{
					foreach (StatusItemGroup.Entry entry in selectable.GetStatusItemGroup())
					{
						statuses += (statuses.Length > 0 ? "; " : "") + entry.item.Id;
					}
				}
				ElementConverter converter = plant.GetComponent<ElementConverter>();
				Storage storage = plant.GetComponent<Storage>();
				string conv = converter == null ? "none" : $"can convert {converter.CanConvertAtAll()}, enough to start {converter.HasEnoughMassToStartConverting()}, stored {(storage != null ? storage.MassStored() * 1000f : 0f):F1} g";
				string growth = plant.GetComponent<Growing>() is Growing g ? $"{g.PercentGrown() * 100f:F0}%" : "-";
				if (pv != null && (pv.ExternalPressureState != PressureVulnerable.PressureState.Normal || !pv.testAreaElementSafe)) complaining++;
				Grid.CellToXY(origin, out int x, out int y);
				sb.AppendLine($"[OniProfiler] plant probe +{after:F0}s: {plant.GetProperName()} at {x},{y}: pressure state {pv?.ExternalPressureState}, safe gas {pv?.testAreaElementSafe}, " +
					$"pressure seen {(pv != null ? pv.GetExternalPressure() * 1000f : -1f):F1} g (warning below {pv?.pressureWarning_Low * 1000f:F1} g), atmosphere {pv?.currentAtmoElement?.id}; " +
					$"wilting {wilt?.IsWilting()} [{wilting}]; converter {conv}; grown {growth}; status [{statuses}]");
				OccupyArea area = plant.GetComponent<OccupyArea>();
				if (area != null)
				{
					foreach (CellOffset offset in area.OccupiedCellsOffsets)
					{
						sb.AppendLine($"[OniProfiler] plant probe +{after:F0}s:   cell {CellText(Grid.OffsetCell(origin, offset))}");
					}
				}
			}
			Debug.Log($"[OniProfiler] plant probe +{after:F0}s: {count} {id}, complaining about the atmosphere {complaining}\n{sb}");
		}
	}
}
