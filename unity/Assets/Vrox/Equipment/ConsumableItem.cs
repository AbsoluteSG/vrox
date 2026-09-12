using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>What using a consumable does.</summary>
    public enum ConsumableEffect : byte
    {
        Heal = 0,
        /// <summary>A temporary multiplier to movement speed.</summary>
        Haste = 1,
        /// <summary>A temporary multiplier to damage.</summary>
        Might = 2,
    }

    /// <summary>A potion or similar: used up, with an immediate or timed effect.</summary>
    [CreateAssetMenu(menuName = "Vrox/Consumable", fileName = "Consumable")]
    public sealed class ConsumableItem : EquipmentItem
    {
        public override EquipmentKind Kind => EquipmentKind.Consumable;

        public ConsumableEffect Effect = ConsumableEffect.Heal;

        [Tooltip("Health restored for Heal; the multiplier's strength otherwise.")]
        [Range(0f, 500f)]
        public float Magnitude = 40f;

        [Tooltip("How long the effect lasts, in milliseconds. Zero means instant.")]
        [Range(0, 60000)]
        public int DurationMs;

        [Tooltip("Milliseconds before another may be used. Stops a stack being drunk in one frame.")]
        [Range(0, 60000)]
        public int CooldownMs = 1000;

        [Tooltip("How many fit in one inventory slot.")]
        [Range(1, 99)]
        public int StackSize = 8;

        public override int MaxStack => StackSize;
    }
}
