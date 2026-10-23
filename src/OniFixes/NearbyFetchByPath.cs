using System;
using System.Reflection;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// A Duplicant carrying things takes, besides the item it set out for, more of the same kind lying in a square of 9 x 9
	/// cells around it, in one trip. The game checks only that each can be reached at all, not how far the way is: an item
	/// two cells up behind a floor may be a long walk round, and the Duplicant goes for it, past nearer ones. Here such an
	/// item is left out when the way to it is much longer than the cells between: the path from the Duplicant to it may be
	/// longer than to the first item by no more than twice the distance between the two (at least three cells). That is a
	/// bound from below of the walk from the first item to it (the game knows path costs only from the Duplicant).
	/// </summary>
	internal static class NearbyFetchByPath
	{
		private const int CostPerCell = 10, MinCells = 3;
		internal static long kept, leftOut;
		internal static string Mode { get; private set; } = "off";

		internal static void Apply(Harmony harmony)
		{
			// The game's visitor is a lambda in FetchAreaChore.StatesInstance.Begin: a method of a compiler-made class that
			// holds root_fetchable and the instance.
			MethodInfo visitor = null;
			foreach (Type nested in typeof(FetchAreaChore.StatesInstance).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (AccessTools.Field(nested, "root_fetchable") == null) continue;
				foreach (MethodInfo m in nested.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
				{
					ParameterInfo[] ps = m.GetParameters();
					if (m.Name.Contains("Begin") && m.ReturnType == typeof(Util.IterationInstruction) && ps.Length == 2) visitor = m;
				}
			}
			if (visitor == null)
			{
				Mode = "off: the game's visitor was not found";
				Debug.LogWarning($"{ModContext.Tag} NearbyFetchByPath: {Mode}");
				return;
			}
			harmony.Patch(visitor, prefix: new HarmonyMethod(typeof(NearbyFetchByPath), nameof(NotFarByPath)));
			Mode = "on";
		}

		/// <summary>Whether an item near the first one is too far by path (a bound from below of the walk from the first).</summary>
		internal static bool TooFar(int rootCell, int rootCost, int cell, int cost)
		{
			if (rootCost < 0 || cost < 0)
			{
				return false; // the game decides (it checks the item can be reached)
			}
			Grid.CellToXY(rootCell, out int rx, out int ry);
			Grid.CellToXY(cell, out int x, out int y);
			int cells = Math.Max(Math.Abs(x - rx), Math.Abs(y - ry));
			return cost - rootCost > CostPerCell * Math.Max(MinCells, 2 * cells);
		}

		private static bool NotFarByPath(object __instance, object obj, ref Util.IterationInstruction __result)
		{
			try
			{
				Pickupable candidate = obj as Pickupable;
				Pickupable root = Traverse.Create(__instance).Field("root_fetchable").GetValue<Pickupable>();
				// Only items the game would take (of the first item's kind): the others it leaves out itself.
				if (candidate == null || root == null || candidate == root || candidate.KPrefabID == null || root.KPrefabID == null
					|| candidate.KPrefabID.PrefabTag != root.KPrefabID.PrefabTag)
				{
					return true;
				}
				object states = Traverse.Create(__instance).Field("<>4__this").GetValue();
				Navigator navigator = states != null ? Traverse.Create(states).Field("rootContext").Field("consumerState").Field("navigator").GetValue<Navigator>() : null;
				if (navigator == null)
				{
					return true;
				}
				int rootCell = Grid.PosToCell(root), cell = Grid.PosToCell(candidate);
				if (TooFar(rootCell, navigator.GetNavigationCost(rootCell), cell, navigator.GetNavigationCost(cell)))
				{
					leftOut++;
					if (leftOut <= 20)
					{
						Grid.CellToXY(rootCell, out int rx, out int ry);
						Grid.CellToXY(cell, out int x, out int y);
						Debug.Log($"{ModContext.Tag} NearbyFetchByPath: {navigator.GetProperName()} leaves {candidate.GetProperName()} at {x},{y} out of the trip from {rx},{ry}: path cost {navigator.GetNavigationCost(cell)} against {navigator.GetNavigationCost(rootCell)} to the first item");
					}
					__result = Util.IterationInstruction.Continue;
					return false;
				}
				kept++;
			}
			catch (Exception)
			{
			}
			return true;
		}
	}
}
