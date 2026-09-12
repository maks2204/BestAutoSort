using System;

namespace BestAutoSort.Core;

internal static class ChestUpgradePath
{
	internal const int WoodenTier = 0;

	internal const int ReinforcedTier = 1;

	internal const int BlackMetalTier = 2;

	internal const int GraustenTier = 3;

	internal const string WoodenPrefab = "piece_chest_wood";

	internal const string ReinforcedPrefab = "piece_chest";

	internal const string BlackMetalPrefab = "piece_chest_blackmetal";

	internal const string GraustenPrefab = "piece_chest_grausten";

	internal static int ResolveManagedTier(string prefabName, int markerTier)
	{
		if (string.Equals(prefabName, "piece_chest_wood", StringComparison.OrdinalIgnoreCase))
		{
			if (markerTier < 0 || markerTier > 2)
			{
				return -1;
			}
			return markerTier;
		}
		if (markerTier == 1 && string.Equals(prefabName, "piece_chest", StringComparison.OrdinalIgnoreCase))
		{
			return 1;
		}
		if (markerTier == 2 && string.Equals(prefabName, "piece_chest_blackmetal", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		if (markerTier == 3 && string.Equals(prefabName, "piece_chest_grausten", StringComparison.OrdinalIgnoreCase))
		{
			return 3;
		}
		return -1;
	}

	internal static bool IsLegacy(string prefabName, int markerTier)
	{
		if (markerTier > 0 && markerTier <= 2)
		{
			return string.Equals(prefabName, "piece_chest_wood", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	internal static bool CanUpgrade(int currentTier, int targetTier)
	{
		if (currentTier >= 0 && currentTier < 3 && targetTier > currentTier)
		{
			return targetTier <= 3;
		}
		return false;
	}

	internal static string PrefabForTier(int tier)
	{
		return tier switch
		{
			0 => "piece_chest_wood", 
			1 => "piece_chest", 
			2 => "piece_chest_blackmetal", 
			3 => "piece_chest_grausten", 
			_ => throw new ArgumentOutOfRangeException("tier"), 
		};
	}

	internal static int[] ObsoleteLegacyTiers(int legacyTier)
	{
		return legacyTier switch
		{
			1 => new int[1], 
			2 => new int[2] { 0, 1 }, 
			_ => Array.Empty<int>(), 
		};
	}
}
