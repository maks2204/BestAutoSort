using System;
using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(InventoryGui), "OnSelectedItem", new Type[]
{
	typeof(InventoryGrid),
	typeof(ItemData),
	typeof(Vector2i),
	typeof(Modifier)
})]
[HarmonyPriority(800)]
internal static class InventoryGuiItemModePatch
{
	private static bool Prefix(InventoryGrid grid, ItemData? item, Modifier mod, ItemData? ___m_dragItem)
	{
		if ((int)mod != 0 || ___m_dragItem != null)
		{
			return true;
		}
		return !ItemInteractionModeService.TryCycle(grid, item);
	}
}
