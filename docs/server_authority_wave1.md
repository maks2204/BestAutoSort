# Server-authority migration — wave 1 (experimental vertical slice)

Branch: `0.6.x-Dedicated-Test`. This is an experimental slice, NOT a completed
release: the server-authority invariant is **not claimed yet**. Wave 2+ must
still close the gaps listed at the bottom.

## 1. What wave 1 does

- Canonical classification: `ServerAuthority.IsServerManagedContainer`
  (instance path) + same-rule ZDO/prefab policy
  `ServerAuthority.IsServerManagedChestZdo` (Container-less paths), both over
  the pure rule `TxServerAuthorityPolicy.IsEligiblePrefabName` (pinned by
  `AUTHORITY_*` tests). Stationary supported storage (`piece_chest*`) IN;
  Player / TombStone / wagon / ship / moving / temporary / special OUT;
  unknown prefab NOT eligible (fail closed). Independent of
  IsShared/lease/range/toggles by construction.
- Authority UID: `ZNet.GetUID()` on dedicated/host/single-player (equals
  `ZDOMan.GetSessionID()`); `GetServerPeer().m_uid` on remote clients;
  missing net layer / missing server peer => uid 0 (unknown authority, fail
  closed). Mode enum `ServerAuthorityMode`: `ServerAuthority` (default on
  dedicated/host/single) + `LegacyDistributed` (untouched legacy paths).
- Hello/version compat: wire hello is `"version;auth=N"`. Same version AND
  same mode required; legacy bare-version peers behave as LegacyDistributed.
  Mismatched peers are rejected (ephemeral tx refusal, no victim state
  change). **Vanilla (mod-less) clients are unsupported for managed chests**:
  the server grants them view-only access but cannot stop their client-local
  vanilla TakeAll (no RPC involved) from forking ghost items into their
  player inventory. Require the same mod version + mode on every peer.
- Source guards (all scoped to positively classified chests only):
  - `Container.RPC_RequestOpen`: access-checked grant WITHOUT `SetOwner` for
    EVERY eligible chest incl. lease/config-disabled (lease still denies with
    `RPC_OpenResponse(false)`).
  - `RPC_RequestStack` / `RPC_RequestTakeAll`: denied for eligible chests
    regardless of IsShared (stack/take-all run only through ChestTX).
  - `RPC_TakeAllResponse`: client-side ownership acquisition suppressed for
    eligible chests.
  - `ZDO.SetOwner` (backstop): for eligible ZDOs only moves TOWARD the
    authority UID are allowed (including release-to-zero blocked); unknown
    authority moves nothing. Covers RequestOpen/RequestStack `SetOwner`,
    redistribution, and any mod/client call.
  - `ZDO.SetOwnerInternal`: validates incoming ownership revisions at the
    `RPC_ZDOData` boundary (the `SetOwner` bypass).
  - `ZNetView.ClaimOwnership`: blocked for eligible chests unless the caller
    IS the authority (repairs + structural on server/host still work);
    every block logs + bumps `UnexpectedClientOwnership`.
  - `ZDOMan.ReleaseNearbyZDOS`: both `SetOwner` call sites rerouted through
    `ServerReleaseGuard.ConditionalSetOwner` (skip eligible chests narrowly).
    Patch-application verified (`PatchedSites == ExpectedSites == 2`); on IL
    change the patch fails and the `SetOwner` backstop still holds
    fail-closed.
  - GUI + local `TakeAll`/`StackAll`: eligible but outside the shared path is
    view-only for non-owners (loud deny, no ghost mutations against a stale
    replica); the owner keeps vanilla behavior.
- `EnsureServerOwnership` (server-only): attach/discovery (`OnContainerAwake`
  + slow-pump scan) and before the first authority op (`OnTxRequest`).
  Metadata-only change (ownership claim, never inventory). Reads persisted
  s_items + ring/floor BEFORE mutations; missing/corrupt/untrusted state
  quarantines (never presumed empty). A new server-created chest may
  materialize an empty s_items blob ONLY in the verified awake/init context
  (self-owned at awake + provably empty RAM + verified re-read) — never
  timer-based. Logs ZDOID/old/new/reason + counters
  (`ServerOwnershipRepairs` / `UnexpectedClientOwnership` /
  `OwnershipRepairFailures`). Reseed stays owned by the existing
  Takeover/Takeover-before-Drain path.
- AutoFeed: unchanged. Animal-owner scheduling kept (`IsOwner` gate);
  server/host never initiates feeding for non-owned animals; no creature
  ownership change anywhere in the new code.
- Kept: actor-first auth, canonical keys, ring/floor, quarantine,
  LegacyDistributed paths untouched; Takeover/LastOwner/TxNullEscape
  retained (legacy only for now; NOT deleted).

## 2. Call graph patched

- `TxContainerOpenPatch` (grant-without-owner for eligible, lease deny kept)
- `TxContainerOpsPatch` (4 prefixes: TakeAll/StackAll view-only,
  RequestStack/RequestTakeAll deny regardless of IsShared)
- `TxGuiPatch` (4 prefixes) + `TxGuiDropOutsidePatch` (view-only gates)
- NEW `ServerAuthorityPatches`: `ZdoSetOwnerGuardPatch`,
  `ZdoSetOwnerInternalGuardPatch`, `ClaimOwnershipGuardPatch`,
  `ReleaseNearbyGuardPatch` (transpiler), `ServerAuthorityTakeAllResponsePatch`
- `ChestTxService.{OnContainerAwake,OnTxRequest}` + `PumpManagerSlow`
  (Ensure hooks), `TxNet` (hello), `ModConfig` (mode entry),
  `Plugin.PatchAllSafely` (release-guard verification log)
- NEW `Runtime/ServerAuthority`, NEW `TxCore/TxServerAuthority` (pure policy)

## 3. Tests

`tests/ChestTx.Tests/TxAuthorityPolicyTests.cs` (production TxCore code):
`AUTHORITY_StationaryChestsEligible`, `AUTHORITY_PlayerExcluded`,
`AUTHORITY_TombstoneExcluded`, `AUTHORITY_MovingExcluded`,
`AUTHORITY_UnknownFailClosed`, `AUTHORITY_CaseRules`,
`AUTHORITY_DenylistBeatsAllowlist`, `AUTHORITY_ModeCompatMatrix`,
`AUTHORITY_HelloNegotiation`, `AUTHORITY_HelloLegacyFallback`.

IL verification (offline harness, real `assembly_valheim.dll` + real
`0Harmony.dll` + shipped `BestAutoSort.dll`): `ReleaseNearbyZDOS` contains
exactly 2 × `callvirt ZDO::SetOwner(int64)` (offsets `0x0083`, `0x00B1`);
the transpiler rerouted 2/2 with no errors.

## 4. Results

- `dotnet build BestAutoSort.csproj -c Release`: 0 errors.
- Full suite (`dotnet run -c Release` in `tests/ChestTx.Tests`):
  ALL TESTS PASSED (incl. the 10 new AUTHORITY/Hello tests).

## 5. What wave 2+ must still do

- Client-initiated structural ops (upgrade / `AcquireForStructural`) under
  ServerAuthority: client claims are now blocked, so upgrades from a
  non-owner fail gracefully — needs a server-mediated structural path.
- tx submission while concurrent use is disabled: eligible non-shared chests
  are view-only for non-owners (no behavior change to the toggle by design).
- Vanilla-client chest lockdown (deny opens to unknown peers) — currently
  documented-unsupported, not enforced (join-grace UX tradeoff open).
- Dedicated-server scale: per-chest `Ensure` scene/prefab lookups on the
  0.5 s pump (correctness first, profiling later).
- The invariant itself (server owns ALL eligible chests at ALL times,
  ownership never observed elsewhere) is explicitly NOT claimed: live
  multi-peer verification pending.
