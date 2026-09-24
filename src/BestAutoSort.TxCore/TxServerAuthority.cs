using System;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Chest ownership authority mode (server-authority migration, wave 1).
    ///
    /// - ServerAuthority: chest ZDO ownership stays on the server/host (the
    ///   authority UID). Clients never own eligible chests; vanilla ownership
    ///   transfer paths (open/stack/take-all acquisition, nearby redistribution,
    ///   client claims, incoming ownership revisions) are guarded. Default on
    ///   dedicated servers, listen-server hosts and single-player.
    /// - LegacyDistributed: pre-migration distributed ownership (ZDO owner =
    ///   chest manager, handed over by vanilla transfer paths). Untouched
    ///   legacy behavior for rollback/mixed groups.
    ///
    /// All peers in a session MUST use the same mode: Hello compat negotiation
    /// rejects mismatched peers (see TxAuthorityHello), and vanilla (mod-less)
    /// clients are unsupported for managed chests.
    ///
    /// This is an experimental vertical slice (wave 1), NOT a completed
    /// release: the invariant is not claimed yet, see wave-2 notes.
    /// </summary>
    public enum ServerAuthorityMode
    {
        ServerAuthority = 0,
        LegacyDistributed = 1
    }

    /// <summary>
    /// Pure server-managed-chest classification (wave 1). No Unity/Valheim refs,
    /// so the offline suite pins the SAME rule the game uses for both the
    /// Container path (IsServerManagedContainer) and the Container-less ZDO
    /// path (prefab hash only).
    ///
    /// Rule: stationary supported storage IN; Player / TombStone / wagon /
    /// ship / moving / temporary / special OUT; unknown prefab NOT eligible
    /// (fail closed). The rule is independent of IsShared, lease, range and
    /// toggles by construction: the signature takes ONLY the prefab name.
    /// </summary>
    public static class TxServerAuthorityPolicy
    {
        /// <summary>Allowlist prefix: stationary player-built storage chests.</summary>
        public const string ChestPrefabPrefix = "piece_chest";

        /// <summary>
        /// Denylist substrings (case-insensitive): anything moving, vehicular,
        /// actor-bound, funerary or otherwise special is OUT even if it ever
        /// carried the allowlist prefix.
        /// </summary>
        public static readonly string[] ExcludedSubstrings = new string[]
        {
            "tomb",
            "player",
            "cart",
            "wagon",
            "vagon",
            "ship",
            "boat",
            "karve",
            "raft",
        };

        /// <summary>
        /// The single prefab-name gate. Null/empty/unknown names are NOT
        /// eligible (fail closed). Case-sensitive prefix (Valheim prefab names
        /// are lowercase by convention); denylist is case-insensitive.
        /// </summary>
        public static bool IsEligiblePrefabName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;
            if (!prefabName.StartsWith(ChestPrefabPrefix, StringComparison.Ordinal))
                return false;
            for (int i = 0; i < ExcludedSubstrings.Length; i++)
            {
                string bad = ExcludedSubstrings[i];
                if (bad != null && prefabName.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Pure Hello/version/mode compat negotiation (wave 1). The wire hello is
    /// "version;auth=N" (N = (int)ServerAuthorityMode). Legacy peers send the
    /// bare version string (no mode concept: they behave as LegacyDistributed).
    ///
    /// Compat rule: same mod version AND same mode. Anything else (version
    /// skew, mode skew, unparseable-but-known legacy under ServerAuthority) is
    /// incompatible: mismatched peers are rejected (ephemeral tx refusal, no
    /// victim state change — same rule as the existing version-skew branch).
    /// </summary>
    public static class TxAuthorityHello
    {
        public const string ModeSeparator = ";auth=";

        /// <summary>Build the wire hello for our version + mode.</summary>
        public static string BuildHello(string version, ServerAuthorityMode mode)
        {
            return (version ?? string.Empty) + ModeSeparator + ((int)mode).ToString();
        }

        /// <summary>
        /// Parse a wire hello. False = legacy/unparseable (bare version string
        /// or garbage): NOT an error by itself — the caller applies the legacy
        /// fallback (see AreCompatibleWithHello).
        /// </summary>
        public static bool TryParseHello(string hello, out string version, out ServerAuthorityMode mode)
        {
            version = string.Empty;
            mode = ServerAuthorityMode.LegacyDistributed;
            if (string.IsNullOrEmpty(hello))
                return false;
            int sep = hello.IndexOf(ModeSeparator, StringComparison.Ordinal);
            if (sep < 0)
                return false;
            string v = hello.Substring(0, sep);
            string m = hello.Substring(sep + ModeSeparator.Length);
            if (string.IsNullOrEmpty(v))
                return false;
            int n;
            if (!int.TryParse(m, out n))
                return false;
            if (n != (int)ServerAuthorityMode.ServerAuthority && n != (int)ServerAuthorityMode.LegacyDistributed)
                return false;
            version = v;
            mode = (ServerAuthorityMode)n;
            return true;
        }

        /// <summary>Structured compat: exact version match AND exact mode match.</summary>
        public static bool AreCompatible(string localVersion, ServerAuthorityMode localMode,
            string remoteVersion, ServerAuthorityMode remoteMode)
        {
            return string.Equals(localVersion, remoteVersion, StringComparison.Ordinal)
                && localMode == remoteMode;
        }

        /// <summary>
        /// Wire-level compat including the legacy fallback: a parseable hello
        /// uses AreCompatible; a legacy bare-version hello is compatible ONLY
        /// when it exactly matches our version AND our own mode is
        /// LegacyDistributed (a legacy peer behaves as LegacyDistributed, so a
        /// ServerAuthority peer must reject it).
        /// </summary>
        public static bool AreCompatibleWithHello(string localVersion, ServerAuthorityMode localMode, string remoteHello)
        {
            string rv;
            ServerAuthorityMode rm;
            if (TryParseHello(remoteHello, out rv, out rm))
                return AreCompatible(localVersion, localMode, rv, rm);
            return localMode == ServerAuthorityMode.LegacyDistributed
                && string.Equals(remoteHello, localVersion, StringComparison.Ordinal);
        }
    }
}
