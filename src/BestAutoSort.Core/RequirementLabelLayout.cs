using System;

namespace BestAutoSort.Core;

internal static class RequirementLabelLayout
{
	internal static float ExpandedWidth(float baseWidth, float preferredWidth, float availableWidth)
	{
		float val = Math.Max(baseWidth, availableWidth - 8f);
		return Math.Min(Math.Max(baseWidth, preferredWidth + 6f), val);
	}

	internal static float OffsetTowardCenter(float baseWidth, float expandedWidth, float elementCenter, float rowCenter)
	{
		float num = Math.Max(0f, expandedWidth - baseWidth);
		if (num <= 0f)
		{
			return 0f;
		}
		if (elementCenter < rowCenter)
		{
			return num * 0.5f;
		}
		if (elementCenter > rowCenter)
		{
			return num * -0.5f;
		}
		return 0f;
	}
}
