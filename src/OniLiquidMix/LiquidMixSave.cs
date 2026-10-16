using System;
using System.IO;
using System.Runtime.Serialization;
using HarmonyLib;
using KSerialization;
using OniCommon;

namespace OniLiquidMix
{
	/// <summary>
	/// Compositions of cells holding a liquid mixture in the game save. The game stores one liquid per cell (for the mod this is the cell's main
	/// liquid) and the total mass; the shares of the others live in the plugin's memory. This component sits on the save object:
	/// before saving it takes the compositions from the plugin, after loading it hands them back.
	///
	/// A save opened without the mod or with mixtures switched off: the game skips the data of an unknown component, the cells
	/// keep their main liquids with the whole mass of the mixture. A composition is restored only into a cell whose substance from the game
	/// save is present in the saved composition, so the game save and the compositions cannot diverge.
	/// </summary>
	[SerializationConfig(MemberSerialization.OptIn)]
	public sealed class LiquidMixState : KMonoBehaviour
	{
		/// <summary>The format is described in sim/plugins/liquid_mix.cpp (SavedMixHeader, SavedMix); liquids are written as element hashes.</summary>
		[Serialize]
		private byte[] mixture;

		[OnSerializing]
		private void BeforeSave()
		{
			mixture = LiquidMixSave.Capture();
		}

		public override void OnSpawn()
		{
			base.OnSpawn();
			LiquidMixSave.Restore(mixture);
			mixture = null;
		}
	}

	internal static class LiquidMixSave
	{
		private const int HeaderBytes = 24, VersionAt = 4, CountAt = 16, BytesAt = 20;

		// Self-check: the file verify_liquid_save.txt in the mod folder. After VerifyAfterTicks ticks the mixture totals are written
		// to the log, and the game is saved to save_files/OniLiquidCheck/OniLiquidCheck.sav (the "continue" pointer is not
		// changed). Loading this save in the next run lets you compare the compositions after loading with the saved ones.
		private const int VerifyAfterTicks = 700;
		private static bool verify;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SaveGame), "OnPrefabInit"), postfix: new HarmonyMethod(typeof(LiquidMixSave), nameof(AttachState)));
			verify = File.Exists(Path.Combine(ModContext.Folder, "verify_liquid_save.txt"));
			if (verify)
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(LiquidMixSave), nameof(VerifyStep)));
			}
		}

		private static void VerifyStep()
		{
			if (!verify || SaveLoader.Instance == null || SimHooks.GetValue("liquid-mix.ticks") < VerifyAfterTicks)
			{
				return;
			}
			verify = false;
			try
			{
				LiquidSurvey.LogStats();
				string file = Path.Combine(SaveLoader.GetSavePrefix(), "OniLiquidCheck", "OniLiquidCheck.sav");
				SaveLoader.Instance.Save(file, isAutoSave: false, updateSavePointer: false);
				Debug.Log($"{ModContext.Tag} liquid save verify: saved the game to {file}, {new FileInfo(file).Length} bytes; the lines above hold the mixtures at the moment of saving");
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} liquid save verify failed: {e}");
			}
		}

		private static void AttachState(SaveGame __instance)
		{
			__instance.gameObject.AddOrGet<LiquidMixState>();
		}

		/// <summary>Cell compositions from the plugin; null if mixtures are switched off.</summary>
		internal static byte[] Capture()
		{
			try
			{
				if (!LiquidSurvey.Mixing)
				{
					return null;
				}
				byte[] buffer = new byte[HeaderBytes];
				int result = SimHooks.Query("liquid-mix.save", buffer);
				for (int attempt = 0; attempt < 3 && result == 3; attempt++)
				{
					int needed = BitConverter.ToInt32(buffer, BytesAt);
					buffer = new byte[needed + needed / 4 + 4096];
					result = SimHooks.Query("liquid-mix.save", buffer);
				}
				if (result != 0)
				{
					Debug.LogWarning($"{ModContext.Tag} liquid mixtures were not saved (code {result}); the save holds the main liquids only");
					return null;
				}
				Array.Resize(ref buffer, BitConverter.ToInt32(buffer, BytesAt));
				// In the save, liquids are written as hashes: element indices may shift with the game version and mods.
				int count = Translate(buffer, index => index >= 0 && index < ElementLoader.elements.Count ? (int)ElementLoader.elements[index].id : 0, out _);
				Debug.Log($"{ModContext.Tag} liquid mixtures saved: {count} cells, {buffer.Length} bytes");
				return buffer;
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} saving liquid mixtures failed, the save holds the main liquids only: {e}");
				return null;
			}
		}

		/// <summary>The game is loaded: the compositions are handed to the plugin (saved == null: none were saved).</summary>
		internal static void Restore(byte[] saved)
		{
			if (!LiquidSurvey.Mixing)
			{
				return;
			}
			try
			{
				byte[] state = new byte[0];
				if (saved != null && saved.Length >= HeaderBytes)
				{
					state = (byte[])saved.Clone();
					int count = Translate(state, hash => ElementLoader.FindElementByHash((SimHashes)hash) is Element element ? element.idx : -1, out int unknown);
					Debug.Log($"{ModContext.Tag} liquid mixtures in the save: {count} cells{(unknown > 0 ? $", {unknown} liquids are not in this game and those cells stay pure" : "")}");
				}
				SimHooks.SendData("liquid-mix.restore", state);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"{ModContext.Tag} restoring liquid mixtures failed, the cells hold their main liquids: {e}");
				SimHooks.SendData("liquid-mix.restore", new byte[0]);
			}
		}

		/// <summary>Replaces the liquid identifiers in the records (element index to hash or back); returns the number of records.</summary>
		private static int Translate(byte[] state, Func<int, int> convert, out int unknown)
		{
			unknown = 0;
			int count = BitConverter.ToInt32(state, CountAt);
			// Version 1 had four slots per cell, version 2 has LiquidMixture.Slots; the plugin reads both.
			int slots = BitConverter.ToInt32(state, VersionAt) == 1 ? 4 : LiquidMixture.Slots;
			int entryBytes = 8 + 8 * slots;
			int at = HeaderBytes;
			for (int i = 0; i < count && at + entryBytes <= state.Length; i++, at += entryBytes)
			{
				int liquids = BitConverter.ToInt32(state, at + 4);
				for (int k = 0; k < liquids && k < slots; k++)
				{
					int converted = convert(BitConverter.ToInt32(state, at + 8 + 4 * k));
					unknown += converted < 0 ? 1 : 0;
					Buffer.BlockCopy(BitConverter.GetBytes(converted), 0, state, at + 8 + 4 * k, 4);
				}
			}
			return count;
		}
	}
}
