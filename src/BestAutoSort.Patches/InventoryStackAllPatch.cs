using System;
using System.Collections.Generic;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Inventory), "StackAll")]
internal static class InventoryStackAllPatch
{
	private sealed class LockedItemState
	{
		internal Inventory Inventory;

		internal List<ItemData> Hidden = new List<ItemData>();

		internal bool Restored;
	}

	private static bool Prefix(Inventory __instance, Inventory fromInventory, ref int __result, out LockedItemState? __state)
	{
		__state = null;
		if (TransferContext.Active && (Object)(object)TransferContext.Container != (Object)null && TransferContext.Records != null && __instance == TransferContext.Container.GetInventory() && (Object)(object)Player.m_localPlayer != (Object)null && fromInventory == ((Humanoid)Player.m_localPlayer).GetInventory())
		{
			__result = QuickStackTransfer.MoveMatching(TransferContext.Container, fromInventory, TransferContext.Records);
			return false;
		}
		if ((Object)(object)Player.m_localPlayer != (Object)null && fromInventory == ((Humanoid)Player.m_localPlayer).GetInventory())
		{
			List<ItemData> list = ItemLockService.HideLockedItems(fromInventory);
			list.AddRange(RestockProfileService.HideTargets(fromInventory));
			if (list.Count > 0)
			{
				__state = new LockedItemState
				{
					Inventory = fromInventory,
					Hidden = list
				};
			}
		}
		return true;
	}

	private static void Postfix(LockedItemState? __state)
	{
		Restore(__state);
	}

	private static Exception? Finalizer(Exception? __exception, LockedItemState? __state)
	{
		Restore(__state);
		return __exception;
	}

	private static void Restore(LockedItemState? state)
	{
		if (state != null && !state.Restored)
		{
			state.Restored = true;
			ItemLockService.RestoreHiddenItems(state.Inventory, state.Hidden);
		}
	}
}
