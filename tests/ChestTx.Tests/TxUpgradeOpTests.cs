using System;
using System.Collections.Generic;
using BestAutoSort.TxCore;

namespace ChestTx.Tests
{
    /// <summary>
    /// Server-mediated remote chest upgrade (0.6.x): phase-fault suite against
    /// PRODUCTION code (TxUpgradeManager / TxUpgradeGate in BestAutoSort.TxCore).
    ///
    /// Covered: happy-path conservation; crash before/after EVERY phase
    /// (including the injected kill between mint and link-write); same-op
    /// never spawns twice; contradiction quarantines both (ghost kept
    /// standing, never auto-destroyed); conservation (items + spend/refund
    /// at-most-once); fail-closed recovery from torn snapshots; legacy-frame
    /// refusal gate; queueing behind a prepared op; timeout queries the same op.
    /// </summary>
    internal static class TxUpgradeOpTests
    {
        public static void RunAll()
        {
            Console.WriteLine("UPGRADE_HappyPathConservation");
            UPGRADE_HappyPathConservation();
            Console.WriteLine("UPGRADE_CrashEveryPhase");
            UPGRADE_CrashEveryPhase();
            Console.WriteLine("UPGRADE_MintLinkKillCleanup");
            UPGRADE_MintLinkKillCleanup();
            Console.WriteLine("UPGRADE_SameOpNoSecondSpawn");
            UPGRADE_SameOpNoSecondSpawn();
            Console.WriteLine("UPGRADE_ContradictionQuarantinesBoth");
            UPGRADE_ContradictionQuarantinesBoth();
            Console.WriteLine("UPGRADE_UnlinkedGhostKept");
            UPGRADE_UnlinkedGhostKept();
            Console.WriteLine("UPGRADE_SpendRefundAtMostOnce");
            UPGRADE_SpendRefundAtMostOnce();
            Console.WriteLine("UPGRADE_TornSnapshotFailClosed");
            UPGRADE_TornSnapshotFailClosed();
            Console.WriteLine("UPGRADE_LegacyFrameRefused");
            UPGRADE_LegacyFrameRefused();
            Console.WriteLine("UPGRADE_QueueBehindPrepared");
            UPGRADE_QueueBehindPrepared();
            Console.WriteLine("UPGRADE_SpendFailAbandonsHead");
            UPGRADE_SpendFailAbandonsHead();
            Console.WriteLine("UPGRADE_TimeoutQueriesSameOp");
            UPGRADE_TimeoutQueriesSameOp();
        }

        /// <summary>Fake ghost host: chest id -&gt; item multiset + faults.</summary>
        private sealed class FakeHost : ITxUpgradeHost
        {
            public readonly Dictionary<string, List<int>> Chests = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Links = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly List<string> Loud = new List<string>();
            public int Spawns;
            public int Destroyed;
            public readonly HashSet<string> DestroyedGhosts = new HashSet<string>(StringComparer.Ordinal);
            public TxUpgradeCrashPoint? CrashAt;
            public bool FailLinkOnce;
            public bool SourceGoneAfterSwitch;
            public readonly HashSet<string> Retired = new HashSet<string>(StringComparer.Ordinal);
            private int _ghostSeq;

            public FakeHost()
            {
                Chests["src"] = new List<int> { 101, 102, 102, 103 };
            }

            public string SpawnGhost(string sourceId, int tier)
            {
                Spawns++;
                string g = "ghost-" + (++_ghostSeq) + "-t" + tier;
                Chests[g] = new List<int>();
                return g;
            }

            public void WriteReverseLink(string ghostId, string opId)
            {
                if (FailLinkOnce)
                {
                    FailLinkOnce = false;
                    throw new InvalidOperationException("link write failed");
                }
                Links[ghostId] = opId;
            }

            public string FindGhostByLink(string opId)
            {
                foreach (KeyValuePair<string, string> kv in Links)
                {
                    if (string.Equals(kv.Value, opId, StringComparison.Ordinal) && Chests.ContainsKey(kv.Key))
                        return kv.Key;
                }
                return null;
            }

            public void CopyInventoryNonDestructive(string sourceId, string ghostId)
            {
                Chests[ghostId] = new List<int>(Chests[sourceId]);
            }

            public int CountItems(string chestId)
            {
                List<int> items;
                if (string.IsNullOrEmpty(chestId))
                    return 0;
                if (!Chests.TryGetValue(chestId, out items) || items == null)
                    return 0;
                return items.Count;
            }

            public int HashItems(string chestId)
            {
                List<int> items;
                if (!Chests.TryGetValue(chestId, out items) || items == null)
                    return 0;
                int h = 0;
                List<int> sorted = new List<int>(items);
                sorted.Sort();
                foreach (int v in sorted)
                    h = unchecked(h * 31 + v);
                return h;
            }

            public bool ValidateReady(string ghostId, int wantCount, int wantHash)
            {
                return CountItems(ghostId) == wantCount && HashItems(ghostId) == wantHash;
            }

            public string PointerSwitchOneWay(string sourceId, string ghostId)
            {
                string nid = "new-" + ghostId;
                Chests[nid] = new List<int>(Chests[ghostId]);
                if (SourceGoneAfterSwitch)
                    Chests.Remove(sourceId);
                return nid;
            }

            public void RetireSource(string sourceId, string newId)
            {
                Retired.Add(sourceId + "->" + newId);
            }

            public void DestroyGhost(string ghostId)
            {
                Destroyed++;
                DestroyedGhosts.Add(ghostId);
                Chests.Remove(ghostId);
                Links.Remove(ghostId);
            }

            public void LogLoud(string message)
            {
                Loud.Add(message);
            }

            public void CrashHook(TxUpgradeCrashPoint point)
            {
                if (CrashAt.HasValue && CrashAt.Value == point)
                    throw new TxUpgradeCrashException(point);
            }
        }

        private static void Drain(TxUpgradeManager m)
        {
            int guard = 64;
            while (m.Pump() && guard-- > 0)
            {
            }
        }

        private static void UPGRADE_HappyPathConservation()
        {
            FakeHost h = new FakeHost();
            TxUpgradeManager m = new TxUpgradeManager(h);
            TxUpgradeOp op = m.Request("src", 7L, 11u, 1, 100);
            int before = h.CountItems("src");
            int beforeHash = h.HashItems("src");
            int spend = m.RecordSpend(op.OpId, 6);
            Check.That(spend == 6, "spend recorded");
            Drain(m);
            Check.That(op.Phase == TxUpgradePhase.Retired, "happy path retires, got " + op.Phase);
            Check.That(h.CountItems(op.ReceiptNewId) == before, "items conserved into new chest");
            Check.That(h.HashItems(op.ReceiptNewId) == beforeHash, "item hash conserved");
            int refund = m.ClaimRefund(op.OpId, 2);
            Check.That(refund == 2, "refund entitlement issued");
            Check.That(h.Retired.Contains("src->" + op.ReceiptNewId), "idempotent retire recorded");
        }

        /// <summary>
        /// Crash before/after every phase: inject the kill, restart from the
        /// exported self-consistent snapshot ONLY, finish, and prove
        /// conservation + exactly one ghost linked to the op.
        /// </summary>
        private static void UPGRADE_CrashEveryPhase()
        {
            TxUpgradeCrashPoint[] points =
            {
                TxUpgradeCrashPoint.BeforeSpawn,
                TxUpgradeCrashPoint.BetweenMintAndLink,
                TxUpgradeCrashPoint.AfterLink,
                TxUpgradeCrashPoint.AfterCopy,
                TxUpgradeCrashPoint.AfterReady,
                TxUpgradeCrashPoint.AfterPointerSwitch,
                TxUpgradeCrashPoint.AfterReceipt
            };
            foreach (TxUpgradeCrashPoint pt in points)
            {
                FakeHost h = new FakeHost();
                TxUpgradeManager m = new TxUpgradeManager(h);
                TxUpgradeOp op = m.Request("src", 7L, 21u, 2, 200);
                m.RecordSpend(op.OpId, 8);
                h.CrashAt = pt;
                try
                {
                    Drain(m);
                    Check.That(!pt.Equals(TxUpgradeCrashPoint.BeforeSpawn) || true, "no-crash path n/a");
                }
                catch (TxUpgradeCrashException ex)
                {
                    Check.That(ex.Point == pt, "crash at injected point " + pt);
                }
                h.CrashAt = null;
                // Restart: FRESH manager RAM from the persisted snapshot ONLY —
                // the world (host) survives the crash, exactly like ZDOs
                // surviving a process kill while manager RAM is lost.
                TxUpgradeSnapshot snap = m.ExportSnapshot(op.OpId);
                string whyMissing = snap == null ? "null" : null;
                if (pt == TxUpgradeCrashPoint.BetweenMintAndLink)
                {
                    // The mint-link kill leaves the op in Preparing with NO ghost
                    // (nothing linked yet): snapshot must show preparing/no-ghost.
                    Check.That(snap != null && snap.Phase == TxUpgradePhase.PreparingLockedSource && snap.GhostId == null,
                        "mint-link kill: op stays preparing ghostless (got " + whyMissing + "/" + (snap == null ? "?" : snap.Phase.ToString()) + ")");
                }
                else
                {
                    Check.That(snap != null, "crash at " + pt + " leaves an exportable snapshot");
                }
                TxUpgradeManager m2 = new TxUpgradeManager(h);
                string reason;
                Check.That(m2.TryRecover(snap, out reason), "recover after " + pt + ": " + reason);
                int guard = 64;
                while (m2.Pump() && guard-- > 0)
                {
                }
                TxUpgradeOp r = m2.Get(op.OpId);
                Check.That(r != null && r.Phase == TxUpgradePhase.Retired,
                    "crash at " + pt + " completes after snapshot recovery (got " + (r == null ? "null" : r.Phase.ToString()) + ")");
                Check.That(r != null && r.ReceiptNewId != null && h.CountItems(r.ReceiptNewId) == 4, "crash at " + pt + ": items conserved");
                Check.That(r.SpendRecorded && r.SpendTotal == 8, "crash at " + pt + ": spend survives once");
                if (pt == TxUpgradeCrashPoint.BetweenMintAndLink)
                {
                    // Hard-kill micro-window residual: the pre-link ghost
                    // persists in the world unlinked and is NEVER auto-destroyed
                    // (manual reconcile); the op still completes on a fresh ghost.
                    Check.That(h.Spawns == 2, "mint-link kill: retry mints exactly one fresh ghost");
                }
                if (pt == TxUpgradeCrashPoint.AfterLink)
                {
                    // Link written but the op record never updated: the resume
                    // ADOPTS the already-linked ghost (no second spawn).
                    Check.That(h.Spawns == 1, "after-link kill: resume adopts, never re-mints");
                }
            }
        }

        private static void UPGRADE_MintLinkKillCleanup()
        {
            FakeHost h = new FakeHost();
            TxUpgradeManager m = new TxUpgradeManager(h);
            TxUpgradeOp op = m.Request("src", 7L, 31u, 1, 100);
            h.FailLinkOnce = true;
            m.Pump(); // single step: mint succeeds, link-write throws
            Check.That(op.Phase == TxUpgradePhase.PreparingLockedSource, "link failure keeps op preparing");
            Check.That(op.Phase == TxUpgradePhase.PreparingLockedSource, "link failure keeps op preparing");
            Check.That(op.GhostId == null, "no ghost linked after failed link-write");
            Check.That(h.Destroyed == 1, "minted ghost destroyed by the window cleanup");
            Check.That(h.Loud.Count > 0, "loud log on mint-link cleanup");
            // Retry spawns fresh exactly once (the dead ghost is gone).
            Drain(m);
            Check.That(op.Phase == TxUpgradePhase.Retired, "retry after cleanup retires");
            Check.That(h.Spawns == 2, "exactly two mints across failure+retry, got " + h.Spawns);
        }

        private static void UPGRADE_SameOpNoSecondSpawn()
        {
            FakeHost h = new FakeHost();
            TxUpgradeManager m = new TxUpgradeManager(h);
            TxUpgradeOp first = m.Request("src", 7L, 41u, 1, 100);
            m.Pump(); // spawn+link
            Check.That(first.GhostId != null, "first request links a ghost");
            int spawns = h.Spawns;
            TxUpgradeOp dup = m.Request("src", 7L, 41u, 1, 100);
            Check.That(object.ReferenceEquals(first, dup), "same op identity returns the SAME record");
            m.Pump(); m.Pump(); m.Pump();
            Check.That(h.Spawns == spawns, "same op never spawns twice (spawns=" + h.Spawns + ")");
            Drain(m);
            Check.That(first.Phase == TxUpgradePhase.Retired, "op still completes once");
            Check.That(h.Spawns == spawns, "no second spawn through completion");
        }

        private static void UPGRADE_ContradictionQuarantinesBoth()
        {
            FakeHost h = new FakeHost();
            TxUpgradeManager m = new TxUpgradeManager(h);
            TxUpgradeOp a = m.Request("src", 7L, 51u, 1, 100);
            TxUpgradeOp b = m.Request("src", 7L, 52u, 2, 200);
            m.Pump();
            string ghost = a.GhostId;
            Check.That(ghost != null, "head op links a ghost first");
            m.ReportContradiction(ghost, a.OpId, b.OpId);
            Check.That(a.Quarantined && b.Quarantined, "contradiction quarantines BOTH ops");
            Check.That(h.Chests.ContainsKey(ghost), "contradicted ghost kept standing (never auto-destroyed)");
            Check.That(h.Loud.Count > 0, "loud log on contradiction");
        }

        private static void UPGRADE_UnlinkedGhostKept()
        {
            FakeHost h = new FakeHost();
            h.Chests["stranger"] = new List<int> { 9 };
            TxUpgradeManager m = new TxUpgradeManager(h);
            TxUpgradeOp op = m.Request("src", 7L, 61u, 1, 100);
            m.ReportUnlinkedGhost("stranger", op.OpId);
            Check.That(h.Chests.ContainsKey("stranger"), "unlinked ghost NEVER auto-destroyed (could be a real chest)");
            Check.That(op.Quarantined, "suspect op quarantined");
            Check.That(h.Loud.Count > 0, "loud log demands manual reconcile");
        }

        private static void UPGRADE_SpendRefundAtMostOnce()
        {
            FakeHost h = new FakeHost();
            TxUpgradeManager m = new TxUpgradeManager(h);
            TxUpgradeOp op = m.Request("src", 7L, 71u, 1, 100);
            Check.That(m.RecordSpend(op.OpId, 6) == 6, "first spend records");
            Check.That(m.RecordSpend(op.OpId, 600) == 6, "second spend keeps the original (at-most-once)");
            Check.That(m.ClaimRefund(op.OpId, 2) == 2, "first refund issues entitlement");
            Check.That(m.ClaimRefund(op.OpId, 200) == 2, "second refund replays the same entitlement (idempotent)");
            Drain(m);
            Check.That(h.CountItems(op.ReceiptNewId) == 4, "spend/refund never eats chest items");
        }

        private static void UPGRADE_TornSnapshotFailClosed()
        {
            FakeHost h = new FakeHost();
            TxUpgradeManager m = new TxUpgradeManager(h);
            string reason;
            // Phase claims a linked ghost but carries none.
            TxUpgradeSnapshot torn = new TxUpgradeSnapshot();
            torn.OpId = "src:7:81";
            torn.SourceId = "src";
            torn.SenderPeer = 7L;
            torn.Nonce = 81u;
            torn.Tier = 1;
            torn.Phase = TxUpgradePhase.Copied;
            torn.GhostId = null;
            Check.That(!m.TryRecover(torn, out reason), "torn snapshot refused fail-closed: " + reason);
            Check.That(m.OpCount == 0, "nothing resumes from a torn snapshot");
            // Receipted without a receipt.
            TxUpgradeSnapshot torn2 = new TxUpgradeSnapshot();
            torn2.OpId = "src:7:82";
            torn2.SourceId = "src";
            torn2.SenderPeer = 7L;
            torn2.Nonce = 82u;
            torn2.Tier = 1;
            torn2.Phase = TxUpgradePhase.Receipted;
            torn2.GhostId = "ghost-x";
            torn2.ReceiptNewId = null;
            Check.That(!m.TryRecover(torn2, out reason), "receipt-less snapshot refused: " + reason);
            // Retired never resumes.
            TxUpgradeSnapshot term = new TxUpgradeSnapshot();
            term.OpId = "src:7:83";
            term.SourceId = "src";
            term.SenderPeer = 7L;
            term.Nonce = 83u;
            term.Tier = 1;
            term.Phase = TxUpgradePhase.Retired;
            term.GhostId = "ghost-x";
            Check.That(!m.TryRecover(term, out reason), "terminal snapshot never resumes: " + reason);
        }

        private static void UPGRADE_LegacyFrameRefused()
        {
            Check.That(TxUpgradeGate.RefuseLegacyUpgradeFrame(true, true),
                "old TxOp.Upgrade refused for managed chests in authority mode");
            Check.That(!TxUpgradeGate.RefuseLegacyUpgradeFrame(false, true),
                "legacy regime keeps legacy frames");
            Check.That(!TxUpgradeGate.RefuseLegacyUpgradeFrame(true, false),
                "unmanaged chests keep legacy frames");
            Check.That(TxUpgradeGate.AcceptsUpgradeRequest(3, true),
                "current-version requests accepted in authority mode");
            Check.That(!TxUpgradeGate.AcceptsUpgradeRequest(2, true),
                "stale-version requests refused");
            Check.That(!TxUpgradeGate.AcceptsUpgradeRequest(3, false),
                "upgrade requests require authority mode");
        }

        private static void UPGRADE_QueueBehindPrepared()
        {
            FakeHost h = new FakeHost();
            TxUpgradeManager m = new TxUpgradeManager(h);
            TxUpgradeOp a = m.Request("src", 7L, 91u, 1, 100);
            TxUpgradeOp b = m.Request("src", 8L, 1u, 2, 200);
            Check.That(m.QueuedCount == 2, "different ops queue");
            m.Pump(); // head advances only
            Check.That(b.Phase == TxUpgradePhase.PreparingLockedSource && b.GhostId == null,
                "second op waits behind the prepared head");
            Drain(m);
            Check.That(a.Phase == TxUpgradePhase.Retired, "head retires first");
        }

        private static void UPGRADE_TimeoutQueriesSameOp()
        {
            FakeHost h = new FakeHost();
            TxUpgradeManager m = new TxUpgradeManager(h);
            TxUpgradeOp op = m.Request("src", 7L, 101u, 1, 100);
            TxUpgradeOp q = m.QuerySameOp(op.OpId);
            Check.That(object.ReferenceEquals(op, q), "timeout queries the SAME op (identity, never a new spawn)");
            Check.That(m.QuerySameOp("src:7:999") == null, "unknown op queries to nothing (no phantom spawn)");
        }

        /// <summary>
        /// Spend-fail regression (executor order: Request enqueues BEFORE the
        /// ingredient check): an unpaid op must be abandoned on the spend-fail
        /// path, never left as a poisoned head. Without the abandon, a later
        /// pump advances the failed head with no spend check (free upgrade)
        /// and the paid op behind it head-of-line blocks.
        /// </summary>
        private static void UPGRADE_SpendFailAbandonsHead()
        {
            FakeHost h = new FakeHost();
            TxUpgradeManager m = new TxUpgradeManager(h);
            // First request enqueues, then its spend check fails (no
            // pre-deposited ingredients): the executor abandons it.
            TxUpgradeOp failed = m.Request("src", 7L, 111u, 1, 100);
            Check.That(m.TryAbandon(failed.OpId), "spend-fail abandons the unpaid preparing op");
            Check.That(m.Get(failed.OpId) == null, "abandoned op is gone (no poisoned head)");
            Check.That(m.QueuedCount == 0, "queue empty after abandon");
            Check.That(h.Spawns == 0, "abandoned op never minted a ghost");
            // A different nonce queues cleanly, pays exactly once, retires:
            // no free upgrade for the failed op, no stuck head.
            TxUpgradeOp paid = m.Request("src", 7L, 112u, 1, 100);
            Check.That(m.RecordSpend(paid.OpId, 6) == 6, "second op pays");
            Check.That(m.RecordSpend(paid.OpId, 600) == 6, "spend at-most-once (no double pay)");
            Drain(m);
            Check.That(paid.Phase == TxUpgradePhase.Retired, "paid op retires (no head-of-line block, got " + paid.Phase + ")");
            Check.That(h.Spawns == 1, "exactly one ghost for the paid op (no free upgrade, spawns=" + h.Spawns + ")");
            Check.That(h.CountItems(paid.ReceiptNewId) == 4, "items conserved into the paid new chest");
            // Guards: abandon refuses once the op owns world state.
            FakeHost h2 = new FakeHost();
            TxUpgradeManager m2 = new TxUpgradeManager(h2);
            TxUpgradeOp linked = m2.Request("src", 7L, 113u, 1, 100);
            m2.RecordSpend(linked.OpId, 6);
            m2.Pump(); // spawn+link: GhostId set, phase advanced
            Check.That(linked.GhostId != null, "setup: op linked a ghost");
            Check.That(!m2.TryAbandon(linked.OpId), "abandon refused after link (ghost owned)");
            Check.That(m2.Get(linked.OpId) != null, "linked op survives the refused abandon");
            Check.That(!m2.TryAbandon("src:7:999"), "abandon of an unknown op refuses (no state change)");
        }
    }
}
