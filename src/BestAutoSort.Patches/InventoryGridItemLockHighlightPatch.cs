using System;
using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(InventoryGrid), "UpdateInventory", new Type[]
{
	typeof(Inventory),
	typeof(Player),
	typeof(ItemData)
})]
internal static class InventoryGridItemLockHighlightPatch
{
	private static void Postfix(InventoryGrid __instance)
	{
		ItemLockService.RefreshHighlights(__instance);
		RestockProfileService.RefreshHighlights(__instance);
	}
}
