using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Runtime;

/// <summary>
/// Deferred placement: the click stages missing mats from shared chests first
/// (nothing is pulled while browsing), then the piece is placed at the stored
/// aim transform once staged. Cancellation on selection change / timeout.
/// </summary>
internal static class NearbyPlaceIntent
{
	private static readonly FieldInfo PlacementGhostField = AccessTools.Field(typeof(Player), "m_placementGhost");

	private static Piece? _piece;

	private static Vector3 _pos;

	private static Quaternion _rot;

	private static float _deadline;

	internal static void Store(Player player, Piece piece)
	{
		try
		{
			GameObject ghost = (PlacementGhostField != null) ? (PlacementGhostField.GetValue(player) as GameObject) : null;
			if ((Object)(object)ghost == (Object)null)
				return;
			_piece = piece;
			_pos = ghost.transform.position;
			_rot = ghost.transform.rotation;
			_deadline = Time.realtimeSinceStartup + 4f;
		}
		catch
		{
		}
	}

	internal static bool HasIntentFor(Piece piece)
	{
		return (Object)(object)_piece != (Object)null && (Object)(object)_piece == (Object)(object)piece && Time.realtimeSinceStartup < _deadline;
	}

	private static Piece? _lastSelected;

	internal static void Pump()
	{
		Player player = Player.m_localPlayer;
		if ((Object)(object)player == (Object)null || player.IsTeleporting() || ((Humanoid)player).IsDead())
		{
			Clear();
			return;
		}
		// Selection tracking runs independent of any pending intent: the common
		// gate-pass pipeline (place → stage, no Store) would otherwise never
		// trigger the return when the piece changes.
		try
		{
			Piece cur = player.GetSelectedPiece();
			if ((Object)(object)cur != (Object)(object)_lastSelected)
			{
				_lastSelected = cur;
				NearbyResourceService.ReturnAheadStock(cur);
			}
		}
		catch
		{
		}
		if ((Object)(object)_piece == (Object)null)
			return;
		if (Time.realtimeSinceStartup >= _deadline)
		{
			Clear();
			TellPlayer("Could not stage building mats in time. Try again.");
			return;
		}
		Piece piece = _piece;
		try
		{
			if ((Object)(object)player.GetSelectedPiece() != (Object)(object)piece)
			{
				Clear();
				return;
			}
		}
		catch
		{
			Clear();
			return;
		}
		if (!NearbyResourceService.HasStagedMatsForPiece(player, piece))
		{
			// TEMP-DIAG(build-stall): remove after diagnosis.
			NearbyResourceService.LogStagedSplit(player, piece);
			return;
		}
		Clear();
		player.PlacePiece(piece, _pos, _rot, doAttack: true);
		bool free = false;
		try
		{
			free = player.NoCostCheat() || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()));
		}
		catch
		{
		}
		if (!free)
		{
			// Chest-aware consumption (same as the upgrade path): the gate above
			// counts player + owned chests, but vanilla ConsumeResources only sees
			// the player inventory (free/discount build otherwise).
			NearbyResourceService.ConsumeRequirements(player, piece.m_resources, 0, -1, 1);
			// Pipeline the next piece: the just-consumed stock must be re-staged
			// NOW, otherwise the next click inside the 2 s prefetch window submits
			// nothing (throttled) and its intent starves until the deadline.
			// Cooldown check is bypassed (the want is certain), the stamp is kept
			// so the next click does not duplicate the in-flight request.
			NearbyResourceService.StageMissingForPiece(player, piece, true);
		}
	}

	internal static void Clear()
	{
		_piece = null;
	}

	private static void TellPlayer(string message)
	{
		Player player = Player.m_localPlayer;
		if (player != null)
			((Character)player).Message((MessageType)2, message, 0, (Sprite)null, false);
	}
}
