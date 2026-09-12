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

	/// <summary>
	/// The recipe currently selected in the crafting panel (or being crafted).
	/// Display-time requirement checks run for EVERY visible recipe: prefetching
	/// for all of them floods the player inventory. Only the selected recipe
	/// may pull missing mats ahead of the actual craft.
	/// </summary>
	internal static bool IsSelectedRecipe(Recipe? recipe)
	{
		if ((Object)(object)recipe == (Object)null)
			return false;
		if (Active && (Object)(object)Recipe == (Object)(object)recipe)
			return true;
		InventoryGui? gui = InventoryGui.instance;
		if ((Object)(object)gui == (Object)null)
			return false;
		try
		{
			object value = CraftRecipeField.GetValue(gui);
			return (Object)(object)(value as Recipe) == (Object)(object)recipe;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsSelectedPiece(Piece? piece)
	{
		if ((Object)(object)piece == (Object)null)
			return false;
		Player? lp = Player.m_localPlayer;
		if ((Object)(object)lp == (Object)null)
			return false;
		try
		{
			// Selection lives on the wielded hammer's PieceTable.
			ItemData? right = ((Humanoid)lp).GetCurrentWeapon();
			PieceTable? table = right?.m_shared?.m_buildPieces;
			if ((Object)(object)table == (Object)null)
				return false;
			return (Object)(object)table.GetSelectedPiece() == (Object)(object)piece;
		}
		catch
		{
			return false;
		}
	}
}
