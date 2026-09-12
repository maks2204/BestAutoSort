# ChestTX — authoritative transactional multiplayer для сундуков

## 1. Проблема исходной архитектуры

Мод (и ванилла) открывал/таскал/стакал через передачу ZDO ownership:
`SaveContainer → ForceSendZDO(peer) → SetOwner(peer)` + ожидание
(`CompleteAfterOwnership`: цикл до дедлайна + `WaitForSeconds(0.05f)`).
Следствия: закрытие чужого GUI (ванильный `UpdateContainer` рисует панель
только владельцу), replay по stale-снапшотам с хранением `ItemData`-ссылок
через сетевые задержки, `Trying to add item to occupied slot -1,-1` при
конкурентном `StackAll`, двойные выдачи при двойном TakeAll, гонки
фон (autofeed/auto-pull/craft/restock/quickstack) vs GUI.

## 2. Сетевая модель ChestTX

Менеджер сундука = владелец ZDO. Ownership НЕ передаётся между операциями
(исключения: timeout-recovery claim и явный structural acquire для апгрейда —
оба разовые, без циклов).

- Запрос: `BestAutoSort_TxRequest(txId, body)` на ZNetView сундука без target —
  движок доставляет владельцу. Тело: `[proto][op][baseRev][playerId][enforceRule][respectReserves][...]`.
- Ответ: `BestAutoSort_TxResponse(txId, status, revision, totalsOnly, body)`
  конкретному пиру. Тела операций — вложенные `ZPackage`.
- Предметы: `[prefabHash][itemPkg][amount][x][y][maxStack]`; `itemPkg` — байты
  `ItemData.Save` (durability/quality/stack/variant/crafter/customData/cheated/world/gridPos).
  Shared резолвится менеджером из префаба по хэшу (как ванильный `Inventory.Load`).
  Живые ссылки между фазами запрещены: менеджер резолвит заново
  (клетка + имя + качество, фолбэк имя + качество + вариант + world).
- Очередь на сундук, строго по одной: validate → apply → `Save()` →
  revision (`ZDO.DataRevision`) → ответ. Всё на main thread (RPC Valheim там же).
- `txId = peerId<<32 | counter`. Идемпотентность: in-memory кэш полных
  результатов (128) + персистентное кольцо `(txId, acceptedTotal, revision)×32`
  в ZDO. Повтор = кэш. Потерянный ответ = Query по тому же txId.
  После handoff: Add — итоги из кольца; Take — redelivery эквивалента
  из живого состояния (консервация соблюдена, клиентский pending-set
  отбрасывает поздние дубликаты).
- Клиент меняет свой инвентарь ТОЛЬКО по ответу, ровно на accepted
  (ref с фолбэком на name-scan; недобор — компенсация обратно).
  Take при полном инвентаре — автокомпенсация в сундук.
- Stale: revision везётся в запросе (лог `stale_revision`), reject — только
  когда цель резолва исчезла/изменилась. Refresh вьюверов — поллингом
  `DataRevision` + сравнение байт `s_items` (без лишних рефрешей),
  перезагрузка с открытым GUI (транспайлер `UpdateContainer`:
  `IsOwner` → `ShouldRender`). Drag из сундука при рефреше отменяется
  (предмет остаётся в сундуке — потерь нет).
- Присутствие: ViewerOpen/Close + heartbeat 5с, prune 15с; менеджер выставляет
  `s_inUse` (крышка). Takeover: новый владелец всегда `Load()` из ZDO
  (committed state) + сеет кольцо.
- Частичное помещение: `accepted = requested - остаток` по правилу
  placed/full-merge/partial (сверено с `Inventory.AddItem`); клиент снимает
  ровно accepted.
- Таймауты: 3с, до 4 попыток с тем же txId, затем 2× Query, затем
  явный фейл с рефрешем. Claim ownership — только по таймауту.

## 3. Операции ChestTransactionService

- Add с запрошенной клеткой (drag&drop) — строго позиционно через
  приватный `Inventory.AddItem(item, amount, x, y)` (как ванилла, без
  глобального мёржа и без фолбэка); без клетки — авторазмещение.
- Presence (ViewerOpen/Close) — fire-and-forget, без ответов.
- EnforceLid считает и собственный открытый GUI менеджера.

`Add / AddBatch / Take / TakeBatch / Move / Sort / Upgrade / SetRule`
(+ `ViewerOpen/Close`, `Query`). GUI, QuickStack, ресток, префетчи,
возвраты займов, trash — все через них. Менеджерские локальные мутации —
через ту же очередь (`MutateLocal` + немедленный drain).
Фоновое (autofeed-протокол сервера, legacy-миграция, консольные команды) —
только владельцем, синхронно (сериально main thread'ом).

## 4. Инвариант

`TOTAL BEFORE + legitimate creation − legitimate consumption = TOTAL AFTER`
для любого предмета. Проверяется тестами 1–12 + soak (см. tests/ChestTx.Tests).

## 5. Логи `[ChestTX]` (опция Debug → TxVerbose)

- `container=<zdoid> tx=<id> peer=<id> op=ADD ... revision=123->124`
- `tx=<id> REJECT stale_revision client=122 server=124`
- `tx=<id> DUPLICATE returning cached result`
- `tx=<id> DUPLICATE handoff-redelivery accepted=N`
- `manager changed old=X new=Y revision=124`
Никаких логов каждый frame.

## 6. Изменённые/новые файлы

- `src/BestAutoSort.TxCore/` (new): `TxProtocol`, `TxModel`, `ModelChest`, `TxCore`
- `tests/ChestTx.Tests/` (new): 12 тестов + soak
- `src/Tx/` (new): `ChestTxService`, `ChestTxService.Client`, `TxState`,
  `TxCodec`, `TxInventory`, `TxReflect`, `TxNet`, `TxLog`
- `src/BestAutoSort.Patches/`: new `TxContainerAwake/Open/Ops/Render/Gui`;
  deleted `MultiUser*` (8), `ContainerStackResponsePatch`
- deleted `src/BestAutoSort.Runtime/MultiUserContainerService.cs`
- `Plugin` (Pump, Sort-tx, Upgrade-acquire, Reset),
  `QuickStackService` (батчи), `RestockProfileService` (цепочка),
  `NearbyResourceService` (manager-only consume, префетч, гейты проверок, no-claim),
  `ProductionItemLoan` (возврат через tx), `InventoryButtons` (trash),
  `ChestRuleEditor` (SetRule tx), `ChestRuleEditor/Buttons` (прямое открытие),
  `ChestAuthority/ChestAuthority/StorageCommands` (TxReflect-хелперы),
  `ModConfig` (TxVerbose, описание AllowConcurrentChestUse)
