using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BestAutoSort.Core;
using BestAutoSort.Tx;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BestAutoSort.Runtime;

internal static class ChestRuleEditor
{
	private sealed class ButtonVisual
	{
		internal RectTransform Rect { get; }

		internal Image Image { get; }

		internal Button Button { get; }

		internal TMP_Text Label { get; }

		internal Color NormalImageColor { get; }

		internal Color NormalLabelColor { get; }

		internal ButtonVisual(RectTransform rect, Image image, Button button, TMP_Text label, Color normalImageColor, Color normalLabelColor)
		{
			Rect = rect;
			Image = image;
			Button = button;
			Label = label;
			NormalImageColor = normalImageColor;
			NormalLabelColor = normalLabelColor;
		}
	}

	private sealed class ItemChoice
	{
		internal string PrefabName { get; }

		internal string DisplayName { get; }

		internal ItemCategory Category { get; }

		internal Sprite? Icon { get; }

		internal string SearchText { get; }

		internal ItemChoice(string prefabName, string displayName, ItemCategory category, Sprite? icon)
		{
			PrefabName = prefabName;
			DisplayName = displayName;
			Category = category;
			Icon = icon;
			ItemCategoryDefinition itemCategoryDefinition = ItemCategoryCatalog.Get(category);
			SearchText = displayName + " " + prefabName + " " + itemCategoryDefinition.DisplayName + " " + itemCategoryDefinition.Group;
		}
	}

	[CompilerGenerated]
	private static class _003C_003EO
	{
		public static UnityAction _003C0_003E__Save;

		public static Func<Image, float> _003C1_003E__ImageArea;
	}

	[Serializable]
	[CompilerGenerated]
	private sealed class _003C_003Ec
	{
		public static readonly _003C_003Ec _003C_003E9 = new _003C_003Ec();

		public static UnityAction _003C_003E9__49_0;

		public static UnityAction _003C_003E9__49_1;

		public static Action<string> _003C_003E9__55_0;

		public static Func<ItemCategoryDefinition, bool> _003C_003E9__55_1;

		public static Action<string> _003C_003E9__56_0;

		public static UnityAction<string> _003C_003E9__59_0;

		public static Func<ItemChoice, bool> _003C_003E9__61_0;

		public static Func<ItemChoice, int> _003C_003E9__64_0;

		public static Func<ItemChoice, string> _003C_003E9__64_1;

		public static Func<ItemChoice, string> _003C_003E9__64_2;

		public static Func<Image, bool> _003C_003E9__81_0;

		internal void _003CBuild_003Eb__49_0()
		{
			Close();
		}

		internal void _003CBuild_003Eb__49_1()
		{
			Close();
		}

		internal void _003CBuildCategoryView_003Eb__55_0(string groupId)
		{
			_activeCategoryGroupId = groupId;
			RenderScope();
		}

		internal bool _003CBuildCategoryView_003Eb__55_1(ItemCategoryDefinition definition)
		{
			return string.Equals(definition.GroupId, _activeCategoryGroupId, StringComparison.OrdinalIgnoreCase);
		}

		internal void _003CBuildItemsView_003Eb__56_0(string groupId)
		{
			_activeItemGroupId = groupId;
			RenderScope();
		}

		internal void _003CCreateSearchField_003Eb__59_0(string value)
		{
			_itemSearch = value ?? string.Empty;
			RefreshItemTiles();
		}

		internal bool _003CRenderItemTiles_003Eb__61_0(ItemChoice choice)
		{
			return string.Equals(ItemCategoryCatalog.Get(choice.Category).GroupId, _activeItemGroupId, StringComparison.OrdinalIgnoreCase);
		}

		internal int _003CCollectItemChoices_003Eb__64_0(ItemChoice choice)
		{
			return ItemCategoryCatalog.SortRank(choice.Category);
		}

		internal string _003CCollectItemChoices_003Eb__64_1(ItemChoice choice)
		{
			return choice.DisplayName;
		}

		internal string _003CCollectItemChoices_003Eb__64_2(ItemChoice choice)
		{
			return choice.PrefabName;
		}

		internal bool _003CFindContainerBackground_003Eb__81_0(Image candidate)
		{
			if ((Object)(object)candidate.sprite != (Object)null)
			{
				if (((Object)((Component)candidate).gameObject).name.IndexOf("bkg", StringComparison.OrdinalIgnoreCase) < 0)
				{
					return ((Object)((Component)candidate).gameObject).name.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
				}
				return true;
			}
			return false;
		}
	}

	private const string Prefix = "BestAutoSort_RuleEditor_";

	private const float PanelWidth = 900f;

	private const float PanelHeight = 650f;

	private const float RailWidth = 190f;

	private const float Gap = 10f;

	private static readonly Color Gold;

	private static readonly Color GoldFaint;

	private static readonly Color SelectedGold;

	private static readonly Color Cream;

	private static readonly Color Muted;

	private static InventoryGui? _gui;

	private static Container? _container;

	private static GameObject? _root;

	private static RectTransform? _body;

	private static RectTransform? _itemContent;

	private static TMP_Text? _status;

	private static Image? _panelImage;

	private static Image? _sourcePanelImage;

	private static Button? _buttonTemplate;

	private static TMP_Text? _titleTemplate;

	private static Sprite? _buttonSprite;

	private static Image.Type _buttonImageType;

	private static Color _buttonImageColor;

	private static Color _buttonLabelColor;

	private static TMP_FontAsset? _bodyFont;

	private static Material? _bodyFontMaterial;

	private static Sprite? _panelSprite;

	private static Image.Type _panelImageType;

	private static Color _panelImageColor;

	private static Material? _panelMaterial;

	private static bool _panelFillCenter;

	private static bool _panelPreserveAspect;

	private static float _panelPixelsPerUnitMultiplier;

	private static bool _panelUseSpriteMesh;

	private static ChestRuleScope _scope;

	private static string _selectedGroupId;

	private static string _activeCategoryGroupId;

	private static string _activeItemGroupId;

	private static string _itemSearch;

	private static readonly HashSet<ItemCategory> SelectedCategories;

	private static readonly HashSet<string> SelectedItems;

	private static readonly Dictionary<ChestRuleScope, ButtonVisual> ModeButtons;

	private static readonly Dictionary<string, ButtonVisual> BrowserGroupButtons;

	private static readonly List<ItemChoice> ItemChoices;

	internal static bool IsOpen => (Object)(object)_root != (Object)null;

	internal static void Open(InventoryGui gui, Container container)
	{
		if (!((Object)(object)gui == (Object)null) && !((Object)(object)container == (Object)null))
		{
			Close();
			_gui = gui;
			_container = container;
			ChestStorageRule chestStorageRule = ChestRuleStore.Read(container);
			_scope = chestStorageRule.Scope;
			_selectedGroupId = ((chestStorageRule.Scope == ChestRuleScope.Group) ? chestStorageRule.GroupId : "materials");
			_activeCategoryGroupId = FirstCategoryGroup(chestStorageRule) ?? "materials";
			_activeItemGroupId = "materials";
			_itemSearch = string.Empty;
			SelectedCategories.Clear();
			SelectedCategories.UnionWith(chestStorageRule.Categories);
			SelectedItems.Clear();
			SelectedItems.UnionWith(chestStorageRule.ItemPrefabNames);
			CollectItemChoices(container);
			Build(gui, container);
		}
	}

	internal static void Update()
	{
		if ((Object)(object)_root == (Object)null)
		{
			return;
		}
		if ((Object)(object)_gui == (Object)null || (Object)(object)_container == (Object)null || !InventoryGui.IsVisible() || (Object)(object)InventoryAccess.CurrentContainer(_gui) != (Object)(object)_container)
		{
			Close();
			return;
		}
		RefreshPanelBackground();
		if (Input.GetKeyDown((KeyCode)27))
		{
			Close();
		}
	}

	internal static void Close(InventoryGui? gui = null)
	{
		if (!((Object)(object)gui != (Object)null) || !((Object)(object)_gui != (Object)null) || !((Object)(object)gui != (Object)(object)_gui))
		{
			if ((Object)(object)_root != (Object)null)
			{
				_root.SetActive(false);
				Object.Destroy((Object)(object)_root);
			}
			_root = null;
			_body = null;
			_itemContent = null;
			_status = null;
			_panelImage = null;
			_sourcePanelImage = null;
			_gui = null;
			_container = null;
			ModeButtons.Clear();
			BrowserGroupButtons.Clear();
			ItemChoices.Clear();
		}
	}

	private static void Build(InventoryGui gui, Container container)
	{
		Transform transform = ((Component)gui).transform;
		RectTransform val = (RectTransform)(object)(((transform is RectTransform) ? transform : null) ?? throw new InvalidOperationException("InventoryGui does not use a RectTransform root."));
		_root = new GameObject("BestAutoSort_RuleEditor_Overlay", new Type[2]
		{
			typeof(RectTransform),
			typeof(Image)
		});
		_root.transform.SetParent((Transform)(object)val, false);
		_root.transform.SetAsLastSibling();
		RectTransform val2 = (RectTransform)_root.transform;
		Stretch(val2, Vector2.zero, Vector2.zero);
		Image component = _root.GetComponent<Image>();
		((Graphic)component).color = new Color(0f, 0f, 0f, 0.52f);
		((Graphic)component).raycastTarget = true;
		CaptureVanillaStyle(gui, container);
		RectTransform val3 = CreateRect("BestAutoSort_RuleEditor_Panel", (Transform)(object)val2);
		Vector2 val4 = default(Vector2);
		val4 = new Vector2(0.5f, 0.5f);
		val3.anchorMax = val4;
		val3.anchorMin = val4;
		val3.pivot = new Vector2(0.5f, 0.5f);
		val3.anchoredPosition = Vector2.zero;
		val3.sizeDelta = new Vector2(900f, 650f);
		_panelImage = ((Component)val3).gameObject.AddComponent<Image>();
		ApplyPanelStyle(_panelImage);
		TMP_Text obj = CreateVanillaTitle((Transform)(object)val3, "BestAutoSort_RuleEditor_Title", "Storage Rule", 30f);
		obj.alignment = (TextAlignmentOptions)514;
		SetTopRect((RectTransform)obj.transform, 70f, 8f, 70f, 40f);
		string content = (((Object)(object)gui.m_containerName != (Object)null && !string.IsNullOrWhiteSpace(gui.m_containerName.text)) ? gui.m_containerName.text : ((Localization.instance != null) ? Localization.instance.Localize(container.m_name) : container.m_name));
		TMP_Text obj2 = CreateText((Transform)(object)val3, "BestAutoSort_RuleEditor_Subtitle", content, 13f, Gold, (TextAlignmentOptions)514);
		obj2.fontStyle = (FontStyles)1;
		SetTopRect((RectTransform)obj2.transform, 70f, 43f, 70f, 24f);
		ButtonVisual buttonVisual = CreateButton((Transform)(object)val3, "BestAutoSort_RuleEditor_Close", "×", 16f, (TextAlignmentOptions)514);
		SetTopRect(buttonVisual.Rect, 842f, 10f, 14f, 38f);
		ButtonClickedEvent onClick = buttonVisual.Button.onClick;
		object obj3 = _003C_003Ec._003C_003E9__49_0;
		if (obj3 == null)
		{
			UnityAction val5 = delegate
			{
				Close();
			};
			_003C_003Ec._003C_003E9__49_0 = val5;
			obj3 = (object)val5;
		}
		((UnityEvent)onClick).AddListener((UnityAction)obj3);
		RectTransform obj4 = CreateRect("BestAutoSort_RuleEditor_Modes", (Transform)(object)val3);
		SetTopRect(obj4, 24f, 72f, 24f, 36f);
		BuildModeButtons(obj4);
		RectTransform obj5 = CreateRect("BestAutoSort_RuleEditor_ModeRule", (Transform)(object)val3);
		SetTopRect(obj5, 24f, 110f, 24f, 1f);
		((Graphic)((Component)obj5).gameObject.AddComponent<Image>()).color = GoldFaint;
		_body = CreateRect("BestAutoSort_RuleEditor_Body", (Transform)(object)val3);
		SetInsets(_body, 24f, 120f, 24f, 68f);
		RectTransform obj6 = CreateRect("BestAutoSort_RuleEditor_FooterRule", (Transform)(object)val3);
		SetTopRect(obj6, 24f, 589f, 24f, 1f);
		((Graphic)((Component)obj6).gameObject.AddComponent<Image>()).color = GoldFaint;
		_status = CreateText((Transform)(object)val3, "BestAutoSort_RuleEditor_Status", ScopeHelp(_scope), 12f, Muted, (TextAlignmentOptions)4097);
		SetTopRect((RectTransform)_status.transform, 24f, 596f, 360f, 34f);
		_status.textWrappingMode = (TextWrappingModes)1;
		ButtonVisual buttonVisual2 = CreateButton((Transform)(object)val3, "BestAutoSort_RuleEditor_Cancel", "Cancel", 15f, (TextAlignmentOptions)514);
		SetTopRect(buttonVisual2.Rect, 570f, 598f, 220f, 34f);
		ButtonClickedEvent onClick2 = buttonVisual2.Button.onClick;
		object obj7 = _003C_003Ec._003C_003E9__49_1;
		if (obj7 == null)
		{
			UnityAction val6 = delegate
			{
				Close();
			};
			_003C_003Ec._003C_003E9__49_1 = val6;
			obj7 = (object)val6;
		}
		((UnityEvent)onClick2).AddListener((UnityAction)obj7);
		ButtonVisual buttonVisual3 = CreateButton((Transform)(object)val3, "BestAutoSort_RuleEditor_Save", "Save rule", 15f, (TextAlignmentOptions)514);
		SetTopRect(buttonVisual3.Rect, 690f, 598f, 24f, 34f);
		SetButtonSelected(buttonVisual3, selected: true);
		ButtonClickedEvent onClick3 = buttonVisual3.Button.onClick;
		object obj8 = _003C_003EO._003C0_003E__Save;
		if (obj8 == null)
		{
			UnityAction val7 = Save;
			_003C_003EO._003C0_003E__Save = val7;
			obj8 = (object)val7;
		}
		((UnityEvent)onClick3).AddListener((UnityAction)obj8);
		RenderScope();
		EventSystem current = EventSystem.current;
		if ((Object)(object)current != (Object)null && ModeButtons.TryGetValue(_scope, out ButtonVisual value))
		{
			current.SetSelectedGameObject(((Component)value.Button).gameObject);
		}
	}

	private static void BuildModeButtons(RectTransform parent)
	{
		ModeButtons.Clear();
		ChestRuleScope[] array = new ChestRuleScope[4]
		{
			ChestRuleScope.Auto,
			ChestRuleScope.Group,
			ChestRuleScope.Categories,
			ChestRuleScope.Items
		};
		string[] array2 = new string[4] { "Auto", "Group", "Category", "Items" };
		float num = 208.5f;
		for (int i = 0; i < array.Length; i++)
		{
			ChestRuleScope selectedScope = array[i];
			ButtonVisual buttonVisual = CreateButton((Transform)(object)parent, "BestAutoSort_RuleEditor_Mode_" + array2[i], array2[i], 12f, (TextAlignmentOptions)514);
			buttonVisual.Rect.anchorMin = new Vector2(0f, 0f);
			buttonVisual.Rect.anchorMax = new Vector2(0f, 1f);
			buttonVisual.Rect.pivot = new Vector2(0f, 0.5f);
			buttonVisual.Rect.anchoredPosition = new Vector2((float)i * (num + 6f), 0f);
			buttonVisual.Rect.sizeDelta = new Vector2(num, 0f);
			((UnityEvent)buttonVisual.Button.onClick).AddListener((UnityAction)delegate
			{
				_scope = selectedScope;
				SetStatus(ScopeHelp(_scope), error: false);
				RenderScope();
			});
			ModeButtons.Add(selectedScope, buttonVisual);
		}
		UpdateModeAppearance();
	}

	private static void RenderScope()
	{
		if (!((Object)(object)_body == (Object)null))
		{
			ClearChildren(_body);
			BrowserGroupButtons.Clear();
			UpdateModeAppearance();
			switch (_scope)
			{
			case ChestRuleScope.Group:
				BuildGroupView(_body);
				break;
			case ChestRuleScope.Categories:
				BuildCategoryView(_body);
				break;
			case ChestRuleScope.Items:
				BuildItemsView(_body);
				break;
			default:
				BuildAutoView(_body);
				break;
			}
		}
	}

	private static void BuildAutoView(RectTransform body)
	{
		TMP_Text obj = CreateText((Transform)(object)body, "BestAutoSort_RuleEditor_AutoHeading", "Automatic by chest contents", 18f, Gold, (TextAlignmentOptions)257);
		obj.fontStyle = (FontStyles)1;
		SetTopRect((RectTransform)obj.transform, 18f, 12f, 18f, 38f);
		TMP_Text obj2 = CreateText((Transform)(object)body, "BestAutoSort_RuleEditor_AutoText", "BestAutoSort reads the items already stored in this chest as seeds. Exact item matches always qualify. With category matching enabled, one item also seeds its detailed category.\n\nChoose GROUP for every item under a main heading, CATEGORY for one or more detailed categories, or ITEMS for exact combinations such as Corewood + Finewood.", 15f, Cream, (TextAlignmentOptions)257);
		SetTopRect((RectTransform)obj2.transform, 18f, 62f, 18f, 138f);
		obj2.textWrappingMode = (TextWrappingModes)1;
		CreateExampleCard(body, new Vector2(18f, -226f), "Seed", "Wood in chest", "Accepts the Wood category");
		CreateExampleCard(body, new Vector2(292f, -226f), "Exact", "Obsidian in chest", "Always accepts Obsidian");
		CreateExampleCard(body, new Vector2(566f, -226f), "Empty", "No stored items", "No automatic destination");
	}

	private static void CreateExampleCard(RectTransform parent, Vector2 position, string tag, string title, string detail)
	{
		RectTransform obj = CreateRect("BestAutoSort_RuleEditor_Example_" + tag, (Transform)(object)parent);
		Vector2 val = default(Vector2);
		val = new Vector2(0f, 1f);
		obj.anchorMax = val;
		obj.anchorMin = val;
		obj.pivot = new Vector2(0f, 1f);
		obj.anchoredPosition = position;
		obj.sizeDelta = new Vector2(256f, 112f);
		Image obj2 = ((Component)obj).gameObject.AddComponent<Image>();
		ApplyButtonSprite(obj2);
		((Graphic)obj2).color = _buttonImageColor;
		((Graphic)obj2).raycastTarget = false;
		TMP_Text obj3 = CreateText((Transform)(object)obj, "BestAutoSort_RuleEditor_Tag", tag, 11f, Gold, (TextAlignmentOptions)257);
		obj3.fontStyle = (FontStyles)1;
		SetTopRect((RectTransform)obj3.transform, 12f, 8f, 12f, 22f);
		TMP_Text obj4 = CreateText((Transform)(object)obj, "BestAutoSort_RuleEditor_CardTitle", title, 14f, Cream, (TextAlignmentOptions)257);
		obj4.fontStyle = (FontStyles)1;
		SetTopRect((RectTransform)obj4.transform, 12f, 32f, 12f, 26f);
		TMP_Text obj5 = CreateText((Transform)(object)obj, "BestAutoSort_RuleEditor_CardDetail", detail, 12f, Muted, (TextAlignmentOptions)257);
		SetTopRect((RectTransform)obj5.transform, 12f, 62f, 12f, 36f);
		obj5.textWrappingMode = (TextWrappingModes)1;
	}

	private static void BuildGroupView(RectTransform body)
	{
		CreateSectionIntro(body, "Main category", "Choose one broad destination. Every subcategory beneath it will be accepted.");
		float num = 66f;
		float num2 = 419f;
		Vector2 val = default(Vector2);
		for (int i = 0; i < ItemCategoryCatalog.Groups.Count; i++)
		{
			ItemCategoryGroupDefinition itemCategoryGroupDefinition = ItemCategoryCatalog.Groups[i];
			int num3 = i % 2;
			int num4 = i / 2;
			ButtonVisual buttonVisual = CreateButton((Transform)(object)body, "BestAutoSort_RuleEditor_Group_" + itemCategoryGroupDefinition.Id, itemCategoryGroupDefinition.DisplayName, 14f, (TextAlignmentOptions)4097);
			RectTransform rect = buttonVisual.Rect;
			RectTransform rect2 = buttonVisual.Rect;
			val = new Vector2(0f, 1f);
			rect2.anchorMax = val;
			rect.anchorMin = val;
			buttonVisual.Rect.pivot = new Vector2(0f, 1f);
			buttonVisual.Rect.anchoredPosition = new Vector2((float)num3 * (num2 + 14f), 0f - (num + (float)num4 * 58f));
			buttonVisual.Rect.sizeDelta = new Vector2(num2, 48f);
			string groupId = itemCategoryGroupDefinition.Id;
			((UnityEvent)buttonVisual.Button.onClick).AddListener((UnityAction)delegate
			{
				_selectedGroupId = groupId;
				UpdateGroupSelection();
			});
			BrowserGroupButtons[itemCategoryGroupDefinition.Id] = buttonVisual;
		}
		UpdateGroupSelection();
	}

	private static void BuildCategoryView(RectTransform body)
	{
		CreateSectionIntro(body, "Detailed categories", "Select one or more categories. Main headings remain filters, not a third tier.");
		RectTransform obj = CreateRect("BestAutoSort_RuleEditor_CategoryRail", (Transform)(object)body);
		SetInsets(obj, 0f, 62f, 662f, 0f);
		RectTransform val = CreateRect("BestAutoSort_RuleEditor_CategoryContent", (Transform)(object)body);
		SetInsets(val, 200f, 62f, 0f, 0f);
		BuildBrowserRail(obj, _activeCategoryGroupId, includeAll: false, delegate(string groupId)
		{
			_activeCategoryGroupId = groupId;
			RenderScope();
		});
		RectTransform val2 = CreateScrollContent(val, "BestAutoSort_RuleEditor_CategoryScroll", out ScrollRect _);
		List<ItemCategoryDefinition> list = ItemCategoryCatalog.All.Where((ItemCategoryDefinition definition) => string.Equals(definition.GroupId, _activeCategoryGroupId, StringComparison.OrdinalIgnoreCase)).ToList();
		float num = 0f;
		foreach (ItemCategoryDefinition item in list)
		{
			ButtonVisual visual = CreateButton((Transform)(object)val2, "BestAutoSort_RuleEditor_Category_" + item.Id, item.DisplayName, 13f, (TextAlignmentOptions)4097);
			visual.Rect.anchorMin = new Vector2(0f, 1f);
			visual.Rect.anchorMax = new Vector2(1f, 1f);
			visual.Rect.pivot = new Vector2(0.5f, 1f);
			visual.Rect.anchoredPosition = new Vector2(0f, 0f - num);
			visual.Rect.sizeDelta = new Vector2(-4f, 42f);
			TMP_Text check = CreateText((Transform)(object)visual.Rect, "BestAutoSort_RuleEditor_Check", "✓", 16f, Gold, (TextAlignmentOptions)514);
			SetRightFill((RectTransform)check.transform, 34f, 8f, 4f, 8f);
			SetCategoryVisual(visual, check, SelectedCategories.Contains(item.Category));
			ItemCategory category = item.Category;
			((UnityEvent)visual.Button.onClick).AddListener((UnityAction)delegate
			{
				if (!SelectedCategories.Add(category))
				{
					SelectedCategories.Remove(category);
				}
				SetCategoryVisual(visual, check, SelectedCategories.Contains(category));
				RefreshBrowserRailCounts(items: false);
			});
			num += 48f;
		}
		SetContentHeight(val2, num);
		RefreshBrowserRailCounts(items: false);
	}

	private static void BuildItemsView(RectTransform body)
	{
		SetTopRect(CreateSearchField(body), 0f, 6f, 0f, 36f);
		RectTransform obj = CreateRect("BestAutoSort_RuleEditor_ItemRail", (Transform)(object)body);
		SetInsets(obj, 0f, 52f, 662f, 0f);
		RectTransform val = CreateRect("BestAutoSort_RuleEditor_ItemContent", (Transform)(object)body);
		SetInsets(val, 200f, 52f, 0f, 0f);
		BuildBrowserRail(obj, _activeItemGroupId, includeAll: true, delegate(string groupId)
		{
			_activeItemGroupId = groupId;
			RenderScope();
		});
		_itemContent = CreateScrollContent(val, "BestAutoSort_RuleEditor_ItemScroll", out ScrollRect _);
		RenderItemTiles(_itemContent);
		RefreshBrowserRailCounts(items: true);
	}

	private static void BuildBrowserRail(RectTransform rail, string activeGroupId, bool includeAll, Action<string> onSelected)
	{
		float num = 0f;
		if (includeAll)
		{
			AddRailButton(rail, "all", "All items", activeGroupId, num, onSelected);
			num += 34f;
		}
		foreach (ItemCategoryGroupDefinition group in ItemCategoryCatalog.Groups)
		{
			AddRailButton(rail, group.Id, group.DisplayName, activeGroupId, num, onSelected);
			num += 34f;
		}
	}

	private static void AddRailButton(RectTransform rail, string groupId, string label, string activeGroupId, float y, Action<string> onSelected)
	{
		ButtonVisual buttonVisual = CreateButton((Transform)(object)rail, "BestAutoSort_RuleEditor_Rail_" + groupId, label, 10.5f, (TextAlignmentOptions)4097);
		buttonVisual.Rect.anchorMin = new Vector2(0f, 1f);
		buttonVisual.Rect.anchorMax = new Vector2(1f, 1f);
		buttonVisual.Rect.pivot = new Vector2(0.5f, 1f);
		buttonVisual.Rect.anchoredPosition = new Vector2(0f, 0f - y);
		buttonVisual.Rect.sizeDelta = new Vector2(0f, 30f);
		SetButtonSelected(buttonVisual, string.Equals(groupId, activeGroupId, StringComparison.OrdinalIgnoreCase));
		((UnityEvent)buttonVisual.Button.onClick).AddListener((UnityAction)delegate
		{
			onSelected(groupId);
		});
		BrowserGroupButtons[groupId] = buttonVisual;
	}

	private static RectTransform CreateSearchField(RectTransform parent)
	{
		RectTransform val = CreateRect("BestAutoSort_RuleEditor_Search", (Transform)(object)parent);
		Image val2 = ((Component)val).gameObject.AddComponent<Image>();
		ApplyButtonSprite(val2);
		((Graphic)val2).color = _buttonImageColor;
		TMP_InputField input = ((Component)val).gameObject.AddComponent<TMP_InputField>();
		RectTransform val3 = CreateRect("BestAutoSort_RuleEditor_SearchViewport", (Transform)(object)val);
		Stretch(val3, new Vector2(8f, 2f), new Vector2(-30f, -2f));
		((Component)val3).gameObject.AddComponent<RectMask2D>();
		TextMeshProUGUI val4 = (TextMeshProUGUI)CreateText((Transform)(object)val3, "BestAutoSort_RuleEditor_SearchText", string.Empty, 12f, Cream, (TextAlignmentOptions)4097);
		Stretch((RectTransform)((TMP_Text)val4).transform, Vector2.zero, Vector2.zero);
		((TMP_Text)val4).fontStyle = (FontStyles)0;
		TextMeshProUGUI val5 = (TextMeshProUGUI)CreateText((Transform)(object)val3, "BestAutoSort_RuleEditor_SearchPlaceholder", "Search known items", 12f, Muted, (TextAlignmentOptions)4097);
		Stretch((RectTransform)((TMP_Text)val5).transform, Vector2.zero, Vector2.zero);
		((TMP_Text)val5).fontStyle = (FontStyles)0;
		((Selectable)input).targetGraphic = (Graphic)(object)val2;
		input.textViewport = val3;
		input.textComponent = (TMP_Text)(object)val4;
		input.placeholder = (Graphic)(object)val5;
		input.lineType = (TMP_InputField.LineType)0;
		input.characterLimit = 64;
		input.caretColor = Gold;
		input.selectionColor = new Color(SelectedGold.r, SelectedGold.g, SelectedGold.b, 0.55f);
		input.SetTextWithoutNotify(_itemSearch);
		((UnityEvent<string>)(object)input.onValueChanged).AddListener((UnityAction<string>)delegate(string value)
		{
			_itemSearch = value ?? string.Empty;
			RefreshItemTiles();
		});
		ButtonVisual buttonVisual = CreateButton((Transform)(object)val, "BestAutoSort_RuleEditor_SearchClear", "×", 15f, (TextAlignmentOptions)514);
		SetRightFill(buttonVisual.Rect, 26f, 2f, 2f, 2f);
		((UnityEvent)buttonVisual.Button.onClick).AddListener((UnityAction)delegate
		{
			_itemSearch = string.Empty;
			input.SetTextWithoutNotify(string.Empty);
			RefreshItemTiles();
		});
		return val;
	}

	private static void RefreshItemTiles()
	{
		if (!((Object)(object)_itemContent == (Object)null))
		{
			ClearChildren(_itemContent);
			RenderItemTiles(_itemContent);
			RefreshBrowserRailCounts(items: true);
		}
	}

	private static void RenderItemTiles(RectTransform content)
	{
		IEnumerable<ItemChoice> source = ItemChoices;
		if (!string.Equals(_activeItemGroupId, "all", StringComparison.OrdinalIgnoreCase))
		{
			source = source.Where((ItemChoice choice) => string.Equals(ItemCategoryCatalog.Get(choice.Category).GroupId, _activeItemGroupId, StringComparison.OrdinalIgnoreCase));
		}
		string query = (_itemSearch ?? string.Empty).Trim();
		if (query.Length > 0)
		{
			source = source.Where((ItemChoice choice) => choice.SearchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
		}
		List<ItemChoice> list = source.ToList();
		float num = 620f;
		Transform parent = ((Transform)content).parent;
		RectTransform val = (RectTransform)(object)((parent is RectTransform) ? parent : null);
		if (val != null)
		{
			Rect rect = val.rect;
			if ((rect).width > 10f)
			{
				rect = val.rect;
				num = (rect).width;
			}
		}
		float num2 = (num - 14f - 4f) / 3f;
		Vector2 val2 = default(Vector2);
		for (int num3 = 0; num3 < list.Count; num3++)
		{
			ItemChoice itemChoice = list[num3];
			int num4 = num3 % 3;
			int num5 = num3 / 3;
			ButtonVisual visual = CreateButton((Transform)(object)content, "BestAutoSort_RuleEditor_Item_" + num3, string.Empty, 11f, (TextAlignmentOptions)514);
			RectTransform rect2 = visual.Rect;
			RectTransform rect3 = visual.Rect;
			val2 = new Vector2(0f, 1f);
			rect3.anchorMax = val2;
			rect2.anchorMin = val2;
			visual.Rect.pivot = new Vector2(0f, 1f);
			visual.Rect.anchoredPosition = new Vector2((float)num4 * (num2 + 7f), (float)(-num5) * 65f);
			visual.Rect.sizeDelta = new Vector2(num2, 58f);
			((Component)visual.Label).gameObject.SetActive(false);
			RectTransform obj = CreateRect("BestAutoSort_RuleEditor_Icon", (Transform)(object)visual.Rect);
			val2 = new Vector2(0f, 0.5f);
			obj.anchorMax = val2;
			obj.anchorMin = val2;
			obj.pivot = new Vector2(0f, 0.5f);
			obj.anchoredPosition = new Vector2(8f, 0f);
			obj.sizeDelta = new Vector2(40f, 40f);
			Image val3 = ((Component)obj).gameObject.AddComponent<Image>();
			val3.sprite = itemChoice.Icon;
			val3.preserveAspect = true;
			((Graphic)val3).raycastTarget = false;
			if ((Object)(object)itemChoice.Icon == (Object)null)
			{
				((Graphic)val3).color = new Color(1f, 1f, 1f, 0.2f);
			}
			TMP_Text obj2 = CreateText((Transform)(object)visual.Rect, "BestAutoSort_RuleEditor_ItemName", itemChoice.DisplayName, 11.5f, Cream, (TextAlignmentOptions)257);
			SetTopRect((RectTransform)obj2.transform, 54f, 7f, 24f, 24f);
			obj2.textWrappingMode = (TextWrappingModes)1;
			obj2.overflowMode = (TextOverflowModes)1;
			SetTopRect((RectTransform)CreateText((Transform)(object)visual.Rect, "BestAutoSort_RuleEditor_ItemCategory", ItemCategoryCatalog.Get(itemChoice.Category).DisplayName, 8.5f, Gold, (TextAlignmentOptions)1025).transform, 54f, 32f, 24f, 18f);
			TMP_Text check = CreateText((Transform)(object)visual.Rect, "BestAutoSort_RuleEditor_ItemCheck", "✓", 14f, Gold, (TextAlignmentOptions)514);
			SetRightFill((RectTransform)check.transform, 22f, 2f, 2f, 36f);
			SetCategoryVisual(visual, check, SelectedItems.Contains(itemChoice.PrefabName));
			string prefabName = itemChoice.PrefabName;
			((UnityEvent)visual.Button.onClick).AddListener((UnityAction)delegate
			{
				if (!SelectedItems.Add(prefabName))
				{
					SelectedItems.Remove(prefabName);
				}
				SetCategoryVisual(visual, check, SelectedItems.Contains(prefabName));
				RefreshBrowserRailCounts(items: true);
			});
			if ((Object)(object)_gui != (Object)null)
			{
				VanillaTooltip.Attach(((Component)visual.Button).gameObject, (Component)(object)_gui, itemChoice.DisplayName, itemChoice.PrefabName);
			}
		}
		int num6 = (list.Count + 3 - 1) / 3;
		SetContentHeight(content, Mathf.Max(20f, (float)num6 * 65f));
		if (list.Count == 0)
		{
			SetTopRect((RectTransform)CreateText((Transform)(object)content, "BestAutoSort_RuleEditor_NoItems", "No known items match this filter", 12f, Muted, (TextAlignmentOptions)257).transform, 8f, 8f, 8f, 26f);
		}
	}

	private static void RefreshBrowserRailCounts(bool items)
	{
		foreach (KeyValuePair<string, ButtonVisual> browserGroupButton in BrowserGroupButtons)
		{
			string id = browserGroupButton.Key;
			if (string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
			{
				browserGroupButton.Value.Label.text = "All items" + CountSuffix(SelectedItems.Count);
			}
			else
			{
				if (!ItemCategoryCatalog.TryGetGroup(id, out ItemCategoryGroupDefinition definition) || definition == null)
				{
					continue;
				}
				int count = (items ? SelectedItems.Count((string name) => ItemChoices.Any((ItemChoice choice) => string.Equals(choice.PrefabName, name, StringComparison.OrdinalIgnoreCase) && string.Equals(ItemCategoryCatalog.Get(choice.Category).GroupId, id, StringComparison.OrdinalIgnoreCase))) : SelectedCategories.Count((ItemCategory category) => string.Equals(ItemCategoryCatalog.Get(category).GroupId, id, StringComparison.OrdinalIgnoreCase)));
				browserGroupButton.Value.Label.text = definition.DisplayName + CountSuffix(count);
			}
		}
	}

	private static string CountSuffix(int count)
	{
		if (count <= 0)
		{
			return string.Empty;
		}
		return "  [" + count + "]";
	}

	private static void CollectItemChoices(Container container)
	{
		ItemChoices.Clear();
		Dictionary<string, ItemChoice> dictionary = new Dictionary<string, ItemChoice>(StringComparer.OrdinalIgnoreCase);
		Player localPlayer = Player.m_localPlayer;
		if ((Object)(object)ObjectDB.instance != (Object)null)
		{
			foreach (GameObject item in ObjectDB.instance.m_items)
			{
				if (!((Object)(object)item == (Object)null))
				{
					ItemDrop component = item.GetComponent<ItemDrop>();
					if (!((Object)(object)component == (Object)null) && component.m_itemData?.m_shared != null && (Object)(object)localPlayer != (Object)null && localPlayer.IsKnownMaterial(component.m_itemData.m_shared.m_name))
					{
						AddItemChoice(dictionary, ((Object)item).name, component.m_itemData, component.m_itemData.GetIcon());
					}
				}
			}
		}
		if ((Object)(object)localPlayer != (Object)null)
		{
			foreach (ItemData allItem in ((Humanoid)localPlayer).GetInventory().GetAllItems())
			{
				AddItemChoice(dictionary, ValheimItemCategoryClassifier.PrefabName(allItem), allItem, allItem.GetIcon());
			}
		}
		foreach (ItemData allItem2 in container.GetInventory().GetAllItems())
		{
			AddItemChoice(dictionary, ValheimItemCategoryClassifier.PrefabName(allItem2), allItem2, allItem2.GetIcon());
		}
		foreach (string selectedItem in SelectedItems)
		{
			if (!dictionary.ContainsKey(selectedItem))
			{
				dictionary[selectedItem] = new ItemChoice(selectedItem, selectedItem, ItemCategory.Miscellaneous, null);
			}
		}
		ItemChoices.AddRange(dictionary.Values.OrderBy((ItemChoice choice) => ItemCategoryCatalog.SortRank(choice.Category)).ThenBy<ItemChoice, string>((ItemChoice choice) => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy<ItemChoice, string>((ItemChoice choice) => choice.PrefabName, StringComparer.OrdinalIgnoreCase));
	}

	private static void AddItemChoice(IDictionary<string, ItemChoice> choices, string prefabName, ItemData item, Sprite? icon)
	{
		string text = ChestStorageRule.NormalizePrefabName(prefabName);
		if (text.Length != 0 && !choices.ContainsKey(text) && !item.m_shared.m_questItem && item.m_shared.m_autoStack)
		{
			string text2 = ((Localization.instance != null) ? Localization.instance.Localize(item.m_shared.m_name) : item.m_shared.m_name);
			choices[text] = new ItemChoice(text, string.IsNullOrWhiteSpace(text2) ? text : text2, ValheimItemCategoryClassifier.Classify(item), icon);
		}
	}

	private static void Save()
	{
		if (!Plugin.IsActive || (Object)(object)_container == (Object)null)
		{
			return;
		}
		Container container = _container;
		try
		{
			ChestStorageRule rule = _scope switch
			{
				ChestRuleScope.Group => ChestStorageRule.ForGroup(_selectedGroupId), 
				ChestRuleScope.Categories => ChestStorageRule.ForCategories(SelectedCategories), 
				ChestRuleScope.Items => ChestStorageRule.ForItems(SelectedItems), 
				_ => ChestStorageRule.Automatic(), 
			};
			if (!ChestTxService.IsShared(container) || container.IsOwner())
			{
				SaveLocal(container, rule);
				return;
			}
			// Non-owner: the manager applies the rule via transaction.
			string serialized = ChestStorageRuleCodec.Serialize(rule);
			ChestTxService.RequestSetRule(container, serialized);
			Player localPlayer = Player.m_localPlayer;
			if (localPlayer != null)
			{
				((Character)localPlayer).Message((MessageType)2, "Chest storage rule sent. It applies once the chest manager confirms.", 0, (Sprite)null, false);
			}
			InventoryButtons.RefreshRuleLabel();
			Close();
		}
		catch (ArgumentException ex)
		{
			SetStatus(ex.Message, error: true);
		}
	}

	private static void SaveLocal(Container container, ChestStorageRule rule)
	{
		try
		{
			if (!ChestRuleStore.TryWrite(container, rule, out string error))
			{
				SetStatus(error, error: true);
				return;
			}
			Player localPlayer = Player.m_localPlayer;
			if (localPlayer != null)
			{
				((Character)localPlayer).Message((MessageType)2, "Chest storage rule: " + ChestRuleStore.ShortSummary(rule), 0, (Sprite)null, false);
			}
			InventoryButtons.RefreshRuleLabel();
			Close();
		}
		catch (ArgumentException ex)
		{
			SetStatus(ex.Message, error: true);
		}
	}

	private static void CreateSectionIntro(RectTransform parent, string heading, string description)
	{
		TMP_Text obj = CreateText((Transform)(object)parent, "BestAutoSort_RuleEditor_SectionTitle", heading, 14f, Gold, (TextAlignmentOptions)257);
		obj.fontStyle = (FontStyles)1;
		SetTopRect((RectTransform)obj.transform, 0f, 0f, 0f, 24f);
		TMP_Text obj2 = CreateText((Transform)(object)parent, "BestAutoSort_RuleEditor_SectionDetail", description, 12f, Muted, (TextAlignmentOptions)257);
		SetTopRect((RectTransform)obj2.transform, 0f, 26f, 0f, 29f);
		obj2.textWrappingMode = (TextWrappingModes)1;
	}

	private static RectTransform CreateScrollContent(RectTransform host, string name, out ScrollRect scroll)
	{
		RectTransform val = CreateRect(name + "Viewport", (Transform)(object)host);
		Stretch(val, Vector2.zero, Vector2.zero);
		((Component)val).gameObject.AddComponent<RectMask2D>();
		RectTransform val2 = CreateRect(name + "Content", (Transform)(object)val);
		val2.anchorMin = new Vector2(0f, 1f);
		val2.anchorMax = new Vector2(1f, 1f);
		val2.pivot = new Vector2(0.5f, 1f);
		val2.anchoredPosition = Vector2.zero;
		val2.sizeDelta = Vector2.zero;
		scroll = ((Component)host).gameObject.AddComponent<ScrollRect>();
		scroll.viewport = val;
		scroll.content = val2;
		scroll.horizontal = false;
		scroll.vertical = true;
		scroll.movementType = (ScrollRect.MovementType)2;
		scroll.scrollSensitivity = ModConfig.RuleEditorScrollSensitivity.Value;
		return val2;
	}

	private static void SetContentHeight(RectTransform content, float height)
	{
		content.SetSizeWithCurrentAnchors((RectTransform.Axis)1, Mathf.Max(1f, height));
	}

	private static void UpdateModeAppearance()
	{
		foreach (KeyValuePair<ChestRuleScope, ButtonVisual> modeButton in ModeButtons)
		{
			bool selected = modeButton.Key == _scope;
			SetButtonSelected(modeButton.Value, selected);
		}
	}

	private static void UpdateGroupSelection()
	{
		foreach (KeyValuePair<string, ButtonVisual> browserGroupButton in BrowserGroupButtons)
		{
			bool selected = string.Equals(browserGroupButton.Key, _selectedGroupId, StringComparison.OrdinalIgnoreCase);
			SetButtonSelected(browserGroupButton.Value, selected);
		}
	}

	private static void SetCategoryVisual(ButtonVisual visual, TMP_Text check, bool selected)
	{
		SetButtonSelected(visual, selected);
		((Component)check).gameObject.SetActive(selected);
	}

	private static void SetButtonSelected(ButtonVisual visual, bool selected)
	{
		((Graphic)visual.Image).color = (selected ? SelectedGold : visual.NormalImageColor);
		((Graphic)visual.Label).color = (selected ? Color.white : visual.NormalLabelColor);
	}

	private static void SetStatus(string message, bool error)
	{
		if (!((Object)(object)_status == (Object)null))
		{
			_status.text = message;
			((Graphic)_status).color = (Color)(error ? new Color(1f, 0.55f, 0.42f, 1f) : Muted);
		}
	}

	private static string ScopeHelp(ChestRuleScope scope)
	{
		return scope switch
		{
			ChestRuleScope.Group => "One main category; all of its detailed categories are accepted.", 
			ChestRuleScope.Categories => "One or more detailed categories; selected categories can span main headings.", 
			ChestRuleScope.Items => "One or more exact items; ideal for Wood alone or Corewood + Finewood.", 
			_ => "Automatic routing uses the items already stored in the chest as seeds.", 
		};
	}

	private static string? FirstCategoryGroup(ChestStorageRule rule)
	{
		using (IEnumerator<ItemCategory> enumerator = rule.Categories.GetEnumerator())
		{
			if (enumerator.MoveNext())
			{
				return ItemCategoryCatalog.Get(enumerator.Current).GroupId;
			}
		}
		return null;
	}

	private static void CaptureVanillaStyle(InventoryGui gui, Container container)
	{
		_buttonTemplate = (((Object)(object)gui.m_stackAllButton != (Object)null) ? gui.m_stackAllButton : gui.m_takeAllButton);
		Image val = (((Object)(object)_buttonTemplate != (Object)null) ? ((Component)_buttonTemplate).GetComponent<Image>() : null);
		if ((Object)(object)val == (Object)null && (Object)(object)gui.m_tabCraft != (Object)null)
		{
			val = ((Component)gui.m_tabCraft).GetComponent<Image>();
		}
		_buttonSprite = (((Object)(object)val != (Object)null) ? val.sprite : null);
		_buttonImageType = (Image.Type)(((Object)(object)val != (Object)null) ? ((int)val.type) : 0);
		_buttonImageColor = (((Object)(object)val != (Object)null) ? ((Graphic)val).color : Color.white);
		TMP_Text val2 = (((Object)(object)_buttonTemplate != (Object)null) ? ((Component)_buttonTemplate).GetComponentInChildren<TMP_Text>(true) : null);
		_buttonLabelColor = (((Object)(object)val2 != (Object)null) ? ((Graphic)val2).color : Gold);
		TMP_Text val3 = gui.m_recipeDecription;
		if ((Object)(object)val3 == (Object)null || (Object)(object)val3.font == (Object)null)
		{
			val3 = val2;
		}
		if ((Object)(object)val3 == (Object)null || (Object)(object)val3.font == (Object)null)
		{
			val3 = gui.m_containerName;
		}
		if ((Object)(object)val3 == (Object)null || (Object)(object)val3.font == (Object)null)
		{
			val3 = gui.m_recipeName;
		}
		_bodyFont = (((Object)(object)val3 != (Object)null) ? val3.font : null);
		_bodyFontMaterial = (((Object)(object)val3 != (Object)null) ? val3.fontSharedMaterial : null);
		_titleTemplate = (((Object)(object)gui.m_containerName != (Object)null) ? gui.m_containerName : gui.m_recipeName);
		_sourcePanelImage = FindContainerBackground(gui, container);
		_panelSprite = (((Object)(object)container.m_bkg != (Object)null) ? container.m_bkg : (((Object)(object)_sourcePanelImage != (Object)null) ? _sourcePanelImage.sprite : null));
		_panelImageType = (Image.Type)((!((Object)(object)_sourcePanelImage != (Object)null)) ? (((Object)(object)_panelSprite != (Object)null && _panelSprite.border != Vector4.zero) ? 1 : 0) : ((int)_sourcePanelImage.type));
		_panelImageColor = (((Object)(object)_sourcePanelImage != (Object)null) ? ((Graphic)_sourcePanelImage).color : Color.white);
		_panelMaterial = (((Object)(object)_sourcePanelImage != (Object)null) ? ((Graphic)_sourcePanelImage).material : null);
		_panelFillCenter = (Object)(object)_sourcePanelImage == (Object)null || _sourcePanelImage.fillCenter;
		_panelPreserveAspect = (Object)(object)_sourcePanelImage != (Object)null && _sourcePanelImage.preserveAspect;
		_panelPixelsPerUnitMultiplier = (((Object)(object)_sourcePanelImage != (Object)null) ? _sourcePanelImage.pixelsPerUnitMultiplier : 1f);
		_panelUseSpriteMesh = (Object)(object)_sourcePanelImage != (Object)null && _sourcePanelImage.useSpriteMesh;
	}

	private static ButtonVisual CreateButton(Transform parent, string name, string label, float fontSize, TextAlignmentOptions alignment = (TextAlignmentOptions)514)
	{
		if ((Object)(object)_buttonTemplate != (Object)null)
		{
			GameObject val = Object.Instantiate<GameObject>(((Component)_buttonTemplate).gameObject, parent, false);
			((Object)val).name = name;
			Localize[] componentsInChildren = val.GetComponentsInChildren<Localize>(true);
			for (int i = 0; i < componentsInChildren.Length; i++)
			{
				((Behaviour)componentsInChildren[i]).enabled = false;
			}
			Button val2 = val.GetComponent<Button>() ?? val.AddComponent<Button>();
			((UnityEventBase)val2.onClick).RemoveAllListeners();
			Graphic targetGraphic = ((Selectable)val2).targetGraphic;
			Image val3 = (Image)(object)(((Selectable)val2).targetGraphic = (Graphic)(((object)((targetGraphic is Image) ? targetGraphic : null)) ?? ((object)(val.GetComponent<Image>() ?? val.AddComponent<Image>()))));
			TMP_Text val5 = val.GetComponentInChildren<TMP_Text>(true) ?? CreateText(val.transform, name + "Label", label, fontSize, _buttonLabelColor, alignment);
			val5.text = label;
			val5.fontSize = fontSize;
			val5.alignment = alignment;
			((Graphic)val5).raycastTarget = false;
			val.SetActive(true);
			return new ButtonVisual((RectTransform)val.transform, val3, val2, val5, ((Graphic)val3).color, ((Graphic)val5).color);
		}
		RectTransform obj = CreateRect(name, parent);
		Image val6 = ((Component)obj).gameObject.AddComponent<Image>();
		ApplyButtonSprite(val6);
		((Graphic)val6).color = _buttonImageColor;
		Button val7 = ((Component)obj).gameObject.AddComponent<Button>();
		((Selectable)val7).targetGraphic = (Graphic)(object)val6;
		ColorBlock colors = ((Selectable)val7).colors;
		(colors).normalColor = Color.white;
		(colors).highlightedColor = new Color(1.16f, 1.16f, 1.16f, 1f);
		(colors).pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
		(colors).selectedColor = (colors).highlightedColor;
		(colors).disabledColor = new Color(0.42f, 0.42f, 0.42f, 0.7f);
		(colors).colorMultiplier = 1f;
		((Selectable)val7).colors = colors;
		TMP_Text val8 = CreateText((Transform)(object)obj, name + "Label", label, fontSize, _buttonLabelColor, alignment);
		Stretch((RectTransform)val8.transform, new Vector2(10f, 2f), new Vector2(-10f, -2f));
		return new ButtonVisual(obj, val6, val7, val8, ((Graphic)val6).color, ((Graphic)val8).color);
	}

	private static void ApplyButtonSprite(Image image)
	{
		image.sprite = _buttonSprite;
		image.type = _buttonImageType;
	}

	private static void ApplyPanelStyle(Image image)
	{
		image.sprite = _panelSprite;
		image.type = _panelImageType;
		((Graphic)image).color = _panelImageColor;
		((Graphic)image).material = _panelMaterial;
		image.fillCenter = _panelFillCenter;
		image.preserveAspect = _panelPreserveAspect;
		image.pixelsPerUnitMultiplier = _panelPixelsPerUnitMultiplier;
		image.useSpriteMesh = _panelUseSpriteMesh;
		((Graphic)image).raycastTarget = true;
	}

	private static Image? FindContainerBackground(InventoryGui gui, Container container)
	{
		Sprite bkg = container.m_bkg;
		IEnumerable<Image> enumerable = (((Object)(object)gui.m_container != (Object)null) ? ((Component)gui.m_container).GetComponentsInChildren<Image>(true) : Array.Empty<Image>());
		Image val = FindLargestSpriteMatch(enumerable, bkg);
		if ((Object)(object)val != (Object)null)
		{
			return val;
		}
		val = FindLargestSpriteMatch(((Component)gui).GetComponentsInChildren<Image>(true), bkg);
		if ((Object)(object)val != (Object)null)
		{
			return val;
		}
		return enumerable.Where((Image candidate) => (Object)(object)candidate.sprite != (Object)null && (((Object)((Component)candidate).gameObject).name.IndexOf("bkg", StringComparison.OrdinalIgnoreCase) >= 0 || ((Object)((Component)candidate).gameObject).name.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0)).OrderByDescending(ImageArea).FirstOrDefault();
	}

	private static Image? FindLargestSpriteMatch(IEnumerable<Image> images, Sprite? expected)
	{
		if ((Object)(object)expected == (Object)null)
		{
			return null;
		}
		return images.Where((Image candidate) => (Object)(object)candidate != (Object)null && (Object)(object)candidate.sprite == (Object)(object)expected).OrderByDescending(ImageArea).FirstOrDefault();
	}

	private static float ImageArea(Image image)
	{
		Rect rect = ((Graphic)image).rectTransform.rect;
		float width = (rect).width;
		rect = ((Graphic)image).rectTransform.rect;
		return Mathf.Abs(width * (rect).height);
	}

	private static void RefreshPanelBackground()
	{
		if (!((Object)(object)_panelImage == (Object)null))
		{
			if ((Object)(object)_sourcePanelImage != (Object)null)
			{
				_panelImage.sprite = _sourcePanelImage.sprite;
				_panelImage.type = _sourcePanelImage.type;
				((Graphic)_panelImage).color = ((Graphic)_sourcePanelImage).color;
				((Graphic)_panelImage).material = ((Graphic)_sourcePanelImage).material;
				_panelImage.fillCenter = _sourcePanelImage.fillCenter;
				_panelImage.preserveAspect = _sourcePanelImage.preserveAspect;
				_panelImage.pixelsPerUnitMultiplier = _sourcePanelImage.pixelsPerUnitMultiplier;
				_panelImage.useSpriteMesh = _sourcePanelImage.useSpriteMesh;
				((Graphic)_panelImage).canvasRenderer.SetColor(((Graphic)_sourcePanelImage).canvasRenderer.GetColor());
			}
			else if ((Object)(object)_container != (Object)null && (Object)(object)_panelImage.sprite != (Object)(object)_container.m_bkg)
			{
				_panelImage.sprite = _container.m_bkg;
			}
		}
	}

	private static TMP_Text CreateVanillaTitle(Transform parent, string name, string content, float fontSize)
	{
		if ((Object)(object)_titleTemplate == (Object)null)
		{
			return CreateText(parent, name, content, fontSize, Gold, (TextAlignmentOptions)514);
		}
		GameObject val = Object.Instantiate<GameObject>(((Component)_titleTemplate).gameObject, parent, false);
		((Object)val).name = name;
		Localize[] componentsInChildren = val.GetComponentsInChildren<Localize>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			((Behaviour)componentsInChildren[i]).enabled = false;
		}
		TMP_Text obj = val.GetComponent<TMP_Text>() ?? val.GetComponentInChildren<TMP_Text>(true);
		obj.text = content;
		obj.enableAutoSizing = false;
		obj.fontSize = fontSize;
		((Graphic)obj).raycastTarget = false;
		val.SetActive(true);
		return obj;
	}

	private static TMP_Text CreateText(Transform parent, string name, string content, float fontSize, Color color, TextAlignmentOptions alignment)
	{
		TextMeshProUGUI val = ((Component)CreateRect(name, parent)).gameObject.AddComponent<TextMeshProUGUI>();
		((TMP_Text)val).text = content;
		((TMP_Text)val).font = _bodyFont;
		if ((Object)(object)_bodyFontMaterial != (Object)null)
		{
			((TMP_Text)val).fontSharedMaterial = _bodyFontMaterial;
		}
		((TMP_Text)val).fontSize = fontSize;
		((TMP_Text)val).fontStyle = (FontStyles)0;
		((Graphic)val).color = color;
		((TMP_Text)val).alignment = alignment;
		((TMP_Text)val).textWrappingMode = (TextWrappingModes)0;
		((TMP_Text)val).outlineWidth = 0.08f;
		((TMP_Text)val).outlineColor = (Color32)new Color(0f, 0f, 0f, 0.85f);
		((Graphic)val).raycastTarget = false;
		return (TMP_Text)(object)val;
	}

	private static RectTransform CreateRect(string name, Transform parent)
	{
		GameObject val = new GameObject(name, new Type[1] { typeof(RectTransform) });
		val.transform.SetParent(parent, false);
		return (RectTransform)val.transform;
	}

	private static void SetInsets(RectTransform rect, float left, float top, float right, float bottom)
	{
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.offsetMin = new Vector2(left, bottom);
		rect.offsetMax = new Vector2(0f - right, 0f - top);
	}

	private static void SetTopRect(RectTransform rect, float left, float top, float right, float height)
	{
		rect.anchorMin = new Vector2(0f, 1f);
		rect.anchorMax = Vector2.one;
		rect.pivot = new Vector2(0.5f, 1f);
		rect.offsetMin = new Vector2(left, 0f - top - height);
		rect.offsetMax = new Vector2(0f - right, 0f - top);
	}

	private static void SetRightFill(RectTransform rect, float width, float top, float right, float bottom)
	{
		rect.anchorMin = new Vector2(1f, 0f);
		rect.anchorMax = Vector2.one;
		rect.pivot = new Vector2(1f, 0.5f);
		rect.offsetMin = new Vector2(0f - right - width, bottom);
		rect.offsetMax = new Vector2(0f - right, 0f - top);
	}

	private static void Stretch(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
	{
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.offsetMin = offsetMin;
		rect.offsetMax = offsetMax;
	}

	private static void ClearChildren(RectTransform parent)
	{
		for (int num = ((Transform)parent).childCount - 1; num >= 0; num--)
		{
			GameObject gameObject = ((Component)((Transform)parent).GetChild(num)).gameObject;
			gameObject.SetActive(false);
			Object.Destroy((Object)(object)gameObject);
		}
	}

	static ChestRuleEditor()
	{
		Gold = new Color(1f, 0.66f, 0.24f, 1f);
		GoldFaint = new Color(0.95f, 0.58f, 0.18f, 0.42f);
		SelectedGold = new Color(1f, 0.76f, 0.42f, 1f);
		Cream = new Color(0.9f, 0.87f, 0.79f, 1f);
		Muted = new Color(0.73f, 0.69f, 0.61f, 1f);
		_buttonImageColor = Color.white;
		_buttonLabelColor = Gold;
		_panelImageColor = Color.white;
		_panelFillCenter = true;
		_panelPixelsPerUnitMultiplier = 1f;
		_selectedGroupId = "materials";
		_activeCategoryGroupId = "materials";
		_activeItemGroupId = "materials";
		_itemSearch = string.Empty;
		SelectedCategories = new HashSet<ItemCategory>();
		SelectedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		ModeButtons = new Dictionary<ChestRuleScope, ButtonVisual>();
		BrowserGroupButtons = new Dictionary<string, ButtonVisual>(StringComparer.OrdinalIgnoreCase);
		ItemChoices = new List<ItemChoice>();
	}
}
