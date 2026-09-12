using System.Collections.Generic;
using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// The item assets a running client can see, so it can draw what a slot holds.
    /// </summary>
    /// <remarks>
    /// The server replicates an <c>ItemDef</c> row per item — name, kind, stack
    /// size, tier — but not a sprite, because art is not data the simulation has
    /// any use for and shipping it would make an icon change a publish.
    ///
    /// So an inventory slot arrives as an id and a count, and this turns that id
    /// into the asset that knows what it looks like. The same split the enemy and
    /// loot-bag catalogues use.
    ///
    /// Keyed by <see cref="EquipmentItem.CatalogueId"/>, not the authored id: that
    /// is the number the server stores and the one a slot refers to. Using the
    /// authored id here would resolve a helmet and a sword to the same entry.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Item Catalogue", fileName = "ItemCatalogue")]
    public sealed class ItemCatalogue : ScriptableObject
    {
        [Tooltip("Every item whose icon should be available at runtime. An item left " +
                 "out still exists and can still be carried; its slot just draws empty.")]
        public List<EquipmentItem> Items = new();

        private Dictionary<ushort, EquipmentItem>? _byId;

        /// <summary>The asset for a catalogue id, or null if it is not listed.</summary>
        public EquipmentItem? For(ushort catalogueId)
        {
            _byId ??= Build();
            return _byId.TryGetValue(catalogueId, out var item) ? item : null;
        }

        public void Invalidate() => _byId = null;

        private void OnValidate() => Invalidate();

        private Dictionary<ushort, EquipmentItem> Build()
        {
            var map = new Dictionary<ushort, EquipmentItem>();
            foreach (var item in Items)
            {
                if (item == null)
                {
                    continue;
                }
                if (!map.TryAdd(item.CatalogueId, item))
                {
                    Debug.LogError($"\"{name}\" lists catalogue id {item.CatalogueId} more "
                                 + $"than once ({item.name}). Only the first is used.", this);
                }
            }
            return map;
        }
    }
}
