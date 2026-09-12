using BestAutoSort.Tx;
using HarmonyLib;

namespace BestAutoSort.Patches
{
    /// <summary>
    /// ChestTX-RPC registration on every chest + manager registry bookkeeping.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Awake")]
    internal static class TxContainerAwakePatch
    {
        private static void Postfix(Container __instance)
        {
            ChestTxService.OnContainerAwake(__instance);
        }
    }
}
