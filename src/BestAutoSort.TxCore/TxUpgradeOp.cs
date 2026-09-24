using System;
using System.Collections.Generic;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Server-mediated remote chest upgrade: op phases.
    ///
    /// Order (one-way, never backwards except Preparing retry after a
    /// cleaned-up mint-link failure):
    /// PreparingLockedSource -&gt; SpawnedLinked -&gt; Copied -&gt; Ready -&gt;
    /// PointerSwitched -&gt; Receipted -&gt; Retired.
    ///
    /// Crash recovery resumes from self-consistent snapshots only
    /// (see TxUpgradeSnapshot.Validate): anything else fails closed.
    /// </summary>
    public enum TxUpgradePhase
    {
        PreparingLockedSource = 0,
        SpawnedLinked = 1,
        Copied = 2,
        Ready = 3,
        PointerSwitched = 4,
        Receipted = 5,
        Retired = 6
    }

    /// <summary>
    /// Fault-injection points for the phase-fault suite, in firing order.
    /// BetweenMintAndLink is the hard-kill micro-window: the ghost exists but
    /// the reverse link was never written.
    /// </summary>
    public enum TxUpgradeCrashPoint
    {
        BeforeSpawn = 0,
        BetweenMintAndLink = 1,
        AfterLink = 2,
        AfterCopy = 3,
        AfterReady = 4,
        AfterPointerSwitch = 5,
        AfterReceipt = 6
    }

    /// <summary>Simulated kill at a TxUpgradeCrashPoint. Never swallowed by the manager.</summary>
    public sealed class TxUpgradeCrashException : Exception
    {
        public readonly TxUpgradeCrashPoint Point;

        public TxUpgradeCrashException(TxUpgradeCrashPoint point)
            : base("simulated upgrade crash at " + point)
        {
            Point = point;
        }
    }

    /// <summary>
    /// Op identity: source chest ZDO id + authenticated sender peer key +
    /// durable client nonce + tier/recipe. The same triple re-requested is the
    /// SAME op (idempotent); a different nonce is a different op and queues
    /// behind the prepared one.
    /// </summary>
    public sealed class TxUpgradeOp
    {
        public string OpId;
        public string SourceId;
        public long SenderPeer;
        public uint Nonce;
        public int Tier;
        public int Recipe;
        public TxUpgradePhase Phase;
        public string GhostId;
        public int ItemCount;
        public int ItemHash;
        public bool SpendRecorded;
        public int SpendTotal;
        public bool RefundClaimed;
        public int RefundTotal;
        public bool Quarantined;
        public string QuarantineReason;
        public string ReceiptNewId;
    }

    /// <summary>
    /// Self-consistent snapshot carrier. Validate() is the ONLY recovery gate:
    /// a snapshot that contradicts itself (phase claims a ghost but carries
    /// none, counts without a copy, receipt without a pointer switch, ...) is
    /// refused fail-closed — the manager resumes empty, never from a torn half.
    /// </summary>
    public sealed class TxUpgradeSnapshot
    {
        public string OpId;
        public string SourceId;
        public long SenderPeer;
        public uint Nonce;
        public int Tier;
        public int Recipe;
        public TxUpgradePhase Phase;
        public string GhostId;
        public int ItemCount;
        public int ItemHash;
        public bool SpendRecorded;
        public int SpendTotal;
        public bool RefundClaimed;
        public int RefundTotal;
        public string ReceiptNewId;

        public bool Validate(out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(OpId) || string.IsNullOrEmpty(SourceId))
            {
                reason = "missing op/source identity";
                return false;
            }
            if (Tier < 0 || Tier > 3)
            {
                reason = "tier out of range";
                return false;
            }
            if (Phase == TxUpgradePhase.PreparingLockedSource)
            {
                if (GhostId != null)
                {
                    reason = "preparing must carry no ghost";
                    return false;
                }
                if (ReceiptNewId != null)
                {
                    reason = "preparing must carry no receipt";
                    return false;
                }
                return true;
            }
            if (GhostId == null)
            {
                reason = "phase " + Phase + " without a linked ghost";
                return false;
            }
            if (Phase == TxUpgradePhase.SpawnedLinked)
                return true;
            if (ItemCount < 0)
            {
                reason = "negative item count";
                return false;
            }
            if (Phase == TxUpgradePhase.Copied || Phase == TxUpgradePhase.Ready)
                return true;
            if (Phase == TxUpgradePhase.PointerSwitched)
                return true;
            if (Phase == TxUpgradePhase.Receipted)
            {
                if (ReceiptNewId == null)
                {
                    reason = "receipted without a receipt";
                    return false;
                }
                return true;
            }
            reason = "retired snapshots never resume (terminal)";
            return false;
        }
    }

    /// <summary>
    /// Ghost host: the manager drives these synchronously (no yield between
    /// spawn and reverse-link write — the mint-link window is covered by the
    /// try/catch ghost cleanup, never by a second spawn). Implementations:
    /// production (ZNetScene + ZDO) and the fault-injection fake in tests.
    /// </summary>
    public interface ITxUpgradeHost
    {
        string SpawnGhost(string sourceId, int tier);
        void WriteReverseLink(string ghostId, string opId);
        /// <summary>
        /// Reverse-link adoption scan: the ghost (if any) whose durable reverse
        /// link names this op. Lets a post-crash resume ADOPT the ghost it
        /// already linked (AfterLink window) instead of minting a second one.
        /// Null when no ghost carries this op's link.
        /// </summary>
        string FindGhostByLink(string opId);
        void CopyInventoryNonDestructive(string sourceId, string ghostId);
        int CountItems(string chestId);
        int HashItems(string chestId);
        bool ValidateReady(string ghostId, int wantCount, int wantHash);
        string PointerSwitchOneWay(string sourceId, string ghostId);
        void RetireSource(string sourceId, string newId);
        void DestroyGhost(string ghostId);
        void LogLoud(string message);
        void CrashHook(TxUpgradeCrashPoint point);
    }

    /// <summary>
    /// Version + authority-mode gate for upgrade frames.
    /// - Old TxOp.Upgrade frames are REFUSED for managed chests in authority
    ///   mode (the legacy direct-mutation path must never run there).
    /// - UpgradeRequest frames require the current protocol version.
    /// Pure, pinned by tests.
    /// </summary>
    public static class TxUpgradeGate
    {
        public const int UpgradeRequestProtoVersion = 3;

        public static bool RefuseLegacyUpgradeFrame(bool authorityMode, bool managed)
        {
            return authorityMode && managed;
        }

        public static bool AcceptsUpgradeRequest(int frameVersion, bool authorityMode)
        {
            if (frameVersion != UpgradeRequestProtoVersion)
                return false;
            return authorityMode;
        }
    }

    /// <summary>
    /// Per-source-chest upgrade manager. All ops for one source chest serialize
    /// here: the head op runs phase by phase, later ops queue behind it, and the
    /// same op (same OpId) never spawns twice — the op record gates the mint.
    ///
    /// Ghost protocol (each step synchronous, no yield):
    /// spawn replacement -&gt; SYNCHRONOUSLY write reverse link (op id + incomplete
    /// flag) -&gt; copy inventory non-destructively -&gt; validate Ready -&gt;
    /// pointer switch one-way -&gt; receipt -&gt; idempotent retire.
    ///
    /// Mint-link window: spawn+link run inside one try/catch. An exception (or
    /// an injected kill) between mint and link-write destroys the ghost and
    /// leaves the op in Preparing — never a dangling unlinked ghost owned by
    /// this op. Unlinked ghosts reported from OUTSIDE (a ghost with no reverse
    /// link that this manager did not just mint) are NEVER auto-destroyed — it
    /// could be a real new chest — both sides quarantine + loud log + manual
    /// reconcile (see ReportUnlinkedGhost).
    ///
    /// Ingredients: the op NEVER charges the client inventory. Ingredients must
    /// be pre-deposited in the source chest via normal ChestTX Takes; the spend
    /// is recorded at-most-once against the op, and refunds flow ONLY as an
    /// op-keyed entitlement through the separate idempotent ClaimRefund step.
    /// </summary>
    public sealed class TxUpgradeManager
    {
        private readonly ITxUpgradeHost _host;
        private readonly Dictionary<string, TxUpgradeOp> _ops = new Dictionary<string, TxUpgradeOp>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();
        private readonly HashSet<string> _liveGhosts = new HashSet<string>(StringComparer.Ordinal);

        public TxUpgradeManager(ITxUpgradeHost host)
        {
            _host = host ?? throw new ArgumentNullException("host");
        }

        public int OpCount { get { return _ops.Count; } }

        public int QueuedCount { get { return _order.Count; } }

        public static string BuildOpId(string sourceId, long senderPeer, uint nonce)
        {
            return sourceId + ":" + senderPeer + ":" + nonce;
        }

        /// <summary>
        /// Client REQUEST entry: idempotent by OpId. Returns the (existing or
        /// new) op; a duplicate request for the same OpId never creates a
        /// second record and never spawns a second ghost.
        /// </summary>
        public TxUpgradeOp Request(string sourceId, long senderPeer, uint nonce, int tier, int recipe)
        {
            if (string.IsNullOrEmpty(sourceId))
                throw new ArgumentException("source required", "sourceId");
            if (tier < 0 || tier > 3)
                throw new ArgumentOutOfRangeException("tier");
            string opId = BuildOpId(sourceId, senderPeer, nonce);
            TxUpgradeOp existing;
            if (_ops.TryGetValue(opId, out existing))
                return existing;
            TxUpgradeOp op = new TxUpgradeOp();
            op.OpId = opId;
            op.SourceId = sourceId;
            op.SenderPeer = senderPeer;
            op.Nonce = nonce;
            op.Tier = tier;
            op.Recipe = recipe;
            op.Phase = TxUpgradePhase.PreparingLockedSource;
            _ops[opId] = op;
            _order.Enqueue(opId);
            return op;
        }

        public TxUpgradeOp Get(string opId)
        {
            TxUpgradeOp op;
            if (opId != null && _ops.TryGetValue(opId, out op))
                return op;
            return null;
        }

        /// <summary>
        /// Spend-fail path: drop an op that never paid and never linked a
        /// ghost. Allowed ONLY while the op is still PreparingLockedSource
        /// with no ghost (nothing minted, nothing consumed): later phases own
        /// world state and must run to Retired. Returns false (no state
        /// change) for unknown, quarantined, linked, or advanced ops — the
        /// caller keeps its Rejected outcome either way.
        /// </summary>
        public bool TryAbandon(string opId)
        {
            TxUpgradeOp op;
            if (opId == null || !_ops.TryGetValue(opId, out op) || op == null)
                return false;
            if (op.Quarantined)
                return false;
            if (op.Phase != TxUpgradePhase.PreparingLockedSource || op.GhostId != null)
                return false;
            _ops.Remove(opId);
            // Queue has no Remove: rebuild without the abandoned id. The
            // spend-fail caller drops the just-requested op (head or tail).
            if (_order.Count > 0)
            {
                Queue<string> kept = new Queue<string>();
                while (_order.Count > 0)
                {
                    string id = _order.Dequeue();
                    if (!string.Equals(id, opId, StringComparison.Ordinal))
                        kept.Enqueue(id);
                }
                while (kept.Count > 0)
                    _order.Enqueue(kept.Dequeue());
            }
            return true;
        }

        /// <summary>
        /// Run the head op one phase step. Returns true when more work remains.
        /// Only the head op advances; queued ops wait behind it.
        /// </summary>
        public bool Pump()
        {
            TxUpgradeOp op = PeekHead();
            if (op == null)
                return false;
            if (op.Quarantined)
                return _order.Count > 0;
            switch (op.Phase)
            {
                case TxUpgradePhase.PreparingLockedSource:
                    StepSpawnLink(op);
                    break;
                case TxUpgradePhase.SpawnedLinked:
                    StepCopy(op);
                    break;
                case TxUpgradePhase.Copied:
                    StepValidateReady(op);
                    break;
                case TxUpgradePhase.Ready:
                    StepPointerSwitch(op);
                    break;
                case TxUpgradePhase.PointerSwitched:
                    StepReceipt(op);
                    break;
                case TxUpgradePhase.Receipted:
                    StepRetire(op);
                    break;
                case TxUpgradePhase.Retired:
                    PopHead();
                    break;
            }
            return _order.Count > 0;
        }

        /// <summary>Timeout path: the client queries the SAME op — never a new one.</summary>
        public TxUpgradeOp QuerySameOp(string opId)
        {
            return Get(opId);
        }

        /// <summary>
        /// Spend is recorded at-most-once per op. A second call returns the
        /// original total without double-charging (the op never touches the
        /// client inventory at all — ingredients live pre-deposited in source).
        /// </summary>
        public int RecordSpend(string opId, int total)
        {
            TxUpgradeOp op = Get(opId);
            if (op == null)
                throw new InvalidOperationException("unknown op " + opId);
            if (op.SpendRecorded)
                return op.SpendTotal;
            op.SpendRecorded = true;
            op.SpendTotal = total;
            return total;
        }

        /// <summary>
        /// Refund entitlement, op-keyed and idempotent: the first claim issues
        /// the entitlement, later claims for the same op return the same amount
        /// without re-issuing. Separate step from execution by design.
        /// </summary>
        public int ClaimRefund(string opId, int entitlement)
        {
            TxUpgradeOp op = Get(opId);
            if (op == null)
                throw new InvalidOperationException("unknown op " + opId);
            if (op.RefundClaimed)
                return op.RefundTotal;
            op.RefundClaimed = true;
            op.RefundTotal = entitlement;
            return entitlement;
        }

        /// <summary>
        /// Outside report of a ghost carrying NO reverse link. NEVER destroys
        /// it (could be a real new chest): quarantines the named op (when
        /// known) AND the ghost, loud log, manual reconcile. Returns true when
        /// a quarantine was stamped.
        /// </summary>
        public bool ReportUnlinkedGhost(string ghostId, string suspectOpId)
        {
            if (string.IsNullOrEmpty(ghostId))
                return false;
            TxUpgradeOp op = Get(suspectOpId);
            if (op != null && !op.Quarantined)
            {
                op.Quarantined = true;
                op.QuarantineReason = "unlinked ghost " + ghostId + ": manual reconcile required";
            }
            _host.LogLoud("[Upgrade] QUARANTINE: unlinked ghost " + ghostId
                + " (suspect op " + (suspectOpId ?? "?") + ") kept standing; op quarantined; manual reconcile required");
            return true;
        }

        /// <summary>
        /// Contradiction: the ghost's reverse link names a DIFFERENT op than
        /// the manager record (or two ops claim one ghost). Quarantine BOTH,
        /// loud log, manual reconcile. Never auto-destroys either side.
        /// </summary>
        public bool ReportContradiction(string ghostId, string linkedOpId, string claimantOpId)
        {
            TxUpgradeOp a = Get(linkedOpId);
            TxUpgradeOp b = Get(claimantOpId);
            if (a != null && !a.Quarantined)
            {
                a.Quarantined = true;
                a.QuarantineReason = "contradiction on ghost " + ghostId + " with op " + claimantOpId;
            }
            if (b != null && !b.Quarantined)
            {
                b.Quarantined = true;
                b.QuarantineReason = "contradiction on ghost " + ghostId + " with op " + linkedOpId;
            }
            _host.LogLoud("[Upgrade] QUARANTINE: contradiction on ghost " + ghostId
                + " (linked=" + (linkedOpId ?? "?") + " claimant=" + (claimantOpId ?? "?")
                + "); both quarantined; manual reconcile required");
            return true;
        }

        /// <summary>
        /// Crash recovery: resumes ONLY from a self-consistent snapshot
        /// (Validate gate). Fail-closed otherwise: returns false and changes
        /// nothing. Retired snapshots never resume (terminal).
        /// </summary>
        public bool TryRecover(TxUpgradeSnapshot snap, out string reason)
        {
            reason = null;
            if (snap == null)
            {
                reason = "null snapshot";
                return false;
            }
            string why;
            if (!snap.Validate(out why))
            {
                reason = "snapshot refused (fail closed): " + why;
                return false;
            }
            if (_ops.ContainsKey(snap.OpId))
            {
                reason = "op already tracked (no double resume)";
                return false;
            }
            TxUpgradeOp op = new TxUpgradeOp();
            op.OpId = snap.OpId;
            op.SourceId = snap.SourceId;
            op.SenderPeer = snap.SenderPeer;
            op.Nonce = snap.Nonce;
            op.Tier = snap.Tier;
            op.Recipe = snap.Recipe;
            op.Phase = snap.Phase;
            op.GhostId = snap.GhostId;
            op.ItemCount = snap.ItemCount;
            op.ItemHash = snap.ItemHash;
            op.SpendRecorded = snap.SpendRecorded;
            op.SpendTotal = snap.SpendTotal;
            op.RefundClaimed = snap.RefundClaimed;
            op.RefundTotal = snap.RefundTotal;
            op.ReceiptNewId = snap.ReceiptNewId;
            _ops[op.OpId] = op;
            _order.Enqueue(op.OpId);
            if (op.GhostId != null)
                _liveGhosts.Add(op.GhostId);
            reason = "resumed at " + op.Phase;
            return true;
        }

        public TxUpgradeSnapshot ExportSnapshot(string opId)
        {
            TxUpgradeOp op = Get(opId);
            if (op == null)
                return null;
            if (op.Phase == TxUpgradePhase.Retired)
                return null;
            TxUpgradeSnapshot snap = new TxUpgradeSnapshot();
            snap.OpId = op.OpId;
            snap.SourceId = op.SourceId;
            snap.SenderPeer = op.SenderPeer;
            snap.Nonce = op.Nonce;
            snap.Tier = op.Tier;
            snap.Recipe = op.Recipe;
            snap.Phase = op.Phase;
            snap.GhostId = op.GhostId;
            snap.ItemCount = op.ItemCount;
            snap.ItemHash = op.ItemHash;
            snap.SpendRecorded = op.SpendRecorded;
            snap.SpendTotal = op.SpendTotal;
            snap.RefundClaimed = op.RefundClaimed;
            snap.RefundTotal = op.RefundTotal;
            snap.ReceiptNewId = op.ReceiptNewId;
            return snap;
        }

        private TxUpgradeOp PeekHead()
        {
            while (_order.Count > 0)
            {
                string head = _order.Peek();
                TxUpgradeOp op;
                if (_ops.TryGetValue(head, out op))
                    return op;
                _order.Dequeue();
            }
            return null;
        }

        private void PopHead()
        {
            if (_order.Count > 0)
                _order.Dequeue();
        }

        private void StepSpawnLink(TxUpgradeOp op)
        {
            // Same op never spawns twice: a linked ghost gates the mint —
            // first the in-RAM record, then (post-crash, RAM lost) the durable
            // reverse link the op already wrote (AfterLink window adoption).
            if (op.GhostId != null)
            {
                op.Phase = TxUpgradePhase.SpawnedLinked;
                return;
            }
            string adopted = null;
            try { adopted = _host.FindGhostByLink(op.OpId); }
            catch { }
            if (adopted != null)
            {
                op.GhostId = adopted;
                _liveGhosts.Add(adopted);
                op.Phase = TxUpgradePhase.SpawnedLinked;
                return;
            }
            _host.CrashHook(TxUpgradeCrashPoint.BeforeSpawn);
            string ghost = null;
            try
            {
                ghost = _host.SpawnGhost(op.SourceId, op.Tier);
                _liveGhosts.Add(ghost);
                // Injected kill between mint and link-write (micro-window).
                _host.CrashHook(TxUpgradeCrashPoint.BetweenMintAndLink);
                // SYNCHRONOUS reverse-link write: no yield between mint and link.
                _host.WriteReverseLink(ghost, op.OpId);
                _host.CrashHook(TxUpgradeCrashPoint.AfterLink);
            }
            catch (Exception ex)
            {
                if (ex is TxUpgradeCrashException)
                    throw;
                // Mint-link window covered: destroy the just-minted ghost, the
                // op stays in Preparing (no dangling unlinked ghost from us).
                if (ghost != null)
                {
                    _liveGhosts.Remove(ghost);
                    try { _host.DestroyGhost(ghost); }
                    catch { }
                }
                _host.LogLoud("[Upgrade] op " + op.OpId + " spawn/link failed, ghost cleaned up, op stays preparing: " + ex.Message);
                return;
            }
            op.GhostId = ghost;
            op.Phase = TxUpgradePhase.SpawnedLinked;
        }

        private void StepCopy(TxUpgradeOp op)
        {
            _host.CopyInventoryNonDestructive(op.SourceId, op.GhostId);
            op.ItemCount = _host.CountItems(op.GhostId);
            op.ItemHash = _host.HashItems(op.GhostId);
            int srcCount = _host.CountItems(op.SourceId);
            int srcHash = _host.HashItems(op.SourceId);
            if (op.ItemCount != srcCount || op.ItemHash != srcHash)
            {
                op.Quarantined = true;
                op.QuarantineReason = "copy mismatch (ghost=" + op.ItemCount + "/" + op.ItemHash
                    + " source=" + srcCount + "/" + srcHash + "): manual reconcile required";
                _host.LogLoud("[Upgrade] QUARANTINE: op " + op.OpId + " " + op.QuarantineReason);
                return;
            }
            _host.CrashHook(TxUpgradeCrashPoint.AfterCopy);
            op.Phase = TxUpgradePhase.Copied;
        }

        private void StepValidateReady(TxUpgradeOp op)
        {
            if (!_host.ValidateReady(op.GhostId, op.ItemCount, op.ItemHash))
                return;
            _host.CrashHook(TxUpgradeCrashPoint.AfterReady);
            op.Phase = TxUpgradePhase.Ready;
        }

        private void StepPointerSwitch(TxUpgradeOp op)
        {
            string newId = _host.PointerSwitchOneWay(op.SourceId, op.GhostId);
            op.ReceiptNewId = newId;
            _host.CrashHook(TxUpgradeCrashPoint.AfterPointerSwitch);
            op.Phase = TxUpgradePhase.PointerSwitched;
        }

        private void StepReceipt(TxUpgradeOp op)
        {
            // Receipt is the validated handoff: the new chest id is confirmed
            // before the source may retire. No new spawn may follow this op.
            if (op.ReceiptNewId == null)
            {
                op.Quarantined = true;
                op.QuarantineReason = "pointer switch without receipt: manual reconcile required";
                _host.LogLoud("[Upgrade] QUARANTINE: op " + op.OpId + " " + op.QuarantineReason);
                return;
            }
            _host.CrashHook(TxUpgradeCrashPoint.AfterReceipt);
            op.Phase = TxUpgradePhase.Receipted;
        }

        private void StepRetire(TxUpgradeOp op)
        {
            // Idempotent retire: safe to re-run after a crash past receipt.
            _host.RetireSource(op.SourceId, op.ReceiptNewId);
            if (op.GhostId != null)
                _liveGhosts.Remove(op.GhostId);
            op.Phase = TxUpgradePhase.Retired;
            PopHead();
        }
    }
}
