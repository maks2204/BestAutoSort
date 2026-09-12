using System;
using System.Linq;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(CookingStation), "OnInteract")]
internal static class CookingFoodFromChestsPatch
{
	private static void Prefix(CookingStation __instance, Humanoid user, out ProductionItemLoan? __state)
	{
		__state = null;
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
