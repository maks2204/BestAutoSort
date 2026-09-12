using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

internal static class CraftingFromChestsContext
{
	private static readonly FieldInfo CraftRecipeField = AccessTools.Field(typeof(InventoryGui), "m_craftRecipe");

	private static readonly FieldInfo SelectedRecipeField = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");

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
	/// Requirement checks run for EVERY visible recipe (list rendering) and every frame
	/// for the panel one: prefetching for all of them floods the player inventory.
	/// Only the selected recipe may pull missing mats ahead of the actual craft.
	/// Vanilla keeps the browsed choice in m_selectedRecipe (RecipeDataPair), NOT in
	/// m_craftRecipe — the latter is assigned only in OnCraftPressed.
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
			if ((Object)(object)(value as Recipe) == (Object)(object)recipe)
				return true;
		}
		catch
		{
		}
		return (Object)(object)GetBrowsedRecipe(gui) == (Object)(object)recipe;
	}

	private static PropertyInfo? _browsedRecipeProperty;

	private static bool _browsedRecipeLookupFailed;

	private static Recipe? GetBrowsedRecipe(InventoryGui gui)
	{
		if (_browsedRecipeLookupFailed)
			return null;
		try
		{
			// m_selectedRecipe is a private nested struct (RecipeDataPair):
			// box it, then read its Recipe property.
			object boxed = SelectedRecipeField.GetValue(gui);
			if (boxed == null)
				return null;
			if (_browsedRecipeProperty == null)
			{
				_browsedRecipeProperty = boxed.GetType().GetProperty("Recipe",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (_browsedRecipeProperty == null)
				{
					_browsedRecipeLookupFailed = true;
					return null;
				}
			}
			return _browsedRecipeProperty.GetValue(boxed) as Recipe;
		}
		catch
		{
			_browsedRecipeLookupFailed = true;
			return null;
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
			return (Object)(object)lp.GetSelectedPiece() == (Object)(object)piece;
		}
		catch
		{
			return false;
		}
	}
}
