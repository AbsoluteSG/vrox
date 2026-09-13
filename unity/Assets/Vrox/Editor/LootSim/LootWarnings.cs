using System.Collections.Generic;
using System.Linq;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Loot set-ups that will not drop what their assets suggest.
    /// </summary>
    /// <remarks>
    /// Each one is a table that still pushes and still rolls, just not the way it
    /// reads — which is the failure a designer cannot tell from bad luck.
    /// </remarks>
    internal static class LootWarnings
    {
        /// <summary>Problems that affect everything, not one asset.</summary>
        public static void Global(LootAssets lib, List<string> into)
        {
            into.Clear();

            foreach (var group in lib.Pools.Where(p => p.Id != 0).GroupBy(p => p.Id).Where(g => g.Count() > 1))
            {
                var kept = lib.ServerPool(group.Key);
                into.Add($"Pools {string.Join(", ", group.Select(p => $"\"{p.name}\""))} share id {group.Key}. " +
                         $"The server keeps the one pushed last (\"{kept?.name}\"); the others never drop.");
            }

            var items = lib.Items;
            bool refused = items.Any(i => i.Id == 0 || i.Id > EquipmentItem.MaxAuthoredId)
                           || items.GroupBy(i => i.CatalogueId).Any(g => g.Count() > 1);
            if (refused)
            {
                into.Add("An item has id 0, an id above the maximum, or a duplicate catalogue id. Push All refuses " +
                         "to run until that is fixed, so none of these tables reach the server.");
            }
        }

        public static void Pool(LootPoolItem pool, LootAssets lib, LootOdds.PoolYield yield, List<string> into)
        {
            into.Clear();
            if (pool.Id == 0)
            {
                into.Add("Id 0 is reserved for \"drops nothing\". The push skips this pool, so it never drops.");
            }
            else if (lib.ServerPool(pool.Id) is { } kept && kept != pool)
            {
                into.Add($"Shares id {pool.Id} with \"{kept.name}\", which is pushed later and replaces this pool on the server.");
            }

            var items = pool.Items ?? new List<LootRoll>();
            if (items.Count == 0)
            {
                into.Add("No entries: choosing this pool never drops a bag.");
                return;
            }
            if (items.Count > 255)
            {
                into.Add($"Only the first 255 of {items.Count} entries are pushed.");
            }

            float best = items.Where(r => r?.Item != null).Select(r => r.ChancePercent).DefaultIfEmpty(0f).Max();
            if (best < 100f)
            {
                into.Add($"No entry at 100%, so this pool can be chosen and still drop nothing " +
                         $"({LootOdds.Percent(1.0 - yield.Bag)} of the time). The enemy's No Drop Weight " +
                         "already says how often nothing drops.");
            }

            for (int i = 0; i < items.Count; i++)
            {
                var roll = items[i];
                if (roll?.Item == null)
                {
                    into.Add($"Entry {i} has no item and is skipped.");
                    continue;
                }
                string name = roll.Item.name;
                if (roll.ChancePercent <= 0f)
                {
                    into.Add($"Entry {i} ({name}) is at 0% and never drops.");
                }
                if (roll.Count > roll.Item.MaxStack)
                {
                    into.Add($"Entry {i} ({name}) drops {roll.Count}, but it stacks to {roll.Item.MaxStack}; " +
                             $"the push sends {roll.Item.MaxStack}.");
                }
                var (w, h) = LootMath.Footprint(lib.Shape(roll.Item.CatalogueId));
                if (w > LootMath.PackWidth || h > LootMath.PackHeight)
                {
                    into.Add($"{name} is {w}x{h}, bigger than the {LootMath.PackWidth}x{LootMath.PackHeight} pack, " +
                             "so it can never be taken out of a bag.");
                }
            }

            foreach (var group in items.Where(r => r?.Item != null).GroupBy(r => r.Item!.CatalogueId).Where(g => g.Count() > 1))
            {
                into.Add($"{group.First().Item!.name} appears {group.Count()} times. Each rolls on its own, and " +
                         "two hits make two separate stacks in the bag.");
            }
        }

        public static void Enemy(EnemyItem e, LootAssets lib, LootOdds.EnemyOdds odds, List<string> into)
        {
            into.Clear();
            if (odds.TotalWeight <= 0)
            {
                into.Add("Every weight is 0, including No Drop Weight, so this enemy never drops anything.");
                return;
            }

            var pools = e.LootPools ?? new List<LootPoolWeight>();
            if (pools.Count > 254)
            {
                into.Add($"Only the first 254 of {pools.Count} pool entries are pushed.");
            }

            for (int i = 0; i < pools.Count; i++)
            {
                var weighted = pools[i];
                if (weighted?.Pool == null)
                {
                    into.Add($"Pool entry {i} is empty and is skipped.");
                    continue;
                }
                string name = weighted.Pool.name;
                if (weighted.Weight == 0)
                {
                    into.Add($"Entry {i} ({name}) has weight 0 and is skipped.");
                    continue;
                }
                if (weighted.Pool.Id == 0)
                {
                    into.Add($"{name} has id 0, so its weight behaves like extra No Drop Weight.");
                    continue;
                }
                var kept = lib.ServerPool(weighted.Pool.Id);
                if (kept != weighted.Pool)
                {
                    into.Add($"{name} shares id {weighted.Pool.Id} with \"{kept?.name}\"; this enemy really rolls that pool.");
                }
                else if (LootOdds.Yield(weighted.Pool, lib).Bag <= 0.0)
                {
                    into.Add($"{name} can never produce a bag, so its weight is really more No Drop Weight.");
                }
            }
        }
    }
}
