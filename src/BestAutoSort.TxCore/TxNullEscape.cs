using System;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Dead-source escape policy for the manager-side null-s_items quarantine
    /// (v0.5.x empty-chest fix). Pure: no Unity/Valheim refs, so the offline
    /// suite pins the SAME predicate the game calls.
    ///
    /// Problem: a brand-new (or genuinely empty) chest whose s_items blob never
    /// replicated stays null forever on the new manager. The fail-closed rule
    /// (null is unavailable, never empty) quarantines it — correctly while the
    /// old owner may still deliver the bytes, but permanently when the old
    /// owner is gone and there is nothing to deliver, AND permanently when a
    /// live owner (or the server relay) never delivers either.
    ///
    /// Escape rules (TWO legs; anything else stays quarantined):
    /// Leg 1 — dead source (ShouldEscape, ALL four must hold):
    /// - quarantined: the chest is ALREADY fail-closed on a manager path.
    ///   Never consulted on a non-quarantine path — null is never treated as
    ///   empty anywhere else, and no destructive Load skips decoded-bytes proof.
    /// - sItemsNull: the current authoritative read saw null bytes (not merely
    ///   invalid — invalid bytes never heal; a present blob, even empty-shaped,
    ///   goes through the normal validated reload, never through here).
    /// - old owner provably NOT live (no peer entry, not the server peer, not
    ///   self, not an owning live player): replication can no longer arrive.
    /// - bounded wait elapsed since the quarantine started (GraceSeconds):
    ///   transient replication delay never triggers a heal.
    /// Leg 2 — live-owner quiescence (ShouldEscapeLiveQuiescent, ALL four must
    ///   hold): quarantined + still null + old owner still LIVE + the EXTENDED
    ///   quiescence window (QuiescenceSeconds of TOTAL null persistence)
    ///   elapsed. The single 30 s grace NEVER escapes a live owner.
    ///
    /// Why a bounded live-owner escape exists (documented here because the
    /// decision is otherwise surprising): only the ZDO owner runs vanilla
    /// Container.Save, so an ex-owner that lost ownership cannot produce
    /// s_items anymore; whatever the new manager sees must arrive via the
    /// server relay, which settles in seconds — not minutes. Meanwhile our
    /// OWN rev-bumping writes (WriteRing/WriteFloor reseeds, SaveContainer)
    /// each bump DataRevision and can stale-drop older server state, so
    /// waiting indefinitely for a live source that never delivers cannot
    /// converge: the quarantine would deadlock an empty chest forever (no
    /// Add, no totals). A bounded quiescence window is the only non-deadlock
    /// option. Residual risk stays LOUD: the server may silently hold
    /// unreplicated items, so the production heal logs an explicit
    /// residual-risk line for manual reconcile.
    ///
    /// The production heal behind EITHER gate additionally requires provably
    /// empty live RAM (never cement empty over real contents) and verifies the
    /// self-heal save by re-reading + validated-loading the fresh blob before
    /// clearing the quarantine. ShouldWarnQuarantine dampens the stuck-pump
    /// Warn to first + every WarnEveryNth retry (12 x 0.5 s = ~6 s cadence).
    /// </summary>
    public static class TxNullEscape
    {
        /// <summary>
        /// Bounded wait before a dead-source heal may fire, in seconds. Far
        /// above the 0.5 s slow-pump cadence and the ~15 s presence timeout so
        /// transient replication/migration delay can never trigger it.
        /// </summary>
        public const double GraceSeconds = 30.0;

        /// <summary>
        /// Extended quiescence window for the LIVE-owner escape leg, in
        /// seconds of TOTAL null persistence since the quarantine started.
        /// Must stay far above GraceSeconds (so the dead-source leg is
        /// unaffected and a live owner never escapes on the short grace)
        /// and far above the ~15 s presence timeout and any server-relay
        /// settle time (seconds, not minutes). 150 s = 2.5 min: bounded
        /// (never infinite deadlock) yet conservative (never fires on a
        /// transient replication/migration stall).
        /// </summary>
        public const double QuiescenceSeconds = 150.0;

        /// <summary>
        /// Stuck-quarantine pump Warn cadence: first retry always logs (same
        /// immediate signal as before), then every Nth retry (~6 s at 0.5 s).
        /// </summary>
        public const int WarnEveryNth = 12;

        /// <summary>
        /// Dead-source escape gate. True ONLY when quarantined AND the fresh
        /// read saw null s_items AND the old owner is provably not live AND
        /// the bounded wait has elapsed. Never throws.
        /// </summary>
        public static bool ShouldEscape(bool quarantined, bool sItemsNull, bool oldOwnerLive, double elapsedSeconds)
        {
            if (!quarantined)
                return false;
            if (!sItemsNull)
                return false;
            if (oldOwnerLive)
                return false;
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
                return false;
            return elapsedSeconds >= GraceSeconds;
        }

        /// <summary>
        /// Live-owner quiescence escape gate. True ONLY when quarantined AND
        /// the fresh read saw null s_items AND the old owner is still live
        /// AND the extended quiescence window has elapsed. This is the ONLY
        /// leg on which a live owner may escape; ShouldEscape (dead-source
        /// leg) still answers false for a live owner at ANY elapsed time.
        /// Never throws.
        /// </summary>
        public static bool ShouldEscapeLiveQuiescent(bool quarantined, bool sItemsNull, bool oldOwnerLive, double elapsedSeconds)
        {
            if (!quarantined)
                return false;
            if (!sItemsNull)
                return false;
            if (!oldOwnerLive)
                return false;
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
                return false;
            return elapsedSeconds >= QuiescenceSeconds;
        }

        /// <summary>
        /// Stuck-quarantine Warn gate: log the first retry and every Nth after
        /// (priorRetries = number of quarantined pump retries already seen for
        /// this quarantine episode). Never throws.
        /// </summary>
        public static bool ShouldWarnQuarantine(int priorRetries)
        {
            if (priorRetries <= 0)
                return true;
            return (priorRetries % WarnEveryNth) == 0;
        }
    }
}
