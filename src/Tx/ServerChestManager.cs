using System;
using System.Collections.Generic;
using BestAutoSort.Runtime;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Option-B (server-side manager) slice 3: ZDO-only Take/Add manager.
    ///
    /// Runs on the dedicated/server authority WITHOUT any Container component:
    /// decode s_items -> detached Inventory -> shared ExecuteCall ->
    /// encode + zdo.Set (+auto revision bump) -> ForceSendZDO hint ->
    /// targeted global response. Idempotency (Processed/ProcOrder/Floor),
    /// durable ring/floor copies and the quarantine matrix mirror the
    /// Container manager leg-for-leg (same TxCore seams, same terminal matrix);
    /// only the inventory host differs (detached RAM instead of live RAM —
    /// execute-throw recovery is trivial: discard the detached copy, the fence
    /// stays, answer UnknownTx).
    ///
    /// Scope: Add/AddBatch/Take/TakeBatch. Every other op answers persisted
    /// Rejected (deterministic, known-not-committed) — Move/Sort/Upgrade/
    /// SetRule follow in a later slice. Legacy (unmanaged) chests are NEVER
    /// answered here (silent drop — their owner-client manager, if any,
    /// answers authoritatively; a server UnknownTx could contradict it).
    ///
    /// Access mirrors ChestAuthority.CanUse leg-for-leg against ZDO/prefab
    /// sources (same ZDO keys, prefab m_privacy/m_checkGuardStone, ZDO creator,
    /// ZDO position, EffectiveAccessRange). Two documented divergences:
    /// (1) ward-opted chests (prefab m_checkGuardStone) fail closed until a
    /// ZDO-level ward scan exists — PrivateAreas have no server-side objects;
    /// (2) m_privacy is read from the prefab (nothing in vanilla or this mod
    /// ever mutates the instance field; a third-party mutator is unsupported).
    /// Both diverge toward refusal, never toward grant.
    ///
    /// Freshness (slice-3 residual, slice-4 barrier): new sessions HOLD
    /// requests (answered post-settle) instead of serving possibly-unflushed
    /// state; the window re-arms while DataRevision moves. This narrows but
    /// does not close the proven ClaimOwnership race.
    /// </summary>
    internal static class ServerChestManager
    {
        internal const float SettleSeconds = 8f;

        internal static void Pump()
        {
            try
            {
                if (!Plugin.IsActive)
                    return;
                ZNet net = null;
                try { net = ZNet.instance; } catch { net = null; }
                if ((UnityEngine.Object)net == (UnityEngine.Object)null)
                    return;
                bool isServer = false;
                try { isServer = net.IsServer(); } catch { isServer = false; }
                if (!isServer)
                    return;
                bool authority = false;
                try { authority = ServerAuthority.IsAuthorityMode(); } catch { authority = false; }
                if (!authority)
                    return;
                float now = Time.realtimeSinceStartup;
                ServerChestSessions.Prune();
                ProcessDueHeld(now);
            }
            catch
            {
            }
        }

        internal static void OnRequest(long sender, ZDOID chestId, long txId, ZPackage payload)
        {
            try
            {
                if (!Plugin.IsActive)
                    return;
                ZNet net = null;
                try { net = ZNet.instance; } catch { net = null; }
                if ((UnityEngine.Object)net == (UnityEngine.Object)null)
                    return;
                bool isServer = false;
                try { isServer = net.IsServer(); } catch { isServer = false; }
                if (!isServer)
                    return;
                bool authority = false;
                try { authority = ServerAuthority.IsAuthorityMode(); } catch { authority = false; }
                if (!authority)
                    return;
                ZDOMan man = null;
                try { man = ZDOMan.instance; } catch { man = null; }
                if (man == null)
                    return;
                ZDO zdo = null;
                try { zdo = man.GetZDO(chestId); } catch { zdo = null; }
                if (zdo == null)
                    return;
                bool valid = false;
                try { valid = zdo.IsValid(); } catch { valid = false; }
                if (!valid)
                    return;
                ServerChestSession session = null;
                string whyGet = "?";
                bool have = false;
                try { have = ServerChestSessions.TryGetOrCreate(zdo, out session, out whyGet); }
                catch { have = false; }
                if (!have || session == null)
                    return;
                uint nowRev = 0u;
                try { nowRev = zdo.DataRevision; } catch { nowRev = 0u; }

                TxOpCall call = null;
                uint baseRev = 0u;
                long playerId = 0L;
                Vector3 actorPos = Vector3.zero;
                bool decoded = false;
                try { decoded = ChestTxService.DecodeCall(payload, out call, out baseRev, out playerId, out actorPos); }
                catch { decoded = false; }
                if (!decoded || call == null)
                {
                    TxLog.Warn("tx=" + txId + " undecodable server request from " + sender + " (silent drop, prod parity)");
                    return;
                }

                long me = 0L;
                try { me = ZNet.GetUID(); } catch { me = 0L; }

                if (!TxNet.IsCompatiblePeer(sender) && sender != me)
                {
                    if (TryRespondCached(session, zdo, sender, txId, call.Op))
                        return;
                    TxLog.Warn("tx=" + txId + " REJECT incompatible peer " + sender + " (ephemeral: no victim state change)");
                    Answer(sender, session, zdo, txId, TxDecision.SpoofTerminal(), nowRev, new ZPackage(), false, call.Op);
                    return;
                }
                if (call.Op == TxOp.ViewerOpen || call.Op == TxOp.ViewerClose)
                    return;
                if (call.Op == TxOp.Query)
                {
                    AnswerQuery(session, zdo, sender, txId, call.IsTransientRetry);
                    return;
                }
                if (call.Op == TxOp.Upgrade
                    && TxUpgradeGate.RefuseLegacyUpgradeFrame(ServerAuthority.IsAuthorityMode(), true))
                {
                    TxLog.Warn("tx=" + txId + " REJECT legacy upgrade frame (ephemeral, use mediated path)");
                    Answer(sender, session, zdo, txId, TxStatus.Rejected, nowRev, new ZPackage(), false, call.Op);
                    return;
                }
                if (TxDecision.ClassifySenderBinding(sender, txId) == TxDecision.SenderBinding.UnboundStranger)
                {
                    if (TryRespondCached(session, zdo, sender, txId, call.Op))
                        return;
                    TxLog.Warn("tx=" + txId + " REJECT sender/txId mismatch sender=" + sender + " (ephemeral: no victim state change)");
                    Answer(sender, session, zdo, txId, TxDecision.SpoofTerminal(), nowRev, new ZPackage(), false, call.Op);
                    return;
                }

                if (CheckReplayOrStale(session, zdo, sender, txId, call, nowRev))
                    return;
                if (!CheckOwnerGate(session, zdo, sender, txId, call.Op, nowRev))
                    return;
                if (!IsSupportedOp(call.Op))
                {
                    TxLog.Info("tx=" + txId + " op=" + call.Op + " unsupported on server manager (persisted reject)");
                    TxStatus rej;
                    if (TryPersistReject(session, zdo, txId, call.Op, sender, out rej) && rej == TxStatus.Rejected)
                        Answer(sender, session, zdo, txId, TxStatus.Rejected, nowRev, new ZPackage(), false, call.Op);
                    else if (rej == TxStatus.UnknownTx)
                        Answer(sender, session, zdo, txId, TxStatus.UnknownTx, nowRev, new ZPackage(), true, call.Op);
                    return;
                }

                if (call.IsTransientRetry && call.Op != TxOp.Query)
                {
                    if (sender != 0L && !TxIdGen.MatchesPeer(sender, txId))
                    {
                        TxLog.Warn("tx=" + txId + " REJECT clean-manager flagged sender mismatch sender=" + sender + " (ephemeral: no state change)");
                        Answer(sender, session, zdo, txId, TxDecision.SpoofTerminal(), nowRev, new ZPackage(), false, call.Op);
                        return;
                    }
                    TxStatus cleanTerminal;
                    bool crOk, cfOk;
                    cleanTerminal = PersistFlagged(session, zdo, txId, call.Op, sender, out crOk, out cfOk);
                    if (cleanTerminal == TxStatus.Rejected)
                    {
                        ForceSend(session);
                        TxLog.Warn("tx=" + txId + " clean-manager flagged resolved durable (status=" + cleanTerminal + ")");
                        Answer(sender, session, zdo, txId, cleanTerminal, nowRev, new ZPackage(), true, call.Op);
                        return;
                    }
                    if (cleanTerminal == TxStatus.TransientUnavailable)
                    {
                        try { session.Transient.Add(txId); } catch { }
                        TxLog.Warn("tx=" + txId + " clean-manager flagged not durable (TRANSIENT recorded: same-tx retry retained, never executes)");
                        Answer(sender, session, zdo, txId, TxStatus.TransientUnavailable, nowRev, new ZPackage(), false, call.Op);
                        return;
                    }
                    TxLog.Warn("tx=" + txId + " clean-manager flagged floor-only (terminal UnknownTx, permanently stale, never executes)");
                    Answer(sender, session, zdo, txId, TxStatus.UnknownTx, nowRev, new ZPackage(), true, call.Op);
                    return;
                }

                if (session.State.RingCorrupt && session.State.FloorCorrupt)
                {
                    TxLog.Warn("tx=" + txId + " refused: ring corrupt (fail closed)");
                    Answer(sender, session, zdo, txId, TxStatus.UnknownTx, nowRev, new ZPackage(), true, call.Op);
                    return;
                }

                if (session.Quarantined)
                {
                    bool qDurable;
                    TxStatus qTerminal = PersistQuarantine(session, zdo, txId, call.Op, sender, out qDurable);
                    if (!qDurable)
                    {
                        try { session.Transient.Add(txId); } catch { }
                        TxLog.Warn("tx=" + txId + " quarantined and refusal not persisted (TRANSIENT recorded: same-tx retry retained)");
                        Answer(sender, session, zdo, txId, TxStatus.TransientUnavailable, nowRev, new ZPackage(), false, call.Op);
                        return;
                    }
                    if (qTerminal == TxStatus.Rejected)
                        ForceSend(session);
                    TxLog.Warn("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " refused: quarantined (" + session.QuarantineReason + ", status=" + qTerminal + ")");
                    Answer(sender, session, zdo, txId, qTerminal, nowRev, new ZPackage(), true, call.Op);
                    return;
                }

                string accessWhy = "ok";
                if (!ServerAccess.CanUse(zdo, sender, playerId, actorPos, out accessWhy))
                {
                    TxLog.Warn("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " REJECT access (" + accessWhy + ")");
                    TxStatus arej;
                    if (TryPersistReject(session, zdo, txId, call.Op, sender, out arej) && arej == TxStatus.Rejected)
                        Answer(sender, session, zdo, txId, TxStatus.Rejected, nowRev, new ZPackage(), false, call.Op);
                    else if (arej == TxStatus.UnknownTx)
                        Answer(sender, session, zdo, txId, TxStatus.UnknownTx, nowRev, new ZPackage(), true, call.Op);
                    return;
                }

                float now = Time.realtimeSinceStartup;
                if (session.SettleUntil == 0f)
                {
                    session.SettleUntil = now + SettleSeconds;
                    try { session.SettleRev = zdo.DataRevision; } catch { session.SettleRev = 0u; }
                    TxLog.Info("container=" + TxLog.Zid(session.ZdoId) + " settle armed until=" + session.SettleUntil);
                }
                else if (now < session.SettleUntil)
                {
                    uint curRev = 0u;
                    try { curRev = zdo.DataRevision; } catch { curRev = 0u; }
                    if (curRev != session.SettleRev)
                    {
                        session.SettleUntil = now + SettleSeconds;
                        session.SettleRev = curRev;
                        TxLog.Info("container=" + TxLog.Zid(session.ZdoId) + " settle re-armed (rev moved)");
                    }
                }
                if (now < session.SettleUntil)
                {
                    HeldRequest held = new HeldRequest();
                    held.Sender = sender;
                    held.TxId = txId;
                    held.Call = call;
                    held.BaseRev = baseRev;
                    held.PlayerId = playerId;
                    held.ActorPos = actorPos;
                    held.ReceivedAt = now;
                    bool dup = false;
                    try
                    {
                        foreach (HeldRequest h in session.Held)
                        {
                            if (h.TxId == txId) { dup = true; break; }
                        }
                        if (!dup)
                            session.Held.Add(held);
                    }
                    catch { }
                    TxLog.Info("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " HELD until settle (no answer yet, at-most-once preserved)");
                    return;
                }

                ProcessRequest(session, zdo, sender, txId, call, baseRev, playerId, actorPos, nowRev);
            }
            catch
            {
            }
        }

        private static void ProcessDueHeld(float now)
        {
            try
            {
                List<ServerChestSession> all = ServerChestSessions.Snapshot();
                if (all == null)
                    return;
                ZDOMan man = null;
                try { man = ZDOMan.instance; } catch { man = null; }
                if (man == null)
                    return;
                foreach (ServerChestSession session in all)
                {
                    List<HeldRequest> due = null;
                    try
                    {
                        if (session == null || session.Held.Count == 0)
                            continue;
                        if (now < session.SettleUntil)
                            continue;
                        due = new List<HeldRequest>(session.Held);
                        session.Held.Clear();
                    }
                    catch { continue; }
                    ZDO zdo = null;
                    try { zdo = man.GetZDO(session.ZdoId); } catch { zdo = null; }
                    bool valid = false;
                    try { valid = zdo != null && zdo.IsValid(); } catch { valid = false; }
                    uint nowRev = 0u;
                    try { nowRev = valid ? zdo.DataRevision : 0u; } catch { nowRev = 0u; }
                    try
                    {
                        due.Sort(delegate (HeldRequest a, HeldRequest b)
                        {
                            if (a == null || b == null)
                                return 0;
                            return a.ReceivedAt.CompareTo(b.ReceivedAt);
                        });
                    }
                    catch { }
                    foreach (HeldRequest h in due)
                    {
                        try
                        {
                            if (h == null || h.Call == null)
                                continue;
                            if (CheckReplayOrStale(session, zdo, h.Sender, h.TxId, h.Call, nowRev))
                                continue;
                            if (!CheckOwnerGate(session, zdo, h.Sender, h.TxId, h.Call.Op, nowRev))
                                continue;
                            if (!valid)
                            {
                                Answer(h.Sender, session, zdo, h.TxId, TxStatus.UnknownTx, 0u, new ZPackage(), true, h.Call.Op);
                                continue;
                            }
                            ProcessRequest(session, zdo, h.Sender, h.TxId, h.Call, h.BaseRev, h.PlayerId, h.ActorPos, nowRev);
                        }
                        catch { }
                    }
                }
            }
            catch
            {
            }
        }

        private static void ProcessRequest(ServerChestSession session, ZDO zdo, long sender, long txId, TxOpCall call, uint baseRev, long playerId, Vector3 actorPos, uint nowRev)
        {
            try
            {
                if (session == null || zdo == null || call == null)
                    return;
                ChestState state = session.State;
                if (baseRev != 0u && baseRev != nowRev)
                    TxLog.Info("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " stale_revision client=" + baseRev + " server=" + nowRev);

                if (!TryPersistFence(session, zdo, txId))
                {
                    TxLog.Warn("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " fence not persisted (indeterminate, never executes)");
                    Answer(sender, session, zdo, txId, TxStatus.UnknownTx, nowRev, new ZPackage(), true, call.Op);
                    return;
                }
                ForceSend(session);

                Inventory inv = null;
                string decWhy = "?";
                bool decOk = false;
                try { decOk = ServerChestSessions.TryDecodeItems(session, zdo, out inv, out decWhy); }
                catch { decOk = false; }
                if (!decOk || inv == null)
                {
                    session.Quarantined = true;
                    session.QuarantineReason = "decode-failed(" + decWhy + ")";
                    try { session.QuarantineOldOwner = zdo.GetOwner(); } catch { }
                    bool qDurable;
                    TxStatus qTerminal = PersistQuarantine(session, zdo, txId, call.Op, sender, out qDurable);
                    if (!qDurable)
                    {
                        try { session.Transient.Add(txId); } catch { }
                        TxLog.Warn("tx=" + txId + " decode failed, quarantined, refusal not persisted (TRANSIENT recorded)");
                        Answer(sender, session, zdo, txId, TxStatus.TransientUnavailable, nowRev, new ZPackage(), false, call.Op);
                        return;
                    }
                    if (qTerminal == TxStatus.Rejected)
                        ForceSend(session);
                    TxLog.Warn("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " decode failed, quarantined (status=" + qTerminal + ")");
                    Answer(sender, session, zdo, txId, qTerminal, nowRev, new ZPackage(), true, call.Op);
                    return;
                }

                TxJob job = new TxJob();
                job.Container = null;
                job.IsLocal = false;
                job.TxId = txId;
                job.Sender = sender;
                job.PlayerId = playerId;
                job.BaseRev = baseRev;
                job.ActorPos = actorPos;
                job.Call = call;
                StoredResult applied = null;
                bool execOk = false;
                try
                {
                    applied = ChestTxService.ExecuteCall(state, job, inv);
                    execOk = applied != null;
                }
                catch (Exception ex)
                {
                    TxLog.Warn("tx=" + txId + " execute failed: " + ex.Message + " (detached RAM discarded, fence stays, indeterminate)");
                    execOk = false;
                }
                if (!execOk)
                {
                    Answer(sender, session, zdo, txId, TxStatus.UnknownTx, nowRev, new ZPackage(), true, call.Op);
                    return;
                }

                bool commitPersisted = CommitResult(session, zdo, job, applied, inv);
                if (!commitPersisted)
                    TxLog.Warn("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " commit result NOT ring-persisted (accepted residual: at-most-once holds via fence)");
                ZPackage body = null;
                try { body = ChestTxService.EncodeResultBody(call, applied); } catch { body = new ZPackage(); }
                Answer(sender, session, zdo, txId, applied.Status, applied.Revision, body != null ? body : new ZPackage(), false, call.Op);
            }
            catch
            {
            }
        }

        internal static bool IsSupportedOp(TxOp op)
        {
            return op == TxOp.Add || op == TxOp.AddBatch || op == TxOp.Take || op == TxOp.TakeBatch;
        }

        private static bool TryRespondCached(ServerChestSession session, ZDO zdo, long sender, long txId, TxOp op)
        {
            try
            {
                StoredResult cached = null;
                bool hit = false;
                try { hit = session.State.Processed.TryGetValue(txId, out cached); } catch { hit = false; }
                if (!hit || cached == null)
                    return false;
                if (cached.Sender != 0 && TxIdGen.PeerKey(cached.Sender) != TxIdGen.PeerKey(sender))
                {
                    Answer(sender, session, zdo, txId, TxStatus.Rejected, cached.Revision, new ZPackage(), false, cached.Op);
                    return true;
                }
                if (TxDecision.IsOpMismatch(cached.Op, op))
                    return false;
                Answer(sender, session, zdo, txId, cached.Status, cached.Revision,
                    ChestTxService.EncodeCachedBody(cached), cached.TotalsOnly, cached.Op);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Single-writer gate: the server executes only when no live client owns
        /// the chest (owner == 0, owner == self, or owner has no live peer). A
        /// live owner could vanilla-write concurrently, so serve is refused
        /// EPHEMERALLY (Rejected, no seed/floor/ring — same seam as
        /// spoof/version-skew refusals): the same txId answers identically
        /// while the owner lives, and becomes servable the moment the owner
        /// dies, with no burned floor. Committed replays are answered before
        /// this gate (CheckReplayOrStale runs first), so an original outcome
        /// survives ownership changes. Ownership itself is never mutated here
        /// (client open-routing depends on owner 0/self-routing; auto-clear is
        /// out of scope — a stuck live owner needs a client relog).
        /// </summary>
        private static bool CheckOwnerGate(ServerChestSession session, ZDO zdo, long sender, long txId, TxOp op, uint nowRev)
        {
            try
            {
                long owner = 0L;
                try { owner = zdo.GetOwner(); } catch { owner = 0L; }
                if (owner == 0L)
                    return true;
                long me = 0L;
                try { me = ZNet.GetUID(); } catch { me = 0L; }
                if (me != 0L && owner == me)
                    return true;
                bool alive = false;
                try
                {
                    ZNet net = ZNet.instance;
                    alive = net != null && net.GetPeer(owner) != null;
                }
                catch { alive = false; }
                if (!alive)
                    return true;
                TxLog.Warn("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " REJECT live-owner " + owner + " (ephemeral: server serves ownerless/dead/self-owned only; relog the owner client)");
                Answer(sender, session, zdo, txId, TxStatus.Rejected, nowRev, new ZPackage(), false, op);
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Shared replay/transient/floor gate (production order: replay, then
        /// transient map, then floor-stale). Used by OnRequest AND per held item
        /// at due time, so UDP reorder inside the settle window cannot execute
        /// below the peer high-water. Returns true when answered.
        /// </summary>
        private static bool CheckReplayOrStale(ServerChestSession session, ZDO zdo, long sender, long txId, TxOpCall call, uint nowRev)
        {
            try
            {
                ChestState state = session.State;
                StoredResult replay = null;
                bool haveReplay = false;
                try { haveReplay = state.Processed.TryGetValue(txId, out replay); } catch { haveReplay = false; }
                if (haveReplay && replay != null)
                {
                    if (replay.Sender != 0 && TxIdGen.PeerKey(replay.Sender) != TxIdGen.PeerKey(sender))
                    {
                        TxLog.Warn("tx=" + txId + " REJECT replay sender mismatch sender=" + sender);
                        Answer(sender, session, zdo, txId, TxStatus.Rejected, replay.Revision, new ZPackage(), false, replay.Op);
                        return true;
                    }
                    if (TxDecision.IsOpMismatch(replay.Op, call.Op))
                    {
                        TxLog.Warn("tx=" + txId + " REJECT replay op mismatch cached=" + replay.Op + " incoming=" + call.Op + " (indeterminate, never executes)");
                        Answer(sender, session, zdo, txId, TxStatus.UnknownTx, nowRev, new ZPackage(), true, call.Op);
                        return true;
                    }
                    TxLog.Info("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " REPLAY status=" + replay.Status);
                    Answer(sender, session, zdo, txId, replay.Status, replay.Revision,
                        ChestTxService.EncodeCachedBody(replay), replay.TotalsOnly, replay.Op);
                    return true;
                }
                bool transientHeld = false;
                try { transientHeld = session.Transient.Contains(txId); } catch { transientHeld = false; }
                if (transientHeld)
                {
                    if (sender != 0L && !TxIdGen.MatchesPeer(sender, txId))
                    {
                        TxLog.Warn("tx=" + txId + " REJECT transient sender mismatch sender=" + sender + " (ephemeral: no state change)");
                        Answer(sender, session, zdo, txId, TxDecision.SpoofTerminal(), nowRev, new ZPackage(), false, call.Op);
                        return true;
                    }
                    if (!call.IsTransientRetry)
                    {
                        TxLog.Info("tx=" + txId + " TRANSIENT (unflagged resend reminded, never executes)");
                        Answer(sender, session, zdo, txId, TxStatus.TransientUnavailable, nowRev, new ZPackage(), false, call.Op);
                        return true;
                    }
                    bool rrOk, rfOk;
                    TxStatus rTerm = PersistFlagged(session, zdo, txId, call.Op, sender, out rrOk, out rfOk);
                    if (rTerm == TxStatus.Rejected)
                    {
                        try { session.Transient.Remove(txId); } catch { }
                        ForceSend(session);
                        Answer(sender, session, zdo, txId, rTerm, nowRev, new ZPackage(), true, call.Op);
                        return true;
                    }
                    if (rTerm == TxStatus.TransientUnavailable)
                    {
                        Answer(sender, session, zdo, txId, TxStatus.TransientUnavailable, nowRev, new ZPackage(), false, call.Op);
                        return true;
                    }
                    try { session.Transient.Remove(txId); } catch { }
                    Answer(sender, session, zdo, txId, TxStatus.UnknownTx, nowRev, new ZPackage(), true, call.Op);
                    return true;
                }
                if (ChestTxService.IsFloorStale(state, txId))
                {
                    TxLog.Warn("tx=" + txId + " refused: at/below execution floor (indeterminate, never executes)");
                    Answer(sender, session, zdo, txId, TxStatus.UnknownTx, nowRev, new ZPackage(), true, call.Op);
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void AnswerQuery(ServerChestSession session, ZDO zdo, long sender, long txId, bool isTransientRetry)
        {
            try
            {
                uint rev = 0u;
                try { rev = zdo.DataRevision; } catch { rev = 0u; }
                StoredResult cached = null;
                bool hit = false;
                try { hit = session.State.Processed.TryGetValue(txId, out cached); } catch { hit = false; }
                if (hit && cached != null)
                {
                    if (cached.Sender != 0L && TxIdGen.PeerKey(cached.Sender) != TxIdGen.PeerKey(sender))
                    {
                        TxLog.Warn("tx=" + txId + " QUERY sender mismatch");
                        Answer(sender, session, zdo, txId, TxStatus.Rejected, cached.Revision, new ZPackage(), false, cached.Op);
                        return;
                    }
                    TxLog.Info("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " QUERY hit status=" + cached.Status);
                    Answer(sender, session, zdo, txId, cached.Status, cached.Revision,
                        ChestTxService.EncodeCachedBody(cached), cached.TotalsOnly, cached.Op);
                    return;
                }
                if (isTransientRetry)
                {
                    if (sender != 0L && !TxIdGen.MatchesPeer(sender, txId))
                    {
                        TxLog.Warn("tx=" + txId + " FLAGGED-QUERY sender mismatch");
                        Answer(sender, session, zdo, txId, TxStatus.Rejected, rev, new ZPackage(), false, TxOp.Query);
                        return;
                    }
                    TxLog.Info("tx=" + txId + " FLAGGED-QUERY no record (same-tx retry retained)");
                    Answer(sender, session, zdo, txId, TxStatus.TransientUnavailable, rev, new ZPackage(), false, TxOp.Query);
                    return;
                }
                Answer(sender, session, zdo, txId, TxStatus.UnknownTx, rev, new ZPackage(), true, TxOp.Query);
            }
            catch
            {
            }
        }

        private static bool TryPersistFence(ServerChestSession session, ZDO zdo, long txId)
        {
            try
            {
                ChestTxService.AdvanceFloor(session.State, txId);
                return WriteFloorTo(session, zdo);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryPersistReject(ServerChestSession session, ZDO zdo, long txId, TxOp op, long sender, out TxStatus terminal)
        {
            terminal = TxStatus.UnknownTx;
            try
            {
                bool seeded = false;
                try { seeded = ChestTxService.SeedReject(session.State, txId, op, sender, true); } catch { seeded = false; }
                bool ringOk = WriteRingTo(session, zdo);
                bool floorOk = WriteFloorTo(session, zdo);
                if (ringOk && floorOk)
                {
                    terminal = TxStatus.Rejected;
                    return true;
                }
                if (seeded)
                {
                    try { ChestTxService.EvictSeededReject(session.State, txId); } catch { }
                }
                if (!ringOk && !floorOk)
                    return false;
                terminal = TxStatus.UnknownTx;
                return true;
            }
            catch
            {
                terminal = TxStatus.UnknownTx;
                return false;
            }
        }

        private static TxStatus PersistQuarantine(ServerChestSession session, ZDO zdo, long txId, TxOp op, long sender, out bool anyDurable)
        {
            anyDurable = false;
            try
            {
                bool seeded = false;
                try { seeded = ChestTxService.SeedReject(session.State, txId, op, sender, true); } catch { seeded = false; }
                bool ringOk = WriteRingTo(session, zdo);
                bool floorOk = WriteFloorTo(session, zdo);
                anyDurable = ringOk || floorOk;
                TxStatus terminal = TxDecision.HandoffDropTerminal(ringOk, floorOk);
                if (terminal != TxStatus.Rejected && seeded)
                {
                    try { ChestTxService.EvictSeededReject(session.State, txId); } catch { }
                }
                return terminal;
            }
            catch
            {
                return TxStatus.UnknownTx;
            }
        }

        private static TxStatus PersistFlagged(ServerChestSession session, ZDO zdo, long txId, TxOp op, long sender, out bool ringOk, out bool floorOk)
        {
            ringOk = false;
            floorOk = false;
            try
            {
                bool seeded = false;
                try { seeded = ChestTxService.SeedReject(session.State, txId, op, sender, true); } catch { seeded = false; }
                ringOk = WriteRingTo(session, zdo);
                floorOk = WriteFloorTo(session, zdo);
                TxStatus terminal = TxDecision.FlaggedRefusalTerminal(ringOk, floorOk);
                if (terminal != TxStatus.Rejected && seeded)
                {
                    try { ChestTxService.EvictSeededReject(session.State, txId); } catch { }
                }
                return terminal;
            }
            catch
            {
                return TxStatus.UnknownTx;
            }
        }

        private static bool CommitResult(ServerChestSession session, ZDO zdo, TxJob job, StoredResult result, Inventory inv)
        {
            try
            {
                ChestState state = session.State;
                bool mutated = result.Status == TxStatus.Accepted || result.Status == TxStatus.Partial;
                if (mutated)
                {
                    // The committed bytes come from the SAME detached inventory
                    // ExecuteCall just mutated (never re-decoded: re-applying
                    // would double-apply; re-reading could clobber the result).
                    uint newRev = 0u;
                    string saveWhy = "?";
                    bool saved = false;
                    try
                    {
                        saved = inv != null && ServerChestSessions.TrySaveItems(session, zdo, inv, out newRev, out saveWhy);
                    }
                    catch { saved = false; }
                    if (!saved)
                        return false;
                    result.Revision = newRev;
                }
                else
                {
                    try { result.Revision = zdo.DataRevision; } catch { result.Revision = 0u; }
                }
                try { result.Sender = TxIdGen.PeerKey(job.Sender); } catch { }
                result.IsReplay = false;
                try
                {
                    state.Processed[job.TxId] = ChestTxService.CloneStored(result, result.Status, false);
                    state.ProcOrder.AddLast(job.TxId);
                    while (state.ProcOrder.Count > TxLimits.ProcessedCacheCap)
                    {
                        long oldest = state.ProcOrder.First.Value;
                        state.ProcOrder.RemoveFirst();
                        state.Processed.Remove(oldest);
                    }
                }
                catch { }
                try { ChestTxService.AdvanceFloor(state, job.TxId); } catch { }
                bool ringOk = WriteRingTo(session, zdo);
                bool floorOk = WriteFloorTo(session, zdo);
                if (!state.FloorCorrupt && ringOk && floorOk)
                    state.RingCorrupt = false;
                ForceSend(session);
                TxLog.Info("container=" + TxLog.Zid(session.ZdoId) + " tx=" + job.TxId + " peer=" + job.Sender
                    + " op=" + job.Call.Op + " accepted=" + result.AcceptedTotal() + " revision=" + result.Revision);
                return ringOk && floorOk;
            }
            catch
            {
                return false;
            }
        }

        private static bool WriteRingTo(ServerChestSession session, ZDO zdo)
        {
            try
            {
                ChestState state = session.State;
                List<RingSlot> slots = new List<RingSlot>(state.ProcOrder.Count);
                LinkedListNode<long> node = state.ProcOrder.First;
                while (node != null)
                {
                    StoredResult r = null;
                    bool hit = false;
                    try { hit = state.Processed.TryGetValue(node.Value, out r); } catch { hit = false; }
                    if (hit && r != null)
                    {
                        RingSlot s;
                        s.TxId = node.Value;
                        s.AcceptedTotal = r.AcceptedTotal();
                        s.Revision = r.Revision;
                        s.Status = r.Status;
                        s.Op = r.Op;
                        s.Sender = r.Sender != 0L ? TxIdGen.PeerKey(r.Sender) : TxIdGen.PeerOf(node.Value);
                        s.Accepted = new List<int>(r.Accepted);
                        s.TakePayloads = ChestTxService.BuildRingTakePayloads(r);
                        slots.Add(s);
                    }
                    node = node.Next;
                }
                List<RingEntry> ring = TxRing.Snapshot(slots);
                byte[] bytes = TxCore.TxCore.EncodeRingV3(ring, state.Floor);
                if (bytes == null)
                {
                    TxLog.Warn("ring persist failed: encode error (persisting nothing)");
                    return false;
                }
                if (ChestTxService.SameBytes(bytes, state.LastRingBytes))
                    return true;
                zdo.Set(ChestTxService.RingKey, bytes);
                state.LastRingBytes = TxSItemsGuard.CloneBytes(bytes);
                return true;
            }
            catch (Exception ex)
            {
                TxLog.Warn("ring persist failed: " + ex.Message);
                return false;
            }
        }

        private static bool WriteFloorTo(ServerChestSession session, ZDO zdo)
        {
            try
            {
                ChestState state = session.State;
                if (state.Floor.Count > TxLimits.FloorCap)
                    TxLog.Warn("container=" + TxLog.Zid(session.ZdoId) + " floor peers=" + state.Floor.Count
                        + " past warn threshold " + TxLimits.FloorCap + " (unbounded, no refusal)");
                byte[] bytes = TxCore.TxCore.EncodeFloor(state.Floor);
                if (bytes == null)
                {
                    TxLog.Warn("floor persist failed: encode error (persisting nothing)");
                    return false;
                }
                if (ChestTxService.SameBytes(bytes, state.LastFloorBytes))
                    return true;
                zdo.Set(ChestTxService.FloorKey, bytes);
                state.LastFloorBytes = TxSItemsGuard.CloneBytes(bytes);
                return true;
            }
            catch (Exception ex)
            {
                TxLog.Warn("floor persist failed: " + ex.Message);
                return false;
            }
        }

        private static void ForceSend(ServerChestSession session)
        {
            try
            {
                if (ZDOMan.instance != null)
                    ZDOMan.instance.ForceSendZDO(session.ZdoId);
            }
            catch
            {
            }
        }

        private static void Answer(long sender, ServerChestSession session, ZDO zdo, long txId, TxStatus status, uint revision, ZPackage body, bool totalsOnly, TxOp op)
        {
            try
            {
                ZPackage pkg = new ZPackage();
                TxCodec.WriteResponseHeader(pkg, txId, status, revision, totalsOnly);
                pkg.Write(body != null ? body : new ZPackage());
                ZPackage outer = new ZPackage();
                outer.Write(session.ZdoId);
                outer.Write(pkg);
                ZRoutedRpc rpc = null;
                try { rpc = ZRoutedRpc.instance; } catch { rpc = null; }
                if (rpc == null)
                    return;
                try { rpc.InvokeRoutedRPC(sender, ServerChestDirector.ServerResponseRpc, new object[1] { outer }); } catch { }
                TxLog.Info("container=" + TxLog.Zid(session.ZdoId) + " tx=" + txId + " response status=" + status + " rev=" + revision + " to=" + sender);
            }
            catch
            {
            }
        }
    }
}
