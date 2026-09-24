using System;
using System.Collections.Generic;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Decoded persistent-ring payload: entries + the durable per-sender
    /// execution floor. Corrupt=true means the persisted bytes were present but
    /// untrustworthy: the loader must fail closed for mutation (never execute),
    /// never silently treat them as an empty usable ring.
    /// </summary>
    public sealed class RingData
    {
        public bool Corrupt;
        public List<RingEntry> Entries = new List<RingEntry>();
        public Dictionary<long, uint> Floor = new Dictionary<long, uint>();
    }

    /// <summary>
    /// Serialized transaction engine of one chest.
    /// Invariant: all mutations of one chest go strictly one at a time through Apply.
    /// Idempotency: a repeated txId returns the cached result without re-applying.
    /// Revision grows only on a really applied mutation.
    /// Thread-safe (lock); in game all calls come from the main thread.
    ///
    /// Stability contract (ring v2):
    /// - Every terminal outcome (Accepted/Partial/Rejected) is cached AND ringed.
    ///   A replay or query returns the ORIGINAL status — the same txId never
    ///   changes its terminal outcome across handoff (Rejected stays Rejected).
    /// - Wire Duplicate is reserved for legacy v1 entries whose original status
    ///   is genuinely unavailable (they restore totals-only).
    /// - The durable per-sender floor (peer -&gt; highest committed counter, capped
    ///   at FloorCap peers, NEVER evicted) makes an absent txId at or below its
    ///   sender's high-water indeterminate (UnknownTx): it must NEVER execute.
    ///   This intentionally includes a delayed first-time out-of-order request.
    /// - Authenticated replay identity is checked before anything else on the
    ///   replay path: a sender/txId mismatch is Rejected without exposing
    ///   another sender's cached payload.
    /// - A corrupt persisted ring fails closed: Apply answers UnknownTx and
    ///   mutates nothing until the state is rebuilt from trustworthy data.
    /// </summary>
    public sealed class TxCore
    {
        private readonly object _gate = new object();
        private readonly LinkedList<long> _order = new LinkedList<long>();
        private readonly Dictionary<long, TxResult> _processed = new Dictionary<long, TxResult>();
        private readonly List<RingEntry> _ring = new List<RingEntry>();
        private readonly Dictionary<long, uint> _floor = new Dictionary<long, uint>();
        private bool _corrupt;

        public uint Revision { get; private set; }

        public int ProcessedCount
        {
            get { lock (_gate) { return _processed.Count; } }
        }

        public int RingCount
        {
            get { lock (_gate) { return _ring.Count; } }
        }

        public int FloorCount
        {
            get { lock (_gate) { return _floor.Count; } }
        }

        public bool RingCorrupt
        {
            get { lock (_gate) { return _corrupt; } }
        }

        /// <summary>
        /// Apply a request to the state. A repeated txId returns the ORIGINAL
        /// cached outcome (IsReplay=true) without re-applying; a stale or
        /// over-capacity request answers UnknownTx without executing.
        /// </summary>
        public TxResult Apply(ModelChest chest, TxRequest request)
        {
            if (chest == null)
                throw new ArgumentNullException("chest");
            if (request == null)
                throw new ArgumentNullException("request");
            lock (_gate)
            {
                if (_corrupt)
                    return UnknownResult();
                TxResult cached;
                if (_processed.TryGetValue(request.TxId, out cached))
                {
                    if (request.Sender != 0 && cached.Sender != 0 && request.Sender != cached.Sender)
                        return SenderMismatch(cached, request.Sender);
                    TxResult dup = cached.Clone();
                    dup.IsReplay = true;
                    return dup;
                }
                long peer = TxIdGen.PeerOf(request.TxId);
                uint ctr = (uint)(request.TxId & 0xFFFFFFFFL);
                if (request.Sender != 0 && request.Sender != peer)
                    return CacheSpoofReject(request, peer, ctr);
                uint hw;
                if (_floor.TryGetValue(peer, out hw) && ctr <= hw)
                {
                    // At or below the sender's high-water but no record: the tx
                    // was evicted, rejected-and-forgotten, or never arrived in
                    // order. Indeterminate — must NEVER execute (a Rejected txId
                    // re-executed here would flip to Accepted).
                    return UnknownResult();
                }
                if (!_floor.ContainsKey(peer) && _floor.Count >= TxLimits.FloorCap)
                {
                    // Floor refuses new peers loudly at capacity instead of
                    // evicting (which would recreate the eviction bug). UnknownTx
                    // is deterministic here: nothing is cached or executed, so a
                    // retry answers the same way forever — fail closed.
                    return UnknownResult();
                }
                TxResult result = Execute(chest, request);
                result.Op = request.Op;
                result.Sender = request.Sender != 0 ? request.Sender : peer;
                result.Revision = Revision;
                _processed[request.TxId] = result.Clone();
                _order.AddLast(request.TxId);
                while (_order.Count > TxLimits.ProcessedCacheCap)
                {
                    long oldest = _order.First.Value;
                    _order.RemoveFirst();
                    _processed.Remove(oldest);
                    _takeBlobs.Remove(oldest);
                }
                AdvanceFloor(peer, ctr);
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
                s.Op = r.Op;
                s.Sender = r.Sender;
                s.Accepted = new List<int>(r.Accepted);
                s.TakePayloads = FindTakeBlobs(txId, r);
                slots.Add(s);
            }
            return slots;
        }

        private readonly Dictionary<long, List<TakePayload>> _takeBlobs = new Dictionary<long, List<TakePayload>>();

        private List<TakePayload> FindTakeBlobs(long txId, TxResult r)
        {
            List<TakePayload> b;
            if (_takeBlobs.TryGetValue(txId, out b))
                return b;
            return null;
        }

        /// <summary>
        /// Query a cached result (lost response).
        /// Returns the original outcome (IsReplay), a totals-only legacy
        /// Duplicate from the ring, or UnknownTx.
        /// </summary>
        public TxResult Query(long txId)
        {
            return Query(txId, 0L);
        }

        public TxResult Query(long txId, long sender)
        {
            lock (_gate)
            {
                TxResult cached;
                if (_processed.TryGetValue(txId, out cached))
                {
                    if (sender != 0 && cached.Sender != 0 && sender != cached.Sender)
                        return SenderMismatch(cached, sender);
                    TxResult r = cached.Clone();
                    r.IsReplay = true;
                    return r;
                }
                for (int i = _ring.Count - 1; i >= 0; i--)
                {
                    if (_ring[i].TxId == txId)
                    {
                        RingEntry e = _ring[i];
                        if (sender != 0 && e.Sender != 0 && sender != e.Sender)
                            return SenderMismatchStatus(e, sender);
                        if (e.Status == TxStatus.Duplicate || !TxRing.IsExactEntry(e))
                        {
                            bool legacy = e.Status == TxStatus.Duplicate;
                            return new TxResult
                            {
                                Status = legacy ? TxStatus.Duplicate : e.Status,
                                Revision = e.Revision,
                                Accepted = legacy ? new List<int> { e.AcceptedTotal } : new List<int>(e.Accepted ?? new List<int> { e.AcceptedTotal }),
                                TotalsOnly = true,
                                Op = e.Op,
                                Sender = e.Sender,
                                IsReplay = true
                            };
                        }
                        return new TxResult
                        {
                            Status = e.Status,
                            Revision = e.Revision,
                            Accepted = new List<int>(e.Accepted),
                            TotalsOnly = false,
                            Op = e.Op,
                            Sender = e.Sender,
                            IsReplay = true
                        };
                    }
                }
                return new TxResult { Status = TxStatus.UnknownTx, Revision = Revision };
            }
        }

        /// <summary>
        /// Load the persistent ring after a manager handoff. TRUE RESET: clears
        /// _processed, _order, _ring and the floor, then restores. v2 entries
        /// restore their original status (exact payloads when available,
        /// totals-only otherwise); legacy v1 entries restore as Duplicate
        /// totals-only. A corrupt payload fails closed (RingCorrupt: Apply
        /// answers UnknownTx and mutates nothing).
        /// </summary>
        public void LoadRing(List<RingEntry> entries)
        {
            RingData data = new RingData();
            if (entries != null)
                data.Entries.AddRange(entries);
            LoadRing(data);
        }

        public void LoadRing(RingData data)
        {
            lock (_gate)
            {
                _processed.Clear();
                _order.Clear();
                _ring.Clear();
                _floor.Clear();
                _takeBlobs.Clear();
                _corrupt = false;
                Revision = 0;
                if (data == null)
                    return;
                if (data.Corrupt)
                {
                    _corrupt = true;
                    return;
                }
                if (data.Floor != null)
                {
                    foreach (KeyValuePair<long, uint> kv in data.Floor)
                    {
                        if (_floor.Count >= TxLimits.FloorCap)
                            break;
                        _floor[kv.Key] = kv.Value;
                    }
                }
                if (data.Entries == null)
                    return;
                int n = Math.Min(data.Entries.Count, TxLimits.RingCap);
                for (int i = 0; i < n; i++)
                {
                    RingEntry e = data.Entries[i];
                    _ring.Add(e);
                    TxResult r;
                    if (e.Status == TxStatus.Duplicate)
                    {
                        r = new TxResult
                        {
                            Status = TxStatus.Duplicate,
                            Revision = e.Revision,
                            Accepted = new List<int> { e.AcceptedTotal },
                            TotalsOnly = true,
                            Op = e.Op,
                            Sender = e.Sender != 0 ? e.Sender : TxIdGen.PeerOf(e.TxId)
                        };
                    }
                    else if (TxRing.IsExactEntry(e))
                    {
                        r = new TxResult
                        {
                            Status = e.Status,
                            Revision = e.Revision,
                            Accepted = new List<int>(e.Accepted ?? new List<int>()),
                            TotalsOnly = false,
                            Op = e.Op,
                            Sender = e.Sender
                        };
                    }
                    else
                    {
                        // Original status preserved, but payloads are gone:
                        // totals-only, never fabricated.
                        r = new TxResult
                        {
                            Status = e.Status,
                            Revision = e.Revision,
                            Accepted = new List<int>(e.Accepted ?? new List<int> { e.AcceptedTotal }),
                            TotalsOnly = true,
                            Op = e.Op,
                            Sender = e.Sender
                        };
                    }
                    _processed[e.TxId] = r;
                    _order.AddLast(e.TxId);
                    if (e.TakePayloads != null)
                        _takeBlobs[e.TxId] = e.TakePayloads;
                }
                while (_order.Count > TxLimits.ProcessedCacheCap)
                {
                    long oldest = _order.First.Value;
                    _order.RemoveFirst();
                    _processed.Remove(oldest);
                    _takeBlobs.Remove(oldest);
                }
                if (_ring.Count > 0)
                    Revision = _ring[_ring.Count - 1].Revision;
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

        public Dictionary<long, uint> DumpFloor()
        {
            lock (_gate)
            {
                return new Dictionary<long, uint>(_floor);
            }
        }

        private TxResult UnknownResult()
        {
            return new TxResult { Status = TxStatus.UnknownTx, Revision = Revision };
        }

        /// <summary>
        /// Authenticated replay identity mismatch: this sender did not commit
        /// this txId. Rejected for THIS sender (nothing committed on its behalf)
        /// with the payload stripped — never another sender's Take bytes.
        /// </summary>
        private TxResult SenderMismatch(TxResult cached, long sender)
        {
            return new TxResult
            {
                Status = TxStatus.Rejected,
                Revision = cached.Revision,
                Accepted = new List<int>(),
                TotalsOnly = false,
                Op = cached.Op,
                Sender = sender,
                IsReplay = true
            };
        }

        /// <summary>
        /// Ring-path twin of SenderMismatch (cache path): same Rejected, no
        /// payload, and the REQUESTER as Sender — mirroring the cache path so
        /// both identities agree on who was refused. Sender is only an
        /// identity tag (never payload), so no Take bytes can leak either way.
        /// </summary>
        private TxResult SenderMismatchStatus(RingEntry e, long sender)
        {
            return new TxResult
            {
                Status = TxStatus.Rejected,
                Revision = e.Revision,
                Accepted = new List<int>(),
                TotalsOnly = false,
                Op = e.Op,
                Sender = sender,
                IsReplay = true
            };
        }

        /// <summary>
        /// Authenticated sender disagrees with the txId peer bits: spoofed txId.
        /// Cached as Rejected under the PEER (so the exact counter stays blocked
        /// with a stable answer) WITHOUT advancing the floor — pushing the true
        /// owner's high-water would amplify the spoof into a wider denial.
        /// </summary>
        private TxResult CacheSpoofReject(TxRequest request, long peer, uint ctr)
        {
            TxResult result = new TxResult
            {
                Status = TxStatus.Rejected,
                Revision = Revision,
                Op = request.Op,
                Sender = peer
            };
            _processed[request.TxId] = result.Clone();
            _order.AddLast(request.TxId);
            while (_order.Count > TxLimits.ProcessedCacheCap)
            {
                long oldest = _order.First.Value;
                _order.RemoveFirst();
                _processed.Remove(oldest);
                _takeBlobs.Remove(oldest);
            }
            _ring.Clear();
            _ring.AddRange(TxRing.Snapshot(CollectSlots()));
            return result;
        }

        private void AdvanceFloor(long peer, uint ctr)
        {
            uint hw;
            if (_floor.TryGetValue(peer, out hw))
            {
                if (ctr > hw)
                    _floor[peer] = ctr;
            }
            else if (_floor.Count < TxLimits.FloorCap)
            {
                _floor[peer] = ctr;
            }
        }

        /// <summary>
        /// Offline Take metadata: the request ItemKey IS the total identity here
        /// (no customData, no durability, no reserve path), so key blobs aligned
        /// with accepted[] are provably sufficient for exact replay. Production
        /// fills the same blob slots with the actual TakeEntry.Item.Save bytes.
        /// </summary>
        public static byte[] EncodeTakeKey(ItemKey key)
        {
            byte[] buf = new byte[16];
            Array.Copy(BitConverter.GetBytes(key.Hash), 0, buf, 0, 4);
            Array.Copy(BitConverter.GetBytes(key.Quality), 0, buf, 4, 4);
            Array.Copy(BitConverter.GetBytes(key.Variant), 0, buf, 8, 4);
            Array.Copy(BitConverter.GetBytes(key.World), 0, buf, 12, 4);
            return buf;
        }

        public static ItemKey DecodeTakeKey(byte[] buf)
        {
            if (buf == null || buf.Length < 16)
                return new ItemKey(0, 0, 0, 0);
            return new ItemKey(
                BitConverter.ToInt32(buf, 0),
                BitConverter.ToInt32(buf, 4),
                BitConverter.ToInt32(buf, 8),
                BitConverter.ToInt32(buf, 12));
        }

        /// <summary>
        /// Ring serialization v1 to bytes: [count:byte][txId:long][total:int][rev:uint]...
        /// Kept for legacy-compat tests; production writes v2 (EncodeRingV2).
        /// Never throws.
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

        /// <summary>
        /// Strict v1 decode: exact length match required, never throws.
        /// Use DecodeEnvelope when the corrupt-vs-empty distinction matters.
        /// </summary>
        public static List<RingEntry> DecodeRing(byte[] buf)
        {
            RingData data = DecodeEnvelope(buf);
            if (data == null || data.Corrupt)
                return new List<RingEntry>();
            return data.Entries;
        }

        /// <summary>
        /// Versioned ring envelope decode (production + tests share this codec):
        /// v2 = [0xFF][0x02][floorCount][peer:long,ctr:uint]...[ringCount][entries]...,
        /// v1 = [count:byte][txId:long,total:int,rev:uint]... (exact length required).
        /// Validates lengths, enums, counts, payload sizes and complete consumption.
        /// Never throws. Null (missing ZDO key) decodes as empty-usable (fresh chest);
        /// present-but-unparseable decodes as Corrupt (fail closed for mutation).
        /// </summary>
        public static RingData DecodeEnvelope(byte[] buf)
        {
            RingData data = new RingData();
            if (buf == null || buf.Length == 0)
                return data;
            try
            {
                if (buf[0] == TxLimits.RingV2Magic)
                    return DecodeV2(buf);
                int n = (int)buf[0];
                if (n < 0 || n > TxLimits.RingCap)
                {
                    data.Corrupt = true;
                    return data;
                }
                if (buf.Length != 1 + n * TxLimits.RingEntryBytes)
                {
                    data.Corrupt = true;
                    return data;
                }
                int o = 1;
                for (int i = 0; i < n; i++)
                {
                    RingEntry e;
                    e.TxId = BitConverter.ToInt64(buf, o);
                    e.AcceptedTotal = BitConverter.ToInt32(buf, o + 8);
                    e.Revision = BitConverter.ToUInt32(buf, o + 12);
                    e.Op = (TxOp)0;
                    e.Status = TxStatus.Duplicate;
                    e.Sender = TxIdGen.PeerOf(e.TxId);
                    e.Accepted = new List<int> { e.AcceptedTotal };
                    e.TakePayloads = null;
                    o += TxLimits.RingEntryBytes;
                    data.Entries.Add(e);
                }
                return data;
            }
            catch (Exception)
            {
                RingData bad = new RingData();
                bad.Corrupt = true;
                return bad;
            }
        }

        /// <summary>
        /// v2 envelope encode shared by production WriteRing and tests.
        /// Legacy-shaped entries (Status==Duplicate) encode with their aggregate;
        /// entries whose sender disagrees with the txId peer bits are SKIPPED
        /// as malformed. Spoof rejects persist by design: CacheSpoofReject caches
        /// them under the PEER (not the spoofer), so they pass this filter, the
        /// ring carries them, and the floor still guards their txIds.
        /// Never throws.
        /// </summary>
        public static byte[] EncodeRingV2(List<RingEntry> ring, Dictionary<long, uint> floor)
        {
            try
            {
                if (ring == null)
                    ring = new List<RingEntry>();
                List<RingEntry> sane = new List<RingEntry>(ring.Count);
                for (int i = 0; i < ring.Count; i++)
                {
                    RingEntry e = ring[i];
                    if (e.Sender != 0 && e.Sender != TxIdGen.PeerOf(e.TxId))
                        continue;
                    if (e.Status == TxStatus.UnknownTx)
                        continue;
                    sane.Add(e);
                }
                while (sane.Count > TxLimits.RingCap)
                    sane.RemoveAt(0);
                List<KeyValuePair<long, uint>> floors = new List<KeyValuePair<long, uint>>();
                if (floor != null)
                {
                    foreach (KeyValuePair<long, uint> kv in floor)
                    {
                        if (floors.Count >= TxLimits.FloorCap)
                            break;
                        floors.Add(kv);
                    }
                }
                List<byte> buf = new List<byte>(128);
                buf.Add(TxLimits.RingV2Magic);
                buf.Add(TxLimits.RingV2Version);
                buf.Add((byte)floors.Count);
                for (int i = 0; i < floors.Count; i++)
                {
                    buf.AddRange(BitConverter.GetBytes(floors[i].Key));
                    buf.AddRange(BitConverter.GetBytes(floors[i].Value));
                }
                buf.Add((byte)sane.Count);
                for (int i = 0; i < sane.Count; i++)
                    AppendEntry(buf, sane[i]);
                return buf.ToArray();
            }
            catch (Exception)
            {
                return new byte[] { TxLimits.RingV2Magic, TxLimits.RingV2Version, 0, 0 };
            }
        }

        private static void AppendEntry(List<byte> buf, RingEntry e)
        {
            List<int> acc = e.Accepted;
            if (acc == null)
                acc = new List<int> { e.AcceptedTotal };
            int accCount = Math.Min(acc.Count, TxLimits.MaxAcceptedPerEntry);
            buf.AddRange(BitConverter.GetBytes(e.TxId));
            buf.AddRange(BitConverter.GetBytes((int)e.Op));
            buf.Add((byte)e.Status);
            buf.AddRange(BitConverter.GetBytes(e.Revision));
            long sender = e.Sender != 0 ? e.Sender : TxIdGen.PeerOf(e.TxId);
            buf.AddRange(BitConverter.GetBytes(sender));
            buf.Add((byte)accCount);
            for (int i = 0; i < accCount; i++)
                buf.AddRange(BitConverter.GetBytes(acc[i]));
            List<TakePayload> takes = e.TakePayloads;
            int takeCount = takes != null ? Math.Min(takes.Count, TxLimits.MaxTakePayloadsPerEntry) : 0;
            buf.Add((byte)takeCount);
            for (int i = 0; i < takeCount; i++)
            {
                TakePayload p = takes[i];
                if (p == null || p.Bytes == null)
                {
                    buf.AddRange(BitConverter.GetBytes(0));
                    buf.AddRange(BitConverter.GetBytes(0));
                    continue;
                }
                int len = Math.Min(p.Bytes.Length, TxLimits.MaxTakePayloadBytes);
                buf.AddRange(BitConverter.GetBytes(p.PrefabHash));
                buf.AddRange(BitConverter.GetBytes(len));
                for (int b = 0; b < len; b++)
                    buf.Add(p.Bytes[b]);
            }
        }

        private static RingData DecodeV2(byte[] buf)
        {
            RingData bad = new RingData();
            bad.Corrupt = true;
            RingData data = new RingData();
            int o = 0;
            if (buf.Length < 4)
                return bad;
            o++; // magic
            byte ver = buf[o++];
            if (ver != TxLimits.RingV2Version)
                return bad;
            int floorCount = (int)buf[o++];
            if (floorCount < 0 || floorCount > TxLimits.FloorCap)
                return bad;
            for (int i = 0; i < floorCount; i++)
            {
                if (o + 12 > buf.Length)
                    return bad;
                long peer = BitConverter.ToInt64(buf, o); o += 8;
                uint ctr = BitConverter.ToUInt32(buf, o); o += 4;
                data.Floor[peer] = ctr;
            }
            if (o + 1 > buf.Length)
                return bad;
            int ringCount = (int)buf[o++];
            if (ringCount < 0 || ringCount > TxLimits.RingCap)
                return bad;
            for (int i = 0; i < ringCount; i++)
            {
                RingEntry e;
                if (!ReadEntry(buf, ref o, out e))
                    return bad;
                data.Entries.Add(e);
            }
            if (o != buf.Length)
                return bad;
            return data;
        }

        private static bool ReadEntry(byte[] buf, ref int o, out RingEntry e)
        {
            e = new RingEntry();
            if (o + 8 + 4 + 1 + 4 + 8 + 1 > buf.Length)
                return false;
            e.TxId = BitConverter.ToInt64(buf, o); o += 8;
            e.Op = (TxOp)BitConverter.ToInt32(buf, o); o += 4;
            byte st = buf[o++];
            if (st > (byte)TxStatus.UnknownTx)
                return false;
            e.Status = (TxStatus)st;
            if (e.Status == TxStatus.UnknownTx)
                return false;
            e.Revision = BitConverter.ToUInt32(buf, o); o += 4;
            e.Sender = BitConverter.ToInt64(buf, o); o += 8;
            if (e.Sender != TxIdGen.PeerOf(e.TxId))
                return false;
            if (e.Status != TxStatus.Duplicate && (int)e.Op == 0)
                return false;
            int accCount = (int)buf[o++];
            if (accCount < 0 || accCount > TxLimits.MaxAcceptedPerEntry)
                return false;
            if (o + accCount * 4 > buf.Length)
                return false;
            e.Accepted = new List<int>(accCount);
            for (int i = 0; i < accCount; i++)
            {
                e.Accepted.Add(BitConverter.ToInt32(buf, o)); o += 4;
            }
            e.AcceptedTotal = 0;
            for (int i = 0; i < e.Accepted.Count; i++)
                e.AcceptedTotal += e.Accepted[i];
            if (o + 1 > buf.Length)
                return false;
            int takeCount = (int)buf[o++];
            if (takeCount < 0 || takeCount > TxLimits.MaxTakePayloadsPerEntry)
                return false;
            e.TakePayloads = takeCount > 0 ? new List<TakePayload>(takeCount) : null;
            for (int i = 0; i < takeCount; i++)
            {
                if (o + 8 > buf.Length)
                    return false;
                int prefab = BitConverter.ToInt32(buf, o); o += 4;
                int len = BitConverter.ToInt32(buf, o); o += 4;
                if (len < 0 || len > TxLimits.MaxTakePayloadBytes)
                    return false;
                if (o + len > buf.Length)
                    return false;
                if (len == 0 && prefab == 0)
                {
                    e.TakePayloads.Add(null);
                }
                else
                {
                    byte[] bytes = new byte[len];
                    Array.Copy(buf, o, bytes, 0, len);
                    o += len;
                    TakePayload p = new TakePayload();
                    p.PrefabHash = prefab;
                    p.Bytes = bytes;
                    e.TakePayloads.Add(p);
                }
            }
            return true;
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
            RecordTakeBlobs(request, result);
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
            RecordTakeBlobs(request, result);
            return result;
        }

        /// <summary>
        /// Remembers the Take metadata blobs for the just-executed tx so the
        /// ring snapshot can carry them (CollectSlots picks them up by txId).
        /// </summary>
        private void RecordTakeBlobs(TxRequest request, TxResult result)
        {
            if (!TxResponsePolicy.IsTakeOp(request.Op))
                return;
            if (result.Accepted.Count != request.Items.Count)
                return;
            List<TakePayload> blobs = new List<TakePayload>(result.Accepted.Count);
            for (int i = 0; i < result.Accepted.Count; i++)
            {
                if (result.Accepted[i] <= 0)
                {
                    blobs.Add(null);
                    continue;
                }
                TakePayload p = new TakePayload();
                p.PrefabHash = request.Items[i].Key.Hash;
                p.Bytes = EncodeTakeKey(request.Items[i].Key);
                blobs.Add(p);
            }
            _takeBlobs[request.TxId] = blobs;
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
