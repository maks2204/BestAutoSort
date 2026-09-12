using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Minimal assertion framework with no external dependencies.
    /// </summary>
    internal static class Check
    {
        public static int Failures;

        public static void That(bool cond, string message)
        {
            if (!cond)
            {
                Failures++;
                Console.WriteLine("  FAIL: " + message);
            }
        }

        public static void Equal(long expected, long actual, string message)
        {
            if (expected != actual)
            {
                Failures++;
                Console.WriteLine("  FAIL: " + message + " (expected " + expected + ", got " + actual + ")");
            }
        }
    }

    /// <summary>
    /// Test actor: own txId counter, own "player inventory" (model).
    /// </summary>
    internal sealed class Actor
    {
        public readonly long PeerId;
        private uint _counter = 1;
        public readonly ModelChest Bag;

        public Actor(long peerId, int w, int h)
        {
            PeerId = peerId;
            Bag = new ModelChest(w, h);
        }

        public TxRequest NewRequest(TxOp op)
        {
            return new TxRequest { TxId = TxIdGen.Next(PeerId, ref _counter), Op = op };
        }
    }

    internal static class TxTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);
        private static readonly ItemKey Stone = new ItemKey(1002, 1, 0, 0);
        private static readonly ItemKey Coal = new ItemKey(1003, 1, 0, 0);

        private static TxItem Lot(ItemKey key, int amount, int maxStack)
        {
            return new TxItem { Key = key, Amount = amount, MaxStack = maxStack };
        }

        // Client side of Add: remove exactly accepted from own bag.
        private static void ClientApplyAdd(Actor a, TxItem req, int accepted)
        {
            int removed = a.Bag.TakeItem(req.Key, accepted);
            Check.Equal(accepted, removed, "client must hold what manager accepted");
        }

        // Client side of Take: put what was received into own bag.
        private static void ClientApplyTake(Actor a, ItemKey key, int accepted, int maxStack)
        {
            int placed = a.Bag.AddItem(key, accepted, maxStack);
            Check.Equal(accepted, placed, "take result must fit test bag");
        }

        private static long Total(ModelChest chest, params Actor[] actors)
        {
            long sum = chest.GrandTotal();
            foreach (Actor a in actors)
                sum += a.Bag.GrandTotal();
            return sum;
        }

        public static void Test1_SimultaneousAdd()
        {
            Console.WriteLine("Test1 simultaneous add");
            ModelChest chest = new ModelChest(6, 4);
            TxCore core = new TxCore();
            Actor a = new Actor(11, 6, 4);
            Actor b = new Actor(22, 6, 4);
            a.Bag.AddItem(Wood, 50, 50);
            b.Bag.AddItem(Wood, 50, 50);

            TxRequest ra = a.NewRequest(TxOp.Add);
            ra.Items.Add(Lot(Wood, 50, 50));
            TxRequest rb = b.NewRequest(TxOp.Add);
            rb.Items.Add(Lot(Wood, 50, 50));

            TxResult resa = null, resb = null;
            Parallel.Invoke(
                delegate { resa = core.Apply(chest, ra); },
                delegate { resb = core.Apply(chest, rb); });
            ClientApplyAdd(a, ra.Items[0], resa.AcceptedTotal());
            ClientApplyAdd(b, rb.Items[0], resb.AcceptedTotal());

            Check.Equal(100, chest.TotalOf(Wood), "chest must hold 100 wood");
            Check.Equal(100, Total(chest, a, b), "conservation");
        }

        public static void Test2_SimultaneousRemove()
        {
            Console.WriteLine("Test2 simultaneous remove");
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 50, 50);
            TxCore core = new TxCore();
            Actor a = new Actor(11, 6, 4);
            Actor b = new Actor(22, 6, 4);

            TxRequest ra = a.NewRequest(TxOp.Take);
            ra.Items.Add(Lot(Wood, 40, 50));
            TxRequest rb = b.NewRequest(TxOp.Take);
            rb.Items.Add(Lot(Wood, 40, 50));

            TxResult resa = null, resb = null;
            Parallel.Invoke(
                delegate { resa = core.Apply(chest, ra); },
                delegate { resb = core.Apply(chest, rb); });
            int gotA = resa.AcceptedTotal(), gotB = resb.AcceptedTotal();
            ClientApplyTake(a, Wood, gotA, 50);
            ClientApplyTake(b, Wood, gotB, 50);

            Check.Equal(50, gotA + gotB, "A+B must equal 50, got " + gotA + "+" + gotB);
            Check.Equal(0, chest.TotalOf(Wood), "chest must be empty");
            Check.Equal(50, Total(chest, a, b), "conservation");
        }

        public static void Test3_SameItemRace1000()
        {
            Console.WriteLine("Test3 same-item race x1000");
            for (int iter = 0; iter < 1000; iter++)
            {
                ModelChest chest = new ModelChest(6, 4);
                chest.AddItem(Wood, 50, 50);
                TxCore core = new TxCore();
                Actor a = new Actor(11, 6, 4);
                Actor b = new Actor(22, 6, 4);
                TxRequest ra = a.NewRequest(TxOp.Take);
                ra.Items.Add(Lot(Wood, 40, 50));
                TxRequest rb = b.NewRequest(TxOp.Take);
                rb.Items.Add(Lot(Wood, 40, 50));
                TxResult resa = null, resb = null;
                try
                {
                    Parallel.Invoke(
                        delegate { resa = core.Apply(chest, ra); },
                        delegate { resb = core.Apply(chest, rb); });
                }
                catch (Exception ex)
                {
                    Check.That(false, "iter " + iter + " threw: " + ex.GetType().Name);
                    return;
                }
                int sum = resa.AcceptedTotal() + resb.AcceptedTotal();
                if (sum != 50 || chest.TotalOf(Wood) != 0)
                {
                    Check.That(false, "iter " + iter + ": sum=" + sum + " chest=" + chest.TotalOf(Wood));
                    return;
                }
                ClientApplyTake(a, Wood, resa.AcceptedTotal(), 50);
                ClientApplyTake(b, Wood, resb.AcceptedTotal(), 50);
                if (Total(chest, a, b) != 50)
                {
                    Check.That(false, "iter " + iter + ": conservation broken");
                    return;
                }
            }
            Console.WriteLine("  1000 iters, no dupe/loss/negative/exception");
        }

        public static void Test4_PartialCapacity()
        {
            Console.WriteLine("Test4 partial capacity");
            // Chest: 1 cell for Wood capped at 17 — exactly 17 of 50 fits.
            ModelChest chest = new ModelChest(1, 1);
            Actor a = new Actor(11, 6, 4);
            a.Bag.AddItem(Wood, 50, 50);
            TxCore core = new TxCore();
            TxRequest r = a.NewRequest(TxOp.Add);
            r.Items.Add(Lot(Wood, 50, 17));
            TxResult res = core.Apply(chest, r);
            Check.That(res.Status == TxStatus.Partial, "must be Partial, got " + res.Status);
            Check.Equal(17, res.AcceptedTotal(), "accepted 17");
            ClientApplyAdd(a, r.Items[0], res.AcceptedTotal());
            Check.Equal(33, a.Bag.TotalOf(Wood), "player remains 33");
            Check.Equal(17, chest.TotalOf(Wood), "chest +17");
            Check.Equal(50, Total(chest, a), "conservation");
        }

        public static void Test5_DuplicatedRpc()
        {
            Console.WriteLine("Test5 duplicated RPC");
            ModelChest chest = new ModelChest(6, 4);
            TxCore core = new TxCore();
            Actor a = new Actor(11, 6, 4);
            a.Bag.AddItem(Wood, 50, 50);
            TxRequest r = a.NewRequest(TxOp.Add);
            r.Items.Add(Lot(Wood, 50, 50));
            TxResult first = core.Apply(chest, r);
            TxResult second = core.Apply(chest, r); // redelivery of the same txId
            Check.That(second.Status == TxStatus.Duplicate, "second must be Duplicate");
            Check.Equal(first.AcceptedTotal(), second.AcceptedTotal(), "duplicate returns cached total");
            Check.Equal(50, chest.TotalOf(Wood), "applied exactly once");
            ClientApplyAdd(a, r.Items[0], first.AcceptedTotal());
            Check.Equal(50, Total(chest, a), "conservation");
        }

        public static void Test6_ReorderedRpc()
        {
            Console.WriteLine("Test6 reordered RPC");
            ModelChest chest = new ModelChest(6, 4);
            TxCore core = new TxCore();
            Actor a = new Actor(11, 6, 4);
            a.Bag.AddItem(Wood, 50, 50);
            a.Bag.AddItem(Stone, 30, 50);
            // TX2 clocked before TX1: both serialize in arrival order, stale baseRev does not reject.
            TxRequest r1 = a.NewRequest(TxOp.Add);
            r1.Items.Add(Lot(Wood, 50, 50));
            r1.BaseRevision = 0;
            TxRequest r2 = a.NewRequest(TxOp.Add);
            r2.Items.Add(Lot(Stone, 30, 50));
            r2.BaseRevision = 0;
            TxResult res2 = core.Apply(chest, r2); // arrived first
            TxResult res1 = core.Apply(chest, r1); // arrived second, baseRev stale
            Check.That(res1.Status == TxStatus.Accepted, "stale baseRev still applied, got " + res1.Status);
            ClientApplyAdd(a, r1.Items[0], res1.AcceptedTotal());
            ClientApplyAdd(a, r2.Items[0], res2.AcceptedTotal());
            Check.Equal(80, chest.GrandTotal(), "both applied");
            Check.Equal(80, Total(chest, a), "conservation");
        }

        public static void Test7_DisconnectDuringTx()
        {
            Console.WriteLine("Test7 disconnect during tx");
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 50, 50);
            TxCore core = new TxCore();
            Actor a = new Actor(11, 6, 4);
            // Case 1: commit happened, response lost → Query returns the cache.
            TxRequest r = a.NewRequest(TxOp.Take);
            r.Items.Add(Lot(Wood, 40, 50));
            TxResult committed = core.Apply(chest, r);
            TxResult queried = core.Query(r.TxId); // client re-asks instead of re-applying
            Check.That(queried.Status == TxStatus.Duplicate, "query returns cached");
            Check.Equal(committed.AcceptedTotal(), queried.AcceptedTotal(), "query total matches");
            ClientApplyTake(a, Wood, queried.AcceptedTotal(), 50);
            Check.Equal(50, Total(chest, a), "conservation after lost response");
            // Case 2: nothing happened before commit → Query UnknownTx, items stay with the client.
            TxRequest r2 = a.NewRequest(TxOp.Take);
            r2.Items.Add(Lot(Wood, 40, 50));
            TxResult q2 = core.Query(r2.TxId);
            Check.That(q2.Status == TxStatus.UnknownTx, "never-applied tx is unknown");
            Check.Equal(10, chest.TotalOf(Wood), "chest untouched");
        }

        public static void Test8_ManagerDisconnect()
        {
            Console.WriteLine("Test8 manager disconnect / handoff");
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 50, 50);
            chest.AddItem(Stone, 20, 50);
            TxCore core1 = new TxCore();
            Actor a = new Actor(11, 6, 4);
            TxRequest r = a.NewRequest(TxOp.Take);
            r.Items.Add(Lot(Wood, 40, 50));
            TxResult res = core1.Apply(chest, r);
            uint revBefore = core1.Revision;
            // Handoff: the new manager loads the latest committed state + the ring.
            ModelChest chest2 = new ModelChest(6, 4);
            chest2.Restore(chest.Snapshot());
            TxCore core2 = new TxCore();
            core2.LoadRing(core1.DumpRing());
            Check.Equal(revBefore, core2.Revision, "revision continues after handoff");
            // Same txId retried by the new manager: no re-apply.
            TxResult dup = core2.Apply(chest2, r);
            Check.That(dup.Status == TxStatus.Duplicate, "handoff duplicate, got " + dup.Status);
            Check.Equal(res.AcceptedTotal(), dup.AcceptedTotal(), "handoff totals match");
            Check.Equal(chest.TotalOf(Wood), chest2.TotalOf(Wood), "state identical");
            ClientApplyTake(a, Wood, res.AcceptedTotal(), 50);
            Check.Equal(70, Total(chest2, a), "conservation (50 wood + 20 stone)");
            // The ring survives serialization.
            byte[] bytes = TxCore.EncodeRing(core1.DumpRing());
            List<RingEntry> back = TxCore.DecodeRing(bytes);
            Check.Equal(core1.DumpRing().Count, back.Count, "ring round-trip");
        }

        public static void Test9_QuickStackConcurrency()
        {
            Console.WriteLine("Test9 quick-stack concurrency");
            for (int iter = 0; iter < 50; iter++)
            {
                ModelChest chest = new ModelChest(6, 4);
                chest.AddItem(Wood, 10, 50);
                TxCore core = new TxCore();
                Actor a = new Actor(11, 6, 4);
                Actor b = new Actor(22, 6, 4);
                a.Bag.AddItem(Wood, 50, 50);
                b.Bag.AddItem(Wood, 50, 50);
                TxRequest ra = a.NewRequest(TxOp.AddBatch);
                ra.Items.Add(Lot(Wood, 50, 50));
                TxRequest rb = b.NewRequest(TxOp.AddBatch);
                rb.Items.Add(Lot(Wood, 50, 50));
                TxResult resa = null, resb = null;
                Parallel.Invoke(
                    delegate { resa = core.Apply(chest, ra); },
                    delegate { resb = core.Apply(chest, rb); });
                ClientApplyAdd(a, ra.Items[0], resa.AcceptedTotal());
                ClientApplyAdd(b, rb.Items[0], resb.AcceptedTotal());
                // Per-type invariant: before(10+50+50) == after.
                Check.Equal(110, Total(chest, a, b), "iter " + iter + " per-type conservation");
                Check.Equal(110, chest.TotalOf(Wood) + a.Bag.TotalOf(Wood) + b.Bag.TotalOf(Wood), "iter " + iter);
            }
        }

        public static void Test10_ManualMovePlusQuickStack()
        {
            Console.WriteLine("Test10 manual move + quick stack");
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            Actor a = new Actor(11, 6, 4);
            Actor b = new Actor(22, 6, 4);
            b.Bag.AddItem(Stone, 30, 50);
            // A manually moves Wood inside the chest, B quick-stacks Stone.
            TxRequest mv = a.NewRequest(TxOp.Move);
            mv.Items.Add(new TxItem { Key = Wood, Amount = 20, MaxStack = 50, X = 0, Y = 0 });
            mv.DstX = 3;
            mv.DstY = 2;
            TxRequest qs = b.NewRequest(TxOp.Add);
            qs.Items.Add(Lot(Stone, 30, 50));
            TxResult r1 = null, r2 = null;
            Parallel.Invoke(
                delegate { r1 = core.Apply(chest, mv); },
                delegate { r2 = core.Apply(chest, qs); });
            Check.That(r1.Status == TxStatus.Accepted || r1.Status == TxStatus.Rejected, "move decided, got " + r1.Status);
            ClientApplyAdd(b, qs.Items[0], r2.AcceptedTotal());
            Check.Equal(50, Total(chest, a, b), "no dupe/loss");
        }

        public static void Test11_AutoPullPlusPlayer()
        {
            Console.WriteLine("Test11 auto-pull + player op");
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Coal, 30, 50);
            TxCore core = new TxCore();
            Actor auto = new Actor(99, 6, 4);
            Actor player = new Actor(11, 6, 4);
            TxRequest pull = auto.NewRequest(TxOp.Take);
            pull.Items.Add(Lot(Coal, 20, 50));
            TxRequest take = player.NewRequest(TxOp.Take);
            take.Items.Add(Lot(Coal, 20, 50));
            TxResult r1 = null, r2 = null;
            Parallel.Invoke(
                delegate { r1 = core.Apply(chest, pull); },
                delegate { r2 = core.Apply(chest, take); });
            ClientApplyTake(auto, Coal, r1.AcceptedTotal(), 50);
            ClientApplyTake(player, Coal, r2.AcceptedTotal(), 50);
            Check.Equal(30, r1.AcceptedTotal() + r2.AcceptedTotal(), "auto+player share 30");
            Check.Equal(30, Total(chest, auto, player), "conservation");
        }

        public static void Test12_ThreePlayerSoak()
        {
            Console.WriteLine("Test12 three-player soak");
            Random rng = new Random(12345);
            ItemKey[] keys = new ItemKey[] { Wood, Stone, Coal };
            ModelChest chest = new ModelChest(8, 6);
            chest.AddItem(Wood, 100, 50);
            chest.AddItem(Stone, 60, 50);
            Actor[] actors = new Actor[]
            {
                new Actor(11, 8, 6), new Actor(22, 8, 6), new Actor(33, 8, 6)
            };
            actors[0].Bag.AddItem(Wood, 40, 50);
            actors[1].Bag.AddItem(Stone, 40, 50);
            actors[2].Bag.AddItem(Coal, 40, 50);
            long spawned = 280; // 100 wood + 60 stone in the chest, 40x3 in bags
            Check.Equal(spawned, Total(chest, actors[0], actors[1], actors[2]), "initial total");
            TxCore core = new TxCore();
            object rngGate = new object();
            int opsPerActor = 200;
            Parallel.For(0, actors.Length, delegate (int ai)
            {
                Actor a = actors[ai];
                for (int i = 0; i < opsPerActor; i++)
                {
                    ItemKey key;
                    int kind, amount;
                    lock (rngGate)
                    {
                        key = keys[rng.Next(keys.Length)];
                        kind = rng.Next(4);
                        amount = rng.Next(1, 51);
                    }
                    if (kind == 0)
                    {
                        // Add from own bag (only what is there).
                        int have = a.Bag.TotalOf(key);
                        if (have == 0)
                            continue;
                        int n = Math.Min(have, amount);
                        TxRequest r = a.NewRequest(TxOp.Add);
                        r.Items.Add(Lot(key, n, 50));
                        TxResult res = core.Apply(chest, r);
                        ClientApplyAdd(a, r.Items[0], res.AcceptedTotal());
                    }
                    else if (kind == 1)
                    {
                        TxRequest r = a.NewRequest(TxOp.Take);
                        r.Items.Add(Lot(key, amount, 50));
                        TxResult res = core.Apply(chest, r);
                        ClientApplyTake(a, key, res.AcceptedTotal(), 50);
                    }
                    else if (kind == 2)
                    {
                        TxRequest r = a.NewRequest(TxOp.Move);
                        r.Items.Add(new TxItem { Key = key, Amount = amount, MaxStack = 50, X = -1, Y = -1 });
                        lock (rngGate)
                        {
                            r.DstX = rng.Next(8);
                            r.DstY = rng.Next(6);
                        }
                        core.Apply(chest, r);
                    }
                    else
                    {
                        TxRequest r = a.NewRequest(TxOp.AddBatch);
                        r.Items.Add(Lot(Wood, Math.Min(a.Bag.TotalOf(Wood), 10), 50));
                        r.Items.Add(Lot(Stone, Math.Min(a.Bag.TotalOf(Stone), 10), 50));
                        TxResult res = core.Apply(chest, r);
                        for (int k = 0; k < r.Items.Count; k++)
                            ClientApplyAdd(a, r.Items[k], res.Accepted[k]);
                    }
                }
            });
            // Conservation: no spawning or consumption during the soak.
            Check.Equal(spawned, Total(chest, actors[0], actors[1], actors[2]), "soak conservation");
            Console.WriteLine("  600 ops across 3 actors, total=" + Total(chest, actors[0], actors[1], actors[2]));
        }
    }
}
