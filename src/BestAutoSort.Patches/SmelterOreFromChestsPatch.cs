using System;
using System.Linq;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Smelter), "OnAddOre")]
internal static class SmelterOreFromChestsPatch
{
	private static void Prefix(Smelter __instance, Humanoid user, ItemData? item, out ProductionItemLoan? __state)
	{
		__state = null;
		if (item != null)
		{
			return;
		}
		if (KilnWoodService.IsCharcoalKiln(__instance))
		{
			NearbyResourceService.TryBorrowProductionItemInPriority((Component)(object)__instance, user, (ItemData candidate) => NearbyResourceService.MatchesAnyConversion(candidate, __instance.m_conversion.Select(conversion => conversion.m_from)), KilnWoodService.AllowedPriority(), out __state);
			return;
		}
		NearbyResourceService.TryBorrowProductionItem((Component)(object)__instance, user, (ItemData candidate) => NearbyResourceService.MatchesAnyConversion(candidate, __instance.m_conversion.Select(conversion => conversion.m_from)), out __state);
	}

	private static void Postfix(ProductionItemLoan? __state)
	{
		ProductionLoanCompletion.Complete(__state);
	}

	private static Exception? Finalizer(Exception? __exception, ProductionItemLoan? __state)
	{
		ProductionLoanCompletion.Complete(__state);
		return __exception;
	}
}
