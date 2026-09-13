using System;
using System.Collections.Generic;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Exact drop probabilities, computed from the rows the server would hold.
    /// </summary>
    /// <remarks>
    /// Exact, not sampled, because the roll allows it: one weighted pick between
    /// mutually exclusive pools, then independent entries. Each row's chance is its
    /// weight over the total; each entry hits with its percentage; a pool yields a
    /// bag unless every valid entry misses. Nothing about the player or the world
    /// changes any of it.
    /// </remarks>
    internal static class LootOdds
    {
        public const int BagKinds = 6;

        public sealed class PoolShare
        {
            public LootPoolItem? Pool;
            public ushort PoolId;
            public uint Weight;

            /// <summary>Chance this row is the one picked.</summary>
            public double Share;

            /// <summary>Chance of a bag once this pool is picked.</summary>
            public double BagIfChosen;
        }

        public sealed class ItemChance
        {
            public EquipmentItem Item = null!;

            /// <summary>Chance a kill drops at least one stack of it.</summary>
            public double PerKill;

            /// <summary>Average number of it per kill.</summary>
            public double ExpectedCount;
        }

        public sealed class EnemyOdds
        {
            public long TotalWeight;
            public double Bag;
            public readonly double[] ByKind = new double[BagKinds];

            /// <summary>Average stacks per kill. A stack of three salves is one.</summary>
            public double ExpectedStacks;

            public readonly List<PoolShare> Pools = new();
            public readonly List<ItemChance> Items = new();
        }

        public sealed class PoolYield
        {
            public double Bag;
            public double ExpectedStacks;
            public readonly Dictionary<ushort, double> ItemChance = new();
            public readonly Dictionary<ushort, double> ItemCount = new();
        }

        [ThreadStatic] private static List<PoolWeightRow>? _rows;
        [ThreadStatic] private static List<LootEntryRow>? _entries;

        /// <summary>What a pool produces once chosen.</summary>
        public static PoolYield Yield(LootPoolItem pool, LootAssets lib)
        {
            var y = new PoolYield();
            var entries = _entries ??= new List<LootEntryRow>();
            if (!LootAssets.PoolRows(pool, entries))
            {
                return y;
            }

            double nothing = 1.0;
            var missed = new Dictionary<ushort, double>();
            foreach (var e in entries)
            {
                if (e.ItemId == 0 || e.Count == 0 || !lib.ItemExists(e.ItemId))
                {
                    continue;
                }
                double p = Math.Clamp(e.ChancePercent / 100.0, 0.0, 1.0);
                nothing *= 1.0 - p;
                y.ExpectedStacks += p;
                missed[e.ItemId] = (missed.TryGetValue(e.ItemId, out var m) ? m : 1.0) * (1.0 - p);
                y.ItemCount[e.ItemId] = (y.ItemCount.TryGetValue(e.ItemId, out var c) ? c : 0.0) + p * e.Count;
            }

            y.Bag = 1.0 - nothing;
            foreach (var pair in missed)
            {
                y.ItemChance[pair.Key] = 1.0 - pair.Value;
            }
            return y;
        }

        /// <summary>Everything one kill of this enemy can produce.</summary>
        public static EnemyOdds ForEnemy(EnemyItem e, LootAssets lib)
        {
            var odds = new EnemyOdds();
            var rows = _rows ??= new List<PoolWeightRow>();
            LootAssets.EnemyRows(e, rows);
            foreach (var row in rows)
            {
                odds.TotalWeight += row.Weight;
            }
            if (odds.TotalWeight <= 0)
            {
                return odds;
            }

            var chance = new Dictionary<ushort, double>();
            var count = new Dictionary<ushort, double>();
            foreach (var row in rows)
            {
                if (row.PoolId == 0 && row.Weight > 0 && row.Equals(rows[0]))
                {
                    continue; // the no-drop slice
                }

                double share = row.Weight / (double)odds.TotalWeight;
                var pool = lib.ServerPool(row.PoolId);
                var entry = new PoolShare { Pool = pool, PoolId = row.PoolId, Weight = row.Weight, Share = share };
                odds.Pools.Add(entry);
                if (pool == null)
                {
                    continue;
                }

                var y = Yield(pool, lib);
                entry.BagIfChosen = y.Bag;
                odds.Bag += share * y.Bag;
                odds.ByKind[Math.Clamp((int)pool.Bag, 0, BagKinds - 1)] += share * y.Bag;
                odds.ExpectedStacks += share * y.ExpectedStacks;
                foreach (var pair in y.ItemChance)
                {
                    chance[pair.Key] = (chance.TryGetValue(pair.Key, out var c) ? c : 0.0) + share * pair.Value;
                    count[pair.Key] = (count.TryGetValue(pair.Key, out var n) ? n : 0.0) + share * y.ItemCount[pair.Key];
                }
            }

            foreach (var pair in chance)
            {
                if (lib.Item(pair.Key) is { } item)
                {
                    odds.Items.Add(new ItemChance { Item = item, PerKill = pair.Value, ExpectedCount = count[pair.Key] });
                }
            }
            odds.Items.Sort((a, b) => b.PerKill.CompareTo(a.PerKill));
            return odds;
        }

        /// <summary>Kills needed for at least <paramref name="confidence"/> chance of one hit, or -1 if it can never happen.</summary>
        public static int KillsFor(double perKill, double confidence)
        {
            if (perKill <= 0.0)
            {
                return -1;
            }
            if (perKill >= 1.0)
            {
                return 1;
            }
            return (int)Math.Ceiling(Math.Log(1.0 - confidence) / Math.Log(1.0 - perKill));
        }

        public static string Percent(double p)
        {
            if (p <= 0.0) return "—";
            if (p < 0.001) return "<0.1%";
            if (p >= 1.0) return "100%";
            if (p > 0.999) return ">99.9%";
            return (p * 100.0).ToString("0.0") + "%";
        }

        public static string Kills(int kills) => kills < 0 ? "never" : kills.ToString("N0");
    }
}
