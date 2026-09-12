using System.Collections.Generic;
using BestAutoSort.Runtime;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches
{
    /// <summary>
    /// Intercept vanilla chest TakeAll/StackAll: ChestTX transactions (batches)
    /// instead of ownership handoff. The old path
    /// Container.StackAll() → ownership → local StackAll() (race source) is gone.
    /// </summary>
    internal static class TxOpsGuard
    {
        internal static bool FeedBusy(Container c)
        {
            if (!c.IsOwner() && AutoFeedService.IsLocked(c))
            {
                Player player = Player.m_localPlayer;
                if (player != null)
                    ((Character)player).Message((MessageType)2, "Auto Feed is finishing a protected chest operation. Try again shortly.", 0, (Sprite)null, false);
                return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Container), "TakeAll")]
    internal static class TxContainerTakeAllPatch
    {
        private static bool Prefix(Container __instance, Humanoid character, ref bool __result)
        {
            if (!ModConfig.AllowConcurrentChestUse.Value || !ChestTxService.IsShared(__instance))
                return true;
            if (TxOpsGuard.FeedBusy(__instance))
                return false;
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null)
            {
                __result = false;
                return false;
            }
            Inventory chestInv = __instance.GetInventory();
            Inventory playerInv = ((Humanoid)player).GetInventory();
            if (chestInv == null || playerInv == null)
            {
                __result = false;
                return false;
            }
            List<TxOpItem> items = new List<TxOpItem>();
            foreach (ItemData item in new List<ItemData>(chestInv.GetAllItems()))
            {
                TxOpItem op = ChestTxService.SnapshotItem(item, item.m_stack, -1, -1);
                if (op != null)
                    items.Add(op);
            }
            if (items.Count == 0)
            {
                __result = true;
                return false;
            }
            Container container = __instance;
            ChestTxService.RequestTakeBatch(container, playerInv, items,
                delegate (ZPackage pkg, TxStatus status, uint rev)
                {
                    List<DecodedTake> takes = TxCodec.ReadTakeResults(pkg);
                    if (takes == null)
                        return;
                    List<TransferRecord> records = new List<TransferRecord>();
                    for (int i = 0; i < takes.Count; i++)
                    {
                        DecodedTake t = takes[i];
                        if (t != null && t.Item != null && t.Item.m_shared != null && t.Accepted > 0)
                            records.Add(new TransferRecord(t.Item.m_shared.m_name, t.Item.GetIcon(), t.Accepted, t.Item.m_shared.m_maxStackSize));
                    }
                    if (records.Count > 0)
                        TransferVisuals.PlayToPlayer(records, container);
                });
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Container), "StackAll")]
    internal static class TxContainerStackAllPatch
    {
        private static bool Prefix(Container __instance)
        {
            if (!ModConfig.AllowConcurrentChestUse.Value || !ChestTxService.IsShared(__instance))
                return true;
            if (TxOpsGuard.FeedBusy(__instance))
                return false;
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null)
                return false;
            Inventory chestInv = __instance.GetInventory();
            Inventory playerInv = ((Humanoid)player).GetInventory();
            if (chestInv == null || playerInv == null)
                return false;
            HashSet<string> names = new HashSet<string>();
            foreach (ItemData chestItem in chestInv.GetAllItems())
            {
                if (chestItem != null && chestItem.m_shared != null)
                    names.Add(chestItem.m_shared.m_name);
            }
            List<TxOpItem> items = new List<TxOpItem>();
            foreach (ItemData item in new List<ItemData>(playerInv.GetAllItems()))
            {
                if (item == null || item.m_shared == null)
                    continue;
                if (item.m_shared.m_questItem || ((Humanoid)player).IsItemEquiped(item))
                    continue;
                if (ItemLockService.IsLocked(item) || RestockProfileService.IsTarget(item))
                    continue;
                if (!names.Contains(item.m_shared.m_name))
                    continue;
                TxOpItem op = ChestTxService.SnapshotItem(item, item.m_stack, -1, -1);
                if (op != null)
                    items.Add(op);
            }
            if (items.Count == 0)
                return false;
            Container stackContainer = __instance;
            TxOpCall stackCall = new TxOpCall();
            stackCall.Op = TxOp.AddBatch;
            stackCall.Items.AddRange(items);
            stackCall.EnforceRule = false;
            ChestTxService.SubmitCall(stackContainer, stackCall, playerInv,
                delegate (List<TransferRecord> records)
                {
                    if (records.Count > 0)
                        TransferVisuals.Play(records, stackContainer);
                });
            return false;
        }
    }

    /// <summary>
    /// Guard: a direct RPC_RequestStack in shared mode is denied —
    /// stacking runs only through ChestTX (otherwise the mutation is unserialized).
    /// Vanilla StackAll()/TakeAll() are intercepted at the Container level and go to tx too.
    /// </summary>
    [HarmonyPatch(typeof(Container), "RPC_RequestStack")]
    internal static class TxContainerStackRpcPatch
    {
        private static bool Prefix(Container __instance, long uid, long playerID)
        {
            if (!ModConfig.AllowConcurrentChestUse.Value || !ChestTxService.IsShared(__instance))
                return true;
            if (TxOpsGuard.FeedBusy(__instance))
                return false;
            ZNetView denyView = TxReflect.GetNetView(__instance);
            if (denyView != null)
                denyView.InvokeRPC(uid, "RPC_StackResponse", new object[1] { false });
            return false;
        }
    }
}
