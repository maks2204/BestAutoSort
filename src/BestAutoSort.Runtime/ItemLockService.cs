using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BestAutoSort.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BestAutoSort.Runtime;

internal static class ItemLockService
{
	private const string HighlightName = "BestAutoSort_StackLockHighlight";

	private static readonly FieldInfo ElementsField = AccessTools.Field(typeof(InventoryGrid), "m_elements");

	internal static bool IsLocked(ItemData? item)
	{
		if (item != null)
		{
			return ItemStackLock.IsLocked(item.m_customData);
		}
		return false;
	}

	internal static bool TrySet(InventoryGrid grid, ItemData? item, bool locked)
	{
		if (!ModConfig.Enabled.Value || item == null)
		{
			return false;
		}
		Inventory inventory = grid.GetInventory();
		Player lockPlayer = Player.m_localPlayer;
		if (inventory == null || (Object)(object)lockPlayer == (Object)null || inventory != ((Humanoid)lockPlayer).GetInventory())
		{
			return false;
		}
		ItemStackLock.SetLocked(item.m_customData, locked);
		inventory.m_onChanged?.Invoke();
		RefreshHighlights(grid);
		return true;
	}

	internal static void RefreshHighlights(InventoryGrid grid)
	{
		Inventory inventory = grid.GetInventory();
		if (inventory == null || !(ElementsField.GetValue(grid) is IList list))
		{
			return;
		}
		foreach (object item in list)
		{
			InventoryElement val = (InventoryElement)((item is InventoryElement) ? item : null);
			if (val != null && (bool)(Object)(object)val)
			{
				Vector2i position = val.Position;
				GetOrCreateHighlight(((Component)val).gameObject).SetActive(IsLocked(inventory.GetItemAt(position.x, position.y)));
			}
		}
	}

	internal static List<ItemData> HideLockedItems(Inventory inventory)
	{
		List<ItemData> allItems = inventory.GetAllItems();
		List<ItemData> list = allItems.FindAll(IsLocked);
		foreach (ItemData item in list)
		{
			allItems.Remove(item);
		}
		return list;
	}

	internal static void RestoreHiddenItems(Inventory inventory, IEnumerable<ItemData> hidden)
	{
		List<ItemData> allItems = inventory.GetAllItems();
		bool flag = false;
		foreach (ItemData item in hidden)
		{
			if (!allItems.Contains(item))
			{
				allItems.Add(item);
				flag = true;
			}
		}
		if (flag)
		{
			inventory.m_onChanged?.Invoke();
		}
	}

	private static GameObject GetOrCreateHighlight(GameObject slot)
	{
		Transform val = slot.transform.Find("BestAutoSort_StackLockHighlight");
		if ((Object)(object)val != (Object)null)
		{
			return ((Component)val).gameObject;
		}
		GameObject val2 = new GameObject("BestAutoSort_StackLockHighlight", new Type[4]
		{
			typeof(RectTransform),
			typeof(CanvasRenderer),
			typeof(Image),
			typeof(Outline)
		})
		{
			layer = slot.layer
		};
		val2.transform.SetParent(slot.transform, false);
		val2.transform.SetAsFirstSibling();
		RectTransform val3 = (RectTransform)val2.transform;
		val3.anchorMin = Vector2.zero;
		val3.anchorMax = Vector2.one;
		val3.offsetMin = new Vector2(2f, 2f);
		val3.offsetMax = new Vector2(-2f, -2f);
		Image component = val2.GetComponent<Image>();
		Image component2 = slot.GetComponent<Image>();
		if ((Object)(object)component2 != (Object)null)
		{
			component.sprite = component2.sprite;
			component.type = component2.type;
			((Graphic)component).material = ((Graphic)component2).material;
		}
		((Graphic)component).color = new Color(1f, 0.58f, 0.08f, 0.28f);
		((Graphic)component).raycastTarget = false;
		Outline component3 = val2.GetComponent<Outline>();
		((Shadow)component3).effectColor = new Color(1f, 0.68f, 0.12f, 0.95f);
		((Shadow)component3).effectDistance = new Vector2(2f, -2f);
		((Shadow)component3).useGraphicAlpha = false;
		return val2;
	}
}
