using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(InventoryGui), "Awake")]
internal static class InventoryGuiAwakePatch
{
	private static void Postfix(InventoryGui __instance)
	{
		InventoryButtons.Attach(__instance);
	}
}
