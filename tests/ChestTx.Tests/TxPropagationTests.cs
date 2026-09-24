using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Propagation-request sequencing (v0.5.x corrective pass). Pins the order the
    /// offline core shares with production through the TxDurability seam:
    /// fence (floor write) -&gt; propagation-request AfterFence -&gt; Execute/save -&gt;
    /// ring + floor rewrite -&gt; propagation-request AfterCommit. Quarantined txIds
    /// seed a durable Rejected (ring + floor rewrite) + propagate AfterFence with
    /// NO execute. Propagation is best-effort
    /// only (never an ACK/barrier, never correctness): a throwing hook is
    /// swallowed and changes no outcome.
    ///
    /// Production mapping (ChestTxService, Unity-coupled, NOT runnable here):
    /// AfterFence fires in ApplyJob between TryPersistExecutionFence and Execute;
    /// AfterCommit fires in CommitResult after the ring + floor rewrite; the
    /// recipient collector (sender + viewers + server, deduped — never
    /// Viewers-only) is observed live via the propagation-request log line
    /// (docs/chesttx_runtime_verification.md O1). What the suite PROVES is the
    /// firing order + the never-affects-outcome contract.
    /// </summary>
    internal static class TxPropagationTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);

        private static long Tx(long peer, uint ctr)
        {
            return unchecked((peer << 32) | ctr);
        }

        private static TxRequest AddReq(long txId, long sender, ItemKey key, int amount)
        {
            TxRequest r = new TxRequest();
            r.TxId = txId;
            r.Op = TxOp.Add;
            r.Sender = sender;
            r.Items.Add(new TxItem { Key = key, Amount = amount, MaxStack = 50 });
            return r;
        }

        /// <summary>Order-logging durable world: every seam call appends its tag.</summary>
        private sealed class OrderWorld
        {
            public readonly List<string> Order = new List<string>();
            public List<int[]> Items = new List<int[]>();
            public bool FailSave;
            public bool FailReload;
            public bool ThrowOnPropagate;
            private int _floors;

            public TxDurability Hooks()
            {
                TxDurability d = new TxDurability();
                d.WriteFloor = delegate (byte[] bytes)
                {
                    _floors++;
                    Order.Add("floor#" + _floors);
                    return true;
                };
                d.WriteRing = delegate (byte[] bytes)
                {
                    Order.Add("ring");
                    return true;
                };
                d.SaveItems = delegate (ModelChest chest)
                {
                    Order.Add("save");
                    if (FailSave)
                        return false;
                    Items = CloneItems(chest.Snapshot());
                    return true;
                };
                d.ReloadItems = delegate (ModelChest chest)
                {
                    Order.Add("reload");
                    if (FailReload)
                        return false;
                    chest.Restore(CloneItems(Items));
                    return true;
                };
                d.IsOwner = delegate () { return true; };
                d.Crash = null;
                d.PropagationRequest = delegate (TxPropagationPoint point)
                {
                    Order.Add("prop:" + point);
                    if (ThrowOnPropagate)
                        throw new InvalidOperationException("propagation must never affect the outcome");
                };
                return d;
            }

            public static List<int[]> CloneItems(List<int[]> src)
            {
                List<int[]> dst = new List<int[]>(src.Count);
                for (int i = 0; i < src.Count; i++)
                    dst.Add((int[])src[i].Clone());
                return dst;
            }

            public string OrderLine()
            {
                return string.Join(">", Order.ToArray());
            }
        }

        public static void RunAll()
        {
            Console.WriteLine("PROPAGATION_FenceBeforeExecuteOrder");
            PROPAGATION_FenceBeforeExecuteOrder();
            Console.WriteLine("PROPAGATION_QuarantineFencePropagatesWithoutExecute");
            PROPAGATION_QuarantineFencePropagatesWithoutExecute();
            Console.WriteLine("PROPAGATION_ThrowingHookNeverAffectsOutcome");
            PROPAGATION_ThrowingHookNeverAffectsOutcome();
        }

        /// <summary>
        /// Commit order: fence floor write, THEN propagation-request AfterFence,
        /// THEN execute/save, THEN ring + floor rewrite, THEN propagation-request
        /// AfterCommit. The AfterFence push lands before Execute so peers learn the
        /// high-water even if the mutation later fails.
        /// </summary>
        private static void PROPAGATION_FenceBeforeExecuteOrder()
        {
            OrderWorld w = new OrderWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            TxResult r = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5));
            Check.That(r.Status == TxStatus.Accepted, "commit, got " + r.Status);
            Check.That(w.OrderLine() == "floor#1>prop:AfterFence>save>ring>floor#2>prop:AfterCommit",
                "fence>propagate>execute/save>ring/floor>propagate, got " + w.OrderLine());
            Check.Equal(5, chest.TotalOf(Wood), "exactly once");
        }

        /// <summary>
        /// Quarantined fresh tx: the durable refusal rewrites ring + floor and fires
        /// propagation-request AfterFence — but NOTHING else (no save, no second
        /// ring/floor rewrite, no AfterCommit): peers learn the high-water, the
        /// txId is refused stable-Rejected, nothing executes. A throwing hook
        /// changes none of that (best-effort, swallowed).
        /// </summary>
        private static void PROPAGATION_QuarantineFencePropagatesWithoutExecute()
        {
            OrderWorld w = new OrderWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");

            w.Order.Clear();
            TxResult fresh = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(fresh.Status == TxStatus.Rejected && !fresh.IsReplay,
                "quarantined fresh tx refused durable-Rejected, got " + fresh.Status);
            Check.That(w.OrderLine() == "ring>floor#4>prop:AfterFence",
                "quarantine persists ring + floor then propagates AfterFence, got " + w.OrderLine());
            Check.Equal(3, w.Order.Count, "exactly ring + floor + propagate, got " + w.OrderLine());
            Check.Equal(10, chest.TotalOf(Wood), "quarantined tx executes nothing (live holds only the failed-tx dirt)");
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 3,
                "quarantined tx fenced (floor 11->3)");
        }

        /// <summary>
        /// Propagation is best-effort: a throwing propagation hook is swallowed —
        /// commits still commit, quarantined fences still fence, outcomes unchanged.
        /// </summary>
        private static void PROPAGATION_ThrowingHookNeverAffectsOutcome()
        {
            OrderWorld w = new OrderWorld();
            w.ThrowOnPropagate = true;
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            TxResult r = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5));
            Check.That(r.Status == TxStatus.Accepted && !r.IsReplay,
                "commit survives a throwing propagation hook, got " + r.Status);
            Check.Equal(5, chest.TotalOf(Wood), "exactly once");

            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");
            TxResult fresh = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(fresh.Status == TxStatus.Rejected && !fresh.IsReplay,
                "quarantine refusal survives a throwing propagation hook, got " + fresh.Status);
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 3,
                "fence landed despite the throwing hook");
        }
    }
}
