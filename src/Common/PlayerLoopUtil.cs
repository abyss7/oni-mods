using System;
using System.Collections.Generic;
using UnityEngine.LowLevel;

namespace OniCommon
{
	internal static class PlayerLoopUtil
	{
		/// <summary>Inserts the handler as the very first stage of the frame. The marker type gives the stage a name in the profiler.</summary>
		internal static void InsertAtFrameStart(Type marker, PlayerLoopSystem.UpdateFunction update)
		{
			PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
			PlayerLoopSystem[] phases = root.subSystemList;
			PlayerLoopSystem first = phases[0];
			List<PlayerLoopSystem> stages = new List<PlayerLoopSystem>(first.subSystemList ?? new PlayerLoopSystem[0]);
			stages.Insert(0, new PlayerLoopSystem { type = marker, updateDelegate = update });
			first.subSystemList = stages.ToArray();
			phases[0] = first;
			root.subSystemList = phases;
			PlayerLoop.SetPlayerLoop(root);
		}
	}
}
