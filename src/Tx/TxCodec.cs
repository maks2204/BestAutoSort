using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Encoding of ChestTX request/response bodies into ZPackage.
    /// Request layout: [proto:int][txId:long][op:int][baseRev:uint][playerId:long][body]
    /// Response layout: [txId:long][status:int][revision:uint][totalsOnly:bool][body]
    /// Item layout: [prefabHash:int][itemPkg:ZPackage][amount:int][x:int][y:int][maxStack:int]
    /// Items are never held across RPCs: the snapshot is serialized immediately, and
    /// the manager re-resolves the live object (ResolveIn) by gridPos+type.
    /// </summary>
    internal static class TxCodec
    {
        public const int ProtoVersion = 1;

        public static void WriteHeader(ZPackage pkg, long txId, TxOp op, uint baseRev, long playerId)
        {
            pkg.Write(ProtoVersion);
            pkg.Write(txId);
            pkg.Write((int)op);
            pkg.Write(baseRev);
            pkg.Write(playerId);
        }

        public static bool ReadHeader(ZPackage pkg, out long txId, out TxOp op, out uint baseRev, out long playerId)
        {
            txId = 0L;
            op = 0;
            baseRev = 0u;
            playerId = 0L;
            try
            {
                if (pkg.ReadInt() != ProtoVersion)
                    return false;
                txId = pkg.ReadLong();
                op = (TxOp)pkg.ReadInt();
                baseRev = pkg.ReadUInt();
                playerId = pkg.ReadLong();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void WriteResponseHeader(ZPackage pkg, long txId, TxStatus status, uint revision, bool totalsOnly)
        {
            pkg.Write(txId);
            pkg.Write((int)status);
            pkg.Write(revision);
            pkg.Write(totalsOnly);
        }

        public static bool ReadResponseHeader(ZPackage pkg, out long txId, out TxStatus status, out uint revision, out bool totalsOnly)
        {
            txId = 0L;
            status = TxStatus.Rejected;
            revision = 0u;
            totalsOnly = false;
            try
            {
                txId = pkg.ReadLong();
                status = (TxStatus)pkg.ReadInt();
                revision = pkg.ReadUInt();
                totalsOnly = pkg.ReadBool();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Item snapshot for the wire: clone with the requested stack + prefab hash.
        /// Returns false when the item cannot be serialized (no prefab).
        /// </summary>
        public static bool WriteItem(ZPackage pkg, ItemData item, int amount)
        {
            if (item == null || item.m_shared == null || (Object)item.m_dropPrefab == null || amount <= 0)
                return false;
            ItemData clone = item.Clone();
            clone.m_stack = amount;
            ZPackage inner = new ZPackage();
            clone.Save(inner);
            pkg.Write(item.m_dropPrefab.name.GetStableHashCode());
            pkg.Write(inner);
            pkg.Write(amount);
            pkg.Write(clone.m_gridPos.x);
            pkg.Write(clone.m_gridPos.y);
            pkg.Write(item.m_shared.m_maxStackSize);
            return true;
        }

        public static bool ReadItem(ZPackage pkg, out int prefabHash, out ItemData item, out int amount, out int x, out int y, out int maxStack)
        {
            prefabHash = 0;
            item = null;
            amount = 0;
            x = -1;
            y = -1;
            maxStack = 50;
            try
            {
                prefabHash = pkg.ReadInt();
                ZPackage inner = pkg.ReadPackage();
                amount = pkg.ReadInt();
                x = pkg.ReadInt();
                y = pkg.ReadInt();
                maxStack = pkg.ReadInt();
                if (prefabHash == 0 || amount <= 0)
                    return false;
                item = ResolvePrefab(prefabHash, inner);
                return item != null;
            }
            catch (Exception)
            {
                item = null;
                return false;
            }
        }

        /// <summary>
        /// Rebuild a live ItemData from a snapshot: shared comes from the prefab by hash
        /// (like vanilla Inventory.Load), everything else from the bytes.
        /// </summary>
        public static ItemData ResolvePrefab(int prefabHash, ZPackage inner)
        {
            try
            {
                ObjectDB db = ObjectDB.instance;
                if ((Object)db == (Object)null)
                    return null;
                GameObject prefab = db.GetItemPrefab(prefabHash);
                if ((Object)prefab == (Object)null)
                    return null;
                ItemDrop drop = prefab.GetComponent<ItemDrop>();
                if ((Object)drop == (Object)null || drop.m_itemData == null || drop.m_itemData.m_shared == null)
                    return null;
                ItemData item = drop.m_itemData.Clone();
                ItemData.Load(inner, item, Version.Item.ChunksNCheats);
                item.m_dropPrefab = prefab;
                return item;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Re-resolve an item in the manager's CURRENT inventory.
        /// First the exact cell (gridPos + name + quality), then a scan
        /// (name + quality + variant + world level). Not found → null (stale → reject).
        /// </summary>
        public static ItemData ResolveIn(Inventory inv, string name, int quality, int variant, int world, int x, int y)
        {
            if (inv == null || string.IsNullOrEmpty(name))
                return null;
            if (x >= 0 && y >= 0)
            {
                ItemData at = inv.GetItemAt(x, y);
                if (at != null && at.m_shared != null
                    && string.Equals(at.m_shared.m_name, name, StringComparison.Ordinal)
                    && at.m_quality == quality)
                    return at;
            }
            foreach (ItemData it in inv.GetAllItems())
            {
                if (it == null || it.m_shared == null)
                    continue;
                if (string.Equals(it.m_shared.m_name, name, StringComparison.Ordinal)
                    && it.m_quality == quality && it.m_variant == variant && it.m_worldLevel == world)
                    return it;
            }
            return null;
        }

        /// <summary>
        /// All stacks of a key in grid order (for RespectReserves: pull from free ones).
        /// </summary>
        public static List<ItemData> ResolveAllIn(Inventory inv, string name, int quality, int variant, int world)
        {
            List<ItemData> res = new List<ItemData>();
            if (inv == null || string.IsNullOrEmpty(name))
                return res;
            foreach (ItemData it in inv.GetAllItems())
            {
                if (it == null || it.m_shared == null)
                    continue;
                if (string.Equals(it.m_shared.m_name, name, StringComparison.Ordinal)
                    && it.m_quality == quality && it.m_variant == variant && it.m_worldLevel == world)
                    res.Add(it);
            }
            return res;
        }

        public static string Describe(ItemData item)
        {
            if (item == null || item.m_shared == null)
                return "null";
            return item.m_shared.m_name + " x" + item.m_stack;
        }

        public static string Describe(string name, int amount)
        {
            return (name ?? "?") + " x" + amount;
        }

        public static List<int> ReadIntList(ZPackage pkg, int count)
        {
            List<int> res = new List<int>(count);
            for (int i = 0; i < count; i++)
                res.Add(pkg.ReadInt());
            return res;
        }

        /// <summary>
        /// Decode Take results for custom completions (automation).
        /// Returns null on a corrupt payload.
        /// </summary>
        public static List<DecodedTake> ReadTakeResults(ZPackage pkg)
        {
            try
            {
                int count = pkg.ReadInt();
                List<DecodedTake> res = new List<DecodedTake>(count);
                for (int i = 0; i < count; i++)
                {
                    int prefabHash = pkg.ReadInt();
                    ZPackage inner = pkg.ReadPackage();
                    int accepted = pkg.ReadInt();
                    DecodedTake entry = new DecodedTake();
                    entry.PrefabHash = prefabHash;
                    entry.Accepted = accepted;
                    if (accepted > 0)
                        entry.Item = ResolvePrefab(prefabHash, inner);
                    res.Add(entry);
                }
                return res;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
