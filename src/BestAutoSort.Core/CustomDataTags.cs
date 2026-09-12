using System;
using System.Collections.Generic;

namespace BestAutoSort.Core;

/// <summary>
/// Bookkeeping customData keys that never affect stacking/merging and are
/// transparent for all BestAutoSort filters (e.g. GearSlots slot tracking:
/// com.jg224.gearslots.* — stamped on every item passing its slots).
/// Truly foreign keys (enchantments etc.) are still respected by SkipCustomData.
/// </summary>
internal static class CustomDataTags
{
	internal static bool IsBenignKey(string key)
	{
		return !string.IsNullOrEmpty(key) && key.StartsWith("com.jg224.gearslots.", StringComparison.Ordinal);
	}

	internal static bool HasForeignData(ItemDrop.ItemData item)
	{
		if (item == null || item.m_customData == null || item.m_customData.Count == 0)
			return false;
		foreach (KeyValuePair<string, string> kv in item.m_customData)
		{
			if (!IsBenignKey(kv.Key))
				return true;
		}
		return false;
	}
}
