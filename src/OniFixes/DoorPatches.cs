using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// Patches of the Door class are put in only once the game's animations are loaded. Door keeps the animation a
	/// Duplicant plays to change a door's setting in a static field, read from the game's assets when the class is first
	/// touched; patching a method of the class while mods load touched it before the animations were there, the field
	/// stayed empty, and a Duplicant going to open or lock a door crashed the game (KAnimControllerBase.AddAnimOverrides).
	/// Here the door patches wait for the end of Assets.OnPrefabInit (or go in at once if that is over), and the field is
	/// filled again if it was left empty (by any mod).
	/// Self-check: the file verify_door_toggle.txt in the mod folder: a door of the active world is set to another setting the
	/// usual way (a Duplicant goes to change it), and the log gets whether that was done without an error.
	/// </summary>
	internal static class DoorPatches
	{
		private static readonly List<Action<Harmony>> waiting = new List<Action<Harmony>>();
		private static Harmony harmony;
		private static bool hooked, ready, checkHooked;

		internal static void Later(Harmony h, Action<Harmony> apply)
		{
			harmony = h;
			if (!checkHooked)
			{
				checkHooked = true;
				if (File.Exists(Path.Combine(ModContext.Folder, "verify_door_toggle.txt")))
				{
					h.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(DoorPatches), nameof(Verify)));
				}
			}
			if (ready || (Assets.instance != null && Assets.AnimTable != null && Assets.AnimTable.Count > 0))
			{
				ready = true;
				Repair();
				apply(h);
				return;
			}
			waiting.Add(apply);
			if (!hooked)
			{
				hooked = true;
				h.Patch(AccessTools.Method(typeof(Assets), "OnPrefabInit"), postfix: new HarmonyMethod(typeof(DoorPatches), nameof(AssetsReady)));
			}
		}

		private static void AssetsReady()
		{
			ready = true;
			Repair();
			foreach (Action<Harmony> apply in waiting)
			{
				try
				{
					apply(harmony);
				}
				catch (Exception e)
				{
					Debug.LogWarning($"{ModContext.Tag} door patches: {e}");
				}
			}
			waiting.Clear();
		}

		/// <summary>The animation for changing a door's setting, if the field was filled before the animations were loaded.</summary>
		private static void Repair()
		{
			var anims = (KAnimFile[])AccessTools.Field(typeof(Door), "OVERRIDE_ANIMS").GetValue(null);
			if (anims != null && anims.Length > 0 && anims[0] == null)
			{
				anims[0] = Assets.GetAnim("anim_use_remote_kanim");
				Debug.Log($"{ModContext.Tag} door patches: the door's work animation was empty (the class was touched before the animations were loaded), filled again: {anims[0] != null}");
			}
		}
	

		// ---------- Self-check ----------

		private static float verifyStart = -1f;
		private static int step, errors;
		private static string lastError;
		private static Door door;
		private static Door.ControlState before;

		private static void CountErrors(string message, string stack, LogType type)
		{
			if (type == LogType.Exception || type == LogType.Error)
			{
				errors++;
				lastError = message.Length > 160 ? message.Substring(0, 160) : message;
			}
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
					foreach (BuildingComplete b in Components.BuildingCompletes.Items)
					{
						Door d = b != null ? b.GetComponent<Door>() : null;
						if (d != null && d.GetMyWorldId() == ClusterManager.Instance.activeWorldId && d.GetComponent<Operational>()?.IsOperational != false) { door = d; break; }
					}
					step = door == null ? 2 : 1;
					if (door == null)
					{
						Debug.Log($"{ModContext.Tag} door toggle check: skipped (no door)");
						return;
					}
					var anims = (KAnimFile[])AccessTools.Field(typeof(Door), "OVERRIDE_ANIMS").GetValue(null);
					before = door.CurrentState;
					Application.logMessageReceived += CountErrors;
					door.QueueStateChange(before == Door.ControlState.Locked ? Door.ControlState.Opened : Door.ControlState.Locked);
					Debug.Log($"{ModContext.Tag} door toggle check: {door.GetProperName()} at {Grid.PosToCell(door)} set from {before}; the work animation {(anims != null && anims.Length > 0 && anims[0] != null ? "is there" : "is EMPTY")}");
				}
				else if (step == 1 && (door == null || door.CurrentState != before || now >= verifyStart + 100f))
				{
					step = 2;
					Application.logMessageReceived -= CountErrors;
					bool done = door != null && door.CurrentState != before;
					Debug.Log($"{ModContext.Tag} door toggle check: {(done && errors == 0 ? "ok" : "FAILED")} (changed by a Duplicant {done}, errors {errors}{(lastError != null ? ": " + lastError : "")})");
				}
			}
			catch (Exception e)
			{
				step = 2;
				Debug.LogWarning($"{ModContext.Tag} door toggle check: FAILED with {e}");
			}
		}
	}
}
