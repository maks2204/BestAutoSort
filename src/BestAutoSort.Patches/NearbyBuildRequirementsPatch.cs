using System;
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
		if (!__result && (int)mode == 0)
		{
			__result = NearbyResourceService.HasPieceRequirements(__instance, piece);
		}
	}
}
