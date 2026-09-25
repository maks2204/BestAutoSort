using System;
using System.Collections.Generic;
using BestAutoSort.Runtime;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Option-B (server-side manager) slice 2: ZDO-keyed sessions + codec.
    ///
    /// The server never has Container components near remote players
    /// (data-only Ghost zones), so the manager works on raw ZDO data:
    ///   decode: s_items bytes -> detached Inventory (vanilla Inventory.Load,
    ///           byte-identical to Container.Load by construction)
    ///   encode: detached Inventory -> s_items bytes (vanilla Inventory.Save,
    ///           byte-identical to Container.Save) -> zdo.Set (+auto revision
    ///           bump) -> ForceSendZDO propagation hint.
    /// Dimensions come from the PREFAB's Container (ZNetScene prefabs are loaded
    /// server-side; instances are not needed): prefab.GetComponent&lt;Container&gt;().
    /// m_width/m_height are public vanilla fields.
    ///
    /// Fail-closed throughout: unknown prefab, missing prefab, insane dims,
    /// null/invalid s_items -> quarantined session (never presumed empty,
    /// never served). No manager decisions here (slice 3).
    /// </summary>
    internal sealed class ServerChestSession
    {
        internal ZDOID ZdoId;
        internal string PrefabName = "?";
        internal int W;
        internal int H;
        internal bool Quarantined;
        internal string QuarantineReason = "?";
        internal long QuarantineOldOwner;
        internal uint LastSeenRev;
        /// <summary>
        /// Embedded manager state (Container == null: ZDO-only path). Reuses the
        /// shared idempotency machinery (Processed/ProcOrder/Floor/ring flags)
        /// without a Container. Server-only: Queue/Draining/Viewers/transient
        /// RAM map stay unused (synchronous per-request processing).
        /// </summary>
        internal readonly ChestState State = new ChestState();
        /// <summary>Both-copies-failed txIds (production TransientRefusals map
        /// equivalent, session-scoped RAM): retained same-tx retries instead of
        /// terminal forget. Dropped with the session (restart/prune).</summary>
        internal readonly HashSet<long> Transient = new HashSet<long>();
        /// <summary>FNV-1a 64 of the last OUR s_items bytes on this session
        /// (stamped on every server save; ring/floor keys never touch s_items).
        /// Revalidation serves only while live bytes still hash equal.</summary>
        internal ulong SItemsHash;
        internal bool HasSItemsHash;
    }

    internal static class ServerChestSessions
    {
        private static readonly Dictionary<ZDOID, ServerChestSession> Sessions =
            new Dictionary<ZDOID, ServerChestSession>();

        internal static void Reset()
        {
            try { Sessions.Clear(); } catch { }
        }

        internal static int Count
        {
            get
            {
                try { return Sessions.Count; } catch { return 0; }
            }
        }

        /// <summary>
        /// Get or create the session for a chest ZDO. Returns false when the ZDO
        /// is not an eligible managed chest (fail closed: no session, no serve).
        /// A quarantined session is still returned (quarantine is a state, not
        /// an absence) with session.Quarantined == true.
        /// </summary>
        internal static bool TryGetOrCreate(ZDO zdo, out ServerChestSession session, out string why)
        {
            session = null;
            why = "?";
            try
            {
                if (zdo == null)
                {
                    why = "null zdo";
                    return false;
                }
                bool eligible = false;
                try { eligible = ServerAuthority.IsServerManagedChestZdo(zdo); }
                catch { eligible = false; }
                if (!eligible)
                {
                    why = "not eligible";
                    return false;
                }
                ZDOID id = zdo.m_uid;
                ServerChestSession existing = null;
                try { Sessions.TryGetValue(id, out existing); } catch { existing = null; }
                if (existing != null)
                {
                    session = existing;
                    why = "ok";
                    return true;
                }
                string prefabName = "?";
                int w = 0;
                int h = 0;
                try
                {
                    ZNetScene scene = ZNetScene.instance;
                    GameObject prefab = scene != null ? scene.GetPrefab(zdo.GetPrefab()) : null;
                    if ((UnityEngine.Object)prefab == (UnityEngine.Object)null)
                    {
                        why = "no prefab";
                        return false;
                    }
                    Container preset = prefab.GetComponent<Container>();
                    if ((UnityEngine.Object)preset == (UnityEngine.Object)null)
                    {
                        why = "prefab has no Container";
                        return false;
                    }
                    prefabName = prefab.name;
                    w = preset.m_width;
                    h = preset.m_height;
                }
                catch
                {
                    why = "prefab read failed";
                    return false;
                }
                if (w < 1 || h < 1 || w > 24 || h > 24 || w * h > 480)
                {
                    why = "insane dims " + w + "x" + h;
                    return false;
                }
                ServerChestSession created = new ServerChestSession();
                created.ZdoId = id;
                created.PrefabName = prefabName;
                created.W = w;
                created.H = h;
                long owner = 0L;
                try { owner = zdo.GetOwner(); } catch { owner = 0L; }
                byte[] items = null;
                bool readOk = false;
                try
                {
                    items = TxSItemsGuard.CloneBytes(zdo.GetByteArray(ZDOVars.s_items));
                    readOk = true;
                }
                catch
                {
                    items = null;
                    readOk = false;
                }
                if (!readOk || items == null)
                {
                    created.Quarantined = true;
                    created.QuarantineReason = !readOk ? "read-failed" : "null-s-items";
                    created.QuarantineOldOwner = owner;
                }
                else
                {
                    string vreason = "ok";
                    bool valid = false;
                    try { valid = TxSItemsGuard.TryValidate(items, out vreason); }
                    catch { valid = false; vreason = "validate-threw"; }
                    if (!valid)
                    {
                        created.Quarantined = true;
                        created.QuarantineReason = "invalid-s-items(" + vreason + ")";
                        created.QuarantineOldOwner = owner;
                    }
                }
                try { created.LastSeenRev = zdo.DataRevision; } catch { created.LastSeenRev = 0u; }
                try
                {
                    byte[] cur = zdo.GetByteArray(ZDOVars.s_items);
                    if (cur != null)
                    {
                        created.SItemsHash = Fnv1a64(cur);
                        created.HasSItemsHash = true;
                    }
                }
                catch { }
                created.State.ZdoId = id;
                // Restart/handoff recovery: rebuild idempotency state from the
                // durable ring + independent floor copies (shared SeedFromRing —
                // Container-free). Absent copies are empty, never corrupt.
                try
                {
                    RingData ring = new RingData();
                    try
                    {
                        byte[] ringBytes = zdo.GetByteArray(ChestTxService.RingKey.GetStableHashCode());
                        if (ringBytes != null)
                            ring = TxCore.TxCore.DecodeEnvelope(ringBytes);
                    }
                    catch
                    {
                        ring = new RingData();
                        ring.Corrupt = true;
                    }
                    FloorData floor = new FloorData();
                    try
                    {
                        byte[] floorBytes = zdo.GetByteArray(ChestTxService.FloorKey.GetStableHashCode());
                        if (floorBytes != null)
                        {
                            floor = TxCore.TxCore.DecodeFloor(floorBytes);
                            floor.Present = true;
                        }
                    }
                    catch
                    {
                        floor = new FloorData();
                        floor.Present = true;
                        floor.Corrupt = true;
                    }
                    ChestTxService.SeedFromRing(created.State, ring, floor);
                }
                catch
                {
                    created.State.RingCorrupt = true;
                    created.State.FloorCorrupt = true;
                }
                if (!created.Quarantined && created.State.RingCorrupt && created.State.FloorCorrupt)
                {
                    created.Quarantined = true;
                    created.QuarantineReason = "ring+floor corrupt";
                    created.QuarantineOldOwner = owner;
                }
                try { Sessions[id] = created; } catch { }
                session = created;
                why = created.Quarantined ? "quarantined(" + created.QuarantineReason + ")" : "ok";
                return true;
            }
            catch
            {
                session = null;
                why = "fault";
                return false;
            }
        }

        internal static List<ServerChestSession> Snapshot()
        {
            try
            {
                return new List<ServerChestSession>(Sessions.Values);
            }
            catch
            {
                return null;
            }
        }

        internal static bool TryGet(ZDOID id, out ServerChestSession session)
        {
            session = null;
            try { return Sessions.TryGetValue(id, out session) && session != null; }
            catch { session = null; return false; }
        }

        /// <summary>
        /// Decode the CURRENT ZDO bytes into a detached Inventory (vanilla
        /// Inventory.Load — same call Container.Load makes). Reads are never
        /// owner-gated. Returns false on null/invalid/undecodable (fail closed).
        /// </summary>
        internal static bool TryDecodeItems(ServerChestSession session, ZDO zdo, out Inventory inv, out string why)
        {
            inv = null;
            why = "?";
            try
            {
                if (session == null || zdo == null)
                {
                    why = "null session/zdo";
                    return false;
                }
                byte[] bytes = null;
                try { bytes = TxSItemsGuard.CloneBytes(zdo.GetByteArray(ZDOVars.s_items)); }
                catch { bytes = null; }
                if (bytes == null)
                {
                    why = "null s_items";
                    return false;
                }
                string vreason = "ok";
                bool valid = false;
                try { valid = TxSItemsGuard.TryValidate(bytes, out vreason); }
                catch { valid = false; vreason = "validate-threw"; }
                if (!valid)
                {
                    why = "invalid s_items(" + vreason + ")";
                    return false;
                }
                Inventory work = null;
                try { work = new Inventory("srv-" + session.PrefabName, null, session.W, session.H); }
                catch
                {
                    why = "inventory alloc failed";
                    return false;
                }
                try
                {
                    ZPackage pkg = new ZPackage(bytes);
                    work.Load(pkg);
                }
                catch (Exception ex)
                {
                    why = "load failed: " + ex.Message;
                    return false;
                }
                try { session.LastSeenRev = zdo.DataRevision; } catch { }
                inv = work;
                why = "ok";
                return true;
            }
            catch
            {
                inv = null;
                why = "fault";
                return false;
            }
        }

        /// <summary>
        /// Encode a detached Inventory back to s_items (vanilla Inventory.Save —
        /// same call Container.Save makes), Set on the ZDO (auto revision bump),
        /// ForceSendZDO propagation hint (never ACK). Returns the new revision.
        /// </summary>
        internal static bool TrySaveItems(ServerChestSession session, ZDO zdo, Inventory inv, out uint newRev, out string why)
        {
            newRev = 0u;
            why = "?";
            try
            {
                if (session == null || zdo == null || inv == null)
                {
                    why = "null session/zdo/inv";
                    return false;
                }
                byte[] bytes = null;
                try
                {
                    ZPackage pkg = new ZPackage();
                    inv.Save(pkg);
                    bytes = pkg.GetArray();
                }
                catch (Exception ex)
                {
                    why = "save failed: " + ex.Message;
                    return false;
                }
                if (bytes == null)
                {
                    why = "save produced null";
                    return false;
                }
                try { zdo.Set(ZDOVars.s_items, bytes); }
                catch (Exception ex)
                {
                    why = "zdo set failed: " + ex.Message;
                    return false;
                }
                try { newRev = zdo.DataRevision; } catch { newRev = 0u; }
                try { session.LastSeenRev = newRev; } catch { }
                try { session.SItemsHash = Fnv1a64(bytes); session.HasSItemsHash = true; } catch { }
                try
                {
                    if (ZDOMan.instance != null)
                        ZDOMan.instance.ForceSendZDO(session.ZdoId);
                }
                catch { }
                why = "ok";
                return true;
            }
            catch
            {
                why = "fault";
                return false;
            }
        }

        internal static ulong Fnv1a64(byte[] bytes)
        {
            ulong h = 14695981039346656037UL;
            if (bytes != null)
            {
                for (int i = 0; i < bytes.Length; i++)
                {
                    h ^= bytes[i];
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        internal static void Prune()
        {
            try
            {
                if (Sessions.Count == 0)
                    return;
                ZDOMan man = null;
                try { man = ZDOMan.instance; } catch { man = null; }
                if (man == null)
                    return;
                List<ZDOID> dead = null;
                foreach (KeyValuePair<ZDOID, ServerChestSession> kv in Sessions)
                {
                    bool alive = false;
                    try
                    {
                        ZDO z = man.GetZDO(kv.Key);
                        alive = z != null && z.IsValid();
                    }
                    catch { alive = false; }
                    if (!alive)
                    {
                        if (dead == null)
                            dead = new List<ZDOID>();
                        dead.Add(kv.Key);
                    }
                }
                if (dead != null)
                {
                    foreach (ZDOID id in dead)
                    {
                        try { Sessions.Remove(id); } catch { }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
