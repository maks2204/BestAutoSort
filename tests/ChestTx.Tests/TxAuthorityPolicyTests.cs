using System;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Wave-1 server-authority policy tests: all against PRODUCTION code
    /// (TxServerAuthorityPolicy / TxAuthorityHello in BestAutoSort.TxCore —
    /// the same functions the game calls for Container and Container-less
    /// classification plus Hello/mode compat negotiation).
    ///
    /// Preserved (not widened here): actor-first auth, canonical keys,
    /// ring/floor, quarantine, LegacyDistributed paths.
    /// </summary>
    internal static class TxAuthorityPolicyTests
    {
        public static void RunAll()
        {
            Console.WriteLine("AUTHORITY_StationaryChestsEligible");
            AUTHORITY_StationaryChestsEligible();
            Console.WriteLine("AUTHORITY_PlayerExcluded");
            AUTHORITY_PlayerExcluded();
            Console.WriteLine("AUTHORITY_TombstoneExcluded");
            AUTHORITY_TombstoneExcluded();
            Console.WriteLine("AUTHORITY_MovingExcluded");
            AUTHORITY_MovingExcluded();
            Console.WriteLine("AUTHORITY_UnknownFailClosed");
            AUTHORITY_UnknownFailClosed();
            Console.WriteLine("AUTHORITY_CaseRules");
            AUTHORITY_CaseRules();
            Console.WriteLine("AUTHORITY_DenylistBeatsAllowlist");
            AUTHORITY_DenylistBeatsAllowlist();
            Console.WriteLine("AUTHORITY_ModeCompatMatrix");
            AUTHORITY_ModeCompatMatrix();
            Console.WriteLine("AUTHORITY_HelloNegotiation");
            AUTHORITY_HelloNegotiation();
            Console.WriteLine("AUTHORITY_HelloLegacyFallback");
            AUTHORITY_HelloLegacyFallback();
            Console.WriteLine("AUTHORITY_ManagerAuthorityMatrix");
            AUTHORITY_ManagerAuthorityMatrix();
            Console.WriteLine("AUTHORITY_ManagerLegacyBitForBit");
            AUTHORITY_ManagerLegacyBitForBit();
            Console.WriteLine("AUTHORITY_ViewerNeverManager");
            AUTHORITY_ViewerNeverManager();
            Console.WriteLine("AUTHORITY_StaleRouteDrop");
            AUTHORITY_StaleRouteDrop();
            Console.WriteLine("AUTHORITY_UpgradeFailClosed");
            AUTHORITY_UpgradeFailClosed();
            Console.WriteLine("AUTHORITY_RemoteNeverMutates");
            AUTHORITY_RemoteNeverMutates();
            Console.WriteLine("AUTHORITY_EscapeDisabledInAuthorityMode");
            AUTHORITY_EscapeDisabledInAuthorityMode();
            Console.WriteLine("AUTHORITY_EscapeKeptInLegacy");
            AUTHORITY_EscapeKeptInLegacy();
            Console.WriteLine("AUTHORITY_QuarantinePersistsOnNullAuthority");
            AUTHORITY_QuarantinePersistsOnNullAuthority();
            Console.WriteLine("AUTHORITY_VerifiedInitStillMaterializes");
            AUTHORITY_VerifiedInitStillMaterializes();
            Console.WriteLine("AUTHORITY_TakeoverGateNeverRemote");
            AUTHORITY_TakeoverGateNeverRemote();
        }

        /// <summary>
        /// Stationary supported storage is IN: the compact-tier prefabs the
        /// upgrade service manages plus the shared prefix rule. The rule takes
        /// ONLY the prefab name (independent of IsShared/lease/range/toggles
        /// by construction — no such parameter exists).
        /// </summary>
        private static void AUTHORITY_StationaryChestsEligible()
        {
            Check.That(TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_wood"), "wood chest eligible");
            Check.That(TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest"), "reinforced chest eligible");
            Check.That(TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_blackmetal"), "blackmetal chest eligible");
            Check.That(TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_grausten"), "grausten chest eligible");
            Check.That(TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_stone"), "prefixed storage eligible");
        }

        /// <summary>Player-bound storage is OUT (never server-managed).</summary>
        private static void AUTHORITY_PlayerExcluded()
        {
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Player"), "Player out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("player_chest"), "player chest out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_player"), "prefixed player out (denylist wins)");
        }

        /// <summary>TombStone storage is OUT (never server-managed).</summary>
        private static void AUTHORITY_TombstoneExcluded()
        {
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("TombStone"), "TombStone out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_tomb"), "prefixed tomb out (denylist wins)");
        }

        /// <summary>
        /// Wagon/ship/moving/special storage is OUT — vanilla names and
        /// prefixed hypotheticals alike (denylist beats the allowlist).
        /// </summary>
        private static void AUTHORITY_MovingExcluded()
        {
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Cart"), "Cart out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Vagon"), "Vagon out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Karve"), "Karve out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("VikingShip"), "VikingShip out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Boat"), "Boat out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Raft"), "Raft out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_cart"), "prefixed cart out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_ship"), "prefixed ship out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_wagon"), "prefixed wagon out");
        }

        /// <summary>
        /// Unknown prefabs are NOT eligible (fail closed): null, empty, world
        /// loot chests, production stations and foreign-mod names all read
        /// false, so unclassified objects keep legacy behavior instead of
        /// entering the new regime.
        /// </summary>
        private static void AUTHORITY_UnknownFailClosed()
        {
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName(null), "null out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName(string.Empty), "empty out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Chest"), "dungeon loot chest out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("TreasureChest"), "treasure chest out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Smelter"), "station out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("MyMod_MegaChest"), "foreign mod out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Piece_Chest"), "wrong-case prefix out");
        }

        /// <summary>
        /// Case rules: the allowlist prefix is case-sensitive (Valheim prefab
        /// names are lowercase by convention); the denylist is
        /// case-insensitive. A wrong-case prefix never slips into the regime.
        /// </summary>
        private static void AUTHORITY_CaseRules()
        {
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("PIECE_CHEST_WOOD"), "all-caps prefix out");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("Piece_Chest"), "mixed-case prefix out");
            Check.That(TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_Wood"), "suffix case irrelevant");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_TOMB"), "denylist case-insensitive");
            Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_Ship"), "denylist case-insensitive (ship)");
        }

        /// <summary>
        /// Precedence pin: the denylist beats the allowlist. Any future
        /// moving/special chest that ever carries the storage prefix stays OUT.
        /// </summary>
        private static void AUTHORITY_DenylistBeatsAllowlist()
        {
            foreach (string bad in TxServerAuthorityPolicy.ExcludedSubstrings)
            {
                Check.That(!TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_" + bad),
                    "prefixed '" + bad + "' out");
            }
            Check.That(TxServerAuthorityPolicy.IsEligiblePrefabName("piece_chest_harbourmaster"),
                "non-denylisted suffix stays in");
        }

        /// <summary>
        /// Mode compat matrix: same version + same mode ONLY. Version skew and
        /// mode skew are both incompatible (mismatched peers rejected).
        /// </summary>
        private static void AUTHORITY_ModeCompatMatrix()
        {
            Check.That(TxAuthorityHello.AreCompatible("0.5.16", ServerAuthorityMode.ServerAuthority,
                "0.5.16", ServerAuthorityMode.ServerAuthority), "same/same compatible");
            Check.That(TxAuthorityHello.AreCompatible("0.5.16", ServerAuthorityMode.LegacyDistributed,
                "0.5.16", ServerAuthorityMode.LegacyDistributed), "legacy/legacy compatible");
            Check.That(!TxAuthorityHello.AreCompatible("0.5.16", ServerAuthorityMode.ServerAuthority,
                "0.5.16", ServerAuthorityMode.LegacyDistributed), "mode skew rejected");
            Check.That(!TxAuthorityHello.AreCompatible("0.5.16", ServerAuthorityMode.LegacyDistributed,
                "0.5.16", ServerAuthorityMode.ServerAuthority), "mode skew rejected (mirror)");
            Check.That(!TxAuthorityHello.AreCompatible("0.5.16", ServerAuthorityMode.ServerAuthority,
                "0.5.15", ServerAuthorityMode.ServerAuthority), "version skew rejected");
            Check.That(!TxAuthorityHello.AreCompatible("0.5.16", ServerAuthorityMode.ServerAuthority,
                "0.5.15", ServerAuthorityMode.LegacyDistributed), "both skew rejected");
        }

        /// <summary>
        /// Hello wire round-trip: BuildHello carries version + mode, TryParse
        /// recovers them exactly, garbage/legacy strings do NOT parse (they
        /// take the legacy fallback, never a structured accept).
        /// </summary>
        private static void AUTHORITY_HelloNegotiation()
        {
            string hello = TxAuthorityHello.BuildHello("0.5.16", ServerAuthorityMode.ServerAuthority);
            string v;
            ServerAuthorityMode m;
            Check.That(TxAuthorityHello.TryParseHello(hello, out v, out m), "own hello parses");
            Check.That(v == "0.5.16" && m == ServerAuthorityMode.ServerAuthority, "own hello round-trips");
            string legacyMode = TxAuthorityHello.BuildHello("0.5.16", ServerAuthorityMode.LegacyDistributed);
            Check.That(TxAuthorityHello.TryParseHello(legacyMode, out v, out m)
                && m == ServerAuthorityMode.LegacyDistributed, "legacy-mode hello round-trips");
            Check.That(!TxAuthorityHello.TryParseHello("0.5.16", out v, out m), "bare version is legacy (no parse)");
            Check.That(!TxAuthorityHello.TryParseHello(string.Empty, out v, out m), "empty never parses");
            Check.That(!TxAuthorityHello.TryParseHello(null, out v, out m), "null never parses");
            Check.That(!TxAuthorityHello.TryParseHello("0.5.16;auth=7", out v, out m), "unknown mode never parses");
            Check.That(!TxAuthorityHello.TryParseHello("0.5.16;auth=", out v, out m), "empty mode never parses");
            Check.That(!TxAuthorityHello.TryParseHello(";auth=0", out v, out m), "empty version never parses");
        }

        /// <summary>
        /// Wire-level compat with the legacy fallback: structured hellos use
        /// the matrix; a legacy bare-version hello is accepted ONLY by a
        /// LegacyDistributed peer with an exact version match — a
        /// ServerAuthority peer rejects legacy peers (and everything unknown).
        /// </summary>
        private static void AUTHORITY_HelloLegacyFallback()
        {
            string modern = TxAuthorityHello.BuildHello("0.5.16", ServerAuthorityMode.ServerAuthority);
            Check.That(TxAuthorityHello.AreCompatibleWithHello("0.5.16",
                ServerAuthorityMode.ServerAuthority, modern), "modern/modern compatible");
            string modernLegacy = TxAuthorityHello.BuildHello("0.5.16", ServerAuthorityMode.LegacyDistributed);
            Check.That(!TxAuthorityHello.AreCompatibleWithHello("0.5.16",
                ServerAuthorityMode.ServerAuthority, modernLegacy), "modern mode-skew rejected");
            Check.That(TxAuthorityHello.AreCompatibleWithHello("0.5.16",
                ServerAuthorityMode.LegacyDistributed, "0.5.16"), "legacy peer accepted by legacy mode");
            Check.That(!TxAuthorityHello.AreCompatibleWithHello("0.5.16",
                ServerAuthorityMode.ServerAuthority, "0.5.16"), "legacy peer rejected by authority mode");
            Check.That(!TxAuthorityHello.AreCompatibleWithHello("0.5.16",
                ServerAuthorityMode.LegacyDistributed, "0.5.15"), "legacy version skew rejected");
            Check.That(!TxAuthorityHello.AreCompatibleWithHello("0.5.16",
                ServerAuthorityMode.ServerAuthority, "garbage"), "garbage rejected");
        }

        /// <summary>
        /// Wave-2 manager matrix (production TxAuthorityRouting): a managed chest
        /// is managed ONLY by the server authority itself (local server peer +
        /// ZDO owned by the authority UID). Every other combination reads false.
        /// </summary>
        private static void AUTHORITY_ManagerAuthorityMatrix()
        {
            const long auth = 1001L;
            Check.That(TxAuthorityRouting.IsAuthorityManager(true, true, true, auth, auth, false),
                "server + owned-by-authority is manager");
            Check.That(!TxAuthorityRouting.IsAuthorityManager(true, true, true, 555L, auth, true),
                "server but owned-by-client is not manager (legacy IsOwner ignored)");
            Check.That(!TxAuthorityRouting.IsAuthorityManager(true, true, true, 0L, auth, false),
                "server but unowned ZDO is not manager");
            Check.That(!TxAuthorityRouting.IsAuthorityManager(true, true, false, auth, auth, true),
                "remote client is never manager even when ZDO names the authority");
            Check.That(!TxAuthorityRouting.IsAuthorityManager(true, true, false, 555L, auth, true),
                "remote client owning the ZDO is still not manager");
            Check.That(!TxAuthorityRouting.IsAuthorityManager(true, true, true, auth, 0L, true),
                "unknown authority (uid 0) manages nothing");
            Check.That(!TxAuthorityRouting.IsAuthorityManager(true, true, false, 555L, 0L, true),
                "unknown authority on remote manages nothing");
        }

        /// <summary>
        /// LegacyDistributed mode and unmanaged chests return the vanilla IsOwner
        /// verdict bit-for-bit (both polarities): the refactor changes nothing
        /// outside the new regime.
        /// </summary>
        private static void AUTHORITY_ManagerLegacyBitForBit()
        {
            Check.That(TxAuthorityRouting.IsAuthorityManager(false, true, true, 1001L, 1001L, true),
                "legacy mode keeps owner=true");
            Check.That(!TxAuthorityRouting.IsAuthorityManager(false, true, true, 1001L, 1001L, false),
                "legacy mode keeps owner=false");
            Check.That(TxAuthorityRouting.IsAuthorityManager(false, false, false, 7L, 9L, true),
                "legacy mode keeps remote owner=true");
            Check.That(TxAuthorityRouting.IsAuthorityManager(true, false, true, 1001L, 1001L, true),
                "unmanaged chest keeps owner=true");
            Check.That(!TxAuthorityRouting.IsAuthorityManager(true, false, true, 1001L, 1001L, false),
                "unmanaged chest keeps owner=false");
            Check.That(TxAuthorityRouting.IsAuthorityManager(true, false, false, 5L, 1001L, true),
                "unmanaged chest keeps remote owner=true");
        }

        /// <summary>
        /// Viewer-never-manager: under ServerAuthority every remote peer reads
        /// false for a managed chest regardless of the legacy ownership bit —
        /// viewers route via tx and never fall back to direct vanilla mutation.
        /// </summary>
        private static void AUTHORITY_ViewerNeverManager()
        {
            foreach (bool legacy in new bool[] { false, true })
            {
                Check.That(!TxAuthorityRouting.IsAuthorityManager(true, true, false, 1001L, 1001L, legacy),
                    "remote viewer never manager (legacy=" + legacy + ", owner=authority)");
                Check.That(!TxAuthorityRouting.IsAuthorityManager(true, true, false, 555L, 1001L, legacy),
                    "remote viewer never manager (legacy=" + legacy + ", owner=client)");
                Check.That(!TxAuthorityRouting.IsAuthorityManager(true, true, false, 0L, 1001L, legacy),
                    "remote viewer never manager (legacy=" + legacy + ", owner=none)");
            }
        }

        /// <summary>
        /// Stale-route discipline: only the manager answers owner-routed RPCs.
        /// A non-manager drops WITHOUT fabricating a terminal outcome (the
        /// sender resends/queries the SAME txId to the current owner).
        /// </summary>
        private static void AUTHORITY_StaleRouteDrop()
        {
            Check.That(!TxAuthorityRouting.ShouldDropStaleRoute(true), "manager answers");
            Check.That(TxAuthorityRouting.ShouldDropStaleRoute(false), "non-manager drops, no terminal outcome");
        }

        /// <summary>
        /// Structural upgrade gate: managed + non-manager never proceeds (fail
        /// closed, no acquire, no claim); managed + manager proceeds (host path
        /// unchanged); legacy/unmanaged defer to the legacy verdict bit-for-bit.
        /// </summary>
        private static void AUTHORITY_UpgradeFailClosed()
        {
            Check.That(!TxAuthorityRouting.CanStructuralUpgrade(true, true, false, true),
                "managed remote refused even when legacy would allow");
            Check.That(!TxAuthorityRouting.CanStructuralUpgrade(true, true, false, false),
                "managed remote refused (double-false)");
            Check.That(TxAuthorityRouting.CanStructuralUpgrade(true, true, true, false),
                "managed manager proceeds");
            Check.That(TxAuthorityRouting.CanStructuralUpgrade(false, true, false, true),
                "legacy defers to legacy=true");
            Check.That(!TxAuthorityRouting.CanStructuralUpgrade(false, true, false, false),
                "legacy defers to legacy=false");
            Check.That(TxAuthorityRouting.CanStructuralUpgrade(true, false, false, true),
                "unmanaged defers to legacy=true");
            Check.That(!TxAuthorityRouting.CanStructuralUpgrade(true, false, true, false),
                "unmanaged defers to legacy=false");
        }

        /// <summary>
        /// Wave-3 timer-escape kill switch (production TxNullEscape.EscapeAllowed,
        /// consulted by the quarantined-null path in TryReloadAuthoritative BEFORE
        /// either leg): ServerAuthority + managed reads false — the 30 s
        /// dead-source leg AND the 150 s live-quiescence leg are both inert, so
        /// a server-owned null-s_items chest stays fail-closed quarantined with
        /// a loud diagnose (no timer exit). Unmanaged chests and the whole
        /// LegacyDistributed regime read true (legs consulted unchanged).
        /// </summary>
        private static void AUTHORITY_EscapeDisabledInAuthorityMode()
        {
            Check.That(!TxNullEscape.EscapeAllowed(true, true),
                "authority + managed: no timer escape (dead-source 30 s leg disabled)");
            Check.That(TxNullEscape.EscapeAllowed(true, false),
                "authority + unmanaged: legacy legs still consulted (unmanaged chests untouched)");
            Check.That(TxNullEscape.EscapeAllowed(false, true),
                "legacy mode: legs still consulted (LegacyDistributed bit-for-bit)");
            Check.That(TxNullEscape.EscapeAllowed(false, false),
                "legacy + unmanaged: legs still consulted");
            // Belt-and-suspenders: even where the raw legs WOULD fire, the gated
            // combination (gate AND leg — exactly what the production null path
            // consults) stays false in authority mode for a managed chest.
            double[] elapsed = new double[] { 0.0, TxNullEscape.GraceSeconds, TxNullEscape.QuiescenceSeconds, 3600.0 };
            foreach (double t in elapsed)
            {
                foreach (bool live in new bool[] { false, true })
                {
                    bool gatedDead = TxNullEscape.EscapeAllowed(true, true)
                        && TxNullEscape.ShouldEscape(true, true, live, t);
                    Check.That(!gatedDead,
                        "authority managed: gated dead-source leg never fires (t=" + t + ", live=" + live + ")");
                    bool gatedLive = TxNullEscape.EscapeAllowed(true, true)
                        && TxNullEscape.ShouldEscapeLiveQuiescent(true, true, live, t);
                    Check.That(!gatedLive,
                        "authority managed: gated live-quiescence leg never fires (t=" + t + ", live=" + live + ")");
                }
            }
        }

        /// <summary>
        /// Wave-3 legacy preservation: with the gate open (every non-(authority
        /// + managed) combination) the legs answer EXACTLY as before — the
        /// dead-source leg fires past the 30 s grace, the live leg past the
        /// 150 s window. The gate adds no behavior to LegacyDistributed.
        /// </summary>
        private static void AUTHORITY_EscapeKeptInLegacy()
        {
            Check.That(TxNullEscape.EscapeAllowed(false, false)
                && TxNullEscape.ShouldEscape(true, true, false, TxNullEscape.GraceSeconds),
                "legacy: dead-source leg still fires at the grace");
            Check.That(TxNullEscape.EscapeAllowed(false, false)
                && TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, TxNullEscape.QuiescenceSeconds),
                "legacy: live-quiescence leg still fires at the window");
            Check.That(!(TxNullEscape.EscapeAllowed(false, false)
                && TxNullEscape.ShouldEscape(true, true, false, TxNullEscape.GraceSeconds - 1.0)),
                "legacy: dead-source leg still refuses inside the grace");
            Check.That(!(TxNullEscape.EscapeAllowed(false, false)
                && TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, TxNullEscape.GraceSeconds)),
                "legacy: live leg still refuses at the short grace");
        }

        /// <summary>
        /// Wave-3 quarantine persistence: in ServerAuthority mode a managed
        /// chest with null s_items has NO timer exit — the gate reads false at
        /// every elapsed time (before the grace, between the legs, past both
        /// windows, at max finite elapsed) for BOTH liveness values, so the
        /// production null path keeps the quarantine and diagnoses loudly
        /// instead of healing. (The production keep-quarantine branch itself is
        /// Unity-side; this pins the decision it consults.)
        /// </summary>
        private static void AUTHORITY_QuarantinePersistsOnNullAuthority()
        {
            double[] elapsed = new double[]
            {
                0.0,
                TxNullEscape.GraceSeconds - 0.001,
                TxNullEscape.GraceSeconds,
                TxNullEscape.GraceSeconds + 60.0,
                TxNullEscape.QuiescenceSeconds - 0.001,
                TxNullEscape.QuiescenceSeconds,
                TxNullEscape.QuiescenceSeconds + 3600.0,
                double.MaxValue
            };
            foreach (double t in elapsed)
            {
                Check.That(!TxNullEscape.EscapeAllowed(true, true),
                    "authority managed null at t=" + t + ": gate closed (quarantine persists, dead source)");
                bool gatedEither = TxNullEscape.EscapeAllowed(true, true)
                    && (TxNullEscape.ShouldEscape(true, true, false, t)
                        || TxNullEscape.ShouldEscapeLiveQuiescent(true, true, true, t));
                Check.That(!gatedEither,
                    "authority managed null at t=" + t + ": neither leg reachable (quarantine persists)");
            }
        }

        /// <summary>
        /// Wave-3 verified-init exception (production
        /// TxNullEscape.InitMaterializeAllowed, consulted by
        /// ServerAuthority.EnsureOnAwake): the ONLY authority-mode heal — a NEW
        /// server-created chest (self-owned at awake + s_items absent + live RAM
        /// provably empty) still materializes its empty blob. Every
        /// unverified shape refuses (foreign owner, present-but-invalid bytes,
        /// non-empty RAM, unmanaged chest): those take the repair/quarantine
        /// path, never a materialize.
        /// </summary>
        private static void AUTHORITY_VerifiedInitStillMaterializes()
        {
            Check.That(TxNullEscape.InitMaterializeAllowed(true, true, true, true, true),
                "verified init (server, self-owned, absent, ram-empty) still materializes");
            Check.That(!TxNullEscape.InitMaterializeAllowed(true, true, false, true, true),
                "foreign owner at awake: no materialize (repair path)");
            Check.That(!TxNullEscape.InitMaterializeAllowed(true, true, true, false, true),
                "present s_items (even invalid): no materialize (validated reload owns it)");
            Check.That(!TxNullEscape.InitMaterializeAllowed(true, true, true, true, false),
                "non-empty RAM: no materialize (never cement empty over real contents)");
            Check.That(!TxNullEscape.InitMaterializeAllowed(true, false, true, true, true),
                "unmanaged chest: no materialize (legacy path owns it)");
            Check.That(!TxNullEscape.InitMaterializeAllowed(false, true, true, true, true),
                "legacy mode: init exception is authority-only (legacy awake path unchanged)");
            Check.That(!TxNullEscape.InitMaterializeAllowed(false, false, false, false, false),
                "all-false: no materialize");
        }

        /// <summary>
        /// Wave-3 Takeover gate (production TxAuthorityRouting.TakeoverAllowed,
        /// consulted by the slow-pump adoption tripwire): a managed chest under
        /// ServerAuthority is taken over ONLY by the server (initial adoption
        /// reseed) — a remote peer never takes over (no client handoff), while
        /// LastOwner tracking still advances so the tripwire does not spin.
        /// LegacyDistributed and unmanaged chests always allow (distributed
        /// handoff bit-for-bit).
        /// </summary>
        private static void AUTHORITY_TakeoverGateNeverRemote()
        {
            Check.That(TxAuthorityRouting.TakeoverAllowed(true, true, true),
                "authority managed on server: takeover allowed (adoption reseed)");
            Check.That(!TxAuthorityRouting.TakeoverAllowed(true, true, false),
                "authority managed on remote: takeover refused (no client handoff)");
            Check.That(TxAuthorityRouting.TakeoverAllowed(false, true, false),
                "legacy mode on remote: takeover allowed (distributed handoff)");
            Check.That(TxAuthorityRouting.TakeoverAllowed(false, true, true),
                "legacy mode on server: takeover allowed");
            Check.That(TxAuthorityRouting.TakeoverAllowed(true, false, false),
                "authority mode unmanaged on remote: takeover allowed (legacy path)");
            Check.That(TxAuthorityRouting.TakeoverAllowed(true, false, true),
                "authority mode unmanaged on server: takeover allowed");
        }

        /// <summary>
        /// Remote-mutation ban: ServerAuthority + managed + non-server reads true
        /// (caller must fail closed); every other combination reads false.
        /// </summary>
        private static void AUTHORITY_RemoteNeverMutates()
        {
            Check.That(TxAuthorityRouting.RemoteMustNotMutate(true, true, false),
                "remote managed mutation banned");
            Check.That(!TxAuthorityRouting.RemoteMustNotMutate(true, true, true),
                "server managed mutation allowed (manager queue)");
            Check.That(!TxAuthorityRouting.RemoteMustNotMutate(false, true, false),
                "legacy remote untouched");
            Check.That(!TxAuthorityRouting.RemoteMustNotMutate(true, false, false),
                "unmanaged remote untouched");
            Check.That(!TxAuthorityRouting.RemoteMustNotMutate(false, false, false),
                "legacy unmanaged untouched");
        }
    }
}
