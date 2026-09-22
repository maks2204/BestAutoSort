using System;

namespace ChestTx.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            Console.WriteLine("ChestTX protocol tests");
            TxTests.Test1_SimultaneousAdd();
            TxTests.Test2_SimultaneousRemove();
            TxTests.Test3_SameItemRace1000();
            TxTests.Test4_PartialCapacity();
            TxTests.Test5_DuplicatedRpc();
            TxTests.Test6_ReorderedRpc();
            TxTests.Test7_DisconnectDuringTx();
            TxTests.Test8_ManagerDisconnect();
            TxTests.Test9_QuickStackConcurrency();
            TxTests.Test10_ManualMovePlusQuickStack();
            TxTests.Test11_AutoPullPlusPlayer();
            TxTests.Test12_ThreePlayerSoak();
            TxTests.Test13_LegacyDimensions();
            Console.WriteLine(Check.Failures == 0 ? "ALL TESTS PASSED" : Check.Failures + " FAILURES");
            return Check.Failures == 0 ? 0 : 1;
        }
    }
}
