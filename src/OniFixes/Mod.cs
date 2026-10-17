using System;
using System.Collections.Generic;
using HarmonyLib;
using KMod;
using OniCommon;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;

namespace OniFixes
{
	public sealed class Mod : UserMod2
	{
		internal static Options Options;

		public override void OnLoad(Harmony harmony)
		{
			ModContext.Folder = path;
			ModContext.Name = nameof(OniFixes);
			PUtil.InitLibrary(false);
			new POptions().RegisterOptions(this, typeof(Options));
			Options = POptions.ReadSettings<Options>() ?? new Options();
			ModLocalization.Apply(harmony, typeof(ONIFIXES));
			AbTest.Init(harmony);

			// Each fix is applied separately and enabled by its own setting.
			(string name, bool enabled, Action<Harmony> apply)[] fixes =
			{
				(nameof(ThrottlePropertyTextures), Options.ThrottlePropertyTextures, ThrottlePropertyTextures.Apply),
				(nameof(NativePropertyTextures), Options.NativePropertyTextures, NativePropertyTextures.Apply),
				(nameof(AsyncPropertyTextures), Options.AsyncPropertyTextures, AsyncPropertyTextures.Apply),
				(nameof(DemandDrivenFetch), Options.DemandDrivenFetch, DemandDrivenFetch.Apply),
				(nameof(CachedSymbolOverrides), Options.CachedSymbolOverrides, CachedSymbolOverrides.Apply),
				(nameof(UiSettings), Options.RememberToolSettings, UiSettings.Apply),
				(nameof(RedrawOnAnimStart), Options.RedrawOnAnimStart, RedrawOnAnimStart.Apply),
				(nameof(ConveyorDepthGuard), Options.ConveyorDepthGuard, ConveyorDepthGuard.Apply),
				(nameof(ConveyorOverlayItems), Options.ConveyorOverlayItems, ConveyorOverlayItems.Apply),
				(nameof(ConveyorArrivals), Options.ConveyorArrivals, ConveyorArrivals.Apply),
				(nameof(QuickPills), Options.QuickPills, QuickPills.Apply),
			};
			List<string> applied = new List<string>();
			foreach ((string name, bool enabled, Action<Harmony> apply) in fixes)
			{
				if (!enabled)
				{
					continue;
				}
				try
				{
					apply(harmony);
					applied.Add(name);
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[OniFixes] {name} failed to apply: {e}");
				}
			}
			Debug.Log($"[OniFixes] {assembly.GetName().Version}, applied: {string.Join(", ", applied)}; settings: {POptions.GetConfigFilePath(typeof(Options))}");
		}
	}
}
