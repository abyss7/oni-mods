using System;
using System.IO;
using HarmonyLib;
using OniCommon;
using UnityEngine;

namespace OniFixes
{
	/// <summary>
	/// MultipleRenderTargetProxy.CreateRenderTarget replaces the screen-size copy render textures (MRT0_Copy,
	/// MRT1_Copy, MRT2_Copy - blit targets of MRTLiquidInterception) without freeing the old ones; the main
	/// textures are freed properly in RecreateRT. The render targets are recreated on every switch of an overlay
	/// mode that toggles the coloured buffer (disease, the temperature modes and others), on every screen resize
	/// and whenever the main texture turns out not created. At 4K one recreation leaks 95-127 MB of render
	/// textures, gigabytes over a session - a long game dies of an OOM (seen under Proton on a 16 GB machine:
	/// the leaked textures leave the video memory into the system one, and the kernel kills the game).
	/// Here the old copies are freed before the game creates the new ones.
	/// Self-check: the file verify_mrt_copies.txt in the mod folder: a few seconds after loading, the coloured
	/// buffer is toggled 5 times and the MRT copies are counted.
	/// </summary>
	internal static class MrtCopies
	{
		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(MultipleRenderTargetProxy), "CreateRenderTarget"),
				prefix: new HarmonyMethod(typeof(MrtCopies), nameof(FreeOldCopies)));
			if (File.Exists(Path.Combine(ModContext.Folder, "verify_mrt_copies.txt")))
			{
				harmony.Patch(AccessTools.Method(typeof(Game), "LateUpdate"), postfix: new HarmonyMethod(typeof(MrtCopies), nameof(Verify)));
			}
		}

		private static void FreeOldCopies(MultipleRenderTargetProxy __instance)
		{
			RenderTexture[] copies = __instance.TexturesCopies;
			for (int i = 0; i < copies.Length; i++)
			{
				if (copies[i] != null)
				{
					// The destruction is deferred to the end of the frame: the old command buffer of
					// MRTLiquidInterception still holds these textures and is rebuilt at the end of CreateRenderTarget.
					UnityEngine.Object.Destroy(copies[i]);
					copies[i] = null;
				}
			}
		}

		private static int verifyStep;
		private static float verifyStart = -1f;
		private static bool verifyColoured;

		private static int CountCopies()
		{
			int count = 0;
			foreach (RenderTexture texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
			{
				if (texture.name.EndsWith("_Copy"))
				{
					count++;
				}
			}
			return count;
		}

		private static void Verify()
		{
			if (verifyStep > 2 || GameClock.Instance == null || CameraController.Instance == null)
			{
				return;
			}
			try
			{
				float now = GameClock.Instance.GetTime();
				if (verifyStart < 0f)
				{
					verifyStart = now;
					return;
				}
				if (verifyStep == 0 && now > verifyStart + 6f)
				{
					verifyColoured = CameraController.Instance.GetComponentInChildren<MultipleRenderTargetProxy>(true)?.IsColouredOverlayBufferEnabled == true;
					for (int toggle = 0; toggle < 5; toggle++)
					{
						// The value does not matter: CreateRenderTarget runs the same way for on and off.
						CameraController.Instance.ToggleColouredOverlayView(true);
					}
					CameraController.Instance.ToggleColouredOverlayView(verifyColoured);
					verifyStep = 1;
				}
				else if (verifyStep == 1)
				{
					// A frame later: the deferred destructions of the old copies have run.
					int after = CountCopies();
					bool ok = after <= 8; // the copies of the active proxies only, not 5 toggles worth of them
					Debug.Log($"{ModContext.Tag} MrtCopies verify: {(ok ? "ok" : "FAILED")} ({after} MRT copy render textures after 5 toggles of the coloured buffer)");
					verifyStep = 2;
				}
			}
			catch (Exception e)
			{
				verifyStep = 2;
				Debug.LogWarning($"{ModContext.Tag} MrtCopies verify: FAILED: {e}");
			}
		}
	}
}
