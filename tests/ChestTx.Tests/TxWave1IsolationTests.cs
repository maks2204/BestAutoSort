using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Wave-1 isolation fixes: sender/txId trust order, quarantine terminals,
    /// enumeration-safe pending drain, claim-lease discipline, drag-restore gate.
    ///
    /// All tests run against PRODUCTION code only: TxCore.Apply / TxDecision /
    /// TxPendingDrain / TxClaimSet (the BestAutoSort.TxCore assembly the game
    /// ships). Unity-coupled paths (ChestTxService request/local/queued/pump
    /// bodies) cannot run in this harness, so they resolve their terminals
    /// through the SAME TxDecision seam functions pinned here — the suite fails
    /// if production drifts off the seam. Call sites are cited per test.
    ///
    /// Preserved (regressed elsewhere, not widened here): actor-first, raw
    /// sender routing, canonical keys, ring v3 + v1/v2 read, durable floor,
    /// UnknownTx-is-Indeterminate, TakeCustom propagation, owner==manager.
    ///
    /// Residual windows, stated honestly (NOT claimed fixed):
    /// - No ACK/journal: a committed-but-Indeterminate tx (save landed, answer
    ///   lost) still needs manual reconciliation — reload keeps the written
    ///   value live but the client credited nothing.
    /// - Client crash between commit and completion still loses the response;
    ///   Query recovers the cached outcome only while the manager holds it.
    /// - Conservation across a client crash is not proven here.
    /// - Local (owner) re-entrant submits share the synchronous drain; the claim
    ///   lease covers the remote Submit handoff to Pending and every
    ///   pre-ownership failure path, terminal release is exactly-once.
    /// </summary>
    internal static class TxWave1IsolationTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);
        private static readonly ItemKey Stone = new ItemKey(1002, 1, 0, 0);

        private const long PosA = 489397592;
        private const long NegA = -1214308360;
        private const long NegB = -311536441;

        /// <summary>Minimal fake durable world: ring/floor byte slots + items snapshot.</summary>
        private sealed class IsoWorld
        {
            public byte[] RingBytes;
            public byte[] FloorBytes;
            public List<int[]> Items = new List<int[]>();
            public bool FailSave;
            public bool FailReload;
            /// <summary>When true the ring write persists NOTHING (ring-ok leg off).</summary>
            public bool FailRingWrite;
            /// <summary>When true the floor write persists NOTHING (floor-ok leg off).</summary>
            public bool FailFloorWrite;

            public TxDurability Hooks()
            {
                TxDurability d = new TxDurability();
                d.WriteFloor = delegate (byte[] bytes)
                {
                    if (FailFloorWrite)
                        return false;
                    FloorBytes = (byte[])bytes.Clone();
                    return true;
                };
                d.WriteRing = delegate (byte[] bytes)
                {
                    if (FailRingWrite)
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
                    if (FailReload)
                        return false;
                    chest.Restore(CloneItems(Items));
                    return true;
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

        /// <summary>Fake pending entry: timing/attempt primitives + liveness + claims.</summary>
        private sealed class FakePending
        {
            public float NextTryAt;
            public float Deadline;
            public int Attempts;
            public bool QuerySent;
            public bool FinalQuerySent;
            public bool Destroyed;
            public readonly List<string> Claims = new List<string>();
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

        private static TxCore HandoffV3(TxCore src)
        {
            byte[] bytes = TxCore.EncodeRingV3(src.DumpRing(), src.DumpFloor());
            Check.That(bytes != null, "v3 encode of valid state must not fail");
            RingData data = TxCore.DecodeEnvelope(bytes);
            Check.That(!data.Corrupt, "shared-codec v3 round-trip must not be corrupt");
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
            Console.WriteLine("SPOOF_VictimTxIdNotBurned");
            SPOOF_VictimTxIdNotBurned();
            Console.WriteLine("SPOOF_IncompatiblePeerCannotRaiseVictimFloor");
            SPOOF_IncompatiblePeerCannotRaiseVictimFloor();
            Console.WriteLine("SPOOF_CachedResultNotLeaked");
            SPOOF_CachedResultNotLeaked();
            Console.WriteLine("SPOOF_SenderBindingSeam");
            SPOOF_SenderBindingSeam();
            Console.WriteLine("QUARANTINE_Add");
            QUARANTINE_Add();
            Console.WriteLine("QUARANTINE_TakeDelayedDuplicateAfterRecovery");
            QUARANTINE_TakeDelayedDuplicateAfterRecovery();
            Console.WriteLine("QUARANTINE_QueuedLocalRemoteShareTerminal");
            QUARANTINE_QueuedLocalRemoteShareTerminal();
            Console.WriteLine("QUARANTINE_HandoffDropPersistenceFailure");
            QUARANTINE_HandoffDropPersistenceFailure();
            Console.WriteLine("QUARANTINE_FreshAddTerminalNeverExecutesAfterRecovery");
            QUARANTINE_FreshAddTerminalNeverExecutesAfterRecovery();
            Console.WriteLine("QUARANTINE_TakeTerminalNeverExecutesAfterRecovery");
            QUARANTINE_TakeTerminalNeverExecutesAfterRecovery();
            Console.WriteLine("QUARANTINE_QueuedFreshTxNeverResurrects");
            QUARANTINE_QueuedFreshTxNeverResurrects();
            Console.WriteLine("QUARANTINE_MatrixBothDurableStableRejected");
            QUARANTINE_MatrixBothDurableStableRejected();
            Console.WriteLine("QUARANTINE_MatrixRingOnlySafeViaEmbeddedFloor");
            QUARANTINE_MatrixRingOnlySafeViaEmbeddedFloor();
            Console.WriteLine("QUARANTINE_MatrixFloorOnlyPermanentlyStale");
            QUARANTINE_MatrixFloorOnlyPermanentlyStale();
            Console.WriteLine("QUARANTINE_MatrixBothFailTransientSameTxRetain");
            QUARANTINE_MatrixBothFailTransientSameTxRetain();
            Console.WriteLine("SAMEOP_SameOpReuseReplaysOriginalDocumented");
            SAMEOP_SameOpReuseReplaysOriginalDocumented();
            Console.WriteLine("REPLAY_MismatchedOpNeverReturnsOldPayload");
            REPLAY_MismatchedOpNeverReturnsOldPayload();
            Console.WriteLine("REPLAY_OpCollisionSameTxIdFailsClosed");
            REPLAY_OpCollisionSameTxIdFailsClosed();
            Console.WriteLine("SENDER0_LegacyFreshAndQueryDocumented");
            SENDER0_LegacyFreshAndQueryDocumented();
            Console.WriteLine("PENDING_DecideMatrix");
            PENDING_DecideMatrix();
            Console.WriteLine("PENDING_MultipleTimeoutsOnePump");
            PENDING_MultipleTimeoutsOnePump();
            Console.WriteLine("PENDING_TerminalCallbackAndClaimsExactlyOnce");
            PENDING_TerminalCallbackAndClaimsExactlyOnce();
            Console.WriteLine("CLAIM_DuplicatePruned");
            CLAIM_DuplicatePruned();
            Console.WriteLine("CLAIM_PrePendingFailuresRelease");
            CLAIM_PrePendingFailuresRelease();
            Console.WriteLine("CLAIM_TerminalReleaseExactlyOnce");
            CLAIM_TerminalReleaseExactlyOnce();
            Console.WriteLine("DRAG_Rejected");
            DRAG_Rejected();
            Console.WriteLine("DRAG_Partial");
            DRAG_Partial();
            Console.WriteLine("DRAG_Indeterminate");
            DRAG_Indeterminate();
        }

        /// <summary>
        /// Wave-1 trust order: a spoofed txId answers ephemeral-Rejected with NO
        /// victim state change (no victim floor/ring/cache), so the victim's SAME
        /// txId still commits exactly once afterwards. Production: TxCore.Apply
        /// gates MatchesPeer (via TxDecision.ClassifySenderBinding) before any
        /// mutation; the service OnTxRequest spoof branch shares the seam.
        /// </summary>
        private static void SPOOF_VictimTxIdNotBurned()
        {
            Check.That(
                TxDecision.ClassifySenderBinding(22, Tx(11, 1)) == TxDecision.SenderBinding.UnboundStranger,
                "seam must classify the stranger binding first");
            Check.That(TxDecision.SpoofTerminal() == TxStatus.Rejected, "spoof terminal is Rejected");

            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();

            TxResult spoof = core.Apply(chest, TakeReq(Tx(11, 1), 22, Wood, 6));
            Check.That(spoof.Status == TxStatus.Rejected, "spoof rejected, got " + spoof.Status);
            Check.Equal(20, chest.TotalOf(Wood), "spoof executes nothing");
            Check.Equal(0, core.ProcessedCount, "spoof seeds no cache entry");
            Check.Equal(0, core.RingCount, "spoof writes no ring entry");
            Check.Equal(0, core.FloorCount, "spoof advances no floor");
            Check.That(core.Query(Tx(11, 1)).Status == TxStatus.UnknownTx, "spoof leaves no record to query");

            TxResult legit = core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 6));
            Check.That(legit.Status == TxStatus.Accepted && !legit.IsReplay,
                "victim txId not burned, got " + legit.Status);
            Check.Equal(14, chest.TotalOf(Wood), "exactly one debit");
            TxResult replay = core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 6));
            Check.That(replay.Status == TxStatus.Accepted && replay.IsReplay, "legit replay stable");
            Check.Equal(14, chest.TotalOf(Wood), "replay debits nothing");
            Check.Equal(1, core.RingCount, "ring carries the victim entry only");
        }

        /// <summary>
        /// No mismatched sender — however skewed or hostile — can raise any floor:
        /// spoof attempts from positive AND negative UIDs persist nothing and fence
        /// nothing, while the victim's own counters commit and fence normally.
        /// Production: the service incompatible-mismatch branch (version-skewed
        /// sender) resolves through the same ephemeral terminal with no
        /// SeedReject/WriteRing/WriteFloor; TxCore never reaches the fence here.
        /// </summary>
        private static void SPOOF_IncompatiblePeerCannotRaiseVictimFloor()
        {
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);

            long[] attackers = new long[] { 22, NegB, PosA };
            long[] victims = new long[] { 11, 11, NegA };
            uint[] counters = new uint[] { 1, 2, 1 };
            for (int i = 0; i < attackers.Length; i++)
            {
                TxResult r = core.Apply(chest, TakeReq(Tx(victims[i], counters[i]), attackers[i], Wood, 6));
                Check.That(r.Status == TxStatus.Rejected, "mismatch rejected, got " + r.Status);
            }
            Check.Equal(20, chest.TotalOf(Wood), "mismatches execute nothing");
            Check.Equal(0, core.FloorCount, "no floor raised by any mismatch");
            Check.Equal(0, core.ProcessedCount, "no cache seeded by any mismatch");
            Check.Equal(0, core.RingCount, "no ring entry from any mismatch");
            Check.That(w.RingBytes == null && w.FloorBytes == null, "no mismatch persists anything");

            TxResult legit = core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 6));
            Check.That(legit.Status == TxStatus.Accepted, "victim commits, got " + legit.Status);
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 1,
                "victim floor tracks only the victim commit");
            Check.Equal(14, chest.TotalOf(Wood), "exactly one debit");

            TxResult again = core.Apply(chest, TakeReq(Tx(11, 2), 22, Wood, 6));
            Check.That(again.Status == TxStatus.Rejected, "later mismatch still rejected");
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 1,
                "later mismatch cannot move the victim floor");
            Check.Equal(14, chest.TotalOf(Wood), "later mismatch executes nothing");
        }

        /// <summary>
        /// A stranger replaying the victim's COMMITTED Take gets Rejected with an
        /// empty body (never the victim's Take bytes) while the victim keeps the
        /// exact original — across a production-codec handoff too. Negative-UID
        /// control: raw negative senders match their own txIds, never others'.
        /// </summary>
        private static void SPOOF_CachedResultNotLeaked()
        {
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 20, 50);
            TxCore core = new TxCore();
            TxRequest take = TakeReq(Tx(NegA, 1), NegA, Wood, 6);
            Check.That(core.Apply(chest, take).Status == TxStatus.Accepted, "victim commits");

            TxResult stranger = core.Apply(chest, TakeReq(Tx(NegA, 1), NegB, Wood, 6));
            Check.That(stranger.Status == TxStatus.Rejected, "stranger rejected, got " + stranger.Status);
            Check.Equal(0, stranger.Accepted.Count, "stranger gets no payload");
            Check.Equal(14, chest.TotalOf(Wood), "stranger resend must not re-apply");
            Check.Equal(1, core.ProcessedCount, "stranger resend seeds nothing new");

            TxResult qStranger = core.Query(take.TxId, NegB);
            Check.That(qStranger.Status == TxStatus.Rejected, "stranger query rejected");
            Check.Equal(0, qStranger.Accepted.Count, "stranger query gets no payload");
            Check.Equal(TxIdGen.PeerKey(NegB), qStranger.Sender, "mismatch names the requester canonically");
            TxResult qMine = core.Query(take.TxId, NegA);
            Check.That(qMine.Status == TxStatus.Accepted && !qMine.TotalsOnly, "victim keeps the original");
            Check.Equal(6, qMine.AcceptedTotal(), "victim keeps the body");

            TxCore post = HandoffV3(core);
            ModelChest chestB = CloneChest(chest);
            TxResult hStranger = post.Apply(chestB, TakeReq(Tx(NegA, 1), NegB, Wood, 6));
            Check.That(hStranger.Status == TxStatus.Rejected, "post-handoff stranger rejected");
            Check.Equal(0, hStranger.Accepted.Count, "post-handoff stranger gets no payload");
            Check.Equal(14, chestB.TotalOf(Wood), "post-handoff stranger re-applies nothing");
            TxResult hMine = post.Apply(chestB, TakeReq(Tx(NegA, 1), NegA, Wood, 6));
            Check.That(hMine.Status == TxStatus.Accepted && hMine.IsReplay && !hMine.TotalsOnly,
                "post-handoff victim replays exactly");
        }

        /// <summary>
        /// Negative-UID control for the shared trust gate: raw negative senders own
        /// their txIds (the P0 canonical-key regression), strangers of either sign
        /// do not, and sender tag 0 stays legacy (never a spoof — skew resends of
        /// committed txIds keep their original outcome through the replay path).
        /// </summary>
        private static void SPOOF_SenderBindingSeam()
        {
            uint c = 1;
            long negTx = TxIdGen.Next(NegA, ref c);
            Check.That(TxDecision.ClassifySenderBinding(NegA, negTx) == TxDecision.SenderBinding.BoundOwner,
                "raw negative sender owns its txId");
            Check.That(TxDecision.ClassifySenderBinding(NegB, negTx) == TxDecision.SenderBinding.UnboundStranger,
                "other negative sender is a stranger");
            Check.That(TxDecision.ClassifySenderBinding(PosA, negTx) == TxDecision.SenderBinding.UnboundStranger,
                "positive sender is a stranger to a negative txId");
            Check.That(TxDecision.ClassifySenderBinding(0, negTx) == TxDecision.SenderBinding.UnauthenticatedLegacy,
                "sender 0 is legacy, never a spoof");

            uint d = 1;
            long posTx = TxIdGen.Next(PosA, ref d);
            Check.That(TxDecision.ClassifySenderBinding(PosA, posTx) == TxDecision.SenderBinding.BoundOwner,
                "positive sender owns its txId");
            Check.That(TxDecision.ClassifySenderBinding(NegA, posTx) == TxDecision.SenderBinding.UnboundStranger,
                "negative sender is a stranger to a positive txId");
            Check.That(TxDecision.ClassifySenderBinding(0, posTx) == TxDecision.SenderBinding.UnauthenticatedLegacy,
                "sender 0 is legacy for positive txIds too");

            // Legacy skew resend (Sender 0) of a committed txId replays the original
            // through the normal path — never coerced by the spoof gate.
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Stone, 10, 50);
            TxCore core = new TxCore();
            TxRequest add = AddReq(Tx(PosA, 1), PosA, Stone, 4);
            Check.That(core.Apply(chest, add).Status == TxStatus.Accepted, "commit");
            TxRequest skew = AddReq(Tx(PosA, 1), 0, Stone, 4);
            TxResult skewed = core.Apply(chest, skew);
            Check.That(skewed.Status == TxStatus.Accepted && skewed.IsReplay,
                "skew resend replays original, got " + skewed.Status);
            Check.Equal(14, chest.TotalOf(Stone), "skew resend applies nothing (10 seed + 4 committed once)");
        }

        /// <summary>
        /// Quarantined Add path (both copies durable leg): the failed tx stays
        /// Indeterminate with its fence kept; the fresh txId is refused through
        /// the durable-terminal matrix — seeded Rejected (known-not-committed,
        /// nothing executes) answered Rejected because the record persisted
        /// (ring AND floor). The same txId replays that stable Rejected after
        /// recovery (never executes — retry as a NEW txId, never the same one).
        /// Only a successful authoritative TryRecover clears the quarantine
        /// (elapsed Applys never do); then NEW txIds commit.
        /// </summary>
        private static void QUARANTINE_Add()
        {
            Check.That(TxDecision.HandoffDropTerminal(true, true) == TxStatus.Rejected,
                "durable quarantine refusal is a stable Rejected");
            Check.That(TxDecision.QuarantinedTerminal() == TxStatus.UnknownTx,
                "undurable leg stays Indeterminate");
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            Check.Equal(5, w.PersistedTotal(Wood), "seed persisted");

            w.FailSave = true;
            w.FailReload = true;
            TxResult failed = core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 10));
            Check.That(failed.Status == TxStatus.UnknownTx && !failed.IsReplay, "failed tx indeterminate");
            Check.That(core.Quarantined, "unrecoverable save failure quarantines");
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 2,
                "failed tx keeps its fence");
            Check.Equal(1, core.ProcessedCount, "ambiguous tx cached nothing exact");
            Check.Equal(5, w.PersistedTotal(Wood), "persisted still the seed");

            TxResult fresh = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(fresh.Status == TxStatus.Rejected && !fresh.IsReplay,
                "quarantined fresh tx refused durable-Rejected (nothing executed), got " + fresh.Status);
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 3,
                "quarantined refusal keeps the floor high-water (11->3)");
            Check.Equal(2, core.ProcessedCount, "durable refusal is cached (stable Rejected)");
            Check.That(core.Quarantined, "elapsed Apply alone never clears quarantine");
            Check.That(!core.TryRecover(chest), "TryRecover fails while reload broken");
            Check.That(core.Quarantined, "still quarantined");

            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "authoritative reload recovers");
            Check.That(!core.Quarantined, "quarantine cleared by reload only");
            TxResult stale = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(stale.Status == TxStatus.Rejected && stale.IsReplay,
                "quarantined-era txId replays the stable Rejected after recovery (never executes, retry as NEW txId), got " + stale.Status);
            Check.Equal(5, chest.TotalOf(Wood), "stable replay executes nothing");
            TxResult after = core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 7));
            Check.That(after.Status == TxStatus.Accepted, "post-recovery NEW tx commits, got " + after.Status);
            Check.Equal(12, chest.TotalOf(Wood), "5 persisted + 7 exactly once");
        }

        /// <summary>
        /// Take delayed-duplicate across recovery: the failed take's fence is kept;
        /// the quarantined-era duplicate is refused through the durable-terminal
        /// matrix (stable Rejected — never executes), so the SAME txId retried
        /// after TryRecover replays that Rejected — including a
        /// transport-duplicated copy delivered twice. Only a NEW txId commits.
        /// Conservation holds.
        /// </summary>
        private static void QUARANTINE_TakeDelayedDuplicateAfterRecovery()
        {
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);

            Check.That(core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 6)).Status == TxStatus.Accepted, "seed take commits");
            Check.Equal(14, chest.TotalOf(Wood), "seed debited");

            w.FailSave = true;
            w.FailReload = true;
            TxResult failed = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 4));
            Check.That(failed.Status == TxStatus.UnknownTx, "failed take indeterminate");
            Check.That(core.Quarantined, "quarantined");

            TxResult dup = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 4));
            Check.That(dup.Status == TxStatus.Rejected && !dup.IsReplay,
                "delayed duplicate while quarantined refused durable-Rejected (never executes), got " + dup.Status);
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 2,
                "quarantine re-fence of the same counter is idempotent (floor stays 2)");
            Check.Equal(10, chest.TotalOf(Wood), "quarantined duplicate debits nothing further (live holds only the failed-tx dirt)");

            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "recovery succeeds");
            Check.Equal(14, chest.TotalOf(Wood), "live matches persisted after recovery");

            TxResult stale = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 4));
            Check.That(stale.Status == TxStatus.Rejected && stale.IsReplay,
                "same txId replays the stable Rejected after recovery (never executes), got " + stale.Status);
            Check.Equal(14, chest.TotalOf(Wood), "stable replay debits nothing");
            // Transport-duplicated copy of the same refused txId: still the stable
            // Rejected, still no debit — the durable record makes redelivery safe.
            TxResult staleCopy = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 4));
            Check.That(staleCopy.Status == TxStatus.Rejected && staleCopy.IsReplay,
                "transport-duplicated refused copy replays Rejected, got " + staleCopy.Status);
            Check.Equal(14, chest.TotalOf(Wood), "transport duplicate debits nothing");

            TxResult fresh = core.Apply(chest, TakeReq(Tx(11, 3), 11, Wood, 4));
            Check.That(fresh.Status == TxStatus.Accepted, "new txId commits, got " + fresh.Status);
            Check.Equal(10, chest.TotalOf(Wood), "exactly one new debit");
            Check.Equal(20, 6 + 4 + 10, "seed + fresh + live conserve the original stock");
        }

        /// <summary>
        /// Queued + local + remote quarantine terminals share ONE seam value AND
        /// one durable-terminal rule (TxDecision.HandoffDropTerminal, the same rule
        /// as queued-but-unapplied handoff drops): remote (OnTxRequest quarantine
        /// refusal), local (MutateLocal refusal and the ApplyJob quarantine recheck)
        /// and queued (Drain fail-closed branch) all seed Rejected and answer
        /// Rejected only when durable — else Indeterminate with the seed evicted.
        /// Core-level proof: even an UNSEEN peer's fresh txId is refused durably
        /// while quarantined — and replays that stable Rejected after recovery
        /// (never executes; retry as a NEW txId, never the same one).
        /// </summary>
        private static void QUARANTINE_QueuedLocalRemoteShareTerminal()
        {
            Check.That(TxDecision.HandoffDropTerminal(true, true) == TxStatus.Rejected,
                "remote/local/queued quarantine terminal is Rejected when durable");
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");

            // Unseen peer: refused durably (seeded Rejected + floor high-water).
            TxResult alien = core.Apply(chest, AddReq(Tx(99, 1), 99, Wood, 5));
            Check.That(alien.Status == TxStatus.Rejected && !alien.IsReplay,
                "unseen-peer tx refused durable-Rejected, got " + alien.Status);
            uint alienHw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(99), out alienHw) && alienHw == 1,
                "quarantine refusal keeps the high-water even for unseen peers (floor 99->1)");
            Check.Equal(2, core.ProcessedCount, "durable refusal is cached");

            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "recovery succeeds");
            TxResult retry = core.Apply(chest, AddReq(Tx(99, 1), 99, Wood, 5));
            Check.That(retry.Status == TxStatus.Rejected && retry.IsReplay,
                "quarantined-era txId replays stable Rejected after recovery, got " + retry.Status);
            Check.Equal(5, chest.TotalOf(Wood), "stale retry executes nothing");
            TxResult fresh = core.Apply(chest, AddReq(Tx(99, 2), 99, Wood, 5));
            Check.That(fresh.Status == TxStatus.Accepted && !fresh.IsReplay,
                "NEW txId commits after recovery, got " + fresh.Status);
            Check.Equal(10, chest.TotalOf(Wood), "5 seed + 5 exactly once");
        }

        /// <summary>
        /// Handoff-drop persistence rule through the shared seam: a stable Rejected
        /// for queued-but-unapplied jobs requires a DURABLE record (ring AND floor
        /// persisted); any persistence failure stays fail-closed Indeterminate
        /// WITHOUT claiming a stable terminal. Production call sites: Takeover and
        /// AcquireForStructural (seed + WriteRing/WriteFloor, evict the unpersisted
        /// seed, keep the floor advance so an in-session retry stays stale-gated
        /// and the sender retries as a NEW tx).
        /// </summary>
        private static void QUARANTINE_HandoffDropPersistenceFailure()
        {
            Check.That(TxDecision.HandoffDropTerminal(true, true) == TxStatus.Rejected,
                "durable record => stable Rejected");
            Check.That(TxDecision.HandoffDropTerminal(false, true) == TxStatus.UnknownTx,
                "ring write failed => Indeterminate, never a RAM-only Rejected");
            Check.That(TxDecision.HandoffDropTerminal(true, false) == TxStatus.UnknownTx,
                "floor write failed => Indeterminate");
            Check.That(TxDecision.HandoffDropTerminal(false, false) == TxStatus.UnknownTx,
                "both failed => Indeterminate");

            // Core-level twin of the rule (deterministic Rejected persist-or-evict):
            // an unpersisted Rejected answers Indeterminate with the seed evicted.
            IsoWorld w = new IsoWorld();
            w.FailSave = false;
            TxCore core = new TxCore();
            core.Durability = new TxDurability
            {
                WriteFloor = w.Hooks().WriteFloor,
                WriteRing = delegate (byte[] bytes) { return false; },
                SaveItems = w.Hooks().SaveItems,
                ReloadItems = w.Hooks().ReloadItems,
                IsOwner = delegate () { return true; }
            };
            ModelChest chest = new ModelChest(4, 4);
            chest.AddItem(Wood, 5, 50);
            TxResult r = core.Apply(chest, TakeReq(Tx(11, 1), 11, Stone, 1));
            Check.That(r.Status == TxStatus.UnknownTx, "unpersisted reject is indeterminate, got " + r.Status);
            Check.Equal(0, core.ProcessedCount, "unpersisted seed evicted");
        }

        /// <summary>
        /// Fresh-Add quarantine terminal (unseen peer): refused durable-Rejected
        /// while quarantined (seeded + persisted, nothing executes); the same txId
        /// replays that stable Rejected after recovery (NEVER executes); only a
        /// NEW txId commits. Pins the durable-terminal rule for the
        /// remote/local/queued production paths at the seam level.
        /// </summary>
        private static void QUARANTINE_FreshAddTerminalNeverExecutesAfterRecovery()
        {
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");

            TxResult fresh = core.Apply(chest, AddReq(Tx(77, 1), 77, Wood, 5));
            Check.That(fresh.Status == TxStatus.Rejected && !fresh.IsReplay,
                "fresh add refused durable-Rejected, got " + fresh.Status);
            Check.Equal(0, fresh.AcceptedTotal(), "quarantined terminal carries no payload");
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(77), out hw) && hw == 1,
                "fresh add refusal keeps the high-water (floor 77->1)");
            Check.Equal(2, core.ProcessedCount, "durable refusal is cached");
            Check.Equal(10, chest.TotalOf(Wood), "quarantined add executes nothing (live holds only the failed-tx dirt: 5 seed + 5 speculative)");

            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "recovery succeeds");
            TxResult retry = core.Apply(chest, AddReq(Tx(77, 1), 77, Wood, 5));
            Check.That(retry.Status == TxStatus.Rejected && retry.IsReplay,
                "same txId replays stable Rejected after recovery (never executes), got " + retry.Status);
            Check.Equal(5, chest.TotalOf(Wood), "post-recovery retry of the fenced txId executes nothing");
            TxResult next = core.Apply(chest, AddReq(Tx(77, 2), 77, Wood, 5));
            Check.That(next.Status == TxStatus.Accepted && !next.IsReplay,
                "new txId commits, got " + next.Status);
            Check.Equal(10, chest.TotalOf(Wood), "5 seed + 5 exactly once");
        }

        /// <summary>
        /// Fresh-Take quarantine terminal: refused durable-Rejected while
        /// quarantined (no debit); the same txId replays that stable Rejected
        /// after recovery (NEVER debits); only a NEW txId debits exactly once.
        /// Take-side twin of the Add terminal.
        /// </summary>
        private static void QUARANTINE_TakeTerminalNeverExecutesAfterRecovery()
        {
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);

            Check.That(core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 6)).Status == TxStatus.Accepted, "seed take commits");
            Check.Equal(14, chest.TotalOf(Wood), "seed debited");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 4)).Status == TxStatus.UnknownTx, "failed take indeterminate");
            Check.That(core.Quarantined, "quarantined");

            TxResult fresh = core.Apply(chest, TakeReq(Tx(11, 3), 11, Wood, 4));
            Check.That(fresh.Status == TxStatus.Rejected && !fresh.IsReplay,
                "fresh take refused durable-Rejected, got " + fresh.Status);
            Check.Equal(0, fresh.Accepted.Count, "quarantined terminal carries no take payload");
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 3,
                "fresh take refusal keeps the high-water (floor 11->3)");
            Check.Equal(10, chest.TotalOf(Wood), "quarantined take debits nothing further (live holds only the failed-tx dirt)");

            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "recovery succeeds");
            TxResult retry = core.Apply(chest, TakeReq(Tx(11, 3), 11, Wood, 4));
            Check.That(retry.Status == TxStatus.Rejected && retry.IsReplay,
                "same take txId replays stable Rejected after recovery (never debits), got " + retry.Status);
            Check.Equal(14, chest.TotalOf(Wood), "post-recovery retry debits nothing");
            TxResult next = core.Apply(chest, TakeReq(Tx(11, 4), 11, Wood, 4));
            Check.That(next.Status == TxStatus.Accepted && !next.IsReplay,
                "new take txId commits, got " + next.Status);
            Check.Equal(10, chest.TotalOf(Wood), "exactly one new debit");
        }

        /// <summary>
        /// Queued fresh txIds never resurrect: every txId submitted while
        /// quarantined is refused durable-Rejected, and NONE executes after
        /// recovery — even when the recovery succeeds on the very next call.
        /// Production twin: the Drain fail-closed branch + the ApplyJob
        /// quarantine recheck.
        /// </summary>
        private static void QUARANTINE_QueuedFreshTxNeverResurrects()
        {
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");

            // Two queued fresh txIds (the Drain queue behind the failed tx).
            Check.That(core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 1)).Status == TxStatus.Rejected, "queued 3 refused durable-Rejected");
            Check.That(core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 2)).Status == TxStatus.Rejected, "queued 4 refused durable-Rejected");
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 4,
                "every queued refusal keeps the high-water (floor 11->4)");
            Check.Equal(3, core.ProcessedCount, "durable refusals are cached");
            Check.Equal(10, chest.TotalOf(Wood), "queued txIds executed nothing (live holds only the failed-tx dirt)");

            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "recovery succeeds");
            TxResult r3 = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 1));
            Check.That(r3.Status == TxStatus.Rejected && r3.IsReplay,
                "queued txId 3 replays stable Rejected (never resurrects), got " + r3.Status);
            TxResult r4 = core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 2));
            Check.That(r4.Status == TxStatus.Rejected && r4.IsReplay,
                "queued txId 4 replays stable Rejected (never resurrects), got " + r4.Status);
            Check.Equal(5, chest.TotalOf(Wood), "resurrected retries execute nothing");
            Check.That(core.Apply(chest, AddReq(Tx(11, 5), 11, Wood, 3)).Status == TxStatus.Accepted,
                "new txId commits");
            Check.Equal(8, chest.TotalOf(Wood), "5 seed + 3 exactly once");
        }

        /// <summary>
        /// RAM discarded: rebuild a core from persisted bytes ONLY (no live RAM
        /// carried over). Null ring bytes = ring copy lost; null floor bytes =
        /// independent floor copy lost (falls back to the ring-embedded copy).
        /// Every restart test below goes through HERE.
        /// </summary>
        private static TxCore RebuildFromPersisted(byte[] ringBytes, byte[] floorBytes)
        {
            RingData ring = (ringBytes != null) ? TxCore.DecodeEnvelope(ringBytes) : new RingData();
            FloorData floor;
            if (floorBytes != null)
            {
                floor = TxCore.DecodeFloor(floorBytes);
                floor.Present = true;
            }
            else
            {
                floor = new FloorData();
            }
            TxCore dst = new TxCore();
            dst.LoadRingWithFloor(ring, floor);
            return dst;
        }

        /// <summary>
        /// Matrix leg 1/4 (ring-ok + floor-ok => stable Rejected): the quarantined
        /// refusal persists both copies; RAM discarded, a rebuild from the
        /// persisted bytes answers the same stable Rejected for the same txId
        /// (never executes), while a NEW txId commits exactly once.
        /// </summary>
        private static void QUARANTINE_MatrixBothDurableStableRejected()
        {
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");
            TxResult refusal = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(refusal.Status == TxStatus.Rejected && !refusal.IsReplay,
                "both durable => stable Rejected, got " + refusal.Status);
            Check.That(w.RingBytes != null && w.FloorBytes != null, "both copies persisted");

            TxCore post = RebuildFromPersisted(w.RingBytes, w.FloorBytes);
            ModelChest chestB = new ModelChest(6, 4);
            chestB.Restore(IsoWorld.CloneItems(w.Items));
            TxResult replay = post.Apply(chestB, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(replay.Status == TxStatus.Rejected && replay.IsReplay,
                "post-restart same txId replays stable Rejected, got " + replay.Status);
            Check.Equal(5, chestB.TotalOf(Wood), "post-restart replay executes nothing");
            TxResult fresh = post.Apply(chestB, AddReq(Tx(11, 4), 11, Wood, 7));
            Check.That(fresh.Status == TxStatus.Accepted && !fresh.IsReplay,
                "post-restart NEW txId commits, got " + fresh.Status);
            Check.Equal(12, chestB.TotalOf(Wood), "5 persisted + 7 exactly once");
        }

        /// <summary>
        /// Matrix leg 2/4 (ring-ok only => safe via embedded floor): in-session the
        /// refusal answers UnknownTx with the seed evicted (a RAM-only Rejected
        /// would flip after a restart), but the persisted ring carries BOTH the
        /// Rejected entry AND the embedded floor copy — a restart from the ring
        /// bytes ALONE (independent floor copy lost) still answers stable
        /// Rejected for the same txId, and the txId never executes.
        /// </summary>
        private static void QUARANTINE_MatrixRingOnlySafeViaEmbeddedFloor()
        {
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");
            w.FailFloorWrite = true;
            TxResult refusal = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(refusal.Status == TxStatus.UnknownTx && !refusal.IsReplay,
                "ring-only => UnknownTx (seed evicted, never a RAM-only Rejected), got " + refusal.Status);
            Check.Equal(1, core.ProcessedCount, "unpersisted seed evicted");
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 3,
                "RAM high-water kept (11->3) but NOT durable protection");
            Check.That(w.RingBytes != null, "ring copy persisted");

            // RAM discarded AND the independent floor copy lost: ring bytes alone.
            TxCore post = RebuildFromPersisted(w.RingBytes, null);
            uint postHw;
            Check.That(post.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out postHw) && postHw == 3,
                "ring-embedded floor survived without the independent copy (floor 11->3)");
            ModelChest chestB = new ModelChest(6, 4);
            chestB.Restore(IsoWorld.CloneItems(w.Items));
            TxResult replay = post.Apply(chestB, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(replay.Status == TxStatus.Rejected && replay.IsReplay,
                "post-restart same txId answers stable Rejected via the ring record, got " + replay.Status);
            Check.Equal(5, chestB.TotalOf(Wood), "post-restart replay executes nothing");
            TxResult fresh = post.Apply(chestB, AddReq(Tx(11, 4), 11, Wood, 7));
            Check.That(fresh.Status == TxStatus.Accepted && !fresh.IsReplay,
                "post-restart NEW txId commits, got " + fresh.Status);
            Check.Equal(12, chestB.TotalOf(Wood), "5 persisted + 7 exactly once");
        }

        /// <summary>
        /// Matrix leg 3/4 (floor-ok only => UnknownTx permanently stale): the ring
        /// carries no record, so a restart from the floor bytes alone leaves the
        /// same txId stale-gated forever (UnknownTx on every attempt, never
        /// executes) while a NEW txId commits exactly once.
        /// </summary>
        private static void QUARANTINE_MatrixFloorOnlyPermanentlyStale()
        {
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");
            w.FailRingWrite = true;
            TxResult refusal = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(refusal.Status == TxStatus.UnknownTx && !refusal.IsReplay,
                "floor-only => UnknownTx (seed evicted), got " + refusal.Status);
            Check.Equal(1, core.ProcessedCount, "unpersisted seed evicted");
            Check.That(w.FloorBytes != null, "floor copy persisted");

            // RAM discarded AND the ring copy lost (write failed): floor bytes alone.
            TxCore post = RebuildFromPersisted(null, w.FloorBytes);
            ModelChest chestB = new ModelChest(6, 4);
            chestB.Restore(IsoWorld.CloneItems(w.Items));
            TxResult retry = post.Apply(chestB, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(retry.Status == TxStatus.UnknownTx && !retry.IsReplay,
                "post-restart same txId stays stale-gated (no record, floor blocks), got " + retry.Status);
            Check.Equal(5, chestB.TotalOf(Wood), "stale retry executes nothing");
            TxResult again = post.Apply(chestB, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(again.Status == TxStatus.UnknownTx && !again.IsReplay,
                "permanently stale: still UnknownTx on a second attempt, got " + again.Status);
            TxResult fresh = post.Apply(chestB, AddReq(Tx(11, 4), 11, Wood, 7));
            Check.That(fresh.Status == TxStatus.Accepted && !fresh.IsReplay,
                "post-restart NEW txId commits, got " + fresh.Status);
            Check.Equal(12, chestB.TotalOf(Wood), "5 persisted + 7 exactly once");
        }

        /// <summary>
        /// Matrix leg 4/4 (both-fail => NO terminal-forget): nothing durable, so
        /// the answer is UnknownTx with the seed evicted — but the SAME txId is
        /// retained, not forgotten. A same-tx retry re-attempts persistence
        /// (quarantine precedes the stale gate, so it never stales out
        /// in-session): while the faults persist it answers UnknownTx again, and
        /// once the writes heal the SAME txId persists durably (stable Rejected)
        /// with no new txId needed. Nothing ever executes. Production twin: the
        /// manager stays SILENT for remote txs (no terminal response — the
        /// client's Pending pump resends/queries the SAME txId) and completes
        /// UnknownTx for local txs (no Pending entry; a retry as a NEW txId
        /// applies at-most-once).
        /// </summary>
        private static void QUARANTINE_MatrixBothFailTransientSameTxRetain()
        {
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");
            w.FailRingWrite = true;
            w.FailFloorWrite = true;
            TxResult refusal = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(refusal.Status == TxStatus.UnknownTx && !refusal.IsReplay,
                "both-fail => UnknownTx, got " + refusal.Status);
            Check.Equal(1, core.ProcessedCount, "seed evicted: no terminal kept (nothing durable to forget)");
            Check.That(core.Query(Tx(11, 3)).Status == TxStatus.UnknownTx,
                "nothing cached: query is UnknownTx");
            uint hw;
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 3,
                "RAM high-water kept (11->3, monotonic, never rolls back)");
            Check.Equal(10, chest.TotalOf(Wood), "refusal executes nothing (live holds only the failed-tx dirt: 5 seed + 5 speculative)");

            // Same-tx retry while faults persist: re-attempts persistence (never
            // stales out in-session), still UnknownTx, still evicted, still idle.
            TxResult retry = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(retry.Status == TxStatus.UnknownTx && !retry.IsReplay,
                "same-tx retry re-attempts (not stale-gated in-session), got " + retry.Status);
            Check.Equal(1, core.ProcessedCount, "still nothing cached");
            Check.Equal(10, chest.TotalOf(Wood), "retry executes nothing");

            // Writes heal: the SAME txId now persists durably — transient recovery
            // without a restart and without a new txId.
            w.FailRingWrite = false;
            w.FailFloorWrite = false;
            TxResult healed = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(healed.Status == TxStatus.Rejected && !healed.IsReplay,
                "healed same-tx retry persists durably (stable Rejected, fresh refusal — never a replay), got " + healed.Status);
            Check.Equal(2, core.ProcessedCount, "healed refusal is cached");
            Check.Equal(10, chest.TotalOf(Wood), "healed refusal executes nothing");
            TxResult stable = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(stable.Status == TxStatus.Rejected && stable.IsReplay,
                "same txId now replays the stable Rejected, got " + stable.Status);
        }

        /// <summary>
        /// Same-op txId-collision documentation + regression (RAM-only, no ring
        /// change): a DIFFERENT request reusing a committed txId with the SAME op
        /// replays the ORIGINAL outcome (false hit — never re-applied, idempotent
        /// by construction: a transport duplicate or a same-tx Query retry can
        /// never double-apply). The cross-op case is guarded (IsOpMismatch =>
        /// Indeterminate, see REPLAY_MismatchedOpNeverReturnsOldPayload); the
        /// same-op case is prevented by UNIQUE ISSUANCE (durable counter
        /// reservation above the persisted execution floor — a reset counter hits
        /// the high-water and answers Indeterminate, never reuses a live txId),
        /// NOT by this gate. This test pins the replay side so any future
        /// request-identity guard stays RAM-only and trivial (never a ring-format
        /// or terminal change).
        /// </summary>
        private static void SAMEOP_SameOpReuseReplaysOriginalDocumented()
        {
            ModelChest chest = new ModelChest(6, 4);
            TxCore core = new TxCore();
            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "first add commits");
            Check.Equal(5, chest.TotalOf(Wood), "applied once");
            // A DIFFERENT Add body reusing the committed txId: the ORIGINAL replays.
            TxResult collision = core.Apply(chest, AddReq(Tx(11, 1), 11, Stone, 9));
            Check.That(collision.Status == TxStatus.Accepted && collision.IsReplay,
                "same-op reuse replays the original outcome, got " + collision.Status);
            Check.Equal(5, collision.AcceptedTotal(), "replay carries the ORIGINAL body (5), not the colliding 9");
            Check.Equal(5, chest.TotalOf(Wood), "collision applies nothing");
            Check.Equal(0, chest.TotalOf(Stone), "colliding body materializes nothing");
            Check.Equal(1, core.ProcessedCount, "collision seeds nothing new");
            TxResult orig = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5));
            Check.That(orig.Status == TxStatus.Accepted && orig.IsReplay,
                "original still replays afterwards, got " + orig.Status);
            Check.Equal(5, chest.TotalOf(Wood), "replay applies nothing");
        }

        /// <summary>
        /// Cross-op replay guard (shared seam TxDecision.IsOpMismatch): a txId that
        /// payload (never the Add body), never executes, and leaves the cache
        /// untouched — the original Add still replays afterwards.
        /// </summary>
        private static void REPLAY_MismatchedOpNeverReturnsOldPayload()
        {
            Check.That(TxDecision.IsOpMismatch(TxOp.Add, TxOp.Take), "add/take mismatch detected by the seam");
            Check.That(!TxDecision.IsOpMismatch(TxOp.Add, TxOp.Add), "same op is not a mismatch");
            Check.That(!TxDecision.IsOpMismatch(TxOp.Add, TxOp.Query), "query lookup is exempt by design");
            TxCore core = new TxCore();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "add commits");
            Check.Equal(5, chest.TotalOf(Wood), "add applied");

            TxResult cross = core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 5));
            Check.That(cross.Status == TxStatus.UnknownTx && !cross.IsReplay,
                "cross-op take refused indeterminate, got " + cross.Status);
            Check.Equal(0, cross.Accepted.Count, "cross-op refusal carries no payload");
            Check.Equal(5, chest.TotalOf(Wood), "cross-op request executes nothing");
            Check.Equal(1, core.ProcessedCount, "cross-op request overwrites nothing");

            TxResult orig = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5));
            Check.That(orig.Status == TxStatus.Accepted && orig.IsReplay,
                "original op still replays, got " + orig.Status);
            Check.Equal(5, orig.AcceptedTotal(), "original body intact");
            Check.Equal(5, chest.TotalOf(Wood), "replay applies nothing");
            TxResult query = core.Query(Tx(11, 1), 11);
            Check.That(query.Status == TxStatus.Accepted && query.IsReplay,
                "query by txId still returns the original outcome");
        }

        /// <summary>
        /// Op-collision twin (reverse direction + legacy sender): a txId that
        /// committed as Take, re-requested as Add (counter collision after a reset
        /// or a stale sender grid), answers Indeterminate — never executes, never
        /// leaks the Take body. The gate holds for sender tag 0 too (legacy resends
        /// with a mismatched op are refused; matching-op sender-0 resends still
        /// replay — see SENDER0_LegacyFreshAndQueryDocumented).
        /// </summary>
        private static void REPLAY_OpCollisionSameTxIdFailsClosed()
        {
            TxCore core = new TxCore();
            ModelChest chest = new ModelChest(6, 4);
            chest.AddItem(Wood, 20, 50);

            Check.That(core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 6)).Status == TxStatus.Accepted, "take commits");
            Check.Equal(14, chest.TotalOf(Wood), "take debited");

            TxResult collision = core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 6));
            Check.That(collision.Status == TxStatus.UnknownTx && !collision.IsReplay,
                "colliding add refused indeterminate, got " + collision.Status);
            Check.Equal(0, collision.Accepted.Count, "collision carries no take payload");
            Check.Equal(14, chest.TotalOf(Wood), "collision executes nothing");

            TxResult skewCollision = core.Apply(chest, AddReq(Tx(11, 1), 0, Wood, 6));
            Check.That(skewCollision.Status == TxStatus.UnknownTx && !skewCollision.IsReplay,
                "sender-0 mismatched op refused too, got " + skewCollision.Status);
            Check.Equal(14, chest.TotalOf(Wood), "skew collision executes nothing");

            TxResult orig = core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 6));
            Check.That(orig.Status == TxStatus.Accepted && orig.IsReplay,
                "original take still replays, got " + orig.Status);
            Check.Equal(6, orig.AcceptedTotal(), "original take body intact");
            Check.Equal(14, chest.TotalOf(Wood), "replay debits nothing");
        }

        /// <summary>
        /// Sender-0 legacy audit (documents, not changes, production behavior):
        /// sender tag 0 is never a spoof (TxDecision.ClassifySenderBinding). A fresh
        /// sender-0 mutation commits on the normal path; a matching-op sender-0
        /// resend replays the original outcome INCLUDING its body (accepted legacy:
        /// version-skew resends must recover); a sender-0 Query by txId returns the
        /// original outcome. Only NONZERO sender mismatches get payload-stripped
        /// Rejected (see SPOOF_CachedResultNotLeaked). Cross-op sender-0 requests
        /// are still refused by the op-mismatch gate.
        /// </summary>
        private static void SENDER0_LegacyFreshAndQueryDocumented()
        {
            TxCore core = new TxCore();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            TxResult skewReplay = core.Apply(chest, AddReq(Tx(11, 1), 0, Wood, 5));
            Check.That(skewReplay.Status == TxStatus.Accepted && skewReplay.IsReplay,
                "sender-0 matching-op resend replays the original, got " + skewReplay.Status);
            Check.Equal(5, skewReplay.AcceptedTotal(), "legacy resend keeps the body (documented, not leaked: same txId family)");
            Check.Equal(5, chest.TotalOf(Wood), "legacy resend applies nothing");

            TxResult skewQuery = core.Query(Tx(11, 1), 0);
            Check.That(skewQuery.Status == TxStatus.Accepted && skewQuery.IsReplay,
                "sender-0 query returns the original outcome");

            TxResult freshZero = core.Apply(chest, AddReq(Tx(11, 9), 0, Wood, 3));
            Check.That(freshZero.Status == TxStatus.Accepted && !freshZero.IsReplay,
                "fresh sender-0 mutation commits on the normal path (legacy, not a spoof), got " + freshZero.Status);
            Check.Equal(8, chest.TotalOf(Wood), "fresh sender-0 add applied once");
        }

        /// <summary>
        /// Pump decision matrix through the production-shared TxPendingDrain.Decide:
        /// deadline beats due-check; FinalQuery fires exactly once; the
        /// payload/query budget split matches PumpPending (PayloadAttempts=4,
        /// QueryAttempts=2); a spent budget waits for the deadline.
        /// </summary>
        private static void PENDING_DecideMatrix()
        {
            Check.That(TxPendingDrain.Decide(0f, 0f, 100f, 0, false, false, 4, 2) == TxPendingDrain.Step.Resend,
                "fresh due entry resends");
            Check.That(TxPendingDrain.Decide(0f, 0f, 100f, 3, false, false, 4, 2) == TxPendingDrain.Step.Resend,
                "payload budget unspent resends");
            Check.That(TxPendingDrain.Decide(0f, 0f, 100f, 4, false, false, 4, 2) == TxPendingDrain.Step.Query,
                "payload budget spent, query unspent => one Query, never a re-apply");
            Check.That(TxPendingDrain.Decide(0f, 0f, 100f, 5, true, false, 4, 2) == TxPendingDrain.Step.Wait,
                "query sent, attempts past budget => wait for the deadline");
            Check.That(TxPendingDrain.Decide(0f, 50f, 100f, 4, false, false, 4, 2) == TxPendingDrain.Step.Wait,
                "not due yet => wait even with budget left");
            Check.That(TxPendingDrain.Decide(100f, 0f, 100f, 9, true, false, 4, 2) == TxPendingDrain.Step.FinalQuery,
                "deadline with last-chance query unspent => FinalQuery");
            Check.That(TxPendingDrain.Decide(101f, 0f, 100f, 9, true, true, 4, 2) == TxPendingDrain.Step.Terminal,
                "deadline with query spent => terminal Indeterminate");
            Check.That(TxPendingDrain.Decide(0f, 0f, 100f, 99, true, true, 4, 2) == TxPendingDrain.Step.Wait,
                "huge attempts but live deadline => still wait (deadline rules)");
        }

        /// <summary>
        /// One pump over the shared drain with several timeouts: every terminal
        /// entry finalizes exactly once in that pump (no in-loop dictionary
        /// mutation, no skipped entries), FinalQuery extends exactly once, due
        /// entries resend, waiting entries are untouched.
        /// </summary>
        private static void PENDING_MultipleTimeoutsOnePump()
        {
            Dictionary<long, FakePending> pending = new Dictionary<long, FakePending>();
            pending[101] = TerminalEntry(50f);
            pending[102] = TerminalEntry(50f);
            pending[103] = TerminalEntry(50f);
            FakePending finalQ = new FakePending();
            finalQ.NextTryAt = 0f;
            finalQ.Deadline = 50f;
            finalQ.Attempts = 6;
            finalQ.QuerySent = true;
            finalQ.FinalQuerySent = false;
            pending[104] = finalQ;
            FakePending resend = new FakePending();
            resend.NextTryAt = 0f;
            resend.Deadline = 500f;
            resend.Attempts = 1;
            pending[105] = resend;
            FakePending waiting = new FakePending();
            waiting.NextTryAt = 400f;
            waiting.Deadline = 500f;
            waiting.Attempts = 1;
            pending[106] = waiting;

            float now = 100f;
            List<long> terminals = new List<long>();
            List<long> resent = new List<long>();
            List<long> queried = new List<long>();
            TxPendingDrain.Drain<long, FakePending>(
                pending,
                delegate (long key, FakePending p)
                {
                    if (p.Destroyed)
                        return TxPendingDrain.Step.Terminal;
                    return TxPendingDrain.Decide(now, p.NextTryAt, p.Deadline, p.Attempts,
                        p.QuerySent, p.FinalQuerySent, 4, 2);
                },
                delegate (long key, FakePending p, TxPendingDrain.Step step)
                {
                    if (step == TxPendingDrain.Step.Resend)
                    {
                        resent.Add(key);
                        p.Attempts++;
                        p.NextTryAt = now + 3f;
                    }
                    else if (step == TxPendingDrain.Step.Query)
                    {
                        queried.Add(key);
                        p.QuerySent = true;
                        p.Attempts++;
                        p.NextTryAt = now + 3f;
                    }
                    else if (step == TxPendingDrain.Step.FinalQuery)
                    {
                        queried.Add(key);
                        p.FinalQuerySent = true;
                        p.QuerySent = true;
                        p.Attempts++;
                        p.Deadline = now + 3f;
                        p.NextTryAt = now + 3f;
                    }
                },
                delegate (long key)
                {
                    terminals.Add(key);
                    pending.Remove(key);
                });

            Check.Equal(3, terminals.Count, "three timeouts finalize in one pump");
            Check.That(terminals.Contains(101) && terminals.Contains(102) && terminals.Contains(103),
                "exactly the timed-out entries finalize");
            Check.Equal(1, queried.Count, "one final query sent");
            Check.Equal(104, queried[0], "final query targets the unspent entry");
            Check.Equal(1, resent.Count, "one due entry resends");
            Check.Equal(105, resent[0], "resend targets the due entry");
            Check.That(pending.ContainsKey(104) && pending.ContainsKey(105) && pending.ContainsKey(106),
                "non-terminals stay pending");
            Check.That(!pending.ContainsKey(101) && !pending.ContainsKey(102) && !pending.ContainsKey(103),
                "terminals removed exactly once");

            // Exactly-once across pumps: the extended final query waits, nothing re-fires.
            int terminalsBefore = terminals.Count;
            int queriedBefore = queried.Count;
            TxPendingDrain.Drain<long, FakePending>(
                pending,
                delegate (long key, FakePending p)
                {
                    if (p.Destroyed)
                        return TxPendingDrain.Step.Terminal;
                    return TxPendingDrain.Decide(now, p.NextTryAt, p.Deadline, p.Attempts,
                        p.QuerySent, p.FinalQuerySent, 4, 2);
                },
                delegate (long key, FakePending p, TxPendingDrain.Step step)
                {
                    if (step == TxPendingDrain.Step.Terminal)
                        terminals.Add(key);
                    else if (step == TxPendingDrain.Step.FinalQuery || step == TxPendingDrain.Step.Query)
                        queried.Add(key);
                    else if (step == TxPendingDrain.Step.Resend)
                        resent.Add(key);
                },
                delegate (long key)
                {
                    terminals.Add(key);
                    pending.Remove(key);
                });
            Check.Equal(terminalsBefore, terminals.Count, "no second terminal wave in the next pump");
            Check.Equal(queriedBefore, queried.Count, "no second final query in the next pump");
        }

        private static FakePending TerminalEntry(float deadline)
        {
            FakePending p = new FakePending();
            p.NextTryAt = 0f;
            p.Deadline = deadline;
            p.Attempts = 9;
            p.QuerySent = true;
            p.FinalQuerySent = true;
            return p;
        }

        /// <summary>
        /// Terminal completion through the shared drain: remove-then-complete with
        /// an idempotent finalizer (production CompletePendingTerminal order), so a
        /// late duplicate finds no entry and every claim releases exactly once.
        /// Destroyed entries finalize terminally instead of stalling silently.
        /// </summary>
        private static void PENDING_TerminalCallbackAndClaimsExactlyOnce()
        {
            Dictionary<long, FakePending> pending = new Dictionary<long, FakePending>();
            FakePending a = TerminalEntry(10f);
            a.Claims.AddRange(new string[] { "sword", "shield" });
            FakePending b = TerminalEntry(10f);
            b.Claims.Add("bow");
            FakePending gone = TerminalEntry(10f);
            gone.Destroyed = true;
            gone.Claims.Add("helm");
            pending[1] = a;
            pending[2] = b;
            pending[3] = gone;

            float now = 100f;
            List<long> callbacks = new List<long>();
            List<string> released = new List<string>();
            Action<long> finalize = delegate (long key)
            {
                FakePending p;
                if (!pending.TryGetValue(key, out p))
                    return; // late duplicate: already completed, touch nothing.
                pending.Remove(key);
                callbacks.Add(key);
                for (int i = 0; i < p.Claims.Count; i++)
                    released.Add(p.Claims[i]);
                p.Claims.Clear();
            };
            TxPendingDrain.Drain<long, FakePending>(
                pending,
                delegate (long key, FakePending p)
                {
                    if (p.Destroyed)
                        return TxPendingDrain.Step.Terminal;
                    return TxPendingDrain.Decide(now, p.NextTryAt, p.Deadline, p.Attempts,
                        p.QuerySent, p.FinalQuerySent, 4, 2);
                },
                delegate (long key, FakePending p, TxPendingDrain.Step step)
                {
                    Check.That(false, "no step expected, got " + step + " for " + key);
                },
                finalize);

            Check.Equal(3, callbacks.Count, "every terminal fires its callback exactly once (incl. destroyed)");
            Check.Equal(0, pending.Count, "every terminal removed");
            Check.Equal(4, released.Count, "every claim released exactly once");
            Check.That(released.Contains("sword") && released.Contains("shield")
                && released.Contains("bow") && released.Contains("helm"), "no claim stranded");

            // A late duplicate finalize is a guarded no-op (production
            // CompletePendingTerminal order: remove first, guarded re-entry).
            finalize(1);
            finalize(3);
            Check.Equal(3, callbacks.Count, "late duplicates fire no second callback");
            Check.Equal(4, released.Count, "late duplicates release nothing twice");
        }

        /// <summary>
        /// Claim lease, production primitive (ChestTxService.InFlightAdds):
        /// first submit wins, later duplicates of the same live item are pruned.
        /// </summary>
        private static void CLAIM_DuplicatePruned()
        {
            TxClaimSet<object> live = new TxClaimSet<object>();
            object sword = new object();
            object shield = new object();
            Check.That(live.TryClaim(sword), "first claim wins");
            Check.That(live.TryClaim(shield), "second item claims");
            Check.That(!live.TryClaim(sword), "duplicate in flight is pruned, never submitted twice");
            Check.Equal(2, live.Count, "pruned duplicate holds no new lease");
            Check.That(live.IsClaimed(sword) && live.IsClaimed(shield), "held claims visible");
            Check.That(!live.IsClaimed(new object()), "unknown item unclaimed");
        }

        /// <summary>
        /// Claim lease across every pre-ownership failure path (production Submit
        /// try/finally shape: counter exhausted, encode failure, invalid netview,
        /// insert failure, destroyed container): whatever was claimed is released,
        /// so the next submit of the same live stack is never starved. The owned
        /// (Pending) case keeps holding until terminal release.
        /// </summary>
        private static void CLAIM_PrePendingFailuresRelease()
        {
            string[] modes = new string[] { "counter", "encode", "netview", "insert", "destroyed" };
            for (int m = 0; m < modes.Length; m++)
            {
                TxClaimSet<object> live = new TxClaimSet<object>();
                object sword = new object();
                object shield = new object();
                List<object> claimed = new List<object>();
                // Mirror of production Submit: claim first, release unless owned.
                bool owned = false;
                try
                {
                    if (live.TryClaim(sword))
                        claimed.Add(sword);
                    if (live.TryClaim(shield))
                        claimed.Add(shield);
                    if (modes[m] == "counter")
                    {
                        owned = false;
                    }
                    else if (modes[m] == "encode" || modes[m] == "netview" || modes[m] == "insert")
                    {
                        owned = false;
                    }
                    else
                    {
                        owned = false; // destroyed before insert: never owned.
                    }
                }
                finally
                {
                    if (!owned)
                        live.ReleaseAll(claimed);
                }
                Check.Equal(0, live.Count, modes[m] + ": failure releases every claim");
                Check.That(live.TryClaim(sword), modes[m] + ": same stack submittable again");
                live.Release(sword);
            }

            // Owned handoff: Pending owns the claims — no early release.
            TxClaimSet<object> held = new TxClaimSet<object>();
            object axe = new object();
            List<object> batch = new List<object>();
            bool pendingOwns = false;
            try
            {
                if (held.TryClaim(axe))
                    batch.Add(axe);
                pendingOwns = true; // insert succeeded: Pending owns them now.
            }
            finally
            {
                if (!pendingOwns)
                    held.ReleaseAll(batch);
            }
            Check.That(held.IsClaimed(axe), "owned claims stay held while Pending owns them");
            Check.That(!held.TryClaim(axe), "duplicate pruned while Pending owns the item");
        }

        /// <summary>
        /// Terminal release is exactly-once and idempotent: double-finalize frees
        /// nothing twice and never strands a claim; null/empty releases are safe.
        /// </summary>
        private static void CLAIM_TerminalReleaseExactlyOnce()
        {
            TxClaimSet<object> live = new TxClaimSet<object>();
            object sword = new object();
            object shield = new object();
            List<object> batch = new List<object>();
            batch.Add(sword);
            batch.Add(shield);
            live.TryClaim(sword);
            live.TryClaim(shield);

            live.ReleaseAll(batch);
            Check.Equal(0, live.Count, "terminal releases every claim");
            live.ReleaseAll(batch);
            Check.Equal(0, live.Count, "double terminal is a no-op, never negative");
            live.Release(sword);
            Check.Equal(0, live.Count, "single re-release is a no-op");
            live.ReleaseAll(null);
            Check.Equal(0, live.Count, "null release is safe");
            Check.That(!live.IsClaimed(null), "null never claimed");

            Check.That(live.TryClaim(sword), "released item reclaimable (never stranded)");
            Check.That(!live.IsClaimed(shield), "other item stays released");
            live.Release(sword);
        }

        /// <summary>
        /// Drag remainder on known-not-committed (Rejected): the full dragged stack
        /// restores — nothing was committed, so restoring all of it is exact.
        /// </summary>
        private static void DRAG_Rejected()
        {
            Check.That(TxDecision.ShouldRestoreDragRemainder(TxCompletionKind.FailedNotCommitted),
                "Rejected is known-safe for restore");
            Check.Equal(10, TxDecision.DragRestoreAmount(10, 0, TxCompletionKind.FailedNotCommitted),
                "Rejected restores the full dragged stack");
        }

        /// <summary>
        /// Drag remainder on committed-with-counts (Partial/Normal): only the
        /// uncommitted remainder restores, never negative, never over the stack.
        /// </summary>
        private static void DRAG_Partial()
        {
            Check.That(TxDecision.ShouldRestoreDragRemainder(TxCompletionKind.Normal),
                "Normal is known-safe for restore");
            Check.Equal(6, TxDecision.DragRestoreAmount(10, 4, TxCompletionKind.Normal),
                "Partial restores stack-minus-accepted");
            Check.Equal(0, TxDecision.DragRestoreAmount(10, 10, TxCompletionKind.Normal),
                "fully accepted restores nothing");
            Check.Equal(0, TxDecision.DragRestoreAmount(10, 99, TxCompletionKind.Normal),
                "over-accept clamps to 0, never negative");
            Check.Equal(0, TxDecision.DragRestoreAmount(0, 0, TxCompletionKind.Normal),
                "empty drag restores nothing");
        }

        /// <summary>
        /// Drag remainder on Indeterminate/details-unavailable: restore NOTHING —
        /// the chest may already hold the items, and a blind full restore would
        /// duplicate them. Withholding is not dropping (phase-2 escrow is future
        /// work, explicitly not built here); the path stays loud in production.
        /// </summary>
        private static void DRAG_Indeterminate()
        {
            Check.That(!TxDecision.ShouldRestoreDragRemainder(TxCompletionKind.Indeterminate),
                "Indeterminate must never blind-restore");
            Check.That(!TxDecision.ShouldRestoreDragRemainder(TxCompletionKind.CommittedTakeDetailsUnavailable),
                "take details-unavailable must never blind-restore");
            Check.That(!TxDecision.ShouldRestoreDragRemainder(TxCompletionKind.CommittedMultiAddDetailsUnavailable),
                "multi-add details-unavailable must never blind-restore");
            // The trap: accepted==0 with a full stack in hand looks restorable —
            // on unknown outcomes it is exactly the duplication window.
            Check.Equal(0, TxDecision.DragRestoreAmount(10, 0, TxCompletionKind.Indeterminate),
                "Indeterminate restores nothing even with accepted==0");
            Check.Equal(0, TxDecision.DragRestoreAmount(10, 0, TxCompletionKind.CommittedMultiAddDetailsUnavailable),
                "details-unavailable restores nothing even with accepted==0");
            Check.Equal(0, TxDecision.DragRestoreAmount(10, 6, TxCompletionKind.Indeterminate),
                "Indeterminate restores nothing even with partial counts");
        }
    }
}
