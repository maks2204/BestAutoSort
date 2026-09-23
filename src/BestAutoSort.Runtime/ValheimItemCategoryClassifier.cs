using System.Runtime.CompilerServices;
using BestAutoSort.Core;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class ValheimItemCategoryClassifier
{
	internal static string PrefabName(ItemData item)
	{
		return ChestStorageRule.NormalizePrefabName((item != null && (Object)(object)item.m_dropPrefab != (Object)null) ? ((Object)item.m_dropPrefab).name : string.Empty);
	}

	internal static ItemCategory Classify(ItemData item)
	{
		SharedData shared = item.m_shared;
		return ItemCategoryClassifier.Classify(new ItemClassificationInput
		{
			PrefabName = PrefabName(item),
			SharedName = (shared.m_name ?? string.Empty),
			ItemType = ((object)System.Runtime.CompilerServices.Unsafe.As<ItemType, ItemType>(ref shared.m_itemType)/*cast due to constrained. prefix*/).ToString(),
			AttachOverrideType = ((object)System.Runtime.CompilerServices.Unsafe.As<ItemType, ItemType>(ref shared.m_attachOverride)/*cast due to constrained. prefix*/).ToString(),
			SkillType = ((object)System.Runtime.CompilerServices.Unsafe.As<SkillType, SkillType>(ref shared.m_skillType)/*cast due to constrained. prefix*/).ToString(),
			AmmoType = (shared.m_ammoType ?? string.Empty),
			IsQuestItem = shared.m_questItem,
			HasBuildPieces = ((Object)(object)shared.m_buildPieces != (Object)null),
			HasConsumeStatusEffect = ((Object)(object)shared.m_consumeStatusEffect != (Object)null),
			Value = shared.m_value,
			FoodHealth = shared.m_food,
			FoodStamina = shared.m_foodStamina,
			FoodEitr = shared.m_foodEitr
		});
	}

	/// <summary>
	/// Player-side quick-stack eligibility. Vanilla sets m_autoStack=false on eggs;
	/// when <see cref="ModConfig.TreatEggsAsAutoStack"/> is on, Egg-category items
	/// (Asksvin/Chicken/Vulture) count as stackable. Scoped narrowly to eggs only.
	/// </summary>
	internal static bool IsAutoStackable(ItemData item)
	{
		if (item == null || item.m_shared == null)
			return false;
		if (item.m_shared.m_autoStack)
			return true;
		return ModConfig.TreatEggsAsAutoStack.Value && Classify(item) == ItemCategory.Egg;
	}
}
