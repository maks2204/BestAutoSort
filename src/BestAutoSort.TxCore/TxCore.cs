using System;
using System.Collections.Generic;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Decoded persistent-ring payload: entries + the durable per-sender
    /// execution floor. Corrupt=true means the persisted bytes were present but
    /// untrustworthy: the loader must fail closed for mutation (never execute),
    /// never silently treat them as an empty usable ring.
    /// </summary>
    public sealed class RingData
    {
        public bool Corrupt;
        public List<RingEntry> Entries = new List<RingEntry>();
        public Dictionary<long, uint> Floor = new Dictionary<long, uint>();
    }

    /// <summary>
    /// Decoded independent floor payload (separate ZDO key): the durable
    /// per-sender execution high-water. Corrupt=true means present but
    /// untrustworthy. Present=false (missing key) means no independent floor
    /// was ever written: fall back to the ring-embedded copy. Keys are
    /// canonical peer keys (TxIdGen.PeerKey).
    /// </summary>
    public sealed class FloorData
    {
        public bool Corrupt;
        public bool Present;
        public Dictionary<long, uint> Floor = new Dictionary<long, uint>();
    }

    /// <summary>
    /// Serialized transaction engine of one chest.
    /// Invariant: all mutations of one chest go strictly one at a time through Apply.
    /// Idempotency: a repeated txId returns the cached result without re-applying.
    /// Revision grows only on a really applied mutation.
    /// Thread-safe (lock); in game all calls come from the main thread.
    ///
    /// Stability contract (ring v2/v3):
    /// - Every terminal outcome (Accepted/Partial/Rejected) is cached AND ringed.
    ///   A replay or query returns the ORIGINAL status — the same txId never
    ///   changes its terminal outcome across handoff (Rejected stays Rejected).
    /// - Wire Duplicate is reserved for legacy v1 entries whose original status
    ///   is genuinely unavailable (they restore totals-only).
    /// - The durable per-sender floor (peer key -&gt; highest committed counter,
    ///   UNBOUNDED, never evicted, never refusing) makes an absent txId at or
    ///   below its sender's high-water indeterminate (UnknownTx): it must NEVER execute.
    ///   This intentionally includes a delayed first-time out-of-order request.
    /// - The durable pre-execution fence (RAM high-water + persisted floor copy,
    ///   shared order with production via the TxDurability seam) is written BEFORE
    ///   any mutation, so a crash in any later window leaves the request
    ///   Indeterminate, never double-applied. The floor is seen-high-water and
    ///   never rolls back.
    /// - Authenticated sender identity is gated through the shared seam
    ///   (TxDecision.ClassifySenderBinding, canonical keys via TxIdGen.MatchesPeer)
    ///   BEFORE any Processed/ProcOrder/Floor/Ring mutation, and a sender/txId
    ///   mismatch is an EPHEMERAL Rejected: no victim floor/ring/cache change, so
    ///   the victim's txId is never burned and no stranger ever sees another
    ///   sender's cached payload.
    /// - A corrupt persisted ring fails closed: Apply answers UnknownTx and
    ///   mutates nothing until the state is rebuilt from trustworthy data —
    ///   or degraded-recovered with an intact independent floor (old-gen stays
    ///   blocked, new-gen above the floor may commit fenced-first).
    /// </summary>
    public sealed class TxCore
    {
        private readonly object _gate = new object();
        private readonly LinkedList<long> _order = new LinkedList<long>();
        private readonly Dictionary<long, TxResult> _processed = new Dictionary<long, TxResult>();
        private readonly List<RingEntry> _ring = new List<RingEntry>();
        private readonly Dictionary<long, uint> _floor = new Dictionary<long, uint>();
        private bool _corrupt;
        private bool _floorCorrupt;
        private bool _quarantined;

        public uint Revision { get; private set; }

        /// <summary>
        /// Optional persistence seam. Null (default) = legacy RAM-only behavior.
        /// Set = durable pre-execution fence order shared with production:
        /// fence (floor write) -&gt; ownership recheck -&gt; execute -&gt; items save -&gt;
        /// cache -&gt; ring write -&gt; floor write, with Indeterminate answers and
        /// crash-point injection (see TxDurability). Replays never fence.
        /// </summary>
        public TxDurability Durability { get; set; }

        public int ProcessedCount
        {
            get { lock (_gate) { return _processed.Count; } }
        }

        public int RingCount
        {
            get { lock (_gate) { return _ring.Count; } }
        }

        public int FloorCount
        {
            get { lock (_gate) { return _floor.Count; } }
        }

        public bool RingCorrupt
        {
            get { lock (_gate) { return _corrupt; } }
        }

        /// <summary>
        /// The independent floor copy is untrustworthy (or missing while the
        /// ring is also corrupt). Mutations stay fail-closed until BOTH the
        /// ring and the floor are trustworthy or the floor is recovered.
        /// </summary>
        public bool FloorCorrupt
        {
            get { lock (_gate) { return _floorCorrupt; } }
        }

        /// <summary>
        /// Post-fence recovery quarantine: live RAM may hold speculative inventory
        /// left by a failed Execute/save whose persistence outcome was ambiguous.
        /// While set, fresh mutations never execute (Apply answers UnknownTx without
        /// fencing, mutating, or caching); replays/queries still answer. Cleared only
        /// by a successful authoritative reload (TryRecover), never by elapsed time.
        /// The fence of the failed tx is kept (never rolls back).
        /// </summary>
        public bool Quarantined
        {
            get { lock (_gate) { return _quarantined; } }
        }

        /// <summary>
        /// Controlled recovery path (pump/takeover analog): reloads live RAM from the
        /// persisted items snapshot through the ReloadItems seam. Returns true only
        /// when every step succeeded (quarantine cleared); false leaves quarantine set.
        /// Unavailable seam (null) counts as recovery failure, never presumed empty.
        /// </summary>
        public bool TryRecover(ModelChest chest)
        {
            if (chest == null)
                throw new ArgumentNullException("chest");
            lock (_gate)
            {
                if (!_quarantined)
                    return true;
                if (ReloadItemsHook(chest))
                {
                    _quarantined = false;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Apply a request to the state. A repeated txId returns the ORIGINAL
        /// cached outcome (IsReplay=true) without re-applying; a stale request
        /// (at/below its sender's high-water) answers UnknownTx without executing.
        /// </summary>
        public TxResult Apply(ModelChest chest, TxRequest request)
        {
            if (chest == null)
                throw new ArgumentNullException("chest");
            if (request == null)
                throw new ArgumentNullException("request");
            lock (_gate)
            {
                if (_corrupt && _floorCorrupt)
                    return UnknownResult();
                TxResult cached;
                if (_processed.TryGetValue(request.TxId, out cached))
                {
                    if (request.Sender != 0 && cached.Sender != 0
                        && TxIdGen.PeerKey(request.Sender) != TxIdGen.PeerKey(cached.Sender))
                        return SenderMismatch(cached, request.Sender);
                    TxResult dup = cached.Clone();
                    dup.IsReplay = true;
                    return dup;
                }
                long peer = TxIdGen.PeerOf(request.TxId);
                uint ctr = TxIdGen.CounterOf(request.TxId);
                // Trust gate FIRST (shared seam): an authenticated sender that does
                // not own the txId peer bits is a stranger. The refusal below is
                // EPHEMERAL and mutates nothing (no cache/ring/floor change) — the
                // cache lookup above is read-only. See EphemeralSpoofReject.
                if (TxDecision.ClassifySenderBinding(request.Sender, request.TxId) == TxDecision.SenderBinding.UnboundStranger)
                    return EphemeralSpoofReject(request);
                if (_quarantined)
                {
                    // Fail-closed quarantine: speculative RAM may be dirty. Safe
                    // replay/query responses still work (handled above/below), but
                    // fresh mutations never execute, fence, or cache here — the same
                    // txId stays retryable after TryRecover clears the quarantine.
                    // (The failed tx that caused the quarantine keeps its fence.)
                    return QuarantinedResult();
                }
                uint hw;
                if (_floor.TryGetValue(peer, out hw) && ctr <= hw)
                {
                    // At or below the sender's high-water but no record: the tx
                    // was evicted, rejected-and-forgotten, or never arrived in
                    // order. Indeterminate — must NEVER execute (a Rejected txId
                    // re-executed here would flip to Accepted).
                    return UnknownResult();
                }
                // The floor map is UNBOUNDED (never evicted, never refusing):
                // FloorCap is only a warn threshold for operators. A fixed cap
                // would brick liveness in long-lived worlds.
                // Durable pre-execution fence: the RAM high-water is recorded AND the
                // floor copy is durably persisted BEFORE any mutation, so a crash in
                // any later window leaves this txId Indeterminate, never double-applied.
                // The floor is seen-high-water and NEVER rolls back — not even when the
                // fence write itself fails (then nothing mutated, so a later FRESH txId
                // still submits at-most-once against an unblocked floor while this
                // txId stays blocked in-session).
                // With Durability unset this degrades to the legacy RAM-only fence.
                AdvanceFloor(peer, ctr);
                if (Durability != null)
                {
                    if (!PersistFloorHook())
                    {
                        // Fence write failed: zero side effects beyond the seen floor
                        // (no mutation, no cache entry, nothing persisted). Loud
                        // Indeterminate, no auto-retry: the client retries as a NEW txId.
                        return UnknownResult();
                    }
                    FireCrash(TxDurabilityPoint.AfterFence);
                    if (!IsOwnerHook())
                    {
                        // Ownership lost after the fence (production ZDO.Set is
                        // owner-gated, so the fence write may itself have no-op'd):
                        // no mutation may follow. The fence stays — never rolls back.
                        return UnknownResult();
                    }
                }
                // Pre-mutation snapshot AFTER the durable fence + ownership recheck:
                // the fallback authority when the persisted reload is unavailable AND
                // the save provably never ran (Execute-throw path only). Never used
                // when the save outcome is ambiguous (save-fail/throw path).
                List<int[]> preTxSnapshot = chest.Snapshot();
                uint preTxRevision = Revision;
                TxResult result;
                try
                {
                    result = Execute(chest, request);
                }
                catch (TxCrashException)
                {
                    throw;
                }
                catch
                {
                    // Post-fence Execute throw: the op may have partially mutated RAM
                    // (never persisted on this path — the save never ran). Recover from
                    // the authoritative persisted snapshot when available, else from the
                    // fence-time snapshot (provably identical: nothing was saved after it).
                    // The fence stays, the same txId is Indeterminate, never re-executes.
                    RecoverAfterExecuteFailure(chest, preTxSnapshot, preTxRevision);
                    return UnknownResult();
                }
                if (Durability != null)
                {
                    FireCrash(TxDurabilityPoint.AfterExecute);
                    bool saved;
                    try
                    {
                        saved = SaveItemsHook(chest);
                    }
                    catch (TxCrashException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Ambiguous save throw: the ZDO write may or may not have landed
                        // (a throw never proves failure). Reload the CURRENT persisted
                        // state — not the stale pre-tx snapshot — or quarantine when the
                        // persisted data is unavailable. Floor kept, never rolls back.
                        RecoverAfterSaveFailure(chest);
                        return UnknownResult();
                    }
                    if (!saved)
                    {
                        // Items-save failed (production SaveContainer): RAM may be
                        // mutated but nothing further persisted — Indeterminate,
                        // fence stays, never re-executes. Reload authoritative state
                        // (clean failure: persisted == pre-tx) or quarantine.
                        RecoverAfterSaveFailure(chest);
                        return UnknownResult();
                    }
                    FireCrash(TxDurabilityPoint.AfterItemsSave);
                }
                result.Op = request.Op;
                result.Sender = request.Sender != 0 ? TxIdGen.PeerKey(request.Sender) : peer;
                result.Revision = Revision;
                _processed[request.TxId] = result.Clone();
                _order.AddLast(request.TxId);
                while (_order.Count > TxLimits.ProcessedCacheCap)
                {
                    long oldest = _order.First.Value;
                    _order.RemoveFirst();
                    _processed.Remove(oldest);
                    _takeBlobs.Remove(oldest);
                }
                AdvanceFloor(peer, ctr);
                RebuildRing();
                if (Durability != null)
                {
                    if (result.Status == TxStatus.Rejected)
                    {
                        // Deterministic Rejected: the entry AND the floor copy must
                        // persist, otherwise the answer is Indeterminate (a RAM-only
                        // Rejected would flip after a restart). The fresh seed is
                        // evicted on failure so a retry re-attempts persistence.
                        if (!PersistRingHook() || !PersistFloorHook())
                        {
                            Evict(request.TxId);
                            return UnknownResult();
                        }
                    }
                    else
                    {
                        // Accepted/Partial: the pre-execution fence is already durable,
                        // so a retry stays Indeterminate (never double-applies) and the
                        // next successful commit heals the ring. Best-effort here.
                        PersistRingHook();
                        FireCrash(TxDurabilityPoint.AfterRing);
                        PersistFloorHook();
                    }
                }
                if (!_floorCorrupt)
                    _corrupt = false; // degraded-mode recovery: the ring was just rebuilt from the live cache.
                return result;
            }
        }

        /// <summary>
        /// Projects the idempotency cache (oldest-first) into the shared ring
        /// builder input. Must be called AFTER the current tx is cached, so
        /// the snapshot always includes the just-committed tx.
        /// </summary>
        private List<RingSlot> CollectSlots()
        {
            List<RingSlot> slots = new List<RingSlot>(_order.Count);
            foreach (long txId in _order)
            {
                TxResult r;
                if (!_processed.TryGetValue(txId, out r) || r == null)
                    continue;
                RingSlot s;
                s.TxId = txId;
                s.AcceptedTotal = r.AcceptedTotal();
                s.Revision = r.Revision;
                s.Status = r.Status;
                s.Op = r.Op;
                s.Sender = r.Sender;
                s.Accepted = new List<int>(r.Accepted);
                s.TakePayloads = FindTakeBlobs(txId, r);
                slots.Add(s);
            }
            return slots;
        }

        private readonly Dictionary<long, List<TakePayload>> _takeBlobs = new Dictionary<long, List<TakePayload>>();

        private List<TakePayload> FindTakeBlobs(long txId, TxResult r)
        {
            List<TakePayload> b;
            if (_takeBlobs.TryGetValue(txId, out b))
                return b;
            return null;
        }

        /// <summary>
        /// Query a cached result (lost response).
        /// Returns the original outcome (IsReplay), a totals-only legacy
        /// Duplicate from the ring, or UnknownTx.
        /// </summary>
        public TxResult Query(long txId)
        {
            return Query(txId, 0L);
        }

        public TxResult Query(long txId, long sender)
        {
            lock (_gate)
            {
                TxResult cached;
                if (_processed.TryGetValue(txId, out cached))
                {
                    if (sender != 0 && cached.Sender != 0
                        && TxIdGen.PeerKey(sender) != TxIdGen.PeerKey(cached.Sender))
                        return SenderMismatch(cached, sender);
                    TxResult r = cached.Clone();
                    r.IsReplay = true;
                    return r;
                }
                for (int i = _ring.Count - 1; i >= 0; i--)
                {
                    if (_ring[i].TxId == txId)
                    {
                        RingEntry e = _ring[i];
                        if (sender != 0 && e.Sender != 0
                            && TxIdGen.PeerKey(sender) != TxIdGen.PeerKey(e.Sender))
                            return SenderMismatchStatus(e, sender);
                        if (e.Status == TxStatus.Duplicate || !TxRing.IsExactEntry(e))
                        {
                            bool legacy = e.Status == TxStatus.Duplicate;
                            return new TxResult
                            {
                                Status = legacy ? TxStatus.Duplicate : e.Status,
                                Revision = e.Revision,
                                Accepted = legacy ? new List<int> { e.AcceptedTotal } : new List<int>(e.Accepted ?? new List<int> { e.AcceptedTotal }),
                                TotalsOnly = true,
                                Op = e.Op,
                                Sender = e.Sender,
                                IsReplay = true
                            };
                        }
                        return new TxResult
                        {
                            Status = e.Status,
                            Revision = e.Revision,
                            Accepted = new List<int>(e.Accepted),
                            TotalsOnly = false,
                            Op = e.Op,
                            Sender = e.Sender,
                            IsReplay = true
                        };
                    }
                }
                return new TxResult { Status = TxStatus.UnknownTx, Revision = Revision };
            }
        }

        /// <summary>
        /// Load the persistent ring after a manager handoff. TRUE RESET: clears
        /// _processed, _order, _ring and the floor, then restores. v2 entries
        /// restore their original status (exact payloads when available,
        /// totals-only otherwise); legacy v1 entries restore as Duplicate
        /// totals-only. A corrupt payload fails closed (RingCorrupt: Apply
        /// answers UnknownTx and mutates nothing).
        /// </summary>
        public void LoadRing(List<RingEntry> entries)
        {
            RingData data = new RingData();
            if (entries != null)
                data.Entries.AddRange(entries);
            LoadRing(data);
        }

        public void LoadRing(RingData data)
        {
            lock (_gate)
            {
                _processed.Clear();
                _order.Clear();
                _ring.Clear();
                _floor.Clear();
                _takeBlobs.Clear();
                _corrupt = false;
                _floorCorrupt = false;
                Revision = 0;
                if (data == null)
                    return;
                if (data.Corrupt)
                {
                    // Legacy combined path with no independent floor: both the
                    // ring and the floor are untrustworthy — full fail-closed.
                    // Use LoadRingWithFloor/RecoverWithFloor when a separate
                    // floor copy exists (degraded recovery keeps old-gen blocked
                    // while new-gen above the floor may proceed).
                    _corrupt = true;
                    _floorCorrupt = true;
                    return;
                }
                MergeFloor(data.Floor);
                if (data.Entries == null)
                    return;
                int n = Math.Min(data.Entries.Count, TxLimits.RingCap);
                for (int i = 0; i < n; i++)
                {
                    RingEntry e = data.Entries[i];
                    if (e.Sender != 0)
                        e.Sender = TxIdGen.PeerKey(e.Sender); // canonicalize legacy signed senders.
                    _ring.Add(e);
                    TxResult r;
                    if (e.Status == TxStatus.Duplicate)
                    {
                        r = new TxResult
                        {
                            Status = TxStatus.Duplicate,
                            Revision = e.Revision,
                            Accepted = new List<int> { e.AcceptedTotal },
                            TotalsOnly = true,
                            Op = e.Op,
                            Sender = e.Sender != 0 ? e.Sender : TxIdGen.PeerOf(e.TxId)
                        };
                    }
                    else if (TxRing.IsExactEntry(e))
                    {
                        r = new TxResult
                        {
                            Status = e.Status,
                            Revision = e.Revision,
                            Accepted = new List<int>(e.Accepted ?? new List<int>()),
                            TotalsOnly = false,
                            Op = e.Op,
                            Sender = e.Sender
                        };
                    }
                    else
                    {
                        // Original status preserved, but payloads are gone:
                        // totals-only, never fabricated.
                        r = new TxResult
                        {
                            Status = e.Status,
                            Revision = e.Revision,
                            Accepted = new List<int>(e.Accepted ?? new List<int> { e.AcceptedTotal }),
                            TotalsOnly = true,
                            Op = e.Op,
                            Sender = e.Sender
                        };
                    }
                    _processed[e.TxId] = r;
                    _order.AddLast(e.TxId);
                    if (e.TakePayloads != null)
                        _takeBlobs[e.TxId] = e.TakePayloads;
                }
                while (_order.Count > TxLimits.ProcessedCacheCap)
                {
                    long oldest = _order.First.Value;
                    _order.RemoveFirst();
                    _processed.Remove(oldest);
                    _takeBlobs.Remove(oldest);
                }
                if (_ring.Count > 0)
                    Revision = _ring[_ring.Count - 1].Revision;
            }
        }

        /// <summary>
        /// Merges one floor copy into the live map: per peer the MAXIMUM wins.
        /// Max-merge is crash-order safe no matter which copy (ring-embedded
        /// or independent key) was written last: high-waters only move up, so
        /// the merged floor never un-blocks an old-generation txId. Keys are
        /// canonicalized (legacy signed senders map to their peer key).
        /// </summary>
        private void MergeFloor(Dictionary<long, uint> copy)
        {
            if (copy == null)
                return;
            foreach (KeyValuePair<long, uint> kv in copy)
            {
                long peer = TxIdGen.PeerKey(kv.Key);
                uint hw;
                if (_floor.TryGetValue(peer, out hw))
                {
                    if (kv.Value > hw)
                        _floor[peer] = kv.Value;
                }
                else
                {
                    _floor[peer] = kv.Value;
                }
            }
        }

        /// <summary>
        /// Split-copy handoff load: the ring plus the independently persisted
        /// floor. TRUE RESET, then restore with max-merge (see MergeFloor).
        /// Torn generations (two non-atomic ZDO keys) merge safely: high-waters
        /// only move up and entries are immutable committed records — never
        /// partial-apply. A present-but-corrupt floor copy is validated-then-
        /// ignored, never merged.
        /// Ring corrupt + independent floor present-and-valid = DEGRADED
        /// recovery: the ring is unusable (no replays) but the floor still
        /// blocks old-generation txIds while new-generation ones above the
        /// floor may commit (fenced first). Ring corrupt with no trustworthy
        /// floor = full fail-closed, like LoadRing(corrupt).
        /// </summary>
        public void LoadRingWithFloor(RingData ring, FloorData floor)
        {
            lock (_gate)
            {
                _processed.Clear();
                _order.Clear();
                _ring.Clear();
                _floor.Clear();
                _takeBlobs.Clear();
                _corrupt = false;
                _floorCorrupt = false;
                Revision = 0;
                bool floorOk = floor != null && floor.Present && !floor.Corrupt;
                if (ring != null && ring.Corrupt)
                {
                    if (floorOk)
                    {
                        MergeFloor(floor.Floor);
                        _corrupt = true;
                        _floorCorrupt = false;
                        return;
                    }
                    _corrupt = true;
                    _floorCorrupt = true;
                    return;
                }
                if (floor != null && floor.Corrupt)
                {
                    // Independent floor griefed but the ring is fine: the
                    // ring-embedded copy is authoritative for this generation.
                    // (Max-merge with a corrupt copy would be unsafe, so the
                    // corrupt copy is ignored, not merged.)
                }
                else if (floorOk)
                {
                    MergeFloor(floor.Floor);
                }
                if (ring != null)
                    MergeFloor(ring.Floor);
                if (ring == null || ring.Entries == null)
                    return;
                RingData second = new RingData();
                second.Entries.AddRange(ring.Entries);
                // Reuse the entry-restore loop through a nested reset-free path:
                // entries were already validated by the shared codec, so only
                // canonicalize senders and rebuild the cache here.
                int n = Math.Min(second.Entries.Count, TxLimits.RingCap);
                for (int i = 0; i < n; i++)
                {
                    RingEntry e = second.Entries[i];
                    if (e.Sender != 0)
                        e.Sender = TxIdGen.PeerKey(e.Sender);
                    _ring.Add(e);
                    _processed[e.TxId] = RingEntryToResult(e);
                    _order.AddLast(e.TxId);
                    if (e.TakePayloads != null)
                        _takeBlobs[e.TxId] = e.TakePayloads;
                }
                while (_order.Count > TxLimits.ProcessedCacheCap)
                {
                    long oldest = _order.First.Value;
                    _order.RemoveFirst();
                    _processed.Remove(oldest);
                    _takeBlobs.Remove(oldest);
                }
                if (_ring.Count > 0)
                    Revision = _ring[_ring.Count - 1].Revision;
            }
        }

        /// <summary>
        /// Recovers a corrupt-ring core with a separately trusted floor: drops
        /// the unusable ring, keeps the floor, stays degraded (RingCorrupt)
        /// until the next successful commit rebuilds the ring from live cache.
        /// Old-generation txIds stay blocked; new-generation ones may commit.
        /// </summary>
        public void RecoverWithFloor(FloorData floor)
        {
            lock (_gate)
            {
                if (!_corrupt)
                    return;
                if (floor == null || !floor.Present || floor.Corrupt)
                    return; // nothing trustworthy to recover with: stay fail-closed.
                _processed.Clear();
                _order.Clear();
                _ring.Clear();
                _floor.Clear();
                _takeBlobs.Clear();
                MergeFloor(floor.Floor);
                _floorCorrupt = false;
                Revision = 0;
            }
        }

        private static TxResult RingEntryToResult(RingEntry e)
        {
            if (e.Status == TxStatus.Duplicate)
            {
                return new TxResult
                {
                    Status = TxStatus.Duplicate,
                    Revision = e.Revision,
                    Accepted = new List<int> { e.AcceptedTotal },
                    TotalsOnly = true,
                    Op = e.Op,
                    Sender = e.Sender != 0 ? e.Sender : TxIdGen.PeerOf(e.TxId)
                };
            }
            if (TxRing.IsExactEntry(e))
            {
                return new TxResult
                {
                    Status = e.Status,
                    Revision = e.Revision,
                    Accepted = new List<int>(e.Accepted ?? new List<int>()),
                    TotalsOnly = false,
                    Op = e.Op,
                    Sender = e.Sender
                };
            }
            return new TxResult
            {
                Status = e.Status,
                Revision = e.Revision,
                Accepted = new List<int>(e.Accepted ?? new List<int> { e.AcceptedTotal }),
                TotalsOnly = true,
                Op = e.Op,
                Sender = e.Sender
            };
        }

        /// <summary>
        /// Ring snapshot for persistence (ZDO).
        /// </summary>
        public List<RingEntry> DumpRing()
        {
            lock (_gate)
            {
                return new List<RingEntry>(_ring);
            }
        }

        public Dictionary<long, uint> DumpFloor()
        {
            lock (_gate)
            {
                return new Dictionary<long, uint>(_floor);
            }
        }

        /// <summary>
        /// Quarantined fresh-tx terminal through the shared seam (TxDecision):
        /// Indeterminate, never fenced/cached/executed. The same txId stays
        /// retryable after TryRecover clears the quarantine.
        /// </summary>
        private TxResult QuarantinedResult()
        {
            return new TxResult { Status = TxDecision.QuarantinedTerminal(), Revision = Revision };
        }

        private TxResult UnknownResult()
        {
            return new TxResult { Status = TxStatus.UnknownTx, Revision = Revision };
        }

        /// <summary>
        /// Authenticated replay identity mismatch: this sender did not commit
        /// this txId. Rejected for THIS sender (nothing committed on its behalf)
        /// with the payload stripped — never another sender's Take bytes.
        /// </summary>
        private TxResult SenderMismatch(TxResult cached, long sender)
        {
            return new TxResult
            {
                Status = TxStatus.Rejected,
                Revision = cached.Revision,
                Accepted = new List<int>(),
                TotalsOnly = false,
                Op = cached.Op,
                Sender = TxIdGen.PeerKey(sender),
                IsReplay = true
            };
        }

        /// <summary>
        /// Ring-path twin of SenderMismatch (cache path): same Rejected, no
        /// payload, and the REQUESTER as Sender — mirroring the cache path so
        /// both identities agree on who was refused. Sender is only an
        /// identity tag (never payload), so no Take bytes can leak either way.
        /// </summary>
        private TxResult SenderMismatchStatus(RingEntry e, long sender)
        {
            return new TxResult
            {
                Status = TxStatus.Rejected,
                Revision = e.Revision,
                Accepted = new List<int>(),
                TotalsOnly = false,
                Op = e.Op,
                Sender = TxIdGen.PeerKey(sender),
                IsReplay = true
            };
        }

        /// <summary>
        /// Spoofed txId (authenticated sender disagrees with the txId peer bits):
        /// EPHEMERAL Rejected through the shared seam (TxDecision.SpoofTerminal).
        /// Persists, fences and caches NOTHING — no victim floor/ring/cache change —
        /// so the victim's txId is never burned: its legitimate use still applies
        /// at-most-once afterwards (every resend of the spoofed txId answers the
        /// same way without recording anything: stable without state). The refused
        /// sender is named by its canonical key; no victim payload is ever attached.
        /// </summary>
        private TxResult EphemeralSpoofReject(TxRequest request)
        {
            return new TxResult
            {
                Status = TxDecision.SpoofTerminal(),
                Revision = Revision,
                Op = request.Op,
                Sender = TxIdGen.PeerKey(request.Sender),
                IsReplay = false
            };
        }

        /// <summary>
        /// Best-effort removal of a just-seeded (never committed) entry whose
        /// persistence failed, so the same txId retries persistence instead of
        /// replaying a non-durable answer. The floor advance is intentionally KEPT
        /// (seen-high-water, never rolls back): an in-session retry is stale-gated
        /// to Indeterminate, and nothing ever executed, so a post-restart fresh
        /// attempt still applies at-most-once (never a silent double-apply).
        /// </summary>
        private void Evict(long txId)
        {
            _processed.Remove(txId);
            _order.Remove(txId);
            _takeBlobs.Remove(txId);
            RebuildRing();
        }

        private void RebuildRing()
        {
            _ring.Clear();
            _ring.AddRange(TxRing.Snapshot(CollectSlots()));
        }

        /// <summary>
        /// Floor-copy write through the seam. Null seam/hook = success (legacy).
        /// False (or encode failure) = nothing persisted. A TxCrashException from
        /// the hook models a crash DURING the write and always propagates.
        /// </summary>
        private bool PersistFloorHook()
        {
            TxDurability seam = Durability;
            if (seam == null || seam.WriteFloor == null)
                return true;
            byte[] bytes = EncodeFloor(_floor);
            if (bytes == null)
                return false;
            return seam.WriteFloor(bytes);
        }

        /// <summary>Ring-copy write through the seam (same contract as PersistFloorHook).</summary>
        private bool PersistRingHook()
        {
            TxDurability seam = Durability;
            if (seam == null || seam.WriteRing == null)
                return true;
            byte[] bytes = EncodeRingV3(_ring, _floor);
            if (bytes == null)
                return false;
            return seam.WriteRing(bytes);
        }

        /// <summary>Items-save through the seam (production Container.Save analog).</summary>
        private bool SaveItemsHook(ModelChest chest)
        {
            TxDurability seam = Durability;
            if (seam == null || seam.SaveItems == null)
                return true;
            return seam.SaveItems(chest);
        }

        /// <summary>
        /// Authoritative reload through the seam (production s_items Load analog).
        /// Null seam/hook or false/throw (except TxCrashException, which propagates)
        /// = recovery failure: the caller stays quarantined, never presumes empty.
        /// </summary>
        private bool ReloadItemsHook(ModelChest chest)
        {
            TxDurability seam = Durability;
            if (seam == null || seam.ReloadItems == null)
                return false;
            try
            {
                return seam.ReloadItems(chest);
            }
            catch (TxCrashException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Mid-execution fault injection (tests only; null = no fault).</summary>
        private bool FailExecuteHook(TxRequest request)
        {
            TxDurability seam = Durability;
            if (seam == null || seam.FailExecute == null)
                return false;
            try
            {
                return seam.FailExecute(request);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Recovery after a post-fence Execute throw (the save never ran, so the
        /// persisted snapshot still equals the fence-time snapshot). Marks quarantine
        /// first, then reloads authoritative persisted state when available; falls back
        /// to the fence-time snapshot (provably identical on this path only). Clears
        /// quarantine only after every step succeeds; the floor is unchanged (kept).
        /// </summary>
        private void RecoverAfterExecuteFailure(ModelChest chest, List<int[]> preTxSnapshot, uint preTxRevision)
        {
            _quarantined = true;
            if (ReloadItemsHook(chest))
            {
                _quarantined = false;
                return;
            }
            if (preTxSnapshot != null)
            {
                try
                {
                    chest.Restore(preTxSnapshot);
                    Revision = preTxRevision;
                    _quarantined = false;
                    return;
                }
                catch
                {
                }
            }
            // Reload unavailable and snapshot unusable: stay quarantined (fail-closed).
        }

        /// <summary>
        /// Recovery after an ambiguous items-save (false or non-crash throw: the ZDO
        /// write may or may not have landed). Marks quarantine first, then reloads the
        /// CURRENT persisted state — never the stale pre-tx snapshot. A post-write throw
        /// therefore keeps the already-written value live (committed-but-Indeterminate:
        /// the client made no reciprocal change and must reconcile manually). Stays
        /// quarantined when persisted data is unavailable. Floor kept, nothing cached.
        /// </summary>
        private void RecoverAfterSaveFailure(ModelChest chest)
        {
            _quarantined = true;
            if (ReloadItemsHook(chest))
                _quarantined = false;
            // Else stay quarantined: fresh mutations blocked until TryRecover succeeds.
        }

        /// <summary>Post-fence ownership recheck through the seam (null = owner).</summary>
        private bool IsOwnerHook()
        {
            TxDurability seam = Durability;
            if (seam == null || seam.IsOwner == null)
                return true;
            return seam.IsOwner();
        }

        private void FireCrash(TxDurabilityPoint point)
        {
            TxDurability seam = Durability;
            if (seam != null && seam.Crash != null)
                seam.Crash(point);
        }

        /// <summary>Write-ahead fence + commit marker: UNBOUNDED, never evicting. FloorCap is a warn threshold only.</summary>
        private void AdvanceFloor(long peer, uint ctr)
        {
            peer = TxIdGen.PeerKey(peer);
            uint hw;
            if (_floor.TryGetValue(peer, out hw))
            {
                if (ctr > hw)
                    _floor[peer] = ctr;
            }
            else
            {
                _floor[peer] = ctr;
            }
        }

        /// <summary>
        /// Offline Take metadata: the request ItemKey IS the total identity here
        /// (no customData, no durability, no reserve path), so key blobs aligned
        /// with accepted[] are provably sufficient for exact replay. Production
        /// fills the same blob slots with the actual TakeEntry.Item.Save bytes.
        /// </summary>
        public static byte[] EncodeTakeKey(ItemKey key)
        {
            byte[] buf = new byte[16];
            Array.Copy(BitConverter.GetBytes(key.Hash), 0, buf, 0, 4);
            Array.Copy(BitConverter.GetBytes(key.Quality), 0, buf, 4, 4);
            Array.Copy(BitConverter.GetBytes(key.Variant), 0, buf, 8, 4);
            Array.Copy(BitConverter.GetBytes(key.World), 0, buf, 12, 4);
            return buf;
        }

        public static ItemKey DecodeTakeKey(byte[] buf)
        {
            if (buf == null || buf.Length < 16)
                return new ItemKey(0, 0, 0, 0);
            return new ItemKey(
                BitConverter.ToInt32(buf, 0),
                BitConverter.ToInt32(buf, 4),
                BitConverter.ToInt32(buf, 8),
                BitConverter.ToInt32(buf, 12));
        }

        /// <summary>
        /// Ring serialization v1 to bytes: [count:byte][txId:long][total:int][rev:uint]...
        /// Kept for legacy-compat tests; production writes v2 (EncodeRingV2).
        /// Never throws.
        /// </summary>
        public static byte[] EncodeRing(List<RingEntry> ring)
        {
            if (ring == null)
                ring = new List<RingEntry>();
            int n = Math.Min(ring.Count, TxLimits.RingCap);
            byte[] buf = new byte[1 + n * TxLimits.RingEntryBytes];
            buf[0] = (byte)n;
            int o = 1;
            for (int i = ring.Count - n; i < ring.Count; i++)
            {
                Array.Copy(BitConverter.GetBytes(ring[i].TxId), 0, buf, o, 8); o += 8;
                Array.Copy(BitConverter.GetBytes(ring[i].AcceptedTotal), 0, buf, o, 4); o += 4;
                Array.Copy(BitConverter.GetBytes(ring[i].Revision), 0, buf, o, 4); o += 4;
            }
            return buf;
        }

        /// <summary>
        /// Strict v1 decode: exact length match required, never throws.
        /// Use DecodeEnvelope when the corrupt-vs-empty distinction matters.
        /// </summary>
        public static List<RingEntry> DecodeRing(byte[] buf)
        {
            RingData data = DecodeEnvelope(buf);
            if (data == null || data.Corrupt)
                return new List<RingEntry>();
            return data.Entries;
        }

        /// <summary>
        /// Versioned ring envelope decode (production + tests share this codec):
        /// v3 = [0xFF][0x03][floorCount:ushort][peerKey:uint32,ctr:uint32]...[ringCount:ushort][entries]...,
        /// v2 = [0xFF][0x02][floorCount][peer:long,ctr:uint]...[ringCount][entries]...,
        /// v1 = [count:byte][txId:long,total:int,rev:uint]... (exact length required).
        /// v2 senders stored as raw signed UIDs are tolerated when their KEY matches
        /// (normalized to canonical); v3 keys compare directly. Validates lengths,
        /// enums, counts, payload sizes and complete consumption.
        /// Never throws. Null (missing ZDO key) decodes as empty-usable (fresh chest);
        /// present-but-unparseable decodes as Corrupt (fail closed for mutation).
        /// </summary>
        public static RingData DecodeEnvelope(byte[] buf)
        {
            RingData data = new RingData();
            if (buf == null || buf.Length == 0)
                return data;
            try
            {
                if (buf[0] == TxLimits.RingV2Magic)
                {
                    if (buf.Length < 2)
                    {
                        data.Corrupt = true;
                        return data;
                    }
                    if (buf[1] == TxLimits.RingV3Version)
                        return DecodeV3(buf);
                    return DecodeV2(buf);
                }
                int n = (int)buf[0];
                if (n < 0 || n > TxLimits.RingCap)
                {
                    data.Corrupt = true;
                    return data;
                }
                if (buf.Length != 1 + n * TxLimits.RingEntryBytes)
                {
                    data.Corrupt = true;
                    return data;
                }
                int o = 1;
                for (int i = 0; i < n; i++)
                {
                    RingEntry e;
                    e.TxId = BitConverter.ToInt64(buf, o);
                    e.AcceptedTotal = BitConverter.ToInt32(buf, o + 8);
                    e.Revision = BitConverter.ToUInt32(buf, o + 12);
                    e.Op = (TxOp)0;
                    e.Status = TxStatus.Duplicate;
                    e.Sender = TxIdGen.PeerOf(e.TxId);
                    e.Accepted = new List<int> { e.AcceptedTotal };
                    e.TakePayloads = null;
                    o += TxLimits.RingEntryBytes;
                    data.Entries.Add(e);
                }
                return data;
            }
            catch (Exception)
            {
                RingData bad = new RingData();
                bad.Corrupt = true;
                return bad;
            }
        }

        /// <summary>
        /// v2 envelope encode shared by production WriteRing and tests.
        /// Legacy-shaped entries (Status==Duplicate) encode with their aggregate;
        /// entries whose sender KEY disagrees with the txId peer bits are SKIPPED
        /// as malformed (canonical comparison: legacy signed senders pass when
        /// their key matches). Spoof rejects persist by design: CacheSpoofReject caches
        /// them under the PEER (not the spoofer), so they pass this filter, the
        /// ring carries them, and the floor still guards their txIds.
        /// Returns null on encoding failure (callers must persist NOTHING:
        /// a failed encode must never become a valid empty ring). Never throws.
        /// </summary>
        public static byte[] EncodeRingV2(List<RingEntry> ring, Dictionary<long, uint> floor)
        {
            try
            {
                if (ring == null)
                    ring = new List<RingEntry>();
                List<RingEntry> sane = new List<RingEntry>(ring.Count);
                for (int i = 0; i < ring.Count; i++)
                {
                    RingEntry e = ring[i];
                    if (e.Sender != 0 && TxIdGen.PeerKey(e.Sender) != TxIdGen.PeerOf(e.TxId))
                        continue;
                    if (e.Status == TxStatus.UnknownTx)
                        continue;
                    sane.Add(e);
                }
                while (sane.Count > TxLimits.RingCap)
                    sane.RemoveAt(0);
                List<KeyValuePair<long, uint>> floors = new List<KeyValuePair<long, uint>>();
                if (floor != null)
                {
                    foreach (KeyValuePair<long, uint> kv in floor)
                    {
                        // v2 carries the floor in a single count byte: at most 255 entries fit.
                        // Overflow truncates the EMBEDDED copy only (the independent floor key carries
                        // the full map in v3 worlds); production writes v3, so this path is legacy/tests.
                        if (floors.Count >= 255)
                            break;
                        floors.Add(kv);
                    }
                }
                List<byte> buf = new List<byte>(128);
                buf.Add(TxLimits.RingV2Magic);
                buf.Add(TxLimits.RingV2Version);
                buf.Add((byte)floors.Count);
                for (int i = 0; i < floors.Count; i++)
                {
                    buf.AddRange(BitConverter.GetBytes(floors[i].Key));
                    buf.AddRange(BitConverter.GetBytes(floors[i].Value));
                }
                buf.Add((byte)sane.Count);
                for (int i = 0; i < sane.Count; i++)
                    AppendEntry(buf, sane[i]);
                return buf.ToArray();
            }
            catch (Exception)
            {
                // Never replace a failed encode with valid empty state: that would
                // wipe the floor and resurrect evicted txIds. Null = persist nothing.
                return null;
            }
        }

        /// <summary>
        /// v3 envelope encode (production writer): entries carry the sender as an
        /// unambiguous uint32 sender KEY and the floor uses uint32 keys + ushort
        /// counts, so an unbounded floor round-trips exactly. v1/v2 stay
        /// decodable; v3 is never migrated in place. Returns null on failure
        /// (persist nothing). Never throws.
        /// </summary>
        public static byte[] EncodeRingV3(List<RingEntry> ring, Dictionary<long, uint> floor)
        {
            try
            {
                if (ring == null)
                    ring = new List<RingEntry>();
                List<RingEntry> sane = new List<RingEntry>(ring.Count);
                for (int i = 0; i < ring.Count; i++)
                {
                    RingEntry e = ring[i];
                    if (e.Sender != 0 && TxIdGen.PeerKey(e.Sender) != TxIdGen.PeerOf(e.TxId))
                        continue;
                    if (e.Status == TxStatus.UnknownTx)
                        continue;
                    sane.Add(e);
                }
                while (sane.Count > TxLimits.RingCap)
                    sane.RemoveAt(0);
                List<KeyValuePair<long, uint>> floors = new List<KeyValuePair<long, uint>>();
                if (floor != null)
                {
                    foreach (KeyValuePair<long, uint> kv in floor)
                        floors.Add(kv);
                }
                if (floors.Count > 65535 || sane.Count > TxLimits.RingCap)
                    return null;
                List<byte> buf = new List<byte>(128);
                buf.Add(TxLimits.RingV2Magic);
                buf.Add(TxLimits.RingV3Version);
                buf.Add((byte)(floors.Count & 0xFF));
                buf.Add((byte)((floors.Count >> 8) & 0xFF));
                for (int i = 0; i < floors.Count; i++)
                {
                    uint peerKey = (uint)(TxIdGen.PeerKey(floors[i].Key) & 0xFFFFFFFFL);
                    buf.AddRange(BitConverter.GetBytes(peerKey));
                    buf.AddRange(BitConverter.GetBytes(floors[i].Value));
                }
                buf.Add((byte)(sane.Count & 0xFF));
                buf.Add((byte)((sane.Count >> 8) & 0xFF));
                for (int i = 0; i < sane.Count; i++)
                    AppendEntryV3(buf, sane[i]);
                return buf.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AppendEntryV3(List<byte> buf, RingEntry e)
        {
            List<int> acc = e.Accepted;
            if (acc == null)
                acc = new List<int> { e.AcceptedTotal };
            int accCount = Math.Min(acc.Count, TxLimits.MaxAcceptedPerEntry);
            buf.AddRange(BitConverter.GetBytes(e.TxId));
            buf.AddRange(BitConverter.GetBytes((int)e.Op));
            buf.Add((byte)e.Status);
            buf.AddRange(BitConverter.GetBytes(e.Revision));
            long key = e.Sender != 0 ? TxIdGen.PeerKey(e.Sender) : TxIdGen.PeerOf(e.TxId);
            buf.AddRange(BitConverter.GetBytes((uint)(key & 0xFFFFFFFFL)));
            buf.Add((byte)accCount);
            for (int i = 0; i < accCount; i++)
                buf.AddRange(BitConverter.GetBytes(acc[i]));
            List<TakePayload> takes = e.TakePayloads;
            int takeCount = takes != null ? Math.Min(takes.Count, TxLimits.MaxTakePayloadsPerEntry) : 0;
            buf.Add((byte)takeCount);
            for (int i = 0; i < takeCount; i++)
            {
                TakePayload p = takes[i];
                if (p == null || p.Bytes == null)
                {
                    buf.AddRange(BitConverter.GetBytes(0));
                    buf.AddRange(BitConverter.GetBytes(0));
                    continue;
                }
                int len = Math.Min(p.Bytes.Length, TxLimits.MaxTakePayloadBytes);
                buf.AddRange(BitConverter.GetBytes(p.PrefabHash));
                buf.AddRange(BitConverter.GetBytes(len));
                for (int b = 0; b < len; b++)
                    buf.Add(p.Bytes[b]);
            }
        }

        private static void AppendEntry(List<byte> buf, RingEntry e)
        {
            List<int> acc = e.Accepted;
            if (acc == null)
                acc = new List<int> { e.AcceptedTotal };
            int accCount = Math.Min(acc.Count, TxLimits.MaxAcceptedPerEntry);
            buf.AddRange(BitConverter.GetBytes(e.TxId));
            buf.AddRange(BitConverter.GetBytes((int)e.Op));
            buf.Add((byte)e.Status);
            buf.AddRange(BitConverter.GetBytes(e.Revision));
            long sender = e.Sender != 0 ? e.Sender : TxIdGen.PeerOf(e.TxId);
            buf.AddRange(BitConverter.GetBytes(sender));
            buf.Add((byte)accCount);
            for (int i = 0; i < accCount; i++)
                buf.AddRange(BitConverter.GetBytes(acc[i]));
            List<TakePayload> takes = e.TakePayloads;
            int takeCount = takes != null ? Math.Min(takes.Count, TxLimits.MaxTakePayloadsPerEntry) : 0;
            buf.Add((byte)takeCount);
            for (int i = 0; i < takeCount; i++)
            {
                TakePayload p = takes[i];
                if (p == null || p.Bytes == null)
                {
                    buf.AddRange(BitConverter.GetBytes(0));
                    buf.AddRange(BitConverter.GetBytes(0));
                    continue;
                }
                int len = Math.Min(p.Bytes.Length, TxLimits.MaxTakePayloadBytes);
                buf.AddRange(BitConverter.GetBytes(p.PrefabHash));
                buf.AddRange(BitConverter.GetBytes(len));
                for (int b = 0; b < len; b++)
                    buf.Add(p.Bytes[b]);
            }
        }

        /// <summary>
        /// Independent floor encode (separate ZDO key): [0xFE][0x01][count:ushort][peerKey:uint32,ctr:uint32]...
        /// Keys are canonical. Returns null on failure (persist nothing). Never throws.
        /// </summary>
        public static byte[] EncodeFloor(Dictionary<long, uint> floor)
        {
            try
            {
                List<KeyValuePair<long, uint>> peers = new List<KeyValuePair<long, uint>>();
                if (floor != null)
                {
                    foreach (KeyValuePair<long, uint> kv in floor)
                        peers.Add(kv);
                }
                if (peers.Count > 65535)
                    return null;
                List<byte> buf = new List<byte>(4 + peers.Count * 8);
                buf.Add(TxLimits.FloorMagic);
                buf.Add(TxLimits.FloorVersion);
                buf.Add((byte)(peers.Count & 0xFF));
                buf.Add((byte)((peers.Count >> 8) & 0xFF));
                for (int i = 0; i < peers.Count; i++)
                {
                    uint peerKey = (uint)(TxIdGen.PeerKey(peers[i].Key) & 0xFFFFFFFFL);
                    buf.AddRange(BitConverter.GetBytes(peerKey));
                    buf.AddRange(BitConverter.GetBytes(peers[i].Value));
                }
                return buf.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Independent floor decode shared by production ReadFloor and tests.
        /// Null (missing ZDO key) decodes as absent-but-usable (Present=false:
        /// fall back to the ring-embedded copy); present-but-unparseable
        /// decodes as Corrupt. Never throws.
        /// </summary>
        public static FloorData DecodeFloor(byte[] buf)
        {
            FloorData data = new FloorData();
            if (buf == null || buf.Length == 0)
                return data;
            data.Present = true;
            try
            {
                if (buf.Length < 4)
                {
                    data.Corrupt = true;
                    return data;
                }
                if (buf[0] != TxLimits.FloorMagic || buf[1] != TxLimits.FloorVersion)
                {
                    data.Corrupt = true;
                    return data;
                }
                int count = (int)buf[2] | ((int)buf[3] << 8);
                if (count < 0 || 4 + count * 8 != buf.Length)
                {
                    data.Corrupt = true;
                    return data;
                }
                int o = 4;
                for (int i = 0; i < count; i++)
                {
                    uint peerKey = BitConverter.ToUInt32(buf, o); o += 4;
                    uint ctr = BitConverter.ToUInt32(buf, o); o += 4;
                    long peer = (long)peerKey;
                    uint hw;
                    if (data.Floor.TryGetValue(peer, out hw))
                    {
                        if (ctr > hw)
                            data.Floor[peer] = ctr;
                    }
                    else
                    {
                        data.Floor[peer] = ctr;
                    }
                }
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

        private static RingData DecodeV2(byte[] buf)
        {
            RingData bad = new RingData();
            bad.Corrupt = true;
            RingData data = new RingData();
            int o = 0;
            if (buf.Length < 4)
                return bad;
            o++; // magic
            byte ver = buf[o++];
            if (ver != TxLimits.RingV2Version)
                return bad;
            int floorCount = (int)buf[o++];
            // v2 floor count is one byte (0..255 by construction); the per-entry
            // length guards below bound memory. The live floor map itself is unbounded.
            if (floorCount < 0 || floorCount > 255)
                return bad;
            for (int i = 0; i < floorCount; i++)
            {
                if (o + 12 > buf.Length)
                    return bad;
                long peer = TxIdGen.PeerKey(BitConverter.ToInt64(buf, o)); o += 8;
                uint ctr = BitConverter.ToUInt32(buf, o); o += 4;
                uint hw;
                if (data.Floor.TryGetValue(peer, out hw))
                {
                    if (ctr > hw)
                        data.Floor[peer] = ctr;
                }
                else
                {
                    data.Floor[peer] = ctr;
                }
            }
            if (o + 1 > buf.Length)
                return bad;
            int ringCount = (int)buf[o++];
            if (ringCount < 0 || ringCount > TxLimits.RingCap)
                return bad;
            for (int i = 0; i < ringCount; i++)
            {
                RingEntry e;
                if (!ReadEntry(buf, ref o, out e))
                    return bad;
                data.Entries.Add(e);
            }
            if (o != buf.Length)
                return bad;
            return data;
        }

        /// <summary>
        /// v3 envelope decode: [0xFF][0x03][floorCount:ushort][peerKey:uint32,ctr:uint32]...
        /// [ringCount:ushort][entries with uint32 senderKey]... Exact consumption required.
        /// Sender keys compare directly against the txId peer bits (both canonical by
        /// construction); any mismatch fails the whole envelope closed. Never throws.
        /// </summary>
        private static RingData DecodeV3(byte[] buf)
        {
            RingData bad = new RingData();
            bad.Corrupt = true;
            RingData data = new RingData();
            if (buf.Length < 6)
                return bad;
            int o = 2; // magic + version (already dispatched)
            int floorCount = (int)buf[o] | ((int)buf[o + 1] << 8); o += 2;
            if (floorCount < 0 || o + floorCount * 8 > buf.Length)
                return bad;
            for (int i = 0; i < floorCount; i++)
            {
                uint peerKey = BitConverter.ToUInt32(buf, o); o += 4;
                uint ctr = BitConverter.ToUInt32(buf, o); o += 4;
                long peer = (long)peerKey;
                uint hw;
                if (data.Floor.TryGetValue(peer, out hw))
                {
                    if (ctr > hw)
                        data.Floor[peer] = ctr;
                }
                else
                {
                    data.Floor[peer] = ctr;
                }
            }
            if (o + 2 > buf.Length)
                return bad;
            int ringCount = (int)buf[o] | ((int)buf[o + 1] << 8); o += 2;
            if (ringCount < 0 || ringCount > TxLimits.RingCap)
                return bad;
            for (int i = 0; i < ringCount; i++)
            {
                RingEntry e;
                if (!ReadEntryV3(buf, ref o, out e))
                    return bad;
                data.Entries.Add(e);
            }
            if (o != buf.Length)
                return bad;
            return data;
        }

        private static bool ReadEntryV3(byte[] buf, ref int o, out RingEntry e)
        {
            e = new RingEntry();
            if (o + 8 + 4 + 1 + 4 + 4 + 1 > buf.Length)
                return false;
            e.TxId = BitConverter.ToInt64(buf, o); o += 8;
            e.Op = (TxOp)BitConverter.ToInt32(buf, o); o += 4;
            byte st = buf[o++];
            if (st > (byte)TxStatus.UnknownTx)
                return false;
            e.Status = (TxStatus)st;
            if (e.Status == TxStatus.UnknownTx)
                return false;
            e.Revision = BitConverter.ToUInt32(buf, o); o += 4;
            long senderKey = (long)BitConverter.ToUInt32(buf, o); o += 4;
            if (senderKey != TxIdGen.PeerOf(e.TxId))
                return false;
            e.Sender = senderKey;
            if (e.Status != TxStatus.Duplicate && (int)e.Op == 0)
                return false;
            int accCount = (int)buf[o++];
            if (accCount < 0 || accCount > TxLimits.MaxAcceptedPerEntry)
                return false;
            if (o + accCount * 4 > buf.Length)
                return false;
            e.Accepted = new List<int>(accCount);
            for (int i = 0; i < accCount; i++)
            {
                e.Accepted.Add(BitConverter.ToInt32(buf, o)); o += 4;
            }
            e.AcceptedTotal = 0;
            for (int i = 0; i < e.Accepted.Count; i++)
                e.AcceptedTotal += e.Accepted[i];
            if (o + 1 > buf.Length)
                return false;
            int takeCount = (int)buf[o++];
            if (takeCount < 0 || takeCount > TxLimits.MaxTakePayloadsPerEntry)
                return false;
            e.TakePayloads = takeCount > 0 ? new List<TakePayload>(takeCount) : null;
            for (int i = 0; i < takeCount; i++)
            {
                if (o + 8 > buf.Length)
                    return false;
                int prefab = BitConverter.ToInt32(buf, o); o += 4;
                int len = BitConverter.ToInt32(buf, o); o += 4;
                if (len < 0 || len > TxLimits.MaxTakePayloadBytes)
                    return false;
                if (o + len > buf.Length)
                    return false;
                if (len == 0 && prefab == 0)
                {
                    e.TakePayloads.Add(null);
                }
                else
                {
                    byte[] bytes = new byte[len];
                    Array.Copy(buf, o, bytes, 0, len);
                    o += len;
                    TakePayload p = new TakePayload();
                    p.PrefabHash = prefab;
                    p.Bytes = bytes;
                    e.TakePayloads.Add(p);
                }
            }
            return true;
        }

        private static bool ReadEntry(byte[] buf, ref int o, out RingEntry e)
        {
            e = new RingEntry();
            if (o + 8 + 4 + 1 + 4 + 8 + 1 > buf.Length)
                return false;
            e.TxId = BitConverter.ToInt64(buf, o); o += 8;
            e.Op = (TxOp)BitConverter.ToInt32(buf, o); o += 4;
            byte st = buf[o++];
            if (st > (byte)TxStatus.UnknownTx)
                return false;
            e.Status = (TxStatus)st;
            if (e.Status == TxStatus.UnknownTx)
                return false;
            e.Revision = BitConverter.ToUInt32(buf, o); o += 4;
            long rawSender = BitConverter.ToInt64(buf, o); o += 8;
            // Canonical tolerance: v2 worlds written before canonicalization stored the
            // raw signed UID. Accept when its KEY matches the txId peer bits and normalize
            // to the canonical key; a true key mismatch is still fail-closed (whole envelope
            // corrupt), so spoofed entries can never smuggle through a persisted ring.
            if (TxIdGen.PeerKey(rawSender) != TxIdGen.PeerOf(e.TxId))
                return false;
            e.Sender = TxIdGen.PeerOf(e.TxId);
            if (e.Status != TxStatus.Duplicate && (int)e.Op == 0)
                return false;
            int accCount = (int)buf[o++];
            if (accCount < 0 || accCount > TxLimits.MaxAcceptedPerEntry)
                return false;
            if (o + accCount * 4 > buf.Length)
                return false;
            e.Accepted = new List<int>(accCount);
            for (int i = 0; i < accCount; i++)
            {
                e.Accepted.Add(BitConverter.ToInt32(buf, o)); o += 4;
            }
            e.AcceptedTotal = 0;
            for (int i = 0; i < e.Accepted.Count; i++)
                e.AcceptedTotal += e.Accepted[i];
            if (o + 1 > buf.Length)
                return false;
            int takeCount = (int)buf[o++];
            if (takeCount < 0 || takeCount > TxLimits.MaxTakePayloadsPerEntry)
                return false;
            e.TakePayloads = takeCount > 0 ? new List<TakePayload>(takeCount) : null;
            for (int i = 0; i < takeCount; i++)
            {
                if (o + 8 > buf.Length)
                    return false;
                int prefab = BitConverter.ToInt32(buf, o); o += 4;
                int len = BitConverter.ToInt32(buf, o); o += 4;
                if (len < 0 || len > TxLimits.MaxTakePayloadBytes)
                    return false;
                if (o + len > buf.Length)
                    return false;
                if (len == 0 && prefab == 0)
                {
                    e.TakePayloads.Add(null);
                }
                else
                {
                    byte[] bytes = new byte[len];
                    Array.Copy(buf, o, bytes, 0, len);
                    o += len;
                    TakePayload p = new TakePayload();
                    p.PrefabHash = prefab;
                    p.Bytes = bytes;
                    e.TakePayloads.Add(p);
                }
            }
            return true;
        }

        private TxResult Execute(ModelChest chest, TxRequest request)
        {
            switch (request.Op)
            {
                case TxOp.Add:
                    return ExecuteAdd(chest, request, false);
                case TxOp.AddBatch:
                    return ExecuteAdd(chest, request, true);
                case TxOp.Take:
                    return ExecuteTake(chest, request, false);
                case TxOp.TakeBatch:
                    return ExecuteTake(chest, request, true);
                case TxOp.Move:
                    return ExecuteMove(chest, request);
                case TxOp.Sort:
                    return ExecuteSort(chest, request);
                default:
                    return new TxResult { Status = TxStatus.Rejected };
            }
        }

        private TxResult ExecuteAdd(ModelChest chest, TxRequest request, bool batch)
        {
            TxResult result = new TxResult();
            int total = 0;
            int full = 0;
            for (int i = 0; i < request.Items.Count; i++)
            {
                if (!batch && i > 0)
                    break;
                TxItem it = request.Items[i];
                int accepted = chest.AddItem(it.Key, it.Amount, it.MaxStack, it.X, it.Y);
                result.Accepted.Add(accepted);
                total += accepted;
                if (accepted == it.Amount)
                    full++;
                if (i == 0 && FailExecuteHook(request))
                    throw new InvalidOperationException("injected execute fault after first add");
            }
            if (total > 0)
                Revision++;
            result.Status = full == result.Accepted.Count && result.Accepted.Count > 0
                ? TxStatus.Accepted
                : (total > 0 ? TxStatus.Partial : TxStatus.Rejected);
            RecordTakeBlobs(request, result);
            return result;
        }

        private TxResult ExecuteTake(ModelChest chest, TxRequest request, bool batch)
        {
            TxResult result = new TxResult();
            int total = 0;
            int full = 0;
            for (int i = 0; i < request.Items.Count; i++)
            {
                if (!batch && i > 0)
                    break;
                TxItem it = request.Items[i];
                int taken = chest.TakeItem(it.Key, it.Amount);
                result.Accepted.Add(taken);
                total += taken;
                if (taken == it.Amount)
                    full++;
                if (i == 0 && FailExecuteHook(request))
                    throw new InvalidOperationException("injected execute fault after first take");
            }
            if (total > 0)
                Revision++;
            result.Status = full == result.Accepted.Count && result.Accepted.Count > 0
                ? TxStatus.Accepted
                : (total > 0 ? TxStatus.Partial : TxStatus.Rejected);
            RecordTakeBlobs(request, result);
            return result;
        }

        /// <summary>
        /// Remembers the Take metadata blobs for the just-executed tx so the
        /// ring snapshot can carry them (CollectSlots picks them up by txId).
        /// </summary>
        private void RecordTakeBlobs(TxRequest request, TxResult result)
        {
            if (!TxResponsePolicy.IsTakeOp(request.Op))
                return;
            if (result.Accepted.Count != request.Items.Count)
                return;
            List<TakePayload> blobs = new List<TakePayload>(result.Accepted.Count);
            for (int i = 0; i < result.Accepted.Count; i++)
            {
                if (result.Accepted[i] <= 0)
                {
                    blobs.Add(null);
                    continue;
                }
                TakePayload p = new TakePayload();
                p.PrefabHash = request.Items[i].Key.Hash;
                p.Bytes = EncodeTakeKey(request.Items[i].Key);
                blobs.Add(p);
            }
            _takeBlobs[request.TxId] = blobs;
        }

        private TxResult ExecuteMove(ModelChest chest, TxRequest request)
        {
            TxResult result = new TxResult();
            if (request.Items.Count == 0)
            {
                result.Status = TxStatus.Rejected;
                return result;
            }
            TxItem it = request.Items[0];
            bool ok = chest.Move(it.Key, it.X, it.Y, request.DstX, request.DstY, it.Amount, it.MaxStack);
            if (FailExecuteHook(request))
                throw new InvalidOperationException("injected execute fault after move");
            if (ok)
                Revision++;
            result.Status = ok ? TxStatus.Accepted : TxStatus.Rejected;
            result.Accepted.Add(ok ? it.Amount : 0);
            return result;
        }

        private TxResult ExecuteSort(ModelChest chest, TxRequest request)
        {
            // Sort only changes positions: conservation is trivial, revision grows.
            // Deterministic order: (hash, quality, variant, world).
            List<int[]> snap = chest.Snapshot();
            snap.Sort(delegate (int[] a, int[] b)
            {
                for (int i = 1; i <= 4; i++)
                {
                    int c = a[i].CompareTo(b[i]);
                    if (c != 0)
                        return request.Desc ? -c : c;
                }
                return 0;
            });
            // Rebuild cells row by row, preserving stacks.
            ModelChest rebuilt = new ModelChest(chest.Width, chest.Height);
            for (int i = 0; i < snap.Count; i++)
            {
                int[] e = snap[i];
                rebuilt.AddItem(new ItemKey(e[1], e[2], e[3], e[4]), e[5], e[6]);
            }
            chest.Restore(rebuilt.Snapshot());
            if (FailExecuteHook(request))
                throw new InvalidOperationException("injected execute fault after sort");
            Revision++;
            TxResult result = new TxResult { Status = TxStatus.Accepted };
            result.Accepted.Add(snap.Count);
            return result;
        }
    }
}
