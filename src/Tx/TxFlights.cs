using System;
using System.Collections.Generic;
using BestAutoSort.Runtime;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Broadcast of quick-stack flight visuals so every nearby player sees them,
    /// not just the sorter. The manager sends one packet per committed AddBatch;
    /// receivers skip their own tx (matched by originator session in the txId).
    /// Order-safe in both directions: no extra state, no double play.
    /// </summary>
    internal static class TxFlights
    {
        internal const string FlightsRpc = "BestAutoSort_TxFlights";
        private const int MaxEntries = 24;

        internal static void BroadcastFlights(ChestState state, TxJob job, StoredResult result)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(state.Container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return;
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (rpc == null)
                    return;
                Vector3 from = ResolveSourcePos(state, job);
                ZPackage pkg = new ZPackage();
                pkg.Write(job.TxId);
                pkg.Write(netView.GetZDO().m_uid);
                pkg.Write(job.Call.Op == TxOp.TakeBatch);
                pkg.Write(from);
                int count = 0;
                for (int i = 0; i < job.Call.Items.Count && i < result.Accepted.Count; i++)
                {
                    if (result.Accepted[i] > 0 && count < MaxEntries)
                        count++;
                }
                pkg.Write(count);
                int written = 0;
                for (int i = 0; i < job.Call.Items.Count && i < result.Accepted.Count && written < MaxEntries; i++)
                {
                    if (result.Accepted[i] <= 0)
                        continue;
                    pkg.Write(job.Call.Items[i].PrefabHash);
                    pkg.Write(result.Accepted[i]);
                    written++;
                }
                rpc.InvokeRoutedRPC(FlightsRpc, pkg);
                TxLog.Info("container=" + TxLog.Zid(netView.GetZDO().m_uid) + " tx=" + job.TxId + " flights broadcast entries=" + written);
            }
            catch (Exception ex)
            {
                TxLog.Warn("flights broadcast failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Проиграть чужой батч у себя на менеджере (броадкаст себя не покрывает).
        /// </summary>
        internal static void PlayLocalBatch(ChestState state, TxJob job, StoredResult result)
        {
            try
            {
                if ((Object)state.Container == (Object)null)
                    return;
                List<TransferRecord> records = new List<TransferRecord>();
                for (int i = 0; i < job.Call.Items.Count && i < result.Accepted.Count; i++)
                {
                    if (result.Accepted[i] <= 0)
                        continue;
                    TxOpItem item = job.Call.Items[i];
                    if (item.Snapshot == null || item.Snapshot.m_shared == null)
                        continue;
                    records.Add(new TransferRecord(item.Snapshot.m_shared.m_name, item.Snapshot.GetIcon(), result.Accepted[i], item.Snapshot.m_shared.m_maxStackSize));
                }
                if (records.Count == 0)
                    return;
                bool toPlayer = job.Call.Op == TxOp.TakeBatch;
                TransferVisuals.PlayFrom(records, state.Container, toPlayer, ResolveSourcePos(state, job));
            }
            catch (Exception ex)
            {
                TxLog.Warn("local batch visuals failed: " + ex.Message);
            }
        }

        private static Vector3 ResolveSourcePos(ChestState state, TxJob job)
        {
            try
            {
                long playerId;
                Vector3 position;
                if (ChestAuthority.ResolveActor(job.Sender, out playerId, out position))
                    return position;
            }
            catch (Exception)
            {
            }
            Player player = Player.m_localPlayer;
            if ((Object)player != (Object)null)
                return ((Component)player).transform.position;
            return ((Component)state.Container).transform.position;
        }

        internal static void OnFlightPacket(long sender, ZPackage pkg)
        {
            if (!Plugin.IsActive)
                return;
            try
            {
                long txId = pkg.ReadLong();
                // Own tx (matched by originator session in the high bits):
                // local visuals already played or playing — skip to avoid doubles.
                if (TxIdGen.PeerOf(txId) == ZNet.GetUID())
                    return;
                ZDOID zdoid = pkg.ReadZDOID();
                bool toPlayer = pkg.ReadBool();
                Vector3 from = pkg.ReadVector3();
                int count = pkg.ReadInt();
                if (count < 0 || count > MaxEntries)
                    return;
                ZNetScene scene = ZNetScene.instance;
                if ((Object)scene == (Object)null)
                    return;
                GameObject go = scene.FindInstance(zdoid);
                if ((Object)go == (Object)null)
                    return;
                Container container = go.GetComponent<Container>();
                if ((Object)container == (Object)null)
                    return;
                List<TransferRecord> records = new List<TransferRecord>(count);
                for (int i = 0; i < count; i++)
                {
                    int hash = pkg.ReadInt();
                    int amount = pkg.ReadInt();
                    if (hash == 0 || amount <= 0)
                        continue;
                    TransferRecord record = BuildRecord(hash, amount);
                    if (record != null)
                        records.Add(record);
                }
                if (records.Count > 0)
                    TransferVisuals.PlayFrom(records, container, toPlayer, from);
            }
            catch (Exception ex)
            {
                TxLog.Warn("flights packet ignored: " + ex.Message);
            }
        }

        private static TransferRecord BuildRecord(int prefabHash, int amount)
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
                return new TransferRecord(drop.m_itemData.m_shared.m_name, drop.m_itemData.GetIcon(), amount, drop.m_itemData.m_shared.m_maxStackSize);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
