using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Container), "RPC_RequestTakeAll")]
internal static class AutoFeedContainerTakeAllLeasePatch
{
	private static bool Prefix(Container __instance, long uid)
	{
		if (!AutoFeedService.IsLocked(__instance))
		{
			return true;
		}
		ZNetView component = ((Component)__instance).GetComponent<ZNetView>();
		if (component != null)
		{
			component.InvokeRPC(uid, "RPC_TakeAllResponse", new object[1] { false });
		}
		return false;
	}
}
