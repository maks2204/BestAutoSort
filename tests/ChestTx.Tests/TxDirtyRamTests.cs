using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Dirty-RAM leakage: post-fence Execute/save failures must never leave speculative
    /// inventory live. Same-process recovery (no restart): TxCore restores the live
    /// ModelChest from the PERSISTED snapshot through the ReloadItems seam, or quarantines
    /// fail-closed when persisted data is unavailable. The fence is kept (never rolls back),
    /// nothing is cached for the ambiguous tx (no exact ring result), the client stays
    /// Indeterminate (no auto-reissue), and quarantine clears only via TryRecover.
    ///
    /// Per-op audit (mirrors the production audit on RecoverPostFenceFailure):
    /// Add/AddBatch/Take/TakeBatch/Move/Sort mutate RAM inventory and are covered by reload
    /// here (Add/Take/partial-batch below; Move/Sort share the same Execute-then-save path).
    /// Upgrade consumes resources and replaces the chest object; SetRule writes a separate ZDO
    /// key — inventory reload cannot undo those non-inventory effects (residual gaps,
    /// documented in production, not claimed as solved by these tests).
    ///
    /// Residual gap proven by DIRTYRAM_PostWriteThrow: a save that writes and THEN throws is
    /// durable while the answer stays Indeterminate (committed-but-Indeterminate, needs manual
    /// reconciliation — at-most-once, never exactly-once in that window).
    /// </summary>
    internal static class TxDirtyRamTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);
        private static readonly ItemKey Stone = new ItemKey(1002, 1, 0, 0);

        /// <summary>Same-process fake durable world: persisted items + seams, no restart.</summary>
        private sealed class DirtyWorld
        {
            public byte[] RingBytes;
            public byte[] FloorBytes;
            public List<int[]> Items = new List<int[]>();
            public readonly int W;
            public readonly int H;
            public bool FailSave;
            public bool FailReload;
            public bool ThrowAfterSave;
            public readonly HashSet<long> FailExecuteIds = new HashSet<long>();

            public DirtyWorld(int w, int h)
            {
                W = w;
                H = h;
            }

            public TxDurability Hooks()
            {
                TxDurability d = new TxDurability();
                d.WriteFloor = delegate (byte[] bytes)
                {
                    FloorBytes = (byte[])bytes.Clone();
                    return true;
                };
                d.WriteRing = delegate (byte[] bytes)
                {
                    RingBytes = (byte[])bytes.Clone();
                    return true;
                };
                d.SaveItems = delegate (ModelChest chest)
                {
                    if (FailSave)
                        return false;
                    if (ThrowAfterSave)
                    {
                        // Post-write throw: the write LANDED, then the save threw.
                        // Ambiguous commit — reload must see the written value.
                        Items = CloneItems(chest.Snapshot());
                        throw new InvalidOperationException("injected post-write save throw");
                    }
                    Items = CloneItems(chest.Snapshot());
                    return true;
                };
                d.ReloadItems = delegate (ModelChest chest)
                {
                    if (FailReload)
                        return false;
                    chest.Restore(CloneItems(Items));
                    return true;
                };
                d.FailExecute = delegate (TxRequest req)
                {
                    return FailExecuteIds.Contains(req.TxId);
                };
                d.IsOwner = delegate () { return true; };
                d.Crash = null;
                return d;
            }

            public static List<int[]> CloneItems(List<int[]> src)
            {
                List<int[]> dst = new List<int[]>(src.Count);
                for (int i = 0; i < src.Count; i++)
                    dst.Add((int[])src[i].Clone());
                return dst;
            }

            public int PersistedTotal(ItemKey key)
            {
                int sum = 0;
                for (int i = 0; i < Items.Count; i++)
                {
                    int[] e = Items[i];
                    if (e[1] == key.Hash && e[2] == key.Quality && e[3] == key.Variant && e[4] == key.World)
                        sum += e[5];
                }
                return sum;
            }
        }

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

        private static TxRequest TakeReq(long txId, long sender, ItemKey key, int amount)
        {
            TxRequest r = new TxRequest();
            r.TxId = txId;
            r.Op = TxOp.Take;
            r.Sender = sender;
            r.Items.Add(new TxItem { Key = key, Amount = amount, MaxStack = 50 });
            return r;
        }

        public static void RunAll()
        {
            Console.WriteLine("DIRTYRAM_AddLeakage_PreWriteSaveFail");
            DIRTYRAM_AddLeakage_PreWriteSaveFail();
            Console.WriteLine("DIRTYRAM_TakeLeakage_PreWriteSaveFail");
            DIRTYRAM_TakeLeakage_PreWriteSaveFail();
            Console.WriteLine("DIRTYRAM_PartialExecute");
            DIRTYRAM_PartialExecute();
            Console.WriteLine("DIRTYRAM_Quarantine_ReloadFailAndQueuedJobs");
            DIRTYRAM_Quarantine_ReloadFailAndQueuedJobs();
            Console.WriteLine("DIRTYRAM_PostWriteThrow_CommittedButIndeterminate");
            DIRTYRAM_PostWriteThrow_CommittedButIndeterminate();
        }

        /// <summary>
        /// Pre-write save failure: Add 5 commits, Add 10 fails the save. Same-process live
        /// RAM must equal persisted (5, not 15) — the speculative +10 never leaks into the
        /// next tx. A fresh tx then commits exactly once.
        /// </summary>
        private static void DIRTYRAM_AddLeakage_PreWriteSaveFail()
        {
            DirtyWorld w = new DirtyWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            TxResult first = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5));
            Check.That(first.Status == TxStatus.Accepted, "first add commits, got " + first.Status);
            Check.Equal(5, chest.TotalOf(Wood), "live 5 after first add");
            Check.Equal(5, w.PersistedTotal(Wood), "persisted 5 after first add");

            w.FailSave = true;
            TxResult failed = core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 10));
            Check.That(failed.Status == TxStatus.UnknownTx, "save failure is indeterminate, got " + failed.Status);
            Check.Equal(5, chest.TotalOf(Wood), "live restored: speculative +10 rolled back, got " + chest.TotalOf(Wood));
            Check.Equal(5, w.PersistedTotal(Wood), "persisted stays 5, not 15");
            Check.That(!core.Quarantined, "clean save-fail with working reload does not quarantine");

            // Same txId retry: still indeterminate via the kept fence, never executes.
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 10)).Status == TxStatus.UnknownTx, "failed txId never re-executes");
            Check.Equal(5, chest.TotalOf(Wood), "retry mutates nothing");

            // Fresh tx commits exactly once against clean RAM (no leaked +10).
            w.FailSave = false;
            TxResult fresh = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 10));
            Check.That(fresh.Status == TxStatus.Accepted, "fresh add commits, got " + fresh.Status);
            Check.Equal(15, chest.TotalOf(Wood), "exactly 5+10, leaked delta would give 25");
            Check.Equal(15, w.PersistedTotal(Wood), "persisted 15");
        }

        /// <summary>
        /// Take 20 → failed take 10 → successful take 4: final 16, not 6. The failed debit
        /// must not linger in RAM to double-debit the next tx.
        /// </summary>
        private static void DIRTYRAM_TakeLeakage_PreWriteSaveFail()
        {
            DirtyWorld w = new DirtyWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);
            // Seed the persisted snapshot with the starting stock (as a prior commit would).
            w.Items = DirtyWorld.CloneItems(chest.Snapshot());

            w.FailSave = true;
            TxResult failed = core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 10));
            Check.That(failed.Status == TxStatus.UnknownTx, "failed take is indeterminate, got " + failed.Status);
            Check.Equal(20, chest.TotalOf(Wood), "live restored: speculative debit rolled back, got " + chest.TotalOf(Wood));
            Check.Equal(20, w.PersistedTotal(Wood), "persisted stays 20");

            w.FailSave = false;
            TxResult good = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 4));
            Check.That(good.Status == TxStatus.Accepted, "fresh take commits, got " + good.Status);
            Check.Equal(16, chest.TotalOf(Wood), "final 16 (20-4), leaked debit would give 6");
            Check.Equal(16, w.PersistedTotal(Wood), "persisted 16");
        }

        /// <summary>
        /// Partial Execute throw mid-batch: the first item mutated RAM, then the fault fired.
        /// Recovery must roll back the partial mutation (live == persisted == pre-tx).
        /// </summary>
        private static void DIRTYRAM_PartialExecute()
        {
            DirtyWorld w = new DirtyWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            long badTx = Tx(11, 1);
            w.FailExecuteIds.Add(badTx);
            TxRequest batch = new TxRequest();
            batch.TxId = badTx;
            batch.Op = TxOp.AddBatch;
            batch.Sender = 11;
            batch.Items.Add(new TxItem { Key = Wood, Amount = 10, MaxStack = 50 });
            batch.Items.Add(new TxItem { Key = Stone, Amount = 10, MaxStack = 50 });
            TxResult r = core.Apply(chest, batch);
            Check.That(r.Status == TxStatus.UnknownTx, "partial execute is indeterminate, got " + r.Status);
            Check.Equal(0, chest.GrandTotal(), "partial mutation rolled back, got " + chest.GrandTotal());
            Check.Equal(0, w.PersistedTotal(Wood) + w.PersistedTotal(Stone), "nothing persisted");

            // Same txId stays blocked; a fresh batch commits fully.
            Check.That(core.Apply(chest, batch).Status == TxStatus.UnknownTx, "failed batch txId never re-executes");
            w.FailExecuteIds.Clear();
            TxRequest retry = new TxRequest();
            retry.TxId = Tx(11, 2);
            retry.Op = TxOp.AddBatch;
            retry.Sender = 11;
            retry.Items.Add(new TxItem { Key = Wood, Amount = 10, MaxStack = 50 });
            retry.Items.Add(new TxItem { Key = Stone, Amount = 10, MaxStack = 50 });
            TxResult ok = core.Apply(chest, retry);
            Check.That(ok.Status == TxStatus.Accepted, "fresh batch commits, got " + ok.Status);
            Check.Equal(10, chest.TotalOf(Wood), "wood exactly once");
            Check.Equal(10, chest.TotalOf(Stone), "stone exactly once");
        }

        /// <summary>
        /// Reload failure quarantines: no fresh mutation executes, queued (subsequent) jobs
        /// complete as Indeterminate AFTER durably fencing their txId (never mutating,
        /// never caching), and only a successful TryRecover clears the quarantine
        /// (elapsed attempts alone never do). Fenced-then-quarantined txIds stay
        /// stale-gated after recovery (retry as a NEW txId).
        /// </summary>
        private static void DIRTYRAM_Quarantine_ReloadFailAndQueuedJobs()
        {
            DirtyWorld w = new DirtyWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            TxResult first = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5));
            Check.That(first.Status == TxStatus.Accepted, "seed commits");
            Check.Equal(5, chest.TotalOf(Wood), "seed live 5");

            // Save fails AND reload is broken: recovery impossible → quarantine.
            w.FailSave = true;
            w.FailReload = true;
            TxResult failed = core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 10));
            Check.That(failed.Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "reload failure quarantines");

            // Queued job while quarantined: refused through the durable-terminal
            // matrix (seeded Rejected + persisted, nothing executes).
            TxResult queued = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(queued.Status == TxStatus.Rejected && !queued.IsReplay, "queued job refused durable-Rejected, got " + queued.Status);
            uint hw;
            bool hasPeer = core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw);
            Check.That(hasPeer && hw == 3, "quarantined refusal keeps the high-water (floor 11->3, got " + hw + ")");
            Check.Equal(2, core.ProcessedCount, "durable refusal is cached");
            Check.That(core.Quarantined, "elapsed Apply alone never clears quarantine");

            // Failed recovery path also never clears.
            Check.That(!core.TryRecover(chest), "TryRecover fails while reload broken");
            Check.That(core.Quarantined, "still quarantined");

            // Controlled recovery: reload works → quarantine clears → fresh tx commits.
            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "TryRecover succeeds with working reload");
            Check.That(!core.Quarantined, "quarantine cleared by authoritative reload only");
            Check.Equal(5, chest.TotalOf(Wood), "live matches persisted 5 after recovery");
            TxResult after = core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 7));
            Check.That(after.Status == TxStatus.Accepted, "post-recovery tx commits, got " + after.Status);
            Check.Equal(12, chest.TotalOf(Wood), "5+7 exactly once");
            // The quarantined-era txId replays its stable Rejected after recovery
            // (never resurrects). (The FAILED txId 2 stays fence-blocked
            // forever for the same reason — never seeded, stale-gated UnknownTx.)
            Check.That(core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7)).Status == TxStatus.Rejected, "quarantined-era txId replays stable Rejected after recovery");
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 10)).Status == TxStatus.UnknownTx, "failed txId stays blocked after recovery");
        }

        /// <summary>
        /// Post-write throw: the items write LANDED, then the save threw. The answer stays
        /// Indeterminate (client made no reciprocal change — manual reconciliation needed),
        /// nothing is cached as exact (Query is Unknown), but live RAM matches persisted
        /// (reload sees the already-written value — no divergence, no leak into the next tx).
        /// </summary>
        private static void DIRTYRAM_PostWriteThrow_CommittedButIndeterminate()
        {
            DirtyWorld w = new DirtyWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            w.ThrowAfterSave = true;
            long txId = Tx(11, 1);
            TxResult r = core.Apply(chest, AddReq(txId, 11, Wood, 10));
            Check.That(r.Status == TxStatus.UnknownTx, "post-write throw stays indeterminate, got " + r.Status);
            Check.Equal(10, w.PersistedTotal(Wood), "write landed before the throw");
            Check.Equal(10, chest.TotalOf(Wood), "reload sees the already-written value (no divergence)");
            Check.That(core.Query(txId).Status == TxStatus.UnknownTx, "no exact result cached for the ambiguous tx");
            Check.Equal(0, core.ProcessedCount, "nothing cached");
            Check.That(!core.Quarantined, "successful reload leaves no quarantine");

            // The committed-but-Indeterminate gap: the client credited nothing, so the next
            // fresh tx applies cleanly on top of the written value (no leaked re-apply).
            w.ThrowAfterSave = false;
            TxResult fresh = core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5));
            Check.That(fresh.Status == TxStatus.Accepted, "fresh tx commits, got " + fresh.Status);
            Check.Equal(15, chest.TotalOf(Wood), "10 (durable-but-indeterminate) + 5 exactly once");
            Check.Equal(15, w.PersistedTotal(Wood), "persisted 15");
        }
    }
}
