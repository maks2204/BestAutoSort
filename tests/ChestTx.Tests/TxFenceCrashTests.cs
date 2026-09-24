using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Durable pre-execution fence + crash windows, through the PRODUCTION-SHARED
    /// order (TxCore.Apply with the TxDurability seam — the same fence-first order
    /// ChestTxService.ApplyJob implements with ZDO keys + Container.Save).
    ///
    /// Root cause under test: the fence used to be RAM-only (AdvanceFloor before
    /// ExecuteCall, FloorKey written only at commit), so a crash between fence and
    /// commit — or a failed pre-fence write — could re-execute a tx (double-apply /
    /// double-debit). The fence is now RAM high-water + a durable floor write BEFORE
    /// any mutation, with post-fence ownership recheck and deterministic Rejected
    /// (entry AND floor persisted, else Indeterminate).
    ///
    /// Model: FenceWorld is the fake ZDO (ring/floor byte slots + items snapshot).
    /// "Restart" builds a FRESH TxCore + ModelChest from the persisted copies ONLY —
    /// never from RAM — exactly like a manager handoff after a crash.
    ///
    /// Windows covered: fence-write failure (zero effects), crash after fence (A),
    /// after execute pre-save (B), after items-save pre-ring (C), after ring pre-final-
    /// floor, ring-fail-after-commit, final-floor-fail, rejected-persist-fail, spoof
    /// isolation, ownership loss after fence, corrupt-ring degraded retest, plus
    /// explicit Add double-apply and Take double-debit proofs.
    /// </summary>
    internal static class TxFenceCrashTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);
        private static readonly ItemKey Stone = new ItemKey(1002, 1, 0, 0);

        private const long NegA = -1214308360;

        /// <summary>Fake durable world: ring/floor byte slots + items snapshot + faults.</summary>
        private sealed class FenceWorld
        {
            public byte[] RingBytes;
            public byte[] FloorBytes;
            public List<int[]> Items = new List<int[]>();
            public readonly int W;
            public readonly int H;
            public bool FailFloor;
            public bool FailRing;
            public bool FailSave;
            public bool LoseOwnership;
            public TxDurabilityPoint? CrashAt;
            public int FloorWrites;
            public HashSet<int> FailFloorCalls;

            public FenceWorld(int w, int h)
            {
                W = w;
                H = h;
            }

            public TxDurability Hooks()
            {
                TxDurability d = new TxDurability();
                d.WriteFloor = delegate (byte[] bytes)
                {
                    FloorWrites++;
                    if (FailFloor)
                        return false;
                    if (FailFloorCalls != null && FailFloorCalls.Contains(FloorWrites))
                        return false;
                    FloorBytes = (byte[])bytes.Clone();
                    return true;
                };
                d.WriteRing = delegate (byte[] bytes)
                {
                    if (FailRing)
                        return false;
                    RingBytes = (byte[])bytes.Clone();
                    return true;
                };
                d.SaveItems = delegate (ModelChest chest)
                {
                    if (FailSave)
                        return false;
                    Items = CloneItems(chest.Snapshot());
                    return true;
                };
                d.IsOwner = delegate () { return !LoseOwnership; };
                d.Crash = delegate (TxDurabilityPoint p)
                {
                    if (CrashAt.HasValue && CrashAt.Value == p)
                        throw new TxCrashException(p);
                };
                return d;
            }

            /// <summary>Restart from the persisted copies ONLY (never RAM).</summary>
            public void Restart(out TxCore core, out ModelChest chest)
            {
                core = new TxCore();
                core.Durability = Hooks();
                chest = new ModelChest(W, H);
                if (Items != null)
                    chest.Restore(CloneItems(Items));
                core.LoadRingWithFloor(TxCore.DecodeEnvelope(RingBytes), TxCore.DecodeFloor(FloorBytes));
            }

            public static List<int[]> CloneItems(List<int[]> src)
            {
                List<int[]> dst = new List<int[]>(src.Count);
                for (int i = 0; i < src.Count; i++)
                    dst.Add((int[])src[i].Clone());
                return dst;
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
            Console.WriteLine("FENCE_WriteFloorFailZeroEffects");
            FENCE_WriteFloorFailZeroEffects();
            Console.WriteLine("FENCE_CrashAfterFence");
            FENCE_CrashAfterFence();
            Console.WriteLine("FENCE_CrashAfterExecuteAddProof");
            FENCE_CrashAfterExecuteAddProof();
            Console.WriteLine("FENCE_CrashAfterItemsSaveTakeProof");
            FENCE_CrashAfterItemsSaveTakeProof();
            Console.WriteLine("FENCE_CrashAfterRingReplayStable");
            FENCE_CrashAfterRingReplayStable();
            Console.WriteLine("FENCE_RingFailAfterCommit");
            FENCE_RingFailAfterCommit();
            Console.WriteLine("FENCE_FinalFloorFailReplayFromRing");
            FENCE_FinalFloorFailReplayFromRing();
            Console.WriteLine("FENCE_RejectedPersistFail");
            FENCE_RejectedPersistFail();
            Console.WriteLine("FENCE_SpoofNeverFences");
            FENCE_SpoofNeverFences();
            Console.WriteLine("FENCE_OwnershipLostAfterFence");
            FENCE_OwnershipLostAfterFence();
            Console.WriteLine("FENCE_CorruptDegradedFenceDurable");
            FENCE_CorruptDegradedFenceDurable();
        }

        /// <summary>
        /// Fence write fails: ZERO side effects (no mutation, no cache entry, nothing
        /// persisted), loud Indeterminate, no auto-retry. The RAM floor still advances
        /// (seen-high-water, never rolls back): the same txId stays blocked in-session
        /// while a NEW txId commits once writes succeed.
        /// </summary>
        private static void FENCE_WriteFloorFailZeroEffects()
        {
            FenceWorld w = new FenceWorld(6, 4);
            w.FailFloor = true;
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            TxRequest add = AddReq(Tx(11, 1), 11, Wood, 50);
            TxResult r = core.Apply(chest, add);
            Check.That(r.Status == TxStatus.UnknownTx, "fence failure is indeterminate, got " + r.Status);
            Check.Equal(0, chest.GrandTotal(), "zero effects: chest untouched");
            Check.Equal(0, core.ProcessedCount, "zero effects: nothing cached");
            Check.That(w.FloorBytes == null && w.RingBytes == null, "zero effects: nothing persisted");
            Check.That(core.Query(add.TxId).Status == TxStatus.UnknownTx, "query indeterminate");

            // Same txId retry in-session: still indeterminate, never executes.
            TxResult retry = core.Apply(chest, add);
            Check.That(retry.Status == TxStatus.UnknownTx && !retry.IsReplay, "retry never executes, got " + retry.Status);
            Check.Equal(0, chest.GrandTotal(), "retry mutates nothing");
            // Seen-high-water: the RAM floor advanced even though the write failed.
            Check.That(core.DumpFloor().ContainsKey(TxIdGen.PeerKey(11)), "RAM floor keeps the seen tx");

            // Writes succeed again: a NEW txId commits exactly once.
            w.FailFloor = false;
            TxResult fresh = core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 50));
            Check.That(fresh.Status == TxStatus.Accepted, "fresh tx commits, got " + fresh.Status);
            Check.Equal(50, chest.TotalOf(Wood), "exactly one application");
        }

        /// <summary>
        /// Crash window A (fence durable, nothing mutated): restart from persisted
        /// copies, retry is Indeterminate, chest clean, new tx works.
        /// </summary>
        private static void FENCE_CrashAfterFence()
        {
            FenceWorld w = new FenceWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            w.CrashAt = TxDurabilityPoint.AfterFence;

            TxRequest add = AddReq(Tx(11, 1), 11, Wood, 50);
            bool crashed = false;
            try { core.Apply(chest, add); }
            catch (TxCrashException ex) { crashed = ex.Point == TxDurabilityPoint.AfterFence; }
            Check.That(crashed, "must crash after fence");
            Check.That(w.FloorBytes != null, "fence is durable across the crash");

            w.CrashAt = null;
            TxCore core2;
            ModelChest chest2;
            w.Restart(out core2, out chest2);
            Check.Equal(0, chest2.GrandTotal(), "restart chest clean (nothing mutated pre-crash)");
            TxResult retry = core2.Apply(chest2, add);
            Check.That(retry.Status == TxStatus.UnknownTx, "retry indeterminate, got " + retry.Status);
            Check.Equal(0, chest2.GrandTotal(), "retry mutates nothing");
            TxResult fresh = core2.Apply(chest2, AddReq(Tx(11, 2), 11, Wood, 50));
            Check.That(fresh.Status == TxStatus.Accepted, "new tx commits after restart");
            Check.Equal(50, chest2.TotalOf(Wood), "exactly one application total");
        }

        /// <summary>
        /// Crash window B (RAM mutated, items NOT saved) + Add double-apply proof:
        /// the crashed attempt persisted nothing, the retry applies nothing, a fresh
        /// tx applies exactly once.
        /// </summary>
        private static void FENCE_CrashAfterExecuteAddProof()
        {
            FenceWorld w = new FenceWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            w.CrashAt = TxDurabilityPoint.AfterExecute;

            TxRequest add = AddReq(Tx(11, 1), 11, Wood, 50);
            bool crashed = false;
            try { core.Apply(chest, add); }
            catch (TxCrashException ex) { crashed = ex.Point == TxDurabilityPoint.AfterExecute; }
            Check.That(crashed, "must crash after execute");

            w.CrashAt = null;
            TxCore core2;
            ModelChest chest2;
            w.Restart(out core2, out chest2);
            Check.Equal(0, chest2.GrandTotal(), "unsaved RAM mutation lost in the crash");
            // Same txId twice post-restart: both indeterminate, nothing applied.
            Check.That(core2.Apply(chest2, add).Status == TxStatus.UnknownTx, "first retry indeterminate");
            Check.That(core2.Apply(chest2, add).Status == TxStatus.UnknownTx, "second retry indeterminate");
            Check.Equal(0, chest2.GrandTotal(), "Add double-apply proof: crashed tx never materializes");
            TxResult fresh = core2.Apply(chest2, AddReq(Tx(11, 2), 11, Wood, 50));
            Check.That(fresh.Status == TxStatus.Accepted, "fresh tx commits");
            Check.Equal(50, chest2.TotalOf(Wood), "exactly one Add application across crash + retries");
        }

        /// <summary>
        /// Crash window C (items saved, ring NOT written) + Take double-debit proof:
        /// the debit persisted exactly once; the retry is Indeterminate and debits
        /// nothing further. (The client got no response, so it credits nothing: the
        /// window stays Indeterminate and needs manual reconciliation — at-most-once,
        /// never exactly-once here.)
        /// </summary>
        private static void FENCE_CrashAfterItemsSaveTakeProof()
        {
            FenceWorld w = new FenceWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);

            w.CrashAt = TxDurabilityPoint.AfterItemsSave;
            TxRequest take = TakeReq(Tx(11, 1), 11, Wood, 6);
            bool crashed = false;
            try { core.Apply(chest, take); }
            catch (TxCrashException ex) { crashed = ex.Point == TxDurabilityPoint.AfterItemsSave; }
            Check.That(crashed, "must crash after items-save");

            w.CrashAt = null;
            TxCore core2;
            ModelChest chest2;
            w.Restart(out core2, out chest2);
            Check.Equal(14, chest2.TotalOf(Wood), "saved debit survives the crash exactly once");
            TxResult retry = core2.Apply(chest2, take);
            Check.That(retry.Status == TxStatus.UnknownTx, "retry indeterminate, got " + retry.Status);
            Check.Equal(14, chest2.TotalOf(Wood), "Take double-debit proof: retry debits nothing");
            TxResult fresh = core2.Apply(chest2, TakeReq(Tx(11, 2), 11, Wood, 4));
            Check.That(fresh.Status == TxStatus.Accepted, "fresh tx commits");
            Check.Equal(10, chest2.TotalOf(Wood), "two txIds debited 6+4 exactly once each");
        }

        /// <summary>
        /// Crash after the ring write (final floor write skipped): the ring entry
        /// restores the cache, so the retry replays the ORIGINAL outcome — no
        /// double-debit. Uses a negative UID to cover canonical identity on the
        /// durable path.
        /// </summary>
        private static void FENCE_CrashAfterRingReplayStable()
        {
            FenceWorld w = new FenceWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);

            uint c = 1;
            long txId = TxIdGen.Next(NegA, ref c);
            TxRequest take = TakeReq(txId, NegA, Wood, 6);
            w.CrashAt = TxDurabilityPoint.AfterRing;
            bool crashed = false;
            try { core.Apply(chest, take); }
            catch (TxCrashException ex) { crashed = ex.Point == TxDurabilityPoint.AfterRing; }
            Check.That(crashed, "must crash after ring");
            Check.That(w.RingBytes != null, "ring entry durable across the crash");

            w.CrashAt = null;
            TxCore core2;
            ModelChest chest2;
            w.Restart(out core2, out chest2);
            TxResult replay = core2.Apply(chest2, take);
            Check.That(replay.Status == TxStatus.Accepted && replay.IsReplay && !replay.TotalsOnly,
                "post-crash replay keeps outcome, got " + replay.Status);
            Check.Equal(6, replay.AcceptedTotal(), "replay keeps body");
            Check.Equal(14, chest2.TotalOf(Wood), "replay debits nothing");
            Check.Equal(TxIdGen.PeerKey(NegA), replay.Sender, "canonical negative sender survives");
        }

        /// <summary>
        /// Ring write fails AFTER a commit (Accepted answered, fence durable): no
        /// double-apply — a post-restart retry is Indeterminate via the floor fence,
        /// and the next successful commit carries on.
        /// </summary>
        private static void FENCE_RingFailAfterCommit()
        {
            FenceWorld w = new FenceWorld(6, 4);
            w.FailRing = true;
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);

            TxRequest take = TakeReq(Tx(11, 1), 11, Wood, 6);
            TxResult r = core.Apply(chest, take);
            Check.That(r.Status == TxStatus.Accepted, "outcome stands on fence protection, got " + r.Status);
            Check.Equal(14, chest.TotalOf(Wood), "committed once");
            Check.That(w.RingBytes == null && w.FloorBytes != null, "ring missing, fence durable");

            w.FailRing = false;
            TxCore core2;
            ModelChest chest2;
            w.Restart(out core2, out chest2);
            Check.Equal(14, chest2.TotalOf(Wood), "restart keeps the single debit");
            Check.That(core2.Apply(chest2, take).Status == TxStatus.UnknownTx, "retry indeterminate via fence");
            Check.Equal(14, chest2.TotalOf(Wood), "retry debits nothing");
            Check.That(core2.Apply(chest2, TakeReq(Tx(11, 2), 11, Wood, 4)).Status == TxStatus.Accepted,
                "next tx commits");
            Check.Equal(10, chest2.TotalOf(Wood), "exactly-once per txId");
        }

        /// <summary>
        /// Only the FINAL floor write fails (fence + ring durable): the ring entry
        /// replays the original outcome after restart.
        /// </summary>
        private static void FENCE_FinalFloorFailReplayFromRing()
        {
            FenceWorld w = new FenceWorld(6, 4);
            w.FailFloorCalls = new HashSet<int>();
            w.FailFloorCalls.Add(2); // 1 = pre-execution fence, 2 = post-commit floor
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);

            TxRequest take = TakeReq(Tx(11, 1), 11, Wood, 6);
            TxResult r = core.Apply(chest, take);
            Check.That(r.Status == TxStatus.Accepted, "outcome stands, got " + r.Status);
            Check.Equal(2, w.FloorWrites, "fence + final floor attempted");
            Check.That(w.RingBytes != null, "ring durable");

            w.FailFloorCalls = null;
            TxCore core2;
            ModelChest chest2;
            w.Restart(out core2, out chest2);
            TxResult replay = core2.Apply(chest2, take);
            Check.That(replay.Status == TxStatus.Accepted && replay.IsReplay, "ring replays outcome, got " + replay.Status);
            Check.Equal(14, chest2.TotalOf(Wood), "no double-debit");
        }

        /// <summary>
        /// A Rejected outcome whose persistence fails is answered Indeterminate
        /// (never a RAM-only Rejected that could flip after restart). The seed is
        /// evicted but the floor advance is kept, so the same txId stays stale;
        /// a NEW txId gets a stable persisted Rejected.
        /// </summary>
        private static void FENCE_RejectedPersistFail()
        {
            FenceWorld w = new FenceWorld(6, 4);
            w.FailRing = true;
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 5, 50);

            TxRequest take = TakeReq(Tx(11, 1), 11, Stone, 1);
            TxResult r = core.Apply(chest, take);
            Check.That(r.Status == TxStatus.UnknownTx, "unpersisted reject is indeterminate, got " + r.Status);
            Check.Equal(5, chest.TotalOf(Wood), "nothing debited");
            Check.Equal(0, core.ProcessedCount, "failed seed evicted");
            Check.That(core.Query(take.TxId).Status == TxStatus.UnknownTx, "query indeterminate");
            // Same txId stays stale (floor advance kept), never executes.
            Check.That(core.Apply(chest, take).Status == TxStatus.UnknownTx, "retry indeterminate");

            w.FailRing = false;
            TxResult rej = core.Apply(chest, TakeReq(Tx(11, 2), 11, Stone, 1));
            Check.That(rej.Status == TxStatus.Rejected, "new tx rejected, got " + rej.Status);
            Check.That(w.RingBytes != null && w.FloorBytes != null, "rejected outcome durable");
            TxResult replay = core.Apply(chest, TakeReq(Tx(11, 2), 11, Stone, 1));
            Check.That(replay.Status == TxStatus.Rejected && replay.IsReplay, "rejected replay stable");
            Check.Equal(5, chest.TotalOf(Wood), "rejected debits nothing");
        }

        /// <summary>
        /// Spoofed txIds never advance any floor: the exact counter stays blocked as
        /// Rejected (stable answer for everyone), while the true owner's other
        /// counters commit normally.
        /// </summary>
        private static void FENCE_SpoofNeverFences()
        {
            FenceWorld w = new FenceWorld(6, 4);
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);

            // Sender 22 claims a peer-11 txId.
            TxRequest spoof = TakeReq(Tx(11, 1), 22, Wood, 6);
            TxResult r = core.Apply(chest, spoof);
            Check.That(r.Status == TxStatus.Rejected, "spoof rejected, got " + r.Status);
            Check.Equal(20, chest.TotalOf(Wood), "spoof executes nothing");
            Check.That(!core.DumpFloor().ContainsKey(TxIdGen.PeerKey(11)), "true peer floor untouched by spoof");
            Check.That(w.RingBytes != null, "spoof reject persisted in ring");

            // The exact counter is burned as Rejected even for the true owner...
            TxRequest ownerSameCounter = TakeReq(Tx(11, 1), 11, Wood, 6);
            TxResult burned = core.Apply(chest, ownerSameCounter);
            Check.That(burned.Status == TxStatus.Rejected && burned.IsReplay, "exact counter stable, got " + burned.Status);
            Check.Equal(20, chest.TotalOf(Wood), "burned counter executes nothing");
            // ...while the owner's next counter commits normally.
            TxResult legit = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 6));
            Check.That(legit.Status == TxStatus.Accepted, "true owner commits, got " + legit.Status);
            Check.Equal(14, chest.TotalOf(Wood), "exactly one debit");
        }

        /// <summary>
        /// Ownership lost after the fence (write succeeded, recheck fails): no
        /// mutation, Indeterminate answer, fence kept — retry stays stale.
        /// </summary>
        private static void FENCE_OwnershipLostAfterFence()
        {
            FenceWorld w = new FenceWorld(6, 4);
            w.LoseOwnership = true;
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            TxRequest add = AddReq(Tx(11, 1), 11, Wood, 50);
            TxResult r = core.Apply(chest, add);
            Check.That(r.Status == TxStatus.UnknownTx, "lost ownership is indeterminate, got " + r.Status);
            Check.Equal(0, chest.GrandTotal(), "no mutation after fence");
            Check.That(w.FloorBytes != null, "fence write preceded the loss");

            w.LoseOwnership = false;
            TxCore core2;
            ModelChest chest2;
            w.Restart(out core2, out chest2);
            Check.That(core2.Apply(chest2, add).Status == TxStatus.UnknownTx, "retry stale via kept fence");
            Check.Equal(0, chest2.GrandTotal(), "retry mutates nothing");
            Check.That(core2.Apply(chest2, AddReq(Tx(11, 2), 11, Wood, 50)).Status == TxStatus.Accepted,
                "new tx commits once ownership holds");
            Check.Equal(50, chest2.TotalOf(Wood), "exactly one application");
        }

        /// <summary>
        /// Corrupt-ring degraded recovery through the durable path: old-gen stays
        /// blocked, new-gen fences durably and heals the ring on commit.
        /// </summary>
        private static void FENCE_CorruptDegradedFenceDurable()
        {
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            for (uint i = 1; i <= 5; i++)
            {
                TxRequest r = TakeReq(Tx(11, i), 11, Wood, 1);
                Check.That(core.Apply(chest, r).Status == TxStatus.Accepted, "pre-corruption commits");
            }
            Check.Equal(15, chest.TotalOf(Wood), "5 debited pre-corruption");
            FloorData intactFloor = TxCore.DecodeFloor(TxCore.EncodeFloor(core.DumpFloor()));
            Check.That(!intactFloor.Corrupt, "independent floor intact");
            intactFloor.Present = true;

            FenceWorld w = new FenceWorld(6, 4);
            w.Items = FenceWorld.CloneItems(chest.Snapshot());
            TxCore rec = new TxCore();
            rec.Durability = w.Hooks();
            rec.LoadRing(TxCore.DecodeEnvelope(new byte[] { 1, 2, 3 }));
            Check.That(rec.RingCorrupt, "corrupt flagged");
            rec.RecoverWithFloor(intactFloor);
            Check.That(rec.RingCorrupt && !rec.FloorCorrupt, "degraded: floor trusted");

            TxRequest oldGen = TakeReq(Tx(11, 3), 11, Wood, 1);
            Check.That(rec.Apply(chest, oldGen).Status == TxStatus.UnknownTx, "old-gen stays blocked");
            Check.Equal(15, chest.TotalOf(Wood), "old-gen debits nothing");

            TxRequest newGen = TakeReq(Tx(11, 6), 11, Wood, 4);
            TxResult committed = rec.Apply(chest, newGen);
            Check.That(committed.Status == TxStatus.Accepted && !committed.IsReplay, "new-gen commits");
            Check.Equal(11, chest.TotalOf(Wood), "exactly one new debit");
            Check.That(w.FloorBytes != null && w.RingBytes != null, "new-gen fenced + ring rebuilt durably");
            Check.That(!rec.RingCorrupt, "successful commit heals the ring flag");
            Check.That(rec.Apply(chest, newGen).IsReplay, "post-recovery replay stable");
            Check.Equal(11, chest.TotalOf(Wood), "replay debits nothing");
        }
    }
}
