using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;

namespace OniFixes
{
	/// <summary>
	/// Finding items to carry.
	///
	/// In stock code, on every duplicant brain update the game goes through all items of the colony, computes
	/// the path cost to each, sorts them and puts them into a shared list, from which fetch tasks
	/// then take the first suitable one. On a colony with 5 thousand items this is half of the brain time.
	///
	/// Here the list is built on demand: only the item kinds requested by this duplicant's task
	/// are processed, with no sorting, and with a per-call cache of cell costs.
	///
	/// Optionally (TripAware) the choice itself changes. In stock code the pile nearest to the duplicant is taken, and
	/// the task cost is computed as "to the delivery point" plus "to the pile". The real trip is: to the pile,
	/// then from the pile to the delivery point. The game does not know the cost of the second leg (the path map
	/// is built only from the duplicant), so a lower bound is used: the maximum of the difference of the two
	/// known costs and the straight-line distance. Task priorities are not affected: the cost
	/// remains the last comparison key.
	/// </summary>
	internal static class DemandDrivenFetch
	{
		private struct Candidate
		{
			public Pickupable pickupable;
			public int cost;
			public int cell;
			public int tagBitsHash;
			public int masterPriority;
			public int freshness;
			public int foodQuality;
		}

		private sealed class GroupCache
		{
			public int epoch;
			public readonly List<Candidate> items = new List<Candidate>();
		}

		// Cost of a floor step in the duplicant navigation grid: for converting cells into path cost units.
		private const int CostPerCell = 10;

		private static bool tripAware;
		private static bool verify;

		// An "epoch" starts with a call to UpdatePickups: from that moment queries belong to this duplicant.
		private static int epoch;
		private static bool epochIsOurs;
		private static Navigator navigator;
		private static int workerId;

		private static readonly Dictionary<FetchManager.FetchablesByPrefabId, GroupCache> caches = new Dictionary<FetchManager.FetchablesByPrefabId, GroupCache>();
		// Key: the cell and the storage the item lies in. The storage determines from which cells the item can be reached.
		private static readonly Dictionary<long, int> cellCosts = new Dictionary<long, int>();
		private static readonly Dictionary<Tag, List<FetchManager.FetchablesByPrefabId>> groupsByCategoryTag = new Dictionary<Tag, List<FetchManager.FetchablesByPrefabId>>();
		private static int groupsByCategoryTagVersion = -1;
		private static readonly List<FetchManager.FetchablesByPrefabId> singleGroup = new List<FetchManager.FetchablesByPrefabId>(1);
		private static readonly List<FetchManager.FetchablesByPrefabId> scratchGroups = new List<FetchManager.FetchablesByPrefabId>();

		private static long epochs, queries, queriesById, queriesByOwnTag, queriesByCategory, edibleQueries, groupsBuilt, itemsScanned;

		// Self-check: the stock algorithm runs in parallel, the results are compared by path cost.
		private static readonly List<FetchManager.Pickup> vanillaPickups = new List<FetchManager.Pickup>();
		private static long verifySame, verifyTie, verifyBetter, verifyExtra, verifyWorse, verifyMissing, verifyEdibleSame, verifyEdibleDiff;
		private static int verifyLogged;

		internal static void Apply(Harmony harmony)
		{
			tripAware = Mod.Options.TripAwareFetch;
			verify = File.Exists(Path.Combine(ModContext.Folder, "verify_fetch.txt"));

			harmony.Patch(AccessTools.Method(typeof(FetchManager), nameof(FetchManager.UpdatePickups)),
				prefix: new HarmonyMethod(typeof(DemandDrivenFetch), nameof(UpdatePickupsPrefix)),
				postfix: new HarmonyMethod(typeof(DemandDrivenFetch), nameof(UpdatePickupsPostfix)));
			harmony.Patch(AccessTools.Method(typeof(FetchManager), nameof(FetchManager.FindFetchTarget), new[] { typeof(Storage), typeof(FetchChore) }),
				prefix: new HarmonyMethod(typeof(DemandDrivenFetch), nameof(FindFetchTargetPrefix)));
			harmony.Patch(AccessTools.Method(typeof(FetchManager), nameof(FetchManager.FindEdibleFetchTarget), new[] { typeof(Storage), typeof(HashSet<Tag>), typeof(Tag[]) }),
				prefix: new HarmonyMethod(typeof(DemandDrivenFetch), nameof(FindEdibleFetchTargetPrefix)));
			if (tripAware)
			{
				// The task condition is stored as a delegate in a static field: we patch the method it points to.
				harmony.Patch(FetchChore.IsFetchTargetAvailable.fn.Method,
					postfix: new HarmonyMethod(typeof(DemandDrivenFetch), nameof(FetchTargetAvailablePostfix)));
			}
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)),
				prefix: new HarmonyMethod(typeof(DemandDrivenFetch), nameof(LogStats)));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.StopBE)),
				prefix: new HarmonyMethod(typeof(DemandDrivenFetch), nameof(Forget)));
		}

		private static bool UpdatePickupsPrefix(FetchManager __instance, Navigator navigator, WorkerBase worker)
		{
			epochIsOurs = AbTest.IsOn(nameof(DemandDrivenFetch));
			if (!epochIsOurs)
			{
				return true;
			}
			epoch++;
			epochs++;
			DemandDrivenFetch.navigator = navigator;
			workerId = worker.GetComponent<KPrefabID>().InstanceID;
			cellCosts.Clear();
			if (verify)
			{
				return true;
			}
			__instance.pickups.Clear();
			return false;
		}

		private static void UpdatePickupsPostfix(FetchManager __instance)
		{
			if (verify && epochIsOurs)
			{
				vanillaPickups.Clear();
				vanillaPickups.AddRange(__instance.pickups);
			}
		}

		private static bool FindFetchTargetPrefix(FetchManager __instance, Storage destination, FetchChore chore, ref Pickupable __result)
		{
			if (!epochIsOurs)
			{
				return true;
			}
			queries++;
			__result = Find(__instance, destination, chore, tripAware, out int cost);
			if (verify)
			{
				VerifyFetch(__instance, destination, chore, tripAware ? Find(__instance, destination, chore, false, out cost) : __result, cost);
			}
			return false;
		}

		/// <summary>Finds the best pile for a task among the item kinds that suit it.</summary>
		private static Pickupable Find(FetchManager manager, Storage destination, FetchChore chore, bool byTrip, out int bestCost)
		{
			bestCost = -1;
			Navigator nav = navigator;
			if (nav == null || destination == null)
			{
				return null;
			}
			int toDestination = -1;
			int destinationCell = -1;
			if (byTrip)
			{
				toDestination = nav.GetNavigationCost(destination);
				destinationCell = Grid.PosToCell(destination);
			}
			bool found = false;
			Candidate best = default(Candidate);
			int bestKey = int.MaxValue;
			List<FetchManager.FetchablesByPrefabId> groups = GroupsFor(manager, chore);
			for (int g = 0; g < groups.Count; g++)
			{
				List<Candidate> items = GetCache(groups[g], nav).items;
				for (int i = 0; i < items.Count; i++)
				{
					Candidate candidate = items[i];
					int key = toDestination >= 0 ? TripCost(toDestination, candidate.cost, candidate.cell, destinationCell) : candidate.cost;
					if (key > bestKey || (key == bestKey && !WinsTie(ref candidate, ref best)))
					{
						continue;
					}
					// The suitability check is more expensive than the cost comparison, so it comes after it.
					if (!FetchManager.IsFetchablePickup(candidate.pickupable, chore, destination))
					{
						continue;
					}
					found = true;
					best = candidate;
					bestKey = key;
				}
			}
			if (!found)
			{
				return null;
			}
			bestCost = best.cost;
			return best.pickupable;
		}

		/// <summary>
		/// Estimate of the whole trip: the path to the pile plus a lower bound on the path from the pile to the delivery point.
		/// The second leg is no shorter than the difference of the known costs (triangle inequality) and no shorter than a straight line.
		/// </summary>
		private static int TripCost(int toDestination, int toPickup, int pickupCell, int destinationCell)
		{
			return toPickup + Math.Max(Math.Abs(toDestination - toPickup), StraightCost(pickupCell, destinationCell));
		}

		private static int StraightCost(int cellA, int cellB)
		{
			Grid.CellToXY(cellA, out int ax, out int ay);
			Grid.CellToXY(cellB, out int bx, out int by);
			return Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by)) * CostPerCell;
		}

		/// <summary>At equal cost, the stock order: closer, then higher food quality, then fresher.</summary>
		private static bool WinsTie(ref Candidate candidate, ref Candidate best)
		{
			if (candidate.cost != best.cost)
			{
				return candidate.cost < best.cost;
			}
			if (candidate.foodQuality != best.foodQuality)
			{
				return candidate.foodQuality > best.foodQuality;
			}
			return candidate.freshness > best.freshness;
		}

		private static List<FetchManager.FetchablesByPrefabId> GroupsFor(FetchManager manager, FetchChore chore)
		{
			Dictionary<Tag, FetchManager.FetchablesByPrefabId> all = manager.prefabIdToFetchables;
			if (chore.criteria == FetchChore.MatchCriteria.MatchID)
			{
				queriesById++;
				scratchGroups.Clear();
				foreach (Tag tag in chore.tags)
				{
					if (all.TryGetValue(tag, out FetchManager.FetchablesByPrefabId group))
					{
						scratchGroups.Add(group);
					}
				}
				return scratchGroups;
			}
			// MatchTags: a single tag. Most often it is the item kind itself (this is how construction materials are ordered).
			Tag wanted = chore.tagsFirst;
			if (all.TryGetValue(wanted, out FetchManager.FetchablesByPrefabId own))
			{
				queriesByOwnTag++;
				singleGroup.Clear();
				singleGroup.Add(own);
				return singleGroup;
			}
			queriesByCategory++;
			return GroupsWithPrefabTag(manager, wanted);
		}

		/// <summary>
		/// Item kinds whose category tag (for example "edible") is set in the prefab.
		/// Assumption: such tags are not attached to individual instances. The self-check will show if this is not so.
		/// </summary>
		private static List<FetchManager.FetchablesByPrefabId> GroupsWithPrefabTag(FetchManager manager, Tag tag)
		{
			Dictionary<Tag, FetchManager.FetchablesByPrefabId> all = manager.prefabIdToFetchables;
			if (groupsByCategoryTagVersion != all.Count)
			{
				groupsByCategoryTag.Clear();
				groupsByCategoryTagVersion = all.Count;
			}
			if (!groupsByCategoryTag.TryGetValue(tag, out List<FetchManager.FetchablesByPrefabId> groups))
			{
				groups = new List<FetchManager.FetchablesByPrefabId>();
				foreach (KeyValuePair<Tag, FetchManager.FetchablesByPrefabId> pair in all)
				{
					UnityEngine.GameObject prefab = Assets.TryGetPrefab(pair.Key);
					KPrefabID id = prefab != null ? prefab.GetComponent<KPrefabID>() : null;
					if (id != null && id.HasTag(tag))
					{
						groups.Add(pair.Value);
					}
				}
				groupsByCategoryTag[tag] = groups;
			}
			return groups;
		}

		/// <summary>Items of one kind that this duplicant can take and reach, with path cost.</summary>
		private static GroupCache GetCache(FetchManager.FetchablesByPrefabId group, Navigator nav)
		{
			if (!caches.TryGetValue(group, out GroupCache cache))
			{
				caches[group] = cache = new GroupCache();
			}
			if (cache.epoch == epoch)
			{
				return cache;
			}
			cache.epoch = epoch;
			cache.items.Clear();
			groupsBuilt++;
			List<FetchManager.Fetchable> fetchables = group.fetchables.GetDataList();
			itemsScanned += fetchables.Count;
			for (int i = 0; i < fetchables.Count; i++)
			{
				FetchManager.Fetchable fetchable = fetchables[i];
				Pickupable pickupable = fetchable.pickupable;
				if (!pickupable.CouldBePickedUpByMinion(workerId))
				{
					continue;
				}
				int cell = pickupable.cachedCell;
				Storage storage = pickupable.storage;
				long key = ((long)((object)storage == null ? 0 : storage.GetInstanceID()) << 32) | (uint)cell;
				if (!cellCosts.TryGetValue(key, out int cost))
				{
					cost = pickupable.GetNavigationCost(nav, cell);
					cellCosts[key] = cost;
				}
				if (cost == -1)
				{
					continue;
				}
				cache.items.Add(new Candidate
				{
					pickupable = pickupable,
					cost = cost,
					cell = cell,
					tagBitsHash = fetchable.tagBitsHash,
					masterPriority = fetchable.masterPriority,
					freshness = fetchable.freshness,
					foodQuality = fetchable.foodQuality
				});
			}
			return cache;
		}

		/// <summary>
		/// Food search is left stock: it accounts for quality and rehydrators. Only the list it walks
		/// is replaced: instead of all items of the colony, the kinds with the needed tags are put into it.
		/// </summary>
		private static bool FindEdibleFetchTargetPrefix(FetchManager __instance, Storage destination, HashSet<Tag> exclude_tags, Tag[] required_tags)
		{
			Navigator nav = navigator;
			if (!epochIsOurs || nav == null || required_tags == null || required_tags.Length == 0)
			{
				return true;
			}
			edibleQueries++;
			List<FetchManager.Pickup> pickups = __instance.pickups;
			pickups.Clear();
			List<FetchManager.FetchablesByPrefabId> groups = GroupsWithPrefabTag(__instance, required_tags[0]);
			for (int g = 0; g < groups.Count; g++)
			{
				List<Candidate> items = GetCache(groups[g], nav).items;
				for (int i = 0; i < items.Count; i++)
				{
					Candidate candidate = items[i];
					pickups.Add(new FetchManager.Pickup
					{
						pickupable = candidate.pickupable,
						tagBitsHash = candidate.tagBitsHash,
						PathCost = (ushort)Math.Min(candidate.cost, ushort.MaxValue),
						masterPriority = candidate.masterPriority,
						freshness = candidate.freshness,
						foodQuality = candidate.foodQuality
					});
				}
			}
			if (verify)
			{
				int ours = BestEdibleScore(pickups, destination, exclude_tags, required_tags);
				int vanilla = BestEdibleScore(vanillaPickups, destination, exclude_tags, required_tags);
				if (ours == vanilla)
				{
					verifyEdibleSame++;
				}
				else
				{
					verifyEdibleDiff++;
					LogVerify($"edible: ours={ours} vanilla={vanilla}");
				}
			}
			return true;
		}

		/// <summary>The first loop of the stock food search: the best sum of path cost and quality penalty.</summary>
		private static int BestEdibleScore(List<FetchManager.Pickup> pickups, Storage destination, HashSet<Tag> exclude_tags, Tag[] required_tags)
		{
			int best = int.MaxValue;
			for (int i = 0; i < pickups.Count; i++)
			{
				FetchManager.Pickup pickup = pickups[i];
				Pickupable pickupable = pickup.pickupable;
				if (FetchManager.IsFetchablePickup_Exclude(pickupable.KPrefabID, pickupable.storage, pickupable.UnreservedFetchAmount, exclude_tags, required_tags, destination))
				{
					best = Math.Min(best, pickup.PathCost + (5 - pickup.foodQuality) * 50);
				}
			}
			return best;
		}

		/// <summary>Replaces the sum "to the delivery point + to the pile" in the task cost with an estimate of the whole trip.</summary>
		private static void FetchTargetAvailablePostfix(ref Chore.Precondition.Context context, bool __result)
		{
			if (!__result || !epochIsOurs)
			{
				return;
			}
			FetchChore chore = context.chore as FetchChore;
			Pickupable pickupable = context.data as Pickupable;
			ChoreConsumer consumer = context.consumerState.consumer;
			if (chore == null || pickupable == null || consumer == null)
			{
				return;
			}
			Navigator nav = consumer.navigator;
			Storage destination = chore.destination;
			if (nav == null || destination == null)
			{
				return;
			}
			int toDestination = nav.GetNavigationCost(destination);
			int toPickup = nav.GetNavigationCost(pickupable);
			if (toDestination < 0 || toPickup < 0)
			{
				return;
			}
			// The game has already added both values to the cost; we keep toPickup and replace toDestination with the estimate of the second leg.
			context.cost += TripCost(toDestination, toPickup, pickupable.cachedCell, Grid.PosToCell(destination)) - toPickup - toDestination;
		}

		private static void VerifyFetch(FetchManager manager, Storage destination, FetchChore chore, Pickupable ours, int ourCost)
		{
			Pickupable vanilla = null;
			int vanillaCost = -1;
			for (int i = 0; i < vanillaPickups.Count; i++)
			{
				if (FetchManager.IsFetchablePickup(vanillaPickups[i].pickupable, chore, destination))
				{
					vanilla = vanillaPickups[i].pickupable;
					vanillaCost = vanillaPickups[i].PathCost;
					break;
				}
			}
			if (vanilla == ours)
			{
				verifySame++;
			}
			else if (vanilla == null)
			{
				// The stock list keeps one pile per group; if it did not suit the task, the list does not see the others.
				verifyExtra++;
			}
			else if (ours == null)
			{
				verifyMissing++;
				LogVerify($"MISSING: vanilla found {vanilla.name} cost={vanillaCost}, criteria={chore.criteria} tag={chore.tagsFirst} tags={chore.tags.Count}");
			}
			else if (ourCost == vanillaCost)
			{
				verifyTie++;
			}
			else if (ourCost < vanillaCost)
			{
				verifyBetter++;
			}
			else
			{
				verifyWorse++;
				LogVerify($"WORSE: ours {ours.name} cost={ourCost}, vanilla {vanilla.name} cost={vanillaCost}, criteria={chore.criteria} tag={chore.tagsFirst}");
			}
		}

		private static void LogVerify(string message)
		{
			if (verifyLogged++ < 20)
			{
				Debug.LogWarning("[OniFixes] DemandDrivenFetch verify: " + message);
			}
		}

		private static void Forget()
		{
			LogStats();
			navigator = null;
			epochIsOurs = false;
			caches.Clear();
			cellCosts.Clear();
			groupsByCategoryTag.Clear();
			groupsByCategoryTagVersion = -1;
			vanillaPickups.Clear();
		}

		private static void LogStats()
		{
			if (epochs == 0)
			{
				return;
			}
			string text = $"[OniFixes] DemandDrivenFetch: updates={epochs}, per update: queries={(double)queries / epochs:F1} " +
				$"(by id {(double)queriesById / epochs:F1}, by own tag {(double)queriesByOwnTag / epochs:F1}, by category {(double)queriesByCategory / epochs:F1}), " +
				$"edible queries={(double)edibleQueries / epochs:F1}, groups built={(double)groupsBuilt / epochs:F1}, items scanned={(double)itemsScanned / epochs:F0}";
			if (verify)
			{
				text += $"; verify fetch: same={verifySame} tie={verifyTie} better={verifyBetter} extra={verifyExtra} WORSE={verifyWorse} MISSING={verifyMissing}; " +
					$"verify edible: same={verifyEdibleSame} DIFF={verifyEdibleDiff}";
			}
			Debug.Log(text);
		}
	}
}
