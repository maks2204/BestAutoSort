using System;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Inventory), "RemoveItem", new Type[]
{
	typeof(string),
	typeof(int),
	typeof(int),
	typeof(bool)
})]
internal static class NearbySingleIngredientConsumptionPatch
{
	private static bool Prefix(Inventory __instance, string name, int amount, int itemQuality)
	{
		if (NearbyResourceService.InternalRemoval)
		{
			return true;
		}
		Player player = CraftingFromChestsContext.Player;
		Recipe recipe = CraftingFromChestsContext.Recipe;
		if ((Object)(object)player == (Object)null || (Object)(object)recipe == (Object)null || !recipe.m_requireOnlyOneIngredient || __instance != ((Humanoid)player).GetInventory() || !ModConfig.CraftFromNearbyChests.Value)
		{
			return true;
		}
		int taken = NearbyResourceService.ConsumeItem(player, name, amount, itemQuality);
		NearbyResourceService.DecrementAhead(name, taken);
		return false;
	}
}
