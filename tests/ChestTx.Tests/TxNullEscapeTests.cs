using System;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Dead-source + live-quiescence escape policy group (v0.5.x empty-chest
    /// fix). Pins the PRODUCTION seams TxNullEscape.ShouldEscape (dead-source
    /// leg) and TxNullEscape.ShouldEscapeLiveQuiescent (live-owner leg) —
    /// the exact predicates the Valheim-side null-quarantine paths (Takeover
    /// / slow-pump TryReloadAuthoritative / structural acquire) consult.
    /// Honest scope (same caveat as TxMustFixTests): the Unity call-site
    /// wiring (peer-liveness scan, RAM-empty proof, self-heal save + verify,
    /// quarantine clear, Add/totals conservation) cannot run in this
    /// Unity-free harness, so these tests pin the decision tables production
    /// MUST consult, not the end-to-end heal. The heal-side properties
    /// (non-empty RAM refuses; empty RAM + verified blob clears quarantine;
    /// Add works and totals conserve after the clear) are enforced by the
    /// NEW gates added in this diff (GetAllItems().Count == 0 refusal +
    /// fresh-read + validated TryLoad verify before any clear), retained by
    /// inspection — this file pins that NEITHER leg fires early and that the
    /// dead-source 30 s leg is byte-for-byte unchanged:
    /// - bounded wait: quarantine + pump retries stay fail-closed before the
    ///   grace, even with the old owner gone (req 1).
    /// - dead-source escape: quarantined + null + old owner gone + grace
    ///   elapsed => true (req 2; production then demands provably empty RAM
    ///   and a verified self-heal save before clearing the quarantine).
    /// - dead-source leg never fires for a live owner, however long the wait
    ///   (req 3a — the short grace NEVER escapes a live owner).
    /// - live-owner quiescence escape: quarantined + null + old owner LIVE +
    ///   EXTENDED quiescence window elapsed => true (req 3b; production then
    ///   demands the SAME provably-empty-RAM + verified-save gates plus the
    ///   loud residual-risk log line). Inside the window a live owner stays
    ///   quarantined.
    /// - never on a non-quarantine path; never for present bytes — including
    ///   present-but-empty blobs, which take the normal validated reload, not
    ///   either escape (req 4).
    /// - stuck-pump Warn dampening: first retry logs, then every Nth (req 5).
    /// </summary>
    internal static class TxNullEscapeTests
    {
        public static void RunAll()
        {
            Console.WriteLine("NULLESCAPE_BoundedWaitStaysQuarantined");
            NULLESCAPE_BoundedWaitStaysQuarantined();
            Console.WriteLine("NULLESCAPE_DeadSourceEscapesAfterGrace");
            NULLESCAPE_DeadSourceEscapesAfterGrace();
            Console.WriteLine("NULLESCAPE_LiveOwnerNeverEscapesDeadLeg");
            NULLESCAPE_LiveOwnerNeverEscapesDeadLeg();
            Console.WriteLine("NULLESCAPE_LiveQuiescenceEscapesAfterWindow");
            NULLESCAPE_LiveQuiescenceEscapesAfterWindow();
            Console.WriteLine("NULLESCAPE_LiveQuiescenceInsideWindowStaysQuarantined");
            NULLESCAPE_LiveQuiescenceInsideWindowStaysQuarantined();
            Console.WriteLine("NULLESCAPE_QuiescenceWindowBounds");
            NULLESCAPE_QuiescenceWindowBounds();
            Console.WriteLine("NULLESCAPE_NonQuarantinePathNeverEscapes");
            NULLESCAPE_NonQuarantinePathNeverEscapes();
            Console.WriteLine("NULLESCAPE_PresentBytesNeverEscape");
            NULLESCAPE_PresentBytesNeverEscape();
            Console.WriteLine("NULLESCAPE_WarnDampening");
            NULLESCAPE_WarnDampening();
        }

        /// <summary>
        /// Req 1 (bounded wait): quarantine + null + dead source stays
        /// fail-closed for the whole grace window — pump retries keep the
        /// quarantine, elapsed time alone escapes nothing.
        /// </summary>
        private static void NULLESCAPE_BoundedWaitStaysQuarantined()
        {
            Check.That(TxNullEscape.GraceSeconds >= 30.0,
                "grace is a real bounded wait (>= 30 s, far above the 0.5 s pump and 15 s presence timeouts), got " + TxNullEscape.GraceSeconds);
            Check.That(!TxNullEscape.ShouldEscape(true, true, false, 0.0),
                "t=0 with dead source: no escape (fail-closed)");
            Check.That(!TxNullEscape.ShouldEscape(true, true, false, TxNullEscape.GraceSeconds - 0.001),
                "just under the grace: no escape");
            Check.That(!TxNullEscape.ShouldEscape(true, true, false, -5.0),
                "negative elapsed (clock skew): no escape");
            Check.That(!TxNullEscape.ShouldEscape(true, true, false, double.NaN),
                "NaN elapsed: no escape");
            Check.That(!TxNullEscape.ShouldEscape(true, true, false, double.PositiveInfinity),
                "dead leg with Infinite elapsed: no escape (IsInfinity guard pinned)");
            Check.That(!TxNullEscape.ShouldEscape(true, true, false, double.NegativeInfinity),
                "dead leg with NegativeInfinity elapsed: no escape");
            Check.That(TxNullEscape.ShouldEscape(true, true, false, double.MaxValue),
                "dead leg at max finite elapsed: escape fires (only NaN/Infinity rejected)");
        }

        /// <summary>
        /// Req 2 (dead-source escape): quarantined + still null + old owner
        /// provably gone + grace elapsed => true. Boundary is inclusive: the
        /// wait is bounded, not infinite.
        /// </summary>
        private static void NULLESCAPE_DeadSourceEscapesAfterGrace()
        {
            Check.That(TxNullEscape.ShouldEscape(true, true, false, TxNullEscape.GraceSeconds),
                "exactly at the grace: escape fires (bounded, not infinite)");
            Check.That(TxNullEscape.ShouldEscape(true, true, false, TxNullEscape.GraceSeconds + 60.0),
                "past the grace with dead source: escape fires");
            Check.That(TxNullEscape.ShouldEscape(true, true, false, 3600.0),
                " hour-long null with dead source: escape fires (the stuck-new-chest case)");
        }

        /// <summary>
        /// Req 3a (dead-source leg never fires for a live owner): the short
        /// grace NEVER escapes a live owner — ShouldEscape answers false for
        /// oldOwnerLive=true at ANY elapsed time, including past the extended
        /// quiescence window. The live-owner exit lives ONLY in
        /// ShouldEscapeLiveQuiescent (req 3b below). Unchanged by the
        /// quiescence extension.
        /// </summary>
        private static void NULLESCAPE_LiveOwnerNeverEscapesDeadLeg()
        {
            Check.That(!TxNullEscape.ShouldEscape(true, true, true, TxNullEscape.GraceSeconds),
                "live owner at the grace: dead-source leg fires nothing");
            Check.That(!TxNullEscape.ShouldEscape(true, true, true, TxNullEscape.QuiescenceSeconds),
                "live owner at the quiescence boundary: dead-source leg STILL fires nothing (only the live leg may)");
            Check.That(!TxNullEscape.ShouldEscape(true, true, true, 86400.0),
                "live owner after a full day of null: dead-source leg still fires nothing");
        }

        /// <summary>
        /// Req 3b (live-owner quiescence escape): quarantined + still null +
        /// old owner LIVE + extended quiescence window elapsed => true.
        /// Boundary is inclusive: the window is bounded, not infinite.
        /// Production then demands the SAME heal gates as the dead-source leg
        /// (provably empty RAM, self-heal save + fresh-read + validated Load
        /// verify before clearing) plus the loud residual-risk log line; those
        /// Unity-side gates cannot run in this harness and are retained by
        /// inspection in TryHealNullSItems(liveQuiescence: true).
        /// </summary>
        private static void NULLESCAPE_LiveQuiescenceEscapesAfterWindow()
        {
            Check.That(TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, TxNullEscape.QuiescenceSeconds),
                "live owner exactly at the quiescence window: escape fires (bounded, not infinite)");
            Check.That(TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, TxNullEscape.QuiescenceSeconds + 60.0),
                "live owner past the window: escape fires");
            Check.That(TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, 3600.0),
                "hour-long null with live owner: escape fires (the stuck-quarantine deadlock case)");
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(true, true, false, TxNullEscape.QuiescenceSeconds + 3600.0),
                "dead source never takes the LIVE leg (legs are exclusive — dead sources escape via ShouldEscape)");
        }

        /// <summary>
        /// Req 3b (inside the window): a live owner stays quarantined for the
        /// WHOLE extended window — the 30 s grace, and everything short of
        /// the quiescence boundary, escapes nothing for a live source.
        /// </summary>
        private static void NULLESCAPE_LiveQuiescenceInsideWindowStaysQuarantined()
        {
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, 0.0),
                "live owner at t=0: no escape (fail-closed)");
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, TxNullEscape.GraceSeconds),
                "live owner at the short grace: NO escape (the single 30 s grace never escapes a live owner)");
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, TxNullEscape.GraceSeconds + 60.0),
                "live owner past the short grace but inside the window: no escape");
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, TxNullEscape.QuiescenceSeconds - 0.001),
                "live owner just under the window: no escape");
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, -5.0),
                "live owner with negative elapsed (clock skew): no escape");
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, double.NaN),
                "live owner with NaN elapsed: no escape");
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, double.PositiveInfinity),
                "live owner with Infinite elapsed: no escape");
        }

        /// <summary>
        /// Window-shape pins: the quiescence window sits in the 120–180 s
        /// band (total null persistence), strictly above the dead-source
        /// grace (so the 30 s leg is unchanged and never escapes a live
        /// owner), and the dead-source grace itself is still exactly 30 s.
        /// </summary>
        private static void NULLESCAPE_QuiescenceWindowBounds()
        {
            Check.That(TxNullEscape.GraceSeconds == 30.0,
                "dead-source grace unchanged at exactly 30 s, got " + TxNullEscape.GraceSeconds);
            Check.That(TxNullEscape.QuiescenceSeconds >= 120.0 && TxNullEscape.QuiescenceSeconds <= 180.0,
                "quiescence window in the 120-180 s band, got " + TxNullEscape.QuiescenceSeconds);
            Check.That(TxNullEscape.QuiescenceSeconds > TxNullEscape.GraceSeconds,
                "quiescence window strictly above the dead-source grace");
        }

        /// <summary>
        /// Req 4a: never treat null as empty on a NON-quarantine path — the
        /// gate answers false whenever the chest is not already fail-closed,
        /// even with null bytes, a gone owner, and an eternity elapsed.
        /// </summary>
        private static void NULLESCAPE_NonQuarantinePathNeverEscapes()
        {
            Check.That(!TxNullEscape.ShouldEscape(false, true, false, TxNullEscape.GraceSeconds + 3600.0),
                "not quarantined + null + dead source + long wait: no escape (non-quarantine paths never heal)");
            Check.That(!TxNullEscape.ShouldEscape(false, true, false, double.MaxValue),
                "not quarantined at max elapsed: no escape");
        }

        /// <summary>
        /// Req 4b: present bytes never go through EITHER escape — including a
        /// present-but-empty (v109/0) blob, which the normal validated reload
        /// handles with decoded-bytes proof. Both escapes are null-only, and
        /// the live leg additionally requires quarantine (never on a
        /// non-quarantine path).
        /// </summary>
        private static void NULLESCAPE_PresentBytesNeverEscape()
        {
            Check.That(!TxNullEscape.ShouldEscape(true, false, false, TxNullEscape.GraceSeconds + 3600.0),
                "quarantined + PRESENT bytes + dead source + long wait: no escape (validated reload owns this case)");
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(true, false, true, TxNullEscape.QuiescenceSeconds + 3600.0),
                "quarantined + PRESENT bytes + live owner + long wait: live leg fires nothing (validated reload owns this case)");
            Check.That(!TxNullEscape.ShouldEscapeLiveQuiescent(false, true, true, TxNullEscape.QuiescenceSeconds + 3600.0),
                "NOT quarantined + null + live owner + long wait: live leg fires nothing (non-quarantine paths never heal)");
            Check.That(TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, double.MaxValue),
                "live leg at max finite elapsed: fires (only NaN/Infinity are rejected — same parity as the dead leg)");
            string reason;
            byte[] emptyBlob = new byte[] { 109, 0, 0, 0, 0, 0 };
            Check.That(TxSItemsGuard.TryValidate(emptyBlob, out reason),
                "empty-shaped v109/0 blob validates (present-empty is proof, not escape), got: " + reason);
            Check.That(!TxSItemsGuard.TryValidate(null, out reason),
                "null still fails the gate (never empty): " + reason);
        }

        /// <summary>
        /// Req 5 (log spam): the stuck-pump Warn fires on the first retry
        /// (same immediate signal as before) then every WarnEveryNth retry —
        /// ~6 s at the 0.5 s slow-pump cadence — instead of every pump.
        /// </summary>
        private static void NULLESCAPE_WarnDampening()
        {
            Check.That(TxNullEscape.WarnEveryNth == 12,
                "Warn cadence 12 x 0.5 s = ~6 s per stuck chest, got " + TxNullEscape.WarnEveryNth);
            Check.That(TxNullEscape.ShouldWarnQuarantine(0), "first retry (0 prior) always warns");
            Check.That(TxNullEscape.ShouldWarnQuarantine(-1), "negative count warns (fail-open logging)");
            for (int i = 1; i < TxNullEscape.WarnEveryNth; i++)
                Check.That(!TxNullEscape.ShouldWarnQuarantine(i), "retry " + i + " suppressed");
            Check.That(TxNullEscape.ShouldWarnQuarantine(TxNullEscape.WarnEveryNth), "retry N warns");
            Check.That(TxNullEscape.ShouldWarnQuarantine(2 * TxNullEscape.WarnEveryNth), "retry 2N warns");
            Check.That(!TxNullEscape.ShouldWarnQuarantine(2 * TxNullEscape.WarnEveryNth + 1), "retry 2N+1 suppressed");
        }
    }
}
