## 0.1.3

- Flight visuals broadcast (TxFlights) + manager-side play for remote batches.

## 0.1.9

- Simultaneous chest access highlight, protocol docs with diagrams, packaging fixes.

## 0.1.8

- Transport-safe batching, feeder on transactions, deferred placement, conservation gates.

## 0.1.7

- Pull-on-press crafting (no browse-time floods), chest-inclusive craft/build display, DoCrafting conservation gate.
- In-flight Add deduplication, drag/grid-inventory fixes, monotonic viewer, GearSlots tag handling.
- Verified against **Valheim 1.0.12**.

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
