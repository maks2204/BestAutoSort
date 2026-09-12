using System.Collections.Generic;

namespace BestAutoSort.Core;

internal static class ItemStackLock
{
	internal const string CustomDataKey = "BestAutoSort.StackLocked";

	internal static bool IsLocked(IDictionary<string, string>? customData)
	{
		if (customData != null && customData.TryGetValue("BestAutoSort.StackLocked", out string value))
		{
			return value == "1";
		}
		return false;
	}

	internal static void SetLocked(IDictionary<string, string> customData, bool locked)
	{
		if (locked)
		{
			customData["BestAutoSort.StackLocked"] = "1";
		}
		else
		{
			customData.Remove("BestAutoSort.StackLocked");
		}
	}
}
