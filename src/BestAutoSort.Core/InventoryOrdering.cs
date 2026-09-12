using System;
using System.Collections.Generic;
using System.Linq;

namespace BestAutoSort.Core;

public static class InventoryOrdering
{
	private sealed class SortableItemComparer : IComparer<SortableItem>
	{
		private readonly SortMode _mode;

		private readonly int _direction;

		public SortableItemComparer(SortMode mode, bool descending)
		{
			_mode = mode;
			_direction = ((!descending) ? 1 : (-1));
		}

		public int Compare(SortableItem? left, SortableItem? right)
		{
			if (left == right)
			{
				return 0;
			}
			if (left == null)
			{
				return -1;
			}
			if (right == null)
			{
				return 1;
			}
			int num = ComparePrimary(left, right) * _direction;
			if (num != 0)
			{
				return num;
			}
			int num2 = StringComparer.OrdinalIgnoreCase.Compare(left.InternalName, right.InternalName);
			if (num2 != 0)
			{
				return num2;
			}
			int num3 = right.Quality.CompareTo(left.Quality);
			if (num3 != 0)
			{
				return num3;
			}
			int num4 = right.Stack.CompareTo(left.Stack);
			if (num4 == 0)
			{
				return left.OriginalIndex.CompareTo(right.OriginalIndex);
			}
			return num4;
		}

		private int ComparePrimary(SortableItem left, SortableItem right)
		{
			switch (_mode)
			{
			case SortMode.Category:
			{
				int num = left.Category.CompareTo(right.Category);
				if (num == 0)
				{
					return StringComparer.CurrentCultureIgnoreCase.Compare(left.LocalizedName, right.LocalizedName);
				}
				return num;
			}
			case SortMode.Name:
				return StringComparer.CurrentCultureIgnoreCase.Compare(left.LocalizedName, right.LocalizedName);
			case SortMode.Weight:
				return left.Weight.CompareTo(right.Weight);
			case SortMode.Value:
				return left.Value.CompareTo(right.Value);
			case SortMode.Quality:
				return left.Quality.CompareTo(right.Quality);
			default:
				return 0;
			}
		}
	}

	public static IReadOnlyList<int> OrderIndices(IReadOnlyList<SortableItem> items, SortMode mode, bool descending)
	{
		if (items == null)
		{
			throw new ArgumentNullException("items");
		}
		return (from item in items.OrderBy<SortableItem, SortableItem>((SortableItem item) => item, new SortableItemComparer(mode, @descending))
			select item.OriginalIndex).ToArray();
	}
}
