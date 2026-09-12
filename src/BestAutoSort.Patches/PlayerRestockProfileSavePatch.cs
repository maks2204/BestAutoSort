using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Player), "Save")]
internal static class PlayerRestockProfileSavePatch
{
	private static void Prefix(Player __instance)
	{
		RestockProfileService.PersistLiveProfiles(__instance);
	}
}
