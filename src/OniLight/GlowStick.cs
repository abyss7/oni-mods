using System;
using System.Collections.Generic;
using HarmonyLib;
using Klei.AI;
using KSerialization;
using OniCommon;
using UnityEngine;

namespace OniLight
{
	/// <summary>
	/// The Blackout mode's light at the start: the colony's first Duplicants come with a glow stick at their back, lit
	/// while their "New Hope" lasts (the game gives it to the starting Duplicants only, for three cycles). It gives 600 lux
	/// to 4 cells, fades out over the last half cycle of New Hope and is gone for good when it ends. The stick itself is
	/// not drawn (as if hidden by the Duplicant's body): what shows is the game's own picture of a light, its coloured glow
	/// and rays, dimming with the stick.
	/// </summary>
	[SerializationConfig(MemberSerialization.OptIn)]
	internal sealed class GlowStick : KMonoBehaviour
	{
		internal const float Lux = 600f, Range = 4f, FadeFor = 0.5f * 600f;
		internal const string NewHopeId = "AnewHope";

		/// <summary>
		/// The light's colour: the green of a chemical light stick. The game's lights have grey rays and only a faint
		/// coloured glow, so the stick's rays take its colour: else the colour hardly shows.
		/// </summary>
		internal static Color Colour = new Color(0.24f, 1f, 0.47f);

		/// <summary>The stick has gone out (for good).</summary>
		[Serialize] public bool gone;

		/// <summary>For the self-check pictures: a colour of its own.</summary>
		internal Color? colour;

		private Light2D light;
		private float shown = -1f;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(MinionConfig), nameof(MinionConfig.CreatePrefab)), postfix: new HarmonyMethod(typeof(GlowStick), nameof(AddToPrefab)));
			harmony.Patch(AccessTools.Method(typeof(BionicMinionConfig), nameof(BionicMinionConfig.CreatePrefab)), postfix: new HarmonyMethod(typeof(GlowStick), nameof(AddToPrefab)));
		}

		private static void AddToPrefab(GameObject __result)
		{
			__result.AddOrGet<GlowStick>();
		}

		private Effects effects;

		/// <summary>How much of its light the stick gives, 0..1: full while New Hope has over half a cycle left, then less.</summary>
		internal float Charge()
		{
			if (gone || effects == null)
			{
				return 0f;
			}
			EffectInstance hope = effects.Get(NewHopeId);
			return hope == null ? 0f : ChargeFor(hope.timeRemaining);
		}

		/// <summary>The light by the time New Hope has left, 0..1: full until the last FadeFor seconds, then smoothly down.</summary>
		internal static float ChargeFor(float left)
		{
			float t = left / FadeFor;
			return t >= 1f ? 1f : t <= 0f ? 0f : t * t * (3f - 2f * t);
		}

		/// <summary>The most New Hope any living Duplicant has left, s (0: none has it).</summary>
		internal static float HopeLeft()
		{
			float most = 0f;
			foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
			{
				EffectInstance hope = minion != null ? minion.GetComponent<Effects>()?.Get(NewHopeId) : null;
				if (hope != null) most = Mathf.Max(most, hope.timeRemaining);
			}
			return most;
		}

		public override void OnSpawn()
		{
			base.OnSpawn();
			effects = GetComponent<Effects>();
		}

		/// <summary>Gives New Hope with this much of it left (the self-check's Duplicants).</summary>
		internal void Hope(float left)
		{
			Effect effect = Db.Get().effects.Get(NewHopeId);
			EffectInstance instance = effects != null && effect != null ? effects.Add(effect, should_save: true) : null;
			if (instance != null) instance.timeRemaining = left;
		}

		private void Light()
		{
			light = gameObject.AddComponent<Light2D>();
			light.Lux = (int)Lux;
			light.Range = Range;
			light.shape = LightShape.Circle;
			light.Offset = new Vector2(0f, 1f); // the chest's cell
			light.Color = colour ?? Colour;
			light.overlayColour = light.Color;
			light.drawOverlay = true; // the game's rays of a light
			light.autoRespondToOperational = false;
		}

		private void Update()
		{
			if (gone)
			{
				return;
			}
			float charge = Charge();
			if (light == null)
			{
				// New Hope comes a few seconds into a new colony.
				if (charge <= 0f) return;
				Light();
			}
			if (charge <= 0f)
			{
				// Gone for good.
				Destroy(light);
				light = null;
				gone = true;
				return;
			}
			// The game's picture of the light dims with it: its coloured glow and its rays.
			light.IntensityAnimation = charge;
			Color rays = colour ?? Colour;
			light.overlayColour = new Color(rays.r * charge, rays.g * charge, rays.b * charge, charge);
			// The light follows the charge in steps of 1% (each step a cheap rescale of its light, not a new trace).
			if (Mathf.Abs(charge - shown) >= 0.01f)
			{
				shown = charge;
				light.Lux = Mathf.Max(1, Mathf.RoundToInt(Lux * charge));
				if (light.isSpawned && light.isActiveAndEnabled) light.FullRefresh();
			}
		}
	}
}
