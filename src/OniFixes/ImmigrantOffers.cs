using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using HarmonyLib;
using Database;
using Klei.AI;
using KSerialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OniCommon;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// The Printing Pod's choice in the game save. The candidates the pod offers (duplicants and care packages) exist only
	/// on the selection screen: the game rolls them anew every time the screen is built, and a save holds none of them.
	/// So saving and loading re-rolled a pending choice, and a Duplicant that looked good could not be kept for later.
	///
	/// Here the current choice is written to the save (as JSON, next to the Immigration component, which the save already
	/// holds) and, while the same print is still pending, the screen is built from it: the same personalities with their
	/// names, voices, traits, aptitudes, starting levels and outfits, the same care packages with their facades, in the
	/// same places in the row. A print that was taken, rejected or lost rolls anew, as in the game.
	/// </summary>
	[SerializationConfig(KSerialization.MemberSerialization.OptIn)]
	public sealed class ImmigrantOffersState : KMonoBehaviour
	{
		[Serialize]
		private string offers;

		[OnSerializing]
		private void BeforeSave()
		{
			// Failed to capture the choice: the save keeps the previous one rather than nothing.
			offers = ImmigrantOffers.Capture() ?? offers;
		}

		public override void OnSpawn()
		{
			base.OnSpawn();
			ImmigrantOffers.Loaded(offers, this);
		}

		/// <summary>The pending print is gone (taken, rejected or lost): nothing to restore at the next one.</summary>
		internal void Consumed()
		{
			offers = null;
		}
	}

	internal static class ImmigrantOffers
	{
		// One offered candidate. A duplicant has the fields of a rolled MinionStartingStats that a personality does not
		// already decide; a care package is its kind plus the facade it was given.
		private sealed class Offer
		{
			public bool dup;
			public int place;
			public string personality, name, stress, joy, congenital, outfit;
			public int voice, rarity;
			public bool valid;
			public List<string> traits = new List<string>();
			public List<KeyValuePair<string, float>> aptitudes = new List<KeyValuePair<string, float>>();
			public List<KeyValuePair<string, int>> levels = new List<KeyValuePair<string, int>>();
			public string package, facade;
		}

		private sealed class Data
		{
			public List<Offer> offers = new List<Offer>();
		}

		private static string saved;      // the choice from the save, as it came in (JSON); null or "[]" = none
		private static ImmigrantOffersState state;
		private static readonly Dictionary<ITelepadDeliverableContainer, Offer> assigned = new Dictionary<ITelepadDeliverableContainer, Offer>();

		// Self-check: file verify_immigrant_offers.txt in the mod folder.
		// - with the word "set": the pod is made ready, the screen is opened and closed, and the game is saved to
		//   save_files/OniPodCheck/OniPodCheck.sav;
		// - with any other content: that save is loaded, the screen is opened and every restored candidate is compared
		//   with the one in the save; the first duplicant is then delivered and the choice must be spent.
		// Both runs write the verdict to the log and close the game.
		private const int OpenAtFrame = 600, CheckAtFrame = 720;
		private static string verify;
		private static int verifyFrame, verifyStep, minionsBefore;
		private static bool same, places, delivered;
		private static CharacterContainer first;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SaveGame), "OnPrefabInit"),
				postfix: new HarmonyMethod(typeof(ImmigrantOffers), nameof(AttachState)));
			harmony.Patch(AccessTools.Method(typeof(Immigration), nameof(Immigration.EndImmigration)),
				postfix: new HarmonyMethod(typeof(ImmigrantOffers), nameof(Consumed)));
			harmony.Patch(AccessTools.Method(typeof(CharacterSelectionController), "InitializeContainers"),
				prefix: new HarmonyMethod(typeof(ImmigrantOffers), nameof(BuildFromSave)));
			harmony.Patch(AccessTools.Method(typeof(CharacterContainer), nameof(CharacterContainer.GenerateCharacter)),
				postfix: new HarmonyMethod(typeof(ImmigrantOffers), nameof(MinionRolled)));
			harmony.Patch(AccessTools.Method(typeof(CarePackageContainer), "GenerateCharacter"),
				postfix: new HarmonyMethod(typeof(ImmigrantOffers), nameof(CarePackageRolled)));
			string marker = Path.Combine(ModContext.Folder, "verify_immigrant_offers.txt");
			verify = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
			if (verify != null)
			{
				harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.LateUpdate)),
					postfix: new HarmonyMethod(typeof(ImmigrantOffers), nameof(VerifyFrame)));
			}
		}

		private static void AttachState(SaveGame __instance)
		{
			__instance.gameObject.AddOrGet<ImmigrantOffersState>();
		}

		private static void Consumed()
		{
			// Every way a print can end (taken at the pod, rejected, nobody came for it, a quest hijacked the pod)
			// runs through here, so the stored choice goes with it.
			saved = null;
			state?.Consumed();
		}

		/// <summary>The choice from the save (JSON, as it came in), or null when the save holds none.</summary>
		internal static void Loaded(string fromSave, ImmigrantOffersState owner)
		{
			saved = string.IsNullOrEmpty(fromSave) || fromSave == "[]" ? null : fromSave;
			state = owner;
		}

		// ---- capturing into the save ---------------------------------------------------------------------------------

		/// <summary>The choice as it stands now; null to keep what the save already holds.</summary>
		internal static string Capture()
		{
			try
			{
				if (Immigration.Instance == null)
				{
					return null;
				}
				if (!Immigration.Instance.ImmigrantsAvailable)
				{
					return "[]";
				}
				return EncodeScreen() ?? saved;
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniFixes] ImmigrantOffers: the pod's choice was not captured for the save: {e}");
				return null;
			}
		}

		/// <summary>The screen's candidates as JSON; null if there is nothing to capture right now.</summary>
		private static string EncodeScreen()
		{
			ImmigrantScreen screen = ImmigrantScreen.instance;
			List<ITelepadDeliverableContainer> containers = Containers(screen);
			if (screen == null || screen.IsStarterMinion || containers == null || containers.Count == 0)
			{
				return null;
			}
			Data data = new Data();
			foreach (ITelepadDeliverableContainer container in containers)
			{
				Offer offer = Encode(container);
				if (offer == null)
				{
					return null;   // a candidate is still rolling: keep the choice already in the save
				}
				offer.place = container.GetGameObject().transform.GetSiblingIndex();
				data.offers.Add(offer);
			}
			return JsonConvert.SerializeObject(data);
		}

		private static Offer Encode(ITelepadDeliverableContainer container)
		{
			if (container is CharacterContainer minion)
			{
				MinionStartingStats stats = minion.Stats;
				if (stats == null)
				{
					return null;
				}
				return new Offer
				{
					dup = true,
					personality = stats.personality.Id,
					name = stats.Name,
					stress = stats.stressTrait.Id,
					joy = stats.joyTrait.Id,
					congenital = stats.congenitaltrait != null ? stats.congenitaltrait.Id : "",
					outfit = stats.overrideOutfitID ?? "",
					voice = stats.voiceIdx,
					rarity = stats.rarityBalance,
					valid = stats.IsValid,
					traits = stats.Traits.Select(trait => trait.Id).ToList(),
					aptitudes = stats.skillAptitudes.Select(pair => new KeyValuePair<string, float>(pair.Key.Id, pair.Value)).ToList(),
					levels = stats.StartingLevels.Select(pair => new KeyValuePair<string, int>(pair.Key, pair.Value)).ToList(),
				};
			}
			CarePackageContainer package = container as CarePackageContainer;
			if (package != null && package.carePackageInstanceData != null)
			{
				return new Offer { dup = false, package = package.carePackageInstanceData.info.id, facade = package.carePackageInstanceData.facadeID ?? "" };
			}
			return null;
		}

		// ---- building the screen from the save -----------------------------------------------------------------------

		/// <summary>Replaces the screen's roll with the saved candidates while the same print is still pending.</summary>
		private static bool BuildFromSave(CharacterSelectionController __instance)
		{
			assigned.Clear();
			if (!(__instance is ImmigrantScreen screen) || screen.IsStarterMinion)
			{
				return true;
			}
			List<ITelepadDeliverableContainer> existing = Containers(screen);
			if (existing != null && existing.Count > 0)
			{
				return true;   // the screen was closed without choosing in this session: the game reuses its candidates
			}
			Data data = Parse(saved);
			if (data == null || data.offers.Count == 0 || Immigration.Instance == null || !Immigration.Instance.ImmigrantsAvailable)
			{
				return true;
			}
			try
			{
				Restore(screen, data);
				return false;
			}
			catch (Exception e)
			{
				assigned.Clear();
				Debug.LogWarning($"[OniFixes] ImmigrantOffers: the saved choice could not be shown, the pod rolls anew: {e}");
				return true;
			}
		}

		private static void Restore(ImmigrantScreen screen, Data data)
		{
			GameObject parent = ScreenField<GameObject>(screen, "containerParent");
			CharacterContainer minionPrefab = ScreenField<CharacterContainer>(screen, "containerPrefab");
			CarePackageContainer packagePrefab = ScreenField<CarePackageContainer>(screen, "carePackageContainerPrefab");
			List<ITelepadDeliverableContainer> made = new List<ITelepadDeliverableContainer>();
			List<(ITelepadDeliverableContainer container, int place)> places = new List<(ITelepadDeliverableContainer, int)>();
			int minions = 0, packages = 0;
			foreach (Offer offer in data.offers)
			{
				ITelepadDeliverableContainer container;
				if (offer.dup)
				{
					CharacterContainer minion = Util.KInstantiateUI<CharacterContainer>(minionPrefab.gameObject, parent);
					minion.SetController(screen);
					minion.SetReshufflingState(enable: true);
					container = minion;
					minions++;
				}
				else
				{
					CarePackageContainer package = Util.KInstantiateUI<CarePackageContainer>(packagePrefab.gameObject, parent);
					package.SetController(screen);
					container = package;
					packages++;
				}
				made.Add(container);
				places.Add((container, offer.place));
				assigned[container] = offer;
			}
			ScreenFieldSet(screen, "containers", made);
			ScreenFieldSet(screen, "selectedDeliverables", new List<ITelepadDeliverable>());
			ScreenFieldSet(screen, "numberOfDuplicantOptions", minions);
			ScreenFieldSet(screen, "numberOfCarePackageOptions", packages);
			screen.OnReplacedEvent = null;
			AccessTools.Method(typeof(CharacterSelectionController), "DisableProceedButton").Invoke(screen, null);
			// The saved places name the whole row; applying them from the left puts every container back where it was.
			foreach ((ITelepadDeliverableContainer container, int place) in places.OrderBy(p => p.place))
			{
				container.GetGameObject().transform.SetSiblingIndex(place);
			}
			Debug.Log($"[OniFixes] ImmigrantOffers: the pod offers the choice from the save: {minions} duplicants and {packages} care packages");
		}

		private static void MinionRolled(CharacterContainer __instance)
		{
			if (!assigned.TryGetValue(__instance, out Offer offer) || !offer.dup)
			{
				return;
			}
			assigned.Remove(__instance);
			MinionStartingStats stats = Decode(offer);
			if (stats == null)
			{
				Debug.LogWarning($"[OniFixes] ImmigrantOffers: the saved duplicant '{offer.personality}' is not in this game; a new one is rolled");
				return;
			}
			__instance.SetMinion(stats);
		}

		private static void CarePackageRolled(CarePackageContainer __instance)
		{
			if (!assigned.TryGetValue(__instance, out Offer offer) || offer.dup)
			{
				return;
			}
			assigned.Remove(__instance);
			CarePackageInfo info = FindCarePackage(offer.package);
			if (info == null)
			{
				Debug.LogWarning($"[OniFixes] ImmigrantOffers: the saved care package '{offer.package}' is not in this game; a new one is rolled");
				return;
			}
			AccessTools.Field(typeof(CarePackageContainer), "info").SetValue(__instance, info);
			__instance.carePackageInstanceData = new CarePackageContainer.CarePackageInstanceData { info = info, facadeID = offer.facade.Length == 0 ? null : offer.facade };
			foreach (string step in new[] { "ClearEntryIcons", "SetAnimator", "SetInfoText" })
			{
				AccessTools.Method(typeof(CarePackageContainer), step).Invoke(__instance, null);
			}
		}

		private static MinionStartingStats Decode(Offer offer)
		{
			Personality personality = Db.Get().Personalities.TryGet(offer.personality);
			if (personality == null || !Game.IsDlcActiveForCurrentSave(personality.requiredDlcId))
			{
				return null;
			}
			MinionStartingStats stats = new MinionStartingStats(personality);
			List<Trait> traits = new List<Trait>();
			foreach (string id in offer.traits)
			{
				Trait trait = Db.Get().traits.TryGet(id);
				if (trait == null)
				{
					return null;
				}
				traits.Add(trait);
			}
			Trait stress = Db.Get().traits.TryGet(offer.stress), joy = Db.Get().traits.TryGet(offer.joy);
			if (stress == null || joy == null)
			{
				return null;
			}
			Trait congenital = offer.congenital.Length == 0 ? null : Db.Get().traits.TryGet(offer.congenital);
			if (congenital == null && offer.congenital.Length > 0)
			{
				return null;
			}
			Dictionary<SkillGroup, float> aptitudes = new Dictionary<SkillGroup, float>();
			foreach (KeyValuePair<string, float> pair in offer.aptitudes)
			{
				SkillGroup group = Db.Get().SkillGroups.TryGet(pair.Key);
				if (group == null)
				{
					return null;
				}
				aptitudes[group] = pair.Value;
			}
			stats.Traits = traits;
			stats.stressTrait = stress;
			stats.joyTrait = joy;
			stats.congenitaltrait = congenital;
			stats.Name = offer.name;
			personality.Name = offer.name;   // a rename in the game lands in the personality too
			stats.voiceIdx = offer.voice;
			stats.rarityBalance = offer.rarity;
			stats.IsValid = offer.valid;
			stats.overrideOutfitID = offer.outfit.Length == 0 ? null : offer.outfit;
			stats.skillAptitudes = aptitudes;
			stats.StartingLevels = offer.levels.ToDictionary(pair => pair.Key, pair => pair.Value);
			return stats;
		}

		private static CarePackageInfo FindCarePackage(string id)
		{
			List<CarePackageInfo> packages = (List<CarePackageInfo>)AccessTools.Field(typeof(Immigration), "carePackages").GetValue(Immigration.Instance);
			return packages?.Find(package => package.id == id);
		}

		private static Data Parse(string json)
		{
			if (string.IsNullOrEmpty(json))
			{
				return null;
			}
			try
			{
				return JsonConvert.DeserializeObject<Data>(json);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniFixes] ImmigrantOffers: the saved choice is unreadable, the pod rolls anew: {e}");
				return null;
			}
		}

		// The choice screen's own fields live on its base class CharacterSelectionController; the mod reaches them by name,
		// the same way for a mod screen and a game screen.
		private static T ScreenField<T>(CharacterSelectionController screen, string field)
		{
			return (T)AccessTools.Field(typeof(CharacterSelectionController), field).GetValue(screen);
		}

		private static void ScreenFieldSet(CharacterSelectionController screen, string field, object value)
		{
			AccessTools.Field(typeof(CharacterSelectionController), field).SetValue(screen, value);
		}

		private static List<ITelepadDeliverableContainer> Containers(CharacterSelectionController screen)
		{
			return screen == null ? null : ScreenField<List<ITelepadDeliverableContainer>>(screen, "containers");
		}

		// ---- self-check -----------------------------------------------------------------------------------------------

		private static void VerifyFrame()
		{
			if (verify == null || Game.Instance == null || GameClock.Instance == null || SaveLoader.Instance == null || ++verifyFrame < OpenAtFrame)
			{
				return;
			}
			try
			{
				Telepad telepad = FindTelepad();
				if (telepad == null)
				{
					Debug.LogWarning("[OniFixes] ImmigrantOffers verify: FAILED: no Printing Pod in the active world");
					VerifyDone();
				return;
				}
				bool set = verify == "set";
				if (verifyStep == 0)
				{
					verifyStep = 1;
					// The pod is made ready at once, so the check does not wait for the next print.
					AccessTools.Field(typeof(Immigration), "bImmigrantAvailable").SetValue(Immigration.Instance, true);
					AccessTools.Field(typeof(Immigration), "timeBeforeSpawn").SetValue(Immigration.Instance, 0f);
					minionsBefore = Components.LiveMinionIdentities.Items.Count;
					ImmigrantScreen.InitializeImmigrantScreen(telepad);
					return;
				}
				if (verifyFrame < CheckAtFrame)
				{
					return;
				}
				if (set)
				{
					// First run: the open choice is saved and the game closes.
					string encoded = EncodeScreen();
					if (encoded == null)
					{
						Debug.LogWarning("[OniFixes] ImmigrantOffers verify: FAILED: nothing to save on the open screen");
					}
					else
					{
						ImmigrantScreen.instance.Show(show: false);
						string file = Path.Combine(SaveLoader.GetSavePrefix(), "OniPodCheck", "OniPodCheck.sav");
						SaveLoader.Instance.Save(file, isAutoSave: false, updateSavePointer: false);
						Debug.Log($"[OniFixes] ImmigrantOffers verify: choice saved to {file}: {encoded}");
					}
					VerifyDone();
				return;
				}
				// Second run: the save from the first run is loaded; the screen must show the same choice.
				if (verifyStep == 1)
				{
					verifyStep = 2;
					string live = EncodeScreen();
					same = live != null && saved != null && JToken.DeepEquals(JToken.Parse(live), JToken.Parse(saved));
					Debug.Log($"[OniFixes] ImmigrantOffers verify: restored choice {(same ? "matches" : "DIFFERS from")} the save:\n  save:  {saved}\n  screen: {live}");
					places = false;
					if (same)
					{
						places = true;
						int place = 0;
						foreach (ITelepadDeliverableContainer container in Containers(ImmigrantScreen.instance))
						{
							places &= container.GetGameObject().transform.GetSiblingIndex() == JToken.Parse(saved)["offers"][place++]["place"].Value<int>();
						}
					}
					// The first duplicant is delivered: the choice must be spent and the minion must arrive (it registers
					// a few frames after delivery, so the count is read at the next step).
					first = Containers(ImmigrantScreen.instance).OfType<CharacterContainer>().FirstOrDefault(minion => minion.Stats != null);
					if (first != null)
					{
						telepad.OnAcceptDelivery(first.Stats);
					}
					return;
				}
				if (verifyFrame < CheckAtFrame + 60)
				{
					return;
				}
				delivered = first != null && !Immigration.Instance.ImmigrantsAvailable && saved == null
					&& Components.LiveMinionIdentities.Items.Count == minionsBefore + 1;
				Debug.Log($"[OniFixes] ImmigrantOffers verify: delivery: available={Immigration.Instance.ImmigrantsAvailable}, choice cleared={saved == null}, minions {minionsBefore} -> {Components.LiveMinionIdentities.Items.Count}");
				Debug.Log($"[OniFixes] ImmigrantOffers verify: {(same && places && delivered ? "PASSED" : "FAILED")}: candidates {(same ? "same" : "differ")}, places {(places ? "same" : "differ")}, delivery and spent choice {(delivered ? "ok" : "failed")}");
				VerifyDone();
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniFixes] ImmigrantOffers verify failed: {e}");
				VerifyDone();
			}
		}

		private static Telepad FindTelepad()
		{
			int world = ClusterManager.Instance.activeWorldId;
			return Components.Telepads.Items.FirstOrDefault(pod => pod != null && Grid.WorldIdx[Grid.PosToCell(pod)] == world);
		}

		private static void VerifyDone()
		{
			verify = null;
			App.Quit();
		}
	}
}
