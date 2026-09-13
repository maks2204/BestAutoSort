using System;
using BestAutoSort;
using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Player), "HaveRequirements", new Type[]
{
	typeof(Piece),
	typeof(RequirementMode)
})]
internal static class NearbyBuildRequirementsPatch
{
	private static void Postfix(Player __instance, Piece piece, RequirementMode mode, ref bool __result)
	{
		// DIAG-PLACE (temporary): prove the postfix fires.
		Plugin.LogInstance.LogInfo((object)("[ChestTX] diag-have piece=" + (((Object)(object)piece != (Object)null && piece.m_name != null) ? piece.m_name : "?") + " mode=" + ((int)mode) + " vanilla=" + (__result ? "1" : "0")));
		if (!__result && (int)mode == 0)
		{
			__result = NearbyResourceService.HasPieceRequirements(__instance, piece);
			Plugin.LogInstance.LogInfo((object)"[ChestTX] diag-have ours=" + __result);
		}
		else if (!__result && (int)mode == 2)
		{
			__result = NearbyResourceService.HasPieceAlmostRequirements(__instance, piece);
		}
	}
}
