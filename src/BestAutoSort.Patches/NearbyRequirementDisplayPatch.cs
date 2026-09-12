using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BestAutoSort.Core;
using BestAutoSort.Runtime;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(InventoryGui), "SetupRequirement", new Type[]
{
	typeof(Transform),
	typeof(Requirement),
	typeof(Player),
	typeof(bool),
	typeof(int),
	typeof(int)
})]
internal static class NearbyRequirementDisplayPatch
{
	private static readonly MethodInfo VanillaCountItems = AccessTools.Method(typeof(Inventory), "CountItems", new Type[3]
	{
		typeof(string),
		typeof(int),
		typeof(bool)
	}, (Type[])null);

	private static readonly MethodInfo NearbyCountItems = AccessTools.Method(typeof(NearbyRequirementDisplayPatch), "CountItemsIncludingNearbyChests", (Type[])null, (Type[])null);

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		foreach (CodeInstruction instruction in instructions)
		{
			if (CodeInstructionExtensions.Calls(instruction, VanillaCountItems))
			{
				instruction.opcode = OpCodes.Call;
				instruction.operand = NearbyCountItems;
			}
			yield return instruction;
		}
	}

	private static int CountItemsIncludingNearbyChests(Inventory inventory, string name, int quality, bool worldLevelBased)
	{
		int result = inventory.CountItems(name, quality, worldLevelBased);
		Player localPlayer = Player.m_localPlayer;
		if (!ModConfig.CraftFromNearbyChests.Value || (Object)(object)localPlayer == (Object)null || inventory != ((Humanoid)localPlayer).GetInventory())
		{
			return result;
		}
		return NearbyResourceService.CountAvailable(localPlayer, name, quality, ((Component)localPlayer).transform.position);
	}

	private static void Postfix(Transform elementRoot, Requirement req, Player player, bool craft, int quality, int craftMultiplier, ref bool __result)
	{
		if (!ModConfig.CraftFromNearbyChests.Value || (Object)(object)player != (Object)(object)Player.m_localPlayer || req?.m_resItem?.m_itemData?.m_shared == null)
		{
			return;
		}
		int num = req.GetAmount(quality) * craftMultiplier;
		if (num <= 0)
		{
			return;
		}
		string name = req.m_resItem.m_itemData.m_shared.m_name;
		int num2 = NearbyResourceService.CountAvailable(player, name, -1, ((Component)player).transform.position);
		Transform val = elementRoot.Find("res_amount");
		TMP_Text val2 = (((Object)(object)val != (Object)null) ? ((Component)val).GetComponent<TMP_Text>() : null);
		if ((Object)(object)val2 != (Object)null)
		{
			val2.text = $"{num} ({num2})";
			if (num2 >= num)
			{
				((Graphic)val2).color = Color.white;
			}
		}
		FitRequirementName(elementRoot);
		if (num2 >= num)
		{
			__result = true;
		}
	}

	private static void FitRequirementName(Transform elementRoot)
	{
		Transform val = elementRoot.Find("res_name");
		TMP_Text val2 = (((Object)(object)val != (Object)null) ? ((Component)val).GetComponent<TMP_Text>() : null);
		RectTransform val3 = ((val2 != null) ? val2.rectTransform : null);
		RectTransform val4 = (RectTransform)(object)((elementRoot is RectTransform) ? elementRoot : null);
		if ((Object)(object)val2 == (Object)null || (Object)(object)val3 == (Object)null || (Object)(object)val4 == (Object)null)
		{
			return;
		}
		RequirementNameLayoutState requirementNameLayoutState = ((Component)val2).GetComponent<RequirementNameLayoutState>() ?? ((Component)val2).gameObject.AddComponent<RequirementNameLayoutState>();
		Rect rect;
		if (!requirementNameLayoutState.Initialized)
		{
			requirementNameLayoutState.Initialized = true;
			requirementNameLayoutState.BaseSizeDelta = val3.sizeDelta;
			requirementNameLayoutState.BaseAnchoredPosition = val3.anchoredPosition;
			rect = val3.rect;
			requirementNameLayoutState.BaseWidth = (rect).width;
		}
		else
		{
			val3.sizeDelta = requirementNameLayoutState.BaseSizeDelta;
			val3.anchoredPosition = requirementNameLayoutState.BaseAnchoredPosition;
		}
		val2.enableAutoSizing = false;
		val2.textWrappingMode = (TextWrappingModes)0;
		val2.overflowMode = (TextOverflowModes)0;
		RectTransform val5 = FindRequirementRow(val4, requirementNameLayoutState.BaseWidth);
		if (!((Object)(object)val5 == (Object)null))
		{
			float x = val2.GetPreferredValues(val2.text).x;
			float baseWidth = requirementNameLayoutState.BaseWidth;
			rect = val5.rect;
			float num = RequirementLabelLayout.ExpandedWidth(baseWidth, x, (rect).width);
			if (!(num <= requirementNameLayoutState.BaseWidth + 0.5f))
			{
				val3.sizeDelta = new Vector2(requirementNameLayoutState.BaseSizeDelta.x + num - requirementNameLayoutState.BaseWidth, requirementNameLayoutState.BaseSizeDelta.y);
				rect = val4.rect;
				Vector3 val6 = ((Transform)val4).TransformPoint((Vector3)(rect).center);
				float x2 = ((Transform)val5).InverseTransformPoint(val6).x;
				float baseWidth2 = requirementNameLayoutState.BaseWidth;
				rect = val5.rect;
				float num2 = RequirementLabelLayout.OffsetTowardCenter(baseWidth2, num, x2, (rect).center.x);
				val3.anchoredPosition = requirementNameLayoutState.BaseAnchoredPosition + new Vector2(num2, 0f);
			}
		}
	}

	private static RectTransform? FindRequirementRow(RectTransform element, float baseWidth)
	{
		Transform parent = ((Transform)element).parent;
		int num = 0;
		while (num < 4 && (Object)(object)parent != (Object)null)
		{
			RectTransform val = (RectTransform)(object)((parent is RectTransform) ? parent : null);
			if (val != null)
			{
				Rect rect = val.rect;
				if ((rect).width >= baseWidth * 2f)
				{
					return val;
				}
			}
			num++;
			parent = parent.parent;
		}
		return null;
	}
}
