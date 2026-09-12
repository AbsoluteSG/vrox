using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>One possible drop from an enemy.</summary>
    /// <remarks>
    /// Rolled independently of every other entry on the same enemy, so the
    /// chances do not have to add up to anything and adding a new entry cannot
    /// quietly make the existing ones rarer. That is the trade against a weighted
    /// table: this cannot express "exactly one of these five", but it can be
    /// edited one line at a time without recomputing the others.
    ///
    /// Everything an enemy rolls in one death goes into a single bag.
    /// </remarks>
    [System.Serializable]
    public sealed class LootRoll
    {
        [Tooltip("What drops. Any equipment kind — the server catalogue holds all " +
                 "four, so this is not limited to weapons.")]
        public EquipmentItem? Item;

        [Tooltip("Chance this entry drops at all, rolled on its own.")]
        [Range(0f, 100f)]
        public float ChancePercent = 25f;

        [Tooltip("How many drop when it hits. Capped by the item's own stack size.")]
        [Range(1, 99)]
        public ushort Count = 1;
    }
}
