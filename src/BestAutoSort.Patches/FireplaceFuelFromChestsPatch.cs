using System;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Fireplace), "Interact")]
internal static class FireplaceFuelFromChestsPatch
{
	private static void Prefix(Fireplace __instance, Humanoid user, out ProductionItemLoan? __state)
	{
		__state = null;
		if (!__instance.m_infiniteFuel && !((Object)(object)__instance.m_fuelItem == (Object)null))
		{
			string fuelName = __instance.m_fuelItem.m_itemData.m_shared.m_name;
			NearbyResourceService.TryBorrowProductionItem((Component)(object)__instance, user, (ItemData candidate) => candidate.m_shared.m_name == fuelName, out __state);
		}
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
