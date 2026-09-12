using System.Collections.Generic;
using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// Which bag a drop arrives in.
    /// </summary>
    /// <remarks>
    /// A byte on the wire. The server carries the number and never the sprite —
    /// what a bag looks like is authored in <c>LootBagCatalogue</c>, so changing
    /// a texture is not a database publish.
    ///
    /// <b>The numbers are the wire format.</b> Add to the end; reordering or
    /// renumbering these silently repaints every bag already on the ground and
    /// every rule later written against them.
    /// </remarks>
    public enum LootBagKind : byte
    {
        Common = 0,
        Uncommon = 1,
        Rare = 2,
        Epic = 3,
        Legendary = 4,
        Boss = 5,
    }

    /// <summary>
    /// A named set of possible items, and the bag they arrive in.
    /// </summary>
    /// <remarks>
    /// An asset rather than a list on the enemy, so "the common bag" is one thing
    /// shared by everything that drops it. A per-enemy copy would drift the first
    /// time one was edited, and the drift would be invisible until someone
    /// compared two enemies by hand.
    ///
    /// Entries inside a pool are rolled independently of each other, so their
    /// chances need not add up and adding one cannot quietly make the rest rarer.
    /// The weighted choice is one level up, between pools — see
    /// <c>EnemyItem.LootPools</c>.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Loot Pool", fileName = "LootPool")]
    public sealed class LootPoolItem : ScriptableObject
    {
        [Tooltip("Stable id. This is what the server stores — renaming the asset is " +
                 "safe, changing this is not. 0 is reserved for \"drops nothing\".")]
        public ushort Id = 1;

        [Tooltip("Which bag a drop from this pool arrives in.")]
        public LootBagKind Bag = LootBagKind.Common;

        [Tooltip("Rolled independently when this pool is chosen. All of them can hit; " +
                 "so can none, in which case nothing drops at all.")]
        public List<LootRoll> Items = new();

        private void OnValidate()
        {
            if (Id == 0)
            {
                Debug.LogWarning($"\"{name}\" has id 0, which is reserved for \"drops "
                               + "nothing\". It will be refused by the push.", this);
            }

            // A pool that can produce nothing is legal but rarely intended: the
            // enemy already has a no-drop weight for that, and expressing it twice
            // makes the real odds hard to read off either place.
            float best = 0f;
            foreach (var roll in Items)
            {
                if (roll != null && roll.ChancePercent > best)
                {
                    best = roll.ChancePercent;
                }
            }
            if (Items.Count > 0 && best < 100f)
            {
                Debug.LogWarning($"\"{name}\" has no entry at 100%, so it can be chosen "
                               + "and still drop nothing. Use the enemy's No Drop Weight "
                               + "for that instead, or the real odds live in two places.",
                                 this);
            }
        }
    }
}
