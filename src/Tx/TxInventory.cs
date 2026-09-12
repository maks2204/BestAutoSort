using System.Collections.Generic;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Thin wrappers over vanilla Inventory with exact result accounting.
    /// Rules verified against the assembly_valheim decompile (Inventory.AddItem/RemoveItem/MoveItemToThis).
    /// </summary>
    internal static class TxInventory
    {
        /// <summary>
        /// Put a clone into the inventory. Returns the actually accepted amount.
        /// Rule: placed (Contains) → all; full-merge → all; otherwise requested minus remainder.
        /// The clone IS mutated by the call (like vanilla) — pass clones only.
        /// </summary>
        public static int AddAndCount(Inventory inv, ItemData clone)
        {
            if (inv == null || clone == null)
                return 0;
            int requested = clone.m_stack;
            if (requested <= 0)
                return 0;
            bool flag = inv.AddItem(clone);
            int remaining = inv.ContainsItem(clone) ? 0 : (flag ? 0 : clone.m_stack);
            return requested - remaining;
        }

        /// <summary>
        /// Positional placement (vanilla drag&drop): strictly into the requested cell,
        /// no global merge. Returns the accepted amount (remainder stays in clone.m_stack).
        /// </summary>
        public static int AddPositional(Inventory inv, ItemData clone, int amount, int x, int y)
        {
            if (inv == null || clone == null || amount <= 0)
                return 0;
            if (x < 0 || y < 0 || x >= inv.GetWidth() || y >= inv.GetHeight())
                return 0;
            int requested = amount;
            clone.m_stack = amount;
            TxReflect.AddItemAt(inv, clone, amount, x, y);
            int remaining = clone.m_stack;
            if (remaining < 0)
                remaining = 0;
            if (remaining > requested)
                remaining = requested;
            return requested - remaining;
        }

        /// <summary>
        /// Remove exactly n units of the resolved object. Returns what was removed.
        /// </summary>
        public static int RemoveExact(Inventory inv, ItemData live, int amount)
        {
            if (inv == null || live == null || amount <= 0)
                return 0;
            if (!inv.ContainsItem(live))
                return 0;
            int n = amount < live.m_stack ? amount : live.m_stack;
            if (!inv.RemoveItem(live, n))
                return 0;
            return n;
        }

        /// <summary>
        /// Remove n units by reference with a name/quality scan fallback.
        /// Returns what was actually removed (for Add completion on the client).
        /// </summary>
        public static int RemoveForTake(Inventory inv, ItemData itemRef, string name, int quality, int amount)
        {
            if (inv == null || amount <= 0)
                return 0;
            if (itemRef != null && inv.ContainsItem(itemRef))
                return RemoveExact(inv, itemRef, amount);
            int remaining = amount;
            foreach (ItemData it in new List<ItemData>(inv.GetAllItems()))
            {
                if (remaining <= 0)
                    break;
                if (it == null || it.m_shared == null)
                    continue;
                if (!string.Equals(it.m_shared.m_name, name, System.StringComparison.Ordinal))
                    continue;
                if (quality >= 0 && it.m_quality != quality)
                    continue;
                remaining -= RemoveExact(inv, it, remaining);
            }
            return amount - remaining;
        }

        /// <summary>
        /// Move within one inventory (drag&drop / rearrange).
        /// Mirrors vanilla InventoryGrid.DropItem: empty cell or same type → MoveItemToThis;
        /// foreign cell → swap only with a full stack. Returns the moved amount (0 = reject).
        /// </summary>
        public static int MoveWithin(Inventory inv, ItemData src, int amount, int dstX, int dstY)
        {
            if (inv == null || src == null || amount <= 0)
                return 0;
            if (!inv.ContainsItem(src))
                return 0;
            if (dstX < 0 || dstY < 0 || dstX >= inv.GetWidth() || dstY >= inv.GetHeight())
                return 0;
            int n = amount < src.m_stack ? amount : src.m_stack;
            if (n <= 0)
                return 0;
            ItemData dst = inv.GetItemAt(dstX, dstY);
            if (dst == src)
                return n;
            if (dst == null || (dst.m_shared != null && src.m_shared != null && dst.IsSameType(src)))
            {
                int before = src.m_stack;
                bool ok = inv.MoveItemToThis(inv, src, n, dstX, dstY);
                if (!ok)
                    return 0;
                // MoveItemToThis reduces src m_stack by the moved amount; removes empties itself.
                int moved = inv.ContainsItem(src) ? before - src.m_stack : before;
                return moved > 0 ? moved : (ok ? n : 0);
            }
            // Foreign cell: swap with a full stack only.
            if (n != src.m_stack)
                return 0;
            Vector2i tmp = src.m_gridPos;
            src.m_gridPos = dst.m_gridPos;
            dst.m_gridPos = tmp;
            if (inv.m_onChanged != null)
                inv.m_onChanged();
            return n;
        }
    }
}
