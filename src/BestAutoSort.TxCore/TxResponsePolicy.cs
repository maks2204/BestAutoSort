namespace BestAutoSort.TxCore
{
    /// <summary>
    /// How a received response completes its pending request.
    /// Decided status-first (then totals-only): a Rejected response must never
    /// be reported as applied, an UnknownTx response is indeterminate (never
    /// grouped with Rejected), and a committed totals-only response must never
    /// fabricate per-item payloads.
    /// </summary>
    public enum TxCompletionKind
    {
        /// <summary>Normal completion: decode the body, credit/remove by accepted.</summary>
        Normal = 0,
        /// <summary>Rejected: known-not-committed.
        /// Complete terminally with no accepted items; credit/remove nothing.</summary>
        FailedNotCommitted = 1,
        /// <summary>Committed Take recovered totals-only (ring replay after handoff):
        /// the chest was debited but the payload is gone. Complete with an empty
        /// Take result; credit nothing, fabricate nothing.</summary>
        CommittedTakeDetailsUnavailable = 2,
        /// <summary>Committed multi-add recovered totals-only: the aggregate total
        /// cannot be attributed across items. Remove nothing from sources and
        /// never cascade/retry (a retry would duplicate what is already stored).</summary>
        CommittedMultiAddDetailsUnavailable = 3,
        /// <summary>Local timeout exhaustion with no usable response: applied-or-not
        /// is genuinely indeterminate. Loud terminal completion, no auto-retry.</summary>
        Indeterminate = 4,
        /// <summary>Transient refusal: the manager holds the txId (both durable
        /// copies failed) and explicitly asks for a SAME-tx retry. NON-terminal:
        /// keep Pending + claims, schedule a flagged same-tx resend/Query with
        /// bounded backoff; the deadline never finalizes while transient
        /// (see TxPendingDrain.DecideTransient). Never cascades, never restores
        /// drag remainder, never credits/removes (nothing committed).</summary>
        TransientRetrySameTx = 5
    }

    /// <summary>
    /// Which authority path gates a remote request.
    /// Actor-first: a resolvable real player always takes the player path, even
    /// when the sender is also the server peer (listen-server host).
    /// Only a genuinely actor-less server sender may use server automation.
    /// </summary>
    public enum TxActorPath
    {
        /// <summary>Sender resolved to a replicated player: bind claim to it.</summary>
        Player = 0,
        /// <summary>No resolvable actor, authenticated server sender:
        /// headless/dedicated automation bound to the chest creator.</summary>
        ServerAutomation = 1,
        /// <summary>No actor and not a server sender: reject (no-actor).</summary>
        RejectNoActor = 2
    }

    /// <summary>
    /// Pure issue-#10 policy helpers (no Unity/Valheim refs): actor classification
    /// is actor-first, response classification is status-first. Used by the
    /// ChestTX manager/client and covered by ChestTx.Tests.
    /// </summary>
    public static class TxResponsePolicy
    {
        public static bool IsTakeOp(TxOp op)
        {
            return op == TxOp.Take || op == TxOp.TakeBatch;
        }

        public static bool IsCommitted(TxStatus status)
        {
            return status == TxStatus.Accepted || status == TxStatus.Partial || status == TxStatus.Duplicate;
        }

        /// <summary>
        /// Actor-first classification: a resolved actor always wins over the
        /// server-sender check, so a listen-server host presenting a real player
        /// body is gated as that player, not as headless automation.
        /// </summary>
        public static TxActorPath ClassifyActorPath(bool actorResolved, bool senderIsServerOrSelf)
        {
            if (actorResolved)
                return TxActorPath.Player;
            if (senderIsServerOrSelf)
                return TxActorPath.ServerAutomation;
            return TxActorPath.RejectNoActor;
        }

        /// <summary>
        /// Status-first response classification.
        /// Wire Rejected means known-not-committed. Wire UnknownTx (evicted or
        /// lost record) says NOTHING about commitment, so it is Indeterminate.
        /// Wire TransientUnavailable (both durable copies failed on the manager)
        /// is NON-terminal: TransientRetrySameTx — retain and retry the SAME txId.
        /// expectedItems: item arity the body must carry (&lt;=0 = unknown/singleton).
        /// </summary>
        public static TxCompletionKind Classify(TxStatus status, bool totalsOnly, bool isTake, int expectedItems)
        {
            if (status == TxStatus.TransientUnavailable)
                return TxCompletionKind.TransientRetrySameTx;
            if (status == TxStatus.UnknownTx)
                return TxCompletionKind.Indeterminate;
            if (status == TxStatus.Rejected)
                return TxCompletionKind.FailedNotCommitted;
            if (!totalsOnly)
                return TxCompletionKind.Normal;
            if (isTake)
                return TxCompletionKind.CommittedTakeDetailsUnavailable;
            if (expectedItems > 1)
                return TxCompletionKind.CommittedMultiAddDetailsUnavailable;
            return TxCompletionKind.Normal;
        }

        public static TxCompletionKind Classify(TxStatus status, bool totalsOnly, TxOp op, int expectedItems)
        {
            return Classify(status, totalsOnly, IsTakeOp(op), expectedItems);
        }

        /// <summary>
        /// Cascade/continuation gate shared by QuickStack and the Shared
        /// Resources return chain. Indeterminate (unknown commitment) and
        /// committed-but-unattributable totals-only outcomes are terminal:
        /// continuing would duplicate what is already stored or fabricate
        /// what was never observed. A known Rejected (FailedNotCommitted)
        /// MAY try the next chest: nothing was committed and the source
        /// items are untouched (removal happens only by accepted counts).
        /// </summary>
        public static bool ShouldCascadeToNextChest(TxCompletionKind disp)
        {
            return disp == TxCompletionKind.Normal
                || disp == TxCompletionKind.FailedNotCommitted;
        }

        /// <summary>
        /// No-progress probe for the quick-stack cascade-spin fix: true when at
        /// least one sent item shows accepted &gt; 0. A short or empty accepted list
        /// (chunk fanout, empty-body all-pruned skips) reads missing slots as 0,
        /// matching the cascade remainder math (got = 0 =&gt; full remainder).
        /// Pure (no Unity refs), pinned by ChestTx.Tests.
        /// </summary>
        public static bool CascadeMadeProgress(System.Collections.Generic.IList<int> accepted, int sentCount)
        {
            if (accepted == null || sentCount <= 0)
                return false;
            int n = accepted.Count < sentCount ? accepted.Count : sentCount;
            for (int i = 0; i < n; i++)
            {
                if (accepted[i] > 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Cascade-spin stop gate (v0.5.x): stop the chain only when the completion
        /// moved nothing (accepted == 0 for every sent item) AND the onward submit
        /// would send nothing new (every remainder item already in flight under a
        /// live sibling tx: submitting would prune to zero and re-cascade once per
        /// chest, since the all-pruned skip answers Normal + zero accepted).
        /// Chest-full with live (unclaimed) items keeps cascading: the next chest
        /// may still take them. Partial/full accept never stops here (progress).
        /// No item loss: the stopped chain submitted and removed nothing (removal
        /// runs only by accepted counts); the sibling tx holding the claims owns
        /// the items and continues through its own remainder path.
        /// </summary>
        public static bool ShouldStopNoProgressCascade(System.Collections.Generic.IList<int> accepted, int sentCount, bool allRemainderInFlight)
        {
            return allRemainderInFlight && !CascadeMadeProgress(accepted, sentCount);
        }
    }
}
