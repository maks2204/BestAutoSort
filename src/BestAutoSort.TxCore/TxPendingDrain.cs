using System;
using System.Collections.Generic;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Enumeration-safe pending-request drain shared by production (ChestTxService
    /// PumpPending) and the offline suite. The two wave-1 rules live HERE, not in
    /// a per-call-site loop:
    /// - NEVER mutate the pending dictionary while enumerating it: keys are
    ///   snapshotted up front, terminal entries are collected during the pass
    ///   and finalized (removed + single callback attempt + claim release) only
    ///   AFTER the loop. In-loop mutation throws InvalidOperationException and
    ///   risks skipping entries.
    /// - Single callback attempt per terminal: each collected key is finalized
    ///   once per pass (the finalizer itself must be idempotent:
    ///   remove-then-complete, guarded so a late duplicate finds no entry and
    ///   touches nothing). At-most-once submit, NOT end-to-end exactly-once
    ///   (no ACK/journal; committed-but-Indeterminate windows stay residual).
    /// Per-entry step callbacks may mutate ENTRY fields (attempt counters,
    /// deadlines) but must never add/remove dictionary entries — only the
    /// post-loop terminal pass removes.
    /// </summary>
    public static class TxPendingDrain
    {
        /// <summary>What one pump pass decides for one pending entry.</summary>
        public enum Step
        {
            /// <summary>Not due yet, or retry budget spent while the deadline is
            /// still ahead: leave pending, touch nothing.</summary>
            Wait = 0,
            /// <summary>Due: resend the original payload, bump attempts, re-arm.</summary>
            Resend = 1,
            /// <summary>Payload budget spent, query budget left: send one Query
            /// for the cached result instead of re-applying.</summary>
            Query = 2,
            /// <summary>Deadline hit with the last-chance query unspent: send it
            /// and extend the deadline exactly once.</summary>
            FinalQuery = 3,
            /// <summary>No queries left (or the entry is dead): terminal
            /// Indeterminate — collect post-loop for a single finalize attempt.</summary>
            Terminal = 4
        }

        /// <summary>
        /// Transient-entry decision (pure, testable): the counterpart of Decide
        /// for entries in TransientRetrySameTx state. The deadline NEVER
        /// finalizes a transient entry — past-deadline due entries Resend (the
        /// same txId, flagged, re-attempts persistence on the manager and never
        /// executes), not-due entries Wait. There is deliberately no
        /// FinalQuery/Terminal leg here: a transient entry leaves Transient
        /// state only via a durable manager response (or ownership loss), never
        /// via elapsed time. Callers MUST route transient entries here instead
        /// of Decide (production PumpPending branches on PendingTx.IsTransientRetry).
        /// Backoff between resends is bounded (see TransientBackoffSeconds).
        /// </summary>
        public static Step DecideTransient(float now, float nextTryAt)
        {
            if (now < nextTryAt)
                return Step.Wait;
            return Step.Resend;
        }

        /// <summary>
        /// Bounded backoff between transient same-tx resends (pure, testable):
        /// 3s base (one RequestTimeout), doubling per transient attempt, capped
        /// at 30s. Bounded delay, unbounded patience: the entry never goes
        /// Terminal on timing alone.
        /// </summary>
        public static float TransientBackoffSeconds(int transientAttempts)
        {
            if (transientAttempts <= 0)
                return 3f;
            if (transientAttempts >= 4)
                return 30f;
            return 3f * (float)(1 << transientAttempts);
        }

        /// <summary>
        /// What a transient Resend step may put on the wire (pure, testable).
        /// There are exactly two legs — deliberately NO unflagged-resend value:
        /// the stored first-attempt frame of a transient entry is unflagged and
        /// must never be resent (an unflagged resend of a held txId is only
        /// reminded TransientUnavailable: never reconciles, never executes —
        /// a non-progress ping). Encode-ok re-sends the freshly re-encoded
        /// flagged mutation (FlaggedMutation); ANY re-encode failure (stale
        /// Snapshot, empty Move items, destroyed container, missing call) sends
        /// a flagged Query for the SAME txId instead (FlaggedQuery: the manager
        /// answers TransientUnavailable while the refusal is held, or the cached
        /// terminal once durable — never UnknownTx for a held/flagged txId).
        /// Both legs retain Pending + claims + txId with backoff; neither goes
        /// terminal and neither mints a new txId. Production SendTransientRetry
        /// mirrors this seam statement by statement (see DecideTransientSend).
        /// </summary>
        public enum TransientSendAction
        {
            /// <summary>Freshly re-encoded flagged same-tx mutation.</summary>
            FlaggedMutation = 0,
            /// <summary>Flagged Query for the same txId (encode unavailable).</summary>
            FlaggedQuery = 1
        }

        /// <summary>
        /// Transient send-path decision (pure, testable): maps the re-encode
        /// outcome to the single wire action. encodeSucceeded true ONLY when the
        /// stored call was just re-encoded with IsTransientRetry in this pass
        /// (the fresh buffer replaces the stored unflagged bytes); false covers
        /// every other case (throw, null call) and NEVER maps back to the stored
        /// bytes. No third leg exists — see TransientSendAction.
        /// </summary>
        public static TransientSendAction DecideTransientSend(bool encodeSucceeded)
        {
            return encodeSucceeded ? TransientSendAction.FlaggedMutation : TransientSendAction.FlaggedQuery;
        }

        /// <summary>
        /// Stored-mutation-bytes send gate (pure, testable): the unflagged
        /// first-attempt frame may go back on the wire ONLY for non-transient
        /// entries. Transient entries never touch the stored bytes — they
        /// re-encode flagged or fall back to a flagged Query (see
        /// DecideTransientSend). IsTransientRetry is one-way (never resets
        /// while pending), so a false here stays false for the entry's life.
        /// </summary>
        public static bool CanSendStoredMutationPayload(bool isTransientRetry)
        {
            return !isTransientRetry;
        }

        /// <summary>
        /// Pure timing/attempts decision on primitives (no Unity refs): the exact
        /// table ChestTxService.PumpPending used to inline. Deadline first (a hit
        /// deadline with the last-chance query unspent gets FinalQuery, otherwise
        /// Terminal), then due-check, then the payload/query budget split.
        /// payloadAttempts/queryAttempts are the production constants
        /// (PayloadAttempts/QueryAttempts); tests pin the matrix through this.
        /// </summary>
        public static Step Decide(
            float now, float nextTryAt, float deadline,
            int attempts, bool querySent, bool finalQuerySent,
            int payloadAttempts, int queryAttempts)
        {
            if (now >= deadline)
                return finalQuerySent ? Step.Terminal : Step.FinalQuery;
            if (now < nextTryAt)
                return Step.Wait;
            int budget = payloadAttempts + (querySent ? 0 : queryAttempts);
            if (attempts >= budget)
                return Step.Wait;
            if (!querySent && attempts >= payloadAttempts)
                return Step.Query;
            return Step.Resend;
        }

        /// <summary>
        /// One pump pass over a pending map. decide maps (key, entry) to a Step
        /// (it must be side-effect free apart from reading); onStep performs the
        /// per-step send/counter work for Wait/Resend/Query/FinalQuery (entry
        /// mutation only, never dictionary mutation); onTerminal finalizes
        /// Terminal entries (dictionary removal + single completion attempt) and
        /// runs strictly after the loop. A decide result of Terminal collects
        /// the key; onStep is never invoked for it. Missing keys (completed
        /// between snapshot and visit) are skipped.
        /// </summary>
        public static void Drain<TKey, TEntry>(
            IDictionary<TKey, TEntry> pending,
            Func<TKey, TEntry, Step> decide,
            Action<TKey, TEntry, Step> onStep,
            Action<TKey> onTerminal)
        {
            if (pending == null || pending.Count == 0)
                return;
            if (decide == null || onStep == null || onTerminal == null)
                return;
            List<TKey> keys = new List<TKey>(pending.Count);
            foreach (TKey key in pending.Keys)
                keys.Add(key);
            List<TKey> terminal = null;
            for (int i = 0; i < keys.Count; i++)
            {
                TKey key = keys[i];
                TEntry entry;
                if (!pending.TryGetValue(key, out entry))
                    continue;
                Step step = decide(key, entry);
                if (step == Step.Terminal)
                {
                    if (terminal == null)
                        terminal = new List<TKey>();
                    terminal.Add(key);
                    continue;
                }
                onStep(key, entry, step);
            }
            if (terminal == null)
                return;
            for (int i = 0; i < terminal.Count; i++)
                onTerminal(terminal[i]);
        }
    }
}
