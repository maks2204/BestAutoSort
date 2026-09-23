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
		if ((Object)(object)_source != (Object)null && ChestTxService.IsShared(_source) && !_source.IsOwner())
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
		if ((Object)(object)_source != (Object)null && _source.IsOwner())
		{
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
