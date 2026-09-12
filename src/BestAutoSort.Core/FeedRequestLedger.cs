using System;
using System.Collections.Generic;
using System.Linq;

namespace BestAutoSort.Core;

public sealed class FeedRequestLedger
{
	private sealed class Entry
	{
		internal string Context = "";

		internal double Expires;

		internal bool? Result;
	}

	private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

	public FeedRequestState Begin(string key, string context, double now)
	{
		if (string.IsNullOrEmpty(key) || key.Length > 96 || context.Length > 192 || double.IsNaN(now) || double.IsInfinity(now))
		{
			return FeedRequestState.Rejected;
		}
		string[] array = (from pair in entries
			where pair.Value.Expires <= now
			select pair.Key).ToArray();
		foreach (string key2 in array)
		{
			entries.Remove(key2);
		}
		if (entries.TryGetValue(key, out Entry value))
		{
			if (value.Context != context)
			{
				return FeedRequestState.Rejected;
			}
			if (!value.Result.HasValue)
			{
				return FeedRequestState.Pending;
			}
			if (!value.Result.Value)
			{
				return FeedRequestState.Rejected;
			}
			return FeedRequestState.Committed;
		}
		if (entries.Count >= 4096)
		{
			return FeedRequestState.Rejected;
		}
		entries.Add(key, new Entry
		{
			Context = context,
			Expires = now + 120.0
		});
		return FeedRequestState.New;
	}

	public void Complete(string key, bool committed)
	{
		if (entries.TryGetValue(key, out Entry value))
		{
			value.Result = committed;
		}
	}

	public void Clear()
	{
		entries.Clear();
	}
}
