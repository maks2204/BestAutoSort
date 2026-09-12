using BestAutoSort.Runtime;
using BestAutoSort.Tx;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches
{
    /// <summary>
    /// Second player opening a chest: allow WITHOUT transferring ZDO ownership.
    /// The vanilla "in use → deny" branch is bypassed in shared mode only;
    /// access checks (privacy/wards) are preserved.
    /// </summary>
    [HarmonyPatch(typeof(Container), "RPC_RequestOpen")]
    internal static class TxContainerOpenPatch
    {
        private static bool Prefix(Container __instance, long uid, long playerID)
        {
            if (AutoFeedService.IsLocked(__instance))
            {
                ZNetView component = ((Component)__instance).GetComponent<ZNetView>();
                if (component != null)
                    component.InvokeRPC(uid, "RPC_OpenResponse", new object[1] { false });
                return false;
            }
            if (!ModConfig.AllowConcurrentChestUse.Value || !ChestTxService.IsShared(__instance))
                return true; // vanilla behavior
            if (!TxReflect.HasAccess(__instance, playerID))
            {
                // Access denied — like vanilla.
                ZNetView denyView = TxReflect.GetNetView(__instance);
                if (denyView != null)
                    denyView.InvokeRPC(uid, "RPC_OpenResponse", new object[1] { false });
                return false;
            }
            // Grant without SetOwner: push the current state to the opener.
            ZNetView view = TxReflect.GetNetView(__instance);
            if (view != null && view.IsValid())
            {
                ZDOMan.instance.ForceSendZDO(uid, view.GetZDO().m_uid);
                view.InvokeRPC(uid, "RPC_OpenResponse", new object[1] { true });
            }
            return false;
        }
    }
}
