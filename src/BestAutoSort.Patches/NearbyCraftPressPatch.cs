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

	private static readonly FieldInfo CraftRecipeField = AccessTools.Field(typeof(InventoryGui), "m_craftRecipe");

	private static readonly FieldInfo CraftUpgradeItemField = AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem");

	private static readonly FieldInfo MultiCraftingField = AccessTools.Field(typeof(InventoryGui), "m_multiCrafting");

	private static readonly FieldInfo MultiCraftAmountField = AccessTools.Field(typeof(InventoryGui), "m_multiCraftAmount");

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
			Recipe recipe = (CraftRecipeField != null) ? (CraftRecipeField.GetValue(__instance) as Recipe) : null;
			if ((Object)(object)recipe == (Object)null)
				return;
			ItemData upgradeItem = (CraftUpgradeItemField != null) ? (CraftUpgradeItemField.GetValue(__instance) as ItemData) : null;
			int quality = (upgradeItem == null) ? 1 : (upgradeItem.m_quality + 1);
			bool multi = MultiCraftingField != null && (bool)MultiCraftingField.GetValue(__instance);
			int multiAmount = 1;
			if (multi && MultiCraftAmountField != null)
				multiAmount = (int)MultiCraftAmountField.GetValue(__instance);
			NearbyResourceService.PrefetchForCraftPress(player, recipe, quality, multi ? multiAmount : 1);
		}
		catch
		{
		}
	}
}
