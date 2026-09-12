using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BestAutoSort.Core;
using BestAutoSort.Tx;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class StorageCommands
{
	[Serializable]
	[CompilerGenerated]
	private sealed class _003C_003Ec
	{
		public static readonly _003C_003Ec _003C_003E9 = new _003C_003Ec();

		public static Func<KeyValuePair<string, int>, string> _003C_003E9__1_1;

		public static ConsoleEvent _003C_003E9__1_0;

		internal void _003CRegister_003Eb__1_0(ConsoleEventArgs args)
		{
			if (!Plugin.IsActive)
			{
				return;
			}
			try
			{
				Player localPlayer = Player.m_localPlayer;
				if ((Object)(object)localPlayer == (Object)null)
				{
					throw new InvalidOperationException("Join a world first.");
				}
				string[] args2 = args.Args;
				if (args2.Length > 1 && args2[1] == "preview")
				{
					foreach (string item in QuickStackService.Preview(localPlayer))
					{
						args.Context.AddString(item);
					}
					return;
				}
				if (args2.Length < 3)
				{
					throw new InvalidOperationException("Use bestautosort preview, reserve list/set/clear, or preset list/save/apply/delete.");
				}
				string text = args2[2];
				if (args2[1] == "preset")
				{
					localPlayer.m_customData.TryGetValue("BestAutoSort.StoragePresets.v1", out var value);
					Dictionary<string, StoragePreset> dictionary = StoragePresetCodec.Decode(value);
					if (text == "list")
					{
						args.Context.AddString("Storage presets: " + string.Join(", ", dictionary.Keys));
						return;
					}
					string text2 = string.Join(" ", args2.Skip(3)).Trim();
					if (!StoragePresetCodec.ValidName(text2))
					{
						throw new InvalidOperationException("Use a preset name of 1–32 letters, digits, spaces, underscores or dashes.");
					}
					if (text == "delete")
					{
						if (!dictionary.Remove(text2))
						{
							throw new InvalidOperationException("Preset not found.");
						}
					}
					else
					{
						Container container = OpenChest(localPlayer);
						if (!(text == "save"))
						{
							if (text == "apply")
							{
								if (!dictionary.TryGetValue(text2, out var value2))
								{
									throw new InvalidOperationException("Preset not found.");
								}
								if (!ChestStorageRuleCodec.TryDeserialize(value2.Rule, out ChestStorageRule rule))
								{
									throw new InvalidOperationException("Preset rule is invalid.");
								}
								StorageReserves.Decode(value2.Reserves);
								if (!ChestRuleStore.TryWrite(container, rule, out string error))
								{
									throw new InvalidOperationException(error);
								}
								ChestReserveStore.Write(container, value2.Reserves);
								args.Context.AddString("Applied storage rule and reserves from " + text2 + ".");
								return;
							}
							throw new InvalidOperationException("Use preset save, apply, delete or list.");
						}
						dictionary[text2] = new StoragePreset(ChestStorageRuleCodec.Serialize(ChestRuleStore.Read(container)), ChestReserveStore.ReadRaw(container));
					}
					localPlayer.m_customData["BestAutoSort.StoragePresets.v1"] = StoragePresetCodec.Encode(dictionary);
					args.Context.AddString("Updated preset " + text2 + ".");
					return;
				}
				if (args2[1] != "reserve")
				{
					throw new InvalidOperationException("Unknown BestAutoSort command.");
				}
				Container container2 = OpenChest(localPlayer);
				Dictionary<string, int> dictionary2 = StorageReserves.Decode(ChestReserveStore.ReadRaw(container2));
				if (text == "list")
				{
					args.Context.AddString("Chest reserves: " + string.Join(", ", dictionary2.Select((KeyValuePair<string, int> pair) => pair.Key + "=" + pair.Value)));
					return;
				}
				if (args2.Length < 4)
				{
					throw new InvalidOperationException("A prefab name is required.");
				}
				string key = ChestStorageRule.NormalizePrefabName(args2[3]);
				if (text == "clear")
				{
					dictionary2.Remove(key);
				}
				else
				{
					if (!(text == "set") || args2.Length != 5 || !int.TryParse(args2[4], out var result) || result < 0)
					{
						throw new InvalidOperationException("Use reserve set <prefab> <minimum> or reserve clear <prefab>.");
					}
					dictionary2[key] = result;
				}
				ChestReserveStore.Write(container2, StorageReserves.Encode(dictionary2));
				args.Context.AddString("Updated reserves for the open chest.");
			}
			catch (Exception ex)
			{
				args.Context.AddString("BestAutoSort: " + ex.Message);
			}
		}

		internal string _003CRegister_003Eb__1_1(KeyValuePair<string, int> pair)
		{
			return pair.Key + "=" + pair.Value;
		}
	}

	private const string PresetsKey = "BestAutoSort.StoragePresets.v1";

	internal static void Register()
	{
		object obj = _003C_003Ec._003C_003E9__1_0;
		if (obj == null)
		{
			ConsoleEvent val = delegate(ConsoleEventArgs args)
			{
				if (!Plugin.IsActive)
				{
					return;
				}
				try
				{
					Player localPlayer = Player.m_localPlayer;
					if ((Object)(object)localPlayer == (Object)null)
					{
						throw new InvalidOperationException("Join a world first.");
					}
					string[] args2 = args.Args;
					if (args2.Length > 1 && args2[1] == "preview")
					{
						foreach (string item in QuickStackService.Preview(localPlayer))
						{
							args.Context.AddString(item);
						}
						return;
					}
					if (args2.Length < 3)
					{
						throw new InvalidOperationException("Use bestautosort preview, reserve list/set/clear, or preset list/save/apply/delete.");
					}
					string text = args2[2];
					if (args2[1] == "preset")
					{
						localPlayer.m_customData.TryGetValue("BestAutoSort.StoragePresets.v1", out var value);
						Dictionary<string, StoragePreset> dictionary = StoragePresetCodec.Decode(value);
						if (text == "list")
						{
							args.Context.AddString("Storage presets: " + string.Join(", ", dictionary.Keys));
						}
						else
						{
							string text2 = string.Join(" ", args2.Skip(3)).Trim();
							if (!StoragePresetCodec.ValidName(text2))
							{
								throw new InvalidOperationException("Use a preset name of 1–32 letters, digits, spaces, underscores or dashes.");
							}
							if (text == "delete")
							{
								if (!dictionary.Remove(text2))
								{
									throw new InvalidOperationException("Preset not found.");
								}
							}
							else
							{
								Container container = OpenChest(localPlayer);
								if (!(text == "save"))
								{
									if (text == "apply")
									{
										if (!dictionary.TryGetValue(text2, out var value2))
										{
											throw new InvalidOperationException("Preset not found.");
										}
										if (!ChestStorageRuleCodec.TryDeserialize(value2.Rule, out ChestStorageRule rule))
										{
											throw new InvalidOperationException("Preset rule is invalid.");
										}
										StorageReserves.Decode(value2.Reserves);
										if (!ChestRuleStore.TryWrite(container, rule, out string error))
										{
											throw new InvalidOperationException(error);
										}
										ChestReserveStore.Write(container, value2.Reserves);
										args.Context.AddString("Applied storage rule and reserves from " + text2 + ".");
										return;
									}
									throw new InvalidOperationException("Use preset save, apply, delete or list.");
								}
								dictionary[text2] = new StoragePreset(ChestStorageRuleCodec.Serialize(ChestRuleStore.Read(container)), ChestReserveStore.ReadRaw(container));
							}
							localPlayer.m_customData["BestAutoSort.StoragePresets.v1"] = StoragePresetCodec.Encode(dictionary);
							args.Context.AddString("Updated preset " + text2 + ".");
						}
					}
					else
					{
						if (args2[1] != "reserve")
						{
							throw new InvalidOperationException("Unknown BestAutoSort command.");
						}
						Container container2 = OpenChest(localPlayer);
						Dictionary<string, int> dictionary2 = StorageReserves.Decode(ChestReserveStore.ReadRaw(container2));
						if (text == "list")
						{
							args.Context.AddString("Chest reserves: " + string.Join(", ", dictionary2.Select((KeyValuePair<string, int> pair) => pair.Key + "=" + pair.Value)));
						}
						else
						{
							if (args2.Length < 4)
							{
								throw new InvalidOperationException("A prefab name is required.");
							}
							string key = ChestStorageRule.NormalizePrefabName(args2[3]);
							if (text == "clear")
							{
								dictionary2.Remove(key);
							}
							else
							{
								if (!(text == "set") || args2.Length != 5 || !int.TryParse(args2[4], out var result) || result < 0)
								{
									throw new InvalidOperationException("Use reserve set <prefab> <minimum> or reserve clear <prefab>.");
								}
								dictionary2[key] = result;
							}
							ChestReserveStore.Write(container2, StorageReserves.Encode(dictionary2));
							args.Context.AddString("Updated reserves for the open chest.");
						}
					}
				}
				catch (Exception ex)
				{
					args.Context.AddString("BestAutoSort: " + ex.Message);
				}
			};
			_003C_003Ec._003C_003E9__1_0 = val;
			obj = (object)val;
		}
		new ConsoleCommand("bestautosort", "preview; reserve list/set <prefab> <minimum>/clear <prefab>; preset list/save/apply/delete <name>", (ConsoleEvent)obj, false, false, false, false, false, false, (ConsoleOptionsFetcher)null, false, false, false);
	}

	private static Container OpenChest(Player player)
	{
		Container val = InventoryAccess.CurrentContainer(InventoryGui.instance);
		if ((Object)(object)val == (Object)null || AutoFeedService.IsLocked(val) || !val.IsOwner() || !TxReflect.HasAccess(val, player.GetPlayerID()) || !ChestAuthority.WardAccess(val, player.GetPlayerID()))
		{
			throw new InvalidOperationException("Open an accessible chest and wait for ownership first.");
		}
		return val;
	}
}
