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

	private static int _cascadePending;

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
		int cleaned = 0;
		foreach (ItemData allItem in ((Humanoid)localPlayer).GetInventory().GetAllItems())
		{
			if (CustomDataTags.StripBenign(allItem))
				cleaned++;
		}
		if (cleaned > 0)
			Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack stripped GearSlots tags from " + cleaned + " item(s)"));
		List<Container> list = FindEligibleContainers(localPlayer);
		Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack start chests=" + list.Count));
		if (list.Count == 0)
			LogEmptyDiagnosis(localPlayer);
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
		_cascadePending = 0;
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
			if (item.m_shared.m_questItem || !ValheimItemCategoryClassifier.IsAutoStackable(item))
				continue;
			if (((Humanoid)player).IsItemEquiped(item))
				continue;
			if (ModConfig.ProtectHotbar.Value && item.m_gridPos.y == 0)
				continue;
			if (ItemLockService.IsLocked(item) || RestockProfileService.IsTarget(item))
				continue;
			if (ModConfig.SkipCustomData.Value && CustomDataTags.HasForeignData(item))
				continue;
			if (!QuickStackTransfer.CanAcceptFromRule(rule, item, destNames, destCats))
				continue;
			TxOpItem op = ChestTxService.SnapshotAuto(item, item.m_stack);
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
		Container[] targets = _targets.ToArray();
		for (int ci = 0; ci < targets.Length; ci++)
		{
			Container chest = targets[ci];
			if (session != _session)
				yield break;
			if ((Object)(object)chest == (Object)null || (Object)(object)player == (Object)null)
				continue;
			// Единый путь для владельца и остальных: SubmitCall сам ведёт
			// владельца локально в очередь (тот же кадр, серийно), остальных — по сети.
			// Коммит всегда идёт через менеджер: броадкаст полётов срабатывает для всех.
			string sharedWhy;
			if (!chest.IsOwner() && !ChestTxService.IsSharedVerbose(chest, out sharedWhy))
			{
				Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack chest skipped: " + sharedWhy));
				continue;
			}
			// Candidates from the local snapshot; the manager re-validates rules at commit.
			List<TxOpItem> candidates = CollectCandidates(player, chest);
			if (candidates.Count == 0)
			{
				Plugin.LogInstance.LogInfo((object)"[ChestTX] quickstack chest skipped (no candidates)");
				continue;
			}
			TxOpCall call = new TxOpCall();
			call.Op = TxOp.AddBatch;
			call.Items.AddRange(candidates);
			call.EnforceRule = true;
			_submitted++;
			List<Container> rest = new List<Container>();
			for (int ri = ci + 1; ri < targets.Length; ri++)
			{
				if ((Object)(object)targets[ri] != (Object)null)
					rest.Add(targets[ri]);
			}
			SubmitCascade(chest, call, player, session, rest);
			if (spacing != null)
			{
				yield return spacing;
			}
			if (Time.realtimeSinceStartup >= deadline)
				break;
		}
		if (session != _session)
			yield break;
		float cascadeDeadline = Time.realtimeSinceStartup + 3f;
		while (_cascadePending > 0 && Time.realtimeSinceStartup < cascadeDeadline)
		{
			if (spacing != null)
				yield return spacing;
			else
				yield return null;
		}
		if (session != _session)
			yield break;
		_routine = null;
		Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack session done moved=" + _totalMoved + " submitted=" + _submitted));
		if (_totalMoved == 0)
			TellPlayer("Quick stack moved nothing (no matching items, chest full, or rules block them).");
		CompleteSession();
	}

	/// <summary>
	/// Submit one chest; whatever it does not accept cascades to the next untried
	/// chest (fill the first, rest to the second). Remainder is re-snapshotted from
	/// live inventory synchronously inside the completion (no interleave window).
	/// </summary>
	private void SubmitCascade(Container chest, TxOpCall call, Player player, int session, List<Container> rest)
	{
		Inventory playerInv = ((Humanoid)player).GetInventory();
		_cascadePending++;
		ChestTxService.SubmitCallDetailed(chest, call, playerInv, delegate (List<TransferRecord> records, List<int> accepted, TxStatus status, TxCompletionKind disp)
		{
			try
			{
				int movedHere = 0;
				for (int i = 0; i < records.Count; i++)
					movedHere += records[i].Amount;
				_totalMoved += movedHere;
				if (session == _session && records.Count > 0)
					TransferVisuals.Play(records, chest);
				if (disp == TxCompletionKind.CommittedMultiAddDetailsUnavailable)
				{
					// Terminal (issue #10): the chest committed but per-item counts are
					// unknown. The remainder is already stored — cascading it onward as a
					// new tx would duplicate items. Warned at the tx layer; stop here.
					Plugin.LogInstance.LogInfo((object)"[ChestTX] quickstack details-unavailable: no cascade (already stored)");
					return;
				}
				if (rest == null || rest.Count == 0 || call == null || call.Items == null)
					return;
				TxOpCall next = new TxOpCall();
				next.Op = TxOp.AddBatch;
				next.EnforceRule = true;
				for (int i = 0; i < call.Items.Count; i++)
				{
					TxOpItem sent = call.Items[i];
					if (sent == null)
						continue;
					int got = (accepted != null && i < accepted.Count) ? accepted[i] : 0;
					int rem = sent.Amount - got;
					if (rem <= 0)
						continue;
					ItemData src = sent.SourceRef;
					if (src == null || !playerInv.GetAllItems().Contains(src))
						src = ResolveRemainder(playerInv, sent);
					if (src == null)
						continue;
					TxOpItem op = ChestTxService.SnapshotAuto(src, rem < src.m_stack ? rem : src.m_stack);
					if (op != null)
						next.Items.Add(op);
				}
				if (next.Items.Count == 0)
					return;
				for (int r = 0; r < rest.Count; r++)
				{
					Container target = rest[r];
					if ((Object)(object)target == (Object)null)
						continue;
					if (!target.IsOwner())
					{
						string why;
						if (!ChestTxService.IsSharedVerbose(target, out why))
							continue;
					}
					List<Container> nextRest = new List<Container>();
					for (int k = r + 1; k < rest.Count; k++)
					{
						if ((Object)(object)rest[k] != (Object)null)
							nextRest.Add(rest[k]);
					}
					Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack cascade " + next.Items.Count + " item(s) onward"));
					_submitted++;
					SubmitCascade(target, next, player, session, nextRest);
					return;
				}
				Plugin.LogInstance.LogInfo((object)"[ChestTX] quickstack cascade exhausted (no chest took the rest)");
			}
			finally
			{
				if (_cascadePending > 0)
					_cascadePending--;
			}
		});
	}

	private static ItemData ResolveRemainder(Inventory playerInv, TxOpItem sent)
	{
		if (playerInv == null || sent == null || sent.Snapshot == null || sent.Snapshot.m_shared == null)
			return null;
		string name = sent.Snapshot.m_shared.m_name;
		int quality = sent.Snapshot.m_quality;
		foreach (ItemData it in playerInv.GetAllItems())
		{
			if (it == null || it.m_shared == null || it.m_stack <= 0)
				continue;
			if (it.m_shared.m_name == name && (quality < 0 || it.m_quality == quality))
				return it;
		}
		return null;
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
			List<TransferRecord> records = new List<TransferRecord>();
			QuickStackTransfer.MoveMatching(openContainer, ((Humanoid)player).GetInventory(), records);
			// Owned chest mutated directly (no tx queue): persist like the manager.
			TxReflect.UpdateRows(openContainer);
			TxReflect.SaveContainer(openContainer);
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
		ChestTxService.SubmitCall(openContainer, call, ((Humanoid)player).GetInventory(), delegate (List<TransferRecord> records2, TxCompletionKind disp2)
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
			if (!allItem.m_shared.m_questItem && ValheimItemCategoryClassifier.IsAutoStackable(allItem) && !((Humanoid)player).IsItemEquiped(allItem) && (!ModConfig.ProtectHotbar.Value || allItem.m_gridPos.y != 0) && !ItemLockService.IsLocked(allItem) && !RestockProfileService.IsTarget(allItem) && (!ModConfig.SkipCustomData.Value || !CustomDataTags.HasForeignData(allItem)))
			{
				return true;
			}
		}
		return false;
	}

	private static void LogEmptyDiagnosis(Player player)
	{
		try
		{
			Vector3 playerPosition = ((Component)player).transform.position;
			float range = ModConfig.NearbyRange.Value;
			float rangeSquared = range * range;
			int inRange = 0;
			System.Collections.Generic.Dictionary<string, int> rejects = new System.Collections.Generic.Dictionary<string, int>();
			foreach (Container container in Object.FindObjectsByType<Container>((FindObjectsSortMode)0))
			{
				if ((Object)(object)container == (Object)null)
					continue;
				Vector3 val = ((Component)container).transform.position - playerPosition;
				if ((val).sqrMagnitude > rangeSquared)
					continue;
				inRange++;
				string why = "rule-seeds";
				if (!((Behaviour)container).isActiveAndEnabled)
					why = "disabled";
				else if (AutoFeedService.IsLocked(container))
					why = "autofeed-lease";
				else if ((Object)(object)((Component)container).GetComponent<Player>() != (Object)null)
					why = "player-inventory";
				else if ((Object)(object)((Component)container).GetComponent<TombStone>() != (Object)null)
					why = "tombstone";
				else if (!ModConfig.IncludeVehicleContainers.Value && ((Object)(object)container.m_wagon != (Object)null || (Object)(object)((Component)container).GetComponentInParent<Ship>() != (Object)null))
					why = "vehicle-policy";
				else if (container.m_checkGuardStone && !PrivateArea.CheckAccess(((Component)container).transform.position, 0f, false, false))
					why = "guard-stone";
				else if (!HasPrivacyAccess(container))
					why = "private";
				else if (!ModConfig.IncludeWorldContainers.Value)
				{
					Piece piece = ((Component)container).GetComponent<Piece>() ?? ((Component)container).GetComponentInParent<Piece>();
					if ((Object)(object)piece == (Object)null || !piece.IsPlacedByPlayer())
						why = "world-container-policy";
				}
				else if (!ChestTxService.IsShared(container))
				{
					string sharedWhy;
					ChestTxService.IsSharedVerbose(container, out sharedWhy);
					why = "not-shared:" + sharedWhy;
				}
				int n;
				rejects.TryGetValue(why, out n);
				rejects[why] = n + 1;
			}
			if (inRange == 0)
			{
				Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack diagnosis: no containers within " + range + "m at all"));
				return;
			}
			int movable = 0;
			int eq = 0;
			int hot = 0;
			int noauto = 0;
			int locked = 0;
			int reserved = 0;
			int quest = 0;
			int custom = 0;
			System.Collections.Generic.Dictionary<string, int> customKeys = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.Dictionary<string, int> movableStacks = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.Dictionary<string, int> movableUnits = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.List<string> movableOrder = new System.Collections.Generic.List<string>();
			System.Collections.Generic.Dictionary<string, int> equippedStacks = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.List<string> equippedOrder = new System.Collections.Generic.List<string>();
			System.Collections.Generic.Dictionary<string, int> hotbarStacks = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.List<string> hotbarOrder = new System.Collections.Generic.List<string>();
			System.Collections.Generic.Dictionary<string, int> noautoStacks = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.List<string> noautoOrder = new System.Collections.Generic.List<string>();
			System.Collections.Generic.Dictionary<string, int> lockedStacks = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.List<string> lockedOrder = new System.Collections.Generic.List<string>();
			System.Collections.Generic.Dictionary<string, int> reservedStacks = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.List<string> reservedOrder = new System.Collections.Generic.List<string>();
			System.Collections.Generic.Dictionary<string, int> questStacks = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.List<string> questOrder = new System.Collections.Generic.List<string>();
			System.Collections.Generic.Dictionary<string, int> customStacks = new System.Collections.Generic.Dictionary<string, int>();
			System.Collections.Generic.List<string> customOrder = new System.Collections.Generic.List<string>();
			System.Action<System.Collections.Generic.Dictionary<string, int>, System.Collections.Generic.List<string>, string> trackFiltered = delegate (System.Collections.Generic.Dictionary<string, int> stacks, System.Collections.Generic.List<string> order, string name)
			{
				if (name == null)
					name = "?";
				bool seen = stacks.ContainsKey(name);
				int fc;
				stacks.TryGetValue(name, out fc);
				stacks[name] = fc + 1;
				if (!seen)
					order.Add(name);
			};
			foreach (ItemData allItem in ((Humanoid)player).GetInventory().GetAllItems())
			{
				if (allItem == null || allItem.m_shared == null)
					continue;
				if (allItem.m_shared.m_questItem)
				{
					quest++;
					trackFiltered(questStacks, questOrder, allItem.m_shared.m_name ?? "?");
					continue;
				}
				if (!ValheimItemCategoryClassifier.IsAutoStackable(allItem))
				{
					noauto++;
					trackFiltered(noautoStacks, noautoOrder, allItem.m_shared.m_name ?? "?");
					continue;
				}
				if (((Humanoid)player).IsItemEquiped(allItem))
				{
					eq++;
					trackFiltered(equippedStacks, equippedOrder, allItem.m_shared.m_name ?? "?");
					continue;
				}
				if (ModConfig.ProtectHotbar.Value && allItem.m_gridPos.y == 0)
				{
					hot++;
					trackFiltered(hotbarStacks, hotbarOrder, allItem.m_shared.m_name ?? "?");
					continue;
				}
				if (ItemLockService.IsLocked(allItem))
				{
					locked++;
					trackFiltered(lockedStacks, lockedOrder, allItem.m_shared.m_name ?? "?");
					continue;
				}
				if (RestockProfileService.IsTarget(allItem))
				{
					reserved++;
					trackFiltered(reservedStacks, reservedOrder, allItem.m_shared.m_name ?? "?");
					continue;
				}
				if (ModConfig.SkipCustomData.Value && CustomDataTags.HasForeignData(allItem))
				{
					custom++;
					trackFiltered(customStacks, customOrder, allItem.m_shared.m_name ?? "?");
					foreach (System.Collections.Generic.KeyValuePair<string, string> kv in allItem.m_customData)
					{
						int kc;
						customKeys.TryGetValue(kv.Key, out kc);
						customKeys[kv.Key] = kc + 1;
					}
					continue;
				}
				movable++;
				string movableKey = allItem.m_shared.m_name ?? "?";
				bool seenMovable = movableStacks.ContainsKey(movableKey);
				int msc;
				movableStacks.TryGetValue(movableKey, out msc);
				movableStacks[movableKey] = msc + 1;
				int mun;
				movableUnits.TryGetValue(movableKey, out mun);
				movableUnits[movableKey] = mun + allItem.m_stack;
				if (!seenMovable)
					movableOrder.Add(movableKey);
			}
			Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack diagnosis: inventory movable=" + movable + " (equipped=" + eq + " hotbar=" + hot + " noautostack=" + noauto + " locked=" + locked + " reserved=" + reserved + " quest=" + quest + " custom=" + custom + ")"));
			if (movableOrder.Count == 0)
			{
				Plugin.LogInstance.LogInfo((object)"[ChestTX] quickstack diagnosis: movable items: none");
			}
			else
			{
				System.Text.StringBuilder msb = new System.Text.StringBuilder();
				msb.Append("[ChestTX] quickstack diagnosis: movable items: ");
				int mshown = 0;
				foreach (string mname in movableOrder)
				{
					if (mshown >= 10)
						break;
					if (mshown > 0)
						msb.Append(", ");
					msb.Append(mname).Append("x").Append(movableStacks[mname]).Append("(").Append(movableUnits[mname]).Append(")");
					mshown++;
				}
				if (movableOrder.Count > mshown)
					msb.Append(", +").Append(movableOrder.Count - mshown).Append(" more");
				Plugin.LogInstance.LogInfo((object)msb.ToString());
			}
			System.Func<System.Collections.Generic.Dictionary<string, int>, System.Collections.Generic.List<string>, string> formatBucket = delegate (System.Collections.Generic.Dictionary<string, int> stacks, System.Collections.Generic.List<string> order)
			{
				if (order.Count == 0)
					return "-";
				System.Text.StringBuilder bsb = new System.Text.StringBuilder();
				int bshown = 0;
				foreach (string bname in order)
				{
					if (bshown >= 5)
						break;
					if (bshown > 0)
						bsb.Append(", ");
					bsb.Append(bname).Append("x").Append(stacks[bname]);
					bshown++;
				}
				if (order.Count > bshown)
					bsb.Append(", +").Append(order.Count - bshown).Append(" more");
				return bsb.ToString();
			};
			System.Text.StringBuilder fsb = new System.Text.StringBuilder();
			fsb.Append("[ChestTX] quickstack diagnosis: filtered items: ");
			fsb.Append("equipped=[").Append(formatBucket(equippedStacks, equippedOrder)).Append("] ");
			fsb.Append("hotbar=[").Append(formatBucket(hotbarStacks, hotbarOrder)).Append("] ");
			fsb.Append("noautostack=[").Append(formatBucket(noautoStacks, noautoOrder)).Append("] ");
			fsb.Append("locked=[").Append(formatBucket(lockedStacks, lockedOrder)).Append("] ");
			fsb.Append("reserved=[").Append(formatBucket(reservedStacks, reservedOrder)).Append("] ");
			fsb.Append("quest=[").Append(formatBucket(questStacks, questOrder)).Append("] ");
			fsb.Append("custom=[").Append(formatBucket(customStacks, customOrder)).Append("]");
			Plugin.LogInstance.LogInfo((object)fsb.ToString());
			Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack diagnosis: config StorageMatchMode=" + ModConfig.StorageMatchMode.Value + " NearbyRange=" + range + "m"));
			Container openChest = InventoryAccess.CurrentContainer(InventoryGui.instance);
			if ((Object)(object)openChest == (Object)null)
			{
				Plugin.LogInstance.LogInfo((object)"[ChestTX] quickstack diagnosis: open-container: none");
			}
			else
			{
				Vector3 openDelta = ((Component)openChest).transform.position - playerPosition;
				float openDist = (openDelta).magnitude;
				bool openInRange = (openDelta).sqrMagnitude <= rangeSquared;
				bool openEligible = IsEligible(openChest, player, FindMovablePlayerItems(player), playerPosition, rangeSquared);
				Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack diagnosis: open-container: " + ((Object)openChest).name + " (" + Math.Round(openDist, 1) + "m) inRange=" + openInRange + " eligible=" + openEligible));
			}
			if (customKeys.Count > 0)
			{
				System.Text.StringBuilder cksb = new System.Text.StringBuilder();
				cksb.Append("[ChestTX] quickstack diagnosis: customData keys: ");
				bool cfirst = true;
				foreach (System.Collections.Generic.KeyValuePair<string, int> kv in customKeys)
				{
					if (!cfirst)
						cksb.Append(", ");
					cfirst = false;
					cksb.Append(kv.Key).Append("x").Append(kv.Value);
				}
				Plugin.LogInstance.LogInfo((object)cksb.ToString());
			}
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			sb.Append("[ChestTX] quickstack diagnosis: ").Append(inRange).Append(" in range, rejected: ");
			bool first = true;
			foreach (System.Collections.Generic.KeyValuePair<string, int> kv in rejects)
			{
				if (!first)
					sb.Append(", ");
				first = false;
				sb.Append(kv.Key).Append("x").Append(kv.Value);
			}
			Plugin.LogInstance.LogInfo((object)sb.ToString());
		}
		catch (System.Exception ex)
		{
			Plugin.LogInstance.LogInfo((object)("[ChestTX] quickstack diagnosis failed: " + ex.Message));
		}
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
			if (!allItem.m_shared.m_questItem && ValheimItemCategoryClassifier.IsAutoStackable(allItem) && !((Humanoid)player).IsItemEquiped(allItem) && (!ModConfig.ProtectHotbar.Value || allItem.m_gridPos.y != 0) && !ItemLockService.IsLocked(allItem) && !RestockProfileService.IsTarget(allItem) && (!ModConfig.SkipCustomData.Value || !CustomDataTags.HasForeignData(allItem)))
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
