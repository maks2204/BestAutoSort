using System;
using System.Collections.Generic;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Serialized transaction engine of one chest.
    /// Invariant: all mutations of one chest go strictly one at a time through Apply.
    /// Idempotency: a repeated txId returns the cached result without re-applying.
    /// Revision grows only on a really applied mutation.
    /// Thread-safe (lock); in game all calls come from the main thread.
    /// </summary>
    public sealed class TxCore
    {
        private readonly object _gate = new object();
        private readonly LinkedList<long> _order = new LinkedList<long>();
        private readonly Dictionary<long, TxResult> _processed = new Dictionary<long, TxResult>();
        private readonly List<RingEntry> _ring = new List<RingEntry>();

        public uint Revision { get; private set; }

        public int ProcessedCount
        {
            get { lock (_gate) { return _processed.Count; } }
        }

        public int RingCount
        {
            get { lock (_gate) { return _ring.Count; } }
        }

        /// <summary>
        /// Apply a request to the state. Never lets a mutation escape twice:
        /// repeated txId → Duplicate with the cache.
        /// </summary>
        public TxResult Apply(ModelChest chest, TxRequest request)
        {
            if (chest == null)
                throw new ArgumentNullException("chest");
            if (request == null)
                throw new ArgumentNullException("request");
            lock (_gate)
            {
                TxResult cached;
                if (_processed.TryGetValue(request.TxId, out cached))
                {
                    TxResult dup = cached.Clone();
                    dup.Status = TxStatus.Duplicate;
                    return dup;
                }
                TxResult result = Execute(chest, request);
                result.Revision = Revision;
                _processed[request.TxId] = result.Clone();
                _order.AddLast(request.TxId);
                while (_order.Count > TxLimits.ProcessedCacheCap)
                {
                    long oldest = _order.First.Value;
                    _order.RemoveFirst();
                    _processed.Remove(oldest);
                }
                _ring.Clear();
                _ring.AddRange(TxRing.Snapshot(CollectSlots()));
                return result;
            }
        }

        /// <summary>
        /// Projects the idempotency cache (oldest-first) into the shared ring
        /// builder input. Must be called AFTER the current tx is cached, so
        /// the snapshot always includes the just-committed tx.
        /// </summary>
        private List<RingSlot> CollectSlots()
        {
            List<RingSlot> slots = new List<RingSlot>(_order.Count);
            foreach (long txId in _order)
            {
                TxResult r;
                if (!_processed.TryGetValue(txId, out r) || r == null)
                    continue;
                RingSlot s;
                s.TxId = txId;
                s.AcceptedTotal = r.AcceptedTotal();
                s.Revision = r.Revision;
                s.Status = r.Status;
                slots.Add(s);
            }
            return slots;
        }

        /// <summary>
        /// Query a cached result (lost response).
        /// Returns the full result, totals-only from the ring, or UnknownTx.
        /// </summary>
        public TxResult Query(long txId)
        {
            lock (_gate)
            {
                TxResult cached;
                if (_processed.TryGetValue(txId, out cached))
                {
                    TxResult r = cached.Clone();
                    r.Status = TxStatus.Duplicate;
                    return r;
                }
                for (int i = _ring.Count - 1; i >= 0; i--)
                {
                    if (_ring[i].TxId == txId)
                    {
                        return new TxResult
                        {
                            Status = TxStatus.Duplicate,
                            Revision = _ring[i].Revision,
                            Accepted = new List<int> { _ring[i].AcceptedTotal },
                            TotalsOnly = true
                        };
                    }
                }
                return new TxResult { Status = TxStatus.UnknownTx, Revision = Revision };
            }
        }

        /// <summary>
        /// Load the persistent ring after a manager handoff.
        /// Repeating the same txIds returns totals without re-applying:
        /// the ring also seeds the processed cache (totals-only entries).
        /// </summary>
        public void LoadRing(List<RingEntry> entries)
        {
            lock (_gate)
            {
                _ring.Clear();
                if (entries != null)
                {
                    for (int i = 0; i < entries.Count && i < TxLimits.RingCap; i++)
                    {
                        _ring.Add(entries[i]);
                        _processed[entries[i].TxId] = new TxResult
                        {
                            Status = TxStatus.Duplicate,
                            Revision = entries[i].Revision,
                            Accepted = new List<int> { entries[i].AcceptedTotal },
                            TotalsOnly = true
                        };
                        _order.AddLast(entries[i].TxId);
                    }
                    while (_order.Count > TxLimits.ProcessedCacheCap)
                    {
                        long oldest = _order.First.Value;
                        _order.RemoveFirst();
                        _processed.Remove(oldest);
                    }
                    if (_ring.Count > 0)
                        Revision = _ring[_ring.Count - 1].Revision;
                }
            }
        }

        /// <summary>
        /// Ring snapshot for persistence (ZDO).
        /// </summary>
        public List<RingEntry> DumpRing()
        {
            lock (_gate)
            {
                return new List<RingEntry>(_ring);
            }
        }

        /// <summary>
        /// Ring serialization to bytes: [count:byte][txId:long][total:int][rev:uint]...
        /// </summary>
        public static byte[] EncodeRing(List<RingEntry> ring)
        {
            if (ring == null)
                ring = new List<RingEntry>();
            int n = Math.Min(ring.Count, TxLimits.RingCap);
            byte[] buf = new byte[1 + n * TxLimits.RingEntryBytes];
            buf[0] = (byte)n;
            int o = 1;
            for (int i = ring.Count - n; i < ring.Count; i++)
            {
                Array.Copy(BitConverter.GetBytes(ring[i].TxId), 0, buf, o, 8); o += 8;
                Array.Copy(BitConverter.GetBytes(ring[i].AcceptedTotal), 0, buf, o, 4); o += 4;
                Array.Copy(BitConverter.GetBytes(ring[i].Revision), 0, buf, o, 4); o += 4;
            }
            return buf;
        }

        public static List<RingEntry> DecodeRing(byte[] buf)
        {
            List<RingEntry> res = new List<RingEntry>();
            if (buf == null || buf.Length < 1)
                return res;
            int n = Math.Min((int)buf[0], TxLimits.RingCap);
            int o = 1;
            for (int i = 0; i < n; i++)
            {
                if (o + TxLimits.RingEntryBytes > buf.Length)
                    break;
                res.Add(new RingEntry
                {
                    TxId = BitConverter.ToInt64(buf, o),
                    AcceptedTotal = BitConverter.ToInt32(buf, o + 8),
                    Revision = BitConverter.ToUInt32(buf, o + 12)
                });
                o += TxLimits.RingEntryBytes;
            }
            return res;
        }

        private TxResult Execute(ModelChest chest, TxRequest request)
        {
            switch (request.Op)
            {
                case TxOp.Add:
                    return ExecuteAdd(chest, request, false);
                case TxOp.AddBatch:
                    return ExecuteAdd(chest, request, true);
                case TxOp.Take:
                    return ExecuteTake(chest, request, false);
                case TxOp.TakeBatch:
                    return ExecuteTake(chest, request, true);
                case TxOp.Move:
                    return ExecuteMove(chest, request);
                case TxOp.Sort:
                    return ExecuteSort(chest, request);
                default:
                    return new TxResult { Status = TxStatus.Rejected };
            }
        }

        private TxResult ExecuteAdd(ModelChest chest, TxRequest request, bool batch)
        {
            TxResult result = new TxResult();
            int total = 0;
            int full = 0;
            for (int i = 0; i < request.Items.Count; i++)
            {
                if (!batch && i > 0)
                    break;
                TxItem it = request.Items[i];
                int accepted = chest.AddItem(it.Key, it.Amount, it.MaxStack, it.X, it.Y);
                result.Accepted.Add(accepted);
                total += accepted;
                if (accepted == it.Amount)
                    full++;
            }
            if (total > 0)
                Revision++;
            result.Status = full == result.Accepted.Count && result.Accepted.Count > 0
                ? TxStatus.Accepted
                : (total > 0 ? TxStatus.Partial : TxStatus.Rejected);
            return result;
        }

        private TxResult ExecuteTake(ModelChest chest, TxRequest request, bool batch)
        {
            TxResult result = new TxResult();
            int total = 0;
            int full = 0;
            for (int i = 0; i < request.Items.Count; i++)
            {
                if (!batch && i > 0)
                    break;
                TxItem it = request.Items[i];
                int taken = chest.TakeItem(it.Key, it.Amount);
                result.Accepted.Add(taken);
                total += taken;
                if (taken == it.Amount)
                    full++;
            }
            if (total > 0)
                Revision++;
            result.Status = full == result.Accepted.Count && result.Accepted.Count > 0
                ? TxStatus.Accepted
                : (total > 0 ? TxStatus.Partial : TxStatus.Rejected);
            return result;
        }

        private TxResult ExecuteMove(ModelChest chest, TxRequest request)
        {
            TxResult result = new TxResult();
            if (request.Items.Count == 0)
            {
                result.Status = TxStatus.Rejected;
                return result;
            }
            TxItem it = request.Items[0];
            bool ok = chest.Move(it.Key, it.X, it.Y, request.DstX, request.DstY, it.Amount, it.MaxStack);
            if (ok)
                Revision++;
            result.Status = ok ? TxStatus.Accepted : TxStatus.Rejected;
            result.Accepted.Add(ok ? it.Amount : 0);
            return result;
        }

        private TxResult ExecuteSort(ModelChest chest, TxRequest request)
        {
            // Sort only changes positions: conservation is trivial, revision grows.
            // Deterministic order: (hash, quality, variant, world).
            List<int[]> snap = chest.Snapshot();
            snap.Sort(delegate (int[] a, int[] b)
            {
                for (int i = 1; i <= 4; i++)
                {
                    int c = a[i].CompareTo(b[i]);
                    if (c != 0)
                        return request.Desc ? -c : c;
                }
                return 0;
            });
            // Rebuild cells row by row, preserving stacks.
            ModelChest rebuilt = new ModelChest(chest.Width, chest.Height);
            for (int i = 0; i < snap.Count; i++)
            {
                int[] e = snap[i];
                rebuilt.AddItem(new ItemKey(e[1], e[2], e[3], e[4]), e[5], e[6]);
            }
            chest.Restore(rebuilt.Snapshot());
            Revision++;
            TxResult result = new TxResult { Status = TxStatus.Accepted };
            result.Accepted.Add(snap.Count);
            return result;
        }
    }
}
