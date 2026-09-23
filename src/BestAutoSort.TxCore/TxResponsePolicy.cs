namespace BestAutoSort.TxCore
{
    /// <summary>
    /// How a received response completes its pending request.
    /// Decided status-first (then totals-only): a Rejected/UnknownTx response
    /// must never be reported as applied, and a committed totals-only response
    /// must never fabricate per-item payloads.
    /// </summary>
    public enum TxCompletionKind
    {
        /// <summary>Normal completion: decode the body, credit/remove by accepted.</summary>
        Normal = 0,
        /// <summary>Rejected or wire-UnknownTx: nothing applied (or nothing known).
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
        Indeterminate = 4
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
        /// expectedItems: item arity the body must carry (&lt;=0 = unknown/singleton).
        /// </summary>
        public static TxCompletionKind Classify(TxStatus status, bool totalsOnly, bool isTake, int expectedItems)
        {
            if (status == TxStatus.Rejected || status == TxStatus.UnknownTx)
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
    }
}
