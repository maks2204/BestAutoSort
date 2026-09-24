using System;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Runtime;

/// <summary>
/// Wave-1 server-authority migration (experimental vertical slice, NOT a
/// completed release — the invariant is NOT claimed yet).
///
/// In ServerAuthority mode the server/host (the authority UID) owns every
/// positively classified chest ZDO; vanilla ownership transfer paths are
/// guarded so ownership never moves. Classification is Independent of
/// IsShared/lease/range/toggles by construction. LegacyDistributed mode keeps
/// every legacy path untouched.
///
/// Authority UID: ZNet.GetUID() on dedicated/host/single-player (where it
/// equals ZDOMan.GetSessionID()); GetServerPeer().m_uid on remote clients.
/// Missing net layer / missing server peer => uid 0 (unknown authority,
/// fail closed: claim/repair nothing, move nothing).
/// </summary>
internal static class ServerAuthority
{
    /// <summary>Successful server-side ownership repairs (adopt-to-authority).</summary>
    internal static long ServerOwnershipRepairs;

    /// <summary>Eligible chests observed owned by (or moving toward) a non-authority uid.</summary>
    internal static long UnexpectedClientOwnership;

    /// <summary>Failed ownership repairs (claim threw or ownership did not hold).</summary>
    internal static long OwnershipRepairFailures;

    internal static ServerAuthorityMode EffectiveMode()
    {
        try
        {
            return ModConfig.ChestAuthorityMode.Value;
        }
        catch
        {
            // Config unavailable: stay legacy (claim/move nothing).
            return ServerAuthorityMode.LegacyDistributed;
        }
    }

    internal static bool IsAuthorityMode()
    {
        try
        {
            return EffectiveMode() == ServerAuthorityMode.ServerAuthority;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The chest authority UID for this peer: ZNet.GetUID() when server
    /// (dedicated/host/single — equals ZDOMan.GetSessionID()), the server
    /// peer uid when a remote client, 0 when unknown (fail closed).
    /// </summary>
    internal static long AuthorityUid()
    {
        try
        {
            ZNet net = ZNet.instance;
            if ((Object)net == (Object)null)
                return 0L;
            if (net.IsServer())
            {
                try
                {
                    return ZNet.GetUID();
                }
                catch
                {
                    return 0L;
                }
            }
            ZNetPeer serverPeer;
            try
            {
                serverPeer = net.GetServerPeer();
            }
            catch
            {
                return 0L;
            }
            if (serverPeer == null)
                return 0L;
            return serverPeer.m_uid;
        }
        catch
        {
            return 0L;
        }
    }

    /// <summary>
    /// Canonical Container classification (wave 1): stationary supported
    /// storage IN; Player / TombStone / wagon / ship / moving / temporary /
    /// special OUT; unknown NOT eligible (fail closed). Independent of
    /// IsShared/lease/range/toggles: none of those are consulted.
    /// </summary>
    internal static bool IsServerManagedContainer(Container container)
    {
        try
        {
            if (!IsAuthorityMode())
                return false;
            if ((Object)container == (Object)null)
                return false;
            if (!((Behaviour)container).isActiveAndEnabled)
                return false;
            if ((Object)container.m_wagon != (Object)null)
                return false;
            if ((Object)((Component)container).GetComponentInParent<Ship>() != (Object)null)
                return false;
            if ((Object)((Component)container).GetComponent<Player>() != (Object)null)
                return false;
            if ((Object)((Component)container).GetComponent<TombStone>() != (Object)null)
                return false;
            Piece piece = ((Component)container).GetComponent<Piece>()
                ?? ((Component)container).GetComponentInParent<Piece>();
            if ((Object)piece == (Object)null || !piece.IsPlacedByPlayer())
                return false;
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return false;
            ZDO zdo;
            try
            {
                zdo = netView.GetZDO();
            }
            catch
            {
                return false;
            }
            if (zdo == null)
                return false;
            return IsServerManagedChestZdo(zdo);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Same-rule ZDO/prefab policy for Container-less paths (ownership guards,
    /// redistribution exclusion, incoming revision validation): the prefab must
    /// carry a Container component and its name must pass the shared pure rule
    /// (TxServerAuthorityPolicy — pinned by AUTHORITY_* tests). Unknown prefab
    /// (no scene, no prefab, no name) is NOT eligible (fail closed).
    /// </summary>
    internal static bool IsServerManagedChestZdo(ZDO zdo)
    {
        try
        {
            if (!IsAuthorityMode())
                return false;
            if (zdo == null)
                return false;
            ZNetScene scene = ZNetScene.instance;
            if ((Object)scene == (Object)null)
                return false;
            GameObject prefab;
            try
            {
                prefab = scene.GetPrefab(zdo.GetPrefab());
            }
            catch
            {
                return false;
            }
            if ((Object)prefab == (Object)null)
                return false;
            if ((Object)prefab.GetComponent<Container>() == (Object)null)
                return false;
            return TxServerAuthorityPolicy.IsEligiblePrefabName(prefab.name);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wave-2 manager routing: who is the chest manager for this chest on
    /// this peer. LegacyDistributed mode (or a chest that is NOT
    /// server-managed) returns the vanilla IsOwner verdict bit-for-bit.
    /// ServerAuthority mode + server-managed chest is true ONLY on the
    /// dedicated/server authority itself (this peer IS the server process
    /// AND the chest ZDO is owned by the authority UID); remote clients are
    /// ALWAYS false — a remote viewer is never the manager and must never
    /// fall back to direct vanilla mutation (fail closed where unroutable).
    /// Unknown authority (uid 0) manages nothing. Never throws.
    ///
    /// Same rule as the pure TxAuthorityRouting.IsAuthorityManager pinned by
    /// AUTHORITY_Manager* tests (signature takes live game state).
    /// </summary>
    internal static bool IsAuthorityManager(Container container)
    {
        try
        {
            if ((Object)container == (Object)null)
                return false;
            if (!IsAuthorityMode())
                return container.IsOwner();
            if (!IsServerManagedContainer(container))
                return container.IsOwner();
            ZNet net = ZNet.instance;
            if ((Object)net == (Object)null || !net.IsServer())
                return false;
            long auth = AuthorityUid();
            if (auth == 0L)
                return false;
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return false;
            long owner = 0L;
            try
            {
                owner = netView.GetZDO().GetOwner();
            }
            catch
            {
                return false;
            }
            return owner == auth;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Local-server probe for TxAuthorityRouting seams: true only when this
    /// peer IS the server process. Unknown/null net reads as false (fail
    /// closed: callers deny). Never throws.
    /// </summary>
    internal static bool IsLocalServer()
    {
        try
        {
            ZNet net = ZNet.instance;
            return (Object)net != (Object)null && net.IsServer();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wave-2 fail-closed gate for direct-mutation fallbacks: under
    /// ServerAuthority a NON-SERVER peer must never run vanilla mutations
    /// against a managed chest (its replica is stale by construction).
    /// True = the caller must block loudly (message + log, no mutation, no
    /// ownership claim). Legacy mode and unmanaged chests return false.
    /// Decision rule is TxAuthorityRouting.RemoteMustNotMutate (single rule).
    /// </summary>
    internal static bool DenyRemoteDirectMutation(Container container, string op)
    {
        try
        {
            if ((Object)container == (Object)null)
                return false;
            // Single rule: the pure TxAuthorityRouting.RemoteMustNotMutate pinned
            // by AUTHORITY_RemoteNeverMutates. Reverting the seam changes this
            // wrapper's verdict by construction (verified by inspection).
            if (!TxAuthorityRouting.RemoteMustNotMutate(IsAuthorityMode(), IsServerManagedContainer(container), IsLocalServer()))
                return false;
            try
            {
                Player player = Player.m_localPlayer;
                if ((Object)player != (Object)null)
                {
                    ((Character)player).Message((MessageType)2,
                        "This chest is server-managed: the operation was refused safely. Try again.", 0, (Sprite)null, false);
                }
            }
            catch
            {
            }
            try
            {
                Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: remote direct mutation refused"
                    + " op=" + op
                    + " (fail closed: use ChestTX, never vanilla)"));
            }
            catch
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wave-2 structural gate (upgrade: destroy + recreate): a non-manager
    /// must fail closed with a loud error — no client AcquireForStructural,
    /// no replacement ClaimOwnership. The manager (host/server-local, already
    /// authority) proceeds unchanged. Legacy/unmanaged chests return false
    /// (caller keeps its legacy verdict).
    /// Decision rule is TxAuthorityRouting.CanStructuralUpgrade (single rule:
    /// block == NOT-may-proceed, with legacy-verdict-true so legacy mode and
    /// unmanaged chests never block). Reverting the seam changes this wrapper
    /// by construction (verified by inspection).
    /// </summary>
    internal static bool BlockStructuralForRemote(Container container, string op)
    {
        try
        {
            if ((Object)container == (Object)null)
                return false;
            if (TxAuthorityRouting.CanStructuralUpgrade(IsAuthorityMode(), IsServerManagedContainer(container), IsAuthorityManager(container), true))
                return false;
            try
            {
                Player player = Player.m_localPlayer;
                if ((Object)player != (Object)null)
                {
                    ((Character)player).Message((MessageType)2,
                        "This chest is server-managed and cannot be upgraded from here.", 0, (Sprite)null, false);
                }
            }
            catch
            {
            }
            try
            {
                Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: structural " + op
                    + " refused for non-manager (fail closed: no acquire, no claim)"));
            }
            catch
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Redistribution-exclusion predicate (ZDOMan.ReleaseNearbyZDOS transpiler):
    /// true = skip this ZDO, keep current (server) ownership. Scoped to
    /// positively classified chests only; unknown => false (legacy path).
    /// </summary>
    internal static bool ShouldKeepServerOwnership(ZDO zdo)
    {
        try
        {
            return IsServerManagedChestZdo(zdo);
        }
        catch
        {
            return false;
        }
    }

    internal static string DescribeZdo(ZDO zdo)
    {
        try
        {
            if (zdo == null)
                return "?";
            string prefabName = "?";
            try
            {
                ZNetScene scene = ZNetScene.instance;
                if ((Object)scene != (Object)null)
                {
                    GameObject prefab = scene.GetPrefab(zdo.GetPrefab());
                    if ((Object)prefab != (Object)null)
                        prefabName = prefab.name;
                }
            }
            catch
            {
            }
            long owner = 0L;
            try
            {
                owner = zdo.GetOwner();
            }
            catch
            {
            }
            return TxLog.Zid(zdo.m_uid) + " prefab=" + prefabName + " owner=" + owner;
        }
        catch
        {
            return "?";
        }
    }

    internal static void NoteUnexpectedClientOwnership(ZDO zdo, long newUid, string site)
    {
        try
        {
            UnexpectedClientOwnership++;
            long auth = AuthorityUid();
            Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: blocked ownership change"
                + " site=" + site
                + " chest=" + DescribeZdo(zdo)
                + " new=" + newUid + " authority=" + auth
                + " (total unexpected=" + UnexpectedClientOwnership + ")"));
        }
        catch
        {
        }
    }

    internal static void NoteBlockedClaim(Container container, long selfUid, string site)
    {
        try
        {
            UnexpectedClientOwnership++;
            string zid = "?";
            try
            {
                ZNetView nv = TxReflect.GetNetView(container);
                if ((Object)nv != (Object)null && nv.IsValid())
                    zid = TxLog.Zid(nv.GetZDO().m_uid);
            }
            catch
            {
            }
            Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: blocked ClaimOwnership"
                + " site=" + site
                + " chest=" + zid
                + " self=" + selfUid + " authority=" + AuthorityUid()
                + " (total unexpected=" + UnexpectedClientOwnership + ")"));
        }
        catch
        {
        }
    }

    /// <summary>
    /// Server-only ownership repair (metadata-only change: ownership, never
    /// inventory). Called on attach/discovery (awake + slow pump) and before
    /// the first authority op. Reads persisted s_items + ring/floor BEFORE any
    /// mutation; missing/corrupt/untrusted state quarantines (never presumed
    /// empty). New server-created chests may materialize an empty s_items blob
    /// ONLY in the verified awake/init context (see EnsureOnAwake) — never
    /// timer-based. Logs ZDOID/old/new/reason + counters.
    /// </summary>
    internal static void EnsureServerOwnership(Container container, string reason)
    {
        try
        {
            if (!IsAuthorityMode())
                return;
            ZNet net = ZNet.instance;
            if ((Object)net == (Object)null || !net.IsServer())
                return;
            if ((Object)container == (Object)null)
                return;
            if (!IsServerManagedContainer(container))
                return;
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            ZDO zdo;
            try
            {
                zdo = netView.GetZDO();
            }
            catch
            {
                return;
            }
            if (zdo == null)
                return;
            long me;
            try
            {
                me = ZNet.GetUID();
            }
            catch
            {
                return;
            }
            if (me == 0L)
                return;
            long owner = 0L;
            try
            {
                owner = zdo.GetOwner();
            }
            catch
            {
            }
            if (owner == me)
                return;
            // Read persisted state BEFORE mutations (reads are not owner-gated):
            // s_items bytes plus the ring/floor copies. This method itself never
            // mutates inventory (metadata-only ownership change); the post-claim
            // Takeover owns the authoritative reseed.
            byte[] items = null;
            try
            {
                items = TxSItemsGuard.CloneBytes(zdo.GetByteArray(ZDOVars.s_items));
            }
            catch
            {
                items = null;
            }
            bool untrusted = items == null;
            string validateReason = "ok";
            if (!untrusted && !TxSItemsGuard.TryValidate(items, out validateReason))
                untrusted = true;
            string ringState = "absent";
            try
            {
                byte[] ringBytes = zdo.GetByteArray(ChestTxService.RingKey.GetStableHashCode());
                byte[] floorBytes = zdo.GetByteArray(ChestTxService.FloorKey.GetStableHashCode());
                RingData ring = ringBytes != null ? TxCore.TxCore.DecodeEnvelope(ringBytes) : null;
                FloorData floor = null;
                if (floorBytes != null)
                {
                    floor = TxCore.TxCore.DecodeFloor(floorBytes);
                    floor.Present = true;
                }
                if (ring == null)
                {
                    ringState = "absent";
                }
                else if (!ring.Corrupt)
                {
                    ringState = "ok";
                }
                else if (floor != null && floor.Present && !floor.Corrupt)
                {
                    ringState = "corrupt(floor-ok)";
                }
                else
                {
                    ringState = "corrupt(no-floor)";
                    untrusted = true;
                }
            }
            catch
            {
                ringState = "unreadable";
                untrusted = true;
            }
            if (untrusted)
            {
                // Quarantine missing/corrupt/untrusted state (never presumed
                // empty). The post-claim Takeover re-reads authoritatively and
                // keeps the quarantine until an authoritative reload succeeds.
                // Wave-3: the timer-based null-escape legs NEVER heal here
                // (EscapeAllowed is false for managed chests — no timer exit,
                // quarantine persists with a loud diagnose).
                UnexpectedClientOwnership++;
                ChestState state = ChestTxService.GetState(container);
                if (state != null)
                {
                    state.TxQuarantined = true;
                    state.QuarantinedSince = Time.realtimeSinceStartup;
                    state.QuarantineOldOwner = owner;
                    state.QuarantineWarns = 0;
                }
                Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: untrusted contents"
                    + " chest=" + DescribeZdo(zdo)
                    + " s_items=" + (items == null ? "null" : "invalid(" + validateReason + ")")
                    + " ring=" + ringState
                    + " quarantined (reason=" + reason + ")"));
            }
            long oldOwner = owner;
            try
            {
                netView.ClaimOwnership();
            }
            catch (Exception ex)
            {
                OwnershipRepairFailures++;
                Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: repair claim failed"
                    + " chest=" + DescribeZdo(zdo)
                    + " old=" + oldOwner + " new=" + me
                    + " reason=" + reason + ": " + ex.Message));
                return;
            }
            bool owned = false;
            try
            {
                owned = container.IsOwner();
            }
            catch
            {
            }
            if (!owned)
            {
                OwnershipRepairFailures++;
                Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: repair claim did not hold"
                    + " chest=" + DescribeZdo(zdo)
                    + " old=" + oldOwner + " new=" + me
                    + " reason=" + reason));
                return;
            }
            ServerOwnershipRepairs++;
            if (oldOwner != me)
                UnexpectedClientOwnership++;
            Plugin.LogInstance.LogInfo((object)("[ChestTX] server-authority: ownership repaired"
                + " chest=" + DescribeZdo(zdo)
                + " old=" + oldOwner + " new=" + me
                + " reason=" + reason
                + " untrusted=" + untrusted
                + " (repairs=" + ServerOwnershipRepairs
                + " unexpected=" + UnexpectedClientOwnership
                + " failures=" + OwnershipRepairFailures + ")"));
            // Reseed (ring/floor + authoritative s_items reload) is owned by the
            // existing Takeover path: the slow pump observes owner==me with a
            // changed LastOwner and reseeds before any mutation (server-side
            // only in ServerAuthority mode — the pump Takeover gate refuses
            // remote adoption); the tx path reseeds via Takeover-before-Drain.
            // This method never mutates inventory itself.
        }
        catch (Exception ex)
        {
            try
            {
                OwnershipRepairFailures++;
                Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: repair fault: " + ex.Message));
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Awake/discovery path (server-only). Same repair as EnsureServerOwnership,
    /// PLUS the single verified-init exception: a NEW server-created chest
    /// (self-owned at awake, s_items absent, live RAM provably empty) may
    /// materialize the empty s_items blob via SaveContainer. Timer-based paths
    /// NEVER materialize (fail closed there).
    /// </summary>
    internal static void EnsureOnAwake(Container container)
    {
        try
        {
            if (!IsAuthorityMode())
                return;
            ZNet net = ZNet.instance;
            if ((Object)net == (Object)null || !net.IsServer())
                return;
            if ((Object)container == (Object)null)
                return;
            if (!IsServerManagedContainer(container))
                return;
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            ZDO zdo;
            try
            {
                zdo = netView.GetZDO();
            }
            catch
            {
                return;
            }
            if (zdo == null)
                return;
            long me;
            try
            {
                me = ZNet.GetUID();
            }
            catch
            {
                return;
            }
            if (me == 0L)
                return;
            long owner = 0L;
            try
            {
                owner = zdo.GetOwner();
            }
            catch
            {
            }
            byte[] items = null;
            // Read success is tracked separately from a null result: a throw
            // means UNKNOWN persisted state (never "verified absent"). Only
            // a successful read returning null may enter the empty-blob
            // materialization below; a failed read must take the repair path.
            bool itemsReadOk = false;
            try
            {
                items = TxSItemsGuard.CloneBytes(zdo.GetByteArray(ZDOVars.s_items));
                itemsReadOk = true;
            }
            catch
            {
                items = null;
                itemsReadOk = false;
            }
            // Verified init context ONLY (Wave-3: the single exception to the
            // authority-mode no-heal rule — server, self-owned at awake,
            // s_items absent, live RAM provably empty — pinned by
            // AUTHORITY_VerifiedInitStillMaterializes against the SAME pure
            // predicate consulted here). Bound to live values: authority mode
            // is recomputed via IsAuthorityMode() (never assumed) and managed
            // is established by the IsServerManagedContainer guard above, so a
            // future guard edit cannot silently diverge predicate context.
            // Materialize the present empty blob and verify it. Anything else
            // (foreign owner, non-empty RAM, failed verify) takes the repair
            // path. Timer-based paths NEVER materialize (fail closed there).
            bool authorityMode = IsAuthorityMode();
            bool managed = true;
            bool ramEmpty = false;
            try
            {
                Inventory inv = container.GetInventory();
                ramEmpty = inv != null && inv.GetAllItems().Count == 0;
            }
            catch
            {
                ramEmpty = false;
            }
            if (!itemsReadOk)
            {
                // Persisted state is UNKNOWN (read failed) — never materialize
                // empty over it. Fail closed via the repair path below.
                Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: init s_items read failed"
                    + " chest=" + DescribeZdo(zdo) + ", taking repair path (never presume empty)"));
                EnsureServerOwnership(container, "awake-read-failed");
                return;
            }
            if (TxNullEscape.InitMaterializeAllowed(authorityMode, managed, owner == me, items == null, ramEmpty))
            {
                try
                {
                    TxReflect.SaveContainer(container);
                }
                catch (Exception ex)
                {
                    OwnershipRepairFailures++;
                    Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: init materialize save failed"
                        + " chest=" + DescribeZdo(zdo) + ": " + ex.Message));
                    EnsureServerOwnership(container, "awake-init-save-failed");
                    return;
                }
                byte[] healed = null;
                try
                {
                    healed = TxSItemsGuard.CloneBytes(zdo.GetByteArray(ZDOVars.s_items));
                }
                catch
                {
                    healed = null;
                }
                string healReason = "?";
                if (healed != null && TxSItemsGuard.TryValidate(healed, out healReason))
                {
                    ServerOwnershipRepairs++;
                    Plugin.LogInstance.LogInfo((object)("[ChestTX] server-authority: init materialized empty s_items"
                        + " chest=" + DescribeZdo(zdo)));
                    return;
                }
                OwnershipRepairFailures++;
                Plugin.LogInstance.LogWarning((object)("[ChestTX] server-authority: init materialize unverified"
                    + " chest=" + DescribeZdo(zdo)
                    + " reason=" + healReason));
            }
            EnsureServerOwnership(container, "awake");
        }
        catch
        {
        }
    }

    /// <summary>
    /// View-only gate for eligible chests outside the shared tx path
    /// (concurrent use / lease off): the owner keeps vanilla behavior, but a
    /// non-owner must never run vanilla local mutations against a stale
    /// replica (ghost items). True = caller must block loudly.
    /// Scoped to positively classified chests only.
    /// </summary>
    internal static bool DenyIfViewOnly(Container container, string op)
    {
        try
        {
            if (!IsAuthorityMode())
                return false;
            if ((Object)container == (Object)null)
                return false;
            if (!IsServerManagedContainer(container))
                return false;
            if (ChestTxService.IsShared(container))
                return false;
            bool owner = false;
            try
            {
                owner = container.IsOwner();
            }
            catch
            {
            }
            if (owner)
                return false;
            try
            {
                Player player = Player.m_localPlayer;
                if ((Object)player != (Object)null)
                {
                    ((Character)player).Message((MessageType)2,
                        "Chest is not in a shared session: viewing only.", 0, (Sprite)null, false);
                }
            }
            catch
            {
            }
            try
            {
                Plugin.LogInstance.LogInfo((object)("[ChestTX] server-authority: view-only deny"
                    + " op=" + op));
            }
            catch
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
