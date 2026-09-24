# Changelog

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
