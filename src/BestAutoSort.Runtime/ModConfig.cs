using System;
using BepInEx.Configuration;
using BestAutoSort.Core;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class ModConfig
{
	internal static ConfigEntry<bool> Enabled { get; private set; }

	internal static ConfigEntry<KeyboardShortcut> QuickStackShortcut { get; private set; }

	internal static ConfigEntry<KeyboardShortcut> SortChestShortcut { get; private set; }

	internal static ConfigEntry<float> NearbyRange { get; private set; }

	internal static ConfigEntry<float> RequestSpacing { get; private set; }

	internal static ConfigEntry<bool> ProtectHotbar { get; private set; }

	internal static ConfigEntry<bool> TreatEggsAsAutoStack { get; private set; }

	internal static ConfigEntry<bool> SkipCustomData { get; private set; }

	internal static ConfigEntry<bool> IncludeVehicleContainers { get; private set; }

	internal static ConfigEntry<bool> IncludeWorldContainers { get; private set; }

	internal static ConfigEntry<StorageMatchMode> StorageMatchMode { get; private set; }

	internal static ConfigEntry<SortMode> ChestSortMode { get; private set; }

	internal static ConfigEntry<bool> SortDescending { get; private set; }

	internal static ConfigEntry<bool> ShowButtons { get; private set; }

	internal static ConfigEntry<float> PlayerSideControlsOffsetX { get; private set; }

	internal static ConfigEntry<bool> ShowTransferFlights { get; private set; }

	internal static ConfigEntry<int> MaxFlightsPerItem { get; private set; }

	internal static ConfigEntry<float> FlightDuration { get; private set; }

	internal static ConfigEntry<float> FlightArcHeight { get; private set; }

	internal static ConfigEntry<string> SignTextColor { get; private set; }

	internal static ConfigEntry<float> RuleEditorScrollSensitivity { get; private set; }

	internal static ConfigEntry<bool> CraftFromNearbyChests { get; private set; }

	internal static ConfigEntry<float> SharedResourceRange { get; private set; }

	internal static ConfigEntry<bool> AllowConcurrentChestUse { get; private set; }

	internal static ConfigEntry<ServerAuthorityMode> ChestAuthorityMode { get; private set; }

	internal static ConfigEntry<bool> AutoFeedEnabled { get; private set; }

	internal static ConfigEntry<float> AutoFeedRange { get; private set; }

	internal static ConfigEntry<float> AutoFeedInterval { get; private set; }

	internal static ConfigEntry<string> AutoFeedContainerPrefix { get; private set; }

	internal static ConfigEntry<bool> KilnAllowCoreWood { get; private set; }

	internal static ConfigEntry<bool> KilnAllowFineWood { get; private set; }

	internal static ConfigEntry<bool> TxVerbose { get; private set; }

	internal static void Bind(ConfigFile config)
	{
		ConfigEntry<int> val = config.Bind<int>("Internal", "DefaultsMigrationVersion", 0, "Tracks one-time default migrations. Do not edit manually.");
		Enabled = config.Bind<bool>("General", "Enabled", true, "Master switch for BestAutoSort.");
		QuickStackShortcut = config.Bind<KeyboardShortcut>("Input", "QuickStackNearby", new KeyboardShortcut((KeyCode)96, Array.Empty<KeyCode>()), "Quick-stack eligible player items into nearby chests that already contain that item type.");
		SortChestShortcut = config.Bind<KeyboardShortcut>("Input", "SortOpenChest", new KeyboardShortcut((KeyCode)115, (KeyCode[])(object)new KeyCode[1] { (KeyCode)308 }), "Sort the currently open chest.");
		NearbyRange = config.Bind<float>("Quick Stack", "NearbyRange", 20f, new ConfigDescription("Maximum quick-stack radius in metres.", (AcceptableValueBase)(object)new AcceptableValueRange<float>(2f, 50f), Array.Empty<object>()));
		RequestSpacing = config.Bind<float>("Quick Stack", "RequestSpacing", 0.06f, new ConfigDescription("Delay between vanilla container ownership requests.", (AcceptableValueBase)(object)new AcceptableValueRange<float>(0f, 0.5f), Array.Empty<object>()));
		ProtectHotbar = config.Bind<bool>("Quick Stack", "ProtectHotbar", true, "Never move items currently placed in the first player-inventory row.");
		TreatEggsAsAutoStack = config.Bind<bool>("Quick Stack", "TreatEggsAsAutoStack", true, "Treat eggs (Asksvin, Chicken, Vulture) as quick-stackable even though vanilla sets m_autoStack=false on them.");
		SkipCustomData = config.Bind<bool>("Quick Stack", "SkipCustomData", true, "Skip items with custom data to avoid merging modded item state into an incompatible stack.");
		IncludeVehicleContainers = config.Bind<bool>("Quick Stack", "IncludeVehicleContainers", true, "Include player-built cart and ship containers. Enabled by default; normal range, access, and in-use checks still apply.");
		IncludeWorldContainers = config.Bind<bool>("Quick Stack", "IncludeWorldContainers", false, "Include loot chests and other physical containers not placed by a player.");
		StorageMatchMode = config.Bind<StorageMatchMode>("Quick Stack", "StorageMatchMode", BestAutoSort.Core.StorageMatchMode.ExactItemOnly, "ExactItemOnly is the default and stores only exact items already present in an Auto chest. ExactItemOrCategory also treats every stored item as a seed for its detailed category.");
		if (val.Value < 1)
		{
			if (StorageMatchMode.Value == BestAutoSort.Core.StorageMatchMode.ExactItemOrCategory)
			{
				StorageMatchMode.Value = BestAutoSort.Core.StorageMatchMode.ExactItemOnly;
			}
			val.Value = 1;
		}
		ChestSortMode = config.Bind<SortMode>("Sorting", "ChestSortMode", SortMode.Category, "Primary criterion used by the chest sort button and shortcut.");
		SortDescending = config.Bind<bool>("Sorting", "SortDescending", false, "Reverse the configured primary sort criterion.");
		ShowButtons = config.Bind<bool>("Interface", "ShowButtons", true, "Add Trash, Stack, Sort Chest, Storage, and unlocked chest-tier Upgrade buttons to the inventory interface.");
		PlayerSideControlsOffsetX = config.Bind<float>("Interface", "PlayerSideControlsOffsetX", 12f, new ConfigDescription("Horizontal offset in Valheim UI canvas units for the Trash and Stack rail beside the vanilla player inventory.", (AcceptableValueBase)(object)new AcceptableValueRange<float>(-100f, 100f), Array.Empty<object>()));
		ShowTransferFlights = config.Bind<bool>("Interface", "ShowTransferFlights", true, "Animate representative item icons from the player to each chest after the server-authorized transfer completes.");
		MaxFlightsPerItem = config.Bind<int>("Interface", "MaxFlightsPerItem", 3, new ConfigDescription("Maximum animated icons per transferred item type.", (AcceptableValueBase)(object)new AcceptableValueRange<int>(1, 8), Array.Empty<object>()));
		FlightDuration = config.Bind<float>("Interface", "FlightDuration", 1.75f, new ConfigDescription("Seconds each item-flight animation lasts.", (AcceptableValueBase)(object)new AcceptableValueRange<float>(0.1f, 4f), Array.Empty<object>()));
		FlightArcHeight = config.Bind<float>("Interface", "FlightArcHeight", 2.5f, new ConfigDescription("Height in metres of the item-flight arc.", (AcceptableValueBase)(object)new AcceptableValueRange<float>(0f, 5f), Array.Empty<object>()));
		SignTextColor = config.Bind<string>("Interface", "SignTextColor", "white", "Default Valheim sign-text color. Accepts names such as white, yellow, red, orange, blue, green, purple, pink, cyan, gold, gray, lime, or teal, and hex values such as #4FC3F7. Use vanilla or none to disable automatic coloring. Existing custom color tags are preserved.");
		CraftFromNearbyChests = config.Bind<bool>("Shared Resources", "CraftFromNearbyChests", true, "Use eligible nearby chests for crafting, building, and manually filling torches, fires, kilns, furnaces, cooking stations, fermenters, and other supported production structures.");
		SharedResourceRange = config.Bind<float>("Shared Resources", "Range", 30f, new ConfigDescription("Maximum shared chest-resource range in metres for crafting, building, and operated production structures.", (AcceptableValueBase)(object)new AcceptableValueRange<float>(2f, 100f), Array.Empty<object>()));
		AllowConcurrentChestUse = config.Bind<bool>("Multiplayer", "AllowConcurrentChestUse", true, "Allow players running the same BestAutoSort version to use one stationary chest concurrently. Mutations are serialized by the authoritative chest manager (ZDO owner) as idempotent transactions; ownership is never ping-ponged.");
		ChestAuthorityMode = config.Bind<ServerAuthorityMode>("Multiplayer", "ChestAuthorityMode", ServerAuthorityMode.ServerAuthority, "Wave-1 server authority (experimental): chest ZDO ownership stays on the server/host and never transfers to clients. LegacyDistributed restores pre-0.6 distributed ownership. All peers must use the same mode and mod version; mismatched peers are rejected, and vanilla (mod-less) clients are unsupported for managed chests.");
		AutoFeedEnabled = config.Bind<bool>("Auto Feed", "Enabled", true, "Automatically feed hungry tameable creatures from eligible nearby chests on the peer that owns the creature.");
		AutoFeedRange = config.Bind<float>("Auto Feed", "Range", 25f, new ConfigDescription("Maximum distance in metres between a hungry creature and a feed chest.", (AcceptableValueBase)(object)new AcceptableValueRange<float>(2f, 50f), Array.Empty<object>()));
		AutoFeedInterval = config.Bind<float>("Auto Feed", "ScanInterval", 5f, new ConfigDescription("Minimum seconds between nearby-chest scans for each hungry creature.", (AcceptableValueBase)(object)new AcceptableValueRange<float>(1f, 60f), Array.Empty<object>()));
		AutoFeedContainerPrefix = config.Bind<string>("Auto Feed", "ContainerNamePrefix", "piece_chest", "Only use container prefab names beginning with this value. Leave empty to allow every otherwise eligible player-built container.");
		KilnAllowCoreWood = config.Bind<bool>("Production", "KilnAllowCoreWood", false, "Allow kilns to pull Core Wood from nearby chests after normal Wood. Does not restrict wood carried by the player.");
		KilnAllowFineWood = config.Bind<bool>("Production", "KilnAllowFineWood", false, "Allow kilns to pull Fine Wood from nearby chests after normal Wood and enabled Core Wood. Does not restrict wood carried by the player.");
		RuleEditorScrollSensitivity = config.Bind<float>("Interface", "RuleEditorScrollSensitivity", 210f, new ConfigDescription("Mouse-wheel scroll speed in the chest rule editor. 210 is 750% of the original speed.", (AcceptableValueBase)(object)new AcceptableValueRange<float>(28f, 560f), Array.Empty<object>()));
		TxVerbose = config.Bind<bool>("Debug", "TxVerbose", false, "Detailed [ChestTX] transaction logs (requests, commits, rejects, duplicates, handoffs). No per-frame logging.");
		if (val.Value < 2)
		{
			if (Mathf.Abs(RuleEditorScrollSensitivity.Value - 140f) < 0.001f)
			{
				RuleEditorScrollSensitivity.Value = 210f;
			}
			val.Value = 2;
		}
		if (val.Value < 3)
		{
			if (Mathf.Abs(FlightDuration.Value - 1.1f) < 0.001f || Mathf.Abs(FlightDuration.Value - 0.55f) < 0.001f)
			{
				FlightDuration.Value = 0.275f;
			}
			val.Value = 3;
		}
		if (val.Value < 4)
		{
			if (!IncludeVehicleContainers.Value)
			{
				IncludeVehicleContainers.Value = true;
			}
			val.Value = 4;
		}
		if (val.Value < 5)
		{
			if (Mathf.Abs(FlightDuration.Value - 0.275f) < 0.001f)
			{
				FlightDuration.Value = 1f;
			}
			val.Value = 5;
		}
	}
}
