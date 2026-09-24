namespace BestAutoSort.TxCore
{
    /// <summary>
    /// Pre-Load gate for persisted s_items blobs (MUST fix: null-is-not-empty,
    /// validate-before-Load, byte discipline). Pure bytes, no Unity refs, so the
    /// offline harness can pin the exact contract the Valheim-side Load sites
    /// enforce before touching live inventory RAM:
    /// - null is UNAVAILABLE, never empty (fail-closed; caller keeps quarantine).
    /// - length + little-endian inventory-version pre-check before any Load.
    /// - CloneBytes is the SOLE copy primitive for Get/Set baselines (ZPackage
    ///   wraps a byte[] without copying and ZDO.GetByteArray may hand out the
    ///   live stored reference, so every retained array goes through here).
    /// s_items carries no checksum (plain Inventory.Save bytes), so "checksum"
    /// is length+version here; ring/floor copies keep their own envelope
    /// validation (TxCore.DecodeEnvelope/DecodeFloor).
    /// </summary>
    public static class TxSItemsGuard
    {
        /// <summary>
        /// Smallest plausible blob: version int32 + item-count uint16, matching
        /// the empty-inventory encoding (version 109 + count 0).
        /// </summary>
        public const int MinSItemsBytes = 6;

        /// <summary>Generous upper bound (a chest blob is ~10^2-10^4 B).</summary>
        public const int MaxSItemsBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Plausible Inventory.Save version range. Generous on purpose: a future
        /// game version must degrade to quarantine (fail-closed), never to a
        /// hard brick — but 0/negative/huge is never a real version.
        /// </summary>
        public const int MinInventoryVersion = 1;
        public const int MaxInventoryVersion = 10000;

        /// <summary>
        /// Decoded-bytes proof gate: true only when bytes are present, sanely
        /// sized, and carry a plausible version. False for null (unavailable —
        /// NEVER empty). Never throws.
        /// </summary>
        public static bool TryValidate(byte[] bytes, out string reason)
        {
            if (bytes == null)
            {
                reason = "null (unavailable, never empty)";
                return false;
            }
            if (bytes.Length < MinSItemsBytes)
            {
                reason = "too short (" + bytes.Length + " < " + MinSItemsBytes + ")";
                return false;
            }
            if (bytes.Length > MaxSItemsBytes)
            {
                reason = "too long (" + bytes.Length + " > " + MaxSItemsBytes + ")";
                return false;
            }
            // Explicit little-endian decode (manual shifts, host-independent).
            int version = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
            if (version < MinInventoryVersion || version > MaxInventoryVersion)
            {
                reason = "bad version (" + version + ")";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// Null-safe defensive copy. Null stays null (unavailable propagates as
        /// null — never synthesized into empty). Never throws.
        /// </summary>
        public static byte[] CloneBytes(byte[] src)
        {
            if (src == null)
                return null;
            byte[] dst = new byte[src.Length];
            System.Buffer.BlockCopy(src, 0, dst, 0, src.Length);
            return dst;
        }

    }
}
