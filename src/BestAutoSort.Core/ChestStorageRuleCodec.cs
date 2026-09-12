using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BestAutoSort.Core;

public static class ChestStorageRuleCodec
{
	private const string Version = "v1";

	public static string Serialize(ChestStorageRule rule)
	{
		if (rule == null)
		{
			throw new ArgumentNullException("rule");
		}
		return rule.Scope switch
		{
			ChestRuleScope.Auto => string.Empty, 
			ChestRuleScope.Group => "v1|group|" + rule.GroupId, 
			ChestRuleScope.Categories => "v1|categories|" + string.Join(",", rule.Categories.Select((ItemCategory category) => ItemCategoryCatalog.Get(category).Id).OrderBy((string id) => id, StringComparer.OrdinalIgnoreCase)), 
			ChestRuleScope.Items => "v1|items|" + string.Join(",", rule.ItemPrefabNames.OrderBy((string name) => name, StringComparer.OrdinalIgnoreCase).Select(Encode)), 
			_ => throw new ArgumentOutOfRangeException("rule", rule.Scope, "Unknown chest rule scope."), 
		};
	}

	public static bool TryDeserialize(string serialized, out ChestStorageRule rule)
	{
		rule = ChestStorageRule.Automatic();
		if (string.IsNullOrWhiteSpace(serialized))
		{
			return true;
		}
		string[] array = serialized.Split(new char[1] { '|' }, 3);
		if (array.Length != 3 || !string.Equals(array[0], "v1", StringComparison.Ordinal))
		{
			return false;
		}
		try
		{
			switch (array[1])
			{
			case "group":
				rule = ChestStorageRule.ForGroup(array[2]);
				return true;
			case "categories":
			{
				List<ItemCategory> list = new List<ItemCategory>();
				foreach (string item in SplitPayload(array[2]))
				{
					if (!ItemCategoryCatalog.TryGet(item, out ItemCategoryDefinition definition) || definition == null)
					{
						return false;
					}
					list.Add(definition.Category);
				}
				rule = ChestStorageRule.ForCategories(list);
				return true;
			}
			case "items":
				rule = ChestStorageRule.ForItems(SplitPayload(array[2]).Select(Decode));
				return true;
			default:
				return false;
			}
		}
		catch (ArgumentException)
		{
			rule = ChestStorageRule.Automatic();
			return false;
		}
		catch (FormatException)
		{
			rule = ChestStorageRule.Automatic();
			return false;
		}
	}

	private static IEnumerable<string> SplitPayload(string payload)
	{
		return from value in (payload ?? string.Empty).Split(new char[1] { ',' }, StringSplitOptions.RemoveEmptyEntries)
			select value.Trim() into value
			where value.Length > 0
			select value;
	}

	private static string Encode(string value)
	{
		return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
	}

	private static string Decode(string value)
	{
		return Encoding.UTF8.GetString(Convert.FromBase64String(value));
	}
}
