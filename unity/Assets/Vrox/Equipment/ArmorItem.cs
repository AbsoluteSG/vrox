using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>Worn protection: flat damage reduction and a health bonus.</summary>
    [CreateAssetMenu(menuName = "Vrox/Armor", fileName = "Armor")]
    public sealed class ArmorItem : EquipmentItem
    {
        public override EquipmentKind Kind => EquipmentKind.Armor;

        [Tooltip("Subtracted from each incoming hit, before any minimum is applied.")]
        [Range(0, 200)]
        public ushort Defense = 5;

        [Tooltip("Added to maximum health while worn.")]
        [Range(0, 500)]
        public ushort HealthBonus;

        [Tooltip("Multiplies movement speed. Heavy armour below 1, light above.")]
        [Range(0.5f, 1.5f)]
        public float SpeedMultiplier = 1f;
    }
}
