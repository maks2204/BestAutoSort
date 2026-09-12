using System;
using System.Collections.Generic;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Model inventory with vanilla Inventory rules (for core tests):
    /// merge by key (hash+quality+variant+world), then new cells row by row,
    /// partial placement, Take across several cells, swap with full stack only.
    /// </summary>
    public sealed class ModelChest
    {
        public readonly int Width;
        public readonly int Height;

        private sealed class Cell
        {
            public ItemKey Key;
            public int Amount;
            public int MaxStack;
        }

        private readonly Dictionary<int, Cell> _cells = new Dictionary<int, Cell>();

        public ModelChest(int width, int height)
        {
            Width = width;
            Height = height;
        }

        private int Slot(int x, int y)
        {
            return y * Width + x;
        }

        private IEnumerable<int> OrderedSlots()
        {
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    yield return Slot(x, y);
        }

        public int TotalOf(ItemKey key)
        {
            int sum = 0;
            foreach (Cell c in _cells.Values)
                if (c.Key.Equals(key))
                    sum += c.Amount;
            return sum;
        }

        public int GrandTotal()
        {
            int sum = 0;
            foreach (Cell c in _cells.Values)
                sum += c.Amount;
            return sum;
        }

        public int CellCount()
        {
            return _cells.Count;
        }

        /// <summary>
        /// Analog of Inventory.AddItem: merge into existing stacks, then new cells.
        /// Returns the accepted amount (may be partial, 0 = no space).
        /// </summary>
        public int AddItem(ItemKey key, int amount, int maxStack, int wantX = -1, int wantY = -1)
        {
            if (amount <= 0)
                return 0;
            int remaining = amount;
            // 1. Merge into existing stacks of the same key (grid order).
            foreach (int s in OrderedSlots())
            {
                if (remaining <= 0)
                    break;
                Cell c;
                if (_cells.TryGetValue(s, out c) && c.Key.Equals(key) && c.Amount < maxStack)
                {
                    int room = maxStack - c.Amount;
                    int take = Math.Min(room, remaining);
                    c.Amount += take;
                    remaining -= take;
                }
            }
            // 2. New cells. A specifically requested cell goes first.
            if (remaining > 0 && wantX >= 0 && wantY >= 0
                && wantX < Width && wantY < Height && !_cells.ContainsKey(Slot(wantX, wantY)))
            {
                int put = Math.Min(maxStack, remaining);
                _cells[Slot(wantX, wantY)] = new Cell { Key = key, Amount = put, MaxStack = maxStack };
                remaining -= put;
            }
            foreach (int s in OrderedSlots())
            {
                if (remaining <= 0)
                    break;
                if (_cells.ContainsKey(s))
                    continue;
                int put = Math.Min(maxStack, remaining);
                _cells[s] = new Cell { Key = key, Amount = put, MaxStack = maxStack };
                remaining -= put;
            }
            return amount - remaining;
        }

        /// <summary>
        /// Analog of RemoveItem(item, amount): take up to amount units of the key. Returns what was taken.
        /// </summary>
        public int TakeItem(ItemKey key, int amount)
        {
            if (amount <= 0)
                return 0;
            int remaining = amount;
            List<int> empty = null;
            foreach (int s in OrderedSlots())
            {
                if (remaining <= 0)
                    break;
                Cell c;
                if (_cells.TryGetValue(s, out c) && c.Key.Equals(key))
                {
                    int take = Math.Min(c.Amount, remaining);
                    c.Amount -= take;
                    remaining -= take;
                    if (c.Amount == 0)
                    {
                        if (empty == null)
                            empty = new List<int>();
                        empty.Add(s);
                    }
                }
            }
            if (empty != null)
                for (int i = 0; i < empty.Count; i++)
                    _cells.Remove(empty[i]);
            return amount - remaining;
        }

        /// <summary>
        /// Move/swap inside the chest. Returns true when applied.
        /// </summary>
        public bool Move(ItemKey key, int srcX, int srcY, int dstX, int dstY, int amount, int maxStack)
        {
            if (dstX < 0 || dstY < 0 || dstX >= Width || dstY >= Height)
                return false;
            if (amount <= 0)
                return false;
            Cell src = null;
            int srcSlot = -1;
            if (srcX >= 0 && srcY >= 0)
            {
                _cells.TryGetValue(Slot(srcX, srcY), out src);
                if (src == null || !src.Key.Equals(key))
                    return false; // stale: cell changed — reject
                srcSlot = Slot(srcX, srcY);
            }
            else
            {
                foreach (int s in OrderedSlots())
                {
                    Cell c;
                    if (_cells.TryGetValue(s, out c) && c.Key.Equals(key))
                    {
                        src = c;
                        srcSlot = s;
                        break;
                    }
                }
                if (src == null)
                    return false;
            }
            int move = Math.Min(amount, src.Amount);
            if (move <= 0)
                return false;
            int dstSlot = Slot(dstX, dstY);
            if (dstSlot == srcSlot)
                return true; // noop
            Cell dst;
            if (!_cells.TryGetValue(dstSlot, out dst))
            {
                // Empty cell: partial move by splitting.
                src.Amount -= move;
                _cells[dstSlot] = new Cell { Key = key, Amount = move, MaxStack = maxStack };
                if (src.Amount == 0)
                    _cells.Remove(srcSlot);
                return true;
            }
            if (dst.Key.Equals(key))
            {
                int room = maxStack - dst.Amount;
                if (room <= 0)
                    return false;
                int take = Math.Min(room, move);
                dst.Amount += take;
                src.Amount -= take;
                if (src.Amount == 0)
                    _cells.Remove(srcSlot);
                return true;
            }
            // Foreign cell: swap with a full stack only (like vanilla DropItem).
            if (move != src.Amount)
                return false;
            _cells[dstSlot] = src;
            _cells[srcSlot] = dst;
            return true;
        }

        /// <summary>
        /// Take everything. Returns a list of (key, amount).
        /// </summary>
        public List<KeyValuePair<ItemKey, int>> TakeAll()
        {
            Dictionary<ItemKey, int> acc = new Dictionary<ItemKey, int>();
            foreach (Cell c in _cells.Values)
            {
                int cur;
                acc.TryGetValue(c.Key, out cur);
                acc[c.Key] = cur + c.Amount;
            }
            _cells.Clear();
            List<KeyValuePair<ItemKey, int>> res = new List<KeyValuePair<ItemKey, int>>(acc.Count);
            foreach (KeyValuePair<ItemKey, int> kv in acc)
                res.Add(kv);
            return res;
        }

        /// <summary>
        /// State snapshot for serialization (handoff): list of (slot, key, amount, maxStack).
        /// </summary>
        public List<int[]> Snapshot()
        {
            List<int[]> res = new List<int[]>();
            foreach (KeyValuePair<int, Cell> kv in _cells)
                res.Add(new int[] { kv.Key, kv.Value.Key.Hash, kv.Value.Key.Quality, kv.Value.Key.Variant, kv.Value.Key.World, kv.Value.Amount, kv.Value.MaxStack });
            return res;
        }

        public void Restore(List<int[]> snapshot)
        {
            _cells.Clear();
            foreach (int[] e in snapshot)
                _cells[e[0]] = new Cell
                {
                    Key = new ItemKey(e[1], e[2], e[3], e[4]),
                    Amount = e[5],
                    MaxStack = e[6]
                };
        }
    }
}
