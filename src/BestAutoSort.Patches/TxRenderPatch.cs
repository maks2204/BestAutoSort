using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BestAutoSort.Runtime;
using BestAutoSort.Tx;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches
{
    /// <summary>
    /// Vanilla InventoryGui.UpdateContainer renders the chest panel for the owner ONLY:
    ///   if (m_currentContainer && m_currentContainer.IsOwner()) { render } else { hide }
    /// That is why the first player's window "closed" on ownership change.
    /// The transpiler swaps the check for TxRender.ShouldRender: owner OR
    /// shared viewer (shared mode, valid chest). Viewer content refreshes
    /// from the ZDO via ChestTxService (revision poll), the GUI stays open.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
    internal static class TxRenderPatch
    {
        internal static int ReplacedCount;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo vanilla = AccessTools.Method(typeof(Container), "IsOwner", (System.Type[])null, (System.Type[])null);
            MethodInfo replacement = AccessTools.Method(typeof(TxRender), "ShouldRender", (System.Type[])null, (System.Type[])null);
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(vanilla))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                    ReplacedCount++;
                }
                yield return ci;
            }
        }
    }

    internal static class TxRender
    {
        public static bool ShouldRender(Container container)
        {
            if ((Object)container == (Object)null)
                return false;
            if (container.IsOwner())
                return true;
            if (!ModConfig.AllowConcurrentChestUse.Value)
                return false;
            // Lease-aware: already-open viewers keep rendering while feeding
            // (opens are denied authoritatively, mutations stay lease-gated).
            if (AutoFeedService.IsLocked(container))
                return true;
            return ChestTxService.IsShared(container);
        }
    }
}
