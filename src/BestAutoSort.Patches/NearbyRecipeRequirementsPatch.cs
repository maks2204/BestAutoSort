using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Player), "HaveRequirementItems")]
internal static class NearbyRecipeRequirementsPatch
{
	private static void Postfix(Player __instance, Recipe piece, bool discover, int qualityLevel, int amount, ref bool __result)
	{
		if (!(__result | discover))
		{
			__result = NearbyResourceService.HasRecipeRequirements(__instance, piece, qualityLevel, amount);
		}
	}
}
