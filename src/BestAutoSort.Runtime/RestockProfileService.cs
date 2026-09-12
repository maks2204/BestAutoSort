using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BestAutoSort.Core;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace BestAutoSort.Runtime;

internal static class RestockProfileService
{
	private sealed class TrackedItem
	{
		internal Inventory Inventory { get; }

		internal RestockInventoryKind Kind { get; }

		internal ItemData Item { get; }

		internal TrackedItem(Inventory inventory, RestockInventoryKind kind, ItemData item)
		{
			Inventory = inventory;
			Kind = kind;
			Item = item;
		}
	}

	private sealed class CustomDataComparer : IEqualityComparer<KeyValuePair<string, string>>
	{
		internal static readonly CustomDataComparer Instance = new CustomDataComparer();

		public bool Equals(KeyValuePair<string, string> x, KeyValuePair<string, string> y)
		{
			if (string.Equals(x.Key, y.Key, StringComparison.Ordinal))
			{
				return string.Equals(x.Value, y.Value, StringComparison.Ordinal);
			}
			return false;
		}

		public int GetHashCode(KeyValuePair<string, string> pair)
		{
			return (StringComparer.Ordinal.GetHashCode(pair.Key) * 397) ^ StringComparer.Ordinal.GetHashCode(pair.Value);
		}
	}

	private const string PlayerDataKey = "dev.maks2204.bestautosort.restock-profiles";


	private const string HighlightName = "BestAutoSort_RestockHighlight";

	private static readonly FieldInfo ElementsField = AccessTools.Field(typeof(InventoryGrid), "m_elements");

	private static bool _reportedInvalidData;

	internal static bool IsTarget(ItemData? item)
	{
		string id;
		int target;
		if (item != null)
		{
			return RestockMarker.TryRead(item.m_customData, out id, out target);
		}
		return false;
	}

	internal static bool HasProfiles(Player player)
	{
		return LoadProfiles(player).Count > 0;
	}

	internal static bool TrySet(InventoryGrid grid, ItemData? item, bool enabled)
	{
		if (!TrySet(grid.GetInventory(), item, enabled))
		{
			return false;
		}
		RefreshHighlights(grid);
		return true;
	}

	private static bool TrySet(Inventory? inventory, ItemData? item, bool enabled)
	{
		if (!ModConfig.Enabled.Value || item == null)
		{
			return false;
		}
		Player localPlayer = Player.m_localPlayer;
		if ((Object)(object)localPlayer == (Object)null || inventory == null || inventory != ((Humanoid)localPlayer).GetInventory())
		{
			return false;
		}
		RestockInventoryKind kind = RestockInventoryKind.Player;
		if (string.IsNullOrWhiteSpace(ValheimItemCategoryClassifier.PrefabName(item)) || item.m_shared.m_maxStackSize <= 1)
		{
			return false;
		}
		List<RestockProfile> list = LoadProfiles(localPlayer);
		if (enabled)
		{
			ItemStackLock.SetLocked(item.m_customData, locked: false);
		}
		string id;
		if (!enabled && RestockMarker.TryRead(item.m_customData, out string existingId, out int target))
		{
			RestockMarker.Clear(item.m_customData);
			list.RemoveAll((RestockProfile profile) => string.Equals(profile.Id, existingId, StringComparison.Ordinal));
		}
		else if (enabled && !RestockMarker.TryRead(item.m_customData, out id, out target))
		{
			string id2 = Guid.NewGuid().ToString("N");
			int target2 = RestockTargetPolicy.GetTarget(item.m_shared.m_maxStackSize);
			RestockMarker.Set(item.m_customData, id2, target2);
			list.Add(CreateProfile(id2, kind, item, target2));
		}
		if (enabled && RestockMarker.TryRead(item.m_customData, out string id3, out target))
		{
			RestockProfileSlotPolicy.RemoveConflicts(list, kind, item.m_gridPos.x, item.m_gridPos.y, id3);
		}
		SynchronizeLiveTargets(localPlayer, list);
		SaveProfiles(localPlayer, list);
		inventory.m_onChanged?.Invoke();
		return true;
	}

	internal static void RefreshHighlights(InventoryGrid grid)
	{
		Inventory inventory = grid.GetInventory();
		Player localPlayer2 = Player.m_localPlayer;
		if (inventory == null || (Object)(object)localPlayer2 == (Object)null || inventory != ((Humanoid)localPlayer2).GetInventory() || !(ElementsField.GetValue(grid) is IList list))
		{
			return;
		}
		foreach (object item in list)
		{
			InventoryElement val = (InventoryElement)((item is InventoryElement) ? item : null);
			if (val != null && (bool)(Object)(object)val)
			{
				Vector2i position = val.Position;
				GameObject orCreateHighlight = GetOrCreateHighlight(((Component)val).gameObject);
				ItemData itemAt = inventory.GetItemAt(position.x, position.y);
			orCreateHighlight.SetActive(IsTarget(itemAt));
			}
		}
	}

	internal static List<ItemData> HideTargets(Inventory inventory)
	{
		List<ItemData> allItems = inventory.GetAllItems();
		List<ItemData> list = allItems.FindAll(IsTarget);
		foreach (ItemData item in list)
		{
			allItems.Remove(item);
		}
		return list;
	}

	internal static void RestockNearby()
	{
		Player localPlayer = Player.m_localPlayer;
		if (!ModConfig.Enabled.Value || (Object)(object)localPlayer == (Object)null)
		{
			return;
		}
		List<RestockProfile> list = LoadProfiles(localPlayer);
		SynchronizeLiveTargets(localPlayer, list);
		if (list.Count == 0)
		{
			SaveProfiles(localPlayer, list);
			return;
		}
		IReadOnlyList<Container> eligibleContainersForTransfer = NearbyResourceService.GetEligibleContainersForTransfer(((Component)localPlayer).transform.position);
		Dictionary<Container, List<TransferRecord>> dictionary = new Dictionary<Container, List<TransferRecord>>();
		List<RestockNeed> remoteNeeds = new List<RestockNeed>();
		foreach (RestockProfile item in list.ToList())
		{
			Inventory inventory = ((Humanoid)localPlayer).GetInventory();
			if (inventory == null || item.X < 0 || item.Y < 0 || item.X >= inventory.GetWidth() || item.Y >= inventory.GetHeight())
			{
				continue;
			}
			ItemData itemAt = inventory.GetItemAt(item.X, item.Y);
			if (itemAt != null && (!RestockMarker.TryRead(itemAt.m_customData, out string id, out int _) || !string.Equals(id, item.Id, StringComparison.Ordinal)))
			{
				continue;
			}
			int num;
			if (itemAt == null)
			{
				num = int.MaxValue;
			}
			else
			{
				item.Target = RestockTargetPolicy.GetTarget(itemAt.m_shared.m_maxStackSize);
				num = Mathf.Max(0, item.Target - itemAt.m_stack);
			}
			if (num == 0)
			{
				continue;
			}
			foreach (Container item2 in eligibleContainersForTransfer)
			{
				if (num == 0)
				{
					break;
				}
				if (item2.IsOwner())
				{
					num = DrainSyncNeeds(item2, inventory, item, ref itemAt, num, dictionary);
					continue;
				}
				if (!ChestTxService.IsShared(item2))
					continue;
				// Remote manager: collect needs, fulfill via Take tx chain.
				Inventory snapshot = item2.GetInventory();
				if (snapshot == null)
					continue;
				foreach (ItemData item3 in new List<ItemData>(snapshot.GetAllItems()))
				{
					if (num == 0)
						break;
					if (!MatchesProfile(item3, item) || IsDifferentRestockTarget(item3, item) || !HasCompatibleCustomData(item3, itemAt))
						continue;
					if (itemAt == null)
					{
						item.Target = RestockTargetPolicy.GetTarget(item3.m_shared.m_maxStackSize);
						num = item.Target;
					}
					int want = Mathf.Min(num, ChestReserveStore.Available(item2, item3));
					if (want <= 0)
						continue;
					RestockNeed need = new RestockNeed();
					need.Container = item2;
					need.PrefabHash = item3.m_dropPrefab != null ? item3.m_dropPrefab.name.GetStableHashCode() : 0;
					need.Name = item3.m_shared.m_name;
					need.Quality = item3.m_quality;
					need.Variant = item3.m_variant;
					need.World = item3.m_worldLevel;
					need.X = item3.m_gridPos.x;
					need.Y = item3.m_gridPos.y;
					need.Amount = want;
					need.MaxStack = item3.m_shared.m_maxStackSize;
					need.Profile = item;
					need.Profiles = list;
					remoteNeeds.Add(need);
					num -= want;
				}
			}
		}
		SaveProfiles(localPlayer, list);
		foreach (KeyValuePair<Container, List<TransferRecord>> item4 in dictionary)
		{
			TransferVisuals.PlayToPlayer(item4.Value, item4.Key);
		}
		if (remoteNeeds.Count > 0)
			EnqueueRemoteNeeds(localPlayer, remoteNeeds);
	}

	private static int DrainSyncNeeds(Container container, Inventory destination, RestockProfile profile, ref ItemData itemAt, int num, Dictionary<Container, List<TransferRecord>> dictionary)
	{
		Inventory inventory2 = container.GetInventory();
		if (inventory2 == null)
			return num;
		foreach (ItemData item3 in new List<ItemData>(inventory2.GetAllItems()))
		{
			if (num == 0)
				break;
			if (!MatchesProfile(item3, profile) || IsDifferentRestockTarget(item3, profile) || !HasCompatibleCustomData(item3, itemAt))
				continue;
			if (itemAt == null)
			{
				profile.Target = RestockTargetPolicy.GetTarget(item3.m_shared.m_maxStackSize);
				num = profile.Target;
			}
			int num2 = MoveToTarget(inventory2, item3, destination, profile, itemAt, Mathf.Min(num, ChestReserveStore.Available(container, item3)));
			if (num2 > 0)
			{
				itemAt = destination.GetItemAt(profile.X, profile.Y);
				num = ((itemAt != null) ? Mathf.Max(0, itemAt.m_shared.m_maxStackSize - itemAt.m_stack) : 0);
				if (!dictionary.TryGetValue(container, out var value))
				{
					value = new List<TransferRecord>();
					dictionary.Add(container, value);
				}
				value.Add(new TransferRecord(item3.m_shared.m_name, item3.GetIcon(), num2, item3.m_shared.m_maxStackSize));
			}
		}
		return num;
	}

	private sealed class RestockNeed
	{
		internal Container Container;
		internal int PrefabHash;
		internal string Name;
		internal int Quality;
		internal int Variant;
		internal int World;
		internal int X;
		internal int Y;
		internal int Amount;
		internal int MaxStack;
		internal RestockProfile Profile;
		internal List<RestockProfile> Profiles;
	}

	private static readonly Queue<RestockNeed> RemoteChain = new Queue<RestockNeed>();
	private static bool ChainRunning;

	private static void EnqueueRemoteNeeds(Player player, List<RestockNeed> needs)
	{
		for (int i = 0; i < needs.Count; i++)
			RemoteChain.Enqueue(needs[i]);
		if (!ChainRunning)
		{
			ChainRunning = true;
			PumpRemoteChain(player);
		}
	}

	private static void PumpRemoteChain(Player player)
	{
		if (RemoteChain.Count == 0)
		{
			ChainRunning = false;
			return;
		}
		if ((Object)(object)player == (Object)null)
		{
			RemoteChain.Clear();
			ChainRunning = false;
			return;
		}
		RestockNeed need = RemoteChain.Dequeue();
		try
		{
			if ((Object)(object)need.Container == (Object)null || !ChestTxService.IsShared(need.Container))
			{
				PumpRemoteChain(player);
				return;
			}
		TxOpItem opItem = new TxOpItem();
		opItem.PrefabHash = need.PrefabHash;
		opItem.Amount = need.Amount;
		opItem.X = need.X;
		opItem.Y = need.Y;
		opItem.MaxStack = need.MaxStack;
		opItem.Snapshot = new ItemData();
		opItem.Snapshot.m_quality = need.Quality;
		opItem.Snapshot.m_variant = need.Variant;
		opItem.Snapshot.m_worldLevel = need.World;
		opItem.Snapshot.m_gridPos = new Vector2i(need.X, need.Y);
		opItem.Snapshot.m_shared = new SharedData();
		opItem.Snapshot.m_shared.m_name = need.Name;
		List<TxOpItem> items = new List<TxOpItem>();
		items.Add(opItem);
		ChestTxService.RequestTakeCustom(need.Container, items, true, delegate (List<DecodedTake> results, TxStatus status, uint rev)
			{
				try
				{
					if (results != null)
					{
						for (int i = 0; i < results.Count; i++)
						{
							DecodedTake take = results[i];
							if (take != null && take.Item != null && take.Accepted > 0)
								PlaceReceived(player, need, take.Item);
						}
					}
					SaveProfiles(player, need.Profiles);
				}
				finally
				{
					PumpRemoteChain(player);
				}
			});
		}
		catch (Exception ex)
		{
			Plugin.LogInstance.LogWarning((object)("Restock chain step failed, continuing: " + ex.Message));
			PumpRemoteChain(player);
		}
	}

	private static void PlaceReceived(Player player, RestockNeed need, ItemData received)
	{
		Inventory destination = ((Humanoid)player).GetInventory();
		if (destination == null || received == null)
			return;
		ItemData targetItem = destination.GetItemAt(need.Profile.X, need.Profile.Y);
		if (targetItem != null)
		{
			if (!string.Equals(targetItem.m_shared.m_name, received.m_shared.m_name, StringComparison.Ordinal))
			{
				// Slot busy: keep nearby without loss, or send back on full.
				if (!destination.AddItem(received))
					ChestTxService.CompensateTakeBackItem(need.Container, need.PrefabHash, received);
				destination.m_onChanged?.Invoke();
				return;
			}
			int room = targetItem.m_shared.m_maxStackSize - targetItem.m_stack;
			int put = received.m_stack < room ? received.m_stack : room;
			if (put <= 0)
			{
				ChestTxService.CompensateTakeBackItem(need.Container, need.PrefabHash, received);
				return;
			}
			targetItem.m_stack += put;
			received.m_stack -= put;
			destination.m_onChanged?.Invoke();
			if (received.m_stack > 0)
				ChestTxService.CompensateTakeBackItem(need.Container, need.PrefabHash, received);
			return;
		}
		received.m_gridPos = new Vector2i(need.Profile.X, need.Profile.Y);
		RestockMarker.Clear(received.m_customData);
		RestockMarker.Set(received.m_customData, need.Profile.Id, need.Profile.Target);
		if (!destination.AddItem(received, new Vector2i(need.Profile.X, need.Profile.Y)))
		{
			if (!destination.AddItem(received))
				ChestTxService.CompensateTakeBackItem(need.Container, need.PrefabHash, received);
		}
		destination.m_onChanged?.Invoke();
	}

	internal static void PersistLiveProfiles(Player player)
	{
		if (!((Object)(object)player != (Object)(object)Player.m_localPlayer))
		{
			List<RestockProfile> profiles = LoadProfiles(player);
			SynchronizeLiveTargets(player, profiles);
			SaveProfiles(player, profiles);
		}
	}

	private static int MoveToTarget(Inventory source, ItemData sourceItem, Inventory destination, RestockProfile profile, ItemData? targetItem, int requested)
	{
		int num = Mathf.Min(requested, sourceItem.m_stack);
		if (targetItem != null)
		{
			num = Mathf.Min(num, targetItem.m_shared.m_maxStackSize - targetItem.m_stack);
			if (num <= 0)
			{
				return 0;
			}
			targetItem.m_stack += num;
			RemoveFromSource(source, sourceItem, num, profile.Id);
			destination.m_onChanged?.Invoke();
			return num;
		}
		ItemData val = sourceItem.Clone();
		val.m_stack = num;
		val.m_gridPos = new Vector2i(profile.X, profile.Y);
		RestockMarker.Clear(val.m_customData);
		RestockMarker.Set(val.m_customData, profile.Id, profile.Target);
		if (!destination.AddItem(val, new Vector2i(profile.X, profile.Y)))
		{
			return 0;
		}
		RemoveFromSource(source, sourceItem, num, profile.Id);
		return num;
	}

	private static void RemoveFromSource(Inventory source, ItemData item, int amount, string fulfilledProfileId)
	{
		item.m_stack -= amount;
		if (item.m_stack <= 0)
		{
			source.RemoveItem(item);
			return;
		}
		if (RestockMarker.TryRead(item.m_customData, out string id, out int _) && string.Equals(id, fulfilledProfileId, StringComparison.Ordinal))
		{
			RestockMarker.Clear(item.m_customData);
		}
		source.m_onChanged?.Invoke();
	}

	private static bool MatchesProfile(ItemData item, RestockProfile profile)
	{
		if (item != null && !item.m_shared.m_questItem && item.m_shared.m_maxStackSize > 1 && string.Equals(ValheimItemCategoryClassifier.PrefabName(item), profile.PrefabName, StringComparison.OrdinalIgnoreCase) && item.m_quality == profile.Quality)
		{
			return item.m_variant == profile.Variant;
		}
		return false;
	}

	private static bool IsDifferentRestockTarget(ItemData item, RestockProfile profile)
	{
		if (RestockMarker.TryRead(item.m_customData, out string id, out int _))
		{
			return !string.Equals(id, profile.Id, StringComparison.Ordinal);
		}
		return false;
	}

	private static bool HasCompatibleCustomData(ItemData source, ItemData? target)
	{
		if (target == null)
		{
			return !FilteredCustomData(source).Any();
		}
		return FilteredCustomData(source).OrderBy<KeyValuePair<string, string>, string>((KeyValuePair<string, string> pair) => pair.Key, StringComparer.Ordinal).SequenceEqual<KeyValuePair<string, string>>(FilteredCustomData(target).OrderBy<KeyValuePair<string, string>, string>((KeyValuePair<string, string> pair) => pair.Key, StringComparer.Ordinal), CustomDataComparer.Instance);
	}

	private static IEnumerable<KeyValuePair<string, string>> FilteredCustomData(ItemData item)
	{
		return item.m_customData.Where((KeyValuePair<string, string> pair) => !string.Equals(pair.Key, "BestAutoSort.RestockId", StringComparison.Ordinal) && !string.Equals(pair.Key, "BestAutoSort.RestockTarget", StringComparison.Ordinal) && !string.Equals(pair.Key, "BestAutoSort.StackLocked", StringComparison.Ordinal));
	}

	private static void SynchronizeLiveTargets(Player player, List<RestockProfile> profiles)
	{
		Dictionary<string, RestockProfile> dictionary = profiles.ToDictionary<RestockProfile, string>((RestockProfile profile) => profile.Id, StringComparer.Ordinal);
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (TrackedItem item in EnumerateTrackedItems(player))
		{
			if (!RestockMarker.TryRead(item.Item.m_customData, out string id, out int target))
			{
				continue;
			}
			if (!hashSet.Add(id))
			{
				RestockMarker.Clear(item.Item.m_customData);
				item.Inventory.m_onChanged?.Invoke();
				continue;
			}
			int target2 = RestockTargetPolicy.GetTarget(item.Item.m_shared.m_maxStackSize);
			if (target != target2)
			{
				target = target2;
				RestockMarker.Set(item.Item.m_customData, id, target);
				item.Inventory.m_onChanged?.Invoke();
			}
			RestockProfile restockProfile = CreateProfile(id, item.Kind, item.Item, target);
			if (dictionary.TryGetValue(id, out var value))
			{
				CopyProfile(restockProfile, value);
				continue;
			}
			profiles.Add(restockProfile);
			dictionary.Add(id, restockProfile);
		}
	}

	private static IEnumerable<TrackedItem> EnumerateTrackedItems(Player player)
	{
		Inventory main = ((Humanoid)player).GetInventory();
		foreach (ItemData allItem in main.GetAllItems())
		{
			yield return new TrackedItem(main, RestockInventoryKind.Player, allItem);
		}
	}

	private static RestockProfile CreateProfile(string id, RestockInventoryKind kind, ItemData item, int target)
	{
		return new RestockProfile(id, kind, item.m_gridPos.x, item.m_gridPos.y, ValheimItemCategoryClassifier.PrefabName(item), item.m_quality, item.m_variant, target);
	}

	private static void CopyProfile(RestockProfile source, RestockProfile destination)
	{
		destination.InventoryKind = source.InventoryKind;
		destination.X = source.X;
		destination.Y = source.Y;
		destination.PrefabName = source.PrefabName;
		destination.Quality = source.Quality;
		destination.Variant = source.Variant;
		destination.Target = source.Target;
	}

	private static List<RestockProfile> LoadProfiles(Player player)
	{
		if (!player.m_customData.TryGetValue("dev.maks2204.bestautosort.restock-profiles", out var value))
		{
			return new List<RestockProfile>();
		}
		if (RestockProfileCodec.TryDeserialize(value, out List<RestockProfile> profiles))
		{
			return profiles;
		}
		if (!_reportedInvalidData)
		{
			_reportedInvalidData = true;
			Plugin.LogInstance.LogWarning((object)"Ignored invalid saved BestAutoSort restock profile data.");
		}
		return new List<RestockProfile>();
	}

	private static void SaveProfiles(Player player, List<RestockProfile> profiles)
	{
		if (profiles.Count == 0)
		{
			player.m_customData.Remove("dev.maks2204.bestautosort.restock-profiles");
		}
		else
		{
			player.m_customData["dev.maks2204.bestautosort.restock-profiles"] = RestockProfileCodec.Serialize(profiles);
		}
	}

	private static GameObject GetOrCreateHighlight(GameObject slot)
	{
		Transform val = slot.transform.Find("BestAutoSort_RestockHighlight");
		if ((Object)(object)val != (Object)null)
		{
			return ((Component)val).gameObject;
		}
		GameObject val2 = new GameObject("BestAutoSort_RestockHighlight", new Type[4]
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
		((Graphic)component).color = new Color(0.08f, 0.72f, 1f, 0.28f);
		((Graphic)component).raycastTarget = false;
		Outline component3 = val2.GetComponent<Outline>();
		((Shadow)component3).effectColor = new Color(0.15f, 0.85f, 1f, 0.98f);
		((Shadow)component3).effectDistance = new Vector2(2f, -2f);
		((Shadow)component3).useGraphicAlpha = false;
		return val2;
	}
}
