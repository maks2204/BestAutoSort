using System.Collections.Generic;
using BestAutoSort.Core;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class QuickStackTransfer
{
	internal static void DestSeeds(Inventory destination, HashSet<string> names, HashSet<ItemCategory> categories)
	{
		foreach (ItemData allItem in destination.GetAllItems())
		{
			if (allItem == null || allItem.m_shared == null)
				continue;
			names.Add(allItem.m_shared.m_name);
			categories.Add(ValheimItemCategoryClassifier.Classify(allItem));
		}
	}

	/// <summary>
	/// Rule check on the manager (no equipped/hotbar — the client filters those).
	/// </summary>
	internal static bool CanAcceptFromRule(ChestStorageRule rule, ItemData item, ISet<string> destinationTypes, ISet<ItemCategory> destinationCategories)
	{
		if (item == null || item.m_shared == null)
			return false;
		ItemCategory itemCategory = ValheimItemCategoryClassifier.Classify(item);
		if (rule.IsAutomatic)
		{
			bool num = destinationTypes.Contains(item.m_shared.m_name);
			bool flag = ModConfig.StorageMatchMode.Value == StorageMatchMode.ExactItemOrCategory && destinationCategories.Contains(itemCategory);
			if (!num && !flag)
				return false;
		}
		else if (!rule.Matches(ValheimItemCategoryClassifier.PrefabName(item), itemCategory))
		{
			return false;
		}
		if (item.m_shared.m_questItem || !ValheimItemCategoryClassifier.IsAutoStackable(item))
			return false;
		if (ModConfig.SkipCustomData.Value && CustomDataTags.HasForeignData(item))
			return false;
		return true;
	}
	internal static int MoveMatching(Container destinationContainer, Inventory source, ICollection<TransferRecord> records)
	{
		Inventory inventory = destinationContainer.GetInventory();
		ChestStorageRule rule = ChestRuleStore.Read(destinationContainer);
		return MoveMatchingInventory(inventory, rule, source, records);
	}

	internal static int MoveMatchingInventory(Inventory destination, ChestStorageRule rule, Inventory source, ICollection<TransferRecord> records)
	{
		HashSet<string> hashSet = new HashSet<string>();
		HashSet<ItemCategory> hashSet2 = new HashSet<ItemCategory>();
		foreach (ItemData allItem in destination.GetAllItems())
		{
			hashSet.Add(allItem.m_shared.m_name);
			hashSet2.Add(ValheimItemCategoryClassifier.Classify(allItem));
		}
		int num = 0;
		bool flag = false;
		foreach (ItemData item in new List<ItemData>(source.GetAllItems()))
		{
			if (!CanMove(item, rule, hashSet, hashSet2))
			{
				continue;
			}
			int stack = item.m_stack;
			bool flag2 = destination.AddItem(item);
			int num2 = (flag2 ? stack : (stack - item.m_stack));
			if (num2 > 0)
			{
				if (flag2)
				{
					source.RemoveItem(item);
				}
				else
				{
					flag = true;
				}
				num += num2;
				records.Add(new TransferRecord(item.m_shared.m_name, item.GetIcon(), num2, item.m_shared.m_maxStackSize));
			}
		}
		if (flag)
		{
			source.m_onChanged?.Invoke();
		}
		return num;
	}

	private static bool CanMove(ItemData item, ChestStorageRule rule, ISet<string> destinationTypes, ISet<ItemCategory> destinationCategories)
	{
		if (item == null || item.m_shared == null)
		{
			return false;
		}
		ItemCategory itemCategory = ValheimItemCategoryClassifier.Classify(item);
		if (rule.IsAutomatic)
		{
			bool num = destinationTypes.Contains(item.m_shared.m_name);
			bool flag = ModConfig.StorageMatchMode.Value == StorageMatchMode.ExactItemOrCategory && destinationCategories.Contains(itemCategory);
			if (!num && !flag)
			{
				return false;
			}
		}
		else if (!rule.Matches(ValheimItemCategoryClassifier.PrefabName(item), itemCategory))
		{
			return false;
		}
		if (item.m_shared.m_questItem || !ValheimItemCategoryClassifier.IsAutoStackable(item))
		{
			return false;
		}
		if (item.m_equipped || ((Object)(object)Player.m_localPlayer != (Object)null && ((Humanoid)Player.m_localPlayer).IsItemEquiped(item)))
		{
			return false;
		}
		if (ModConfig.ProtectHotbar.Value && item.m_gridPos.y == 0)
		{
			return false;
		}
		if (ItemLockService.IsLocked(item))
		{
			return false;
		}
		if (RestockProfileService.IsTarget(item))
		{
			return false;
		}
		if (ModConfig.SkipCustomData.Value && CustomDataTags.HasForeignData(item))
		{
			return false;
		}
		return true;
	}
}
