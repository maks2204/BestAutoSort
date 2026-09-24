using System;
using System.Collections.Generic;
using System.Linq;
using BestAutoSort.Core;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class ChestReserveStore
{
	internal const string Key = "BestAutoSort.StorageReserves.v1";

	internal static string ReadRaw(Container container)
	{
		ZNetView component = ((Component)container).GetComponent<ZNetView>();
		object obj;
		if (component == null)
		{
			obj = null;
		}
		else
		{
			ZDO zDO = component.GetZDO();
			obj = ((zDO != null) ? zDO.GetString("BestAutoSort.StorageReserves.v1", "") : null);
		}
		if (obj == null)
		{
			obj = "";
		}
		return (string)obj;
	}

	internal static int Available(Container container, ItemData item)
	{
		try
		{
			Dictionary<string, int> dictionary = StorageReserves.Decode(ReadRaw(container));
			string prefab = ValheimItemCategoryClassifier.PrefabName(item);
			if (!dictionary.TryGetValue(prefab, out var value))
			{
				return item.m_stack;
			}
			int total = (from candidate in container.GetInventory().GetAllItems()
				where string.Equals(ValheimItemCategoryClassifier.PrefabName(candidate), prefab, StringComparison.OrdinalIgnoreCase)
				select candidate).Sum((ItemData candidate) => candidate.m_stack);
			return Math.Min(item.m_stack, StorageReserves.Available(total, value));
		}
		catch (Exception)
		{
			return 0;
		}
	}

	internal static int Count(Container container, string name, int quality)
	{
		try
		{
			Dictionary<string, int> dictionary = StorageReserves.Decode(ReadRaw(container));
			int num = 0;
			foreach (IGrouping<string, ItemData> item in (from item in container.GetInventory().GetAllItems()
				where item.m_shared.m_name == name && !item.m_shared.m_questItem
				select item).GroupBy<ItemData, string>(ValheimItemCategoryClassifier.PrefabName, StringComparer.OrdinalIgnoreCase))
			{
				dictionary.TryGetValue(item.Key, out var value);
				num += Math.Min(item.Where((ItemData item) => item.m_worldLevel >= Game.m_worldLevel && (quality < 0 || item.m_quality == quality)).Sum((ItemData item) => item.m_stack), StorageReserves.Available(item.Sum((ItemData item) => item.m_stack), value));
			}
			return num;
		}
		catch (Exception)
		{
			return 0;
		}
	}

	internal static void Write(Container container, string encoded)
	{
		StorageReserves.Decode(encoded);
		ZNetView component = ((Component)container).GetComponent<ZNetView>();
		// Wave-2: authority-routed. Remote managed chests fail closed (never a
		// direct ZDO write); the manager path is unchanged.
		if ((Object)(object)component == (Object)null || !component.IsValid() || !BestAutoSort.Tx.ChestTxService.IsManager(container) || AutoFeedService.IsLocked(container))
		{
			throw new InvalidOperationException("Open the chest and wait for ownership before changing its reserves.");
		}
		component.GetZDO().Set("BestAutoSort.StorageReserves.v1", encoded);
	}
}
