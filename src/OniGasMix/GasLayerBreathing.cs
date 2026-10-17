using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Klei.AI;
using TUNING;
using UnityEngine;
using OniCommon;

namespace OniGasMix
{
	/// <summary>
	/// Breathing by the real composition of the cell and by what was actually inhaled.
	///
	/// In the game a duplicant asks the simulation every 0.2 s for its quota (100 g/s) from one cell near the head:
	/// the one where the main gas is breathable and there is more than 50 g of it. The simulation gives as much as there is, but the breath bar
	/// is binary: if a cell was found, the bar grows at full speed, no matter how much gas was obtained.
	///
	/// Here (the author's decision of 2026-10-11):
	/// - all Oxygen and Polluted Oxygen of the cell counts as breathable, by the composition of the mixture layer, not only the main
	///   gas: in a cell where Carbon Dioxide has become the main gas, one can breathe its Oxygen;
	/// - a duplicant inhales exactly as much as there is: first from the cell with the most breathable gas,
	///   the shortfall is taken from the other cells around the head; there is no 50 g threshold;
	/// - the breath bar rate depends on the share inhaled: the full quota gives full growth, half the quota holds
	///   the bar in place, nothing gives full decline. "There is air" for the rest of the game's logic (holding
	///   breath, suffocation) means "inhaled at least half the quota";
	/// - Carbon Dioxide is exhaled according to what was inhaled, not according to the quota.
	///
	/// Behaviour. All the game's checks "can one breathe here" and "is there too little oxygen here" (where to run to catch breath,
	/// safe cells, the colony's breathing indicator) converge on one function "how much breathable gas
	/// is in the cell"; the breathable gas that is not the main one in the cell is added to its answer. The 50 g threshold
	/// stays in these checks: there is no point running to a pocket of five grams.
	///
	/// Where the data comes from. Together with each data frame for the game, the layer takes two per-cell arrays
	/// ("gas-mix.frame"): the non-main breathable gas and the mass of all gases of the cell except the main one. The second is needed
	/// because the cell mass the game sees (Grid.Mass) is now the mass of the whole mixture: this way buildings and sensors
	/// see the real pressure. The main gas's mass is obtained by subtraction (<see cref="MainMass"/>).
	/// The duplicant takes the main gas from the simulation, as before; the non-main gas is taken by a request to the layer
	/// ("gas-mix.consume"), which is carried out on the next pass; so that two duplicants do not inhale the same gas,
	/// what was inhaled is subtracted at once from the mod's copy of the array. Suits, transit tubes and bionics are not affected: they have their own air sources.
	/// Non-main gas is recorded in reports as Oxygen, even if it is Polluted Oxygen, and carries no germs.
	/// </summary>
	internal static class GasLayerBreathing
	{
		private sealed class Breath
		{
			public float need, got;
			public bool enough;   // inhaled at least half the quota
			public bool fresh;    // breathed the world's air in this tick (not from a suit)
			public bool adjusted; // breath bar rates were set by this code
			public SuffocationMonitor.Instance monitor;
		}

		private const int Header = 4;             // the request header takes four floats: address of the game's mass array and the number of cells
		private static float[] frame;             // header; non-main breathable gas per cell; all non-main gases per cell
		private static int cells;                 // length of each of the two arrays
		private static bool[] breathableElement; // by element number: this gas is breathable
		private static volatile bool active;     // the array was received from the layer in this game
		private static readonly Dictionary<int, float> takenFromMain = new Dictionary<int, float>(); // for this tick, per cell
		private static readonly Dictionary<OxygenBreather, Breath> breaths = new Dictionary<OxygenBreather, Breath>();
		private static readonly byte[] request = new byte[12]; // cell, mass, germs
		private static readonly bool[] used = new bool[16];
		private static Element oxygen;
		private static Element labelElement;
		private static float labelMass = -1f;
		private static string label = "";
		// Figures for the log.
		private static long breathCount, shortBreaths;
		private static double needTotal, gotMain, gotOther;
		private static long inhaledGerms; // germs inhaled together with non-main gas
		private static long frameCalls, frameTicks; // how many times and how long the main thread took the layer's arrays

		internal static bool Active => active;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(GasBreatherFromWorldProvider), "GetBreathableCellMass"),
				postfix: new HarmonyMethod(typeof(GasLayerBreathing), nameof(AddOtherBreathable)));
			harmony.Patch(AccessTools.Method(typeof(GasBreatherFromWorldProvider), nameof(GasBreatherFromWorldProvider.ConsumeGas)),
				prefix: new HarmonyMethod(typeof(GasLayerBreathing), nameof(Breathe)));
			harmony.Patch(AccessTools.Method(typeof(OxygenBreather), nameof(OxygenBreather.Sim200ms)),
				postfix: new HarmonyMethod(typeof(GasLayerBreathing), nameof(AfterBreath)));
			harmony.Patch(AccessTools.Method(typeof(HoverTextHelper), nameof(HoverTextHelper.MassStringsReadOnly)),
				postfix: new HarmonyMethod(typeof(GasLayerBreathing), nameof(BreathableLabel)));
			harmony.Patch(AccessTools.Method(typeof(CellSelectionObject), nameof(CellSelectionObject.UpdateValues)),
				transpiler: new HarmonyMethod(typeof(GasLayerBreathing), nameof(PanelMassRead)));
		}

		/// <summary>The game is loaded or started: previous duplicants and the array are no longer valid.</summary>
		internal static void GameLoaded()
		{
			active = false;
			byMolecules = MolarMasses.ByMolecules;
			breaths.Clear();
			takenFromMain.Clear();
			breathableElement = null;
			// The layer returns arrays only for active regions; the rest of the buffer must stay zero.
			if (frame != null)
			{
				Array.Clear(frame, 0, frame.Length);
			}
		}

		/// <summary>Mass of all gases of the cell except the main one. The game shows the cell mass including it.</summary>
		internal static float OtherMass(int cell)
		{
			return active ? frame[Header + cells + cell] : 0f;
		}

		/// <summary>Mass of the cell's main gas: what lies in the simulation's own cell.</summary>
		internal static float MainMass(int cell)
		{
			return active ? Math.Max(0f, Grid.Mass[cell] - frame[Header + cells + cell]) : Grid.Mass[cell];
		}

		/// <summary>
		/// On every data frame from the simulation: the layer's arrays for this frame. gameMass: address of the cell mass array
		/// in this frame (the layer finds its snapshot by it).
		/// </summary>
		internal static unsafe void Tick(ulong gameMass)
		{
			try
			{
				if (breathableElement == null || breathableElement.Length != ElementLoader.elements.Count)
				{
					breathableElement = new bool[ElementLoader.elements.Count];
					for (int i = 0; i < breathableElement.Length; i++)
					{
						breathableElement[i] = IsBreathable(ElementLoader.elements[i]);
					}
					oxygen = ElementLoader.FindElementByHash(SimHashes.Oxygen);
				}
				if (frame == null || cells != Grid.CellCount)
				{
					active = false;
					cells = Grid.CellCount;
					frame = new float[Header + 2 * cells];
				}
				takenFromMain.Clear();
				// A data frame is identified by the address of the mass array the game holds: the layer has a snapshot for each.
				fixed (float* start = frame)
				{
					*(ulong*)start = gameMass;
					((int*)start)[2] = cells;
					((int*)start)[3] = 0;
				}
				long began = System.Diagnostics.Stopwatch.GetTimestamp();
				bool got = SimHooks.Query("gas-mix.frame", frame) == 0;
				frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
				frameCalls++;
				if (!got)
				{
					// The layer took nothing for this frame: so it did not touch the cell mass in it either.
					Array.Clear(frame, Header, frame.Length - Header);
				}
				active = got;
			}
			catch (Exception e)
			{
				active = false;
				Debug.LogWarning($"[OniGasMix] GasLayerBreathing: no data from the layer, Duplicants breathe as in the game: {e}");
			}
		}

		internal static bool IsBreathable(Element element)
		{
			return element.IsGas && element.HasTag(GameTags.Breathable);
		}

		/// <summary>How much breathable gas is in the cell: the main one (if breathable) and the non-main one.</summary>
		private static float Breathable(int cell)
		{
			return (breathableElement[Grid.ElementIdx[cell]] ? Math.Max(0f, Grid.Mass[cell] - frame[Header + cells + cell]) : 0f) + frame[Header + cell];
		}

		// ---------- Game checks "can one breathe in the cell" ----------

		private static void AddOtherBreathable(int cell, ref SimHashes elementID, ref float __result)
		{
			if (!active || !Grid.IsValidCell(cell))
			{
				return;
			}
			// The game returned the cell mass if the main gas is breathable; it now holds the whole mixture, including gases
			// that cannot be breathed.
			if (elementID != SimHashes.Vacuum)
			{
				__result = Math.Max(0f, __result - frame[Header + cells + cell]);
			}
			float other = frame[Header + cell];
			if (other > 0f)
			{
				__result += other;
				if (elementID == SimHashes.Vacuum)
				{
					elementID = SimHashes.Oxygen;
				}
			}
		}

		// ---------- Inhalation ----------

		private static bool Breathe(GasBreatherFromWorldProvider __instance, OxygenBreather oxygen_breather, float mass_to_consume, ref bool __result)
		{
			if (!active)
			{
				return true;
			}
			if (!breaths.TryGetValue(oxygen_breather, out Breath breath))
			{
				breaths[oxygen_breather] = breath = new Breath();
			}
			breath.need = mass_to_consume;
			breath.fresh = true;
			if (__instance.nav.CurrentNavType == NavType.Tube)
			{
				// In a transit tube the game assumes there is air and takes nothing.
				breath.got = mass_to_consume;
				breath.enough = true;
				SetBreathRate(oxygen_breather, breath, 1f);
				__result = true;
				return false;
			}
			CellOffset[] offsets = GasBreatherFromWorldProvider.DEFAULT_BREATHABLE_OFFSETS;
			int home = Grid.PosToCell(oxygen_breather);
			float left = mass_to_consume, fromMain = 0f, fromOther = 0f;
			Array.Clear(used, 0, used.Length);
			// Cells by descending breathable gas: the richest first, as in the game, then the others.
			for (int pick = 0; pick < offsets.Length && left > 1e-7f; pick++)
			{
				int best = -1, bestCell = Grid.InvalidCell;
				float bestAmount = 0f;
				for (int i = 0; i < offsets.Length && i < used.Length; i++)
				{
					int cell = Grid.OffsetCell(home, offsets[i]);
					if (used[i] || !Grid.IsValidCell(cell))
					{
						continue;
					}
					float amount = MainAvailable(cell) + frame[Header + cell];
					if (amount > bestAmount)
					{
						best = i;
						bestCell = cell;
						bestAmount = amount;
					}
				}
				if (best < 0)
				{
					break;
				}
				used[best] = true;
				float main = Math.Min(left, MainAvailable(bestCell));
				if (main > 0f)
				{
					// The cell's main gas is taken by the simulation, with the same message and the same response as in the game.
					HandleVector<Game.ComplexCallbackInfo<Sim.MassConsumedCallback>>.Handle handle =
						Game.Instance.massConsumedCallbackManager.Add(GasBreatherFromWorldProvider.OnSimConsumeCallbackAction, oxygen_breather, "GasBreatherFromWorldProvider");
					SimMessages.ConsumeMass(bestCell, Grid.Element[bestCell].id, main, 3, handle.index);
					takenFromMain.TryGetValue(bestCell, out float already);
					takenFromMain[bestCell] = already + main;
					left -= main;
					fromMain += main;
				}
				float other = Math.Min(left, frame[Header + bestCell]);
				if (other > 0f)
				{
					frame[Header + bestCell] -= other;
					// Together with the gas the duplicant inhales its share of the cell's germs (by the mass of the whole mixture): the layer
					// later removes the same amount from the cell.
					byte germ = Grid.DiseaseIdx[bestCell];
					int germs = 0;
					float mixture = GasMixture.RealTotal(bestCell);
					if (germ != byte.MaxValue && Grid.DiseaseCount[bestCell] > 0 && mixture > 0f)
					{
						germs = (int)(Grid.DiseaseCount[bestCell] * Mathf.Clamp01(other / mixture));
					}
					Buffer.BlockCopy(BitConverter.GetBytes(bestCell), 0, request, 0, 4);
					Buffer.BlockCopy(BitConverter.GetBytes(other), 0, request, 4, 4);
					Buffer.BlockCopy(BitConverter.GetBytes(germs), 0, request, 8, 4);
					SimHooks.SendData("gas-mix.consume", request);
					OxygenBreather.BreathableGasConsumed(oxygen_breather, SimHashes.Oxygen, other, Grid.Temperature[bestCell], germs > 0 ? germ : byte.MaxValue, germs);
					inhaledGerms += germs;
					left -= other;
					fromOther += other;
				}
			}
			breath.got = mass_to_consume - left;
			float share = mass_to_consume > 0f ? Mathf.Clamp01(breath.got / mass_to_consume) : 1f;
			breath.enough = share >= 0.5f;
			SetBreathRate(oxygen_breather, breath, share);
			breathCount++;
			needTotal += mass_to_consume;
			gotMain += fromMain;
			gotOther += fromOther;
			if (!breath.enough)
			{
				shortBreaths++;
			}
			__result = breath.enough;
			return false;
		}

		/// <summary>How much of the cell's main gas can still be inhaled in this tick (taking into account what was already requested from the simulation).</summary>
		private static float MainAvailable(int cell)
		{
			if (!breathableElement[Grid.ElementIdx[cell]])
			{
				return 0f;
			}
			takenFromMain.TryGetValue(cell, out float taken);
			return Math.Max(0f, Grid.Mass[cell] - frame[Header + cells + cell] - taken);
		}

		/// <summary>
		/// Breath bar rate by the share inhaled: 1 gives full growth, 0.5 zero, 0 full decline. The game
		/// applies one of two modifiers to the bar ("breathing" or "holding breath") depending on whether
		/// there is air, and switches them with a two-second delay; both get the same signed value,
		/// so the bar moves correctly during that delay too.
		/// </summary>
		private static void SetBreathRate(OxygenBreather breather, Breath breath, float share)
		{
			if (breath.monitor == null)
			{
				breath.monitor = breather.GetSMI<SuffocationMonitor.Instance>();
				if (breath.monitor == null)
				{
					return;
				}
			}
			float rate = DUPLICANTSTATS.STANDARD.Breath.BREATH_RATE * (2f * share - 1f);
			breath.monitor.increaseBreathModifier.SetValue(rate);
			breath.monitor.decreaseBreathModifier.SetValue(rate);
			breath.adjusted = true;
		}

		/// <summary>After a duplicant's breathing tick: exhalation by what was inhaled; outside the world's air the bar rates are as in the game.</summary>
		private static void AfterBreath(OxygenBreather __instance)
		{
			if (!breaths.TryGetValue(__instance, out Breath breath))
			{
				return;
			}
			if (breath.fresh)
			{
				breath.fresh = false;
				// The game computed exhalation by the quota if "there is air", and none if not.
				__instance.accumulatedCO2 += (breath.got - (breath.enough ? breath.need : 0f)) * __instance.O2toCO2conversion;
			}
			else if (breath.adjusted && breath.monitor != null)
			{
				// The duplicant is in a suit, dead or not breathing: the game controls the bar.
				float rate = DUPLICANTSTATS.STANDARD.Breath.BREATH_RATE;
				breath.monitor.increaseBreathModifier.SetValue(rate);
				breath.monitor.decreaseBreathModifier.SetValue(-rate);
				breath.adjusted = false;
			}
		}

		// ---------- What the player sees ----------

		/// <summary>
		/// The word about breathability in the tooltip under the cursor: the game sets it by the main gas, but it should be
		/// by all the breathable gas of the cell.
		/// </summary>
		private static void BreathableLabel(int cell, string[] __result)
		{
			if (!active || __result == null || __result.Length < 4 || !Grid.IsValidCell(cell))
			{
				return;
			}
			Element element = Grid.Element[cell];
			if (!(element.IsGas || element.IsVacuum))
			{
				return;
			}
			float other = frame[Header + cell];
			bool mainBreathable = breathableElement[element.idx];
			Element shownAs = mainBreathable || other <= 0f ? element : oxygen;
			float mass = mainBreathable ? MainMass(cell) + other : other > 0f ? other : Grid.Mass[cell];
			if (shownAs != labelElement || mass != labelMass)
			{
				labelElement = shownAs;
				labelMass = mass;
				label = " " + GameUtil.GetBreathableString(shownAs, mass);
			}
			__result[3] = label;
			// By molecules the game sees a gas cell's mass as its pressure (the mass of Oxygen giving the same pressure); the
			// cursor tooltip shows the real mass of the gas.
			if (byMolecules && element.IsGas)
			{
				FormatMass(GasMixture.RealTotal(cell), __result);
			}
		}

		private static bool byMolecules;

		/// <summary>The game's mass text (number, decimal, unit), as in HoverTextHelper.MassStringsReadOnly.</summary>
		private static void FormatMass(float mass, string[] strings)
		{
			strings[2] = STRINGS.UI.UNITSUFFIXES.MASS.KILOGRAM;
			if (mass < 5f)
			{
				mass *= 1000f;
				strings[2] = STRINGS.UI.UNITSUFFIXES.MASS.GRAM;
			}
			if (mass < 5f)
			{
				mass *= 1000f;
				strings[2] = STRINGS.UI.UNITSUFFIXES.MASS.MILLIGRAM;
			}
			if (mass < 5f)
			{
				mass *= 1000f;
				strings[2] = STRINGS.UI.UNITSUFFIXES.MASS.MICROGRAM;
				mass = Mathf.Floor(mass);
			}
			int whole = Mathf.FloorToInt(mass);
			int tenths = Mathf.RoundToInt(10f * (mass - whole));
			if (tenths == 10)
			{
				whole++;
				tenths = 0;
			}
			strings[0] = whole.ToString();
			strings[1] = "." + tenths;
		}

		/// <summary>The selected cell panel reads the cell mass once: for a gas cell it gets the real mass (see BreathableLabel).</summary>
		private static IEnumerable<CodeInstruction> PanelMassRead(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> code = new List<CodeInstruction>(instructions);
			System.Reflection.FieldInfo massField = AccessTools.Field(typeof(Grid), nameof(Grid.Mass));
			int at = code.FindIndex(instruction => instruction.opcode == OpCodes.Ldsflda && Equals(instruction.operand, massField));
			int call = at < 0 ? -1 : code.FindIndex(at, instruction => instruction.opcode == OpCodes.Call && instruction.operand is System.Reflection.MethodInfo getter && getter.Name == "get_Item");
			if (at < 0 || call < 0 || call - at > 4)
			{
				Debug.LogWarning("[OniGasMix] the cell mass read was not found in the selected cell panel; it shows the mass the game sees");
				return code;
			}
			code[at].opcode = OpCodes.Nop;
			code[at].operand = null;
			code[call] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GasLayerBreathing), nameof(PanelMass)));
			return code;
		}

		private static float PanelMass(int cell)
		{
			return byMolecules && Grid.IsValidCell(cell) && Grid.Element[cell].IsGas ? GasMixture.RealTotal(cell) : Grid.Mass[cell];
		}

		/// <summary>
		/// The normal view of the oxygen overlay (breathable or not): the same rule as in the game, but by all the
		/// breathable gas of the cell. Called from worker threads.
		/// </summary>
		internal static Color OverlayColour(SimDebugView instance, int cell)
		{
			if (Grid.IsLiquid(cell) || Grid.Solid[cell])
			{
				return Color.black;
			}
			float mass = Breathable(cell);
			if (mass > SimDebugView.minimumBreathable)
			{
				return instance.breathableGradient.Evaluate(Mathf.Clamp((mass - SimDebugView.minimumBreathable) / SimDebugView.optimallyBreathable, 0f, 1f));
			}
			return instance.unbreathableColour;
		}

		internal static string Stats()
		{
			string text = $"breaths={breathCount}, wanted {needTotal:F2} kg, inhaled {gotMain + gotOther:F2} kg (main gas {gotMain:F2}, other gases of the mixture {gotOther:F2}), " +
				$"breaths under half the need={shortBreaths}; germs inhaled with gases that are not the main one={inhaledGerms} (taken from cells by the layer: {SimHooks.GetValue("gas-mix.breath-germs")}); taken by the layer {SimHooks.GetValue("gas-mix.breathed-grams") / 1000.0:F2} kg, not found there {SimHooks.GetValue("gas-mix.breath-short-grams") / 1000.0:F3} kg; " +
				$"layer arrays fetched {frameCalls} times, {(frameCalls > 0 ? frameTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / frameCalls : 0):F0} us each on the main thread";
			return text;
		}
	}
}
