using System;
using System.Linq;
using System.Reflection;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Fermenter), "Interact")]
internal static class FermenterInputFromChestsPatch
{
	private static readonly MethodInfo GetStatusMethod = AccessTools.Method(typeof(Fermenter), "GetStatus", (Type[])null, (Type[])null);

	private static void Prefix(Fermenter __instance, Humanoid user, out ProductionItemLoan? __state)
	{
		__state = null;
		object obj = GetStatusMethod.Invoke(__instance, null);
		if (obj == null || Convert.ToInt32(obj) != 0)
		{
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
