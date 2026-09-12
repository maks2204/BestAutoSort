using System;
using System.Collections.Generic;
using System.Reflection;
using BestAutoSort.Tx;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class ChestAuthority
{
	private static readonly FieldInfo AllAreas = AccessTools.Field(typeof(PrivateArea), "m_allAreas");

	private static readonly MethodInfo IsPermitted = AccessTools.Method(typeof(PrivateArea), "IsPermitted", (Type[])null, (Type[])null);

	private static readonly MethodInfo IsInside = AccessTools.Method(typeof(PrivateArea), "IsInside", (Type[])null, (Type[])null);

	private static readonly MethodInfo IsEnabled = AccessTools.Method(typeof(PrivateArea), "IsEnabled", (Type[])null, (Type[])null);

	internal static bool IsServerSender(long sender)
	{
		if ((Object)(object)ZNet.instance != (Object)null && !ZNet.instance.IsServer())
		{
			ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
			if (serverPeer == null)
			{
				return false;
			}
			return serverPeer.m_uid == sender;
		}
		return false;
	}

	internal static bool ResolveActor(long sender, out long playerId, out Vector3 position)
	{
		playerId = 0L;
		position = Vector3.zero;
		ZNet instance = ZNet.instance;
		if ((Object)(object)instance == (Object)null)
		{
			return false;
		}
		ZNetPeer peer = instance.GetPeer(sender);
		if (instance.IsServer() && peer == null)
		{
			return false;
		}
		if (peer != null && peer.m_characterID != ZDOID.None)
		{
			ZDOMan instance2 = ZDOMan.instance;
			ZDO val = ((instance2 != null) ? instance2.GetZDO(peer.m_characterID) : null);
			if (val != null && val.GetOwner() == sender)
			{
				ZNetScene instance3 = ZNetScene.instance;
				object obj;
				if (instance3 == null)
				{
					obj = null;
				}
				else
				{
					GameObject prefab = instance3.GetPrefab(val.GetPrefab());
					obj = ((prefab != null) ? prefab.GetComponent<Player>() : null);
				}
				if (!((Object)obj == (Object)null))
				{
					playerId = val.GetLong(ZDOVars.s_playerID, 0L);
					position = val.GetPosition();
					return playerId != 0;
				}
			}
			return false;
		}
		foreach (Player allPlayer in Player.GetAllPlayers())
		{
			ZNetView component = ((Component)allPlayer).GetComponent<ZNetView>();
			if (!((Object)(object)component == (Object)null) && component.IsValid() && component.GetZDO().GetOwner() == sender)
			{
				playerId = allPlayer.GetPlayerID();
				position = ((Component)allPlayer).transform.position;
				return playerId != 0;
			}
		}
		return false;
	}

	internal static bool CanUse(Container container, long sender, long claimedPlayerId)
	{
		if (!ResolveActor(sender, out var playerId, out var position) || playerId != claimedPlayerId)
		{
			return false;
		}
		float num = Mathf.Clamp(ModConfig.NearbyRange.Value, 4f, 100f);
		Vector3 val = ((Component)container).transform.position - position;
		if ((val).sqrMagnitude <= num * num && TxReflect.HasAccess(container, playerId))
		{
			return WardAccess(container, playerId);
		}
		return false;
	}

	internal static bool WardAccess(Container container, long playerId)
	{
		if (!container.m_checkGuardStone)
		{
			return true;
		}
		if (!(AllAreas.GetValue(null) is IEnumerable<PrivateArea> enumerable))
		{
			return false;
		}
		bool flag = false;
		bool flag2 = default(bool);
		foreach (PrivateArea item in enumerable)
		{
			if ((Object)(object)item == (Object)null)
			{
				continue;
			}
			object obj = IsEnabled.Invoke(item, Array.Empty<object>());
			if (!(obj is bool) || !(bool)obj)
			{
				continue;
			}
			obj = IsInside.Invoke(item, new object[2]
			{
				((Component)container).transform.position,
				0f
			});
			if (!(obj is bool) || !(bool)obj)
			{
				continue;
			}
			flag = true;
			Piece component = ((Component)item).GetComponent<Piece>();
			if (playerId == 0L)
			{
				continue;
			}
			if (component == null || component.GetCreator() != playerId)
			{
				obj = IsPermitted.Invoke(item, new object[1] { playerId });
				int num;
				if (obj is bool)
				{
					flag2 = (bool)obj;
					num = 1;
				}
				else
				{
					num = 0;
				}
				if (((uint)num & (flag2 ? 1u : 0u)) == 0)
				{
					continue;
				}
			}
			return true;
		}
		return !flag;
	}
}
