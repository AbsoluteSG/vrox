using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>How much damage of one element an enemy takes.</summary>
    /// <remarks>
    /// A percentage rather than a multiplier so the intent reads without doing
    /// arithmetic: 50 resists, 200 is a weakness, 100 is neutral and the same as
    /// leaving the element out entirely.
    ///
    /// This is what makes an element mean something. Until it existed, a bullet's
    /// element fed the stats tables and changed no damage at all — an ice gun and
    /// a fire gun were the same gun with different entries on a chart.
    /// </remarks>
    [System.Serializable]
    public sealed class ElementResist
    {
        public DamageElement Element = DamageElement.Physical;

        [Tooltip("100 neutral, below resists, above is a weakness.")]
        [Range(0, 500)]
        public ushort Percent = 100;
    }
}
