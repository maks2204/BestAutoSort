using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Container), "Awake")]
internal static class ContainerUpgradeAwakePatch
{
	private static void Postfix(Container __instance)
	{
		ChestUpgradeService.ApplyState(__instance);
	}
}
