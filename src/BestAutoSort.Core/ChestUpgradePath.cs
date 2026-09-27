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

	/// <summary>
	/// Effective managed tier. An explicit marker (1..3, stamped by our own
	/// upgrade flows) always wins: the prefab never changes in place, so a
	/// marker necessarily describes this object. Unmarked (0/absent) chests
	/// resolve by prefab (naturally built reinforced/blackmetal/grausten are
	/// tiers 1/2/3, not -1). Unknown prefabs without a marker fail closed.
	/// </summary>
	internal static int ResolveManagedTier(string prefabName, int markerTier)
	{
		if (markerTier >= 1 && markerTier <= 3)
		{
			return markerTier;
		}
		if (string.Equals(prefabName, "piece_chest_wood", StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		if (string.Equals(prefabName, "piece_chest", StringComparison.OrdinalIgnoreCase))
		{
			return 1;
		}
		if (string.Equals(prefabName, "piece_chest_blackmetal", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		if (string.Equals(prefabName, "piece_chest_grausten", StringComparison.OrdinalIgnoreCase))
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

	internal static bool PrefabMatchesTier(string prefabName, int tier)
	{
		if (tier < 0 || tier > 3)
		{
			return false;
		}
	 try
		{
			return string.Equals(prefabName, PrefabForTier(tier), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
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
