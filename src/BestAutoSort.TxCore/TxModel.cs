using System;
using System.Collections.Generic;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Stackable-item identity for manager-side resolving.
    /// Mirrors vanilla FindFreeStackItem/IsSameType rules:
    /// merge by (hash, quality, world), variant for precision.
    /// Durability/customData are NOT part of identity (stacks merge regardless of them).
    /// </summary>
    public struct ItemKey : IEquatable<ItemKey>
    {
        public int Hash;
        public int Quality;
        public int Variant;
        public int World;

        public ItemKey(int hash, int quality, int variant, int world)
        {
            Hash = hash;
            Quality = quality;
            Variant = variant;
            World = world;
        }

        public bool Equals(ItemKey other)
        {
            return Hash == other.Hash && Quality == other.Quality
                && Variant == other.Variant && World == other.World;
        }

        public override bool Equals(object obj)
        {
            return obj is ItemKey && Equals((ItemKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + Hash;
                h = h * 31 + Quality;
                h = h * 31 + Variant;
                h = h * 31 + World;
                return h;
            }
        }

        public override string ToString()
        {
            return string.Format("key({0},q{1},v{2},w{3})", Hash, Quality, Variant, World);
        }
    }

    /// <summary>
    /// One item in a request. Amount — requested count.
    /// X/Y — desired cell (-1 = anywhere).
    /// </summary>
    public sealed class TxItem
    {
        public ItemKey Key;
        public int Amount;
        public int X = -1;
        public int Y = -1;
        public int MaxStack = 50;

        public TxItem Clone()
        {
            return new TxItem { Key = Key, Amount = Amount, X = X, Y = Y, MaxStack = MaxStack };
        }
    }

    /// <summary>
    /// Mutation request.
    /// Sender: CANONICAL peer key (TxIdGen.PeerKey of the authenticated raw sender; 0 = unknown/legacy, skips checks).
    /// Must equal TxIdGen.PeerOf(TxId) or the request is a spoofed txId. Compare with MatchesPeer, never raw.
    /// </summary>
    public sealed class TxRequest
    {
        public long TxId;
        public TxOp Op;
        public uint BaseRevision;
        public List<TxItem> Items = new List<TxItem>();
        public int DstX = -1;
        public int DstY = -1;
        public int Mode;
        public bool Desc;
        public long Sender;
    }

    /// <summary>
    /// Result. Accepted — accepted amounts per item (for Add*/Take*).
    /// TotalsOnly=true — entry restored without exact payloads (legacy ring or
    /// inexact Take metadata): never fabricate per-item data from it.
    /// Status on a replay is the ORIGINAL terminal outcome (Accepted/Partial/
    /// Rejected stable across handoff); IsReplay separates replay from original.
    /// Wire Duplicate survives only where the original status is genuinely
    /// unavailable (legacy v1 ring entries).
    /// Sender: canonical peer key recorded at commit (TxIdGen.PeerOf of the txId; 0 = legacy unknown).
    /// </summary>
    public sealed class TxResult
    {
        public TxStatus Status;
        public uint Revision;
        public List<int> Accepted = new List<int>();
        public bool TotalsOnly;
        public TxOp Op;
        public long Sender;
        public bool IsReplay;

        public int AcceptedTotal()
        {
            int sum = 0;
            for (int i = 0; i < Accepted.Count; i++)
                sum += Accepted[i];
            return sum;
        }

        public TxResult Clone()
        {
            return new TxResult
            {
                Status = Status,
                Revision = Revision,
                Accepted = new List<int>(Accepted),
                TotalsOnly = TotalsOnly,
                Op = Op,
                Sender = Sender,
                IsReplay = IsReplay
            };
        }
    }

    /// <summary>
    /// One Take payload in a v2 ring entry: the ACTUAL debited item metadata
    /// (production: TakeEntry.Item.Save bytes + prefab hash; offline: encoded
    /// ItemKey). Null entries mean "no payload" (accepted==0 there). A Take
    /// entry whose payloads do not cover every accepted&gt;0 slot is inexact and
    /// restores totals-only — never fabricated.
    /// </summary>
    public sealed class TakePayload
    {
        public int PrefabHash;
        public byte[] Bytes;
    }

    /// <summary>
    /// Persistent ring entry (for manager handoff via ZDO).
    /// v2: Op/Status(original terminal outcome)/Sender/exact Accepted[]/TakePayloads.
    /// v1 legacy: only TxId/AcceptedTotal/Revision; Status==Duplicate marks a
    /// legacy entry (original outcome genuinely unavailable) and restores
    /// totals-only. A Duplicate status NEVER appears on a freshly committed entry.
    /// </summary>
    public struct RingEntry
    {
        public long TxId;
        public int AcceptedTotal;
        public uint Revision;
        public TxOp Op;
        public TxStatus Status;
        public long Sender;
        public List<int> Accepted;
        public List<TakePayload> TakePayloads;
    }
}
