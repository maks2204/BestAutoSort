using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BestAutoSort.Core;
using BestAutoSort.Tx;
using BestAutoSort.TxCore;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class AutoFeedService
{
	private sealed class ScanCooldown
	{
		internal float Next;
	}

	private const string LeaseKey = "BestAutoSort.AutoFeedLeaseUntil.v1";

	/// <summary>Animals with a Take already in flight (animal -> expiry).</summary>
	private static readonly Dictionary<ZDOID, float> FeedingAnimals = new Dictionary<ZDOID, float>();

	private static readonly Dictionary<int, float> LastProductionDiagnosticAt = new Dictionary<int, float>();

	private static void LogProductionDiagnostic(Component production, string message, bool warning)
	{
		int instanceID = ((Object)production).GetInstanceID();
		float realtimeSinceStartup = Time.realtimeSinceStartup;
		float value;
		if (!LastProductionDiagnosticAt.TryGetValue(instanceID, out value) || !(realtimeSinceStartup - value < 2f))
		{
			LastProductionDiagnosticAt[instanceID] = realtimeSinceStartup;
			if (warning)
				Plugin.LogInstance.LogWarning((object)message);
			else
				Plugin.LogInstance.LogInfo((object)message);
		}
	}

	private static readonly FieldInfo TameableNetViewField = AccessTools.Field(typeof(Tameable), "m_nview");

	private static readonly FieldInfo MonsterAiField = AccessTools.Field(typeof(Tameable), "m_monsterAI");

	private static readonly MethodInfo ConsumedItemMethod = AccessTools.Method(typeof(Tameable), "OnConsumedItem", (Type[])null, (Type[])null);

	private static readonly FieldInfo ContainerNetViewField = AccessTools.Field(typeof(Container), "m_nview");

	private static readonly MethodInfo GetPrefabNameMethod = AccessTools.Method(typeof(ZNetView), "GetPrefabName", (Type[])null, (Type[])null);

	private static readonly MethodInfo InventoryChangedMethod = AccessTools.Method(typeof(Inventory), "Changed", new Type[2]
	{
		typeof(bool),
		typeof(bool)
	}, (Type[])null);

	private static ConditionalWeakTable<Tameable, ScanCooldown> ScanCooldowns = new ConditionalWeakTable<Tameable, ScanCooldown>();

	private static readonly BoundedScanQueue<Tameable> ScanQueue = new BoundedScanQueue<Tameable>(256);

	private static readonly HashSet<Container> LoadedContainers = new HashSet<Container>();

	private static Container[]? _containerSnapshot;

	private static ZNet? _sessionNet;

	private static ZNetScene? _sessionScene;

	private static bool _discoverySeeded;

	private static readonly Stopwatch ScanBudget = new Stopwatch();

	private static Tameable? _scanningAnimal;

	private static Container[]? _scanContainers;

	private static Dictionary<string, ItemDrop>? _scanFood;

	private static int _scanIndex;

	private static Container? _nearestFood;

	private static float _nearestDistance;



	internal static void Attach(Container container)
	{
		EnsureSession();
		TrackContainer(container);
	}

	internal static void TryFeed(Tameable tameable)
	{
		if (!Plugin.IsActive)
		{
			return;
		}
		EnsureSession();
		if (!ModConfig.Enabled.Value || !ModConfig.AutoFeedEnabled.Value || (Object)(object)tameable == (Object)null || (Object)(object)ZNet.instance == (Object)null)
		{
			return;
		}
		object value = TameableNetViewField.GetValue(tameable);
		ZNetView animalView = (ZNetView)((value is ZNetView) ? value : null);
		object value2 = MonsterAiField.GetValue(tameable);
		MonsterAI val = (MonsterAI)((value2 is MonsterAI) ? value2 : null);
		// Wave-2: the scheduler stays animal-owner-side (this IsOwner gate is on the
		// ANIMAL, not a chest, and is intentionally unchanged): the server never
		// initiates feeding for non-owned animals. Chest Takes below go through
		// the server ChestTX manager queue (RequestTakeCustom, authority-routed).
		if (!((Object)(object)animalView == (Object)null) && animalView.IsValid() && animalView.IsOwner() && !((Object)(object)val == (Object)null) && !((BaseAI)val).IsAlerted() && tameable.IsHungry())
		{
			float realtimeSinceStartup = Time.realtimeSinceStartup;
			ScanCooldown value3 = ScanCooldowns.GetValue(tameable, (Tameable _) => new ScanCooldown());
			float feedExp;
			if (AutoFeedPolicy.ShouldScan(realtimeSinceStartup, value3.Next) && (!FeedingAnimals.TryGetValue(animalView.GetZDO().m_uid, out feedExp) || realtimeSinceStartup >= feedExp) && _scanningAnimal != tameable && ScanQueue.Enqueue(tameable))
			{
				value3.Next = realtimeSinceStartup + ModConfig.AutoFeedInterval.Value;
				(((Component)tameable).GetComponent<AutoFeedAnimalTracker>() ?? ((Component)tameable).gameObject.AddComponent<AutoFeedAnimalTracker>()).Animal = tameable;
			}
		}
	}

	private static void UpdateScans()
	{
		if ((Object)(object)ZNet.instance == (Object)null || (Object)(object)ZNetScene.instance == (Object)null)
		{
			return;
		}
		if (!_discoverySeeded)
		{
			Container[] array = Object.FindObjectsByType<Container>((FindObjectsSortMode)0);
			for (int i = 0; i < array.Length; i++)
			{
				TrackContainer(array[i]);
			}
			_discoverySeeded = true;
		}
		if (!ModConfig.Enabled.Value || !ModConfig.AutoFeedEnabled.Value)
		{
			ScanQueue.Clear();
			ClearScan();
			return;
		}
		ScanBudget.Restart();
		int num = 32;
		while (num > 0 && ScanBudget.ElapsedMilliseconds < 2)
		{
			if ((Object)(object)_scanningAnimal == (Object)null)
			{
				ClearScan();
				Tameable val = ScanQueue.Dequeue();
				if ((Object)(object)val == (Object)null)
				{
					if (ScanQueue.Count == 0)
					{
						break;
					}
					num--;
					continue;
				}
				if (!TryGetHungryOwner(val, out ZNetView _, out MonsterAI ai))
				{
					num--;
					continue;
				}
				_scanningAnimal = val;
				_scanFood = AcceptedFood(ai);
				_scanContainers = _containerSnapshot ?? (_containerSnapshot = LoadedContainers.ToArray());
				_nearestDistance = float.MaxValue;
			}
			if (_scanIndex < _scanContainers.Length)
			{
				num--;
				Container val2 = _scanContainers[_scanIndex++];
				if ((Object)(object)val2 == (Object)null)
				{
					continue;
				}
				Vector3 val3 = ((Component)val2).transform.position - ((Component)_scanningAnimal).transform.position;
				float sqrMagnitude = (val3).sqrMagnitude;
				if (!(sqrMagnitude >= _nearestDistance) && !(sqrMagnitude > ModConfig.AutoFeedRange.Value * ModConfig.AutoFeedRange.Value))
				{
					long num2;
					if (!ZNet.instance.IsServer())
					{
						Player localPlayer = Player.m_localPlayer;
						num2 = ((localPlayer != null) ? localPlayer.GetPlayerID() : 0);
					}
					else
					{
						num2 = Creator(val2);
					}
					long actor = num2;
					if (IsEligible(val2, ((Component)_scanningAnimal).transform.position, actor) && HasFood(val2, _scanFood))
					{
						_nearestFood = val2;
						_nearestDistance = sqrMagnitude;
					}
				}
			}
			else
			{
				num--;
				Tameable scanningAnimal = _scanningAnimal;
				Container nearestFood = _nearestFood;
				ClearScan();
				if ((Object)(object)nearestFood != (Object)null)
				{
					DispatchFeed(scanningAnimal, nearestFood);
				}
			}
		}
		ScanBudget.Stop();
	}

	private static bool HasFood(Container container, Dictionary<string, ItemDrop> accepted)
	{
		return container.GetInventory().GetAllItems().Any((ItemData item) => !item.m_shared.m_questItem && accepted.ContainsKey(item.m_shared.m_name) && ChestReserveStore.Available(container, item) > 0);
	}

	private static bool TryGetHungryOwner(Tameable animal, out ZNetView? view, out MonsterAI? ai)
	{
		object obj;
		if (!((Object)(object)animal == (Object)null))
		{
			object value = TameableNetViewField.GetValue(animal);
			obj = ((value is ZNetView) ? value : null);
		}
		else
		{
			obj = null;
		}
		view = (ZNetView?)obj;
		object obj2;
		if (!((Object)(object)animal == (Object)null))
		{
			object value2 = MonsterAiField.GetValue(animal);
			obj2 = ((value2 is MonsterAI) ? value2 : null);
		}
		else
		{
			obj2 = null;
		}
		ai = (MonsterAI?)obj2;
		if ((Object)(object)animal != (Object)null && (Object)(object)view != (Object)null && view.IsValid() && view.IsOwner() && (Object)(object)ai != (Object)null && !((BaseAI)ai).IsAlerted())
		{
			return animal.IsHungry();
		}
		return false;
	}

	/// <summary>
	/// Feed one unit via the tx queue (manager-serialized single-owner order,
	/// multi-key ZDO writes still non-atomic): no ownership
	/// transfer, no snapshots, no lease. The animal owner feeds directly; remote
	/// managers commit the Take like any client request.
	/// </summary>
	private static void DispatchFeed(Tameable tameable, Container container)
	{
		if ((Object)(object)ZNet.instance == (Object)null)
			return;
		if (!TryGetHungryOwner(tameable, out ZNetView view, out MonsterAI ai))
			return;
		ZDOID animalId = view.GetZDO().m_uid;
		float now = Time.realtimeSinceStartup;
		float exp;
		if (FeedingAnimals.TryGetValue(animalId, out exp) && now < exp)
			return;
		// Feeder identity must match the CanUse server branch: any server
		// (dedicated or host) stamps the chest creator; pure clients stamp self.
		// Stamped position is the ANIMAL's (matches the range check semantics).
		long feederId;
		if (ZNet.instance.IsServer())
			feederId = Creator(container);
		else
		{
			Player localPlayer = Player.m_localPlayer;
			if (localPlayer == null)
				return;
			feederId = localPlayer.GetPlayerID();
		}
		Vector3 animalPos = ((Component)tameable).transform.position;
		if (!IsEligible(container, animalPos, feederId))
			return;
		Dictionary<string, ItemDrop> accepted = AcceptedFood(ai);
		ItemData pick = null;
		foreach (ItemData candidate in container.GetInventory().GetAllItems())
		{
			if (candidate == null || candidate.m_shared == null)
				continue;
			if (!candidate.m_shared.m_questItem && accepted.ContainsKey(candidate.m_shared.m_name) && ChestReserveStore.Available(container, candidate) > 0)
			{
				pick = candidate;
				break;
			}
		}
		if (pick == null)
			return;
		TxOpItem op = ChestTxService.SnapshotItem(pick, 1, -1, -1);
		if (op == null)
			return;
		FeedingAnimals[animalId] = now + 15f;
		List<TxOpItem> items = new List<TxOpItem>();
		items.Add(op);
		// Flights must end at the ANIMAL, not at the feeder: stamp its position.
		ChestTxService.RequestTakeCustom(container, items, true, delegate(List<DecodedTake> decoded, TxStatus status, uint rev, TxCompletionKind disp)
		{
			FeedingAnimals.Remove(animalId);
			if (disp == TxCompletionKind.Indeterminate)
			{
				// Outcome unknown: the chest may have been debited. Never feed,
				// never compensate-as-failed, never take again - stay loud.
				LogProductionDiagnostic(tameable, "Feed take indeterminate: outcome unknown, check the chest before retrying.", warning: true);
				return;
			}
			DecodedTake got = null;
			if (decoded != null)
			{
				foreach (DecodedTake dt in decoded)
				{
					if (dt != null && dt.Accepted > 0 && dt.Item != null)
					{
						got = dt;
						break;
					}
				}
			}
			if (got == null)
				return;
			// Re-verify the animal before consuming: still ours, hungry, calm.
			ZNetScene scene = ZNetScene.instance;
			GameObject go = (scene != null) ? scene.FindInstance(animalId) : null;
			Tameable live = (go != null) ? go.GetComponent<Tameable>() : null;
			if (live == null || !TryGetHungryOwner(live, out ZNetView _, out MonsterAI liveAi))
			{
				// Took the food but cannot feed: send it back instead of voiding it.
				ItemData back = got.Item;
				back.m_stack = got.Accepted;
				ChestTxService.CompensateTakeBackItem(container, got.PrefabHash, back);
				return;
			}
			Dictionary<string, ItemDrop> liveAccepted = AcceptedFood(liveAi);
			ItemDrop prefab;
			if (got.Item.m_shared != null && liveAccepted.TryGetValue(got.Item.m_shared.m_name, out prefab) && (Object)(object)prefab != (Object)null)
			{
				ConsumedItemMethod.Invoke(live, new object[1] { prefab });
				LogProductionDiagnostic(tameable, "Fed " + got.Item.m_shared.m_name + " to " + ((Object)live).name + " via tx.", warning: false);
			}
			else
			{
				ItemData back2 = got.Item;
				back2.m_stack = got.Accepted;
				ChestTxService.CompensateTakeBackItem(container, got.PrefabHash, back2);
			}
		}, 0L, animalPos);
	}

	private static void TrackContainer(Container container)
	{
		if (!((Object)(object)container == (Object)null))
		{
			if (LoadedContainers.Add(container))
			{
				_containerSnapshot = null;
			}
			(((Component)container).GetComponent<AutoFeedContainerTracker>() ?? ((Component)container).gameObject.AddComponent<AutoFeedContainerTracker>()).Container = container;
		}
	}

	internal static void ForgetContainer(Container container)
	{
		if (LoadedContainers.Remove(container))
		{
			_containerSnapshot = null;
		}
	}

	internal static void ForgetAnimal(Tameable animal)
	{
		ScanCooldowns.Remove(animal);
		ScanQueue.Remove(animal);
		if (_scanningAnimal == animal)
		{
			ClearScan();
		}
	}

	private static void ClearScan()
	{
		_scanningAnimal = null;
		_scanContainers = null;
		_scanFood = null;
		_scanIndex = 0;
		_nearestFood = null;
	}

	private static void EnsureSession()
	{
		if (_sessionNet != ZNet.instance || _sessionScene != ZNetScene.instance)
		{
			ResetState(returnAnimals: false);
			_sessionNet = ZNet.instance;
			_sessionScene = ZNetScene.instance;
		}
	}

	internal static void Update()
	{
		if (!Plugin.IsActive)
		{
			return;
		}
		EnsureSession();
		UpdateScans();
		// Expire lost feed flights (responses always clear explicitly).
		float realtimeSinceStartup = Time.realtimeSinceStartup;
		ZDOID[] stale = FeedingAnimals.Where((KeyValuePair<ZDOID, float> kv) => realtimeSinceStartup >= kv.Value).Select((KeyValuePair<ZDOID, float> kv) => kv.Key).ToArray();
		for (int i = 0; i < stale.Length; i++)
			FeedingAnimals.Remove(stale[i]);
	}

	internal static bool IsLocked(Container container)
	{
		// Legacy leases from older builds self-expire (timestamped); nothing sets new ones.
		ZNetView? netView = GetNetView(container);
		ZDO val = ((netView != null) ? netView.GetZDO() : null);
		if (val != null && (Object)(object)ZNet.instance != (Object)null)
			return val.GetLong(LeaseKey, 0L) > ZNet.instance.GetTime().Ticks;
		return false;
	}

	private static bool IsEligible(Container container, Vector3 animalPosition, long actor, bool ownLease = false)
	{
		if (!((Object)(object)container == (Object)null) && ((Behaviour)container).isActiveAndEnabled && (ownLease || !IsLocked(container)))
		{
			Vector3 val = ((Component)container).transform.position - animalPosition;
			if (!((val).sqrMagnitude > ModConfig.AutoFeedRange.Value * ModConfig.AutoFeedRange.Value) && !((Object)(object)((Component)container).GetComponent<Player>() != (Object)null) && !((Object)(object)((Component)container).GetComponent<TombStone>() != (Object)null))
			{
				if (!ModConfig.IncludeVehicleContainers.Value && ((Object)(object)container.m_wagon != (Object)null || (Object)(object)((Component)container).GetComponentInParent<Ship>() != (Object)null))
				{
					return false;
				}
				Piece val2 = ((Component)container).GetComponent<Piece>() ?? ((Component)container).GetComponentInParent<Piece>();
				if (!ModConfig.IncludeWorldContainers.Value && ((Object)(object)val2 == (Object)null || !val2.IsPlacedByPlayer()))
				{
					return false;
				}
				ZNetView netView = GetNetView(container);
				if ((Object)(object)netView == (Object)null || !netView.IsValid() || !AutoFeedPolicy.MatchesContainerPrefix(GetPrefabNameMethod.Invoke(netView, Array.Empty<object>()) as string, ModConfig.AutoFeedContainerPrefix.Value))
				{
					return false;
				}
				if (TxReflect.HasAccess(container, actor))
				{
					return ChestAuthority.WardAccess(container, actor);
				}
				return false;
			}
		}
		return false;
	}

	private static long Creator(Container container)
	{
		Piece obj = ((Component)container).GetComponent<Piece>() ?? ((Component)container).GetComponentInParent<Piece>();
		if (obj == null)
		{
			return 0L;
		}
		return obj.GetCreator();
	}

	private static Dictionary<string, ItemDrop> AcceptedFood(MonsterAI monsterAi)
	{
		Dictionary<string, ItemDrop> dictionary = new Dictionary<string, ItemDrop>(StringComparer.Ordinal);
		foreach (ItemDrop item in monsterAi.m_consumeItems ?? new List<ItemDrop>())
		{
			string text = item?.m_itemData?.m_shared?.m_name;
			if ((Object)(object)item != (Object)null && !string.IsNullOrEmpty(text) && !dictionary.ContainsKey(text))
			{
				dictionary.Add(text, item);
			}
		}
		return dictionary;
	}

	private static ZNetView? GetNetView(Container container)
	{
		if (!((Object)(object)container == (Object)null))
		{
			object value = ContainerNetViewField.GetValue(container);
			return (ZNetView?)((value is ZNetView) ? value : null);
		}
		return null;
	}

	internal static void Reset()
	{
		ResetState(returnAnimals: true);
		_sessionNet = null;
		_sessionScene = null;
	}

	private static void ResetState(bool returnAnimals)
	{
		FeedingAnimals.Clear();
		ScanCooldowns = new ConditionalWeakTable<Tameable, ScanCooldown>();
		ScanQueue.Clear();
		LoadedContainers.Clear();
		_containerSnapshot = null;
		_discoverySeeded = false;
		ClearScan();
	}
}
