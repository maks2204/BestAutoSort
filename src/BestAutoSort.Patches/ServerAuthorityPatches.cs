using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BestAutoSort;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches
{
    /// <summary>
    /// Wave-1 server-authority ownership guards (experimental slice).
    ///
    /// Backstop rule (all prefixes): for a positively classified chest ZDO,
    /// ownership may move TOWARD the authority UID and never away from it
    /// (including release-to-zero); unknown authority (uid 0) moves nothing.
    /// LegacyDistributed mode and unclassified objects keep vanilla behavior.
    ///
    /// - ZDO.SetOwner: covers vanilla transfer paths (RequestOpen/RequestStack
    ///   SetOwner, ReleaseNearbyZDOS redistribution) plus any mod/client call.
    /// - ZDO.SetOwnerInternal: the RPC_ZDOData boundary — validates incoming
    ///   ownership revisions that bypass SetOwner.
    /// - ZNetView.ClaimOwnership: blocks mod/client claims for eligible chests
    ///   (authority itself may still claim: repairs + structural).
    /// - ZDOMan.ReleaseNearbyZDOS: transpiler reroutes both SetOwner call
    ///   sites through ConditionalSetOwner (skip eligible chests narrowly).
    ///   Patch-application is verified (ExpectedSites): on IL change the patch
    ///   fails and the SetOwner backstop above still holds fail-closed.
    /// - Container.RPC_TakeAllResponse: blocks the client-side ownership
    ///   acquisition for eligible chests (take-all runs through ChestTX).
    /// </summary>
    internal static class ServerReleaseGuard
    {
        /// <summary>SetOwner call sites in ReleaseNearbyZDOS (verified by IL dump).</summary>
        internal const int ExpectedSites = 2;

        internal static int PatchedSites;

        internal static void ConditionalSetOwner(ZDO zdo, long uid)
        {
            try
            {
                if (zdo != null && ServerAuthority.ShouldKeepServerOwnership(zdo))
                    return;
            }
            catch
            {
            }
            try
            {
                zdo.SetOwner(uid);
            }
            catch
            {
            }
        }
    }

    [HarmonyPatch(typeof(ZDO), "SetOwner", new Type[] { typeof(long) })]
    internal static class ZdoSetOwnerGuardPatch
    {
        private static bool Prefix(ZDO __instance, long uid)
        {
            try
            {
                if (!ServerAuthority.IsAuthorityMode())
                    return true;
                if (__instance == null || !ServerAuthority.IsServerManagedChestZdo(__instance))
                    return true;
                long auth = ServerAuthority.AuthorityUid();
                if (auth == 0L)
                    return false;
                if (uid == auth)
                    return true;
                try
                {
                    if (__instance.GetOwner() == uid)
                        return true;
                }
                catch { }
                // Relinquish (owner -> 0) is always safe and never a takeover:
                // nobody gains, vanilla open-routing (broadcast on 0) keeps
                // working, and managed chests converge to the servable state.
                // Required so virgin-clear and peer-disconnect releases (plus
                // every peer adopting them) are not stuck behind this guard.
                if (uid == 0L)
                    return true;
                ServerAuthority.NoteUnexpectedClientOwnership(__instance, uid, "SetOwner");
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(ZDO), "SetOwnerInternal")]
    internal static class ZdoSetOwnerInternalGuardPatch
    {
        private static bool Prefix(ZDO __instance, long uid)
        {
            try
            {
                if (!ServerAuthority.IsAuthorityMode())
                    return true;
                if (__instance == null || !ServerAuthority.IsServerManagedChestZdo(__instance))
                    return true;
                long auth = ServerAuthority.AuthorityUid();
                if (auth == 0L)
                    return false;
                if (uid == auth)
                    return true;
                try
                {
                    if (__instance.GetOwner() == uid)
                        return true;
                }
                catch { }
                if (uid == 0L)
                    return true;
                ServerAuthority.NoteUnexpectedClientOwnership(__instance, uid, "SetOwnerInternal");
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(ZNetView), "ClaimOwnership")]
    internal static class ClaimOwnershipGuardPatch
    {
        private static bool Prefix(ZNetView __instance)
        {
            try
            {
                if (!ServerAuthority.IsAuthorityMode())
                    return true;
                if ((Object)__instance == (Object)null)
                    return true;
                Container container = __instance.GetComponent<Container>();
                if ((Object)container == (Object)null)
                    return true;
                if (!ServerAuthority.IsServerManagedContainer(container))
                    return true;
                long auth = ServerAuthority.AuthorityUid();
                long self = 0L;
                try
                {
                    self = ZNet.GetUID();
                }
                catch
                {
                }
                if (auth != 0L && self == auth)
                    return true;
                ServerAuthority.NoteBlockedClaim(container, self, "ClaimOwnership");
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
    internal static class ReleaseNearbyGuardPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            System.Reflection.MethodInfo vanilla = AccessTools.Method(typeof(ZDO), "SetOwner", new Type[] { typeof(long) });
            System.Reflection.MethodInfo replacement = AccessTools.Method(typeof(ServerReleaseGuard), "ConditionalSetOwner", new Type[] { typeof(ZDO), typeof(long) });
            int sites = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.Calls(vanilla))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                    sites++;
                }
                yield return ci;
            }
            ServerReleaseGuard.PatchedSites = sites;
            if (sites != ServerReleaseGuard.ExpectedSites)
                throw new InvalidOperationException("ReleaseNearbyZDOS IL changed: found " + sites + " SetOwner sites, expected " + ServerReleaseGuard.ExpectedSites + " (failing closed: SetOwner backstop still holds)");
        }
    }

    [HarmonyPatch(typeof(Container), "RPC_TakeAllResponse")]
    internal static class ServerAuthorityTakeAllResponsePatch
    {
        private static bool Prefix(Container __instance, long uid, bool granted)
        {
            try
            {
                if (!granted)
                    return true;
                if (!ServerAuthority.IsServerManagedContainer(__instance))
                    return true;
                try
                {
                    Plugin.LogInstance.LogInfo((object)"[ChestTX] server-authority: TakeAllResponse ownership claim suppressed (take-all runs through ChestTX)");
                }
                catch
                {
                }
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
