using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// A ring or amulet: small modifiers rather than a role of its own.
    /// </summary>
    /// <remarks>
    /// Deliberately additive and multiplicative in separate fields. Mixing the two
    /// into one number makes stacking rules impossible to reason about once there
    /// is more than one source.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Jewellery", fileName = "Jewellery")]
    public sealed class JewelleryItem : EquipmentItem
    {
        public override EquipmentKind Kind => EquipmentKind.Jewellery;

        [Header("Flat bonuses")]
        [Range(0, 500)]
        public ushort HealthBonus;

        [Range(0, 100)]
        public ushort DefenseBonus;

        [Range(0, 100)]
        public ushort DamageBonus;

        [Header("Multipliers")]
        [Tooltip("Scales weapon fire rate. Below 1 is faster.")]
        [Range(0.25f, 2f)]
        public float FireRateMultiplier = 1f;

        [Range(0.5f, 2f)]
        public float SpeedMultiplier = 1f;
    }
}
