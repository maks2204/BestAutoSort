# ChestTX — authoritative transactional chest multiplayer

## 1. Problem with the original architecture

The mod (and vanilla) opened/stacked/took via ZDO ownership transfer:
`SaveContainer → ForceSendZDO(peer) → SetOwner(peer)` + waiting
(`CompleteAfterOwnership`: loop until deadline + `WaitForSeconds(0.05f)`).
Consequences: someone else's GUI closing (vanilla `UpdateContainer` renders
the panel for the owner only), replays over stale snapshots while holding
`ItemData` references across network delays, `Trying to add item to occupied
slot -1,-1` under concurrent `StackAll`, double payouts on double TakeAll,
background (autofeed/auto-pull/craft/restock/quickstack) vs GUI races.

## 2. ChestTX network model

The chest manager is the ZDO owner. Ownership is NEVER handed over between
operations (exception: one-shot explicit acquire for upgrades — no loops).

- Request: `BestAutoSort_TxRequest(txId, body)` to the chest ZNetView with no target —
  the engine delivers it to the owner. Body: `[proto][op][baseRev][playerId][enforceRule][respectReserves][...]`.
- Response: `BestAutoSort_TxResponse(txId, status, revision, totalsOnly, body)`
  to the specific peer. Operation bodies are nested `ZPackage`s.
- Items: `[prefabHash][itemPkg][amount][x][y][maxStack]`; `itemPkg` holds
  `ItemData.Save` bytes (durability/quality/stack/variant/crafter/customData/cheated/world/gridPos).
  The manager resolves shared from the prefab by hash (like vanilla `Inventory.Load`).
  Live references across phases are forbidden: the manager re-resolves
  (cell + name + quality, fallback name + quality + variant + world).
- Per-chest queue, strictly one at a time: validate → apply → `Save()` →
  revision (`ZDO.DataRevision`) → response. All on the main thread (Valheim RPCs run there too).
- `txId = peerId<<32 | counter`. Idempotency: in-memory full-result
  cache (128) + persistent `(txId, acceptedTotal, revision)×32` ring
  in the ZDO. Retry = cache hit. Lost response = Query with the same txId.
  After handoff: Add returns totals from the ring; Take gets an equivalent
  redelivered from live state (conservation holds, the client pending-set
  drops late duplicates).
- The client touches its own inventory ONLY on response, exactly by accepted
  (ref with a name-scan fallback; shortfall is compensated back).
  Take with a full inventory auto-compensates back into the chest.
- Staleness: revision rides along in the request (`stale_revision` log), reject only
  when the resolve target vanished/changed. Viewer refresh polls
  `DataRevision` + compares `s_items` bytes (no redundant refreshes),
  reloading with the GUI open (the `UpdateContainer` transpiler:
  `IsOwner` → `ShouldRender`). Chest-sourced drags are cancelled on refresh
  (the item stays in the chest — no loss).
- Presence: ViewerOpen/Close + 5s heartbeat, 15s prune; the manager sets
  `s_inUse` (the lid). Takeover: the new owner always `Load()`s from the ZDO
  (committed state) + seeds the ring.
- Partial placement: `accepted = requested - remainder` per the
  placed/full-merge/partial rule (verified against `Inventory.AddItem`); the client
  removes exactly accepted.
- Timeouts: 3s, up to 4 attempts with the same txId, then 2× Query, then
  an explicit failure with refresh.

## 3. ChestTransactionService operations

- Add with a requested cell (drag&drop) goes strictly positional through the
  private `Inventory.AddItem(item, amount, x, y)` (like vanilla, no
  global merge and no fallback); without a cell — auto-place.
- Presence (ViewerOpen/Close) is fire-and-forget, no responses.
- EnforceLid counts the manager's own open GUI too.
- Quick-stack flights (`TxFlights`): after every committed AddBatch the manager
  broadcasts `(txId, chest, sourcePos, hash+amount list)` to all peers; receivers
  resolve icons locally and play `TransferVisuals.PlayFrom`. The originator is
  skipped by matching the session id in the txId high bits — order-safe both ways.

`Add / AddBatch / Take / TakeBatch / Move / Sort / Upgrade / SetRule`
(+ `ViewerOpen/Close`, `Query`). GUI, QuickStack, restock, prefetches,
loan returns, trash — all through them. Manager-local mutations go through
the same queue (`MutateLocal` + immediate drain).
Background work (server autofeed protocol, legacy migration, console commands) —
owner-only, synchronous (serial on the main thread).

## 4. Invariant

`TOTAL BEFORE + legitimate creation − legitimate consumption = TOTAL AFTER`
for any item. Covered by tests 1–12 + soak (see tests/ChestTx.Tests).

## 5. `[ChestTX]` logs (Debug → TxVerbose option)

- `container=<zdoid> tx=<id> peer=<id> op=ADD ... revision=123->124`
- `tx=<id> REJECT stale_revision client=122 server=124`
- `tx=<id> DUPLICATE returning cached result`
- `tx=<id> DUPLICATE handoff-redelivery accepted=N`
- `manager changed old=X new=Y revision=124`
No per-frame logging.

## 6. Changed/new files

- `src/BestAutoSort.TxCore/` (new): `TxProtocol`, `TxModel`, `ModelChest`, `TxCore`
- `tests/ChestTx.Tests/` (new): 12 tests + soak
- `src/Tx/` (new): `ChestTxService`, `ChestTxService.Client`, `TxState`,
  `TxCodec`, `TxInventory`, `TxReflect`, `TxNet`, `TxLog`
- `src/BestAutoSort.Patches/`: new `TxContainerAwake/Open/Ops/Render/Gui/DropOutside`;
  deleted `MultiUser*` (8), `ContainerStackResponsePatch`
- deleted `src/BestAutoSort.Runtime/MultiUserContainerService.cs`
- `Plugin` (Pump, Sort-tx, Upgrade-acquire, Reset),
  `QuickStackService` (batches), `RestockProfileService` (chain),
  `NearbyResourceService` (manager-only consume, prefetch, check gates, no-claim),
  `ProductionItemLoan` (return via tx), `InventoryButtons` (trash),
  `ChestRuleEditor` (SetRule tx), rule editor opening (direct),
  `ChestAuthority/StorageCommands` (TxReflect helpers),
  `ModConfig` (TxVerbose, AllowConcurrentChestUse description)
