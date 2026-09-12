using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Tameable), "TamingUpdate")]
internal static class AutoFeedTamingUpdatePatch
{
	private static void Postfix(Tameable __instance)
	{
		AutoFeedService.TryFeed(__instance);
	}
}
