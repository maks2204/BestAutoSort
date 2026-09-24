using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Cascade-spin stop-gate policy group (v0.5.x quick-stack fix). Pins the
    /// PRODUCTION seams TxResponsePolicy.CascadeMadeProgress and
    /// TxResponsePolicy.ShouldStopNoProgressCascade — the exact predicates the
    /// Unity-side SubmitCascade remainder path consults before an onward submit.
    /// Honest scope: the Unity call-site wiring (accepted-list plumbing,
    /// InFlightAdds claim peek, remainder re-snapshot) cannot run in this
    /// Unity-free harness, so these tests pin the decision tables production
    /// MUST consult, not the end-to-end cascade. Call-site wiring is retained
    /// by inspection: SubmitCascade stops ONLY on ShouldStopNoProgressCascade
    /// (all-zeros accepted + every remainder item live-claimed), still cascades
    /// partial accept and chest-full-with-live-items, and keeps the
    /// Indeterminate/details-unavailable terminal guards plus session accounting.
    /// - all-pruned remainder terminates: all-zeros + all in flight => stop (no
    ///   onward submit, no _submitted++).
    /// - partial accept still cascades even when the remainder is fully claimed
    ///   (real movement happened; one bounded hop at most before the next gate).
    /// - chest-full with live items still cascades (all-zeros but NOT in flight
    ///   => the next chest may take them).
    /// - short/empty accepted lists (chunk fanout, empty-body skips) read as zero
    ///   progress, matching the remainder math (missing slot => got 0).
    /// - legacy cascade gate unchanged: Normal/FailedNotCommitted cascade,
    ///   Indeterminate/details-unavailable stay terminal.
    /// </summary>
    internal static class TxCascadeNoProgressTests
    {
        public static void RunAll()
        {
            Console.WriteLine("CASCADE_AllPrunedStops");
            CASCADE_AllPrunedStops();
            Console.WriteLine("CASCADE_PartialAcceptCascades");
            CASCADE_PartialAcceptCascades();
            Console.WriteLine("CASCADE_ChestFullLiveItemsCascade");
            CASCADE_ChestFullLiveItemsCascade();
            Console.WriteLine("CASCADE_ProgressProbe");
            CASCADE_ProgressProbe();
            Console.WriteLine("CASCADE_LegacyGateUnchanged");
            CASCADE_LegacyGateUnchanged();
        }

        // Req 1: accepted == 0 for ALL items + onward submit would carry an
        // identical item set with zero new submissions (all pruned) => STOP.
        private static void CASCADE_AllPrunedStops()
        {
            Check.That(TxResponsePolicy.ShouldStopNoProgressCascade(new List<int> { 0, 0, 0 }, 3, true),
                "all-zeros + all remainder in flight must stop the chain");
            Check.That(TxResponsePolicy.ShouldStopNoProgressCascade(new List<int>(), 2, true),
                "empty accepted (all-pruned skip body) + all in flight must stop");
            Check.That(TxResponsePolicy.ShouldStopNoProgressCascade(null, 2, true),
                "null accepted + all in flight must stop (fail toward termination, never a spin)");
            Check.That(!TxResponsePolicy.CascadeMadeProgress(new List<int> { 0, 0 }, 2),
                "all-zeros is no progress");
        }

        // Req 3: normal cascade on partial/full accept is kept — progress never
        // stops, even if every remainder item is currently claimed (the next hop
        // prunes to zero and ITS gate stops; bounded to one extra hop).
        private static void CASCADE_PartialAcceptCascades()
        {
            Check.That(!TxResponsePolicy.ShouldStopNoProgressCascade(new List<int> { 5, 0, 0 }, 3, true),
                "partial accept + all in flight must still cascade (progress wins)");
            Check.That(!TxResponsePolicy.ShouldStopNoProgressCascade(new List<int> { 0, 3 }, 2, false),
                "partial accept + live remainder must cascade");
            Check.That(!TxResponsePolicy.ShouldStopNoProgressCascade(new List<int> { 4, 4 }, 2, true),
                "full accept must never stop here (empty remainder stops earlier)");
            Check.That(TxResponsePolicy.CascadeMadeProgress(new List<int> { 0, 1 }, 2),
                "single accepted item is progress");
        }

        // Chest-full shape: all-zeros accepted but items live (unclaimed) => the
        // next chest may take them, so the chain MUST continue.
        private static void CASCADE_ChestFullLiveItemsCascade()
        {
            Check.That(!TxResponsePolicy.ShouldStopNoProgressCascade(new List<int> { 0, 0 }, 2, false),
                "all-zeros + live remainder must cascade (chest full, try next)");
            Check.That(!TxResponsePolicy.ShouldStopNoProgressCascade(new List<int> { 0 }, 1, false),
                "single live item all-zeros must cascade");
        }

        // Progress probe edge table: short lists (chunk fanout reports per chunk
        // against the full call) match the remainder math — missing slots are 0.
        private static void CASCADE_ProgressProbe()
        {
            Check.That(!TxResponsePolicy.CascadeMadeProgress(new List<int> { 0 }, 4),
                "short all-zero chunk completion is no progress for the full call");
            Check.That(TxResponsePolicy.CascadeMadeProgress(new List<int> { 0, 2 }, 4),
                "short chunk with one acceptance is progress");
            Check.That(!TxResponsePolicy.CascadeMadeProgress(null, 3),
                "null accepted is no progress");
            Check.That(!TxResponsePolicy.CascadeMadeProgress(new List<int> { 0 }, 0),
                "zero sent count is no progress");
            Check.That(!TxResponsePolicy.CascadeMadeProgress(new List<int>(), 1),
                "empty accepted is no progress");
        }

        // Req 3: the legacy Indeterminate/details-unavailable terminal guards and
        // the Normal/FailedNotCommitted cascade permission are unchanged.
        private static void CASCADE_LegacyGateUnchanged()
        {
            Check.That(TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.Normal),
                "Normal still cascades");
            Check.That(TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.FailedNotCommitted),
                "FailedNotCommitted still cascades (nothing committed)");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.Indeterminate),
                "Indeterminate stays terminal");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.CommittedMultiAddDetailsUnavailable),
                "details-unavailable stays terminal");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.CommittedTakeDetailsUnavailable),
                "take details-unavailable stays terminal");
            Check.That(!TxResponsePolicy.ShouldCascadeToNextChest(TxCompletionKind.TransientRetrySameTx),
                "transient same-tx retry never cascades");
        }
    }
}
