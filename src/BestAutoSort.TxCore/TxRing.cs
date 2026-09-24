using System.Collections.Generic;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// One idempotency-cache slot offered to the persistent-ring snapshot.
    /// Production (StoredResult) and tests (TxResult) both project their cache
    /// into this shape so the ring is built by ONE shared helper, never by two
    /// parallel implementations that can drift.
    /// </summary>
    public struct RingSlot
    {
        public long TxId;
        public int AcceptedTotal;
        public uint Revision;
        public TxStatus Status;
        public TxOp Op;
        public long Sender;
        public List<int> Accepted;
        public List<TakePayload> TakePayloads;
    }

    /// <summary>
    /// Pure persistent-ring builder shared by the Unity manager
    /// (ChestTxService.CommitResult/WriteRing) and the offline TxCore.
    ///
    /// Rules:
    /// - The just-committed tx must already sit in the offered history BEFORE
    ///   the snapshot is built; otherwise an immediate handoff exposes a ring
    ///   without its idempotency record and the replay re-applies.
    /// - EVERY terminal outcome persists (Accepted/Partial/Rejected/Duplicate):
    ///   a Rejected txId that is forgotten can later be executed as Accepted —
    ///   the same txId must never change its terminal outcome across handoff.
    ///   UnknownTx is never cached and never persists.
    /// - Replays restore the ORIGINAL status (Accepted/Partial/Rejected stable);
    ///   wire Duplicate is reserved for legacy v1 entries whose original status
    ///   is genuinely unavailable (they restore totals-only).
    /// - Oldest-first, capped to RingCap (newest survivors).
    ///
    /// <remarks>
    /// v1 conservation gap (legacy): v1 entries carry only the aggregate
    /// accepted total — no per-item counts, no Take item payloads. A totals-only
    /// committed AddBatch cannot be attributed across items (callers must remove
    /// nothing and never cascade), and a totals-only committed Take cannot credit
    /// the client (payload gone). v2 entries carry the exact accepted[] and, for
    /// Take ops, the actual debited-item payloads; inexact Take entries restore
    /// totals-only instead of fabricating. Evicted txs answer UnknownTx, which is
    /// Indeterminate — never a rejection. These cases need manual reconciliation;
    /// see TxResponsePolicy.
    ///
    /// Take exactness note (offline): request snapshot + accepted[] IS sufficient
    /// here because ItemKey (hash+quality+variant+world) is the TOTAL item
    /// identity in the model — there is no customData, no durability, no reserve
    /// path drawing from a different live stack. Production Take additionally
    /// persists the actual TakeEntry.Item.Save bytes, because matching does NOT
    /// establish equality of custom data or durability there.
    /// </remarks>
    /// </summary>
    public static class TxRing
    {
        public static bool IsCommittedStatus(TxStatus status)
        {
            return status == TxStatus.Accepted
                || status == TxStatus.Partial
                || status == TxStatus.Duplicate;
        }

        /// <summary>
        /// Every terminal outcome that may be cached and persisted. UnknownTx is
        /// never a cached outcome and must never enter the ring.
        /// </summary>
        public static bool IsTerminalStatus(TxStatus status)
        {
            return status == TxStatus.Accepted
                || status == TxStatus.Partial
                || status == TxStatus.Rejected
                || status == TxStatus.Duplicate;
        }

        /// <summary>
        /// A v2 Take entry is exact only when payloads align 1:1 with accepted[]
        /// and every accepted&gt;0 slot carries its actual debited-item payload.
        /// Anything else restores totals-only (credit nothing, fabricate nothing).
        /// Non-Take entries are exact by construction (accepted[] are plain ints).
        /// Legacy entries (Status==Duplicate) are never exact.
        /// </summary>
        public static bool IsExactEntry(RingEntry e)
        {
            if (e.Status == TxStatus.Duplicate)
                return false;
            if (e.Status == TxStatus.Rejected)
                return true;
            if (e.Op != TxOp.Take && e.Op != TxOp.TakeBatch)
                return true;
            if (e.Accepted == null || e.TakePayloads == null)
                return false;
            if (e.TakePayloads.Count != e.Accepted.Count)
                return false;
            for (int i = 0; i < e.Accepted.Count; i++)
            {
                TakePayload p = e.TakePayloads[i];
                if (e.Accepted[i] > 0 && (p == null || p.Bytes == null))
                    return false;
                if (e.Accepted[i] <= 0 && p != null && p.Bytes != null && p.Bytes.Length > 0)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Oldest-first snapshot of the terminal slots, capped to RingCap.
        /// historyOldestFirst must already include the just-committed tx.
        /// </summary>
        public static List<RingEntry> Snapshot(IList<RingSlot> historyOldestFirst)
        {
            List<RingEntry> res = new List<RingEntry>();
            if (historyOldestFirst == null)
                return res;
            for (int i = 0; i < historyOldestFirst.Count; i++)
            {
                RingSlot s = historyOldestFirst[i];
                if (!IsTerminalStatus(s.Status))
                    continue;
                RingEntry e;
                e.TxId = s.TxId;
                e.AcceptedTotal = s.AcceptedTotal;
                e.Revision = s.Revision;
                e.Op = s.Op;
                e.Status = s.Status;
                e.Sender = s.Sender;
                e.Accepted = s.Accepted != null ? new List<int>(s.Accepted) : null;
                e.TakePayloads = s.TakePayloads;
                res.Add(e);
            }
            while (res.Count > TxLimits.RingCap)
                res.RemoveAt(0);
            return res;
        }
    }
}
