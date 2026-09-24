using System;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Production-shared terminal-decision seam (wave-1 isolation). Pure: no
    /// Unity/Valheim refs, so the offline suite pins the SAME functions the
    /// game calls. Every spoof/quarantine/handoff-drop/drag terminal in
    /// production (TxCore.Apply plus the ChestTxService remote, local, queued
    /// and handoff-drop paths) resolves through THESE functions — tests that
    /// cannot drive Unity end-to-end still exercise the production decision
    /// table, and any production drift breaks the suite.
    ///
    /// Decisions (never widened without a suite update):
    /// - Sender binding: the ONLY sender-vs-txId comparison (canonical keys via
    ///   TxIdGen.MatchesPeer). A non-owner sender is a stranger: the reply is an
    ///   EPHEMERAL Rejected that persists, fences and caches NOTHING
    ///   (no victim floor/ring/cache change), so the victim's txId is never burned.
    /// - Quarantined fresh tx: Indeterminate (UnknownTx), durably FENCED first
    ///   (floor high-water + persisted floor copy, same choke point as every
    ///   fresh mutation) but never cached/executed — the same txId stays
    ///   stale-gated after TryRecover clears the quarantine (retry as a NEW
    ///   txId, never the same one). A fence-write failure stays fail-closed
    ///   Indeterminate without claiming anything further.
    /// - Handoff-drop (queued-but-unapplied at takeover/structural acquire):
    ///   stable Rejected ONLY when the seeded record is durable (ring AND floor
    ///   persisted); any persistence failure stays fail-closed Indeterminate
    ///   WITHOUT claiming a stable terminal (the floor advance is kept, never
    ///   rolls back, so an in-session retry stays stale-gated).
    /// - Drag remainder (alreadyRemoved path): restore ONLY on known-safe
    ///   outcomes (committed-with-counts or known-not-committed). Indeterminate
    ///   or details-unavailable outcomes restore NOTHING — the chest may already
    ///   hold the items, and a blind full restore would duplicate them. Real
    ///   restart-safe escrow is future work (phase 2), never built here.
    /// </summary>
    public static class TxDecision
    {
        /// <summary>
        /// How the authenticated sender relates to the txId peer bits.
        /// Actor-first/raw-sender/canonical-key rules are unchanged: raw RPC
        /// senders (which may be negative) compare by canonical key, never raw.
        /// </summary>
        public enum SenderBinding
        {
            /// <summary>The raw sender owns the txId peer bits (or is the legacy
            /// unauthenticated skew tag only when UnauthenticatedLegacy).</summary>
            BoundOwner = 0,
            /// <summary>Authenticated sender disagrees with the txId peer bits:
            /// spoofed txId. Refuse ephemerally, mutate nothing.</summary>
            UnboundStranger = 1,
            /// <summary>Sender tag 0 (legacy/version-skew resend): not treated as
            /// a spoof — committed-tx replays keep their original outcome through
            /// the normal replay path, strangers still get no payload.</summary>
            UnauthenticatedLegacy = 2
        }

        /// <summary>
        /// The single trust gate for sender/txId binding. Production MUST call
        /// this (never a raw comparison) BEFORE any Processed/ProcOrder/Floor/
        /// Ring mutation for the request.
        /// </summary>
        public static SenderBinding ClassifySenderBinding(long rawSender, long txId)
        {
            if (rawSender == 0L)
                return SenderBinding.UnauthenticatedLegacy;
            return TxIdGen.MatchesPeer(rawSender, txId)
                ? SenderBinding.BoundOwner
                : SenderBinding.UnboundStranger;
        }

        /// <summary>
        /// Spoof terminal: ephemeral Rejected. The caller must persist NOTHING
        /// (no cache seed, no floor advance, no ring write) and fence nothing:
        /// the victim's floor, ring and cache are untouched and the victim's
        /// txId stays usable. Stable in practice because every resend of the
        /// same unowned txId answers the same way without recording anything.
        /// </summary>
        public static TxStatus SpoofTerminal()
        {
            return TxStatus.Rejected;
        }

        /// <summary>
        /// Quarantined fresh-tx terminal: Indeterminate (UnknownTx). Live RAM may
        /// hold speculative inventory from a post-fence failure, so fresh
        /// mutations never execute or cache while quarantined — but the txId IS
        /// durably fenced first (floor high-water + floor persist, same as every
        /// fresh mutation), so the same txId stays stale-gated after
        /// authoritative recovery and the sender must retry as a NEW txId.
        /// Shared by the remote (request), local (MutateLocal/ApplyJob) and
        /// queued (Drain) paths. A fence-write failure answers the same
        /// Indeterminate fail-closed (nothing claimed, nothing cached).
        /// </summary>
        public static TxStatus QuarantinedTerminal()
        {
            return TxStatus.UnknownTx;
        }

        /// <summary>
        /// Replay op-mismatch gate (fail-closed, no payload): a cached txId may be
        /// replayed ONLY for the same op that committed it. A mutation request
        /// whose op differs from the cached op (stale sender grid, counter
        /// collision after a reset, cross-op retry) answers Indeterminate
        /// (UnknownTx) — never the cached payload (a Take replay for an Add
        /// txId would credit uncommitted items) and never re-executed (the txId
        /// is fenced, so executing would double-apply). The cache entry is left
        /// untouched: the original op still replays afterwards. Query lookups
        /// (TxOp.Query) are NOT mutations and are exempt: a Query by txId
        /// returns the original outcome (that IS the lost-response path).
        /// Sender-0 legacy note: the gate applies regardless of sender — a
        /// version-skew resend (sender 0) with a mismatched op is refused the
        /// same way; sender-0 resends with the MATCHING op still replay through
        /// the normal path (see SenderBinding.UnauthenticatedLegacy).
        /// </summary>
        public static bool IsOpMismatch(TxOp cachedOp, TxOp incomingOp)
        {
            if (incomingOp == TxOp.Query)
                return false;
            return cachedOp != incomingOp;
        }

        /// <summary>
        /// Handoff-drop terminal for queued-but-unapplied jobs (nothing applied,
        /// so the sender retries as a NEW tx): stable Rejected ONLY when the
        /// seeded record is durable (ring entry AND floor copy persisted).
        /// Any persistence failure answers Indeterminate — never a stable
        /// Rejected for a non-durable record (a RAM-only Rejected would flip
        /// after a restart/handoff). Production keeps the floor advance (never
        /// rolls back) and evicts the unpersisted seed on this outcome.
        /// </summary>
        public static TxStatus HandoffDropTerminal(bool ringPersisted, bool floorPersisted)
        {
            return (ringPersisted && floorPersisted) ? TxStatus.Rejected : TxStatus.UnknownTx;
        }

        /// <summary>
        /// Drag-remainder gate (alreadyRemoved path): restore ONLY when the
        /// outcome is known-safe — Normal (accepted counts observed: restore the
        /// uncommitted remainder) or FailedNotCommitted (known-not-committed:
        /// restore everything). Indeterminate and committed-but-unattributable
        /// outcomes restore NOTHING (see DragRestoreAmount).
        /// </summary>
        public static bool ShouldRestoreDragRemainder(TxCompletionKind disp)
        {
            return disp == TxCompletionKind.Normal
                || disp == TxCompletionKind.FailedNotCommitted;
        }

        /// <summary>
        /// Drag-remainder arithmetic (production helper behind RestoreDragRemainder):
        /// FailedNotCommitted restores the full dragged stack (nothing committed);
        /// Normal restores stack-minus-accepted (never negative); every other
        /// disposition restores 0 — the chest may already hold the items, so a
        /// blind restore would duplicate them. A 0 result means "withhold and
        /// reconcile manually", never "drop".
        /// </summary>
        public static int DragRestoreAmount(int dragStack, int accepted, TxCompletionKind disp)
        {
            if (dragStack <= 0)
                return 0;
            if (disp == TxCompletionKind.FailedNotCommitted)
                return dragStack;
            if (disp == TxCompletionKind.Normal)
            {
                int remainder = dragStack - accepted;
                return remainder > 0 ? remainder : 0;
            }
            return 0;
        }
    }
}
