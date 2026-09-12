using BestAutoSort.Core;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class ItemInteractionModeService
{
	internal static bool TryCycle(InventoryGrid grid, ItemData? item)
	{
		if (!ModConfig.Enabled.Value || item == null || !IsAltPressed())
		{
			return false;
		}
		Inventory inventory = grid.GetInventory();
		Player modePlayer = Player.m_localPlayer;
		if (inventory == null || (Object)(object)modePlayer == (Object)null || inventory != ((Humanoid)modePlayer).GetInventory())
		{
			return false;
		}
		bool num = RestockProfileService.IsTarget(item);
		bool flag = ItemLockService.IsLocked(item);
		int current = (num ? 2 : (flag ? 1 : 0));
		bool canReplenish = item.m_shared.m_maxStackSize > 1 && !string.IsNullOrWhiteSpace(ValheimItemCategoryClassifier.PrefabName(item));
		ItemInteractionMode itemInteractionMode = ItemInteractionModeCycle.Next((ItemInteractionMode)current, canReplenish);
		if (flag)
		{
			ItemLockService.TrySet(grid, item, locked: false);
		}
		if (num)
		{
			RestockProfileService.TrySet(grid, item, enabled: false);
		}
		switch (itemInteractionMode)
		{
		case ItemInteractionMode.Locked:
			ItemLockService.TrySet(grid, item, locked: true);
			break;
		case ItemInteractionMode.Replenish:
			RestockProfileService.TrySet(grid, item, enabled: true);
			break;
		}
		ItemLockService.RefreshHighlights(grid);
		RestockProfileService.RefreshHighlights(grid);
		Player localPlayer = Player.m_localPlayer;
		if (localPlayer != null)
		{
			((Character)localPlayer).Message((MessageType)2, itemInteractionMode switch
			{
				ItemInteractionMode.Locked => "Item mode: Locked", 
				ItemInteractionMode.Nothing => "Item mode: Nothing", 
				_ => $"Item mode: Replenish (target: {item.m_shared.m_maxStackSize})", 
			}, 0, (Sprite)null, false);
		}
		return true;
	}

	private static bool IsAltPressed()
	{
		if (!Input.GetKey((KeyCode)308))
		{
			return Input.GetKey((KeyCode)307);
		}
		return true;
	}
}
