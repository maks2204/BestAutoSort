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
        /// <summary>True when the entry was restored from the ZDO ring (no items).</summary>
        public bool TotalsOnly;
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
        public long Sender;
        public long PlayerId;
        public uint BaseRev;
        public TxOpCall Call;
        /// <summary>Remote: answer by RPC. Local: direct callback.</summary>
        public System.Action<StoredResult> Complete;
    }

    /// <summary>
    /// Per-chest state on this peer.
    /// </summary>
    internal sealed class ChestState
    {
        public Container Container;
        public ZDOID ZdoId;
        public Queue<TxJob> Queue = new Queue<TxJob>();
        public bool Draining;
        public Dictionary<long, StoredResult> Processed = new Dictionary<long, StoredResult>();
        public LinkedList<long> ProcOrder = new LinkedList<long>();
        public long LastOwner;
        // Viewer (we watch a foreign chest):
        public bool ViewedByMe;
        public uint SeenRev = uint.MaxValue;
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
        /// <summary>Completion context (closures over live refs — valid only until the deadline).</summary>
        public System.Action<ZPackage, TxStatus, uint> OnResponse;
    }
}
