using System;
using System.Collections.Generic;
using BestAutoSort.Runtime;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// ChestTX: authoritative transactional access to stationary chests.
    ///
    /// Network model:
    /// - Chest manager = ZDO owner. Never handed over between operations.
    /// - Client sends BestAutoSort_TxRequest to the chest ZNetView (no target = to owner).
    /// - Manager applies mutations STRICTLY ONE AT A TIME from the chest queue:
    ///   validate → apply → Save() → revision → response to the specific peer.
    /// - Client touches its own inventory ONLY on response, exactly by accepted.
    /// - Idempotency: txId = peerId&lt;&lt;32 | counter. Retry = cached result.
    ///   Full-result cache in memory + persistent ring in ZDO (handoff).
    /// - Lost response: Query with the same txId (no re-apply).
    /// - Viewers poll ZDO.DataRevision and reload the open GUI without closing it.
    /// - Presence (chest lid): ViewerOpen/ViewerClose + heartbeat, manager sets s_inUse.
    /// - Handoff: the new owner always Loads from ZDO (committed state) + reads the ring.
    ///
    /// Everything runs on the Unity main thread (Valheim RPCs dispatch there too).
    /// </summary>
    internal static partial class ChestTxService
    {
        private const float RequestTimeout = 3f;
        private const int PayloadAttempts = 4;
        private const int QueryAttempts = 2;
        private const float FailDeadline = 16f;
        private const float PresenceHeartbeat = 5f;
        private const float PresenceTimeout = 15f;
        private const float RefreshPoll = 0.25f;
        private const float SlowPump = 0.5f;
        private const int DrainPerFrame = 64;

        internal const string RingKey = "BestAutoSort_TxRing";
        internal const string ItemsKey = "items";

        private static readonly Dictionary<int, ChestState> States = new Dictionary<int, ChestState>();
        private static readonly Dictionary<long, PendingTx> Pending = new Dictionary<long, PendingTx>();
        private static uint _clientCounter = 1;
        private static float _nextSlowPump;
        private static float _nextRefreshPoll;
        private static int _openInstanceId;
        private static float _nextPresenceAt;

        // ============================ lifecycle ============================

        internal static void Reset()
        {
            States.Clear();
            Pending.Clear();
            _clientCounter = 1;
            _nextSlowPump = 0f;
            _nextRefreshPoll = 0f;
            _openInstanceId = 0;
            _nextPresenceAt = 0f;
        }

        internal static void OnContainerAwake(Container container)
        {
            if ((Object)container == (Object)null)
                return;
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            try
            {
                netView.Register<ZPackage>(TxNet.TxRequestRpc, delegate (long sender, ZPackage pkg)
                {
                    OnTxRequest(container, sender, pkg);
                });
                netView.Register<ZPackage>(TxNet.TxResponseRpc, delegate (long sender, ZPackage pkg)
                {
                    OnTxResponse(container, sender, pkg);
                });
            }
            catch (Exception ex)
            {
                TxLog.Warn("container " + TxLog.Zid(netView.GetZDO().m_uid) + " RPC register failed: " + ex.Message);
            }
            int id = ((Object)container).GetInstanceID();
            if (!States.ContainsKey(id))
            {
                ChestState state = new ChestState();
                state.Container = container;
                state.ZdoId = netView.GetZDO().m_uid;
                state.LastOwner = netView.GetZDO().GetOwner();
                States[id] = state;
            }
            AutoFeedService.Attach(container);
        }

        /// <summary>
        /// IsShared + причина отказа (только для диагностики тихих скипов).
        /// </summary>
        internal static bool IsSharedVerbose(Container container, out string why)
        {
            why = "ok";
            if (!ModConfig.AllowConcurrentChestUse.Value) { why = "AllowConcurrentChestUse disabled"; return false; }
            if ((Object)container == (Object)null) { why = "null container"; return false; }
            if (!((Behaviour)container).isActiveAndEnabled) { why = "behaviour disabled"; return false; }
            if (AutoFeedService.IsLocked(container)) { why = "autofeed lease/block active"; return false; }
            if ((Object)container.m_wagon != (Object)null) { why = "wagon cart"; return false; }
            if ((Object)((Component)container).GetComponentInParent<Ship>() != (Object)null) { why = "ship"; return false; }
            if ((Object)((Component)container).GetComponent<Player>() != (Object)null) { why = "player inventory"; return false; }
            if ((Object)((Component)container).GetComponent<TombStone>() != (Object)null) { why = "tombstone"; return false; }
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null) { why = "no netview"; return false; }
            if (!netView.IsValid()) { why = "netview invalid"; return false; }
            return true;
        }

        internal static bool IsShared(Container container)
        {
            string why;
            return IsSharedVerbose(container, out why);
        }

        internal static bool IsManager(Container container)
        {
            return container.IsOwner();
        }

        internal static ChestState GetState(Container container)
        {
            if ((Object)container == (Object)null)
                return null;
            ChestState state;
            States.TryGetValue(((Object)container).GetInstanceID(), out state);
            return state;
        }

        // ============================ public API: GUI/automation ============================

        internal static void RequestAdd(Container container, Inventory srcInv, ItemData item, int amount, int wantX, int wantY, Action<ZPackage, TxStatus, uint> onDone)
        {
            // A stale grid may report a cell outside the inventory (beyond W/H):
            // then auto-place instead of refusing (like click-move).
            Inventory chestInv = container != null ? container.GetInventory() : null;
            if (chestInv != null && wantX >= 0 && wantY >= 0
                && (wantX >= chestInv.GetWidth() || wantY >= chestInv.GetHeight()))
            {
                TxLog.Info("want-clamped (" + wantX + "," + wantY + ") vs "
                    + chestInv.GetWidth() + "x" + chestInv.GetHeight());
                wantX = -1;
                wantY = -1;
            }
            TxOpItem opItem = SnapshotItem(item, amount, wantX, wantY);
            if (opItem == null)
            {
                TellPlayer("Item cannot be sent (no prefab).");
                return;
            }
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Add;
            call.Items.Add(opItem);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                int accepted = ReadAcceptedAt(pkg, 0);
                CompleteAdd(srcInv, item, opItem, accepted, status, rev, container, onDone);
            });
        }

        internal static void RequestAddBatch(Container container, Inventory srcInv, List<TxOpItem> items, Action<ZPackage, TxStatus, uint> onDone)
        {
            if (items == null || items.Count == 0)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.AddBatch;
            call.Items.AddRange(items);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                List<int> accepted = ReadAcceptedList(pkg, items.Count);
                for (int i = 0; i < items.Count && i < accepted.Count; i++)
                    CompleteAdd(srcInv, null, items[i], accepted[i], status, rev, container, null);
                RefreshNow(container);
                if (onDone != null)
                    onDone(pkg, status, rev);
            });
        }

        internal static TxOpItem SnapshotItem(ItemData item, int amount, int wantX, int wantY)
        {
            if (item == null || item.m_shared == null || (Object)item.m_dropPrefab == null || amount <= 0)
                return null;
            ItemData clone = item.Clone();
            clone.m_stack = amount;
            TxOpItem opItem = new TxOpItem();
            opItem.Snapshot = clone;
            opItem.SourceRef = item;
            opItem.PrefabHash = item.m_dropPrefab.name.GetStableHashCode();
            opItem.Amount = amount;
            opItem.X = item.m_gridPos.x;
            opItem.Y = item.m_gridPos.y;
            opItem.MaxStack = item.m_shared.m_maxStackSize;
            if (wantX >= 0 && wantY >= 0)
            {
                opItem.X = wantX;
                opItem.Y = wantY;
            }
            return opItem;
        }

        internal static void RequestTake(Container container, Inventory dstInv, ItemData snapshot, int amount, Action<ZPackage, TxStatus, uint> onDone)
        {
            TxOpItem opItem = SnapshotItem(snapshot, amount, -1, -1);
            if (opItem == null)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Take;
            call.Items.Add(opItem);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                CompleteTake(dstInv, pkg, status, rev, container, onDone);
            });
        }

        internal static void RequestTakeBatch(Container container, Inventory dstInv, List<TxOpItem> items, Action<ZPackage, TxStatus, uint> onDone)
        {
            if (items == null || items.Count == 0)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.TakeBatch;
            call.Items.AddRange(items);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                CompleteTake(dstInv, pkg, status, rev, container, onDone);
            });
        }

        internal static void RequestMove(Container container, ItemData snapshot, int amount, int dstX, int dstY, Action<ZPackage, TxStatus, uint> onDone)
        {
            TxOpItem opItem = SnapshotItem(snapshot, amount, -1, -1);
            if (opItem == null)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Move;
            call.Items.Add(opItem);
            call.DstX = dstX;
            call.DstY = dstY;
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                RefreshNow(container);
                if (status == TxStatus.Rejected)
                    TellPlayer("The shared chest changed. Try that item move again.");
                if (onDone != null)
                    onDone(pkg, status, rev);
            });
        }

        internal static void RequestSort(Container container, int mode, bool desc)
        {
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Sort;
            call.Mode = mode;
            call.Desc = desc;
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                RefreshNow(container);
                if (status != TxStatus.Accepted && status != TxStatus.Duplicate)
                    TellPlayer("Chest sort failed. Try again.");
            });
        }

        internal static void RequestSetRule(Container container, string rule)
        {
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.SetRule;
            call.Rule = rule ?? string.Empty;
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev)
            {
                RefreshNow(container);
                if (status != TxStatus.Accepted && status != TxStatus.Duplicate)
                    TellPlayer("Chest rule was not saved. Try again.");
            });
        }

        /// <summary>
        /// Manager-local mutation (own GUI/automation on the owner):
        /// goes into the same chest queue and runs immediately, serially with remote ones.
        /// </summary>
        internal static void MutateLocal(Container container, TxOpCall call, Action<StoredResult> onDone)
        {
            ChestState state = GetState(container);
            if (state == null || !container.IsOwner())
            {
                if (onDone != null)
                {
                    StoredResult r = new StoredResult();
                    r.Status = TxStatus.Rejected;
                    onDone(r);
                }
                return;
            }
            TxJob job = new TxJob();
            job.Container = container;
            job.IsLocal = true;
            job.TxId = NextLocalTxId();
            job.Sender = ZNet.GetUID();
            job.PlayerId = LocalPlayerId();
            job.BaseRev = CurrentRevision(container);
            job.Call = call;
            job.Complete = onDone;
            state.Queue.Enqueue(job);
            Drain(state);
        }

        /// <summary>
        /// Manager fast path: run queued remote operations right now
        /// (before its own local mutation — strict order without RPC).
        /// </summary>
        internal static void DrainForLocal(Container container)
        {
            ChestState state = GetState(container);
            if (state == null || !container.IsOwner())
                return;
            Drain(state);
        }

        /// <summary>
        /// One-shot explicit ownership acquire for STRUCTURAL operations
        /// (chest upgrade: the object is destroyed and recreated).
        /// Not ping-pong: called only on explicit user action, no loops.
        /// Returns true when this peer is now the owner with fresh state.
        /// </summary>
        internal static bool AcquireForStructural(Container container)
        {
            if (!IsShared(container))
                return false;
            if (container.IsOwner())
                return true;
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return false;
            try
            {
                netView.ClaimOwnership();
                if (!container.IsOwner())
                    return false;
                // Load the latest committed state before the structural operation.
                byte[] bytes = netView.GetZDO().GetByteArray(ZDOVars.s_items);
                if (bytes != null)
                {
                    container.GetInventory().Load(new ZPackage(bytes));
                    TxReflect.SetLastRevision(container, netView.GetZDO().DataRevision);
                    TxReflect.UpdateRows(container);
                }
                ChestState state = GetState(container);
                if (state != null)
                {
                    state.Queue.Clear();
                    state.Processed.Clear();
                    state.ProcOrder.Clear();
                    SeedFromRing(state, ReadRing(container));
                    state.LastOwner = ZNet.GetUID();
                }
                TxLog.Info("container=" + TxLog.Zid(netView.GetZDO().m_uid) + " structural acquire by " + ZNet.GetUID());
                return true;
            }
            catch (Exception ex)
            {
                TxLog.Warn("structural acquire failed: " + ex.Message);
                return false;
            }
        }

        // ============================ submit / transport ============================

        private static long NextLocalTxId()
        {
            return TxIdGen.Next(ZNet.GetUID(), ref _clientCounter);
        }

        private static void Submit(Container container, TxOpCall call, Action<ZPackage, TxStatus, uint> onDone)
        {
            if (!Plugin.IsActive || !IsShared(container))
            {
                TellPlayer("Shared chest is not available.");
                return;
            }
            if (container.IsOwner())
            {
                // Manager: enqueue locally, run right away.
                MutateLocal(container, call, delegate (StoredResult r)
                {
                    RefreshNow(container);
                    if (onDone != null)
                        onDone(null, r.Status, r.Revision);
                });
                return;
            }
            long txId = TxIdGen.Next(ZNet.GetUID(), ref _clientCounter);
            ZPackage payload = EncodeCall(call, CurrentRevision(container));
            ZPackage request = new ZPackage();
            request.Write(txId);
            request.Write(payload);
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
            {
                TellPlayer("Shared chest is not available.");
                return;
            }
            PendingTx pending = new PendingTx();
            pending.TxId = txId;
            pending.Container = container;
            pending.Payload = request;
            pending.Op = call.Op;
            pending.Attempts = 0;
            pending.NextTryAt = 0f;
            pending.Deadline = Time.realtimeSinceStartup + FailDeadline;
            pending.OnResponse = onDone;
            Pending[txId] = pending;
            TxLog.Info("container=" + TxLog.Zid(netView.GetZDO().m_uid) + " tx=" + txId
                + " peer=" + ZNet.GetUID() + " op=" + call.Op + " " + DescribeCall(call) + " SEND");
            SendPayload(container, request);
            pending.Attempts = 1;
            pending.NextTryAt = Time.realtimeSinceStartup + RequestTimeout;
        }

        private static void SendPayload(Container container, ZPackage request)
        {
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            try
            {
                netView.InvokeRPC(TxNet.TxRequestRpc, request);
            }
            catch (Exception ex)
            {
                TxLog.Warn("send failed: " + ex.Message);
            }
        }

        private static string DescribeCall(TxOpCall call)
        {
            try
            {
                if (call.Items.Count == 0)
                    return "";
                TxOpItem first = call.Items[0];
                string name = first.Snapshot != null && first.Snapshot.m_shared != null ? first.Snapshot.m_shared.m_name : "?";
                string text = "item=" + name + " x" + first.Amount + " want=(" + first.X + "," + first.Y + ")";
                if (call.Items.Count > 1)
                    text += " (+" + (call.Items.Count - 1) + " more)";
                return text;
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static ZPackage EncodeCall(TxOpCall call, uint baseRev)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(TxCodec.ProtoVersion);
            pkg.Write((int)call.Op);
            pkg.Write(baseRev);
            pkg.Write(LocalPlayerId());
            pkg.Write(LocalActorPos());
            pkg.Write(call.EnforceRule);
            pkg.Write(call.RespectReserves);
            switch (call.Op)
            {
                case TxOp.Add:
                case TxOp.AddBatch:
                case TxOp.Take:
                case TxOp.TakeBatch:
                    pkg.Write(call.Items.Count);
                    for (int i = 0; i < call.Items.Count; i++)
                        WriteOpItem(pkg, call.Items[i]);
                    break;
                case TxOp.Move:
                    WriteOpItem(pkg, call.Items[0]);
                    pkg.Write(call.DstX);
                    pkg.Write(call.DstY);
                    break;
                case TxOp.Sort:
                    pkg.Write(call.Mode);
                    pkg.Write(call.Desc);
                    break;
                case TxOp.Upgrade:
                    pkg.Write(call.Tier);
                    break;
                case TxOp.SetRule:
                    pkg.Write(call.Rule ?? string.Empty);
                    break;
                case TxOp.ViewerOpen:
                case TxOp.ViewerClose:
                    break;
            }
            return pkg;
        }

        private static void WriteOpItem(ZPackage pkg, TxOpItem item)
        {
            ZPackage inner = new ZPackage();
            item.Snapshot.Save(inner);
            pkg.Write(item.PrefabHash);
            pkg.Write(inner);
            pkg.Write(item.Amount);
            pkg.Write(item.X);
            pkg.Write(item.Y);
            pkg.Write(item.MaxStack);
        }

        // ============================ manager: request handling ============================

        private static void OnTxRequest(Container container, long sender, ZPackage request)
        {
            if (!Plugin.IsActive || !IsShared(container) || !container.IsOwner())
                return;
            long txId;
            ZPackage payload;
            try
            {
                txId = request.ReadLong();
                payload = request.ReadPackage();
            }
            catch (Exception ex)
            {
                TxLog.Warn("bad request packet from " + sender + ": " + ex.Message);
                return;
            }
            TxOpCall call;
            uint baseRev;
            long playerId;
            Vector3 actorPos;
            if (!DecodeCall(payload, out call, out baseRev, out playerId, out actorPos))
            {
                TxLog.Warn("tx=" + txId + " undecodable request from " + sender);
                return;
            }
            if (!TxNet.IsCompatiblePeer(sender) && sender != ZNet.GetUID())
            {
                TxLog.Warn("tx=" + txId + " REJECT incompatible peer " + sender);
                Respond(container, sender, txId, TxStatus.Rejected, CurrentRevision(container), new ZPackage(), true, call.Op);
                return;
            }
            if ((call.Op == TxOp.ViewerOpen || call.Op == TxOp.ViewerClose))
            {
                // Fire-and-forget: no responses (otherwise the client spams late-duplicates
                // and churns refresh/cancel-drag on every heartbeat).
                HandlePresence(container, sender, call.Op);
                return;
            }
            if (call.Op == TxOp.Query)
            {
                RespondQuery(container, sender, txId);
                return;
            }
            if (!ChestAuthority.CanUse(container, sender, playerId, actorPos))
            {
                TxLog.Warn("tx=" + txId + " REJECT access peer=" + sender);
                Respond(container, sender, txId, TxStatus.Rejected, CurrentRevision(container), new ZPackage(), true, call.Op);
                return;
            }
            ChestState state = GetState(container);
            if (state == null)
                return;
            TxJob job = new TxJob();
            job.Container = container;
            job.TxId = txId;
            job.Sender = sender;
            job.PlayerId = playerId;
            job.BaseRev = baseRev;
            job.Call = call;
            job.Complete = delegate (StoredResult r)
            {
                ZPackage body = EncodeResultBody(call, r);
                Respond(container, sender, txId, r.Status, r.Revision, body, r.TotalsOnly, call.Op);
            };
            state.Queue.Enqueue(job);
            Drain(state);
        }

        private static bool DecodeCall(ZPackage payload, out TxOpCall call, out uint baseRev, out long playerId, out Vector3 actorPos)
        {
            call = null;
            baseRev = 0u;
            playerId = 0L;
            actorPos = Vector3.zero;
            try
            {
                if (payload.ReadInt() != TxCodec.ProtoVersion)
                    return false;
                TxOp op = (TxOp)payload.ReadInt();
                baseRev = payload.ReadUInt();
                playerId = payload.ReadLong();
                actorPos = payload.ReadVector3();
                call = new TxOpCall();
                call.Op = op;
                call.EnforceRule = payload.ReadBool();
                call.RespectReserves = payload.ReadBool();
                switch (op)
                {
                    case TxOp.Add:
                    case TxOp.AddBatch:
                    case TxOp.Take:
                    case TxOp.TakeBatch:
                        {
                            int count = payload.ReadInt();
                            if (count < 0 || count > 64)
                                return false;
                            for (int i = 0; i < count; i++)
                            {
                                TxOpItem item;
                                if (!ReadOpItem(payload, out item))
                                    return false;
                                call.Items.Add(item);
                            }
                            break;
                        }
                    case TxOp.Move:
                        {
                            TxOpItem item;
                            if (!ReadOpItem(payload, out item))
                                return false;
                            call.Items.Add(item);
                            call.DstX = payload.ReadInt();
                            call.DstY = payload.ReadInt();
                            break;
                        }
                    case TxOp.Sort:
                        call.Mode = payload.ReadInt();
                        call.Desc = payload.ReadBool();
                        break;
                    case TxOp.Upgrade:
                        call.Tier = payload.ReadInt();
                        break;
                    case TxOp.SetRule:
                        call.Rule = payload.ReadString();
                        break;
                    case TxOp.ViewerOpen:
                    case TxOp.ViewerClose:
                    case TxOp.Query:
                        break;
                    default:
                        return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ReadOpItem(ZPackage payload, out TxOpItem item)
        {
            item = null;
            try
            {
                int prefabHash = payload.ReadInt();
                ZPackage inner = payload.ReadPackage();
                int amount = payload.ReadInt();
                int x = payload.ReadInt();
                int y = payload.ReadInt();
                int maxStack = payload.ReadInt();
                if (prefabHash == 0 || amount <= 0 || amount > 9999)
                    return false;
                ItemData snapshot = TxCodec.ResolvePrefab(prefabHash, inner);
                if (snapshot == null)
                    return false;
                item = new TxOpItem();
                item.Snapshot = snapshot;
                item.PrefabHash = prefabHash;
                item.Amount = amount;
                item.X = x;
                item.Y = y;
                item.MaxStack = maxStack <= 0 ? 50 : maxStack;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ============================ manager: serial drain ============================

        private static void Drain(ChestState state)
        {
            if (state == null || state.Draining)
                return;
            if ((Object)state.Container == (Object)null || !state.Container.IsOwner())
                return;
            state.Draining = true;
            try
            {
                int n = 0;
                while (state.Queue.Count > 0 && n < DrainPerFrame)
                {
                    n++;
                    TxJob job = state.Queue.Dequeue();
                    StoredResult result = ApplyJob(state, job);
                    try
                    {
                        if (job.Complete != null)
                            job.Complete(result);
                    }
                    catch (Exception ex)
                    {
                        TxLog.Error("tx=" + job.TxId + " completion failed: " + ex.Message);
                    }
                    if ((job.Call.Op == TxOp.Add || job.Call.Op == TxOp.AddBatch || job.Call.Op == TxOp.TakeBatch)
                        && result.AcceptedTotal() > 0)
                    {
                        // Броадкаст с target 0 заходит и локально (loopback),
                        // так что менеджер тоже проигрывает — отдельно не нужно.
                        TxFlights.BroadcastFlights(state, job, result);
                    }
                }
            }
            finally
            {
                state.Draining = false;
            }
        }

        private static StoredResult ApplyJob(ChestState state, TxJob job)
        {
            // Idempotency: same txId repeated.
            StoredResult cached;
            if (state.Processed.TryGetValue(job.TxId, out cached))
            {
                if (cached.TotalsOnly && IsTakeOp(job.Call.Op))
                    return RedeliverTake(state, job, cached);
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " DUPLICATE returning cached result");
                return CloneStored(cached, TxStatus.Duplicate);
            }
            Inventory inv = job.Container.GetInventory();
            if (inv == null)
                return Reject(state, job);
            StoredResult result = ExecuteCall(state, job, inv);
            CommitResult(state, job, result);
            uint baseRev = job.BaseRev;
            uint now = CurrentRevision(job.Container);
            if (baseRev != 0u && baseRev != now && baseRev != result.Revision)
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " stale_revision client=" + baseRev + " server=" + now);
            return result;
        }

        private static StoredResult ExecuteCall(ChestState state, TxJob job, Inventory inv)
        {
            switch (job.Call.Op)
            {
                case TxOp.Add:
                case TxOp.AddBatch:
                    return ExecuteAdd(inv, job.Call, state);
                case TxOp.Take:
                case TxOp.TakeBatch:
                    return ExecuteTake(inv, job.Call, state);
                case TxOp.Move:
                    return ExecuteMove(inv, job.Call);
                case TxOp.Sort:
                    return ExecuteSort(inv, job.Call);
                case TxOp.Upgrade:
                    return ExecuteUpgrade(state, job);
                case TxOp.SetRule:
                    return ExecuteSetRule(state, job);
                default:
                    return RejectNew();
            }
        }

        private static StoredResult ExecuteAdd(Inventory inv, TxOpCall call, ChestState state)
        {
            StoredResult result = new StoredResult();
            result.Op = call.Op;
            HashSet<string> destNames = null;
            HashSet<BestAutoSort.Core.ItemCategory> destCats = null;
            BestAutoSort.Core.ChestStorageRule rule = null;
            if (call.EnforceRule)
            {
                destNames = new HashSet<string>();
                destCats = new HashSet<BestAutoSort.Core.ItemCategory>();
                QuickStackTransfer.DestSeeds(inv, destNames, destCats);
                rule = ChestRuleStore.Read(state.Container);
            }
            int full = 0;
            for (int i = 0; i < call.Items.Count; i++)
            {
                TxOpItem it = call.Items[i];
                string iname = it.Snapshot.m_shared != null ? it.Snapshot.m_shared.m_name : "?";
                if (it.X >= inv.GetWidth() || it.Y >= inv.GetHeight())
                {
                    // Cell outside the real dims (stale sender grid): auto-place.
                    TxLog.Info("op=ADD item=" + TxCodec.Describe(iname, it.Amount) + " want=(" + it.X + "," + it.Y + ") oob vs "
                        + inv.GetWidth() + "x" + inv.GetHeight() + ", auto-place");
                    it.X = -1;
                    it.Y = -1;
                }
                ItemData clone = it.Snapshot.Clone();
                clone.m_stack = it.Amount;
                int accepted = 0;
                string why = "";
                if (!call.EnforceRule || QuickStackTransfer.CanAcceptFromRule(rule, clone, destNames, destCats))
                {
                    // Drag&drop with a requested cell goes strictly positional (like vanilla),
                    // otherwise merge/auto-place. No fallback: the remainder stays with the client.
                    if (it.X >= 0 && it.Y >= 0)
                    {
                        accepted = TxInventory.AddPositional(inv, clone, it.Amount, it.X, it.Y);
                        if (accepted < it.Amount)
                        {
                            ItemData occupant = null;
                            string cell = "oob";
                            if (it.X < inv.GetWidth() && it.Y < inv.GetHeight())
                            {
                                occupant = inv.GetItemAt(it.X, it.Y);
                                cell = occupant != null && occupant.m_shared != null
                                    ? occupant.m_shared.m_name + " x" + occupant.m_stack
                                    : "empty";
                            }
                            why = (accepted > 0 ? "partial-pos" : "pos-blocked")
                                + " chest=" + inv.GetWidth() + "x" + inv.GetHeight()
                                + " cell=" + cell;
                        }
                    }
                    else
                    {
                        accepted = TxInventory.AddAndCount(inv, clone);
                        if (accepted < it.Amount)
                            why = accepted > 0 ? "partial-full" : "full";
                    }
                    if (accepted > 0 && call.EnforceRule)
                    {
                        destNames.Add(clone.m_shared.m_name);
                        destCats.Add(ValheimItemCategoryClassifier.Classify(clone));
                    }
                }
                else
                {
                    why = "rule";
                }
                TxLog.Info("op=ADD item=" + TxCodec.Describe(iname, it.Amount) + " want=(" + it.X + "," + it.Y + ") accepted=" + accepted + (why.Length > 0 ? " why=" + why : ""));
                result.Accepted.Add(accepted);
                if (accepted == it.Amount)
                    full++;
            }
            result.Status = full == result.Accepted.Count && result.Accepted.Count > 0
                ? TxStatus.Accepted : (result.AcceptedTotal() > 0 ? TxStatus.Partial : TxStatus.Rejected);
            return result;
        }

        private static StoredResult ExecuteTake(Inventory inv, TxOpCall call, ChestState state)
        {
            StoredResult result = new StoredResult();
            result.Op = call.Op;
            int full = 0;
            for (int i = 0; i < call.Items.Count; i++)
            {
                TxOpItem it = call.Items[i];
                int taken;
                TakeEntry entry;
                if (call.RespectReserves)
                    taken = TakeRespectingReserves(state, inv, it, out entry);
                else
                    taken = TakeSingle(inv, it, out entry);
                result.Accepted.Add(taken);
                result.Takes.Add(entry);
                if (taken == it.Amount)
                    full++;
                TxLog.Info("op=TAKE item=" + TxCodec.Describe(it.Snapshot.m_shared.m_name, it.Amount)
                    + " requested=" + it.Amount + " taken=" + taken);
            }
            result.Status = full == result.Accepted.Count && result.Accepted.Count > 0
                ? TxStatus.Accepted : (result.AcceptedTotal() > 0 ? TxStatus.Partial : TxStatus.Rejected);
            return result;
        }

        private static int TakeSingle(Inventory inv, TxOpItem it, out TakeEntry entry)
        {
            entry = null;
            ItemData live = TxCodec.ResolveIn(inv, it.Snapshot.m_shared.m_name,
                it.Snapshot.m_quality, it.Snapshot.m_variant, it.Snapshot.m_worldLevel, it.X, it.Y);
            if (live == null)
                return 0;
            int n = it.Amount < live.m_stack ? it.Amount : live.m_stack;
            ItemData outItem = live.Clone();
            outItem.m_stack = n;
            int taken = TxInventory.RemoveExact(inv, live, n);
            if (taken <= 0)
                return 0;
            outItem.m_stack = taken;
            entry = new TakeEntry();
            entry.PrefabHash = it.PrefabHash;
            entry.Item = outItem;
            entry.Accepted = taken;
            return taken;
        }

        private static int TakeRespectingReserves(ChestState state, Inventory inv, TxOpItem it, out TakeEntry entry)
        {
            entry = null;
            List<ItemData> stacks = TxCodec.ResolveAllIn(inv, it.Snapshot.m_shared.m_name,
                it.Snapshot.m_quality, it.Snapshot.m_variant, it.Snapshot.m_worldLevel);
            if (stacks.Count == 0)
                return 0;
            int remaining = it.Amount;
            int taken = 0;
            ItemData first = null;
            foreach (ItemData stack in stacks)
            {
                if (remaining <= 0)
                    break;
                int avail = ChestReserveStore.Available(state.Container, stack);
                if (avail <= 0)
                    continue;
                int n = remaining < stack.m_stack ? remaining : stack.m_stack;
                if (n > avail)
                    n = avail;
                if (first == null)
                    first = stack.Clone();
                int got = TxInventory.RemoveExact(inv, stack, n);
                taken += got;
                remaining -= got;
            }
            if (taken <= 0)
                return 0;
            first.m_stack = taken;
            entry = new TakeEntry();
            entry.PrefabHash = it.PrefabHash;
            entry.Item = first;
            entry.Accepted = taken;
            return taken;
        }

        private static StoredResult ExecuteMove(Inventory inv, TxOpCall call)
        {
            StoredResult result = new StoredResult();
            result.Op = call.Op;
            if (call.Items.Count == 0)
            {
                result.Status = TxStatus.Rejected;
                result.Accepted.Add(0);
                return result;
            }
            TxOpItem it = call.Items[0];
            ItemData live = TxCodec.ResolveIn(inv, it.Snapshot.m_shared.m_name,
                it.Snapshot.m_quality, it.Snapshot.m_variant, it.Snapshot.m_worldLevel, it.X, it.Y);
            int moved = 0;
            if (live != null)
                moved = TxInventory.MoveWithin(inv, live, it.Amount, call.DstX, call.DstY);
            result.Status = moved > 0 ? TxStatus.Accepted : TxStatus.Rejected;
            result.Accepted.Add(moved);
            TxLog.Info("op=MOVE item=" + TxCodec.Describe(it.Snapshot.m_shared.m_name, it.Amount)
                + " dst=(" + call.DstX + "," + call.DstY + ") moved=" + moved);
            return result;
        }

        private static StoredResult ExecuteSort(Inventory inv, TxOpCall call)
        {
            StoredResult result = new StoredResult();
            result.Op = call.Op;
            int count = InventorySorter.Sort(inv, (BestAutoSort.Core.SortMode)call.Mode, call.Desc);
            result.Status = TxStatus.Accepted;
            result.Accepted.Add(count);
            TxLog.Info("op=SORT mode=" + call.Mode + " desc=" + call.Desc + " items=" + count);
            return result;
        }

        private static StoredResult ExecuteUpgrade(ChestState state, TxJob job)
        {
            // Structural operation: runs directly on the manager.
            // Currently runs only when the manager sees this chest; otherwise reject.
            StoredResult result = new StoredResult();
            result.Op = job.Call.Op;
            Container open = InventoryAccess.CurrentContainer(InventoryGui.instance);
            if ((Object)open != (Object)state.Container)
            {
                result.Status = TxStatus.Rejected;
                result.Accepted.Add(0);
                TxLog.Info("op=UPGRADE REJECT manager-gui-mismatch tier=" + job.Call.Tier);
                return result;
            }
            ChestUpgradeService.UpgradeOpenChest(job.Call.Tier);
            result.Status = TxStatus.Accepted;
            result.Accepted.Add(1);
            return result;
        }

        private static StoredResult ExecuteSetRule(ChestState state, TxJob job)
        {
            StoredResult result = new StoredResult();
            result.Op = job.Call.Op;
            BestAutoSort.Core.ChestStorageRule rule;
            if (!BestAutoSort.Core.ChestStorageRuleCodec.TryDeserialize(job.Call.Rule ?? string.Empty, out rule))
            {
                result.Status = TxStatus.Rejected;
                result.Accepted.Add(0);
                return result;
            }
            string error;
            if (!ChestRuleStore.TryWrite(state.Container, rule, out error))
            {
                result.Status = TxStatus.Rejected;
                result.Accepted.Add(0);
                TxLog.Info("op=SETRULE REJECT " + error);
                return result;
            }
            result.Status = TxStatus.Accepted;
            result.Accepted.Add(1);
            return result;
        }

        private static void CommitResult(ChestState state, TxJob job, StoredResult result)
        {
            bool mutated = result.Status == TxStatus.Accepted || result.Status == TxStatus.Partial;
            if (mutated)
            {
                // Chest height must track content (otherwise viewers see rows
                // the manager lacks, and drops there get rejected).
                TxReflect.UpdateRows(state.Container);
                TxReflect.SaveContainer(state.Container);
                WriteRing(state);
            }
            result.Revision = CurrentRevision(state.Container);
            state.Processed[job.TxId] = CloneStored(result, result.Status);
            state.ProcOrder.AddLast(job.TxId);
            while (state.ProcOrder.Count > TxLimits.ProcessedCacheCap)
            {
                long oldest = state.ProcOrder.First.Value;
                state.ProcOrder.RemoveFirst();
                state.Processed.Remove(oldest);
            }
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " peer=" + job.Sender
                + " op=" + job.Call.Op + " accepted=" + result.AcceptedTotal() + " revision=" + result.Revision);
        }

        private static StoredResult Reject(ChestState state, TxJob job)
        {
            StoredResult result = new StoredResult();
            result.Op = job.Call.Op;
            result.Status = TxStatus.Rejected;
            result.Revision = CurrentRevision(job.Container);
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " REJECT op=" + job.Call.Op);
            return result;
        }

        private static StoredResult RejectNew()
        {
            StoredResult result = new StoredResult();
            result.Status = TxStatus.Rejected;
            return result;
        }

        private static StoredResult CloneStored(StoredResult src, TxStatus status)
        {
            StoredResult r = new StoredResult();
            r.Op = src.Op;
            r.Status = status;
            r.Revision = src.Revision;
            r.Accepted = new List<int>(src.Accepted);
            r.Takes = new List<TakeEntry>(src.Takes);
            r.TotalsOnly = src.TotalsOnly;
            return r;
        }

        private static bool IsTakeOp(TxOp op)
        {
            return op == TxOp.Take || op == TxOp.TakeBatch;
        }

        /// <summary>
        /// Take retry after handoff (ring holds totals only): pull the equivalent
        /// from live state. Conservation holds — items come out of the chest now.
        /// </summary>
        private static StoredResult RedeliverTake(ChestState state, TxJob job, StoredResult cached)
        {
            Inventory inv = job.Container.GetInventory();
            StoredResult fresh = ExecuteTake(inv, job.Call, state);
            fresh.Revision = 0u;
            if (fresh.AcceptedTotal() > 0)
            {
                TxReflect.SaveContainer(job.Container);
                WriteRing(state);
            }
            fresh.Revision = CurrentRevision(job.Container);
            state.Processed[job.TxId] = CloneStored(fresh, TxStatus.Duplicate);
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId
                + " DUPLICATE handoff-redelivery accepted=" + fresh.AcceptedTotal());
            StoredResult r = CloneStored(fresh, TxStatus.Duplicate);
            return r;
        }

        // ============================ manager: ring persist ============================

        private static void WriteRing(ChestState state)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(state.Container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return;
                List<RingEntry> ring = new List<RingEntry>();
                // ProcOrder is oldest-first: walk from the end (newest), then reverse.
                LinkedListNode<long> node = state.ProcOrder.Last;
                int collected = 0;
                while (node != null && collected < TxLimits.RingCap)
                {
                    StoredResult r;
                    if (state.Processed.TryGetValue(node.Value, out r))
                    {
                        RingEntry e;
                        e.TxId = node.Value;
                        e.AcceptedTotal = r.AcceptedTotal();
                        e.Revision = r.Revision;
                        ring.Add(e);
                        collected++;
                    }
                    node = node.Previous;
                }
                ring.Reverse();
                netView.GetZDO().Set(RingKey, TxCore.TxCore.EncodeRing(ring));
            }
            catch (Exception ex)
            {
                TxLog.Warn("ring persist failed: " + ex.Message);
            }
        }

        private static List<RingEntry> ReadRing(Container container)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return new List<RingEntry>();
                byte[] bytes = netView.GetZDO().GetByteArray(RingKey.GetStableHashCode());
                if (bytes == null)
                    return new List<RingEntry>();
                return TxCore.TxCore.DecodeRing(bytes);
            }
            catch (Exception)
            {
                return new List<RingEntry>();
            }
        }

        private static void SeedFromRing(ChestState state, List<RingEntry> ring)
        {
            state.Processed.Clear();
            state.ProcOrder.Clear();
            for (int i = 0; i < ring.Count && i < TxLimits.RingCap; i++)
            {
                StoredResult r = new StoredResult();
                r.Status = TxStatus.Duplicate;
                r.Revision = ring[i].Revision;
                r.Accepted = new List<int> { ring[i].AcceptedTotal };
                r.TotalsOnly = true;
                state.Processed[ring[i].TxId] = r;
                state.ProcOrder.AddLast(ring[i].TxId);
            }
        }
    }
}
