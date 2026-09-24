using System;
using System.Collections.Generic;
using BestAutoSort.Core;
using BestAutoSort.Runtime;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// ChestTX client side: responses, timeouts/retries/Query, completions,
    /// viewer refresh, presence, takeover, main Pump.
    /// </summary>
    internal static partial class ChestTxService
    {
        // ============================ responses ============================

        private static void Respond(Container container, long peer, long txId, TxStatus status, uint revision, ZPackage body, bool totalsOnly, TxOp op)
        {
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            if (peer == ZNet.GetUID())
                TxLog.Info("tx=" + txId + " respond loopback (manager==requester)");
            ZPackage pkg = new ZPackage();
            TxCodec.WriteResponseHeader(pkg, txId, status, revision, totalsOnly);
            pkg.Write(body);
            if (body != null)
            {
                try
                {
                    int bytes = pkg.GetArray().Length;
                    if (bytes > 4096)
                        TxLog.Info("tx=" + txId + " respond bytes=" + bytes + " (large)");
                }
                catch
                {
                }
            }
            try
            {
                netView.InvokeRPC(peer, TxNet.TxResponseRpc, pkg);
            }
            catch (Exception ex)
            {
                TxLog.Warn("tx=" + txId + " respond failed: " + ex.Message);
            }
        }

        private static ZPackage EncodeResultBody(TxOpCall call, StoredResult r)
        {
            ZPackage body = new ZPackage();
            switch (call.Op)
            {
                case TxOp.Add:
                case TxOp.AddBatch:
                case TxOp.Move:
                case TxOp.Sort:
                case TxOp.Upgrade:
                case TxOp.SetRule:
                    body.Write(r.Accepted.Count);
                    for (int i = 0; i < r.Accepted.Count; i++)
                        body.Write(r.Accepted[i]);
                    break;
                case TxOp.Take:
                case TxOp.TakeBatch:
                    body.Write(r.Takes.Count);
                    for (int i = 0; i < r.Takes.Count; i++)
                    {
                        TakeEntry e = r.Takes[i];
                        if (e == null)
                        {
                            body.Write(0);
                            body.Write(new ZPackage());
                            body.Write(0);
                        }
                        else
                        {
                            ZPackage inner = new ZPackage();
                            e.Item.Save(inner);
                            body.Write(e.PrefabHash);
                            body.Write(inner);
                            body.Write(e.Accepted);
                        }
                    }
                    break;
                default:
                    break;
            }
            return body;
        }

        private static void RespondQuery(Container container, long sender, long txId)
        {
            ChestState state = GetState(container);
            if (state == null)
                return;
            StoredResult cached;
            if (state.Processed.TryGetValue(txId, out cached))
            {
                // Original outcome (never coerced to Duplicate): the same txId
                // keeps its answer forever. A stranger gets Rejected with no
                // payload — never another sender's Take bytes. Canonical identity.
                if (cached.Sender != 0L && TxIdGen.PeerKey(cached.Sender) != TxIdGen.PeerKey(sender))
                {
                    TxLog.Warn("container=" + TxLog.Zid(state.ZdoId) + " tx=" + txId + " QUERY sender mismatch");
                    Respond(container, sender, txId, TxStatus.Rejected, cached.Revision, new ZPackage(), false, cached.Op);
                    return;
                }
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " tx=" + txId + " QUERY hit status=" + cached.Status);
                Respond(container, sender, txId, cached.Status, cached.Revision,
                    EncodeCachedBody(cached), cached.TotalsOnly, cached.Op);
                return;
            }
            Respond(container, sender, txId, TxStatus.UnknownTx, CurrentRevision(container), new ZPackage(), true, TxOp.Query);
        }

        private static ZPackage EncodeCachedBody(StoredResult cached)
        {
            ZPackage body = new ZPackage();
            if (cached.TotalsOnly)
            {
                // Same shape as a normal result: Add*/others — [1][total];
                // Take* — [0] (no items, see OnTxResponse).
                if (cached.Op == TxOp.Take || cached.Op == TxOp.TakeBatch)
                {
                    body.Write(0);
                    return body;
                }
                body.Write(1);
                body.Write(cached.AcceptedTotal());
                return body;
            }
            TxOpCall fake = new TxOpCall();
            fake.Op = cached.Op;
            return EncodeResultBody(fake, cached);
        }

        // ============================ client: responses ============================

        private static void OnTxResponse(Container container, long sender, ZPackage pkg)
        {
            if (!Plugin.IsActive)
                return;
            long txId;
            TxStatus status;
            uint revision;
            bool totalsOnly;
            if (!TxCodec.ReadResponseHeader(pkg, out txId, out status, out revision, out totalsOnly))
            {
                TxLog.Warn("bad response packet from " + sender);
                return;
            }
            PendingTx pending;
            if (!Pending.TryGetValue(txId, out pending))
            {
                // Late duplicate after completion: refresh only, do NOT touch items.
                RefreshNow(container);
                TxLog.Info("tx=" + txId + " late-duplicate ignored (already completed)");
                return;
            }
            // NOTE: Pending is removed only by a terminal completion below. Decode
            // failures keep it alive and force a re-query: the manager still holds
            // the cached result, so dropping the bytes must never void items.
            TxLog.Info("tx=" + txId + " response status=" + status + " rev=" + revision + (totalsOnly ? " totals-only" : ""));
            ZPackage body;
            try
            {
                body = pkg.ReadPackage();
            }
            catch (Exception ex)
            {
                TxLog.Warn("tx=" + txId + " response body unreadable: " + ex.Message);
                RefreshNow(pending.Container);
                if (!ForceRefetch(txId))
                    FinalizeUnknownTx(txId);
                return;
            }
            pkg = body;
            // Status-first (issue #10): Rejected is known-not-committed; wire
            // UnknownTx (evicted/lost record) says nothing about commitment and
            // is Indeterminate — never "applied", never a rejection. A committed
            // totals-only replay never fabricates per-item payloads.
            TxCompletionKind disp = TxResponsePolicy.Classify(status, totalsOnly, pending.Op, pending.ExpectedItems);
            if (disp == TxCompletionKind.Indeterminate)
            {
                // Terminal, exactly-once: credit nothing, remove nothing,
                // cascade nothing. The chest may or may not have applied the
                // mutation — the user must verify before retrying as a NEW tx.
                TxLog.Warn("tx=" + txId + " outcome UNKNOWN op=" + pending.Op + " (manager has no record, nothing credited/removed, no cascade)");
                TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
                CompletePendingTerminal(txId, EmptyCountBody(), status, revision, disp);
                return;
            }
            if (disp == TxCompletionKind.FailedNotCommitted)
            {
                // Rejected: known-not-committed. Nothing applied: credit and
                // remove nothing, complete terminally so no callback chain stalls.
                TxLog.Warn("tx=" + txId + " REJECTED op=" + pending.Op + " (nothing applied, nothing credited/removed)");
                CompletePendingTerminal(txId, EmptyCountBody(), status, revision, disp);
                return;
            }
            if (disp == TxCompletionKind.CommittedTakeDetailsUnavailable)
            {
                // Totals-only Take after handoff carries no items: the chest was
                // debited and the payload is gone. Loud, never silent — but the
                // callback still fires with a valid empty Take result so
                // automation (feed/restock/trash/prefetch) terminates instead of
                // hanging. Credit nothing, fabricate nothing.
                TxLog.Warn("tx=" + txId + " COMMITTED Take totals-only: applied but items unrecoverable from cache (nothing credited)");
                TellPlayer("Chest applied the move but the items cannot be recovered. Check the chest.");
                CompletePendingTerminal(txId, EmptyCountBody(), status, revision, disp);
                return;
            }
            if (disp == TxCompletionKind.CommittedMultiAddDetailsUnavailable)
            {
                // A totals-only AddBatch body is [1][total]: unattributable across
                // items. Removing [total,0,...] would over-remove the first key,
                // and retrying/cascading would duplicate what is already stored.
                // Terminal with zero removal; the Submit layer blocks the cascade.
                TxLog.Warn("tx=" + txId + " COMMITTED multi-add totals-only: applied but per-item counts unknown (nothing removed, no auto-retry)");
                TellPlayer("Chest applied the move but per-item counts are unknown. Check the chest before retrying.");
                CompletePendingTerminal(txId, EmptyCountBody(), status, revision, disp);
                return;
            }
            if (!totalsOnly && IsTakeOp(pending.Op) && !TakeBodyReadable(pkg))
            {
                TxLog.Warn("tx=" + txId + " take body corrupt: re-querying instead of voiding");
                RefreshNow(pending.Container);
                if (!ForceRefetch(txId))
                    FinalizeUnknownTx(txId);
                return;
            }
            if (!totalsOnly && !IsTakeOp(pending.Op) && pending.ExpectedItems >= 0 && !AddBodyMatchesArity(pkg, pending.ExpectedItems))
            {
                TxLog.Warn("tx=" + txId + " add body corrupt: re-querying instead of misattributing");
                RefreshNow(pending.Container);
                if (!ForceRefetch(txId))
                    FinalizeUnknownTx(txId);
                return;
            }
            CompletePendingTerminal(txId, pkg, status, revision, disp);
        }

        /// <summary>
        /// Exactly-once terminal completion (issue #10): remove from Pending before
        /// invoking the callback, release claims once, invoke inside try/catch,
        /// then refresh. A late duplicate finds no pending entry and touches nothing.
        /// Decode failures still eligible for Query must stay pending; only
        /// terminal paths use this helper.
        /// </summary>
        private static void CompletePendingTerminal(long txId, ZPackage body, TxStatus status, uint rev, TxCompletionKind disp)
        {
            PendingTx p;
            if (!Pending.TryGetValue(txId, out p))
                return;
            Pending.Remove(txId);
            ReleaseClaimed(p.Claimed);
            try
            {
                if (p.OnResponse != null)
                    p.OnResponse(body, status, rev, disp);
            }
            catch (Exception ex)
            {
                TxLog.Error("tx=" + txId + " completion failed: " + ex.Message);
            }
            RefreshNow(p.Container);
        }

        /// <summary>
        /// Valid empty result body ([0]): decodes as zero takes / zero accepted
        /// for every arity, so completions credit/remove nothing and still fire.
        /// </summary>
        private static ZPackage EmptyCountBody()
        {
            ZPackage body = new ZPackage();
            body.Write(0);
            body.SetPos(0);
            return body;
        }

        // ============================ client: completions ============================

        /// <summary>
        /// Re-fetch a cached result for an undecided tx: sends one last Query and
        /// extends the deadline once. Returns false when no query is left to spend
        /// (caller must finalize loud).
        /// </summary>
        internal static bool ForceRefetch(long txId)
        {
            PendingTx p;
            if (!Pending.TryGetValue(txId, out p) || p.FinalQuerySent)
                return false;
            if ((Object)p.Container == (Object)null)
                return false;
            p.FinalQuerySent = true;
            p.QuerySent = true;
            SendQuery(p);
            p.Attempts++;
            p.Deadline = Time.realtimeSinceStartup + RequestTimeout;
            p.NextTryAt = Time.realtimeSinceStartup + RequestTimeout;
            TxLog.Warn("tx=" + txId + " forced re-query (decode/timeout), deadline extended once");
            return true;
        }

        /// <summary>
        /// Terminal indeterminate outcome: loud, never silent. Completions keep the
        /// safe direction (no credit, no removal); the user must verify the chest.
        /// Removes the pending entry and releases claims.
        /// </summary>
        internal static void FinalizeUnknownTx(long txId)
        {
            PendingTx p;
            if (!Pending.TryGetValue(txId, out p))
                return;
            TxLog.Warn("tx=" + p.TxId + " UNKNOWN op=" + p.Op + " (applied-or-not indeterminate, no queries left)");
            TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
            CompletePendingTerminal(txId, null, TxStatus.UnknownTx, 0u, TxCompletionKind.Indeterminate);
        }

        /// <summary>Structural walk of a Take body ([count](prefab,item,accepted)); resets pos.</summary>
        private static bool TakeBodyReadable(ZPackage body)
        {
            try
            {
                if (body == null)
                    return false;
                int n = body.ReadInt();
                if (n < 0 || n > 4096)
                    return false;
                for (int i = 0; i < n; i++)
                {
                    body.ReadInt();
                    body.ReadPackage();
                    body.ReadInt();
                }
                body.SetPos(0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Structural walk of an Add body ([count][accepted...]); resets pos.</summary>
        private static bool AddBodyMatchesArity(ZPackage body, int expected)
        {
            try
            {
                if (body == null)
                    return false;
                int n = body.ReadInt();
                if (n != expected)
                    return false;
                for (int i = 0; i < n; i++)
                    body.ReadInt();
                body.SetPos(0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int ReadAcceptedAt(ZPackage pkg, int index)
        {
            try
            {
                if (pkg == null)
                    return 0;
                int count = pkg.ReadInt();
                int val = 0;
                for (int i = 0; i < count; i++)
                {
                    int a = pkg.ReadInt();
                    if (i == index)
                        val = a;
                }
                return val;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static List<int> ReadAcceptedList(ZPackage pkg, int count)
        {
            List<int> res = new List<int>();
            try
            {
                if (pkg == null)
                    return res;
                int n = pkg.ReadInt();
                for (int i = 0; i < n && i < count; i++)
                    res.Add(pkg.ReadInt());
            }
            catch (Exception)
            {
            }
            while (res.Count < count)
                res.Add(0);
            return res;
        }

        private static void CompleteAdd(Inventory srcInv, ItemData itemRef, TxOpItem sent, int accepted, TxStatus status, uint rev, Container container, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone)
        {
            CompleteAdd(srcInv, itemRef, sent, accepted, status, rev, container, onDone, false, TxCompletionKind.Normal);
        }

        private static void LogNonMainInventory(string op, Inventory inv)
        {
            try
            {
                Player lp = Player.m_localPlayer;
                if (lp != null && inv != null && !object.ReferenceEquals(inv, ((Humanoid)lp).GetInventory()))
                    TxLog.Info(op + " uses non-main inventory (dedicated slots?)");
            }
            catch
            {
            }
        }

        private static void CompleteAdd(Inventory srcInv, ItemData itemRef, TxOpItem sent, int accepted, TxStatus status, uint rev, Container container, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone, bool alreadyRemoved, TxCompletionKind disp)
        {
            LogNonMainInventory("add", srcInv);
            if (alreadyRemoved)
            {
                // Drag-deposit: the stack already left the inventory when the drag started
                // (vanilla), and itemRef is the abandoned drag object holding the full
                // dragged amount. Never remove again — restore whatever was not committed.
                RestoreDragRemainder(srcInv, itemRef, accepted);
                if (status == TxStatus.UnknownTx)
                    TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
                else if (status == TxStatus.Rejected)
                    TellPlayer("The shared chest refused the move. Try again.");
                if (onDone != null)
                    onDone(null, status, rev, disp);
                return;
            }
            if (accepted > 0 && srcInv != null && sent != null)
            {
                string name = sent.Snapshot != null && sent.Snapshot.m_shared != null ? sent.Snapshot.m_shared.m_name : null;
                int quality = sent.Snapshot != null ? sent.Snapshot.m_quality : -1;
                int variant = sent.Snapshot != null ? sent.Snapshot.m_variant : -1;
                float world = sent.Snapshot != null ? sent.Snapshot.m_worldLevel : -1f;
                int removed = TxInventory.RemoveForTake(srcInv, itemRef, name, quality, accepted, variant, world);
                TxLog.Info("add-remove accepted=" + accepted + " removed=" + removed);
                if (removed < accepted)
                {
                    // Source changed mid-RTT: send the excess back to the chest as compensation.
                    int excess = accepted - removed;
                    TxLog.Warn("tx short-removal: accepted=" + accepted + " removed=" + removed + " compensating " + excess);
                    CompensateAddBack(container, sent, excess);
                }
            }
            if (status == TxStatus.UnknownTx)
                TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
            else if (status == TxStatus.Rejected)
                TellPlayer("The shared chest refused the move. Try again.");
            if (onDone != null)
                onDone(null, status, rev, disp);
        }

        private static void RestoreDragRemainder(Inventory srcInv, ItemData itemRef, int accepted)
        {
            if (srcInv == null || itemRef == null)
                return;
            CustomDataTags.StripBenign(itemRef);
            TxLog.Info("drag-restore stack=" + itemRef.m_stack + " accepted=" + accepted);
            int restore = itemRef.m_stack - accepted;
            if (restore <= 0)
                return;
            itemRef.m_stack = restore;
            if (srcInv.AddItem(itemRef))
            {
                srcInv.m_onChanged?.Invoke();
                return;
            }
            // Inventory full: drop at feet rather than lose the items.
            Player player = Player.m_localPlayer;
            if (player != null)
                ((Humanoid)player).DropItem(srcInv, itemRef, restore);
            else
                TxLog.Error("drag restore failed: no player, items lost: " + restore);
        }

        private static void CompensateAddBack(Container container, TxOpItem sent, int amount)
        {
            // Short-removal: the chest materialized `accepted` items but the client
            // removed fewer. Re-adding the excess would inflate the total (the chest
            // already holds it). Instead burn the excess chest-side with a corrective
            // Take: chest C+N-excess + player P-removed == exact conservation in all
            // cases, wherever the unfound items actually are.
            if (amount <= 0 || sent == null || sent.Snapshot == null || sent.Snapshot.m_shared == null)
                return;
            TxOpItem burn = SnapshotAuto(sent.Snapshot, amount);
            if (burn == null)
                return;
            List<TxOpItem> items = new List<TxOpItem>();
            items.Add(burn);
            RequestTakeCustom(container, items, false, delegate (List<DecodedTake> decoded, TxStatus status, uint rev, TxCompletionKind burnDisp)
            {
                int took = 0;
                if (decoded != null)
                {
                    foreach (DecodedTake dt in decoded)
                    {
                        if (dt != null && dt.Accepted > 0)
                            took += dt.Accepted;
                    }
                }
                if (burnDisp == TxCompletionKind.Indeterminate || (status != TxStatus.Accepted && status != TxStatus.Partial && status != TxStatus.Duplicate))
                {
                    // Rejected OR indeterminate (UnknownTx): never retry the burn —
                    // retrying an uncertain corrective Take double-debits. Loud.
                    TxLog.Warn("short-removal burn not applied (" + status + "/" + burnDisp + "), excess stays in chest — check the chest");
                    TellPlayer("Chest could not correct an over-deposit. Check the chest.");
                }
                else if (took < amount)
                {
                    TxLog.Warn("short-removal burn partial: burned=" + took + " of " + amount);
                    TellPlayer("Chest could not correct an over-deposit. Check the chest.");
                }
                else
                    TxLog.Info("short-removal burn ok: " + took);
                RefreshNow(container);
            });
        }

        private static void CompleteTake(Inventory dstInv, ZPackage pkg, TxStatus status, uint rev, Container container, Action<ZPackage, TxStatus, uint, TxCompletionKind> onDone, int wantDstX = -1, int wantDstY = -1, Action<System.Collections.Generic.Dictionary<string, int>> onPlaced = null, TxCompletionKind disp = TxCompletionKind.Normal)
        {
            LogNonMainInventory("take", dstInv);
            if ((status == TxStatus.Accepted || status == TxStatus.Partial || status == TxStatus.Duplicate) && dstInv != null && pkg != null)
            {
                int count = 0;
                try
                {
                    count = pkg.ReadInt();
                }
                catch (Exception ex)
                {
                    int verspreq = -1;
                    try
                    {
                        verspreq = pkg.GetArray().Length;
                    }
                    catch
                    {
                    }
                    TxLog.Error("take completion decode failed: " + ex.Message + " (body bytes=" + verspreq + ")");
                }
                // Per-item isolation: one broken entry must not void the rest —
                // anything uncredited is sent back instead of being lost.
                System.Collections.Generic.Dictionary<string, int> _placedByName = null;
                for (int i = 0; i < count; i++)
                {
                    int prefabHash = 0;
                    ZPackage inner = null;
                    int accepted = 0;
                    try
                    {
                        prefabHash = pkg.ReadInt();
                        inner = pkg.ReadPackage();
                        accepted = pkg.ReadInt();
                    }
                    catch (Exception ex)
                    {
                        TxLog.Error("take completion entry " + i + " unreadable (" + ex.Message + "), aborting rest");
                        break;
                    }
                    if (accepted <= 0)
                        continue;
                    try
                    {
                        ItemData item = TxCodec.ResolvePrefab(prefabHash, inner);
                        CustomDataTags.StripBenign(item);
                        if (item == null)
                        {
                            TxLog.Error("take completion: prefab " + prefabHash + " missing, compensating " + accepted);
                            CompensateTakeBack(container, prefabHash, inner, accepted);
                            continue;
                        }
                        int placed = TxInventory.AddTakeAndCount(dstInv, item, wantDstX, wantDstY);
                        if (placed > 0 && onPlaced != null && item.m_shared != null)
                        {
                            if (_placedByName == null)
                                _placedByName = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
                            int cur;
                            _placedByName.TryGetValue(item.m_shared.m_name, out cur);
                            _placedByName[item.m_shared.m_name] = cur + placed;
                        }
                        if (placed < accepted)
                        {
                            // Did not fit the inventory: send the remainder back to the chest.
                            int leftover = accepted - placed;
                            TxLog.Warn("take completion: player full, compensating " + leftover);
                            ItemData back = item.Clone();
                            back.m_stack = leftover;
                            CompensateTakeBackItem(container, prefabHash, back);
                        }
                    }
                    catch (Exception ex)
                    {
                        TxLog.Error("take completion entry " + i + " failed (" + ex.Message + "), compensating " + accepted);
                        try
                        {
                            CompensateTakeBack(container, prefabHash, inner, accepted);
                        }
                        catch (Exception ex2)
                        {
                            TxLog.Error("take completion entry " + i + " compensation failed: " + ex2.Message);
                        }
                    }
                }
                if (_placedByName != null && _placedByName.Count > 0 && onPlaced != null)
                {
                    try { onPlaced(_placedByName); } catch { }
                }
            }
            if (status == TxStatus.UnknownTx)
                TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
            else if (status == TxStatus.Rejected)
                TellPlayer("The shared chest changed. Try again.");
            if (onDone != null)
                onDone(pkg, status, rev, disp);
        }

        private static void CompensateTakeBack(Container container, int prefabHash, ZPackage inner, int amount)
        {
            ItemData item = TxCodec.ResolvePrefab(prefabHash, inner);
            if (item == null)
            {
                TxLog.Error("compensation failed: prefab missing");
                return;
            }
            CompensateTakeBackItem(container, prefabHash, item);
        }

        internal static void CompensateTakeBackItem(Container container, int prefabHash, ItemData item)
        {
            int wanted = (item != null) ? item.m_stack : 0;
            TxOpItem back = SnapshotAuto(item, item.m_stack);
            if (back == null)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.Add;
            call.Items.Add(back);
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                if (disp == TxCompletionKind.Indeterminate)
                {
                    // The compensation Add itself is indeterminate: the chest may
                    // already hold the orphan. Restoring it to the player as
                    // though rejection were known would duplicate it — restore
                    // nothing, stay loud, let the user reconcile.
                    TxLog.Warn("compensate-add indeterminate: outcome unknown, nothing restored — check the chest");
                    TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
                    RefreshNow(container);
                    return;
                }
                int acc = ReadAcceptedAt(pkg, 0);
                TxLog.Info("compensate-add status=" + status + " accepted=" + acc + " of " + wanted);
                if (acc < wanted)
                {
                    // The orphan must never be voided: keep the unaccepted
                    // remainder in the player inventory, loudly.
                    RestoreCompensateLeftover(item, acc);
                }
                RefreshNow(container);
            });
        }

        /// <summary>
        /// Leftover of a failed compensate-add goes back to the player inventory
        /// (or drops at their feet when full). Never silently dropped.
        /// </summary>
        private static void RestoreCompensateLeftover(ItemData item, int accepted)
        {
            if (item == null)
                return;
            int leftover = item.m_stack - accepted;
            if (leftover <= 0)
                return;
            Player player = Player.m_localPlayer;
            Inventory playerInv = (player != null) ? ((Humanoid)player).GetInventory() : null;
            if (playerInv == null)
            {
                TxLog.Error("compensate restore failed: no player inventory, items lost: " + leftover);
                return;
            }
            ItemData rest = item.Clone();
            rest.m_stack = leftover;
            CustomDataTags.StripBenign(rest);
            if (TxInventory.AddAndCount(playerInv, rest) < leftover)
            {
                ((Humanoid)player).DropItem(playerInv, rest, leftover);
                TxLog.Warn("compensate leftover did not fit, dropped at feet: " + leftover);
            }
            else
            {
                TxLog.Warn("compensate-add short, restored to player: " + leftover);
            }
            TellPlayer("Chest could not take back some items. They were returned to you.");
        }

        // ============================ client: timeouts / retry / query ============================

        /// <summary>
        /// Take for prefetch/automation: received items land in dstInv (CompleteTake),
        /// shortfall/failure handled via compensation and refresh inside.
        /// </summary>
        internal static void SubmitTakePrefetch(Container container, TxOpCall call, Inventory dstInv, Action<System.Collections.Generic.Dictionary<string, int>> onLanded = null)
        {
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                CompleteTake(dstInv, pkg, status, rev, container, null, -1, -1, onLanded, disp);
            });
        }

        /// <summary>
        /// Take request with custom completion for automation (production, feeding, restock).
        /// Received items are NOT placed anywhere automatically — onDone decides.
        /// The completion disposition is propagated: Indeterminate means no retry,
        /// no compensate-as-failed, no feeding and no second Take — the chest may
        /// already have been debited. A null/corrupt body never converts an
        /// Indeterminate outcome into a Rejected one.
        /// </summary>
        internal static void RequestTakeCustom(Container container, List<TxOpItem> items, bool respectReserves, Action<List<DecodedTake>, TxStatus, uint, TxCompletionKind> onDone, long playerId = 0L, Vector3? actorPos = null)
        {
            if (items == null || items.Count == 0)
                return;
            TxOpCall call = new TxOpCall();
            call.Op = TxOp.TakeBatch;
            call.Items.AddRange(items);
            call.RespectReserves = respectReserves;
            if (container.IsOwner())
            {
                // Owner fast path: map the StoredResult directly. The Submit path hands
                // a null package for local commits, which the body decoder cannot read
                // (taken items would be voided).
                MutateLocal(container, call, delegate (StoredResult r)
                {
                    RefreshNow(container);
                    List<DecodedTake> direct = new List<DecodedTake>();
                    if (r != null && r.Takes != null)
                    {
                        foreach (TakeEntry te in r.Takes)
                        {
                            if (te == null || te.Accepted <= 0)
                                continue;
                            DecodedTake dt = new DecodedTake();
                            dt.PrefabHash = te.PrefabHash;
                            dt.Item = te.Item;
                            dt.Accepted = te.Accepted;
                            direct.Add(dt);
                        }
                    }
                    TxStatus st = (r != null) ? r.Status : TxStatus.UnknownTx;
                    uint rev = (r != null) ? r.Revision : CurrentRevision(container);
                    TxCompletionKind ownDisp = (r != null)
                        ? TxResponsePolicy.Classify(st, r.TotalsOnly, TxOp.TakeBatch, items.Count)
                        : TxCompletionKind.Indeterminate;
                    if (onDone != null)
                        onDone(direct, st, rev, ownDisp);
                }, playerId, actorPos);
                return;
            }
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                List<DecodedTake> decoded = TxCodec.ReadTakeResults(pkg);
                if (decoded == null)
                {
                    // Corrupt body: never convert Indeterminate into Rejected. A
                    // committed-but-undecodable Take debited the chest with the
                    // payload lost — credit nothing, retry nothing.
                    if (disp == TxCompletionKind.Indeterminate)
                    {
                        TxLog.Warn("take-custom completion decode failed (indeterminate, nothing credited, no retry)");
                        if (onDone != null)
                            onDone(new List<DecodedTake>(), status, rev, disp);
                        return;
                    }
                    if (status == TxStatus.Accepted || status == TxStatus.Partial || status == TxStatus.Duplicate)
                    {
                        TxLog.Warn("take-custom completion decode failed (committed, payload lost: nothing credited, no retry)");
                        if (onDone != null)
                            onDone(new List<DecodedTake>(), status, rev, TxCompletionKind.CommittedTakeDetailsUnavailable);
                        return;
                    }
                    TxLog.Warn("take-custom completion decode failed");
                    if (onDone != null)
                        onDone(new List<DecodedTake>(), status, rev, disp);
                    return;
                }
                if (onDone != null)
                    onDone(decoded, status, rev, disp);
            }, playerId, actorPos);
        }

        /// <summary>
        /// Universal AddBatch submit (quick-stack/automation): owner enqueues locally,
        /// otherwise over the network. On result removes accepted from srcInv and builds TransferRecords.
        /// </summary>
        /// <summary>Max items per tx: keeps request/response bodies far from transport truncation.</summary>
        internal const int BatchChunkSize = 4;

        internal static void SubmitCall(Container container, TxOpCall call, Inventory srcInv, Action<List<TransferRecord>, TxCompletionKind> onDone)
        {
            SubmitCallDetailed(container, call, srcInv, delegate (List<TransferRecord> records, List<int> accepted, TxStatus status, TxCompletionKind disp)
            {
                if (onDone != null)
                    onDone(records, disp);
            });
        }

        /// <summary>
        /// Detailed batch submit: per-item accepted counts + status for cascade retry,
        /// plus the completion disposition. CommittedMultiAddDetailsUnavailable is
        /// terminal: the remainder must NOT cascade (it is already stored).
        /// Chunking applies inside (each chunk reports separately).
        /// </summary>
        internal static void SubmitCallDetailed(Container container, TxOpCall call, Inventory srcInv, Action<List<TransferRecord>, List<int>, TxStatus, TxCompletionKind> onDone)
        {
            if (call != null && call.Items.Count > BatchChunkSize)
            {
                // Split big batches: each chunk is an independent tx (own txId,
                // own atomic commit). Disjoint item sets => the claim guard stays out.
                // onDone fires per chunk (all current handlers tolerate repeats).
                int i = 0;
                while (i < call.Items.Count)
                {
                    TxOpCall part = new TxOpCall();
                    part.Op = call.Op;
                    part.EnforceRule = call.EnforceRule;
                    part.RespectReserves = call.RespectReserves;
                    for (int j = i; j < call.Items.Count && j < i + BatchChunkSize; j++)
                        part.Items.Add(call.Items[j]);
                    SubmitCallDetailed(container, part, srcInv, onDone);
                    i += BatchChunkSize;
                }
                return;
            }
            if (container.IsOwner())
            {
                MutateLocal(container, call, delegate (StoredResult r)
                {
                    List<int> acc = new List<int>();
                    for (int i = 0; i < call.Items.Count; i++)
                        acc.Add((r != null && r.Accepted != null && i < r.Accepted.Count) ? r.Accepted[i] : 0);
                    TxStatus st = (r != null) ? r.Status : TxStatus.UnknownTx;
                    List<TransferRecord> records = FinishBatchCompletion(container, srcInv, call, r);
                    TxCompletionKind ownDisp = (r != null)
                        ? TxResponsePolicy.Classify(st, r.TotalsOnly, call.Op, call.Items.Count)
                        : TxCompletionKind.Indeterminate;
                    if (onDone != null)
                        onDone(records, acc, st, ownDisp);
                });
                return;
            }
            Submit(container, call, delegate (ZPackage pkg, TxStatus status, uint rev, TxCompletionKind disp)
            {
                if (disp == TxCompletionKind.Indeterminate)
                {
                    // Terminal: commitment unknown (wire UnknownTx or exhausted
                    // queries). Remove nothing, cascade nothing, retry nothing:
                    // the chest may already hold the items. Loud, never silent.
                    TxLog.Warn("batch indeterminate: outcome unknown, nothing removed, no cascade — check the chest before retrying");
                    TellPlayer("Chest request outcome unknown. Check the chest before retrying.");
                    RefreshNow(container);
                    if (onDone != null)
                        onDone(new List<TransferRecord>(), ZeroAccepted(call.Items.Count), status, disp);
                    return;
                }
                if (disp == TxCompletionKind.CommittedMultiAddDetailsUnavailable)
                {
                    // Committed but per-item counts unknown (ring replay after handoff):
                    // remove nothing from the source and report zero movement with the
                    // disposition, so the caller terminates instead of cascading.
                    TxLog.Warn("batch details-unavailable: committed but unattributed, nothing removed, no cascade");
                    TellPlayer("Chest applied the move but per-item counts are unknown. Check the chest before retrying.");
                    RefreshNow(container);
                    if (onDone != null)
                        onDone(new List<TransferRecord>(), ZeroAccepted(call.Items.Count), status, disp);
                    return;
                }
                List<int> accepted = ReadAcceptedList(pkg, call.Items.Count);
                List<TransferRecord> records = new List<TransferRecord>();
                for (int i = 0; i < call.Items.Count && i < accepted.Count; i++)
                {
                    TxOpItem sent = call.Items[i];
                    CompleteAdd(srcInv, sent.SourceRef, sent, accepted[i], status, rev, container, null, false, disp);
                    if (accepted[i] > 0 && sent.Snapshot != null && sent.Snapshot.m_shared != null)
                        records.Add(new TransferRecord(sent.Snapshot.m_shared.m_name, sent.Snapshot.GetIcon(), accepted[i], sent.Snapshot.m_shared.m_maxStackSize));
                }
                RefreshNow(container);
                if (onDone != null)
                    onDone(records, accepted, status, disp);
            }, 0L, null, call.Items.Count);
        }

        private static List<int> ZeroAccepted(int count)
        {
            List<int> res = new List<int>(count < 0 ? 0 : count);
            for (int i = 0; i < count; i++)
                res.Add(0);
            return res;
        }

        private static List<TransferRecord> FinishBatchCompletion(Container container, Inventory srcInv, TxOpCall call, StoredResult r)
        {
            List<TransferRecord> records = new List<TransferRecord>();
            for (int i = 0; i < call.Items.Count && i < r.Accepted.Count; i++)
            {
                TxOpItem sent = call.Items[i];
                int accepted = r.Accepted[i];
                if (accepted > 0 && srcInv != null)
                {
                    string name = sent.Snapshot != null && sent.Snapshot.m_shared != null ? sent.Snapshot.m_shared.m_name : null;
                    int quality = sent.Snapshot != null ? sent.Snapshot.m_quality : -1;
                    int variant = sent.Snapshot != null ? sent.Snapshot.m_variant : -1;
                    float world = sent.Snapshot != null ? sent.Snapshot.m_worldLevel : -1f;
                    int removed = TxInventory.RemoveForTake(srcInv, sent.SourceRef, name, quality, accepted, variant, world);
                    if (removed < accepted)
                    {
                        int excess = accepted - removed;
                        TxLog.Warn("local batch short-removal: compensating " + excess);
                        CompensateAddBack(container, sent, excess);
                    }
                    if (sent.Snapshot != null && sent.Snapshot.m_shared != null)
                        records.Add(new TransferRecord(sent.Snapshot.m_shared.m_name, sent.Snapshot.GetIcon(), accepted, sent.Snapshot.m_shared.m_maxStackSize));
                }
            }
            RefreshNow(container);
            return records;
        }

        private static void PumpPending()
        {
            if (Pending.Count == 0)
                return;
            float now = Time.realtimeSinceStartup;
            List<long> done = null;
            foreach (KeyValuePair<long, PendingTx> kv in Pending)
            {
                PendingTx p = kv.Value;
                if ((Object)p.Container == (Object)null)
                {
                    if (done == null)
                        done = new List<long>();
                    done.Add(kv.Key);
                    ReleaseClaimed(p.Claimed);
                    continue;
                }
                if (now >= p.Deadline)
                {
                    if (!p.FinalQuerySent && (Object)p.Container != (Object)null)
                    {
                        // One last query before giving up: a lost response is still
                        // sitting in the manager cache in the common case.
                        p.FinalQuerySent = true;
                        p.QuerySent = true;
                        SendQuery(p);
                        p.Attempts++;
                        p.Deadline = now + RequestTimeout;
                        p.NextTryAt = now + RequestTimeout;
                        TxLog.Warn("tx=" + p.TxId + " TIMEOUT op=" + p.Op + " final query sent");
                        continue;
                    }
                    if (done == null)
                        done = new List<long>();
                    done.Add(kv.Key);
                    FinalizeUnknownTx(kv.Key);
                    continue;
                }
                if (now < p.NextTryAt)
                    continue;
                if (p.Attempts < PayloadAttempts + (p.QuerySent ? 0 : QueryAttempts))
                {
                    if (!p.QuerySent && p.Attempts >= PayloadAttempts)
                    {
                        p.QuerySent = true;
                        SendQuery(p);
                    }
                    else
                    {
                        SendPayload(p.Container, p.Payload);
                    }
                    p.Attempts++;
                    p.NextTryAt = now + RequestTimeout;
                }
            }
            if (done != null)
                for (int i = 0; i < done.Count; i++)
                    Pending.Remove(done[i]);
        }

        private static void SendQuery(PendingTx p)
        {
            // Query: same txId, empty body. The manager returns the cache without re-applying.
            ZPackage body = new ZPackage();
            body.Write(TxCodec.ProtoVersion);
            body.Write((int)TxOp.Query);
            body.Write(0u);
            body.Write(LocalPlayerId());
            body.Write(LocalActorPos());
            body.Write(false);
            body.Write(false);
            ZPackage request = new ZPackage();
            request.Write(p.TxId);
            request.Write(body);
            TxLog.Info("tx=" + p.TxId + " QUERY (response was lost)");
            SendPayload(p.Container, request);
        }

        // ============================ viewer refresh / presence / takeover ============================

        internal static void RefreshNow(Container container)
        {
            ChestState state = GetState(container);
            if (state == null)
                return;
            state.SeenRev = 0u;
            state.SeenOnce = false;
            state.SeenBytes = null;
        }

        private static void PumpViewerRefresh()
        {
            if (Time.realtimeSinceStartup < _nextRefreshPoll)
                return;
            _nextRefreshPoll = Time.realtimeSinceStartup + RefreshPoll;
            InventoryGui gui = InventoryGui.instance;
            Container open = InventoryAccess.CurrentContainer(gui);
            int openId = (Object)open != (Object)null ? ((Object)open).GetInstanceID() : 0;
            if (openId != _openInstanceId)
            {
                ChestState prev = null;
                if (_openInstanceId != 0)
                    States.TryGetValue(_openInstanceId, out prev);
                if (prev != null && (Object)prev.Container != (Object)null)
                    SendPresence(prev.Container, TxOp.ViewerClose);
                _openInstanceId = openId;
                if (open != null)
                {
                    RefreshNow(open);
                    SendPresence(open, TxOp.ViewerOpen);
                    _nextPresenceAt = Time.realtimeSinceStartup + PresenceHeartbeat;
                }
            }
            if ((Object)open == (Object)null || open.IsOwner())
                return;
            if (!IsShared(open))
                return;
            if (Time.realtimeSinceStartup >= _nextPresenceAt)
            {
                SendPresence(open, TxOp.ViewerOpen);
                _nextPresenceAt = Time.realtimeSinceStartup + PresenceHeartbeat;
            }
            ChestState state = GetState(open);
            if (state == null)
                return;
            ZNetView netView = TxReflect.GetNetView(open);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            uint rev = netView.GetZDO().DataRevision;
            if (state.SeenOnce && rev <= state.SeenRev)
            {
                // Same or stale ZDO revision (network reorder): never roll the
                // open GUI backwards. First poll always applies.
                if (rev < state.SeenRev)
                    TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " viewer stale rev=" + rev + " (seen " + state.SeenRev + "), skipped");
                return;
            }
            byte[] bytes;
            try
            {
                bytes = netView.GetZDO().GetByteArray(ZDOVars.s_items);
            }
            catch (Exception)
            {
                return;
            }
            if (SameBytes(bytes, state.SeenBytes))
            {
                state.SeenRev = rev;
                return;
            }
            // Content changed: reload the local copy, do NOT close the GUI.
            if (gui != null)
                TxReflect.CancelDragFrom(gui, open.GetInventory());
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " viewer dims before=" + open.GetInventory().GetWidth() + "x" + open.GetInventory().GetHeight());
            try
            {
                ZPackage pkg = bytes != null ? new ZPackage(bytes) : EmptyInventoryPackage(open);
                open.GetInventory().Load(pkg);
                TxReflect.SetLastRevision(open, rev);
                TxReflect.UpdateRows(open);
            }
            catch (Exception ex)
            {
                TxLog.Warn("viewer refresh failed: " + ex.Message);
                return;
            }
            state.SeenRev = rev;
            state.SeenOnce = true;
            state.SeenBytes = bytes;
            TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " viewer refresh rev=" + rev);
        }

        private static ZPackage EmptyInventoryPackage(Container container)
        {
            ZPackage pkg = new ZPackage();
            pkg.Write(109);
            pkg.Write((ushort)0);
            return pkg;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static void SendPresence(Container container, TxOp op)
        {
            if (!IsShared(container) || container.IsOwner())
                return;
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return;
            long txId = IssueTxId();
            if (txId == 0L)
                return; // counter exhausted/unreserved: stay silent, fail closed (presence is fire-and-forget).
            ZPackage body = new ZPackage();
            body.Write(TxCodec.ProtoVersion);
            body.Write((int)op);
            body.Write(0u);
            body.Write(LocalPlayerId());
            body.Write(LocalActorPos());
            body.Write(false);
            body.Write(false);
            ZPackage request = new ZPackage();
            request.Write(txId);
            request.Write(body);
            SendPayload(container, request);
        }

        private static void HandlePresence(Container container, long sender, TxOp op)
        {
            ChestState state = GetState(container);
            if (state == null)
                return;
            if (op == TxOp.ViewerOpen)
            {
                state.Viewers[sender] = Time.realtimeSinceStartup;
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " viewer open peer=" + sender);
            }
            else
            {
                state.Viewers.Remove(sender);
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " viewer close peer=" + sender);
            }
        }

        private static void PumpManagerSlow()
        {
            if (Time.realtimeSinceStartup < _nextSlowPump)
                return;
            _nextSlowPump = Time.realtimeSinceStartup + SlowPump;
            List<int> dead = null;
            foreach (KeyValuePair<int, ChestState> kv in States)
            {
                ChestState state = kv.Value;
                if ((Object)state.Container == (Object)null)
                {
                    if (dead == null)
                        dead = new List<int>();
                    dead.Add(kv.Key);
                    continue;
                }
                ZNetView netView = TxReflect.GetNetView(state.Container);
                if ((Object)netView == (Object)null || !netView.IsValid())
                    continue;
                long owner = netView.GetZDO().GetOwner();
                if (owner == ZNet.GetUID() && state.LastOwner != owner)
                {
                    // Takeover: I became the manager — load the latest committed state.
                    Takeover(state);
                }
                state.LastOwner = owner;
                if (owner != ZNet.GetUID())
                    continue;
                // I am the manager: queue, presence, lid.
                Drain(state);
                try
                {
                    // Own vanilla mutations (own GUI) also grow content past commits
                    // — height must always follow it.
                    TxReflect.UpdateRows(state.Container);
                }
                catch (Exception)
                {
                }
                PruneViewers(state);
                EnforceLid(state);
            }
            if (dead != null)
                for (int i = 0; i < dead.Count; i++)
                    States.Remove(dead[i]);
        }

        private static void Takeover(ChestState state)
        {
            try
            {
                ZNetView netView = TxReflect.GetNetView(state.Container);
                byte[] bytes = netView.GetZDO().GetByteArray(ZDOVars.s_items);
                if (bytes != null)
                {
                    if (InventoryGui.instance != null)
                        TxReflect.CancelDragFrom(InventoryGui.instance, state.Container.GetInventory());
                    state.Container.GetInventory().Load(new ZPackage(bytes));
                    TxReflect.SetLastRevision(state.Container, netView.GetZDO().DataRevision);
                    TxReflect.UpdateRows(state.Container);
                }
                List<TxJob> dropped = DequeueAll(state);
                state.Processed.Clear();
                state.ProcOrder.Clear();
                RingData ring = ReadRing(state.Container);
                SeedFromRing(state, ring, ReadFloor(state.Container));
                // Queued-but-unapplied jobs keep a stable outcome: seed them as
                // Rejected in the FRESH cache (nothing was applied — the sender
                // retries as a NEW tx) so the same txId can never execute later.
                foreach (TxJob dj in dropped)
                    SeedReject(state, dj.TxId, dj.Call != null ? dj.Call.Op : TxOp.Query, dj.Sender, true);
                foreach (TxJob dj in dropped)
                    AnswerReject(state, dj);
                state.Viewers.Clear();
                state.SeenRev = 0u;
                state.SeenOnce = false;
                state.SeenBytes = null;
                TxLog.Info("container=" + TxLog.Zid(state.ZdoId) + " manager changed old=" + state.LastOwner
                    + " new=" + ZNet.GetUID() + " revision=" + netView.GetZDO().DataRevision);
            }
            catch (Exception ex)
            {
                TxLog.Error("takeover failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Answer queued-but-unapplied jobs (handoff / structural acquire).
        /// Prefer the two-phase path in Takeover (DequeueAll + SeedReject +
        /// AnswerReject) so the Rejected outcome persists in the fresh cache.
        /// This legacy entry point answers without seeding (callers that already
        /// hold a seeded cache must not double-answer).
        /// </summary>
        internal static void RejectQueued(ChestState state)
        {
            if (state == null)
                return;
            foreach (TxJob job in DequeueAll(state))
                AnswerReject(state, job);
        }

        private static void PruneViewers(ChestState state)
        {
            float now = Time.realtimeSinceStartup;
            List<long> gone = null;
            foreach (KeyValuePair<long, float> kv in state.Viewers)
            {
                if (now - kv.Value > PresenceTimeout)
                {
                    if (gone == null)
                        gone = new List<long>();
                    gone.Add(kv.Key);
                }
            }
            if (gone != null)
                for (int i = 0; i < gone.Count; i++)
                    state.Viewers.Remove(gone[i]);
        }

        private static void EnforceLid(ChestState state)
        {
            try
            {
                // Viewers plus the manager's own open GUI (otherwise flips
                // against vanilla SetInUse(true) every frame and the lid flaps).
                bool want = state.Viewers.Count > 0 || IsOpenByMe(state.Container);
                state.Container.SetInUse(want);
            }
            catch (Exception)
            {
            }
        }

        private static bool IsOpenByMe(Container container)
        {
            InventoryGui gui = InventoryGui.instance;
            if ((Object)gui == (Object)null || !InventoryGui.IsVisible())
                return false;
            return (Object)InventoryAccess.CurrentContainer(gui) == (Object)container;
        }

        // ============================ main pump ============================

        internal static void Pump()
        {
            if (!Plugin.IsActive)
                return;
            TxNet.PumpHello();
            PumpManagerSlow();
            PumpPending();
            PumpViewerRefresh();
        }

        // ============================ helpers ============================

        private static uint CurrentRevision(Container container)
        {
            ZNetView netView = TxReflect.GetNetView(container);
            if ((Object)netView == (Object)null || !netView.IsValid())
                return 0u;
            return netView.GetZDO().DataRevision;
        }

        private static Vector3 LocalActorPos()
        {
            Player player = Player.m_localPlayer;
            if ((Object)player == (Object)null)
                return Vector3.zero;
            return ((Component)player).transform.position;
        }

        private static long LocalPlayerId()
        {
            Game game = Game.instance;
            if ((Object)game == (Object)null)
                return 0L;
            PlayerProfile profile = game.GetPlayerProfile();
            if (profile == null)
                return 0L;
            return profile.GetPlayerID();
        }

        private static void TellPlayer(string message)
        {
            Player player = Player.m_localPlayer;
            if (player != null)
                ((Character)player).Message((MessageType)2, message, 0, (Sprite)null, false);
        }
    }
}
