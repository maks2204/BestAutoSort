using BestAutoSort.TxCore;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Validated s_items -&gt; Inventory.Load path (MUST fix: validate-before-Load)
    /// for manager/takeover/reload paths. Every destructive Load of persisted
    /// bytes on those paths goes through HERE (the viewer refresh path
    /// intentionally bypasses the RAM snapshot: viewer RAM is non-owner
    /// read-only and never Saved, so torn GUI state cannot leak into a later
    /// save — it only preserves last-good SeenBytes on throw):
    /// - decoded-bytes proof FIRST (TxSItemsGuard: null/length/version). Null is
    ///   unavailable (never empty): RAM is left untouched and false is returned
    ///   so the caller stays fail-closed (quarantine kept / refresh skipped).
    /// - the bytes are defensively cloned before the ZPackage wrap
    ///   (ZPackage(byte[]) wraps without copying; the ZDO layer may hand out
    ///   the live stored reference — Load must never alias either side).
    /// - live RAM is snapshotted via Inventory.Save before Load; a mid-Load
    ///   throw restores the snapshot (best-effort) so corrupt-but-non-null
    ///   bytes can never leave torn RAM live behind a quarantine flag.
    /// Returns true only on full success. Never throws.
    /// </summary>
    internal static class TxSItemsLoad
    {
        internal static bool TryLoad(Container container, byte[] bytes, out string reason)
        {
            reason = null;
            if ((UnityEngine.Object)container == (UnityEngine.Object)null)
            {
                reason = "null container";
                return false;
            }
            if (!TxSItemsGuard.TryValidate(bytes, out reason))
                return false;
            Inventory inv;
            try
            {
                inv = container.GetInventory();
            }
            catch (System.Exception ex)
            {
                reason = "inventory unavailable (" + ex.Message + ")";
                return false;
            }
            if (inv == null)
            {
                reason = "null inventory";
                return false;
            }
            // Fallback copy of LIVE ram (not of the incoming bytes): restore
            // point if the Load below tears.
            ZPackage ramSnapshot = null;
            try
            {
                ramSnapshot = new ZPackage();
                inv.Save(ramSnapshot);
            }
            catch (System.Exception ex)
            {
                reason = "ram snapshot failed (" + ex.Message + ")";
                return false;
            }
            byte[] owned = TxSItemsGuard.CloneBytes(bytes);
            try
            {
                inv.Load(new ZPackage(owned));
            }
            catch (System.Exception ex)
            {
                reason = "load failed (" + ex.Message + ")";
                try
                {
                    if (ramSnapshot != null)
                    {
                        ramSnapshot.SetPos(0);
                        inv.Load(ramSnapshot);
                    }
                }
                catch (System.Exception restoreEx)
                {
                    TxLog.Warn("s_items validated load tore RAM and snapshot restore failed: " + restoreEx.Message);
                }
                return false;
            }
            return true;
        }
    }
}
