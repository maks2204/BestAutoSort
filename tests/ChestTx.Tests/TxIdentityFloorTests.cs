using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Canonical peer identity + ring v3 + floor lifecycle + corrupt recovery.
    ///
    /// Root cause under test (P0): TxIdGen.Next stores the UID's UNSIGNED low 32
    /// bits, but the old code compared them against the raw SIGNED RPC sender —
    /// every legitimate negative UID failed the comparison. All identity checks
    /// now go through TxIdGen.PeerKey/MatchesPeer (canonical keys); ChestAuthority
    /// and RPC routing intentionally keep the raw UID.
    ///
    /// All handoffs go through the PRODUCTION-SHARED codecs
    /// (TxCore.EncodeRingV2/EncodeRingV3/EncodeFloor + TxCore.DecodeEnvelope/
    /// DecodeFloor) — never a test-only serializer.
    ///
    /// Real issue-log peer IDs used throughout: 489397592, 566772066,
    /// -1214308360, -311536441.
    /// </summary>
    internal static class TxIdentityFloorTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);
        private static readonly ItemKey Stone = new ItemKey(1002, 1, 0, 0);

        private const long PosA = 489397592;
        private const long PosB = 566772066;
        private const long NegA = -1214308360;
        private const long NegB = -311536441;

        private static long Tx(long peer, uint ctr)
        {
            return unchecked((peer << 32) | ctr);
        }

        private static TxItem Lot(ItemKey key, int amount, int maxStack)
        {
            return new TxItem { Key = key, Amount = amount, MaxStack = maxStack };
        }

        private static TxCore HandoffV3(TxCore src)
        {
            byte[] bytes = TxCore.EncodeRingV3(src.DumpRing(), src.DumpFloor());
            Check.That(bytes != null, "v3 encode of valid state must not fail");
            RingData data = TxCore.DecodeEnvelope(bytes);
            Check.That(!data.Corrupt, "shared-codec v3 round-trip must not be corrupt");
            TxCore dst = new TxCore();
            dst.LoadRing(data);
            return dst;
        }

        private static ModelChest CloneChest(ModelChest src)
        {
            ModelChest c = new ModelChest(src.Width, src.Height);
            c.Restore(src.Snapshot());
            return c;
        }

        public static void RunAll()
        {
            Console.WriteLine("ID_CanonicalKeyBasics");
            ID_CanonicalKeyBasics();
            Console.WriteLine("ID_NegativeCommitExactReplay");
            ID_NegativeCommitExactReplay();
            Console.WriteLine("ID_NegativeTakeHandoffConservation");
            ID_NegativeTakeHandoffConservation();
            Console.WriteLine("ID_NegativeRejectPersists");
            ID_NegativeRejectPersists();
            Console.WriteLine("ID_SpoofStillRejected");
            ID_SpoofStillRejected();
            Console.WriteLine("ID_SenderMismatchNoLeakNegative");
            ID_SenderMismatchNoLeakNegative();
            Console.WriteLine("ID_RealIssuePeersMatrix");
            ID_RealIssuePeersMatrix();
            Console.WriteLine("ID_V1CompatNegative");
            ID_V1CompatNegative();
            Console.WriteLine("ID_V2LegacySignedSenderTolerated");
            ID_V2LegacySignedSenderTolerated();
            Console.WriteLine("ID_V3RoundTripAndMalformed");
            ID_V3RoundTripAndMalformed();
            Console.WriteLine("ID_FloorCodec");
            ID_FloorCodec();
            Console.WriteLine("ID_CounterResetOldGenBlockedNewGenRuns");
            ID_CounterResetOldGenBlockedNewGenRuns();
            Console.WriteLine("ID_CounterExhaustionFailClosed");
            ID_CounterExhaustionFailClosed();
            Console.WriteLine("ID_ManyPeersUnbounded");
            ID_ManyPeersUnbounded();
            Console.WriteLine("ID_V3LargeFloorRoundTrip");
            ID_V3LargeFloorRoundTrip();
            Console.WriteLine("ID_CorruptRecovery");
            ID_CorruptRecovery();
            Console.WriteLine("ID_SplitFloorMerge");
            ID_SplitFloorMerge();
            Console.WriteLine("ID_TornReadSafe");
            ID_TornReadSafe();
            Console.WriteLine("ID_ActorFirstIntact");
            ID_ActorFirstIntact();
        }

        private static void ID_CanonicalKeyBasics()
        {
            Check.Equal(3080658936L, TxIdGen.PeerKey(NegA), "PeerKey(-1214308360)");
            Check.Equal(3983430855L, TxIdGen.PeerKey(NegB), "PeerKey(-311536441)");
            Check.Equal(PosA, TxIdGen.PeerKey(PosA), "PeerKey idempotent for positive UIDs");
            uint c = 7;
            long txId = TxIdGen.Next(NegA, ref c);
            Check.That(TxIdGen.PeerOf(txId) == TxIdGen.PeerKey(NegA), "PeerOf matches PeerKey for negative UID");
            Check.That(TxIdGen.MatchesPeer(NegA, txId), "raw negative sender matches its own txId");
            Check.That(!TxIdGen.MatchesPeer(NegB, txId), "different negative sender does not match");
            Check.That(!TxIdGen.MatchesPeer(PosA, txId), "positive stranger does not match negative txId");
            Check.Equal(7u, TxIdGen.CounterOf(txId), "counter half");
        }

        /// <summary>Legit negative sender commits AddBatch and replays exactly (no carve-out).</summary>
        private static void ID_NegativeCommitExactReplay()
        {
            ModelChest chest = new ModelChest(6, 4);
            TxCore core = new TxCore();
            uint c = 1;
            TxRequest add = new TxRequest { TxId = TxIdGen.Next(NegA, ref c), Op = TxOp.AddBatch, Sender = NegA };
            add.Items.Add(Lot(Wood, 10, 50));
            add.Items.Add(Lot(Stone, 20, 50));
            TxResult r1 = core.Apply(chest, add);
            Check.That(r1.Status == TxStatus.Accepted, "negative sender commits, got " + r1.Status);
            Check.Equal(TxIdGen.PeerKey(NegA), r1.Sender, "stored sender is the canonical key");
            TxResult r2 = core.Apply(chest, add);
            Check.That(r2.IsReplay && r2.Status == TxStatus.Accepted && !r2.TotalsOnly, "exact replay");
            Check.Equal(10, r2.Accepted[0], "replay accepted[0]");
            Check.Equal(20, r2.Accepted[1], "replay accepted[1]");
            Check.Equal(30, chest.GrandTotal(), "applied exactly once");
        }

        /// <summary>Negative-sender Take survives a v3 handoff with exact metadata; chest+client conserve.</summary>
        private static void ID_NegativeTakeHandoffConservation()
        {
            ModelChest chestA = new ModelChest(6, 4);
            chestA.AddItem(Wood, 20, 50);
            TxCore coreA = new TxCore();
            uint c = 1;
            TxRequest take = new TxRequest { TxId = TxIdGen.Next(NegA, ref c), Op = TxOp.Take, Sender = NegA };
            take.Items.Add(Lot(Wood, 6, 50));
            TxResult r1 = coreA.Apply(chestA, take);
            Check.That(r1.Status == TxStatus.Accepted, "negative take commits");

            TxCore coreB = HandoffV3(coreA);
            ModelChest chestB = CloneChest(chestA);
            TxResult replay = coreB.Apply(chestB, take);
            Check.That(replay.Status == TxStatus.Accepted && replay.IsReplay && !replay.TotalsOnly,
                "post-handoff exact replay, got " + replay.Status);
            Check.Equal(6, replay.AcceptedTotal(), "replay keeps body");
            Check.Equal(14, chestB.TotalOf(Wood), "no double-debit");
            // Client removes exactly the replayed counts: conservation.
            ModelChest bag = new ModelChest(6, 4);
            bag.AddItem(Wood, 6, 50);
            int removed = bag.TakeItem(Wood, replay.Accepted[0]);
            Check.Equal(6, removed, "client removes exactly credited");
            Check.Equal(0, bag.GrandTotal(), "bag empty");
            Check.Equal(20, 14 + 6, "chest + bag conserve the original stock");
        }

        /// <summary>Negative-sender Rejected persists across a v3 handoff and never flips.</summary>
        private static void ID_NegativeRejectPersists()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 5, 50);
            TxCore coreA = new TxCore();
            uint c = 1;
            TxRequest take = new TxRequest { TxId = TxIdGen.Next(NegB, ref c), Op = TxOp.Take, Sender = NegB };
            take.Items.Add(Lot(Stone, 1, 50));
            Check.That(coreA.Apply(chestA, take).Status == TxStatus.Rejected, "reject commits");

            TxCore coreB = HandoffV3(coreA);
            ModelChest chestB = CloneChest(chestA);
            chestB.AddItem(Stone, 10, 50);
            TxResult replay = coreB.Apply(chestB, take);
            Check.That(replay.Status == TxStatus.Rejected, "rejected never flips, got " + replay.Status);
            Check.Equal(10, chestB.TotalOf(Stone), "stock untouched");
        }

        /// <summary>True spoofs still fail with negative IDs in play — no negative carve-out.</summary>
        private static void ID_SpoofStillRejected()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            uint c = 1;
            // txId peer bits say NegA, authenticated sender is NegB.
            TxRequest spoof = new TxRequest { TxId = TxIdGen.Next(NegA, ref c), Op = TxOp.Take, Sender = NegB };
            spoof.Items.Add(Lot(Wood, 6, 50));
            TxResult r = core.Apply(chest, spoof);
            Check.That(r.Status == TxStatus.Rejected, "neg-to-neg spoof rejected, got " + r.Status);
            Check.Equal(20, chest.TotalOf(Wood), "spoof executes nothing");
            // Positive sender claiming a negative txId.
            TxRequest spoof2 = new TxRequest { TxId = TxIdGen.Next(NegA, ref c), Op = TxOp.Take, Sender = PosA };
            spoof2.Items.Add(Lot(Wood, 6, 50));
            Check.That(core.Apply(chest, spoof2).Status == TxStatus.Rejected, "pos-to-neg spoof rejected");
            Check.Equal(20, chest.TotalOf(Wood), "second spoof executes nothing");
            // And the true owner is unaffected: its next counter is fresh, not fenced by the spoof.
            TxRequest legit = new TxRequest { TxId = TxIdGen.Next(NegA, ref c), Op = TxOp.Take, Sender = NegA };
            legit.Items.Add(Lot(Wood, 6, 50));
            Check.That(core.Apply(chest, legit).Status == TxStatus.Accepted, "true owner commits after spoofs");
            Check.Equal(14, chest.TotalOf(Wood), "exactly one debit");
        }

        private static void ID_SenderMismatchNoLeakNegative()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 20, 50);
            TxCore coreA = new TxCore();
            uint c = 1;
            TxRequest take = new TxRequest { TxId = TxIdGen.Next(NegA, ref c), Op = TxOp.Take, Sender = NegA };
            take.Items.Add(Lot(Wood, 6, 50));
            Check.That(coreA.Apply(chestA, take).Status == TxStatus.Accepted, "commit");

            TxCore coreB = HandoffV3(coreA);
            TxResult stranger = coreB.Query(take.TxId, NegB);
            Check.That(stranger.Status == TxStatus.Rejected, "negative stranger rejected");
            Check.Equal(0, stranger.Accepted.Count, "stranger gets no payload");
            Check.Equal(TxIdGen.PeerKey(NegB), stranger.Sender, "mismatch names the requester canonically");
            TxResult mine = coreB.Query(take.TxId, NegA);
            Check.That(mine.Status == TxStatus.Accepted && !mine.TotalsOnly, "true negative sender replays exactly");
            Check.Equal(6, mine.AcceptedTotal(), "true sender keeps body");
        }

        /// <summary>All four real issue-log peers transact side by side, then replay exactly after a v3 handoff.</summary>
        private static void ID_RealIssuePeersMatrix()
        {
            long[] peers = new long[] { PosA, PosB, NegA, NegB };
            ModelChest chestA = new ModelChest(16, 16);
            chestA.AddItem(Wood, 9999, 50);
            TxCore coreA = new TxCore();
            List<TxRequest> reqs = new List<TxRequest>();
            for (int i = 0; i < peers.Length; i++)
            {
                uint c = 1;
                TxRequest take = new TxRequest { TxId = TxIdGen.Next(peers[i], ref c), Op = TxOp.Take, Sender = peers[i] };
                take.Items.Add(Lot(Wood, 3, 50));
                TxResult r = coreA.Apply(chestA, take);
                Check.That(r.Status == TxStatus.Accepted, "peer " + peers[i] + " commits, got " + r.Status);
                reqs.Add(take);
            }
            Check.Equal(peers.Length, coreA.FloorCount, "one floor entry per peer");

            TxCore coreB = HandoffV3(coreA);
            ModelChest chestB = CloneChest(chestA);
            for (int i = 0; i < reqs.Count; i++)
            {
                TxResult replay = coreB.Apply(chestB, reqs[i]);
                Check.That(replay.Status == TxStatus.Accepted && replay.IsReplay,
                    "peer " + peers[i] + " replays exactly, got " + replay.Status);
            }
            Check.Equal(chestA.GrandTotal(), chestB.GrandTotal(), "replays conserve");
        }

        /// <summary>v1 legacy bytes with a negative peer restore Duplicate totals-only and answer the raw sender.</summary>
        private static void ID_V1CompatNegative()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 20, 50);
            TxCore coreA = new TxCore();
            uint c = 1;
            TxRequest take = new TxRequest { TxId = TxIdGen.Next(NegA, ref c), Op = TxOp.Take };
            take.Items.Add(Lot(Wood, 6, 50));
            coreA.Apply(chestA, take);

            byte[] v1 = TxCore.EncodeRing(coreA.DumpRing());
            RingData legacy = TxCore.DecodeEnvelope(v1);
            Check.That(!legacy.Corrupt, "v1 bytes decode");
            Check.Equal(1, legacy.Entries.Count, "one legacy entry");
            Check.That(legacy.Entries[0].Status == TxStatus.Duplicate, "legacy marks unknown status");
            Check.Equal(TxIdGen.PeerKey(NegA), legacy.Entries[0].Sender, "v1 sender backfilled canonical");

            TxCore coreB = new TxCore();
            coreB.LoadRing(legacy);
            TxResult q = coreB.Query(take.TxId, NegA);
            Check.That(q.Status == TxStatus.Duplicate && q.TotalsOnly, "legacy restores Duplicate totals-only");
            TxResult stranger = coreB.Query(take.TxId, NegB);
            Check.That(stranger.Status == TxStatus.Rejected, "legacy stranger rejected");
        }

        /// <summary>
        /// v2 worlds written before canonicalization stored the RAW signed UID in
        /// the sender field. The decoder tolerates them when the KEY matches
        /// (normalizing to canonical); true key mismatches stay corrupt.
        /// </summary>
        private static void ID_V2LegacySignedSenderTolerated()
        {
            uint c = 1;
            long txId = TxIdGen.Next(NegA, ref c);
            RingEntry e;
            e.TxId = txId;
            e.AcceptedTotal = 5;
            e.Revision = 1;
            e.Op = TxOp.Add;
            e.Status = TxStatus.Accepted;
            e.Sender = TxIdGen.PeerKey(NegA); // canonical, as current writers store
            e.Accepted = new List<int> { 5 };
            e.TakePayloads = null;
            byte[] v2 = TxCore.EncodeRingV2(new List<RingEntry> { e }, null);
            Check.That(v2 != null && v2[0] == TxLimits.RingV2Magic, "v2 envelope");
            // Sender field offset with empty floor: magic(1)+ver(1)+floorCount(1)+ringCount(1)
            // + txId(8)+op(4)+status(1)+rev(4) = 21.
            byte[] legacy = (byte[])v2.Clone();
            Array.Copy(BitConverter.GetBytes(NegA), 0, legacy, 21, 8);
            RingData back = TxCore.DecodeEnvelope(legacy);
            Check.That(!back.Corrupt, "raw-negative v2 sender tolerated");
            Check.Equal(1, back.Entries.Count, "one entry");
            Check.Equal(TxIdGen.PeerKey(NegA), back.Entries[0].Sender, "normalized to canonical key");

            // True key mismatch is still fail-closed.
            byte[] tampered = (byte[])v2.Clone();
            Array.Copy(BitConverter.GetBytes(NegB), 0, tampered, 21, 8);
            Check.That(TxCore.DecodeEnvelope(tampered).Corrupt, "key mismatch stays corrupt");

            // The tolerated entry loads and answers the raw negative sender exactly.
            TxCore core = new TxCore();
            core.LoadRing(back);
            TxResult q = core.Query(txId, NegA);
            Check.That(q.Status == TxStatus.Accepted && !q.TotalsOnly, "tolerated entry replays exactly");
        }

        private static void ID_V3RoundTripAndMalformed()
        {
            uint c = 1;
            long txId = TxIdGen.Next(NegA, ref c);
            RingEntry e;
            e.TxId = txId;
            e.AcceptedTotal = 5;
            e.Revision = 3;
            e.Op = TxOp.Add;
            e.Status = TxStatus.Accepted;
            e.Sender = TxIdGen.PeerKey(NegA);
            e.Accepted = new List<int> { 5 };
            e.TakePayloads = null;
            Dictionary<long, uint> floor = new Dictionary<long, uint>();
            floor[TxIdGen.PeerKey(NegA)] = 1;
            byte[] v3 = TxCore.EncodeRingV3(new List<RingEntry> { e }, floor);
            Check.That(v3 != null && v3[0] == TxLimits.RingV2Magic && v3[1] == TxLimits.RingV3Version, "v3 envelope");
            RingData back = TxCore.DecodeEnvelope(v3);
            Check.That(!back.Corrupt, "v3 round-trips");
            Check.Equal(1, back.Entries.Count, "one v3 entry");
            Check.Equal(TxIdGen.PeerKey(NegA), back.Entries[0].Sender, "v3 sender key exact");
            Check.That(back.Floor.ContainsKey(TxIdGen.PeerKey(NegA)), "v3 floor survives");

            byte[] trailing = new byte[v3.Length + 1];
            Array.Copy(v3, trailing, v3.Length);
            Check.That(TxCore.DecodeEnvelope(trailing).Corrupt, "v3 trailing bytes are corrupt");
            byte[] badCount = (byte[])v3.Clone();
            badCount[badCount.Length - 1] ^= 0xFF; // may or may not break framing; must never throw.
            TxCore.DecodeEnvelope(badCount);
            // UnknownTx status never persists: craft by flipping the status byte.
            // v3 entry status offset with 1 floor entry: 2 + 2 + 8 + 2 + 8+4 = 26.
            byte[] unknown = (byte[])v3.Clone();
            unknown[26] = (byte)TxStatus.UnknownTx;
            Check.That(TxCore.DecodeEnvelope(unknown).Corrupt, "UnknownTx entry is corrupt");
        }

        private static void ID_FloorCodec()
        {
            Dictionary<long, uint> floor = new Dictionary<long, uint>();
            floor[TxIdGen.PeerKey(NegA)] = 41;
            floor[PosA] = 7;
            byte[] bytes = TxCore.EncodeFloor(floor);
            Check.That(bytes != null && bytes[0] == TxLimits.FloorMagic, "floor envelope");
            FloorData back = TxCore.DecodeFloor(bytes);
            Check.That(!back.Corrupt && back.Present, "floor round-trips");
            Check.Equal(41u, back.Floor[TxIdGen.PeerKey(NegA)], "negative peer floor survives");
            Check.Equal(7u, back.Floor[PosA], "positive peer floor survives");

            FloorData missing = TxCore.DecodeFloor(null);
            Check.That(!missing.Corrupt && !missing.Present, "missing floor key is absent-usable");
            Check.That(TxCore.DecodeFloor(new byte[0]).Present == false, "empty floor key is absent-usable");
            Check.That(TxCore.DecodeFloor(new byte[] { 1, 2, 3 }).Corrupt, "garbage floor is corrupt");
            byte[] trailing = new byte[bytes.Length + 1];
            Array.Copy(bytes, trailing, bytes.Length);
            Check.That(TxCore.DecodeFloor(trailing).Corrupt, "floor trailing bytes are corrupt");
            Check.That(TxCore.EncodeFloor(null) != null, "null floor encodes (empty-usable)");
        }

        /// <summary>
        /// Counter-reset/reconnect: same UID restarts at 1 — at/below-floor
        /// txIds stay blocked (never execute), the next counter above the floor
        /// commits. Old-gen never executes; new-gen is fresh.
        /// </summary>
        private static void ID_CounterResetOldGenBlockedNewGenRuns()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            uint sessionA = 1;
            for (uint i = 0; i < 3; i++)
            {
                TxRequest r = new TxRequest { TxId = TxIdGen.Next(NegA, ref sessionA), Op = TxOp.Take, Sender = NegA };
                r.Items.Add(Lot(Wood, 2, 50));
                Check.That(core.Apply(chest, r).Status == TxStatus.Accepted, "session A commits ctr " + (i + 1));
            }
            Check.Equal(14, chest.TotalOf(Wood), "session A debited 6");

            // Session B: counter restarted at 1 (reservation file lost). Same txIds replay;
            // unseen-but-stale counters are indeterminate — NEVER executed.
            uint sessionB = 1;
            for (uint i = 1; i <= 3; i++)
            {
                TxRequest r = new TxRequest { TxId = Tx(NegA, i), Op = TxOp.Take, Sender = NegA };
                r.Items.Add(Lot(Wood, 2, 50));
                TxResult res = core.Apply(chest, r);
                if (i <= 3)
                    Check.That(res.Status == TxStatus.UnknownTx || res.IsReplay,
                        "old-gen ctr " + i + " never re-executes, got " + res.Status);
            }
            sessionB++;
            Check.Equal(14, chest.TotalOf(Wood), "old-gen debits nothing");
            TxRequest fresh = new TxRequest { TxId = TxIdGen.Next(NegA, ref sessionA), Op = TxOp.Take, Sender = NegA };
            fresh.Items.Add(Lot(Wood, 2, 50));
            Check.That(core.Apply(chest, fresh).Status == TxStatus.Accepted, "new-gen above floor commits");
            Check.Equal(12, chest.TotalOf(Wood), "exactly one new debit");
        }

        private static void ID_CounterExhaustionFailClosed()
        {
            uint c = uint.MaxValue - 1;
            long txId;
            Check.That(TxIdGen.TryNext(PosA, ref c, out txId), "last usable counter issues");
            Check.Equal((long)(uint.MaxValue - 1), TxIdGen.CounterOf(txId), "issued counter value");
            Check.That(!TxIdGen.TryNext(PosA, ref c, out txId), "exhausted counter refuses");
            Check.Equal(0L, txId, "refusal yields txId 0");
            c = 0;
            Check.That(!TxIdGen.TryNext(PosA, ref c, out txId), "wrapped counter refuses");
        }

        /// <summary>Floor unbounded past FloorCap, mixing negative and positive peers.</summary>
        private static void ID_ManyPeersUnbounded()
        {
            ModelChest chest = new ModelChest(64, 64);
            chest.AddItem(Wood, 99999, 50);
            TxCore core = new TxCore();
            int peers = TxLimits.FloorCap + 6;
            for (int i = 1; i <= peers; i++)
            {
                long peer = (i % 2 == 0) ? -((long)i) : (long)i;
                uint c = 1;
                TxRequest r = new TxRequest { TxId = TxIdGen.Next(peer, ref c), Op = TxOp.Take, Sender = peer };
                r.Items.Add(Lot(Wood, 1, 50));
                Check.That(core.Apply(chest, r).Status == TxStatus.Accepted, "peer " + peer + " transacts");
            }
            Check.Equal(peers, core.FloorCount, "floor unbounded");
            Check.Equal(99999 - peers, chest.TotalOf(Wood), "exactly-once per peer");
        }

        /// <summary>v3 carries a 300-peer floor exactly (v2's count byte could not).</summary>
        private static void ID_V3LargeFloorRoundTrip()
        {
            TxCore core = new TxCore();
            ModelChest chest = new ModelChest(128, 128);
            chest.AddItem(Wood, 999999, 50);
            for (int i = 1; i <= 300; i++)
            {
                uint c = 1;
                TxRequest r = new TxRequest { TxId = TxIdGen.Next((long)i, ref c), Op = TxOp.Take, Sender = (long)i };
                r.Items.Add(Lot(Wood, 1, 50));
                core.Apply(chest, r);
            }
            byte[] v3 = TxCore.EncodeRingV3(core.DumpRing(), core.DumpFloor());
            Check.That(v3 != null, "v3 encodes 300-peer floor");
            RingData back = TxCore.DecodeEnvelope(v3);
            Check.That(!back.Corrupt, "300-peer v3 decodes");
            Check.Equal(300, back.Floor.Count, "full floor survives");
            TxCore dst = new TxCore();
            dst.LoadRing(back);
            Check.Equal(300, dst.FloorCount, "floor restores");
        }

        /// <summary>
        /// Corrupt ring fail-safe WITH recovery: full fail-closed first, then
        /// degraded recovery on the intact independent floor — old-gen blocked,
        /// new-gen commits, conservation holds, and the flag heals on commit.
        /// </summary>
        private static void ID_CorruptRecovery()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            uint c = 1;
            for (uint i = 1; i <= 5; i++)
            {
                TxRequest r = new TxRequest { TxId = TxIdGen.Next(NegA, ref c), Op = TxOp.Take, Sender = NegA };
                r.Items.Add(Lot(Wood, 1, 50));
                core.Apply(chest, r);
            }
            Check.Equal(15, chest.TotalOf(Wood), "5 debited pre-corruption");
            FloorData intactFloor = TxCore.DecodeFloor(TxCore.EncodeFloor(core.DumpFloor()));
            Check.That(!intactFloor.Corrupt, "independent floor intact");
            intactFloor.Present = true;

            // Full fail-closed with no trustworthy floor.
            RingData bad = TxCore.DecodeEnvelope(new byte[] { 1, 2, 3 });
            TxCore dead = new TxCore();
            dead.LoadRing(bad);
            Check.That(dead.RingCorrupt, "corrupt flagged");
            TxRequest probe = new TxRequest { TxId = Tx(NegA, 99), Op = TxOp.Take, Sender = NegA };
            probe.Items.Add(Lot(Wood, 1, 50));
            Check.That(dead.Apply(chest, probe).Status == TxStatus.UnknownTx, "fail closed mutates nothing");
            Check.Equal(15, chest.TotalOf(Wood), "nothing debited while fail-closed");
            // Recovery with nothing trustworthy stays fail-closed.
            dead.RecoverWithFloor(null);
            Check.That(dead.Apply(chest, probe).Status == TxStatus.UnknownTx, "no-trust recovery stays closed");

            // Degraded recovery with the intact floor.
            TxCore rec = new TxCore();
            rec.LoadRing(bad);
            rec.RecoverWithFloor(intactFloor);
            Check.That(rec.RingCorrupt && !rec.FloorCorrupt, "degraded: ring unusable, floor trusted");
            TxRequest oldGen = new TxRequest { TxId = Tx(NegA, 3), Op = TxOp.Take, Sender = NegA };
            oldGen.Items.Add(Lot(Wood, 1, 50));
            Check.That(rec.Apply(chest, oldGen).Status == TxStatus.UnknownTx, "old-gen stays blocked");
            Check.Equal(15, chest.TotalOf(Wood), "old-gen debits nothing");
            TxRequest newGen = new TxRequest { TxId = Tx(NegA, 6), Op = TxOp.Take, Sender = NegA };
            newGen.Items.Add(Lot(Wood, 4, 50));
            TxResult committed = rec.Apply(chest, newGen);
            Check.That(committed.Status == TxStatus.Accepted && !committed.IsReplay, "new-gen commits");
            Check.Equal(11, chest.TotalOf(Wood), "exactly one new debit: conservation holds");
            Check.That(!rec.RingCorrupt, "successful commit heals the ring flag");
            TxResult replay = rec.Apply(chest, newGen);
            Check.That(replay.IsReplay && replay.Status == TxStatus.Accepted, "post-recovery replay stable");
            Check.Equal(11, chest.TotalOf(Wood), "replay debits nothing");
        }

        /// <summary>Split-copy seed max-merges (either crash order safe); corrupt ring + good floor degrades.</summary>
        private static void ID_SplitFloorMerge()
        {
            RingData ring = new RingData();
            ring.Floor[TxIdGen.PeerKey(NegA)] = 3;
            FloorData floor = new FloorData();
            floor.Present = true;
            floor.Floor[TxIdGen.PeerKey(NegA)] = 7;
            floor.Floor[PosA] = 2;

            TxCore core = new TxCore();
            core.LoadRingWithFloor(ring, floor);
            Check.That(!core.RingCorrupt && !core.FloorCorrupt, "healthy split seed");
            Check.Equal(2, core.FloorCount, "both peers present");
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            // ctr 5 is above the ring copy (3) but below the merged max (7): blocked.
            TxRequest mid = new TxRequest { TxId = Tx(NegA, 5), Op = TxOp.Take, Sender = NegA };
            mid.Items.Add(Lot(Wood, 1, 50));
            Check.That(core.Apply(chest, mid).Status == TxStatus.UnknownTx, "max-merge blocks below-max");
            Check.Equal(20, chest.TotalOf(Wood), "blocked debits nothing");
            TxRequest fresh = new TxRequest { TxId = Tx(NegA, 8), Op = TxOp.Take, Sender = NegA };
            fresh.Items.Add(Lot(Wood, 1, 50));
            Check.That(core.Apply(chest, fresh).Status == TxStatus.Accepted, "above-max commits");

            // Corrupt ring + good floor degrades; corrupt floor + good ring uses the ring copy.
            TxCore degraded = new TxCore();
            RingData bad = TxCore.DecodeEnvelope(new byte[] { 9, 9, 9 });
            degraded.LoadRingWithFloor(bad, floor);
            Check.That(degraded.RingCorrupt && !degraded.FloorCorrupt, "degraded split seed");
            TxCore ringWins = new TxCore();
            FloorData badFloor = TxCore.DecodeFloor(new byte[] { 0xFE, 0x01, 9, 9 });
            RingData good = new RingData();
            good.Floor[PosA] = 4;
            ringWins.LoadRingWithFloor(good, badFloor);
            Check.That(!ringWins.RingCorrupt && !ringWins.FloorCorrupt, "good ring wins over corrupt floor copy");
            Check.Equal(1, ringWins.FloorCount, "ring-embedded floor used");
        }

        /// <summary>
        /// Torn reads (two non-atomic ZDO keys, either generation newer) merge
        /// safely: max-merge never lowers a high-water. A stale floor key next
        /// to a newer ring cannot un-block an old-generation txId.
        /// </summary>
        private static void ID_TornReadSafe()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            // Newer ring (hw=7) + stale floor key (hw=3): merged max is 7.
            RingData ring = new RingData();
            ring.Floor[TxIdGen.PeerKey(NegA)] = 7;
            FloorData staleKey = new FloorData();
            staleKey.Present = true;
            staleKey.Floor[TxIdGen.PeerKey(NegA)] = 3;
            TxCore core = new TxCore();
            core.LoadRingWithFloor(ring, staleKey);
            Check.That(!core.RingCorrupt && !core.FloorCorrupt, "torn seed stays usable");
            TxRequest mid = new TxRequest { TxId = Tx(NegA, 5), Op = TxOp.Take, Sender = NegA };
            mid.Items.Add(Lot(Wood, 1, 50));
            Check.That(core.Apply(chest, mid).Status == TxStatus.UnknownTx, "stale key cannot un-block below-max");
            Check.Equal(20, chest.TotalOf(Wood), "blocked debits nothing");
            TxRequest fresh = new TxRequest { TxId = Tx(NegA, 8), Op = TxOp.Take, Sender = NegA };
            fresh.Items.Add(Lot(Wood, 1, 50));
            Check.That(core.Apply(chest, fresh).Status == TxStatus.Accepted, "above-max commits on torn seed");
            Check.Equal(19, chest.TotalOf(Wood), "exactly one debit");
        }

        private static void ID_ActorFirstIntact()
        {
            Check.That(TxResponsePolicy.ClassifyActorPath(true, true) == TxActorPath.Player,
                "resolved actor always takes the player path (even for the server sender)");
            Check.That(TxResponsePolicy.ClassifyActorPath(true, false) == TxActorPath.Player,
                "resolved actor, non-server sender");
            Check.That(TxResponsePolicy.ClassifyActorPath(false, true) == TxActorPath.ServerAutomation,
                "actor-less server sender is automation");
            Check.That(TxResponsePolicy.ClassifyActorPath(false, false) == TxActorPath.RejectNoActor,
                "actor-less stranger rejected");
        }
    }
}
