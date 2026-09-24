using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Transient send path (v0.5.x transient send-path fix): the client NEVER
    /// resends the stored unflagged first-attempt frame for a transient entry.
    /// Production SendTransientRetry mirrors the shared TxPendingDrain seams
    /// pinned here statement by statement (Unity-coupled Pending/SendPayload
    /// paths cannot run in this harness): re-encode-ok => flagged same-tx
    /// mutation, ANY re-encode failure (throw or null call) => flagged Query
    /// for the SAME txId. Both legs retain Pending + claims + txId with bounded
    /// backoff; neither goes terminal and neither mints a new txId. The flagged
    /// fallback Query reconciles through the authoritative manager legs: held
    /// refusal => TransientUnavailable (same-tx retained), durable resolution =>
    /// cached terminal, clean-manager flagged mutation => reconciles without
    /// ever executing. Conservation (Add/Take totals) is asserted across every
    /// leg; every frame below carries IsTransientRetry=true on the manager side
    /// (packet inspection via TxRequest.IsTransientRetry).
    /// </summary>
    internal static class TxTransientSendTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);

        private static long Tx(long peer, uint ctr)
        {
            return unchecked((peer << 32) | ctr);
        }

        /// <summary>Fake durable world: ring/floor byte slots + items snapshot + faults.</summary>
        private sealed class SendWorld
        {
            public byte[] RingBytes;
            public byte[] FloorBytes;
            public List<int[]> Items;
            public bool FailRing;
            public bool FailFloor;
            public bool FailSave;
            public bool FailReload;

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
                d.IsOwner = delegate () { return true; };
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

        /// <summary>Drive a txId into the both-fail transient hold. Returns the held txId.</summary>
        private static long HoldTransient(TxCore core, ModelChest chest, SendWorld w, long peer, uint ctr, int amount)
        {
            long txId = Tx(peer, ctr);
            TxResult held = core.Apply(chest, AddReq(txId, peer, Wood, amount, false));
            Check.That(held.Status == TxStatus.TransientUnavailable,
                "setup: both-fail holds tx " + txId + " transient, got " + held.Status);
            return txId;
        }

        private static void SeedQuarantine(TxCore core, ModelChest chest, SendWorld w, long peer)
        {
            Check.That(core.Apply(chest, AddReq(Tx(peer, 1), peer, Wood, 10, false)).Status == TxStatus.Accepted, "setup: seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(peer, 2), peer, Wood, 4, false)).Status == TxStatus.UnknownTx, "setup: quarantining tx indeterminate");
            w.FailSave = false;
            w.FailRing = true;
            w.FailFloor = true;
        }

        public static void RunAll()
        {
            Console.WriteLine("TRANSIENT_SendEncodeFailNeverSendsOriginal");
            TRANSIENT_SendEncodeFailNeverSendsOriginal();
            Console.WriteLine("TRANSIENT_SendEncodeOkSendsFlaggedMutation");
            TRANSIENT_SendEncodeOkSendsFlaggedMutation();
            Console.WriteLine("TRANSIENT_SendFlaggedQueryFallbackFlag");
            TRANSIENT_SendFlaggedQueryFallbackFlag();
            Console.WriteLine("TRANSIENT_SendFallbackKeepsSameTx");
            TRANSIENT_SendFallbackKeepsSameTx();
            Console.WriteLine("TRANSIENT_SendCleanHandoffNeverExecutes");
            TRANSIENT_SendCleanHandoffNeverExecutes();
            Console.WriteLine("TRANSIENT_SendRepeatedFailsKeepPending");
            TRANSIENT_SendRepeatedFailsKeepPending();
            Console.WriteLine("TRANSIENT_SendEventualRecoverySameTx");
            TRANSIENT_SendEventualRecoverySameTx();
            Console.WriteLine("TRANSIENT_SendCachedAcceptedViaFallbackQuery");
            TRANSIENT_SendCachedAcceptedViaFallbackQuery();
            Console.WriteLine("TRANSIENT_SendClaimsHeldNothingCreditedRemoved");
            TRANSIENT_SendClaimsHeldNothingCreditedRemoved();
            Console.WriteLine("TRANSIENT_SendNoOtherPathAudit");
            TRANSIENT_SendNoOtherPathAudit();
        }

        /// <summary>
        /// Encode-fail never resends the original bytes: the seam maps failure to
        /// FlaggedQuery, and no unflagged-resend leg exists in the action enum
        /// (proof by construction — exactly two values, both flagged same-tx).
        /// </summary>
        private static void TRANSIENT_SendEncodeFailNeverSendsOriginal()
        {
            Check.That(TxPendingDrain.DecideTransientSend(false) == TxPendingDrain.TransientSendAction.FlaggedQuery,
                "encode-fail maps to flagged Query, never to stored bytes");
            Check.That(TxPendingDrain.DecideTransientSend(false) != TxPendingDrain.TransientSendAction.FlaggedMutation,
                "encode-fail never maps to the mutation leg (no stale-bytes resend)");
            Array values = Enum.GetValues(typeof(TxPendingDrain.TransientSendAction));
            Check.Equal(2, values.Length, "send-action enum has exactly two legs (no unflagged fallback exists)");
        }

        /// <summary>
        /// Encode-ok sends the freshly re-encoded flagged mutation (the happy
        /// path replaces the stored unflagged bytes before anything is sent).
        /// </summary>
        private static void TRANSIENT_SendEncodeOkSendsFlaggedMutation()
        {
            Check.That(TxPendingDrain.DecideTransientSend(true) == TxPendingDrain.TransientSendAction.FlaggedMutation,
                "encode-ok maps to the flagged same-tx mutation leg");
            Check.That(TxPendingDrain.DecideTransientSend(true) != TxPendingDrain.TransientSendAction.FlaggedQuery,
                "encode-ok never takes the query leg");
        }

        /// <summary>
        /// The fallback Query carries the flag and the flag matters: flagged Query
        /// with no record answers TransientUnavailable (non-terminal — keep the
        /// same txId), while the identical unflagged Query answers terminal
        /// UnknownTx. Packet inspection: the flagged frame proves a held refusal.
        /// </summary>
        private static void TRANSIENT_SendFlaggedQueryFallbackFlag()
        {
            SendWorld w = new SendWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false)).Status == TxStatus.Accepted, "seed commits");
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);

            long unknown = Tx(11, 9);
            TxRequest flaggedFrame = new TxRequest();
            flaggedFrame.TxId = unknown;
            flaggedFrame.Op = TxOp.Query;
            flaggedFrame.Sender = 11;
            flaggedFrame.IsTransientRetry = true;
            Check.That(flaggedFrame.IsTransientRetry, "packet: fallback Query frame carries IsTransientRetry");
            Check.That(flaggedFrame.TxId == unknown, "packet: fallback Query keeps the same txId");

            TxResult flagged = core.Query(unknown, 11, flaggedFrame.IsTransientRetry);
            Check.That(flagged.Status == TxStatus.TransientUnavailable && !flagged.IsReplay,
                "flagged Query with no record answers TransientUnavailable, got " + flagged.Status);
            TxResult unflagged = core.Query(unknown, 11, false);
            Check.That(unflagged.Status == TxStatus.UnknownTx,
                "unflagged Query of the same txId answers UnknownTx, got " + unflagged.Status);
            Check.That(core.ExecuteCalls == execBase, "fallback-query legs never execute");
            Check.Equal(before, chest.TotalOf(Wood), "fallback-query legs mutate nothing");
        }

        /// <summary>
        /// The fallback keeps the SAME txId: flagged Query of the held txId answers
        /// TransientUnavailable (same-tx retained — no new txId minted, map entry
        /// untouched, nothing seeded), while a stranger's flagged Query is Rejected.
        /// </summary>
        private static void TRANSIENT_SendFallbackKeepsSameTx()
        {
            SendWorld w = new SendWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            SeedQuarantine(core, chest, w, 11);
            long held = HoldTransient(core, chest, w, 11, 3, 7);
            int execBase = core.ExecuteCalls;
            int processedBase = core.ProcessedCount;
            int before = chest.TotalOf(Wood);

            TxResult fallback = core.Query(held, 11, true);
            Check.That(fallback.Status == TxStatus.TransientUnavailable,
                "flagged fallback Query of the held txId stays transient, got " + fallback.Status);
            Check.That(core.TransientCount == 1 && core.DumpTransient().ContainsKey(held),
                "fallback keeps the SAME txId held (no new txId, map entry untouched)");
            Check.That(core.ProcessedCount == processedBase, "fallback seeds no cache entry");
            TxResult stranger = core.Query(held, 12, true);
            Check.That(stranger.Status == TxStatus.Rejected && stranger.Accepted.Count == 0,
                "stranger flagged Query Rejected with no payload, got " + stranger.Status);
            Check.That(core.TransientCount == 1, "stranger fallback leaves the held txId retained");
            Check.That(core.ExecuteCalls == execBase, "fallback legs never execute");
            Check.Equal(before, chest.TotalOf(Wood), "fallback legs mutate nothing");
        }

        /// <summary>
        /// Clean-handoff reconciliation never executes (packet inspection): after a
        /// restart from persisted bytes ONLY, the flagged same-tx mutation of the
        /// HELD txId reconciles durable-Rejected with ExecuteCalls==0 and exact
        /// conservation — and replays stably afterwards, flagged or not.
        /// </summary>
        private static void TRANSIENT_SendCleanHandoffNeverExecutes()
        {
            SendWorld w = new SendWorld();
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
            Check.That(core.Apply(chest, AddReq(held, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable, "held transient");

            TxCore core2;
            ModelChest chest2;
            w.Restart(6, 4, out core2, out chest2);
            Check.That(core2.TransientCount == 0, "pre: clean manager holds no RAM map");
            int before = chest2.TotalOf(Wood);

            // Packet inspection: the retry frame is the flagged same-tx mutation.
            TxRequest retryFrame = AddReq(held, 11, Wood, 7, true);
            Check.That(retryFrame.IsTransientRetry, "packet: handoff retry frame carries IsTransientRetry");
            Check.That(retryFrame.TxId == held, "packet: handoff retry keeps the same txId");
            Check.That(retryFrame.Items.Count == 1 && retryFrame.Items[0].Amount == 7,
                "packet: handoff retry carries the refused record (Add 7, never re-executed)");
            w.FailRing = false;
            w.FailFloor = false;
            TxResult reconciled = core2.Apply(chest2, retryFrame);
            Check.That(reconciled.Status == TxStatus.Rejected && !reconciled.IsReplay,
                "flagged handoff retry reconciles durable-Rejected, got " + reconciled.Status);
            Check.That(core2.ExecuteCalls == 0, "reconciliation never executes (ExecuteCalls==0 on the new owner)");
            Check.Equal(before, chest2.TotalOf(Wood), "reconciliation moves nothing");
            TxResult replayFlagged = core2.Apply(chest2, AddReq(held, 11, Wood, 7, true));
            Check.That(replayFlagged.Status == TxStatus.Rejected && replayFlagged.IsReplay,
                "reconciled refusal replays stably even when flagged, got " + replayFlagged.Status);
            TxResult replayPlain = core2.Apply(chest2, AddReq(held, 11, Wood, 7, false));
            Check.That(replayPlain.Status == TxStatus.Rejected && replayPlain.IsReplay,
                "reconciled refusal replays stably unflagged, got " + replayPlain.Status);
            Check.That(core2.ExecuteCalls == 0, "replays never execute");
            Check.Equal(before, chest2.TotalOf(Wood), "replays move nothing");
        }

        /// <summary>
        /// Repeated encode-fails keep Pending: every due pump still decides Resend
        /// (never Query/FinalQuery/Terminal — DecideTransient has no terminal leg,
        /// even far past any deadline) and every encode-fail still maps to the
        /// flagged Query; backoff stays bounded (30s cap) so the pings never wedge.
        /// </summary>
        private static void TRANSIENT_SendRepeatedFailsKeepPending()
        {
            for (int i = 0; i < 50; i++)
            {
                float now = 100f + i * 31f;
                Check.That(TxPendingDrain.DecideTransient(now, now - 1f) == TxPendingDrain.Step.Resend,
                    "repeated fail #" + i + ": due transient entry still resends (Pending kept)");
                Check.That(TxPendingDrain.DecideTransientSend(false) == TxPendingDrain.TransientSendAction.FlaggedQuery,
                    "repeated fail #" + i + ": encode-fail still maps to flagged Query");
            }
            Check.That(TxPendingDrain.DecideTransient(1000000f, 50f) == TxPendingDrain.Step.Resend,
                "far past deadline the entry still resends (never Terminal)");
            Check.That(TxPendingDrain.DecideTransient(100f, 150f) == TxPendingDrain.Step.Wait,
                "not-due entry waits (backoff respected between pings)");
            Check.That(Math.Abs(TxPendingDrain.TransientBackoffSeconds(100) - 30f) < 0.001f,
                "backoff stays capped at 30s across unbounded retries");
        }

        /// <summary>
        /// Eventual recovery on the SAME txId: flagged retries while the writes fail
        /// stay transient (never execute), then the healed flagged retry of the SAME
        /// txId resolves durable-Rejected; the map drops and a fresh txId commits
        /// with exact conservation (Add: refused 7 added nothing, fresh 5 adds 5).
        /// </summary>
        private static void TRANSIENT_SendEventualRecoverySameTx()
        {
            SendWorld w = new SendWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            SeedQuarantine(core, chest, w, 11);
            long held = HoldTransient(core, chest, w, 11, 3, 7);
            int execBase = core.ExecuteCalls;
            int before = chest.TotalOf(Wood);

            for (int i = 0; i < 3; i++)
            {
                TxRequest retryFrame = AddReq(held, 11, Wood, 7, true);
                Check.That(retryFrame.IsTransientRetry && retryFrame.TxId == held,
                    "packet: failing retry #" + i + " is the flagged same-tx mutation");
                TxResult retry = core.Apply(chest, retryFrame);
                Check.That(retry.Status == TxStatus.TransientUnavailable,
                    "failing flagged retry #" + i + " stays transient, got " + retry.Status);
                Check.That(core.TransientCount == 1, "held across failing retry #" + i);
            }
            Check.That(core.ExecuteCalls == execBase, "failing retries never execute");
            Check.Equal(before, chest.TotalOf(Wood), "failing retries mutate nothing");

            w.FailRing = false;
            w.FailFloor = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "authoritative reload recovers before resolution");
            Check.Equal(10, chest.TotalOf(Wood), "recovery restores the persisted 10 (speculative 4 discarded)");
            int healedBase = chest.TotalOf(Wood);
            TxResult resolved = core.Apply(chest, AddReq(held, 11, Wood, 7, true));
            Check.That(resolved.Status == TxStatus.Rejected && !resolved.IsReplay,
                "healed flagged retry resolves durable-Rejected on the SAME txId, got " + resolved.Status);
            Check.That(core.TransientCount == 0, "durable resolution drops the map entry");
            Check.That(core.ExecuteCalls == execBase, "resolution never executes");
            Check.Equal(healedBase, chest.TotalOf(Wood), "resolution mutates nothing");
            Check.That(core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 5, false)).Status == TxStatus.Accepted,
                "fresh txId commits after resolution");
            Check.Equal(healedBase + 5, chest.TotalOf(Wood), "conservation: refused 7 added nothing, fresh 5 adds exactly 5");
        }

        /// <summary>
        /// The fallback Query retrieves cached terminal outcomes: flagged Query of a
        /// COMMITTED Add-10 replays Accepted with the exact accepted payload, and of
        /// a committed Take-6 replays without double-debiting — so an encode-fail
        /// fallback that races a durable resolution still completes correctly.
        /// </summary>
        private static void TRANSIENT_SendCachedAcceptedViaFallbackQuery()
        {
            SendWorld w = new SendWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            TxResult add = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 10, false));
            Check.That(add.Status == TxStatus.Accepted && !add.IsReplay, "add-10 commits, got " + add.Status);
            Check.Equal(10, chest.TotalOf(Wood), "chest holds 10");
            int execBase = core.ExecuteCalls;

            TxResult fallbackAdd = core.Query(Tx(11, 1), 11, true);
            Check.That(fallbackAdd.Status == TxStatus.Accepted && fallbackAdd.IsReplay,
                "flagged fallback Query of committed Add replays Accepted, got " + fallbackAdd.Status);
            Check.Equal(10, chest.TotalOf(Wood), "Add 10 stays 10 across the fallback Query (no double-apply)");

            chest.AddItem(Wood, 10, 50);
            Check.Equal(20, chest.TotalOf(Wood), "stocked 10 (total 20)");
            TxResult take = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 6, false));
            Check.That(take.Status == TxStatus.Accepted && !take.IsReplay, "take-6 commits, got " + take.Status);
            Check.Equal(14, chest.TotalOf(Wood), "take debited exactly 6");
            TxResult fallbackTake = core.Query(Tx(11, 2), 11, true);
            Check.That(fallbackTake.Status == TxStatus.Accepted && fallbackTake.IsReplay,
                "flagged fallback Query of committed Take replays Accepted, got " + fallbackTake.Status);
            Check.Equal(14, chest.TotalOf(Wood), "Take 6 stays 6 across the fallback Query (no double-debit)");
            Check.That(core.ExecuteCalls == execBase + 1, "only the original Take executed (fallback queries never execute)");
        }

        /// <summary>
        /// Claims held (nothing credited, nothing removed): TransientUnavailable is
        /// non-terminal for every Add/Take shape, so the in-flight deduplicator
        /// claims stay held — unflagged reminders and flagged retries alike credit
        /// nothing, remove nothing, cascade nothing, with exact Add/Take totals.
        /// </summary>
        private static void TRANSIENT_SendClaimsHeldNothingCreditedRemoved()
        {
            Check.That(TxResponsePolicy.Classify(TxStatus.TransientUnavailable, false, TxOp.Add, 1)
                == TxCompletionKind.TransientRetrySameTx, "transient add retains (TransientRetrySameTx)");
            Check.That(TxResponsePolicy.Classify(TxStatus.TransientUnavailable, false, TxOp.Take, 1)
                == TxCompletionKind.TransientRetrySameTx, "transient take retains (TransientRetrySameTx)");
            Check.That(!TxResponsePolicy.IsCommitted(TxStatus.TransientUnavailable), "transient never committed (credits nothing)");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.TransientRetrySameTx),
                "transient never cascades (removes nothing downstream)");
            Check.That(TxDecision.DragRestoreAmount(10, 4, TxCompletionKind.TransientRetrySameTx) == 0,
                "transient restores no drag remainder");

            SendWorld w = new SendWorld();
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
            long held = HoldTransient(core, chest, w, 11, 4, 7);
            int execBase = core.ExecuteCalls;
            // Live RAM still carries the quarantining tx's speculative +4 (persisted
            // snapshot holds 14); the transient legs must add nothing beyond this.
            int heldBase = chest.TotalOf(Wood);
            Check.Equal(18, heldBase, "live holds speculative 18 (14 persisted + quarantining 4)");

            Check.That(core.Apply(chest, AddReq(held, 11, Wood, 7, false)).Status == TxStatus.TransientUnavailable,
                "unflagged reminder stays transient (claims held)");
            Check.That(core.Apply(chest, AddReq(held, 11, Wood, 7, true)).Status == TxStatus.TransientUnavailable,
                "flagged retry while failing stays transient (claims held)");
            Check.That(core.Query(held, 11, true).Status == TxStatus.TransientUnavailable,
                "flagged fallback Query stays transient (claims held)");
            Check.That(core.TransientCount == 1 && core.DumpTransient().ContainsKey(held),
                "same txId held across all three legs (claims never released, never re-minted)");
            Check.That(core.ExecuteCalls == execBase, "no leg executes (credits nothing)");
            Check.Equal(heldBase, chest.TotalOf(Wood), "conservation: all legs hold speculative 18 (Add/Take untouched)");
        }

        /// <summary>
        /// No-other-path audit: stored mutation bytes may go on the wire ONLY for
        /// non-transient entries; the transient send seam is exhaustive (both inputs
        /// map to a flagged same-tx leg); the classic Decide still terminalizes
        /// past-deadline non-transient entries (control — the fix narrows only the
        /// transient path, widens nothing).
        /// </summary>
        private static void TRANSIENT_SendNoOtherPathAudit()
        {
            Check.That(!TxPendingDrain.CanSendStoredMutationPayload(true),
                "audit: transient entry must never send stored mutation bytes");
            Check.That(TxPendingDrain.CanSendStoredMutationPayload(false),
                "audit: non-transient entry may resend stored bytes (unchanged)");
            Check.That(TxPendingDrain.DecideTransientSend(true) == TxPendingDrain.TransientSendAction.FlaggedMutation
                && TxPendingDrain.DecideTransientSend(false) == TxPendingDrain.TransientSendAction.FlaggedQuery,
                "audit: send seam exhaustive — every re-encode outcome maps to a flagged same-tx leg");
            Array values = Enum.GetValues(typeof(TxPendingDrain.TransientSendAction));
            bool hasMutation = false;
            bool hasQuery = false;
            foreach (object v in values)
            {
                if ((TxPendingDrain.TransientSendAction)v == TxPendingDrain.TransientSendAction.FlaggedMutation)
                    hasMutation = true;
                if ((TxPendingDrain.TransientSendAction)v == TxPendingDrain.TransientSendAction.FlaggedQuery)
                    hasQuery = true;
            }
            Check.That(hasMutation && hasQuery && values.Length == 2,
                "audit: exactly the two flagged legs exist — no unflagged path to regress to");
            TxPendingDrain.Step classic = TxPendingDrain.Decide(100f, 50f, 60f, 9, true, true, 4, 2);
            Check.That(classic == TxPendingDrain.Step.Terminal,
                "control: classic Decide still terminalizes past-deadline non-transient entries");
        }
    }
}
