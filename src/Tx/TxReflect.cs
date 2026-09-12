using System;
using System.Reflection;
using BestAutoSort.Runtime;
using HarmonyLib;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Reflection over vanilla private members.
    /// </summary>
    internal static class TxReflect
    {
        private static readonly FieldInfo NetViewField = AccessTools.Field(typeof(Container), "m_nview");
        private static readonly FieldInfo LastRevisionField = AccessTools.Field(typeof(Container), "m_lastRevision");
        private static readonly MethodInfo SaveMethod = AccessTools.Method(typeof(Container), "Save", (Type[])null, (Type[])null);
        private static readonly MethodInfo AddItemAtMethod = AccessTools.Method(typeof(Inventory), "AddItem", new Type[] { typeof(ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });

        /// <summary>
        /// Vanilla positional placement (like drag&drop): merge/place strictly into the cell.
        /// The clone is mutated (m_stack reduced by the placed amount). Returns the vanilla flag.
        /// </summary>
        internal static bool AddItemAt(Inventory inv, ItemData clone, int amount, int x, int y)
        {
            object result = AddItemAtMethod.Invoke(inv, new object[5] { clone, amount, x, y, false });
            return result is bool && (bool)result;
        }
        private static readonly MethodInfo LoadMethod = AccessTools.Method(typeof(Container), "Load", (Type[])null, (Type[])null);
        private static readonly MethodInfo UpdateRowsMethod = AccessTools.Method(typeof(Container), "UpdateRows", (Type[])null, (Type[])null);
        private static readonly FieldInfo DragInventoryField = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        private static readonly MethodInfo SetupDragItemMethod = AccessTools.Method(typeof(InventoryGui), "SetupDragItem", (Type[])null, (Type[])null);

        internal static ZNetView GetNetView(Container container)
        {
            if ((Object)container == (Object)null)
                return null;
            object value = NetViewField.GetValue(container);
            return (ZNetView)((value is ZNetView) ? value : null);
        }

        private static readonly MethodInfo CheckAccessMethod = AccessTools.Method(typeof(Container), "CheckAccess", (Type[])null, (Type[])null);

        internal static bool HasAccess(Container container, long playerId)
        {
            try
            {
                object result = CheckAccessMethod.Invoke(container, new object[1] { playerId });
                return result is bool && (bool)result;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void SaveContainer(Container container)
        {
            SaveMethod.Invoke(container, Array.Empty<object>());
        }

        internal static bool LoadContainer(Container container)
        {
            object result = LoadMethod.Invoke(container, Array.Empty<object>());
            return result is bool && (bool)result;
        }

        internal static void SetLastRevision(Container container, uint revision)
        {
            LastRevisionField.SetValue(container, revision);
        }

        internal static void UpdateRows(Container container)
        {
            UpdateRowsMethod.Invoke(container, Array.Empty<object>());
        }

        /// <summary>
        /// Safely cancel a drag sourced from the chest inventory (item stays in the chest — no loss).
        /// </summary>
        internal static void CancelDragFrom(InventoryGui gui, Inventory chestInventory)
        {
            if ((Object)gui == (Object)null || chestInventory == null)
                return;
            try
            {
                object value = DragInventoryField.GetValue(gui);
                if (value is Inventory && (Inventory)value == chestInventory)
                    SetupDragItemMethod.Invoke(gui, new object[3] { null, null, 1 });
            }
            catch (Exception)
            {
            }
        }
    }
}
