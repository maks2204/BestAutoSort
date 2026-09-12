using System;

namespace BestAutoSort.Core;

internal static class ChestToolbarLayout
{
	internal static float TopForButton(float chestTop, int index, float buttonHeight, float gap)
	{
		if (index < 0)
		{
			throw new ArgumentOutOfRangeException("index");
		}
		return chestTop - (float)index * (Math.Max(0f, buttonHeight) + Math.Max(0f, gap));
	}
}
