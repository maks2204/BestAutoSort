using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BestAutoSort.Core;

internal static class RestockProfileCodec
{
	private const string Version = "v1";

	internal static string Serialize(IEnumerable<RestockProfile> profiles)
	{
		IEnumerable<string> second = from profile in profiles.Where((RestockProfile profile) => IsValid(profile)).OrderBy<RestockProfile, string>((RestockProfile profile) => profile.Id, StringComparer.Ordinal)
			select string.Join("|", profile.Id, profile.InventoryKind.ToString(), profile.X.ToString(CultureInfo.InvariantCulture), profile.Y.ToString(CultureInfo.InvariantCulture), Encode(profile.PrefabName), profile.Quality.ToString(CultureInfo.InvariantCulture), profile.Variant.ToString(CultureInfo.InvariantCulture), profile.Target.ToString(CultureInfo.InvariantCulture));
		return string.Join("\n", new string[1] { "v1" }.Concat(second));
	}

	internal static bool TryDeserialize(string? data, out List<RestockProfile> profiles)
	{
		profiles = new List<RestockProfile>();
		if (string.IsNullOrWhiteSpace(data))
		{
			return true;
		}
		string[] array = data.Replace("\r", string.Empty).Split('\n');
		if (array.Length == 0 || !string.Equals(array[0], "v1", StringComparison.Ordinal))
		{
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		for (int i = 1; i < array.Length; i++)
		{
			if (!string.IsNullOrWhiteSpace(array[i]))
			{
				string[] array2 = array[i].Split('|');
				if (array2.Length != 8 || string.IsNullOrWhiteSpace(array2[0]) || !Enum.TryParse<RestockInventoryKind>(array2[1], ignoreCase: false, out var result) || !int.TryParse(array2[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result2) || !int.TryParse(array2[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result3) || !TryDecode(array2[4], out string decoded) || !int.TryParse(array2[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result4) || !int.TryParse(array2[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result5) || !int.TryParse(array2[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result6))
				{
					profiles.Clear();
					return false;
				}
				RestockProfile restockProfile = new RestockProfile(array2[0], result, result2, result3, decoded, result4, result5, result6);
				if (!IsValid(restockProfile) || !hashSet.Add(restockProfile.Id))
				{
					profiles.Clear();
					return false;
				}
				profiles.Add(restockProfile);
			}
		}
		return true;
	}

	private static bool IsValid(RestockProfile profile)
	{
		if (profile != null && !string.IsNullOrWhiteSpace(profile.Id) && !string.IsNullOrWhiteSpace(profile.PrefabName) && profile.X >= 0 && profile.Y >= 0 && profile.Quality >= 0 && profile.Variant >= 0)
		{
			return profile.Target > 0;
		}
		return false;
	}

	private static string Encode(string value)
	{
		return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
	}

	private static bool TryDecode(string value, out string decoded)
	{
		decoded = string.Empty;
		try
		{
			decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
			return !string.IsNullOrWhiteSpace(decoded);
		}
		catch (FormatException)
		{
			return false;
		}
	}
}
