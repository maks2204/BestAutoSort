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
        internal const string FloorKey = "BestAutoSort_TxFloor";
        internal const string ItemsKey = "items";

        private static readonly Dictionary<int, ChestState> States = new Dictionary<int, ChestState>();
        private static readonly Dictionary<long, PendingTx> Pending = new Dictionary<long, PendingTx>();
        private static uint _clientCounter = 1;
        /// <summary>
        /// Observation-only diagnostic: how many times a fail-closed path saw
        /// null s_items and stayed quarantined instead of presuming empty
        /// (reload / takeover / structural-acquire). Monotonic, never reset
        /// except by Reset(). NO behavior change — handling is untouched; the
        /// count exists only for the runtime-verification checklist
        /// (docs/chesttx_runtime_verification.md).
        /// </summary>
        internal static long SItemsNullDenials;
        /// <summary>Exclusive upper bound of the persisted counter reservation block (0 = none reserved yet).</summary>
        private static uint _counterReservedCeil;
        private const uint CounterReserveBlock = 4096;
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
            _counterReservedCeil = 0;
            SItemsNullDenials = 0;
            LoadReservedCounter();
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
            if (job.TxId == 0L)
            {
                // Counter exhausted/unreserved: fail closed, known-not-committed.
                TxLog.Warn("local tx refused: counter exhausted (fail closed)");
                if (onDone != null)
                {
                    StoredResult r = new StoredResult();
                    r.Op = call.Op;
                    r.Status = TxStatus.Rejected;
                    r.Revision = CurrentRevision(container);
                    onDone(r);
                }
                return;
            }
            job.Sender = ZNet.GetUID();
            job.PlayerId = playerId != 0L ? playerId : LocalPlayerId();
            job.ActorPos = actorPos.HasValue ? actorPos.Value : LocalActorPos();
            job.BaseRev = CurrentRevision(container);
            job.Call = call;
            job.Complete = onDone;
            if (state.RingCorrupt && state.FloorCorrupt)
            {
                // Fully fail closed: both the ring and the floor are untrustworthy.
                // (Ring corrupt alone with an intact floor runs degraded: old-gen
                // blocked by the floor, new-gen above it commits fenced-first.)
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
            if (state.TxQuarantined)
            {
                // Fail-closed quarantine: live RAM may hold speculative inventory from
                // a post-fence failure. Fresh mutations never execute — the refusal is
                // a durable terminal through the shared matrix (Rejected only when the
                // seeded record is durable: ring AND floor; else Indeterminate with the
                // unpersisted seed evicted). The floor advance is kept (monotonic) but
                // a RAM-only floor is NOT durable protection. Local callers have no
                // Pending entry: always complete (a both-copies failure is still
                // UnknownTx here — nothing executed, so a retry as a NEW txId
                // applies at-most-once).
                bool anyDurable;
                TxStatus terminal = PersistQuarantineRefusal(state, job.TxId, call.Op, job.Sender, out anyDurable);
                if (terminal == TxStatus.Rejected)
                    RequestPropagation(state, job.Sender);
                TxLog.Warn("tx=" + job.TxId + " local refused: quarantined after post-fence failure (status=" + terminal + ", durable=" + anyDurable + ")");
                if (onDone != null)
                {
                    StoredResult u = new StoredResult();
                    u.Op = call.Op;
                    u.Status = terminal;
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
                // Dead branch retained for fail-closed shape: the floor is
                // UNBOUNDED (IsFloorCappedFor always returns false), so this
                // never fires. FloorCap survives only as a warn threshold.
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
                // Fence order (grant-before-state): ClaimOwnership ran FIRST
                // (above), this read runs AFTER. ZDO writes are owner-gated, so
                // once the claim holds no other peer can commit between our read
                // and our reseed below: the bytes include every pre-claim commit
                // and no post-claim commit can exist. The defensive clone breaks
                // any aliasing with the ZDO layer (GetByteArray may hand out the
                // live stored reference). A lost claim (owner recheck) abandons
                // the op instead of adopting possibly-stale bytes.
                byte[] bytes = TxSItemsGuard.CloneBytes(netView.GetZDO().GetByteArray(ZDOVars.s_items));
                if (!container.IsOwner())
                    return false;
                // Load the latest committed state before the structural operation.
                // Decoded-bytes proof first (length/version pre-check + RAM
                // snapshot fallback inside): null is unavailable, never empty.
                bool reloaded = false;
                string loadReason = null;
                if (TxSItemsLoad.TryLoad(container, bytes, out loadReason))
                {
                    TxReflect.SetLastRevision(container, netView.GetZDO().DataRevision);
                    TxReflect.UpdateRows(container);
                    reloaded = true;
                }
                ChestState state = GetState(container);
                if (state != null)
                {
                    List<TxJob> dropped = DequeueAll(state);
                    state.Processed.Clear();
                    state.ProcOrder.Clear();
                    SeedFromRing(state, ReadRing(container), ReadFloor(container));
                    // Queued-but-unapplied jobs keep a stable outcome ONLY when durable
                    // (shared seam TxDecision.HandoffDropTerminal): seeded Rejected in the
                    // FRESH cache (nothing was applied, so the sender retries as a NEW tx)
                    // instead of letting the same txId execute later and flip to Accepted.
                    // Rejected is answered only when durably persisted (ring + floor);
                    // otherwise each job keeps Indeterminate. On eviction-after-failure
                    // the floor advance is kept (never rolls back), so an in-session
                    // retry stays stale.
                    TxStatus droppedStatus = TxStatus.Rejected;
                    if (dropped.Count > 0)
                    {
                        System.Collections.Generic.List<long> seeded = new System.Collections.Generic.List<long>();
                        foreach (TxJob dj in dropped)
                            if (SeedReject(state, dj.TxId, dj.Call != null ? dj.Call.Op : TxOp.Query, dj.Sender, true))
                                seeded.Add(dj.TxId);
                        bool ringOk = WriteRing(state);
                        bool floorOk = ringOk ? WriteFloor(state) : false;
                        droppedStatus = TxDecision.HandoffDropTerminal(ringOk, floorOk);
                        if (droppedStatus == TxStatus.UnknownTx)
                        {
                            TxLog.Warn("structural acquire drops not persisted (indeterminate)");
                            foreach (long txId in seeded)
                                EvictSeededReject(state, txId);
                        }
                        // Reseed rewrote ZDO keys: propagation-request (never ACK).
                        RequestPropagation(state);
                    }
                    foreach (TxJob dj in dropped)
                        AnswerReject(state, dj, droppedStatus);
                    state.LastOwner = ZNet.GetUID();
                    // Structural reacquire reloads committed state + reseeds the ring:
                    // authoritative again, so a successful reload clears any prior
                    // quarantine. Null s_items means no reload ran: stay quarantined
                    // (never presumed empty) so speculative RAM never goes live.
                    if (reloaded)
                        state.TxQuarantined = false;
                    else
                    {
                        state.TxQuarantined = true;
                        if (bytes == null)
                        {
                            NoteSItemsNull("structural-acquire");
                            TxLog.Warn("structural acquire without authoritative reload (null s_items), staying quarantined");
                        }
                        else
                            TxLog.Warn("structural acquire without authoritative reload (invalid s_items: " + loadReason + "), staying quarantined");
                    }
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

        /// <summary>
        /// Issues the next local txId (0 = fail-closed: counter exhausted or the
        /// reservation could not be persisted). The counter is reserved in
        /// durable blocks as cheap defense-in-depth: ZNet UIDs are EPHEMERAL
        /// per-process values (NOT SteamIDs — a relaunch normally yields a fresh
        /// UID, i.e. a fresh floor peer), so the reservation only matters in the
        /// rare case a UID repeats with a reset counter. The floor is the real
        /// guard (a same-UID reset counter hits the high-water and answers
        /// Indeterminate — never double-applies). Counter wrap fails closed loudly.
        /// </summary>
        private static long IssueTxId()
        {
            if (!ReserveTxCounter())
                return 0L;
            long txId;
            if (!TxIdGen.TryNext(ZNet.GetUID(), ref _clientCounter, out txId))
            {
                TxLog.Warn("tx counter exhausted (wrap): refusing new transactions (fail closed)");
                return 0L;
            }
            return txId;
        }

        private static long NextLocalTxId()
        {
            return IssueTxId();
        }

        private static bool ReserveTxCounter()
        {
            // Fail-closed issuance latch (see LoadReservedCounter): a corrupt load
            // with evidence refuses ALL new txIds (IssueTxId 0) instead of
            // restarting at 1 where a reused UID could collide below the floor.
            if (_counterFailClosed)
                return false;
            // Pure rule in the shared seam (TxCounterFile.TryReserve, pinned by
            // HARDENING_Counter* tests): refuses fail-closed at 0/MaxValue and
            // when the next block would reach MaxValue, so the counter can
            // never wrap silently. Only a successful ATOMIC persist advances
            // the reservation ceil.
            uint newCeil;
            if (!TxCounterFile.TryReserve(_clientCounter, _counterReservedCeil, CounterReserveBlock, out newCeil))
                return false;
            if (newCeil == _counterReservedCeil)
                return true;
            if (!PersistReservedCounter(newCeil))
            {
                TxLog.Warn("tx counter reservation persist failed: refusing new transactions (fail closed)");
                return false;
            }
            _counterReservedCeil = newCeil;
            return true;
        }

        private static string CounterPath()
        {
            try
            {
                return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "BestAutoSort_TxCounter.txt");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Crash-resistant reservation persist through the shared seam
        /// (TxCounterFile.WriteAtomically: temp + OS flush + Replace with a
        /// backup copy, delete+move fallback, best-effort backup fsync). False =
        /// persisted NOTHING: the caller refuses new txIds fail-closed.
        /// Terminology: crash-RESISTANT, not crash-atomic — the Replace path is
        /// atomic, but the delete+move fallback has a Delete-to-Move window where
        /// the primary is absent (the backup copy covers it; the next load
        /// restores from backup). A torn temp file never parses.
        /// </summary>
        private static bool PersistReservedCounter(uint ceil)
        {
            try
            {
                string path = CounterPath();
                if (path == null)
                    return false;
                if (!TxCounterFile.WriteAtomically(path, ceil))
                {
                    TxLog.Warn("tx counter persist failed (atomic write refused)");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                TxLog.Warn("tx counter persist failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Explicit loader outcome for the counter reservation (see
        /// TxCounterFile.CounterLoadState): Fresh = no file evidence, may
        /// initialise at 1; Resumed* = trustworthy ceil loaded; FailClosed =
        /// evidence exists but nothing trustworthy — issuance refused (see
        /// _counterFailClosed), never a silent restart at 1.
        /// </summary>
        internal static TxCounterFile.CounterLoadState CounterLoadState = TxCounterFile.CounterLoadState.Fresh;
        /// <summary>
        /// Fail-closed issuance latch: set when the counter load found evidence
        /// but nothing trustworthy (both copies corrupt/unreadable) or no path
        /// at all. While set, ReserveTxCounter refuses and IssueTxId returns 0
        /// (loud, never a same-UID reset counter colliding below the floor).
        /// Cleared only by a successful load (Fresh or Resumed).
        /// </summary>
        private static bool _counterFailClosed;
        /// <summary>Test seam: true while counter issuance is fail-closed.</summary>
        internal static bool CounterIssuanceBlocked
        {
            get { return _counterFailClosed; }
        }

        private static void LoadReservedCounter()
        {
            _counterFailClosed = false;
            CounterLoadState = TxCounterFile.CounterLoadState.Fresh;
            try
            {
                string path = CounterPath();
                if (path == null)
                {
                    CounterLoadState = TxCounterFile.CounterLoadState.FailClosed;
                    _counterFailClosed = true;
                    TxLog.Warn("tx counter load failed: no counter path (fail closed: refusing new transactions)");
                    return;
                }
                // Evidence-aware load (shared seam TxCounterFile.ClassifyLoad): the
                // versioned format is checksum-checked, legacy plain-integer
                // files still read, and a torn/corrupt primary falls back to
                // the backup copy (restoring it best-effort). A MISSING primary
                // still consults the backup (a backup-only reservation is evidence
                // too). The persistent .blocked marker is checked BEFORE the Fresh
                // classification: archiving the corrupt copies removes the file
                // evidence, so without the marker a restart would see NEITHER file
                // and go Fresh (start at 1). Both copies untrustworthy WITH evidence =
                // write the marker FIRST, archive the evidence aside, then REFUSE
                // issuance fail-closed (never restart at 1: UIDs are ephemeral per
                // process so this normally belongs to a retired UID, but in the rare
                // UID-reuse case a reset counter would collide below the persisted
                // floor — safe only via refusal, since the floor stale-gates same-UID
                // replays to Indeterminate ONLY for txIds that were actually fenced).
                // Fresh (NEITHER file exists AND no marker) may initialise at 1.
                // Never invent a high counter here — that could skip fencing.
                // Repair: inspect the .corrupt-* sidecars, restore ONE trustworthy
                // counter file, delete the .blocked marker, restart (ResumedPrimary).
                string backupPath = TxCounterFile.BackupPathFor(path);
                bool blocked = TxCounterFile.BlockedMarkerPresent(path);
                bool primaryExists = false;
                bool backupExists = false;
                try { primaryExists = System.IO.File.Exists(path); }
                catch { }
                try { backupExists = System.IO.File.Exists(backupPath); }
                catch { }
                string primaryText = null;
                string backupText = null;
                try { if (primaryExists) primaryText = System.IO.File.ReadAllText(path); }
                catch { }
                try
                {
                    if (backupExists)
                        backupText = System.IO.File.ReadAllText(backupPath);
                }
                catch { }
                uint ceil;
                bool restoreBackup;
                CounterLoadState = TxCounterFile.ClassifyLoad(primaryExists, primaryText, backupExists, backupText, blocked, out ceil, out restoreBackup);
                if (blocked)
                {
                    // Persistent fail-closed marker present: a previous load already
                    // archived corrupt evidence aside and stamped this marker. Stay
                    // FailClosed across restarts WITHOUT archiving again — the files
                    // on disk (if any) may be a human-restored trustworthy copy
                    // awaiting only the marker deletion, and moving them aside
                    // would destroy the repair. Only the documented manual repair
                    // (restore ONE trustworthy counter file, delete the .blocked
                    // marker, restart) clears this; the loader never deletes it.
                    _counterFailClosed = true;
                    TxLog.Warn("tx counter issuance BLOCKED by persistent marker (fail closed: refusing new transactions; repair: restore ONE trustworthy counter file, delete the .blocked marker, restart)");
                    return;
                }
                if (CounterLoadState == TxCounterFile.CounterLoadState.ResumedPrimary)
                {
                    // Resume above everything reserved before the restart. UIDs are
                    // ephemeral per process, so this normally belongs to a retired UID
                    // (harmless); in the rare UID-reuse case it keeps the new session
                    // above its own old high-water instead of colliding with it.
                    _clientCounter = ceil;
                    _counterReservedCeil = ceil;
                    return;
                }
                if (CounterLoadState == TxCounterFile.CounterLoadState.ResumedBackup)
                {
                    _clientCounter = ceil;
                    _counterReservedCeil = ceil;
                    TxLog.Warn("tx counter primary corrupt, resumed from backup ceil=" + ceil + " (restoring primary best-effort)");
                    try { TxCounterFile.WriteAtomically(path, ceil); }
                    catch { }
                    return;
                }
                if (CounterLoadState == TxCounterFile.CounterLoadState.Fresh)
                {
                    TxLog.Info("tx counter: no reservation evidence, starting at 1");
                    return;
                }
                ArchiveCorruptCounter(path, backupPath);
                _counterFailClosed = true;
                TxLog.Warn("tx counter file corrupt (primary AND backup untrustworthy): evidence archived aside, REFUSING new transactions (fail closed, restart after inspecting the .corrupt-* sidecars)");
            }
            catch (Exception ex)
            {
                // Unreadable reservation is fail-closed: refuse issuance loudly.
                // Never invent a high counter here — that could skip fencing.
                CounterLoadState = TxCounterFile.CounterLoadState.FailClosed;
                _counterFailClosed = true;
                TxLog.Warn("tx counter load failed, refusing new transactions (fail closed): " + ex.Message);
            }
        }

        /// <summary>
        /// Corrupt-counter evidence preservation (diagnostic only): FIRST confirms
        /// the persistent fail-closed (.blocked) marker through the crash-resistant
        /// gate (tmp + OS flush + install, never overwrites an existing marker
        /// blindly — see TxCounterFile.ConfirmBlockedMarker), THEN renames
        /// untrustworthy primary/backup copies aside with a timestamp suffix so
        /// the failure stays inspectable. Marker-first ordering is safety, not
        /// tidiness: archiving REMOVES the file evidence, so a crash (or the
        /// next restart) between the archive and any later marker write would
        /// otherwise see "neither file exists" and go Fresh (restart issuance
        /// at 1 — a same-UID reset counter colliding below the persisted
        /// execution floor). With the marker written first, every later load is
        /// FailClosed until a human repairs. NO archive without a confirmed marker:
        /// when the marker cannot be confirmed the copies are left in place and
        /// issuance stays fail-closed in RAM (loud log) — a restart may lose the
        /// evidence, so inspect the copies immediately. Never throws. Does NOT invent a
        /// replacement counter — the caller refuses issuance fail-closed
        /// (IssueTxId 0) until restart after inspection.
        /// </summary>
        private static void ArchiveCorruptCounter(string path, string backupPath)
        {
            try
            {
                // Marker BEFORE the moves, through the crash-resistant gate: a
                // crash between marker and archive still leaves FailClosed
                // evidence either way. Unconfirmed marker = NO archive.
                bool markerOk = false;
                try { markerOk = TxCounterFile.ConfirmBlockedMarker(path, "counter copies untrustworthy (primary AND backup)"); }
                catch { }
                if (!markerOk)
                {
                    TxLog.Warn("tx counter .blocked marker NOT confirmed (archive SKIPPED: copies left in place, staying fail-closed in RAM — inspect them immediately)");
                    return;
                }
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try
                {
                    if (System.IO.File.Exists(path))
                        System.IO.File.Move(path, path + ".corrupt-" + stamp);
                }
                catch { }
                try
                {
                    if (System.IO.File.Exists(backupPath))
                        System.IO.File.Move(backupPath, backupPath + ".corrupt-" + stamp);
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// In-flight deduplicator for Adds: the same live ItemData must never be committed
        /// twice (quickstack snapshots every chest from one inventory state; a double click
        /// re-sends the same stack). First submit wins; later duplicates are pruned.
        /// Takes are safe by re-resolution on the manager and are not tracked.
        /// Shared lease primitive (TxClaimSet): claims are held only until the Pending
        /// map (remote) or the synchronous local drain owns them — EVERY pre-ownership
        /// failure path releases via try/finally (see Submit), and terminal completion
        /// releases the claim before the callback (see CompletePendingTerminal:
        /// exactly-once callback ATTEMPT per Pending entry — at-most-once submit,
        /// not end-to-end exactly-once; committed-but-Indeterminate and client-crash
        /// windows stay residual). Never held across frames except inside Pending.
        /// </summary>
        private static readonly TxClaimSet<ItemDrop.ItemData> InFlightAdds = new TxClaimSet<ItemDrop.ItemData>();

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
                if (InFlightAdds.TryClaim(src))
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
            InFlightAdds.ReleaseAll(claimed);
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
                // All items already in flight (claimed is empty here: only surviving
                // items are ever claimed). Release defensively — a stranded claim
                // would starve every future submit of that stack.
                ReleaseClaimed(claimed);
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
            // Claim lease: `claimed` is held ONLY until the Pending map owns it.
            // EVERY pre-ownership failure path below (counter exhausted, encode
            // failure, invalid netview, insert failure, destroyed container)
            // releases via the finally — a stranded claim would starve every
            // future submit of that live stack. Pending owns the claims from
            // `owned = true` until CompletePendingTerminal releases them exactly
            // once (exactly-once callback + claim release, late duplicates find
            // no entry and touch nothing).
            bool owned = false;
            try
            {
                long txId = IssueTxId();
                if (txId == 0L)
                {
                    TellPlayer("Chest request counter exhausted. Restart the game before retrying.");
                    TxLog.Warn("submit refused: counter exhausted (fail closed)");
                    return;
                }
                ZPackage payload;
                try
                {
                    payload = EncodeCall(call, CurrentRevision(container), playerId, actorPos);
                }
                catch (Exception ex)
                {
                    TellPlayer("Shared chest is not available.");
                    TxLog.Warn("submit refused: encode failed: " + ex.Message);
                    return;
                }
                ZPackage request;
                try
                {
                    request = new ZPackage();
                    request.Write(txId);
                    request.Write(payload);
                }
                catch (Exception ex)
                {
                    TellPlayer("Shared chest is not available.");
                    TxLog.Warn("submit refused: framing failed: " + ex.Message);
                    return;
                }
                ZNetView netView = TxReflect.GetNetView(container);
                if ((Object)netView == (Object)null || !netView.IsValid() || (Object)container == (Object)null)
                {
                    TellPlayer("Shared chest is not available.");
                    TxLog.Warn("submit refused: chest netview invalid/destroyed (claims released)");
                    return;
                }
                PendingTx pending = new PendingTx();
                pending.TxId = txId;
                pending.Container = container;
                pending.Payload = request;
                pending.Op = call.Op;
                pending.Call = call;
                pending.Claimed = claimed;
                pending.Attempts = 0;
                pending.ExpectedItems = expectedItems;
                pending.NextTryAt = 0f;
                pending.Deadline = Time.realtimeSinceStartup + FailDeadline;
                pending.OnResponse = onDone;
                try
                {
                    Pending.Add(txId, pending);
                }
                catch (Exception ex)
                {
                    TellPlayer("Shared chest is not available.");
                    TxLog.Warn("submit refused: pending insert failed (claims released): " + ex.Message);
                    return;
                }
                owned = true;
                TxLog.Info("container=" + TxLog.Zid(netView.GetZDO().m_uid) + " tx=" + txId
                    + " peer=" + ZNet.GetUID() + " op=" + call.Op + " " + DescribeCall(call) + " SEND");
                SendPayload(container, request);
                pending.Attempts = 1;
                pending.NextTryAt = Time.realtimeSinceStartup + RequestTimeout;
            }
            finally
            {
                if (!owned)
                    ReleaseClaimed(claimed);
            }
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
            pkg.Write(call.IsTransientRetry);
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
            // Stale-route discipline (grant-before-state): this RPC was routed
            // to the owner at SEND time. If ownership moved in flight we are no
            // longer the manager: dropping WITHOUT answering is INTENTIONAL —
            // the sender's Pending pump resends/queries the SAME txId, which
            // routes to the CURRENT owner. A non-manager UnknownTx here could
            // contradict the real outcome (e.g. finalize Indeterminate for a tx
            // the new manager already committed), so only the manager answers.
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
                // Undecodable: indeterminate (never applied, never cached). A
                // non-transient client resends the stored bytes on timeout, so the
                // retry answers the same way — stable without poisoning the txId.
                // (Transient entries never reach this leg flagged: an undecodable
                // tx answers UnknownTx, never TransientUnavailable, so no entry
                // becomes transient for it; transient resends re-encode flagged via
                // SendTransientRetry — never byte-identical — or fall back to a
                // flagged Query for the same txId.)
                TxLog.Warn("tx=" + txId + " undecodable request from " + sender);
                Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, TxOp.Query);
                return;
            }
            ChestState state = GetState(container);
            if (state == null)
                return;
            // Takeover-before-Drain (grant-before-state): ownership may have
            // arrived after the last slow pump (poll-based detection). Draining
            // on un-reseeded Processed/Floor RAM would execute against stale
            // state, so reseed synchronously here; the pump skips its own
            // Takeover via the LastOwner update below. Takeover never throws
            // and fail-closes (quarantine) when s_items is unavailable, in
            // which case Drain below refuses fresh jobs as Indeterminate.
            if (state.LastOwner != ZNet.GetUID())
            {
                Takeover(state);
                state.LastOwner = ZNet.GetUID();
            }
            if (!TxNet.IsCompatiblePeer(sender) && sender != ZNet.GetUID())
            {
                // Cache first (READ-ONLY): a committed txId keeps its ORIGINAL outcome
                // even when re-sent over a version-skewed link — never overwrite it
                // with a fresh Rejected (same sender-mismatch rule as replays; a
                // cross-op cached txId is NOT replayed — see the op-mismatch gate).
                if (TryRespondCached(container, sender, txId, state, call.Op))
                    return;
                // Version-skewed sender, uncommitted txId: EPHEMERAL Rejected through
                // the shared seam — no victim state change (no cache seed, no floor
                // advance, no ring write). A persisted Rejected would fence a peer
                // whose version may flap; the same txId answers the same way on
                // resend without recording anything.
                TxLog.Warn("tx=" + txId + " REJECT incompatible peer " + sender + " (ephemeral: no victim state change)");
                Respond(container, sender, txId, TxDecision.SpoofTerminal(), CurrentRevision(container), new ZPackage(), false, call.Op);
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
                RespondQuery(container, sender, txId, call.IsTransientRetry);
                return;
            }
            if (TxDecision.ClassifySenderBinding(sender, txId) == TxDecision.SenderBinding.UnboundStranger)
            {
                // Trust gate FIRST: the authenticated sender provably does not own
                // this txId. The cache lookup below is READ-ONLY (a committed txId
                // keeps its ORIGINAL outcome for the SAME op; a stranger gets Rejected
                // with no payload — never another sender's Take bytes; a cross-op
                // cached txId is NOT replayed — see the op-mismatch gate). The uncached
                // tail is an EPHEMERAL Rejected through the shared seam: no victim
                // Processed/ProcOrder/Floor/Ring mutation, so the victim's txId is
                // never burned and a spoof can never fence the true owner's floor.
                if (TryRespondCached(container, sender, txId, state, call.Op))
                    return;
                TxLog.Warn("tx=" + txId + " REJECT sender/txId mismatch sender=" + sender + " (ephemeral: no victim state change)");
                Respond(container, sender, txId, TxDecision.SpoofTerminal(), CurrentRevision(container), new ZPackage(), false, call.Op);
                return;
            }
            StoredResult replay;
            if (state.Processed.TryGetValue(txId, out replay) && replay != null)
            {
                // Authenticated replay BEFORE access re-evaluation: a committed
                // retry must return its original outcome even after permissions
                // changed — re-checking access here would flip it to Rejected.
                // Canonical identity: raw negative RPC senders match their txIds via MatchesPeer semantics.
                if (replay.Sender != 0 && TxIdGen.PeerKey(replay.Sender) != TxIdGen.PeerKey(sender))
                {
                    // Stranger asking for another sender's tx: reject WITHOUT the
                    // cached payload (never leak Take bytes across senders).
                    TxLog.Warn("tx=" + txId + " REJECT replay sender mismatch sender=" + sender);
                    Respond(container, sender, txId, TxStatus.Rejected, replay.Revision, new ZPackage(), false, replay.Op);
                    return;
                }
                if (TxDecision.IsOpMismatch(replay.Op, call.Op))
                {
                    // Cross-op replay (shared seam): the txId committed for a DIFFERENT
                    // op. Never replay the cached payload, never execute, never
                    // overwrite the cache: Indeterminate, and the original op still
                    // replays afterwards. (Query ops never reach here: handled above.)
                    TxLog.Warn("tx=" + txId + " REJECT replay op mismatch cached=" + replay.Op + " incoming=" + call.Op + " (indeterminate, never executes)");
                    Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, call.Op);
                    return;
                }
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + txId + " REPLAY status=" + replay.Status);
                Respond(container, sender, txId, replay.Status, replay.Revision,
                    EncodeCachedBody(replay), replay.TotalsOnly, replay.Op);
                return;
            }
            TransientRefusal tref;
            if (TransientRefusals.TryGetValue(txId, out tref))
            {
                // Transient same-tx retry (both durable copies failed earlier).
                // MatchesPeer FIRST (spoof-safe): a stranger gets an ephemeral
                // Rejected and the map is untouched. Sender-0 legacy skips the
                // gate (lookup only, never executes). Durable cache above already
                // won; this path NEVER executes — a flagged mutation resend
                // re-attempts persistence of the refused record, anything else
                // is reminded TransientUnavailable.
                if (sender != 0L && !TxIdGen.MatchesPeer(sender, txId))
                {
                    TxLog.Warn("tx=" + txId + " REJECT transient sender mismatch sender=" + sender + " (ephemeral: no state change)");
                    Respond(container, sender, txId, TxDecision.SpoofTerminal(), CurrentRevision(container), new ZPackage(), false, call.Op);
                    return;
                }
                if (!call.IsTransientRetry)
                {
                    TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + txId + " TRANSIENT (unflagged resend reminded, never executes)");
                    Respond(container, sender, txId, TxStatus.TransientUnavailable, CurrentRevision(container), new ZPackage(), false, call.Op);
                    return;
                }
                bool reRingOk;
                bool reFloorOk;
                TxStatus reTerminal = PersistFlaggedRefusal(state, txId, call.Op, sender, out reRingOk, out reFloorOk);
                if (reTerminal != TxStatus.Rejected)
                {
                    // Core gate (TxCore.Apply transient leg, shared seam
                    // TxDecision.FlaggedRefusalTerminal): single-copy-durable
                    // terminalizes — the tx never executes, so one persisted copy
                    // is enough for a stable answer. reTerminal == Rejected means
                    // the ring copy is durable (ring-embedded floor included);
                    // UnknownTx means floor-only (seed evicted below, map dropped,
                    // permanently stale-gated); TransientUnavailable means both
                    // copies failed (re-note, same-tx retained, never executes).
                    if (reTerminal == TxStatus.TransientUnavailable)
                    {
                        NoteTransientRefusal(state, txId, call.Op, sender);
                        TxLog.Warn("tx=" + txId + " TRANSIENT still not durable (same-tx retry retained, never executes)");
                        Respond(container, sender, txId, TxStatus.TransientUnavailable, CurrentRevision(container), new ZPackage(), false, call.Op);
                        return;
                    }
                    // Floor-only terminal: evict the non-durable seed and drop the
                    // transient entry — the kept floor high-water stale-gates every
                    // later retry of this txId to the same UnknownTx (terminal,
                    // never executable). Never executes on this path either way.
                    EvictSeededReject(state, txId);
                    TransientRefusals.Remove(txId);
                    TxLog.Warn("tx=" + txId + " transient floor-only (terminal UnknownTx, permanently stale, never executes)");
                    Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, call.Op);
                    return;
                }
                // Durable resolution: the refusal is now stable — drop the
                // transient entry so later retries replay the terminal outcome.
                TransientRefusals.Remove(txId);
                if (reTerminal == TxStatus.Rejected)
                    RequestPropagation(state, sender);
                TxLog.Warn("tx=" + txId + " transient resolved durable (status=" + reTerminal + ")");
                Respond(container, sender, txId, reTerminal, CurrentRevision(container), new ZPackage(), true, call.Op);
                return;
            }
            if (state.RingCorrupt && state.FloorCorrupt)
            {
                // Fully fail closed: both copies untrustworthy, mutate nothing.
                // Ring-corrupt alone with an intact floor runs degraded below.
                TxLog.Warn("tx=" + txId + " refused: ring corrupt (fail closed)");
                Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, call.Op);
                return;
            }
            if (state.TxQuarantined)
            {
                // Fail-closed quarantine after a post-fence failure: live RAM may hold
                // speculative inventory. Fresh txIds never execute — the refusal is a
                // durable terminal through the shared matrix (Rejected only when the
                // seeded record is durable: ring AND floor; else Indeterminate with the
                // unpersisted seed evicted). Replays above already answered from cache.
                // Both copies failed: stay SILENT (no terminal response). The client's
                // Pending pump keeps the entry and resends/queries the SAME txId (never
                // a new one); a same-tx retry re-attempts persistence here (quarantine
                // precedes the stale gate), so transient write faults recover in-session.
                // Answering UnknownTx terminally would forget a non-durable outcome.
                bool anyDurable;
                TxStatus terminal = PersistQuarantineRefusal(state, txId, call.Op, sender, out anyDurable);
                if (!anyDurable)
                {
                    NoteTransientRefusal(state, txId, call.Op, sender);
                    TxLog.Warn("tx=" + txId + " quarantined and refusal not persisted (TRANSIENT recorded: same-tx retry retained)");
                    Respond(container, sender, txId, TxStatus.TransientUnavailable, CurrentRevision(container), new ZPackage(), false, call.Op);
                    return;
                }
                if (terminal == TxStatus.Rejected)
                    RequestPropagation(state, sender);
                TxLog.Warn("tx=" + txId + " refused: quarantined after post-fence failure (status=" + terminal + ")");
                Respond(container, sender, txId, terminal, CurrentRevision(container), new ZPackage(), true, call.Op);
                return;
            }
            if (call.IsTransientRetry && call.Op != TxOp.Query)
            {
                // AUTHORITATIVE flagged branch (clean manager): no cache entry and
                // no transient entry (RAM map discarded by handoff/restart, or never
                // held), yet the client proves a held refusal by retrying flagged
                // with the SAME txId. Reconciliation ONLY — seed the refusal +
                // persist ring/floor, NEVER ExecuteCall, even when the floor sits
                // below this counter (a fresh-looking flagged txId must not run as
                // a new mutation). After the sender/tx binding + exact-cache replay
                // gates above and the transient-map / quarantine legs, but BEFORE
                // the stale/fresh gates: the stale gate would terminally forget a
                // non-durable outcome, and the fresh path would double-apply it.
                // Matrix: TxDecision.FlaggedRefusalTerminal (single-copy-durable,
                // shared with TxCore.Apply) — ring persisted => stable Rejected
                // (replayable via the ring-embedded floor); floor-only => terminal
                // UnknownTx (permanently stale-gated below); both failed =>
                // TransientUnavailable (noted, same-tx retained). Spoof-safe:
                // MatchesPeer FIRST — a stranger gets an ephemeral Rejected and the
                // map/floor/cache are untouched. Sender-0 legacy skips the gate
                // (lookup-grade, never executes).
                if (sender != 0L && !TxIdGen.MatchesPeer(sender, txId))
                {
                    TxLog.Warn("tx=" + txId + " REJECT clean-manager flagged sender mismatch sender=" + sender + " (ephemeral: no state change)");
                    Respond(container, sender, txId, TxDecision.SpoofTerminal(), CurrentRevision(container), new ZPackage(), false, call.Op);
                    return;
                }
                bool cleanRingOk;
                bool cleanFloorOk;
                TxStatus cleanTerminal = PersistFlaggedRefusal(state, txId, call.Op, sender, out cleanRingOk, out cleanFloorOk);
                if (cleanTerminal == TxStatus.Rejected)
                {
                    RequestPropagation(state, sender);
                    TxLog.Warn("tx=" + txId + " clean-manager flagged resolved durable (status=" + cleanTerminal + ")");
                    Respond(container, sender, txId, cleanTerminal, CurrentRevision(container), new ZPackage(), true, call.Op);
                    return;
                }
                if (cleanTerminal == TxStatus.TransientUnavailable)
                {
                    NoteTransientRefusal(state, txId, call.Op, sender);
                    TxLog.Warn("tx=" + txId + " clean-manager flagged not durable (TRANSIENT recorded: same-tx retry retained, never executes)");
                    Respond(container, sender, txId, TxStatus.TransientUnavailable, CurrentRevision(container), new ZPackage(), false, call.Op);
                    return;
                }
                // Floor-only terminal: the seed was evicted inside
                // PersistFlaggedRefusal and no transient entry is noted — the kept
                // floor high-water stale-gates every later retry of this txId to
                // the same UnknownTx (terminal, never executable).
                TxLog.Warn("tx=" + txId + " clean-manager flagged floor-only (terminal UnknownTx, permanently stale, never executes)");
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
                // Dead branch (see above): floor is unbounded, never refuses.
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
                if (!TryPersistReject(state, txId, call.Op, sender, true))
                {
                    TxLog.Warn("tx=" + txId + " REJECT not persisted (indeterminate)");
                    Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, call.Op);
                    return;
                }
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
                int version = payload.ReadInt();
                if (!TxCodec.IsSupportedVersion(version))
                    return false;
                TxOp op = (TxOp)payload.ReadInt();
                baseRev = payload.ReadUInt();
                playerId = payload.ReadLong();
                actorPos = payload.ReadVector3();
                call = new TxOpCall();
                call.Op = op;
                call.EnforceRule = payload.ReadBool();
                call.RespectReserves = payload.ReadBool();
                // v3 trailing flag (client-set ONLY after TransientUnavailable);
                // v2 frames predate it (defaults false). Tolerant read: a truncated
                // v3 frame fails closed to unflagged (reminded TransientUnavailable,
                // safe Indeterminate — never executes), never to a misframed body.
                call.IsTransientRetry = false;
                if (version == TxCodec.ProtoVersion)
                {
                    try { call.IsTransientRetry = payload.ReadBool(); }
                    catch { call.IsTransientRetry = false; }
                }
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
            {
                DropTransientFor(state);
                return;
            }
            state.Draining = true;
            try
            {
                int n = 0;
                while (state.Queue.Count > 0 && n < DrainPerFrame)
                {
                    n++;
                    TxJob job = state.Queue.Dequeue();
                    if (state.TxQuarantined)
                    {
                        // Fail-closed quarantine: live RAM may hold speculative inventory.
                        // Never execute fresh mutations against dirty RAM. Each queued job is
                        // refused through the durable-terminal matrix (Rejected only when the
                        // seeded record is durable: ring AND floor; else Indeterminate with
                        // the unpersisted seed evicted — never silently dropped, never
                        // mutated). A both-copies failure on a REMOTE job stays SILENT (no
                        // terminal completion: the client's Pending pump resends/queries the
                        // SAME txId); a local job has no Pending entry and completes
                        // UnknownTx. (The failed tx that caused the quarantine keeps its fence.)
                        TxOp qop = (job.Call != null) ? job.Call.Op : TxOp.Query;
                        bool anyDurable;
                        TxStatus qterminal = PersistQuarantineRefusal(state, job.TxId, qop, job.Sender, out anyDurable);
                        if (qterminal == TxStatus.Rejected)
                            RequestPropagation(state, job.Sender);
                        TxLog.Warn("tx=" + job.TxId + " quarantined: answered " + qterminal + " without executing (durable=" + anyDurable + ")");
                        if (!anyDurable && !job.IsLocal)
                        {
                            NoteTransientRefusal(state, job.TxId, qop, job.Sender);
                            continue;
                        }
                        StoredResult q = new StoredResult();
                        q.Op = qop;
                        q.Status = qterminal;
                        q.Revision = CurrentRevision(state.Container);
                        try
                        {
                            if (job.Complete != null)
                                job.Complete(q);
                        }
                        catch (Exception ex)
                        {
                            TxLog.Error("tx=" + job.TxId + " completion failed: " + ex.Message);
                        }
                        continue;
                    }
                    StoredResult result;
                    try
                    {
                        result = ApplyJob(state, job);
                    }
                    catch (Exception ex)
                    {
                        // Backstop: ApplyJob answers its own fence/execute failures, so this
                        // fires only for post-fence throws (SaveContainer inside CommitResult
                        // or an unexpected fault). The op may have mutated before throwing, and
                        // inventory/ring/floor writes are not atomic: commitment is unknowable,
                        // so answer INDETERMINATE — never a definitive Rejected that a later
                        // retry could contradict by executing. The durable pre-execution fence
                        // already recorded this txId before any mutation; the floor is
                        // seen-high-water and NEVER rolls back, so the same txId can never
                        // execute later. Recover authoritative RAM BEFORE the next job (reload
                        // s_items or quarantine), then re-persist the fence copies best-effort.
                        TxLog.Error("tx=" + job.TxId + " apply failed: " + ex.Message);
                        RecoverPostFenceFailure(state, null, false, true);
                        result = new StoredResult();
                        result.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                        result.Status = TxStatus.UnknownTx;
                        result.Revision = CurrentRevision(state.Container);
                        AdvanceFloor(state, job.TxId);
                        WriteRing(state);
                        WriteFloor(state);
                        // The fence re-persist above rewrote ZDO keys: ask the
                        // net layer to push them (propagation-request, §RequestPropagation).
                        RequestPropagation(state, job.Sender);
                    }
                    if (result != null && result.SilentDrop && !job.IsLocal)
                    {
                        // Both-copies-failed quarantine refusal (ApplyJob recheck): stay
                        // SILENT — no terminal completion, so the client's Pending pump
                        // retains and resends/queries the SAME txId (never a new one).
                        // A same-tx retry re-attempts persistence (quarantine precedes
                        // the stale gate). Local jobs never set SilentDrop.
                        TxLog.Warn("tx=" + job.TxId + " quarantined refusal not persisted (silent: same-tx retry retained)");
                        continue;
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
                if (cached.Sender != 0 && TxIdGen.PeerKey(cached.Sender) != TxIdGen.PeerKey(job.Sender))
                {
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " REPLAY sender mismatch");
                    StoredResult denied = new StoredResult();
                    denied.Op = cached.Op;
                    denied.Status = TxStatus.Rejected;
                    denied.Revision = cached.Revision;
                    return denied;
                }
                if (job.Call != null && TxDecision.IsOpMismatch(cached.Op, job.Call.Op))
                {
                    // Cross-op replay (shared seam): committed for a DIFFERENT op.
                    // Never the cached payload, never executed, cache untouched.
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " REPLAY op mismatch cached=" + cached.Op + " incoming=" + job.Call.Op + " (indeterminate, never executes)");
                    StoredResult mismatched = new StoredResult();
                    mismatched.Op = job.Call.Op;
                    mismatched.Status = TxStatus.UnknownTx;
                    mismatched.Revision = CurrentRevision(job.Container);
                    return mismatched;
                }
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " REPLAY status=" + cached.Status);
                return CloneStored(cached, cached.Status, true);
            }
            if (state.RingCorrupt && state.FloorCorrupt)
            {
                // Fail closed (recheck: both copies may have loaded corrupt after
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
                // Deterministic rejection: the Rejected outcome is answered only when it
                // is durably persisted (ring entry AND floor copy). Otherwise the same
                // txId must stay Indeterminate — a forgotten Rejected re-executed later
                // could flip to Accepted. CommitResult caches everything and writes
                // ring+floor; the re-write below is an idempotent retry (SameBytes skip)
                // that only fires when the first write failed.
                StoredResult result = new StoredResult();
                result.Op = job.Call.Op;
                result.Status = TxStatus.Rejected;
                CommitResult(state, job, result);
                if (!WriteRing(state) || !WriteFloor(state))
                {
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " REJECT not persisted (indeterminate)");
                    EvictSeededReject(state, job.TxId);
                    StoredResult unpersisted = new StoredResult();
                    unpersisted.Op = job.Call.Op;
                    unpersisted.Status = TxStatus.UnknownTx;
                    unpersisted.Revision = CurrentRevision(job.Container);
                    return unpersisted;
                }
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " REJECT op=" + job.Call.Op);
                return result;
            }
            // Durable pre-execution fence: AFTER every gate (replay/stale/spoof/access/
            // both-corrupt rechecks above), BEFORE every fresh Execute* — the single
            // choke point for ALL callers (GUI, Sort, SetRule, Upgrade, automation).
            // Replays never reach here (answered from cache above, never re-fenced).
            // RAM high-water + FloorKey write. False => ZERO side effects: nothing
            // executed, nothing cached, answer UnknownTx (Indeterminate) loudly, with
            // no auto-retry and no new-tx synthesis — the client retries as a NEW txId.
            // The floor is seen-high-water and NEVER rolls back (not even on fence-write
            // failure: nothing mutated, so a later FRESH txId still submits
            // at-most-once while this txId stays blocked in-session).
            if (!TryPersistExecutionFence(state, job.TxId))
            {
                TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " fence not persisted (indeterminate, never executes)");
                StoredResult fenceFail = new StoredResult();
                fenceFail.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                fenceFail.Status = TxStatus.UnknownTx;
                fenceFail.Revision = CurrentRevision(job.Container);
                return fenceFail;
            }
            // Propagation-request BEFORE Execute (after WriteFloor): peers learn the
            // new high-water even if the mutation later fails. Best-effort only
            // (never an ACK/barrier, never correctness).
            RequestPropagation(state, job.Sender);
            if (!job.Container.IsOwner())
            {
                // Ownership lost after the fence (ZDO.Set is owner-gated, so the fence
                // write above may itself have no-op'd): no mutation may follow. The floor
                // stays advanced — never rolls back — and the answer is Indeterminate.
                // Transient RAM for this chest is dropped (handoff discards RAM —
                // the flagged retry reconciles on the clean manager through the
                // authoritative flagged branch, see OnTxRequest).
                DropTransientFor(state);
                TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " lost ownership after fence (indeterminate)");
                StoredResult ownerLost = new StoredResult();
                ownerLost.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                ownerLost.Status = TxStatus.UnknownTx;
                ownerLost.Revision = CurrentRevision(job.Container);
                return ownerLost;
            }
            if (state.TxQuarantined)
            {
                // Recheck: quarantine engaged while queued (a previous post-fence failure
                // dirtied RAM). The durable pre-execution fence above already recorded
                // this txId (floor advanced + floor write), so the refusal goes through
                // the durable-terminal matrix: Rejected when the seeded record persists
                // (ring write; the floor copy is already durable from the fence), else
                // Indeterminate with the seed evicted and the txId stale-gated by the
                // fence floor (retry as a NEW txId — except a both-copies failure on a
                // REMOTE job, which stays SILENT via SilentDrop so the same txId is
                // retained and re-attempts persistence). Never execute against dirty RAM.
                // Both copies failed on a REMOTE job: SILENT (no terminal completion —
                // the client's Pending pump resends/queries the SAME txId); a local
                // job has no Pending entry and completes UnknownTx. Signalled via
                // SilentDrop so Drain skips the completion instead of terminally
                // forgetting a non-durable outcome.
                TxOp rop = (job.Call != null) ? job.Call.Op : TxOp.Query;
                bool ranyDurable;
                TxStatus rterminal = PersistQuarantineRefusal(state, job.TxId, rop, job.Sender, out ranyDurable);
                if (rterminal == TxStatus.Rejected)
                    RequestPropagation(state, job.Sender);
                TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " quarantined (status=" + rterminal + ", durable=" + ranyDurable + ", never executes)");
                StoredResult quarantined = new StoredResult();
                quarantined.Op = rop;
                quarantined.Status = rterminal;
                quarantined.Revision = CurrentRevision(job.Container);
                if (!ranyDurable && !job.IsLocal)
                {
                    quarantined.SilentDrop = true;
                    NoteTransientRefusal(state, job.TxId, rop, job.Sender);
                }
                return quarantined;
            }
            // Pre-mutation snapshot AFTER the durable fence + ownership recheck: the
            // persisted s_items bytes at fence time. Fallback authority ONLY when the
            // save provably never ran (Execute-throw path); never when the save outcome
            // is ambiguous (save-throw path must reload CURRENT persisted state).
            byte[] preTxItems = null;
            bool preTxRead = false;
            try
            {
                ZNetView snapView = TxReflect.GetNetView(job.Container);
                if ((Object)snapView != (Object)null && snapView.IsValid())
                {
                    // Defensive copy at capture: the ZDO layer may hand out the
                    // live stored reference; the fence-time fallback must be an
                    // immutable snapshot (consumed via a second clone at use).
                    preTxItems = TxSItemsGuard.CloneBytes(snapView.GetZDO().GetByteArray(ZDOVars.s_items));
                    preTxRead = true;
                }
            }
            catch
            {
                preTxRead = false;
            }
            StoredResult applied;
            try
            {
                applied = ExecuteCall(state, job, inv);
            }
            catch (Exception ex)
            {
                // Post-fence Execute throw: the op may have partially mutated RAM
                // inventory (never persisted on this path — the save never ran).
                // Recover authoritative RAM BEFORE the next job (persisted reload
                // preferred, fence-time snapshot fallback), then answer Indeterminate.
                // The fence stays: the same txId never re-executes. Floor never rolls back.
                TxLog.Error("tx=" + job.TxId + " execute failed: " + ex.Message);
                RecoverPostFenceFailure(state, preTxItems, preTxRead, false);
                StoredResult execFail = new StoredResult();
                execFail.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                execFail.Status = TxStatus.UnknownTx;
                execFail.Revision = CurrentRevision(job.Container);
                return execFail;
            }
            try
            {
                CommitResult(state, job, applied);
            }
            catch (Exception ex)
            {
                // SaveContainer/UpdateRows throw AFTER RAM mutation: the ZDO write may or
                // may not have landed (a throw never proves failure). Recover the CURRENT
                // persisted s_items — never the stale pre-tx snapshot — or quarantine when
                // persisted data is unavailable. No exact ring result is cached; the fence
                // stays so the same txId stays Indeterminate forever. Floor never rolls back.
                // Residual gap: a post-write throw leaves a committed-but-Indeterminate tx
                // (client made no reciprocal change — manual reconciliation needed). See
                // RecoverPostFenceFailure per-op audit below.
                TxLog.Error("tx=" + job.TxId + " commit save failed: " + ex.Message);
                RecoverPostFenceFailure(state, preTxItems, preTxRead, true);
                StoredResult commitFail = new StoredResult();
                commitFail.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                commitFail.Status = TxStatus.UnknownTx;
                commitFail.Revision = CurrentRevision(job.Container);
                return commitFail;
            }
            // CommitResult persists items, then ring+floor best-effort. A failed commit
            // write does NOT change the answer: the pre-execution fence is already
            // durable, so a retry stays Indeterminate (never double-applies) and the
            // next successful commit heals the ring.
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

        /// <summary>
        /// Persists a terminal outcome (EVERY status incl. Rejected): items save, then
        /// RAM cache + fence + ring + floor. Returns true only when BOTH the ring and
        /// the floor copies were (re)written — callers answering a FINAL Rejected must
        /// treat false as Indeterminate (see ApplyJob inv==null). Accepted/Partial
        /// callers keep the outcome on false: the pre-execution fence is already durable.
        /// </summary>
        private static bool CommitResult(ChestState state, TxJob job, StoredResult result)
        {
            bool mutated = result.Status == TxStatus.Accepted || result.Status == TxStatus.Partial;
            if (mutated)
            {
                // Chest height must track content (otherwise viewers see rows
                // the manager lacks, and drops there get rejected).
                TxReflect.UpdateRows(state.Container);
                // A SaveContainer throw propagates to the Drain backstop (UnknownTx):
                // RAM inventory may be mutated while ZDO items stay unsaved/torn, but
                // the pre-execution fence is durable — the same txId stays Indeterminate
                // forever, never re-executes. The floor NEVER rolls back.
                TxReflect.SaveContainer(state.Container);
            }
            // Post-inventory-save commit checkpoint: captured BEFORE the ring
            // ZDO write and used consistently in the result, the RAM cache,
            // the ring entry and the response. Never re-read DataRevision after
            // ZDO.Set(RingKey): the ring write itself may bump the revision, so
            // a read-back would describe the ring write, not the commit.
            result.Revision = CurrentRevision(state.Container);
            // Authenticated committer's CANONICAL peer key, stamped before caching: replays check it.
            result.Sender = TxIdGen.PeerKey(job.Sender);
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
            // The ring carries every terminal outcome now (v3), so it is
            // rewritten on rejections too — WriteRing skips the ZDO write when
            // the bytes are unchanged. A failed encode persists NOTHING (never
            // a valid empty ring). On a healthy floor, a successful rewrite
            // heals a degraded corrupt-ring flag: the ring was just rebuilt
            // from the live cache.
            bool ringOk = WriteRing(state);
            bool floorOk = WriteFloor(state);
            if (!state.FloorCorrupt && ringOk && floorOk)
                state.RingCorrupt = false;
            // Committed (or deterministically rejected) state just rewrote ZDO
            // keys: ask the net layer to push them (propagation-request, see
            // RequestPropagation — never an ACK/barrier). The committer is
            // included: its refresh must not depend on presence state.
            RequestPropagation(state, job.Sender);
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + job.TxId + " peer=" + job.Sender
                + " op=" + job.Call.Op + " accepted=" + result.AcceptedTotal() + " revision=" + result.Revision);
            return ringOk && floorOk;
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
        /// UNBOUNDED and never evicted — FloorCap is a warn threshold only
        /// (see WriteFloor), never a refusal cap. An absent txId at/below
        /// high-water is indeterminate.
        /// </summary>
        private static void AdvanceFloor(ChestState state, long txId)
        {
            long peer = TxIdGen.PeerOf(txId);
            uint ctr = TxIdGen.CounterOf(txId);
            uint hw;
            if (state.Floor.TryGetValue(peer, out hw))
            {
                if (ctr > hw)
                    state.Floor[peer] = ctr;
            }
            else
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
            return TxIdGen.CounterOf(txId) <= hw;
        }

        /// <summary>
        /// Retained as a no-op: the floor is UNBOUNDED (never evicted, never
        /// refusing), so no peer is ever "capped out". FloorCap survives only
        /// as a warn threshold (see WriteFloor). Callers keep their fail-closed
        /// shape; this never fires.
        /// </summary>
        private static bool IsFloorCappedFor(ChestState state, long txId)
        {
            return false;
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
            return TryRespondCached(container, sender, txId, state, TxOp.Query);
        }

        /// <summary>
        /// Overload carrying the incoming op for the cross-op replay gate (shared
        /// seam TxDecision.IsOpMismatch): a cached txId committed for a DIFFERENT
        /// mutation op is NOT replayed — the caller falls through to its normal
        /// refusal path (spoof/version-skew ephemeral Rejected). Query lookups pass
        /// TxOp.Query (exempt by design: a Query by txId returns the original
        /// outcome — that IS the lost-response path).
        /// </summary>
        private static bool TryRespondCached(Container container, long sender, long txId, ChestState state, TxOp incomingOp)
        {
            StoredResult hit;
            if (!state.Processed.TryGetValue(txId, out hit) || hit == null)
                return false;
            if (hit.Sender != 0L && TxIdGen.PeerKey(hit.Sender) != TxIdGen.PeerKey(sender))
            {
                TxLog.Warn("tx=" + txId + " cached answer sender mismatch sender=" + sender);
                Respond(container, sender, txId, TxStatus.Rejected, hit.Revision, new ZPackage(), false, hit.Op);
                return true;
            }
            if (TxDecision.IsOpMismatch(hit.Op, incomingOp))
            {
                // Committed for a different op: never the cached payload. Return
                // false so the caller refuses through its own fail-closed path.
                TxLog.Warn("tx=" + txId + " cached answer op mismatch cached=" + hit.Op + " incoming=" + incomingOp + " (no replay)");
                return false;
            }
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + txId + " CACHED status=" + hit.Status);
            Respond(container, sender, txId, hit.Status, hit.Revision,
                EncodeCachedBody(hit), hit.TotalsOnly, hit.Op);
            return true;
        }

        /// <summary>
        /// Seed a definitive Rejected outcome into the fresh cache (pre-queue
        /// rejects: access denial, handoff/structural seeds) so the same txId
        /// keeps its answer forever. Advances the floor unless told otherwise
        /// (spoof entries must not push the true owner's high-water).
        /// Never overwrites a committed outcome: pre-replay callers answer from
        /// cache first (TryRespondCached), and this guard keeps the seeding
        /// itself idempotent (no outcome flip, no duplicate ProcOrder entry).
        /// Returns true when the entry was freshly seeded (callers use it to evict
        /// a seed whose persistence failed — see TryPersistReject).
        /// </summary>
        private static bool SeedReject(ChestState state, long txId, TxOp op, long storedSender, bool advanceFloor)
        {
            if (state.Processed.ContainsKey(txId))
                return false;
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
            r.Sender = TxIdGen.PeerKey(storedSender);
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
            return true;
        }

        /// <summary>
        /// Durable pre-execution fence shared by ApplyJob (the single choke point before
        /// EVERY fresh Execute*): RAM high-water first (seen-high-water, never rolled
        /// back), then the FloorKey write. True only when the fence is durable.
        /// A SameBytes skip counts as durable: LastFloorBytes holds ONLY bytes from a
        /// successful ZDO.Set by THIS peer and is cleared to null on every SeedFromRing
        /// reset, so a skip can never bypass the first persistence after a handoff.
        /// </summary>
        private static bool TryPersistExecutionFence(ChestState state, long txId)
        {
            AdvanceFloor(state, txId);
            return WriteFloor(state);
        }

        /// <summary>
        /// Deterministic pre-queue Rejected (incompatible peer, access denial,
        /// handoff seeds): seed the RAM entry, then persist it. The Rejected
        /// answer may be sent ONLY when the outcome is durable — the ring entry,
        /// plus the floor copy when the floor advances. Otherwise the caller must
        /// answer UnknownTx (Indeterminate): a RAM-only Rejected would flip after
        /// a restart/handoff. On failure a freshly seeded entry is evicted so a
        /// retry re-attempts persistence instead of replaying a non-durable answer
        /// (the floor advance is KEPT: seen-high-water, never rolls back, so an
        /// in-session retry is stale-gated to Indeterminate).
        /// NOTE: spoofed sender/txId mismatches NO LONGER use this helper — they
        /// are refused ephemerally (no seed, no floor advance, no persist) so a
        /// spoof can never fence the true owner's high-water. Surviving callers
        /// pass advanceFloor=true and never carry a spoofed txId.
        /// Returns true when Rejected may be sent.
        /// </summary>
        private static bool TryPersistReject(ChestState state, long txId, TxOp op, long storedSender, bool advanceFloor)
        {
            bool seeded = SeedReject(state, txId, op, storedSender, advanceFloor);
            bool ringOk = WriteRing(state);
            bool floorOk = true;
            if (advanceFloor)
                floorOk = WriteFloor(state);
            if (ringOk && floorOk)
                return true;
            if (seeded)
                EvictSeededReject(state, txId);
            return false;
        }

        /// <summary>
        /// Quarantine durable terminal (shared matrix TxDecision.HandoffDropTerminal,
        /// the same rule as queued-but-unapplied handoff drops): seed the txId as
        /// Rejected (known-not-committed — a quarantined txId never executes), then
        /// persist ring + floor. Rejected is answered ONLY when the seeded record is
        /// durable (ring entry AND floor copy); any persistence failure answers
        /// UnknownTx (Indeterminate) and a freshly seeded entry is evicted (a RAM-only
        /// Rejected would flip after a restart — same rule as TryPersistReject). The
        /// floor advance is always KEPT (seen-high-water, monotonic); a RAM-only floor
        /// is NOT durable protection, and because quarantine precedes the stale gate a
        /// same-tx retry re-attempts persistence (transient recovery without a restart).
        /// anyDurable=false (BOTH copies failed) means nothing is durable: remote
        /// callers must stay SILENT (no terminal response — the client's Pending pump
        /// resends/queries the SAME txId, never a new one) instead of terminally
        /// forgetting a non-durable outcome. Local callers have no Pending entry and
        /// must still complete: they answer UnknownTx (nothing executed, so a retry as
        /// a NEW txId applies at-most-once).
        /// </summary>
        private static TxStatus PersistQuarantineRefusal(ChestState state, long txId, TxOp op, long sender, out bool anyDurable)
        {
            bool seeded = SeedReject(state, txId, op, sender, true);
            bool ringOk = WriteRing(state);
            bool floorOk = WriteFloor(state);
            anyDurable = ringOk || floorOk;
            TxStatus terminal = TxDecision.HandoffDropTerminal(ringOk, floorOk);
            if (terminal != TxStatus.Rejected && seeded)
                EvictSeededReject(state, txId);
            return terminal;
        }

        /// <summary>
        /// Flagged-retry reconciliation persist (IsTransientRetry-authoritative patch,
        /// shared seam TxDecision.FlaggedRefusalTerminal — the same rule as the
        /// TxCore.Apply flagged legs): seed the txId as Rejected (known-not-committed —
        /// a flagged txId never executes), then persist ring + floor. Unlike
        /// PersistQuarantineRefusal (both-copies gate, preserved for the quarantine-
        /// fresh path), a SINGLE durable copy terminalizes here: ring persisted =>
        /// stable Rejected (seed kept — the ring entry carries the refusal and the
        /// ring-embedded floor, so the outcome replays after handoff/restart);
        /// floor-only => UnknownTx terminal (seed evicted — a RAM-only Rejected
        /// would flip after a restart — the kept floor high-water stale-gates the
        /// txId permanently); both failed => TransientUnavailable (seed evicted,
        /// caller re-notes the transient map). The floor advance is always KEPT
        /// (seen-high-water, monotonic). Never executes.
        /// </summary>
        private static TxStatus PersistFlaggedRefusal(ChestState state, long txId, TxOp op, long sender, out bool ringOk, out bool floorOk)
        {
            bool seeded = SeedReject(state, txId, op, sender, true);
            ringOk = WriteRing(state);
            floorOk = WriteFloor(state);
            TxStatus terminal = TxDecision.FlaggedRefusalTerminal(ringOk, floorOk);
            if (!ringOk && seeded)
                EvictSeededReject(state, txId);
            return terminal;
        }

        /// <summary>
        /// Transient-refusal RAM map (v3): txId -&gt; minimal refusal metadata.
        /// Populated ONLY on both-copies-failed quarantine refusals (nothing
        /// durable). A held txId answers TransientUnavailable (non-terminal) and
        /// a flagged same-tx mutation retry re-attempts persistence (never
        /// executes); a Query for it answers TransientUnavailable BEFORE the
        /// cache-miss UnknownTx (sender-validated). Entries are removed after a
        /// durable resolution and dropped on ownership loss / ring reload
        /// (restart/handoff discards RAM — the flagged retry then reconciles on
        /// the clean manager through the authoritative flagged branch, see
        /// OnTxRequest and TxDecision.FlaggedRefusalTerminal, so the handoff
        /// needs no RAM entry); quarantine clear (TryReloadAuthoritative) keeps
        /// them authoritative. Never cached, never persisted, never executed.
        /// </summary>
        internal sealed class TransientRefusal
        {
            public long TxId;
            public TxOp Op;
            public long Sender;
            public ZDOID ZdoId;
        }

        private static readonly System.Collections.Generic.Dictionary<long, TransientRefusal> TransientRefusals =
            new System.Collections.Generic.Dictionary<long, TransientRefusal>();

        /// <summary>Test seam: how many txIds sit in the transient-refusal RAM map.</summary>
        internal static int TransientRefusalCount
        {
            get { return TransientRefusals.Count; }
        }

        /// <summary>
        /// Record a both-copies-failed refusal (ONLY call site: both-fail legs).
        /// Overwrites any prior entry for the txId (same-tx retries re-note).
        /// </summary>
        private static void NoteTransientRefusal(ChestState state, long txId, TxOp op, long sender)
        {
            try
            {
                TransientRefusal note = new TransientRefusal();
                note.TxId = txId;
                note.Op = op;
                note.Sender = sender;
                note.ZdoId = state != null ? state.ZdoId : new ZDOID();
                TransientRefusals[txId] = note;
            }
            catch
            {
            }
        }

        /// <summary>
        /// Drop transient entries for one chest (ownership loss / ring reload:
        /// RAM is discarded on handoff — the flagged retry then reconciles on the
        /// clean manager through the authoritative flagged branch, see OnTxRequest
        /// and TxDecision.FlaggedRefusalTerminal, so the handoff needs no RAM entry).
        /// Quarantine clear deliberately does NOT call this (transient stays
        /// authoritative across TryReloadAuthoritative).
        /// </summary>
        private static void DropTransientFor(ChestState state)
        {
            try
            {
                if (state == null)
                    return;
                ZDOID zid = state.ZdoId;
                System.Collections.Generic.List<long> dead = null;
                foreach (System.Collections.Generic.KeyValuePair<long, TransientRefusal> kv in TransientRefusals)
                {
                    if (kv.Value != null && kv.Value.ZdoId == zid)
                    {
                        if (dead == null)
                            dead = new System.Collections.Generic.List<long>();
                        dead.Add(kv.Key);
                    }
                }
                if (dead != null)
                    for (int i = 0; i < dead.Count; i++)
                        TransientRefusals.Remove(dead[i]);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Best-effort removal of a just-seeded (never committed) Rejected entry whose
        /// persistence failed, so the same txId retries persistence instead of replaying
        /// a non-durable answer. Never touches committed outcomes: callers only evict
        /// txIds they just seeded (and only when the seed was fresh — see SeedReject).
        /// </summary>
        private static void EvictSeededReject(ChestState state, long txId)
        {
            state.Processed.Remove(txId);
            try
            {
                state.ProcOrder.Remove(txId);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Central post-fence recovery: authoritative persisted-s_items reload + fail-closed
        /// quarantine for ExecuteCall/save exceptions. Called BEFORE Drain advances to the
        /// next job. The floor is unchanged here (the failed tx keeps the fence it already
        /// took; fresh txIds fence-then-refuse while quarantined — never executed,
        /// never cached). Nothing is cached for the
        /// ambiguous tx (no exact ring result).
        ///
        /// Steps: mark quarantined FIRST, then TryReloadAuthoritative (fresh s_items read →
        /// Inventory.Load → SetLastRevision(DataRevision) → UpdateRows → invalidate viewer
        /// refresh caches). Clear quarantine ONLY after every step succeeds. When the save
        /// provably never ran (saveMayHaveRun=false, Execute-throw path) and the fresh reload
        /// is unavailable, fall back to the fence-time snapshot (provably identical: nothing
        /// was saved after it). When the save outcome is ambiguous (saveMayHaveRun=true),
        /// NEVER use the stale snapshot: a SaveContainer throw never proves the ZDO write
        /// failed, so the CURRENT persisted bytes are the only authority. Unavailable or
        /// unreadable persisted data (null ZDO, invalid netview, Load/UpdateRows throw) is
        /// recovery failure — quarantine stays, never presumed empty.
        ///
        /// Per-op audit (inventory vs non-inventory effects):
        /// - Add/AddBatch: RAM merge/new-cells via TxInventory; persisted via s_items save.
        ///   Reload discards speculative credit. Client stays Indeterminate (removes nothing).
        /// - Take/TakeBatch: RAM RemoveExact; reload discards speculative debit. Client credits
        ///   nothing on Indeterminate. Totals-only ring replay still credits nothing.
        /// - Move: RAM MoveWithin/swap; reload discards speculative reorder. Refresh only.
        /// - Sort: RAM full reorder; reload discards speculative order. Refresh only.
        /// - Upgrade: NO inventory mutation in Execute (destroys/recreates the chest object,
        ///   consumes requirements, migrates the rule). Inventory reload cannot undo consumed
        ///   resources, effects, or object-identity change — residual non-inventory gap.
        /// - SetRule: NO inventory mutation (ZDO rule key written synchronously in Execute,
        ///   NOT via s_items). The rule write is durable even when the incidental items-save
        ///   then throws (split-brain: rule persisted, UnknownTx answered) — reload cannot
        ///   undo it. Residual non-inventory gap, documented not solved.
        /// - Post-write throw residual gap (all mutating ops): when Container.Save writes
        ///   s_items and THEN throws, the tx is durable while the client receives
        ///   Indeterminate and makes no reciprocal change. Reload keeps the written value
        ///   live (correct) but the committed-but-Indeterminate conservation gap needs manual
        ///   reconciliation — reported plainly, never claimed as solved by reload.
        /// </summary>
        private static bool RecoverPostFenceFailure(ChestState state, byte[] preTxItems, bool preTxRead, bool saveMayHaveRun)
        {
            if (state == null)
                return false;
            if (!state.TxQuarantined)
            {
                state.TxQuarantined = true;
                TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " quarantined after post-fence failure (authoritative reload pending)");
            }
            if (TryReloadAuthoritative(state))
                return true;
            if (!saveMayHaveRun && preTxRead && preTxItems != null)
            {
                // Fence-time snapshot fallback (save provably never ran):
                // routed through TxSItemsLoad.TryLoad — decoded-bytes proof
                // first, live RAM snapshotted before Load and restored on a
                // mid-Load throw, so a torn Load can never leave torn RAM
                // live behind the quarantine. Never throws.
                string snapReason;
                if (!TxSItemsLoad.TryLoad(state.Container, preTxItems, out snapReason))
                {
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " fence-time snapshot invalid (" + snapReason + "), staying quarantined");
                    return false;
                }
                try
                {
                    ZNetView nv = TxReflect.GetNetView(state.Container);
                    TxReflect.SetLastRevision(state.Container, nv.GetZDO().DataRevision);
                    TxReflect.UpdateRows(state.Container);
                    state.SeenRev = 0u;
                    state.SeenOnce = false;
                    state.SeenBytes = null;
                    state.TxQuarantined = false;
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " recovered from fence-time snapshot (save never ran)");
                    return true;
                }
                catch (Exception ex)
                {
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " snapshot fallback failed, staying quarantined: " + ex.Message);
                    return false;
                }
            }
            TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " authoritative reload failed, staying quarantined (no fresh mutations)");
            return false;
        }

        /// <summary>
        /// Authoritative reload: fresh s_items bytes are the best available authority after an
        /// ambiguous save (a throw never proves the write failed). Returns true only when every
        /// step succeeds (read → Load → SetLastRevision → UpdateRows → viewer-cache invalidate)
        /// and clears the quarantine; any failure (null netview, unreadable ZDO, null bytes,
        /// Load/UpdateRows throw) leaves quarantine set. Null bytes count as failure (never
        /// presumed empty). Never throws.
        /// </summary>
        /// <summary>
        /// Observation-only s_items==null tally (see SItemsNullDenials). Never
        /// throws; records the site tag at TxVerbose level for field diagnosis.
        /// </summary>
        private static void NoteSItemsNull(string site)
        {
            try
            {
                SItemsNullDenials++;
                TxLog.Info("s_items null observed at " + site + " (fail-closed, quarantined; total=" + SItemsNullDenials + ")");
            }
            catch { }
        }

        private static bool TryReloadAuthoritative(ChestState state)
        {
            try
            {
                if (state == null || (Object)state.Container == (Object)null)
                    return false;
                ZNetView netView = TxReflect.GetNetView(state.Container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return false;
                byte[] bytes;
                try
                {
                    // Defensive copy at capture: never retain the ZDO layer's
                    // live stored reference (aliasing would corrupt later
                    // comparisons and the quarantine baseline together).
                    bytes = TxSItemsGuard.CloneBytes(netView.GetZDO().GetByteArray(ZDOVars.s_items));
                }
                catch
                {
                    return false;
                }
                if (bytes == null)
                {
                    // NO behavior change (hardening observation only): null s_items
                    // stays fail-closed (quarantine kept, never presumed empty).
                    // Counted for the runtime-verification checklist.
                    NoteSItemsNull("reload");
                    return false;
                }
                // Decoded-bytes proof + RAM snapshot fallback: corrupt-but-
                // non-null bytes return false (quarantine kept) WITHOUT leaving
                // torn RAM live. Never throws.
                string loadReason;
                if (!TxSItemsLoad.TryLoad(state.Container, bytes, out loadReason))
                {
                    TxLog.Warn("authoritative reload refused invalid s_items (" + loadReason + "), staying quarantined");
                    return false;
                }
                TxReflect.SetLastRevision(state.Container, netView.GetZDO().DataRevision);
                TxReflect.UpdateRows(state.Container);
                state.SeenRev = 0u;
                state.SeenOnce = false;
                state.SeenBytes = null;
                state.TxQuarantined = false;
                TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " authoritative reload ok, quarantine cleared");
                return true;
            }
            catch (Exception ex)
            {
                TxLog.Warn("authoritative reload failed, staying quarantined: " + ex.Message);
                return false;
            }
        }

        /// <summary>Drop the whole queue without answering (caller seeds then answers).</summary>
        private static List<TxJob> DequeueAll(ChestState state)
        {
            List<TxJob> dropped = new List<TxJob>(state.Queue.Count);
            while (state.Queue.Count > 0)
                dropped.Add(state.Queue.Dequeue());
            return dropped;
        }

        /// <summary>
        /// Answer one dropped queued-but-unapplied job (nothing applied). The caller passes
        /// Rejected only when the seeded outcome was durably persisted (ring + floor);
        /// otherwise UnknownTx (Indeterminate) so the txId can never execute later.
        /// </summary>
        private static void AnswerReject(ChestState state, TxJob job, TxStatus status)
        {
            try
            {
                StoredResult r = new StoredResult();
                r.Op = (job.Call != null) ? job.Call.Op : TxOp.Query;
                r.Status = status;
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

        /// <summary>
        /// Persists the v3 ring (entries + embedded floor copy). Returns false when
        /// nothing was written (encode failure persists NOTHING — never a valid
        /// empty ring — or the ZDO write threw). Callers use the result to decide
        /// whether a degraded corrupt-ring flag may heal.
        /// A SameBytes skip returns success only against LastRingBytes, which holds
        /// solely bytes from a successful ZDO.Set by THIS peer and is cleared to null
        /// on every SeedFromRing reset — the first persistence after a handoff can
        /// never be skipped. LastRingBytes is assigned ONLY after ZDO.Set returns.
        /// </summary>
        private static bool WriteRing(ChestState state)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(state.Container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return false;
                // ProcOrder is oldest-first and already includes the
                // just-committed tx (CommitResult caches before writing).
                // v3 persists EVERY terminal outcome with its original status:
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
                        // Canonical sender key (PeerOf is already canonical; PeerKey is idempotent).
                        s.Sender = r.Sender != 0L ? TxIdGen.PeerKey(r.Sender) : TxIdGen.PeerOf(node.Value);
                        s.Accepted = new List<int>(r.Accepted);
                        s.TakePayloads = BuildRingTakePayloads(r);
                        slots.Add(s);
                    }
                    node = node.Next;
                }
                List<RingEntry> ring = TxRing.Snapshot(slots);
                // Production writes v3; legacy v1/v2 ZDO bytes are only ever
                // read (never migrated in place, no world migration).
                byte[] bytes = TxCore.TxCore.EncodeRingV3(ring, state.Floor);
                if (bytes == null)
                {
                    TxLog.Warn("ring persist failed: encode error (persisting nothing)");
                    return false;
                }
                if (SameBytes(bytes, state.LastRingBytes))
                    return true;
                netView.GetZDO().Set(RingKey, bytes);
                // Byte discipline: the ZDO layer may retain the Set array by
                // reference, so the SameBytes baseline MUST be an independent
                // copy — otherwise a later in-place mutation corrupts channel
                // and baseline together and defeats the skip guard. Assigned
                // ONLY after ZDO.Set returns.
                state.LastRingBytes = TxSItemsGuard.CloneBytes(bytes);
                return true;
            }
            catch (Exception ex)
            {
                TxLog.Warn("ring persist failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Persists the independent floor copy (own ZDO key). Ring first, floor
        /// second: with per-peer max-merge on load, either crash order stays safe.
        /// ZDO reality (no atomicity across keys, each Set bumps DataRevision,
        /// writes are owner-gated): readers MUST tolerate torn generations —
        /// a newer ring with an older floor (or vice versa) is routine, not
        /// corruption. Max-merge only ever raises high-waters, and ring entries
        /// are immutable committed records, so a torn read can never un-block
        /// an old-generation txId nor partially apply anything. Present-but-bad
        /// floor bytes are validated-then-ignored (never partial-applied); only
        /// a corrupt ring with NO trustworthy floor fails fully closed.
        /// Warns past the FloorCap threshold (unbounded map, liveness preserved).
        /// Returns false when nothing was written. Never throws.
        /// A SameBytes skip returns success only against LastFloorBytes, which holds
        /// solely bytes from a successful ZDO.Set by THIS peer and is cleared to null
        /// on every SeedFromRing reset — the first persistence after a handoff can
        /// never be skipped. LastFloorBytes is assigned ONLY after ZDO.Set returns.
        /// </summary>
        private static bool WriteFloor(ChestState state)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(state.Container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return false;
                if (state.Floor.Count > TxLimits.FloorCap)
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " floor peers=" + state.Floor.Count
                        + " past warn threshold " + TxLimits.FloorCap + " (unbounded, no refusal)");
                byte[] bytes = TxCore.TxCore.EncodeFloor(state.Floor);
                if (bytes == null)
                {
                    TxLog.Warn("floor persist failed: encode error (persisting nothing)");
                    return false;
                }
                if (SameBytes(bytes, state.LastFloorBytes))
                    return true;
                netView.GetZDO().Set(FloorKey, bytes);
                // Same byte-discipline note as WriteRing: independent baseline
                // copy, assigned ONLY after ZDO.Set returns.
                state.LastFloorBytes = TxSItemsGuard.CloneBytes(bytes);
                return true;
            }
            catch (Exception ex)
            {
                TxLog.Warn("floor persist failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Propagation request after a ZDO write (CommitResult, fence re-persist,
        /// handoff reseeds, pre-execute fence): asks the network layer to push the
        /// chest ZDO to interested peers NOW instead of waiting for the next
        /// periodic sync.
        ///
        /// Durability vocabulary (used consistently here and in
        /// docs/chesttx_runtime_verification.md):
        /// - local-write: ZDO.Set / Container.Save updated the manager's copy.
        /// - propagation-request: THIS helper (ForceSendZDO per recipient).
        /// - observed: a viewer polled a newer DataRevision/s_items.
        /// - world-save: the server persisted the ZDO to the world file.
        /// - reciprocal: the client applied its matching inventory change.
        ///
        /// This helper is ONLY a propagation-request: best-effort, never an
        /// ACK/barrier, never proof of observed/world-save/reciprocal. Uses
        /// solely the attested ZDOMan API shape (ForceSendZDO(peerUid, zdoUid),
        /// the same call TxContainerOpenPatch makes on the open-grant path).
        /// Recipients are collected per call (see CollectPropagationRecipients):
        /// the requesting sender + the presence Viewers set + the server peer
        /// where valid, deduped. Viewers-only was INSUFFICIENT (a requester whose
        /// presence never registered, and the world-save authority, would miss
        /// the push), so the collector — not Viewers alone — is the contract.
        /// Never throws; TxVerbose-gated logging only (TxLog.Info).
        /// </summary>
        private static void RequestPropagation(ChestState state)
        {
            RequestPropagation(state, 0L);
        }

        /// <summary>
        /// Propagation request including the requesting sender (fence/commit points
        /// know it; reseed paths pass 0 and rely on viewers + server).
        /// </summary>
        private static void RequestPropagation(ChestState state, long senderHint)
        {
            TryForceSendZdo(state, senderHint);
        }

        /// <summary>
        /// Recipient collector for propagation-requests: the requesting sender
        /// (needs the outcome/refresh even before its presence registers) + every
        /// known presence viewer + the server peer where valid (world-save
        /// authority), deduped, skipping 0 tags and self (a push to the manager
        /// itself is pointless). Viewers is presence state, NOT a routing table —
        /// callers must go through HERE, never Viewers alone.
        /// </summary>
        private static List<long> CollectPropagationRecipients(ChestState state, long senderHint)
        {
            List<long> recipients = new List<long>(8);
            try
            {
                long self = 0L;
                try { self = ZNet.GetUID(); }
                catch { }
                if (senderHint != 0L && senderHint != self && !recipients.Contains(senderHint))
                    recipients.Add(senderHint);
                if (state != null && state.Viewers != null)
                {
                    foreach (long viewer in state.Viewers.Keys)
                    {
                        if (viewer == 0L || viewer == self || recipients.Contains(viewer))
                            continue;
                        recipients.Add(viewer);
                    }
                }
                long server = ServerPeerUid();
                if (server != 0L && server != self && !recipients.Contains(server))
                    recipients.Add(server);
            }
            catch
            {
            }
            return recipients;
        }

        /// <summary>
        /// Server/world-save peer uid where valid: the server peer on a client,
        /// 0 on the server itself (self is skipped by the collector anyway) or
        /// when the net layer is unavailable. Never throws.
        /// </summary>
        private static long ServerPeerUid()
        {
            try
            {
                if ((UnityEngine.Object)ZNet.instance == (UnityEngine.Object)null)
                    return 0L;
                if (ZNet.instance.IsServer())
                    return 0L;
                ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
                if (serverPeer == null)
                    return 0L;
                return serverPeer.m_uid;
            }
            catch
            {
                return 0L;
            }
        }

        private static void TryForceSendZdo(ChestState state)
        {
            TryForceSendZdo(state, 0L);
        }

        private static void TryForceSendZdo(ChestState state, long senderHint)
        {
            try
            {
                if (state == null || (Object)state.Container == (Object)null)
                    return;
                // Recipient collector (never Viewers alone): sender + viewers +
                // server, deduped. Empty set = nobody to push to (self needs no
                // push); not an error.
                System.Collections.Generic.List<long> peers = CollectPropagationRecipients(state, senderHint);
                if (peers.Count == 0)
                    return;
                ZNetView netView = TxReflect.GetNetView(state.Container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return;
                ZDOID zdoUid;
                try { zdoUid = netView.GetZDO().m_uid; }
                catch { return; }
                ZDOMan zdoMan = ZDOMan.instance;
                if (zdoMan == null)
                    return;
                int pushed = 0;
                for (int i = 0; i < peers.Count; i++)
                {
                    long peer = peers[i];
                    if (peer == 0L)
                        continue;
                    try
                    {
                        zdoMan.ForceSendZDO(peer, zdoUid);
                        pushed++;
                    }
                    catch { }
                }
                TxLog.Info("container=" + TxLog.Zid(zdoUid) + " propagation-request pushed=" + pushed + "/" + peers.Count + " (best-effort, no ACK)");
            }
            catch { }
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

        private static FloorData ReadFloor(Container container)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    return new FloorData();
                byte[] bytes = netView.GetZDO().GetByteArray(FloorKey.GetStableHashCode());
                if (bytes == null)
                    return new FloorData(); // absent: fall back to the ring-embedded copy.
                FloorData data = TxCore.TxCore.DecodeFloor(bytes);
                data.Present = true;
                return data;
            }
            catch (Exception)
            {
                FloorData bad = new FloorData();
                bad.Present = true;
                bad.Corrupt = true;
                return bad;
            }
        }

        private static void SeedFromRing(ChestState state, RingData data)
        {
            SeedFromRing(state, data, null);
        }

        /// <summary>Per-peer max-merge of one floor copy (canonical keys, unbounded). Crash-order safe.</summary>
        private static void MergeFloor(ChestState state, Dictionary<long, uint> copy)
        {
            if (copy == null)
                return;
            foreach (KeyValuePair<long, uint> kv in copy)
            {
                long peer = TxIdGen.PeerKey(kv.Key);
                uint hw;
                if (state.Floor.TryGetValue(peer, out hw))
                {
                    if (kv.Value > hw)
                        state.Floor[peer] = kv.Value;
                }
                else
                {
                    state.Floor[peer] = kv.Value;
                }
            }
        }

        /// <summary>
        /// Split-copy seed: the ring plus the independently persisted floor.
        /// True reset, then restore with per-peer max-merge (either crash order
        /// stays safe: high-waters only move up). Floor keys are canonicalized.
        /// TORN READS are expected (two non-atomic ZDO keys): any combination of
        /// ring/floor generations merges safely — max-merge never lowers a
        /// high-water and entries are immutable committed records, so nothing
        /// partially applies. A present-but-corrupt floor copy is validated-
        /// then-ignored in favor of the ring-embedded copy; a corrupt ring with
        /// no trustworthy floor fails fully closed (both flags).
        /// Ring corrupt + independent floor present-and-valid = DEGRADED recovery:
        /// empty caches + RingCorrupt kept for visibility, floor trusted — old-gen
        /// txIds stay blocked, new-gen above the floor commits fenced-first, and
        /// the next successful commit rebuilds (and heals) the ring. Ring corrupt
        /// with no trustworthy floor = full fail-closed (both flags).
        /// </summary>
        private static void SeedFromRing(ChestState state, RingData data, FloorData floor)
        {
            // True reset of the idempotency state, then restore. v2/v3 entries keep
            // their ORIGINAL status with exact payloads when available; legacy
            // v1 entries (Status==Duplicate) restore totals-only. A corrupt
            // payload fails closed: empty caches + RingCorrupt (no mutation).
            // The transient-refusal RAM map is dropped (restart/handoff discards
            // RAM — the flagged retry reconciles on the clean manager through
            // the authoritative flagged branch, see OnTxRequest).
            DropTransientFor(state);
            state.Processed.Clear();
            state.ProcOrder.Clear();
            state.Floor.Clear();
            state.LastRingBytes = null;
            state.LastFloorBytes = null;
            state.RingCorrupt = false;
            state.FloorCorrupt = false;
            if (data == null)
                return;
            bool floorOk = floor != null && floor.Present && !floor.Corrupt;
            if (data.Corrupt)
            {
                if (floorOk)
                {
                    MergeFloor(state, floor.Floor);
                    state.RingCorrupt = true; // degraded: no replays, floor still guards old-gen.
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId)
                        + " ring corrupt: degraded recovery on independent floor (old-gen blocked, new-gen may commit)");
                    return;
                }
                state.RingCorrupt = true;
                state.FloorCorrupt = true;
                TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " ring corrupt: fail closed (no mutations until trustworthy state)");
                return;
            }
            if (floor != null && floor.Corrupt)
            {
                // Independent floor griefed but the ring is fine: the ring-embedded
                // copy is authoritative for this generation (never max-merge a
                // corrupt copy).
            }
            else if (floorOk)
            {
                MergeFloor(state, floor.Floor);
            }
            MergeFloor(state, data.Floor);
            if (data.Entries == null)
                return;
            for (int i = 0; i < data.Entries.Count && i < TxLimits.RingCap; i++)
            {
                RingEntry e = data.Entries[i];
                if (e.Sender != 0L)
                    e.Sender = TxIdGen.PeerKey(e.Sender); // canonicalize legacy signed senders.
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
