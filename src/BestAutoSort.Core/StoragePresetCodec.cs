using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BestAutoSort.Core;

public static class StoragePresetCodec
{
	public static bool ValidName(string name)
	{
		if (!string.IsNullOrWhiteSpace(name) && name.Length <= 32)
		{
			return name.All((char character) => char.IsLetterOrDigit(character) || character == ' ' || character == '-' || character == '_');
		}
		return false;
	}

	public static string Encode(IReadOnlyDictionary<string, StoragePreset> presets)
	{
		if (presets.Count > 32)
		{
			throw new ArgumentException("At most 32 storage presets can be saved.");
		}
		using MemoryStream memoryStream = new MemoryStream();
		using (BinaryWriter binaryWriter = new BinaryWriter(memoryStream, Encoding.UTF8, leaveOpen: true))
		{
			binaryWriter.Write(1);
			binaryWriter.Write(presets.Count);
			foreach (KeyValuePair<string, StoragePreset> item in presets.OrderBy<KeyValuePair<string, StoragePreset>, string>((KeyValuePair<string, StoragePreset> pair) => pair.Key, StringComparer.OrdinalIgnoreCase))
			{
				Validate(item.Key, item.Value);
				binaryWriter.Write(item.Key);
				binaryWriter.Write(item.Value.Rule);
				binaryWriter.Write(item.Value.Reserves);
			}
		}
		if (memoryStream.Length > 1048576)
		{
			throw new ArgumentException("Storage presets exceed the one MiB limit.");
		}
		return Convert.ToBase64String(memoryStream.ToArray());
	}

	public static Dictionary<string, StoragePreset> Decode(string? payload)
	{
		Dictionary<string, StoragePreset> dictionary = new Dictionary<string, StoragePreset>(StringComparer.OrdinalIgnoreCase);
		if (payload == null)
		{
			return dictionary;
		}
		if (payload.Length > 1400000)
		{
			throw new FormatException("Storage preset payload is too large.");
		}
		using MemoryStream memoryStream = new MemoryStream(Convert.FromBase64String(payload));
		using BinaryReader binaryReader = new BinaryReader(memoryStream, Encoding.UTF8);
		if (binaryReader.ReadInt32() != 1)
		{
			throw new FormatException("Unsupported storage preset format.");
		}
		int num = binaryReader.ReadInt32();
		if (num < 0 || num > 32)
		{
			throw new FormatException("Invalid storage preset count.");
		}
		for (int i = 0; i < num; i++)
		{
			string text = binaryReader.ReadString();
			StoragePreset storagePreset = new StoragePreset(binaryReader.ReadString(), binaryReader.ReadString());
			Validate(text, storagePreset);
			dictionary.Add(text, storagePreset);
		}
		if (memoryStream.Position != memoryStream.Length)
		{
			throw new FormatException("Unexpected storage preset data.");
		}
		return dictionary;
	}

	private static void Validate(string name, StoragePreset preset)
	{
		if (!ValidName(name) || preset.Rule.Length > 65536 || !ChestStorageRuleCodec.TryDeserialize(preset.Rule, out ChestStorageRule _))
		{
			throw new FormatException("Invalid storage preset.");
		}
		StorageReserves.Decode(preset.Reserves);
	}
}
