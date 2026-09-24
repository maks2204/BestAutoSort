using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Transient P0 (v0.5.x wave-3 + IsTransientRetry-authoritative patch):
    /// both-durable-copies-failed is NON-terminal.
    /// All tests run against PRODUCTION code (TxCore.Apply/Query, TxDecision,
    /// TxResponsePolicy, TxPendingDrain, TxRing, TxIdGen): the manager holds a
    /// both-fail txId in a RAM-only transient-refusal map (populated ONLY on
    /// both-fail) and answers TransientUnavailable; the client contract (pinned
    /// through the shared Classify/DecideTransient seams — Unity-coupled
    /// Pending paths cannot run in this harness) is retain Pending + claims and
    /// retry the SAME txId flagged, with bounded backoff and no deadline
    /// finalization. ExecuteCalls pins "never executes" across every retry leg;
    /// chest totals pin conservation; restart legs rebuild from persisted bytes
    /// ONLY (RAM discarded) and the flagged retry reconciles authoritatively on
    /// the clean manager (TxDecision.FlaggedRefusalTerminal: ring persisted =>
    /// stable Rejected, floor-only => terminal UnknownTx permanently stale,
    /// both failed => TransientUnavailable same-tx retained). Cached outcomes
    /// beat the flag; the spoof gate runs before any flagged mutation.
    /// Production OnTxRequest/RespondQuery paths are Unity-coupled and cannot
    /// run in this harness — they mirror the pinned core legs statement by
    /// statement (same seam, same order) and are covered by build + review.
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

        private static TxRequest TakeReq(long txId, long sender, ItemKey key, int amount, bool transientRetry)
        {
            TxRequest r = new TxRequest();
            r.TxId = txId;
            r.Op = TxOp.Take;
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
            Console.WriteLine("TRANSIENT_FlaggedRetryPartialTerminalizes");
            TRANSIENT_FlaggedRetryPartialTerminalizes();
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
            Console.WriteLine("TRANSIENT_CleanHandoffFlaggedMutationReconcilesDurableRejected");
            TRANSIENT_CleanHandoffFlaggedMutationReconcilesDurableRejected();
            Console.WriteLine("TRANSIENT_CleanHandoffFlaggedMutationBothFailStaysTransient");
            TRANSIENT_CleanHandoffFlaggedMutationBothFailStaysTransient();
            Console.WriteLine("TRANSIENT_CleanHandoffRingOnlyTerminalizesRejected");
            TRANSIENT_CleanHandoffRingOnlyTerminalizesRejected();
            Console.WriteLine("TRANSIENT_CleanHandoffFloorOnlyTerminalizesUnknownTx");
            TRANSIENT_CleanHandoffFloorOnlyTerminalizesUnknownTx();
            Console.WriteLine("TRANSIENT_CleanHandoffFlaggedQueryNeverTerminal");
            TRANSIENT_CleanHandoffFlaggedQueryNeverTerminal();
            Console.WriteLine("TRANSIENT_CleanHandoffCachedAcceptedBeatsFlag");
            TRANSIENT_CleanHandoffCachedAcceptedBeatsFlag();
            Console.WriteLine("TRANSIENT_CleanHandoffCrossOpGuardKept");
            TRANSIENT_CleanHandoffCrossOpGuardKept();
            Console.WriteLine("TRANSIENT_CleanHandoffSpoofFlagCannotBurnVictim");
            TRANSIENT_CleanHandoffSpoofFlagCannotBurnVictim();
            Console.WriteLine("TRANSIENT_CleanHandoffWireV2DefaultsFalse");
            TRANSIENT_CleanHandoffWireV2DefaultsFalse();
            Console.WriteLine("TRANSIENT_CleanHandoffConservationAcrossHandoff");
            TRANSIENT_CleanHandoffConservationAcrossHandoff();
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
            // Flagged-retry reconciliation seam (IsTransientRetry-authoritative patch):
            // single-copy-durable terminalizes — the tx never executes, so one
            // persisted copy is a stable answer. Quarantine-fresh / handoff-drop
            // paths keep the strict both-copies HandoffDropTerminal above.
            Check.That(TxDecision.FlaggedRefusalTerminal(true, true) == TxStatus.Rejected, "flagged matrix: both durable => Rejected");
            Check.That(TxDecision.FlaggedRefusalTerminal(true, false) == TxStatus.Rejected, "flagged matrix: ring-only => stable Rejected (ring-embedded floor)");
            Check.That(TxDecision.FlaggedRefusalTerminal(false, true) == TxStatus.UnknownTx, "flagged matrix: floor-only => terminal UnknownTx");
            Check.That(TxDecision.FlaggedRefusalTerminal(false, false) == TxStatus.TransientUnavailable, "flagged matrix: both failed => TransientUnavailable");

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
        /// Partial-liveness leg (the core gate production mirrors in OnTxRequest
        /// through TxDecision.FlaggedRefusalTerminal): a flagged same-tx mutation
        /// retry that persists EXACTLY ONE copy terminalizes — the tx never
        /// executes, so one durable copy is a stable answer. Ring-only resolves
        /// stable-Rejected (replayable via the ring-embedded floor); floor-only
        /// answers terminal UnknownTx (seed evicted, map dropped, permanently
        /// stale-gated); healing the writes lets the SAME flagged txId reconcile
        /// durable-Rejected with no new txId needed. Nothing ever executes.
        /// </summary>
        private static void TRANSIENT_FlaggedRetryPartialTerminalizes()
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

            // Partial direction 1 (ring persists, floor fails): single-copy-durable
            // terminalizes — stable Rejected, replayable, never executes. The ring
            // entry carries the refusal and the ring-embedded floor copy.
            w.FailRing = false;
            TxResult ringOnly = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(ringOnly.Status == TxStatus.Rejected && !ringOnly.IsReplay,
                "flagged retry with ring-only persist resolves stable-Rejected, got " + ringOnly.Status);
            Check.That(core.TransientCount == 0, "ring-only resolution drops the map entry");
            Check.That(core.ProcessedCount == processedBase + 1, "ring-only seed kept (replayable)");
            Check.That(core.ExecuteCalls == execBase, "ring-only resolution executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "ring-only resolution mutates nothing");
            TxResult ringReplay = core.Apply(chest, AddReq(txId, 11, Wood, 7, false));
            Check.That(ringReplay.Status == TxStatus.Rejected && ringReplay.IsReplay,
                "ring-only Rejected replays stably, got " + ringReplay.Status);
            Check.That(core.ExecuteCalls == execBase, "replay executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "replay mutates nothing");

            // Partial direction 2 (floor persists, ring fails): terminal UnknownTx —
            // seed evicted, map dropped, permanently stale-gated, never executes.
            // Healing the writes lets the SAME flagged txId reconcile (liveness:
            // a floor-only terminal wedges nothing).
            TransientWorld w2 = new TransientWorld();
            TxCore coreB = new TxCore();
            coreB.Durability = w2.Hooks();
            ModelChest chestB = new ModelChest(6, 4);
            Check.That(coreB.Apply(chestB, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "seed B commits");
            w2.FailSave = true;
            w2.FailReload = true;
            Check.That(coreB.Apply(chestB, AddReq(Tx(11, 2), 11, Wood, 5, false)).Status == TxStatus.UnknownTx, "quarantine B");
            Check.That(coreB.Quarantined, "quarantined B");
            w2.FailSave = false;
            w2.FailRing = true;
            w2.FailFloor = true;
            long txB = Tx(11, 3);
            Check.That(coreB.Apply(chestB, AddReq(txB, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "both-fail B transient");
            Check.That(coreB.TransientCount == 1, "both-fail B populates the map");
            int execB = coreB.ExecuteCalls;
            int beforeB = chestB.TotalOf(Wood);
            int processedB = coreB.ProcessedCount;
            w2.FailFloor = false;
            TxResult floorOnly = coreB.Apply(chestB, AddReq(txB, 11, Wood, 7, true));
            Check.That(floorOnly.Status == TxStatus.UnknownTx && !floorOnly.IsReplay,
                "flagged retry with floor-only persist answers terminal UnknownTx, got " + floorOnly.Status);
            Check.That(coreB.TransientCount == 0, "floor-only terminal drops the map entry");
            Check.That(coreB.ProcessedCount == processedB, "floor-only seed evicted (never cached)");
            Check.That(coreB.ExecuteCalls == execB, "floor-only terminal executes nothing");
            Check.Equal(beforeB, chestB.TotalOf(Wood), "floor-only terminal mutates nothing");
            uint hwB;
            Check.That(coreB.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hwB) && hwB == 3,
                "floor high-water kept (tx permanently stale-gated)");
            // Permanently stale: unflagged resend, flagged resend and Query all
            // answer the same UnknownTx without executing.
            Check.That(coreB.Apply(chestB, AddReq(txB, 11, Wood, 7, false)).Status == TxStatus.UnknownTx,
                "unflagged resend stale-gated UnknownTx");
            Check.That(coreB.Apply(chestB, AddReq(txB, 11, Wood, 7, true)).Status == TxStatus.UnknownTx,
                "flagged resend still UnknownTx (floor-only reconcile, never executes)");
            Check.That(coreB.Query(txB, 11).Status == TxStatus.UnknownTx, "Query still UnknownTx");
            Check.That(coreB.ExecuteCalls == execB, "stale legs execute nothing");
            Check.Equal(beforeB, chestB.TotalOf(Wood), "stale legs mutate nothing");
            // Liveness: writes heal => the SAME flagged txId reconciles
            // durable-Rejected (the flagged branch runs before the stale gate).
            w2.FailRing = false;
            TxResult healedB = coreB.Apply(chestB, AddReq(txB, 11, Wood, 7, true));
            Check.That(healedB.Status == TxStatus.Rejected && !healedB.IsReplay,
                "healed flagged retry resolves durable-Rejected, got " + healedB.Status);
            Check.That(coreB.TransientCount == 0, "healed resolution holds no map entry");
            Check.That(coreB.ExecuteCalls == execB, "healed resolution executes nothing");
            Check.Equal(beforeB, chestB.TotalOf(Wood), "healed resolution mutates nothing");
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
        /// Ownership loss drops the RAM map; the held txId reconciles across a
        /// clean handoff: the post-fence owner check clears transient; a restart
        /// from persisted bytes ONLY starts with an empty map (pre-asserted with
        /// floor==2 and no tx3 record); the flagged retry of the HELD txId on the
        /// new owner reconciles durable-Rejected through the authoritative flagged
        /// branch (never executes, even with the floor below the held counter)
        /// and replays stably afterwards.
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

            // Handoff leg (the old tx4 false-positive is gone: the HELD txId itself
            // reconciles across a clean restart). A fresh world holds tx3, then
            // restarts from persisted bytes ONLY.
            TransientWorld hw = new TransientWorld();
            TxCore hcore = new TxCore();
            hcore.Durability = hw.Hooks();
            ModelChest hchest = new ModelChest(6, 4);
            Check.That(hcore.Apply(hchest, AddReq(Tx(11, 1), 11, Wood, 5, false)).Status == TxStatus.Accepted, "handoff seed commits");
            hw.FailSave = true;
            hw.FailReload = true;
            Check.That(hcore.Apply(hchest, AddReq(Tx(11, 2), 11, Wood, 5, false)).Status == TxStatus.UnknownTx, "handoff quarantining tx indeterminate");
            hw.FailSave = false;
            hw.FailRing = true;
            hw.FailFloor = true;
            long held = Tx(11, 3);
            Check.That(hcore.Apply(hchest, AddReq(held, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "handoff both-fail transient");
            Check.That(hcore.TransientCount == 1, "held before handoff");

            TxCore core2;
            ModelChest chest2;
            hw.Restart(6, 4, out core2, out chest2);
            // Pre-asserts: clean manager — empty RAM map, persisted floor at the
            // quarantining fence (counter 2), no record of the held txId.
            Check.That(core2.TransientCount == 0, "restart starts with an empty transient map (RAM discarded)");
            uint hwFloor;
            Check.That(core2.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hwFloor) && hwFloor == 2,
                "persisted floor kept at the quarantining fence (floor==2, held counter==3)");
            Check.That(core2.ProcessedCount == 1, "no tx3 record survived the restart (only the seed replays)");
            Check.That(core2.Query(held, 11).Status == TxStatus.UnknownTx, "unflagged Query of the held tx finds no record");
            int before2 = chest2.TotalOf(Wood);
            // The flagged retry of the HELD txId reconciles durable-Rejected on the
            // clean manager (authoritative flagged branch — never executes, even
            // though the floor sits below the held counter).
            hw.FailRing = false;
            hw.FailFloor = false;
            TxResult handoffRetry = core2.Apply(chest2, AddReq(held, 11, Wood, 7, true));
            Check.That(handoffRetry.Status == TxStatus.Rejected && !handoffRetry.IsReplay,
                "flagged handoff retry reconciles durable-Rejected, got " + handoffRetry.Status);
            Check.That(core2.TransientCount == 0, "clean handoff holds no map entry");
            Check.That(core2.ExecuteCalls == 0, "handoff retry executes nothing on the new owner (ExecuteCalls==0)");
            Check.Equal(before2, chest2.TotalOf(Wood), "handoff retry mutates nothing: conservation holds");
            TxResult handoffReplay = core2.Apply(chest2, AddReq(held, 11, Wood, 7, false));
            Check.That(handoffReplay.Status == TxStatus.Rejected && handoffReplay.IsReplay,
                "reconciled refusal replays stably, got " + handoffReplay.Status);
            Check.Equal(before2, chest2.TotalOf(Wood), "replay mutates nothing");
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

            // The reconciled held tx replays its Rejected even when flagged: the
            // exact-cache replay beats the flag (never re-reconciles, never executes).
            TxResult heldReplay = core2.Apply(chest2, AddReq(heldTx, 11, Wood, 6, true));
            Check.That(heldReplay.Status == TxStatus.Rejected && heldReplay.IsReplay,
                "leg 7: reconciled held tx replays Rejected even when flagged, got " + heldReplay.Status);
            Check.Equal(20, chest2.TotalOf(Wood), "leg 7: flagged replay applies nothing");
            Check.That(core2.ExecuteCalls == 0, "leg 7: flagged replay executes nothing on the new owner");

            TxResult replay = core2.Apply(chest2, AddReq(Tx(11, 4), 11, Wood, 10, false));
            Check.That(replay.Status == TxStatus.Accepted && replay.IsReplay, "leg 7: committed add-10 replays original outcome, got " + replay.Status);
            Check.Equal(20, chest2.TotalOf(Wood), "leg 7: replay applies nothing (no double-apply)");
            Check.That(core2.ExecuteCalls == 0, "leg 7: replay executes nothing on the new owner");

            TxResult post = core2.Apply(chest2, AddReq(Tx(11, 5), 11, Wood, 5, false));
            Check.That(post.Status == TxStatus.Accepted, "leg 8: post-handoff add commits, got " + post.Status);
            Check.Equal(25, chest2.TotalOf(Wood), "leg 8: conservation end state 25 (10 + 10 + 5)");
            Check.That(core2.ExecuteCalls == 1, "leg 8: exactly one execution on the new owner");
        }

        /// <summary>
        /// Clean-handoff core proof: a flagged mutation retry of the HELD txId on a
        /// manager with an empty RAM map and the floor BELOW the held counter
        /// reconciles durable-Rejected (authoritative flagged branch — never
        /// executes), replays stably, and a fresh txId still commits afterwards
        /// with exact conservation.
        /// </summary>
        private static void TRANSIENT_CleanHandoffFlaggedMutationReconcilesDurableRejected()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 4, false)).Status == TxStatus.UnknownTx, "quarantining tx indeterminate");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long held = Tx(11, 3);
            Check.That(core.Apply(chest, AddReq(held, 11, Wood, 6, false)).Status == TxStatus.TransientUnavailable, "held transient");

            TxCore core2;
            ModelChest chest2;
            w.Restart(6, 4, out core2, out chest2);
            Check.That(core2.TransientCount == 0, "pre: empty transient map");
            uint hw;
            Check.That(core2.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 2, "pre: floor==2 (held counter==3)");
            Check.That(core2.ProcessedCount == 1, "pre: no tx3 record (only the seed replays)");
            int before = chest2.TotalOf(Wood);
            Check.Equal(10, before, "pre: handoff chest holds the seed 10");

            w.FailRing = false;
            w.FailFloor = false;
            TxResult reconciled = core2.Apply(chest2, AddReq(held, 11, Wood, 6, true));
            Check.That(reconciled.Status == TxStatus.Rejected && !reconciled.IsReplay,
                "flagged handoff retry reconciles durable-Rejected, got " + reconciled.Status);
            Check.That(core2.TransientCount == 0, "no map entry on the clean manager");
            Check.That(core2.ExecuteCalls == 0, "reconciliation never executes (ExecuteCalls==0)");
            Check.Equal(before, chest2.TotalOf(Wood), "reconciliation mutates nothing");
            TxResult replay = core2.Apply(chest2, AddReq(held, 11, Wood, 6, false));
            Check.That(replay.Status == TxStatus.Rejected && replay.IsReplay, "reconciled refusal replays stably");
            Check.That(core2.Apply(chest2, AddReq(Tx(11, 4), 11, Wood, 5, false)).Status == TxStatus.Accepted, "fresh txId commits after reconcile");
            Check.Equal(before + 5, chest2.TotalOf(Wood), "conservation: only the fresh tx applied");
            Check.That(core2.ExecuteCalls == 1, "exactly one execution on the new owner");
        }

        /// <summary>
        /// Clean-manager flagged retry with both copies failing: answered
        /// TransientUnavailable (non-terminal, same-tx retained) and noted in the
        /// map — even though no entry existed and the floor sits below the tx
        /// counter (a fresh-looking flagged txId never runs as a new mutation).
        /// </summary>
        private static void TRANSIENT_CleanHandoffFlaggedMutationBothFailStaysTransient()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false)).Status == TxStatus.Accepted, "seed commits");
            Check.That(core.TransientCount == 0, "pre: empty transient map");
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 1, "pre: floor==1");
            Check.That(core.ProcessedCount == 1, "pre: only the seed cached");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);

            w.FailRing = true;
            w.FailFloor = true;
            long freshFlagged = Tx(11, 9);
            TxResult transient = core.Apply(chest, AddReq(freshFlagged, 11, Wood, 7, true));
            Check.That(transient.Status == TxStatus.TransientUnavailable && !transient.IsReplay,
                "clean-manager flagged both-fail stays transient, got " + transient.Status);
            Check.That(core.TransientCount == 1 && core.DumpTransient().ContainsKey(freshFlagged), "both-fail noted in the map");
            Check.That(core.ProcessedCount == 1, "both-fail seed evicted (never cached)");
            Check.That(core.ExecuteCalls == execBase, "flagged reconcile executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "flagged reconcile mutates nothing");

            TxResult reminded = core.Apply(chest, AddReq(freshFlagged, 11, Wood, 7, false));
            Check.That(reminded.Status == TxStatus.TransientUnavailable, "unflagged resend reminded transient");
            Check.That(core.TransientCount == 1, "map kept while writes fail");
            Check.That(core.ExecuteCalls == execBase, "reminder executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "reminder mutates nothing");
        }

        /// <summary>
        /// Clean-manager ring-only: the flagged retry resolves stable-Rejected
        /// (replayable — the ring entry carries the refusal and the ring-embedded
        /// floor, so the outcome survives a restart), never executes.
        /// </summary>
        private static void TRANSIENT_CleanHandoffRingOnlyTerminalizesRejected()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false)).Status == TxStatus.Accepted, "seed commits");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);
            int processedBase = core.ProcessedCount;

            w.FailRing = false;
            w.FailFloor = true;
            long txId = Tx(11, 9);
            TxResult ringOnly = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(ringOnly.Status == TxStatus.Rejected && !ringOnly.IsReplay,
                "clean-manager ring-only resolves stable-Rejected, got " + ringOnly.Status);
            Check.That(core.TransientCount == 0, "no map entry");
            Check.That(core.ProcessedCount == processedBase + 1, "ring-only seed kept (replayable)");
            Check.That(core.ExecuteCalls == execBase, "resolution executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "resolution mutates nothing");
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, true)).Status == TxStatus.Rejected, "flagged replay stable");

            TxCore core2;
            ModelChest chest2;
            w.Restart(6, 4, out core2, out chest2);
            Check.That(core2.Query(txId, 11).Status == TxStatus.Rejected, "ring-embedded refusal replays after restart");
            Check.That(core2.Apply(chest2, AddReq(txId, 11, Wood, 7, true)).Status == TxStatus.Rejected, "flagged retry replays after restart");
            Check.That(core2.ExecuteCalls == 0, "restart legs execute nothing");
            Check.Equal(before, chest2.TotalOf(Wood), "restart legs mutate nothing");
        }

        /// <summary>
        /// Clean-manager floor-only: the flagged retry answers terminal UnknownTx
        /// (seed evicted, map dropped, permanently stale-gated) and never
        /// executes; healing the writes lets the SAME flagged txId reconcile
        /// (liveness — nothing wedged).
        /// </summary>
        private static void TRANSIENT_CleanHandoffFloorOnlyTerminalizesUnknownTx()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false)).Status == TxStatus.Accepted, "seed commits");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);
            int processedBase = core.ProcessedCount;

            w.FailRing = true;
            w.FailFloor = false;
            long txId = Tx(11, 9);
            TxResult floorOnly = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(floorOnly.Status == TxStatus.UnknownTx && !floorOnly.IsReplay,
                "clean-manager floor-only answers terminal UnknownTx, got " + floorOnly.Status);
            Check.That(core.TransientCount == 0, "no map entry");
            Check.That(core.ProcessedCount == processedBase, "floor-only seed evicted (never cached)");
            Check.That(core.ExecuteCalls == execBase, "floor-only terminal executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "floor-only terminal mutates nothing");
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 9, "floor high-water kept (permanently stale)");
            Check.That(core.Apply(chest, AddReq(txId, 11, Wood, 7, false)).Status == TxStatus.UnknownTx, "unflagged resend stale-gated");
            Check.That(core.Query(txId, 11).Status == TxStatus.UnknownTx, "Query stale-gated");
            Check.That(core.ExecuteCalls == execBase, "stale legs execute nothing");
            Check.Equal(before, chest.TotalOf(Wood), "stale legs mutate nothing");

            w.FailRing = false;
            TxResult healed = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(healed.Status == TxStatus.Rejected && !healed.IsReplay,
                "healed flagged retry reconciles durable-Rejected, got " + healed.Status);
            Check.That(core.ExecuteCalls == execBase, "healed resolution executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "healed resolution mutates nothing");
        }

        /// <summary>
        /// Flagged Query on a clean manager: no cache + no map + flag =>
        /// TransientUnavailable (never terminal UnknownTx), never executes or
        /// mutates; unflagged Query of the same txId still answers UnknownTx;
        /// strangers get Rejected with no payload; cached outcomes beat the flag.
        /// </summary>
        private static void TRANSIENT_CleanHandoffFlaggedQueryNeverTerminal()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false)).Status == TxStatus.Accepted, "seed commits");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);
            int processedBase = core.ProcessedCount;
            int floorBase = core.DumpFloor().Count;

            long unknown = Tx(11, 9);
            TxResult flagged = core.Query(unknown, 11, true);
            Check.That(flagged.Status == TxStatus.TransientUnavailable && !flagged.IsReplay,
                "flagged Query with no record answers TransientUnavailable, got " + flagged.Status);
            TxResult unflagged = core.Query(unknown, 11);
            Check.That(unflagged.Status == TxStatus.UnknownTx, "unflagged Query still UnknownTx, got " + unflagged.Status);
            TxResult stranger = core.Query(unknown, 12, true);
            Check.That(stranger.Status == TxStatus.Rejected, "stranger flagged Query Rejected, got " + stranger.Status);
            Check.That(stranger.Accepted.Count == 0, "stranger Query leaks no payload");
            Check.That(core.ProcessedCount == processedBase, "queries seed no cache entry");
            Check.That(core.TransientCount == 0, "queries note no map entry");
            Check.That(core.DumpFloor().Count == floorBase, "queries move no floor");
            Check.That(core.ExecuteCalls == execBase, "queries never execute");
            Check.Equal(before, chest.TotalOf(Wood), "queries mutate nothing");

            TxResult cachedFlagged = core.Query(Tx(11, 1), 11, true);
            Check.That(cachedFlagged.Status == TxStatus.Accepted && cachedFlagged.IsReplay,
                "cached outcome beats the flag, got " + cachedFlagged.Status);
        }

        /// <summary>
        /// Cached Accepted/Take beat the flag: a flagged mutation retry of a
        /// COMMITTED txId replays the original outcome (never reconciles, never
        /// re-executes) with exact conservation.
        /// </summary>
        private static void TRANSIENT_CleanHandoffCachedAcceptedBeatsFlag()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            TxResult add = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false));
            Check.That(add.Status == TxStatus.Accepted && !add.IsReplay, "add commits, got " + add.Status);
            Check.Equal(10, chest.TotalOf(Wood), "chest holds 10");
            int execBase = core.ExecuteCalls;

            TxResult flaggedAdd = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, true));
            Check.That(flaggedAdd.Status == TxStatus.Accepted && flaggedAdd.IsReplay,
                "flagged retry of committed Add replays Accepted, got " + flaggedAdd.Status);
            Check.Equal(10, chest.TotalOf(Wood), "Add N stays N across the flagged retry");
            Check.That(core.ExecuteCalls == execBase, "flagged replay executes nothing");

            chest.AddItem(Wood, 20, 50);
            Check.Equal(30, chest.TotalOf(Wood), "stocked 20 (total 30)");
            TxResult take = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 20, false));
            Check.That(take.Status == TxStatus.Accepted && !take.IsReplay, "take-20 commits, got " + take.Status);
            Check.Equal(10, chest.TotalOf(Wood), "take debited exactly 20");
            TxResult flaggedTake = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 20, true));
            Check.That(flaggedTake.Status == TxStatus.Accepted && flaggedTake.IsReplay,
                "flagged retry of committed Take replays, got " + flaggedTake.Status);
            Check.Equal(10, chest.TotalOf(Wood), "Take 20 stays 20 across the flagged retry (no double-debit)");
            Check.That(core.ExecuteCalls == execBase + 1, "only the original Take executed");
        }

        /// <summary>
        /// Cross-op guard kept: a flagged retry carrying a DIFFERENT op than the
        /// cached outcome answers UnknownTx (never the cached payload, never
        /// executes) and the original op still replays afterwards.
        /// </summary>
        private static void TRANSIENT_CleanHandoffCrossOpGuardKept()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false)).Status == TxStatus.Accepted, "add commits");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);

            TxResult cross = core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 4, true));
            Check.That(cross.Status == TxStatus.UnknownTx && !cross.IsReplay,
                "flagged cross-op retry refused Indeterminate, got " + cross.Status);
            Check.That(cross.Accepted.Count == 0, "cross-op refusal carries no payload");
            Check.That(core.ExecuteCalls == execBase, "cross-op retry executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "cross-op retry mutates nothing");
            TxResult original = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, true));
            Check.That(original.Status == TxStatus.Accepted && original.IsReplay,
                "original op still replays after the cross-op refusal, got " + original.Status);
            Check.Equal(before, chest.TotalOf(Wood), "original replay mutates nothing");
        }

        /// <summary>
        /// Spoof-flag on a clean manager: a stranger's flagged mutation gets an
        /// ephemeral Rejected and changes NOTHING (map, floor, cache, chest,
        /// ExecuteCalls) — the victim's txId is never burned, and the owner's
        /// flagged retry still reconciles afterwards.
        /// </summary>
        private static void TRANSIENT_CleanHandoffSpoofFlagCannotBurnVictim()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false)).Status == TxStatus.Accepted, "seed commits");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);
            long victim = Tx(11, 9);

            w.FailRing = true;
            w.FailFloor = true;
            TxResult spoof = core.Apply(chest, AddReq(victim, 12, Wood, 7, true));
            Check.That(spoof.Status == TxStatus.Rejected && !spoof.IsReplay, "stranger flagged clean-manager resend ephemerally Rejected, got " + spoof.Status);
            Check.That(spoof.Accepted.Count == 0, "spoof answer carries no payload");
            Check.That(core.TransientCount == 0, "spoof notes no map entry");
            Check.That(core.ProcessedCount == 1, "spoof seeds no cache entry");
            Check.That(core.DumpFloor().Count == 1, "spoof moves no floor");
            Check.That(core.ExecuteCalls == execBase, "spoof executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "spoof mutates nothing");

            TxResult owner = core.Apply(chest, AddReq(victim, 11, Wood, 7, true));
            Check.That(owner.Status == TxStatus.TransientUnavailable, "victim txId not burned: owner flagged retry held transient, got " + owner.Status);
            w.FailRing = false;
            w.FailFloor = false;
            TxResult resolved = core.Apply(chest, AddReq(victim, 11, Wood, 7, true));
            Check.That(resolved.Status == TxStatus.Rejected, "owner flagged retry resolves after spoof, got " + resolved.Status);
            Check.That(core.ExecuteCalls == execBase, "resolution executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "resolution mutates nothing");
        }

        /// <summary>
        /// Wire v2 analog: the flag defaults false (never set on a first attempt),
        /// so an unflagged resend of a held txId is only reminded
        /// TransientUnavailable (never reconciles, never executes); setting the
        /// flag lets the SAME txId resolve once the writes heal. Production
        /// DecodeCall fails a truncated v3 frame closed to false and rejects
        /// unsupported versions (fail-safe UnknownTx, never executed) — verified
        /// by inspection + build (Unity-coupled, outside this harness).
        /// </summary>
        private static void TRANSIENT_CleanHandoffWireV2DefaultsFalse()
        {
            Check.That(!new TxRequest().IsTransientRetry, "wire: flag defaults false (v2 frames predate it)");
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
            TxRequest v2style = AddReq(txId, 11, Wood, 7, false);
            Check.That(!v2style.IsTransientRetry, "v2-style resend carries no flag");
            Check.That(core.Apply(chest, v2style).Status == TxStatus.TransientUnavailable, "both-fail transient");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);

            TxResult reminded = core.Apply(chest, AddReq(txId, 11, Wood, 7, false));
            Check.That(reminded.Status == TxStatus.TransientUnavailable, "unflagged resend only reminded (never reconciles)");
            Check.That(core.TransientCount == 1, "unflagged resend keeps the map entry");
            Check.That(core.ExecuteCalls == execBase, "unflagged resend executes nothing");

            w.FailRing = false;
            w.FailFloor = false;
            TxResult resolved = core.Apply(chest, AddReq(txId, 11, Wood, 7, true));
            Check.That(resolved.Status == TxStatus.Rejected, "flagged retry resolves once healed, got " + resolved.Status);
            Check.That(core.ExecuteCalls == execBase, "resolution executes nothing");
            Check.Equal(before, chest.TotalOf(Wood), "no leg mutated the chest");
        }

        /// <summary>
        /// Conservation across handoff: add-10 + take-6 commit, a quarantine +
        /// both-fail hold follows, the restart rebuilds from persisted bytes ONLY
        /// (pre-asserted empty map, fence floor, held totals), the flagged retry
        /// reconciles without moving a single item, and a fresh add commits the
        /// exact end state with exactly one new-owner execution.
        /// </summary>
        private static void TRANSIENT_CleanHandoffConservationAcrossHandoff()
        {
            TransientWorld w = new TransientWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false)).Status == TxStatus.Accepted, "add-10 commits");
            chest.AddItem(Wood, 10, 50);
            Check.That(core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 6, false)).Status == TxStatus.Accepted, "take-6 commits");
            Check.Equal(14, chest.TotalOf(Wood), "live holds 14 (10 + 10 - 6)");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 4, false)).Status == TxStatus.UnknownTx, "quarantining tx indeterminate");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
            long held = Tx(11, 4);
            Check.That(core.Apply(chest, AddReq(held, 11, Wood, 6, false)).Status == TxStatus.TransientUnavailable, "held transient");

            TxCore core2;
            ModelChest chest2;
            w.Restart(6, 4, out core2, out chest2);
            Check.That(core2.TransientCount == 0, "pre: empty transient map");
            uint hw;
            Check.That(core2.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 3, "pre: floor==3 (held counter==4)");
            Check.That(core2.ProcessedCount == 2, "pre: only committed txs replay (no held record)");
            Check.Equal(14, chest2.TotalOf(Wood), "pre: handoff chest holds 14");
            int before = chest2.TotalOf(Wood);

            w.FailRing = false;
            w.FailFloor = false;
            TxResult reconciled = core2.Apply(chest2, AddReq(held, 11, Wood, 6, true));
            Check.That(reconciled.Status == TxStatus.Rejected && !reconciled.IsReplay,
                "flagged handoff retry reconciles durable-Rejected, got " + reconciled.Status);
            Check.That(core2.ExecuteCalls == 0, "reconciliation never executes (ExecuteCalls==0)");
            Check.Equal(before, chest2.TotalOf(Wood), "reconciliation moves nothing");
            Check.That(core2.Apply(chest2, AddReq(Tx(11, 5), 11, Wood, 5, false)).Status == TxStatus.Accepted, "fresh add-5 commits");
            Check.Equal(19, chest2.TotalOf(Wood), "end state 19 (14 + 5, refused txs added nothing)");
            Check.That(core2.ExecuteCalls == 1, "exactly one execution on the new owner");
        }
        
    }
}
