using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class NearbyResourceService
{
	private const float CacheLifetime = 0.35f;

	private const float CacheOriginToleranceSquared = 0.25f;

	private static readonly MethodInfo ContainerCheckAccessMethod = AccessTools.Method(typeof(Container), "CheckAccess", (Type[])null, (Type[])null);

	private static readonly FieldInfo ContainerNetViewField = AccessTools.Field(typeof(Container), "m_nview");

	private static readonly List<Container> CachedContainers = new List<Container>();

	private static readonly Dictionary<int, float> LastProductionDiagnosticAt = new Dictionary<int, float>();

	private static Vector3 _cachedOrigin;

	private static float _cacheExpiresAt = -1f;

	private static int _inventoryRepairPlayerId = int.MinValue;

	[ThreadStatic]
	private static bool _internalRemoval;

	internal static bool InternalRemoval => _internalRemoval;

	internal static void RepairInvalidPlayerInventoryPositions()
	{
		Player localPlayer = Player.m_localPlayer;
		if ((Object)(object)localPlayer == (Object)null)
		{
			_inventoryRepairPlayerId = int.MinValue;
			return;
		}
		int instanceID = ((Object)localPlayer).GetInstanceID();
		if (_inventoryRepairPlayerId == instanceID)
		{
			return;
		}
		_inventoryRepairPlayerId = instanceID;
		Inventory inventory = ((Humanoid)localPlayer).GetInventory();
		List<ItemData> list = (from item in inventory.GetAllItems()
			where item.m_gridPos.x < 0 || item.m_gridPos.y < 0
			select item).ToList();
		if (list.Count == 0)
		{
			return;
		}
		int num = 0;
		int num2 = 0;
		foreach (ItemData item in list)
		{
			if (TryPlaceInFreePlayerSlot(inventory, item))
			{
				num++;
				continue;
			}
			int stack = item.m_stack;
			if (inventory.RemoveItem(item))
			{
				ItemDrop.DropItem(item, stack, ((Component)localPlayer).transform.position + ((Component)localPlayer).transform.forward + Vector3.up, Quaternion.identity);
				num2++;
			}
		}
		inventory.m_onChanged?.Invoke();
		Plugin.LogInstance.LogWarning((object)($"Recovered {list.Count} invalid production-loan item(s): " + $"{num} relocated, {num2} dropped safely beside the player."));
	}

	internal static bool HasRecipeRequirements(Player player, Recipe recipe, int qualityLevel, int craftMultiplier)
	{
		if (!ModConfig.CraftFromNearbyChests.Value || (Object)(object)player != (Object)(object)Player.m_localPlayer)
		{
			return false;
		}
		CraftingStation currentCraftingStation = player.GetCurrentCraftingStation();
		bool result = false;
		Requirement[] resources = recipe.m_resources;
		foreach (Requirement val in resources)
		{
			if (!AppliesToStation(val, currentCraftingStation))
			{
				continue;
			}
			int num = val.GetAmount(qualityLevel) * craftMultiplier;
			if (num <= 0)
			{
				continue;
			}
			string name = val.m_resItem.m_itemData.m_shared.m_name;
			int maxQuality = val.m_resItem.m_itemData.m_shared.m_maxQuality;
			bool flag = false;
			for (int j = 1; j <= maxQuality; j++)
			{
				int local = CountAvailable(player, name, j, ((Component)player).transform.position, true);
				if (local >= num)
				{
					flag = true;
					break;
				}
			}
			if (recipe.m_requireOnlyOneIngredient)
			{
				if (flag)
				{
					result = true;
				}
			}
			else if (!flag)
			{
				return false;
			}
		}
		if (!recipe.m_requireOnlyOneIngredient)
		{
			return true;
		}
		return result;
	}

	private static bool AppliesToStation(Requirement requirement, CraftingStation? station)
	{
		if (requirement?.m_resItem?.m_itemData?.m_shared != null)
		{
			return requirement.m_upgraderResource == ((Object)(object)station != (Object)null && station.m_upgrader);
		}
		return false;
	}

	internal static bool HasPieceRequirements(Player player, Piece piece)
	{
		if (!ModConfig.CraftFromNearbyChests.Value || (Object)(object)player != (Object)(object)Player.m_localPlayer)
		{
			return false;
		}
		if ((Object)(object)piece.m_craftingStation != (Object)null && !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, ((Component)player).transform.position) && !ZoneSystem.instance.GetGlobalKey((GlobalKeys)27))
		{
			return false;
		}
		if (!string.IsNullOrEmpty(piece.m_dlc) && !DLCMan.instance.IsDLCInstalled(piece.m_dlc))
		{
			return false;
		}
		Requirement[] resources = piece.m_resources;
		foreach (Requirement val in resources)
		{
			if (val?.m_resItem?.m_itemData?.m_shared != null && val.m_amount > 0)
			{
				string name = val.m_resItem.m_itemData.m_shared.m_name;
				int local = CountAvailable(player, name, -1, ((Component)player).transform.position, true);
				if (local < val.m_amount)
				{
					if (global::BestAutoSort.Patches.CraftingFromChestsContext.IsSelectedPiece(piece))
						PrefetchMissing(player, name, -1, val.m_amount - local);
					return false;
				}
			}
		}
		return true;
	}

	internal static ItemData? FindFirstRequiredItem(Player player, Recipe recipe, int qualityLevel, int craftMultiplier, out int amount, out int extraAmount)
	{
		amount = 0;
		extraAmount = 0;
		if (!ModConfig.CraftFromNearbyChests.Value || (Object)(object)player != (Object)(object)Player.m_localPlayer)
		{
			return null;
		}
		CraftingStation currentCraftingStation = player.GetCurrentCraftingStation();
		Requirement[] resources = recipe.m_resources;
		foreach (Requirement val in resources)
		{
			if (!AppliesToStation(val, currentCraftingStation))
			{
				continue;
			}
			int num = val.GetAmount(qualityLevel) * craftMultiplier;
			if (num <= 0)
			{
				continue;
			}
			string name = val.m_resItem.m_itemData.m_shared.m_name;
			int maxQuality = val.m_resItem.m_itemData.m_shared.m_maxQuality;
			int quality;
			for (quality = 1; quality <= maxQuality; quality++)
			{
				int local = CountAvailable(player, name, quality, ((Component)player).transform.position, true);
				if (local >= num)
				{
					ItemData val2 = ((Humanoid)player).GetInventory().GetItem(name, quality, false) ?? FindItem(((Component)player).transform.position, (ItemData candidate) => candidate.m_shared.m_name == name && candidate.m_quality == quality);
					if (val2 != null)
					{
						amount = num;
						extraAmount = val.m_extraAmountOnlyOneIngredient;
						return val2;
					}
				}
			}
		}
		return null;
	}

	/// <summary>
	/// Stage missing mats for the pressed recipe from foreign chests.
	/// Called once per craft press; vanilla DoCrafting runs seconds later.
	/// </summary>
	internal static void PrefetchForCraftPress(Player player, Recipe recipe, int qualityLevel, int craftMultiplier)
	{
		if (!ModConfig.CraftFromNearbyChests.Value || (Object)(object)player != (Object)(object)Player.m_localPlayer)
			return;
		if ((Object)(object)recipe == (Object)null)
			return;
		CraftingStation currentCraftingStation = player.GetCurrentCraftingStation();
		Requirement[] resources = recipe.m_resources;
		foreach (Requirement val in resources)
		{
			if (!AppliesToStation(val, currentCraftingStation))
				continue;
			int num = val.GetAmount(qualityLevel) * craftMultiplier;
			if (num <= 0 || val.m_resItem == null || val.m_resItem.m_itemData == null || val.m_resItem.m_itemData.m_shared == null)
				continue;
			string name = val.m_resItem.m_itemData.m_shared.m_name;
			int maxQuality = val.m_resItem.m_itemData.m_shared.m_maxQuality;
			for (int j = 1; j <= maxQuality; j++)
			{
				int local = CountAvailable(player, name, j, ((Component)player).transform.position, true);
				if (local < num)
					PrefetchMissing(player, name, j, num - local);
			}
		}
	}

	internal static void ConsumeRequirements(Player player, Requirement[] requirements, int qualityLevel, int itemQuality, int multiplier)
	{
		CraftingStation currentCraftingStation = player.GetCurrentCraftingStation();
		foreach (Requirement val in requirements)
		{
			if (AppliesToStation(val, currentCraftingStation))
			{
				int num = val.GetAmount(qualityLevel) * multiplier;
				if (num > 0)
				{
					ConsumeItem(player, val.m_resItem.m_itemData.m_shared.m_name, num, itemQuality);
				}
			}
		}
	}

	internal static int ConsumeItem(Player player, string name, int amount, int quality)
	{
		if (amount <= 0)
		{
			return 0;
		}
		int num = RemoveFromInventory(((Humanoid)player).GetInventory(), name, amount, quality);
		int num2 = amount - num;
		if (num2 <= 0 || !ModConfig.CraftFromNearbyChests.Value)
		{
			return num;
		}
		foreach (Container eligibleContainer in GetEligibleContainers(((Component)player).transform.position))
		{
			// Synchronous consumption — manager only (serial, no races).
			// Foreign chests are prefetched ahead of time (see requirement checks).
			if (!eligibleContainer.IsOwner())
				continue;
		int num3 = RemoveFromInventory(eligibleContainer.GetInventory(), name, num2, quality, eligibleContainer);
		num += num3;
		num2 -= num3;
			if (num2 <= 0)
			{
				break;
			}
		}
		return num;
	}

	internal static bool PlayerHasMatchingItem(Player player, Func<ItemData, bool> predicate)
	{
		return ((Humanoid)player).GetInventory().GetAllItems().Any<ItemData>(predicate);
	}

	internal static bool TryBorrowProductionItem(Component production, Humanoid user, Func<ItemData, bool> predicate, out ProductionItemLoan? loan)
	{
		loan = null;
		if (ModConfig.CraftFromNearbyChests.Value)
		{
			Player val = (Player)(object)((user is Player) ? user : null);
			if (val != null && !((Object)(object)val != (Object)(object)Player.m_localPlayer) && !PlayerHasMatchingItem(val, predicate))
			{
				Inventory inventory = ((Humanoid)val).GetInventory();
				IReadOnlyList<Container> eligibleContainers = GetEligibleContainers(production.transform.position);
				int num = 0;
				int num2 = 0;
				int num3 = 0;
				foreach (Container container in eligibleContainers)
				{
					ItemData val2 = container.GetInventory().GetAllItems().FirstOrDefault((ItemData candidate) => !candidate.m_shared.m_questItem && predicate(candidate) && ChestReserveStore.Available(container, candidate) > 0);
					if (val2 == null)
					{
						continue;
					}
					num++;
					if (!container.IsOwner())
					{
						num2++;
						PrefetchBorrow(val, container, val2);
						continue;
					}
					string name = val2.m_shared.m_name;
					if (container.GetInventory().ContainsItem(val2) && ChestReserveStore.Available(container, val2) > 0)
					{
						int quality = val2.m_quality;
						int playerCountBefore = inventory.CountItems(name, quality, true);
						if (TryLoanOneItem(container.GetInventory(), inventory, val2, playerCountBefore, out ItemData loanedItem) && loanedItem != null)
						{
							loan = new ProductionItemLoan(container, val, loanedItem);
							LogProductionDiagnostic(production, "Pulled " + name + " from " + ((Object)container).name + " for " + ((Object)production).name + ".", warning: false);
							return true;
						}
						num3++;
					}
				}
				LogProductionDiagnostic(production, $"Could not pull an input for {((Object)production).name}: {eligibleContainers.Count} eligible chest(s), " + $"{num} matching stack(s), {num2} ownership failure(s), " + $"{num3} transfer failure(s).", warning: true);
				return false;
			}
		}
		return false;
	}

	internal static bool TryBorrowProductionItemInPriority(Component production, Humanoid user, Func<ItemData, bool> playerPredicate, IReadOnlyList<string> preferredNames, out ProductionItemLoan? loan)
	{
		loan = null;
		if (ModConfig.CraftFromNearbyChests.Value)
		{
			Player val = (Player)(object)((user is Player) ? user : null);
			if (val != null && !((Object)(object)val != (Object)(object)Player.m_localPlayer) && !PlayerHasMatchingItem(val, playerPredicate))
			{
				Inventory inventory = ((Humanoid)val).GetInventory();
				IReadOnlyList<Container> eligibleContainers = GetEligibleContainers(production.transform.position);
				int num = 0;
				int num2 = 0;
				int num3 = 0;
				foreach (string preferredName in preferredNames)
				{
					foreach (Container container in eligibleContainers)
					{
						ItemData val2 = container.GetInventory().GetAllItems().FirstOrDefault((ItemData candidate) => !candidate.m_shared.m_questItem && candidate.m_shared.m_name == preferredName && ChestReserveStore.Available(container, candidate) > 0);
						if (val2 == null)
						{
							continue;
						}
						num++;
						if (!container.IsOwner())
						{
							num2++;
							PrefetchBorrow(val, container, val2);
						}
						else if (container.GetInventory().ContainsItem(val2) && ChestReserveStore.Available(container, val2) > 0)
						{
							int quality = val2.m_quality;
							int playerCountBefore = inventory.CountItems(preferredName, quality, true);
							if (TryLoanOneItem(container.GetInventory(), inventory, val2, playerCountBefore, out ItemData loanedItem) && loanedItem != null)
							{
								loan = new ProductionItemLoan(container, val, loanedItem);
								LogProductionDiagnostic(production, "Pulled " + preferredName + " from " + ((Object)container).name + " for " + ((Object)production).name + ".", warning: false);
								return true;
							}
							num3++;
						}
					}
				}
				LogProductionDiagnostic(production, "Could not pull a prioritized input for " + ((Object)production).name + ": " + $"{eligibleContainers.Count} eligible chest(s), {num} matching stack(s), " + $"{num2} ownership failure(s), {num3} transfer failure(s).", warning: true);
				return false;
			}
		}
		return false;
	}

	/// <summary>
	/// Prefetch one stack for a production borrow from a foreign chest.
	/// The manager re-validates reserves; the item lands in the player inventory next frame.
	/// </summary>
	private static void PrefetchBorrow(Player player, Container container, ItemData item)
	{
		if ((Object)(object)player == (Object)null || item == null || item.m_shared == null)
			return;
		if (!ChestTxService.IsShared(container) || container.IsOwner())
			return;
		TxOpItem op = ChestTxService.SnapshotItem(item, item.m_stack, -1, -1);
		if (op == null)
			return;
		TxOpCall call = new TxOpCall();
		call.Op = TxOp.Take;
		call.Items.Add(op);
		call.RespectReserves = true;
		ChestTxService.SubmitTakePrefetch(container, call, ((Humanoid)player).GetInventory());
	}

	private static void LogProductionDiagnostic(Component production, string message, bool warning)
	{
		int instanceID = ((Object)production).GetInstanceID();
		float realtimeSinceStartup = Time.realtimeSinceStartup;
		if (!LastProductionDiagnosticAt.TryGetValue(instanceID, out var value) || !(realtimeSinceStartup - value < 2f))
		{
			LastProductionDiagnosticAt[instanceID] = realtimeSinceStartup;
			if (warning)
			{
				Plugin.LogInstance.LogWarning((object)message);
			}
			else
			{
				Plugin.LogInstance.LogInfo((object)message);
			}
		}
	}

	private static bool TryLoanOneItem(Inventory source, Inventory playerInventory, ItemData sourceItem, int playerCountBefore, out ItemData? loanedItem)
	{
		loanedItem = null;
		playerInventory.MoveItemToThis(source, sourceItem, 1, -1, -1);
		if (playerInventory.CountItems(sourceItem.m_shared.m_name, sourceItem.m_quality, true) > playerCountBefore)
		{
			loanedItem = playerInventory.GetItem(sourceItem.m_shared.m_name, sourceItem.m_quality, false);
			return loanedItem != null;
		}
		if (!source.GetAllItems().Contains(sourceItem))
		{
			return false;
		}
		ItemData val = sourceItem.Clone();
		val.m_stack = 1;
		val.m_gridPos = new Vector2i(0, 0);
		if (!source.RemoveItem(sourceItem, 1))
		{
			return false;
		}
		playerInventory.GetAllItems().Add(val);
		playerInventory.m_onChanged?.Invoke();
		loanedItem = val;
		return true;
	}

	internal static void KeepLoanedItemSafe(Player player, ItemData item)
	{
		Inventory inventory = ((Humanoid)player).GetInventory();
		if (!inventory.GetAllItems().Contains(item) || IsValidUnoccupiedPosition(inventory, item))
		{
			return;
		}
		if (TryPlaceInFreePlayerSlot(inventory, item))
		{
			inventory.m_onChanged?.Invoke();
			return;
		}
		int stack = item.m_stack;
		if (inventory.RemoveItem(item))
		{
			ItemDrop.DropItem(item, stack, ((Component)player).transform.position + ((Component)player).transform.forward + Vector3.up, Quaternion.identity);
		}
	}

	private static bool TryPlaceInFreePlayerSlot(Inventory inventory, ItemData item)
	{
		int width = inventory.GetWidth();
		int height = inventory.GetHeight();
		HashSet<int> hashSet = new HashSet<int>(from candidate in inventory.GetAllItems()
			where candidate != item && candidate.m_gridPos.x >= 0 && candidate.m_gridPos.x < width && candidate.m_gridPos.y >= 0 && candidate.m_gridPos.y < height
			select candidate.m_gridPos.y * width + candidate.m_gridPos.x);
		for (int num = 0; num < height; num++)
		{
			for (int num2 = 0; num2 < width; num2++)
			{
				if (!hashSet.Contains(num * width + num2))
				{
					item.m_gridPos = new Vector2i(num2, num);
					return true;
				}
			}
		}
		return false;
	}

	private static bool IsValidUnoccupiedPosition(Inventory inventory, ItemData item)
	{
		int x = item.m_gridPos.x;
		int y = item.m_gridPos.y;
		if (x < 0 || y < 0 || x >= inventory.GetWidth() || y >= inventory.GetHeight())
		{
			return false;
		}
		return !inventory.GetAllItems().Any((ItemData candidate) => candidate != item && candidate.m_gridPos.x == x && candidate.m_gridPos.y == y);
	}

	internal static bool MatchesAnyConversion(ItemData item, IEnumerable<ItemDrop> inputs)
	{
		return inputs.Any((ItemDrop input) => input?.m_itemData?.m_shared != null && input.m_itemData.m_shared.m_name == item.m_shared.m_name);
	}

	internal static int CountAvailable(Player player, string name, int quality, Vector3 origin, bool localOnly = false)
	{
		int num = ((Humanoid)player).GetInventory().CountItems(name, quality, true);
		foreach (Container eligibleContainer in GetEligibleContainers(origin))
		{
			if (!AutoFeedService.IsLocked(eligibleContainer))
			{
				// Craft checks count only synchronously consumable stock (own + manager).
				// Foreign stock arrives via prefetch; display counts everything.
				if (localOnly && !eligibleContainer.IsOwner())
					continue;
				num += ChestReserveStore.Count(eligibleContainer, name, quality);
			}
		}
		return num;
	}

	private static readonly Dictionary<string, float> PrefetchCooldown = new Dictionary<string, float>(StringComparer.Ordinal);

	/// <summary>
	/// Prefetch missing material from foreign chests into the player inventory.
	/// Called from requirement checks (2s throttle per name). The manager re-validates.
	/// </summary>
	internal static void PrefetchMissing(Player player, string name, int quality, int missing)
	{
		if (!ModConfig.CraftFromNearbyChests.Value || missing <= 0)
			return;
		if ((Object)(object)player != (Object)(object)Player.m_localPlayer)
			return;
		float now = Time.realtimeSinceStartup;
		float next;
		if (PrefetchCooldown.TryGetValue(name, out next) && now < next)
			return;
		PrefetchCooldown[name] = now + 2f;
		int wanted = missing;
		int skippedLease = 0;
		int skippedLocal = 0;
		foreach (Container container in GetEligibleContainers(((Component)player).transform.position))
		{
			if (missing <= 0)
				break;
			if (!ChestTxService.IsShared(container))
			{
				string why;
				ChestTxService.IsSharedVerbose(container, out why);
				if (why.Contains("lease") || why.Contains("autofeed"))
					skippedLease++;
				continue;
			}
			if (container.IsOwner())
			{
				skippedLocal++;
				continue;
			}
			if (AutoFeedService.IsLocked(container))
				continue;
			Inventory chestInv = container.GetInventory();
			if (chestInv == null)
				continue;
			foreach (ItemData item in new List<ItemData>(chestInv.GetAllItems()))
			{
				if (missing <= 0)
					break;
				if (item == null || item.m_shared == null)
					continue;
				if (!string.Equals(item.m_shared.m_name, name, StringComparison.Ordinal))
					continue;
				if (quality >= 0 && item.m_quality != quality)
					continue;
				if (item.m_shared.m_questItem || !item.m_shared.m_autoStack)
					continue;
				if (ModConfig.SkipCustomData.Value && item.m_customData.Count > 0)
					continue;
				int n = missing < item.m_stack ? missing : item.m_stack;
				TxOpItem op = ChestTxService.SnapshotItem(item, n, -1, -1);
				if (op == null)
					continue;
				TxOpCall call = new TxOpCall();
				call.Op = TxOp.Take;
				call.Items.Add(op);
				call.RespectReserves = true;
				Inventory playerInv = ((Humanoid)player).GetInventory();
				ChestTxService.SubmitTakePrefetch(container, call, playerInv);
				missing -= n;
			}
		}
		if (missing > 0)
			Plugin.LogInstance.LogInfo((object)("[ChestTX] prefetch " + name + " short by " + missing + " of " + wanted + " (lease-locked=" + skippedLease + " local-owner=" + skippedLocal + ")"));
	}

	private static ItemData? FindItem(Vector3 origin, Func<ItemData, bool> predicate)
	{
		foreach (Container container in GetEligibleContainers(origin))
		{
			if (!AutoFeedService.IsLocked(container))
			{
				ItemData val = container.GetInventory().GetAllItems().FirstOrDefault((ItemData candidate) => predicate(candidate) && ChestReserveStore.Available(container, candidate) > 0);
				if (val != null)
				{
					return val;
				}
			}
		}
		return null;
	}

	private static int RemoveFromInventory(Inventory inventory, string name, int requested, int quality, Container? container = null)
	{
		if ((Object)(object)container != (Object)null)
		{
			int num = 0;
			ItemData[] array = inventory.GetAllItems().ToArray();
			foreach (ItemData val in array)
			{
				if (!(val.m_shared.m_name != name) && !val.m_shared.m_questItem && val.m_worldLevel >= Game.m_worldLevel && (quality < 0 || val.m_quality == quality))
				{
					int num2 = Math.Min(requested - num, ChestReserveStore.Available(container, val));
					if (num2 > 0 && inventory.RemoveItem(val, num2))
					{
						num += num2;
					}
					if (num == requested)
					{
						break;
					}
				}
			}
			return num;
		}
		int num3 = Mathf.Min(requested, inventory.CountItems(name, quality, true));
		if (num3 <= 0)
		{
			return 0;
		}
		try
		{
			_internalRemoval = true;
			inventory.RemoveItem(name, num3, quality, true);
			return num3;
		}
		finally
		{
			_internalRemoval = false;
		}
	}

	internal static IReadOnlyList<Container> GetEligibleContainersForTransfer(Vector3 origin)
	{
		return GetEligibleContainers(origin);
	}

	private static IReadOnlyList<Container> GetEligibleContainers(Vector3 origin)
	{
		if (Time.time <= _cacheExpiresAt)
		{
			Vector3 val = origin - _cachedOrigin;
			if ((val).sqrMagnitude <= 0.25f)
			{
				return CachedContainers;
			}
		}
		CachedContainers.Clear();
		_cachedOrigin = origin;
		_cacheExpiresAt = Time.time + 0.35f;
		float rangeSquared = ModConfig.SharedResourceRange.Value * ModConfig.SharedResourceRange.Value;
		CachedContainers.AddRange(Object.FindObjectsByType<Container>((FindObjectsSortMode)0).Where(delegate(Container container)
		{
			return IsEligible(container, origin, rangeSquared);
		}).OrderBy(delegate(Container container)
		{
			Vector3 val2 = ((Component)container).transform.position - origin;
			return (val2).sqrMagnitude;
		}));
		return CachedContainers;
	}

	private static bool IsEligible(Container container, Vector3 origin, float rangeSquared)
	{
		if ((Object)(object)container == (Object)null || !((Behaviour)container).isActiveAndEnabled)
		{
			return false;
		}
		Vector3 val = ((Component)container).transform.position - origin;
		if ((val).sqrMagnitude > rangeSquared)
		{
			return false;
		}
		// In a multi-view world in-use (open by anyone) is normal: the manager
		// serializes mutations via ChestTX, such chests must not be excluded.
		if ((Object)(object)((Component)container).GetComponent<Player>() != (Object)null || (Object)(object)((Component)container).GetComponent<TombStone>() != (Object)null)
		{
			return false;
		}
		if (!ModConfig.IncludeVehicleContainers.Value && ((Object)(object)container.m_wagon != (Object)null || (Object)(object)((Component)container).GetComponentInParent<Ship>() != (Object)null))
		{
			return false;
		}
		if (container.m_checkGuardStone && !PrivateArea.CheckAccess(((Component)container).transform.position, 0f, false, false))
		{
			return false;
		}
		long playerID = Game.instance.GetPlayerProfile().GetPlayerID();
		if (!(bool)ContainerCheckAccessMethod.Invoke(container, new object[1] { playerID }))
		{
			return false;
		}
		Piece val3 = ((Component)container).GetComponent<Piece>() ?? ((Component)container).GetComponentInParent<Piece>();
		if (!ModConfig.IncludeWorldContainers.Value)
		{
			if ((Object)(object)val3 != (Object)null)
			{
				return val3.IsPlacedByPlayer();
			}
			return false;
		}
		return true;
	}

}
