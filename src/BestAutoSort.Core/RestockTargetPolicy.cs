using System;

namespace BestAutoSort.Core;

internal static class RestockTargetPolicy
{
	internal static int GetTarget(int maxStack)
	{
		return Math.Max(1, maxStack);
	}
}
