# BestAutoSort — developer guide

Folder: `H:/valheimmods/BestAutoSort`
Game: `D:/SteamLibrary/steamapps/common/Valheim`
Mod: standalone storage (GUID `dev.maks2204.bestautosort`, version 0.1.2, net472, BepInEx 5.4)

## 1. Layout

```
BestAutoSort/
├── BestAutoSort.csproj        # PROJECT in root: net472, BepInEx + Harmony + game
├── src/                       # MOD SOURCES (the only thing that compiles)
│   ├── GlobalUsings.cs          # nested game-type aliases (ItemData, Requirement, ...)
│   ├── Properties/AssemblyInfo.cs
│   ├── BestAutoSort/Plugin.cs     # entry point: config, Harmony patches, hotkeys, Update loop
│   ├── BestAutoSort.Core/         # models: chest rules, categories, sorting, restock, upgrades
│   ├── BestAutoSort.Patches/      # 40+ Harmony patches (craft/fuel from chests, shared chests, ...)
│   ├── BestAutoSort.Runtime/      # services: QuickStack, Restock, AutoFeed, Upgrades, Rules, ModConfig, ...
│   ├── BestAutoSort.TxCore/       # dependency-free protocol core (netstandard2.0, also used by tests)
│   └── Tx/                        # ChestTX wire layer: codec, manager, client, reflection helpers
├── tests/ChestTx.Tests/       # protocol test harness (net8.0 console, `dotnet run`)
├── docs/CHEST_TX.md             # ChestTX protocol spec + race-condition report
├── manifest.json                # Thunderstore manifest (BepInExPack dependency only)
├── README.md                    # Thunderstore description
├── CHANGELOG.md
├── icon.png                     # Thunderstore icon (256x256)
├── package.ps1                  # Thunderstore zip build
└── DEV.md                       # this file
```

## 2. Quick start

```powershell
cd H:/valheimmods/BestAutoSort

# build + auto-deploy to D:/SteamLibrary/.../BepInEx/plugins/BestAutoSort (Debug copies itself)
dotnet build BestAutoSort.csproj -c Debug

# if the game lives elsewhere:
$env:VALHEIM_INSTALL = "D:/SteamLibrary/steamapps/common/Valheim"

# release + thunderstore zip:
.\package.ps1

# protocol tests (offline, no packages needed):
dotnet run --project tests/ChestTx.Tests/ChestTx.Tests.csproj -c Release
```

Verify deploy: `D:/SteamLibrary/steamapps/common/Valheim/BepInEx/plugins/BestAutoSort/BestAutoSort.dll`
Logs: `D:/SteamLibrary/steamapps/common/Valheim/BepInEx/LogOutput.log` — look for `[Info : BestAutoSort]`.

Own GUID, own config (`dev.maks2204.bestautosort.cfg`), own save keys —
no compatibility with other storage mods by design. Conflicting mods
(from BepInIncompatibility): Quick Stack Store, MultiUserChest, AutoFeed — do not install together.

## 3. Multiplayer model (ChestTX)

The chest manager is the ZDO owner. Ownership is never handed over between operations.
Clients send `BestAutoSort_TxRequest` to the chest view (routed to the owner);
the manager applies mutations strictly one at a time from a per-chest queue
(validate → apply → `Save()` → revision → response to the peer).
Clients touch their own inventory only on response, exactly by the accepted amount.
See `docs/CHEST_TX.md` for the full protocol (idempotency, handoff ring, Query,
viewer refresh, presence, lid) and `Debug → TxVerbose` for `[ChestTX]` logs.

Key files: `src/Tx/ChestTxService.cs` (+`.Client.cs`), `TxCodec`, `TxState`,
`TxInventory`, `TxReflect`, `TxNet`, `TxLog`; pure core in `src/BestAutoSort.TxCore/`.

## 4. What was cut vs the original

All gameplay code is as in the original. Removed only:

- `src/BestAutoSort/Plugin.cs`: `ModCoreApi` check/services, module/namespace/UI/metrics
  registration, `SemanticVersion`, `ModuleId`, `_registrations`, static constructor.
  Config is a plain BepInEx `ConfigFile`
- `src/BestAutoSort.Runtime/ConfigFileMigration.cs`: just creates the `ConfigFile`, no migrations
- `[BepInDependency("com.jg224.modcore")]` and the GearSlots dependency removed entirely

GearSlots integration cut wholesale (bridge, placement patch, restock quick/ammo slots, item-id).

## 5. Decompile notes

`src` was ported from an ilspycmd decompile. What was fixed in it:
- `((T)(ref x)).M` → `x.M`; `((T)(ref v))._002Ector(a)` → `v = new T(a)`; `base._002Ector()` removed
- Restored original aliases (`GlobalUsings.cs`): `ItemData`, `Requirement=Piece.Requirement`,
  `RequirementMode=Player.RequirementMode`, `Modifier=InventoryGrid.Modifier`, `ItemType`, `SkillType`,
  `SharedData`, `MessageType`, `ConsoleEvent*`/`ConsoleCommand`, `ButtonClickedEvent`,
  `Object=UnityEngine.Object`, `ThreadTimer` (the game has a global `Timer` from assembly_utils!)
- Unreadable dependency attribute decoded via dnfile (see git history)
- Locally: `Logger`, `(bool)(Object)x`, `(Image.Type)`, auto-properties, `out` in TryParse,
  `(Vector2i?)`, `(ScrollRect.MovementType)`, `(TMP_InputField.LineType)`, `Smelter.ItemConversion`, etc.

## 6. Typical tasks and where to go

| Want... | Where |
|---|---|
| Sorting (mode, order) | `src/BestAutoSort.Runtime/InventorySorter.cs`, `src/BestAutoSort.Core/InventoryOrdering.cs`, `SortMode.cs` |
| Quick-stack | `src/BestAutoSort.Runtime/QuickStackService.cs`, `QuickStackTransfer.cs` |
| Craft/fuel from chests | `src/BestAutoSort.Patches/NearbyCrafting*`, `*FuelFromChestsPatch.cs`, `src/BestAutoSort.Runtime/NearbyResourceService.cs` |
| Chest rules, presets | `src/BestAutoSort.Runtime/ChestRuleStore.cs`, `ChestRuleEditor.cs`, `src/BestAutoSort.Core/ChestStorageRule*.cs` |
| Chest upgrades | `src/BestAutoSort.Runtime/ChestUpgradeService.cs`, `src/BestAutoSort.Core/ChestUpgradePath.cs` |
| Animal auto-feed | `src/BestAutoSort.Runtime/AutoFeed*.cs`, `src/BestAutoSort.Core/AutoFeedPolicy.cs` |
| Restock | `src/BestAutoSort.Runtime/RestockProfileService.cs`, `src/BestAutoSort.Core/Restock*.cs` |
| Config (new options) | `src/BestAutoSort.Runtime/ModConfig.cs` |
| Hotkeys, Update loop | `src/BestAutoSort/Plugin.cs` → `Update()`, `ModConfig` |

## 7. Debugging

- `LogOutput.log` after every game launch.
- If the game does not see the DLL: check for BepInEx 5 (`BepInEx/core` with `0Harmony.dll`), not BepInEx 6.
- Mod profile backup: `C:/Users/maks2/Desktop/mods` is a copy; the live folder is `D:/SteamLibrary/.../Valheim/BepInEx`.
