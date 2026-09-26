using System;
using System.Reflection;
using BestAutoSort.Runtime;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches
{
    /// <summary>
    /// Hammer-remove of a server-managed chest goes through the server-mediated
    /// destroy protocol (TxDestroy) instead of the vanilla ownership-gated chain
    /// (which silently does nothing — or refunds + reappears — for ownerless
    /// chests). Legacy/unmanaged pieces keep vanilla behavior bit-for-bit.
    ///
    /// The prefix replicates only the aim raycast (never the vanilla gates).
    /// Enforced authoritatively server-side: managed detection, compat,
    /// sender binding, Piece.m_canBeRemoved, privacy+empty, access (incl
    /// range+slack), wards, creator. Residuals: no-build-zone and
    /// crafting-station gates cannot evaluate headless and are skipped;
    /// the damage-destroy path is untouched. Pre-managed and fault paths
    /// return true (vanilla runs, old behavior, no new holes); a managed
    /// send-fail or busy chest stays swallowed (fail-closed, retry) and never
    /// falls back to vanilla.
    /// </summary>
    [HarmonyPatch(typeof(Player), "RemovePiece")]
    internal static class TxDestroyRemovePatch
    {
        private static readonly FieldInfo RemoveRayMaskField =
            AccessTools.Field(typeof(Player), "m_removeRayMask");
        private static readonly FieldInfo MaxPlaceDistanceField =
            AccessTools.Field(typeof(Player), "m_maxPlaceDistance");

        private static bool Prefix(Player __instance)
        {
            try
            {
                if (!ServerAuthority.IsAuthorityMode())
                    return true;
                if ((UnityEngine.Object)__instance == (UnityEngine.Object)null)
                    return true;
                Player player = Player.m_localPlayer;
                if ((UnityEngine.Object)player == (UnityEngine.Object)null || (UnityEngine.Object)player != (UnityEngine.Object)__instance)
                    return true;
                int mask = 0;
                float maxDist = 0f;
                try
                {
                    if (RemoveRayMaskField == null || MaxPlaceDistanceField == null)
                        return true;
                    mask = (int)RemoveRayMaskField.GetValue(__instance);
                    maxDist = (float)MaxPlaceDistanceField.GetValue(__instance);
                }
                catch
                {
                    return true;
                }
                if (mask == 0 || maxDist <= 0f)
                    return true;
                RaycastHit hitInfo;
                Transform eye = null;
                try { eye = ((Component)__instance).transform; } catch { eye = null; }
                if ((UnityEngine.Object)eye == (UnityEngine.Object)null)
                    return true;
                bool hit = false;
                try { hit = Physics.Raycast(GameCamera.instance.transform.position, GameCamera.instance.transform.forward, out hitInfo, 50f, mask); }
                catch { return true; }
                if (!hit)
                    return true;
                float dist = 0f;
                try { dist = Vector3.Distance(hitInfo.point, eye.position); } catch { return true; }
                if (!(dist < maxDist))
                    return true;
                Piece piece = null;
                try { piece = hitInfo.collider.GetComponentInParent<Piece>(); } catch { piece = null; }
                if ((UnityEngine.Object)piece == (UnityEngine.Object)null)
                    return true;
                Container container = null;
                try { container = ((Component)piece).GetComponent<Container>(); } catch { container = null; }
                if (container == null)
                {
                    try { container = ((Component)piece).GetComponentInChildren<Container>(); } catch { container = null; }
                }
                if ((UnityEngine.Object)container == (UnityEngine.Object)null)
                    return true;
                bool managed = false;
                try { managed = ServerAuthority.IsServerManagedContainer(container); } catch { managed = false; }
                if (!managed)
                    return true;
                ZDOID chestId = ZDOID.None;
                try
                {
                    ZNetView netView = TxReflect.GetNetView(container);
                    if ((UnityEngine.Object)netView != (UnityEngine.Object)null && netView.IsValid())
                        chestId = netView.GetZDO().m_uid;
                }
                catch { chestId = ZDOID.None; }
                if (chestId.IsNone())
                    return true;
                long playerId = 0L;
                try
                {
                    Game game = Game.instance;
                    if (game != null)
                    {
                        PlayerProfile profile = game.GetPlayerProfile();
                        if (profile != null)
                            playerId = profile.GetPlayerID();
                    }
                }
                catch { playerId = 0L; }
                Vector3 actorPos = Vector3.zero;
                try { actorPos = ((Component)__instance).transform.position; } catch { actorPos = Vector3.zero; }
                // Residual: other clients' in-flight txs vs this racing destroy
                // resolve to Indeterminate (ms race class) — only the local
                // client's pending txs can be gated here.
                if (ChestTxService.HasPendingFor(container))
                {
                    try
                    {
                        Player lp2 = Player.m_localPlayer;
                        if ((UnityEngine.Object)lp2 != (UnityEngine.Object)null)
                            ((Character)lp2).Message((MessageType)2, "Finish pending chest operations first.", 0, (Sprite)null, false);
                    }
                    catch { }
                    return false;
                }
                if (TxDestroy.NoteDuplicate(chestId))
                    return false;
                bool sent = false;
                try { sent = TxDestroy.SubmitDestroy(container, chestId, playerId, actorPos); } catch { sent = false; }
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
