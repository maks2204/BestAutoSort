using UnityEngine;

namespace BestAutoSort.Patches;

internal static class BuildingFromChestsContext
{
	internal static Player? Player { get; private set; }

	internal static bool Active => (Object)(object)Player != (Object)null;

	internal static void Begin(Player player)
	{
		Player = player;
	}

	internal static void End()
	{
		Player = null;
	}
}
