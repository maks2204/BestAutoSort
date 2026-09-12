using System.Collections.Generic;
using System.Globalization;

namespace BestAutoSort.Core;

internal static class RestockMarker
{
	internal const string IdKey = "BestAutoSort.RestockId";

	internal const string TargetKey = "BestAutoSort.RestockTarget";

	internal static bool TryRead(IDictionary<string, string>? customData, out string id, out int target)
	{
		id = string.Empty;
		target = 0;
		if (customData == null || !customData.TryGetValue("BestAutoSort.RestockId", out string value) || string.IsNullOrWhiteSpace(value) || !customData.TryGetValue("BestAutoSort.RestockTarget", out string value2) || !int.TryParse(value2, NumberStyles.Integer, CultureInfo.InvariantCulture, out target) || target <= 0)
		{
			target = 0;
			return false;
		}
		id = value;
		return true;
	}

	internal static void Set(IDictionary<string, string> customData, string id, int target)
	{
		customData["BestAutoSort.RestockId"] = id;
		customData["BestAutoSort.RestockTarget"] = target.ToString(CultureInfo.InvariantCulture);
	}

	internal static void Clear(IDictionary<string, string> customData)
	{
		customData.Remove("BestAutoSort.RestockId");
		customData.Remove("BestAutoSort.RestockTarget");
	}
}
