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
- All players + server must run the same version (exact-match hello gate).

## Multiplayer protocol (ChestTX)

One chest = one manager = the ZDO owner. Ownership is never ping-ponged.
Every mutation is a serial idempotent transaction: validate → apply →
`Save()` → revision → response. Details: `docs/CHEST_TX.md`.

RPCs (all through Ketch-verified `assembly_valheim` API):

- `BestAutoSort_TxRequest` — client → chest ZNetView, no target (engine routes to owner).
- `BestAutoSort_TxResponse` — manager → requesting peer (targeted).
- `BestAutoSort_TxFlights` — manager → everybody (target `0L`, loopback included).
- `BestAutoSort_MultiUserHello` — version string broadcast, exact-match compatibility.

Packet layouts (`ZPackage`, nested):

```
Request outer:  [txId:long][body:ZPackage]
Request body:   [proto:int=2][op:int][baseRev:uint][playerId:long][actorPos:vec3]
                [enforceRule:bool][respectReserves:bool][op-body...]
Response:       [txId:long][status:int][revision:uint][totalsOnly:bool][body:ZPackage]
Take body:      [count:int]([prefabHash:int][item:ZPackage][accepted:int])...
Add body:       [count:int]([accepted:int])...
Item snapshot:  [prefabHash][itemPkg][amount][x][y][maxStack], itemPkg = ItemData.Save
                (quality/variant/durability/stack/crafter/customData/world/gridPos)
Ops: Add/AddBatch/Take/TakeBatch/Move/Sort/Upgrade/SetRule/ViewerOpen/ViewerClose/Query
Statuses: Accepted/Partial/Rejected/Duplicate/UnknownTx
```

Take one stack (GUI drag, client `C`, manager `M` — the ZDO owner):

```
C: snapshot item -> Take(txId=peer<<32|counter, want=chest cell)
C ──TxRequest────────────────────────────────────────────▶ M
M: idempotency check (in-memory cache 128 + ZDO ring x32) → resolve live
   stack by cell+name+quality → remove → Save() → revision++ → respond
M ──TxResponse(Accepted, revision, take-body)──────────────▶ C
C: credit exactly accepted into inventory (ref, name-scan fallback),
   shortfall/overflow compensated back; late duplicates ignored.
   Flights: M broadcasts (chest→taker); originator skips its own txId.
```

Deposit (quick-stack batch, same pattern per chest):

```
C: collect candidates (rule/seeds vs local snapshot, auto-place X=Y=-1) →
   AddBatch(txId, EnforceRule=true), >4 items split into chunk-txs of 4
C ──TxRequest────────────────────────────────────────────▶ M
M: per item: rule check → merge/auto-place → Save() → respond counts
   (Partial when chest fills, Rejected on rule/access, never partial state)
M ──TxResponse(Accepted/Partial, accepted[])──────────────▶ C
C: remove exactly accepted from own inventory (in-flight claim guard drops
   double-submits of the same stack); short-removal compensated back.
```

Lost response / retry / handoff:

```
C: timeout → resend same txId (manager cache → Duplicate, no re-apply) →
   still nothing → Query(txId) → cached result or UnknownTx
Manager migration: new owner seeds cache from the ZDO ring (totals-only);
   Take redelivery re-takes live equivalents (conservation holds),
   Adds answer totals. Client pending-set drops late duplicates.
```

Viewers (many GUIs on one chest) + feeding:

```
Open:   C ──ViewerOpen (fire-and-forget, heartbeat)──▶ M ; M tracks presence
View:   C polls ZDO.DataRevision + s_items bytes; reload with GUI open,
        monotonic (stale revs skipped); lid flap suppressed for viewers.
Feed:   animal owner submits Take(1 unit, actorPos=animal) like any client;
        on accept -> vanilla OnConsumedItem on the animal, else the unit is
        sent back. No ownership transfer, no leases. Flights fly chest→animal.
```

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
