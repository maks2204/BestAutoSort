namespace BestAutoSort.Core;

internal static class ItemInteractionModeCycle
{
	internal static ItemInteractionMode Next(ItemInteractionMode current, bool canReplenish)
	{
		switch (current)
		{
		case ItemInteractionMode.Nothing:
			return ItemInteractionMode.Locked;
		case ItemInteractionMode.Locked:
			if (!canReplenish)
			{
				return ItemInteractionMode.Nothing;
			}
			return ItemInteractionMode.Replenish;
		default:
			return ItemInteractionMode.Nothing;
		}
	}
}
