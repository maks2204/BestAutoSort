using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Ring v2 + rejected persistence + Take-exact recovery + floor tests.
    ///
    /// All handoffs go through the PRODUCTION-SHARED codec
    /// (TxCore.EncodeRingV2 / TxCore.DecodeEnvelope) — the exact functions the
    /// Unity manager calls in WriteRing/ReadRing — never a test-only serializer.
    ///
    /// Contract under test:
    /// - Same txId never changes its terminal outcome across handoff
    ///   (Accepted/Partial/Rejected stable; replays carry IsReplay).
    /// - Wire Duplicate survives only for legacy v1 entries (genuinely unknown
    ///   original): they restore totals-only.
    /// - v2 Take entries carry the actual debited-item metadata; inexact ones
    ///   restore totals-only instead of fabricating.
    /// - The durable per-sender floor (64 peers, never evicted) makes absent
    ///   txIds at/below high-water indeterminate — never executed.
    /// - Malformed persisted bytes fail closed (no mutation), never decode as
    ///   an empty usable ring.
    /// </summary>
    internal static class TxRingV2Tests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);
        private static readonly ItemKey Stone = new ItemKey(1002, 1, 0, 0);

        private static long Tx(long peer, uint ctr)
        {
            return unchecked((peer << 32) | ctr);
        }

        private static TxItem Lot(ItemKey key, int amount, int maxStack)
        {
            return new TxItem { Key = key, Amount = amount, MaxStack = maxStack };
        }

        private static TxCore Handoff(TxCore src)
        {
            byte[] bytes = TxCore.EncodeRingV2(src.DumpRing(), src.DumpFloor());
            RingData data = TxCore.DecodeEnvelope(bytes);
            Check.That(!data.Corrupt, "shared-codec round-trip must not be corrupt");
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
            Console.WriteLine("V2_AddBatchExactReplay");
            V2_AddBatchExactReplay();
            Console.WriteLine("V2_RejectedAddHandoffRace");
            V2_RejectedAddHandoffRace();
            Console.WriteLine("V2_RejectedTakeInverse");
            V2_RejectedTakeInverse();
            Console.WriteLine("V2_TakeBatchExactMetadata");
            V2_TakeBatchExactMetadata();
            Console.WriteLine("V2_ReplayVsOriginal");
            V2_ReplayVsOriginal();
            Console.WriteLine("V2_V1CompatRewrite");
            V2_V1CompatRewrite();
            Console.WriteLine("V2_MalformedFailClosed");
            V2_MalformedFailClosed();
            Console.WriteLine("V2_FloorEvictionIndeterminate");
            V2_FloorEvictionIndeterminate();
            Console.WriteLine("V2_FloorUnboundedBeyondCap");
            V2_FloorUnboundedBeyondCap();
            Console.WriteLine("V2_OutOfOrderDelayed");
            V2_OutOfOrderDelayed();
            Console.WriteLine("V2_SenderMismatchNoLeak");
            V2_SenderMismatchNoLeak();
            Console.WriteLine("V2_SpoofTxIdRejected");
            V2_SpoofTxIdRejected();
            Console.WriteLine("V2_CachedOutcomeSurvivesSpoofAndSkewResend");
            V2_CachedOutcomeSurvivesSpoofAndSkewResend();
            Console.WriteLine("V2_LoadRingTrueReset");
            V2_LoadRingTrueReset();
            Console.WriteLine("V2_ConservationMatrix");
            V2_ConservationMatrix();
            Console.WriteLine("V2_RejectedPersistsInRing");
            V2_RejectedPersistsInRing();
            Console.WriteLine("V2_InexactTakeTotalsOnly");
            V2_InexactTakeTotalsOnly();
        }

        /// <summary>
        /// Exact AddBatch replay: [10,20,5] survives the handoff with per-item
        /// counts, so the client removes exactly what the chest stored.
        /// </summary>
        private static void V2_AddBatchExactReplay()
        {
            ModelChest chestA = new ModelChest(6, 4);
            TxCore coreA = new TxCore();
            TxRequest add = new TxRequest { TxId = Tx(7, 1), Op = TxOp.AddBatch, Sender = 7 };
            add.Items.Add(Lot(Wood, 10, 50));
            add.Items.Add(Lot(Wood, 20, 50));
            add.Items.Add(Lot(Wood, 5, 50));
            TxResult r1 = coreA.Apply(chestA, add);
            Check.That(r1.Status == TxStatus.Accepted, "addbatch must be Accepted, got " + r1.Status);
            Check.Equal(3, r1.Accepted.Count, "three accepted counts");
            Check.Equal(10, r1.Accepted[0], "accepted[0]");
            Check.Equal(20, r1.Accepted[1], "accepted[1]");
            Check.Equal(5, r1.Accepted[2], "accepted[2]");

            TxCore coreB = Handoff(coreA);
            ModelChest chestB = CloneChest(chestA);
            TxResult replay = coreB.Apply(chestB, add);
            Check.That(replay.Status == TxStatus.Accepted && !replay.TotalsOnly && replay.IsReplay,
                "replay keeps original outcome exact, got " + replay.Status);
            Check.Equal(3, replay.Accepted.Count, "replay arity");
            Check.Equal(10, replay.Accepted[0], "replay accepted[0]");
            Check.Equal(20, replay.Accepted[1], "replay accepted[1]");
            Check.Equal(5, replay.Accepted[2], "replay accepted[2]");
            Check.Equal(35, chestB.TotalOf(Wood), "replay must not re-apply");
            // Exact source removal by the replayed counts.
            ModelChest bag = new ModelChest(6, 4);
            bag.AddItem(Wood, 35, 50);
            int removed = 0;
            for (int i = 0; i < replay.Accepted.Count; i++)
                removed += bag.TakeItem(Wood, replay.Accepted[i]);
            Check.Equal(35, removed, "client removes exactly what the chest stored");
            Check.Equal(0, bag.GrandTotal(), "bag empty after exact removal");
        }

        /// <summary>
        /// Rejected Add (chest full) persists as Rejected; the same txId never
        /// executes even after space frees up.
        /// </summary>
        private static void V2_RejectedAddHandoffRace()
        {
            ModelChest chestA = new ModelChest(1, 1);
            chestA.AddItem(Wood, 50, 50);
            TxCore coreA = new TxCore();
            TxRequest add = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Add, Sender = 7 };
            add.Items.Add(Lot(Stone, 10, 50));
            TxResult r1 = coreA.Apply(chestA, add);
            Check.That(r1.Status == TxStatus.Rejected, "add to full chest must be Rejected, got " + r1.Status);

            TxCore coreB = Handoff(coreA);
            ModelChest chestB = new ModelChest(6, 4); // plenty of space now
            TxResult replay = coreB.Apply(chestB, add);
            Check.That(replay.Status == TxStatus.Rejected,
                "rejected add must never flip to Accepted, got " + replay.Status);
            Check.Equal(0, chestB.GrandTotal(), "nothing stored on replay");
        }

        /// <summary>
        /// Inverse Take: rejected for absent stock, then the stock appears —
        /// the same txId still answers Rejected and debits nothing.
        /// </summary>
        private static void V2_RejectedTakeInverse()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 5, 50);
            TxCore coreA = new TxCore();
            TxRequest take = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Take, Sender = 7 };
            take.Items.Add(Lot(Stone, 1, 50));
            TxResult r1 = coreA.Apply(chestA, take);
            Check.That(r1.Status == TxStatus.Rejected, "take of absent item must be Rejected");

            TxCore coreB = Handoff(coreA);
            ModelChest chestB = CloneChest(chestA);
            chestB.AddItem(Stone, 10, 50); // stock appears after handoff
            TxResult replay = coreB.Apply(chestB, take);
            Check.That(replay.Status == TxStatus.Rejected,
                "same rejected txId must never execute, got " + replay.Status);
            Check.Equal(10, chestB.TotalOf(Stone), "stock untouched");
            Check.That(TxResponsePolicy.Classify(replay.Status, replay.TotalsOnly, TxOp.Take, 1)
                == TxCompletionKind.FailedNotCommitted, "stays failed-not-committed");
        }

        /// <summary>
        /// Exact TakeBatch replay: per-item accepted[] plus the actual
        /// debited-item metadata blobs round-trip through the shared codec.
        /// </summary>
        private static void V2_TakeBatchExactMetadata()
        {
            ModelChest chestA = new ModelChest(6, 4);
            chestA.AddItem(Wood, 20, 50);
            chestA.AddItem(Stone, 20, 50);
            TxCore coreA = new TxCore();
            TxRequest take = new TxRequest { TxId = Tx(7, 1), Op = TxOp.TakeBatch, Sender = 7 };
            take.Items.Add(Lot(Wood, 6, 50));
            take.Items.Add(Lot(Stone, 4, 50));
            TxResult r1 = coreA.Apply(chestA, take);
            Check.That(r1.Status == TxStatus.Accepted, "takebatch must be Accepted");

            List<RingEntry> ring = coreA.DumpRing();
            Check.Equal(1, ring.Count, "one ring entry");
            Check.That(ring[0].Status == TxStatus.Accepted, "ring keeps original status");
            Check.That(ring[0].TakePayloads != null && ring[0].TakePayloads.Count == 2,
                "take metadata blobs aligned with accepted[]");
            ItemKey k0 = TxCore.DecodeTakeKey(ring[0].TakePayloads[0].Bytes);
            ItemKey k1 = TxCore.DecodeTakeKey(ring[0].TakePayloads[1].Bytes);
            Check.That(k0.Equals(Wood) && k1.Equals(Stone), "blobs carry the actual debited keys");

            TxCore coreB = Handoff(coreA);
            TxResult q = coreB.Query(take.TxId, 7);
            Check.That(q.Status == TxStatus.Accepted && !q.TotalsOnly && q.IsReplay,
                "query replays exact take, got " + q.Status);
            Check.Equal(2, q.Accepted.Count, "take arity preserved");
            Check.Equal(6, q.Accepted[0], "take accepted[0]");
            Check.Equal(4, q.Accepted[1], "take accepted[1]");
            ModelChest chestB = CloneChest(chestA);
            TxResult replay = coreB.Apply(chestB, take);
            Check.That(replay.Status == TxStatus.Accepted && replay.IsReplay, "apply replays, no re-debit");
            Check.Equal(14, chestB.TotalOf(Wood), "no double-debit wood");
            Check.Equal(16, chestB.TotalOf(Stone), "no double-debit stone");
        }

        private static void V2_ReplayVsOriginal()
        {
            ModelChest chest = new ModelChest(4, 4);
            TxCore core = new TxCore();
            TxRequest add = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Add, Sender = 7 };
            add.Items.Add(Lot(Wood, 5, 50));
            TxResult first = core.Apply(chest, add);
            TxResult second = core.Apply(chest, add);
            Check.That(!first.IsReplay && first.Status == TxStatus.Accepted, "original");
            Check.That(second.IsReplay && second.Status == first.Status,
                "replay separated by flag, outcome stable");
            Check.That(second.Status != TxStatus.Duplicate, "v2 replays do not degrade to Duplicate");
            Check.Equal(5, chest.TotalOf(Wood), "applied exactly once");
        }

        /// <summary>
        /// v1 compat: legacy bytes restore as Duplicate totals-only; the next
        /// successful mutation rewrites the ring as v2 (mixed entries coexist).
        /// No world migration: old bytes are never rewritten in place.
        /// </summary>
        private static void V2_V1CompatRewrite()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 20, 50);
            TxCore coreA = new TxCore();
            TxRequest take = new TxRequest { TxId = 101, Op = TxOp.TakeBatch };
            take.Items.Add(Lot(Wood, 6, 50));
            coreA.Apply(chestA, take);

            byte[] v1bytes = TxCore.EncodeRing(coreA.DumpRing());
            RingData legacy = TxCore.DecodeEnvelope(v1bytes);
            Check.That(!legacy.Corrupt, "v1 bytes must decode");
            Check.Equal(1, legacy.Entries.Count, "one legacy entry");
            Check.That(legacy.Entries[0].Status == TxStatus.Duplicate, "legacy marks genuinely-unknown status");

            TxCore coreB = new TxCore();
            coreB.LoadRing(legacy);
            TxResult q = coreB.Query(101);
            Check.That(q.Status == TxStatus.Duplicate && q.TotalsOnly,
                "legacy restores Duplicate totals-only, got " + q.Status);

            ModelChest chestB = CloneChest(chestA);
            TxRequest fresh = new TxRequest { TxId = 102, Op = TxOp.TakeBatch };
            fresh.Items.Add(Lot(Wood, 4, 50));
            TxResult r = coreB.Apply(chestB, fresh);
            Check.That(r.Status == TxStatus.Accepted, "fresh tx still applies on legacy-seeded core");

            byte[] v2bytes = TxCore.EncodeRingV2(coreB.DumpRing(), coreB.DumpFloor());
            Check.That(v2bytes[0] == TxLimits.RingV2Magic, "next mutation rewrites v2");
            RingData back = TxCore.DecodeEnvelope(v2bytes);
            Check.That(!back.Corrupt && back.Entries.Count == 2, "mixed ring round-trips");
            Check.That(back.Entries[0].Status == TxStatus.Duplicate, "legacy entry preserved as Duplicate");
            Check.That(back.Entries[1].Status == TxStatus.Accepted, "new entry keeps original status");
        }

        private static void V2_MalformedFailClosed()
        {
            // Missing key (null/empty) = fresh chest, usable.
            Check.That(!TxCore.DecodeEnvelope(null).Corrupt, "null is empty-usable");
            Check.That(!TxCore.DecodeEnvelope(new byte[0]).Corrupt, "empty is empty-usable");
            // Present-but-unparseable = corrupt.
            Check.That(TxCore.DecodeEnvelope(new byte[] { 1, 2, 3 }).Corrupt, "garbage is corrupt");
            Check.That(TxCore.DecodeEnvelope(new byte[] { 2, 0, 1 }).Corrupt, "truncated v1 is corrupt");
            Check.That(TxCore.DecodeEnvelope(new byte[] { 33 }).Corrupt, "over-cap v1 count is corrupt");
            byte[] v1 = TxCore.EncodeRing(new List<RingEntry> { LegacyEntry(5, 3, 9) });
            byte[] trailing = new byte[v1.Length + 1];
            Array.Copy(v1, trailing, v1.Length);
            Check.That(TxCore.DecodeEnvelope(trailing).Corrupt, "v1 trailing bytes are corrupt");
            Check.That(TxCore.DecodeEnvelope(new byte[] { 0xFF, 0x09, 0, 0 }).Corrupt, "unknown v2 version is corrupt");
            byte[] v2 = TxCore.EncodeRingV2(new List<RingEntry> { ExactEntry(Tx(7, 1), 7) }, null);
            byte[] v2trailing = new byte[v2.Length + 2];
            Array.Copy(v2, v2trailing, v2.Length);
            Check.That(TxCore.DecodeEnvelope(v2trailing).Corrupt, "v2 trailing bytes are corrupt");
            v2[v2.Length - 1] ^= 0xFF;
            if (TxCore.DecodeEnvelope(v2).Corrupt)
            {
                // Flipped tail may or may not break framing; either way no crash.
            }

            // Fail closed: corrupt state never mutates.
            RingData bad = TxCore.DecodeEnvelope(new byte[] { 1, 2, 3 });
            TxCore core = new TxCore();
            core.LoadRing(bad);
            Check.That(core.RingCorrupt, "corrupt ring flagged");
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxRequest take = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Take, Sender = 7 };
            take.Items.Add(Lot(Wood, 5, 50));
            TxResult r = core.Apply(chest, take);
            Check.That(r.Status == TxStatus.UnknownTx, "corrupt core answers UnknownTx, got " + r.Status);
            Check.Equal(20, chest.TotalOf(Wood), "corrupt core mutates nothing");
        }

        /// <summary>
        /// Ring overflow: the evicted tx is UnknownTx after handoff (floor makes
        /// it indeterminate) and re-applying the same txId never executes.
        /// </summary>
        private static void V2_FloorEvictionIndeterminate()
        {
            ModelChest chestA = new ModelChest(64, 64);
            chestA.AddItem(Wood, 99999, 50);
            TxCore coreA = new TxCore();
            long peer = 7;
            uint firstCtr = 1000;
            uint count = (uint)TxLimits.RingCap + 5;
            for (uint c = 0; c < count; c++)
            {
                TxRequest r = new TxRequest { TxId = Tx(peer, (uint)(firstCtr + c)), Op = TxOp.Take, Sender = peer };
                r.Items.Add(Lot(Wood, 1, 50));
                coreA.Apply(chestA, r);
            }
            TxCore coreB = Handoff(coreA);
            TxResult q = coreB.Query(Tx(peer, firstCtr), peer);
            Check.That(q.Status == TxStatus.UnknownTx, "evicted tx must be UnknownTx, got " + q.Status);
            Check.That(TxResponsePolicy.Classify(q.Status, q.TotalsOnly, TxOp.Take, 1)
                == TxCompletionKind.Indeterminate, "evicted UnknownTx is Indeterminate");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.Indeterminate),
                "evicted tx must not cascade");
            ModelChest chestB = CloneChest(chestA);
            int before = chestB.TotalOf(Wood);
            TxRequest retry = new TxRequest { TxId = Tx(peer, firstCtr), Op = TxOp.Take, Sender = peer };
            retry.Items.Add(Lot(Wood, 1, 50));
            TxResult re = coreB.Apply(chestB, retry);
            Check.That(re.Status == TxStatus.UnknownTx, "evicted txId must never re-execute, got " + re.Status);
            Check.Equal(before, chestB.TotalOf(Wood), "no debit on indeterminate replay");
        }

        /// <summary>
        /// Floor is UNBOUNDED: FloorCap is a warn threshold, never a refusal.
        /// Peers past 64 still transact (no liveness brick in long-lived
        /// worlds); the map only grows and every peer keeps its high-water.
        /// </summary>
        private static void V2_FloorUnboundedBeyondCap()
        {
            ModelChest chest = new ModelChest(64, 64);
            chest.AddItem(Wood, 99999, 50);
            TxCore core = new TxCore();
            int peers = TxLimits.FloorCap + 16;
            for (long peer = 1; peer <= peers; peer++)
            {
                TxRequest r = new TxRequest { TxId = Tx(peer, 1), Op = TxOp.Take, Sender = peer };
                r.Items.Add(Lot(Wood, 1, 50));
                TxResult res = core.Apply(chest, r);
                Check.That(res.Status == TxStatus.Accepted, "peer " + peer + " transacts past the warn threshold");
            }
            Check.Equal(peers, core.FloorCount, "floor grows unbounded, never evicted");
            Check.Equal(99999 - peers, chest.TotalOf(Wood), "every peer debited exactly once");
            // An established peer still transacts.
            TxRequest known = new TxRequest { TxId = Tx(1, 2), Op = TxOp.Take, Sender = 1 };
            known.Items.Add(Lot(Wood, 1, 50));
            Check.That(core.Apply(chest, known).Status == TxStatus.Accepted, "known peer unaffected");
        }

        /// <summary>
        /// Delayed first-time out-of-order request (counter below high-water,
        /// never seen) is indeterminate — never executed.
        /// </summary>
        private static void V2_OutOfOrderDelayed()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            TxRequest late = new TxRequest { TxId = Tx(7, 5), Op = TxOp.Take, Sender = 7 };
            late.Items.Add(Lot(Wood, 5, 50));
            Check.That(core.Apply(chest, late).Status == TxStatus.Accepted, "first arrival applies");
            TxRequest delayed = new TxRequest { TxId = Tx(7, 3), Op = TxOp.Take, Sender = 7 };
            delayed.Items.Add(Lot(Wood, 5, 50));
            TxResult r = core.Apply(chest, delayed);
            Check.That(r.Status == TxStatus.UnknownTx, "delayed first-timer indeterminate, got " + r.Status);
            Check.Equal(15, chest.TotalOf(Wood), "never executed");
        }

        /// <summary>
        /// Sender/txId mismatch on replay: Rejected for the stranger, with the
        /// cached payload stripped (no Take-byte leak); the true sender still
        /// replays exactly.
        /// </summary>
        private static void V2_SenderMismatchNoLeak()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 20, 50);
            TxCore coreA = new TxCore();
            TxRequest take = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Take, Sender = 7 };
            take.Items.Add(Lot(Wood, 6, 50));
            Check.That(coreA.Apply(chestA, take).Status == TxStatus.Accepted, "commit");

            TxCore coreB = Handoff(coreA);
            TxResult stranger = coreB.Query(take.TxId, 8);
            Check.That(stranger.Status == TxStatus.Rejected, "stranger replay rejected, got " + stranger.Status);
            Check.Equal(0, stranger.Accepted.Count, "stranger gets no payload");
            TxResult mine = coreB.Query(take.TxId, 7);
            Check.That(mine.Status == TxStatus.Accepted && !mine.TotalsOnly, "true sender replays exactly");
            Check.Equal(6, mine.AcceptedTotal(), "true sender keeps payload");
        }

        private static void V2_SpoofTxIdRejected()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            // txId peer bits say 7, but the authenticated sender is 8.
            TxRequest spoof = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Take, Sender = 8 };
            spoof.Items.Add(Lot(Wood, 6, 50));
            TxResult r = core.Apply(chest, spoof);
            Check.That(r.Status == TxStatus.Rejected, "spoofed txId rejected, got " + r.Status);
            Check.Equal(20, chest.TotalOf(Wood), "spoof executes nothing");
        }

        /// <summary>
        /// P0 regression (service pre-replay branches): a committed Accepted txId
        /// re-sent with mismatched sender bytes — or as a version-skew/legacy
        /// resend (Sender 0) — must NOT overwrite the cached outcome. The spoof
        /// resend is Rejected with no payload, the skew resend replays the
        /// original, and the true sender still Queries the original status/body.
        /// Holds across a production-codec handoff too.
        /// </summary>
        private static void V2_CachedOutcomeSurvivesSpoofAndSkewResend()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            TxRequest take = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Take, Sender = 7 };
            take.Items.Add(Lot(Wood, 6, 50));
            TxResult first = core.Apply(chest, take);
            Check.That(first.Status == TxStatus.Accepted, "commit Accepted, got " + first.Status);
            int afterCommit = chest.TotalOf(Wood);

            // Spoofed resend: same txId, mismatched sender bytes.
            TxRequest spoof = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Take, Sender = 8 };
            spoof.Items.Add(Lot(Wood, 6, 50));
            TxResult spoofed = core.Apply(chest, spoof);
            Check.That(spoofed.Status == TxStatus.Rejected, "spoof resend rejected, got " + spoofed.Status);
            Check.Equal(0, spoofed.Accepted.Count, "stranger gets no payload");
            Check.Equal(afterCommit, chest.TotalOf(Wood), "spoof resend must not re-apply");

            // Version-skew/legacy resend (unauthenticated sender tag).
            TxRequest skew = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Take, Sender = 0 };
            skew.Items.Add(Lot(Wood, 6, 50));
            TxResult skewed = core.Apply(chest, skew);
            Check.That(skewed.Status == TxStatus.Accepted && skewed.IsReplay,
                "skew resend replays original, got " + skewed.Status);
            Check.Equal(6, skewed.AcceptedTotal(), "skew resend keeps original body");
            Check.Equal(afterCommit, chest.TotalOf(Wood), "skew resend must not re-apply");

            // True sender still sees the original outcome; stranger is refused.
            TxResult mine = core.Query(take.TxId, 7);
            Check.That(mine.Status == TxStatus.Accepted && !mine.TotalsOnly, "true sender keeps original");
            Check.Equal(6, mine.AcceptedTotal(), "true sender keeps body");
            TxResult stranger = core.Query(take.TxId, 8);
            Check.That(stranger.Status == TxStatus.Rejected, "stranger rejected");
            Check.Equal(0, stranger.Accepted.Count, "stranger gets no payload");
            Check.Equal(8, stranger.Sender, "mismatch names the requester");

            // Same guarantees after a handoff through the production codec.
            TxCore post = Handoff(core);
            ModelChest chestB = CloneChest(chest);
            TxResult spoofedPost = post.Apply(chestB, spoof);
            Check.That(spoofedPost.Status == TxStatus.Rejected, "post-handoff spoof resend rejected");
            Check.Equal(0, spoofedPost.Accepted.Count, "post-handoff stranger gets no payload");
            TxResult minePost = post.Query(take.TxId, 7);
            Check.That(minePost.Status == TxStatus.Accepted && !minePost.TotalsOnly,
                "post-handoff true sender keeps original");
            Check.Equal(6, minePost.AcceptedTotal(), "post-handoff true sender keeps body");
            Check.Equal(afterCommit, chestB.TotalOf(Wood), "post-handoff resend must not re-apply");
        }

        /// <summary>
        /// LoadRing is a true reset: _processed, _order, _ring, floor and
        /// revision are all rebuilt from the payload (empty = fresh incarnation).
        /// </summary>
        private static void V2_LoadRingTrueReset()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            TxRequest take = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Take, Sender = 7 };
            take.Items.Add(Lot(Wood, 5, 50));
            core.Apply(chest, take);
            Check.That(core.ProcessedCount > 0 && core.RingCount > 0 && core.FloorCount > 0, "state exists");
            core.LoadRing(new RingData());
            Check.Equal(0, core.ProcessedCount, "processed reset");
            Check.Equal(0, core.RingCount, "ring reset");
            Check.Equal(0, core.FloorCount, "floor reset");
            Check.Equal(0, core.Revision, "revision reset");
            Check.That(!core.RingCorrupt, "empty is usable, not corrupt");
        }

        /// <summary>
        /// Conservation matrix: every op × handoff × replay conserves the
        /// grand total and keeps its outcome.
        /// </summary>
        private static void V2_ConservationMatrix()
        {
            TxOp[] ops = new TxOp[] { TxOp.Add, TxOp.AddBatch, TxOp.Take, TxOp.TakeBatch, TxOp.Move };
            for (int oi = 0; oi < ops.Length; oi++)
            {
                ModelChest chestA = new ModelChest(6, 4);
                chestA.AddItem(Wood, 40, 50);
                chestA.AddItem(Stone, 40, 50);
                ModelChest bagA = new ModelChest(6, 4);
                bagA.AddItem(Wood, 40, 50);
                TxCore coreA = new TxCore();
                TxRequest req = new TxRequest { TxId = Tx(7, (uint)(11 + oi)), Op = ops[oi], Sender = 7 };
                req.Items.Add(Lot(Wood, 7, 50));
                if (ops[oi] == TxOp.AddBatch || ops[oi] == TxOp.TakeBatch)
                    req.Items.Add(Lot(Stone, 5, 50));
                TxResult first = coreA.Apply(chestA, req);
                int chestBefore = chestA.GrandTotal();

                TxCore coreB = Handoff(coreA);
                ModelChest chestB = CloneChest(chestA);
                TxResult replay = coreB.Apply(chestB, req);
                Check.That(replay.Status == first.Status && replay.IsReplay,
                    ops[oi] + ": outcome stable, got " + replay.Status);
                Check.Equal(chestBefore, chestB.GrandTotal(), ops[oi] + ": replay conserves chest");
                Check.Equal(first.AcceptedTotal(), replay.AcceptedTotal(), ops[oi] + ": totals match");
            }
        }

        private static void V2_RejectedPersistsInRing()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 5, 50);
            TxCore core = new TxCore();
            TxRequest take = new TxRequest { TxId = Tx(7, 1), Op = TxOp.Take, Sender = 7 };
            take.Items.Add(Lot(Stone, 1, 50));
            Check.That(core.Apply(chest, take).Status == TxStatus.Rejected, "rejected");
            List<RingEntry> ring = core.DumpRing();
            Check.Equal(1, ring.Count, "rejected entry persists in ring");
            Check.That(ring[0].Status == TxStatus.Rejected, "ring keeps Rejected (not Duplicate)");
            RingData back = TxCore.DecodeEnvelope(TxCore.EncodeRingV2(ring, core.DumpFloor()));
            Check.That(!back.Corrupt && back.Entries.Count == 1
                && back.Entries[0].Status == TxStatus.Rejected, "rejected survives the shared codec");
            Check.That(back.Floor.ContainsKey(7), "floor survives the shared codec");
        }

        /// <summary>
        /// A Take entry whose blobs do not cover every accepted&gt;0 slot is
        /// inexact: it restores the ORIGINAL status but totals-only — the client
        /// classifies CommittedTakeDetailsUnavailable and credits nothing.
        /// </summary>
        private static void V2_InexactTakeTotalsOnly()
        {
            RingEntry e = ExactEntry(Tx(7, 1), 7);
            e.Op = TxOp.TakeBatch;
            e.Status = TxStatus.Accepted;
            e.Accepted = new List<int> { 6, 4 };
            e.AcceptedTotal = 10;
            e.TakePayloads = new List<TakePayload> { null, null }; // payloads lost
            Check.That(!TxRing.IsExactEntry(e), "missing take payloads are inexact");
            TxCore core = new TxCore();
            RingData data = new RingData();
            data.Entries.Add(e);
            data.Floor[7] = 1;
            core.LoadRing(data);
            TxResult q = core.Query(e.TxId, 7);
            Check.That(q.Status == TxStatus.Accepted && q.TotalsOnly && q.IsReplay,
                "inexact take keeps status but totals-only, got " + q.Status);
            Check.That(TxResponsePolicy.Classify(q.Status, q.TotalsOnly, TxOp.TakeBatch, 2)
                == TxCompletionKind.CommittedTakeDetailsUnavailable, "no credit, no fabrication");
        }

        private static RingEntry LegacyEntry(long txId, int total, uint rev)
        {
            RingEntry e;
            e.TxId = txId;
            e.AcceptedTotal = total;
            e.Revision = rev;
            e.Op = (TxOp)0;
            e.Status = TxStatus.Duplicate;
            e.Sender = TxIdGen.PeerOf(txId);
            e.Accepted = new List<int> { total };
            e.TakePayloads = null;
            return e;
        }

        private static RingEntry ExactEntry(long txId, long sender)
        {
            RingEntry e;
            e.TxId = txId;
            e.AcceptedTotal = 5;
            e.Revision = 1;
            e.Op = TxOp.Add;
            e.Status = TxStatus.Accepted;
            e.Sender = sender;
            e.Accepted = new List<int> { 5 };
            e.TakePayloads = null;
            return e;
        }
    }
}
