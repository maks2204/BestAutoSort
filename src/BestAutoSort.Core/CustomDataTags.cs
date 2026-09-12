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

	/// <summary>Remove benign bookkeeping keys from the live item. Returns true if anything was removed.</summary>
	internal static bool StripBenign(ItemDrop.ItemData item)
	{
		if (item == null || item.m_customData == null || item.m_customData.Count == 0)
			return false;
		bool removed = false;
		System.Collections.Generic.List<string> drop = null;
		foreach (System.Collections.Generic.KeyValuePair<string, string> kv in item.m_customData)
		{
			if (IsBenignKey(kv.Key))
			{
				if (drop == null)
					drop = new System.Collections.Generic.List<string>();
				drop.Add(kv.Key);
			}
		}
		if (drop != null)
		{
			foreach (string key in drop)
			{
				if (item.m_customData.Remove(key))
					removed = true;
			}
		}
		return removed;
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
