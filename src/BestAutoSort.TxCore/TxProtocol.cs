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
    /// </summary>
    public enum TxStatus
    {
        Accepted = 0,
        Partial = 1,
        Rejected = 2,
        Duplicate = 3,
        UnknownTx = 4
    }

    /// <summary>
    /// Protocol limits.
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
        /// Durable per-sender execution floor: how many distinct sender peers the
        /// high-water map holds. NEVER evicted: reaching the cap refuses new-peer
        /// mutations loudly (fail closed) instead of recreating the eviction bug.
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
    }

    /// <summary>
    /// transactionId generation: upper 32 bits — sender peerId,
    /// lower — monotonic client counter. Retries reuse the same id.
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
    }
}
