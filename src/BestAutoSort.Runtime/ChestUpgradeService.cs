using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BestAutoSort.Core;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class ChestUpgradeService
{
	private readonly struct ItemPosition
	{
		internal ItemData Item { get; }

		internal Vector2i Position { get; }

		internal ItemPosition(ItemData item, Vector2i position)
		{
			Item = item;
			Position = position;
		}
	}

	private sealed class TierDefinition
	{
		internal int Tier { get; }

		internal string DisplayName { get; }

		internal string PrefabName { get; }

		internal TierDefinition(int tier, string displayName, string prefabName)
		{
			Tier = tier;
			DisplayName = displayName;
			PrefabName = prefabName;
		}
	}

	private const string TierKey = "BestAutoSort.ChestTier";

	internal const int ReinforcedTier = 1;

	internal const int BlackMetalTier = 2;

	internal const int GraustenTier = 3;

	private static readonly FieldInfo InventoryNameField = AccessTools.Field(typeof(Inventory), "m_name");

	private static readonly FieldInfo InventoryBackgroundField = AccessTools.Field(typeof(Inventory), "m_bkg");

	private static readonly FieldInfo InventoryWidthField = AccessTools.Field(typeof(Inventory), "m_width");

	private static readonly FieldInfo InventoryHeightField = AccessTools.Field(typeof(Inventory), "m_height");

	private static readonly MethodInfo GetPrefabNameMethod = AccessTools.Method(typeof(ZNetView), "GetPrefabName", (Type[])null, (Type[])null);

	private static readonly TierDefinition[] Tiers = new TierDefinition[4]
	{
		new TierDefinition(0, "Chest", "piece_chest_wood"),
		new TierDefinition(1, "Reinforced", "piece_chest"),
		new TierDefinition(2, "Black Metal", "piece_chest_blackmetal"),
		new TierDefinition(3, "Grausten", "piece_chest_grausten")
	};

	private static int _lastLegacyMigrationInstanceId = int.MinValue;

	internal static bool CanUpgradeTo(Container? container, int targetTier)
	{
		Player localPlayer = Player.m_localPlayer;
		int currentTier = ManagedTier(container);
		if ((Object)(object)localPlayer == (Object)null || !ChestUpgradePath.CanUpgrade(currentTier, targetTier))
		{
			return false;
		}
		if (!TryGetTierComponents(Tiers[targetTier], out Container _, out Piece piece) || (Object)(object)piece == (Object)null)
		{
			return false;
		}
		if (!localPlayer.NoCostCheat())
		{
			return localPlayer.IsRecipeKnown(piece.m_name);
		}
		return true;
	}

	internal static bool UpdateLegacyMigration()
	{
		if (!ModConfig.Enabled.Value || (Object)(object)InventoryGui.instance == (Object)null || (Object)(object)Player.m_localPlayer == (Object)null)
		{
			_lastLegacyMigrationInstanceId = int.MinValue;
			return false;
		}
		Container val = InventoryAccess.CurrentContainer(InventoryGui.instance);
		if ((Object)(object)val == (Object)null)
		{
			_lastLegacyMigrationInstanceId = int.MinValue;
			return false;
		}
		int num = ReadTierMarker(val);
		if (!ChestUpgradePath.IsLegacy(PrefabName(val), num))
		{
			return false;
		}
		int instanceID = ((Object)val).GetInstanceID();
		if (_lastLegacyMigrationInstanceId == instanceID)
		{
			return false;
		}
		ZNetView component = ((Component)val).GetComponent<ZNetView>();
		if ((Object)(object)component == (Object)null || !component.IsOwner())
		{
			return false;
		}
		_lastLegacyMigrationInstanceId = instanceID;
		Player localPlayer = Player.m_localPlayer;
		InventoryGui.instance.Hide();
		if (!TryReplaceChest(val, num, out Container replacement, out string error) || (Object)(object)replacement == (Object)null)
		{
			ShowMessage("Legacy chest migration failed: " + error);
			return true;
		}
		int[] array = ChestUpgradePath.ObsoleteLegacyTiers(num);
		foreach (int num2 in array)
		{
			if (TryGetTierComponents(Tiers[num2], out Container _, out Piece piece) && (Object)(object)piece != (Object)null)
			{
				RefundRequirements(localPlayer, piece.m_resources);
			}
		}
		PlayUpgradeEffect(num, replacement);
		ShowMessage("BestAutoSort converted this legacy upgrade to a compact vanilla " + Tiers[num].DisplayName + " chest. Reopen it to continue.");
		return true;
	}

	internal static void UpgradeOpenChest(int targetTier)
	{
		Player localPlayer = Player.m_localPlayer;
		Container val = InventoryAccess.CurrentContainer(InventoryGui.instance);
		if ((Object)(object)localPlayer == (Object)null || (Object)(object)val == (Object)null)
		{
			ShowMessage("Open a wooden, compact reinforced, or compact black-metal chest to upgrade it.");
			return;
		}
		int num = ManagedTier(val);
		if (num < 0)
		{
			ShowMessage("Only a standard wooden chest or BestAutoSort compact chest can be upgraded in place.");
			return;
		}
		if (!ChestUpgradePath.CanUpgrade(num, targetTier))
		{
			ShowMessage("That chest tier is not an available upgrade from the current chest.");
			return;
		}
		ZNetView component = ((Component)val).GetComponent<ZNetView>();
		ZDO val2 = (((Object)(object)component != (Object)null) ? component.GetZDO() : null);
		if ((Object)(object)component == (Object)null || val2 == null)
		{
			ShowMessage("This chest cannot persist an upgrade.");
			return;
		}
		if (!component.IsOwner())
		{
			ShowMessage("Waiting for chest ownership. Try Upgrade again.");
			return;
		}
		if (!PrivateArea.CheckAccess(((Component)val).transform.position, 0f, true, false))
		{
			ShowMessage("You do not have access to upgrade this chest.");
			return;
		}
		TierDefinition tierDefinition = Tiers[targetTier];
		if (!TryGetTierComponents(tierDefinition, out Container container, out Piece piece) || (Object)(object)piece == (Object)null)
		{
			ShowMessage(tierDefinition.DisplayName + " chest assets are not available.");
			return;
		}
		if (!TryGetTierComponents(Tiers[num], out container, out Piece piece2) || (Object)(object)piece2 == (Object)null)
		{
			ShowMessage("The current chest recipe is not available.");
			return;
		}
		bool flag = localPlayer.NoCostCheat() || ((Object)(object)ZoneSystem.instance != (Object)null && ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()));
		if (!flag && !localPlayer.IsRecipeKnown(piece.m_name))
		{
			ShowMessage(tierDefinition.DisplayName + " chest construction has not been unlocked yet.");
			return;
		}
		if (!flag && !localPlayer.HaveRequirements(piece, (RequirementMode)0))
		{
			ShowMessage("Upgrade requires " + DescribeRequirements(piece) + " and the appropriate crafting station.");
			return;
		}
		if (!flag)
		{
			// HaveRequirements counts nearby chests, but ConsumeResources only
			// sees the player inventory: stage the missing part first (async tx
			// pull), and gate the upgrade until it lands. Next press succeeds.
			// TEMP-DIAG(upgrade-free): always-on logs, remove after diagnosis.
			Plugin.LogInstance.LogInfo((object)("[ChestTX] UPGRADE-DIAG press tier=" + num + "->" + targetTier + " owner=" + component.IsOwner()));
			NearbyResourceService.StageMissingForPiece(localPlayer, piece);
			string missing;
			bool staged = NearbyResourceService.HasStagedMatsForPiece(localPlayer, piece, out missing);
			Plugin.LogInstance.LogInfo((object)("[ChestTX] UPGRADE-DIAG staged=" + staged + " missing=" + missing));
			if (!staged)
			{
				ShowMessage("Gathering " + missing + " from nearby chests. Press Upgrade again.");
				return;
			}
		}
		if (!flag)
		{
			// Chest-aware consumption: vanilla ConsumeResources only sees the
			// player inventory, so stock sitting in owned chests would survive
			// (free upgrade). This consumes inventory first, then self-owned
			// chests synchronously; foreign stock was staged above by the gate.
			NearbyResourceService.ConsumeRequirements(localPlayer, piece.m_resources, 0, -1, 1);
		}
		InventoryGui instance = InventoryGui.instance;
		if (instance != null)
		{
			instance.Hide();
		}
		if (!TryReplaceChest(val, tierDefinition.Tier, out Container replacement, out string error) || (Object)(object)replacement == (Object)null)
		{
			if (!flag)
			{
				RefundRequirements(localPlayer, piece.m_resources);
			}
			ShowMessage("Chest upgrade failed: " + error);
		}
		else
		{
			DropRequirementsBesideChest(localPlayer, replacement, piece2.m_resources);
			PlayUpgradeEffect(tierDefinition.Tier, replacement);
			ShowMessage("Chest upgraded to compact " + tierDefinition.DisplayName + ". Reopen it to continue.");
		}
	}

	internal static void ApplyState(Container container)
	{
		if ((Object)(object)container == (Object)null)
		{
			return;
		}
		string prefabName = PrefabName(container);
		int markerTier = ReadTierMarker(container);
		int num = ChestUpgradePath.ResolveManagedTier(prefabName, markerTier);
		if (num <= 0)
		{
			return;
		}
		ChestUpgradeVisualState chestUpgradeVisualState = ((Component)container).GetComponent<ChestUpgradeVisualState>() ?? ((Component)container).gameObject.AddComponent<ChestUpgradeVisualState>();
		if (chestUpgradeVisualState.AppliedTier == num)
		{
			return;
		}
		Inventory __inv = container.GetInventory();
		Plugin.LogInstance.LogInfo((object)("[ChestTX] applystate prefab=" + prefabName + " marker=" + markerTier + " tier=" + num + " dims=" + (__inv != null ? (__inv.GetWidth() + "x" + __inv.GetHeight()) : "?")));
		Piece piece;
		bool flag;
		if (ChestUpgradePath.IsLegacy(prefabName, markerTier))
		{
			if (!TryGetTierComponents(Tiers[num], out Container container2, out piece) || (Object)(object)container2 == (Object)null)
			{
				return;
			}
			ApplyContainerDefinition(container, container2);
			flag = TryApplyLegacyVisual(container, container2, chestUpgradeVisualState);
		}
		else
		{
			if (!TryGetTierComponents(Tiers[0], out Container container3, out piece) || (Object)(object)container3 == (Object)null)
			{
				return;
			}
			flag = TryApplyCompactVisual(container, container3, chestUpgradeVisualState);
		}
		if (flag)
		{
			chestUpgradeVisualState.AppliedTier = num;
		}
	}

	internal static Requirement[]? AdditionalRefundRequirements(Piece piece)
	{
		Container val = (((Object)(object)piece != (Object)null) ? ((Component)piece).GetComponent<Container>() : null);
		if ((Object)(object)val == (Object)null)
		{
			return null;
		}
		int num = ReadTierMarker(val);
		if (!ChestUpgradePath.IsLegacy(PrefabName(val), num))
		{
			return null;
		}
		List<Requirement> list = new List<Requirement>();
		for (int i = 1; i <= num; i++)
		{
			if (TryGetTierComponents(Tiers[i], out Container _, out Piece piece2) && (Object)(object)piece2 != (Object)null)
			{
				list.AddRange(piece2.m_resources.Where((Requirement requirement) => requirement != null));
			}
		}
		if (list.Count <= 0)
		{
			return null;
		}
		return list.ToArray();
	}

	private static bool TryReplaceChest(Container source, int targetTier, out Container? replacement, out string error)
	{
		replacement = null;
		error = string.Empty;
		if ((Object)(object)ZNetScene.instance == (Object)null)
		{
			error = "the network scene is unavailable.";
			return false;
		}
		ZNetView component = ((Component)source).GetComponent<ZNetView>();
		Piece component2 = ((Component)source).GetComponent<Piece>();
		if ((Object)(object)component == (Object)null || component.GetZDO() == null || !component.IsOwner() || (Object)(object)component2 == (Object)null)
		{
			error = "the original chest is not locally owned.";
			return false;
		}
		GameObject prefab = ZNetScene.instance.GetPrefab(ChestUpgradePath.PrefabForTier(targetTier));
		if ((Object)(object)prefab == (Object)null)
		{
			error = "the target vanilla chest prefab is unavailable.";
			return false;
		}
		PlatformUserID val = PlatformUserID.None;
		int creatorPlatformUserIdIndex = component2.GetCreatorPlatformUserIdIndex();
		if (creatorPlatformUserIdIndex >= 0)
		{
			World val2 = (((Object)(object)ZNet.instance != (Object)null) ? ZNet.instance.GetWorld() : null);
			if (val2 == null || creatorPlatformUserIdIndex >= val2.m_playerHistory.Count)
			{
				error = "the original chest creator record is unavailable.";
				return false;
			}
			val = val2.m_playerHistory[creatorPlatformUserIdIndex].m_id;
		}
		ChestStorageRule rule = ChestRuleStore.Read(source);
		GameObject val3 = null;
		bool flag = false;
		try
		{
			val3 = Object.Instantiate<GameObject>(prefab, ((Component)source).transform.position, ((Component)source).transform.rotation);
			replacement = val3.GetComponent<Container>();
			ZNetView component3 = val3.GetComponent<ZNetView>();
			Piece component4 = val3.GetComponent<Piece>();
			ZDO val4 = (((Object)(object)component3 != (Object)null) ? component3.GetZDO() : null);
			if ((Object)(object)replacement == (Object)null || (Object)(object)component3 == (Object)null || (Object)(object)component4 == (Object)null || val4 == null)
			{
				error = "the replacement chest did not initialize.";
				return false;
			}
			if (!component3.IsOwner())
			{
				component3.ClaimOwnership();
			}
			if (!component3.IsOwner())
			{
				error = "the replacement chest could not be claimed.";
				return false;
			}
			component4.SetCreator(component2.GetCreator(), val);
			val4.Set("BestAutoSort.ChestTier", targetTier);
			if (!ChestRuleStore.TryWrite(replacement, rule, out error))
			{
				return false;
			}
			ChestReserveStore.Write(replacement, ChestReserveStore.ReadRaw(source));
			if (!TryMoveInventory(source, replacement, out error))
			{
				return false;
			}
			flag = true;
			try
			{
				ApplyState(replacement);
			}
			catch (Exception ex)
			{
				Plugin.LogInstance.LogWarning((object)("The replacement chest is safe but could not be compacted: " + ex));
			}
			try
			{
				component.Destroy();
			}
			catch (Exception ex2)
			{
				Plugin.LogInstance.LogWarning((object)("The old chest was emptied but its shell could not be removed: " + ex2));
			}
			return true;
		}
		catch (Exception ex3)
		{
			error = ex3.GetBaseException().Message;
			Plugin.LogInstance.LogError((object)("Chest replacement failed: " + ex3));
			return false;
		}
		finally
		{
			if (!flag && (Object)(object)val3 != (Object)null)
			{
				ZNetView component5 = val3.GetComponent<ZNetView>();
				if ((Object)(object)component5 != (Object)null && component5.IsValid() && component5.IsOwner())
				{
					component5.Destroy();
				}
				else
				{
					Object.Destroy((Object)(object)val3);
				}
				replacement = null;
			}
		}
	}

	private static bool TryMoveInventory(Container source, Container target, out string error)
	{
		error = string.Empty;
		Inventory inventory = source.GetInventory();
		Inventory targetInventory = target.GetInventory();
		List<ItemPosition> list = inventory.GetAllItems().Select(delegate(ItemData item)
		{
			return new ItemPosition(item, item.m_gridPos);
		}).ToList();
		if (list.Any(delegate(ItemPosition entry)
		{
			return entry.Position.x < 0 || entry.Position.y < 0 || entry.Position.x >= targetInventory.GetWidth() || entry.Position.y >= target.m_height;
		}))
		{
			error = "the target chest cannot contain every existing item position.";
			return false;
		}
		List<ItemPosition> list2 = new List<ItemPosition>();
		foreach (ItemPosition item in list)
		{
			int stack = item.Item.m_stack;
			if (!targetInventory.MoveItemToThis(inventory, item.Item, stack, item.Position.x, item.Position.y))
			{
				RollBackMovedItems(inventory, targetInventory, list2);
				error = "an inventory stack could not be transferred safely.";
				return false;
			}
			list2.Add(item);
		}
		if (inventory.GetAllItems().Count != 0)
		{
			RollBackMovedItems(inventory, targetInventory, list2);
			error = "the original chest was not empty after transfer.";
			return false;
		}
		return true;
	}

	private static void RollBackMovedItems(Inventory sourceInventory, Inventory targetInventory, IReadOnlyList<ItemPosition> moved)
	{
		for (int num = moved.Count - 1; num >= 0; num--)
		{
			ItemPosition itemPosition = moved[num];
			sourceInventory.MoveItemToThis(targetInventory, itemPosition.Item, itemPosition.Item.m_stack, itemPosition.Position.x, itemPosition.Position.y);
		}
	}

	private static void RefundRequirements(Player player, IEnumerable<Requirement> requirements)
	{
		foreach (Requirement requirement in requirements)
		{
			if (requirement != null && requirement.m_recover && requirement.m_amount > 0)
			{
				ItemDrop resItem = requirement.m_resItem;
				if (resItem?.m_itemData?.m_shared != null)
				{
					RefundResource(player, resItem, requirement.m_amount);
				}
			}
		}
	}

	private static void RefundResource(Player player, ItemDrop resource, int amountToRefund)
	{
		Inventory inventory = ((Humanoid)player).GetInventory();
		Vector3 val = ((Component)player).transform.position + ((Component)player).transform.forward + Vector3.up;
		int num = amountToRefund;
		int num2 = Mathf.Max(1, resource.m_itemData.m_shared.m_maxStackSize);
		while (num > 0)
		{
			int num3 = Mathf.Min(num, num2);
			ItemData val2 = CreateResourceItem(resource, num3);
			if (!inventory.CanAddItem(val2, num3) || !inventory.AddItem(val2))
			{
				ItemDrop.DropItem(val2, num3, val, Quaternion.identity);
			}
			num -= num3;
		}
	}

	private static ItemData CreateResourceItem(ItemDrop resource, int amount)
	{
		ItemData val = resource.m_itemData.Clone();
		val.m_stack = amount;
		if ((Object)(object)val.m_dropPrefab == (Object)null)
		{
			val.m_dropPrefab = ((Component)resource).gameObject;
		}
		return val;
	}

	private static void DropRequirementsBesideChest(Player player, Container chest, IEnumerable<Requirement> requirements)
	{
		Vector3 val = ((Component)player).transform.position - ((Component)chest).transform.position;
		val.y = 0f;
		if ((val).sqrMagnitude < 0.01f)
		{
			val = ((Component)chest).transform.forward;
			val.y = 0f;
		}
		if ((val).sqrMagnitude < 0.01f)
		{
			val = Vector3.forward;
		}
		(val).Normalize();
		Vector3 val2 = ((Component)chest).transform.position + val * 0.9f + Vector3.up * 0.5f;
		int num = 0;
		Vector3 val3 = default(Vector3);
		foreach (Requirement requirement in requirements)
		{
			if (requirement == null || !requirement.m_recover || requirement.m_amount <= 0)
			{
				continue;
			}
			ItemDrop resItem = requirement.m_resItem;
			if (resItem?.m_itemData?.m_shared == null)
			{
				continue;
			}
			int num2 = requirement.m_amount;
			int num3 = Mathf.Max(1, resItem.m_itemData.m_shared.m_maxStackSize);
			while (num2 > 0)
			{
				int num4 = Mathf.Min(num2, num3);
				try
				{
					ItemData obj = CreateResourceItem(resItem, num4);
					float num5 = (float)num * 137.5f * ((float)Math.PI / 180f);
					float num6 = 0.08f * Mathf.Sqrt((float)num);
					val3 = new Vector3(Mathf.Cos(num5) * num6, 0f, Mathf.Sin(num5) * num6);
					ItemDrop.DropItem(obj, num4, val2 + val3, Quaternion.identity);
					num2 -= num4;
					num++;
				}
				catch (Exception ex)
				{
					Plugin.LogInstance.LogError((object)("Could not drop the destroyed chest's " + resItem.m_itemData.m_shared.m_name + " refund; returning the remaining amount safely to the player instead: " + ex));
					try
					{
						RefundResource(player, resItem, num2);
					}
					catch (Exception ex2)
					{
						Plugin.LogInstance.LogError((object)($"Could not return {num2} of {resItem.m_itemData.m_shared.m_name} after the " + "ground refund failed: " + ex2));
					}
					break;
				}
			}
		}
	}

	private static void ApplyContainerDefinition(Container target, Container source)
	{
		target.m_name = source.m_name;
		target.m_bkg = source.m_bkg;
		target.m_width = source.m_width;
		target.m_height = source.m_height;
		target.m_openEffects = source.m_openEffects;
		target.m_closeEffects = source.m_closeEffects;
		Inventory inventory = target.GetInventory();
		if (inventory != null)
		{
			InventoryNameField.SetValue(inventory, source.m_name);
			InventoryBackgroundField.SetValue(inventory, source.m_bkg);
			InventoryWidthField.SetValue(inventory, source.m_width);
			InventoryHeightField.SetValue(inventory, source.m_height);
		}
	}

	private static bool TryApplyLegacyVisual(Container target, Container source, ChestUpgradeVisualState state)
	{
		if ((Object)(object)source.m_closed == (Object)null || (Object)(object)source.m_open == (Object)null || (Object)(object)target.m_closed == (Object)null || (Object)(object)target.m_open == (Object)null)
		{
			return false;
		}
		if (!state.HasBaseBounds)
		{
			state.OriginalClosed = target.m_closed;
			state.OriginalOpen = target.m_open;
			Transform val = FindCommonAncestor(((Component)target).transform, state.OriginalClosed.transform, state.OriginalOpen.transform);
			if ((Object)(object)val == (Object)null || !TryCalculateBounds(((Component)target).transform, ((Component)val).gameObject, state.OriginalOpen.transform, out var bounds))
			{
				return false;
			}
			state.OriginalVisualRoot = ((Component)val).gameObject;
			state.OriginalRenderers = ((Component)val).GetComponentsInChildren<Renderer>(true);
			state.BaseClosedBounds = bounds;
			state.HasBaseBounds = true;
		}
		return TryInstallFittedVisual(target, source, state, replaceColliders: false);
	}

	private static bool TryApplyCompactVisual(Container target, Container woodenTemplate, ChestUpgradeVisualState state)
	{
		if ((Object)(object)target.m_closed == (Object)null || (Object)(object)target.m_open == (Object)null || (Object)(object)woodenTemplate.m_closed == (Object)null || (Object)(object)woodenTemplate.m_open == (Object)null)
		{
			return false;
		}
		if (!state.HasBaseBounds)
		{
			Transform val = FindCommonAncestor(((Component)woodenTemplate).transform, woodenTemplate.m_closed.transform, woodenTemplate.m_open.transform);
			Transform val2 = FindCommonAncestor(((Component)target).transform, target.m_closed.transform, target.m_open.transform);
			if ((Object)(object)val == (Object)null || (Object)(object)val2 == (Object)null || !TryCalculateBounds(((Component)woodenTemplate).transform, ((Component)val).gameObject, woodenTemplate.m_open.transform, out var bounds))
			{
				return false;
			}
			state.OriginalClosed = target.m_closed;
			state.OriginalOpen = target.m_open;
			state.OriginalVisualRoot = ((Component)val2).gameObject;
			state.OriginalRenderers = ((Component)val2).GetComponentsInChildren<Renderer>(true);
			state.OriginalColliders = ((Component)target).GetComponentsInChildren<Collider>(true);
			state.OriginalColliderEnabled = state.OriginalColliders.Select((Collider collider) => (Object)(object)collider != (Object)null && collider.enabled).ToArray();
			state.BaseClosedBounds = bounds;
			state.HasBaseBounds = true;
		}
		return TryInstallFittedVisual(target, target, state, replaceColliders: true, woodenTemplate);
	}

	private static bool TryInstallFittedVisual(Container target, Container visualSource, ChestUpgradeVisualState state, bool replaceColliders, Container? colliderSource = null)
	{
		if ((Object)(object)state.VisualRoot != (Object)null)
		{
			state.VisualRoot.SetActive(false);
			Object.Destroy((Object)(object)state.VisualRoot);
		}
		if ((Object)(object)state.CollisionRoot != (Object)null)
		{
			Object.Destroy((Object)(object)state.CollisionRoot);
		}
		GameObject val = new GameObject("BestAutoSort_CompactVisual");
		val.transform.SetParent(((Component)target).transform, false);
		if (!TryCloneVisualHierarchy(visualSource, val.transform, out GameObject hierarchy, out GameObject closed, out GameObject open) || (Object)(object)hierarchy == (Object)null || (Object)(object)closed == (Object)null || (Object)(object)open == (Object)null)
		{
			Object.Destroy((Object)(object)val);
			return false;
		}
		if (!TryCalculateBounds(((Component)target).transform, hierarchy, open.transform, out var bounds))
		{
			Object.Destroy((Object)(object)val);
			return false;
		}
		Transform transform = val.transform;
		Bounds baseClosedBounds = state.BaseClosedBounds;
		float num = SafeScale((baseClosedBounds).size.x, (bounds).size.x);
		baseClosedBounds = state.BaseClosedBounds;
		float num2 = SafeScale((baseClosedBounds).size.y, (bounds).size.y);
		baseClosedBounds = state.BaseClosedBounds;
		transform.localScale = new Vector3(num, num2, SafeScale((baseClosedBounds).size.z, (bounds).size.z));
		if (TryCalculateBounds(((Component)target).transform, hierarchy, open.transform, out var bounds2))
		{
			Transform transform2 = val.transform;
			Vector3 localPosition = transform2.localPosition;
			baseClosedBounds = state.BaseClosedBounds;
			transform2.localPosition = localPosition + ((baseClosedBounds).center - (bounds2).center);
		}
		GameObject collisionRoot = null;
		if (replaceColliders)
		{
			if ((Object)(object)colliderSource == (Object)null || !TryCloneColliders(colliderSource, target, out collisionRoot))
			{
				Object.Destroy((Object)(object)val);
				return false;
			}
			Collider[] originalColliders = state.OriginalColliders;
			foreach (Collider val2 in originalColliders)
			{
				if ((Object)(object)val2 != (Object)null)
				{
					val2.enabled = false;
				}
			}
		}
		Renderer[] originalRenderers = state.OriginalRenderers;
		foreach (Renderer val3 in originalRenderers)
		{
			if ((Object)(object)val3 != (Object)null)
			{
				val3.enabled = false;
			}
		}
		target.m_closed = closed;
		target.m_open = open;
		state.VisualRoot = val;
		state.CollisionRoot = collisionRoot;
		bool flag = target.IsInUse();
		closed.SetActive(!flag);
		open.SetActive(flag);
		return true;
	}

	private static bool TryCloneVisualHierarchy(Container source, Transform parent, out GameObject? hierarchy, out GameObject? closed, out GameObject? open)
	{
		hierarchy = null;
		closed = null;
		open = null;
		Transform val = FindCommonAncestor(((Component)source).transform, source.m_closed.transform, source.m_open.transform);
		if ((Object)(object)val == (Object)null)
		{
			return false;
		}
		int[] path = ChildPath(val, source.m_closed.transform);
		int[] path2 = ChildPath(val, source.m_open.transform);
		hierarchy = Object.Instantiate<GameObject>(((Component)val).gameObject, parent, false);
		((Object)hierarchy).name = "BestAutoSort_Model";
		Matrix4x4 val2 = ((Component)source).transform.worldToLocalMatrix * val.localToWorldMatrix;
		Vector4 column = (val2).GetColumn(3);
		hierarchy.transform.localPosition = new Vector3(column.x, column.y, column.z);
		hierarchy.transform.localRotation = (val2).rotation;
		hierarchy.transform.localScale = (val2).lossyScale;
		Collider[] componentsInChildren = hierarchy.GetComponentsInChildren<Collider>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = false;
		}
		MonoBehaviour[] componentsInChildren2 = hierarchy.GetComponentsInChildren<MonoBehaviour>(true);
		for (int i = 0; i < componentsInChildren2.Length; i++)
		{
			((Behaviour)componentsInChildren2[i]).enabled = false;
		}
		Transform? obj = FollowPath(hierarchy.transform, path);
		closed = ((obj != null) ? ((Component)obj).gameObject : null);
		Transform? obj2 = FollowPath(hierarchy.transform, path2);
		open = ((obj2 != null) ? ((Component)obj2).gameObject : null);
		if ((Object)(object)closed != (Object)null)
		{
			return (Object)(object)open != (Object)null;
		}
		return false;
	}

	private static bool TryCloneColliders(Container source, Container target, out GameObject? collisionRoot)
	{
		collisionRoot = new GameObject("BestAutoSort_CompactColliders");
		collisionRoot.transform.SetParent(((Component)target).transform, false);
		int num = 0;
		Collider[] componentsInChildren = ((Component)source).GetComponentsInChildren<Collider>(true);
		foreach (Collider val in componentsInChildren)
		{
			if (!((Object)(object)val == (Object)null))
			{
				GameObject val2 = new GameObject(((Object)((Component)val).gameObject).name + "_Collider");
				val2.layer = ((Component)val).gameObject.layer;
				val2.transform.SetParent(collisionRoot.transform, false);
				Matrix4x4 val3 = ((Component)source).transform.worldToLocalMatrix * ((Component)val).transform.localToWorldMatrix;
				Vector4 column = (val3).GetColumn(3);
				val2.transform.localPosition = new Vector3(column.x, column.y, column.z);
				val2.transform.localRotation = (val3).rotation;
				val2.transform.localScale = (val3).lossyScale;
				Collider val4 = CloneCollider(val, val2);
				if ((Object)(object)val4 == (Object)null)
				{
					Object.Destroy((Object)(object)val2);
					continue;
				}
				val4.enabled = val.enabled;
				val4.isTrigger = val.isTrigger;
				val4.sharedMaterial = val.sharedMaterial;
				val4.contactOffset = val.contactOffset;
				num++;
			}
		}
		if (num > 0)
		{
			return true;
		}
		Object.Destroy((Object)(object)collisionRoot);
		collisionRoot = null;
		return false;
	}

	private static Collider? CloneCollider(Collider source, GameObject holder)
	{
		BoxCollider val = (BoxCollider)(object)((source is BoxCollider) ? source : null);
		if (val != null)
		{
			BoxCollider obj = holder.AddComponent<BoxCollider>();
			obj.center = val.center;
			obj.size = val.size;
			return (Collider?)(object)obj;
		}
		SphereCollider val2 = (SphereCollider)(object)((source is SphereCollider) ? source : null);
		if (val2 != null)
		{
			SphereCollider obj2 = holder.AddComponent<SphereCollider>();
			obj2.center = val2.center;
			obj2.radius = val2.radius;
			return (Collider?)(object)obj2;
		}
		CapsuleCollider val3 = (CapsuleCollider)(object)((source is CapsuleCollider) ? source : null);
		if (val3 != null)
		{
			CapsuleCollider obj3 = holder.AddComponent<CapsuleCollider>();
			obj3.center = val3.center;
			obj3.radius = val3.radius;
			obj3.height = val3.height;
			obj3.direction = val3.direction;
			return (Collider?)(object)obj3;
		}
		MeshCollider val4 = (MeshCollider)(object)((source is MeshCollider) ? source : null);
		if (val4 != null)
		{
			MeshCollider obj4 = holder.AddComponent<MeshCollider>();
			obj4.sharedMesh = val4.sharedMesh;
			obj4.convex = val4.convex;
			return (Collider?)(object)obj4;
		}
		return null;
	}

	private static Transform? FindCommonAncestor(Transform boundary, Transform first, Transform second)
	{
		HashSet<Transform> hashSet = new HashSet<Transform>();
		Transform val = first;
		while ((Object)(object)val != (Object)null)
		{
			hashSet.Add(val);
			if ((Object)(object)val == (Object)(object)boundary)
			{
				break;
			}
			val = val.parent;
		}
		Transform val2 = second;
		while ((Object)(object)val2 != (Object)null)
		{
			if (hashSet.Contains(val2))
			{
				if (!((Object)(object)val2 == (Object)(object)boundary))
				{
					return val2;
				}
				return null;
			}
			if ((Object)(object)val2 == (Object)(object)boundary)
			{
				break;
			}
			val2 = val2.parent;
		}
		return null;
	}

	private static int[] ChildPath(Transform ancestor, Transform descendant)
	{
		List<int> list = new List<int>();
		Transform val = descendant;
		while ((Object)(object)val != (Object)(object)ancestor)
		{
			list.Add(val.GetSiblingIndex());
			val = val.parent;
		}
		list.Reverse();
		return list.ToArray();
	}

	private static Transform? FollowPath(Transform root, IEnumerable<int> path)
	{
		Transform val = root;
		foreach (int item in path)
		{
			if (item < 0 || item >= val.childCount)
			{
				return null;
			}
			val = val.GetChild(item);
		}
		return val;
	}

	private static bool TryCalculateBounds(Transform coordinateSpace, GameObject visual, Transform? excludedBranch, out Bounds bounds)
	{
		bounds = default(Bounds);
		bool flag = false;
		Renderer[] componentsInChildren = visual.GetComponentsInChildren<Renderer>(true);
		foreach (Renderer val in componentsInChildren)
		{
			if ((Object)(object)excludedBranch != (Object)null && ((Object)(object)((Component)val).transform == (Object)(object)excludedBranch || ((Component)val).transform.IsChildOf(excludedBranch)))
			{
				continue;
			}
			SkinnedMeshRenderer val2 = (SkinnedMeshRenderer)(object)((val is SkinnedMeshRenderer) ? val : null);
			Bounds val3;
			if (val2 != null)
			{
				val3 = ((Renderer)val2).localBounds;
			}
			else
			{
				MeshFilter component = ((Component)val).GetComponent<MeshFilter>();
				if ((Object)(object)component == (Object)null || (Object)(object)component.sharedMesh == (Object)null)
				{
					continue;
				}
				val3 = component.sharedMesh.bounds;
			}
			Vector3 center = (val3).center;
			Vector3 extents = (val3).extents;
			for (int j = -1; j <= 1; j += 2)
			{
				for (int k = -1; k <= 1; k += 2)
				{
					for (int l = -1; l <= 1; l += 2)
					{
						Vector3 val4 = center + Vector3.Scale(extents, new Vector3((float)j, (float)k, (float)l));
						Vector3 val5 = coordinateSpace.InverseTransformPoint(((Component)val).transform.TransformPoint(val4));
						if (!flag)
						{
							bounds = new Bounds(val5, Vector3.zero);
							flag = true;
						}
						else
						{
							(bounds).Encapsulate(val5);
						}
					}
				}
			}
		}
		if (flag)
		{
			Vector3 size = (bounds).size;
			return (size).sqrMagnitude > 1E-06f;
		}
		return false;
	}

	private static float SafeScale(float targetSize, float sourceSize)
	{
		if (!(sourceSize > 0.0001f))
		{
			return 1f;
		}
		return targetSize / sourceSize;
	}

	private static bool TryGetTierComponents(TierDefinition definition, out Container? container, out Piece? piece)
	{
		container = null;
		piece = null;
		if ((Object)(object)ZNetScene.instance == (Object)null)
		{
			return false;
		}
		GameObject prefab = ZNetScene.instance.GetPrefab(definition.PrefabName);
		if ((Object)(object)prefab == (Object)null)
		{
			return false;
		}
		container = prefab.GetComponent<Container>();
		piece = prefab.GetComponent<Piece>();
		if ((Object)(object)container != (Object)null)
		{
			return (Object)(object)piece != (Object)null;
		}
		return false;
	}

	private static int ManagedTier(Container? container)
	{
		if ((Object)(object)container == (Object)null)
		{
			return -1;
		}
		return ChestUpgradePath.ResolveManagedTier(PrefabName(container), ReadTierMarker(container));
	}

	private static string PrefabName(Container container)
	{
		ZNetView component = ((Component)container).GetComponent<ZNetView>();
		if (!((Object)(object)component != (Object)null))
		{
			return string.Empty;
		}
		return (GetPrefabNameMethod.Invoke(component, null) as string) ?? string.Empty;
	}

	private static int ReadTierMarker(Container? container)
	{
		if ((Object)(object)container == (Object)null)
		{
			return 0;
		}
		ZNetView component = ((Component)container).GetComponent<ZNetView>();
		ZDO val = (((Object)(object)component != (Object)null) ? component.GetZDO() : null);
		if (val != null)
		{
			return val.GetInt("BestAutoSort.ChestTier", 0);
		}
		return 0;
	}

	private static void PlayUpgradeEffect(int tier, Container container)
	{
		if (TryGetTierComponents(Tiers[tier], out Container _, out Piece piece) && !((Object)(object)piece == (Object)null))
		{
			EffectList placeEffect = piece.m_placeEffect;
			if (placeEffect != null)
			{
				placeEffect.Create(((Component)container).transform.position, ((Component)container).transform.rotation, ((Component)container).transform, 1f, -1, default(ZDOID));
			}
		}
	}

	private static string DescribeRequirements(Piece piece)
	{
		IEnumerable<string> values = piece.m_resources.Where((Requirement requirement) => requirement?.m_resItem?.m_itemData?.m_shared != null && requirement.m_amount > 0).Select(delegate(Requirement requirement)
		{
			string name = requirement.m_resItem.m_itemData.m_shared.m_name;
			string text2 = ((Localization.instance != null) ? Localization.instance.Localize(name) : name);
			return requirement.m_amount + " " + text2;
		});
		string text = string.Join(", ", values);
		if (text.Length <= 0)
		{
			return "the next chest tier's materials";
		}
		return text;
	}

	private static void ShowMessage(string message)
	{
		Player localPlayer = Player.m_localPlayer;
		if (localPlayer != null)
		{
			((Character)localPlayer).Message((MessageType)2, message, 0, (Sprite)null, false);
		}
	}
}
