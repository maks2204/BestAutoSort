namespace BestAutoSort.Core;

public sealed class SortableItem
{
	public int OriginalIndex { get; set; }

	public int Category { get; set; }

	public string InternalName { get; set; } = string.Empty;

	public string LocalizedName { get; set; } = string.Empty;

	public float Weight { get; set; }

	public int Value { get; set; }

	public int Quality { get; set; }

	public int Stack { get; set; }
}
