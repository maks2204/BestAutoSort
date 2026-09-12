using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BestAutoSort.Core;
using BestAutoSort.Tx;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class AutoFeedService
{
	private sealed class ScanCooldown
	{
		internal float Next;
	}

	private sealed class ClientRequest
	{
		internal int Id;

		internal Container Container;

		internal ZDOID Animal;

		internal float Expires;

		internal float RetryAt;
	}

	private sealed class ServerRequest
	{
		internal string Key = "";

		internal int Id;

		internal long Sender;

		internal long Actor;

		internal Container Container;

		internal ZDOID Animal;

		internal long PreviousOwner;

		internal float Expires;

		internal bool Prepared;

		internal byte[]? Inventory;
	}

	internal const string TakeFoodRpc = "BestAutoSort_AutoFeedTake";

	internal const string TakeFoodResponseRpc = "BestAutoSort_AutoFeedTakeResponse";

	internal const string FinalizeFoodRpc = "BestAutoSort_AutoFeedFinalize";

	internal const string PrepareFoodRpc = "BestAutoSort_AutoFeedPrepare";

	internal const string PreparedFoodRpc = "BestAutoSort_AutoFeedPrepared";

	private const string ReceiptKey = "BestAutoSort.AutoFeedReceipt.v1";

	private const string LeaseKey = "BestAutoSort.AutoFeedLeaseUntil.v1";

	private const int MaximumInventoryBytes = 65536;

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

	private static readonly Dictionary<int, ClientRequest> ClientRequests = new Dictionary<int, ClientRequest>();

	private static readonly Dictionary<string, ServerRequest> ServerRequests = new Dictionary<string, ServerRequest>(StringComparer.Ordinal);

	private static readonly FeedRequestLedger Receipts = new FeedRequestLedger();

	private static readonly HashSet<ZDOID> BusyAnimals = new HashSet<ZDOID>();

	private static readonly HashSet<ZDOID> BlockedChests = new HashSet<ZDOID>();

	private static readonly Dictionary<long, Queue<float>> RequestRates = new Dictionary<long, Queue<float>>();

	private static int nextRequestId;

	internal static void Attach(Container container)
	{
		EnsureSession();
		TrackContainer(container);
		ZNetView view = GetNetView(container);
		if ((Object)(object)view == (Object)null || !view.IsValid())
		{
			return;
		}
		view.Register<ZPackage>("BestAutoSort_AutoFeedTake", (Action<long, ZPackage>)delegate(long sender, ZPackage package)
		{
			ReceiveRequest(container, sender, package);
		});
		view.Register<int, bool>("BestAutoSort_AutoFeedTakeResponse", (Action<long, int, bool>)delegate(long sender, int request, bool committed)
		{
			if (Plugin.IsActive && ChestAuthority.IsServerSender(sender) && ClientRequests.TryGetValue(request, out ClientRequest value) && !((Object)(object)value.Container != (Object)(object)container))
			{
				ClientRequests.Remove(request);
				view.InvokeRPC(sender, "BestAutoSort_AutoFeedFinalize", new object[2] { request, committed });
			}
		});
		view.Register<int, bool>("BestAutoSort_AutoFeedFinalize", (Action<long, int, bool>)delegate
		{
		});
		view.Register<ZPackage>("BestAutoSort_AutoFeedPrepare", (Action<long, ZPackage>)delegate(long sender, ZPackage package)
		{
			PrepareInventory(container, sender, package);
		});
		view.Register<ZPackage>("BestAutoSort_AutoFeedPrepared", (Action<long, ZPackage>)delegate(long sender, ZPackage package)
		{
			ReceivePrepared(container, sender, package);
		});
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
		if (!((Object)(object)animalView == (Object)null) && animalView.IsValid() && animalView.IsOwner() && !((Object)(object)val == (Object)null) && !((BaseAI)val).IsAlerted() && tameable.IsHungry())
		{
			float realtimeSinceStartup = Time.realtimeSinceStartup;
			ScanCooldown value3 = ScanCooldowns.GetValue(tameable, (Tameable _) => new ScanCooldown());
			if (AutoFeedPolicy.ShouldScan(realtimeSinceStartup, value3.Next) && !ClientRequests.Values.Any(delegate(ClientRequest request)
			{
				return request.Animal == animalView.GetZDO().m_uid;
			}) && _scanningAnimal != tameable && ScanQueue.Enqueue(tameable))
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

	private static void DispatchFeed(Tameable tameable, Container container)
	{
		if ((Object)(object)ZNet.instance == (Object)null || !TryGetHungryOwner(tameable, out ZNetView view, out MonsterAI ai))
		{
			return;
		}
		long num;
		if (!ZNet.instance.IsServer())
		{
			Player localPlayer = Player.m_localPlayer;
			num = ((localPlayer != null) ? localPlayer.GetPlayerID() : 0);
		}
		else
		{
			num = Creator(container);
		}
		long actor = num;
		if (!IsEligible(container, ((Component)tameable).transform.position, actor) || !HasFood(container, AcceptedFood(ai)))
		{
			return;
		}
		ZDOID animal = view.GetZDO().m_uid;
		if (ClientRequests.Values.Any(delegate(ClientRequest request)
		{
			return request.Animal == animal;
		}))
		{
			return;
		}
		int num2 = NextRequestId();
		if (ZNet.instance.IsServer())
		{
			BeginRequest(container, 0L, num2, animal);
			return;
		}
		ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
		if (serverPeer != null)
		{
			view.GetZDO().SetOwner(serverPeer.m_uid);
			ZDOMan.instance.ForceSendZDO(serverPeer.m_uid, animal);
			float realtimeSinceStartup = Time.realtimeSinceStartup;
			ClientRequest clientRequest = new ClientRequest
			{
				Id = num2,
				Container = container,
				Animal = animal,
				Expires = realtimeSinceStartup + 12f,
				RetryAt = realtimeSinceStartup + 1f
			};
			ClientRequests[num2] = clientRequest;
			SendRequest(serverPeer.m_uid, clientRequest);
		}
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
		float realtimeSinceStartup = Time.realtimeSinceStartup;
		ClientRequest[] array = ClientRequests.Values.ToArray();
		foreach (ClientRequest clientRequest in array)
		{
			if ((Object)(object)clientRequest.Container == (Object)null || realtimeSinceStartup >= clientRequest.Expires)
			{
				ClientRequests.Remove(clientRequest.Id);
			}
			else if (!(realtimeSinceStartup < clientRequest.RetryAt))
			{
				clientRequest.RetryAt = realtimeSinceStartup + 1f;
				ZNet instance = ZNet.instance;
				ZNetPeer val = ((instance != null) ? instance.GetServerPeer() : null);
				if (val != null)
				{
					SendRequest(val.m_uid, clientRequest);
				}
			}
		}
		if ((Object)(object)ZNet.instance == (Object)null || !ZNet.instance.IsServer())
		{
			return;
		}
		ServerRequest[] array2 = ServerRequests.Values.ToArray();
		foreach (ServerRequest serverRequest in array2)
		{
			if ((Object)(object)serverRequest.Container == (Object)null || realtimeSinceStartup >= serverRequest.Expires)
			{
				Finish(serverRequest, committed: false);
				continue;
			}
			ZNetScene instance2 = ZNetScene.instance;
			GameObject obj = ((instance2 != null) ? instance2.FindInstance(serverRequest.Animal) : null);
			Tameable val2 = ((obj != null) ? obj.GetComponent<Tameable>() : null);
			object obj2;
			if (!((Object)(object)val2 == (Object)null))
			{
				object value = TameableNetViewField.GetValue(val2);
				obj2 = ((value is ZNetView) ? value : null);
			}
			else
			{
				obj2 = null;
			}
			ZNetView val3 = (ZNetView)obj2;
			ZNetView netView = GetNetView(serverRequest.Container);
			if ((Object)(object)val3 == (Object)null || !val3.IsValid() || !val3.IsOwner() || (Object)(object)netView == (Object)null || !netView.IsOwner() || !serverRequest.Prepared)
			{
				continue;
			}
			bool committed = false;
			try
			{
				if (serverRequest.Inventory != null)
				{
					Inventory inventory = serverRequest.Container.GetInventory();
					Inventory val4 = new Inventory("BestAutoSort feed validation", inventory.GetBkg(), inventory.GetWidth(), inventory.GetHeight());
					val4.Load(new ZPackage(serverRequest.Inventory));
					inventory.GetAllItems().Clear();
					inventory.GetAllItems().AddRange(val4.GetAllItems());
					InventoryChangedMethod.Invoke(inventory, new object[2] { false, false });
					serverRequest.Inventory = null;
					TxReflect.SaveContainer(serverRequest.Container);
				}
				else
				{
					TxReflect.LoadContainer(serverRequest.Container);
				}
				long num = ((serverRequest.Sender == 0L) ? Creator(serverRequest.Container) : serverRequest.Actor);
				if (serverRequest.Sender != 0L)
				{
					if (ChestAuthority.ResolveActor(serverRequest.Sender, out var playerId, out var position) && playerId == num)
					{
						Vector3 val5 = position - ((Component)val2).transform.position;
						if (!((val5).sqrMagnitude > 16384f))
						{
							goto IL_02bf;
						}
					}
					Finish(serverRequest, committed: false);
					continue;
				}
				goto IL_02bf;
				IL_02bf:
				if (IsEligible(serverRequest.Container, ((Component)val2).transform.position, num, ownLease: true))
				{
					committed = CompleteOperation(serverRequest, val2);
				}
			}
			catch (Exception ex)
			{
				Plugin.LogInstance.LogError((object)("Server Auto Feed operation failed: " + ex));
			}
			Finish(serverRequest, committed);
		}
		long[] array3 = RequestRates.Keys.Where((long peer) => ZNet.instance.GetPeer(peer) == null).ToArray();
		foreach (long key in array3)
		{
			RequestRates.Remove(key);
		}
	}

	private static void SendRequest(long server, ClientRequest request)
	{
		ZPackage val = new ZPackage();
		val.Write(request.Id);
		val.Write(request.Animal);
		ZNetView? netView = GetNetView(request.Container);
		if (netView != null)
		{
			netView.InvokeRPC(server, "BestAutoSort_AutoFeedTake", new object[1] { val });
		}
	}

	private static void ReceiveRequest(Container container, long sender, ZPackage package)
	{
		if (!Plugin.IsActive || (Object)(object)ZNet.instance == (Object)null || !ZNet.instance.IsServer() || package == null || package.Size() > 64 || !ChestAuthority.ResolveActor(sender, out var _, out var _))
		{
			return;
		}
		if (!RequestRates.TryGetValue(sender, out Queue<float> value))
		{
			RequestRates.Add(sender, value = new Queue<float>());
		}
		float realtimeSinceStartup = Time.realtimeSinceStartup;
		while (value.Count > 0 && realtimeSinceStartup - value.Peek() >= 1f)
		{
			value.Dequeue();
		}
		if (value.Count >= 20)
		{
			return;
		}
		value.Enqueue(realtimeSinceStartup);
		try
		{
			int num = package.ReadInt();
			ZDOID animal = package.ReadZDOID();
			if (num > 0 && package.GetPos() == package.Size())
			{
				BeginRequest(container, sender, num, animal);
			}
		}
		catch (Exception ex)
		{
			Plugin.LogInstance.LogWarning((object)("Rejected Auto Feed request: " + ex.Message));
		}
	}

	private unsafe static void BeginRequest(Container container, long sender, int id, ZDOID animal)
	{
		ZNetView netView = GetNetView(container);
		if (!ModConfig.Enabled.Value || !ModConfig.AutoFeedEnabled.Value || (Object)(object)netView == (Object)null || !netView.IsValid() || BlockedChests.Contains(netView.GetZDO().m_uid))
		{
			return;
		}
		string text = sender + ":" + id;
		ZDOID uid = netView.GetZDO().m_uid;
		string text2 = ((object)(*(ZDOID*)(&uid))/*cast due to constrained. prefix*/).ToString();
		uid = animal;
		string context = text2 + "/" + ((object)(*(ZDOID*)(&uid))/*cast due to constrained. prefix*/).ToString();
		FeedRequestState feedRequestState = Receipts.Begin(text, context, Time.realtimeSinceStartup);
		if (feedRequestState != FeedRequestState.New)
		{
			if (sender != 0L && feedRequestState != FeedRequestState.Pending)
			{
				netView.InvokeRPC(sender, "BestAutoSort_AutoFeedTakeResponse", new object[2]
				{
					id,
					feedRequestState == FeedRequestState.Committed
				});
			}
			return;
		}
		long playerId = Creator(container);
		if (sender != 0L && !ChestAuthority.ResolveActor(sender, out playerId, out var _))
		{
			Receipts.Complete(text, committed: false);
			return;
		}
		ZNetScene instance = ZNetScene.instance;
		GameObject obj = ((instance != null) ? instance.FindInstance(animal) : null);
		Tameable val = ((obj != null) ? obj.GetComponent<Tameable>() : null);
		if ((Object)(object)val == (Object)null || !IsEligible(container, ((Component)val).transform.position, playerId) || ServerRequests.Count >= 64 || BusyAnimals.Contains(animal))
		{
			Receipts.Complete(text, committed: false);
			if (sender != 0L)
			{
				netView.InvokeRPC(sender, "BestAutoSort_AutoFeedTakeResponse", new object[2] { id, false });
			}
			ReturnAnimal(animal, sender);
			return;
		}
		string text3 = netView.GetZDO().GetString("BestAutoSort.AutoFeedReceipt.v1", "");
		uid = animal;
		if (text3 == text + "/" + ((object)(*(ZDOID*)(&uid))/*cast due to constrained. prefix*/).ToString())
		{
			Receipts.Complete(text, committed: true);
			if (sender != 0L)
			{
				netView.InvokeRPC(sender, "BestAutoSort_AutoFeedTakeResponse", new object[2] { id, true });
			}
			ReturnAnimal(animal, sender);
			return;
		}
		ServerRequest serverRequest = new ServerRequest
		{
			Key = text,
			Id = id,
			Sender = sender,
			Actor = playerId,
			Container = container,
			Animal = animal,
			PreviousOwner = netView.GetZDO().GetOwner(),
			Expires = Time.realtimeSinceStartup + 8f,
			Prepared = netView.IsOwner()
		};
		ServerRequests.Add(text, serverRequest);
		BusyAnimals.Add(animal);
		if (serverRequest.Prepared)
		{
			SetLease(netView);
		}
		else if (serverRequest.PreviousOwner == 0L)
		{
			netView.ClaimOwnership();
			serverRequest.Prepared = netView.IsOwner();
			if (serverRequest.Prepared)
			{
				SetLease(netView);
			}
		}
		else
		{
			ZPackage val2 = new ZPackage();
			val2.Write(text);
			netView.InvokeRPC(serverRequest.PreviousOwner, "BestAutoSort_AutoFeedPrepare", new object[1] { val2 });
		}
	}

	private static void PrepareInventory(Container container, long sender, ZPackage package)
	{
		if (!Plugin.IsActive)
		{
			return;
		}
		ZNetView netView = GetNetView(container);
		if (!ChestAuthority.IsServerSender(sender) || (Object)(object)netView == (Object)null || !netView.IsOwner() || package == null || package.Size() > 128)
		{
			return;
		}
		try
		{
			string text = package.ReadString();
			if (text.Length <= 96 && package.GetPos() == package.Size())
			{
				TxReflect.SaveContainer(container);
				ZPackage val = new ZPackage();
				container.GetInventory().Save(val);
				byte[] array = val.GetArray();
				if (array.Length <= 65536)
				{
					ZPackage val2 = new ZPackage();
					val2.Write(text);
					val2.Write(array);
					SetLease(netView);
					netView.GetZDO().SetOwner(sender);
					ZDOMan.instance.ForceSendZDO(sender, netView.GetZDO().m_uid);
					netView.InvokeRPC(sender, "BestAutoSort_AutoFeedPrepared", new object[1] { val2 });
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.LogInstance.LogWarning((object)("Could not prepare Auto Feed chest: " + ex.Message));
		}
	}

	private static void ReceivePrepared(Container container, long sender, ZPackage package)
	{
		if (!Plugin.IsActive || (Object)(object)ZNet.instance == (Object)null || !ZNet.instance.IsServer() || package == null || package.Size() > 65792)
		{
			return;
		}
		try
		{
			string text = package.ReadString();
			byte[] array = package.ReadByteArray();
			if (text.Length <= 96 && array.Length <= 65536 && package.GetPos() == package.Size() && ServerRequests.TryGetValue(text, out ServerRequest value) && !((Object)(object)value.Container != (Object)(object)container) && value.PreviousOwner == sender && !value.Prepared)
			{
				value.Inventory = array;
				value.Prepared = true;
			}
		}
		catch (Exception ex)
		{
			Plugin.LogInstance.LogWarning((object)("Rejected Auto Feed chest preparation: " + ex.Message));
		}
	}

	private unsafe static bool CompleteOperation(ServerRequest request, Tameable tameable)
	{
		object value = MonsterAiField.GetValue(tameable);
		MonsterAI val = (MonsterAI)((value is MonsterAI) ? value : null);
		if ((Object)(object)val == (Object)null || ((BaseAI)val).IsAlerted() || !tameable.IsHungry())
		{
			return false;
		}
		Dictionary<string, ItemDrop> accepted = AcceptedFood(val);
		Inventory inventory = request.Container.GetInventory();
		ItemData item = inventory.GetAllItems().FirstOrDefault((ItemData candidate) => !candidate.m_shared.m_questItem && accepted.ContainsKey(candidate.m_shared.m_name) && ChestReserveStore.Available(request.Container, candidate) > 0);
		if (item == null)
		{
			return false;
		}
		ItemData[] originals = inventory.GetAllItems().ToArray();
		int[] stacks = originals.Select((ItemData candidate) => candidate.m_stack).ToArray();
		Vector2i[] positions = originals.Select(delegate(ItemData candidate)
		{
			return candidate.m_gridPos;
		}).ToArray();
		ZDO animal = ((ZNetView)TameableNetViewField.GetValue(tameable)).GetZDO();
		long before = animal.GetLong(ZDOVars.s_tameLastFeeding, 0L);
		ZDO chest = GetNetView(request.Container).GetZDO();
		string oldReceipt = chest.GetString("BestAutoSort.AutoFeedReceipt.v1", "");
		try
		{
			return FeedTransaction.Execute(() => inventory.RemoveItem(item, 1), delegate
			{
				ConsumedItemMethod.Invoke(tameable, new object[1] { accepted[item.m_shared.m_name] });
			}, () => !tameable.IsHungry(), delegate
			{
				TxReflect.SaveContainer(request.Container);
				ZDO obj = chest;
				string key = request.Key;
				ZDOID animal2 = request.Animal;
				obj.Set("BestAutoSort.AutoFeedReceipt.v1", key + "/" + ((object)(*(ZDOID*)(&animal2))/*cast due to constrained. prefix*/).ToString());
			}, delegate
			{
				inventory.GetAllItems().Clear();
				inventory.GetAllItems().AddRange(originals);
				for (int i = 0; i < originals.Length; i++)
				{
					originals[i].m_stack = stacks[i];
					originals[i].m_gridPos = positions[i];
				}
				animal.Set(ZDOVars.s_tameLastFeeding, before);
				chest.Set("BestAutoSort.AutoFeedReceipt.v1", oldReceipt);
				InventoryChangedMethod.Invoke(inventory, new object[2] { false, false });
				TxReflect.SaveContainer(request.Container);
			});
		}
		catch (AggregateException ex)
		{
			BlockedChests.Add(chest.m_uid);
			Plugin.LogInstance.LogError((object)("Auto Feed could not finish rollback persistence. This chest is paused; preserve the world and logs before recovery: " + ex));
			return false;
		}
	}

	private static void Finish(ServerRequest request, bool committed)
	{
		ServerRequests.Remove(request.Key);
		BusyAnimals.Remove(request.Animal);
		Receipts.Complete(request.Key, committed);
		ZNetView netView = GetNetView(request.Container);
		if ((Object)(object)netView != (Object)null && netView.IsOwner())
		{
			netView.GetZDO().Set("BestAutoSort.AutoFeedLeaseUntil.v1", 0L);
		}
		if (request.Sender != 0L && (Object)(object)request.Container != (Object)null)
		{
			ZNetView? netView2 = GetNetView(request.Container);
			if (netView2 != null)
			{
				netView2.InvokeRPC(request.Sender, "BestAutoSort_AutoFeedTakeResponse", new object[2] { request.Id, committed });
			}
		}
		ReturnAnimal(request.Animal, request.Sender);
	}

	private static void ReturnAnimal(ZDOID animal, long sender)
	{
		if (sender == 0L)
		{
			return;
		}
		ZNet instance = ZNet.instance;
		if (((instance != null) ? instance.GetPeer(sender) : null) != null)
		{
			ZNetScene instance2 = ZNetScene.instance;
			object obj;
			if (instance2 == null)
			{
				obj = null;
			}
			else
			{
				GameObject obj2 = instance2.FindInstance(animal);
				obj = ((obj2 != null) ? obj2.GetComponent<ZNetView>() : null);
			}
			ZNetView val = (ZNetView)obj;
			if (!((Object)(object)val == (Object)null) && val.IsValid() && val.IsOwner())
			{
				val.GetZDO().SetOwner(sender);
				ZDOMan.instance.ForceSendZDO(sender, animal);
			}
		}
	}

	internal static bool IsLocked(Container container)
	{
		ZNetView? netView = GetNetView(container);
		ZDO val = ((netView != null) ? netView.GetZDO() : null);
		if (val != null)
		{
			if (!BlockedChests.Contains(val.m_uid))
			{
				if ((Object)(object)ZNet.instance != (Object)null)
				{
					return val.GetLong("BestAutoSort.AutoFeedLeaseUntil.v1", 0L) > ZNet.instance.GetTime().Ticks;
				}
				return false;
			}
			return true;
		}
		return false;
	}

	private static void SetLease(ZNetView view)
	{
		view.GetZDO().Set("BestAutoSort.AutoFeedLeaseUntil.v1", ZNet.instance.GetTime().AddSeconds(12.0).Ticks);
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

	private static int NextRequestId()
	{
		nextRequestId = ((nextRequestId == int.MaxValue) ? 1 : (nextRequestId + 1));
		return nextRequestId;
	}

	internal static void Reset()
	{
		ResetState(returnAnimals: true);
		_sessionNet = null;
		_sessionScene = null;
	}

	private static void ResetState(bool returnAnimals)
	{
		if (returnAnimals)
		{
			ServerRequest[] array = ServerRequests.Values.ToArray();
			foreach (ServerRequest serverRequest in array)
			{
				ReturnAnimal(serverRequest.Animal, serverRequest.Sender);
			}
		}
		ServerRequests.Clear();
		ClientRequests.Clear();
		BusyAnimals.Clear();
		BlockedChests.Clear();
		Receipts.Clear();
		RequestRates.Clear();
		ScanCooldowns = new ConditionalWeakTable<Tameable, ScanCooldown>();
		ScanQueue.Clear();
		LoadedContainers.Clear();
		_containerSnapshot = null;
		_discoverySeeded = false;
		ClearScan();
	}
}
