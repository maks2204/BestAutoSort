using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

internal static class CraftingFromChestsContext
{
	private static readonly FieldInfo CraftRecipeField = AccessTools.Field(typeof(InventoryGui), "m_craftRecipe");

	internal static Player? Player { get; private set; }

	internal static Recipe? Recipe { get; private set; }

	internal static bool Active
	{
		get
		{
			if ((Object)(object)Player != (Object)null)
			{
				return (Object)(object)Recipe != (Object)null;
			}
			return false;
		}
	}

	internal static void Begin(InventoryGui gui, Player player)
	{
		Player = player;
		object value = CraftRecipeField.GetValue(gui);
		Recipe = (Recipe?)((value is Recipe) ? value : null);
	}

	internal static void End()
	{
		Player = null;
		Recipe = null;
	}
}
