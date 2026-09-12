using System;
using System.Collections.Generic;
using System.Linq;

namespace BestAutoSort.Core;

public sealed class ChestStorageRule
{
	private static readonly ChestStorageRule AutomaticRule = new ChestStorageRule(ChestRuleScope.Auto, string.Empty, Array.Empty<ItemCategory>(), Array.Empty<string>());

	private readonly HashSet<ItemCategory> _categories;

	private readonly HashSet<string> _itemPrefabNames;

	public ChestRuleScope Scope { get; }

	public string GroupId { get; }

	public IReadOnlyCollection<ItemCategory> Categories => _categories;

	public IReadOnlyCollection<string> ItemPrefabNames => _itemPrefabNames;

	public bool IsAutomatic => Scope == ChestRuleScope.Auto;

	private ChestStorageRule(ChestRuleScope scope, string groupId, IEnumerable<ItemCategory> categories, IEnumerable<string> itemPrefabNames)
	{
		Scope = scope;
		GroupId = groupId ?? string.Empty;
		_categories = new HashSet<ItemCategory>(categories ?? Array.Empty<ItemCategory>());
		_itemPrefabNames = new HashSet<string>((itemPrefabNames ?? Array.Empty<string>()).Where((string name) => !string.IsNullOrWhiteSpace(name)).Select(NormalizePrefabName), StringComparer.OrdinalIgnoreCase);
	}

	public static ChestStorageRule Automatic()
	{
		return AutomaticRule;
	}

	public static ChestStorageRule ForGroup(string groupId)
	{
		if (!ItemCategoryCatalog.TryGetGroup(groupId, out ItemCategoryGroupDefinition definition) || definition == null)
		{
			throw new ArgumentException("Unknown storage group.", "groupId");
		}
		return new ChestStorageRule(ChestRuleScope.Group, definition.Id, Array.Empty<ItemCategory>(), Array.Empty<string>());
	}

	public static ChestStorageRule ForCategories(IEnumerable<ItemCategory> categories)
	{
		ItemCategory[] array = (categories ?? Array.Empty<ItemCategory>()).Distinct().ToArray();
		if (array.Length == 0)
		{
			throw new ArgumentException("Select at least one category.", "categories");
		}
		return new ChestStorageRule(ChestRuleScope.Categories, string.Empty, array, Array.Empty<string>());
	}

	public static ChestStorageRule ForItems(IEnumerable<string> itemPrefabNames)
	{
		string[] array = (itemPrefabNames ?? Array.Empty<string>()).Where((string name) => !string.IsNullOrWhiteSpace(name)).Select(NormalizePrefabName).Distinct<string>(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (array.Length == 0)
		{
			throw new ArgumentException("Select at least one item.", "itemPrefabNames");
		}
		return new ChestStorageRule(ChestRuleScope.Items, string.Empty, Array.Empty<ItemCategory>(), array);
	}

	public bool Matches(string prefabName, ItemCategory category)
	{
		return Scope switch
		{
			ChestRuleScope.Group => string.Equals(ItemCategoryCatalog.Get(category).GroupId, GroupId, StringComparison.OrdinalIgnoreCase), 
			ChestRuleScope.Categories => _categories.Contains(category), 
			ChestRuleScope.Items => _itemPrefabNames.Contains(NormalizePrefabName(prefabName)), 
			_ => false, 
		};
	}

	public static string NormalizePrefabName(string value)
	{
		string text = (value ?? string.Empty).Trim();
		if (text.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(0, text.Length - "(Clone)".Length).TrimEnd();
		}
		return text;
	}
}
