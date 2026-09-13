using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Every enemy, pool and item asset, and the rows Push All would turn them into.
    /// </summary>
    /// <remarks>
    /// The simulator works from the rows the server would hold, not the assets as
    /// they read, because the push reshapes them: entries without an item are
    /// skipped, counts are capped to the item's stack, zero weights are left out,
    /// and a pool id used twice ends up as whichever pool was pushed last. Odds
    /// computed from the raw assets would be wrong in exactly those cases.
    ///
    /// Asset lists are reloaded when the project changes (an asset is created,
    /// deleted or renamed). Field values are read live on every use, so edits show
    /// immediately.
    ///
    /// Editor-only and read only by the loot simulator. The game never builds these
    /// rows; the server's tables are the ones it plays from.
    /// </remarks>
    internal sealed class LootAssets
    {
        public readonly List<EnemyItem> Enemies = new();

        /// <summary>In push order, which is what decides which of two same-id pools the server keeps.</summary>
        public readonly List<LootPoolItem> Pools = new();

        public readonly List<EquipmentItem> Items = new();

        private readonly Dictionary<ushort, LootPoolItem> _poolById = new();
        private readonly Dictionary<ushort, EquipmentItem> _itemById = new();

        private static LootAssets? _shared;
        private static bool _stale = true;

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.projectChanged -= Reload;
            EditorApplication.projectChanged += Reload;
        }

        public static void Reload() => _stale = true;

        /// <summary>The library, reloaded if assets were added, removed or renamed, and re-indexed.</summary>
        public static LootAssets Shared
        {
            get
            {
                if (_shared == null || _stale)
                {
                    _shared = new LootAssets();
                    _shared.Load();
                    _stale = false;
                }
                _shared.Index();
                return _shared;
            }
        }

        private void Load()
        {
            // Same query and order as PushEquipment.LoadAll.
            Enemies.AddRange(LoadAll<EnemyItem>().OrderBy(e => e.IsBoss).ThenBy(e => e.name));
            Pools.AddRange(LoadAll<LootPoolItem>());
            Items.AddRange(LoadAll<EquipmentItem>());
        }

        /// <summary>Rebuilds the id lookups from live field values.</summary>
        private void Index()
        {
            Enemies.RemoveAll(e => e == null);
            Pools.RemoveAll(p => p == null);
            Items.RemoveAll(i => i == null);

            // Pushed in order, each UpsertLootPool replacing the last: the later pool wins.
            _poolById.Clear();
            foreach (var pool in Pools)
            {
                if (pool.Id != 0)
                {
                    _poolById[pool.Id] = pool;
                }
            }

            // A duplicate catalogue id makes the push refuse to run at all; the
            // first is used here so the simulator still has something to show.
            _itemById.Clear();
            foreach (var item in Items)
            {
                _itemById.TryAdd(item.CatalogueId, item);
            }
        }

        /// <summary>The pool the server holds under an id, or null.</summary>
        public LootPoolItem? ServerPool(ushort id) => id != 0 && _poolById.TryGetValue(id, out var p) ? p : null;

        public EquipmentItem? Item(ushort catalogueId) => _itemById.TryGetValue(catalogueId, out var i) ? i : null;

        public bool ItemExists(ushort catalogueId) => _itemById.ContainsKey(catalogueId);

        /// <summary>How an item packs, clamped as <c>UpsertItemDef</c> clamps it.</summary>
        public ItemShape? Shape(ushort catalogueId) => Item(catalogueId) is { } item
            ? new ItemShape
            {
                MaxStack = (ushort)Mathf.Clamp(item.MaxStack, 1, ushort.MaxValue),
                Width = (byte)Mathf.Clamp(item.Width, 1, 10),
                Height = (byte)Mathf.Clamp(item.Height, 1, 6),
            }
            : null;

        /// <summary>An enemy's <c>EnemyLoot</c> rows, as PushEquipment writes them.</summary>
        public static void EnemyRows(EnemyItem e, List<PoolWeightRow> into)
        {
            into.Clear();
            into.Add(new PoolWeightRow { PoolId = 0, Weight = e.NoDropWeight });
            if (e.LootPools == null)
            {
                return;
            }
            for (int i = 0; i < e.LootPools.Count && i < 254; i++)
            {
                var weighted = e.LootPools[i];
                if (weighted?.Pool == null || weighted.Weight == 0)
                {
                    continue;
                }
                into.Add(new PoolWeightRow { PoolId = weighted.Pool.Id, Weight = weighted.Weight });
            }
        }

        /// <summary>A pool's <c>LootEntry</c> rows, as PushEquipment writes them and UpsertLoot clamps them.</summary>
        /// <returns>False for a pool the push refuses (id 0).</returns>
        public static bool PoolRows(LootPoolItem pool, List<LootEntryRow> into)
        {
            into.Clear();
            if (pool.Id == 0)
            {
                return false;
            }
            if (pool.Items == null)
            {
                return true;
            }
            for (int i = 0; i < pool.Items.Count && i < 255; i++)
            {
                var roll = pool.Items[i];
                if (roll?.Item == null)
                {
                    continue;
                }
                ushort count = (ushort)Mathf.Min(roll.Count, roll.Item.MaxStack);
                into.Add(new LootEntryRow
                {
                    ItemId = roll.Item.CatalogueId,
                    ChancePercent = Mathf.Clamp(roll.ChancePercent, 0f, 100f),
                    Count = count < 1 ? (ushort)1 : count,
                });
            }
            return true;
        }

        private static List<T> LoadAll<T>() where T : Object =>
            AssetDatabase.FindAssets($"t:{typeof(T).Name}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<T>)
                .Where(a => a != null)
                .ToList();
    }
}
