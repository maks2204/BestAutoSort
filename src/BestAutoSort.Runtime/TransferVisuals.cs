using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace BestAutoSort.Runtime;

internal static class TransferVisuals
{
	private sealed class FlightGroup
	{
		internal Sprite? Icon;

		internal int Amount;

		internal int MaxStackSize;
	}

	private static readonly Dictionary<int, int> DestinationAnimationTokens = new Dictionary<int, int>();

	private static int _nextDestinationAnimationToken;

	internal static void Play(IReadOnlyCollection<TransferRecord> records, Container container)
	{
		Play(records, container, toPlayer: false);
	}

	internal static void PlayToPlayer(IReadOnlyCollection<TransferRecord> records, Container container)
	{
		Play(records, container, toPlayer: true);
	}

	private static void Play(IReadOnlyCollection<TransferRecord> records, Container container, bool toPlayer)
	{
		PlayFrom(records, container, toPlayer, null);
	}

	/// <summary>
	/// Flights with an explicit remote endpoint (for remote viewers).
	/// toPlayer=false: remotePos is the start (sorter), end is the chest.
	/// toPlayer=true: start is the chest, remotePos is the end (taker).
	/// remotePos == null falls back to the local player (previous behavior).
	/// </summary>
	internal static void PlayFrom(IReadOnlyCollection<TransferRecord> records, Container container, bool toPlayer, Vector3? remotePos)
	{
		if (!ModConfig.ShowTransferFlights.Value || records == null || records.Count == 0 || (Object)(object)container == (Object)null)
		{
			return;
		}
		Dictionary<string, FlightGroup> dictionary = new Dictionary<string, FlightGroup>();
		foreach (TransferRecord record in records)
		{
			if (!dictionary.TryGetValue(record.ItemName, out var value))
			{
				value = new FlightGroup
				{
					Icon = record.Icon,
					MaxStackSize = record.MaxStackSize
				};
				dictionary.Add(record.ItemName, value);
			}
			value.Amount += record.Amount;
		}
		int num = 0;
		float num2 = 0f;
		bool flag = false;
		foreach (FlightGroup value2 in dictionary.Values)
		{
			if (!((Object)(object)value2.Icon == (Object)null))
			{
				int num3 = Mathf.Max(1, Mathf.CeilToInt((float)value2.Amount / (float)Mathf.Max(1, value2.MaxStackSize)));
				int num4 = Mathf.Min(ModConfig.MaxFlightsPerItem.Value, num3);
				for (int i = 0; i < num4; i++)
				{
					float num5 = (float)num * 0.035f + (float)i * 0.055f;
					num2 = Mathf.Max(num2, num5);
					flag = true;
					((MonoBehaviour)Plugin.Instance).StartCoroutine(Fly(value2.Icon, container, num5, toPlayer, remotePos));
				}
				num++;
			}
		}
		if (flag)
		{
			StartDestinationAnimation(container, num2 + ModConfig.FlightDuration.Value + 0.2f);
		}
	}

	private static void StartDestinationAnimation(Container container, float duration)
	{
		if (!((Object)(object)container == (Object)null) && !container.IsInUse())
		{
			int instanceID = ((Object)container).GetInstanceID();
			int num = ++_nextDestinationAnimationToken;
			DestinationAnimationTokens[instanceID] = num;
			((MonoBehaviour)Plugin.Instance).StartCoroutine(AnimateDestination(container, instanceID, num, duration));
		}
	}

	private static IEnumerator AnimateDestination(Container container, int id, int token, float duration)
	{
		SetDestinationVisual(container, open: true);
		yield return (object)new WaitForSeconds(Mathf.Max(0.2f, duration));
		if (DestinationAnimationTokens.TryGetValue(id, out var value) && value == token)
		{
			DestinationAnimationTokens.Remove(id);
			if ((Object)(object)container != (Object)null && !container.IsInUse())
			{
				SetDestinationVisual(container, open: false);
			}
		}
	}

	private static void SetDestinationVisual(Container container, bool open)
	{
		if ((Object)(object)container.m_open != (Object)null)
		{
			container.m_open.SetActive(open);
		}
		if ((Object)(object)container.m_closed != (Object)null)
		{
			container.m_closed.SetActive(!open);
		}
		EffectList obj = (open ? container.m_openEffects : container.m_closeEffects);
		if (obj != null)
		{
			obj.Create(((Component)container).transform.position, ((Component)container).transform.rotation, (Transform)null, 1f, -1, default(ZDOID));
		}
	}

	private static IEnumerator Fly(Sprite icon, Container container, float delay, bool toPlayer, Vector3? remotePos = null)
	{
		if (delay > 0f)
		{
			yield return (object)new WaitForSeconds(delay);
		}
		if ((Object)(object)container == (Object)null)
		{
			yield break;
		}
		Vector3 remoteOrLocal;
		Vector3 chestPos = ((Component)container).transform.position;
		Vector3 val;
		Vector3 val2;
		Vector3 start;
		Vector3 end;
		if (remotePos != null)
		{
			if (toPlayer)
			{
				start = chestPos + Vector3.up * 1.25f;
				end = remotePos.Value + Vector3.up * 1.25f;
			}
			else
			{
				start = remotePos.Value + Vector3.up * 1.25f;
				end = chestPos + Vector3.up * 0.8f;
			}
			val = start;
			val2 = end;
		}
		else
		{
			if ((Object)(object)Player.m_localPlayer == (Object)null)
			{
				yield break;
			}
			val = ((Component)Player.m_localPlayer).transform.position + Vector3.up * 1.25f;
			val2 = ((Component)container).transform.position + Vector3.up * 0.8f;
			start = (toPlayer ? val2 : val);
			end = (toPlayer ? val : val2);
		}
		Vector3 val3 = Vector3.Cross(Vector3.up, end - start);
		Vector3 sideways = (val3).normalized * Random.Range(-0.35f, 0.35f);
		GameObject flight = new GameObject("BestAutoSort_ItemFlight");
		SpriteRenderer renderer = flight.AddComponent<SpriteRenderer>();
		renderer.sprite = icon;
		((Renderer)renderer).sortingOrder = 32767;
		Bounds bounds = icon.bounds;
		float x = (bounds).size.x;
		bounds = icon.bounds;
		float num = Mathf.Max(x, (bounds).size.y);
		float num2 = 0.42f;
		float num3 = ((num > 0f) ? (num2 / num) : num2);
		flight.transform.localScale = Vector3.one * num3;
		float duration = ModConfig.FlightDuration.Value;
		float elapsed = 0f;
		Color baseColor = renderer.color;
		try
		{
			while (elapsed < duration && (Object)(object)container != (Object)null)
			{
				elapsed += Time.deltaTime;
				float num4 = Mathf.Clamp01(elapsed / duration);
				float num5 = num4 * num4 * (3f - 2f * num4);
				Vector3 val4 = Vector3.up * (Mathf.Sin((float)Math.PI * num5) * ModConfig.FlightArcHeight.Value);
				flight.transform.position = Vector3.Lerp(start, end, num5) + val4 + sideways * Mathf.Sin((float)Math.PI * num5);
				Camera main = Camera.main;
				if ((Object)(object)main != (Object)null)
				{
					flight.transform.rotation = ((Component)main).transform.rotation;
				}
				if (num4 > 0.75f)
				{
					baseColor.a = 1f - (num4 - 0.75f) / 0.25f;
					renderer.color = baseColor;
				}
				yield return null;
			}
		}
		finally
		{
			Object.Destroy((Object)(object)flight);
		}
	}
}
