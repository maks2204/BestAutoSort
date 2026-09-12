namespace BestAutoSort.Core;

internal readonly struct PlayerSideControlPlacement
{
	internal float CenterX { get; }

	internal float Width { get; }

	internal float StackBottomY { get; }

	internal float StackHeight { get; }

	internal float TrashBottomY { get; }

	internal float TrashHeight { get; }

	internal PlayerSideControlPlacement(float centerX, float width, float stackBottomY, float stackHeight, float trashBottomY, float trashHeight)
	{
		CenterX = centerX;
		Width = width;
		StackBottomY = stackBottomY;
		StackHeight = stackHeight;
		TrashBottomY = trashBottomY;
		TrashHeight = trashHeight;
	}
}
