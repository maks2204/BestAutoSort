using System;
using System.Collections.Generic;
using BestAutoSort.Runtime;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Option-B server-mediated chest destroy (hammer-remove parity).
    ///
    /// Why this exists (proven by decompilation): ZNetScene.Destroy deletes the
    /// ZDO only `if (zDO.IsOwner())`, and WearNTear.RPC_Remove routes to the
    /// owner — for ownerless (0) managed chests destruction silently does
    /// nothing; the non-WearNTear chain additionally refunds + locally destroys
    /// (reappear + dupe farm). The server owns the truth but has no objects, so
    /// it performs the authoritative half (spill list + ZDO delete) while the
    /// requesting client performs the local half (spawn drops, refund, effects,
    /// object destroy with spill suppression).
    ///
    /// Flow (async, idempotent): client Player.RemovePiece prefix (managed only,
    /// vanilla untouched otherwise) → global TxServerDestroy request
    /// [chestId][txId][playerId][actorPos] → server: gates → decode → spill
    /// blobs (Take-shaped body, zero new codec) → claim (pass the IsOwner gate
    /// inside DestroyZDO) → ZDOMan.DestroyZDO → drop session → tombstone keyed
    /// by originating txId (bounded) → ok+blobs. Gone chest with tomb hit +
    /// sender match → replay blobs (alreadyGone); tomb hit + sender mismatch
    /// → silent drop with no answer (packet replay); gone chest without tomb
    /// → ok-empty (a fresh txId from ghost re-hammer misses by construction:
    /// no drops, no refund).
    /// Deny (access/unreadable/ineligible-silent) → client message, chest kept.
    /// Client timeout (10s, 3 attempts same txId) → message, chest kept.
    ///
    /// Residuals (documented, accepted): damage-destroy path untouched (still
    /// broken-as-today for ownerless); no-build-zone and crafting-station gates
    /// cannot evaluate headless (no scene objects) and are skipped — range,
    /// wards, privacy, creator and access still gate every destroy; in-flight
    /// tx vs racing destroy resolves to Indeterminate (ms window, manual
    /// reconcile, same class as crash windows); cross-client in-flight tx vs
    /// racing destroy → Indeterminate (ms race class: replay is bound to the
    /// originating txId — legit replays only ever reuse the same auto-retried
    /// txId — with sender match required; a manual ghost re-hammer mints a
    /// fresh txId by construction so it misses the tomb and gets ok-empty;
    /// ZDOID-keying would hand the full spill to any fresh txId from the same
    /// sender, a self-dupe farm); tombs are bounded RAM (256 entries, 10min
    /// expiry)
    /// and restart-wiped (a relaunch drops pending tombs — a re-destroy then
    /// answers ok-empty: no drops, no refund).
    /// </summary>
    internal static class TxDestroy
    {
        internal const string DestroyRequestRpc = "BestAutoSort_TxServerDestroy";
        internal const string DestroyResponseRpc = "BestAutoSort_TxServerDestroyResponse";

        private const float DestroyTimeout = 10f;
        private const int MaxAttempts = 3;
        private const int TombCap = 256;
        private const float TombExpirySeconds = 600f;
        private const int RefundCap = 256;

        internal sealed class PendingDestroy
        {
            internal ZDOID ChestId;
            internal long TxId;
            internal GameObject Go;
            internal Piece Piece;
            internal Container Container;
            internal long PlayerId;
            internal Vector3 ActorPos;
            internal float SentAt;
            internal int Attempts;
            internal bool Messaged;
        }

        private static readonly Dictionary<ZDOID, PendingDestroy> Pending =
            new Dictionary<ZDOID, PendingDestroy>();
        private sealed class TombEntry
        {
            internal long Sender;
            internal ZPackage Blobs;
            internal float StoredAt;
        }
        private static readonly Dictionary<long, TombEntry> Tombs = new Dictionary<long, TombEntry>();
        private static readonly HashSet<ZDOID> Refunded = new HashSet<ZDOID>();
        private static long _lastPeerUid;
        private static long _lastSessionId;

        internal static void Reset()
        {
            try { Pending.Clear(); } catch { }
            try { Tombs.Clear(); } catch { }
            try { Refunded.Clear(); } catch { }
        }

        internal static void RegisterGlobal(ZRoutedRpc rpc)
        {
            try
            {
                if (rpc == null)
                    return;
                rpc.Register<ZPackage>(DestroyRequestRpc, OnDestroyRequest);
                rpc.Register<ZPackage>(DestroyResponseRpc, OnDestroyResponse);
            }
            catch
            {
            }
        }

        /// <summary>Client: request server-mediated destroy. Returns false when not sent.</summary>
        internal static bool HasPending(ZDOID chestId)
        {
            try { return !chestId.IsNone() && Pending.ContainsKey(chestId); }
            catch { return false; }
        }

        /// <summary>Pending-destroy early-out with once-only messaging (see Messaged).</summary>
        internal static bool NoteDuplicate(ZDOID chestId)
        {
            try
            {
                if (chestId.IsNone())
                    return false;
                PendingDestroy dup = null;
                if (!Pending.TryGetValue(chestId, out dup) || dup == null)
                    return false;
                if (!dup.Messaged)
                {
                    dup.Messaged = true;
                    TellPlayerStatic("Chest destroy already in progress.");
                }
                return true;
            }
            catch { return false; }
        }

        internal static bool SubmitDestroy(Container container, ZDOID chestId, long playerId, Vector3 actorPos)
        {
            try
            {
                if ((UnityEngine.Object)container == (UnityEngine.Object)null || chestId.IsNone())
                    return false;
                try
                {
                    PendingDestroy dup = null;
                    if (Pending.TryGetValue(chestId, out dup) && dup != null)
                    {
                        if (!dup.Messaged)
                        {
                            dup.Messaged = true;
                            TellPlayerStatic("Chest destroy already in progress.");
                        }
                        return false;
                    }
                }
                catch { }
                ZRoutedRpc rpc = null;
                try { rpc = ZRoutedRpc.instance; } catch { rpc = null; }
                if (rpc == null)
                {
                    TellPlayerStatic("Chest destroy got no answer. The chest was kept.");
                    return false;
                }
                long txId = ChestTxService.IssueTxId();
                if (txId == 0L)
                {
                    TellPlayerStatic("Chest destroy unavailable (tx pool exhausted).");
                    return false;
                }
                ZNetView netView = TxReflect.GetNetView(container);
                GameObject go = null;
                Piece piece = null;
                try { go = ((Component)container).gameObject; } catch { go = null; }
                try { piece = ((Component)container).GetComponent<Piece>(); } catch { piece = null; }
                ZPackage pkg = new ZPackage();
                try
                {
                    pkg.Write(chestId);
                    pkg.Write(txId);
                    pkg.Write(playerId);
                    pkg.Write(actorPos);
                }
                catch
                {
                    try { TellPlayerStatic("Chest destroy got no answer. The chest was kept."); } catch { }
                    return false;
                }
                PendingDestroy pending = new PendingDestroy();
                pending.ChestId = chestId;
                pending.TxId = txId;
                pending.Go = go;
                pending.Piece = piece;
                pending.Container = container;
                pending.PlayerId = playerId;
                pending.ActorPos = actorPos;
                pending.SentAt = Time.realtimeSinceStartup;
                pending.Attempts = 1;
                try { Pending[chestId] = pending; } catch { try { TellPlayerStatic("Chest destroy got no answer. The chest was kept."); } catch { } return false; }
                try { rpc.InvokeRoutedRPC(DestroyRequestRpc, new object[1] { pkg }); }
                catch
                {
                    try { Pending.Remove(chestId); } catch { }
                    TellPlayerStatic("Chest destroy got no answer. The chest was kept.");
                    return false;
                }
                TxLog.Info("container=" + TxLog.Zid(chestId) + " destroy submit tx=" + txId);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void Pump()
        {
            try
            {
                try
                {
                    long peerUid = ZNet.GetUID();
                    long sessionId = 0L;
                    try { sessionId = ZDOMan.GetSessionID(); }
                    catch { sessionId = 0L; }
                    if (peerUid != _lastPeerUid || sessionId != _lastSessionId)
                    {
                        Reset();
                        _lastPeerUid = peerUid;
                        _lastSessionId = sessionId;
                    }
                }
                catch { }
                if (!Plugin.IsActive || Pending.Count == 0)
                    return;
                float now = Time.realtimeSinceStartup;
                List<ZDOID> done = null;
                foreach (KeyValuePair<ZDOID, PendingDestroy> kv in Pending)
                {
                    PendingDestroy p = kv.Value;
                    if (p == null)
                    {
                        if (done == null)
                            done = new List<ZDOID>();
                        done.Add(kv.Key);
                        continue;
                    }
                    if (now - p.SentAt < DestroyTimeout)
                        continue;
                    if (p.Attempts < MaxAttempts)
                    {
                        p.Attempts++;
                        p.SentAt = now;
                        ZPackage pkg = new ZPackage();
                        try
                        {
                            pkg.Write(p.ChestId);
                            pkg.Write(p.TxId);
                            pkg.Write(p.PlayerId);
                            pkg.Write(p.ActorPos);
                        }
                        catch { continue; }
                        ZRoutedRpc rpc = null;
                        try { rpc = ZRoutedRpc.instance; } catch { rpc = null; }
                        if (rpc == null)
                            continue;
                        try { rpc.InvokeRoutedRPC(DestroyRequestRpc, new object[1] { pkg }); } catch { }
                        TxLog.Info("container=" + TxLog.Zid(p.ChestId) + " destroy retry tx=" + p.TxId + " attempt=" + p.Attempts);
                        continue;
                    }
                    if (done == null)
                        done = new List<ZDOID>();
                    done.Add(kv.Key);
                    try { TellPlayerStatic("Chest destroy outcome unknown. Verify before retrying."); } catch { }
                    TxLog.Warn("container=" + TxLog.Zid(p.ChestId) + " destroy timeout tx=" + p.TxId + " (chest kept)");
                }
                if (done != null)
                {
                    foreach (ZDOID id in done)
                    {
                        try { Pending.Remove(id); } catch { }
                    }
                }
            }
            catch
            {
            }
        }

        private static void TellPlayerStatic(string message)
        {
            try
            {
                Player player = Player.m_localPlayer;
                if ((UnityEngine.Object)player != (UnityEngine.Object)null)
                    ((Character)player).Message((MessageType)2, message, 0, (Sprite)null, false);
            }
            catch
            {
            }
        }

        // ============================ client: response ============================

        private static void OnDestroyResponse(long sender, ZPackage pkg)
        {
            try
            {
                if (!Plugin.IsActive || pkg == null)
                    return;
                bool trusted = false;
                try
                {
                    long me = ZNet.GetUID();
                    ZNet net = ZNet.instance;
                    bool self = net != null && net.IsServer();
                    if (self)
                        trusted = sender != 0L && sender == me;
                    else if (net != null)
                    {
                        ZNetPeer sp = net.GetServerPeer();
                        trusted = sp != null && sender == sp.m_uid;
                    }
                }
                catch { trusted = false; }
                if (!trusted)
                {
                    try { TxLog.Warn("destroy response from untrusted sender=" + sender + " dropped"); } catch { }
                    return;
                }
                ZDOID chestId = ZDOID.None;
                long txId = 0L;
                bool granted = false;
                string reason = "?";
                bool alreadyGone = false;
                ZPackage body = null;
                try
                {
                    chestId = pkg.ReadZDOID();
                    txId = pkg.ReadLong();
                    granted = pkg.ReadBool();
                    reason = pkg.ReadString();
                    alreadyGone = pkg.ReadBool();
                    body = pkg.ReadPackage();
                }
                catch { return; }
                if (chestId.IsNone() || txId == 0L)
                    return;
                PendingDestroy pending = null;
                bool hit = false;
                try { hit = Pending.TryGetValue(chestId, out pending); } catch { hit = false; }
                if (!hit || pending == null)
                    return;
                if (pending.TxId != txId)
                {
                    try { TxLog.Info("container=" + TxLog.Zid(chestId) + " stale destroy response dropped tx=" + txId + " (live tx=" + pending.TxId + " owns the tomb replay)"); } catch { }
                    return;
                }
                try { Pending.Remove(chestId); } catch { }
                if (!granted)
                {
                    try { TellPlayerStatic(string.IsNullOrEmpty(reason) ? "Chest destroy refused." : reason); } catch { }
                    TxLog.Warn("container=" + TxLog.Zid(chestId) + " destroy refused: " + reason);
                    return;
                }
                Teardown(pending, body, alreadyGone);
            }
            catch
            {
            }
        }

        private static void SpawnSpillDrops(PendingDestroy pending, ZPackage body)
        {
            try
            {
                if (pending == null)
                    return;
                GameObject go = pending.Go;
                try
                {
                    if ((UnityEngine.Object)go == (UnityEngine.Object)null && (UnityEngine.Object)pending.Container != (UnityEngine.Object)null)
                        go = ((Component)pending.Container).gameObject;
                }
                catch { }
                Vector3 pos = pending.ActorPos;
                if ((UnityEngine.Object)go != (UnityEngine.Object)null)
                {
                    try { pos = go.transform.position; } catch { pos = pending.ActorPos; }
                }
                List<DecodedTake> drops = null;
                try { drops = TxCodec.ReadTakeResults(body); } catch { drops = null; }
                if (drops == null)
                    return;
                foreach (DecodedTake d in drops)
                {
                    try
                    {
                        if (d == null || d.Item == null || d.Accepted <= 0)
                            continue;
                        ItemData clone = d.Item.Clone();
                        clone.m_stack = d.Accepted;
                        Vector3 p = pos + UnityEngine.Random.insideUnitSphere * 1f + Vector3.up * 0.5f;
                        ItemDrop.DropItem(clone, d.Accepted, p, UnityEngine.Random.rotation);
                    }
                    catch { }
                }
            }
            catch
            {
            }
        }

        private static void Teardown(PendingDestroy pending, ZPackage body, bool alreadyGone)
        {
            try
            {
                if (pending == null)
                    return;
                Container container = pending.Container;
                GameObject go = pending.Go;
                try
                {
                    if ((UnityEngine.Object)go == (UnityEngine.Object)null && (UnityEngine.Object)container != (UnityEngine.Object)null)
                        go = ((Component)container).gameObject;
                }
                catch { }
                // GO-null is no longer an abort: fall back to the actor position
                // captured at submit (stored pending.ChestId is used
                // unconditionally — never re-read from the live NetView).
                ZDOID chestId = pending.ChestId;
                Vector3 pos = pending.ActorPos;
                if ((UnityEngine.Object)go != (UnityEngine.Object)null)
                {
                    try { pos = go.transform.position; } catch { pos = pending.ActorPos; }
                }
                try { TxLog.Info("container=" + TxLog.Zid(chestId) + " destroy teardown alreadyGone=" + alreadyGone); } catch { }
                try { SpawnSpillDrops(pending, body); } catch { }
                bool refund = false;
                try
                {
                    if (Refunded.Add(chestId))
                    {
                        refund = true;
                        while (Refunded.Count > RefundCap)
                        {
                            ZDOID oldest = ZDOID.None;
                            foreach (ZDOID k in Refunded) { oldest = k; break; }
                            if (oldest.IsNone())
                                break;
                            try { Refunded.Remove(oldest); } catch { break; }
                        }
                    }
                    else
                    {
                        try { TxLog.Info("container=" + TxLog.Zid(chestId) + " destroy refund already applied, skipping duplicate"); } catch { }
                    }
                }
                catch { refund = !alreadyGone; }
                if (refund)
                {
                    try
                    {
                        if ((UnityEngine.Object)pending.Piece != (UnityEngine.Object)null)
                            pending.Piece.DropResources();
                    }
                    catch { }
                }
                try
                {
                    Player player = Player.m_localPlayer;
                    if ((UnityEngine.Object)player != (UnityEngine.Object)null)
                    {
                        try { pending.Piece.m_placeEffect.Create(pos, go.transform.rotation, go.transform, 1f, -1, player.GetZDOID()); } catch { }
                        try
                        {
                            if (player.m_removeEffects != null)
                                player.m_removeEffects.Create(pos, Quaternion.identity, null, 1f, -1, player.GetZDOID());
                        }
                        catch { }
                    }
                }
                catch { }
                try
                {
                    InventoryGui gui = InventoryGui.instance;
                    if ((UnityEngine.Object)gui != (UnityEngine.Object)null && InventoryGui.IsVisible())
                    {
                        try
                        {
                            if ((UnityEngine.Object)InventoryAccess.CurrentContainer(gui) == (UnityEngine.Object)container)
                                gui.Hide();
                        }
                        catch { }
                    }
                }
                catch { }
                // No spill suppression needed: decompiled ZNetScene.Destroy only
                // detaches + deletes the ZDO (never invokes Container.OnDestroyed,
                // which fires solely via Destructible.Destroy, a path this flow
                // bypasses) — so the server blobs above are the only drops.
                try { ZNetScene.instance.Destroy(go); } catch { }
            }
            catch
            {
            }
        }

        // ============================ server ============================

        private static void OnDestroyRequest(long sender, ZPackage pkg)
        {
            try
            {
                if (!Plugin.IsActive || pkg == null)
                    return;
                ZNet net = null;
                try { net = ZNet.instance; } catch { net = null; }
                if ((UnityEngine.Object)net == (UnityEngine.Object)null || !net.IsServer())
                    return;
                bool authority = false;
                try { authority = ServerAuthority.IsAuthorityMode(); } catch { authority = false; }
                if (!authority)
                    return;
                ZDOID chestId = ZDOID.None;
                long txId = 0L;
                long playerId = 0L;
                Vector3 actorPos = Vector3.zero;
                try
                {
                    chestId = pkg.ReadZDOID();
                    txId = pkg.ReadLong();
                    playerId = pkg.ReadLong();
                    actorPos = pkg.ReadVector3();
                }
                catch { return; }
                if (chestId.IsNone() || txId == 0L)
                    return;
                ZDOMan man = null;
                try { man = ZDOMan.instance; } catch { man = null; }
                if (man == null)
                    return;
                ZDO zdo = null;
                try { zdo = man.GetZDO(chestId); } catch { zdo = null; }
                bool valid = false;
                try { valid = zdo != null && zdo.IsValid(); } catch { valid = false; }
                TombEntry tomb = null;
                try { Tombs.TryGetValue(txId, out tomb); } catch { tomb = null; }
                if (!valid)
                {
                    if (tomb != null)
                    {
                        bool fresh = false;
                        try { fresh = Time.realtimeSinceStartup - tomb.StoredAt <= TombExpirySeconds; } catch { fresh = false; }
                        if (!fresh)
                        {
                            try { Tombs.Remove(txId); } catch { }
                            tomb = null;
                        }
                        else if (tomb.Sender != sender)
                        {
                            // txId-keyed replay: a mismatched sender on an exact
                            // in-flight txId is packet replay — drop silently
                            // with no answer at all (not even ok-empty).
                            try { TxLog.Warn("container=" + TxLog.Zid(chestId) + " destroy tomb sender mismatch dropped tx=" + txId + " from=" + sender); } catch { }
                            return;
                        }
                    }
                    if (tomb != null)
                        AnswerDestroy(sender, chestId, txId, true, string.Empty, tomb.Blobs != null ? tomb.Blobs : EmptyBody(), true);
                    else
                        AnswerDestroy(sender, chestId, txId, true, string.Empty, EmptyBody(), true);
                    return;
                }
                if (tomb != null)
                {
                    try { Tombs.Remove(txId); } catch { }
                }
                bool eligible = false;
                try { eligible = ServerAuthority.IsServerManagedChestZdo(zdo); } catch { eligible = false; }
                if (!eligible)
                    return;
                List<ItemData> stock = null;
                string decodeWhy = "?";
                if (!TryDecodeStock(zdo, out stock, out decodeWhy))
                {
                    AnswerDestroy(sender, chestId, txId, false, "Chest contents unreadable. Try again.", EmptyBody(), false);
                    return;
                }
                if (!DestroyGates(zdo, sender, txId, playerId, actorPos, stock, out string denyWhy))
                {
                    AnswerDestroy(sender, chestId, txId, false, denyWhy, EmptyBody(), false);
                    return;
                }
                ZPackage spill = BuildSpill(stock);
                long me = 0L;
                try { me = ZNet.GetUID(); } catch { me = 0L; }
                bool claimed = false;
                try
                {
                    if (me != 0L)
                    {
                        zdo.SetOwner(me);
                        claimed = zdo.GetOwner() == me;
                    }
                }
                catch { claimed = false; }
                if (!claimed)
                {
                    AnswerDestroy(sender, chestId, txId, false, "Chest destroy unavailable. Try again.", EmptyBody(), false);
                    return;
                }
                try { man.DestroyZDO(zdo); } catch { }
                try { ServerChestSessions.Remove(chestId); } catch { }
                try
                {
                    TombEntry entry = new TombEntry();
                    entry.Sender = sender;
                    entry.Blobs = spill;
                    try { entry.StoredAt = Time.realtimeSinceStartup; } catch { entry.StoredAt = 0f; }
                    Tombs[txId] = entry;
                    while (Tombs.Count > TombCap)
                    {
                        long oldest = 0L;
                        foreach (long k in Tombs.Keys) { oldest = k; break; }
                        if (oldest == 0L)
                            break;
                        try { Tombs.Remove(oldest); } catch { break; }
                    }
                }
                catch { }
                try { Plugin.LogInstance.LogInfo((object)("[ChestTX] container=" + TxLog.Zid(chestId) + " DESTROYED by=" + sender + " spilled=" + (stock != null ? stock.Count : 0))); } catch { }
                AnswerDestroy(sender, chestId, txId, true, string.Empty, spill, false);
            }
            catch
            {
            }
        }

        private static bool TryDecodeStock(ZDO zdo, out List<ItemData> stock, out string why)
        {
            stock = null;
            why = "?";
            try
            {
                if (zdo == null)
                {
                    why = "null zdo";
                    return false;
                }
                byte[] bytes = null;
                try { bytes = TxSItemsGuard.CloneBytes(zdo.GetByteArray(ZDOVars.s_items)); } catch { bytes = null; }
                List<ItemData> list = new List<ItemData>();
                if (bytes != null)
                {
                    string vreason = "ok";
                    bool valid = false;
                    try { valid = TxSItemsGuard.TryValidate(bytes, out vreason); }
                    catch { valid = false; vreason = "validate-threw"; }
                    if (!valid)
                    {
                        why = "invalid s_items(" + vreason + ")";
                        return false;
                    }
                    ZNetScene scene = ZNetScene.instance;
                    GameObject prefab = null;
                    int w = 0;
                    int h = 0;
                    try
                    {
                        prefab = scene != null ? scene.GetPrefab(zdo.GetPrefab()) : null;
                        Container preset = prefab != null ? prefab.GetComponent<Container>() : null;
                        if ((UnityEngine.Object)preset == (UnityEngine.Object)null)
                        {
                            why = "no prefab container";
                            return false;
                        }
                        w = preset.m_width;
                        h = preset.m_height;
                    }
                    catch
                    {
                        why = "prefab read failed";
                        return false;
                    }
                    if (w < 1 || h < 1 || w > 24 || h > 24)
                    {
                        why = "insane dims";
                        return false;
                    }
                    Inventory inv = null;
                    try { inv = new Inventory("srv-destroy", null, w, h); } catch { inv = null; }
                    if (inv == null)
                    {
                        why = "inventory alloc failed";
                        return false;
                    }
                    try { inv.Load(new ZPackage(bytes)); }
                    catch (Exception ex)
                    {
                        why = "load failed: " + ex.Message;
                        return false;
                    }
                    try { list = inv.GetAllItems(); } catch { list = new List<ItemData>(); }
                }
                stock = list;
                why = "ok";
                return true;
            }
            catch
            {
                stock = null;
                why = "fault";
                return false;
            }
        }

        private static bool DestroyGates(ZDO zdo, long sender, long txId, long playerId, Vector3 actorPos, List<ItemData> stock, out string why)
        {
            why = "ok";
            try
            {
                if (!TxNet.IsCompatiblePeer(sender) && sender != ZNet.GetUID())
                {
                    why = "Incompatible mod version.";
                    return false;
                }
                if (TxDecision.ClassifySenderBinding(sender, txId) == TxDecision.SenderBinding.UnboundStranger)
                {
                    why = "Request not recognized.";
                    return false;
                }
                GameObject prefab = null;
                try
                {
                    ZNetScene scene = ZNetScene.instance;
                    prefab = scene != null ? scene.GetPrefab(zdo.GetPrefab()) : null;
                }
                catch { prefab = null; }
                if ((UnityEngine.Object)prefab == (UnityEngine.Object)null)
                {
                    why = "Chest unavailable.";
                    return false;
                }
                Piece piece = null;
                try { piece = prefab.GetComponent<Piece>(); } catch { piece = null; }
                if ((UnityEngine.Object)piece == (UnityEngine.Object)null || !piece.m_canBeRemoved)
                {
                    why = "This cannot be removed.";
                    return false;
                }
                string accessWhy = "ok";
                bool accessOk = false;
                try { accessOk = ServerAccess.CanUse(zdo, sender, playerId, actorPos, out accessWhy); }
                catch { accessOk = false; accessWhy = "unverifiable"; }
                if (!accessOk)
                {
                    if (accessWhy == "too-far")
                        why = "Too far away.";
                    else if (accessWhy == "ward")
                        why = "Warded area.";
                    else if (accessWhy == "vanilla-access")
                        why = "No access.";
                    else
                        why = "Destroy not allowed.";
                    return false;
                }
                try
                {
                    if ((UnityEngine.Object)prefab != (UnityEngine.Object)null)
                    {
                        Container preset = prefab.GetComponent<Container>();
                        if ((UnityEngine.Object)preset != (UnityEngine.Object)null
                            && (int)preset.m_privacy == 0 && stock != null && stock.Count > 0)
                        {
                            why = "Private chest: empty it first.";
                            return false;
                        }
                    }
                }
                catch { }
                return true;
            }
            catch
            {
                why = "Destroy not allowed.";
                return false;
            }
        }

        private static ZPackage BuildSpill(List<ItemData> stock)
        {
            ZPackage body = new ZPackage();
            try
            {
                if (stock == null || stock.Count == 0)
                {
                    body.Write(0);
                    return body;
                }
                TxOpCall fakeCall = new TxOpCall();
                fakeCall.Op = TxOp.TakeBatch;
                StoredResult r = new StoredResult();
                r.Op = TxOp.TakeBatch;
                r.Status = TxStatus.Accepted;
                foreach (ItemData item in stock)
                {
                    try
                    {
                        if (item == null)
                            continue;
                        ItemData clone = item.Clone();
                        int stack = Math.Max(1, clone.m_stack);
                        clone.m_stack = stack;
                        TakeEntry e = new TakeEntry();
                        e.PrefabHash = 0;
                        try
                        {
                            if (clone.m_dropPrefab != null)
                                e.PrefabHash = clone.m_dropPrefab.name.GetStableHashCode();
                        }
                        catch { e.PrefabHash = 0; }
                        e.Item = clone;
                        e.Accepted = stack;
                        r.Takes.Add(e);
                        r.Accepted.Add(stack);
                    }
                    catch { }
                }
                return ChestTxService.EncodeResultBody(fakeCall, r);
            }
            catch
            {
                ZPackage empty = new ZPackage();
                try { empty.Write(0); } catch { }
                return empty;
            }
        }

        private static ZPackage EmptyBody()
        {
            ZPackage body = new ZPackage();
            try { body.Write(0); } catch { }
            return body;
        }

        private static void AnswerDestroy(long sender, ZDOID chestId, long txId, bool granted, string reason, ZPackage body, bool alreadyGone)
        {
            try
            {
                ZPackage pkg = new ZPackage();
                try
                {
                    pkg.Write(chestId);
                    pkg.Write(txId);
                    pkg.Write(granted);
                    pkg.Write(reason != null ? reason : string.Empty);
                    pkg.Write(alreadyGone);
                    pkg.Write(body != null ? body : EmptyBody());
                }
                catch
                {
                    return;
                }
                ZRoutedRpc rpc = null;
                try { rpc = ZRoutedRpc.instance; } catch { rpc = null; }
                if (rpc == null)
                    return;
                try { rpc.InvokeRoutedRPC(sender, DestroyResponseRpc, new object[1] { pkg }); } catch { }
                TxLog.Info("container=" + TxLog.Zid(chestId) + " tx=" + txId + " destroy response granted=" + granted + " gone=" + alreadyGone + " to=" + sender);
            }
            catch
            {
            }
        }
    }
}
