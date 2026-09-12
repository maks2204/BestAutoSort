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

	private static readonly FieldInfo CraftUpgradeItemField = AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem");

	private static readonly FieldInfo MultiCraftingField = AccessTools.Field(typeof(InventoryGui), "m_multiCrafting");

	private static readonly FieldInfo MultiCraftAmountField = AccessTools.Field(typeof(InventoryGui), "m_multiCraftAmount");

	/// <summary>
	/// The craft the GUI is currently running (pressed, timer ticking, or executing):
	/// recipe + quality + multi, mirroring vanilla OnCraftPressed/DoCrafting math.
	/// </summary>
	internal static bool GetCraftState(InventoryGui gui, out Recipe? recipe, out int quality, out int multi)
	{
		recipe = null;
		quality = 1;
		multi = 1;
		if ((Object)(object)gui == (Object)null)
			return false;
		try
		{
			recipe = CraftRecipeField.GetValue(gui) as Recipe;
			if ((Object)(object)recipe == (Object)null)
				return false;
			ItemData upgradeItem = (CraftUpgradeItemField != null) ? (CraftUpgradeItemField.GetValue(gui) as ItemData) : null;
			quality = (upgradeItem == null) ? 1 : (upgradeItem.m_quality + 1);
			bool isMulti = MultiCraftingField != null && (bool)MultiCraftingField.GetValue(gui);
			if (isMulti && MultiCraftAmountField != null)
				multi = System.Math.Max(1, (int)MultiCraftAmountField.GetValue(gui));
			return true;
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
			return (Object)(object)lp.GetSelectedPiece() == (Object)(object)piece;
		}
		catch
		{
			return false;
		}
	}
}
