using System;

namespace BestAutoSort.Core;

internal static class TrashOperationPlanner
{
	internal static int GetDestroyAmount(int stack, int heldAmount, bool isQuestItem)
	{
		if (isQuestItem || stack <= 0 || heldAmount <= 0)
		{
			return 0;
		}
		return Math.Min(stack, heldAmount);
	}
}
