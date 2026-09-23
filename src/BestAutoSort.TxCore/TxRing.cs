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
    }

    /// <summary>
    /// Pure persistent-ring builder shared by the Unity manager
    /// (ChestTxService.CommitResult/WriteRing) and the offline TxCore.
    ///
    /// Rules:
    /// - The just-committed tx must already sit in the offered history BEFORE
    ///   the snapshot is built; otherwise an immediate handoff exposes a ring
    ///   without its idempotency record and the replay re-applies.
    /// - Only committed entries (Accepted/Partial/Duplicate) are encoded.
    ///   Rejected results stay in the RAM cache for immediate duplicate
    ///   handling but must never persist: SeedFromRing restores every ring
    ///   entry as Duplicate (= committed), so a persisted Rejected would come
    ///   back as a false Duplicate implying commitment.
    /// - Oldest-first, capped to RingCap (newest survivors).
    ///
    /// <remarks>
    /// v1 conservation gap (deliberate, ring v2 out of scope): entries carry
    /// only the aggregate accepted total — no per-item counts, no Take item
    /// payloads. A totals-only committed AddBatch cannot be attributed across
    /// items (callers must remove nothing and never cascade), and a totals-only
    /// committed Take cannot credit the client (payload gone). Evicted txs
    /// answer UnknownTx, which is Indeterminate — never a rejection. These
    /// cases need manual reconciliation; see TxResponsePolicy.
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
        /// Oldest-first snapshot of the committed slots, capped to RingCap.
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
                if (!IsCommittedStatus(s.Status))
                    continue;
                RingEntry e;
                e.TxId = s.TxId;
                e.AcceptedTotal = s.AcceptedTotal;
                e.Revision = s.Revision;
                res.Add(e);
            }
            while (res.Count > TxLimits.RingCap)
                res.RemoveAt(0);
            return res;
        }
    }
}
