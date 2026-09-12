using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>One pool an enemy can drop, and how likely it is against the others.</summary>
    /// <remarks>
    /// A weight, not a percentage. Weights are read against each other, so they
    /// cannot add up to more than certainty and adding a pool does not require
    /// re-balancing the ones already there — the same reason the enemy's no-drop
    /// chance is a weight in the same roll rather than a separate percentage.
    /// </remarks>
    [System.Serializable]
    public sealed class LootPoolWeight
    {
        public LootPoolItem? Pool;

        [Tooltip("Relative likelihood against the other pools and No Drop Weight. " +
                 "0 disables this entry.")]
        [Min(0)]
        public uint Weight = 1;
    }
}
