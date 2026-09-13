using BestAutoSort;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

/// <summary>
/// Conservation gate for building: the menu/list show chest-inclusive availability,
/// but placement may only consume synchronously-available stock (inventory +
/// self-owned chests, incl. just-staged prefetches). Without this, a click whose
/// prefetch has not landed yet would place at a discount (vanilla spawns the piece
/// after partial consumption). Silent deny like vanilla failed checks.
/// </summary>
[HarmonyPatch(typeof(Player), "TryPlacePiece")]
internal static class NearbyPlaceGatePatch
{
	private static bool Prefix(Player __instance, Piece piece)
	{
		if (!ModConfig.CraftFromNearbyChests.Value)
			return true;
		if ((Object)(object)__instance == (Object)null || (Object)(object)piece == (Object)null)
			return true;
		if ((Object)(object)__instance != (Object)(object)Player.m_localPlayer)
			return true;
		if (__instance.NoCostCheat())
			return true;
		if (piece.m_repairPiece || piece.m_removePiece)
			return true;
		try
		{
			if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
				return true;
		}
		catch
		{
		}
		if (!NearbyResourceService.HasStagedMatsForPiece(__instance, piece))
		{
			Plugin.LogInstance.LogInfo((object)"[ChestTX] place gated: staged mats not landed yet");
			return false;
		}
		return true;
	}
}
