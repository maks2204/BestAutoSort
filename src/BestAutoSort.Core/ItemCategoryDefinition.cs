namespace BestAutoSort.Core;

public sealed class ItemCategoryDefinition
{
	public ItemCategory Category { get; }

	public string Id { get; }

	public string GroupId { get; }

	public string Group { get; }

	public string DisplayName { get; }

	public ItemCategoryDefinition(ItemCategory category, string id, string groupId, string group, string displayName)
	{
		Category = category;
		Id = id;
		GroupId = groupId;
		Group = group;
		DisplayName = displayName;
	}
}
