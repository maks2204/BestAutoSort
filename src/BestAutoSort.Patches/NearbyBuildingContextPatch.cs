using System;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Player), "UpdatePlacement", new Type[]
{
	typeof(bool),
	typeof(float)
})]
internal static class NearbyBuildingContextPatch
{
	private static void Prefix(Player __instance)
	{
		if ((Object)(object)__instance == (Object)(object)Player.m_localPlayer && ModConfig.CraftFromNearbyChests.Value)
		{
			BuildingFromChestsContext.Begin(__instance);
		}
	}

	private static void Postfix()
	{
		BuildingFromChestsContext.End();
	}

	private static Exception? Finalizer(Exception? __exception)
	{
		BuildingFromChestsContext.End();
		return __exception;
	}
}
