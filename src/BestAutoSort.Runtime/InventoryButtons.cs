using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BestAutoSort.Core;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BestAutoSort.Runtime;

internal static class InventoryButtons
{
	[CompilerGenerated]
	private static class _003C_003EO
	{
		public static UnityAction _003C0_003E__DestroyHeldItem;

		public static UnityAction _003C1_003E__OpenRuleEditor;

		public static Action _003C2_003E__OpenRuleEditor;
	}

	private static InventoryGui? _gui;

	private static Button? _template;

	private static Button? _quickStackButton;

	private static Button? _trashButton;

	private static Button? _sortButton;

	private static Button? _rulesButton;

	private static Button? _reinforcedUpgradeButton;

	private static Button? _blackMetalUpgradeButton;

	private static Button? _graustenUpgradeButton;

	private static TMP_Text? _rulesLabel;

	private static UITooltip? _rulesTooltip;

	private static Container? _labelContainer;

	private static readonly FieldInfo DragItemField = AccessTools.Field(typeof(InventoryGui), "m_dragItem");

	private static readonly FieldInfo DragInventoryField = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");

	private static readonly FieldInfo DragAmountField = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");

	private static readonly MethodInfo SetupDragItemMethod = AccessTools.Method(typeof(InventoryGui), "SetupDragItem", new Type[3]
	{
		typeof(ItemData),
		typeof(Inventory),
		typeof(int)
	}, (Type[])null);

	private static readonly Vector3[] UiCorners = (Vector3[])(object)new Vector3[4];

	internal static void Attach(InventoryGui gui)
	{
		Detach(gui);
		_gui = gui;
		Button val = (((Object)(object)gui.m_stackAllButton != (Object)null) ? gui.m_stackAllButton : gui.m_takeAllButton);
		if ((Object)(object)val == (Object)null)
		{
			Plugin.LogInstance.LogWarning((object)"BestAutoSort could not find a vanilla inventory button to clone.");
			return;
		}
		_template = val;
		Transform val2 = (((Object)(object)gui.m_container != (Object)null && (Object)(object)((Transform)gui.m_container).parent != (Object)null) ? ((Transform)gui.m_container).parent : ((Component)val).transform.parent);
		Transform parent = GetPlayerSideParent(gui) ?? val2;
		RectTransform val3 = (RectTransform)((Component)val).transform;
		_quickStackButton = CloneButton(val, parent, "BestAutoSort_QuickStackNearby", "Stack");
		RectTransform val4 = (RectTransform)((Component)_quickStackButton).transform;
		val4.anchorMin = val3.anchorMin;
		val4.anchorMax = val3.anchorMax;
		val4.pivot = val3.pivot;
		val4.sizeDelta = val3.sizeDelta;
		((UnityEvent)_quickStackButton.onClick).AddListener(new UnityAction(Plugin.Instance.QuickStackNearby));
		VanillaTooltip.Attach(((Component)_quickStackButton).gameObject, (Component)(object)gui, "Stack", "Deposit eligible items, then refill Replenish targets from nearby chests.");
		_trashButton = CloneButton(val, parent, "BestAutoSort_Trash", "Trash");
		ButtonClickedEvent onClick = _trashButton.onClick;
		object obj = _003C_003EO._003C0_003E__DestroyHeldItem;
		if (obj == null)
		{
			UnityAction val5 = DestroyHeldItem;
			_003C_003EO._003C0_003E__DestroyHeldItem = val5;
			obj = (object)val5;
		}
		((UnityEvent)onClick).AddListener((UnityAction)obj);
		VanillaTooltip.Attach(((Component)_trashButton).gameObject, (Component)(object)gui, "Trash", "Permanently destroy the item stack currently held by the cursor. This cannot be undone.");
		_sortButton = CloneButton(val, val2, "BestAutoSort_SortChest", "Sort");
		RectTransform val6 = (RectTransform)((Component)_sortButton).transform;
		val6.anchorMin = val3.anchorMin;
		val6.anchorMax = val3.anchorMax;
		val6.pivot = val3.pivot;
		val6.sizeDelta = val3.sizeDelta;
		((UnityEvent)_sortButton.onClick).AddListener(new UnityAction(Plugin.Instance.SortOpenChest));
		_rulesButton = CloneButton(val, val2, "BestAutoSort_StorageRules", "Storage\nAuto");
		RectTransform val7 = (RectTransform)((Component)_rulesButton).transform;
		val7.anchorMin = val3.anchorMin;
		val7.anchorMax = val3.anchorMax;
		val7.pivot = val3.pivot;
		val7.sizeDelta = val3.sizeDelta;
		_rulesLabel = ((Component)_rulesButton).GetComponentInChildren<TMP_Text>(true);
		if ((Object)(object)_rulesLabel != (Object)null)
		{
			_rulesLabel.fontSize = Mathf.Min(_rulesLabel.fontSize, 13f);
		}
		_rulesTooltip = VanillaTooltip.Attach(((Component)_rulesButton).gameObject, (Component)(object)gui, "Storage", "Configure what this chest accepts.\nCurrent rule: Auto");
		ButtonClickedEvent onClick2 = _rulesButton.onClick;
		object obj2 = _003C_003EO._003C1_003E__OpenRuleEditor;
		if (obj2 == null)
		{
			UnityAction val8 = OpenRuleEditor;
			_003C_003EO._003C1_003E__OpenRuleEditor = val8;
			obj2 = (object)val8;
		}
		((UnityEvent)onClick2).AddListener((UnityAction)obj2);
		_reinforcedUpgradeButton = CloneButton(val, val2, "BestAutoSort_UpgradeReinforced", "Upgrade:\nReinforced");
		ConfigureUpgradeButton(_reinforcedUpgradeButton, val3, 1);
		_blackMetalUpgradeButton = CloneButton(val, val2, "BestAutoSort_UpgradeBlackMetal", "Upgrade:\nBlack Metal");
		ConfigureUpgradeButton(_blackMetalUpgradeButton, val3, 2);
		_graustenUpgradeButton = CloneButton(val, val2, "BestAutoSort_UpgradeGrausten", "Upgrade:\nGrausten");
		ConfigureUpgradeButton(_graustenUpgradeButton, val3, 3);
		UpdateVisibility();
	}

	internal static void UpdateVisibility()
	{
		if ((Object)(object)_gui == (Object)null)
		{
			return;
		}
		bool flag = ModConfig.Enabled.Value && ModConfig.ShowButtons.Value && InventoryGui.IsVisible();
		Container val = InventoryAccess.CurrentContainer(_gui);
		bool flag2 = flag && ChestUpgradeService.CanUpgradeTo(val, 1);
		bool flag3 = flag && ChestUpgradeService.CanUpgradeTo(val, 2);
		bool flag4 = flag && ChestUpgradeService.CanUpgradeTo(val, 3);
		if (flag)
		{
			UpdatePlayerSideLayout();
			UpdateChestButtonLayout(flag2, flag3, flag4);
		}
		if ((Object)(object)_quickStackButton != (Object)null)
		{
			((Component)_quickStackButton).gameObject.SetActive(flag);
		}
		if ((Object)(object)_trashButton != (Object)null)
		{
			((Component)_trashButton).gameObject.SetActive(flag);
		}
		if ((Object)(object)_sortButton != (Object)null)
		{
			((Component)_sortButton).gameObject.SetActive(flag && (Object)(object)val != (Object)null);
		}
		if ((Object)(object)_rulesButton != (Object)null)
		{
			((Component)_rulesButton).gameObject.SetActive(flag && (Object)(object)val != (Object)null);
			if ((Object)(object)val != (Object)(object)_labelContainer)
			{
				RefreshRuleLabel();
			}
		}
		if ((Object)(object)_reinforcedUpgradeButton != (Object)null)
		{
			((Component)_reinforcedUpgradeButton).gameObject.SetActive(flag2);
		}
		if ((Object)(object)_blackMetalUpgradeButton != (Object)null)
		{
			((Component)_blackMetalUpgradeButton).gameObject.SetActive(flag3);
		}
		if ((Object)(object)_graustenUpgradeButton != (Object)null)
		{
			((Component)_graustenUpgradeButton).gameObject.SetActive(flag4);
		}
	}

	internal static void RefreshRuleLabel()
	{
		if (!((Object)(object)_gui == (Object)null) && !((Object)(object)_rulesLabel == (Object)null))
		{
			Container val = (_labelContainer = InventoryAccess.CurrentContainer(_gui));
			string text = (((Object)(object)val == (Object)null) ? "Auto" : ChestRuleStore.ShortSummary(ChestRuleStore.Read(val)));
			_rulesLabel.text = "Storage\n" + text;
			string text2 = "Configure what this chest accepts.\nCurrent rule: " + text;
			if ((Object)(object)_rulesTooltip == (Object)null && (Object)(object)_rulesButton != (Object)null)
			{
				_rulesTooltip = VanillaTooltip.Attach(((Component)_rulesButton).gameObject, (Component)(object)_gui, "Storage", text2);
			}
			else if ((Object)(object)_rulesTooltip != (Object)null)
			{
				_rulesTooltip.m_text = text2;
			}
		}
	}

	internal static void Detach(InventoryGui? gui = null)
	{
		if (!((Object)(object)gui != (Object)null) || !((Object)(object)_gui != (Object)null) || !((Object)(object)gui != (Object)(object)_gui))
		{
			if ((Object)(object)_quickStackButton != (Object)null)
			{
				Object.Destroy((Object)(object)((Component)_quickStackButton).gameObject);
			}
			if ((Object)(object)_trashButton != (Object)null)
			{
				Object.Destroy((Object)(object)((Component)_trashButton).gameObject);
			}
			if ((Object)(object)_sortButton != (Object)null)
			{
				Object.Destroy((Object)(object)((Component)_sortButton).gameObject);
			}
			if ((Object)(object)_rulesButton != (Object)null)
			{
				Object.Destroy((Object)(object)((Component)_rulesButton).gameObject);
			}
			if ((Object)(object)_reinforcedUpgradeButton != (Object)null)
			{
				Object.Destroy((Object)(object)((Component)_reinforcedUpgradeButton).gameObject);
			}
			if ((Object)(object)_blackMetalUpgradeButton != (Object)null)
			{
				Object.Destroy((Object)(object)((Component)_blackMetalUpgradeButton).gameObject);
			}
			if ((Object)(object)_graustenUpgradeButton != (Object)null)
			{
				Object.Destroy((Object)(object)((Component)_graustenUpgradeButton).gameObject);
			}
			ChestRuleEditor.Close(gui);
			_quickStackButton = null;
			_trashButton = null;
			_sortButton = null;
			_rulesButton = null;
			_reinforcedUpgradeButton = null;
			_blackMetalUpgradeButton = null;
			_graustenUpgradeButton = null;
			_rulesLabel = null;
			_rulesTooltip = null;
			_labelContainer = null;
			_template = null;
			_gui = null;
		}
	}

	private static void UpdateChestButtonLayout(bool includeReinforced, bool includeBlackMetal, bool includeGrausten)
	{
		if (!((Object)(object)_gui == (Object)null) && !((Object)(object)_sortButton == (Object)null) && !((Object)(object)_rulesButton == (Object)null) && !((Object)(object)_reinforcedUpgradeButton == (Object)null) && !((Object)(object)_blackMetalUpgradeButton == (Object)null) && !((Object)(object)_graustenUpgradeButton == (Object)null) && !((Object)(object)_template == (Object)null) && !((Object)(object)_gui.m_container == (Object)null))
		{
			RectTransform val = (RectTransform)((Component)_template).transform;
			Rect rect = val.rect;
			float num;
			if (!((rect).width > 0f))
			{
				num = Mathf.Max(120f, val.sizeDelta.x);
			}
			else
			{
				rect = val.rect;
				num = (rect).width;
			}
			float num2 = Mathf.Min(145f, num);
			// Chest column matches the Trash/Stack width (player-side layout runs first).
			if ((Object)(object)_trashButton != (Object)null)
			{
				RectTransform trashRect = (RectTransform)((Component)_trashButton).transform;
				float trashWidth = trashRect.rect.width;
				if (trashWidth <= 0f)
				{
					trashWidth = trashRect.sizeDelta.x;
				}
				if (trashWidth > 0f)
				{
					num2 = trashWidth;
				}
			}
			rect = val.rect;
			float num3;
			if (!((rect).height > 0f))
			{
				num3 = Mathf.Max(34f, val.sizeDelta.y);
			}
			else
			{
				rect = val.rect;
				num3 = (rect).height;
			}
			float num4 = num3;
			List<RectTransform> list = new List<RectTransform>
			{
				(RectTransform)((Component)_rulesButton).transform,
				(RectTransform)((Component)_sortButton).transform
			};
			if (includeReinforced)
			{
				list.Add((RectTransform)((Component)_reinforcedUpgradeButton).transform);
			}
			if (includeBlackMetal)
			{
				list.Add((RectTransform)((Component)_blackMetalUpgradeButton).transform);
			}
			if (includeGrausten)
			{
				list.Add((RectTransform)((Component)_graustenUpgradeButton).transform);
			}
			RectTransform val2 = (RectTransform)((Component)_rulesButton).transform.parent;
			Vector3[] array = (Vector3[])(object)new Vector3[4];
			_gui.m_container.GetWorldCorners(array);
			Vector3 val3 = ((Transform)val2).InverseTransformPoint(array[2]);
			Vector2 val5 = default(Vector2);
			for (int i = 0; i < list.Count; i++)
			{
				RectTransform val4 = list[i];
				val5 = new Vector2(0.5f, 0.5f);
				val4.anchorMax = val5;
				val4.anchorMin = val5;
				val4.pivot = new Vector2(0f, 1f);
				val4.sizeDelta = new Vector2(num2, num4);
				((Transform)val4).localPosition = new Vector3(val3.x + 8f, ChestToolbarLayout.TopForButton(val3.y, i, num4, 8f), ((Transform)val4).localPosition.z);
			}
		}
	}

	private static void UpdatePlayerSideLayout()
	{
		if ((Object)(object)_gui == (Object)null || (Object)(object)_quickStackButton == (Object)null || (Object)(object)_trashButton == (Object)null || !TryGetWeightPanel(_gui, out RectTransform weightPanel) || !TryGetArmorPanel(_gui, out RectTransform armorPanel))
		{
			return;
		}
		Transform parent = ((Transform)weightPanel).parent;
		RectTransform val = (RectTransform)(object)((parent is RectTransform) ? parent : null);
		if (val != null)
		{
			RectTransform val2 = (RectTransform)((Component)_quickStackButton).transform;
			RectTransform val3 = (RectTransform)((Component)_trashButton).transform;
			if ((Object)(object)((Transform)val2).parent != (Object)(object)val)
			{
				((Transform)val2).SetParent((Transform)(object)val, false);
			}
			if ((Object)(object)((Transform)val3).parent != (Object)(object)val)
			{
				((Transform)val3).SetParent((Transform)(object)val, false);
			}
			weightPanel.GetWorldCorners(UiCorners);
			Vector3 val4 = ((Transform)val).InverseTransformPoint(UiCorners[0]);
			Vector3 val5 = ((Transform)val).InverseTransformPoint(UiCorners[2]);
			armorPanel.GetWorldCorners(UiCorners);
			float y = ((Transform)val).InverseTransformPoint(UiCorners[0]).y;
			Transform obj = ((Transform)_gui.m_player).Find("Bkg");
			((RectTransform)(((object)((obj is RectTransform) ? obj : null)) ?? ((object)_gui.m_player))).GetWorldCorners(UiCorners);
			float y2 = ((Transform)val).InverseTransformPoint(UiCorners[0]).y;
			float x = val4.x;
			float x2 = val5.x;
			float y3 = val5.y;
			Rect rect = val2.rect;
			float preferredStackHeight;
			if (!((rect).height > 0f))
			{
				preferredStackHeight = 38f;
			}
			else
			{
				rect = val2.rect;
				preferredStackHeight = (rect).height;
			}
			PlayerSideControlPlacement playerSideControlPlacement = PlayerSideControlLayout.Calculate(x, x2, y3, y, y2, preferredStackHeight, 6f, 18f, ModConfig.PlayerSideControlsOffsetX.Value);
			Vector2 val6 = default(Vector2);
			val6 = new Vector2(0.5f, 0.5f);
			val2.anchorMax = val6;
			val2.anchorMin = val6;
			val2.pivot = new Vector2(0.5f, 0f);
			val2.sizeDelta = new Vector2(playerSideControlPlacement.Width, playerSideControlPlacement.StackHeight);
			((Transform)val2).localPosition = new Vector3(playerSideControlPlacement.CenterX, playerSideControlPlacement.StackBottomY, ((Transform)val2).localPosition.z);
			val6 = new Vector2(0.5f, 0.5f);
			val3.anchorMax = val6;
			val3.anchorMin = val6;
			val3.pivot = new Vector2(0.5f, 0f);
			val3.sizeDelta = new Vector2(playerSideControlPlacement.Width, playerSideControlPlacement.TrashHeight);
			((Transform)val3).localPosition = new Vector3(playerSideControlPlacement.CenterX, playerSideControlPlacement.TrashBottomY, ((Transform)val3).localPosition.z);
			((Transform)val2).SetAsFirstSibling();
			((Transform)val3).SetAsFirstSibling();
		}
	}

	private static Transform? GetPlayerSideParent(InventoryGui gui)
	{
		if (!TryGetWeightPanel(gui, out RectTransform weightPanel))
		{
			return null;
		}
		return ((Transform)weightPanel).parent;
	}

	private static bool TryGetWeightPanel(InventoryGui gui, out RectTransform weightPanel)
	{
		weightPanel = null;
		if ((Object)(object)gui.m_weight == (Object)null)
		{
			return false;
		}
		RectTransform rectTransform = gui.m_weight.rectTransform;
		Transform parent = ((Transform)rectTransform).parent;
		weightPanel = (RectTransform)(((object)((parent is RectTransform) ? parent : null)) ?? ((object)rectTransform));
		return ((Transform)weightPanel).parent is RectTransform;
	}

	private static bool TryGetArmorPanel(InventoryGui gui, out RectTransform armorPanel)
	{
		armorPanel = null;
		if ((Object)(object)gui.m_armor == (Object)null)
		{
			return false;
		}
		RectTransform rectTransform = gui.m_armor.rectTransform;
		Transform parent = ((Transform)rectTransform).parent;
		armorPanel = (RectTransform)(((object)((parent is RectTransform) ? parent : null)) ?? ((object)rectTransform));
		return ((Transform)armorPanel).parent is RectTransform;
	}

	private static void DestroyHeldItem()
	{
		if (!Plugin.IsActive || (Object)(object)_gui == (Object)null || (Object)(object)Player.m_localPlayer == (Object)null)
		{
			return;
		}
		object value = DragItemField.GetValue(_gui);
		ItemData val = (ItemData)((value is ItemData) ? value : null);
		object value2 = DragInventoryField.GetValue(_gui);
		Inventory val2 = (Inventory)((value2 is Inventory) ? value2 : null);
		int num = ((DragAmountField.GetValue(_gui) is int num2) ? num2 : 0);
		if (val == null || val2 == null || num <= 0)
		{
			((Character)Player.m_localPlayer).Message((MessageType)2, "Hold an item over Trash first.", 0, (Sprite)null, false);
			return;
		}
		if (!val2.ContainsItem(val))
		{
			SetupDragItemMethod.Invoke(_gui, new object[3] { null, null, 1 });
			return;
		}
		int destroyAmount = TrashOperationPlanner.GetDestroyAmount(val.m_stack, num, val.m_shared.m_questItem);
		if (val.m_shared.m_questItem)
		{
			((Character)Player.m_localPlayer).Message((MessageType)2, "Quest items cannot be destroyed.", 0, (Sprite)null, false);
		}
		else if (destroyAmount > 0)
		{
			((Humanoid)Player.m_localPlayer).RemoveEquipAction(val);
			((Humanoid)Player.m_localPlayer).UnequipItem(val, false);
			Container trashContainer = (val2 != ((Humanoid)Player.m_localPlayer).GetInventory()) ? InventoryAccess.CurrentContainer(_gui) : null;
			bool trashShared = (Object)(object)trashContainer != (Object)null && (Object)(object)trashContainer.GetInventory() == (Object)(object)val2
				&& ChestTxService.IsShared(trashContainer);
			if (trashShared && !trashContainer.IsOwner())
			{
				// Trash from a foreign chest: Take by transaction, destroy what arrives (settles nowhere).
				TxOpItem opItem = ChestTxService.SnapshotItem(val, destroyAmount, -1, -1);
				if (opItem == null)
					return;
				List<TxOpItem> trashItems = new List<TxOpItem>();
				trashItems.Add(opItem);
				string trashName = val.m_shared.m_name;
				ChestTxService.RequestTakeCustom(trashContainer, trashItems, false, delegate (List<DecodedTake> results, TxStatus status, uint rev)
					{
						int destroyed = 0;
						if (results != null)
						{
							for (int i = 0; i < results.Count; i++)
							{
								if (results[i] != null)
									destroyed += results[i].Accepted;
							}
						}
						SetupDragItemMethod.Invoke(_gui, new object[3] { null, null, 1 });
						if (destroyed > 0)
							((Character)Player.m_localPlayer).Message((MessageType)2, $"Destroyed {destroyed} × {Localization.instance.Localize(trashName)}.", 0, (Sprite)null, false);
						else
							((Character)Player.m_localPlayer).Message((MessageType)2, "The item could not be destroyed safely.", 0, (Sprite)null, false);
					});
				return;
			}
			if (!val2.RemoveItem(val, destroyAmount))
			{
				((Character)Player.m_localPlayer).Message((MessageType)2, "The item could not be destroyed safely.", 0, (Sprite)null, false);
				return;
			}
			SetupDragItemMethod.Invoke(_gui, new object[3] { null, null, 1 });
			((Character)Player.m_localPlayer).Message((MessageType)2, $"Destroyed {destroyAmount} × {Localization.instance.Localize(val.m_shared.m_name)}.", 0, (Sprite)null, false);
		}
	}

	private static void ConfigureUpgradeButton(Button button, RectTransform template, int targetTier)
	{
		RectTransform val = (RectTransform)((Component)button).transform;
		val.anchorMin = template.anchorMin;
		val.anchorMax = template.anchorMax;
		val.pivot = template.pivot;
		val.sizeDelta = template.sizeDelta;
		TMP_Text componentInChildren = ((Component)button).GetComponentInChildren<TMP_Text>(true);
		if ((Object)(object)componentInChildren != (Object)null)
		{
			componentInChildren.fontSize = Mathf.Min(componentInChildren.fontSize, 13f);
		}
		((UnityEvent)button.onClick).AddListener((UnityAction)delegate
		{
			Plugin.Instance.UpgradeOpenChest(targetTier);
		});
	}

	private static void OpenRuleEditor()
	{
		if (Plugin.IsActive && !((Object)(object)_gui == (Object)null))
		{
			Container val = InventoryAccess.CurrentContainer(_gui);
			if (!((Object)(object)val == (Object)null))
			{
				// The manager saves the rule (SetRule tx) — the editor opens right away.
				ChestRuleEditor.Open(_gui, val);
			}
		}
	}

	private static Button CloneButton(Button template, Transform parent, string name, string label)
	{
		GameObject val = Object.Instantiate<GameObject>(((Component)template).gameObject, parent, false);
		((Object)val).name = name;
		Button component = val.GetComponent<Button>();
		((UnityEventBase)component.onClick).RemoveAllListeners();
		Localize[] componentsInChildren = val.GetComponentsInChildren<Localize>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			((Behaviour)componentsInChildren[i]).enabled = false;
		}
		TMP_Text componentInChildren = val.GetComponentInChildren<TMP_Text>(true);
		if ((Object)(object)componentInChildren != (Object)null)
		{
			componentInChildren.text = label;
			componentInChildren.fontSize = Mathf.Min(componentInChildren.fontSize, 16f);
		}
		val.SetActive(true);
		return component;
	}
}
