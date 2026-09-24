using System;
using System.Collections.Generic;
using BestAutoSort.Core;
using BestAutoSort.Runtime;
using BestAutoSort.TxCore;
using Splatform;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Server-mediated remote chest upgrade (0.6.x): the server executes the
    /// ghost protocol in its per-chest manager queue; the client only sends a
    /// REQUEST (source ZDO id + authenticated sender + durable client nonce +
    /// tier) and waits for a validated receipt. Host-local upgrades route
    /// through the SAME queue (same op machine, same receipt shape).
    ///
    /// Ghost protocol (synchronous, no yield between mint and link):
    /// spawn replacement -&gt; SYNCHRONOUSLY write reverse link (op id +
    /// incomplete flag) -&gt; copy inventory non-destructively -&gt; validate
    /// Ready -&gt; pointer switch one-way -&gt; receipt -&gt; idempotent retire.
    ///
    /// - Mint-link window: spawn+link run in one try/catch; an exception
    ///   destroys the just-minted ghost and the op stays preparing. A hard
    ///   kill in the window leaves an UNLINKED ghost that is NEVER
    ///   auto-destroyed (could be a real new chest): quarantine + loud log +
    ///   manual reconcile (see OnContainerAwake / ReportUnlinkedGhost).
    /// - Ingredients are NEVER taken from the client inventory. They must be
    ///   pre-deposited in the source chest via normal ChestTX Takes; the
    ///   executor consumes them from the source chest at-most-once
    ///   (RecordSpend) and refunds flow only as an op-keyed entitlement
    ///   through the idempotent ClaimRefund step (dropped beside the new
    ///   chest exactly once, tracked in the persisted snapshot).
    /// - Crash recovery resumes from self-consistent ZDO snapshots only
    ///   (TxUpgradeSnapshot.Validate); torn snapshots fail closed.
    /// - Old TxOp.Upgrade frames are REFUSED for managed chests in authority
    ///   mode (see the OnTxRequest gate); only UpgradeRequest runs here.
    /// </summary>
    internal static partial class ChestTxService
    {
        private static readonly Dictionary<string, TxUpgradeManager> UpgradeManagers =
            new Dictionary<string, TxUpgradeManager>(StringComparer.Ordinal);

        private static readonly Dictionary<string, Container> UpgradeLive =
            new Dictionary<string, Container>(StringComparer.Ordinal);

        private const string UpOpKey = "BestAutoSort.UpgradeOp";
        private const string UpPhaseKey = "BestAutoSort.UpgradePhase";
        private const string UpGhostKey = "BestAutoSort.UpgradeGhost";
        private const string UpCountKey = "BestAutoSort.UpgradeCount";
        private const string UpHashKey = "BestAutoSort.UpgradeHash";
        private const string UpSpendKey = "BestAutoSort.UpgradeSpend";
        private const string UpSpendRecKey = "BestAutoSort.UpgradeSpendRec";
        private const string UpRefundKey = "BestAutoSort.UpgradeRefund";
        private const string UpRefundDoneKey = "BestAutoSort.UpgradeRefundDone";
        private const string UpReceiptKey = "BestAutoSort.UpgradeReceipt";
        private const string UpNonceKey = "BestAutoSort.UpgradeNonce";
        private const string UpSenderKey = "BestAutoSort.UpgradeSender";
        private const string UpTierKey = "BestAutoSort.UpgradeTier";
        private const string UpIncompleteKey = "BestAutoSort.UpgradeIncomplete";

        private const int UpgradePumpGuard = 32;

        private static string UpgradeChestId(Container container)
        {
            try
            {
                ZNetView nv = TxReflect.GetNetView(container);
                ZDO zdo = nv != null ? nv.GetZDO() : null;
                if (zdo == null)
                    return null;
                return zdo.m_uid.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static void UpgradeTrackLive(Container container)
        {
            try
            {
                string id = UpgradeChestId(container);
                if (!string.IsNullOrEmpty(id) && (UnityEngine.Object)container != (UnityEngine.Object)null)
                    UpgradeLive[id] = container;
            }
            catch
            {
            }
        }

        private static bool UpgradeResolve(string chestId, out Container container)
        {
            container = null;
            if (string.IsNullOrEmpty(chestId))
                return false;
            Container live;
            if (UpgradeLive.TryGetValue(chestId, out live) && (UnityEngine.Object)live != (UnityEngine.Object)null)
            {
                container = live;
                return true;
            }
            return false;
        }

        private static TxUpgradeManager UpgradeManagerFor(string sourceId, ITxUpgradeHost host)
        {
            TxUpgradeManager m;
            if (!UpgradeManagers.TryGetValue(sourceId, out m) || m == null)
            {
                m = new TxUpgradeManager(host);
                UpgradeManagers[sourceId] = m;
            }
            return m;
        }

        /// <summary>
        /// Awake sweep (cheap, never throws): re-registers live objects, resumes
        /// a persisted op snapshot, and quarantines loudly — never destroys — a
        /// ghost carrying an incomplete reverse link with no live op behind it.
        /// </summary>
        internal static void TxRemoteUpgradeOnAwake(Container container)
        {
            try
            {
                UpgradeTrackLive(container);
                ZNetView nv = TxReflect.GetNetView(container);
                ZDO zdo = nv != null ? nv.GetZDO() : null;
                if (zdo == null)
                    return;
                string ghostOp = zdo.GetString(UpOpKey, string.Empty);
                int incomplete = zdo.GetInt(UpIncompleteKey, 0);
                if (!string.IsNullOrEmpty(ghostOp) && incomplete == 1)
                {
                    // A ghost (or a source mid-op) woke up: is there a live op?
                    bool live = false;
                    foreach (KeyValuePair<string, TxUpgradeManager> kv in UpgradeManagers)
                    {
                        TxUpgradeOp op = kv.Value.Get(ghostOp);
                        if (op != null && !op.Quarantined && op.Phase != TxUpgradePhase.Retired)
                        {
                            live = true;
                            break;
                        }
                    }
                    if (!live)
                    {
                        TxLog.Warn("container=" + TxLog.Zid(zdo.m_uid) + " upgrade ghost/link with no live op (kept standing, manual reconcile): op=" + ghostOp);
                        TellPlayer("An unfinished chest upgrade was found. It was kept standing — reconcile manually.");
                    }
                    return;
                }
                string opId = zdo.GetString(UpOpKey, string.Empty);
                if (!string.IsNullOrEmpty(opId) && string.IsNullOrEmpty(ghostOp))
                {
                    // Source chest carrying a persisted op snapshot: resume it.
                    TxUpgradeSnapshot snap = ReadUpgradeSnapshot(zdo);
                    string why = null;
                    bool valid = snap != null && snap.Validate(out why);
                    if (valid)
                    {
                        string sourceId = UpgradeChestId(container);
                        TxUpgradeManager m = UpgradeManagerFor(sourceId, new ServerUpgradeHost());
                        string reason;
                        if (m.Get(snap.OpId) == null && m.TryRecover(snap, out reason))
                        {
                            TxLog.Warn("container=" + TxLog.Zid(zdo.m_uid) + " upgrade op resumed after restart: " + reason);
                            PumpUpgradeManager(sourceId, m);
                        }
                    }
                    else if (snap != null)
                    {
                        TxLog.Warn("container=" + TxLog.Zid(zdo.m_uid) + " torn upgrade snapshot refused (fail closed): " + why);
                    }
                }
            }
            catch (Exception ex)
            {
                TxLog.Warn("upgrade awake sweep failed (no behavior change): " + ex.Message);
            }
        }

        /// <summary>
        /// Client entry: send an upgrade REQUEST for a server-managed chest.
        /// Closes the old view up front; the new view opens ONLY on a validated
        /// receipt. A timeout re-queries the SAME txId/op (Submit pump) — never
        /// a new nonce — so retries never spawn twice.
        /// </summary>
        internal static void RequestUpgradeRemote(Container container, int tier, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone)
        {
            long txId = IssueTxId();
            if (txId == 0L)
            {
                TellPlayer("Chest request counter exhausted. Restart the game before retrying.");
                TxLog.Warn("upgrade submit refused: counter exhausted (fail closed)");
                return;
            }
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.UpgradeRequest;
            call.Tier = tier;
            call.UpgradeNonce = TxIdGen.CounterOf(txId);
            call.UpgradeFree = false;
            try
            {
                if ((UnityEngine.Object)InventoryGui.instance != (UnityEngine.Object)null)
                    InventoryGui.instance.Hide();
            }
            catch
            {
            }
            TellPlayer("Upgrade requested — the server is rebuilding the chest. Wait for the receipt.");
            SubmitPreIssued(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                CompleteUpgradeResponse(container, pkg, status, rev, disp, onDone);
            }, txId);
        }

        /// <summary>
        /// Host-local entry: the SAME queue as remote requests (MutateLocal
        /// serializes with them); the nonce derives from the fencing txId.
        /// </summary>
        internal static void RequestUpgradeLocal(Container container, int tier, Action<StoredResult> onDone)
        {
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.UpgradeRequest;
            call.Tier = tier;
            call.UpgradeNonce = 0u;
            try
            {
                Player localPlayer = Player.m_localPlayer;
                call.UpgradeFree = localPlayer != null && localPlayer.NoCostCheat();
            }
            catch
            {
                call.UpgradeFree = false;
            }
            try
            {
                if ((UnityEngine.Object)InventoryGui.instance != (UnityEngine.Object)null)
                    InventoryGui.instance.Hide();
            }
            catch
            {
            }
            MutateLocal(container, call, delegate (StoredResult r)
            {
                RefreshNow(container);
                CompleteUpgradeLocal(r, onDone);
            });
        }

        private static void CompleteUpgradeLocal(StoredResult r, Action<StoredResult> onDone)
        {
            try
            {
                if (r != null && (r.Status == TxStatus.Accepted || r.Status == TxStatus.Partial) && !string.IsNullOrEmpty(r.Receipt))
                    TellPlayer("Chest upgraded. Reopen it to continue.");
                else if (r != null && r.Status == TxStatus.Accepted)
                    TellPlayer("Chest upgrade applied but the new chest is unknown. Check the chest before retrying.");
                else if (r != null && r.Status == TxStatus.Rejected)
                    TellPlayer("Chest upgrade refused. See the log for details.");
                else
                    TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
            }
            catch
            {
            }
            if (onDone != null)
            {
                try { onDone(r); }
                catch (Exception ex) { TxLog.Warn("upgrade local completion failed: " + ex.Message); }
            }
        }

        private static void CompleteUpgradeResponse(Container container, ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone)
        {
            RefreshNow(container);
            try
            {
                if (disp == TxCompletionKind.Normal && (status == TxStatus.Accepted || status == TxStatus.Partial))
                {
                    string receipt = ReadUpgradeReceipt(pkg);
                    if (!string.IsNullOrEmpty(receipt))
                        TellPlayer("Chest upgraded. Reopen it to continue.");
                    else
                        TellPlayer("Chest upgrade applied but the new chest is unknown. Check the chest before retrying.");
                }
                else if (disp == TxCompletionKind.FailedNotCommitted)
                {
                    TellPlayer("Chest upgrade refused. See the log for details.");
                }
                else
                {
                    TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
                }
            }
            catch (Exception ex)
            {
                TxLog.Warn("upgrade response completion failed: " + ex.Message);
            }
            if (onDone != null)
            {
                try
                {
                    if (pkg != null)
                    {
                        try { pkg.SetPos(0); }
                        catch { }
                    }
                    onDone(pkg, status, rev, disp);
                }
                catch (Exception ex)
                {
                    TxLog.Warn("upgrade onDone failed: " + ex.Message);
                }
            }
        }

        private static string ReadUpgradeReceipt(ZPackage pkg)
        {
            if (pkg == null)
                return string.Empty;
            try
            {
                pkg.SetPos(0);
                int n = pkg.ReadInt();
                for (int i = 0; i < n; i++)
                    pkg.ReadInt();
                return pkg.ReadString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static StoredResult ExecuteUpgradeRequest(ChestState state, TxJob job)
        {
            StoredResult result = new StoredResult();
            result.Op = TxOp.UpgradeRequest;
            Container source = state.Container;
            if ((UnityEngine.Object)source == (UnityEngine.Object)null || !IsManager(source))
            {
                result.Status = TxStatus.Rejected;
                result.Accepted.Add(0);
                TxLog.Warn("op=UPGRADEREQ REJECT not-manager");
                return result;
            }
            bool managed = false;
            bool authority = false;
            try
            {
                authority = ServerAuthority.IsAuthorityMode();
                managed = ServerAuthority.IsServerManagedContainer(source);
            }
            catch
            {
            }
            bool isRemote = job.Sender != 0L && job.Sender != ZNet.GetUID();
            if (isRemote)
            {
                // Version + authority-mode gate: stale frames and unmanaged
                // targets never execute remotely. Old TxOp.Upgrade frames are
                // refused for managed chests at the OnTxRequest gate.
                if (!TxUpgradeGate.AcceptsUpgradeRequest(TxCodec.ProtoVersion, authority) || !managed)
                {
                    result.Status = TxStatus.Rejected;
                    result.Accepted.Add(0);
                    TxLog.Warn("op=UPGRADEREQ REJECT remote gate (authority=" + authority + " managed=" + managed + ")");
                    return result;
                }
            }
            string sourceId = UpgradeChestId(source);
            if (string.IsNullOrEmpty(sourceId))
            {
                result.Status = TxStatus.Rejected;
                result.Accepted.Add(0);
                TxLog.Warn("op=UPGRADEREQ REJECT no source ZDO");
                return result;
            }
            UpgradeTrackLive(source);
            long senderPeer = TxIdGen.PeerKey(job.Sender != 0L ? job.Sender : ZNet.GetUID());
            uint nonce = job.Call != null && job.Call.UpgradeNonce != 0u
                ? job.Call.UpgradeNonce
                : TxIdGen.CounterOf(job.TxId);
            int tier = job.Call != null ? job.Call.Tier : -1;
            int currentTier = ChestUpgradeService.ManagedTierForExecutor(source);
            if (!ChestUpgradePath.CanUpgrade(currentTier, tier))
            {
                result.Status = TxStatus.Rejected;
                result.Accepted.Add(0);
                TxLog.Warn("op=UPGRADEREQ REJECT bad tier path current=" + currentTier + " target=" + tier);
                return result;
            }
            TxUpgradeManager m = UpgradeManagerFor(sourceId, new ServerUpgradeHost());
            TxUpgradeOp op;
            try
            {
                op = m.Request(sourceId, senderPeer, nonce, tier, 0);
            }
            catch (Exception ex)
            {
                result.Status = TxStatus.Rejected;
                result.Accepted.Add(0);
                TxLog.Warn("op=UPGRADEREQ REJECT bad request: " + ex.Message);
                return result;
            }
            // Ingredients: pre-deposited in source via normal ChestTX Takes. The
            // op never charges the client inventory; the spend is recorded
            // at-most-once against the op (RecordSpend gate in the pump host).
            // Consumed BEFORE the copy phase (the ghost copies the remainder),
            // with the durable ZDO spend flag written synchronously at consume
            // time so a crash between consume and snapshot still resumes once.
            if (!op.SpendRecorded && !UpgradeConsumeSpend(source, tier, op, !isRemote && job.Call != null && job.Call.UpgradeFree))
            {
                // Unpaid and ghostless: drop the just-enqueued op so it can
                // never become a poisoned head (a later pump would advance it
                // with no spend check: free upgrade + head-of-line block).
                // TryAbandon refuses once the op owns world state.
                try { m.TryAbandon(op.OpId); }
                catch { }
                result.Status = TxStatus.Rejected;
                result.Accepted.Add(0);
                TxLog.Warn("op=UPGRADEREQ REJECT missing pre-deposited ingredients op=" + op.OpId);
                return result;
            }
            PumpUpgradeManager(sourceId, m);
            op = m.Get(op.OpId);
            if (op == null || op.Quarantined)
            {
                result.Status = TxStatus.UnknownTx;
                TxLog.Warn("op=UPGRADEREQ INDETERMINATE op=" + (op != null ? op.OpId : "?") + " (quarantined, manual reconcile)");
                return result;
            }
            if (op.Phase != TxUpgradePhase.Retired)
            {
                result.Status = TxStatus.UnknownTx;
                TxLog.Warn("op=UPGRADEREQ INDETERMINATE op=" + op.OpId + " phase=" + op.Phase + " (not retired)");
                return result;
            }
            // Refund: op-keyed entitlement, separate idempotent step (dropped
            // beside the new chest exactly once; the snapshot flag survives
            // crashes so a resume never re-drops).
            UpgradeDropRefund(op, currentTier);
            PumpUpgradeManager(sourceId, m);
            result.Status = TxStatus.Accepted;
            result.Accepted.Add(1);
            result.Receipt = op.ReceiptNewId;
            result.Revision = CurrentRevision(source);
            return result;
        }

        /// <summary>
        /// Spend from the SOURCE chest (pre-deposited ingredients), at-most-once
        /// per op. The durable ZDO spend flag is written synchronously with the
        /// removal, so a crash between consume and the next snapshot still
        /// resumes without re-consuming. Never touches any player inventory.
        /// </summary>
        private static bool UpgradeConsumeSpend(Container source, int tier, TxUpgradeOp op, bool cheatFree)
        {
            try
            {
                ZNetView nv = TxReflect.GetNetView(source);
                ZDO zdo = nv != null ? nv.GetZDO() : null;
                if (zdo != null && zdo.GetInt(UpSpendRecKey, 0) != 0)
                {
                    try { op.SpendRecorded = true; op.SpendTotal = zdo.GetInt(UpSpendKey, 0); }
                    catch { }
                    return true;
                }
                Piece piece;
                if (!ChestUpgradeService.TryGetUpgradeRecipe(tier, out piece) || (UnityEngine.Object)piece == (UnityEngine.Object)null)
                    return false;
                bool free = cheatFree;
                try { free = free || ((UnityEngine.Object)ZoneSystem.instance != (UnityEngine.Object)null && ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())); }
                catch { }
                int total = 0;
                if (!free)
                {
                    Inventory inv = source.GetInventory();
                    if (inv == null)
                        return false;
                    // Pre-check everything before removing anything.
                    foreach (Piece.Requirement req in piece.m_resources)
                    {
                        if (req == null || req.m_amount <= 0 || req.m_resItem == null || req.m_resItem.m_itemData == null || req.m_resItem.m_itemData.m_shared == null)
                            continue;
                        if (UpgradeCountIn(req.m_resItem.m_itemData.m_shared.m_name, inv) < req.m_amount)
                            return false;
                    }
                    foreach (Piece.Requirement req in piece.m_resources)
                    {
                        if (req == null || req.m_amount <= 0 || req.m_resItem == null || req.m_resItem.m_itemData == null || req.m_resItem.m_itemData.m_shared == null)
                            continue;
                        total += UpgradeRemoveFrom(req.m_resItem.m_itemData.m_shared.m_name, req.m_amount, inv);
                    }
                }
                if (zdo != null)
                {
                    zdo.Set(UpSpendKey, total);
                    zdo.Set(UpSpendRecKey, 1);
                }
                try
                {
                    TxUpgradeManager m;
                    // RAM flag through the at-most-once gate (second call replays).
                    string sourceId = UpgradeChestId(source);
                    if (sourceId != null && UpgradeManagers.TryGetValue(sourceId, out m) && m != null)
                        m.RecordSpend(op.OpId, total);
                    else
                    {
                        op.SpendRecorded = true;
                        op.SpendTotal = total;
                    }
                }
                catch
                {
                    op.SpendRecorded = true;
                    op.SpendTotal = total;
                }
                return true;
            }
            catch (Exception ex)
            {
                TxLog.Warn("op=UPGRADEREQ spend failed (fail closed): " + ex.Message);
                return false;
            }
        }

        private static int UpgradeCountIn(string name, Inventory inv)
        {
            int total = 0;
            foreach (ItemDrop.ItemData item in inv.GetAllItems())
            {
                if (item == null || item.m_shared == null)
                    continue;
                if (string.Equals(item.m_shared.m_name, name, StringComparison.Ordinal))
                    total += Math.Max(0, item.m_stack);
            }
            return total;
        }

        private static int UpgradeRemoveFrom(string name, int amount, Inventory inv)
        {
            int left = amount;
            List<ItemDrop.ItemData> stacks = new List<ItemDrop.ItemData>(inv.GetAllItems());
            foreach (ItemDrop.ItemData item in stacks)
            {
                if (left <= 0)
                    break;
                if (item == null || item.m_shared == null)
                    continue;
                if (!string.Equals(item.m_shared.m_name, name, StringComparison.Ordinal))
                    continue;
                int take = Math.Min(left, Math.Max(0, item.m_stack));
                if (take > 0 && inv.RemoveItem(item, take))
                    left -= take;
            }
            return amount - left;
        }

        /// <summary>
        /// Refund of the destroyed tier beside the new chest: op-keyed,
        /// idempotent (ZDO flag + ClaimRefund gate — a resume never re-drops).
        /// </summary>
        private static void UpgradeDropRefund(TxUpgradeOp op, int oldTier)
        {
            try
            {
                if (op == null || op.RefundClaimed || string.IsNullOrEmpty(op.ReceiptNewId))
                    return;
                Container ghost;
                if (!UpgradeResolve(op.ReceiptNewId, out ghost) || (UnityEngine.Object)ghost == (UnityEngine.Object)null)
                    return;
                ZNetView nv = TxReflect.GetNetView(ghost);
                ZDO zdo = nv != null ? nv.GetZDO() : null;
                if (zdo != null && zdo.GetInt(UpRefundDoneKey, 0) != 0)
                {
                    try
                    {
                        string sourceId = op.SourceId;
                        TxUpgradeManager m;
                        if (UpgradeManagers.TryGetValue(sourceId, out m) && m != null)
                            m.ClaimRefund(op.OpId, zdo.GetInt(UpRefundKey, 0));
                    }
                    catch { }
                    op.RefundClaimed = true;
                    return;
                }
                Piece piece;
                if (!ChestUpgradeService.TryGetUpgradeRecipe(oldTier, out piece) || (UnityEngine.Object)piece == (UnityEngine.Object)null)
                    return;
                int dropped = 0;
                Vector3 at = ((Component)ghost).transform.position + Vector3.up * 0.5f;
                int n = 0;
                foreach (Piece.Requirement req in piece.m_resources)
                {
                    if (req == null || !req.m_recover || req.m_amount <= 0 || req.m_resItem == null || req.m_resItem.m_itemData == null || req.m_resItem.m_itemData.m_shared == null)
                        continue;
                    int left = req.m_amount;
                    int max = Math.Max(1, req.m_resItem.m_itemData.m_shared.m_maxStackSize);
                    while (left > 0)
                    {
                        int stack = Math.Min(left, max);
                        try
                        {
                            ItemDrop.ItemData drop = req.m_resItem.m_itemData.Clone();
                            drop.m_stack = stack;
                            if ((UnityEngine.Object)drop.m_dropPrefab == (UnityEngine.Object)null)
                                drop.m_dropPrefab = ((Component)req.m_resItem).gameObject;
                            Vector3 pos = at + new Vector3((float)(n % 3) * 0.5f, 0.2f * (float)(n / 3), 0f);
                            ItemDrop.DropItem(drop, stack, pos, Quaternion.identity);
                            dropped += stack;
                        }
                        catch (Exception ex)
                        {
                            TxLog.Warn("op=UPGRADEREQ refund drop failed: " + ex.Message);
                            break;
                        }
                        left -= stack;
                        n++;
                    }
                }
                if (zdo != null)
                {
                    zdo.Set(UpRefundKey, dropped);
                    zdo.Set(UpRefundDoneKey, 1);
                }
                try
                {
                    TxUpgradeManager m;
                    if (UpgradeManagers.TryGetValue(op.SourceId, out m) && m != null)
                        m.ClaimRefund(op.OpId, dropped);
                    else
                    {
                        op.RefundClaimed = true;
                        op.RefundTotal = dropped;
                    }
                }
                catch
                {
                    op.RefundClaimed = true;
                    op.RefundTotal = dropped;
                }
            }
            catch (Exception ex)
            {
                TxLog.Warn("op=UPGRADEREQ refund failed (entitlement kept, manual check): " + ex.Message);
            }
        }

        private static void PumpUpgradeManager(string sourceId, TxUpgradeManager m)
        {
            int guard = UpgradePumpGuard;
            try
            {
                while (guard-- > 0)
                {
                    bool more;
                    try
                    {
                        more = m.Pump();
                    }
                    catch (TxUpgradeCrashException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        TxLog.Warn("upgrade pump step failed (op stays, fail closed): " + ex.Message);
                        break;
                    }
                    if (!more)
                        break;
                }
            }
            finally
            {
                try
                {
                    Container src;
                    if (UpgradeResolve(sourceId, out src) && (UnityEngine.Object)src != (UnityEngine.Object)null)
                    {
                        ZNetView nv = TxReflect.GetNetView(src);
                        ZDO zdo = nv != null ? nv.GetZDO() : null;
                        if (zdo != null)
                        {
                            // Persist the head snapshot best-effort; a torn write
                            // is refused fail-closed on read (Validate gate).
                            WriteUpgradeSnapshot(zdo, m);
                        }
                    }
                }
                catch (Exception ex)
                {
                    TxLog.Warn("upgrade snapshot persist failed (fail closed on read): " + ex.Message);
                }
            }
        }

        private static void WriteUpgradeSnapshot(ZDO zdo, TxUpgradeManager m)
        {
            // Best-effort durable op record: every key is plain ZDO state and the
            // reader refuses torn combinations fail-closed (Validate gate), so a
            // crash between these Sets can never resume a half-op.
            try
            {
                // Head op only: later ops queue behind it and carry no snapshot
                // until they become head (their identity is the fencing txId).
                System.Reflection.FieldInfo orderField = typeof(TxUpgradeManager).GetField("_order",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                System.Reflection.FieldInfo opsField = typeof(TxUpgradeManager).GetField("_ops",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (orderField == null || opsField == null)
                    return;
                Queue<string> order = orderField.GetValue(m) as Queue<string>;
                Dictionary<string, TxUpgradeOp> ops = opsField.GetValue(m) as Dictionary<string, TxUpgradeOp>;
                if (order == null || ops == null || order.Count == 0)
                    return;
                TxUpgradeOp op;
                if (!ops.TryGetValue(order.Peek(), out op) || op == null)
                    return;
                TxUpgradeSnapshot snap = m.ExportSnapshot(op.OpId);
                if (snap == null)
                {
                    zdo.Set(UpOpKey, string.Empty);
                    return;
                }
                zdo.Set(UpOpKey, snap.OpId ?? string.Empty);
                zdo.Set(UpPhaseKey, (int)snap.Phase);
                zdo.Set(UpGhostKey, snap.GhostId ?? string.Empty);
                zdo.Set(UpCountKey, snap.ItemCount);
                zdo.Set(UpHashKey, snap.ItemHash);
                zdo.Set(UpSpendKey, snap.SpendTotal);
                zdo.Set(UpSpendRecKey, snap.SpendRecorded ? 1 : 0);
                zdo.Set(UpRefundKey, snap.RefundTotal);
                zdo.Set(UpRefundDoneKey, snap.RefundClaimed ? 1 : 0);
                zdo.Set(UpReceiptKey, snap.ReceiptNewId ?? string.Empty);
                zdo.Set(UpNonceKey, (long)snap.Nonce);
                zdo.Set(UpSenderKey, snap.SenderPeer);
                zdo.Set(UpTierKey, snap.Tier);
            }
            catch (Exception ex)
            {
                TxLog.Warn("upgrade snapshot write failed: " + ex.Message);
            }
        }

        private static TxUpgradeSnapshot ReadUpgradeSnapshot(ZDO zdo)
        {
            try
            {
                string opId = zdo.GetString(UpOpKey, string.Empty);
                if (string.IsNullOrEmpty(opId))
                    return null;
                TxUpgradeSnapshot snap = new TxUpgradeSnapshot();
                snap.OpId = opId;
                snap.Phase = (TxUpgradePhase)zdo.GetInt(UpPhaseKey, 0);
                string ghost = zdo.GetString(UpGhostKey, string.Empty);
                snap.GhostId = string.IsNullOrEmpty(ghost) ? null : ghost;
                snap.ItemCount = zdo.GetInt(UpCountKey, 0);
                snap.ItemHash = zdo.GetInt(UpHashKey, 0);
                snap.SpendTotal = zdo.GetInt(UpSpendKey, 0);
                snap.SpendRecorded = zdo.GetInt(UpSpendRecKey, 0) != 0;
                snap.RefundTotal = zdo.GetInt(UpRefundKey, 0);
                snap.RefundClaimed = zdo.GetInt(UpRefundDoneKey, 0) != 0;
                string receipt = zdo.GetString(UpReceiptKey, string.Empty);
                snap.ReceiptNewId = string.IsNullOrEmpty(receipt) ? null : receipt;
                snap.Nonce = (uint)zdo.GetLong(UpNonceKey, 0L);
                snap.SenderPeer = zdo.GetLong(UpSenderKey, 0L);
                snap.Tier = zdo.GetInt(UpTierKey, -1);
                snap.Recipe = 0;
                // Source identity is the ZDO carrying the snapshot.
                snap.SourceId = zdo.m_uid.ToString();
                return snap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Production ghost host over live Unity objects.</summary>
        private sealed class ServerUpgradeHost : ITxUpgradeHost
        {
            public string SpawnGhost(string sourceId, int tier)
            {
                Container source;
                if (!UpgradeResolve(sourceId, out source) || (UnityEngine.Object)source == (UnityEngine.Object)null)
                    throw new InvalidOperationException("source chest not loaded");
                if ((UnityEngine.Object)ZNetScene.instance == (UnityEngine.Object)null)
                    throw new InvalidOperationException("network scene unavailable");
                GameObject prefab = ZNetScene.instance.GetPrefab(ChestUpgradePath.PrefabForTier(tier));
                if ((UnityEngine.Object)prefab == (UnityEngine.Object)null)
                    throw new InvalidOperationException("target prefab unavailable");
                GameObject go = UnityEngine.Object.Instantiate<GameObject>(
                    prefab, ((Component)source).transform.position, ((Component)source).transform.rotation);
                Container ghost = go != null ? go.GetComponent<Container>() : null;
                ZNetView ghostView = go != null ? go.GetComponent<ZNetView>() : null;
                Piece ghostPiece = go != null ? go.GetComponent<Piece>() : null;
                ZDO ghostZdo = ghostView != null ? ghostView.GetZDO() : null;
                if ((UnityEngine.Object)ghost == (UnityEngine.Object)null || ghostView == null
                    || (UnityEngine.Object)ghostPiece == (UnityEngine.Object)null || ghostZdo == null)
                    throw new InvalidOperationException("replacement chest did not initialize");
                if (!ghostView.IsOwner())
                    throw new InvalidOperationException("replacement chest not owned by the server");
                // Creator + tier marker mirror the legacy replacement path.
                try
                {
                    Piece sourcePiece = ((Component)source).GetComponent<Piece>();
                    ZNetView sourceView = TxReflect.GetNetView(source);
                    if ((UnityEngine.Object)sourcePiece != (UnityEngine.Object)null)
                        ghostPiece.SetCreator(sourcePiece.GetCreator(), PlatformUserID.None);
                }
                catch
                {
                }
                ghostZdo.Set("BestAutoSort.ChestTier", tier);
                // Rule + reserves migrate with the chest (same as legacy path).
                try
                {
                    string ruleError;
                    ChestRuleStore.TryWrite(ghost, ChestRuleStore.Read(source), out ruleError);
                    ChestReserveStore.Write(ghost, ChestReserveStore.ReadRaw(source));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("rule migration failed: " + ex.Message);
                }
                string ghostId = ghostZdo.m_uid.ToString();
                UpgradeLive[ghostId] = ghost;
                return ghostId;
            }

            public void WriteReverseLink(string ghostId, string opId)
            {
                Container ghost;
                if (!UpgradeResolve(ghostId, out ghost) || (UnityEngine.Object)ghost == (UnityEngine.Object)null)
                    throw new InvalidOperationException("ghost not loaded for link-write");
                ZNetView nv = TxReflect.GetNetView(ghost);
                ZDO zdo = nv != null ? nv.GetZDO() : null;
                if (zdo == null)
                    throw new InvalidOperationException("ghost has no ZDO for link-write");
                // SYNCHRONOUS reverse-link write (no yield between mint and link).
                zdo.Set(UpOpKey, opId);
                zdo.Set(UpIncompleteKey, 1);
            }

            public string FindGhostByLink(string opId)
            {
                try
                {
                    foreach (KeyValuePair<string, Container> kv in UpgradeLive)
                    {
                        if ((UnityEngine.Object)kv.Value == (UnityEngine.Object)null)
                            continue;
                        ZNetView nv = TxReflect.GetNetView(kv.Value);
                        ZDO zdo = nv != null ? nv.GetZDO() : null;
                        if (zdo == null)
                            continue;
                        if (string.Equals(zdo.GetString(UpOpKey, string.Empty), opId, StringComparison.Ordinal)
                            && zdo.GetInt(UpIncompleteKey, 0) == 1)
                            return kv.Key;
                    }
                }
                catch
                {
                }
                return null;
            }

            public void CopyInventoryNonDestructive(string sourceId, string ghostId)
            {
                Container source;
                Container ghost;
                if (!UpgradeResolve(sourceId, out source) || (UnityEngine.Object)source == (UnityEngine.Object)null)
                    throw new InvalidOperationException("source not loaded for copy");
                if (!UpgradeResolve(ghostId, out ghost) || (UnityEngine.Object)ghost == (UnityEngine.Object)null)
                    throw new InvalidOperationException("ghost not loaded for copy");
                Inventory src = source.GetInventory();
                Inventory dst = ghost.GetInventory();
                if (src == null || dst == null)
                    throw new InvalidOperationException("missing inventory for copy");
                // Non-destructive: clone every stack into the ghost; the source
                // keeps everything until the pointer switch. Positions are
                // best-effort (auto-place); conservation is exact by multiset.
                List<ItemDrop.ItemData> stacks = new List<ItemDrop.ItemData>(src.GetAllItems());
                foreach (ItemDrop.ItemData item in stacks)
                {
                    if (item == null || item.m_shared == null)
                        continue;
                    ItemDrop.ItemData clone = item.Clone();
                    if (!dst.AddItem(clone))
                        throw new InvalidOperationException("ghost cannot hold the copied contents");
                }
            }

            public int CountItems(string chestId)
            {
                return UpgradeItemUnits(chestId);
            }

            public int HashItems(string chestId)
            {
                return UpgradeItemHash(chestId);
            }

            public bool ValidateReady(string ghostId, int wantCount, int wantHash)
            {
                return CountItems(ghostId) == wantCount && HashItems(ghostId) == wantHash;
            }

            public string PointerSwitchOneWay(string sourceId, string ghostId)
            {
                Container source;
                Container ghost;
                if (!UpgradeResolve(sourceId, out source) || (UnityEngine.Object)source == (UnityEngine.Object)null)
                    throw new InvalidOperationException("source not loaded for pointer switch");
                if (!UpgradeResolve(ghostId, out ghost) || (UnityEngine.Object)ghost == (UnityEngine.Object)null)
                    throw new InvalidOperationException("ghost not loaded for pointer switch");
                // One-way: the ghost is now the chest. Clear the source
                // (its contents were verified in the ghost), persist the new
                // chest, mark the ghost complete, capture the receipt.
                Inventory src = source.GetInventory();
                if (src != null)
                {
                    List<ItemDrop.ItemData> left = new List<ItemDrop.ItemData>(src.GetAllItems());
                    foreach (ItemDrop.ItemData item in left)
                    {
                        if (item == null)
                            continue;
                        src.RemoveItem(item, item.m_stack);
                    }
                }
                try
                {
                    ChestUpgradeService.ApplyState(ghost);
                }
                catch (Exception ex)
                {
                    Plugin.LogInstance.LogWarning((object)("Upgrade ghost compact failed (contents safe): " + ex));
                }
                TxReflect.UpdateRows(ghost);
                TxReflect.SaveContainer(ghost);
                ZNetView ghostView = TxReflect.GetNetView(ghost);
                ZDO ghostZdo = ghostView != null ? ghostView.GetZDO() : null;
                if (ghostZdo == null)
                    throw new InvalidOperationException("new chest lost its ZDO at pointer switch");
                string newId = ghostZdo.m_uid.ToString();
                ghostZdo.Set(UpIncompleteKey, 0);
                ghostZdo.Set(UpReceiptKey, newId);
                // Source carries the receipt too (durable until retire).
                ZNetView sourceView = TxReflect.GetNetView(source);
                ZDO sourceZdo = sourceView != null ? sourceView.GetZDO() : null;
                if (sourceZdo != null)
                    sourceZdo.Set(UpReceiptKey, newId);
                return newId;
            }

            public void RetireSource(string sourceId, string newId)
            {
                // Idempotent retire: missing source = already retired.
                Container source;
                if (!UpgradeResolve(sourceId, out source) || (UnityEngine.Object)source == (UnityEngine.Object)null)
                    return;
                ZNetView nv = TxReflect.GetNetView(source);
                try
                {
                    if (nv != null && nv.IsValid())
                        nv.Destroy();
                    else
                        UnityEngine.Object.Destroy((UnityEngine.Object)((Component)source).gameObject);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("source retire failed: " + ex.Message);
                }
                UpgradeLive.Remove(sourceId);
            }

            public void DestroyGhost(string ghostId)
            {
                Container ghost;
                if (UpgradeResolve(ghostId, out ghost) && (UnityEngine.Object)ghost != (UnityEngine.Object)null)
                {
                    ZNetView nv = TxReflect.GetNetView(ghost);
                    try
                    {
                        if (nv != null && nv.IsValid() && nv.IsOwner())
                            nv.Destroy();
                        else
                            UnityEngine.Object.Destroy((UnityEngine.Object)((Component)ghost).gameObject);
                    }
                    catch
                    {
                    }
                }
                UpgradeLive.Remove(ghostId);
            }

            public void LogLoud(string message)
            {
                try
                {
                    Plugin.LogInstance.LogWarning((object)message);
                }
                catch
                {
                }
                TxLog.Warn(message);
            }

            public void CrashHook(TxUpgradeCrashPoint point)
            {
            }
        }

        private static int UpgradeItemUnits(string chestId)
        {
            try
            {
                Container c;
                if (!UpgradeResolve(chestId, out c) || (UnityEngine.Object)c == (UnityEngine.Object)null)
                    return 0;
                Inventory inv = c.GetInventory();
                if (inv == null)
                    return 0;
                int total = 0;
                foreach (ItemDrop.ItemData item in inv.GetAllItems())
                {
                    if (item == null || item.m_shared == null)
                        continue;
                    total += Math.Max(0, item.m_stack);
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }

        private static int UpgradeItemHash(string chestId)
        {
            try
            {
                Container c;
                if (!UpgradeResolve(chestId, out c) || (UnityEngine.Object)c == (UnityEngine.Object)null)
                    return 0;
                Inventory inv = c.GetInventory();
                if (inv == null)
                    return 0;
                // Multiset hash over (name, quality, variant, total stack): slot
                // merges during auto-place preserve it, so conservation holds
                // even when grid positions change across tiers.
                Dictionary<string, int> totals = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (ItemDrop.ItemData item in inv.GetAllItems())
                {
                    if (item == null || item.m_shared == null)
                        continue;
                    string key = item.m_shared.m_name + "#" + item.m_quality + "#" + item.m_variant;
                    int cur;
                    totals.TryGetValue(key, out cur);
                    totals[key] = cur + Math.Max(0, item.m_stack);
                }
                List<string> keys = new List<string>(totals.Keys);
                keys.Sort(StringComparer.Ordinal);
                int h = 17;
                foreach (string key in keys)
                    h = unchecked(h * 31 + key.GetHashCode()) * 31 + totals[key];
                return h;
            }
            catch
            {
                return 0;
            }
        }
    }
}
