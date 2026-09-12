using System;
using System.Collections.Generic;

namespace BestAutoSort.Core;

public static class SignTextColorMarkup
{
	private const string ColorSuffix = "</color>";

	private const string White = "#FFFFFF";

	private static readonly IReadOnlyDictionary<string, string> NamedColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["black"] = "#000000",
		["blue"] = "#0000FF",
		["brown"] = "#A52A2A",
		["cyan"] = "#00FFFF",
		["gold"] = "#FFD700",
		["gray"] = "#808080",
		["green"] = "#008000",
		["grey"] = "#808080",
		["lime"] = "#00FF00",
		["magenta"] = "#FF00FF",
		["orange"] = "#FFA500",
		["pink"] = "#FFC0CB",
		["purple"] = "#800080",
		["red"] = "#FF0000",
		["silver"] = "#C0C0C0",
		["teal"] = "#008080",
		["white"] = "#FFFFFF",
		["yellow"] = "#FFFF00"
	};

	public static string ApplyDefault(string text, string configuredColor, out string automaticColor)
	{
		automaticColor = string.Empty;
		if (string.IsNullOrEmpty(text) || text.IndexOf("<color=", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return text;
		}
		string text2 = NormalizeOrWhite(configuredColor);
		if (text2.Length == 0)
		{
			return text;
		}
		automaticColor = text2;
		return "<color=" + text2 + ">" + text + "</color>";
	}

	public static string RemoveAutomatic(string text, string automaticColor)
	{
		if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(automaticColor))
		{
			return text;
		}
		string text2 = NormalizeOrWhite(automaticColor);
		string text3 = "<color=" + text2 + ">";
		if (!text.StartsWith(text3, StringComparison.OrdinalIgnoreCase))
		{
			return text;
		}
		int num = text.Length - text3.Length;
		if (text.EndsWith("</color>", StringComparison.OrdinalIgnoreCase))
		{
			num -= "</color>".Length;
		}
		return text.Substring(text3.Length, Math.Max(0, num));
	}

	public static string RemoveLegacyYellow(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		string[] array = new string[3] { "<color=yellow>", "<color=#FFFF00>", "<color=#FFFF00FF>" };
		foreach (string text2 in array)
		{
			if (text.StartsWith(text2, StringComparison.OrdinalIgnoreCase))
			{
				int num = text.Length - text2.Length;
				if (text.EndsWith("</color>", StringComparison.OrdinalIgnoreCase))
				{
					num -= "</color>".Length;
				}
				return text.Substring(text2.Length, Math.Max(0, num));
			}
		}
		return text;
	}

	public static string NormalizeOrWhite(string configuredColor)
	{
		string text = configuredColor?.Trim() ?? string.Empty;
		if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
		{
			return string.Empty;
		}
		if (NamedColors.TryGetValue(text, out string value))
		{
			return value;
		}
		string text2 = ((text[0] == '#') ? text.Substring(1) : text);
		if (!IsHex(text2))
		{
			return "#FFFFFF";
		}
		if (text2.Length == 3 || text2.Length == 4)
		{
			char[] array = new char[text2.Length * 2];
			for (int i = 0; i < text2.Length; i++)
			{
				array[i * 2] = text2[i];
				array[i * 2 + 1] = text2[i];
			}
			text2 = new string(array);
		}
		return "#" + text2.ToUpperInvariant();
	}

	private static bool IsHex(string value)
	{
		if (value.Length != 3 && value.Length != 4 && value.Length != 6 && value.Length != 8)
		{
			return false;
		}
		foreach (char c in value)
		{
			bool num = c >= '0' && c <= '9';
			bool flag = c >= 'a' && c <= 'f';
			bool flag2 = c >= 'A' && c <= 'F';
			if (!num && !flag && !flag2)
			{
				return false;
			}
		}
		return true;
	}
}
