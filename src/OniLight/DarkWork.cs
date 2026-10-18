using System;
using System.Collections.Generic;
using HarmonyLib;
using Klei.AI;
using OniCommon;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// The Blackout mode, what darkness does to Duplicants (by the real light of their cell, not what the player sees):
	/// - Work by the light where the Duplicant works: -50% in total darkness, -20% in dim light (1-499 lux), as usual from
	///   500 lux, +15% from 1000 lux (the game gives +15% for any light). Status items say which.
	/// - In total darkness no research, art, medicine, cooking or machine work: such errands are not taken where it is
	///   dark, and stop when the light goes out.
	/// - Moving in total darkness is 20% slower (walking, climbing, swimming; not the transit tubes).
	/// - "In the Dark": awake in total darkness for over a minute, a Duplicant grows stressed (+10% a cycle).
	/// </summary>
	internal static class DarkWork
	{
		internal const string InTheDarkId = "OniLightInTheDark";
		private const float InTheDarkAfter = 60f, CheckPeriod = 1f, DarkMove = 0.8f;
		internal const float Dim = 500f, Bright = 1000f;

		private static Effect inTheDark;
		private static StatusItem darkWorkspace, dimWorkspace;
		private static readonly Dictionary<Workable, (Guid handle, StatusItem item)> offered = new Dictionary<Workable, (Guid, StatusItem)>();
		private static readonly Dictionary<MinionIdentity, float> darkSince = new Dictionary<MinionIdentity, float>();
		private static readonly HashSet<WorkerBase> toStop = new HashSet<WorkerBase>();
		private static HashSet<ChoreGroup> needLight;
		private static float lastCheck = -1f;
		internal static long Slowed, Stopped, Checks, InTheDarkCount;

		/// <summary>For the self-check: how long a Duplicant has been awake in total darkness, s (-1: not now).</summary>
		internal static float DarkFor(MinionIdentity minion) =>
			minion != null && GameClock.Instance != null && darkSince.TryGetValue(minion, out float since) ? GameClock.Instance.GetTime() - since : -1f;

		internal static readonly Chore.Precondition LitEnough = new Chore.Precondition
		{
			id = "OniLightLitEnough",
			description = ONILIGHT.CHORES.TOODARK,
			fn = (ref Chore.Precondition.Context context, object data) => !IsDarkAt(context.chore.gameObject),
			canExecuteOnAnyThread = false,
		};

		internal static void Apply(Harmony harmony)
		{
			// Another mod may have made the game's database already (by asking for it while loading): then add at once.
			if (Db._Instance != null)
			{
				AddToDb();
			}
			else
			{
				harmony.Patch(AccessTools.Method(typeof(Db), nameof(Db.Initialize)), postfix: new HarmonyMethod(typeof(DarkWork), nameof(AddToDb)));
			}
			harmony.Patch(AccessTools.Method(typeof(Workable), nameof(Workable.GetEfficiencyMultiplier)), postfix: new HarmonyMethod(typeof(DarkWork), nameof(ByLight)));
			harmony.Patch(AccessTools.Method(typeof(Workable), nameof(Workable.StopWork)), postfix: new HarmonyMethod(typeof(DarkWork), nameof(Stopping)));
			harmony.Patch(AccessTools.Method(typeof(ChoreProvider), nameof(ChoreProvider.AddChore)), postfix: new HarmonyMethod(typeof(DarkWork), nameof(AddLightPrecondition)));
			harmony.Patch(AccessTools.Method(typeof(Workable), nameof(Workable.WorkTick)), prefix: new HarmonyMethod(typeof(DarkWork), nameof(StopInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(BipedTransitionLayer), nameof(BipedTransitionLayer.BeginTransition)), postfix: new HarmonyMethod(typeof(DarkWork), nameof(SlowerInTheDark)));
			harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(DarkWork), nameof(Frame)) { priority = Priority.Low });
		}

		private static void AddToDb()
		{
			string stress = Db.Get().Amounts.Stress.deltaAttribute.Id;
			inTheDark = new Effect(InTheDarkId, ONILIGHT.EFFECTS.INTHEDARK.NAME, ONILIGHT.EFFECTS.INTHEDARK.TOOLTIP, 0f, true, false, true);
			inTheDark.Add(new AttributeModifier(stress, 10f / 600f, ONILIGHT.EFFECTS.INTHEDARK.NAME));
			Db.Get().effects.Add(inTheDark);
			Debug.Log($"{ModContext.Tag} darkness: effect {InTheDarkId} and the workspace status items added");
			darkWorkspace = new StatusItem("OniLightDarkWorkspace", ONILIGHT.STATUSITEMS.DARKWORKSPACE.NAME, ONILIGHT.STATUSITEMS.DARKWORKSPACE.TOOLTIP, "",
				StatusItem.IconType.Info, NotificationType.Bad, false, OverlayModes.None.ID);
			dimWorkspace = new StatusItem("OniLightDimWorkspace", ONILIGHT.STATUSITEMS.DIMWORKSPACE.NAME, ONILIGHT.STATUSITEMS.DIMWORKSPACE.TOOLTIP, "",
				StatusItem.IconType.Info, NotificationType.BadMinor, false, OverlayModes.None.ID);
			Database.ChoreGroups groups = Db.Get().ChoreGroups;
			needLight = new HashSet<ChoreGroup> { groups.Research, groups.Art, groups.MedicalAid, groups.Cook, groups.MachineOperating };
		}

		internal static int Lux(int cell) => Grid.IsValidCell(cell) ? Grid.LightIntensity[cell] : 0;

		private static bool IsDarkAt(GameObject go) => go != null && Lux(Grid.PosToCell(go)) <= 0;

		/// <summary>The share of work speed the light gives, against the usual.</summary>
		internal static float Bonus(int lux) => lux <= 0 ? -0.5f : lux < Dim ? -0.2f : lux < Bright ? 0f : 0.15f;

		private static void ByLight(Workable __instance, WorkerBase worker, ref float __result)
		{
			if (!__instance.lightEfficiencyBonus || worker == null)
			{
				return;
			}
			int lux = Lux(Grid.PosToCell(worker.gameObject));
			// The game has added its +15% for any light; the light's own share instead.
			float game = lux > 0 ? TUNING.DUPLICANTSTATS.STANDARD.Light.LIGHT_WORK_EFFICIENCY_BONUS : 0f;
			__result = Mathf.Max(__result - game + Bonus(lux), __instance.minimumAttributeMultiplier);
			// The game's "lit workspace" only for bright light; dim and dark have their own.
			if (lux < Bright && __instance.lightEfficiencyBonusStatusItemHandle != Guid.Empty)
			{
				worker.RevokeStatusItem(__instance.lightEfficiencyBonusStatusItemHandle);
				__instance.lightEfficiencyBonusStatusItemHandle = Guid.Empty;
			}
			StatusItem wanted = lux <= 0 ? darkWorkspace : lux < Dim ? dimWorkspace : null;
			offered.TryGetValue(__instance, out (Guid handle, StatusItem item) current);
			if (current.item == wanted)
			{
				return;
			}
			if (current.handle != Guid.Empty)
			{
				worker.RevokeStatusItem(current.handle);
			}
			if (wanted != null)
			{
				offered[__instance] = (worker.OfferStatusItem(wanted, __instance), wanted);
			}
			else
			{
				offered.Remove(__instance);
			}
		}

		private static void Stopping(Workable __instance, WorkerBase workerToStop)
		{
			if (offered.TryGetValue(__instance, out (Guid handle, StatusItem item) current))
			{
				if (workerToStop != null && current.handle != Guid.Empty)
				{
					workerToStop.RevokeStatusItem(current.handle);
				}
				offered.Remove(__instance);
			}
		}

		internal static bool NeedsLight(Chore chore)
		{
			if (needLight == null || chore?.choreType?.groups == null)
			{
				return false;
			}
			foreach (ChoreGroup group in chore.choreType.groups)
			{
				if (needLight.Contains(group)) return true;
			}
			return false;
		}

		private static void AddLightPrecondition(ChoreProvider __instance, Chore chore)
		{
			if (NeedsLight(chore))
			{
				chore.AddPrecondition(LitEnough);
			}
		}

		private static void StopInTheDark(Workable __instance, WorkerBase worker)
		{
			if (worker == null || Lux(Grid.PosToCell(worker.gameObject)) > 0)
			{
				return;
			}
			Chore chore = worker.GetComponent<ChoreDriver>()?.GetCurrentChore();
			if (NeedsLight(chore))
			{
				toStop.Add(worker); // stopped after the frame's work, not in the middle of it
			}
		}

		private static void SlowerInTheDark(Navigator navigator, Navigator.ActiveTransition transition)
		{
			if (navigator == null || transition.start == NavType.Tube || transition.end == NavType.Tube || Lux(Grid.PosToCell(navigator)) > 0
				|| navigator.GetComponent<MinionIdentity>() == null)
			{
				return;
			}
			transition.speed *= DarkMove;
			transition.animSpeed *= DarkMove;
			Slowed++;
		}

		private static void Frame()
		{
			if (toStop.Count > 0)
			{
				foreach (WorkerBase worker in toStop)
				{
					ChoreDriver driver = worker != null ? worker.GetComponent<ChoreDriver>() : null;
					if (driver != null && NeedsLight(driver.GetCurrentChore()))
					{
						driver.StopChore();
						Stopped++;
					}
				}
				toStop.Clear();
			}
			if (GameClock.Instance == null || inTheDark == null)
			{
				return;
			}
			float now = GameClock.Instance.GetTime();
			if (now < lastCheck + CheckPeriod)
			{
				return;
			}
			lastCheck = now;
			Checks++;
			foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
			{
				if (minion == null)
				{
					continue;
				}
				int feet = Grid.PosToCell(minion);
				bool dark = !minion.HasTag(GameTags.Asleep) && Lux(feet) <= 0 && Lux(Grid.CellAbove(feet)) <= 0;
				Effects effects = minion.GetComponent<Effects>();
				if (!dark)
				{
					darkSince.Remove(minion);
					if (effects != null && effects.HasEffect(InTheDarkId)) effects.Remove(InTheDarkId);
					continue;
				}
				if (!darkSince.TryGetValue(minion, out float since))
				{
					darkSince[minion] = now;
				}
				else if (now - since >= InTheDarkAfter && effects != null && !effects.HasEffect(InTheDarkId))
				{
					effects.Add(inTheDark, should_save: false);
					InTheDarkCount++;
				}
			}
		}
	}
}
