using BestAutoSort.Runtime;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches
{
    /// <summary>
    /// Dropping a chest item outside the grid (onto the ground): vanilla removes locally
    /// with no sync — dupe (on the ground, never taken from the chest).
    /// Non-owner: Take by transaction, then drop the received copy on the ground.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "OnDropOutside")]
    internal static class TxGuiDropOutsidePatch
    {
        private static bool Prefix(InventoryGui __instance)
        {
            Container container = InventoryAccess.CurrentContainer(__instance);
            if ((Object)container == (Object)null || !ChestTxService.IsShared(container))
                return true;
            ItemData dragItem = TxGui.GetDragItem(__instance);
            Inventory dragInv = TxGui.GetDragInventory(__instance);
            int dragAmount = TxGui.GetDragAmount(__instance);
            if (dragItem == null || dragInv == null || dragAmount <= 0)
                return true;
            if (dragInv != container.GetInventory())
                return true;
            if (container.IsOwner())
            {
                ChestTxService.DrainForLocal(container);
                return true;
            }
            if (TxGui.BlockedByFeed(container))
                return false;
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null)
                return false;
            Inventory playerInv = ((Humanoid)player).GetInventory();
            string name = dragItem.m_shared != null ? dragItem.m_shared.m_name : null;
            int quality = dragItem.m_quality;
            int amount = dragAmount < dragItem.m_stack ? dragAmount : dragItem.m_stack;
            TxGui.CancelDrag(__instance);
            ChestTxService.RequestTake(container, playerInv, dragItem, amount,
                delegate (ZPackage pkg, TxStatus status, uint rev)
                {
                    if (status != TxStatus.Accepted && status != TxStatus.Partial && status != TxStatus.Duplicate)
                        return;
                    ItemData mine = TxGui.FindInPlayer(playerInv, name, quality);
                    if (mine != null)
                    {
                        int drop = mine.m_stack < amount ? mine.m_stack : amount;
                        player.DropItem(playerInv, mine, drop);
                    }
                });
            return false;
        }
    }
}
