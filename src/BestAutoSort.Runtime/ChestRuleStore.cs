using System.Collections.Generic;
using BestAutoSort.Core;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class ChestRuleStore
{
	private const string RuleKey = "BestAutoSort.StorageRule";

	internal static ChestStorageRule Read(Container container)
	{
		if ((Object)(object)container == (Object)null)
		{
			return ChestStorageRule.Automatic();
		}
		ZNetView component = ((Component)container).GetComponent<ZNetView>();
		ZDO val = (((Object)(object)component != (Object)null) ? component.GetZDO() : null);
		if (val == null)
		{
			return ChestStorageRule.Automatic();
		}
		if (ChestStorageRuleCodec.TryDeserialize(val.GetString("BestAutoSort.StorageRule", string.Empty), out ChestStorageRule rule))
		{
			return rule;
		}
		Plugin.LogInstance.LogWarning((object)("Ignored an invalid BestAutoSort storage rule on " + ((Object)container).name + "."));
		return ChestStorageRule.Automatic();
	}

	internal static bool TryWrite(Container container, ChestStorageRule rule, out string error)
	{
		error = string.Empty;
		if ((Object)(object)container == (Object)null)
		{
			error = "The chest is no longer available.";
			return false;
		}
		ZNetView component = ((Component)container).GetComponent<ZNetView>();
		ZDO val = (((Object)(object)component != (Object)null) ? component.GetZDO() : null);
		if ((Object)(object)component == (Object)null || val == null)
		{
			error = "This container cannot persist storage rules.";
			return false;
		}
		if (!component.IsOwner())
		{
			error = "Waiting for chest ownership. Try Save again.";
			return false;
		}
		val.Set("BestAutoSort.StorageRule", ChestStorageRuleCodec.Serialize(rule));
		return true;
	}

	internal static string ShortSummary(ChestStorageRule rule)
	{
		switch (rule.Scope)
		{
		case ChestRuleScope.Group:
		{
			if (!ItemCategoryCatalog.TryGetGroup(rule.GroupId, out ItemCategoryGroupDefinition definition) || definition == null)
			{
				return "Group";
			}
			return definition.DisplayName;
		}
		case ChestRuleScope.Categories:
			if (rule.Categories.Count == 1)
			{
				using IEnumerator<ItemCategory> enumerator = rule.Categories.GetEnumerator();
				if (enumerator.MoveNext())
				{
					return ItemCategoryCatalog.Get(enumerator.Current).DisplayName;
				}
			}
			return rule.Categories.Count + " Categories";
		case ChestRuleScope.Items:
			return rule.ItemPrefabNames.Count + ((rule.ItemPrefabNames.Count == 1) ? " Item" : " Items");
		default:
			return "Auto";
		}
	}
}
