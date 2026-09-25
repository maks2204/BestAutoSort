# Changelog

## 0.6.9 (virgin veto refinement)

- Virgin veto refined: only committed ring entries (Accepted/Partial/
  Duplicate via TxRing.IsCommittedStatus) block; Rejected-only rings and
  floor keys no longer self-poison virgin chests. Corrupt rings still veto.


## 0.6.8 (virgin init, submit truth, honest Take diagnostics)

- Virgin-chest initialization: ownerless s_items-null with no ring/floor
  history materializes a vanilla-empty blob and serves (new chests usable);
  any history/live owner keeps the quarantine.
- SEND line carries items=/expected= submit truth (open AddBatch arity case).
- Answer() logs takes= blob count for Take ops instead of a fake int sum.


## 0.6.7 (remote flow visuals, guard no-op quiet, arity diagnostics)

- Server broadcasts TxFlights after Add/AddBatch/TakeBatch commits: watchers
  see other players' quick-stack icons (same packet, ZDO-sourced).
- Ownership guards pass through no-op changes (owner==requested) silently;
  real changes still blocked + counted.
- Arity diagnostics: client logs expected/bodyLen/bodyFirst on add-body
  mismatch; server logs op/acceptedTotal per response (open AddBatch case).


## 0.6.6 (Move/Sort served, settle removed)

- Server manager serves Move/Sort (pure-inventory shared Execute paths).
- Settle-HOLD fully removed: requests execute immediately (strict parity with
  the production Takeover path; the live-owner gate already excludes
  concurrent writers before execution). Zero open-delay by construction.
- Single-writer owner gate (code shipped in 0.6.3, documented here): serve
  only owner==0/self/dead-peer, else ephemeral Rejected; ownership never mutated.

## 0.6.5 (ZDO-level ward evaluation)

- Server access gate evaluates wards from ZDO data (sector scan, s_enabled,
  prefab m_radius, s_creator + pu_id list); no covering ward allows exactly
  like vanilla. Ward-opted fail-closed removed.

## 0.6.4 (server transport framing fix)

- KindTx byte on server request/response outer frames (was misparsed as kind,
  all tx traffic silently dropped; ping/pong unaffected).


## 0.6.3 (unreleased: option-B server manager, slices 1-3)

- Slice 1 (transport): instance-independent global RPCs `BestAutoSort_TxServerRequest/Response` (ZDOID.None dispatch, Hello precedent); client ping every 60s, first pong logged unconditionally. Zero GameObjects needed.
- Slice 2 (sessions + codec): ZDO-keyed `ServerChestSession` (dims from prefab Container, quarantine-first); decode/encode via vanilla `Inventory.Load/Save` (byte-identical by construction); `zdo.Set` auto-bumps revision; restart recovery via durable ring/floor reseed.
- Slice 3 (manager): ZDO-only Take/Add(+Batch) through the SHARED `ExecuteCall` (rule/reserve ZDO overloads, `ExecZdo` context); shared idempotency (Processed/ProcOrder/Floor), durable ring/floor, quarantine matrix, spoof/binding gates mirrored; access mirrors `ChestAuthority.CanUse` (ZDO/prefab sources); new sessions HOLD 8s (rev-re-armed) with deferred answers; unsupported ops -> persisted Rejected; legacy chests never answered (silent drop).
- Residuals: Move/Sort/Upgrade/SetRule on dedicated stay Rejected; ward-opted chests fail closed; slice-4 freshness barrier pending (settle narrows, does not close, the proven ClaimOwnership race); m_privacy read from prefab.


## 0.6.2 (unreleased: Awake-independent authority discovery sweep)

- Server-only discovery sweep every 15s (`PumpAuthoritySweep`): live managed chests missing from `States` (Awake edge never fired — late zone activation on dedicated servers) get the exact Awake registration (RPCs + state + `EnsureOnAwake` adoption). Adoption stays metadata-only + quarantine-first; the sweep path skips the verified-init materialization (`fromSweep`, timer paths NEVER materialize holds literally); unconditional log on change + 5-min heartbeat (`discovery sweep found=/managed=/registered=`).
- Known residual (proven by decompilation, fix deferred): `ClaimOwnership` is a local flag flip with no flush handshake — the server is not guaranteed the ex-owner's latest state at adoption. Mitigations active: quarantine on null/invalid, owner-gated writes, `SetOwnerInternal` guard; freshness handshake (ForceSendZDO + DataRevision compare) is the planned second stage.

## 0.6.2-DIAG (temporary dedicated-server silence diagnostics, revert after diagnosis)

- Temporary verbose probes only, no behavior change: `[ChestTX] config:` startup dump (TxVerbose/mode/effective), `authority awake probe` at EnsureOnAwake entry, `container awake: <name>` at OnContainerAwake entry. Purpose: prove whether Container.Awake/adoption runs at all on the dedicated server (live symptom: client SENDs, zero server ChestTX lines, every op Indeterminate). Revert before any release.

## 0.6.1 (experimental: full review-loop fixes on 0.6.x)

- Review-loop hardening: dead authority seams wired into production (remote-mutation ban, stale-route drop, structural single rule); quarantine gates on craft consume/loan and SortLocal paths (incl. priority loan); manifest 0.6.x packaging consistent; README experimental banner + wire/ops table; threat model stated; verification table narrowed to seam-confirmed with wiring-assumed + transient C15 row.
- Residuals unchanged from 0.6.0 (live multi-peer traces outstanding).

## 0.6.0 (experimental: server-authoritative chests, invariant NOT claimed pending live multi-peer verification)

- Server-authority migration (waves 1-3): canonical eligible-chest policy, authority UID, ServerAuthority (default) + LegacyDistributed modes with Hello/mode compat; source guards (open/stack/take-all without SetOwner, ReleaseNearbyZDOS exclusion, RPC_ZDOData boundary, ClaimOwnership block); server-only metadata adoption with quarantine-first; IsAuthorityManager routing for every mutation path with stale-route drop; host queue-serialized; remote Upgrade fail-closed; timer null-heals disabled in authority mode; ForceSendZDO documented as viewer-propagation hint.
- Server-mediated remote chest upgrade (`TxOp.UpgradeRequest`, honest-weak): ghost protocol (spawn -> sync reverse-link -> copy -> Ready -> one-way pointer switch -> receipt -> idempotent retire); op-keyed entitlements; legacy direct-upgrade frames refused. Residuals in `docs/remote-upgrade-mediated.md`.
- Residuals: live multi-peer traces outstanding; client-crash/ACK/eviction/post-write/SetRule/Upgrade-structural/response-trust gaps unchanged from v0.5.x; vanilla clients unsupported for managed chests.

## 0.6.x-Dedicated-Test remote upgrade (experimental slice, honest-weak)

- Server-mediated remote chest upgrade (`TxOp.UpgradeRequest`): client sends a REQUEST (source ZDOID + authenticated sender + durable client nonce = fencing txId counter half + tier); the server executes the ghost protocol in its per-chest manager queue (pure core `TxUpgradeManager`/`TxUpgradeGate` in `BestAutoSort.TxCore/TxUpgradeOp.cs`, production host in `src/Tx/TxRemoteUpgrade.cs`). Spawn -> synchronous reverse-link write -> non-destructive copy -> validate Ready -> one-way pointer switch -> receipt -> idempotent retire; same op never spawns twice (record gate + reverse-link adoption); different ops queue behind the prepared head.
- Ingredients pre-deposited in the source chest via normal ChestTX Takes; the op never charges the client inventory; spend recorded at-most-once, refunds only as an op-keyed entitlement via the idempotent ClaimRefund step. Old `TxOp.Upgrade` frames refused for managed chests in authority mode; host-local upgrades route through the same queue. UI closes the old view, shows locked-pending, and opens the new view only on a validated receipt; timeouts re-query the same op.
- Residuals (documented in `docs/remote-upgrade-mediated.md`): hard-kill mint-link micro-window => unlinked ghost kept standing + quarantine + manual reconcile; copy-window duplication safe-direction; totals-only ring replay loses the receipt (manual check, never auto-open).

## 0.6.x-Dedicated-Test wave 1 (experimental slice, invariant NOT claimed)

- Server-authority migration, vertical slice: canonical eligible-chest policy (stationary storage in, player/tomb/wagon/ship/moving/unknown out), authority UID (server GetUID / client server-peer uid, unknown fail closed), ServerAuthority (default) + LegacyDistributed modes with Hello/mode compat negotiation (mismatched peers rejected; vanilla clients unsupported for managed chests).
- Source guards for eligible chests only: access-checked open grant without SetOwner (lease still denies); request-stack/take-all denied regardless of IsShared; TakeAllResponse claim suppressed; ZDO.SetOwner/SetOwnerInternal ownership backstop (toward-authority only); mod/client ClaimOwnership blocked with diagnosis; ReleaseNearbyZDOS redistribution exclusion via verified transpiler (2/2 sites, fail-closed SetOwner backstop on IL change); GUI/TakeAll/StackAll view-only for non-owners outside the shared path.
- Server-only EnsureServerOwnership (awake/discovery/tx-request hooks, metadata-only, pre-mutation s_items + ring/floor read, quarantine on untrusted, verified-init empty-blob materialize only, ZDOID/old/new/reason logs + repair counters). AutoFeed untouched (animal-owner scheduling, no creature ownership change). Takeover/LastOwner/TxNullEscape retained.

## 0.5.15

- s_items load-gate hardening: null is never empty, blobs validated before Load with RAM-snapshot restore, defensive byte clones, vanilla handoff order.

## 0.5.14

- Transient send-path one-way invariant: no unflagged fallback after transient state; flagged Query-or-Wait with Pending/claims retained.

## 0.5.13

- Authoritative transient-retry flag: flagged requests never execute as fresh, even on a clean new manager; clean-handoff proof.

## 0.5.12

- Transient same-tx retry (non-terminal) with bounded backoff; crash-resistant counter blocked marker.

## 0.5.11

- Quarantine durable-terminal matrix; persistent counter block surviving restart; replay op-mismatch guard.

## 0.5.10

- Quarantine fence-first, counter fail-closed, replay op guard, fence propagation requests.

## 0.5.9

- Wave-1 isolation + v3 hardening: ephemeral spoof, quarantine fencing, drain/claims lifecycle, drag gate, atomic counter.

## 0.5.8

- Authoritative inventory reload + quarantine for post-fence failures.

## 0.5.7

- Durable pre-execution floor fence before every fresh mutation; crash-safe tx ordering.

## 0.5.6

- Canonical peer identity (negative UID support), durable floor, corrupt-ring degraded recovery.

## 0.5.5

- Persistent ring v2 with stable terminal outcomes across handoff; UnknownTx is Indeterminate.

## 0.5.4

- Ring includes the committed tx before handoff; UnknownTx no longer treated as committed.

## 0.5.3

- Eggs quick-stackable behind TreatEggsAsAutoStack (default on); quickstack empty-diagnosis logging; issue #10 listen-server actor-first authority fix.
- Quick-stack duplication race fix (also on branch): ExecuteAdd credits clamped to the live source stack.

## 0.4.0

- Legacy chests no longer shrink a larger runtime inventory applied by other mods (final = max(current, template), issue #8).
- Eating food from a shared chest as non-owner no longer pulls the whole stack into inventory (take 1 unit, issue #6).

## 0.3.2

- Staged-stock return no longer breaks crafting (craft stages untracked) and no longer loops on upgrades (recent-upgrade keep window).

## 0.3.1

- Deferred building pipelines the next set (no more multi-click starvation); unused staged stock returns to chests on selection change, hammer put-away, or late landing.

## 0.3.0

- Absolute tx idempotency (no re-apply after handoff), retry-or-loud client, no-void compensation.
- Owned-chest persistence everywhere (restock, loans, quick-stack into open, trash, sort, replacement).
- Deferred builds and upgrades consume chest stock; quest items never leak via TakeAll/drop/right-click.
- Shared-mode deny for direct TakeAll RPC; lease-safe chest open; drag-take lands in the drop cell.

## 0.2.0

- Feeder flights target the animal, not the player.
- Upgrade button stages chest stock and consumes owned-chest resources; replacement chest contents persisted (no more ghost items).
- Chest buttons narrowed to Trash/Stack width; Sort Chest renamed to Sort.

## 0.1.10

- BepInExPack 5.4.2350, simultaneous-access highlight, protocol docs.

## 0.1.9

- Simultaneous chest access highlight, protocol docs with diagrams, packaging fixes.

## 0.1.8

- Transport-safe batching, feeder on transactions, deferred placement, conservation gates.

## 0.1.7

- Pull-on-press crafting (no browse-time floods), chest-inclusive craft/build display, DoCrafting conservation gate.
- In-flight Add deduplication, drag/grid-inventory fixes, monotonic viewer, GearSlots tag handling.
- Verified against **Valheim 1.0.12**.

## 0.1.6

- Wire protocol v2 (client-stamped actor position), build commit hash baked into the DLL.

## 0.1.5

- Quick-stack outcome visibility and skip diagnostics, build tag in startup log.

## 0.1.4

- Owner quick-stack through the tx queue (broadcasts for all), quick-stack entry logging.

## 0.1.3

- Flight visuals broadcast (TxFlights) + manager-side play for remote batches.

## 0.1.2

- Fix response body framing (nested package was read flat: Adds silently applied 0, Takes crashed) and short presence/query bodies (heartbeats died, lid flapped).

## 0.1.1

- Wire protocol fix (ProtoVersion header) + version gate: mixed 0.1.0/0.1.1 peers are rejected loudly instead of silent garbage.

## 0.1.0

- Rebrand to BestAutoSort (`dev.maks2204.bestautosort`): new GUID, name, version, own plugin folder.
- Fresh config `dev.maks2204.bestautosort.cfg`, no legacy imports.
- Console command renamed to `bestautosort`.
- Full storage feature set (quick-stack, restock, rules, crafting/fuel from chests,
  autofeed, shared chests, upgrades, sort, trash), no ModCore dependency.
- Own save-data keys, no compatibility with other storage mods by design.
