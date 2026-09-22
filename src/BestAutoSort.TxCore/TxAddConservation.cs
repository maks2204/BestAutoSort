using System;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Conservation guard for Add destination crediting.
    ///
    /// A quick-stack cascade or batch plans each item amount (TxOpItem.Amount) when the
    /// batch is built. An earlier operation in the same batch/cascade may already have
    /// consumed part of the same physical source stack, so the planned amount can be
    /// stale by the time the manager credits the destination. Crediting the stale
    /// amount duplicates items: the chest keeps N while the live source only backs M.
    ///
    /// Invariant: amount credited to the destination &lt;= amount still backed by the
    /// live source. The live source is only visible for local/owner submissions
    /// (TxOpItem.SourceRef, never serialized); remote operations pass null and keep
    /// current behavior. This helper is the shared, testable statement of that rule:
    /// the Unity manager (ChestTxService.ExecuteAdd) applies it to the live
    /// SourceRef.m_stack, and the offline harness regresses it here.
    /// </summary>
    public static class TxAddConservation
    {
        /// <summary>
        /// Clamp a planned Add amount to the live source stack.
        /// liveSourceStack == null (remote / no live reference): planned unchanged.
        /// </summary>
        public static int ClampAddAmount(int planned, int? liveSourceStack)
        {
            if (!liveSourceStack.HasValue)
                return planned;
            int live = Math.Max(0, liveSourceStack.Value);
            int want = Math.Max(0, planned);
            return Math.Min(want, live);
        }

        /// <summary>
        /// Whether an item counts as a fully successful transfer for status purposes.
        /// A completely depleted source (amount == 0, accepted == 0) must NOT count:
        /// otherwise a fully depleted batch would look Accepted because 0 == 0.
        /// </summary>
        public static bool CountsAsFull(int amount, int accepted)
        {
            return amount > 0 && accepted == amount;
        }
    }
}
