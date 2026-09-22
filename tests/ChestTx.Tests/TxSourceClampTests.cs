using System;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Regression coverage for the quick-stack duplication race in ExecuteAdd.
    ///
    /// Production rule (src/Tx/ChestTxService.cs, ExecuteAdd): the planned item amount
    /// (TxOpItem.Amount, fixed when a cascade/batch is built) is clamped to the live
    /// source stack (TxOpItem.SourceRef.m_stack) immediately before crediting the
    /// destination; depleted entries credit nothing and never count as full.
    ///
    /// The Unity manager itself cannot run in this offline harness (net8.0, no Valheim
    /// assemblies), so these tests execute the shared decision rule
    /// (TxAddConservation, the exact helper the manager calls) plus the real
    /// destination-crediting path (ModelChest via TxCore.Apply) to prove the
    /// conservation invariant: amount credited to the destination &lt;= amount still
    /// backed by the live source.
    /// </summary>
    internal static class TxSourceClampTests
    {
        private static readonly ItemKey Wood = new ItemKey(1001, 1, 0, 0);

        /// <summary>
        /// Mirror of the manager's per-item credit order: clamp to the live source
        /// first, skip inventory-add when depleted, then credit the destination.
        /// </summary>
        private static int ManagerCredit(ModelChest chest, ItemKey key, int planned, int? liveSourceStack, int maxStack, out int amount)
        {
            amount = TxAddConservation.ClampAddAmount(planned, liveSourceStack);
            if (amount <= 0)
                return 0;
            return chest.AddItem(key, amount, maxStack);
        }

        public static void RunAll()
        {
            Test_StaleAmountClamp();
            Test_CompletelyDepletedSource();
            Test_NormalTransferUnaffected();
            Test_RemoteNoSourceRefUnaffected();
            Test_ConservationAcrossSharedSource();
        }

        // Test 1 — stale amount clamp: planned 50, live source 30 → credit 30, never 50.
        public static void Test_StaleAmountClamp()
        {
            Console.WriteLine("TestSourceClamp stale amount clamp");
            ModelChest chest = new ModelChest(6, 4);
            int amount;
            int accepted = ManagerCredit(chest, Wood, 50, 30, 50, out amount);
            Check.Equal(30, amount, "clamped amount must be 30");
            Check.Equal(30, accepted, "destination credited 30, never 50");
            Check.Equal(30, chest.TotalOf(Wood), "chest holds 30");
            Check.That(TxAddConservation.CountsAsFull(amount, accepted), "30/30 counts as full");
        }

        // Test 2 — completely depleted source: planned 50, live 0 → credit 0,
        // accepted 0, and 0 == 0 must NOT count as a fully successful transfer.
        public static void Test_CompletelyDepletedSource()
        {
            Console.WriteLine("TestSourceClamp completely depleted source");
            ModelChest chest = new ModelChest(6, 4);
            int amount;
            int accepted = ManagerCredit(chest, Wood, 50, 0, 50, out amount);
            Check.Equal(0, amount, "clamped amount must be 0");
            Check.Equal(0, accepted, "destination credited 0");
            Check.Equal(0, chest.TotalOf(Wood), "chest untouched");
            Check.That(!TxAddConservation.CountsAsFull(amount, accepted), "0/0 must NOT count as full (else a depleted batch looks Accepted)");
            // A batch of only depleted entries carries no meaningful transfer:
            // nothing moved -> Rejected, never Accepted.
            Check.That(!(amount > 0 && accepted == amount), "depleted entry is not a successful transfer");
        }

        // Test 3 — unaffected normal transfer: live >= planned keeps current behavior.
        public static void Test_NormalTransferUnaffected()
        {
            Console.WriteLine("TestSourceClamp normal transfer unaffected");
            Check.Equal(50, TxAddConservation.ClampAddAmount(50, 50), "live == planned unchanged");
            Check.Equal(50, TxAddConservation.ClampAddAmount(50, 80), "live > planned unchanged");
            ModelChest chest = new ModelChest(6, 4);
            int amount;
            int accepted = ManagerCredit(chest, Wood, 50, 80, 50, out amount);
            Check.Equal(50, accepted, "normal credit 50");
            Check.That(TxAddConservation.CountsAsFull(amount, accepted), "50/50 counts as full");
        }

        // Test 4 — remote / no SourceRef: null live reference preserves behavior.
        public static void Test_RemoteNoSourceRefUnaffected()
        {
            Console.WriteLine("TestSourceClamp remote (no SourceRef) unaffected");
            Check.Equal(50, TxAddConservation.ClampAddAmount(50, null), "null live source keeps planned amount");
            Check.Equal(0, TxAddConservation.ClampAddAmount(0, null), "null live source keeps 0");
            ModelChest chest = new ModelChest(6, 4);
            int amount;
            int accepted = ManagerCredit(chest, Wood, 50, null, 50, out amount);
            Check.Equal(50, accepted, "remote credit path unchanged");
        }

        // Test 5 — conservation scenario: one live source of 100 shared by two planned
        // operations (70 then stale 50). The second may credit at most 30, and the
        // global total is conserved.
        public static void Test_ConservationAcrossSharedSource()
        {
            Console.WriteLine("TestSourceClamp conservation across shared source");
            ModelChest chest = new ModelChest(6, 4);
            int liveSource = 100; // the one physical source stack both ops were planned from
            const int totalBefore = 100;

            int amount1;
            int accepted1 = ManagerCredit(chest, Wood, 70, liveSource, 50, out amount1);
            Check.Equal(70, accepted1, "first op credits 70");
            liveSource -= accepted1; // client removes exactly what the manager accepted

            // Second op was planned for 50 before the first op consumed 70.
            int amount2;
            int accepted2 = ManagerCredit(chest, Wood, 50, liveSource, 50, out amount2);
            Check.That(accepted2 <= 30, "second op credits at most 30 (live remainder), got " + accepted2);
            Check.Equal(30, accepted2, "second op credits exactly the 30 backed by the source");
            liveSource -= accepted2;

            int totalAfter = chest.GrandTotal() + liveSource;
            Check.Equal(totalBefore, totalAfter, "conservation: total_before == total_after");
            Check.Equal(100, chest.TotalOf(Wood), "chest holds exactly 100, no duplication");
            Check.Equal(0, liveSource, "source fully accounted for");
        }
    }
}
