using System;
using System.Collections.Generic;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// A seeded batch of kills, rolled the way the server rolls them, and what fits in a pack afterwards.
    /// </summary>
    /// <remarks>
    /// Where <see cref="LootOdds"/> gives the average, this gives the experience:
    /// the dry spells, the lucky streaks, what a run of bags actually looks like.
    /// A seed makes it repeatable, so a change to a pool can be compared against
    /// the same run of luck.
    /// </remarks>
    internal sealed class LootRoller
    {
        /// <summary>Bags kept for drawing and pack fit. Statistics cover every kill.</summary>
        public const int KeptBags = 400;

        public struct Bag
        {
            public int Kill;
            public byte Kind;
            public BagStack[] Items;
        }

        public sealed class PackFit
        {
            public readonly List<PackStack> Pack = new();
            public readonly List<(int Bag, BagStack Left)> Left = new();
            public int FirstOverflow = -1;
            public int Looted;
        }

        public readonly List<Bag> Bags = new();
        public readonly long[] KindCounts = new long[LootOdds.BagKinds];

        /// <summary>Kills that dropped at least one stack of an item.</summary>
        public readonly Dictionary<ushort, int> KillsWithItem = new();

        public readonly Dictionary<ushort, long> ItemCount = new();
        public int Kills;
        public int BagCount;
        public int LongestDry;

        private readonly List<PoolWeightRow> _rows = new();
        private readonly List<LootEntryRow> _entries = new();
        private readonly List<BagStack> _contents = new();
        private readonly HashSet<ushort> _seen = new();

        public void Roll(EnemyItem e, LootAssets lib, int kills, int seed)
        {
            Bags.Clear();
            Array.Clear(KindCounts, 0, KindCounts.Length);
            KillsWithItem.Clear();
            ItemCount.Clear();
            Kills = Math.Max(0, kills);
            BagCount = 0;
            LongestDry = 0;

            var rng = new Random(seed);
            LootAssets.EnemyRows(e, _rows);

            // Resolved once per pool: the rows cannot change during a batch.
            var resolved = new Dictionary<ushort, (LootEntryRow[] Rows, byte Kind)?>();
            int dry = 0;

            for (int kill = 0; kill < Kills; kill++)
            {
                ushort poolId = LootMath.RollPool(_rows, rng);
                _contents.Clear();
                byte kind = 0;

                if (poolId != 0)
                {
                    if (!resolved.TryGetValue(poolId, out var pool))
                    {
                        var asset = lib.ServerPool(poolId);
                        pool = asset != null && LootAssets.PoolRows(asset, _entries)
                            ? (_entries.ToArray(), (byte)asset.Bag)
                            : null;
                        resolved[poolId] = pool;
                    }
                    if (pool is { } p)
                    {
                        kind = p.Kind;
                        LootMath.RollEntries(p.Rows, rng, lib.ItemExists, _contents);
                    }
                }

                if (_contents.Count == 0)
                {
                    dry++;
                    LongestDry = Math.Max(LongestDry, dry);
                    continue;
                }

                dry = 0;
                BagCount++;
                KindCounts[Math.Min((int)kind, KindCounts.Length - 1)]++;
                _seen.Clear();
                foreach (var stack in _contents)
                {
                    ItemCount[stack.ItemId] = (ItemCount.TryGetValue(stack.ItemId, out var n) ? n : 0) + stack.Count;
                    if (_seen.Add(stack.ItemId))
                    {
                        KillsWithItem[stack.ItemId] = (KillsWithItem.TryGetValue(stack.ItemId, out var k) ? k : 0) + 1;
                    }
                }
                if (Bags.Count < KeptBags)
                {
                    Bags.Add(new Bag { Kill = kill, Kind = kind, Items = _contents.ToArray() });
                }
            }
        }

        /// <summary>
        /// Takes All from the first <paramref name="bags"/> bags, in order, into one empty pack.
        /// </summary>
        /// <remarks>
        /// An empty pack is the best case. A real pack already holds a weapon, armour
        /// and whatever the run has picked up, so it fills sooner than this shows.
        /// </remarks>
        public PackFit Fit(LootAssets lib, int bags)
        {
            var fit = new PackFit();
            var left = new List<BagStack>();
            int n = Math.Min(bags, Bags.Count);
            for (int b = 0; b < n; b++)
            {
                LootMath.TakeAll(Bags[b].Items, fit.Pack, lib.Shape, left);
                fit.Looted++;
                foreach (var stack in left)
                {
                    fit.Left.Add((b, stack));
                }
                if (left.Count > 0 && fit.FirstOverflow < 0)
                {
                    fit.FirstOverflow = b;
                }
            }
            return fit;
        }
    }
}
