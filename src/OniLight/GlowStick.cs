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
		/// The light's colour: the red of the chemical light sticks dropped in caves (glowing near 620 nm). The rays are the
		/// game's own grey, as for every light in it.
		/// </summary>
		internal static Color Colour = new Color(1f, 0.2f, 0.12f);
		internal static readonly Color YellowGreen = new Color(0.62f, 1f, 0.28f);
		private static readonly Color Rays = new Color(0.56f, 0.56f, 0.56f, 1f);

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
			if (hope == null)
			{
				return 0f;
			}
			float t = hope.timeRemaining / FadeFor;
			return t >= 1f ? 1f : t <= 0f ? 0f : t * t * (3f - 2f * t);
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
			light.overlayColour = Rays;
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
			light.overlayColour = new Color(Rays.r * charge, Rays.g * charge, Rays.b * charge, charge);
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
