using System;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
internal static class NearbyCraftingContextPatch
{
	private static void Prefix(InventoryGui __instance, Player player)
	{
		CraftingFromChestsContext.Begin(__instance, player);
	}

	private static void Postfix()
	{
		CraftingFromChestsContext.End();
	}

	private static Exception? Finalizer(Exception? __exception)
	{
		CraftingFromChestsContext.End();
		return __exception;
	}
}
