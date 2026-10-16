using System;
using HarmonyLib;
using OniCommon;

namespace OniDup
{
	/// <summary>
	/// No escape from being buried. When solid ground forms in a Duplicant's cells (sand falls on them, a tile is finished in
	/// their cell, liquid freezes around them), the game looks for a free cell nearby, as far as a few cells away, and moves
	/// the Duplicant there. Now they stay where they are, in the game's own "Entombed" state: buried up to the waist or over
	/// the head (its animations), with the Entombed status and notification, until someone digs them out. Breathing goes on
	/// by the game's rules: a Duplicant whose face is buried still takes air from the free cells next to their head, unless
	/// the mod option says buried Duplicants suffocate (DupAir).
	///
	/// A door that closes or locks on a Duplicant still lets them out as before, and so does the game's move back into the
	/// world from beyond its edge. Robots are not affected.
	/// </summary>
	internal static class DupEntombed
	{
		internal static string Mode { get; private set; } = "off";

		internal static void Apply(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(FallMonitor.Instance), nameof(FallMonitor.Instance.TryEntombedEscape)),
					prefix: new HarmonyMethod(typeof(DupEntombed), nameof(StayBuried)));
				Mode = "on";
			}
			catch (Exception e)
			{
				Mode = "off: " + e.Message;
				Debug.LogWarning($"{ModContext.Tag} DupEntombed failed to apply: {e}");
			}
		}

		private static bool StayBuried(FallMonitor.Instance __instance)
		{
			if (__instance.GetComponent<MinionIdentity>() == null)
			{
				return true;
			}
			int cell = Grid.PosToCell(__instance.transform.GetPosition());
			int head = Grid.CellAbove(cell);
			if (!Buried(cell) && !Buried(head))
			{
				return true; // a door or a cell closed to Duplicants: the game's way out
			}
			__instance.GoTo(__instance.sm.entombed.stuck);
			return false;
		}

		internal static bool Buried(int cell)
		{
			return Grid.IsValidCell(cell) && Grid.Solid[cell] && !Grid.DupePassable[cell] && !Grid.HasDoor[cell];
		}
	}
}
