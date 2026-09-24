using BestAutoSort;
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
            bool leased = AutoFeedService.IsLocked(__instance);
            if (leased)
            {
                // Authoritative deny during feeding: falling through would run the
                // vanilla path (IsShared is false under lease) and transfer ZDO
                // ownership mid-feed. Watching stays available to already-open
                // viewers via lease-aware ShouldRender; mutations stay lease-gated.
                Plugin.LogInstance.LogInfo((object)("[ChestTX] open denied (autofeed lease) for peer=" + uid));
                ZNetView leaseView = TxReflect.GetNetView(__instance);
                if (leaseView != null)
                    leaseView.InvokeRPC(uid, "RPC_OpenResponse", new object[1] { false });
                return false;
            }
            if (!ModConfig.AllowConcurrentChestUse.Value || !ChestTxService.IsShared(__instance))
            {
                string why = !ModConfig.AllowConcurrentChestUse.Value ? "concurrent-use disabled" : "not-shared";
                bool busy = false;
                try
                {
                    busy = __instance.IsInUse();
                }
                catch
                {
                }
                Plugin.LogInstance.LogInfo((object)("[ChestTX] open via vanilla (" + why + "), owner inUse=" + busy + ", peer=" + uid));
                return true; // vanilla behavior
            }
            if (!TxReflect.HasAccess(__instance, playerID))
            {
                // Access denied — like vanilla.
                ZNetView denyView = TxReflect.GetNetView(__instance);
                if (denyView != null)
                    denyView.InvokeRPC(uid, "RPC_OpenResponse", new object[1] { false });
                return false;
            }
            // Grant WITHOUT SetOwner (deliberate: concurrent viewing keeps the
            // manager where it is; vanilla ownership transfer never runs).
            // The push below is a propagation-request (hint-only, best-effort,
            // never an ACK/barrier, never proof the opener observed anything):
            // the opener renders ONLY what its DataRevision/s_items poll
            // (PumpViewerRefresh, strictly-newer-revision gated) actually
            // observes, and reconciles before any commit. Grant is access
            // permission, not state proof — stale-grant bytes never apply.
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

    /// <summary>
    /// Requester side: log open denials so "says in use" is diagnosable.
    /// Vanilla shows $msg_inuse for every deny (lease, access, busy).
    /// </summary>
    [HarmonyPatch(typeof(Container), "RPC_OpenResponse")]
    internal static class TxContainerOpenResponsePatch
    {
        private static void Postfix(Container __instance, long uid, bool granted)
        {
            if (!granted && Plugin.IsActive)
                Plugin.LogInstance.LogInfo((object)"[ChestTX] open denied by manager (busy/lease/access?)");
        }
    }
