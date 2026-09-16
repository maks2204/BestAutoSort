using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

/// <summary>
/// Conservation gate: the button/list show chest-inclusive availability, but the actual
/// craft may only consume synchronously-available stock (inventory + self-owned chests).
/// Without this, a press whose prefetch has not landed yet would craft at a discount
/// (vanilla spawns the item after partial consumption). Abort is clean: nothing is
/// consumed or spawned, staged mats keep arriving, the next press crafts.
/// </summary>
[HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
internal static class NearbyCraftGatePatch
{
	private static bool Prefix(InventoryGui __instance, Player player)
	{
		if (!ModConfig.CraftFromNearbyChests.Value)
			return true;
		if ((Object)(object)player == (Object)null || (Object)(object)player != (Object)(object)Player.m_localPlayer)
			return true;
		if (player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost))
			return true;
		try
		{
			Recipe? recipe;
			int quality;
			int multi;
			if (!CraftingFromChestsContext.GetCraftState(__instance, out recipe, out quality, out multi))
				return true;
			if (!NearbyResourceService.HasStagedMats(player, recipe, quality, multi))
			{
				Plugin.LogInstance.LogInfo((object)"[ChestTX] craft gated: staged mats not landed yet, press again");
				TxGui.TellPlayer("Fetching mats from nearby chests - press Craft again");
				return false;
			}
			return true;
		}
		catch
		{
			return true;
		}
	}
}
