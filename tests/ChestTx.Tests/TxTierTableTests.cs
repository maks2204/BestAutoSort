using System;
using BestAutoSort.Core;

namespace ChestTx.Tests
{
    /// <summary>
    /// Managed-tier resolution table (marker-wins + prefab fallback): pins the
    /// contract the buttons, the entry gate, ApplyState and the server marker
    /// flow all share. An explicit marker (1..3, stamped only by our own
    /// upgrade flows) always wins because in-place upgrades never change the
    /// prefab; unmarked (0/absent) chests resolve by prefab so naturally built
    /// reinforced/blackmetal/grausten are tiers 1/2/3 instead of -1.
    /// </summary>
    internal static class TxTierTableTests
    {
        private static void Check(int actual, int expected, string name)
        {
            if (actual != expected)
                throw new InvalidOperationException("tier table: " + name + " expected=" + expected + " actual=" + actual);
        }

        private static void TIER_Delta()
        {
            System.Collections.Generic.Dictionary<string, int> target =
                new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
            target["$item_iron"] = 10;
            target["$item_blackmetal"] = 20;
            target["$item_tar"] = 5;
            System.Collections.Generic.Dictionary<string, int> current =
                new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
            current["$item_iron"] = 10;
            current["$item_copper"] = 4;
            System.Collections.Generic.Dictionary<string, int> d =
                ChestUpgradePath.DeltaCosts(target, current);
            if (d.Count != 2 || d["$item_blackmetal"] != 20 || d["$item_tar"] != 5)
                throw new InvalidOperationException("delta: overlap must credit iron fully");
            if (d.ContainsKey("$item_iron") || d.ContainsKey("$item_copper"))
                throw new InvalidOperationException("delta: zeroed/current-only keys must drop");
            System.Collections.Generic.Dictionary<string, int> full =
                ChestUpgradePath.DeltaCosts(target, null);
            if (full.Count != 3)
                throw new InvalidOperationException("delta: null current must yield full target");
            System.Collections.Generic.Dictionary<string, int> empty =
                ChestUpgradePath.DeltaCosts(null, current);
            if (empty.Count != 0)
                throw new InvalidOperationException("delta: null target must yield empty");
            System.Collections.Generic.Dictionary<string, int> pt =
                new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
            pt["$item_wood"] = 10;
            pt["$item_stone"] = 5;
            pt["$item_flint"] = 0;
            pt[""] = 3;
            System.Collections.Generic.Dictionary<string, int> pc =
                new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
            pc["$item_wood"] = 4;
            pc["$item_stone"] = 9;
            System.Collections.Generic.Dictionary<string, int> pd =
                ChestUpgradePath.DeltaCosts(pt, pc);
            if (pd.Count != 1 || pd["$item_wood"] != 6)
                throw new InvalidOperationException("delta: partial 10-4=6, over-cover and junk drop");
            System.Collections.Generic.Dictionary<string, int> r =
                ChestUpgradePath.DeltaCosts(current, target);
            if (r.Count != 1 || r["$item_copper"] != 4)
                throw new InvalidOperationException("delta: reversed args yield previous-unique refund");
        }

        public static void RunAll()
        {
            Console.WriteLine("TIER_MarkerWins");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest_wood", 2), 2, "wood+2");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest", 2), 2, "reinforced+2");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest", 1), 1, "reinforced+1");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest_blackmetal", 2), 2, "blackmetal+2");
            Console.WriteLine("TIER_PrefabFallback");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest_wood", 0), 0, "wood+0");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest", 0), 1, "reinforced+0");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest_blackmetal", 0), 2, "blackmetal+0");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest_grausten", 0), 3, "grausten+0");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest_grausten", 3), 3, "grausten+3");
            Console.WriteLine("TIER_FailClosed");
            Check(ChestUpgradePath.ResolveManagedTier("piece_cart", 0), -1, "unknown+0");
            Check(ChestUpgradePath.ResolveManagedTier("piece_cart", 2), 2, "unknown+2");
            Check(ChestUpgradePath.ResolveManagedTier("piece_chest_wood", 9), 0, "wood+garbage-marker");
            Console.WriteLine("TIER_PrefabMatch");
            if (!ChestUpgradePath.PrefabMatchesTier("piece_chest", 1))
                throw new InvalidOperationException("match: reinforced tier 1");
            if (!ChestUpgradePath.PrefabMatchesTier("piece_chest_blackmetal", 2))
                throw new InvalidOperationException("match: blackmetal tier 2");
            if (ChestUpgradePath.PrefabMatchesTier("piece_chest", 2))
                throw new InvalidOperationException("match: reinforced is not tier 2");
            if (ChestUpgradePath.PrefabMatchesTier("piece_chest_wood", 2))
                throw new InvalidOperationException("match: wood is not tier 2");
            if (ChestUpgradePath.PrefabMatchesTier("piece_chest", 9))
                throw new InvalidOperationException("match: bad tier must fail");
            Console.WriteLine("TIER_Paths");
            if (!ChestUpgradePath.CanUpgrade(1, 2))
                throw new InvalidOperationException("tier path: 1->2 must hold");
            if (!ChestUpgradePath.CanUpgrade(0, 3))
                throw new InvalidOperationException("tier path: 0->3 must hold");
            if (ChestUpgradePath.CanUpgrade(2, 2))
                throw new InvalidOperationException("tier path: 2->2 must fail");
            if (ChestUpgradePath.CanUpgrade(-1, 2))
                throw new InvalidOperationException("tier path: -1->2 must fail");
            if (ChestUpgradePath.CanUpgrade(2, 1))
                throw new InvalidOperationException("tier path: 2->1 must fail");
            Console.WriteLine("TIER_Delta");
            TIER_Delta();
        }
    }
}
