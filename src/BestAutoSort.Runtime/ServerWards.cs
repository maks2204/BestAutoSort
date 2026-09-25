using System;
using System.Collections.Generic;
using UnityEngine;

namespace BestAutoSort.Runtime
{
    /// <summary>
    /// Option-B ZDO-level ward evaluation (PrivateArea objects never instantiate
    /// server-side, but every fact vanilla needs lives in ZDOs):
    /// - enabled: ZDO s_enabled (mirrors PrivateArea.IsEnabled, minus the
    ///   netview-validity clause which is zdo.IsValid() here);
    /// - coverage: DistanceXZ(wardPos, point) &lt; prefab m_radius (+0 radius,
    ///   mirroring the Container call-site);
    /// - access: ward creator (s_creator, same key Piece uses) or permitted
    ///   list (s_permitted count + pu_id{i}, mirroring GetPermittedPlayers).
    /// No ward covering the point behaves exactly like vanilla
    /// (PrivateArea.CheckAccess returns true when nothing covers).
    /// Sector-bounded scan (FindSectorObjects around the chest zone), so cost
    /// is proportional to nearby ZDOs, not the world.
    /// </summary>
    internal static class ServerWards
    {
        internal static bool WardAllows(Vector3 chestPos, long playerId, out string why)
        {
            why = "ok";
            try
            {
                ZDOMan man = null;
                try { man = ZDOMan.instance; } catch { man = null; }
                ZNetScene scene = null;
                try { scene = ZNetScene.instance; } catch { scene = null; }
                if (man == null || scene == null)
                {
                    why = "no-scene";
                    return false;
                }
                Vector2s zone = ZoneSystem.GetZone(chestPos);
                List<ZDO> near = new List<ZDO>();
                try
                {
                    SimulationDistance dist = new SimulationDistance(1, 0, true);
                    man.FindSectorObjects(zone, dist, near);
                }
                catch
                {
                    why = "sector-scan-failed";
                    return false;
                }
                if (near == null || near.Count == 0)
                    return true;
                foreach (ZDO z in near)
                {
                    try
                    {
                        if (z == null || !z.IsValid())
                            continue;
                        GameObject prefab = null;
                        try { prefab = scene.GetPrefab(z.GetPrefab()); } catch { prefab = null; }
                        if ((UnityEngine.Object)prefab == (UnityEngine.Object)null)
                            continue;
                        PrivateArea area = null;
                        try { area = prefab.GetComponent<PrivateArea>(); } catch { area = null; }
                        if ((UnityEngine.Object)area == (UnityEngine.Object)null)
                            continue;
                        bool enabled = false;
                        try { enabled = z.GetBool(ZDOVars.s_enabled); } catch { enabled = false; }
                        if (!enabled)
                            continue;
                        float radius = 10f;
                        try { radius = area.m_radius; } catch { radius = 10f; }
                        Vector3 wardPos = Vector3.zero;
                        try { wardPos = z.GetPosition(); } catch { continue; }
                        float dx = wardPos.x - chestPos.x;
                        float dz = wardPos.z - chestPos.z;
                        if (dx * dx + dz * dz >= radius * radius)
                            continue;
                        long creator = 0L;
                        try { creator = z.GetLong(ZDOVars.s_creator, 0L); } catch { creator = 0L; }
                        if (creator != 0L && creator == playerId)
                            continue;
                        if (IsPermitted(z, playerId))
                            continue;
                        why = "ward";
                        return false;
                    }
                    catch
                    {
                        // Per-candidate fault: the ward set is undecidable, so
                        // deny the whole check (fail closed, never grant).
                        why = "ward-undecidable";
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

        private static bool IsPermitted(ZDO wardZdo, long playerId)
        {
            try
            {
                if (playerId == 0L)
                    return false;
                int count = 0;
                try { count = wardZdo.GetInt(ZDOVars.s_permitted); } catch { count = 0; }
                for (int i = 0; i < count; i++)
                {
                    long id = 0L;
                    try { id = wardZdo.GetLong("pu_id" + i, 0L); } catch { id = 0L; }
                    if (id != 0L && id == playerId)
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
