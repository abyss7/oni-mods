using System;
using System.IO;
using HarmonyLib;
using Klei.AI;
using OniCommon;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// A slowed Duplicant (low Athletics: Carbon Dioxide, little Oxygen, the game's own penalties; deep liquid) moves in slow
	/// motion: the game slows the animation of a step by half as much as the movement, and an animation played slower shows
	/// each of its drawings longer, so the Duplicant moves in jerks. Here no move is animated slower than its normal pace:
	/// - walking, climbing a ladder or a pole (looping moves): the body moves slower, the steps keep their pace;
	/// - a single move (a jump onto a ledge, getting on or off a ladder) lasts as long as its animation: it is not slowed.
	/// Faster moves (high Athletics, fast ladders and floors) stay as the game makes them.
	/// Self-check: the file verify_smooth_moves.txt in the mod folder: a few seconds after loading, a Duplicant of the active
	/// world is given -40 Athletics for half a minute (a skilled one is not slowed by less), and its moves are counted: the
	/// slowed ones kept at their pace, and any animated below its pace.
	/// </summary>
	internal static class SmoothSlowMoves
	{
		internal static int kept, slowedLooping;
		private static int animatedSlower;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(BipedTransitionLayer), nameof(BipedTransitionLayer.BeginTransition)),
				prefix: new HarmonyMethod(typeof(SmoothSlowMoves), nameof(Before)), postfix: new HarmonyMethod(typeof(SmoothSlowMoves), nameof(After)) { priority = Priority.Last });
			if (File.Exists(Path.Combine(ModContext.Folder, "verify_smooth_moves.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(SmoothSlowMoves), nameof(Verify)));
				// A finalizer: it sees the move after every postfix.
				harmony.Patch(AccessTools.Method(typeof(BipedTransitionLayer), nameof(BipedTransitionLayer.BeginTransition)),
					finalizer: new HarmonyMethod(typeof(SmoothSlowMoves), nameof(Watch)));
			}
		}

		private static void Before(Navigator.ActiveTransition transition, out float __state)
		{
			__state = transition != null ? transition.animSpeed : 1f;
			lastPace = __state;
		}

		private static void After(Navigator.ActiveTransition transition, float __state)
		{
			if (transition == null || transition.animSpeed >= __state)
			{
				return;
			}
			if (transition.isLooping) slowedLooping++;
			transition.animSpeed = __state;
			kept++;
		}

		// ---------- Self-check ----------

		private static float verifyStart = -1f;
		private static int step;
		private static Navigator watched;
		private static AttributeModifier penalty;
		private static int moves, slowMoves, keptBefore;
		private static float lastPace = 1f; // the pace the move had before the layer changed it

		private static Exception Watch(Navigator navigator, Navigator.ActiveTransition transition, Exception __exception)
		{
			if (navigator == null || navigator != watched || transition == null)
			{
				return __exception;
			}
			moves++;
			if (transition.isLooping && transition.speed < 1.5f) slowMoves++;
			if (transition.animSpeed < lastPace - 0.001f) animatedSlower++;
			return __exception;
		}

		private static void Verify()
		{
			if (step > 1 || GameClock.Instance == null || ClusterManager.Instance == null)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			if (verifyStart < 0f) verifyStart = now;
			try
			{
				if (step == 0 && now >= verifyStart + 8f)
				{
					step = 1;
					foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
					{
						if (minion != null && minion.GetMyWorldId() == ClusterManager.Instance.activeWorldId && !minion.HasTag(GameTags.Asleep))
						{
							watched = minion.GetComponent<Navigator>();
							break;
						}
					}
					if (watched == null)
					{
						step = 2;
						Debug.Log($"{ModContext.Tag} smooth moves check: skipped (no Duplicant)");
						return;
					}
					keptBefore = kept;
					penalty = new AttributeModifier(Db.Get().Attributes.Athletics.Id, -40f, "smooth moves check");
					watched.GetAttributes().Add(penalty);
				}
				else if (step == 1 && now >= verifyStart + 38f)
				{
					step = 2;
					float multiplier = BipedTransitionLayer.GetMovementSpeedMultiplier(Db.Get().AttributeConverters.MovementSpeed.Lookup(watched.gameObject));
					watched.GetAttributes().Remove(penalty);
					int keptHere = kept - keptBefore;
					bool ok = moves > 0 && keptHere > 0 && animatedSlower == 0;
					Debug.Log($"{ModContext.Tag} smooth moves check: {(ok ? "ok" : "FAILED")} ({watched.GetProperName()} with -40 Athletics, movement x{multiplier:F2}: " +
						$"{moves} moves, {slowMoves} looping ones slower than 1.5 cells/s, slowed ones kept at their pace {keptHere}, animated below their pace {animatedSlower}; " +
						$"kept at pace in all {kept}, of them looping {slowedLooping})");
				}
			}
			catch (Exception e)
			{
				step = 2;
				Debug.LogWarning($"{ModContext.Tag} smooth moves check: FAILED with {e}");
			}
		}
	}
}
