using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Transient P0 (v0.5.x wave-3): both-durable-copies-failed is NON-terminal.
    /// All tests run against PRODUCTION code (TxCore.Apply/Query, TxDecision,
    /// TxResponsePolicy, TxPendingDrain, TxRing, TxIdGen): the manager holds a
    /// both-fail txId in a RAM-only transient-refusal map (populated ONLY on
    /// both-fail) and answers TransientUnavailable; the client contract (pinned
    /// through the shared Classify/DecideTransient seams — Unity-coupled
    /// Pending paths cannot run in this harness) is retain Pending + claims and
    /// retry the SAME txId flagged, with bounded backoff and no deadline
    /// finalization. ExecuteCalls pins "never executes" across every retry leg;
    /// chest totals pin conservation; restart legs rebuild from persisted bytes
    /// ONLY (RAM discarded, flagged retry covers the handoff).
    /// </summary>
    internal static class TxTransientTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);

        private static long Tx(long peer, uint ctr)
        {
            return unchecked((peer << 32) | ctr);
        }

        /// <summary>Fake durable world: ring/floor byte slots + items snapshot + faults.</summary>
        private sealed class TransientWorld
        {
            public byte[] RingBytes;
            public byte[] FloorBytes;
            public List<int[]> Items;
            public bool FailRing;
            public bool FailFloor;
            public bool FailSave;
            public bool FailReload;
            public bool LoseOwnership;

            public TxDurability Hooks()
            {
                TxDurability d = new TxDurability();
                d.WriteFloor = delegate (byte[] bytes)
                {
                    if (FailFloor)
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
                d.ReloadItems = delegate (ModelChest chest)
                {
                    if (FailReload || Items == null)
                        return false;
                    chest.Restore(CloneItems(Items));
                    return true;
                };
                d.IsOwner = delegate () { return !LoseOwnership; };
                d.Crash = null;
                return d;
            }

            /// <summary>Restart from the persisted copies ONLY (never RAM).</summary>
            public void Restart(int w, int h, out TxCore core, out ModelChest chest)
            {
                core = new TxCore();
                core.Durability = Hooks();
                chest = new ModelChest(w, h);
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

        private static TxRequest AddReq(long txId, long sender, ItemKey key, int amount, bool transientRetry)
        {
            TxRequest r = new TxRequest();
            r.TxId = txId;
            r.Op = TxOp.Add;
            r.Sender = sender;
            r.IsTransientRetry = transientRetry;
            r.Items.Add(new TxItem { Key = key, Amount = amount, MaxStack = 50 });
            return r;
        }

        public static void RunAll()
        {
            Console.WriteLine("TRANSIENT_StatusIsNonTerminal");
            TRANSIENT_StatusIsNonTerminal();
            Console.WriteLine("TRANSIENT_BothFailPopulatesRamMap");
            TRANSIENT_BothFailPopulatesRamMap();
            Console.WriteLine("TRANSIENT_MutationResendReattemptsPersistenceNeverExecutes");
            TRANSIENT_MutationResendReattemptsPersistenceNeverExecutes();
            Console.WriteLine("TRANSIENT_FlaggedRetryPartialStaysTransient");
            TRANSIENT_FlaggedRetryPartialStaysTransient();
            Console.WriteLine("TRANSIENT_QueryChecksTransientBeforeCacheMiss");
            TRANSIENT_QueryChecksTransientBeforeCacheMiss();
            Console.WriteLine("TRANSIENT_QuarantineClearKeepsTransientAuthoritative");
            TRANSIENT_QuarantineClearKeepsTransientAuthoritative();
            Console.WriteLine("TRANSIENT_TransientRemovedAfterDurableResolution");
            TRANSIENT_TransientRemovedAfterDurableResolution();
            Console.WriteLine("TRANSIENT_OwnershipLossDropsRamMap");
            TRANSIENT_OwnershipLossDropsRamMap();
            Console.WriteLine("TRANSIENT_DeadlineNeverFinalizes");
            TRANSIENT_DeadlineNeverFinalizes();
            Console.WriteLine("TRANSIENT_FlagSpoofSafe");
            TRANSIENT_FlagSpoofSafe();
            Console.WriteLine("TRANSIENT_EndToEndAdd10WithHandoff");
            TRANSIENT_EndToEndAdd10WithHandoff();
        }

        /// <summary>
        /// Transient semantic: TransientUnavailable classifies TransientRetrySameTx
        /// for every totalsOnly/op/arity shape, is never committed, never terminal,
        /// never cascades, never restores drag remainder (0 amount).
        /// </summary>
        private static void TRANSIENT_StatusIsNonTerminal()
        {
            Check.That(TxResponsePolicy.Classify(TxStatus.TransientUnavailable, false, TxOp.Add, 1)
                == TxCompletionKind.TransientRetrySameTx, "transient add classifies TransientRetrySameTx");
            Check.That(TxResponsePolicy.Classify(TxStatus.TransientUnavailable, true, TxOp.Take, 1)
                == TxCompletionKind.TransientRetrySameTx, "transient totals-only take classifies TransientRetrySameTx");
            Check.That(TxResponsePolicy.Classify(TxStatus.TransientUnavailable, true, TxOp.AddBatch, 5)
                == TxCompletionKind.TransientRetrySameTx, "transient multi-add classifies TransientRetrySameTx");
            Check.That(!TxResponsePolicy.IsCommitted(TxStatus.TransientUnavailable), "transient never committed");
            Check.That(!TxRing.IsCommittedStatus(TxStatus.TransientUnavailable), "ring: transient never committed");
            Check.That(!TxRing.IsTerminalStatus(TxStatus.TransientUnavailable), "ring: transient never terminal");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.TransientRetrySameTx),
                "transient never cascades");
            Check.That(!TxDecision.ShouldRestoreDragRemainder(TxCompletionKind.TransientRetrySameTx),
                "transient never restores drag remainder");
            Check.That(TxDecision.DragRestoreAmount(10, 4, TxCompletionKind.TransientRetrySameTx) == 0,
                "transient drag-restore amount is 0");
            // Control: the classic terminals are untouched.
            Check.That(TxResponsePolicy.Classify(TxStatus.UnknownTx, true, TxOp.Add, 1)
                == TxCompletionKind.Indeterminate, "control: UnknownTx still Indeterminate");
            Check.That(TxResponsePolicy.Classify(TxStatus.Rejected, false, TxOp.Add, 1)
                == TxCompletionKind.FailedNotCommitted, "control: Rejected still FailedNotCommitted");
        }

        /// <summary>
        /// Matrix both-fail: quarantine refusal with ring AND floor writes failing
        /// records the txId in the RAM map (populated ONLY on both-fail) and answers
        /// TransientUnavailable — never cached, never persisted, never executed.
        /// Partial persistence still answers UnknownTx with NO map entry.
        /// </summary>
        private static void TRANSIENT_BothFailPopulatesRamMap()
        {
            Check.That(TxDecision.HandoffDropTerminal(true, true) == TxStatus.Rejected, "matrix: both durable => Rejected");
            Check.That(TxDecision.HandoffDropTerminal(false, false) == TxStatus.UnknownTx, "matrix: both failed => UnknownTx");
            Check.That(TxDecision.HandoffDropTerminal(true, false) == TxStatus.UnknownTx, "matrix: ring-only => UnknownTx");
            Check.That(TxDecision.HandoffDropTerminal(false, true) == TxStatus.UnknownTx, "matrix: floor-only => UnknownTx");

            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "seed commits");
            int execAfterSeed = core.ExecuteCalls;
            Check.That(execAfterSeed == 1, "seed executed once");

            w.FailSave = true;
            w.FailReload = true;
            TxResult failed = core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5, false));
            Check.That(failed.Status == TxStatus.UnknownTx, "failed tx indeterminate, got " + failed.Status);
            Check.That(core.Quarantined, "failed save quarantines");
            int execAfterQuarantine = core.ExecuteCalls;

            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            int before = chest.TotalOf(Wood);
            int processedBefore = core.ProcessedCount;
            TxResult transient = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7, false));
            Check.That(transient.Status == TxStatus.TransientUnavailable, "both-fail answers TransientUnavailable, got " + transient.Status);
            Check.That(!transient.IsReplay, "transient answer is not a replay");
            Check.That(core.TransientCount == 1, "RAM map holds exactly the both-fail txId");
            Check.That(core.DumpTransient().ContainsKey(Tx(11, 3)), "map key is the refused txId");
            Check.That(core.ProcessedCount == processedBefore, "both-fail seed evicted (never cached)");
            Check.That(core.ExecuteCalls == execAfterQuarantine, "both-fail executes nothing (ExecuteCalls==" + core.ExecuteCalls + ")");
            Check.Equal(before, chest.TotalOf(Wood), "both-fail mutates nothing");

            // Partial persistence (ring ok, floor failed): UnknownTx, NO map entry.
            w.FailRing = false;
            TxResult partial = core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 7, false));
            Check.That(partial.Status == TxStatus.UnknownTx, "partial persist answers UnknownTx, got " + partial.Status);
            Check.That(core.TransientCount == 1, "partial failure does NOT populate the map");
            Check.That(!core.DumpTransient().ContainsKey(Tx(11, 4)), "partial-fail txId not in map");
        }

        /// <summary>
        /// Mutation resend while transient: unflagged resends are reminded
        /// TransientUnavailable; flagged resends re-attempt persistence ONLY
        /// (ExecuteCalls stays 0 across every leg) until the writes heal.
        /// </summary>
        private static void TRANSIENT_MutationResendReattemptsPersistenceNeverExecutes()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5, false)).Status == TxStatus.UnknownTx, "quarantining tx indeterminate");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long txId = Tx(11, 3);
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "both-fail transient");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);

            TxResult unflagged = core.Apply(chest, AddReq(txId, 11, Wood, 7, false));
            Check.That(unflagged.Status == TxStatus.TransientUnavailable, "unflagged resend reminded transient, got " + unflagged.Status);
            Check.That(core.ExecuteCalls == execBase, "unflagged resend executes nothing (ExecuteCalls==0 this tx)");
            Check.That(core.TransientCount == 1, "unflagged resend keeps the map entry");

            TxResult flaggedFailing = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(flaggedFailing.Status == TxStatus.TransientUnavailable, "flagged resend while failing stays transient, got " + flaggedFailing.Status);
            Check.That(core.ExecuteCalls == execBase, "flagged resend re-attempts persistence but never executes");
            Check.That(core.TransientCount == 1, "still held while writes fail");
            Check.Equal(before, chest.TotalOf(Wood), "no leg mutated the chest");

            w.FailRing = false;
            w.FailFloor = false;
            TxResult resolved = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(resolved.Status == TxStatus.Rejected && !resolved.IsReplay, "healed flagged retry resolves durable-Rejected, got " + resolved.Status);
            Check.That(core.TransientCount == 0, "durable resolution drops the map entry");
            Check.That(core.ExecuteCalls == execBase, "resolution executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "resolution mutates nothing");
        }

        /// <summary>
        /// Partial-retry leg (the core gate production mirrors in OnTxRequest): a
        /// flagged same-tx mutation retry that persists EXACTLY ONE copy is NOT
        /// durable — the map entry is kept, the answer stays TransientUnavailable
        /// (never a terminal UnknownTx/Rejected), and nothing ever executes.
        /// Both partial directions are pinned; healing both copies lets the SAME
        /// flagged txId resolve durable-Rejected with no new txId needed.
        /// </summary>
        private static void TRANSIENT_FlaggedRetryPartialStaysTransient()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5, false)).Status == TxStatus.UnknownTx, "quarantining tx indeterminate");
            Check.That(core.Quarantined, "failed save quarantines");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long txId = Tx(11, 3);
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "both-fail transient");
            Check.That(core.TransientCount == 1, "both-fail populates the map");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);
            int processedBase = core.ProcessedCount;

            // Partial direction 1 (ring persists, floor fails): the flagged retry
            // re-attempts persistence but stays transient — same-tx retained.
            w.FailRing = false;
            TxResult ringOnly = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(ringOnly.Status == TxStatus.TransientUnavailable && !ringOnly.IsReplay,
                "flagged retry with ring-only persist stays transient, got " + ringOnly.Status);
            Check.That(core.TransientCount == 1 && core.DumpTransient().ContainsKey(txId), "partial retry keeps the map entry");
            Check.That(core.ProcessedCount == processedBase, "partial seed evicted (never cached)");
            Check.That(core.ExecuteCalls == execBase, "partial retry executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "partial retry mutates nothing");

            // Partial direction 2 (floor persists, ring fails): same contract.
            w.FailRing = true;
            w.FailFloor = false;
            TxResult floorOnly = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(floorOnly.Status == TxStatus.TransientUnavailable && !floorOnly.IsReplay,
                "flagged retry with floor-only persist stays transient, got " + floorOnly.Status);
            Check.That(core.TransientCount == 1 && core.DumpTransient().ContainsKey(txId), "partial retry keeps the map entry");
            Check.That(core.ProcessedCount == processedBase, "partial seed evicted (never cached)");
            Check.That(core.ExecuteCalls == execBase, "partial retry executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "partial retry mutates nothing");

            // Writes heal: the SAME flagged txId resolves durable-Rejected.
            w.FailRing = false;
            w.FailFloor = false;
            TxResult resolved = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(resolved.Status == TxStatus.Rejected && !resolved.IsReplay,
                "healed flagged retry resolves durable-Rejected, got " + resolved.Status);
            Check.That(core.TransientCount == 0, "durable resolution drops the map entry");
            Check.That(core.ExecuteCalls == execBase, "resolution executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "resolution mutates nothing");
        }

        /// <summary>
        /// Query checks transient BEFORE the cache-miss UnknownTx (sender-validated):
        /// owner Query of a held txId answers TransientUnavailable; unknown txIds
        /// still answer UnknownTx; strangers get Rejected with no payload and the
        /// map is untouched.
        /// </summary>
        private static void TRANSIENT_QueryChecksTransientBeforeCacheMiss()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5, false)).Status == TxStatus.UnknownTx, "quarantine");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long txId = Tx(11, 3);
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "both-fail transient");

            TxResult q = core.Query(txId, 11);
            Check.That(q.Status == TxStatus.TransientUnavailable, "owner Query of held txId answers transient, got " + q.Status);
            TxResult qUnknown = core.Query(Tx(11, 99), 11);
            Check.That(qUnknown.Status == TxStatus.UnknownTx, "Query of unknown txId still UnknownTx, got " + qUnknown.Status);
            TxResult qStranger = core.Query(txId, 12);
            Check.That(qStranger.Status == TxStatus.Rejected, "stranger Query of transient txId Rejected, got " + qStranger.Status);
            Check.That(qStranger.Accepted.Count == 0, "stranger Query leaks no payload");
            Check.That(core.TransientCount == 1, "stranger Query leaves the map untouched");
            Check.That(core.ExecuteCalls == 2, "queries never execute (ExecuteCalls==2: seed + quarantining tx)");
        }

        /// <summary>
        /// Quarantine clear keeps transient authoritative: TryRecover clears the
        /// quarantine but NOT the map; the flagged retry still resolves through
        /// the transient path (durable Rejected, never executes).
        /// </summary>
        private static void TRANSIENT_QuarantineClearKeepsTransientAuthoritative()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5, false)).Status == TxStatus.UnknownTx, "quarantine");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long txId = Tx(11, 3);
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "both-fail transient");
            int execBase = core.ExecuteCalls;

            w.FailReload = false;
            Check.That(core.TryRecover(chest), "authoritative reload recovers");
            Check.That(!core.Quarantined, "quarantine cleared by reload");
            Check.That(core.TransientCount == 1, "quarantine clear keeps the transient entry authoritative");

            w.FailRing = false;
            w.FailFloor = false;
            TxResult resolved = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(resolved.Status == TxStatus.Rejected && !resolved.IsReplay, "flagged retry resolves durable-Rejected after clear, got " + resolved.Status);
            Check.That(core.TransientCount == 0, "map dropped after durable resolution");
            Check.That(core.ExecuteCalls == execBase, "resolution never executes");
        }

        /// <summary>
        /// Transient removed after durable resolution: the resolved refusal replays
        /// stably, a NEW txId commits normally afterwards, and conservation holds.
        /// </summary>
        private static void TRANSIENT_TransientRemovedAfterDurableResolution()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5, false)).Status == TxStatus.UnknownTx, "quarantine");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long txId = Tx(11, 3);
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "both-fail transient");

            w.FailRing = false;
            w.FailFloor = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "recover");
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, true)).Status == TxStatus.Rejected, "durable resolution");
            Check.That(core.TransientCount == 0, "map empty after resolution");

            TxResult replay = core.Apply(chest, AddReq(txId, 11, Wood, 7, false));
            Check.That(replay.Status == TxStatus.Rejected && replay.IsReplay, "resolved refusal replays stably, got " + replay.Status);
            int before = chest.TotalOf(Wood);
            Check.That(core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 9, false)).Status == TxStatus.Accepted, "fresh txId commits after resolution");
            Check.Equal(before + 9, chest.TotalOf(Wood), "conservation: only the fresh tx applied");
        }

        /// <summary>
        /// Ownership loss drops the RAM map (client retry flag covers the handoff):
        /// the post-fence owner check clears transient; a restart from persisted
        /// bytes ONLY starts with an empty map; the flagged retry of the old txId
        /// on the new owner never executes (stale-gated, floor kept).
        /// </summary>
        private static void TRANSIENT_OwnershipLossDropsRamMap()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5, false)).Status == TxStatus.UnknownTx, "quarantine");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long txId = Tx(11, 3);
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "both-fail transient");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);

            w.FailReload = false;
            Check.That(core.TryRecover(chest), "recover (map kept)");
            Check.That(core.TransientCount == 1, "map kept across quarantine clear");
            w.FailRing = false;
            w.FailFloor = false;
            before = chest.TotalOf(Wood);
            w.LoseOwnership = true;
            TxResult lost = core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 9, false));
            Check.That(lost.Status == TxStatus.UnknownTx, "owner-loss after fence indeterminate, got " + lost.Status);
            Check.That(core.TransientCount == 0, "ownership loss drops the RAM map");
            Check.That(core.ExecuteCalls == execBase, "owner loss executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "owner loss mutates nothing");

            w.LoseOwnership = false;
            TxCore core2;
            ModelChest chest2;
            w.Restart(6, 4, out core2, out chest2);
            Check.That(core2.TransientCount == 0, "restart starts with an empty transient map (RAM discarded)");
            int before2 = chest2.TotalOf(Wood);
            TxResult handoffRetry = core2.Apply(chest2, AddReq(txId, 11, Wood, 7, true));
            Check.That(handoffRetry.Status == TxStatus.UnknownTx || handoffRetry.Status == TxStatus.Rejected,
                "flagged handoff retry never commits the old tx, got " + handoffRetry.Status);
            Check.That(core2.ExecuteCalls == 0, "handoff retry executes nothing on the new owner (ExecuteCalls==0)");
            Check.Equal(before2, chest2.TotalOf(Wood), "handoff retry mutates nothing: conservation holds");
        }

        /// <summary>
        /// Deadline never finalizes a transient entry: DecideTransient routes
        /// past-deadline due entries to Resend (never FinalQuery/Terminal) and
        /// not-due entries to Wait; backoff is bounded (3s base, doubling, 30s
        /// cap). Control: the classic Decide still terminalizes at deadline.
        /// </summary>
        private static void TRANSIENT_DeadlineNeverFinalizes()
        {
            Check.That(TxPendingDrain.DecideTransient(100f, 50f) == TxPendingDrain.Step.Resend,
                "transient past-deadline due entry resends (never Terminal)");
            Check.That(TxPendingDrain.DecideTransient(100f, 150f) == TxPendingDrain.Step.Wait,
                "transient not-due entry waits");
            Check.That(TxPendingDrain.DecideTransient(1000000f, 50f) == TxPendingDrain.Step.Resend,
                "transient far past deadline still resends (deadline never finalizes)");
            Check.That(Math.Abs(TxPendingDrain.TransientBackoffSeconds(0) - 3f) < 0.001f, "backoff attempt 0 is 3s");
            Check.That(Math.Abs(TxPendingDrain.TransientBackoffSeconds(1) - 6f) < 0.001f, "backoff attempt 1 is 6s");
            Check.That(Math.Abs(TxPendingDrain.TransientBackoffSeconds(2) - 12f) < 0.001f, "backoff attempt 2 is 12s");
            Check.That(Math.Abs(TxPendingDrain.TransientBackoffSeconds(3) - 24f) < 0.001f, "backoff attempt 3 is 24s");
            Check.That(Math.Abs(TxPendingDrain.TransientBackoffSeconds(4) - 30f) < 0.001f, "backoff attempt 4 capped at 30s");
            Check.That(Math.Abs(TxPendingDrain.TransientBackoffSeconds(100) - 30f) < 0.001f, "backoff stays capped (bounded)");
            TxPendingDrain.Step classic = TxPendingDrain.Decide(100f, 50f, 60f, 9, true, true, 4, 2);
            Check.That(classic == TxPendingDrain.Step.Terminal, "control: classic Decide still terminalizes past-deadline non-transient");
        }

        /// <summary>
        /// Retry flag is spoof-safe: MatchesPeer runs FIRST — a stranger's flagged
        /// resend of a transient txId gets an ephemeral Rejected with no payload
        /// and changes NOTHING (map, floor, cache, chest, ExecuteCalls all intact).
        /// </summary>
        private static void TRANSIENT_FlagSpoofSafe()
        {
            Check.That(TxDecision.ClassifySenderBinding(12, Tx(11, 3))
                == TxDecision.SenderBinding.UnboundStranger, "seam: stranger binding detected");
            Check.That(TxDecision.ClassifySenderBinding(11, Tx(11, 3))
                == TxDecision.SenderBinding.BoundOwner, "seam: owner binding detected");

            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5, false)).Status == TxStatus.UnknownTx, "quarantine");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long txId = Tx(11, 3);
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "both-fail transient");

            int execBase = core.ExecuteCalls;
            int processedBase = core.ProcessedCount;
            int floorBase = core.DumpFloor().Count;
            int before = chest.TotalOf(Wood);
            uint hwBefore = core.DumpFloor()[TxIdGen.PeerKey(11)];

            TxResult spoof = core.Apply(chest, AddReq(txId, 12, Wood, 7, true));
            Check.That(spoof.Status == TxStatus.Rejected && !spoof.IsReplay, "stranger flagged resend ephemerally Rejected, got " + spoof.Status);
            Check.That(spoof.Accepted.Count == 0, "spoof answer carries no payload");
            Check.That(core.TransientCount == 1, "spoof leaves the map untouched");
            Check.That(core.ProcessedCount == processedBase, "spoof seeds no cache entry");
            Check.That(core.DumpFloor().Count == floorBase, "spoof moves no floor");
            Check.That(core.DumpFloor()[TxIdGen.PeerKey(11)] == hwBefore, "victim high-water untouched");
            Check.That(core.ExecuteCalls == execBase, "spoof executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "spoof mutates nothing");

            // The true owner's flagged retry still works afterwards (victim txId not burned).
            w.FailRing = false;
            w.FailFloor = false;
            TxResult owner = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(owner.Status == TxStatus.Rejected, "owner flagged retry resolves after spoof, got " + owner.Status);
        }

        /// <summary>
        /// Full end-to-end add-10 sequence with handoff: commit, quarantine,
        /// both-fail transient, flagged-retry resolution, authoritative recovery,
        /// second add-10, restart from persisted bytes ONLY, replay stability, and
        /// conservation across every leg (ExecuteCalls pins single execution).
        /// </summary>
        private static void TRANSIENT_EndToEndAdd10WithHandoff()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            TxResult add10 = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false));
            Check.That(add10.Status == TxStatus.Accepted && !add10.IsReplay, "leg 1: add-10 commits, got " + add10.Status);
            Check.Equal(10, chest.TotalOf(Wood), "leg 1: chest holds 10");

            w.FailSave = true;
            w.FailReload = true;
            TxResult failed = core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 4, false));
            Check.That(failed.Status == TxStatus.UnknownTx, "leg 2: failed tx indeterminate, got " + failed.Status);
            Check.That(core.Quarantined, "leg 2: quarantined");

            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long heldTx = Tx(11, 3);
            TxResult held = core.Apply(chest, AddReq(heldTx, 11, Wood, 6, false));
            Check.That(held.Status == TxStatus.TransientUnavailable, "leg 3: both-fail transient, got " + held.Status);
            Check.That(core.TransientCount == 1, "leg 3: map holds the tx");
            int execAfterHold = core.ExecuteCalls;

            TxResult retryFailing = core.Apply(chest, AddReq(heldTx, 11, Wood, 6, true));
            Check.That(retryFailing.Status == TxStatus.TransientUnavailable, "leg 4: flagged retry while failing stays transient");
            Check.That(core.ExecuteCalls == execAfterHold, "leg 4: ExecuteCalls unchanged (never executes)");
            Check.That(core.Query(heldTx, 11).Status == TxStatus.TransientUnavailable, "leg 4: Query answers transient before cache-miss");

            w.FailRing = false;
            w.FailFloor = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "leg 5: authoritative reload recovers");
            Check.Equal(10, chest.TotalOf(Wood), "leg 5: recovery restores the persisted 10 (speculative 4 discarded)");
            TxResult resolved = core.Apply(chest, AddReq(heldTx, 11, Wood, 6, true));
            Check.That(resolved.Status == TxStatus.Rejected, "leg 5: flagged retry resolves durable-Rejected, got " + resolved.Status);
            Check.That(core.TransientCount == 0, "leg 5: map dropped");
            Check.That(core.ExecuteCalls == execAfterHold, "leg 5: resolution never executes");

            TxResult add10b = core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 10, false));
            Check.That(add10b.Status == TxStatus.Accepted, "leg 6: second add-10 commits, got " + add10b.Status);
            Check.Equal(20, chest.TotalOf(Wood), "leg 6: chest holds 20 (conservation: 10 + 10, refused txs added nothing)");

            // Handoff: fresh core + chest from the persisted copies ONLY.
            TxCore core2;
            ModelChest chest2;
            w.Restart(6, 4, out core2, out chest2);
            Check.That(core2.TransientCount == 0, "leg 7: handoff starts with empty transient map (RAM discarded)");
            Check.Equal(20, chest2.TotalOf(Wood), "leg 7: handoff chest holds 20");

            TxResult replay = core2.Apply(chest2, AddReq(Tx(11, 4), 11, Wood, 10, false));
            Check.That(replay.Status == TxStatus.Accepted && replay.IsReplay, "leg 7: committed add-10 replays original outcome, got " + replay.Status);
            Check.Equal(20, chest2.TotalOf(Wood), "leg 7: replay applies nothing (no double-apply)");
            Check.That(core2.ExecuteCalls == 0, "leg 7: replay executes nothing on the new owner");

            TxResult post = core2.Apply(chest2, AddReq(Tx(11, 5), 11, Wood, 5, false));
            Check.That(post.Status == TxStatus.Accepted, "leg 8: post-handoff add commits, got " + post.Status);
            Check.Equal(25, chest2.TotalOf(Wood), "leg 8: conservation end state 25 (10 + 10 + 5)");
            Check.That(core2.ExecuteCalls == 1, "leg 8: exactly one execution on the new owner");
        }
    }
}
