using System;
using System.IO;
using System.Text;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Durable txId-counter reservation file: pure format + atomic-write helpers
    /// shared by production (ChestTxService.Persist/LoadReservedCounter) and the
    /// offline suite. No Unity/Valheim refs.
    ///
    /// Why this exists: the counter reservation is crash defense-in-depth (ZNet
    /// UIDs are ephemeral per process, so a reservation normally belongs to a
    /// retired UID; it matters only in the rare UID-reuse-with-reset-counter
    /// case). The execution floor is the real guard — a same-UID reset counter
    /// hits the persisted high-water and answers Indeterminate, never
    /// double-applies. This file only narrows that window; it is NOT an
    /// ACK/journal and gives NO end-to-end exactly-once (at-most-once submit /
    /// exactly-once callback attempt; committed-but-Indeterminate and
    /// client-crash windows stay residual).
    ///
    /// Format: versioned "v1:&lt;ceil&gt;:&lt;fnv1a32-hex&gt;" where ceil is the
    /// exclusive reservation upper bound and the checksum is FNV-1a over the
    /// ASCII "BestAutoSortTxCounter:" + ceil. Legacy plain-integer files
    /// ("4096") still parse. Anything else (garbage, truncation/torn write,
    /// checksum mismatch, zero) does NOT parse: the caller falls back to the
    /// backup copy, and only when BOTH copies are untrustworthy archives the
    /// evidence aside and resumes at 1 LOUDLY (warned, never silent). Ceil
    /// values 0/1 are parsed but never usable (resume needs ceil &gt; 1).
    ///
    /// Reservation rule (TryReserve): mirrors the production block reservation.
    /// Refuses fail-closed at 0/uint.MaxValue and when the next block would
    /// reach MaxValue, so the counter can never wrap silently (TxIdGen.TryNext
    /// also refuses at the boundary).
    /// </summary>
    public static class TxCounterFile
    {
        private const string Prefix = "v1:";
        private const string CheckScope = "BestAutoSortTxCounter:";
        private const string BackupSuffix = ".bak";
        private const string TmpSuffix = ".tmp";

        /// <summary>Formats a reservation ceil in the versioned format.</summary>
        public static string Format(uint ceil)
        {
            return Prefix + ceil.ToString() + ":" + CheckHex(ceil);
        }

        /// <summary>
        /// Parses a counter file body: versioned v1 (checksum-checked) or legacy
        /// plain integer. False = untrustworthy (null/empty/garbage/truncated/
        /// checksum mismatch/non-numeric). A parsed ceil of 0/1 is NOT usable as
        /// a resume point (see ResolveLoad).
        /// </summary>
        public static bool TryParse(string text, out uint ceil)
        {
            ceil = 0;
            if (string.IsNullOrEmpty(text))
                return false;
            string t = text.Trim();
            if (t.StartsWith(Prefix, StringComparison.Ordinal))
            {
                string[] parts = t.Split(':');
                if (parts.Length != 3)
                    return false;
                uint v;
                if (!TryParseUint(parts[1], out v))
                    return false;
                if (!string.Equals(parts[2], CheckHex(v), StringComparison.OrdinalIgnoreCase))
                    return false;
                ceil = v;
                return true;
            }
            return TryParseUint(t, out ceil);
        }

        /// <summary>
        /// Pure block-reservation rule. True = the caller may use `current` and
        /// persist newCeil as the new reservation ceil (newCeil == reservedCeil
        /// when still covered). False = refuse new txIds (fail closed): counter
        /// unreserved (0), exhausted (MaxValue), or the next block would reach
        /// MaxValue (wrap can never go silent).
        /// </summary>
        public static bool TryReserve(uint current, uint reservedCeil, uint block, out uint newCeil)
        {
            newCeil = reservedCeil;
            if (block == 0)
                return false;
            if (current == 0 || current == uint.MaxValue)
                return false;
            if (current < reservedCeil)
                return true;
            ulong want = (ulong)current + block;
            if (want >= (ulong)uint.MaxValue)
                return false;
            newCeil = (uint)want;
            return true;
        }

        /// <summary>
        /// Backup-aware load decision on already-read file bodies (pure,
        /// testable). True + ceil (&gt; 1) = resume there; restoreBackup tells
        /// the caller to rewrite the primary from the backup. False = NEITHER
        /// copy is trustworthy: the caller must archive the evidence aside and
        /// resume at 1 loudly (warned, never silent) — never invent a high
        /// counter (that could skip fencing) and never roll back silently.
        /// </summary>
        public static bool ResolveLoad(string primaryText, string backupText, out uint ceil, out bool restoreBackup)
        {
            ceil = 0;
            restoreBackup = false;
            uint p;
            if (TryParse(primaryText, out p) && p > 1)
            {
                ceil = p;
                return true;
            }
            uint b;
            if (TryParse(backupText, out b) && b > 1)
            {
                ceil = b;
                restoreBackup = true;
                return true;
            }
            return false;
        }

        /// <summary>Backup path for a counter file path (same directory).</summary>
        public static string BackupPathFor(string path)
        {
            return path + BackupSuffix;
        }

        /// <summary>
        /// Crash-atomic persist: temp file in the SAME directory + OS flush +
        /// Replace (backup copy kept), with a delete+move fallback where
        /// Replace is unavailable. Never throws; false = persist NOTHING (the
        /// caller must refuse new txIds fail-closed). A torn write can only
        /// leave a truncated temp file (never a half primary): the next load
        /// fails checksum and falls back to the backup.
        /// </summary>
        public static bool WriteAtomically(string path, uint ceil)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    return false;
                string text = Format(ceil);
                string tmp = path + TmpSuffix;
                string bak = BackupPathFor(path);
                byte[] bytes = Encoding.ASCII.GetBytes(text);
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                try
                {
                    if (File.Exists(path))
                        File.Replace(tmp, path, bak);
                    else
                        File.Move(tmp, path);
                    return true;
                }
                catch
                {
                    // Replace unavailable (some Unix/Mono stacks): keep a manual
                    // backup, then delete+move. Same-directory move is atomic
                    // on one filesystem; the backup covers the gap.
                    try
                    {
                        if (File.Exists(path))
                            File.Copy(path, bak, true);
                    }
                    catch
                    {
                    }
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch
                    {
                        return false;
                    }
                    File.Move(tmp, path);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseUint(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
                return false;
            text = text.Trim();
            if (text.Length == 0 || text.Length > 10)
                return false;
            uint v = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                    return false;
                uint d = (uint)(c - '0');
                if (v > (uint.MaxValue - d) / 10)
                    return false;
                v = v * 10 + d;
            }
            value = v;
            return true;
        }

        private static string CheckHex(uint ceil)
        {
            string s = CheckScope + ceil.ToString();
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= (byte)s[i];
                h *= 16777619u;
            }
            return h.ToString("x8");
        }
    }
}
