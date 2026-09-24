using System;
using System.IO;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Counter-marker P0 (v0.5.x wave-3): marker-before-archive with a
    /// crash-resistant install. All tests run against PRODUCTION code
    /// (TxCounterFile) on real temp directories; every restart leg re-reads
    /// from disk into fresh locals (RAM discarded — no state carried between
    /// legs). The repair procedure is unchanged (marker wins until a human
    /// removes it) and the terminology is crash-resistant (never crash-atomic).
    /// </summary>
    internal static class TxMarkerCrashTests
    {
        public static void RunAll()
        {
            Console.WriteLine("COUNTER_CrashResistantMarkerInstallTmpFlush");
            COUNTER_CrashResistantMarkerInstallTmpFlush();
            Console.WriteLine("COUNTER_MarkerNeverOverwrittenBlindly");
            COUNTER_MarkerNeverOverwrittenBlindly();
            Console.WriteLine("COUNTER_NoArchiveWithoutConfirmedMarker");
            COUNTER_NoArchiveWithoutConfirmedMarker();
            Console.WriteLine("COUNTER_RestartLegsDiscardRamKeepMarker");
            COUNTER_RestartLegsDiscardRamKeepMarker();
            Console.WriteLine("COUNTER_RepairProcedureUnchangedMarkerWins");
            COUNTER_RepairProcedureUnchangedMarkerWins();
        }

        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "chesttx_mk_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DelDir(string dir)
        {
            try { Directory.Delete(dir, true); }
            catch { }
        }

        /// <summary>
        /// Crash-resistant install: tmp + flush + install leaves the marker
        /// present, no .tmp residue, and the body stamps the reason, the
        /// crash-resistant terminology and the repair procedure.
        /// </summary>
        private static void COUNTER_CrashResistantMarkerInstallTmpFlush()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "BestAutoSort_TxCounter.txt");
                Check.That(TxCounterFile.WriteBlockedMarkerCrashResistant(path, "test: both copies corrupt"),
                    "crash-resistant marker install confirms");
                Check.That(TxCounterFile.BlockedMarkerPresent(path), "marker present after install");
                Check.That(!File.Exists(TxCounterFile.BlockedPathFor(path) + ".tmp"), "no .tmp residue after install");
                string body = File.ReadAllText(TxCounterFile.BlockedPathFor(path));
                Check.That(body.Contains("test: both copies corrupt"), "marker stamps the reason");
                Check.That(body.Contains("crash-resistant"), "marker terminology is crash-resistant");
                Check.That(body.Contains(".corrupt-") && body.Contains(".blocked") && body.Contains("restart"),
                    "marker body documents the repair procedure");
                Check.That(!TxCounterFile.WriteBlockedMarkerCrashResistant(null, "x"), "null path never confirms");
                Check.That(!TxCounterFile.WriteBlockedMarkerCrashResistant(string.Empty, "x"), "empty path never confirms");
            }
            finally
            {
                DelDir(dir);
            }
        }

        /// <summary>
        /// Never overwrite blindly: a pre-existing marker body (older install,
        /// human note, raced writer) is left byte-identical while the call still
        /// confirms (presence is the signal).
        /// </summary>
        private static void COUNTER_MarkerNeverOverwrittenBlindly()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "BestAutoSort_TxCounter.txt");
                File.WriteAllText(TxCounterFile.BlockedPathFor(path), "sentinel-body");
                Check.That(TxCounterFile.WriteBlockedMarkerCrashResistant(path, "different reason"),
                    "existing marker confirms without rewrite");
                Check.That(File.ReadAllText(TxCounterFile.BlockedPathFor(path)) == "sentinel-body",
                    "existing marker body untouched (never overwritten blindly)");
                Check.That(!File.Exists(TxCounterFile.BlockedPathFor(path) + ".tmp"), "no .tmp residue on the keep path");
                Check.That(TxCounterFile.BlockedMarkerPresent(path), "marker still present");
            }
            finally
            {
                DelDir(dir);
            }
        }

        /// <summary>
        /// Archive gate: ConfirmBlockedMarker is false exactly when no marker can
        /// be confirmed (null/empty path) — production ArchiveCorruptCounter
        /// skips the archive on false (copies left in place, fail-closed in RAM).
        /// Positive leg confirms through the crash-resistant install.
        /// </summary>
        private static void COUNTER_NoArchiveWithoutConfirmedMarker()
        {
            Check.That(!TxCounterFile.ConfirmBlockedMarker(null, "x"), "null path: gate refuses (no archive)");
            Check.That(!TxCounterFile.ConfirmBlockedMarker(string.Empty, "x"), "empty path: gate refuses (no archive)");
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "BestAutoSort_TxCounter.txt");
                File.WriteAllText(path, "garbage-primary");
                File.WriteAllText(TxCounterFile.BackupPathFor(path), "garbage-backup");
                Check.That(TxCounterFile.ConfirmBlockedMarker(path, "counter copies untrustworthy (primary AND backup)"),
                    "gate confirms via the crash-resistant install");
                Check.That(TxCounterFile.BlockedMarkerPresent(path), "marker present after gate confirm");
                Check.That(File.Exists(path) && File.Exists(TxCounterFile.BackupPathFor(path)),
                    "gate moves nothing by itself (production archives only after confirm)");
            }
            finally
            {
                DelDir(dir);
            }
        }

        /// <summary>
        /// Restart legs with RAM discarded: corrupt copies + marker (leg 1
        /// FailClosed), archive aside (leg 2 still FailClosed via the marker —
        /// without it the same state would go Fresh), repair + restart (leg 3
        /// ResumedPrimary). Every leg re-reads from disk into fresh locals.
        /// </summary>
        private static void COUNTER_RestartLegsDiscardRamKeepMarker()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "BestAutoSort_TxCounter.txt");
                string backup = TxCounterFile.BackupPathFor(path);
                File.WriteAllText(path, "garbage-primary");
                File.WriteAllText(backup, "garbage-backup");
                Check.That(TxCounterFile.WriteBlockedMarkerCrashResistant(path, "test: both copies corrupt"), "marker installs");

                // Leg 1 (fresh locals, re-read from disk): evidence + marker => FailClosed.
                {
                    bool primaryExists = File.Exists(path);
                    string primaryText = primaryExists ? File.ReadAllText(path) : null;
                    bool backupExists = File.Exists(backup);
                    string backupText = backupExists ? File.ReadAllText(backup) : null;
                    bool blocked = TxCounterFile.BlockedMarkerPresent(path);
                    uint ceil;
                    bool restore;
                    Check.That(TxCounterFile.ClassifyLoad(primaryExists, primaryText, backupExists, backupText, blocked, out ceil, out restore)
                        == TxCounterFile.CounterLoadState.FailClosed, "leg 1: corrupt copies + marker => FailClosed");
                }

                // Archive step (production order: marker FIRST — already confirmed — then moves).
                File.Move(path, path + ".corrupt-leg");
                File.Move(backup, backup + ".corrupt-leg");

                // Leg 2 (fresh locals again: RAM discarded): no files, marker => still FailClosed.
                {
                    bool primaryExists = File.Exists(path);
                    bool backupExists = File.Exists(backup);
                    bool blocked = TxCounterFile.BlockedMarkerPresent(path);
                    uint ceil;
                    bool restore;
                    Check.That(TxCounterFile.ClassifyLoad(primaryExists, null, backupExists, null, blocked, out ceil, out restore)
                        == TxCounterFile.CounterLoadState.FailClosed, "leg 2: restart after archive stays FailClosed via the marker");
                    Check.That(TxCounterFile.ClassifyLoad(false, null, false, null, false, out ceil, out restore)
                        == TxCounterFile.CounterLoadState.Fresh, "control: without the marker the same state would go Fresh");
                }

                // Repair (human): trustworthy file restored, marker deleted. Leg 3 resumes.
                string good = TxCounterFile.Format(8192u);
                File.WriteAllText(path, good);
                File.Delete(TxCounterFile.BlockedPathFor(path));
                {
                    bool primaryExists = File.Exists(path);
                    string primaryText = primaryExists ? File.ReadAllText(path) : null;
                    bool blocked = TxCounterFile.BlockedMarkerPresent(path);
                    uint ceil;
                    bool restore;
                    Check.That(TxCounterFile.ClassifyLoad(primaryExists, primaryText, false, null, blocked, out ceil, out restore)
                        == TxCounterFile.CounterLoadState.ResumedPrimary && ceil == 8192u,
                        "leg 3: restart after repair resumes above the restored ceil");
                }
            }
            finally
            {
                DelDir(dir);
            }
        }

        /// <summary>
        /// Repair procedure unchanged: the marker wins over valid files (no
        /// auto-heal, no auto-delete, classification moves nothing), bodies stay
        /// diagnostically readable, and deleting the marker with a trustworthy
        /// file present resumes normally.
        /// </summary>
        private static void COUNTER_RepairProcedureUnchangedMarkerWins()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "BestAutoSort_TxCounter.txt");
                string backup = TxCounterFile.BackupPathFor(path);
                File.WriteAllText(path, TxCounterFile.Format(8192u));
                File.WriteAllText(backup, TxCounterFile.Format(4096u));
                Check.That(TxCounterFile.WriteBlockedMarkerCrashResistant(path, "test"), "marker installs");
                uint ceil;
                bool restore;
                Check.That(TxCounterFile.ClassifyLoad(true, File.ReadAllText(path), true, File.ReadAllText(backup), true, out ceil, out restore)
                    == TxCounterFile.CounterLoadState.FailClosed, "marker wins over valid files (repair step pending)");
                Check.That(File.Exists(path) && File.Exists(backup), "classification moves nothing by itself");
                uint resolved;
                Check.That(TxCounterFile.ResolveLoad(File.ReadAllText(path), File.ReadAllText(backup), out resolved, out restore)
                    && resolved == 8192u, "file bodies stay diagnostically readable for the human repair");
                File.Delete(TxCounterFile.BlockedPathFor(path));
                Check.That(TxCounterFile.ClassifyLoad(File.Exists(path), File.ReadAllText(path), File.Exists(backup), File.ReadAllText(backup),
                    TxCounterFile.BlockedMarkerPresent(path), out ceil, out restore)
                    == TxCounterFile.CounterLoadState.ResumedPrimary && ceil == 8192u,
                    "marker deletion with a trustworthy file resumes (repair procedure unchanged)");
            }
            finally
            {
                DelDir(dir);
            }
        }
    }
}
