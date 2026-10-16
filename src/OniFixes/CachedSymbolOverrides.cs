using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using OniCommon;

namespace OniFixes
{
	/// <summary>
	/// For duplicants the game re-lays out all symbol overrides (clothing, hair, body: dozens of entries)
	/// into GPU data on every animation frame, even though the override set itself
	/// rarely changes. Only the face really changes frame to frame: blinking, speech, head turning.
	///
	/// Here the full layout runs only when the override set, the animation batch
	/// or its texture set has changed. In other frames only the face step runs, as in the original.
	/// </summary>
	internal static class CachedSymbolOverrides
	{
		private sealed class State
		{
			public bool valid;
			public int version;
			public KAnimBatch batch;
			public int atlasHash;
			public SymbolOverrideInfoGpuData data;
		}

		private sealed class ReferenceComparer : IEqualityComparer<SymbolOverrideController>
		{
			public bool Equals(SymbolOverrideController a, SymbolOverrideController b)
			{
				return ReferenceEquals(a, b);
			}

			public int GetHashCode(SymbolOverrideController controller)
			{
				return RuntimeHelpers.GetHashCode(controller);
			}
		}

		private static readonly Dictionary<SymbolOverrideController, State> states = new Dictionary<SymbolOverrideController, State>(new ReferenceComparer());
		private static long calls, skipped;

		// Self-check: file verify_symbol_overrides.txt in the mod folder. In frames where the layout would be
		// skipped, it runs anyway, and the result is compared byte by byte with the result of skipping.
		private static bool verify;
		private static byte[] snapshot = new byte[0];
		private static long verifyChecks, verifyMismatches, verifyFaceChanged;

		internal static void Apply(Harmony harmony)
		{
			verify = File.Exists(Path.Combine(ModContext.Folder, "verify_symbol_overrides.txt"));
			harmony.Patch(AccessTools.Method(typeof(SymbolOverrideController), nameof(SymbolOverrideController.ApplyOverrides)),
				prefix: new HarmonyMethod(typeof(CachedSymbolOverrides), nameof(Prefix)),
				postfix: new HarmonyMethod(typeof(CachedSymbolOverrides), nameof(Postfix)));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.OnApplicationQuit)),
				prefix: new HarmonyMethod(typeof(CachedSymbolOverrides), nameof(LogStats)));
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.StopBE)),
				prefix: new HarmonyMethod(typeof(CachedSymbolOverrides), nameof(Forget)));
		}

		/// <param name="__state">true if this call of the original runs only for the comparison.</param>
		private static bool Prefix(SymbolOverrideController __instance, out bool __state)
		{
			__state = false;
			calls++;
			if (!AbTest.IsOn(nameof(CachedSymbolOverrides)))
			{
				return true;
			}
			// requiresSorting is set on any change of the override set and cleared by the layout itself.
			if (__instance.requiresSorting || !states.TryGetValue(__instance, out State state) || !state.valid)
			{
				return true;
			}
			KBatchedAnimController controller = __instance.animController;
			KAnimBatch batch = controller.GetBatch();
			if (batch == null || state.version != __instance.version || !ReferenceEquals(state.batch, batch)
				|| !ReferenceEquals(state.data, controller.symbolOverrideInfoGpuData) || state.atlasHash != AtlasHash(batch))
			{
				return true;
			}
			skipped++;
			if (__instance.faceGraph != null)
			{
				__instance.faceGraph.ApplyShape();
			}
			if (!verify)
			{
				return false;
			}
			if (__instance.requiresSorting)
			{
				// The face step itself changed the override set (the first frame of speech adds a mouth override). The original, run
				// now for the comparison, would already see the new set and diverge from what it would itself have done in this frame.
				verifyFaceChanged++;
				return false;
			}
			byte[] bytes = controller.symbolOverrideInfoGpuData.symbolOverrideInfoConverter.bytes;
			if (snapshot.Length < bytes.Length)
			{
				snapshot = new byte[bytes.Length];
			}
			System.Buffer.BlockCopy(bytes, 0, snapshot, 0, bytes.Length);
			__state = true;
			return true;
		}

		private static void Postfix(SymbolOverrideController __instance, bool __state, bool __runOriginal)
		{
			if (!__runOriginal)
			{
				return;
			}
			KBatchedAnimController controller = __instance.animController;
			KAnimBatch batch = controller != null ? controller.GetBatch() : null;
			if (batch == null)
			{
				return;
			}
			if (__state)
			{
				verifyChecks++;
				byte[] bytes = controller.symbolOverrideInfoGpuData.symbolOverrideInfoConverter.bytes;
				for (int i = 0; i < bytes.Length; i++)
				{
					if (bytes[i] != snapshot[i])
					{
						verifyMismatches++;
						break;
					}
				}
			}
			if (!states.TryGetValue(__instance, out State state))
			{
				states[__instance] = state = new State();
			}
			state.valid = true;
			state.version = __instance.version;
			state.batch = batch;
			state.atlasHash = AtlasHash(batch);
			state.data = controller.symbolOverrideInfoGpuData;
		}

		/// <summary>Texture indices in the override data depend on which textures, and in which order, the batch has already taken.</summary>
		private static int AtlasHash(KAnimBatch batch)
		{
			List<Texture2D> atlases = batch.atlases.atlases;
			int hash = atlases.Count;
			for (int i = 0; i < atlases.Count; i++)
			{
				hash = hash * 31 + RuntimeHelpers.GetHashCode(atlases[i]);
			}
			return hash;
		}

		private static void Forget()
		{
			states.Clear();
		}

		private static void LogStats()
		{
			if (calls == 0)
			{
				return;
			}
			string text = $"[OniFixes] CachedSymbolOverrides: full layout skipped {skipped}/{calls} ({100 * skipped / calls}%)";
			if (verify)
			{
				text += $"; verify: {verifyMismatches} mismatches in {verifyChecks} checks, {verifyFaceChanged} not comparable (face step changed overrides)";
			}
			Debug.Log(text);
		}
	}
}
