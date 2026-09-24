using System;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Crash-injection points inside the durable Apply order, in firing order:
    /// AfterFence (fence durable, nothing mutated), AfterExecute (RAM mutated,
    /// items NOT saved), AfterItemsSave (items saved, ring NOT written),
    /// AfterRing (ring written, final floor write NOT done).
    /// A hook throwing TxCrashException at one of these points simulates a
    /// process crash there: the test harness then "restarts" from the persisted
    /// copies only (ring/floor bytes + items snapshot captured by the hooks).
    /// </summary>
    public enum TxDurabilityPoint
    {
        AfterFence,
        AfterExecute,
        AfterItemsSave,
        AfterRing
    }

    /// <summary>Simulated process crash at a TxDurabilityPoint. Never swallowed by the core.</summary>
    public sealed class TxCrashException : Exception
    {
        public TxDurabilityPoint Point;

        public TxCrashException(TxDurabilityPoint point)
            : base("simulated crash at " + point)
        {
            Point = point;
        }
    }

    /// <summary>
    /// Injectable persistence seam for the durable pre-execution fence. Production
    /// (ChestTxService: ZDO keys + Container.Save) and the offline crash harness
    /// (FakeWorld byte slots + ModelChest snapshot) share the SAME order through
    /// TxCore.Apply; only these delegates differ. Every delegate is optional
    /// (null = always succeed / owner present / no crash): with Durability unset
    /// Apply keeps its exact legacy RAM-only behavior.
    ///
    /// Contract:
    /// - WriteFloor/WriteRing receive the exact encoded bytes and return true only
    ///   when they are durably persisted. False (or a non-crash throw) = write
    ///   failed, nothing persisted: the core answers Indeterminate and persists
    ///   nothing further for that tx. A throw of TxCrashException models a crash
    ///   DURING the write and propagates (never coerced to failure).
    /// - SaveItems runs after Execute mutated RAM and models Container.Save: false =
    ///   save failed (RAM mutation stays unpersisted), answer Indeterminate.
    ///   A non-crash throw from SaveItems models an ambiguous save (the ZDO write
    ///   may or may not have landed): the core recovers from persisted state and
    ///   answers Indeterminate. TxCrashException always propagates.
    /// - ReloadItems restores the live chest from the persisted items snapshot
    ///   (production: ZDOVars.s_items) and returns true only when every step
    ///   succeeded. Null/unavailable/false = recovery failure: the core stays
    ///   quarantined, never presumes empty. Used after post-fence Execute/save
    ///   failures (same-process authoritative reload, no restart needed).
    /// - FailExecute models a mid-execution fault: when it returns true for a
    ///   request, Execute mutates the first item and then throws, proving that
    ///   partial RAM mutation is rolled back by recovery, not left dirty.
    /// - IsOwner is the post-fence ownership recheck (ZDO.Set is owner-gated):
    ///   false = ownership lost after the fence — no mutation may follow.
    /// - Crash fires at TxDurabilityPoints; throwing TxCrashException simulates
    ///   the crash window. Anything else thrown propagates as-is.
    /// </summary>
    public sealed class TxDurability
    {
        public Func<byte[], bool> WriteFloor;
        public Func<byte[], bool> WriteRing;
        public Func<ModelChest, bool> SaveItems;
        public Func<ModelChest, bool> ReloadItems;
        public Func<TxRequest, bool> FailExecute;
        public Func<bool> IsOwner;
        public Action<TxDurabilityPoint> Crash;
    }
}
