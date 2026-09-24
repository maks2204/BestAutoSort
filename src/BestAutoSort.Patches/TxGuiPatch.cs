using System;
using System.Collections.Generic;
using System.Reflection;
using BestAutoSort.Runtime;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using HarmonyLib;
using UnityEngine;
using BestAutoSort;

namespace BestAutoSort.Patches
{
    /// <summary>
    /// Shared-chest GUI operations go through ChestTX instead of ownership handoff.
    /// Owner (manager): drain the remote queue, then vanilla code synchronously
    /// (the main thread serializes everything itself — strict order).
    /// Non-owner: build the tx, send it, release the drag ghost, skip vanilla.
    /// Own-inventory items are removed ONLY on response (in ChestTxService).
    /// Purely local stuff (player→player, drag start, split dialog) stays vanilla.
    /// </summary>
    internal static class TxGui
    {
        private static readonly FieldInfo DragInventoryField = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        private static readonly FieldInfo DragItemField = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo DragAmountField = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");

        private static readonly MethodInfo SetupDragItemMethod = AccessTools.Method(typeof(InventoryGui), "SetupDragItem", (Type[])null, (Type[])null);

        internal static Inventory GetDragInventory(InventoryGui gui)
        {
            object value = DragInventoryField.GetValue(gui);
            return (Inventory)((value is Inventory) ? value : null);
        }

        internal static ItemData GetDragItem(InventoryGui gui)
        {
            object value = DragItemField.GetValue(gui);
            return (ItemData)((value is ItemData) ? value : null);
        }

        internal static int GetDragAmount(InventoryGui gui)
        {
            object value = DragAmountField.GetValue(gui);
            if (value is int)
                return (int)value;
            return 0;
        }

        internal static void CancelDrag(InventoryGui gui)
        {
            SetupDragItemMethod.Invoke(gui, new object[3] { null, null, 1 });
        }

        internal static void TellPlayer(string message)
        {
            Player player = Player.m_localPlayer;
            if (player != null)
                ((Character)player).Message((MessageType)2, message, 0, (Sprite)null, false);
        }

        /// <summary>
        /// Autofeed lease: while feeding holds the chest — nobody may touch it (as before).
        /// </summary>
        internal static bool BlockedByFeed(Container container)
        {
            if (AutoFeedService.IsLocked(container))
            {
                TellPlayer("Auto Feed is finishing a protected chest operation. Try again shortly.");
                return true;
            }
            return false;
        }

        internal static ItemData FindInPlayer(Inventory playerInv, string name, int quality)
        {
            if (playerInv == null || string.IsNullOrEmpty(name))
                return null;
            foreach (ItemData it in playerInv.GetAllItems())
            {
                if (it != null && it.m_shared != null
                    && string.Equals(it.m_shared.m_name, name, StringComparison.Ordinal)
                    && it.m_quality == quality)
                    return it;
            }
            return null;
        }

        /// <summary>
        /// Total stack of an exact key (name+quality+variant+world). Used to measure
        /// what a Take actually placed: before/after delta, never name+quality alone.
        /// </summary>
        internal static int CountInPlayer(Inventory playerInv, string name, int quality, int variant, int world)
        {
            if (playerInv == null || string.IsNullOrEmpty(name))
                return 0;
            int total = 0;
            foreach (ItemData it in playerInv.GetAllItems())
            {
                if (it != null && it.m_shared != null
                    && string.Equals(it.m_shared.m_name, name, StringComparison.Ordinal)
                    && it.m_quality == quality && it.m_variant == variant && it.m_worldLevel == world)
                    total += it.m_stack;
            }
            return total;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
    internal static class TxGuiSelectedPatch
    {
        private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemData item, Vector2i pos, Modifier mod)
        {
            Container container = InventoryAccess.CurrentContainer(__instance);
            if ((Object)container == (Object)null || !ChestTxService.IsShared(container))
            {
                // Eligible but outside the shared path: view-only for non-owners.
                if (ServerAuthority.DenyIfViewOnly(container, "gui-select"))
                    return false;
                return true;
            }
            if (TxGui.BlockedByFeed(container))
                return false;
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null)
                return false;
            Inventory chestInv = container.GetInventory();
            Inventory playerInv = ((Humanoid)player).GetInventory();
            Inventory targetInv = grid.GetInventory();

            // Wave-2: authority-routed. Only the manager runs vanilla here (after
            // draining the remote queue); remote managed chests always go via tx.
            if (ChestTxService.IsManager(container))
            {
                ChestTxService.DrainForLocal(container);
                return true;
            }

            ItemData dragItem = TxGui.GetDragItem(__instance);
            Inventory dragInv = TxGui.GetDragInventory(__instance);
            bool hasDrag = dragItem != null && dragInv != null;

            if (hasDrag)
            {
                // player->player stays fully local.
                if (dragInv == playerInv && targetInv == playerInv)
                    return true;
                HandleDragDrop(__instance, container, chestInv, player, playerInv, targetInv, dragItem, dragInv, item, pos);
                return false;
            }

            // No drag: intercept only chest-related Move/Drop.
            // Select/Split (drag start, dialog) stays vanilla: no mutations.
            if (mod != Modifier.Move && mod != Modifier.Drop)
                return true;
            if (targetInv != chestInv)
            {
                // Click on own inventory: Move with an open chest = deposit (tx),
                // Drop = drop on the ground (vanilla, chest untouched).
                if (mod == Modifier.Move)
                {
                    if (item == null)
                        return false;
                    HandleClickMove(__instance, container, chestInv, player, playerInv, targetInv, item);
                    return false;
                }
                return true;
            }
            HandleClickMove(__instance, container, chestInv, player, playerInv, targetInv, item);
            return false;
        }

        private static void HandleClickMove(InventoryGui gui, Container container, Inventory chestInv, Player player, Inventory playerInv, Inventory targetInv, ItemData item)
        {
            if (item == null)
                return;
            if (targetInv == chestInv)
            {
                // Chest -> player: Take the full stack.
                if (item.m_shared.m_questItem)
                    return;
                player.RemoveEquipAction(item);
                player.UnequipItem(item);
                ChestTxService.RequestTake(container, playerInv, item, item.m_stack, null);
            }
            else
            {
                // Player -> chest: Add the full stack. Source is the clicked grid's
                // inventory (GearSlots dedicated slots are separate inventories:
                // removing from the main one would miss and duplicate).
                if (item.m_shared.m_questItem)
                    return;
                player.RemoveEquipAction(item);
                player.UnequipItem(item);
                Inventory srcInv = (targetInv != null) ? targetInv : playerInv;
                ChestTxService.RequestAdd(container, srcInv, item, item.m_stack, -1, -1, null);
            }
        }

        private static void HandleDragDrop(InventoryGui gui, Container container, Inventory chestInv, Player player, Inventory playerInv, Inventory targetInv, ItemData dragItem, Inventory dragInv, ItemData item, Vector2i pos)
        {
            int dragAmount = TxGui.GetDragAmount(gui);
            if (dragAmount <= 0)
            {
                TxGui.CancelDrag(gui);
                return;
            }
            if (targetInv != chestInv && targetInv != playerInv)
            {
                TxGui.CancelDrag(gui);
                return;
            }
            if ((dragItem.m_shared.m_questItem || (item != null && item.m_shared.m_questItem)) && dragInv != targetInv)
            {
                TxGui.CancelDrag(gui);
                return;
            }
            if (targetInv == chestInv && dragInv != chestInv)
            {
                if (((Humanoid)player).IsItemEquiped(dragItem))
                    player.UnequipItem(dragItem, false);
                Plugin.LogInstance.LogInfo((object)("[ChestTX] drag-drop chest=" + chestInv.GetWidth() + "x" + chestInv.GetHeight() + " pos=(" + pos.x + "," + pos.y + ")"));
                ChestTxService.RequestAdd(container, dragInv, dragItem, Math.Min(dragAmount, dragItem.m_stack), pos.x, pos.y, null);
                TxGui.CancelDrag(gui);
                return;
            }
            if (targetInv != chestInv && dragInv == chestInv)
            {
                ChestTxService.RequestTake(container, targetInv, dragItem, Math.Min(dragAmount, dragItem.m_stack), null, pos.x, pos.y);
                TxGui.CancelDrag(gui);
                return;
            }
            if (targetInv == chestInv && dragInv == chestInv)
            {
                ChestTxService.RequestMove(container, dragItem, Math.Min(dragAmount, dragItem.m_stack), pos.x, pos.y, null);
                TxGui.CancelDrag(gui);
                return;
            }
            TxGui.CancelDrag(gui);
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "OnTakeAll")]
    internal static class TxGuiTakeAllPatch
    {
        private static bool Prefix(InventoryGui __instance)
        {
            Container container = InventoryAccess.CurrentContainer(__instance);
            if ((Object)container == (Object)null || !ChestTxService.IsShared(container))
            {
                // Eligible but outside the shared path: view-only for non-owners.
                if (ServerAuthority.DenyIfViewOnly(container, "gui-takeall"))
                    return false;
                return true;
            }
            if (TxGui.BlockedByFeed(container))
                return false;
            // Всегда через tx (владелец — локально в очередь): иначе у владельца
            // немая ванилла без визуала и без броадкаста.
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null || player.IsTeleporting())
                return false;
            Inventory chestInv = container.GetInventory();
            Inventory playerInv = ((Humanoid)player).GetInventory();
            if (chestInv == null || playerInv == null)
                return false;
            ChestTxService.DrainForLocal(container);
            List<TxOpItem> items = new List<TxOpItem>();
            foreach (ItemData it in new List<ItemData>(chestInv.GetAllItems()))
            {
                // Quest items stay: click-move paths block them, TakeAll must agree.
                if (it != null && it.m_shared != null && it.m_shared.m_questItem)
                    continue;
                TxOpItem op = ChestTxService.SnapshotItem(it, it.m_stack, -1, -1);
                if (op != null)
                    items.Add(op);
            }
            if (items.Count == 0)
                return false;
            ChestTxService.RequestTakeBatchChunked(container, playerInv, items,
                delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
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
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "OnStackAll")]
    internal static class TxGuiStackAllPatch
    {
        private static bool Prefix(InventoryGui __instance)
        {
            Container container = InventoryAccess.CurrentContainer(__instance);
            if ((Object)container == (Object)null || !ChestTxService.IsShared(container))
            {
                // Eligible but outside the shared path: view-only for non-owners.
                if (ServerAuthority.DenyIfViewOnly(container, "gui-stackall"))
                    return false;
                return true;
            }
            if (TxGui.BlockedByFeed(container))
                return false;
            // Всегда через tx (владелец — локально в очередь): иначе у владельца
            // немая ванилла без визуала и без броадкаста.
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null || player.IsTeleporting())
                return false;
            Inventory chestInv = container.GetInventory();
            Inventory playerInv = ((Humanoid)player).GetInventory();
            if (chestInv == null || playerInv == null)
                return false;
            ChestTxService.DrainForLocal(container);
            HashSet<string> names = new HashSet<string>();
            foreach (ItemData chestItem in chestInv.GetAllItems())
            {
                if (chestItem != null && chestItem.m_shared != null)
                    names.Add(chestItem.m_shared.m_name);
            }
            List<TxOpItem> items = new List<TxOpItem>();
            foreach (ItemData it in new List<ItemData>(playerInv.GetAllItems()))
            {
                if (it == null || it.m_shared == null || it.m_shared.m_questItem)
                    continue;
                if (((Humanoid)player).IsItemEquiped(it))
                    continue;
                if (ItemLockService.IsLocked(it) || RestockProfileService.IsTarget(it))
                    continue;
                if (!names.Contains(it.m_shared.m_name))
                    continue;
                TxOpItem op = ChestTxService.SnapshotAuto(it, it.m_stack);
                if (op != null)
                    items.Add(op);
            }
            Plugin.LogInstance.LogInfo((object)("[ChestTX] stack-all button candidates=" + items.Count));
            if (items.Count == 0)
                return false;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.AddBatch;
            call.Items.AddRange(items);
            call.EnforceRule = false;
            ChestTxService.SubmitCall(container, call, playerInv,
                delegate (List<TransferRecord> records, TxCompletionKind disp)
                {
                    int total = 0;
                    for (int i = 0; i < records.Count; i++)
                        total += records[i].Amount;
                    if (total > 0)
                    {
                        TransferVisuals.Play(records, container);
                        TxGui.TellPlayer(Localization.instance.Localize("$msg_stackall " + total));
                    }
                    else
                    {
                        TxGui.TellPlayer(Localization.instance.Localize("$msg_stackall_none"));
                    }
                });
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
    internal static class TxGuiRightClickPatch
    {
        private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemData item, Vector2i pos)
        {
            Container container = InventoryAccess.CurrentContainer(__instance);
            if ((Object)container == (Object)null || !ChestTxService.IsShared(container))
            {
                // Eligible but outside the shared path: view-only for non-owners.
                if (ServerAuthority.DenyIfViewOnly(container, "gui-useclick"))
                    return false;
                return true;
            }
            if (TxGui.BlockedByFeed(container))
                return false;
            if (grid.GetInventory() != container.GetInventory())
                return true;
            if (item != null && item.m_shared != null && item.m_shared.m_questItem)
                return true;
            // Wave-2: authority-routed (manager-only vanilla, remote via tx).
            if (ChestTxService.IsManager(container))
            {
                ChestTxService.DrainForLocal(container);
                return true;
            }
            if (item == null)
                return false;
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null)
                return false;
            Inventory playerInv = ((Humanoid)player).GetInventory();
            // Use from the chest: Take a single unit, then Use your own copy.
            // Full-stack take would eat 1 and orphan N-1 in the player
            // inventory (issue #6). Mirrors the PrefetchBorrow pattern.
            string name = item.m_shared != null ? item.m_shared.m_name : null;
            int quality = item.m_quality;
            int variant = item.m_variant;
            int world = item.m_worldLevel;
            int before = TxGui.CountInPlayer(playerInv, name, quality, variant, world);
            ChestTxService.RequestTake(container, playerInv, item, 1,
                delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
                {
                    if (status != TxStatus.Accepted && status != TxStatus.Partial && status != TxStatus.Duplicate)
                        return;
                    // Only Use what this take actually placed: a compensated take
                    // (full inventory) must not consume the player's own stack.
                    int placed = TxGui.CountInPlayer(playerInv, name, quality, variant, world) - before;
                    if (placed <= 0)
                        return;
                    ItemData mine = TxCodec.ResolveIn(playerInv, name, quality, variant, world, -1, -1);
                    if (mine != null)
                        player.UseItem(playerInv, mine, true);
                });
            return false;
        }

    }
}
