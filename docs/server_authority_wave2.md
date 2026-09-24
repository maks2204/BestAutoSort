# Server-authority migration — wave 2 (manager routing)

Branch: `0.6.x-Dedicated-Test`. Builds on the wave-1 tree (policy/guards/
adoption/Hello/tests). Wave 2 replaces every IsOwner-as-manager branch for
chest paths with authority routing. The invariant itself (server owns ALL
eligible chests at ALL times) is still NOT claimed: live multi-peer
verification pending (waves 3-4).

## 1. Manager rule

`ServerAuthority.IsAuthorityManager(container)` (live) /
`TxAuthorityRouting.IsAuthorityManager(...)` (pure, test-pinned):

- LegacyDistributed mode OR unmanaged chest: vanilla `IsOwner` bit-for-bit.
- ServerAuthority + managed chest: true ONLY on the server process
  (`ZNet.IsServer()`) with the ZDO owned by the authority UID.
  Remote clients ALWAYS false; unknown authority (uid 0) manages nothing.

Helpers: `DenyRemoteDirectMutation` (loud fail-closed for direct-mutation
fallbacks), `BlockStructuralForRemote` (loud fail-closed for structural ops).

## 2. Per-path routing evidence

- Core tx (`src/Tx/ChestTxService.cs`): `IsManager` authority-routed;
  `Submit` / `RequestTakeCustom` / `SubmitCallDetailed` fast paths queue via
  `MutateLocal` only for the manager (host local ops serialize through the
  same manager queue; single-player keeps the sync local authority path with
  identical tx semantics); remote always goes over owner-routed
  `ZNetView.InvokeRPC` (owner == authority, unchanged transport).
- RPC (`OnTxRequest`): non-manager drops WITHOUT answering (no fabricated
  terminal outcome; sender resends/queries the SAME txId to the current owner).
- Host serialization: manager-side synchronous mutations drain the remote
  queue first (`DrainForLocal` before direct consume/loan/return/trash/sort
  paths); `MutateLocal` / structural entry points repair ownership first
  (`EnsureServerOwnership`, server-only inside) so a genuine host op is never
  mistaken for a remote attempt.
- Structural upgrade: `AcquireForStructural` + `Plugin.UpgradeOpenChest` +
  `ChestUpgradeService.{UpgradeOpenChest,TryReplaceChest,UpdateLegacyMigration}`
  fail closed (loud message + log) for non-managers — no client acquire, no
  replacement `ClaimOwnership`. Host path unchanged (already authority).
- Paths routed via `IsManager`: QuickStack (submit-skip, cascade, open-chest),
  Restock (`DrainSyncNeeds` vs tx chain), Craft/Consume (`ConsumeItem`,
  `CountAvailable` local-only, `PrefetchMissing`/`PrefetchBorrow`),
  production loans (`TryBorrow*`, `ProductionItemLoan` return), GUI
  (select/drag/click/drop-outside/right-click: manager drains then vanilla,
  remote always tx), sort (`SortLocal` manager-only, shared via `RequestSort`
  tx), rules (`SaveLocal` manager-only, remote via `SetRule` tx;
  `TryWrite` fail-closed message), trash (remote Takes by tx, manager destroys
  directly), reserves (`ChestReserveStore.Write` manager-gated), console
  (`StorageCommands.OpenChest` manager-gated), lease gate (`FeedBusy`).
- AutoFeed: scheduler stays animal-owner-side (animal `IsOwner` gates
  untouched); chest Takes go through server ChestTX (`RequestTakeCustom`,
  authority-routed); server never initiates feeding for non-owned animals
  (documented at `TryFeed`).
- Untouched by design: viewer rendering (`TxRender.ShouldRender`), own-claim
  verifications (post-`ClaimOwnership` rechecks), failed-shell cleanup,
  offline model seam (`TxCore` — LegacyDistributed bit-for-bit), sign patch.

## 3. Tests

`TxAuthorityRouting` (pure, production code) pinned by 6 new AUTHORITY tests:
`AUTHORITY_ManagerAuthorityMatrix`, `AUTHORITY_ManagerLegacyBitForBit`,
`AUTHORITY_ViewerNeverManager`, `AUTHORITY_StaleRouteDrop`,
`AUTHORITY_UpgradeFailClosed`, `AUTHORITY_RemoteNeverMutates`.

## 4. Results

- `dotnet build BestAutoSort.csproj -c Release`: 0 errors.
- Full suite: ALL TESTS PASSED (incl. 10 wave-1 + 6 wave-2 AUTHORITY tests).

## 5. What remains for waves 3-4

- Server-mediated structural path (remote upgrade currently refused, not
  serviced) — wave 2 chose fail-closed over a new protocol.
- Non-shared managed chests on the host still mutate directly after drain
  (queue-serialized, but not tx-enveloped); consider routing them through
  `MutateLocal` for full uniformity.
- Vanilla-client lockdown (documented-unsupported, not enforced) — unchanged.
- Dedicated-server scale: per-chest `Ensure` lookups on the 0.5 s pump.
- The invariant itself: live multi-peer verification (ownership never observed
  elsewhere, no unexpected-client-ownership counter growth under soak).
