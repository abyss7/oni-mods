using System;
using System.Collections.Generic;
using HarmonyLib;
using OniCommon;

namespace OniDup
{
	/// <summary>
	/// Digging and building without the hiccups: a duplicant who has to step aside stops and comes back properly, and finishes
	/// the dig or the build they started.
	///
	/// - Moved to another cell while working (by the game: out of a door that closed on them, back into the world from beyond
	///   its edge; being buried no longer moves anyone, see DupEntombed): the game plays the idle animation, but the work goes
	///   on: the duplicant stands still and the work progresses from a cell that may not even reach it. Now the work stops
	///   there, and the duplicant comes back to it by the usual way, with the animation and the progress bar.
	/// - A dig or a build that finishes in the first half second of the tool's swing: the "work done" signal comes before the
	///   swing loop starts and is lost, so the duplicant keeps swinging for 10 s with no progress bar until the game gives up
	///   waiting. Now the tool goes straight to its final animation.
	/// - After a short break (a cough, a reaction) a duplicant picks the next errand afresh and
	///   often takes another tile of the same priority, leaving the started one half done. Now among errands of the same
	///   priority they come back to a dig or a build they started, unless it is more than two steps further than the other.
	/// </summary>
	internal static class DupWork
	{
		private const int ExtraCost = 20; // two steps of walking

		internal static string Mode { get; private set; } = "off";

		private static int escapes, swings, resumes;
		private static float lastReport = float.MinValue;

		internal static void Apply(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(FallMonitor.Instance), "MoveToCell"), prefix: new HarmonyMethod(typeof(DupWork), nameof(BeforeEscape)),
					postfix: new HarmonyMethod(typeof(DupWork), nameof(AfterEscape)));
				harmony.Patch(AccessTools.Method(typeof(StandardWorker), "StartPlayingPostAnim"), postfix: new HarmonyMethod(typeof(DupWork), nameof(FinishSwing)));
				harmony.Patch(AccessTools.Method(typeof(ChoreConsumer), "ChooseChore"), prefix: new HarmonyMethod(typeof(DupWork), nameof(ResumeStarted)));
				Mode = "on";
			}
			catch (Exception e)
			{
				Mode = "off: " + e.Message;
				Debug.LogWarning($"{ModContext.Tag} DupWork failed to apply: {e}");
			}
		}

		private static void BeforeEscape(FallMonitor.Instance __instance, out bool __state)
		{
			WorkerBase worker = __instance.GetComponent<WorkerBase>();
			__state = worker != null && worker.GetState() == WorkerBase.State.Working;
		}

		private static void AfterEscape(FallMonitor.Instance __instance, bool __state)
		{
			ChoreDriver driver = __state ? __instance.GetComponent<ChoreDriver>() : null;
			if (driver != null && driver.HasChore())
			{
				driver.StopChore();
				escapes++;
				Report();
			}
		}

		private static void FinishSwing(StandardWorker __instance)
		{
			if (__instance.GetState() == WorkerBase.State.PendingCompletion && __instance.smi is MultitoolController.Instance tool &&
				tool.GetCurrentState() == tool.sm.pre)
			{
				tool.GoTo(tool.sm.pst);
				swings++;
				Report();
			}
		}

		/// <summary>
		/// The game takes the last successful context of the sorted list. Before it, among the contexts of the same priority
		/// (everything but the cost equal), look for a dig or a build this duplicant started and left.
		/// </summary>
		private static bool ResumeStarted(ChoreConsumer __instance, ref Chore.Precondition.Context out_context, List<Chore.Precondition.Context> succeeded_contexts, ref bool __result)
		{
			ChoreDriver driver = __instance.choreDriver;
			if (driver == null || driver.GetCurrentChore() != null)
			{
				return true;
			}
			int top = succeeded_contexts.Count - 1;
			while (top >= 0 && !succeeded_contexts[top].IsSuccess())
			{
				top--;
			}
			if (top < 0 || Started(succeeded_contexts[top].chore, driver))
			{
				return true;
			}
			Chore.Precondition.Context best = succeeded_contexts[top];
			for (int i = top - 1; i >= 0; i--)
			{
				Chore.Precondition.Context context = succeeded_contexts[i];
				if (!context.IsSuccess() || !SamePriority(context, best) || context.cost > best.cost + ExtraCost)
				{
					break; // sorted: further down the priority is lower or the way longer
				}
				if (Started(context.chore, driver))
				{
					out_context = context;
					__result = true;
					resumes++;
					return false;
				}
			}
			return true;
		}

		private static bool SamePriority(Chore.Precondition.Context a, Chore.Precondition.Context b)
		{
			return a.masterPriority.priority_class == b.masterPriority.priority_class && a.personalPriority == b.personalPriority &&
				a.masterPriority.priority_value == b.masterPriority.priority_value && a.priority == b.priority && a.priorityMod == b.priorityMod &&
				a.consumerPriority == b.consumerPriority;
		}

		/// <summary>A dig or a build that this duplicant worked on last, nobody works on now, and that is partly done.</summary>
		private static bool Started(Chore chore, ChoreDriver driver)
		{
			if (chore == null || chore.driver != null || chore.lastDriver != driver || !(chore is WorkChore<Diggable> || chore is WorkChore<Constructable>))
			{
				return false;
			}
			if (chore.gameObject == null)
			{
				return false;
			}
			if (chore is WorkChore<Diggable>)
			{
				int cell = Grid.PosToCell(chore.gameObject);
				return Grid.IsValidCell(cell) && Grid.Damage[cell] > 0f;
			}
			Constructable constructable = chore.gameObject.GetComponent<Constructable>();
			float done = constructable != null ? constructable.GetPercentComplete() : 0f;
			return done > 0f && done < 1f;
		}

		/// <summary>The counts in the log, at most once a cycle.</summary>
		private static void Report()
		{
			float now = GameClock.Instance != null ? GameClock.Instance.GetTime() : 0f;
			if (now - lastReport < 600f)
			{
				return;
			}
			lastReport = now;
			Debug.Log($"{ModContext.Tag} work: stopped after a move {escapes}, swings finished early {swings}, started digs and builds resumed {resumes}");
		}
	}
}
