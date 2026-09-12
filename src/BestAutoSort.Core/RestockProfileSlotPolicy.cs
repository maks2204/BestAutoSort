using System;
using System.Collections.Generic;

namespace BestAutoSort.Core;

internal static class RestockProfileSlotPolicy
{
	internal static void RemoveConflicts(IList<RestockProfile> profiles, RestockInventoryKind inventoryKind, int x, int y, string activeId)
	{
		for (int num = profiles.Count - 1; num >= 0; num--)
		{
			RestockProfile restockProfile = profiles[num];
			if (restockProfile.InventoryKind == inventoryKind && restockProfile.X == x && restockProfile.Y == y && !string.Equals(restockProfile.Id, activeId, StringComparison.Ordinal))
			{
				profiles.RemoveAt(num);
			}
		}
	}
}
