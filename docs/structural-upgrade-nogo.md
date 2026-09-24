# Structural upgrade: NO-GO verdict (verified)

> **Supersession note (0.6.0):** this verdict still stands for the
> *exactly-once* variant — pre-reserved replacement IDs remain impossible
> on the public Valheim API. The *honest-weak* variant (ghost protocol with
> quarantine-instead-of-delete, documented residuals) is now implemented in
> `src/Tx/TxRemoteUpgrade.cs` + `src/BestAutoSort.TxCore/TxUpgradeOp.cs`;
> see `docs/remote-upgrade-mediated.md`. Remote Upgrade is therefore
> serviced (weak contract), not refused, as of 0.6.0.

Date: 2026-09-24. Branch: `0.6.x-Dedicated-Test`. Assembly:
`valheim_Data/Managed/assembly_valheim.dll` (ilspycmd 11.0.0.9375).

## Verdict

Remote server-mediated structural chest upgrade is **not implementable**
with exactly-once guarantees on the public Valheim API. Remote Upgrade
stays fail-closed (`BlockStructuralForRemote`); host/server-local path
unchanged.

## Evidence (decompiled `ZDOMan`, independently re-verified by supervisor)

- `public ZDO CreateNewZDO(Vector3 position, int prefabHash)` mints
  `(m_sessionID, m_nextUid++)` with a collision loop, then calls the
  id-binding overload.
- `private ZDO CreateNewZDO(ZDOID uid, ...)` — **private**. It registers
  the ZDO in `m_objectsByID` immediately (network-visible; picked up by
  `SendZDOToPeers2` sync). No public reserve/bind API exists;
  `m_nextUid` is private with no peek/reserve accessor.
- Therefore "reserve ID → spawn at reserved ID → idempotent re-spawn"
  is unachievable: minting already publishes the ID, and a crash between
  mint and `Instantiate` leaves a visible ghost the protocol cannot
  distinguish from a committed replacement. Re-instantiating at the same
  ZDO after a crash has no public path.

## Consequence

The phased structural protocol (prepared → reserved → spawn → copy →
Ready → pointer-switch → receipt → retire) cannot meet its core
invariant ("same op never spawns two replacements") and is NOT built.
Remote structural upgrade remains refused fail-closed with a loud error
until Valheim exposes a reservation primitive or the design changes.
Host/server-local upgrades are unaffected (already authority).

## What would reopen this

- A public reserve/bind ZDO-identity API in a future Valheim build, or
- an accepted design that tolerates ghost shells with a validated
  adopt-or-destroy recovery keyed by op identity (requires its own
  design review; the ghost/duplicate-replacement hazard must be closed
  first, not assumed away).
