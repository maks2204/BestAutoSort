using System;
using System.Collections.Generic;
using System.IO;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Hardening group (v0.5.x wave-2). Covers ONLY ground wave-1 left open;
    /// everything wave-1 already pins is REUSED by reference (no duplicates):
    ///
    /// - quarantine delayed dup ........... QUARANTINE_TakeDelayedDuplicateAfterRecovery (TxWave1IsolationTests)
    /// - spoof victim-txId/floor/payload ... SPOOF_VictimTxIdNotBurned,
    ///   SPOOF_IncompatiblePeerCannotRaiseVictimFloor, SPOOF_CachedResultNotLeaked (TxWave1IsolationTests)
    /// - drag restore x3 .................. DRAG_Rejected, DRAG_Partial, DRAG_Indeterminate (TxWave1IsolationTests)
    /// - pending enumeration + terminal ... PENDING_MultipleTimeoutsOnePump,
    ///   PENDING_TerminalCallbackAndClaimsExactlyOnce (TxWave1IsolationTests)
    /// - claim lease release .............. CLAIM_DuplicatePruned, CLAIM_PrePendingFailuresRelease,
    ///   CLAIM_TerminalReleaseExactlyOnce (TxWave1IsolationTests)
    /// - out-of-order Indeterminate ........ V2_OutOfOrderDelayed (TxRingV2Tests) + TxTests trusted-floor case
    /// - reload-fail quarantine ........... DIRTYRAM_Quarantine_ReloadFailAndQueuedJobs (TxDirtyRamTests)
    ///
    /// NEW here (all against PRODUCTION code — TxCore/TxCounterFile/TxIdGen):
    /// - counter legacy/atomic/crash/wrap: TxCounterFile format, checksum,
    ///   torn-write fallback, atomic persist, uint-boundary fail-closed.
    /// - empty-chest observation: Take on an empty chest is a stable persisted
    ///   Rejected (known-not-committed), never Indeterminate, never a debit.
    /// - s_items==null observation: a null persisted authority (model for null
    ///   s_items bytes) keeps the quarantine — never presumed empty — with the
    ///   same terminal the production reload/takeover/structural paths answer.
    ///   Production handling is UNCHANGED (diagnostic counter only).
    /// </summary>
    internal static class TxHardeningTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);

        private static long Tx(long peer, uint ctr)
        {
            return unchecked((peer << 32) | ctr);
        }

        /// <summary>Fake durable world; NullAuthority models null s_items bytes
        /// (no persisted authority to reload from — never presumed empty).</summary>
        private sealed class HardWorld
        {
            public byte[] RingBytes;
            public byte[] FloorBytes;
            public List<int[]> Items;
            public bool FailSave;
            public bool FailReload;
            public bool NullAuthority;

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
                    if (FailReload || NullAuthority || Items == null)
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

        private static TxRequest AddReq(long txId, long sender, ItemKey key, int amount)
        {
            TxRequest r = new TxRequest();
            r.TxId = txId;
            r.Op = TxOp.Add;
            r.Sender = sender;
            r.Items.Add(new TxItem { Key = key, Amount = amount, MaxStack = 50 });
            return r;
        }

        public static void RunAll()
        {
            Console.WriteLine("HARDENING_CounterLegacyPlainInteger");
            HARDENING_CounterLegacyPlainInteger();
            Console.WriteLine("HARDENING_CounterVersionedRoundTrip");
            HARDENING_CounterVersionedRoundTrip();
            Console.WriteLine("HARDENING_CounterChecksumMismatch");
            HARDENING_CounterChecksumMismatch();
            Console.WriteLine("HARDENING_CounterCorruptPrimaryBackupAware");
            HARDENING_CounterCorruptPrimaryBackupAware();
            Console.WriteLine("HARDENING_CounterWrapFailClosed");
            HARDENING_CounterWrapFailClosed();
            Console.WriteLine("HARDENING_CounterAtomicWrite");
            HARDENING_CounterAtomicWrite();
            Console.WriteLine("HARDENING_EmptyChestTakeRejected");
            HARDENING_EmptyChestTakeRejected();
            Console.WriteLine("HARDENING_SItemsNullNeverPresumedEmpty");
            HARDENING_SItemsNullNeverPresumedEmpty();
        }

        /// <summary>
        /// Legacy plain-integer counter files (pre-v1, written by WriteAllText)
        /// still parse, including trailing newlines. Production LoadReservedCounter
        /// resolves them through the same seam.
        /// </summary>
        private static void HARDENING_CounterLegacyPlainInteger()
        {
            uint ceil;
            Check.That(TxCounterFile.TryParse("4096", out ceil) && ceil == 4096u, "legacy plain integer parses");
            Check.That(TxCounterFile.TryParse("  8192\r\n", out ceil) && ceil == 8192u, "legacy with whitespace parses");
            Check.That(TxCounterFile.TryParse("1", out ceil) && ceil == 1u, "legacy 1 parses (unusable as resume, see ResolveLoad)");
            uint resolved;
            bool restore;
            Check.That(TxCounterFile.ResolveLoad("4096", null, out resolved, out restore)
                && resolved == 4096u && !restore, "usable legacy primary resumes without backup restore");
            Check.That(!TxCounterFile.ResolveLoad("1", null, out resolved, out restore),
                "ceil 0/1 is never a resume point");
            Check.That(!TxCounterFile.ResolveLoad("0", "4096", out resolved, out restore)
                || resolved != 0u, "zero primary never resumes");
        }

        /// <summary>Versioned format round-trips, including boundary-adjacent ceils.</summary>
        private static void HARDENING_CounterVersionedRoundTrip()
        {
            uint[] ceils = new uint[] { 2u, 4096u, 8192u, 1000000u, uint.MaxValue - 4097u };
            for (int i = 0; i < ceils.Length; i++)
            {
                string text = TxCounterFile.Format(ceils[i]);
                uint back;
                Check.That(TxCounterFile.TryParse(text, out back) && back == ceils[i],
                    "v1 round-trip ceil=" + ceils[i] + " text=" + text);
            }
        }

        /// <summary>
        /// Checksum mismatch, truncation (torn write), garbage and empties never
        /// parse — the loader must fall back to the backup, never trust them.
        /// </summary>
        private static void HARDENING_CounterChecksumMismatch()
        {
            uint ceil;
            string good = TxCounterFile.Format(4096u);
            Check.That(!TxCounterFile.TryParse(null, out ceil), "null never parses");
            Check.That(!TxCounterFile.TryParse(string.Empty, out ceil), "empty never parses");
            Check.That(!TxCounterFile.TryParse("garbage-bytes", out ceil), "garbage never parses");
            Check.That(!TxCounterFile.TryParse("v1:4096", out ceil), "truncated v1 (torn write) never parses");
            Check.That(!TxCounterFile.TryParse("v1:4096:", out ceil), "empty checksum never parses");
            Check.That(!TxCounterFile.TryParse("v1:4096:deadbeef", out ceil), "wrong checksum never parses");
            string[] parts = good.Split(':');
            string tampered = parts[0] + ":4097:" + parts[2];
            Check.That(!TxCounterFile.TryParse(tampered, out ceil), "ceil tamper against old checksum never parses");
            Check.That(!TxCounterFile.TryParse("v1:abc:" + parts[2], out ceil), "non-numeric ceil never parses");
            Check.That(!TxCounterFile.TryParse("v1:99999999999:" + parts[2], out ceil), "overflowing ceil never parses");
        }

        /// <summary>
        /// Backup-aware load: corrupt primary + good backup resumes from the
        /// backup (flagging a primary restore); good primary wins untouched;
        /// both untrustworthy resolves to false (caller archives evidence and
        /// resumes at 1 LOUDLY — never a silent rollback, never an invented
        /// high counter).
        /// </summary>
        private static void HARDENING_CounterCorruptPrimaryBackupAware()
        {
            string primary = TxCounterFile.Format(8192u);
            string backup = TxCounterFile.Format(4096u);
            uint resolved;
            bool restore;
            Check.That(TxCounterFile.ResolveLoad(primary, backup, out resolved, out restore)
                && resolved == 8192u && !restore, "good primary wins, no restore");
            Check.That(TxCounterFile.ResolveLoad(primary, "torn-write", out resolved, out restore)
                && resolved == 8192u && !restore, "good primary survives corrupt backup");
            Check.That(TxCounterFile.ResolveLoad("v1:8192:deadbeef", backup, out resolved, out restore)
                && resolved == 4096u && restore, "corrupt primary falls back to backup with restore flag");
            Check.That(TxCounterFile.ResolveLoad(null, backup, out resolved, out restore)
                && resolved == 4096u && restore, "missing primary falls back to backup");
            Check.That(TxCounterFile.ResolveLoad("1", backup, out resolved, out restore)
                && resolved == 4096u && restore, "unusable primary (ceil 1) falls back to backup");
            Check.That(!TxCounterFile.ResolveLoad("garbage", "also-garbage", out resolved, out restore),
                "both corrupt resolves false (archive + loud resume at 1)");
            Check.That(!TxCounterFile.ResolveLoad(null, null, out resolved, out restore),
                "both missing resolves false");
            Check.That(!TxCounterFile.ResolveLoad("0", "1", out resolved, out restore),
                "both unusable resolves false");
        }

        /// <summary>
        /// uint-boundary fail-closed: TryNext refuses at 0/MaxValue (never wraps
        /// silently) and the reservation rule refuses a block that would reach
        /// MaxValue. Production IssueTxId/ReserveTxCounter share this seam.
        /// </summary>
        private static void HARDENING_CounterWrapFailClosed()
        {
            long txId;
            uint c = uint.MaxValue - 1u;
            Check.That(TxIdGen.TryNext(7, ref c, out txId), "one slot below max still issues");
            Check.That(TxIdGen.CounterOf(txId) == uint.MaxValue - 1u, "issued counter is MaxValue-1");
            Check.That(c == uint.MaxValue, "counter advanced to MaxValue");
            Check.That(!TxIdGen.TryNext(7, ref c, out txId) && txId == 0L, "at MaxValue: refused, txId 0");
            c = uint.MaxValue;
            Check.That(!TxIdGen.TryNext(7, ref c, out txId) && txId == 0L, "MaxValue always refused");
            c = 0u;
            Check.That(!TxIdGen.TryNext(7, ref c, out txId) && txId == 0L, "zero counter always refused");

            uint newCeil;
            Check.That(!TxCounterFile.TryReserve(uint.MaxValue, uint.MaxValue, 4096u, out newCeil),
                "reserve at MaxValue refused");
            Check.That(!TxCounterFile.TryReserve(0u, 0u, 4096u, out newCeil), "reserve at 0 refused");
            Check.That(!TxCounterFile.TryReserve(uint.MaxValue - 4096u, 0u, 4096u, out newCeil),
                "block reaching MaxValue refused (no silent wrap)");
            Check.That(TxCounterFile.TryReserve(uint.MaxValue - 4097u, 0u, 4096u, out newCeil)
                && newCeil == uint.MaxValue - 1u, "block ending below MaxValue allowed");
            Check.That(TxCounterFile.TryReserve(100u, 8192u, 4096u, out newCeil)
                && newCeil == 8192u, "covered counter needs no new persist");
            Check.That(!TxCounterFile.TryReserve(100u, 0u, 0u, out newCeil), "zero block refused");
        }

        /// <summary>
        /// Crash-atomic persist against a real temp directory: primary parses
        /// after write, the second write keeps a parseable backup, and a
        /// simulated torn primary resolves to the backup.
        /// </summary>
        private static void HARDENING_CounterAtomicWrite()
        {
            string dir = Path.Combine(Path.GetTempPath(), "chesttx_hard_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "BestAutoSort_TxCounter.txt");
                string backup = TxCounterFile.BackupPathFor(path);
                Check.That(!TxCounterFile.WriteAtomically(null, 1u), "null path refused");
                Check.That(!TxCounterFile.WriteAtomically(string.Empty, 1u), "empty path refused");
                Check.That(TxCounterFile.WriteAtomically(path, 5000u), "first atomic write succeeds");
                uint ceil;
                Check.That(TxCounterFile.TryParse(File.ReadAllText(path), out ceil) && ceil == 5000u,
                    "primary parses after first write");
                Check.That(TxCounterFile.WriteAtomically(path, 9096u), "second atomic write succeeds");
                Check.That(TxCounterFile.TryParse(File.ReadAllText(path), out ceil) && ceil == 9096u,
                    "primary carries the new ceil");
                Check.That(File.Exists(backup), "backup copy exists after second write");
                Check.That(TxCounterFile.TryParse(File.ReadAllText(backup), out ceil) && ceil == 5000u,
                    "backup carries the previous ceil");
                // Simulated crash mid-write: torn primary, intact backup.
                File.WriteAllText(path, "v1:90");
                uint resolved;
                bool restore;
                Check.That(TxCounterFile.ResolveLoad(File.ReadAllText(path), File.ReadAllText(backup),
                    out resolved, out restore) && resolved == 5000u && restore,
                    "torn primary resolves to backup with restore flag");
                Check.That(TxCounterFile.WriteAtomically(path, resolved), "restore rewrite succeeds");
                Check.That(TxCounterFile.TryParse(File.ReadAllText(path), out ceil) && ceil == 5000u,
                    "primary healthy again after restore");
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        /// <summary>
        /// Empty-chest observation (no behavior change — pins it): Take on an
        /// empty chest is a STABLE persisted Rejected (known-not-committed),
        /// never Indeterminate and never a debit; the retry is a replay of the
        /// same Rejected; revision does not move; a later real Take still commits.
        /// </summary>
        private static void HARDENING_EmptyChestTakeRejected()
        {
            HardWorld w = new HardWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);
            Check.Equal(0, chest.TotalOf(Wood), "chest starts empty");

            TxResult take = core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 5));
            Check.That(take.Status == TxStatus.Rejected && !take.IsReplay,
                "take on empty chest is Rejected (known-not-committed), got " + take.Status);
            Check.Equal(0, chest.TotalOf(Wood), "empty take debits nothing");
            Check.Equal(0, (long)core.Revision, "empty take moves no revision");

            TxResult retry = core.Apply(chest, TakeReq(Tx(11, 1), 11, Wood, 5));
            Check.That(retry.Status == TxStatus.Rejected && retry.IsReplay,
                "same-txId retry replays the stable Rejected, got " + retry.Status);
            Check.Equal(0, chest.TotalOf(Wood), "replay debits nothing");

            Check.That(core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 5)).Status == TxStatus.Accepted, "add commits");
            TxResult real = core.Apply(chest, TakeReq(Tx(11, 3), 11, Wood, 5));
            Check.That(real.Status == TxStatus.Accepted, "take of present stock commits, got " + real.Status);
            Check.Equal(0, chest.TotalOf(Wood), "stock conserved: 5 in, 5 out");
        }

        /// <summary>
        /// s_items==null observation (no behavior change — pins it): when the
        /// persisted authority is unavailable (null, modelling null s_items
        /// bytes at the production reload/takeover/structural sites), recovery
        /// FAILS, the quarantine stays, fresh txIds answer the shared
        /// quarantine terminal WITHOUT mutating, and NOTHING is presumed empty.
        /// Only a successful authoritative reload clears it.
        /// </summary>
        private static void HARDENING_SItemsNullNeverPresumedEmpty()
        {
            Check.That(TxDecision.QuarantinedTerminal() == TxStatus.UnknownTx,
                "shared quarantine terminal is Indeterminate");
            HardWorld w = new HardWorld();
            TxCore core = new TxCore();
            core.Durability = w.Hooks();
            ModelChest chest = new ModelChest(6, 4);

            Check.That(core.Apply(chest, AddReq(Tx(11, 1), 11, Wood, 5)).Status == TxStatus.Accepted, "seed commits");
            Check.Equal(5, chest.TotalOf(Wood), "seed live");

            w.FailSave = true;
            w.NullAuthority = true;
            TxResult failed = core.Apply(chest, AddReq(Tx(11, 2), 11, Wood, 10));
            Check.That(failed.Status == TxStatus.UnknownTx && !failed.IsReplay, "failed tx indeterminate");
            Check.That(core.Quarantined, "null authority quarantines (never presumed empty)");
            Check.That(!core.TryRecover(chest), "recovery with null authority fails");
            Check.That(core.Quarantined, "still quarantined after failed recovery");

            int before = chest.TotalOf(Wood);
            TxResult fresh = core.Apply(chest, AddReq(Tx(11, 3), 11, Wood, 7));
            Check.That(fresh.Status == TxDecision.QuarantinedTerminal() && !fresh.IsReplay,
                "quarantined fresh tx indeterminate, got " + fresh.Status);
            Check.Equal(before, chest.TotalOf(Wood), "quarantined tx mutates nothing (nothing presumed empty)");

            w.FailSave = false;
            w.NullAuthority = false;
            w.Items = HardWorld.CloneItems(chest.Snapshot());
            Check.That(core.TryRecover(chest), "authoritative reload recovers once authority exists");
            Check.That(!core.Quarantined, "quarantine cleared by reload only");
        }
    }
}
