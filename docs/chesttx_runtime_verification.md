# ChestTX runtime verification checklist (v0.5.x)

What the offline suite PROVES vs what still needs a live game session.
Status words are used strictly:

- **confirmed** — pinned by an offline test against production-shared code
  (`BestAutoSort.TxCore` seam or codec). Breaks the build if it regresses.
- **observed** — production-only path (Unity/ZDO/RPC); verifiable in a live
  session via logs/diagnostics. NOT proven offline.
- **assumed** — taken on trust from the engine/modding API; a wrong assumption
  is a bug source. Listed so it can be challenged.
- **unknown** — runtime-required: static inspection cannot answer it.

## 1. Durability vocabulary (used in code comments)

- **local-write**: `ZDO.Set` / `Container.Save` updated the manager's copy.
- **propagation-request**: `TryForceSendZdo` asked the net layer to push now.
- **observed**: a viewer polled a newer `DataRevision`/`s_items`.
- **world-save**: the server persisted the ZDO to the world file.
- **reciprocal**: the client applied its matching inventory change.

`RequestPropagation` is ONLY a propagation-request: best-effort, never an
ACK/barrier, never proof of observed/world-save/reciprocal.

## 2. Ring vs floor roles (two separate ZDO keys, non-atomic across keys)

- **Ring**: immutable committed OUTCOME records (txId, status, accepted totals,
  revision, sender key; v3). Answers replays/Queries with the ORIGINAL outcome
  so a txId never flips across handoff. Cap 32, oldest evicted.
- **Floor**: per-sender execution HIGH-WATER (peer key → highest counter seen).
  UNBOUNDED, never evicted, never refusing. An absent txId at/below its
  sender's high-water is Indeterminate and must NEVER execute. Max-merged
  across copies (either crash order safe).

Torn generations (newer ring + older floor or vice versa) are ROUTINE, not
corruption. A present-but-corrupt floor is validated-then-ignored in favor of
the ring-embedded copy; a corrupt ring with no trustworthy floor fails fully
closed (degraded recovery only with an intact independent floor).

## 3. Delivery order (no-FIFO)

The engine does NOT guarantee request/response/Query/presence arrival order.
Nothing depends on it: every request carries its full identity (txId); every
outcome is idempotent (Processed-cache replay + per-sender floor gate +
Query-by-txId). A reordered duplicate is a cache hit; a reordered first-timer
at/below the floor is Indeterminate, never executed.

## 4. Static-inspection record: Save / ForceSendZDO (item f)

Proven by static inspection (repo code only — no game assemblies shipped here):

- `TxReflect.SaveContainer` → `AccessTools.Method(typeof(Container), "Save")`
  invoked with zero args. It is called on the commit path (`CommitResult`,
  only when the result mutated), on structural paths, and by unrelated
  runtime services (trash/upgrade/restock/quickstack). What `Container.Save`
  writes internally (s_items bytes vs other keys), whether it flushes, and its
  throw surface are **unknown** (runtime-required): the protocol treats every
  post-fence save as ambiguous (reload-or-quarantine, never presumed).
- `ZDOMan.instance.ForceSendZDO(uid, view.GetZDO().m_uid)` — the ONLY
  attested signature shape: first arg a peer uid (long), second the ZDO uid.
  Attested call site: `TxContainerOpenPatch` (open-grant push to the opener).
  `TryForceSendZdo` reuses EXACTLY this shape. No overloads invented.
- Recipients are collected per call (`CollectPropagationRecipients`): the
  requesting sender + the presence `Viewers` set (same uid domain: presence
  keys are the sender uids the RPC layer reports) + the server peer where valid,
  deduped, skipping 0 tags and self. Viewers-only was INSUFFICIENT (a requester
  whose presence never registered, and the world-save authority, would miss the
  push) — the collector, not Viewers alone, is the contract.
- Firing order (shared with the offline core via the `TxDurability`
  `PropagationRequest` seam, pinned by the PROPAGATION_* tests): fence floor
  write → propagation-request AfterFence → Execute/save → ring + floor rewrite
  → propagation-request AfterCommit. Quarantined txIds fence + propagate
  AfterFence with no execute. `RequestPropagation` calls exist at CommitResult
  (with the committer), the ApplyJob pre-execute fence, the fence re-persist,
  the quarantine fence points, and takeover/structural reseeds — all are
  propagation-requests only (best-effort, never an ACK/barrier).

Runtime-required (unknown): ForceSendZDO delivery timing, whether it survives
ownership transfer races, server-side batching/throttling, exact `Save`
write set and failure modes. None of the correctness arguments depend on them.

## 5. Checklist

### Confirmed (offline suite)

| # | Claim | Pinned by |
|---|-------|-----------|
| C1 | Trust gate (sender/txId binding) runs before any fence/cache/ring mutation; spoof is ephemeral, victim txId never burned, no payload leak; sender-0 legacy documented (fresh commits, matching-op resends/queries replay, cross-op still refused) | SPOOF_VictimTxIdNotBurned, SPOOF_IncompatiblePeerCannotRaiseVictimFloor, SPOOF_CachedResultNotLeaked, SPOOF_SenderBindingSeam, FENCE_SpoofNeverFences, SENDER0_LegacyFreshAndQueryDocumented |
| C2 | Quarantine terminal is Indeterminate on remote/local/queued paths; every quarantined txId is durably FENCED first (never executed/cached) and stays stale-gated after recovery — retry as a NEW txId, never the same one; queued fresh txIds never resurrect; transport-duplicated stale copies stay Indeterminate | QUARANTINE_Add, QUARANTINE_TakeDelayedDuplicateAfterRecovery, QUARANTINE_QueuedLocalRemoteShareTerminal, QUARANTINE_HandoffDropPersistenceFailure, QUARANTINE_FreshAddTerminalNeverExecutesAfterRecovery, QUARANTINE_TakeTerminalNeverExecutesAfterRecovery, QUARANTINE_QueuedFreshTxNeverResurrects |
| C3 | Pending drain never mutates the map while enumerating; one callback attempt per terminal; destroyed-container finalizes loudly | PENDING_DecideMatrix, PENDING_MultipleTimeoutsOnePump, PENDING_TerminalCallbackAndClaimsExactlyOnce |
| C4 | Claim lease: duplicates pruned, pre-ownership failures release, terminal releases before its callback | CLAIM_DuplicatePruned, CLAIM_PrePendingFailuresRelease, CLAIM_TerminalReleaseExactlyOnce |
| C5 | Drag remainder restored only on known-safe dispositions with seam arithmetic | DRAG_Rejected, DRAG_Partial, DRAG_Indeterminate |
| C6 | Fence-first order + crash windows (A/B/C/ring/floor), rejected-persist fail, ownership loss, degraded corrupt-ring | TxFenceCrashTests group |
| C7 | Dirty-RAM reload/quarantine discipline; committed-but-Indeterminate stated residual | TxDirtyRamTests group |
| C8 | Counter file: legacy parse, v1 round-trip, checksum/torn rejection, backup-aware resolve, crash-resistant write + restore, uint-boundary fail-closed; explicit loader states (Fresh/ResumedPrimary/ResumedBackup/FailClosed); missing primary still consults backup; evidence-with-nothing-trustworthy refuses issuance (never restarts at 1) | HARDENING_Counter* (7 tests, incl. HARDENING_CounterLoadStatesFailClosed) |
| C9 | Take on an empty chest is stable persisted Rejected (known-not-committed), never Indeterminate/debit | HARDENING_EmptyChestTakeRejected |
| C10 | Null persisted authority keeps quarantine; nothing presumed empty; reload-only clearing | HARDENING_SItemsNullNeverPresumedEmpty |
| C11 | Out-of-order first-timer at/below floor is Indeterminate, never executes | V2_OutOfOrderDelayed, TxTests floor case |
| C12 | Cross-op replay guard: cached.Op != incoming mutation op answers Indeterminate with no payload, never executes, cache untouched; Query-by-txId exempt by design (the lost-response path); sender-0 mismatched-op refused too | REPLAY_MismatchedOpNeverReturnsOldPayload, REPLAY_OpCollisionSameTxIdFailsClosed |
| C13 | Propagation-request order: fence → AfterFence → execute/save → ring/floor → AfterCommit; quarantine fences + propagates AfterFence with no execute; throwing hook swallowed, outcomes unchanged (recipient collector is production-only, observed via O1) | PROPAGATION_FenceBeforeExecuteOrder, PROPAGATION_QuarantineFencePropagatesWithoutExecute, PROPAGATION_ThrowingHookNeverAffectsOutcome |
| C14 | s_items Load-gate helper predicates pinned (null/length/version pre-check, clone discipline, viewer strictly-newer rule); production call-site wiring (viewer skip, takeover, structural, reload paths) is assumed — Unity-coupled, no revert-sensitive test in this harness | MUST2_NullIsNotEmpty, MUST3_BlobValidationFallback, MUST4_CopyDisciplineViaSharedHelper, MUST5_HandoffOrderRace (seam pins; wiring assumed, see test-file header) |

### Observed (live session; logs/diagnostics)

| # | Claim | How to observe |
|---|-------|----------------|
| O1 | Propagation-requests push without errors | `[ChestTX] propagation-request pushed=N/M` (TxVerbose); no `force-send` warnings (never throws by construction) |
| O2 | Viewers observe newer revisions after commits | `viewer refresh rev=` lines following commit lines for the same container |
| O3 | Null s_items stays fail-closed (never presumed empty); invalid blobs refused without tearing RAM; viewer keeps last-good | `without authoritative reload (null s_items), staying quarantined` + `SItemsNullDenials` growth; quarantine cleared only by `authoritative reload ok`; `viewer null s_items at rev=... (keeping last-good` / `viewer refresh refused invalid s_items` / `authoritative reload refused invalid s_items` |
| O4 | Counter reservation persists across restarts; corrupt file falls back loudly; untrustworthy-with-evidence refuses issuance | `tx counter primary corrupt, resumed from backup` / `evidence archived aside, REFUSING new transactions` + `counter exhausted` refusal lines; `.corrupt-*` sidecars in the config dir |
| O5 | No-FIFO safety in practice | reordered-duplicate `REPLAY` lines; stale first-timers answer Indeterminate, never double-apply |

### Assumed

| # | Assumption | Risk if wrong |
|---|------------|---------------|
| A1 | ZNet UIDs are ephemeral per process (fresh UID per relaunch) | Counter reservation could collide with a live peer's floor; mitigated by the floor stale-gate |
| A2 | `ZDO.Set` from a non-owner is a no-op (owner-gated) | Post-fence ownership recheck exists precisely because of this; a silent cross-owner write would break single-manager order |
| A3 | `DataRevision` grows on every ZDO write | Viewer staleness guard (`rev <= SeenRev` skip) could miss updates; byte-compare is the second guard |
| A4 | `File.Replace` is atomic on one filesystem; the delete+move fallback is NOT (Delete-to-Move window) | Crash-resistant (not crash-atomic) by design: a torn primary fails checksum and restores from the backup copy, so a non-atomic fallback degrades to a loud restore, never corruption |
| A5 | `ZPackage(byte[])` / `ZDO.GetByteArray` / `ZDO.Set(byte[])` copy semantics are engine-defined and unattested (ZPackage is MemoryStream-backed; Get may hand out the live stored array) | Neutralized by construction, not by assumption: every retained array goes through `TxSItemsGuard.CloneBytes` (clone at Get-capture, owned clone into Load, post-Set baseline clone), so aliasing vs copying no longer matters |

### Unknown (runtime-required)

| # | Question | Why it matters |
|---|----------|----------------|
| U1 | ForceSendZDO exact delivery/timing/throttling semantics | Only performance/visibility, never correctness (see §4) |
| U2 | `Container.Save` internal write set + throw surface | Treated as fully ambiguous by design (reload-or-quarantine) |
| U3 | Server world-save timing for tx ZDO keys | A server crash before world-save loses the tail; floor/ring design bounds this to Indeterminate, never double-apply |
| U4 | Relay ordering/duplication under packet loss | Covered by design (§3); soak in live play still worthwhile |

## 6. Residual windows (NOT claimed fixed — no v4 journal/ACK/escrow)

- Committed-but-Indeterminate: save landed, answer lost → manual reconciliation.
- Client crash between commit and completion loses the response; Query recovers
  it only while the manager holds the cache.
- Restart-safe drag escrow is future work (phase 2); withholding is loud, never
  a drop.
- Counter fail-closed lockout: both-copies-corrupt refuses ALL new txIds until
  restart after inspecting the `.corrupt-*` sidecars (by design — a same-UID
  reset counter must never collide below the persisted floor).
- Propagation recipients (sender + viewers + server) are a best-effort push set:
  an unknown peer with no presence and no in-flight tx still relies on periodic
  ZDO sync (visibility only, never correctness).
- Guarantee wording everywhere is at-most-once submit / exactly-once callback
  attempt — never end-to-end exactly-once.
