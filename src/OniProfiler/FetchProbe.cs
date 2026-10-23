using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace OniProfiler
{
	/// <summary>
	/// What Duplicants carrying things do after loading (the file probe_fetch.txt in the mod folder): every second for a
	/// minute each Duplicant's errand and cell, and for one that fetches: what it set out to carry, from where (the root
	/// item and the other items of the trip), where to; and, once per errand, every item that would suit the errand, with
	/// its cell, mass, the unreserved amount, its storage, whether the errand may take it, and the path cost from the
	/// Duplicant (the game's choice: cheapest to the item) and the cost of the whole trip to the item and on to the
	/// destination as OniFixes' TripAwareFetch reckons it.
	/// </summary>
	internal static class FetchProbe
	{
		private const float Every = 1f, For = 60f;
		private static float start = -1f, last = -1f;
		private static readonly Dictionary<MinionIdentity, string> lastLine = new Dictionary<MinionIdentity, string>();
		private static readonly HashSet<Chore> listed = new HashSet<Chore>();

		internal static void Apply(Harmony harmony, string folder)
		{
			if (File.Exists(Path.Combine(folder, "probe_fetch.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(FetchProbe), nameof(Frame)));
			}
		}

		private static string Where(int cell)
		{
			if (!Grid.IsValidCell(cell)) return "-";
			Grid.CellToXY(cell, out int x, out int y);
			return $"{x},{y}";
		}

		private static void Frame()
		{
			if (GameClock.Instance == null || Components.LiveMinionIdentities.Count == 0)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			if (start < 0f) start = now;
			if (now > start + For || now < last + Every)
			{
				return;
			}
			last = now;
			try
			{
				foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
				{
					if (minion == null) continue;
					Report(minion, now - start);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniProfiler] fetch probe: {e}");
				start = -1000f;
			}
		}

		private static void Report(MinionIdentity minion, float t)
		{
			Chore chore = minion.GetComponent<ChoreDriver>()?.GetCurrentChore();
			Navigator navigator = minion.GetComponent<Navigator>();
			Storage carried = minion.GetComponent<Storage>();
			var sb = new StringBuilder($"{minion.GetProperName()} at {Where(Grid.PosToCell(minion))}: {chore?.choreType?.Id ?? "none"}");
			if (carried != null && carried.items.Count > 0)
			{
				sb.Append(", carries");
				foreach (GameObject item in carried.items) if (item != null) sb.Append($" {item.GetProperName()} {item.GetComponent<PrimaryElement>()?.Mass:F1} kg");
			}
			FetchChore root = null;
			if (chore is FetchAreaChore area && area.smi != null)
			{
				root = (FetchChore)AccessTools.Field(typeof(FetchAreaChore.StatesInstance), "rootChore").GetValue(area.smi);
				var fetchables = (List<Pickupable>)AccessTools.Field(typeof(FetchAreaChore.StatesInstance), "fetchables").GetValue(area.smi);
				sb.Append($"; root {root?.tagsFirst} for {root?.destination?.GetProperName()} at {Where(root != null && root.destination != null ? Grid.PosToCell(root.destination) : -1)}, deliveries {area.smi.deliveries.Count}; trip items:");
				foreach (Pickupable p in fetchables) if (p != null) sb.Append($" {Where(Grid.PosToCell(p))} {p.PrimaryElement.Mass:F1} kg{(p.storage != null ? " in " + p.storage.GetProperName() : "")} (cost from here {(navigator != null ? navigator.GetNavigationCost(Grid.PosToCell(p)) : -1)})");
			}
			else if (chore is FetchChore fetch)
			{
				root = fetch;
				sb.Append($"; for {fetch.destination?.GetProperName()} at {Where(fetch.destination != null ? Grid.PosToCell(fetch.destination) : -1)}, target {Where(fetch.fetchTarget != null ? Grid.PosToCell(fetch.fetchTarget) : -1)}");
			}
			string line = sb.ToString();
			if (!lastLine.TryGetValue(minion, out string was) || was != line)
			{
				lastLine[minion] = line;
				Debug.Log($"[OniProfiler] fetch probe +{t:F0}s: {line}");
			}
			if (root != null && root.destination != null && navigator != null && listed.Add(root))
			{
				Candidates(minion, navigator, root, t);
			}
		}

		private static void Candidates(MinionIdentity minion, Navigator navigator, FetchChore chore, float t)
		{
			int destination = Grid.PosToCell(chore.destination);
			int toDestination = navigator.GetNavigationCost(destination);
			var rows = new List<(int trip, string text)>();
			foreach (Pickupable p in Components.Pickupables.Items)
			{
				if (p == null || p.KPrefabID == null) continue;
				bool matches = chore.criteria == FetchChore.MatchCriteria.MatchID ? chore.tags.Contains(p.KPrefabID.PrefabTag) : p.KPrefabID.HasTag(chore.tagsFirst);
				if (!matches) continue;
				int cell = Grid.PosToCell(p);
				int cost = navigator.GetNavigationCost(cell);
				Grid.CellToXY(cell, out int px, out int py);
				Grid.CellToXY(destination, out int dx, out int dy);
				int straight = Math.Max(Math.Abs(px - dx), Math.Abs(py - dy)) * 10;
				int trip = cost < 0 ? int.MaxValue : cost + Math.Max(Math.Abs(toDestination - cost), straight);
				bool fetchable = FetchManager.IsFetchablePickup(p, chore, chore.destination);
				rows.Add((trip, $"{Where(cell)} {p.GetProperName()} {p.PrimaryElement.Mass:F1} kg, unreserved {p.UnreservedFetchAmount:F1}, " +
					$"{(p.storage != null ? "in " + p.storage.GetProperName() : "loose")}, may take {fetchable}{(p.KPrefabID.HasTag(GameTags.MarkedForMove) ? ", marked for move" : "")}, " +
					$"cost {cost}, trip {(trip == int.MaxValue ? "-" : trip.ToString())}"));
			}
			rows.Sort((a, b) => a.trip.CompareTo(b.trip));
			var sb = new StringBuilder($"[OniProfiler] fetch probe +{t:F0}s: {minion.GetProperName()}'s errand for {chore.tagsFirst} to {Where(destination)} (cost there {toDestination}): {rows.Count} items that match, by trip cost:\n");
			foreach ((int trip, string text) in rows) sb.AppendLine("    " + text);
			Debug.Log(sb.ToString());
		}
	}
}
