# Server-mediated remote chest upgrade (0.6.x, honest-weak)

Date: 2026-09-24. Branch: `0.6.x-Dedicated-Test`.

## Relation to the NO-GO verdict

`structural-upgrade-nogo.md` refused the **exactly-once** variant: without a
public ZDO reserve/bind primitive, "same op never spawns two replacements"
is unachievable across the mint-link micro-window. This slice implements the
**honest-weak** variant instead, which keeps every NO-GO finding and narrows
the claim:

- No pre-reserved ZDOIDs (Valheim forbids them — unchanged).
- The mint-link window is covered by try/catch ghost cleanup for *caught*
  failures (exception ⇒ destroy ghost ⇒ op stays preparing).
- A *hard kill* between mint and link-write still leaves an unlinked ghost.
  That ghost is NEVER auto-destroyed (it could be a real new chest): both
  sides quarantine + loud log + manual reconcile. This is the documented
  residual, not a solved case.
- AfterLink crash (link written, op record not yet updated) is closed by
  reverse-link adoption: the resume adopts the already-linked ghost instead
  of minting a second one (pinned by `UPGRADE_CrashEveryPhase/AfterLink`).

## Protocol (server executes in its per-chest manager queue)

Op identity: source ZDOID + authenticated sender peer key + durable client
nonce (the fencing txId's counter half — `TxOp.UpgradeRequest.UpgradeNonce`).
Client sends a REQUEST; the server runs, synchronously with no yield:

spawn replacement → SYNCHRONOUS reverse-link write (op id + incomplete
flag) → copy inventory non-destructively → validate Ready → pointer switch
one-way → receipt → idempotent retire.

Phases: `PreparingLockedSource → SpawnedLinked → Copied → Ready →
PointerSwitched → Receipted → Retired` (`TxUpgradePhase`, pure core
`TxUpgradeManager` in `BestAutoSort.TxCore/TxUpgradeOp.cs`).

- Same op never spawns twice: the op record gates the mint (in-RAM ghost id
  first, durable reverse-link adoption second).
- Different ops queue behind the prepared head.
- Ingredients are pre-deposited in the SOURCE chest via normal ChestTX Takes.
  The op never charges the client inventory; the spend is recorded
  at-most-once (`RecordSpend` + synchronous ZDO spend flag) and refunds flow
  only as an op-keyed entitlement through the separate idempotent
  `ClaimRefund` step (dropped beside the new chest exactly once).
- Crash recovery resumes from self-consistent ZDO snapshots only
  (`TxUpgradeSnapshot.Validate`); torn snapshots fail closed.
- Version + authority-mode gate (`TxUpgradeGate`): old `TxOp.Upgrade` frames
  are REFUSED for managed chests in authority mode (ephemeral Rejected);
  `UpgradeRequest` requires the current protocol version. Host-local upgrades
  route through the same queue (same op machine, same receipt shape).
- UI: the old view closes up front, a locked-pending message shows, and the
  new view opens ONLY on a validated receipt (non-empty receipt + Accepted).
  A receipt-less Accepted (totals-only ring replay) is "completed but new
  chest unknown". A timeout re-queries the SAME txId/op — never a new nonce.

## Residuals (documented, not solved)

1. Hard-kill micro-window (mint → link-write): unlinked ghost persists,
   kept standing, op + ghost quarantined, manual reconcile required.
2. Copy-window duplication: between non-destructive copy and pointer switch
   the contents exist twice (source + ghost). A crash there is safe (spend
   recorded once, nothing credited to any player) but needs the resume to
   finish — an abandoned quarantine needs manual reconcile.
3. Totals-only ring replay loses the receipt (Accepted without a new-chest
   id): the client is told to check manually, never auto-opened.
4. Post-retire source ZDO is gone: the receipt lives in manager RAM + the
   ring entry, not in the world. A server restart after retire but before a
   client re-query answers from the ring (Accepted, totals-only shape).

## Validation

- `dotnet build BestAutoSort.csproj -c Release`: 0 errors.
- Full suite: ALL TESTS PASSED, incl. the new `TxUpgradeOpTests`
  phase-fault suite (crash before/after every phase incl. injected kill
  between mint and link-write; same-op no-second-spawn; contradiction
  quarantines both; conservation incl. spend/refund at-most-once;
  torn-snapshot fail-closed; legacy-frame refusal; queue-behind-prepared;
  timeout-queries-same-op).
