using System.Collections.Generic;
using System.Linq;
using BestAutoSort.Core;

namespace BestAutoSort.Runtime;

internal static class KilnWoodService
{
	internal static bool IsCharcoalKiln(Smelter smelter)
	{
		return smelter.m_conversion.Any((Smelter.ItemConversion conversion) => conversion?.m_from?.m_itemData?.m_shared != null && conversion.m_to?.m_itemData?.m_shared != null && conversion.m_from.m_itemData.m_shared.m_name == "$item_wood" && conversion.m_to.m_itemData.m_shared.m_name == "$item_coal");
	}

	internal static IReadOnlyList<string> AllowedPriority()
	{
		return KilnWoodPolicy.AllowedPriority(ModConfig.KilnAllowCoreWood.Value, ModConfig.KilnAllowFineWood.Value);
	}
}
