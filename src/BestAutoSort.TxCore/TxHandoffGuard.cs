namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Viewer refresh rule (MUST fix: grant-before-state). A grant (open response,
    /// ownership arrival) is NEVER state proof for the viewer: content applies
    /// only when provably newer than what is already live. Stale grants never
    /// roll the view backwards. Viewer-only predicate (sole production caller:
    /// PumpViewerRefresh); manager handoff ordering (Takeover-before-Drain) is
    /// a separate ordering rule, not this predicate. Pure; pinned by the
    /// offline harness.
    /// </summary>
    public static class TxHandoffGuard
    {
        /// <summary>
        /// Viewer content rule: apply revision rev only when this is the first
        /// poll (!seenOnce) or rev is strictly newer than seenRev. Equal-or-older
        /// (network reorder, duplicate push, pre-push render) is skipped — the
        /// last-good view stays live. Never throws.
        /// </summary>
        public static bool ShouldApplyViewerRefresh(bool seenOnce, uint seenRev, uint rev)
        {
            if (!seenOnce)
                return true;
            return rev > seenRev;
        }
    }
}
