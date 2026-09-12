using System;
using System.Linq;
using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Piece), "DropResources", new Type[] { typeof(HitData) })]
internal static class UpgradedChestRefundPatch
{
	private static void Prefix(Piece __instance, out Requirement[]? __state)
	{
		__state = null;
		Requirement[] array = ChestUpgradeService.AdditionalRefundRequirements(__instance);
		if (array != null && array.Length != 0)
		{
			__state = __instance.m_resources;
			__instance.m_resources = __state.Concat<Requirement>(array).ToArray();
		}
	}

	private static void Postfix(Piece __instance, Requirement[]? __state)
	{
		Restore(__instance, __state);
	}

	private static Exception? Finalizer(Exception? __exception, Piece __instance, Requirement[]? __state)
	{
		Restore(__instance, __state);
		return __exception;
	}

	private static void Restore(Piece piece, Requirement[]? original)
	{
		if (original != null)
		{
			piece.m_resources = original;
		}
	}
}
