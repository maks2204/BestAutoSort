using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BestAutoSort.Core;

public static class StorageReserves
{
	public static int Available(int total, int reserve)
	{
		return Math.Max(0, total - Math.Max(0, reserve));
	}

	public static string Encode(IReadOnlyDictionary<string, int> values)
	{
		if (values.Count > 128)
		{
			throw new ArgumentException("At most 128 reserve entries are allowed.");
		}
		foreach (KeyValuePair<string, int> value in values)
		{
			if (string.IsNullOrWhiteSpace(value.Key) || value.Key.Length > 128 || value.Value < 0 || value.Value > 999999)
			{
				throw new ArgumentException("Invalid reserve entry.");
			}
		}
		return "1;" + string.Join(";", from pair in values.OrderBy<KeyValuePair<string, int>, string>((KeyValuePair<string, int> pair) => pair.Key, StringComparer.OrdinalIgnoreCase)
			select Uri.EscapeDataString(pair.Key) + "=" + pair.Value.ToString(CultureInfo.InvariantCulture));
	}

	public static Dictionary<string, int> Decode(string text)
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(text))
		{
			return dictionary;
		}
		if (text.Length > 65536 || !text.StartsWith("1;", StringComparison.Ordinal))
		{
			throw new FormatException("Unsupported reserve data.");
		}
		string[] array = text.Substring(2).Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string text2 in array)
		{
			int num = text2.IndexOf('=');
			if (num <= 0 || !int.TryParse(text2.Substring(num + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var result))
			{
				throw new FormatException("Invalid reserve quantity.");
			}
			string text3 = Uri.UnescapeDataString(text2.Substring(0, num));
			if (string.IsNullOrWhiteSpace(text3) || text3.Length > 128 || result < 0 || result > 999999 || dictionary.Count >= 128 || dictionary.ContainsKey(text3))
			{
				throw new FormatException("Invalid reserve entry.");
			}
			dictionary.Add(text3, result);
		}
		return dictionary;
	}
}
