using UnityEngine;

namespace BestAutoSort.Runtime;

internal sealed class TransferRecord
{
	internal string ItemName { get; }

	internal Sprite? Icon { get; }

	internal int Amount { get; }

	internal int MaxStackSize { get; }

	internal TransferRecord(string itemName, Sprite? icon, int amount, int maxStackSize)
	{
		ItemName = itemName;
		Icon = icon;
		Amount = amount;
		MaxStackSize = maxStackSize;
	}
}
