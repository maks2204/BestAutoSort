using System;

namespace BestAutoSort.Core;

internal static class PlayerSideControlLayout
{
	internal static PlayerSideControlPlacement Calculate(float left, float right, float weightTop, float armorBottom, float inventoryBottom, float preferredStackHeight, float armorGap, float weightIconClearance, float horizontalOffset)
	{
		float width = Math.Max(64f, right - left);
		float stackHeight = Math.Max(32f, Math.Min(44f, preferredStackHeight));
		float num = weightTop + Math.Max(0f, weightIconClearance);
		float num2 = armorBottom - Math.Max(0f, armorGap);
		float trashHeight = Math.Max(1f, num2 - num);
		return new PlayerSideControlPlacement((left + right) * 0.5f + horizontalOffset, width, inventoryBottom, stackHeight, num, trashHeight);
	}
}
