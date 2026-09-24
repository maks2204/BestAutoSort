namespace BestAutoSort.TxCore
{
    /// <summary>
    /// ChestTX operation codes. Serialized as int in RPC packets.
    /// </summary>
    public enum TxOp
    {
        Add = 1,
        AddBatch = 2,
        Take = 3,
        TakeBatch = 4,
        Move = 5,
        Sort = 6,
        Upgrade = 7,
        SetRule = 8,
        /// <summary>Viewer presence (opened/closed the GUI). No mutation.</summary>
        ViewerOpen = 100,
        ViewerClose = 101,
        /// <summary>Cached-result query by txId (lost response).</summary>
        Query = 102
    }

    /// <summary>
    /// Transaction processing status.
    /// TransientUnavailable (5) is a NON-terminal RAM-only refusal signal:
    /// the manager holds the txId in its transient-refusal map (both durable
    /// copies failed) and the client must retain Pending + claims and retry
    /// the SAME txId (flagged), never a new one. It is never cached, never
    /// persisted to the ring (ring readers reject it as corrupt), never
    /// committed, and never terminal (see TxResponsePolicy.Classify and
    /// TxRing.IsTerminalStatus). Wire skew: a peer that does not know this
    /// value must treat it as Indeterminate (never execute, never forget
    /// silently) — production decodes it status-first through the same seam.
    /// </summary>
    public enum TxStatus
    {
        Accepted = 0,
        Partial = 1,
        Rejected = 2,
        Duplicate = 3,
        UnknownTx = 4,
        TransientUnavailable = 5
    }

    /// <summary>
    /// Ring vs floor roles (durable ZDO copies, two SEPARATE keys, writes NOT
    /// atomic across keys — torn generations are routine, never corruption):
    /// - Ring: immutable committed OUTCOME records (txId, status, accepted totals,
    ///   revision, sender key; v3). Answers replays/Queries with the ORIGINAL
    ///   outcome so a txId never flips across handoff. Cap 32, oldest evicted.
    /// - Floor: per-sender execution HIGH-WATER (peer key -> highest counter seen).
    ///   UNBOUNDED, never evicted, never refusing. An absent txId at/below its
    ///   sender's high-water is Indeterminate (evicted, forgotten, or out-of-order)
    ///   and must NEVER execute. Max-merged across copies (either crash order safe).
    /// A present-but-corrupt floor copy is validated-then-ignored in favor of the
    /// ring-embedded copy; a corrupt ring with no trustworthy floor fails fully closed.
    /// </summary>
    public static class TxLimits
    {
        /// <summary>How many full results the in-memory idempotency cache holds.</summary>
        public const int ProcessedCacheCap = 128;
        /// <summary>How many entries the persistent ring holds (for manager handoff).</summary>
        public const int RingCap = 32;
        /// <summary>One v1 ring entry size in bytes: txId(8) + acceptedTotal(4) + revision(4).</summary>
        public const int RingEntryBytes = 16;
        /// <summary>
        /// Durable per-sender execution floor: ordinary worlds hold this many distinct sender peers. The map itself
        /// is UNBOUNDED (never evicted, never refusing): past this threshold the manager only logs a warning.
        /// </summary>
        public const int FloorCap = 64;
        /// <summary>Max per-item accepted counts carried in one v2 ring entry (matches the request item cap).</summary>
        public const int MaxAcceptedPerEntry = 64;
        /// <summary>Max Take payloads carried in one v2 ring entry.</summary>
        public const int MaxTakePayloadsPerEntry = 64;
        /// <summary>Max bytes of a single Take payload (anti-grief bound; real Item.Save blobs are ~10^2 B).</summary>
        public const int MaxTakePayloadBytes = 4096;
        /// <summary>v2 envelope sentinel: first byte 0xFF (a v1 count byte never exceeds RingCap).</summary>
        public const byte RingV2Magic = 0xFF;
        /// <summary>Persistent-ring format version carried after the magic byte.</summary>
        public const byte RingV2Version = 2;
        /// <summary>v3 ring: same entries as v2, but sender is an unambiguous uint32 sender KEY (canonical peer key).</summary>
        public const byte RingV3Version = 3;
        /// <summary>Independent floor-payload sentinel (separate ZDO key).</summary>
        public const byte FloorMagic = 0xFE;
        /// <summary>Independent floor-payload version.</summary>
        public const byte FloorVersion = 1;
    }

    /// <summary>
    /// transactionId generation: upper 32 bits = canonical sender peer KEY, lower = monotonic client counter.
    /// Raw RPC senders and persisted identity fields are canonicalized with PeerKey before any comparison.
    /// UIDs are opaque per-session values (ephemeral per process, may be negative, NOT SteamIDs):
    /// the floor keys off the txId-embedded peer key, so retries carrying an old UID stay correct.
    /// </summary>
    public static class TxIdGen
    {
        public static long Next(long peerId, ref uint counter)
        {
            unchecked
            {
                uint c = counter++;
                return ((peerId & 0xFFFFFFFFL) << 32) | c;
            }
        }

        public static long PeerOf(long txId)
        {
            return (txId >> 32) & 0xFFFFFFFFL;
        }

        /// <summary>Canonical peer key of a raw signed UID (idempotent for values already in 0..2^32-1).</summary>
        public static long PeerKey(long rawUid)
        {
            return rawUid & 0xFFFFFFFFL;
        }

        /// <summary>Counter half of a txId.</summary>
        public static uint CounterOf(long txId)
        {
            return (uint)(txId & 0xFFFFFFFFL);
        }

        /// <summary>The ONLY correct sender-vs-txId comparison: true when the raw RPC sender owns the txId peer bits.</summary>
        public static bool MatchesPeer(long rawSender, long txId)
        {
            return PeerKey(rawSender) == PeerOf(txId);
        }

        /// <summary>Checked generation: false (txId 0) when the counter is exhausted, so it can never wrap silently.</summary>
        public static bool TryNext(long peerId, ref uint counter, out long txId)
        {
            if (counter == 0 || counter == uint.MaxValue)
            {
                txId = 0L;
                return false;
            }
            txId = Next(peerId, ref counter);
            return true;
        }
    }
}
