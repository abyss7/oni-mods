namespace OniDup
{
	/// <summary>
	/// The mod's own strings shown to the player. The English text is here; the mod loads translations from
	/// translations/&lt;language code&gt;.po in the mod folder, if present. Key of a string in a translation file:
	/// STRINGS.ONIDUP.&lt;class&gt;.&lt;field&gt;.
	/// </summary>
	public static class ONIDUP
	{
		public static class EFFECTS
		{
			public static class HYPOXIA
			{
				public static LocString NAME = "Lightheaded";
				public static LocString TOOLTIP = "This Duplicant isn't getting enough <link=\"OXYGEN\">Oxygen</link> from the air here\n\nThe air needs more pressure or a richer share of <link=\"OXYGEN\">Oxygen</link>";
			}

			public static class SEVEREHYPOXIA
			{
				public static LocString NAME = "Gasping for Air";
				public static LocString TOOLTIP = "This Duplicant can barely draw any <link=\"OXYGEN\">Oxygen</link> from this thin air and will soon pass out";
			}

			public static class EBULLISM
			{
				public static LocString NAME = "Vacuum Exposure";
				public static LocString TOOLTIP = "The pressure here is so low that the moisture in this Duplicant's body is boiling away\n\nAn <link=\"OXYGENMASK\">Oxygen Mask</link> won't help here, only a sealed suit like the <link=\"ATMOSUIT\">Atmo Suit</link> will";
			}

			public static class STUFFYAIR
			{
				public static LocString NAME = "Stuffy Air";
				public static LocString TOOLTIP = "The air here has a little too much <link=\"CARBONDIOXIDE\">Carbon Dioxide</link> in it, making this Duplicant drowsy";
			}

			public static class CO2HEADACHE
			{
				public static LocString NAME = "CO₂ Headache";
				public static LocString TOOLTIP = "The <link=\"CARBONDIOXIDE\">Carbon Dioxide</link> in the air is giving this Duplicant a pounding headache";
			}

			public static class CO2POISONING
			{
				public static LocString NAME = "CO₂ Poisoning";
				public static LocString TOOLTIP = "This Duplicant is dizzy and confused from breathing too much <link=\"CARBONDIOXIDE\">Carbon Dioxide</link>\n\nAn <link=\"OXYGENMASK\">Oxygen Mask</link> protects from it";
			}

			public static class OXYGENTOXICITY
			{
				public static LocString NAME = "Oxygen Toxicity";
				public static LocString TOOLTIP = "This Duplicant has been breathing <link=\"OXYGEN\">Oxygen</link> at a very high pressure for too long and their lungs burn\n\nIt fades after half a cycle at a normal pressure";
			}

			public static class OXYGENSEIZURE
			{
				public static LocString NAME = "Oxygen Seizure";
				public static LocString TOOLTIP = "Breathing <link=\"OXYGEN\">Oxygen</link> at an extreme pressure gave this Duplicant a seizure";
			}

			public static class BAROTRAUMA
			{
				public static LocString NAME = "Decompression Injury";
				public static LocString TOOLTIP = "A sudden drop in pressure hurt this Duplicant's ears and lungs\n\nA sealed suit like the <link=\"ATMOSUIT\">Atmo Suit</link> prevents this";
			}
		}

		/// <summary>Names of the changes to the breath bar, shown in its tooltip.</summary>
		public static class BREATH
		{
			public static LocString THINAIR = "Thin air in the lungs";
			public static LocString GASPING = "Gasping for Air";
			public static LocString VACUUM = "Vacuum Exposure";
			public static LocString CARBONDIOXIDE = "CO₂ Poisoning";
		}

		public static class STATUSITEMS
		{
			public static class VACUUM
			{
				public static LocString NAME = "Exposed to Vacuum";
				public static LocString TOOLTIP = "This Duplicant is in a near-vacuum and will pass out within seconds\n\nOnly a sealed suit like the <link=\"ATMOSUIT\">Atmo Suit</link> protects against vacuum";
			}
		}

		/// <summary>"Oxygen Pressure" view of the oxygen overlay and its legend.</summary>
		public static class OVERLAY
		{
			public static LocString NAME = "Breathing Conditions";
			public static LocString TOOLTIP = "Shows how Duplicants would fare breathing the air here: the pressure of <link=\"OXYGEN\">Oxygen</link> reaching their lungs and the <link=\"CARBONDIOXIDE\">Carbon Dioxide</link> in it";
			public static LocString TOXIC = "Toxic Oxygen";
			public static LocString TOXIC_TOOLTIP = "<b>Toxic Oxygen</b>\nSo much <link=\"OXYGEN\">Oxygen</link> that Duplicants get <b>Oxygen Toxicity</b> and may have seizures";
			public static LocString RICH = "Oxygen-Rich";
			public static LocString RICH_TOOLTIP = "<b>Oxygen-Rich</b>\nDuplicants who stay here for half a cycle get <b>Oxygen Toxicity</b>";
			public static LocString NORMAL = "Easy Breathing";
			public static LocString NORMAL_TOOLTIP = "<b>Easy Breathing</b>\nEnough pressure of <link=\"OXYGEN\">Oxygen</link> to breathe freely";
			public static LocString THIN = "Thin or Stuffy Air";
			public static LocString THIN_TOOLTIP = "<b>Thin or Stuffy Air</b>\nDuplicants get <b>Low Oxygen</b>, or <b>Stuffy Air</b> from <link=\"CARBONDIOXIDE\">Carbon Dioxide</link>\n\nAn <link=\"OXYGENMASK\">Oxygen Mask</link> helps";
			public static LocString LIGHTHEADED = "Lightheaded or Headache";
			public static LocString LIGHTHEADED_TOOLTIP = "<b>Lightheaded or Headache</b>\nToo little <link=\"OXYGEN\">Oxygen</link> or too much <link=\"CARBONDIOXIDE\">Carbon Dioxide</link>: Duplicants slow down and grow stressed\n\nAn <link=\"OXYGENMASK\">Oxygen Mask</link> helps if the pressure isn't too low";
			public static LocString GASPING = "Gasping or Poisoned";
			public static LocString GASPING_TOOLTIP = "<b>Gasping or Poisoned</b>\nDuplicants can barely breathe here or are poisoned by <link=\"CARBONDIOXIDE\">Carbon Dioxide</link>\n\nAn <link=\"OXYGENMASK\">Oxygen Mask</link> helps if the pressure isn't too low";
			public static LocString UNBREATHABLE = "Unbreathable";
			public static LocString UNBREATHABLE_TOOLTIP = "<b>Unbreathable</b>\nNot enough <link=\"OXYGEN\">Oxygen</link> to breathe at all, or enough <link=\"CARBONDIOXIDE\">Carbon Dioxide</link> to knock a Duplicant out\n\nDuplicants pass out and suffocate if trapped here";
			public static LocString VACUUM = "Vacuum";
			public static LocString VACUUM_TOOLTIP = "<b>Vacuum</b>\nThe pressure is so low that Duplicants pass out within seconds\n\nOnly a sealed suit like the <link=\"ATMOSUIT\">Atmo Suit</link> protects here";
		}

		public static class DEATHS
		{
			public static class CARBONDIOXIDE
			{
				public static LocString NAME = "Carbon Dioxide Poisoning";
				public static LocString DESCRIPTION = "{Target} breathed too much Carbon Dioxide.";
			}

			public static class DECOMPRESSION
			{
				public static LocString NAME = "Vacuum Exposure";
				public static LocString DESCRIPTION = "{Target} was exposed to the vacuum for too long.";
			}
		}
	}
}
