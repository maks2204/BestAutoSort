using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Issue #10: ChestTX authority (actor-first) + totals-only (status-first).
    /// Pure-policy regression tests through TxResponsePolicy plus TxCore handoff
    /// semantics (ownership migration with local-owner=0 must keep working).
    /// </summary>
    internal static class TxIssue10Tests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);

        public static void RunAll()
        {
            Console.WriteLine("Test14_ActorPath");
            Test14_ActorPath();
            Console.WriteLine("Test15_ResponseDisposition");
            Test15_ResponseDisposition();
            Console.WriteLine("Test16_HandoffTotalsOnly");
            Test16_HandoffTotalsOnly();
            Console.WriteLine("Test17_HandoffNewTxStillApplies");
            Test17_HandoffNewTxStillApplies();
        }

        /// <summary>
        /// Actor-first: a resolvable actor always takes the player path — even
        /// when the sender is also the server peer (listen-server host whose
        /// chest ownership migrated to a remote client). Only a genuinely
        /// actor-less server sender may use dedicated automation.
        /// </summary>
        private static void Test14_ActorPath()
        {
            // Normal remote player: resolved actor, ordinary sender -> player path.
            Check.That(TxResponsePolicy.ClassifyActorPath(true, false) == TxActorPath.Player,
                "resolved remote player must take the player path");
            // KEY (issue #10): listen host -> remote owner. The host IS the server
            // peer, but it also has a real replicated Player body -> player path,
            // not chest-creator automation (which rejected it with player-mismatch).
            Check.That(TxResponsePolicy.ClassifyActorPath(true, true) == TxActorPath.Player,
                "resolved listen-host must take the player path even as server sender");
            // Genuine dedicated automation: no actor (headless), loopback/server
            // sender -> automation path bound to the chest creator.
            Check.That(TxResponsePolicy.ClassifyActorPath(false, true) == TxActorPath.ServerAutomation,
                "actor-less server sender must take the automation path");
            // Spoofed/unknown sender: no actor, not a server sender -> reject.
            // (Claim binding itself — resolved != claimed => player-mismatch —
            // stays in CanUse; the path alone never accepts.)
            Check.That(TxResponsePolicy.ClassifyActorPath(false, false) == TxActorPath.RejectNoActor,
                "unresolvable non-server sender must be rejected (no-actor)");
        }

        /// <summary>
        /// Status-first: Rejected/UnknownTx + totalsOnly is a failure, never
        /// "applied". Committed totals-only Take/multi-add complete loudly with
        /// no item fabrication and no per-item misattribution.
        /// </summary>
        private static void Test15_ResponseDisposition()
        {
            // Rejected + totalsOnly must NOT be reported as applied (was: Take and
            // multi-add branches logged "applied" for any status incl. Duplicate).
            Check.That(TxResponsePolicy.Classify(TxStatus.Rejected, true, TxOp.Take, -1)
                == TxCompletionKind.FailedNotCommitted, "Rejected+totalsOnly Take must fail, not claim applied");
            Check.That(TxResponsePolicy.Classify(TxStatus.Rejected, true, TxOp.AddBatch, 3)
                == TxCompletionKind.FailedNotCommitted, "Rejected+totalsOnly AddBatch must fail, not claim applied");
            // Wire UnknownTx is likewise not-committed: no credit/removal.
            Check.That(TxResponsePolicy.Classify(TxStatus.UnknownTx, true, TxOp.TakeBatch, -1)
                == TxCompletionKind.FailedNotCommitted, "UnknownTx+totalsOnly Take must fail without credit");
            Check.That(TxResponsePolicy.Classify(TxStatus.UnknownTx, true, TxOp.AddBatch, 2)
                == TxCompletionKind.FailedNotCommitted, "UnknownTx+totalsOnly AddBatch must fail without removal");
            // Committed totals-only Take: details unavailable, credit nothing.
            Check.That(TxResponsePolicy.Classify(TxStatus.Accepted, true, TxOp.Take, -1)
                == TxCompletionKind.CommittedTakeDetailsUnavailable, "Accepted+totalsOnly Take must be details-unavailable");
            Check.That(TxResponsePolicy.Classify(TxStatus.Duplicate, true, TxOp.TakeBatch, -1)
                == TxCompletionKind.CommittedTakeDetailsUnavailable, "Duplicate+totalsOnly TakeBatch must be details-unavailable");
            Check.That(TxResponsePolicy.Classify(TxStatus.Partial, true, TxOp.Take, -1)
                == TxCompletionKind.CommittedTakeDetailsUnavailable, "Partial+totalsOnly Take must be details-unavailable");
            // Committed totals-only multi-add: aggregate unattributable — never
            // decode [1][aggregate] as item zero, never auto-cascade/retry.
            Check.That(TxResponsePolicy.Classify(TxStatus.Accepted, true, TxOp.AddBatch, 3)
                == TxCompletionKind.CommittedMultiAddDetailsUnavailable, "Accepted+totalsOnly AddBatch(3) must be details-unavailable");
            Check.That(TxResponsePolicy.Classify(TxStatus.Duplicate, true, TxOp.AddBatch, 2)
                == TxCompletionKind.CommittedMultiAddDetailsUnavailable, "Duplicate+totalsOnly AddBatch(2) must be details-unavailable");
            Check.That(TxResponsePolicy.Classify(TxStatus.Duplicate, true, TxOp.Add, 2)
                == TxCompletionKind.CommittedMultiAddDetailsUnavailable, "Duplicate+totalsOnly multi-item Add must be details-unavailable");
            // Single-add totals-only keeps the safe committed recovery (arity 1:
            // the aggregate IS attributable).
            Check.That(TxResponsePolicy.Classify(TxStatus.Duplicate, true, TxOp.Add, 1)
                == TxCompletionKind.Normal, "Duplicate+totalsOnly single Add must stay Normal");
            Check.That(TxResponsePolicy.Classify(TxStatus.Duplicate, true, TxOp.Move, -1)
                == TxCompletionKind.Normal, "Duplicate+totalsOnly singleton op must stay Normal");
            // Non-totals responses are untouched (existing decode-guard path).
            Check.That(TxResponsePolicy.Classify(TxStatus.Accepted, false, TxOp.TakeBatch, -1)
                == TxCompletionKind.Normal, "non-totals Take must stay Normal");
            Check.That(TxResponsePolicy.Classify(TxStatus.Rejected, false, TxOp.Add, 1)
                == TxCompletionKind.FailedNotCommitted, "plain Rejected must fail without credit/removal");
            Check.That(TxResponsePolicy.Classify(TxStatus.UnknownTx, false, TxOp.AddBatch, 2)
                == TxCompletionKind.FailedNotCommitted, "plain UnknownTx must fail without credit/removal");
        }

        /// <summary>
        /// Ownership handoff with local-owner=0: the new manager seeds from the
        /// ring; a replayed Take returns Duplicate totals-only WITHOUT re-applying
        /// (no double-debit). Treating local-owner=0 as an error would contradict
        /// the handoff architecture.
        /// </summary>
        private static void Test16_HandoffTotalsOnly()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 20, 50);
            TxCore coreA = new TxCore();
            TxRequest take = new TxRequest { TxId = 101, Op = TxOp.TakeBatch };
            take.Items.Add(new TxItem { Key = Wood, Amount = 6, MaxStack = 50 });
            TxResult r1 = coreA.Apply(chestA, take);
            Check.That(r1.Status == TxStatus.Accepted, "initial take must be Accepted");
            Check.Equal(14, CountIn(chestA, Wood), "chest holds 14 after take of 6");

            // Handoff: new manager (local owner id 0 — normal, not an error) loads
            // only the persisted ring, then sees the same txId again (retry/Query).
            TxCore coreB = new TxCore();
            coreB.LoadRing(coreA.DumpRing());
            TxResult q = coreB.Query(101);
            Check.That(q.Status == TxStatus.Duplicate && q.TotalsOnly,
                "handoff Query of committed take must be Duplicate totals-only");

            ModelChest chestB = new ModelChest(4, 4);
            chestB.AddItem(Wood, 14, 50);
            TxResult replay = coreB.Apply(chestB, take);
            Check.That(replay.Status == TxStatus.Duplicate, "replayed take after handoff must be Duplicate");
            Check.Equal(14, CountIn(chestB, Wood), "replayed take must NOT double-debit the chest");
        }

        /// <summary>
        /// After a handoff the new manager still applies fresh transactions
        /// exactly once (handoff enables, not blocks, the new owner).
        /// </summary>
        private static void Test17_HandoffNewTxStillApplies()
        {
            ModelChest chestA = new ModelChest(4, 4);
            chestA.AddItem(Wood, 20, 50);
            TxCore coreA = new TxCore();
            TxRequest first = new TxRequest { TxId = 201, Op = TxOp.TakeBatch };
            first.Items.Add(new TxItem { Key = Wood, Amount = 6, MaxStack = 50 });
            coreA.Apply(chestA, first);

            TxCore coreB = new TxCore();
            coreB.LoadRing(coreA.DumpRing());
            ModelChest chestB = new ModelChest(4, 4);
            chestB.AddItem(Wood, 14, 50);
            TxRequest fresh = new TxRequest { TxId = 202, Op = TxOp.TakeBatch };
            fresh.Items.Add(new TxItem { Key = Wood, Amount = 4, MaxStack = 50 });
            TxResult r = coreB.Apply(chestB, fresh);
            Check.That(r.Status == TxStatus.Accepted, "fresh tx after handoff must be Accepted");
            Check.Equal(10, CountIn(chestB, Wood), "fresh take applies exactly once after handoff");
            TxResult dup = coreB.Apply(chestB, fresh);
            Check.That(dup.Status == TxStatus.Duplicate, "repeat of fresh tx must be Duplicate");
            Check.Equal(10, CountIn(chestB, Wood), "repeat must not re-apply");
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
