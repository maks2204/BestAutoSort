using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BestAutoSort.Core;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal sealed class QuickStackService
{
	private sealed class ItemMatchIndex
	{
		internal HashSet<string> Names { get; } = new HashSet<string>();

		internal HashSet<string> PrefabNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		internal HashSet<ItemCategory> Categories { get; } = new HashSet<ItemCategory>();
	}

	private readonly Plugin _plugin;

	private List<Container> _targets = new List<Container>();

	private Coroutine? _routine;
	private static int _totalMoved;
	private static int _submitted;

	private int _session;

	private Action? _onCompleted;

	internal QuickStackService(Plugin plugin)
	{
		_plugin = plugin;
	}

	internal void BeginNearbyQuickStack(Action? onCompleted = null)
	{
		Player localPlayer = Player.m_localPlayer;
		if (!ModConfig.Enabled.Value || (Object)(object)localPlayer == (Object)null)
		{
			Plugin.LogInstance.LogInfo((object)"[ChestTX] quickstack ignored (disabled or no player)");
			return;
		}
		if (_routine != null)
		{
			Plugin.LogInstance.LogInfo((object)"[ChestTX] quickstack ignored (session already running)");
			return;
		}
		_session++;
		_targets = new List<Container>();
		_totalMoved = 0;
		_submitted = 0;
		_onCompleted = onCompleted;
		List<Container> list = FindEligibleContainers(localPlayer);
		Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack start chests=" + list.Count));
		bool flag = StackIntoOpenContainer(localPlayer, list);
		if (list.Count == 0)
		{
			if (flag || !HasMovablePlayerItems(localPlayer) || RestockProfileService.HasProfiles(localPlayer))
			{
				Plugin.LogInstance.LogInfo((object)"[ChestTX] quickstack nothing to do");
				CompleteSession();
				return;
			}
			TellPlayer("No eligible nearby chests contain matching items or category seeds.");
			CompleteSession();
			return;
		}
		_targets.AddRange(list);
		_routine = ((MonoBehaviour)_plugin).StartCoroutine(RequestStacking(_session));
	}

	internal void Reset()
	{
		if (_routine != null)
		{
			((MonoBehaviour)_plugin).StopCoroutine(_routine);
		}
		_routine = null;
		_targets.Clear();
		_onCompleted = null;
	}

	private static List<TxOpItem> CollectCandidates(Player player, Container chest)
	{
		List<TxOpItem> result = new List<TxOpItem>();
		Inventory playerInv = ((Humanoid)player).GetInventory();
		Inventory chestInv = chest.GetInventory();
		if (playerInv == null || chestInv == null)
			return result;
		HashSet<string> destNames = new HashSet<string>();
		HashSet<ItemCategory> destCats = new HashSet<ItemCategory>();
		QuickStackTransfer.DestSeeds(chestInv, destNames, destCats);
		ChestStorageRule rule = ChestRuleStore.Read(chest);
		foreach (ItemData item in new List<ItemData>(playerInv.GetAllItems()))
		{
			if (item == null || item.m_shared == null)
				continue;
			if (item.m_shared.m_questItem || !item.m_shared.m_autoStack)
				continue;
			if (((Humanoid)player).IsItemEquiped(item))
				continue;
			if (ModConfig.ProtectHotbar.Value && item.m_gridPos.y == 0)
				continue;
			if (ItemLockService.IsLocked(item) || RestockProfileService.IsTarget(item))
				continue;
			if (ModConfig.SkipCustomData.Value && item.m_customData.Count > 0)
				continue;
			if (!QuickStackTransfer.CanAcceptFromRule(rule, item, destNames, destCats))
				continue;
			TxOpItem op = ChestTxService.SnapshotItem(item, item.m_stack, -1, -1);
			if (op != null)
				result.Add(op);
		}
		return result;
	}

	private IEnumerator RequestStacking(int session)
	{
		try
		{
			yield return RequestStackingInner(session);
		}
		finally
		{
			// The coroutine must free its slot even on exception,
			// otherwise the Stack button dies until the game restarts.
			if (session == _session)
				_routine = null;
		}
	}

	private IEnumerator RequestStackingInner(int session)
	{
		WaitForSeconds spacing = ((!(ModConfig.RequestSpacing.Value > 0f)) ? ((WaitForSeconds)null) : new WaitForSeconds(ModConfig.RequestSpacing.Value));
		float deadline = Time.realtimeSinceStartup + 5f;
		Player player = Player.m_localPlayer;
		foreach (Container chest in _targets.ToArray())
		{
			if (session != _session)
				yield break;
			if ((Object)(object)chest == (Object)null || (Object)(object)player == (Object)null)
				continue;
			if (chest.IsOwner())
			{
				List<TransferRecord> direct = new List<TransferRecord>();
				QuickStackTransfer.MoveMatching(chest, ((Humanoid)player).GetInventory(), direct);
				int movedHere = 0;
				for (int i = 0; i < direct.Count; i++)
					movedHere += direct[i].Amount;
				_totalMoved += movedHere;
				Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack owner-direct moved=" + movedHere));
				if (direct.Count > 0)
					TransferVisuals.Play(direct, chest);
				continue;
			}
			if (!ChestTxService.IsShared(chest))
				continue;
			// Candidates from the local snapshot; the manager re-validates rules at commit.
			List<TxOpItem> candidates = CollectCandidates(player, chest);
			if (candidates.Count == 0)
			{
				Plugin.LogInstance.LogInfo((object)"[ChestTX] quickstack chest skipped (no candidates)");
				continue;
			}
				continue;
			TxOpCall call = new TxOpCall();
			call.Op = TxOp.AddBatch;
			call.Items.AddRange(candidates);
			call.EnforceRule = true;
			_submitted++;
			ChestTxService.SubmitCall(chest, call, ((Humanoid)player).GetInventory(), delegate (List<TransferRecord> records)
				{
					if (session == _session && records.Count > 0)
						TransferVisuals.Play(records, chest);
				});
			if (spacing != null)
			{
				yield return spacing;
			}
			if (Time.realtimeSinceStartup >= deadline)
				break;
		}
		if (session != _session)
			yield break;
		_routine = null;
		Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack session done moved=" + _totalMoved + " submitted=" + _submitted));
		if (_totalMoved == 0 && _submitted == 0)
			TellPlayer("Quick stack moved nothing (no matching items, chest full, or rules block them).");
		CompleteSession();
	}

	private static bool StackIntoOpenContainer(Player player, ICollection<Container> containers)
	{
		Container openContainer = InventoryAccess.CurrentContainer(InventoryGui.instance);
		if ((Object)(object)openContainer == (Object)null || !containers.Remove(openContainer))
		{
			return false;
		}
		if (!ChestTxService.IsShared(openContainer))
		{
			if (!openContainer.IsOwner())
				return true;
			if (!openContainer.IsOwner())
				return true;
			List<TransferRecord> records = new List<TransferRecord>();
			QuickStackTransfer.MoveMatching(openContainer, ((Humanoid)player).GetInventory(), records);
			TransferVisuals.Play(records, openContainer);
			return true;
		}
		List<TxOpItem> candidates = CollectCandidates(player, openContainer);
		Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack open-chest candidates=" + candidates.Count));
		if (candidates.Count == 0)
			return true;
		TxOpCall call = new TxOpCall();
		call.Op = TxOp.AddBatch;
		call.Items.AddRange(candidates);
		call.EnforceRule = true;
		_submitted++;
		ChestTxService.SubmitCall(openContainer, call, ((Humanoid)player).GetInventory(), delegate (List<TransferRecord> records2)
			{
				if (records2.Count > 0)
					TransferVisuals.Play(records2, openContainer);
				RestockProfileService.RestockNearby();
			});
		return true;
	}

	private static bool HasMovablePlayerItems(Player player)
	{
		foreach (ItemData allItem in ((Humanoid)player).GetInventory().GetAllItems())
		{
			if (!allItem.m_shared.m_questItem && allItem.m_shared.m_autoStack && !((Humanoid)player).IsItemEquiped(allItem) && (!ModConfig.ProtectHotbar.Value || allItem.m_gridPos.y != 0) && !ItemLockService.IsLocked(allItem) && !RestockProfileService.IsTarget(allItem) && (!ModConfig.SkipCustomData.Value || allItem.m_customData.Count <= 0))
			{
				return true;
			}
		}
		return false;
	}

	private static List<Container> FindEligibleContainers(Player player)
	{
		Vector3 playerPosition = ((Component)player).transform.position;
		float rangeSquared = ModConfig.NearbyRange.Value * ModConfig.NearbyRange.Value;
		ItemMatchIndex movableItems = FindMovablePlayerItems(player);
		return Object.FindObjectsByType<Container>((FindObjectsSortMode)0).Where(delegate(Container container)
		{
			return IsEligible(container, player, movableItems, playerPosition, rangeSquared);
		}).OrderBy(delegate(Container container)
		{
			Vector3 val = ((Component)container).transform.position - playerPosition;
			return (val).sqrMagnitude;
		})
			.ToList();
	}

	internal static IEnumerable<string> Preview(Player player)
	{
		HashSet<Container> eligible = new HashSet<Container>(FindEligibleContainers(player));
		Inventory source = CloneInventory(((Humanoid)player).GetInventory());
		int total = 0;
		foreach (Container item in (from container in Object.FindObjectsByType<Container>((FindObjectsSortMode)0).Where(delegate(Container container)
			{
				if ((Object)(object)container != (Object)null)
				{
					Vector3 val2 = ((Component)container).transform.position - ((Component)player).transform.position;
					return (val2).sqrMagnitude <= ModConfig.NearbyRange.Value * ModConfig.NearbyRange.Value;
				}
				return false;
			})
			orderby (!((Object)(object)container == (Object)(object)InventoryAccess.CurrentContainer(InventoryGui.instance))) ? 1 : 0
			select container).ThenBy(delegate(Container container)
		{
			Vector3 val2 = ((Component)container).transform.position - ((Component)player).transform.position;
			return (val2).sqrMagnitude;
		}))
		{
			string name = ((Object)item).name;
			Vector3 val = ((Component)item).transform.position - ((Component)player).transform.position;
			string text = name + " (" + Math.Round((val).magnitude, 1) + "m)";
			if (!eligible.Contains(item))
			{
				string text2 = (AutoFeedService.IsLocked(item) ? "Auto Feed operation in progress" : ((!HasPrivacyAccess(item)) ? "private" : ((!ChestAuthority.WardAccess(item, player.GetPlayerID())) ? "ward access denied" : ((!ModConfig.IncludeVehicleContainers.Value && ((Object)(object)item.m_wagon != (Object)null || (Object)(object)((Component)item).GetComponentInParent<Ship>() != (Object)null)) ? "vehicle policy" : ((!ModConfig.IncludeWorldContainers.Value && ((Object)(object)((Component)item).GetComponent<Piece>() == (Object)null || !((Component)item).GetComponent<Piece>().IsPlacedByPlayer())) ? "world-container policy" : "protected container or no movable items match its rule/seeds")))));
				yield return text + ": skipped — " + text2;
				continue;
			}
			List<TransferRecord> records = new List<TransferRecord>();
			int num = QuickStackTransfer.MoveMatchingInventory(CloneInventory(item.GetInventory()), ChestRuleStore.Read(item), source, records);
			total += num;
			yield return text + ": " + num + " item(s)" + ((num == 0) ? " — full or earlier destinations accepted them" : "");
		}
		yield return "Preview total: " + total + " item(s). No inventories changed; availability can change before quick stack runs.";
	}

	private static Inventory CloneInventory(Inventory original)
	{
		Inventory val = new Inventory("BestAutoSort preview", original.GetBkg(), original.GetWidth(), original.GetHeight());
		val.GetAllItems().AddRange(from item in original.GetAllItems()
			select item.Clone());
		return val;
	}

	private static ItemMatchIndex FindMovablePlayerItems(Player player)
	{
		ItemMatchIndex itemMatchIndex = new ItemMatchIndex();
		foreach (ItemData allItem in ((Humanoid)player).GetInventory().GetAllItems())
		{
			if (!allItem.m_shared.m_questItem && allItem.m_shared.m_autoStack && !((Humanoid)player).IsItemEquiped(allItem) && (!ModConfig.ProtectHotbar.Value || allItem.m_gridPos.y != 0) && !ItemLockService.IsLocked(allItem) && !RestockProfileService.IsTarget(allItem) && (!ModConfig.SkipCustomData.Value || allItem.m_customData.Count <= 0))
			{
				itemMatchIndex.Names.Add(allItem.m_shared.m_name);
				itemMatchIndex.PrefabNames.Add(ValheimItemCategoryClassifier.PrefabName(allItem));
				itemMatchIndex.Categories.Add(ValheimItemCategoryClassifier.Classify(allItem));
			}
		}
		return itemMatchIndex;
	}

	private static bool IsEligible(Container container, Player player, ItemMatchIndex movableItems, Vector3 playerPosition, float rangeSquared)
	{
		if ((Object)(object)container == (Object)null || !((Behaviour)container).isActiveAndEnabled)
		{
			return false;
		}
		if (AutoFeedService.IsLocked(container))
		{
			return false;
		}
		Vector3 val = ((Component)container).transform.position - playerPosition;
		if ((val).sqrMagnitude > rangeSquared)
		{
			return false;
		}
		// In a multi-view world in-use (open by anyone) is normal: the manager
		// serializes mutations via ChestTX, such chests must not be excluded.
		if ((Object)(object)((Component)container).GetComponent<Player>() != (Object)null)
		{
			return false;
		}
		if ((Object)(object)((Component)container).GetComponent<TombStone>() != (Object)null)
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
		if (!HasPrivacyAccess(container))
		{
			return false;
		}
		Piece val3 = ((Component)container).GetComponent<Piece>() ?? ((Component)container).GetComponentInParent<Piece>();
		if (!ModConfig.IncludeWorldContainers.Value && ((Object)(object)val3 == (Object)null || !val3.IsPlacedByPlayer()))
		{
			return false;
		}
		ChestStorageRule chestStorageRule = ChestRuleStore.Read(container);
		if (!chestStorageRule.IsAutomatic)
		{
			return MatchesExplicitRule(movableItems, chestStorageRule);
		}
		foreach (ItemData allItem in container.GetInventory().GetAllItems())
		{
			if (movableItems.Names.Contains(allItem.m_shared.m_name))
			{
				return true;
			}
			if (ModConfig.StorageMatchMode.Value == StorageMatchMode.ExactItemOrCategory && movableItems.Categories.Contains(ValheimItemCategoryClassifier.Classify(allItem)))
			{
				return true;
			}
		}
		return false;
	}

	private static bool MatchesExplicitRule(ItemMatchIndex movableItems, ChestStorageRule rule)
	{
		if (rule.Scope == ChestRuleScope.Items)
		{
			foreach (string prefabName in movableItems.PrefabNames)
			{
				if (rule.Matches(prefabName, ItemCategory.Miscellaneous))
				{
					return true;
				}
			}
			return false;
		}
		foreach (ItemCategory category in movableItems.Categories)
		{
			if (rule.Matches(string.Empty, category))
			{
				return true;
			}
		}
		return false;
	}

	private static bool HasPrivacyAccess(Container container)
	{
		if ((int)container.m_privacy == 2)
		{
			return true;
		}
		if ((int)container.m_privacy == 1)
		{
			return false;
		}
		Piece val = ((Component)container).GetComponent<Piece>() ?? ((Component)container).GetComponentInParent<Piece>();
		long playerID = Game.instance.GetPlayerProfile().GetPlayerID();
		if ((Object)(object)val != (Object)null)
		{
			return val.GetCreator() == playerID;
		}
		return false;
	}

	private static void TellPlayer(string message)
	{
		Player localPlayer = Player.m_localPlayer;
		if (localPlayer != null)
		{
			((Character)localPlayer).Message((MessageType)2, message, 0, (Sprite)null, false);
		}
	}

	private void CompleteSession()
	{
		Action onCompleted = _onCompleted;
		_onCompleted = null;
		if (onCompleted == null)
		{
			return;
		}
		try
		{
			onCompleted();
		}
		catch (Exception ex)
		{
			Plugin.LogInstance.LogError((object)("Restock after nearby Stack failed: " + ex));
		}
	}
}
