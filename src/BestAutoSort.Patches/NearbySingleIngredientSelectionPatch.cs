using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Player), "GetFirstRequiredItem")]
internal static class NearbySingleIngredientSelectionPatch
{
	private static void Postfix(Player __instance, Inventory inventory, Recipe recipe, int qualityLevel, ref int amount, ref int extraAmount, int craftMultiplier, ref ItemData? __result)
	{
		if (__result == null && inventory == ((Humanoid)__instance).GetInventory() && recipe.m_requireOnlyOneIngredient)
		{
			__result = NearbyResourceService.FindFirstRequiredItem(__instance, recipe, qualityLevel, craftMultiplier, out amount, out extraAmount);
		}
		// Rows show chest-inclusive counts: the tooltip must agree. If vanilla reports
		// a shortfall the nearby chests cover, there is nothing missing.
		if (__result != null && inventory == ((Humanoid)__instance).GetInventory() && ModConfig.CraftFromNearbyChests.Value)
		{
			if (NearbyResourceService.HasRecipeRequirements(__instance, recipe, qualityLevel, craftMultiplier))
			{
				__result = null;
				amount = 0;
				extraAmount = 0;
			}
		}
	}
}
