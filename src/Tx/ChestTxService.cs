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

        internal static void RequestAdd(Container container, Inventory srcInv, ItemData item, int amount, int wantX, int wantY, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone)
        {
            RequestAdd(container, srcInv, item, amount, wantX, wantY, onDone, false);
        }

        internal static void RequestAdd(Container container, Inventory srcInv, ItemData item, int amount, int wantX, int wantY, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone, bool alreadyRemoved)
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
            if (wantX < 0 || wantY < 0)
            {
                // No explicit cell: SnapshotItem defaults X/Y to the SOURCE grid
                // pos (needed for Takes), but for Adds that is the player-grid
                // slot — the manager would read it as a positional want and park
                // the item in the same chest cell (ctrl-click lands in-slot).
                opItem.X = -1;
                opItem.Y = -1;
            }
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Add;
            call.Items.Add(opItem);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                int accepted = ReadAcceptedAt(pkg, 0);
                CompleteAdd(srcInv, item, opItem, accepted, status, rev, container, onDone, alreadyRemoved, disp);
            }, 0L, null, 1);
        }

        internal static void RequestAddBatch(Container container, Inventory srcInv, List<TxOpItem> items, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone)
        {
            if (items == null || items.Count == 0)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.AddBatch;
            call.Items.AddRange(items);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                if (disp == TxCompletionKind.Indeterminate)
                {
                    // Terminal: commitment unknown. Remove nothing, retry nothing —
                    // the chest may already hold the items. Loud, never silent.
                    TxLog.Warn("addbatch indeterminate: outcome unknown, nothing removed — check the chest before retrying");
                    TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
                    RefreshNow(container);
                    if (onDone != null)
                        onDone(EmptyCountBody(), status, rev, disp);
                    return;
                }
                if (disp == TxCompletionKind.CommittedMultiAddDetailsUnavailable)
                {
                    // Committed but per-item counts unknown (ring replay after handoff):
                    // the aggregate cannot be attributed, so remove nothing and let
                    // the caller reconcile manually. Terminal: no cascade/retry.
                    TxLog.Warn("addbatch details-unavailable: committed but unattributed, nothing removed");
                    TellPlayer("Chest applied the move but per-item counts are unknown. Check the chest before retrying.");
                    RefreshNow(container);
                    if (onDone != null)
                        onDone(EmptyCountBody(), status, rev, disp);
                    return;
                }
                List<int> accepted = ReadAcceptedList(pkg, items.Count);
                for (int i = 0; i < items.Count && i < accepted.Count; i++)
                    CompleteAdd(srcInv, null, items[i], accepted[i], status, rev, container, null, false, disp);
                RefreshNow(container);
                if (onDone != null)
                    onDone(pkg, status, rev, disp);
            }, 0L, null, items.Count);
        }

        /// <summary>
        /// Add-intent snapshot: no positional want (auto-place/merge on the manager).
        /// Player-grid coordinates must never leak into chest cells (pos-blocked rejects).
        /// </summary>
        internal static TxOpItem SnapshotAuto(ItemData item, int amount)
        {
            TxOpItem op = SnapshotItem(item, amount, -1, -1);
            if (op != null)
            {
                op.X = -1;
                op.Y = -1;
            }
            return op;
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

        internal static void RequestTake(Container container, Inventory dstInv, ItemData snapshot, int amount, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone, int wantDstX = -1, int wantDstY = -1)
        {
            TxOpItem opItem = SnapshotItem(snapshot, amount, -1, -1);
            if (opItem == null)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Take;
            call.Items.Add(opItem);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                CompleteTake(dstInv, pkg, status, rev, container, onDone, wantDstX, wantDstY, null, disp);
            });
        }

        internal static void RequestTakeBatchChunked(Container container, Inventory dstInv, List<TxOpItem> items, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone)
        {
            if (items == null || items.Count == 0)
                return;
            if (items.Count <= ChestTxService.BatchChunkSize)
            {
                RequestTakeBatch(container, dstInv, items, onDone);
                return;
            }
            for (int i = 0; i < items.Count; i += ChestTxService.BatchChunkSize)
            {
                List<TxOpItem> part = new List<TxOpItem>();
                for (int j = i; j < items.Count && j < i + ChestTxService.BatchChunkSize; j++)
                    part.Add(items[j]);
                RequestTakeBatch(container, dstInv, part, onDone);
            }
        }

        internal static void RequestTakeBatch(Container container, Inventory dstInv, List<TxOpItem> items, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone)
        {
            if (items == null || items.Count == 0)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.TakeBatch;
            call.Items.AddRange(items);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                CompleteTake(dstInv, pkg, status, rev, container, onDone, -1, -1, null, disp);
            });
        }

        internal static void RequestMove(Container container, ItemData snapshot, int amount, int dstX, int dstY, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone)
        {
            TxOpItem opItem = SnapshotItem(snapshot, amount, -1, -1);
            if (opItem == null)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Move;
            call.Items.Add(opItem);
            call.DstX = dstX;
            call.DstY = dstY;
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                RefreshNow(container);
                if (status == TxStatus.UnknownTx)
                    TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
                else if (status == TxStatus.Rejected)
                    TellPlayer("The shared chest changed. Try that item move again.");
                if (onDone != null)
                    onDone(pkg, status, rev, disp);
            });
        }

        internal static void RequestSort(Container container, int mode, bool desc)
        {
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Sort;
            call.Mode = mode;
            call.Desc = desc;
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                RefreshNow(container);
                if (status == TxStatus.UnknownTx)
                    TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
                else if (status != TxStatus.Accepted && status != TxStatus.Duplicate)
                    TellPlayer("Chest sort failed. Try again.");
            });
        }

        internal static void RequestSetRule(Container container, string rule)
        {
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.SetRule;
            call.Rule = rule ?? string.Empty;
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                RefreshNow(container);
                if (status == TxStatus.UnknownTx)
                    TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
                else if (status != TxStatus.Accepted && status != TxStatus.Duplicate)
                    TellPlayer("Chest rule was not saved. Try again.");
            });
        }

        /// <summary>
        /// Manager-local mutation (own GUI/automation on the owner):
        /// goes into the same chest queue and runs immediately, serially with remote ones.
        /// </summary>
        internal static void MutateLocal(Container container, TxOpCall call, Action<StoredResult> onDone, long playerId = 0L, Vector3? actorPos = null)
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
            System.Collections.Generic.List<ItemDrop.ItemData> claimed;
            if (!TryClaimAddItems(call, out claimed))
            {
                TxLog.Info("local add skipped: all items already in flight");
                if (onDone != null)
                {
                    StoredResult empty = new StoredResult();
                    empty.Op = call.Op;
                    empty.Status = TxStatus.Accepted;
                    empty.Revision = CurrentRevision(container);
                    onDone(empty);
                }
                return;
            }
            try
            {
            TxJob job = new TxJob();
            job.Container = container;
            job.IsLocal = true;
            job.TxId = NextLocalTxId();
            job.Sender = ZNet.GetUID();
            job.PlayerId = playerId != 0L ? playerId : LocalPlayerId();
            job.ActorPos = actorPos.HasValue ? actorPos.Value : LocalActorPos();
            job.BaseRev = CurrentRevision(container);
            job.Call = call;
            job.Complete = onDone;
            if (state.RingCorrupt)
            {
                // Fail closed: persisted state untrustworthy, mutate nothing.
                TxLog.Warn("tx=" + job.TxId + " local refused: ring corrupt (fail closed)");
                if (onDone != null)
                {
                    StoredResult u = new StoredResult();
                    u.Op = call.Op;
                    u.Status = TxStatus.UnknownTx;
                    u.Revision = CurrentRevision(container);
                    onDone(u);
                }
                return;
            }
            if (IsFloorStale(state, job.TxId))
            {
                // Same-txId/counter already recorded: indeterminate, never execute.
                TxLog.Warn("tx=" + job.TxId + " local refused: at/below execution floor (indeterminate)");
                if (onDone != null)
                {
                    StoredResult u = new StoredResult();
                    u.Op = call.Op;
                    u.Status = TxStatus.UnknownTx;
                    u.Revision = CurrentRevision(container);
                    onDone(u);
                }
                return;
            }
            if (IsFloorCappedFor(state, job.TxId))
            {
                TxLog.Warn("tx=" + job.TxId + " local refused: execution floor at capacity (fail closed)");
                if (onDone != null)
                {
                    StoredResult u = new StoredResult();
                    u.Op = call.Op;
                    u.Status = TxStatus.UnknownTx;
                    u.Revision = CurrentRevision(container);
                    onDone(u);
                }
                return;
            }
            state.Queue.Enqueue(job);
            Drain(state);
            }
            finally
            {
                ReleaseClaimed(claimed);
            }
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
                    // Queued-but-unapplied jobs must keep a stable outcome: seed
                    // them as Rejected in the FRESH cache (nothing was applied, so
                    // the sender retries as a NEW tx) instead of letting the same
                    // txId execute later and flip to Accepted.
                    List<TxJob> dropped = DequeueAll(state);
                    state.Processed.Clear();
                    state.ProcOrder.Clear();
                    SeedFromRing(state, ReadRing(container));
                    foreach (TxJob dj in dropped)
                        SeedReject(state, dj.TxId, dj.Call != null ? dj.Call.Op : TxOp.Query, dj.Sender, true);
                    foreach (TxJob dj in dropped)
                        AnswerReject(state, dj);
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

        /// <summary>
        /// In-flight deduplicator for Adds: the same live ItemData must never be committed
        /// twice (quickstack snapshots every chest from one inventory state; a double click
        /// re-sends the same stack). First submit wins; later duplicates are pruned.
        /// Takes are safe by re-resolution on the manager and are not tracked.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<ItemDrop.ItemData> InFlightAdds = new System.Collections.Generic.HashSet<ItemDrop.ItemData>();

        private static bool TryClaimAddItems(TxOpCall call, out System.Collections.Generic.List<ItemDrop.ItemData> claimed)
        {
            claimed = null;
            if (call == null || (call.Op != TxOp.Add && call.Op != TxOp.AddBatch))
                return true;
            for (int i = call.Items.Count - 1; i >= 0; i--)
            {
                TxOpItem it = call.Items[i];
                ItemDrop.ItemData src = it != null ? it.SourceRef : null;
                if (src == null)
                    continue;
                if (InFlightAdds.Add(src))
                {
                    if (claimed == null)
                        claimed = new System.Collections.Generic.List<ItemDrop.ItemData>();
                    claimed.Add(src);
                }
                else
                {
                    call.Items.RemoveAt(i);
                    TxLog.Info("add submit pruned duplicate in-flight item (already sending)");
                }
            }
            return call.Items.Count > 0;
        }

        private static void ReleaseClaimed(System.Collections.Generic.List<ItemDrop.ItemData> claimed)
        {
            if (claimed == null)
                return;
            for (int i = 0; i < claimed.Count; i++)
            {
                if (claimed[i] != null)
                    InFlightAdds.Remove(claimed[i]);
            }
        }

        private static void Submit(Container container, TxOpCall call, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone, long playerId = 0L, Vector3? actorPos = null, int expectedItems = -1)
        {
            if (!Plugin.IsActive || !IsShared(container))
            {
                if (AutoFeedService.IsLocked(container))
                    TellPlayer("Chest is busy (animal feeding in progress). Try again shortly.");
                else
                    TellPlayer("Shared chest is not available.");
                return;
            }
            if (container.IsOwner())
            {
                // Manager: enqueue locally, run right away. Synthesize the same
                // result body remote managers send (never null: decoders void takes).
                MutateLocal(container, call, delegate (StoredResult r)
                {
                    RefreshNow(container);
                    if (onDone != null)
                    {
                        ZPackage body = null;
                        try
                        {
                            body = EncodeResultBody(call, r);
                            if (body != null)
                                body.SetPos(0);
                        }
                        catch
                        {
                        }
                        TxStatus st = (r != null) ? r.Status : TxStatus.UnknownTx;
                        bool totals = (r != null) && r.TotalsOnly;
                        onDone(body, st, (r != null) ? r.Revision : CurrentRevision(container),
                            TxResponsePolicy.Classify(st, totals, call.Op, call.Items.Count));
                    }
                }, playerId, actorPos);
                return;
            }
            System.Collections.Generic.List<ItemDrop.ItemData> claimed;
            if (!TryClaimAddItems(call, out claimed))
            {
                TxLog.Info("add submit skipped: all items already in flight");
                if (onDone != null)
                {
                    // The live sibling tx carries these items; answer empty-accepted
                    // so cascades/automation waiting on the callback do not stall.
                    // Empty body reads as accepted=0: nothing is removed twice.
                    try
                    {
                        onDone(new ZPackage(), TxStatus.Accepted, CurrentRevision(container), TxCompletionKind.Normal);
                    }
                    catch (Exception ex)
                    {
                        TxLog.Error("pruned-add completion failed: " + ex.Message);
                    }
                }
                return;
            }
            long txId = TxIdGen.Next(ZNet.GetUID(), ref _clientCounter);
            ZPackage payload = EncodeCall(call, CurrentRevision(container), playerId, actorPos);
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
            pending.Claimed = claimed;
            pending.Attempts = 0;
            pending.ExpectedItems = expectedItems;
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

        private static ZPackage EncodeCall(TxOpCall call, uint baseRev, long playerId, Vector3? actorPos)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(TxCodec.ProtoVersion);
            pkg.Write((int)call.Op);
            pkg.Write(baseRev);
            pkg.Write(playerId != 0L ? playerId : LocalPlayerId());
            pkg.Write(actorPos.HasValue ? actorPos.Value : LocalActorPos());
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
                // Undecodable: indeterminate (never applied, never cached). The
                // client resends byte-identical bytes on timeout, so the retry
                // answers the same way — stable without poisoning the txId.
                TxLog.Warn("tx=" + txId + " undecodable request from " + sender);
                Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, TxOp.Query);
                return;
            }
            ChestState state = GetState(container);
            if (state == null)
                return;
            if (!TxNet.IsCompatiblePeer(sender) && sender != ZNet.GetUID())
            {
                // Cache first: a committed txId keeps its ORIGINAL outcome even
                // when re-sent over a version-skewed link — never overwrite it
                // with a fresh Rejected (same sender-mismatch rule as replays).
                if (TryRespondCached(container, sender, txId, state))
                    return;
                // Deterministic version mismatch: persist the definitive Rejected
                // so the same txId can never execute later (not even after handoff).
                TxLog.Warn("tx=" + txId + " REJECT incompatible peer " + sender);
                SeedReject(state, txId, call.Op, sender, true);
                WriteRing(state);
                Respond(container, sender, txId, TxStatus.Rejected, CurrentRevision(container), new ZPackage(), false, call.Op);
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
            if (sender != TxIdGen.PeerOf(txId))
            {
                // Cache first: a committed txId keeps its ORIGINAL outcome even
                // when re-sent with mismatched sender bytes — never overwrite
                // it with a fresh spoof Rejected (stranger => Rejected without
                // payload; true sender => original status/body).
                if (TryRespondCached(container, sender, txId, state))
                    return;
                // Spoofed txId (authenticated sender disagrees with the txId peer
                // bits). Cache Rejected under the PEER (not the spoofer) without
                // advancing the floor: the exact counter is blocked, lower/upper
                // counters of the true owner are unaffected.
                TxLog.Warn("tx=" + txId + " REJECT sender/txId mismatch sender=" + sender);
                SeedReject(state, txId, call.Op, TxIdGen.PeerOf(txId), false);
                WriteRing(state);
                Respond(container, sender, txId, TxStatus.Rejected, CurrentRevision(container), new ZPackage(), false, call.Op);
                return;
            }
            StoredResult replay;
            if (state.Processed.TryGetValue(txId, out replay) && replay != null)
            {
                // Authenticated replay BEFORE access re-evaluation: a committed
                // retry must return its original outcome even after permissions
                // changed — re-checking access here would flip it to Rejected.
                if (replay.Sender != 0 && replay.Sender != sender)
                {
                    // Stranger asking for another sender's tx: reject WITHOUT the
                    // cached payload (never leak Take bytes across senders).
                    TxLog.Warn("tx=" + txId + " REJECT replay sender mismatch sender=" + sender);
                    Respond(container, sender, txId, TxStatus.Rejected, replay.Revision, new ZPackage(), false, replay.Op);
                    return;
                }
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + txId + " REPLAY status=" + replay.Status);
                Respond(container, sender, txId, replay.Status, replay.Revision,
                    EncodeCachedBody(replay), replay.TotalsOnly, replay.Op);
                return;
            }
            if (state.RingCorrupt)
            {
                // Fail closed: persisted state untrustworthy, mutate nothing.
                TxLog.Warn("tx=" + txId + " refused: ring corrupt (fail closed)");
                Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, call.Op);
                return;
            }
            if (IsFloorStale(state, txId))
            {
                // At/below the sender's high-water with no record: evicted,
                // rejected-and-forgotten, or out-of-order. Indeterminate — never
                // execute (a forgotten Rejected would flip to Accepted here).
                TxLog.Warn("tx=" + txId + " refused: at/below execution floor (indeterminate, never executes)");
                Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, call.Op);
                return;
            }
            if (IsFloorCappedFor(state, txId))
            {
                // The floor never evicts: refuse new-peer mutations loudly.
                // Nothing cached or executed, so retries answer identically.
                TxLog.Warn("tx=" + txId + " refused: execution floor at capacity (fail closed)");
                Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, call.Op);
                return;
            }
            string accessWhy;
            if (!ChestAuthority.CanUse(container, sender, playerId, actorPos, out accessWhy))
            {
                // Persist the definitive Rejected: same txId, same answer forever.
                LogAccessReject(container, txId, sender, playerId, call.Op, accessWhy);
                SeedReject(state, txId, call.Op, sender, true);
                WriteRing(state);
                Respond(container, sender, txId, TxStatus.Rejected, CurrentRevision(container), new ZPackage(), false, call.Op);
                return;
            }
            TxJob job = new TxJob();
            job.Container = container;
            job.TxId = txId;
            job.Sender = sender;
            job.PlayerId = playerId;
            job.BaseRev = baseRev;
            job.ActorPos = actorPos;
            job.Call = call;
            job.Complete = delegate (StoredResult r)
            {
                ZPackage body = EncodeResultBody(call, r);
                Respond(container, sender, txId, r.Status, r.Revision, body, r.TotalsOnly, call.Op);
            };
            state.Queue.Enqueue(job);
            Drain(state);
        }

        /// <summary>
        /// Rejection diagnostics (issue #10): container/tx/sender/claimed/resolved/
        /// server-sender/op/reason/owner/local-uid. Logged only on rejection.
        /// </summary>
        private static void LogAccessReject(Container container, long txId, long sender, long claimedPlayerId, TxOp op, string why)
        {
            string zid = "?";
            long owner = 0L;
            try
            {
                ZNetView nv = TxReflect.GetNetView(container);
                if ((Object)nv != (Object)null && nv.IsValid())
                {
                    zid = TxLog.Zid(nv.GetZDO().m_uid);
                    owner = nv.GetZDO().GetOwner();
                }
            }
            catch
            {
            }
            string resolved = "none";
            try
            {
                long resolvedId;
                Vector3 resolvedPos;
                if (ChestAuthority.ResolveActor(sender, out resolvedId, out resolvedPos))
                    resolved = resolvedId.ToString();
            }
            catch
            {
            }
            bool senderIsServer = false;
            try
            {
                senderIsServer = ChestAuthority.IsServerSenderOrSelf(sender);
            }
            catch
            {
            }
            long localUid = 0L;
            try
            {
                localUid = ZNet.GetUID();
            }
            catch
            {
            }
            TxLog.Warn("tx=" + txId + " REJECT access(" + why + ") container=" + zid
                + " sender=" + sender + " claimed=" + claimedPlayerId + " resolved=" + resolved
                + " senderIsServer=" + senderIsServer + " op=" + op
                + " owner=" + owner + " local=" + localUid);
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
                    StoredResult result;
                    try
                    {
                        result = ApplyJob(state, job);
                    }
                    catch (Exception ex)
                    {
                        // The op may have mutated before throwing (inventory and
                        // ring writes are not atomic): commitment is unknowable, so
                        // answer INDETERMINATE — never a definitive Rejected that a
                        // later retry could contradict by executing. The floor still
                        // advances so the same txId can never execute later.
                        TxLog.Error("tx=" + job.TxId + " apply failed: " + ex.Message);
                        result = new StoredResult();
                        result.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                        result.Status = TxStatus.UnknownTx;
                        result.Revision = CurrentRevision(state.Container);
                        AdvanceFloor(state, job.TxId);
                        WriteRing(state);
                    }
                    try
                    {
                        if (job.Complete != null)
                            job.Complete(result);
                    }
                    catch (Exception ex)
                    {
                        TxLog.Error("tx=" + job.TxId + " completion failed: " + ex.Message);
                    }
                    if (!result.IsReplay
                        && (job.Call.Op == TxOp.Add || job.Call.Op == TxOp.AddBatch || job.Call.Op == TxOp.TakeBatch)
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
            // Idempotency is absolute: the ORIGINAL outcome is returned and a
            // committed tx is NEVER re-applied, even when the cache entry is
            // totals-only after a handoff. Re-pulling live stock for a Take retry
            // double-debits the chest while the client credits once (or zero
            // times on Query). Authenticated replay identity is checked first:
            // a stranger gets Rejected with no payload, never another sender's
            // Take bytes.
            StoredResult cached;
            if (state.Processed.TryGetValue(job.TxId, out cached))
            {
                if (cached.Sender != 0 && cached.Sender != job.Sender)
                {
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " REPLAY sender mismatch");
                    StoredResult denied = new StoredResult();
                    denied.Op = cached.Op;
                    denied.Status = TxStatus.Rejected;
                    denied.Revision = cached.Revision;
                    return denied;
                }
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " REPLAY status=" + cached.Status);
                return CloneStored(cached, cached.Status, true);
            }
            if (state.RingCorrupt)
            {
                // Fail closed (recheck: the ring may have loaded corrupt after
                // the request was enqueued). Mutate nothing.
                StoredResult u = new StoredResult();
                u.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                u.Status = TxStatus.UnknownTx;
                u.Revision = CurrentRevision(job.Container);
                return u;
            }
            if (IsFloorStale(state, job.TxId))
            {
                // Recheck: a same-peer newer tx may have committed while queued.
                StoredResult u = new StoredResult();
                u.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                u.Status = TxStatus.UnknownTx;
                u.Revision = CurrentRevision(job.Container);
                return u;
            }
            Inventory inv = job.Container.GetInventory();
            if (inv == null)
            {
                // Persist the rejection like any other terminal outcome so the
                // same txId keeps its answer (CommitResult caches everything).
                StoredResult result = new StoredResult();
                result.Op = job.Call.Op;
                result.Status = TxStatus.Rejected;
                CommitResult(state, job, result);
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " REJECT op=" + job.Call.Op);
                return result;
            }
            StoredResult applied = ExecuteCall(state, job, inv);
            CommitResult(state, job, applied);
            uint baseRev = job.BaseRev;
            uint now = CurrentRevision(job.Container);
            if (baseRev != 0u && baseRev != now && baseRev != applied.Revision)
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " stale_revision client=" + baseRev + " server=" + now);
            return applied;
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
                // Conservation clamp: it.Amount was planned when the batch/cascade was
                // built; an earlier operation may already have consumed part of the same
                // physical source stack. For local/owner submissions SourceRef is the
                // live client-side stack — never credit more than it still backs.
                // Remote operations (SourceRef == null, never serialized) keep current
                // behavior. This complements, not duplicates, the in-flight Claimed
                // guard (TryClaimAddItems), which dedups the same ItemData within one
                // submit but cannot see staleness across sequential operations.
                int amount = it.Amount;
                if (it.SourceRef != null)
                {
                    int liveStack = Math.Max(0, it.SourceRef.m_stack);
                    int clamped = TxAddConservation.ClampAddAmount(amount, liveStack);
                    if (clamped < amount)
                    {
                        TxLog.Warn("op=ADD source-clamp item=" + TxCodec.Describe(iname, it.Amount)
                            + " requested=" + amount + " clamped=" + clamped
                            + " liveSource=" + liveStack);
                        amount = clamped;
                    }
                }
                ItemData clone = it.Snapshot.Clone();
                clone.m_stack = amount;
                int accepted = 0;
                string why = "";
                if (amount <= 0)
                {
                    // Source depleted after planning (cascade/batch consumed it):
                    // credit nothing, record accepted 0, never count as full.
                    why = "source-depleted";
                }
                else if (!call.EnforceRule || QuickStackTransfer.CanAcceptFromRule(rule, clone, destNames, destCats))
                {
                    // Drag&drop with a requested cell goes strictly positional (like vanilla),
                    // otherwise merge/auto-place. No fallback: the remainder stays with the client.
                    if (it.X >= 0 && it.Y >= 0)
                    {
                        accepted = TxInventory.AddPositional(inv, clone, amount, it.X, it.Y);
                        if (accepted < amount)
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
                        if (accepted < amount)
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
                TxLog.Info("op=ADD item=" + TxCodec.Describe(iname, amount) + " want=(" + it.X + "," + it.Y + ") accepted=" + accepted + (why.Length > 0 ? " why=" + why : ""));
                result.Accepted.Add(accepted);
                if (TxAddConservation.CountsAsFull(amount, accepted))
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
            }
            // Post-inventory-save commit checkpoint: captured BEFORE the ring
            // ZDO write and used consistently in the result, the RAM cache,
            // the ring entry and the response. Never re-read DataRevision after
            // ZDO.Set(RingKey): the ring write itself may bump the revision, so
            // a read-back would describe the ring write, not the commit.
            result.Revision = CurrentRevision(state.Container);
            // Authenticated committer, stamped before caching: replays check it.
            result.Sender = job.Sender;
            result.IsReplay = false;
            // EVERY terminal outcome is cached (including Rejected) BEFORE the
            // ring snapshot is built: an immediate handoff otherwise exposes a
            // ring without its idempotency record and the replay re-applies
            // (double-apply) or flips Rejected to Accepted.
            state.Processed[job.TxId] = CloneStored(result, result.Status, false);
            state.ProcOrder.AddLast(job.TxId);
            while (state.ProcOrder.Count > TxLimits.ProcessedCacheCap)
            {
                long oldest = state.ProcOrder.First.Value;
                state.ProcOrder.RemoveFirst();
                state.Processed.Remove(oldest);
            }
            AdvanceFloor(state, job.TxId);
            // The ring carries every terminal outcome now (v2), so it is
            // rewritten on rejections too — WriteRing skips the ZDO write when
            // the bytes are unchanged.
            WriteRing(state);
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " peer=" + job.Sender
                + " op=" + job.Call.Op + " accepted=" + result.AcceptedTotal() + " revision=" + result.Revision);
        }

        private static StoredResult RejectNew()
        {
            StoredResult result = new StoredResult();
            result.Status = TxStatus.Rejected;
            return result;
        }

        private static StoredResult CloneStored(StoredResult src, TxStatus status, bool isReplay)
        {
            StoredResult r = new StoredResult();
            r.Op = src.Op;
            r.Status = status;
            r.Revision = src.Revision;
            r.Accepted = new List<int>(src.Accepted);
            r.Takes = new List<TakeEntry>(src.Takes);
            r.TotalsOnly = src.TotalsOnly;
            r.Sender = src.Sender;
            r.IsReplay = isReplay;
            return r;
        }

        private static bool IsTakeOp(TxOp op)
        {
            return op == TxOp.Take || op == TxOp.TakeBatch;
        }

        // ============================ manager: execution floor ============================

        /// <summary>
        /// Durable per-sender execution floor: peer -&gt; highest tx counter seen.
        /// Capped at FloorCap peers, NEVER evicted — reaching the cap refuses
        /// new-peer mutations loudly (fail closed) instead of recreating the
        /// eviction bug. An absent txId at/below high-water is indeterminate.
        /// </summary>
        private static void AdvanceFloor(ChestState state, long txId)
        {
            long peer = TxIdGen.PeerOf(txId);
            uint ctr = (uint)(txId & 0xFFFFFFFFL);
            uint hw;
            if (state.Floor.TryGetValue(peer, out hw))
            {
                if (ctr > hw)
                    state.Floor[peer] = ctr;
            }
            else if (state.Floor.Count < TxLimits.FloorCap)
            {
                state.Floor[peer] = ctr;
            }
        }

        private static bool IsFloorStale(ChestState state, long txId)
        {
            if (state.Processed.ContainsKey(txId))
                return false;
            uint hw;
            if (!state.Floor.TryGetValue(TxIdGen.PeerOf(txId), out hw))
                return false;
            return (uint)(txId & 0xFFFFFFFFL) <= hw;
        }

        private static bool IsFloorCappedFor(ChestState state, long txId)
        {
            if (state.Processed.ContainsKey(txId))
                return false;
            if (state.Floor.ContainsKey(TxIdGen.PeerOf(txId)))
                return false;
            return state.Floor.Count >= TxLimits.FloorCap;
        }

        /// <summary>
        /// Answer from the idempotency cache for pre-replay branches
        /// (version-skew / spoof resends): a committed txId keeps its ORIGINAL
        /// outcome — the caller must return without seeding anything.
        /// Same sender-mismatch rule as replays: a stranger gets Rejected with
        /// no payload (never another sender's Take bytes); the true sender
        /// replays the original status/body. Returns true when answered.
        /// </summary>
        private static bool TryRespondCached(Container container, long sender, long txId, ChestState state)
        {
            StoredResult hit;
            if (!state.Processed.TryGetValue(txId, out hit) || hit == null)
                return false;
            if (hit.Sender != 0L && hit.Sender != sender)
            {
                TxLog.Warn("tx=" + txId + " cached answer sender mismatch sender=" + sender);
                Respond(container, sender, txId, TxStatus.Rejected, hit.Revision, new ZPackage(), false, hit.Op);
                return true;
            }
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + txId + " CACHED status=" + hit.Status);
            Respond(container, sender, txId, hit.Status, hit.Revision,
                EncodeCachedBody(hit), hit.TotalsOnly, hit.Op);
            return true;
        }

        /// <summary>
        /// Seed a definitive Rejected outcome into the fresh cache (pre-queue
        /// rejects: incompatible peer, access denial, spoof) so the same txId
        /// keeps its answer forever. Advances the floor unless told otherwise
        /// (spoof entries must not push the true owner's high-water).
        /// Never overwrites a committed outcome: pre-replay callers answer from
        /// cache first (TryRespondCached), and this guard keeps the seeding
        /// itself idempotent (no outcome flip, no duplicate ProcOrder entry).
        /// </summary>
        private static void SeedReject(ChestState state, long txId, TxOp op, long storedSender, bool advanceFloor)
        {
            if (state.Processed.ContainsKey(txId))
                return;
            StoredResult r = new StoredResult();
            r.Op = op;
            r.Status = TxStatus.Rejected;
            try
            {
                ZNetView nv = TxReflect.GetNetView(state.Container);
                r.Revision = (nv != null && nv.IsValid()) ? nv.GetZDO().DataRevision : 0u;
            }
            catch
            {
                r.Revision = 0u;
            }
            r.Sender = storedSender;
            r.IsReplay = false;
            state.Processed[txId] = r;
            if (!state.ProcOrder.Contains(txId))
                state.ProcOrder.AddLast(txId);
            while (state.ProcOrder.Count > TxLimits.ProcessedCacheCap)
            {
                long oldest = state.ProcOrder.First.Value;
                state.ProcOrder.RemoveFirst();
                state.Processed.Remove(oldest);
            }
            if (advanceFloor)
                AdvanceFloor(state, txId);
        }

        /// <summary>Drop the whole queue without answering (caller seeds then answers).</summary>
        private static List<TxJob> DequeueAll(ChestState state)
        {
            List<TxJob> dropped = new List<TxJob>(state.Queue.Count);
            while (state.Queue.Count > 0)
                dropped.Add(state.Queue.Dequeue());
            return dropped;
        }

        /// <summary>Answer one dropped queued-but-unapplied job as Rejected (nothing applied).</summary>
        private static void AnswerReject(ChestState state, TxJob job)
        {
            try
            {
                StoredResult r = new StoredResult();
                r.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                r.Status = TxStatus.Rejected;
                try
                {
                    ZNetView nv = TxReflect.GetNetView(state.Container);
                    r.Revision = (nv != null && nv.IsValid()) ? nv.GetZDO().DataRevision : 0u;
                }
                catch
                {
                    r.Revision = 0u;
                }
                r.Sender = job.Sender;
                if (job.Complete != null)
                    job.Complete(r);
            }
            catch (Exception ex)
            {
                TxLog.Error("tx=" + job.TxId + " reject-queued failed: " + ex.Message);
            }
        }

        // ============================ manager: ring persist ============================

        private static void WriteRing(ChestState state)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(state.Container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return;
                // ProcOrder is oldest-first and already includes the
                // just-committed tx (CommitResult caches before writing).
                // v2 persists EVERY terminal outcome with its original status:
                // a forgotten Rejected would otherwise resurrect as Accepted.
                List<RingSlot> slots = new List<RingSlot>(state.ProcOrder.Count);
                LinkedListNode<long> node = state.ProcOrder.First;
                while (node != null)
                {
                    StoredResult r;
                    if (state.Processed.TryGetValue(node.Value, out r) && r != null)
                    {
                        RingSlot s;
                        s.TxId = node.Value;
                        s.AcceptedTotal = r.AcceptedTotal();
                        s.Revision = r.Revision;
                        s.Status = r.Status;
                        s.Op = r.Op;
                        s.Sender = r.Sender != 0L ? r.Sender : TxIdGen.PeerOf(node.Value);
                        s.Accepted = new List<int>(r.Accepted);
                        s.TakePayloads = BuildRingTakePayloads(r);
                        slots.Add(s);
                    }
                    node = node.Next;
                }
                List<RingEntry> ring = TxRing.Snapshot(slots);
                // The next successful mutation rewrites v2; legacy v1 ZDO bytes
                // are only ever read (never migrated in place, no world migration).
                byte[] bytes = TxCore.TxCore.EncodeRingV2(ring, state.Floor);
                if (SameBytes(bytes, state.LastRingBytes))
                    return;
                state.LastRingBytes = bytes;
                netView.GetZDO().Set(RingKey, bytes);
            }
            catch (Exception ex)
            {
                TxLog.Warn("ring persist failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Take payloads for the ring: the ACTUAL debited item bytes
        /// (TakeEntry.Item.Save + prefab hash). Request snapshots are NOT
        /// sufficient: matching does not establish equality of custom data or
        /// durability, and a reserve-respecting Take may draw from a different
        /// live stack. Anything uncapturable (oversize, uncapturable, misaligned)
        /// yields null = inexact: the entry restores totals-only, never fabricated.
        /// </summary>
        private static List<TakePayload> BuildRingTakePayloads(StoredResult r)
        {
            if (r.Op != TxOp.Take && r.Op != TxOp.TakeBatch)
                return null;
            if (r.TotalsOnly)
                return null;
            if (r.Takes == null || r.Takes.Count != r.Accepted.Count)
                return null;
            List<TakePayload> blobs = new List<TakePayload>(r.Accepted.Count);
            for (int i = 0; i < r.Accepted.Count; i++)
            {
                if (r.Accepted[i] <= 0)
                {
                    blobs.Add(null);
                    continue;
                }
                TakeEntry e = r.Takes[i];
                if (e == null || e.Item == null)
                    return null;
                byte[] bytes;
                try
                {
                    ZPackage inner = new ZPackage();
                    e.Item.Save(inner);
                    bytes = inner.GetArray();
                }
                catch
                {
                    return null;
                }
                if (bytes == null || bytes.Length == 0 || bytes.Length > TxLimits.MaxTakePayloadBytes)
                    return null;
                TakePayload p = new TakePayload();
                p.PrefabHash = e.PrefabHash;
                p.Bytes = bytes;
                blobs.Add(p);
            }
            return blobs;
        }

        private static RingData ReadRing(Container container)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return new RingData();
                byte[] bytes = netView.GetZDO().GetByteArray(RingKey.GetStableHashCode());
                if (bytes == null)
                    return new RingData();
                return TxCore.TxCore.DecodeEnvelope(bytes);
            }
            catch (Exception)
            {
                RingData bad = new RingData();
                bad.Corrupt = true;
                return bad;
            }
        }

        private static void SeedFromRing(ChestState state, RingData data)
        {
            // True reset of the idempotency state, then restore. v2 entries keep
            // their ORIGINAL status with exact payloads when available; legacy
            // v1 entries (Status==Duplicate) restore totals-only. A corrupt
            // payload fails closed: empty caches + RingCorrupt (no mutation).
            state.Processed.Clear();
            state.ProcOrder.Clear();
            state.Floor.Clear();
            state.LastRingBytes = null;
            state.RingCorrupt = false;
            if (data == null)
                return;
            if (data.Corrupt)
            {
                state.RingCorrupt = true;
                TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " ring corrupt: fail closed (no mutations until trustworthy state)");
                return;
            }
            if (data.Floor != null)
            {
                foreach (KeyValuePair<long, uint> kv in data.Floor)
                {
                    if (state.Floor.Count >= TxLimits.FloorCap)
                        break;
                    state.Floor[kv.Key] = kv.Value;
                }
            }
            if (data.Entries == null)
                return;
            for (int i = 0; i < data.Entries.Count && i < TxLimits.RingCap; i++)
            {
                RingEntry e = data.Entries[i];
                StoredResult r = new StoredResult();
                r.Op = e.Op;
                r.Revision = e.Revision;
                r.Sender = e.Sender != 0L ? e.Sender : TxIdGen.PeerOf(e.TxId);
                r.IsReplay = false;
                if (e.Status == TxStatus.Duplicate)
                {
                    // Legacy v1: original status genuinely unavailable.
                    r.Status = TxStatus.Duplicate;
                    r.Accepted = new List<int> { e.AcceptedTotal };
                    r.TotalsOnly = true;
                }
                else if (TxRing.IsExactEntry(e))
                {
                    r.Status = e.Status;
                    r.Accepted = e.Accepted != null ? new List<int>(e.Accepted) : new List<int>();
                    r.TotalsOnly = false;
                    r.Takes = RestoreTakeEntries(e, r.Accepted, out bool takesOk);
                    if (!takesOk)
                    {
                        // Prefab gone: payloads undecodable — totals-only, no fabrication.
                        r.Takes = new List<TakeEntry>();
                        r.TotalsOnly = true;
                    }
                }
                else
                {
                    // Original status preserved, payloads gone: totals-only.
                    r.Status = e.Status;
                    r.Accepted = e.Accepted != null ? new List<int>(e.Accepted) : new List<int> { e.AcceptedTotal };
                    r.TotalsOnly = true;
                }
                state.Processed[e.TxId] = r;
                state.ProcOrder.AddLast(e.TxId);
            }
        }

        /// <summary>
        /// Rebuild Take entries from ring blobs (manager-side, ObjectDB available).
        /// Returns false when any payload is undecodable (caller falls back totals-only).
        /// </summary>
        private static List<TakeEntry> RestoreTakeEntries(RingEntry e, List<int> accepted, out bool ok)
        {
            ok = true;
            List<TakeEntry> takes = new List<TakeEntry>();
            if (e.Op != TxOp.Take && e.Op != TxOp.TakeBatch)
                return takes;
            if (e.TakePayloads == null || accepted == null || e.TakePayloads.Count != accepted.Count)
            {
                ok = false;
                return takes;
            }
            for (int i = 0; i < accepted.Count; i++)
            {
                if (accepted[i] <= 0)
                {
                    takes.Add(null);
                    continue;
                }
                TakePayload p = e.TakePayloads[i];
                if (p == null || p.Bytes == null)
                {
                    ok = false;
                    return new List<TakeEntry>();
                }
                ItemData item;
                try
                {
                    item = TxCodec.ResolvePrefab(p.PrefabHash, new ZPackage(p.Bytes));
                }
                catch
                {
                    item = null;
                }
                if (item == null)
                {
                    ok = false;
                    return new List<TakeEntry>();
                }
                TakeEntry te = new TakeEntry();
                te.PrefabHash = p.PrefabHash;
                te.Item = item;
                te.Accepted = accepted[i];
                takes.Add(te);
            }
            return takes;
        }
    }
}
