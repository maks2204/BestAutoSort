using System.Reflection;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

/// <summary>
/// Pull-on-press: when the craft button is pressed, stage missing mats from foreign
/// chests into the player inventory. Vanilla starts m_craftTimer on press and runs
/// DoCrafting seconds later — the tx round-trip lands inside that window, so no
/// browsing-time prefetch is needed (and the inventory no longer floods on select).
/// If mats have not landed by DoCrafting (slow link), vanilla aborts cleanly and the
/// next press succeeds with staged mats.
/// </summary>
[HarmonyPatch(typeof(InventoryGui), "OnCraftPressed")]
internal static class NearbyCraftPressPatch
{
	private static readonly FieldInfo CraftTimerField = AccessTools.Field(typeof(InventoryGui), "m_craftTimer");

	private static void Postfix(InventoryGui __instance)
	{
		if (!ModConfig.CraftFromNearbyChests.Value)
			return;
		Player player = Player.m_localPlayer;
		if ((Object)(object)player == (Object)null || (Object)(object)__instance == (Object)null)
			return;
		try
		{
			// Press did not start a craft (no recipe / inventory full): nothing to stage.
			if (CraftTimerField == null || (float)CraftTimerField.GetValue(__instance) < 0f)
				return;
			Recipe? recipe;
			int quality;
			int multi;
			if (!CraftingFromChestsContext.GetCraftState(__instance, out recipe, out quality, out multi))
				return;
			NearbyResourceService.PrefetchForCraftPress(player, recipe, quality, multi);
		}
		catch
		{
		}
	}
}
