using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Internal manager-side operation (single shape for local and remote calls).
    /// Item snapshots are copies; no live ItemData is held across phases.
    /// </summary>
    internal sealed class TxOpCall
    {
        public TxOp Op;
        public List<TxOpItem> Items = new List<TxOpItem>();
        public int DstX = -1;
        public int DstY = -1;
        public int Mode;
        public bool Desc;
        public int Tier;
        public string Rule;
        /// <summary>
        /// Enforce chest rules on the manager (quick-stack/automation).
        /// Manual GUI moves pass false (vanilla behavior).
        /// </summary>
        public bool EnforceRule;
        /// <summary>
        /// Respect reserves (ChestReserveStore) on Take: pull only from free stacks.
        /// Manual player Takes pass false (explicit action). Automation passes true.
        /// </summary>
        public bool RespectReserves;
        /// <summary>
        /// Same-tx transient retry flag (v3 wire). Set client-side ONLY after
        /// receiving TransientUnavailable for this txId: a flagged mutation
        /// resend re-attempts persistence of the refused record and NEVER
        /// executes. Never set on a first attempt.
        /// </summary>
        public bool IsTransientRetry;
    }

    internal sealed class TxOpItem
    {
        /// <summary>Decoded snapshot (shared already resolved from the prefab).</summary>
        public ItemData Snapshot;
        public int PrefabHash;
        public int Amount;
        public int X = -1;
        public int Y = -1;
        public int MaxStack = 50;
        /// <summary>
        /// Live source on the client (for removal on response). Never serialized;
        /// may go stale between send and response — removal uses a fallback.
        /// </summary>
        public ItemData SourceRef;
    }

    /// <summary>
    /// Cached result for idempotency (full — includes Take items).
    /// Status is the ORIGINAL terminal outcome (Accepted/Partial/Rejected stable
    /// across handoff); IsReplay separates a replay from the original.
    /// Sender is the authenticated committer's CANONICAL peer key (Replay identity check).
    /// </summary>
    internal sealed class StoredResult
    {
        public TxOp Op;
        public TxStatus Status;
        public uint Revision;
        /// <summary>Accepted amounts, per item.</summary>
        public List<int> Accepted = new List<int>();

        public int AcceptedTotal()
        {
            int sum = 0;
            for (int i = 0; i < Accepted.Count; i++)
                sum += Accepted[i];
            return sum;
        }
        /// <summary>Take-result items (clones with the actual stack). Same-manager only.</summary>
        public List<TakeEntry> Takes = new List<TakeEntry>();
        /// <summary>True when the entry was restored without exact payloads (legacy ring or inexact Take metadata).</summary>
        public bool TotalsOnly;
        /// <summary>Authenticated sender peer key recorded at commit (TxIdGen.PeerOf(txId)).</summary>
        public long Sender;
        /// <summary>True when served from cache/ring instead of freshly executed.</summary>
        public bool IsReplay;
        /// <summary>
        /// Both-copies-failed quarantine refusal for a REMOTE job: Drain must skip
        /// completion entirely (no terminal response — the client's Pending pump
        /// retains and resends/queries the SAME txId, never a new one). Set only
        /// by the ApplyJob quarantine recheck when the refusal persisted NOTHING
        /// (ring AND floor writes failed); local jobs never set it (no Pending
        /// entry — they complete UnknownTx). Never cached, never persisted.
        /// </summary>
        public bool SilentDrop;
    }

    internal sealed class TakeEntry
    {
        public int PrefabHash;
        public ItemData Item;
        public int Accepted;
    }

    /// <summary>
    /// Decoded Take result for custom completions (automation).
    /// </summary>
    internal sealed class DecodedTake
    {
        public int PrefabHash;
        public ItemData Item;
        public int Accepted;
    }

    /// <summary>
    /// Mutation queue of one chest. Drained strictly one at a time (main thread).
    /// </summary>
    internal sealed class TxJob
    {
        public Container Container;
        public bool IsLocal;
        public long TxId;
        /// <summary>Raw authenticated RPC sender (routing + ChestAuthority); canonicalized to a peer key at commit.</summary>
        public long Sender;
        public long PlayerId;
        public uint BaseRev;
        /// <summary>Position stamped by the submitter (player pos; animal pos for feeder takes).</summary>
        public UnityEngine.Vector3 ActorPos;
        public TxOpCall Call;
        /// <summary>Remote: answer by RPC. Local: direct callback.</summary>
        public System.Action<StoredResult> Complete;
    }

    /// <summary>
    /// Per-chest state on this peer.
    /// Floor: durable per-sender execution high-water (canonical peer key -&gt; highest tx
    /// counter), persisted with the ring AND in its own floor key, UNBOUNDED (never evicted,
    /// never refusing). An absent txId at or below its sender's high-water is indeterminate
    /// and must never execute. FloorCap is a warn threshold only.
    /// RingCorrupt: persisted ring bytes were untrustworthy. FloorCorrupt: the independent
    /// floor copy is untrustworthy (or missing while the ring is also corrupt).
    /// Mutations are fully fail-closed only when BOTH are untrustworthy; a corrupt ring
    /// with an intact floor runs degraded (old-gen blocked, new-gen above the floor commits
    /// fenced-first) until a successful commit rebuilds the ring.
    /// </summary>
    internal sealed class ChestState
    {
        public Container Container;
        public ZDOID ZdoId;
        public Queue<TxJob> Queue = new Queue<TxJob>();
        public bool Draining;
        public Dictionary<long, StoredResult> Processed = new Dictionary<long, StoredResult>();
        public LinkedList<long> ProcOrder = new LinkedList<long>();
        public Dictionary<long, uint> Floor = new Dictionary<long, uint>();
        public bool RingCorrupt;
        public bool FloorCorrupt;
        public byte[] LastRingBytes;
        public byte[] LastFloorBytes;
        public long LastOwner;
        /// <summary>
        /// Post-fence recovery quarantine: live RAM may hold speculative inventory
        /// from a failed Execute/save whose persistence outcome was ambiguous.
        /// While set, fresh mutations never execute — the refusal is a durable
        /// terminal through the shared matrix (TxDecision.HandoffDropTerminal):
        /// seeded Rejected (known-not-committed — retry as a NEW txId) answered
        /// Rejected ONLY when the record is durable (ring entry AND floor copy),
        /// else Indeterminate with the unpersisted seed evicted. The floor advance
        /// is kept (monotonic) but a RAM-only floor is NOT durable protection;
        /// a same-tx retry re-attempts persistence (transient recovery without a
        /// restart). Both copies failed on a remote tx = SILENT (no terminal
        /// response — the client retains and retries the SAME txId); local txs
        /// have no Pending entry and complete UnknownTx. Replays/queries still
        /// answer. Cleared only by a successful authoritative s_items reload
        /// (pump/takeover/reacquire path), never by elapsed time. The failed tx
        /// keeps its fence (never rolls back).
        /// </summary>
        public bool TxQuarantined;
        // Viewer (we watch a foreign chest):
        public bool ViewedByMe;
        public uint SeenRev;
        public bool SeenOnce;
        public byte[] SeenBytes;
        // Presence (we are the manager): peer -> last hello time.
        public Dictionary<long, float> Viewers = new Dictionary<long, float>();
    }

    /// <summary>
    /// Client request awaiting a response.
    /// </summary>
    internal sealed class PendingTx
    {
        public long TxId;
        public Container Container;
        public ZPackage Payload;
        public TxOp Op;
        public int Attempts;
        public float NextTryAt;
        public float Deadline;
        public bool QuerySent;
        /// <summary>Last-chance query already spent (decode failure / deadline): no more extensions.</summary>
        public bool FinalQuerySent;
        /// <summary>
        /// Transient state (v3): set ONLY after receiving TransientUnavailable for
        /// this txId. While set the entry NEVER goes terminal on timing alone:
        /// PumpPending routes it to DecideTransient (same-tx flagged resend with
        /// bounded backoff) and the deadline is re-armed, never finalized.
        /// Cleared only by a durable (terminal) manager response.
        /// </summary>
        public bool IsTransientRetry;
        /// <summary>Transient resend attempts (bounded-backoff index, never a terminal budget).</summary>
        public int TransientAttempts;
        /// <summary>Original call (for re-encoding flagged transient resends).</summary>
        public TxOpCall Call;
        /// <summary>Item arity the response body must carry (-1 = unknown, legacy behavior).</summary>
        public int ExpectedItems = -1;
        /// <summary>Completion context (closures over live refs — valid only until the deadline).</summary>
        public System.Action<ZPackage, TxStatus, uint, TxCompletionKind> OnResponse;
        /// <summary>Source items claimed by the in-flight deduplicator (Add/AddBatch).</summary>
        public System.Collections.Generic.List<ItemDrop.ItemData> Claimed;
    }
}
