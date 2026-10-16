using KSerialization;
using UnityEngine;

namespace OniLiquidMix
{
	/// <summary>
	/// Liquid sensor threshold: from what share of the mixture's mass the sensor considers its liquid present. A slider in the
	/// sensor's window (the game shows it itself for buildings with ISingleSliderControl); the value is stored in the save on the sensor.
	/// Until the player touches the slider, the common threshold (10%) applies, as for the gas sensor in the gas mixture mod. A save without
	/// the mod loads as usual: the game skips the data of an unknown component.
	/// </summary>
	[SerializationConfig(MemberSerialization.OptIn)]
	public sealed class LiquidSensorThreshold : KMonoBehaviour, ISingleSliderControl
	{
		/// <summary>Threshold in percent; a negative number: the common threshold.</summary>
		[Serialize]
		private float percent = -1f;

		internal float Share => (percent >= 0f ? percent : LiquidMixture.PresenceShare * 100f) / 100f;

		public string SliderTitleKey => "STRINGS.ONILIQUIDMIX.SENSOR.THRESHOLD_TITLE";

		public string SliderUnits => STRINGS.UI.UNITSUFFIXES.PERCENT;

		public int SliderDecimalPlaces(int index)
		{
			return 0;
		}

		public float GetSliderMin(int index)
		{
			return 1f;
		}

		public float GetSliderMax(int index)
		{
			return 100f;
		}

		public float GetSliderValue(int index)
		{
			return Mathf.Round(Share * 100f);
		}

		public void SetSliderValue(float value, int index)
		{
			percent = Mathf.Clamp(Mathf.Round(value), 1f, 100f);
		}

		public string GetSliderTooltipKey(int index)
		{
			return "STRINGS.ONILIQUIDMIX.SENSOR.THRESHOLD_TOOLTIP";
		}

		public string GetSliderTooltip(int index)
		{
			return string.Format(Strings.Get(GetSliderTooltipKey(index)), GetSliderValue(index));
		}
	}
}
