using System;
using System.Linq;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// MUST-fix regression group (v0.5.x): pins the production TxCore seam
    /// predicates the Valheim-side paths call. Honest scope: helper predicates
    /// pinned; call-site wiring in ChestTxService(.Client).cs is assumed — it
    /// cannot run in this Unity-free harness, so no test here fails if a
    /// production call site is reverted (see docs C14, same caveat).
    /// - null-gate (fix 2): null s_items is unavailable, never empty; every
    ///   destructive op requires decoded-bytes proof (TryValidate).
    /// - blob-validation fallback (fix 3): length/version pre-check rejects
    ///   garbage BEFORE any Inventory.Load can tear RAM.
    /// - copy discipline via shared helper (fix 4): CloneBytes is the sole copy
    ///   primitive; baselines never alias the channel buffer.
    /// - handoff order/race (fix 5): stale grants never apply (strictly-newer
    ///   revision rule shared with PumpViewerRefresh).
    /// ForceSend-as-hint (fix 1) is comment/log-only (no behavior seam to pin).
    /// </summary>
    internal static class TxMustFixTests
    {
        // Test-local null-aware equality for assertions only (not production
        // logic: production never compares baselines for equality, it only
        // stores clones and compares lengths via SameBytes at call sites).
        private static bool NullSeqEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null)
                return a == b;
            return a.SequenceEqual(b);
        }

        private static byte[] Blob(int version, int extraBytes)
        {
            // Synthetic provenance: hand-built LE version + zero count, matching
            // the guard's own LE decode (not bytes captured from Inventory.Save).
            // Asserts pin the guard contract, not the game serializer.
            byte[] b = new byte[6 + extraBytes];
            b[0] = (byte)(version & 0xFF);
            b[1] = (byte)((version >> 8) & 0xFF);
            b[2] = (byte)((version >> 16) & 0xFF);
            b[3] = (byte)((version >> 24) & 0xFF);
            b[4] = 0;
            b[5] = 0;
            return b;
        }

        public static void RunAll()
        {
            Console.WriteLine("MUST2_NullIsNotEmpty");
            MUST2_NullIsNotEmpty();
            Console.WriteLine("MUST3_BlobValidationFallback");
            MUST3_BlobValidationFallback();
            Console.WriteLine("MUST4_CopyDisciplineViaSharedHelper");
            MUST4_CopyDisciplineViaSharedHelper();
            Console.WriteLine("MUST5_HandoffOrderRace");
            MUST5_HandoffOrderRace();
        }

        /// <summary>
        /// Null-gate: null bytes fail validation (unavailable, never empty), so
        /// no destructive op may treat them as an empty inventory. Empty array
        /// is likewise not a valid inventory (below minimum, never presumed).
        /// </summary>
        private static void MUST2_NullIsNotEmpty()
        {
            string reason;
            Check.That(!TxSItemsGuard.TryValidate(null, out reason), "null s_items fails the gate (never empty)");
            Check.That(reason != null && reason.Contains("null"), "null reason says null, got: " + reason);
            Check.That(!TxSItemsGuard.TryValidate(new byte[0], out reason), "zero-length is not an inventory");
            Check.That(!TxSItemsGuard.TryValidate(new byte[5], out reason), "short (< 6) is not an inventory");
            // Null clones to null: unavailability propagates, never synthesizes empty.
            Check.That(TxSItemsGuard.CloneBytes(null) == null, "clone(null) is null (no fabricated empty)");
        }

        /// <summary>
        /// Blob-validation fallback: only sanely-sized, plausibly-versioned blobs
        /// pass; everything else is refused BEFORE Load (RAM untouched, caller
        /// keeps quarantine / keeps last-good). Version window is generous so a
        /// future game version degrades to quarantine, never to a hard brick —
        /// while 0/negative/absurd is never real.
        /// </summary>
        private static void MUST3_BlobValidationFallback()
        {
            string reason;
            Check.That(TxSItemsGuard.TryValidate(Blob(109, 0), out reason), "empty-shaped v109 blob passes: " + reason);
            Check.That(TxSItemsGuard.TryValidate(Blob(1, 100), out reason), "min version passes");
            Check.That(TxSItemsGuard.TryValidate(Blob(10000, 100), out reason), "max version passes");
            Check.That(!TxSItemsGuard.TryValidate(Blob(0, 100), out reason), "version 0 refused");
            Check.That(!TxSItemsGuard.TryValidate(Blob(-1, 100), out reason), "negative version refused");
            Check.That(!TxSItemsGuard.TryValidate(Blob(10001, 100), out reason), "absurd version refused");
            // One-time ~16MB transient to exercise the length bound; freed
            // immediately after the assert (no perf guard needed for one alloc).
            Check.That(!TxSItemsGuard.TryValidate(new byte[TxSItemsGuard.MaxSItemsBytes + 1], out reason),
                "oversize blob refused");
        }

        /// <summary>
        /// Copy discipline: the helper clone is content-equal but reference- and
        /// mutation-independent (the SameBytes-baseline pattern: store the clone,
        /// mutate the working buffer, baseline unchanged). Null-aware equality:
        /// null == null, null != empty (unavailable is never empty).
        /// </summary>
        private static void MUST4_CopyDisciplineViaSharedHelper()
        {
            byte[] channel = Blob(109, 4);
            channel[6] = 0xAB;
            byte[] baseline = TxSItemsGuard.CloneBytes(channel);
            Check.That(channel.SequenceEqual(baseline), "clone is content-equal");
            Check.That(!object.ReferenceEquals(channel, baseline), "clone is not the same reference");
            channel[6] = 0x00; // later in-place mutation of the working buffer
            Check.That(baseline[6] == 0xAB, "baseline survives working-buffer mutation (aliasing broken)");
            Check.That(!channel.SequenceEqual(baseline), "mutation is detected, not masked");
            Check.That(new byte[0].SequenceEqual(new byte[0]), "empty == empty");
            Check.That(!NullSeqEqual(null, new byte[0]), "null != empty");
            Check.That(!NullSeqEqual(new byte[0], null), "empty != null");
            Check.That(!NullSeqEqual(new byte[] { 1 }, new byte[] { 1, 2 }), "length mismatch != equal");
        }

        /// <summary>
        /// Handoff order/race: a grant is never state proof — content applies
        /// only when provably newer. First poll applies; equal/older revisions
        /// (reorder, duplicate push, pre-push render) never roll the view back.
        /// This is the rule PumpViewerRefresh enforces through the shared seam.
        /// </summary>
        private static void MUST5_HandoffOrderRace()
        {
            Check.That(TxHandoffGuard.ShouldApplyViewerRefresh(false, 0u, 5u), "first poll applies");
            Check.That(TxHandoffGuard.ShouldApplyViewerRefresh(false, 0u, 0u), "first poll applies even at rev 0");
            Check.That(TxHandoffGuard.ShouldApplyViewerRefresh(true, 5u, 6u), "newer rev applies");
            Check.That(!TxHandoffGuard.ShouldApplyViewerRefresh(true, 5u, 5u), "same rev skipped (duplicate push)");
            Check.That(!TxHandoffGuard.ShouldApplyViewerRefresh(true, 5u, 4u), "older rev skipped (reorder never rolls back)");
            Check.That(TxHandoffGuard.ShouldApplyViewerRefresh(true, 0u, 1u), "newer applies from zero baseline");
            Check.That(!TxHandoffGuard.ShouldApplyViewerRefresh(true, uint.MaxValue, uint.MaxValue), "max rev equal skipped");
        }
    }
}
