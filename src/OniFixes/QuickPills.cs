using HarmonyLib;

namespace OniFixes
{
	/// <summary>
	/// Taking a pill is 10 s of work in the game, played with the animation of eating on the floor: the Duplicant sits down
	/// (1.7 s), then the eating loop runs for about 8 s, then they get up. With no food in their hands the loop is a still
	/// pose with puffed cheeks, so the Duplicant looks frozen until they suddenly get up. Here a pill takes 2 s: sit down,
	/// swallow, get up. What the pill does is unchanged.
	/// </summary>
	internal static class QuickPills
	{
		private const float PillSeconds = 2f;

		internal static void Apply(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(MedicinalPillWorkable), "OnSpawn"), postfix: new HarmonyMethod(typeof(QuickPills), nameof(Postfix)));
		}

		private static void Postfix(MedicinalPillWorkable __instance)
		{
			__instance.SetWorkTime(PillSeconds);
		}
	}
}
