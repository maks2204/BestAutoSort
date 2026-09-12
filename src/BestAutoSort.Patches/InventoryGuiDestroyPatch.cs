using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(InventoryGui), "OnDestroy")]
internal static class InventoryGuiDestroyPatch
{
	private static void Prefix(InventoryGui __instance)
	{
		InventoryButtons.Detach(__instance);
	}
}
