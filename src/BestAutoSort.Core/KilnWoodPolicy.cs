using System.Collections.Generic;

namespace BestAutoSort.Core;

internal static class KilnWoodPolicy
{
	internal const string Wood = "$item_wood";

	internal const string CoreWood = "$item_roundlog";

	internal const string FineWood = "$item_finewood";

	internal const string Coal = "$item_coal";

	internal static IReadOnlyList<string> AllowedPriority(bool allowCoreWood, bool allowFineWood)
	{
		List<string> list = new List<string> { "$item_wood" };
		if (allowCoreWood)
		{
			list.Add("$item_roundlog");
		}
		if (allowFineWood)
		{
			list.Add("$item_finewood");
		}
		return list;
	}
}
