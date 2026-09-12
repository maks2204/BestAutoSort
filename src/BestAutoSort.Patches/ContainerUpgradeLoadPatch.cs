using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Container), "Load")]
internal static class ContainerUpgradeLoadPatch
{
	private static void Postfix(Container __instance, bool __result)
	{
		if (__result)
		{
			ChestUpgradeService.ApplyState(__instance);
		}
	}
}
