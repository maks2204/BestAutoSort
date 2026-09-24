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
        /// Quarantined Add path: the failed tx stays Indeterminate with its fence
        /// kept; fresh txIds (even unseen peers) answer Indeterminate WITHOUT
        /// fencing/mutating/caching; only a successful authoritative TryRecover
        /// clears the quarantine (elapsed Applys never do); then new txIds commit.
        /// </summary>
        private static void QUARANTINE_Add()
        {
            Check.That(TxDecision.QuarantinedTerminal() == TxStatus.UnknownTx,
                "shared quarantine terminal is Indeterminate");
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
            Check.That(fresh.Status == TxDecision.QuarantinedTerminal() && !fresh.IsReplay,
                "quarantined fresh tx indeterminate, got " + fresh.Status);
            Check.That(core.DumpFloor().TryGetValue(TxIdGen.PeerKey(11), out hw) && hw == 2,
                "quarantined tx never fenced");
            Check.Equal(1, core.ProcessedCount, "quarantined tx cached nothing");
            Check.That(core.Quarantined, "elapsed Apply alone never clears quarantine");
            Check.That(!core.TryRecover(chest), "TryRecover fails while reload broken");
            Check.That(core.Quarantined, "still quarantined");

            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "authoritative reload recovers");
            Check.That(!core.Quarantined, "quarantine cleared by reload only");
            TxResult after = core.Apply(chest, AddReq(Tx(11, 4), 11, Wood, 7));
            Check.That(after.Status == TxStatus.Accepted, "post-recovery tx commits, got " + after.Status);
            Check.Equal(12, chest.TotalOf(Wood), "5 persisted + 7 exactly once");
        }

        /// <summary>
        /// Take delayed-duplicate across recovery: the quarantined attempt's fence
        /// is kept, so the SAME txId retried after TryRecover stays Indeterminate
        /// (never executes); only a NEW txId commits. Conservation holds.
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
            Check.That(dup.Status == TxStatus.UnknownTx && !dup.IsReplay,
                "delayed duplicate while quarantined never executes, got " + dup.Status);

            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "recovery succeeds");
            Check.Equal(14, chest.TotalOf(Wood), "live matches persisted after recovery");

            TxResult stale = core.Apply(chest, TakeReq(Tx(11, 2), 11, Wood, 4));
            Check.That(stale.Status == TxStatus.UnknownTx && !stale.IsReplay,
                "same txId stays stale-gated after recovery (fence kept), got " + stale.Status);
            Check.Equal(14, chest.TotalOf(Wood), "stale retry debits nothing");

            TxResult fresh = core.Apply(chest, TakeReq(Tx(11, 3), 11, Wood, 4));
            Check.That(fresh.Status == TxStatus.Accepted, "new txId commits, got " + fresh.Status);
            Check.Equal(10, chest.TotalOf(Wood), "exactly one new debit");
            Check.Equal(20, 6 + 4 + 10, "seed + fresh + live conserve the original stock");
        }

        /// <summary>
        /// Queued + local + remote quarantine terminals share ONE seam value:
        /// remote (OnTxRequest quarantine refusal), local (MutateLocal refusal and
        /// the ApplyJob quarantine recheck) and queued (Drain fail-closed branch)
        /// all answer Indeterminate through TxDecision.QuarantinedTerminal.
        /// Core-level proof: even an UNSEEN peer's fresh txId is refused WITHOUT
        /// fencing while quarantined — and stays retryable after recovery.
        /// </summary>
        private static void QUARANTINE_QueuedLocalRemoteShareTerminal()
        {
            Check.That(TxDecision.QuarantinedTerminal() == TxStatus.UnknownTx,
                "remote/local/queued quarantine terminal is Indeterminate");
            IsoWorld w = new IsoWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            w.FailSave = true;
            w.FailReload = true;
            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.UnknownTx, "failed tx indeterminate");
            Check.That(core.Quarantined, "quarantined");

            // Unseen peer, never fenced: still refused, and NOT fenced (retryable).
            TxResult alien = core.Apply(chest, AddReq(Tx(99, 1), 99, Wood, 5));
            Check.That(alien.Status == TxDecision.QuarantinedTerminal() && !alien.IsReplay,
                "unseen-peer tx refused without fencing, got " + alien.Status);
            Check.That(!core.DumpFloor().ContainsKey(TxIdGen.PeerKey(99)), "quarantine fences nothing, even unseen peers");
            Check.Equal(1, core.ProcessedCount, "quarantine caches nothing");

            w.FailSave = false;
            w.FailReload = false;
            Check.That(core.TryRecover(chest), "recovery succeeds");
            TxResult retry = core.Apply(chest, AddReq(Tx(99, 1), 99, Wood, 5));
            Check.That(retry.Status == TxStatus.Accepted && !retry.IsReplay,
                "unfenced txId commits after recovery, got " + retry.Status);
            Check.Equal(10, chest.TotalOf(Wood), "5 seed + 5 exactly once");
        }

        /// <summary>
        /// Handoff-drop persistence rule through the shared seam: a stable Rejected
        /// for queued-but-unapplied jobs requires a DURABLE record (ring AND floor
        /// persisted); any persistence failure stays fail-closed Indeterminate
        /// WITHOUT claiming a stable terminal. Production call sites: Takeover and
        /// AcquireForStructural (seed + WriteRing/WriteFloor, evict the unpersisted
        /// seed, keep the floor advance so an in-session retry stays stale-gated).
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
