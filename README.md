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

```
+----------------+
|   ONE CHEST    |
|   ONE OWNER    |  <- ZDO owner = manager, never ping-ponged
|   ONE TX       |  <- validate -> apply -> Save() -> revision -> respond
|   AT A TIME    |     (per-chest serial queue, main thread)
+----------------+
```

Details: `docs/CHEST_TX.md`.

### Actors & channels

```
+----------+  TxRequest   +----------------------+  TxResponse  +----------+
|  CLIENT  | -----------> |  CHEST ZNetView      | -----------> |  CLIENT  |
| (any peer|  (no target: |  (engine routes the  |  (targeted   | (same     |
|  incl.   |  straight to |   packet to the ZDO  |   at the     |  peer)    |
|  server) |  the owner)  |   owner = MANAGER)   |   requester) |          |
+----------+              +----------------------+              +----------+
   TxFlights (target 0 = broadcast, loopback included) ----------> EVERYBODY
   MultiUserHello (version string, exact match) -----------------> EVERYBODY
```

### Packets (`ZPackage`, nested)

| Packet | Layout |
|---|---|
| Request outer | `[txId:long]` `txId = peerId<<32 \| counter` + `[body:ZPackage]` |
| Request body | `[proto:int=2]` `[op:int]` `[baseRev:uint]` `[playerId:long]` `[actorPos:vec3]` `[enforceRule:bool]` `[respectReserves:bool]` `[op-body...]` |
| Response | `[txId:long]` `[status:int]` `[revision:uint]` `[totalsOnly:bool]` `[body:ZPackage]` |
| Take body | `[count:int]` then per entry `[prefabHash:int]` `[item:ZPackage]` `[accepted:int]` |
| Add body | `[count:int]` then per entry `[accepted:int]` |
| Item snapshot | `[prefabHash]` `[itemPkg]` `[amount]` `[x]` `[y]` `[maxStack]`; `itemPkg` = `ItemData.Save` (quality/variant/durability/stack/crafter/customData/world/gridPos) |
| Ops | Add / AddBatch / Take / TakeBatch / Move / Sort / Upgrade / SetRule / ViewerOpen / ViewerClose / Query |
| Statuses | Accepted / Partial / Rejected / Duplicate / UnknownTx |

### Take one stack (GUI drag)

```
CLIENT (C)                         MANAGER (M, ZDO owner)
   |                                         |
   | 1. snapshot item, want = chest cell     |
   |    txId = peer<<32|counter              |
   |--- TxRequest -------------------------->|
   |                                         | 2. idempotency: mem-cache 128
   |                                         |    + ZDO ring x32
   |                                         | 3. resolve LIVE stack
   |                                         |    (cell+name+quality)
   |                                         | 4. remove -> Save()
   |                                         |    -> revision++
   |<-- TxResponse(Accepted, rev, take-body) -|
   | 5. credit EXACTLY accepted (ref, else   |
   |    name-scan fallback); overflow /      |
   |    shortfall compensated back; late     |
   |    duplicates ignored                   |
   |                                         |
   +- Flights: M broadcasts (chest->taker) to all; originator skips own txId
```

### Deposit batch (quick-stack, per chest)

```
CLIENT (C)                         MANAGER (M, ZDO owner)
   |                                         |
   | 1. candidates vs local snapshot         |
   |    (rule/seeds, auto-place X=Y=-1)      |
   | 2. AddBatch(EnforceRule); >4 items      |
   |    become chunk-txs of 4; claim guard   |
   |    drops double-submits of one stack    |
   |--- TxRequest -------------------------->|
   |                                         | 3. per item: rule -> merge /
   |                                         |    auto-place
   |                                         | 4. Save() -> respond counts
   |<-- TxResponse(Accepted/Partial,         |    (Partial if full, Rejected
   |              accepted[]) ---------------|     on rule/access; never
   | 5. remove EXACTLY accepted from self;   |     a partial STATE)
   |    short-removal compensated back       |
   | 6. remainder cascades to next chest     |
```

### Failure paths: retry, query, handoff

```
CLIENT (C)                 MANAGER (M)                 NEW OWNER (M')
   |                          |                             |
   |--- TxRequest ------------>| X response lost             |
   |      (timeout)            |                             |
   |--- TxRequest (same txId) ->| cache hit -> Duplicate,     |
   |<-- TxResponse(Duplicate) -| NO re-apply                 |
   |      (still nothing)      |                             |
   |--- Query(txId) ---------->| cached result -> C          |
   |                          | (or UnknownTx -> client      |
   |                          |  retries as a NEW tx)        |
   |                          |      +- ownership migrates ->|
   |                          |      | M' seeds cache from   |
   |                          |      | ZDO ring (totals, x32)|
   |--- retry / Query --------+----->| M' Take: re-take live |
   |<-- Duplicate + items ----+------| equivalents; Adds:    |
   |                                 | answer totals         |
   +-- client pending-set drops late duplicates: no double credit --+
```

### Viewers (many GUIs, one chest) + feeding

```
OPEN:  C --- ViewerOpen (fire-and-forget + heartbeat) ---> M tracks presence
VIEW:  C polls ZDO.DataRevision + s_items bytes ---> reload with GUI OPEN
       (monotonic: stale revs skipped; lid flap suppressed for viewers)
FEED:  animal owner --- Take(1 unit, actorPos=ANIMAL) ---> M (like any client)
       on accept ---> vanilla OnConsumedItem(animal); else unit sent back.
       No ownership transfer, no leases. Flights: chest ---> animal.
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
