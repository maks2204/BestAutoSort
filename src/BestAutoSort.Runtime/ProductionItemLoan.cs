using System.Collections.Generic;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal sealed class ProductionItemLoan
{
	private readonly Container _source;

	private readonly Player _player;

	private readonly ItemData _item;

	internal ProductionItemLoan(Container source, Player player, ItemData item)
	{
		_source = source;
		_player = player;
		_item = item;
	}

	internal void ReturnIfPresent()
	{
		if ((Object)(object)_player == (Object)null)
		{
			return;
		}
		Inventory inventory = ((Humanoid)_player).GetInventory();
		if (!inventory.GetAllItems().Contains(_item))
		{
			return;
		}
		// Wave-2: authority-routed. Remote managed chests return loans by tx.
		if ((Object)(object)_source != (Object)null && ChestTxService.IsShared(_source) && !ChestTxService.IsManager(_source))
		{
			// Return the loan remainder to a foreign chest via Add transaction (no ownership).
			TxOpItem op = ChestTxService.SnapshotAuto(_item, _item.m_stack);
			if (op != null)
			{
				TxOpCall call = new TxOpCall();
				call.Op = TxOp.Add;
				call.Items.Add(op);
				call.EnforceRule = false;
				Inventory srcInv = inventory;
				ItemData itemRef = _item;
				ChestTxService.SubmitCall(_source, call, srcInv, delegate (List<TransferRecord> records, TxCompletionKind disp)
					{
					NearbyResourceService.KeepLoanedItemSafe(_player, itemRef);
				});
				return;
			}
		}
		// Wave-2: manager-only direct return (remote managed chests use the tx
		// above); drain queued remote ops first.
		if ((Object)(object)_source != (Object)null && ChestTxService.IsManager(_source))
		{
			ChestTxService.DrainForLocal(_source);
			_source.GetInventory().MoveItemToThis(inventory, _item, _item.m_stack, -1, -1);
			// Owned chest mutated directly (no tx queue): persist like the manager.
			TxReflect.UpdateRows(_source);
			TxReflect.SaveContainer(_source);
			if (!inventory.GetAllItems().Contains(_item))
			{
				return;
			}
		}
		NearbyResourceService.KeepLoanedItemSafe(_player, _item);
	}
}
