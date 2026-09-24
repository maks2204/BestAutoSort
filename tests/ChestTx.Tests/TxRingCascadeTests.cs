using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Ring-persistence + UnknownTx + cascade regression coverage.
    ///
    /// Exercises the PRODUCTION-SHARED helpers (TxRing.Snapshot,
    /// TxResponsePolicy.Classify/ShouldCascadeToNextChest) — the exact code
    /// the Unity manager calls — plus TxCore.Apply/Query/LoadRing/DumpRing,
    /// which now build the ring through the same TxRing.Snapshot helper.
    ///
    /// v1 conservation gap (documented, ring v2 out of scope): ring entries
    /// carry only aggregate totals. Committed totals-only AddBatch cannot be
    /// attributed (remove nothing, no cascade); committed totals-only Take
    /// cannot credit (payload gone); evicted txs are Indeterminate and need
    /// manual reconciliation.
    /// </summary>
    internal static class TxRingCascadeTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);
        private static readonly ItemKey Stone = new ItemKey(1002, 1, 0, 0);

        public static void RunAll()
        {
            Console.WriteLine("TestA_ImmediateHandoffSurvives");
            TestA_ImmediateHandoffSurvives();
            Console.WriteLine("TestB_PrevAndCurrentPersist");
            TestB_PrevAndCurrentPersist();
            Console.WriteLine("TestC_EvictionIsIndeterminate");
            TestC_EvictionIsIndeterminate();
            Console.WriteLine("TestD_RejectedStaysFailed");
            TestD_RejectedStaysFailed();
            Console.WriteLine("TestE_UnknownTxIndeterminateTerminal");
            TestE_UnknownTxIndeterminateTerminal();
            Console.WriteLine("TestF_IndeterminateStopsCascade");
            TestF_IndeterminateStopsCascade();
            Console.WriteLine("TestG_RealRejectedMayTryNextChest");
            TestG_RealRejectedMayTryNextChest();
            Console.WriteLine("TestH_CommittedTotalsOnlyAddNoCascade");
            TestH_CommittedTotalsOnlyAddNoCascade();
            Console.WriteLine("TestI_CommittedTotalsOnlyTakeNoCredit");
            TestI_CommittedTotalsOnlyTakeNoCredit();
            Console.WriteLine("TestJ_RejectedNeverPersistsAsDuplicate");
            TestJ_RejectedNeverPersistsAsDuplicate();
            Console.WriteLine("TestK_RingHelperFiltersAndCaps");
            TestK_RingHelperFiltersAndCaps();
        }

        /// <summary>
        /// A: the just-committed tx must already be in the ring the moment the
        /// commit returns — an immediate handoff answers the ORIGINAL outcome
        /// (exact, IsReplay) and never re-applies (no double-debit).
        /// </summary>
        private static void TestA_ImmediateHandoffSurvives()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 20, 50);
            TxCore coreA = new TxCore();
            TxRequest take = new TxRequest { TxId = 301, Op = TxOp.TakeBatch };
            take.Items.Add(new TxItem { Key = Wood, Amount = 6, MaxStack = 50 });
            TxResult r1 = coreA.Apply(chestA, take);
            Check.That(r1.Status == TxStatus.Accepted, "commit must be Accepted");

            // Immediate handoff: no further tx before the manager changes.
            bool found = false;
            List<RingEntry> ring = coreA.DumpRing();
            for (int i = 0; i < ring.Count; i++)
                if (ring[i].TxId == 301)
                    found = true;
            Check.That(found, "just-committed tx must already be in the ring");

            TxCore coreB = new TxCore();
            ModelChest chestB = new ModelChest(4, 4);
            chestB.AddItem(Wood, 14, 50);
            coreB.LoadRing(ring);
            TxResult replay = coreB.Apply(chestB, take);
            Check.That(replay.Status == TxStatus.Accepted && !replay.TotalsOnly && replay.IsReplay,
                "immediate-handoff replay must be the original outcome (exact), got " + replay.Status);
            Check.Equal(6, replay.AcceptedTotal(), "exact accepted preserved");
            Check.Equal(14, CountIn(chestB, Wood), "replay must NOT double-debit");
        }

        /// <summary>
        /// B: previous AND current entries persist across the handoff.
        /// </summary>
        private static void TestB_PrevAndCurrentPersist()
        {
            ModelChest chestA = new ModelChest(6, 4);
            chestA.AddItem(Wood, 50, 50);
            TxCore coreA = new TxCore();
            TxRequest first = new TxRequest { TxId = 401, Op = TxOp.TakeBatch };
            first.Items.Add(new TxItem { Key = Wood, Amount = 10, MaxStack = 50 });
            TxRequest second = new TxRequest { TxId = 402, Op = TxOp.TakeBatch };
            second.Items.Add(new TxItem { Key = Wood, Amount = 8, MaxStack = 50 });
            coreA.Apply(chestA, first);
            coreA.Apply(chestA, second);

            TxCore coreB = new TxCore();
            coreB.LoadRing(coreA.DumpRing());
            TxResult q1 = coreB.Query(401);
            TxResult q2 = coreB.Query(402);
            Check.That(q1.Status == TxStatus.Accepted && !q1.TotalsOnly && q1.IsReplay, "prev tx survives handoff with original outcome, got " + q1.Status);
            Check.That(q2.Status == TxStatus.Accepted && !q2.TotalsOnly && q2.IsReplay, "current tx survives handoff with original outcome, got " + q2.Status);
            Check.Equal(10, q1.AcceptedTotal(), "prev total preserved");
            Check.Equal(8, q2.AcceptedTotal(), "current total preserved");
        }

        /// <summary>
        /// C: an evicted tx answers UnknownTx, which classifies Indeterminate
        /// (never FailedNotCommitted) and never cascades.
        /// </summary>
        private static void TestC_EvictionIsIndeterminate()
        {
            ModelChest chest = new ModelChest(64, 64);
            chest.AddItem(Wood, 99999, 50);
            TxCore core = new TxCore();
            long firstId = 1000;
            for (long id = firstId; id < firstId + TxLimits.RingCap + 5; id++)
            {
                TxRequest r = new TxRequest { TxId = id, Op = TxOp.TakeBatch };
                r.Items.Add(new TxItem { Key = Wood, Amount = 1, MaxStack = 50 });
                core.Apply(chest, r);
            }
            // Ring bound bites at handoff: the fresh manager keeps ring-only
            // state (RAM cache is gone), so the ring-evicted tx is UnknownTx.
            TxCore handoff = new TxCore();
            handoff.LoadRing(core.DumpRing());
            TxResult q = handoff.Query(firstId);
            Check.That(q.Status == TxStatus.UnknownTx, "evicted tx must be UnknownTx");
            TxCompletionKind disp = TxResponsePolicy.Classify(q.Status, q.TotalsOnly, TxOp.TakeBatch, -1);
            Check.That(disp == TxCompletionKind.Indeterminate, "evicted UnknownTx must be Indeterminate");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(disp), "evicted tx must not cascade");
        }

        /// <summary>
        /// D: wire Rejected stays FailedNotCommitted (known-not-committed),
        /// with or without totalsOnly.
        /// </summary>
        private static void TestD_RejectedStaysFailed()
        {
            Check.That(TxResponsePolicy.Classify(TxStatus.Rejected, false, TxOp.Add, 1)
                == TxCompletionKind.FailedNotCommitted, "plain Rejected must fail");
            Check.That(TxResponsePolicy.Classify(TxStatus.Rejected, true, TxOp.AddBatch, 3)
                == TxCompletionKind.FailedNotCommitted, "Rejected+totalsOnly must fail");
            Check.That(TxResponsePolicy.Classify(TxStatus.Rejected, true, TxOp.Take, -1)
                == TxCompletionKind.FailedNotCommitted, "Rejected+totalsOnly Take must fail");
        }

        /// <summary>
        /// E: UnknownTx is indeterminate regardless of totalsOnly/op: no removal,
        /// no credit, no cascade — terminal exactly-once by the caller's
        /// CompletePendingTerminal (policy side asserted here).
        /// </summary>
        private static void TestE_UnknownTxIndeterminateTerminal()
        {
            Check.That(TxResponsePolicy.Classify(TxStatus.UnknownTx, false, TxOp.Add, 1)
                == TxCompletionKind.Indeterminate, "UnknownTx must be indeterminate (plain)");
            Check.That(TxResponsePolicy.Classify(TxStatus.UnknownTx, true, TxOp.AddBatch, 2)
                == TxCompletionKind.Indeterminate, "UnknownTx must be indeterminate (totals-only add)");
            Check.That(TxResponsePolicy.Classify(TxStatus.UnknownTx, true, TxOp.TakeBatch, -1)
                == TxCompletionKind.Indeterminate, "UnknownTx must be indeterminate (totals-only take)");
            Check.That(TxResponsePolicy.Classify(TxStatus.UnknownTx, false, TxOp.Take, -1)
                == TxCompletionKind.Indeterminate, "UnknownTx must be indeterminate (plain take)");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.Indeterminate),
                "indeterminate must not cascade/retry");
        }

        /// <summary>
        /// F: QuickStack on Indeterminate sends nothing to chest B — the shared
        /// cascade gate is closed (production SubmitCascade consults the same
        /// ShouldCascadeToNextChest helper).
        /// </summary>
        private static void TestF_IndeterminateStopsCascade()
        {
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.Indeterminate),
                "Indeterminate must close the cascade gate (nothing sent to next chest)");
        }

        /// <summary>
        /// G: a KNOWN Rejected (FailedNotCommitted) may try the next chest:
        /// nothing was committed and source items are untouched (removal happens
        /// only by accepted counts, which are zero here).
        /// </summary>
        private static void TestG_RealRejectedMayTryNextChest()
        {
            Check.That(TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.FailedNotCommitted),
                "known-Rejected may try the next chest (nothing committed, source untouched)");
            Check.That(TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.Normal),
                "Normal completions keep cascading the remainder");
        }

        /// <summary>
        /// H: committed totals-only multi-add: no cascade, no fabrication —
        /// the [1][aggregate] body must never be decoded as item zero.
        /// </summary>
        private static void TestH_CommittedTotalsOnlyAddNoCascade()
        {
            TxCompletionKind disp = TxResponsePolicy.Classify(TxStatus.Duplicate, true, TxOp.AddBatch, 3);
            Check.That(disp == TxCompletionKind.CommittedMultiAddDetailsUnavailable,
                "totals-only multi-add must be details-unavailable");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(disp),
                "totals-only multi-add must not cascade");
        }

        /// <summary>
        /// I: committed totals-only Take: no credit, no fabrication, no cascade.
        /// </summary>
        private static void TestI_CommittedTotalsOnlyTakeNoCredit()
        {
            TxCompletionKind disp = TxResponsePolicy.Classify(TxStatus.Accepted, true, TxOp.TakeBatch, -1);
            Check.That(disp == TxCompletionKind.CommittedTakeDetailsUnavailable,
                "totals-only Take must be details-unavailable");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(disp),
                "totals-only Take must not cascade");
        }

        /// <summary>
        /// J (outcome-stability guard): rejected-then-committed-then-handoff. The
        /// Rejected tx persists AS Rejected, so after handoff it still answers
        /// Rejected (FailedNotCommitted) — never a false Duplicate implying
        /// commitment, and never flipped to Accepted even after the chest gains
        /// the missing stock. The committed tx replays its original outcome.
        /// </summary>
        private static void TestJ_RejectedNeverPersistsAsDuplicate()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 5, 50);
            TxCore coreA = new TxCore();
            TxRequest over = new TxRequest { TxId = 501, Op = TxOp.TakeBatch };
            over.Items.Add(new TxItem { Key = Stone, Amount = 1, MaxStack = 50 });
            TxResult rej = coreA.Apply(chestA, over);
            Check.That(rej.Status == TxStatus.Rejected, "take of absent item must be Rejected");
            TxRequest ok = new TxRequest { TxId = 502, Op = TxOp.TakeBatch };
            ok.Items.Add(new TxItem { Key = Wood, Amount = 3, MaxStack = 50 });
            TxResult acc = coreA.Apply(chestA, ok);
            Check.That(acc.Status == TxStatus.Accepted, "fitting take must be Accepted");

            TxCore coreB = new TxCore();
            coreB.LoadRing(coreA.DumpRing());
            TxResult qrej = coreB.Query(501);
            Check.That(qrej.Status == TxStatus.Rejected,
                "rejected tx must stay Rejected after handoff, got " + qrej.Status);
            Check.That(TxResponsePolicy.Classify(qrej.Status, qrej.TotalsOnly, TxOp.TakeBatch, -1)
                == TxCompletionKind.FailedNotCommitted, "persisted rejection stays failed-not-committed");
            // The chest now HAS the stock, but the same txId must never execute.
            chestA.AddItem(Stone, 10, 50);
            TxRequest retry = new TxRequest { TxId = 501, Op = TxOp.TakeBatch };
            retry.Items.Add(new TxItem { Key = Stone, Amount = 1, MaxStack = 50 });
            TxResult re = coreB.Apply(chestA, retry);
            Check.That(re.Status == TxStatus.Rejected, "same rejected txId must never flip to Accepted, got " + re.Status);
            TxResult qok = coreB.Query(502);
            Check.That(qok.Status == TxStatus.Accepted && !qok.TotalsOnly && qok.IsReplay,
                "committed tx replays original outcome after handoff, got " + qok.Status);
        }

        /// <summary>
        /// K: the shared ring helper itself — keeps every terminal outcome
        /// (Accepted/Partial/Rejected/Duplicate), oldest-first, capped to
        /// RingCap newest survivors.
        /// </summary>
        private static void TestK_RingHelperFiltersAndCaps()
        {
            List<RingSlot> slots = new List<RingSlot>();
            RingSlot a = new RingSlot();
            a.TxId = 1; a.AcceptedTotal = 10; a.Revision = 7; a.Status = TxStatus.Accepted; a.Op = TxOp.Add;
            slots.Add(a);
            RingSlot r = new RingSlot();
            r.TxId = 2; r.AcceptedTotal = 0; r.Revision = 7; r.Status = TxStatus.Rejected; r.Op = TxOp.Take;
            slots.Add(r);
            RingSlot b = new RingSlot();
            b.TxId = 3; b.AcceptedTotal = 4; b.Revision = 8; b.Status = TxStatus.Partial; b.Op = TxOp.Add;
            slots.Add(b);
            List<RingEntry> snap = TxRing.Snapshot(slots);
            Check.Equal(3, snap.Count, "every terminal outcome persists (incl Rejected)");
            Check.Equal(1, (int)snap[0].TxId, "oldest-first order kept");
            Check.That(snap[1].Status == TxStatus.Rejected, "rejected outcome preserved in ring");
            Check.Equal(3, (int)snap[2].TxId, "current entry included");
            Check.Equal(8u, snap[2].Revision, "commit-checkpoint revision carried");

            List<RingSlot> many = new List<RingSlot>();
            for (int i = 0; i < TxLimits.RingCap + 10; i++)
            {
                RingSlot s = new RingSlot();
                s.TxId = 100 + i; s.AcceptedTotal = 1; s.Revision = (uint)i; s.Status = TxStatus.Accepted; s.Op = TxOp.Add;
                many.Add(s);
            }
            List<RingEntry> capped = TxRing.Snapshot(many);
            Check.Equal(TxLimits.RingCap, capped.Count, "ring capped to RingCap");
            Check.Equal(100 + 10, (int)capped[0].TxId, "oldest evicted first");
            Check.Equal(100 + TxLimits.RingCap + 10 - 1, (int)capped[capped.Count - 1].TxId, "newest survives");
        }

        private static int CountIn(ModelChest chest, ItemKey key)
        {
            int n = 0;
            List<int[]> snap = chest.Snapshot();
            for (int i = 0; i < snap.Count; i++)
            {
                int[] e = snap[i];
                if (e[1] == key.Hash && e[2] == key.Quality && e[3] == key.Variant && e[4] == key.World)
                    n += e[5];
            }
            return n;
        }
    }
}
