using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BestAutoSort.Runtime;
using BestAutoSort.Tx;
using BestAutoSort.Patches;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort;

[BepInPlugin("dev.maks2204.bestautosort", "BestAutoSort", "0.6.1")]
[BepInProcess("valheim.exe")]
[BepInProcess("valheim_server.exe")]
[BepInIncompatibility("goldenrevolver.quick_stack_store")]
[BepInIncompatibility("com.maxsch.valheim.MultiUserChest")]
[BepInIncompatibility("Narolith.AutoFeed")]
public sealed class Plugin : BaseUnityPlugin
{
	internal const string PluginGuid = "dev.maks2204.bestautosort";

	internal const string PluginName = "BestAutoSort";

	internal const string PluginVersion = "0.6.3";


	private Harmony? _harmony;

	private bool _shutDown;

	internal static Plugin Instance { get; private set; }

	internal static ManualLogSource LogInstance { get; private set; }

	internal QuickStackService QuickStackService { get; private set; }

	internal static bool IsActive { get; private set; }

	private void Awake()
	{
		IsActive = false;
		_shutDown = false;
		Instance = this;
		LogInstance = Logger;
		try
		{
			ModConfig.Bind(ConfigFileMigration.Open((BaseUnityPlugin)(object)this, Paths.ConfigPath, Logger));
			QuickStackService = new QuickStackService(this);
			StorageCommands.Register();
			_harmony = new Harmony("dev.maks2204.bestautosort");
			PatchAllSafely();
			IsActive = true;
			Logger.LogInfo((object)("BestAutoSort " + PluginVersion + " (" + BuildInfo.Commit + ") loaded."));
			try
			{
				Logger.LogInfo((object)("[ChestTX] config: TxVerbose=" + ModConfig.TxVerbose.Value + " ChestAuthorityMode=" + ModConfig.ChestAuthorityMode.Value + " effective=" + ServerAuthority.EffectiveMode()));
			}
			catch (Exception ex2)
			{
				Logger.LogInfo((object)("[ChestTX] config dump failed: " + ex2.Message));
			}
		}
		catch (Exception ex)
		{
			Logger.LogError((object)string.Format("{0} failed to initialize safely: {1}", "BestAutoSort", ex));
			Shutdown();
			throw;
		}
	}

	private void PatchAllSafely()
	{
		Type[] array = (from type2 in typeof(Plugin).Assembly.GetTypes()
			where type2.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length != 0
			select type2).OrderBy((Type type2) => type2.FullName, StringComparer.Ordinal).ToArray();
		int num = 0;
		Type[] array2 = array;
		foreach (Type type in array2)
		{
			try
			{
				_harmony.PatchAll(type);
				num++;
			}
			catch (Exception arg)
			{
				Logger.LogError((object)$"Failed to apply patch class {type.FullName}; other BestAutoSort features will remain available: {arg}");
			}
		}
		Logger.LogInfo((object)$"Applied {num} of {array.Length} BestAutoSort patch classes.");
		if (ServerReleaseGuard.PatchedSites != ServerReleaseGuard.ExpectedSites)
			Logger.LogError((object)("ServerReleaseGuard transpiler applied to " + ServerReleaseGuard.PatchedSites + " of " + ServerReleaseGuard.ExpectedSites + " ReleaseNearbyZDOS ownership sites - redistribution exclusion DISABLED, game update likely changed (SetOwner backstop still holds fail-closed)."));
		else
			Logger.LogInfo((object)("ServerReleaseGuard rerouted " + ServerReleaseGuard.PatchedSites + " ReleaseNearbyZDOS ownership site(s) through the server-authority guard."));
		if (TxRenderPatch.ReplacedCount == 0)
			Logger.LogError((object)"TxRenderPatch transpiler found no Container.IsOwner call in InventoryGui.UpdateContainer — multi-viewer rendering is DISABLED, game update likely changed.");
		else
			Logger.LogInfo((object)$"TxRenderPatch replaced {TxRenderPatch.ReplacedCount} owner-check(s) for shared rendering.");
	}

	private void Update()
	{
		if (!IsActive)
		{
			return;
		}
		ChestTxService.Pump();
		AutoFeedService.Update();
		NearbyPlaceIntent.Pump();
		NearbyResourceService.RepairInvalidPlayerInventoryPositions();
		if (ChestUpgradeService.UpdateLegacyMigration())
		{
			return;
		}
		ChestRuleEditor.Update();
		InventoryButtons.UpdateVisibility();
		if (ModConfig.Enabled.Value && CanAcceptShortcut())
		{
			if (IsShortcutDownAllowingOtherKeys(ModConfig.QuickStackShortcut.Value))
			{
				QuickStackNearby();
			}
			KeyboardShortcut value = ModConfig.SortChestShortcut.Value;
			if ((value).IsDown())
			{
				SortOpenChest();
			}
		}
	}

	internal void QuickStackNearby()
	{
		if (!IsActive)
		{
			return;
		}
		try
		{
			QuickStackService.BeginNearbyQuickStack(RestockProfileService.RestockNearby);
		}
		catch (Exception arg)
		{
			Logger.LogError((object)$"Nearby quick stack failed: {arg}");
		}
	}

	internal void SortOpenChest()
	{
		if (!IsActive)
		{
			return;
		}
		try
		{
			Container val = InventoryAccess.CurrentContainer(InventoryGui.instance);
			if ((Object)(object)val == (Object)null)
			{
				Player localPlayer = Player.m_localPlayer;
				if (localPlayer != null)
				{
					((Character)localPlayer).Message((MessageType)2, "Open a chest to sort it.", 0, (Sprite)null, false);
				}
				return;
			}
			if (!ChestTxService.IsShared(val))
			{
				// Non-shared (carts/ships/vanilla): manager only, as before.
				// Wave-2: authority-routed, so remote managed chests fail closed
				// (never SortLocal against a stale replica).
				// Quarantine gate (mirrors MutateLocal): sorting re-persists
				// the whole inventory, so a quarantined chest must refuse —
				// saving speculative RAM would defeat authoritative reload.
				if (ChestTxService.IsManager(val) && !ChestTxService.IsQuarantined(val))
					SortLocal(val);
				else if (ChestTxService.IsQuarantined(val))
				{
					TxLog.Warn("sort refused: chest quarantined (fail closed)");
					Player quarantinePlayer = Player.m_localPlayer;
					if ((Object)(object)quarantinePlayer != (Object)(object)null)
						((Character)quarantinePlayer).Message((MessageType)2, "Chest state is still being verified. Try sorting again shortly.", 0, (Sprite)null, false);
				}
				return;
			}
			ChestTxService.RequestSort(val, (int)ModConfig.ChestSortMode.Value, ModConfig.SortDescending.Value);
		}
		catch (Exception arg)
		{
			Logger.LogError((object)$"Chest sort failed: {arg}");
		}
	}

	internal static void SortLocal(Container container)
	{
		int num = InventorySorter.Sort(container.GetInventory(), ModConfig.ChestSortMode.Value, ModConfig.SortDescending.Value);
		// Owned chest mutated directly: persist the new order like the manager.
		TxReflect.UpdateRows(container);
		TxReflect.SaveContainer(container);
		Player localPlayer2 = Player.m_localPlayer;
		if (localPlayer2 != null)
		{
			((Character)localPlayer2).Message((MessageType)2, string.Format("Sorted {0} item stack{1} by {2}.", num, (num == 1) ? string.Empty : "s", ModConfig.ChestSortMode.Value), 0, (Sprite)null, false);
		}
	}

	internal void UpgradeOpenChest(int targetTier)
	{
		if (!IsActive)
		{
			return;
		}
		try
		{
			Container val = InventoryAccess.CurrentContainer(InventoryGui.instance);
			if ((Object)(object)val == (Object)null)
				return;
			// Wave-2: repair-first (server-only inside; remote no-op) so a genuine
			// host upgrade is not mistaken for a remote structural attempt.
			ServerAuthority.EnsureServerOwnership(val, "upgrade-open");
			// 0.6.x: server-managed chests upgrade through the server-mediated
			// queue (ghost protocol + validated receipt) — host and remote alike.
			if (ServerAuthority.IsAuthorityMode() && ServerAuthority.IsServerManagedContainer(val))
			{
				ChestUpgradeService.UpgradeOpenChest(targetTier);
				return;
			}
			if (!ChestTxService.IsShared(val) || ChestTxService.IsManager(val))
			{
				ChestUpgradeService.UpgradeOpenChest(targetTier);
				return;
			}
			// Structural operation without ping-pong: one-shot explicit ownership acquire,
			// then a local upgrade. Viewers keep seeing the chest via presence+refresh.
			if (ChestTxService.AcquireForStructural(val))
			{
				ChestUpgradeService.UpgradeOpenChest(targetTier);
			}
			else
			{
				Player localPlayer = Player.m_localPlayer;
				if (localPlayer != null)
				{
					((Character)localPlayer).Message((MessageType)2, "Could not acquire the chest for upgrade. Try again.", 0, (Sprite)null, false);
				}
			}
		}
		catch (Exception arg)
		{
			Logger.LogError((object)$"Chest upgrade failed: {arg}");
			Player localPlayer = Player.m_localPlayer;
			if (localPlayer != null)
			{
				((Character)localPlayer).Message((MessageType)2, "Chest upgrade failed. See the log for details.", 0, (Sprite)null, false);
			}
		}
	}

	private static bool CanAcceptShortcut()
	{
		if ((Object)(object)Player.m_localPlayer == (Object)null)
		{
			return false;
		}
		if (ChestRuleEditor.IsOpen)
		{
			return false;
		}
		if ((Object)(object)Chat.instance != (Object)null && Chat.instance.HasFocus())
		{
			return false;
		}
		if (Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible())
		{
			return false;
		}
		if ((Object)(object)Minimap.instance != (Object)null && Minimap.InTextInput())
		{
			return false;
		}
		return true;
	}

	private static bool IsShortcutDownAllowingOtherKeys(KeyboardShortcut shortcut)
	{
		KeyCode mainKey = (shortcut).MainKey;
		if ((int)mainKey == 0 || !UnityInput.Current.GetKeyDown(mainKey))
		{
			return false;
		}
		return (shortcut).Modifiers.All(delegate(KeyCode modifier)
		{
			return modifier == mainKey || UnityInput.Current.GetKey(modifier);
		});
	}

	private void OnDestroy()
	{
		Shutdown();
	}

	private void Shutdown()
	{
		if (_shutDown)
		{
			return;
		}
		_shutDown = true;
		IsActive = false;
		QuickStackService?.Reset();
		ChestTxService.Reset();
		TxNet.Reset();
		AutoFeedService.Reset();
		InventoryButtons.Detach();
		TransferContext.End();
		Harmony? harmony = _harmony;
		if (harmony != null)
		{
			harmony.UnpatchSelf();
		}
		_harmony = null;
	}
}
