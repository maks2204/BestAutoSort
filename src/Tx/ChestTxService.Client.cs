using System;
using System.Collections.Generic;
using BestAutoSort.Core;
using BestAutoSort.Runtime;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// ChestTX client side: responses, timeouts/retries/Query, completions,
    /// viewer refresh, presence, takeover, main Pump.
    /// </summary>
    internal static partial class ChestTxService
    {
        // ============================ responses ============================

        private static void Respond(Container container, long peer, long txId, TxStatus status, uint revision, ZPackage body, bool totalsOnly, TxOp op)
        {
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            ZPackage pkg = new ZPackage();
            TxCodec.WriteResponseHeader(pkg, txId, status, revision, totalsOnly);
            pkg.Write(body);
            try
            {
                netView.InvokeRPC(peer, TxNet.TxResponseRpc, pkg);
            }
            catch (Exception ex)
            {
                TxLog.Warn("tx=" + txId + " respond failed: " + ex.Message);
            }
        }

        private static ZPackage EncodeResultBody(TxOpCall call, StoredResult r)
        {
            ZPackage body = new ZPackage();
            switch (call.Op)
            {
                case TxOp.Add:
                case TxOp.AddBatch:
                case TxOp.Move:
                case TxOp.Sort:
                case TxOp.Upgrade:
                case TxOp.SetRule:
                    body.Write(r.Accepted.Count);
                    for (int i = 0; i < r.Accepted.Count; i++)
                        body.Write(r.Accepted[i]);
                    break;
                case TxOp.Take:
                case TxOp.TakeBatch:
                    body.Write(r.Takes.Count);
                    for (int i = 0; i < r.Takes.Count; i++)
                    {
                        TakeEntry e = r.Takes[i];
                        if (e == null)
                        {
                            body.Write(0);
                            body.Write(new ZPackage());
                            body.Write(0);
                        }
                        else
                        {
                            ZPackage inner = new ZPackage();
                            e.Item.Save(inner);
                            body.Write(e.PrefabHash);
                            body.Write(inner);
                            body.Write(e.Accepted);
                        }
                    }
                    break;
                default:
                    break;
            }
            return body;
        }

        private static void RespondQuery(Container container, long sender, long txId)
        {
            ChestState state = GetState(container);
            if (state == null)
                return;
            StoredResult cached;
            if (state.Processed.TryGetValue(txId, out cached))
            {
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + txId + " QUERY hit");
                Respond(container, sender, txId, TxStatus.Duplicate, cached.Revision,
                    EncodeCachedBody(cached), cached.TotalsOnly, cached.Op);
                return;
            }
            Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, TxOp.Query);
        }

        private static ZPackage EncodeCachedBody(StoredResult cached)
        {
            ZPackage body = new ZPackage();
            if (cached.TotalsOnly)
            {
                // Same shape as a normal result: Add*/others — [1][total];
                // Take* — [0] (no items, see OnTxResponse).
                if (cached.Op == TxOp.Take || cached.Op == TxOp.TakeBatch)
                {
                    body.Write(0);
                    return body;
                }
                body.Write(1);
                body.Write(cached.AcceptedTotal());
                return body;
            }
            TxOpCall fake = new TxOpCall();
            fake.Op = cached.Op;
            return EncodeResultBody(fake, cached);
        }

        // ============================ client: responses ============================

        private static void OnTxResponse(Container container, long sender, ZPackage pkg)
        {
            if (!Plugin.IsActive)
                return;
            long txId;
            TxStatus status;
            uint revision;
            bool totalsOnly;
            if (!TxCodec.ReadResponseHeader(pkg, out txId, out status, out revision, out totalsOnly))
            {
                TxLog.Warn("bad response packet from " + sender);
                return;
            }
            PendingTx pending;
            if (!Pending.TryGetValue(txId, out pending))
            {
                // Late duplicate after completion: refresh only, do NOT touch items.
                RefreshNow(container);
                TxLog.Info("tx=" + txId + " late-duplicate ignored (already completed)");
                return;
            }
            Pending.Remove(txId);
            ReleaseClaimed(pending.Claimed);
            TxLog.Info("tx=" + txId + " response status=" + status + " rev=" + revision + (totalsOnly ? " totals-only" : ""));
            ZPackage body;
            try
            {
                body = pkg.ReadPackage();
            }
            catch (Exception ex)
            {
                TxLog.Warn("tx=" + txId + " response body unreadable: " + ex.Message);
                RefreshNow(pending.Container);
                return;
            }
            pkg = body;
            if (totalsOnly && IsTakeOp(pending.Op))
            {
                // Totals-only Take results after handoff carry no items: nothing to complete with.
                TellPlayer("Could not confirm chest contents after reconnect. Check the chest.");
                RefreshNow(pending.Container);
                return;
            }
            try
            {
                if (pending.OnResponse != null)
                    pending.OnResponse(pkg, status, revision);
            }
            catch (Exception ex)
            {
                TxLog.Error("tx=" + txId + " completion failed: " + ex.Message);
            }
            RefreshNow(container);
        }

        // ============================ client: completions ============================

        private static int ReadAcceptedAt(ZPackage pkg, int index)
        {
            try
            {
                if (pkg == null)
                    return 0;
                int count = pkg.ReadInt();
                int val = 0;
                for (int i = 0; i < count; i++)
                {
                    int a = pkg.ReadInt();
                    if (i == index)
                        val = a;
                }
                return val;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static List<int> ReadAcceptedList(ZPackage pkg, int count)
        {
            List<int> res = new List<int>();
            try
            {
                if (pkg == null)
                    return res;
                int n = pkg.ReadInt();
                for (int i = 0; i < n && i < count; i++)
                    res.Add(pkg.ReadInt());
            }
            catch (Exception)
            {
            }
            while (res.Count < count)
                res.Add(0);
            return res;
        }

        private static void CompleteAdd(Inventory srcInv, ItemData itemRef, TxOpItem sent, int accepted, TxStatus status, uint rev, Container container, Action<ZPackage, TxStatus, uint> onDone)
        {
            CompleteAdd(srcInv, itemRef, sent, accepted, status, rev, container, onDone, false);
        }

        private static void LogNonMainInventory(string op, Inventory inv)
        {
            try
            {
                Player lp = Player.m_localPlayer;
                if (lp != null && inv != null && !object.ReferenceEquals(inv, ((Humanoid)lp).GetInventory()))
                    TxLog.Info(op + " uses non-main inventory (dedicated slots?)");
            }
            catch
            {
            }
        }

        private static void CompleteAdd(Inventory srcInv, ItemData itemRef, TxOpItem sent, int accepted, TxStatus status, uint rev, Container container, Action<ZPackage, TxStatus, uint> onDone, bool alreadyRemoved)
        {
            LogNonMainInventory("add", srcInv);
            if (alreadyRemoved)
            {
                // Drag-deposit: the stack already left the inventory when the drag started
                // (vanilla), and itemRef is the abandoned drag object holding the full
                // dragged amount. Never remove again — restore whatever was not committed.
                RestoreDragRemainder(srcInv, itemRef, accepted);
                if (status == TxStatus.Rejected || status == TxStatus.UnknownTx)
                    TellPlayer("The shared chest refused the move. Try again.");
                if (onDone != null)
                    onDone(null, status, rev);
                return;
            }
            if (accepted > 0 && srcInv != null && sent != null)
            {
                string name = sent.Snapshot != null && sent.Snapshot.m_shared != null ? sent.Snapshot.m_shared.m_name : null;
                int quality = sent.Snapshot != null ? sent.Snapshot.m_quality : -1;
                int removed = TxInventory.RemoveForTake(srcInv, itemRef, name, quality, accepted);
                TxLog.Info("add-remove accepted=" + accepted + " removed=" + removed);
                if (removed < accepted)
                {
                    // Source changed mid-RTT: send the excess back to the chest as compensation.
                    int excess = accepted - removed;
                    TxLog.Warn("tx short-removal: accepted=" + accepted + " removed=" + removed + " compensating " + excess);
                    CompensateAddBack(container, sent, excess);
                }
            }
            if (status == TxStatus.Rejected || status == TxStatus.UnknownTx)
                TellPlayer("The shared chest refused the move. Try again.");
            if (onDone != null)
                onDone(null, status, rev);
        }

        private static void RestoreDragRemainder(Inventory srcInv, ItemData itemRef, int accepted)
        {
            if (srcInv == null || itemRef == null)
                return;
            CustomDataTags.StripBenign(itemRef);
            TxLog.Info("drag-restore stack=" + itemRef.m_stack + " accepted=" + accepted);
            int restore = itemRef.m_stack - accepted;
            if (restore <= 0)
                return;
            itemRef.m_stack = restore;
            if (srcInv.AddItem(itemRef))
            {
                srcInv.m_onChanged?.Invoke();
                return;
            }
            // Inventory full: drop at feet rather than lose the items.
            Player player = Player.m_localPlayer;
            if (player != null)
                ((Humanoid)player).DropItem(srcInv, itemRef, restore);
            else
                TxLog.Error("drag restore failed: no player, items lost: " + restore);
        }

        private static void CompensateAddBack(Container container, TxOpItem sent, int amount)
        {
            if (amount <= 0 || sent == null || sent.Snapshot == null)
                return;
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null)
                return;
            Inventory playerInv = ((Humanoid)player).GetInventory();
            if (playerInv == null)
                return;
            // Find the excess in the player inventory (it is there since removal fell short) and send it back.
            ItemData live = TxCodec.ResolveIn(playerInv, sent.Snapshot.m_shared.m_name,
                sent.Snapshot.m_quality, sent.Snapshot.m_variant, sent.Snapshot.m_worldLevel, -1, -1);
            if (live == null)
            {
                TxLog.Error("compensation failed: item not found in player inventory");
                return;
            }
            TxOpItem back = SnapshotItem(live, Math.Min(amount, live.m_stack), -1, -1);
            if (back == null)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Add;
            call.Items.Add(back);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                int acc = ReadAcceptedAt(pkg, 0);
                CompleteAdd(playerInv, live, back, acc, status, rev, container, null);
            });
        }

        private static void CompleteTake(Inventory dstInv, ZPackage pkg, TxStatus status, uint rev, Container container, Action<ZPackage, TxStatus, uint> onDone)
        {
            LogNonMainInventory("take", dstInv);
            if ((status == TxStatus.Accepted || status == TxStatus.Partial || status == TxStatus.Duplicate) && dstInv != null && pkg != null)
            {
                try
                {
                    int count = pkg.ReadInt();
                    for (int i = 0; i < count; i++)
                    {
                        int prefabHash = pkg.ReadInt();
                        ZPackage inner = pkg.ReadPackage();
                        int accepted = pkg.ReadInt();
                        if (accepted <= 0)
                            continue;
                        ItemData item = TxCodec.ResolvePrefab(prefabHash, inner);
                        CustomDataTags.StripBenign(item);
                        if (item == null)
                        {
                            TxLog.Error("take completion: prefab " + prefabHash + " missing, compensating " + accepted);
                            CompensateTakeBack(container, prefabHash, inner, accepted);
                            continue;
                        }
                        int placed = TxInventory.AddAndCount(dstInv, item);
                        if (placed < accepted)
                        {
                            // Did not fit the inventory: send the remainder back to the chest.
                            int leftover = accepted - placed;
                            TxLog.Warn("take completion: player full, compensating " + leftover);
                            ItemData back = item.Clone();
                            back.m_stack = leftover;
                            CompensateTakeBackItem(container, prefabHash, back);
                        }
                    }
                }
                catch (Exception ex)
                {
                    TxLog.Error("take completion decode failed: " + ex.Message);
                }
            }
            if (status == TxStatus.Rejected || status == TxStatus.UnknownTx)
                TellPlayer("The shared chest changed. Try again.");
            if (onDone != null)
                onDone(pkg, status, rev);
        }

        private static void CompensateTakeBack(Container container, int prefabHash, ZPackage inner, int amount)
        {
            ItemData item = TxCodec.ResolvePrefab(prefabHash, inner);
            if (item == null)
            {
                TxLog.Error("compensation failed: prefab missing");
                return;
            }
            CompensateTakeBackItem(container, prefabHash, item);
        }

        internal static void CompensateTakeBackItem(Container container, int prefabHash, ItemData item)
        {
            TxOpItem back = SnapshotItem(item, item.m_stack, -1, -1);
            if (back == null)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Add;
            call.Items.Add(back);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                TxLog.Info("compensate-add status=" + status + " accepted=" + ReadAcceptedAt(pkg, 0));
                RefreshNow(container);
            });
        }

        // ============================ client: timeouts / retry / query ============================

        /// <summary>
        /// Take for prefetch/automation: received items land in dstInv (CompleteTake),
        /// shortfall/failure handled via compensation and refresh inside.
        /// </summary>
        internal static void SubmitTakePrefetch(Container container, TxOpCall call, Inventory dstInv)
        {
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                CompleteTake(dstInv, pkg, status, rev, container, null);
            });
        }

        /// <summary>
        /// Take request with custom completion for automation (production, feeding, restock).
        /// Received items are NOT placed anywhere automatically — onDone decides.
        /// </summary>
        internal static void RequestTakeCustom(Container container, List<TxOpItem> items, bool respectReserves, Action<List<DecodedTake>, TxStatus, uint> onDone)
        {
            if (items == null || items.Count == 0)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.TakeBatch;
            call.Items.AddRange(items);
            call.RespectReserves = respectReserves;
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                List<DecodedTake> decoded = TxCodec.ReadTakeResults(pkg);
                if (decoded == null)
                {
                    TxLog.Warn("take-custom completion decode failed");
                    if (onDone != null)
                        onDone(new List<DecodedTake>(), TxStatus.Rejected, rev);
                    return;
                }
                if (onDone != null)
                    onDone(decoded, status, rev);
            });
        }

        /// <summary>
        /// Universal AddBatch submit (quick-stack/automation): owner enqueues locally,
        /// otherwise over the network. On result removes accepted from srcInv and builds TransferRecords.
        /// </summary>
        internal static void SubmitCall(Container container, TxOpCall call, Inventory srcInv, Action<List<TransferRecord>> onDone)
        {
            if (container.IsOwner())
            {
                MutateLocal(container, call, delegate (StoredResult r)
                {
                    List<TransferRecord> records = FinishBatchCompletion(container, srcInv, call, r);
                    if (onDone != null)
                        onDone(records);
                });
                return;
            }
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                List<int> accepted = ReadAcceptedList(pkg, call.Items.Count);
                List<TransferRecord> records = new List<TransferRecord>();
                for (int i = 0; i < call.Items.Count && i < accepted.Count; i++)
                {
                    TxOpItem sent = call.Items[i];
                    CompleteAdd(srcInv, sent.SourceRef, sent, accepted[i], status, rev, container, null);
                    if (accepted[i] > 0 && sent.Snapshot != null && sent.Snapshot.m_shared != null)
                        records.Add(new TransferRecord(sent.Snapshot.m_shared.m_name, sent.Snapshot.GetIcon(), accepted[i], sent.Snapshot.m_shared.m_maxStackSize));
                }
                RefreshNow(container);
                if (onDone != null)
                    onDone(records);
            });
        }

        private static List<TransferRecord> FinishBatchCompletion(Container container, Inventory srcInv, TxOpCall call, StoredResult r)
        {
            List<TransferRecord> records = new List<TransferRecord>();
            for (int i = 0; i < call.Items.Count && i < r.Accepted.Count; i++)
            {
                TxOpItem sent = call.Items[i];
                int accepted = r.Accepted[i];
                if (accepted > 0 && srcInv != null)
                {
                    string name = sent.Snapshot != null && sent.Snapshot.m_shared != null ? sent.Snapshot.m_shared.m_name : null;
                    int quality = sent.Snapshot != null ? sent.Snapshot.m_quality : -1;
                    int removed = TxInventory.RemoveForTake(srcInv, sent.SourceRef, name, quality, accepted);
                    if (removed < accepted)
                    {
                        int excess = accepted - removed;
                        TxLog.Warn("local batch short-removal: compensating " + excess);
                        CompensateAddBack(container, sent, excess);
                    }
                    if (sent.Snapshot != null && sent.Snapshot.m_shared != null)
                        records.Add(new TransferRecord(sent.Snapshot.m_shared.m_name, sent.Snapshot.GetIcon(), accepted, sent.Snapshot.m_shared.m_maxStackSize));
                }
            }
            RefreshNow(container);
            return records;
        }

        private static void PumpPending()
        {
            if (Pending.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            List<long> done = null;
            foreach (KeyValuePair<long, PendingTx> kv in Pending)
            {
                PendingTx p = kv.Value;
                if ((Object)p.Container == (Object)null)
                {
                    if (done == null)
                        done = new List<long>();
                    done.Add(kv.Key);
                    ReleaseClaimed(p.Claimed);
                    continue;
                }
                if (now >= p.Deadline)
                {
                    if (done == null)
                        done = new List<long>();
                    done.Add(kv.Key);
                    ReleaseClaimed(p.Claimed);
                    RefreshNow(p.Container);
                    TellPlayer("Shared chest request timed out. Try again.");
                    TxLog.Warn("tx=" + p.TxId + " TIMEOUT op=" + p.Op);
                    try
                    {
                        if (p.OnResponse != null)
                            p.OnResponse(null, TxStatus.UnknownTx, 0u);
                    }
                    catch (Exception ex)
                    {
                        TxLog.Error("tx=" + p.TxId + " timeout completion failed: " + ex.Message);
                    }
                    continue;
                }
                if (now < p.NextTryAt)
                    continue;
                if (p.Attempts < PayloadAttempts + (p.QuerySent ? 0 : QueryAttempts))
                {
                    if (!p.QuerySent && p.Attempts >= PayloadAttempts)
                    {
                        p.QuerySent = true;
                        SendQuery(p);
                    }
                    else
                    {
                        SendPayload(p.Container, p.Payload);
                    }
                    p.Attempts++;
                    p.NextTryAt = now + RequestTimeout;
                }
            }
            if (done != null)
                for (int i = 0; i < done.Count; i++)
                    Pending.Remove(done[i]);
        }

        private static void SendQuery(PendingTx p)
        {
            // Query: same txId, empty body. The manager returns the cache without re-applying.
            ZPackage body = new ZPackage();
            body.Write(TxCodec.ProtoVersion);
            body.Write((int)TxOp.Query);
            body.Write(0u);
            body.Write(LocalPlayerId());
            body.Write(LocalActorPos());
            body.Write(false);
            body.Write(false);
            ZPackage request = new ZPackage();
            request.Write(p.TxId);
            request.Write(body);
            TxLog.Info("tx=" + p.TxId + " QUERY (response was lost)");
            SendPayload(p.Container, request);
        }

        // ============================ viewer refresh / presence / takeover ============================

        internal static void RefreshNow(Container container)
        {
            ChestState state = GetState(container);
            if (state == null)
                return;
            state.SeenRev = uint.MaxValue;
            state.SeenBytes = null;
        }

        private static void PumpViewerRefresh()
        {
            if (Time.realtimeSinceStartup < _nextRefreshPoll)
                return;
            _nextRefreshPoll = Time.realtimeSinceStartup + RefreshPoll;
            InventoryGui gui = InventoryGui.instance;
            Container open = InventoryAccess.CurrentContainer(gui);
            int openId = (Object)open != (Object)null ? ((Object)open).GetInstanceID() : 0;
            if (openId != _openInstanceId)
            {
                ChestState prev = null;
                if (_openInstanceId != 0)
                    States.TryGetValue(_openInstanceId, out prev);
                if (prev != null && (Object)prev.Container != (Object)null)
                    SendPresence(prev.Container, TxOp.ViewerClose);
                _openInstanceId = openId;
                if (open != null)
                {
                    RefreshNow(open);
                    SendPresence(open, TxOp.ViewerOpen);
                    _nextPresenceAt = Time.realtimeSinceStartup + PresenceHeartbeat;
                }
            }
            if ((Object)open == (Object)null || open.IsOwner())
                return;
            if (!IsShared(open))
                return;
            if (Time.realtimeSinceStartup >= _nextPresenceAt)
            {
                SendPresence(open, TxOp.ViewerOpen);
                _nextPresenceAt = Time.realtimeSinceStartup + PresenceHeartbeat;
            }
            ChestState state = GetState(open);
            if (state == null)
                return;
            ZNetView netView = TxReflect.GetNetView(open);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            uint rev = netView.GetZDO().DataRevision;
            if (rev == state.SeenRev)
                return;
            byte[] bytes;
            try
            {
                bytes = netView.GetZDO().GetByteArray(ZDOVars.s_items);
            }
            catch (Exception)
            {
                return;
            }
            if (SameBytes(bytes, state.SeenBytes))
            {
                state.SeenRev = rev;
                return;
            }
            // Content changed: reload the local copy, do NOT close the GUI.
            if (gui != null)
                TxReflect.CancelDragFrom(gui, open.GetInventory());
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " viewer dims before=" + open.GetInventory().GetWidth() + "x" + open.GetInventory().GetHeight());
            try
            {
                ZPackage pkg = bytes != null ? new ZPackage(bytes) : EmptyInventoryPackage(open);
                open.GetInventory().Load(pkg);
                TxReflect.SetLastRevision(open, rev);
                TxReflect.UpdateRows(open);
            }
            catch (Exception ex)
            {
                TxLog.Warn("viewer refresh failed: " + ex.Message);
                return;
            }
            state.SeenRev = rev;
            state.SeenBytes = bytes;
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " viewer refresh rev=" + rev);
        }

        private static ZPackage EmptyInventoryPackage(Container container)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(109);
            pkg.Write((ushort)0);
            return pkg;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static void SendPresence(Container container, TxOp op)
        {
            if (!IsShared(container) || container.IsOwner())
                return;
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            long txId = TxIdGen.Next(ZNet.GetUID(), ref _clientCounter);
            ZPackage body = new ZPackage();
            body.Write(TxCodec.ProtoVersion);
            body.Write((int)op);
            body.Write(0u);
            body.Write(LocalPlayerId());
            body.Write(LocalActorPos());
            body.Write(false);
            body.Write(false);
            ZPackage request = new ZPackage();
            request.Write(txId);
            request.Write(body);
            SendPayload(container, request);
        }

        private static void HandlePresence(Container container, long sender, TxOp op)
        {
            ChestState state = GetState(container);
            if (state == null)
                return;
            if (op == TxOp.ViewerOpen)
            {
                state.Viewers[sender] = Time.realtimeSinceStartup;
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " viewer open peer=" + sender);
            }
            else
            {
                state.Viewers.Remove(sender);
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " viewer close peer=" + sender);
            }
        }

        private static void PumpManagerSlow()
        {
            if (Time.realtimeSinceStartup < _nextSlowPump)
                return;
            _nextSlowPump = Time.realtimeSinceStartup + SlowPump;
            List<int> dead = null;
            foreach (KeyValuePair<int, ChestState> kv in States)
            {
                ChestState state = kv.Value;
                if ((Object)state.Container == (Object)null)
                {
                    if (dead == null)
                        dead = new List<int>();
                    dead.Add(kv.Key);
                    continue;
                }
                ZNetView netView = TxReflect.GetNetView(state.Container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    continue;
                long owner = netView.GetZDO().GetOwner();
                if (owner == ZNet.GetUID() && state.LastOwner != owner)
                {
                    // Takeover: I became the manager — load the latest committed state.
                    Takeover(state);
                }
                state.LastOwner = owner;
                if (owner != ZNet.GetUID())
                    continue;
                // I am the manager: queue, presence, lid.
                Drain(state);
                try
                {
                    // Own vanilla mutations (own GUI) also grow content past commits
                    // — height must always follow it.
                    TxReflect.UpdateRows(state.Container);
                }
                catch (Exception)
                {
                }
                PruneViewers(state);
                EnforceLid(state);
            }
            if (dead != null)
                for (int i = 0; i < dead.Count; i++)
                    States.Remove(dead[i]);
        }

        private static void Takeover(ChestState state)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(state.Container);
                byte[] bytes = netView.GetZDO().GetByteArray(ZDOVars.s_items);
                if (bytes != null)
                {
                    if (InventoryGui.instance != null)
                        TxReflect.CancelDragFrom(InventoryGui.instance, state.Container.GetInventory());
                    state.Container.GetInventory().Load(new ZPackage(bytes));
                    TxReflect.SetLastRevision(state.Container, netView.GetZDO().DataRevision);
                    TxReflect.UpdateRows(state.Container);
                }
                state.Queue.Clear();
                state.Processed.Clear();
                state.ProcOrder.Clear();
                List<RingEntry> ring = ReadRing(state.Container);
                SeedFromRing(state, ring);
                state.Viewers.Clear();
                state.SeenRev = uint.MaxValue;
                state.SeenBytes = null;
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " manager changed old=" + state.LastOwner
                    + " new=" + ZNet.GetUID() + " revision=" + netView.GetZDO().DataRevision);
            }
            catch (Exception ex)
            {
                TxLog.Error("takeover failed: " + ex.Message);
            }
        }

        private static void PruneViewers(ChestState state)
        {
            float now = Time.realtimeSinceStartup;
            List<long> gone = null;
            foreach (KeyValuePair<long, float> kv in state.Viewers)
            {
                if (now - kv.Value > PresenceTimeout)
                {
                    if (gone == null)
                        gone = new List<long>();
                    gone.Add(kv.Key);
                }
            }
            if (gone != null)
                for (int i = 0; i < gone.Count; i++)
                    state.Viewers.Remove(gone[i]);
        }

        private static void EnforceLid(ChestState state)
        {
            try
            {
                // Viewers plus the manager's own open GUI (otherwise flips
                // against vanilla SetInUse(true) every frame and the lid flaps).
                bool want = state.Viewers.Count > 0 || IsOpenByMe(state.Container);
                state.Container.SetInUse(want);
            }
            catch (Exception)
            {
            }
        }

        private static bool IsOpenByMe(Container container)
        {
            InventoryGui gui = InventoryGui.instance;
            if ((Object)gui == (Object)null || !InventoryGui.IsVisible())
                return false;
            return (Object)InventoryAccess.CurrentContainer(gui) == (Object)container;
        }

        // ============================ main pump ============================

        internal static void Pump()
        {
            if (!Plugin.IsActive)
                return;
            TxNet.PumpHello();
            PumpManagerSlow();
            PumpPending();
            PumpViewerRefresh();
        }

        // ============================ helpers ============================

        private static uint CurrentRevision(Container container)
        {
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return 0u;
            return netView.GetZDO().DataRevision;
        }

        private static Vector3 LocalActorPos()
        {
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null)
                return Vector3.zero;
            return ((Component)player).transform.position;
        }

        private static long LocalPlayerId()
        {
            Game game = Game.instance;
            if ((Object)game == (Object)null)
                return 0L;
            PlayerProfile profile = game.GetPlayerProfile();
            if (profile == null)
                return 0L;
            return profile.GetPlayerID();
        }

        private static void TellPlayer(string message)
        {
            Player player = Player.m_localPlayer;
            if (player != null)
                ((Character)player).Message((MessageType)2, message, 0, (Sprite)null, false);
        }
    }
}
