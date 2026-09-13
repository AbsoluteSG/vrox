using System;
using System.Collections.Generic;

namespace Vrox.Equipment
{
    /// <summary>One row of an enemy's drop table as the server stores it: <c>EnemyLoot</c>.</summary>
    public struct PoolWeightRow
    {
        /// <summary>0 is the no-drop slice.</summary>
        public ushort PoolId;
        public uint Weight;
    }

    /// <summary>One entry of a pool as the server stores it: <c>LootEntry</c>.</summary>
    public struct LootEntryRow
    {
        public ushort ItemId;
        public float ChancePercent;
        public ushort Count;
    }

    /// <summary>One stack in a dropped bag: <c>BagItem</c>.</summary>
    public struct BagStack
    {
        public ushort ItemId;
        public ushort Count;
    }

    /// <summary>One stack in the pack: a <c>GridItem</c> in container 0.</summary>
    public struct PackStack
    {
        public byte X;
        public byte Y;
        public ushort ItemId;
        public ushort Count;
    }

    /// <summary>The parts of an <c>ItemDef</c> that decide how it packs.</summary>
    public struct ItemShape
    {
        public ushort MaxStack;
        public byte Width;
        public byte Height;
    }

    /// <summary>
    /// The client's one copy of the server's loot roll and pack placement.
    /// </summary>
    /// <remarks>
    /// Each method mirrors a named piece of <c>server/module/Module.cs</c>:
    /// <c>RollPool</c>, the entry loop in <c>RecordKill</c>, and <c>FootprintOf</c>,
    /// <c>FitsAt</c>, <c>FindSpot</c>, <c>PutIn</c> and <c>TakeAllFromBag</c> for the
    /// pack. Kept in step by hand, like <see cref="VolleyMath"/>; the editor's loot
    /// simulator runs it and the game does not.
    ///
    /// Random numbers are drawn in the server's order — one for the pool, then one
    /// per entry that has an item and a count — so a seeded run is the same
    /// sequence of decisions the server would make from the same stream. The
    /// server's stream is shared with the rest of its tick, so a real drop cannot
    /// be replayed; the odds, which depend on nothing else, are exact.
    /// </remarks>
    public static class LootMath
    {
        public const byte PackWidth = 5;
        public const byte PackHeight = 3;

        /// <summary>Picks one pool by weight, or 0 for nothing. Mirrors <c>RollPool</c>.</summary>
        public static ushort RollPool(IReadOnlyList<PoolWeightRow> rows, Random rng)
        {
            long total = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                total += rows[i].Weight;
            }
            if (total <= 0)
            {
                return 0;
            }

            long pick = (long)(rng.NextDouble() * total);
            for (int i = 0; i < rows.Count; i++)
            {
                pick -= rows[i].Weight;
                if (pick < 0)
                {
                    return rows[i].PoolId;
                }
            }
            return 0;
        }

        /// <summary>Rolls every entry of a chosen pool into <paramref name="into"/>. Mirrors <c>RecordKill</c>.</summary>
        /// <remarks>
        /// An entry with no item or no count is skipped before drawing a number; an
        /// item missing from the catalogue is skipped after, so it still uses one.
        /// </remarks>
        public static void RollEntries(IReadOnlyList<LootEntryRow> entries, Random rng,
                                       Func<ushort, bool> itemExists, List<BagStack> into)
        {
            into.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.ItemId == 0 || entry.Count == 0
                    || rng.NextDouble() * 100.0 >= entry.ChancePercent)
                {
                    continue;
                }
                if (!itemExists(entry.ItemId))
                {
                    continue;
                }
                into.Add(new BagStack { ItemId = entry.ItemId, Count = entry.Count });
            }
        }

        /// <summary>Cells an item covers in the pack. Mirrors <c>FootprintOf</c>.</summary>
        public static (byte w, byte h) Footprint(ItemShape? shape) => shape is { } s
            ? (s.Width < 1 ? (byte)1 : s.Width, s.Height < 1 ? (byte)1 : s.Height)
            : ((byte)1, (byte)1);

        /// <summary>Whether a footprint fits at a pack cell. Mirrors <c>FitsAt</c>.</summary>
        public static bool FitsAt(List<PackStack> pack, Func<ushort, ItemShape?> shapes,
                                  int x, int y, int w, int h)
        {
            if (x + w > PackWidth || y + h > PackHeight)
            {
                return false;
            }
            for (int i = 0; i < pack.Count; i++)
            {
                var other = pack[i];
                var (ow, oh) = Footprint(shapes(other.ItemId));
                if (!(x + w <= other.X || other.X + ow <= x || y + h <= other.Y || other.Y + oh <= y))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The first free cell, rows then columns. Mirrors <c>FindSpot</c>.</summary>
        public static bool FindSpot(List<PackStack> pack, Func<ushort, ItemShape?> shapes,
                                    int w, int h, out byte fx, out byte fy)
        {
            for (int y = 0; y + h <= PackHeight; y++)
            {
                for (int x = 0; x + w <= PackWidth; x++)
                {
                    if (FitsAt(pack, shapes, x, y, w, h))
                    {
                        fx = (byte)x;
                        fy = (byte)y;
                        return true;
                    }
                }
            }
            fx = 0;
            fy = 0;
            return false;
        }

        /// <summary>Puts a stack in the pack, topping up matching stacks first. Mirrors <c>PutIn</c>.</summary>
        /// <returns>How many fitted.</returns>
        public static ushort PutIn(List<PackStack> pack, Func<ushort, ItemShape?> shapes,
                                   ushort itemId, ushort count)
        {
            if (count == 0)
            {
                return 0;
            }

            ushort max = shapes(itemId) is { } shape && shape.MaxStack > 1 ? shape.MaxStack : (ushort)1;
            ushort placed = 0;

            if (max > 1)
            {
                for (int i = 0; i < pack.Count && placed < count; i++)
                {
                    if (pack[i].ItemId != itemId || pack[i].Count >= max)
                    {
                        continue;
                    }
                    var stack = pack[i];
                    ushort take = Math.Min((ushort)(max - stack.Count), (ushort)(count - placed));
                    stack.Count += take;
                    pack[i] = stack;
                    placed += take;
                }
            }

            var (fw, fh) = Footprint(shapes(itemId));
            while (placed < count)
            {
                if (!FindSpot(pack, shapes, fw, fh, out byte x, out byte y))
                {
                    break;
                }
                ushort take = Math.Min((ushort)(count - placed), max);
                pack.Add(new PackStack { X = x, Y = y, ItemId = itemId, Count = take });
                placed += take;
            }
            return placed;
        }

        /// <summary>Takes everything that fits from a bag. Mirrors <c>TakeAllFromBag</c>.</summary>
        /// <param name="left">Filled with what stays in the bag.</param>
        public static void TakeAll(IReadOnlyList<BagStack> bag, List<PackStack> pack,
                                   Func<ushort, ItemShape?> shapes, List<BagStack> left)
        {
            left.Clear();
            for (int i = 0; i < bag.Count; i++)
            {
                var entry = bag[i];
                ushort placed = PutIn(pack, shapes, entry.ItemId, entry.Count);
                if (placed < entry.Count)
                {
                    left.Add(new BagStack { ItemId = entry.ItemId, Count = (ushort)(entry.Count - placed) });
                }
            }
        }
    }
}
