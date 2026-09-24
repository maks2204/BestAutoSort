using System;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Pure chest-manager routing decisions (server-authority migration, wave 2).
    /// No Unity/Valheim refs, so the offline suite pins the SAME rule the game
    /// uses when it swaps every IsOwner-as-manager branch for chest paths.
    ///
    /// Rule:
    /// - LegacyDistributed mode, or a chest that is NOT server-managed: the
    ///   caller-supplied legacy verdict (vanilla IsOwner) is returned bit-for-bit.
    /// - ServerAuthority mode + server-managed chest: true ONLY for the
    ///   dedicated/server authority itself (local peer IS the server process AND
    ///   the chest ZDO is owned by the authority UID). Remote clients are ALWAYS
    ///   false — a remote viewer is never the manager and must never fall back
    ///   to direct vanilla mutation (fail closed where unroutable).
    /// - Unknown authority (uid 0): false (fail closed: claim/manage nothing).
    ///
    /// Preserved (not widened here): actor-first auth, canonical keys,
    /// ring/floor, quarantine, LegacyDistributed behavior bit-for-bit.
    /// </summary>
    public static class TxAuthorityRouting
    {
        /// <summary>
        /// Who is the chest manager for this chest on this peer.
        /// </summary>
        public static bool IsAuthorityManager(
            bool authorityMode,
            bool managed,
            bool localIsServer,
            long zdoOwner,
            long authorityUid,
            bool legacyIsOwner)
        {
            if (!authorityMode)
                return legacyIsOwner;
            if (!managed)
                return legacyIsOwner;
            if (authorityUid == 0L)
                return false;
            if (!localIsServer)
                return false;
            return zdoOwner == authorityUid;
        }

        /// <summary>
        /// Wave-3 Takeover gate: who may reseed as the new manager. Legacy /
        /// unmanaged chests keep the legacy verdict (Takeover always allowed —
        /// the distributed handoff). A ServerAuthority managed chest may be
        /// taken over ONLY by the server itself (the initial adoption reseed
        /// after EnsureServerOwnership): a remote peer never takes over a
        /// managed chest — there is no client handoff in ServerAuthority mode.
        /// The pump advances LastOwner regardless, so a refused tripwire does
        /// not spin. Never throws (pure bools).
        /// </summary>
        public static bool TakeoverAllowed(bool authorityMode, bool managed, bool localIsServer)
        {
            if (!authorityMode)
                return true;
            if (!managed)
                return true;
            return localIsServer;
        }

        /// <summary>
        /// Remote peers must never run direct vanilla mutations against a
        /// managed chest (their replica is stale by construction). True = the
        /// caller must fail closed (loud deny, no mutation, no ownership claim).
        /// </summary>
        public static bool RemoteMustNotMutate(bool authorityMode, bool managed, bool localIsServer)
        {
            return authorityMode && managed && !localIsServer;
        }

        /// <summary>
        /// Stale-route discipline for owner-routed RPCs (owner == authority):
        /// only the manager answers. A non-manager drops WITHOUT fabricating a
        /// terminal outcome (no UnknownTx/Rejected answer: the sender's pump
        /// resends/queries the SAME txId, which routes to the current owner).
        /// True = drop silently (no answer).
        /// </summary>
        public static bool ShouldDropStaleRoute(bool isManager)
        {
            return !isManager;
        }

        /// <summary>
        /// Structural operations (upgrade: destroy + recreate) under
        /// ServerAuthority. A non-manager must fail closed with a loud error:
        /// no client AcquireForStructural, no replacement ClaimOwnership.
        /// The manager (host/server-local, already authority) proceeds.
        /// Legacy/unmanaged chests defer to the caller's legacy verdict.
        /// True = the structural op may proceed on this peer.
        /// </summary>
        public static bool CanStructuralUpgrade(
            bool authorityMode,
            bool managed,
            bool isManager,
            bool legacyVerdict)
        {
            if (!authorityMode)
                return legacyVerdict;
            if (!managed)
                return legacyVerdict;
            return isManager;
        }
    }
}
