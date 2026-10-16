using HarmonyLib;

namespace OniFixes
{
	/// <summary>
	/// The game does not mark an animation for redraw at the moment it is started. It relies on two indirect
	/// signs: either the previous animation was still playing and got stopped (stopping marks it),
	/// or on the next update the frame number turns out different.
	///
	/// The second sign compares numbers of different nature: after the start, the stored frame number counts from the start
	/// of the animation file, while the update computes a number from the start of the animation itself. For the first
	/// animation in the file they coincide. If the controller has already been stopped by then and the new
	/// animation consists of a single frame (a typical "off"), the change never reaches the GPU:
	/// the building keeps showing the old frame until something unrelated redraws it,
	/// for example the highlight when the cursor hovers over it.
	///
	/// Here the animation is marked for redraw right when it starts.
	/// </summary>
	internal static class RedrawOnAnimStart
	{
		private static long starts, wouldBeMissed;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(KAnimControllerBase), nameof(KAnimControllerBase.StartQueuedAnim)),
				postfix: new HarmonyMethod(typeof(RedrawOnAnimStart), nameof(Postfix)));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)),
				prefix: new HarmonyMethod(typeof(RedrawOnAnimStart), nameof(LogStats)));
		}

		private static void Postfix(KAnimControllerBase __instance)
		{
			starts++;
			if (WouldBeMissed(__instance))
			{
				wouldBeMissed++;
			}
			__instance.SetDirty();
		}

		/// <summary>Statistics only: would the original have lost the redraw on this start.</summary>
		private static bool WouldBeMissed(KAnimControllerBase controller)
		{
			KAnim.Anim anim = controller.curAnim;
			KBatchedAnimController batched = controller as KBatchedAnimController;
			if (anim == null || batched == null || batched.batch == null)
			{
				return false;
			}
			// The next update will not see a frame change...
			if (controller.currentFrame != anim.GetFrameIdx(controller.mode, controller.elapsedTime))
			{
				return false;
			}
			// ...and the controller has not yet been marked by the stop of the previous animation.
			KAnimBatch batch = batched.batch;
			return !batch.controllersToIdx.TryGetValue(batched, out int index) || !batch.dirtySet.Contains(index);
		}

		private static void LogStats()
		{
			if (starts > 0)
			{
				Debug.Log($"[OniFixes] RedrawOnAnimStart: animation starts={starts}, redraws the game would have missed={wouldBeMissed}");
			}
		}
	}
}
