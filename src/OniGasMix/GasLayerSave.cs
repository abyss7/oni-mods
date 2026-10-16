using System;
using System.IO;
using System.Runtime.Serialization;
using HarmonyLib;
using KSerialization;
using OniCommon;

namespace OniGasMix
{
	/// <summary>
	/// State of the gas mixture layer in the game's save. The game stores one gas per cell (for the layer that is the cell's main gas),
	/// and only that gas is in its save. The other gases of the mixture live in the layer's memory; this component sits on the
	/// save object and takes them from the layer before saving, and hands them back after loading.
	///
	/// What happens in different cases:
	/// - the mod is on, the layer moves gas: the mixture is saved and restored as it was;
	/// - the save is opened without the mod or with the layer off: the game does not find this component on the save object and silently
	///   skips its data (as it does with any unknown component); the cells keep what
	///   the game saved itself, i.e. the main gases. The next save no longer has the layer's data;
	/// - an old save, the layer is on for the first time: there is no data, the layer starts from the main gases.
	///
	/// On load the cell's main gas is always taken from the game's cells; only the other gases are restored
	/// from the saved data: this way the game's save and the layer's state cannot diverge.
	/// </summary>
	[SerializationConfig(MemberSerialization.OptIn)]
	public sealed class GasLayerState : KMonoBehaviour
	{
		/// <summary>The format is described in sim/plugins/gas_mix.cpp (SavedHeader); gases are written as element hashes.</summary>
		[Serialize]
		private byte[] mixture;

		/// <summary>The gas selected in the mixtures overlay (element hash); 0: the normal view.</summary>
		[Serialize]
		private int overlayGas;

		[OnSerializing]
		private void BeforeSave()
		{
			mixture = GasLayerSave.Capture();
			overlayGas = GasLayerOverlay.SelectedGasHash;
		}

		public override void OnSpawn()
		{
			base.OnSpawn();
			GasLayerSave.Restore(mixture);
			// No point keeping a copy until the next save: right before it, a fresh one is taken from the layer.
			mixture = null;
			if (overlayGas != 0 && GasLayerOverlay.RestoreSelection(overlayGas))
			{
				Debug.Log($"[OniGasMix] GasLayer: overlay choice restored from the save: {(SimHashes)overlayGas}");
			}
		}
	}

	internal static class GasLayerSave
	{
		private const int HeaderBytes = 24;              // magic, version, width, height, number of gases, length
		private const int GasCountAt = 16, BytesAt = 20;
		private const int BlockHeaderBytes = 8, EntryBytes = 8;

		// Self-check: the file verify_gas_save.txt in the mod folder. When the layer has run VerifyAfterTicks ticks,
		// the layer's figures are written to the log, and the game saves to save_files/OniGasCheck/OniGasCheck.sav (a normal
		// game save; the "continue" pointer does not change). Loading this save in the next run, one can
		// compare the layer's figures after loading with those written before saving.
		private const int VerifyAfterTicks = 700;
		private static bool verify;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SaveGame), "OnPrefabInit"),
				postfix: new HarmonyMethod(typeof(GasLayerSave), nameof(AttachState)));
			verify = File.Exists(Path.Combine(ModContext.Folder, "verify_gas_save.txt"));
			if (verify)
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"),
					postfix: new HarmonyMethod(typeof(GasLayerSave), nameof(VerifyStep)));
			}
		}

		private static void AttachState(SaveGame __instance)
		{
			__instance.gameObject.AddOrGet<GasLayerState>();
		}

		/// <summary>Takes the cells' gases, except the main ones, from the layer. null if the layer does not move gas.</summary>
		internal static byte[] Capture()
		{
			try
			{
				if (!GasLayer.Moving)
				{
					return null;
				}
				// First with a buffer for just the header: the layer answers how much space is needed. Between the two requests
				// the layer may have run a tick and the state may have grown, so the space is taken with headroom.
				byte[] buffer = new byte[HeaderBytes];
				int result = SimHooks.Query("gas-mix.save", buffer);
				for (int attempt = 0; attempt < 3 && result == 3; attempt++)
				{
					int needed = BitConverter.ToInt32(buffer, BytesAt);
					buffer = new byte[needed + needed / 4 + 4096];
					result = SimHooks.Query("gas-mix.save", buffer);
				}
				if (result != 0)
				{
					if (result != 2)
					{
						Debug.LogWarning($"[OniGasMix] GasLayerSave: the layer did not give its state (code {result}); the save will hold main gases only");
					}
					return null;
				}
				Array.Resize(ref buffer, BitConverter.ToInt32(buffer, BytesAt));
				// In the save, gases are written as element hashes: element numbers may shift with the game version and mods.
				Summary summary = Translate(buffer, index => index >= 0 && index < ElementLoader.elements.Count ? (int)ElementLoader.elements[index].id : 0);
				Debug.Log($"[OniGasMix] GasLayerSave: saved {summary.gases} gases, {summary.entries} cell entries, {summary.mass:F1} kg besides the main gases, {buffer.Length} bytes");
				return buffer;
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniGasMix] GasLayerSave: saving the layer state failed, the save will hold main gases only: {e}");
				return null;
			}
		}

		/// <summary>
		/// The game is loaded or started: the layer starts over from the game's cells and restores the saved gases
		/// (saved == null: nothing was saved).
		/// </summary>
		internal static void Restore(byte[] saved)
		{
			try
			{
				byte[] state = new byte[0];
				if (saved != null && saved.Length >= HeaderBytes)
				{
					state = (byte[])saved.Clone();
					Summary summary = Translate(state, hash =>
					{
						Element element = ElementLoader.FindElementByHash((SimHashes)hash);
						return element != null ? element.idx : -1;
					});
					Debug.Log($"[OniGasMix] GasLayerSave: the save holds {summary.gases} gases, {summary.entries} cell entries, {summary.mass:F1} kg besides the main gases{(summary.unknown > 0 ? $"; {summary.unknown} gases are not in this game and are dropped" : "")}");
				}
				else if (GasLayer.Moving)
				{
					Debug.Log("[OniGasMix] GasLayerSave: the save holds no layer state; the layer starts from the main gases");
				}
				SimHooks.SendData("gas-mix.restore", state);
				GasLayer.GameLoaded();
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniGasMix] GasLayerSave: restoring the layer state failed, the layer starts from the main gases: {e}");
				SimHooks.SendData("gas-mix.restore", new byte[0]);
				GasLayer.GameLoaded();
			}
		}

		private struct Summary
		{
			public int gases, entries, unknown;
			public double mass;
		}

		/// <summary>Replaces gas designations in the block (element number to hash or back) and counts the contents.</summary>
		private static Summary Translate(byte[] state, Func<int, int> convert)
		{
			Summary summary = default;
			int gases = BitConverter.ToInt32(state, GasCountAt);
			int at = HeaderBytes;
			for (int g = 0; g < gases && at + BlockHeaderBytes <= state.Length; g++)
			{
				int count = BitConverter.ToInt32(state, at + 4);
				if (count < 0 || at + BlockHeaderBytes + (long)count * EntryBytes > state.Length)
				{
					break;
				}
				int converted = convert(BitConverter.ToInt32(state, at));
				Buffer.BlockCopy(BitConverter.GetBytes(converted), 0, state, at, 4);
				if (converted < 0)
				{
					summary.unknown++;
				}
				summary.gases++;
				summary.entries += count;
				at += BlockHeaderBytes;
				for (int i = 0; i < count; i++, at += EntryBytes)
				{
					summary.mass += BitConverter.ToSingle(state, at + 4);
				}
			}
			return summary;
		}

		private static void VerifyStep()
		{
			if (!verify || SaveLoader.Instance == null || SimHooks.GetValue("gas-mix.ticks") < VerifyAfterTicks)
			{
				return;
			}
			verify = false;
			try
			{
				GasLayer.LogStats();
				string file = Path.Combine(SaveLoader.GetSavePrefix(), "OniGasCheck", "OniGasCheck.sav");
				SaveLoader.Instance.Save(file, isAutoSave: false, updateSavePointer: false);
				Debug.Log($"[OniGasMix] GasLayerSave verify: saved the game to {file}, {new FileInfo(file).Length} bytes; the line above holds the layer figures at the moment of saving");
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[OniGasMix] GasLayerSave verify failed: {e}");
			}
		}
	}
}
