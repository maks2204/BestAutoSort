namespace BestAutoSort.Core;

public sealed class ItemCategoryGroupDefinition
{
	public string Id { get; }

	public string DisplayName { get; }

	public ItemCategoryGroupDefinition(string id, string displayName)
	{
		Id = id;
		DisplayName = displayName;
	}
}
