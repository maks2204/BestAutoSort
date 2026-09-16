using System.Reflection;
using BestAutoSort;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

/// <summary>
/// Deferred placement: the click stages missing mats first (nothing is pulled
/// while browsing); the piece is placed at the stored aim transform once staged.
/// Conservation gate included: without staged stock the click is swallowed instead
/// of placing at a discount (vanilla spawns the piece after partial consumption).
/// </summary>
[HarmonyPatch(typeof(Player), "TryPlacePiece")]
internal static class NearbyPlaceGatePatch
{
	private static readonly FieldInfo PlacementStatusField = AccessTools.Field(typeof(Player), "m_placementStatus");

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
		// Invalid ghost first: vanilla rejects without consuming, so no ahead
		// staging for such clicks (or every obstructed click would strand a set).
		try
		{
			if (PlacementStatusField != null && (Player.PlacementStatus)PlacementStatusField.GetValue(__instance) != Player.PlacementStatus.Valid)
				return true;
		}
		catch
		{
		}
		if (NearbyResourceService.HasStagedMatsForPiece(__instance, piece))
		{
			// Gate passed: vanilla places AND consumes NOW (Pump uninvolved), so
			// pipeline the next full set here — otherwise the next click starves.
			NearbyResourceService.StageMissingForPiece(__instance, piece, true);
			return true;
		}
		// Not staged: the click's own HaveRequirements already submitted the
		// prefetch (selected piece). Defer placement until mats land.
		if (NearbyPlaceIntent.HasIntentFor(piece))
			return false;
		NearbyResourceService.StageMissingForPiece(__instance, piece);
		string missing;
		NearbyResourceService.HasStagedMatsForPiece(__instance, piece, out missing);
		Plugin.LogInstance.LogInfo((object)("[ChestTX] place deferred, staging mats: " + missing));
		NearbyPlaceIntent.Store(__instance, piece);
		return false;
	}
}
