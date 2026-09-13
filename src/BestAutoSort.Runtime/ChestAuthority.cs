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

	/// <summary>
	/// Access gate must cover every scanner that submits txs (quickstack,
	/// shared-resource prefetch, autofeed), otherwise found mats get rejected.
	/// </summary>
	internal static float EffectiveAccessRange()
	{
		float range = ModConfig.NearbyRange.Value;
		if (ModConfig.SharedResourceRange.Value > range)
			range = ModConfig.SharedResourceRange.Value;
		if (ModConfig.AutoFeedRange.Value > range)
			range = ModConfig.AutoFeedRange.Value;
		return Mathf.Clamp(range, 4f, 100f);
	}

	internal static long CreatorOf(Container container)
	{
		try
		{
			Piece piece = ((Component)container).GetComponent<Piece>() ?? ((Component)container).GetComponentInParent<Piece>();
			if ((Object)(object)piece != (Object)null)
				return piece.GetCreator();
		}
		catch
		{
		}
		return 0L;
	}

	internal static bool IsServerSenderOrSelf(long sender)
	{
		try
		{
			if (sender == ZNet.GetUID() && (Object)(object)ZNet.instance != (Object)null && ZNet.instance.IsServer())
				return true;
		}
		catch
		{
		}
		return IsServerSender(sender);
	}

	internal static bool CanUse(Container container, long sender, long claimedPlayerId, Vector3 claimedPos, out string why)
	{
		// The range check uses the position stamped by the client: the manager's
		// replicated copy of a remote player lags behind, which wrongly rejected
		// walk-up-and-click deposits (items bounced back to the inventory).
		// Spoofing is still contained: the playerId must match the ZDO-owned
		// character, plus vanilla CheckAccess and the ward check apply.
		why = "ok";
		if (sender != 0L && IsServerSenderOrSelf(sender))
		{
			// Headless/dedicated server feeding: no player body, so no actor to resolve.
			// The claim must be the chest creator (same rule the old feeder used);
			// distance is checked against the stamped animal position.
			long creator = CreatorOf(container);
			if (creator == 0L || claimedPlayerId != creator)
			{
				why = "player-mismatch";
				return false;
			}
			float range = EffectiveAccessRange();
			Vector3 toChest = ((Component)container).transform.position - claimedPos;
			if ((toChest).sqrMagnitude > range * range)
			{
				why = "too-far";
				return false;
			}
			if (!TxReflect.HasAccess(container, creator))
			{
				why = "vanilla-access";
				return false;
			}
			if (!WardAccess(container, creator))
			{
				why = "ward";
				return false;
			}
			return true;
		}
		if (!ResolveActor(sender, out var playerId, out var _))
		{
			why = "no-actor";
			return false;
		}
		if (playerId != claimedPlayerId)
		{
			why = "player-mismatch";
			return false;
		}
		float num = EffectiveAccessRange();
		Vector3 val = ((Component)container).transform.position - claimedPos;
		if ((val).sqrMagnitude > num * num)
		{
			why = "too-far";
			return false;
		}
		if (!TxReflect.HasAccess(container, playerId))
		{
			why = "vanilla-access";
			return false;
		}
		if (!WardAccess(container, playerId))
		{
			why = "ward";
			return false;
		}
		return true;
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
