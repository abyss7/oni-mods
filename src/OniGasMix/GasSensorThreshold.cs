using KSerialization;
using UnityEngine;

namespace OniGasMix
{
	/// <summary>
	/// Gas sensor threshold: the share of molecules in the mixture at which the sensor considers its gas present. A slider in the
	/// sensor's window (the game shows it on its own for buildings with ISingleSliderControl); the value is stored in the save on the sensor.
	/// Until the player touches the slider, the common threshold from the mod settings applies. A save without the mod loads as
	/// usual: the game skips the data of an unknown component.
	/// </summary>
	[SerializationConfig(MemberSerialization.OptIn)]
	public sealed class GasSensorThreshold : KMonoBehaviour, ISingleSliderControl
	{
		/// <summary>Threshold in percent; a negative number means the common threshold from the mod settings.</summary>
		[Serialize]
		private float percent = -1f;

		internal float Share => (percent >= 0f ? percent : GasMixture.PresenceShare * 100f) / 100f;

		public string SliderTitleKey => "STRINGS.ONIGASMIX.SENSOR.THRESHOLD_TITLE";

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
			return "STRINGS.ONIGASMIX.SENSOR.THRESHOLD_TOOLTIP";
		}

		public string GetSliderTooltip(int index)
		{
			return string.Format(Strings.Get(GetSliderTooltipKey(index)), GetSliderValue(index));
		}
	}
}
