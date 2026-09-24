# Server-authority migration — wave 3 (fail-closed timers off, no client handoff)

Branch: `0.6.x-Dedicated-Test`. Builds on the waves 1–2 tree (policy/guards/
adoption/Hello/manager-routing/tests). Wave 3 closes the timer-based
assume-empty heals and the client-handoff paths for managed chests in
ServerAuthority mode. The live multi-peer invariant verification itself
(ownership never observed elsewhere under soak) remains wave 4.

## 1. Authority architecture (the invariant waves 1–3 enforce)

- The server/host owns EVERY eligible chest ZDO at ALL times
  (`ServerAuthority.EnsureServerOwnership`: attach/discovery repair + slow-pump
  scan + before-first-authority-op repair; `ZDO.SetOwner` backstop moves only
  TOWARD the authority UID; `ClaimOwnership` blocked for non-authorities;
  `ReleaseNearbyZDOS` skips eligible chests; open/stack/take-all acquisition
  suppressed for eligible chests).
- Clients NEVER own and NEVER mutate eligible chests. Every chest mutation —
  local GUI, automation, quick-stack, restock, craft/consume, loans, sort,
  rules, trash, reserves, console — routes through ChestTX (`Submit` /
  `MutateLocal` / tx ops), and the manager verdict is authority-routed
  (`IsAuthorityManager`: true ONLY on the server with the ZDO owned by the
  authority UID; remote clients ALWAYS false, never falling back to direct
  vanilla mutation — `DenyRemoteDirectMutation` fails closed loudly).
- ChestTX serializes AT THE SERVER: the manager-side queue (`Drain`) runs on
  the server only; owner-routed RPCs are answered only by the manager
  (stale-route drop without a fabricated outcome).
- `ForceSendZDO` (`RequestPropagation` / `TryForceSendZdo`) is VIEWER-ONLY: a
  best-effort refresh hint so viewers poll the new `DataRevision` sooner. It
  never transfers ownership, never carries mutation, and never hands a chest
  to a client — the manager stays the server before AND after every push.
- NO client handoff exists in ServerAuthority mode. `Takeover` runs server-side
  only (the initial adoption reseed after `EnsureServerOwnership`); remote
  adoption is refused (`TxAuthorityRouting.TakeoverAllowed`, pump gate), while
  `LastOwner` tracking still advances so the tripwire does not spin.

## 2. What wave 3 changes (ServerAuthority mode only)

1. Timer-based assume-empty heals DISABLED for managed chests
   (`TxNullEscape.EscapeAllowed(authorityMode, managed)` — pure, test-pinned):
   - `false` for authority + managed: BOTH the 30 s dead-source leg
     (`ShouldEscape`) and the 150 s live-quiescence leg
     (`ShouldEscapeLiveQuiescent`) are inert. A server-owned chest with null
     `s_items` stays fail-closed quarantined with a LOUD diagnose
     (chest/oldOwner/liveness/elapsed + "manually reconcile") — no timer exit
     at any elapsed time, for either liveness value. The server IS the
     authority, so there is no dead source to wait out and no live source to
     converge with; healing empty would risk cementing empty over
     unreplicated state.
   - `true` everywhere else: LegacyDistributed legs run bit-for-bit unchanged
     (all existing `TxNullEscapeTests` pass untouched), as do unmanaged chests
     under ServerAuthority.
   - Defense-in-depth backstop: `TryHealNullSItems` refuses managed chests
     outright (loud log), so no future caller can timer-heal one even past
     the gated legs.
2. Verified-init exception STAYS (the single authority-mode heal):
   `TxNullEscape.InitMaterializeAllowed` (pure, test-pinned) — a NEW
   server-created chest (self-owned at awake + `s_items` absent + live RAM
   provably empty) still materializes its empty blob via `SaveContainer` with
   re-read + validated-`TryLoad` verification. `EnsureOnAwake` consults THIS
   predicate (same function the suite pins). Timer-based paths NEVER
   materialize (fail closed there).
3. Takeover gated (`TxAuthorityRouting.TakeoverAllowed`, pure, test-pinned):
   authority + managed + remote reads false (loud pump Warn, `LastOwner`
   still advances); every other combination reads true. The
   `OnTxRequest` Takeover-before-Drain leg needs no new branch: remotes never
   reach it for managed chests (the `IsManager` early return drops stale
   routes first) — documented at the call site.
4. `DropTransientFor` / `SeedFromRing` transient-RAM paths documented as
   inert-by-construction in ServerAuthority mode (ownership never moves, so
   the loss/handoff legs never fire for managed chests; remotes never enqueue
   so the `Drain` early-out drop is a no-op for them). Kept untouched for the
   LegacyDistributed ownership-loss/handoff paths. Each site carries a
   `Wave-3:` comment stating exactly why no branch was added.
5. Comments describing remote-client managers qualified for the new regime
   (legacy text preserved, mode scope added): `ChestTxService` network-model
   header (manager = owner MEANS manager = server; serialization AT THE
   SERVER; handoff legacy-only), `ForceSend` docs (viewer-only, never
   ownership/handoff), `TxState` quarantine docs (escape legacy-only),
   `TxNullEscape` class docs, pump/`OnTxRequest`/drain/reseed sites, and the
   two `ServerAuthority` comments that still named the null-escape legs as
   the quarantine exit.

## 3. Tests

`TxAuthorityPolicyTests` (production TxCore code), 5 new AUTHORITY tests:
`AUTHORITY_EscapeDisabledInAuthorityMode` (gate closed for authority +
managed across 4 elapsed × 2 liveness points incl. the exact fire points of
both legs; open everywhere else), `AUTHORITY_EscapeKeptInLegacy` (gated
combinations in legacy answer exactly as before), 
`AUTHORITY_QuarantinePersistsOnNullAuthority` (gate closed at 8 elapsed points
from 0 to `double.MaxValue` — quarantine has no timer exit),
`AUTHORITY_VerifiedInitStillMaterializes` (init predicate true ONLY for the
fully verified shape; 6 refusal shapes), `AUTHORITY_TakeoverGateNeverRemote`
(full 2×2×2 matrix minus duplicates: server adopts, remote refused, legacy /
unmanaged always allowed). Plus the new pure API `TakeoverAllowed` /
`EscapeAllowed` / `InitMaterializeAllowed` (no Unity refs, same rule the game
consults).

## 4. Results

- `dotnet build BestAutoSort.csproj -c Release`: 0 errors.
- Full suite: ALL TESTS PASSED (incl. 10 wave-1 + 6 wave-2 + 5 wave-3 AUTHORITY
  tests and the unchanged NULLESCAPE legacy-leg pins).

## 5. Residual for wave 4 (live traces, NOT covered here)

- Live multi-peer verification of the invariant itself (server owns ALL
  eligible chests at ALL times; `UnexpectedClientOwnership` stays flat under
  soak; quarantined-managed-null reconcile drill on a dedicated server).
- Server-mediated structural path (remote upgrade still refused fail-closed,
  not serviced) — carried over from wave 2.
- Vanilla-client lockdown (documented-unsupported, not enforced) — unchanged.
- Dedicated-server scale: per-chest `Ensure`/`IsServerManagedContainer`
  lookups on the 0.5 s pump (wave 3 adds one managed-classification call per
  quarantined-null reload plus the existing per-pump call).
