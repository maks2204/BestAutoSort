using System;
using System.Collections.Generic;
using BestAutoSort.Core;

namespace BestAutoSort.Runtime;

internal static class InventorySorter
{
	internal static int Sort(Inventory inventory, SortMode mode, bool descending)
	{
		if (inventory == null)
		{
			throw new ArgumentNullException("inventory");
		}
		List<ItemData> list = new List<ItemData>(inventory.GetAllItems());
		List<SortableItem> list2 = new List<SortableItem>(list.Count);
		for (int i = 0; i < list.Count; i++)
		{
			ItemData val = list[i];
			string text = val.m_shared.m_name ?? string.Empty;
			string localizedName = ((Localization.instance != null) ? Localization.instance.Localize(text) : text);
			list2.Add(new SortableItem
			{
				OriginalIndex = i,
				Category = ItemCategoryCatalog.SortRank(ValheimItemCategoryClassifier.Classify(val)),
				InternalName = text,
				LocalizedName = localizedName,
				Weight = val.GetNonStackedWeight(),
				Value = val.GetValue(),
				Quality = val.m_quality,
				Stack = val.m_stack
			});
		}
		IReadOnlyList<int> readOnlyList = InventoryOrdering.OrderIndices(list2, mode, descending);
		int width = inventory.GetWidth();
		for (int j = 0; j < readOnlyList.Count; j++)
		{
			list[readOnlyList[j]].m_gridPos = new Vector2i(j % width, j / width);
		}
		inventory.m_onChanged?.Invoke();
		return list.Count;
	}
}
