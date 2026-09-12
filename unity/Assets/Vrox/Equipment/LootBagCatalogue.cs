using System.Collections.Generic;
using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// What each kind of loot bag looks like.
    /// </summary>
    /// <remarks>
    /// The one place a bag kind becomes a picture and a sound. The server sends a
    /// byte and nothing else, so this never has to agree with a database row — a
    /// bag's art can change without a publish, and a missing sprite cannot
    /// corrupt anything.
    ///
    /// A list rather than a dictionary because Unity does not serialise
    /// dictionaries; the lookup is built once on first use. Duplicate entries are
    /// reported rather than silently letting the last one win.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Loot Bag Catalogue", fileName = "LootBagCatalogue")]
    public sealed class LootBagCatalogue : ScriptableObject
    {
        [System.Serializable]
        public sealed class Entry
        {
            public LootBagKind Kind = LootBagKind.Common;
            public Sprite? Sprite;

            [Tooltip("Multiplies the renderer's base size, for bags that should read " +
                     "as bigger without a bigger texture.")]
            [Range(0.25f, 4f)]
            public float Scale = 1f;

            [Tooltip("Tints the sprite. White leaves the art as authored.")]
            public Color Tint = Color.white;

            [Tooltip("Played when a bag of this kind drops. Leave empty for a tier that " +
                     "should land in silence — the common ones, most likely, so the " +
                     "rare ones mean something.\n\n" +
                     "This is the drop, not the pickup: it fires wherever the bag lands, " +
                     "including off screen, because a bag you cannot see is exactly the " +
                     "one worth being told about.")]
            public AudioClip? Drop;
        }

        public List<Entry> Bags = new();

        private Dictionary<LootBagKind, Entry>? _byKind;

        /// <summary>The entry for a bag kind, or null if none is authored.</summary>
        /// <remarks>
        /// Null rather than a stand-in entry. A caller that draws nothing for an
        /// unauthored kind is visibly missing art; one handed a default would draw
        /// a plausible bag of the wrong kind, and the mistake would survive all
        /// the way to a player picking up the wrong thing.
        /// </remarks>
        public Entry? For(byte kind)
        {
            _byKind ??= Build();
            return _byKind.TryGetValue((LootBagKind)kind, out var entry) ? entry : null;
        }

        /// <summary>Drops the cached lookup, so edits show up without a domain reload.</summary>
        public void Invalidate() => _byKind = null;

        private void OnValidate() => Invalidate();

        private Dictionary<LootBagKind, Entry> Build()
        {
            var map = new Dictionary<LootBagKind, Entry>();
            foreach (var entry in Bags)
            {
                if (entry == null)
                {
                    continue;
                }
                if (!map.TryAdd(entry.Kind, entry))
                {
                    Debug.LogError($"\"{name}\" lists {entry.Kind} more than once. "
                                 + "Only the first is used.", this);
                }
            }
            return map;
        }
    }
}
