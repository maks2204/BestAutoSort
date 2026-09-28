using System;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches
{
    /// <summary>
    /// Vanilla Container.Save postfix: re-anchor the server session content
    /// hash after EVERY save (proven by decompilation: Container wires
    /// inventory.m_onChanged to OnContainerChanged, which calls Save() on any
    /// live-RAM mutation by the owner outside m_loading). Host-side vanilla
    /// drags, in-chest moves, loans, sorts and restocks all save through here
    /// outside any Tx commit; without the re-anchor the next remote job reads
    /// our own save as a foreign write and quarantines the chest. Never
    /// touches quarantine flags; failures leave the hash as-was.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Save")]
    internal static class TxContainerSavePatch
    {
        private static void Postfix(Container __instance)
        {
            try
            {
                if (__instance == null)
                    return;
                ZNetView nv = null;
                try
                {
                    nv = __instance.GetComponent<ZNetView>();
                    if ((UnityEngine.Object)nv == (UnityEngine.Object)null)
                        return;
                    if (!nv.IsValid())
                        return;
                }
                catch { return; }
                ZDO zdo = null;
                try { zdo = nv.GetZDO(); } catch { zdo = null; }
                if (zdo == null)
                    return;
                ServerChestSession session = null;
                string why = "?";
                bool have = false;
                try { have = ServerChestSessions.TryGetOrCreate(zdo, out session, out why); } catch { have = false; }
                if (!have || session == null)
                    return;
                byte[] cur = null;
                try { cur = TxSItemsGuard.CloneBytes(zdo.GetByteArray(ZDOVars.s_items)); } catch { cur = null; }
                if (cur == null)
                    return;
                try { session.SItemsHash = ServerChestSessions.Fnv1a64(cur); session.HasSItemsHash = true; } catch { }
                try { session.LastSeenRev = zdo.DataRevision; } catch { }
            }
            catch { }
        }
    }
}
