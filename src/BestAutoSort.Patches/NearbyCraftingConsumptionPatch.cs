using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Player), "ConsumeResources")]
internal static class NearbyCraftingConsumptionPatch
{
	private static bool Prefix(Player __instance, Requirement[] requirements, int qualityLevel, int itemQuality, int multiplier)
	{
		bool num = CraftingFromChestsContext.Active && (Object)(object)CraftingFromChestsContext.Player == (Object)(object)__instance;
		bool flag = BuildingFromChestsContext.Active && (Object)(object)BuildingFromChestsContext.Player == (Object)(object)__instance;
		if ((!num && !flag) || !ModConfig.CraftFromNearbyChests.Value)
		{
			return true;
		}
		NearbyResourceService.ConsumeRequirements(__instance, requirements, qualityLevel, itemQuality, multiplier);
		return false;
	}
}
