using System;

namespace BestAutoSort.Core;

internal static class AutoFeedPolicy
{
	internal static bool MatchesContainerPrefix(string? prefabName, string? configuredPrefix)
	{
		string text = configuredPrefix?.Trim() ?? string.Empty;
		if (text.Length == 0)
		{
			return true;
		}
		return (prefabName ?? string.Empty).StartsWith(text, StringComparison.OrdinalIgnoreCase);
	}

	internal static bool ShouldScan(float now, float nextScanAt)
	{
		return now >= nextScanAt;
	}
}
