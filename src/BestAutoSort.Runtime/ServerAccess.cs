using System;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Runtime
{
    /// <summary>
    /// Option-B server access gate: mirrors ChestAuthority.CanUse leg-for-leg
    /// against headless sources (ZDO + prefab), because the server has no
    /// Container components near remote players.
    ///
    /// Source mapping (identical values by construction):
    /// - chest position: zdo.GetPosition() (drives the instance transform).
    /// - creator: zdo s_creator (Piece.Awake loads m_creator from the same key).
    /// - m_privacy / m_checkGuardStone: the PREFAB's Container (nothing in
    ///   vanilla or this mod mutates the instance fields at runtime).
    /// - actor resolution, paths, range: shared ChestAuthority helpers.
    ///
    /// Documented divergences (both toward refusal):
    /// - wards evaluate ZDO-level (ServerWards: sector scan, s_enabled,
    ///   prefab m_radius, s_creator + pu_id{i}); no-ward-covered allows
    ///   exactly like vanilla. Prefab-field trust as below.
    /// - prefab m_privacy is trusted (see above); a hypothetical third-party
    ///   runtime mutator of the instance field is unsupported.
    /// </summary>
    internal static class ServerAccess
    {
        /// <summary>
        /// Lag slack added to the range check against the server-resolved
        /// actor position (replication-lag tolerance). The client-stamped
        /// position drives visuals only. Denials report "too-far".
        /// </summary>
        private const float RangeSlackMeters = 15f;

        internal static bool CanUse(ZDO zdo, long sender, long claimedPlayerId, Vector3 claimedPos, out string why)
        {
            return CanUse(zdo, sender, claimedPlayerId, claimedPos, ZDOID.None, out why);
        }

        /// <summary>Feeder-aware overload: automation stamps the hungry animal's
        /// ZDO (TxOpCall.FeederId); range is then anchored at the animal
        /// (server-verified below), never at the player.</summary>
        internal static bool CanUse(ZDO zdo, long sender, long claimedPlayerId, Vector3 claimedPos, ZDOID feederId, out string why)
        {
            why = "ok";
            try
            {
                if (zdo == null)
                {
                    why = "null-zdo";
                    return false;
                }
                long resolvedPlayerId = 0L;
                Vector3 resolvedPos = Vector3.zero;
                bool actorResolved = false;
                try { actorResolved = ChestAuthority.ResolveActor(sender, out resolvedPlayerId, out resolvedPos); }
                catch { actorResolved = false; }
                bool serverSender = false;
                try { serverSender = sender != 0L && ChestAuthority.IsServerSenderOrSelf(sender); }
                catch { serverSender = false; }
                TxActorPath path = TxActorPath.Player;
                try { path = TxResponsePolicy.ClassifyActorPath(actorResolved, serverSender); }
                catch { path = TxActorPath.RejectNoActor; }

                long creator = 0L;
                try { creator = zdo.GetLong(ZDOVars.s_creator, 0L); } catch { creator = 0L; }
                Vector3 chestPos = Vector3.zero;
                try { chestPos = zdo.GetPosition(); } catch { chestPos = Vector3.zero; }
                float range = 4f;
                try { range = ChestAuthority.EffectiveAccessRange(); } catch { range = 4f; }

                int privacyPublic = 2;
                bool wardOptIn = false;
                try
                {
                    ZNetScene scene = ZNetScene.instance;
                    GameObject prefab = scene != null ? scene.GetPrefab(zdo.GetPrefab()) : null;
                    if ((UnityEngine.Object)prefab != (UnityEngine.Object)null)
                    {
                        Container preset = prefab.GetComponent<Container>();
                        if ((UnityEngine.Object)preset != (UnityEngine.Object)null)
                        {
                            privacyPublic = (int)preset.m_privacy;
                            wardOptIn = preset.m_checkGuardStone;
                        }
                    }
                }
                catch { }

                if (path == TxActorPath.ServerAutomation)
                {
                    if (creator == 0L || claimedPlayerId != creator)
                    {
                        why = "player-mismatch";
                        return false;
                    }
                    if (!InRange(chestPos, claimedPos, range))
                    {
                        why = "too-far";
                        return false;
                    }
                    if (!VanillaAccess(privacyPublic, creator, claimedPlayerId))
                    {
                        why = "vanilla-access";
                        return false;
                    }
                    if (wardOptIn)
                    {
                        string wwhy = "ok";
                        bool allowed = false;
                        try { allowed = ServerWards.WardAllows(chestPos, creator, out wwhy); }
                        catch { allowed = false; wwhy = "fault"; }
                        if (!allowed)
                        {
                            why = "ward";
                            return false;
                        }
                    }
                    return true;
                }
                if (path == TxActorPath.RejectNoActor)
                {
                    why = "no-actor";
                    return false;
                }
                long playerId = resolvedPlayerId;
                if (playerId != claimedPlayerId)
                {
                    why = "player-mismatch";
                    return false;
                }
                // Range: automation with a verified animal anchors at the animal
                // (server-verified ZDO below) — the player's position is
                // irrelevant when feeding (AfK farms) and unspoofable anyway.
                // Otherwise range anchors at the server-resolved player.
                bool feederOk = false;
                if (!feederId.IsNone())
                {
                    try { feederOk = VerifyFeeder(feederId, chestPos); } catch { feederOk = false; }
                }
                if (!feederOk && !InRange(chestPos, resolvedPos, range + RangeSlackMeters))
                {
                    why = "too-far";
                    return false;
                }
                if (!VanillaAccess(privacyPublic, creator, playerId))
                {
                    why = "vanilla-access";
                    return false;
                }
                if (wardOptIn)
                {
                    string wwhy = "ok";
                    bool allowed = false;
                    try { allowed = ServerWards.WardAllows(chestPos, playerId, out wwhy); }
                    catch { allowed = false; wwhy = "fault"; }
                    if (!allowed)
                    {
                        why = "ward";
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                why = "fault";
                return false;
            }
        }

        private const float FeederSlackMeters = 5f;

        /// <summary>
        /// Feeder anchor verification (headless): the ZDO exists, its prefab
        /// carries Tameable (exactly the population our feeder feeds — a
        /// creature without it can never be a legitimate feeder source), and it
        /// stands within AutoFeedRange of the chest. Hunger itself is verified
        /// client-side (no AI state headless); identity/privacy/wards still
        /// gate the request.
        /// </summary>
        private static bool VerifyFeeder(ZDOID feederId, Vector3 chestPos)
        {
            try
            {
                if (feederId.IsNone())
                    return false;
                ZDOMan man = null;
                ZNetScene scene = null;
                try { man = ZDOMan.instance; } catch { man = null; }
                try { scene = ZNetScene.instance; } catch { scene = null; }
                if (man == null || scene == null)
                    return false;
                ZDO z = null;
                try { z = man.GetZDO(feederId); } catch { z = null; }
                if (z == null)
                    return false;
                bool valid = false;
                try { valid = z.IsValid(); } catch { valid = false; }
                if (!valid)
                    return false;
                GameObject prefab = null;
                try { prefab = scene.GetPrefab(z.GetPrefab()); } catch { prefab = null; }
                if ((UnityEngine.Object)prefab == (UnityEngine.Object)null)
                    return false;
                Tameable tame = null;
                try { tame = prefab.GetComponent<Tameable>(); } catch { tame = null; }
                if ((UnityEngine.Object)tame == (UnityEngine.Object)null)
                    return false;
                Vector3 ap = Vector3.zero;
                try { ap = z.GetPosition(); } catch { return false; }
                float range = 25f;
                try { range = ModConfig.AutoFeedRange.Value; } catch { range = 25f; }
                Vector3 d = ap - chestPos;
                float r = range + FeederSlackMeters;
                return d.sqrMagnitude <= r * r;
            }
            catch
            {
                return false;
            }
        }

        private static bool InRange(Vector3 chestPos, Vector3 claimedPos, float range)
        {
            try
            {
                Vector3 d = chestPos - claimedPos;
                return d.sqrMagnitude <= range * range;
            }
            catch
            {
                return false;
            }
        }

        private static bool VanillaAccess(int privacyPublic, long creator, long playerId)
        {
            try
            {
                if (privacyPublic == 2)
                    return true;
                if (privacyPublic == 0)
                    return creator != 0L && creator == playerId;
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
