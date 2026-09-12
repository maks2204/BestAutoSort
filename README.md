# BestAutoSort

![BestAutoSort icon](icon.png)

Standalone storage mod for Valheim (GUID `dev.maks2204.bestautosort`).
Updated and tested for **Valheim 1.0.12**.

- **Quick-stack:** hotkey moves matching items into nearby storage
- **Lock and restock:** Alt + left-click cycles an item through Locked → Replenish → Nothing
- **Storage rules:** per-chest item/category filters, presets, reserves
- **Crafting and building:** materials from nearby chests within radius
- **Fuel and ingredients:** machines, cooking stations, kilns, smelters, fermenters pull from nearby chests
- **Animal feeding:** hungry tame animals fed automatically from nearby chests
- **Shared chests:** multiple players use the same chest at once
- **Chest upgrades:** wooden → Reinforced / Black Metal / Grausten tiers, contents kept
- **Sort and trash:** LeftAlt + S sorts the open chest, Trash button deletes

Notes:
- Own plugin folder (`BepInEx/plugins/BestAutoSort/`), own config (`dev.maks2204.bestautosort.cfg`).
- Console command: `bestautosort`.

## Install

1. Install [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
2. Drop `BestAutoSort.dll` into `Valheim/BepInEx/plugins/BestAutoSort/`


## Build from source

```powershell
# Game path (if not D:/SteamLibrary/steamapps/common/Valheim)
$env:VALHEIM_INSTALL = "D:/SteamLibrary/steamapps/common/Valheim"

# Debug build + auto-deploy to the game
dotnet build BestAutoSort.csproj -c Debug

# Thunderstore release zip
.\package.ps1
```

See `DEV.md`.
