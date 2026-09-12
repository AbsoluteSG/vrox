using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// The stats every player starts with.
    /// </summary>
    /// <remarks>
    /// One of these in the project. It is authoring data — the server holds the
    /// row that actually governs movement, damage and rate of fire, and pushing
    /// happens on Play like everything else.
    ///
    /// Every stat here has to be enforced server-side. A client-side speed or
    /// defence would be a number nothing reads, because the server is what moves
    /// you and what decides a hit.
    ///
    /// Changing a value and pressing Play applies it to everyone immediately,
    /// including players already connected.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Player Config", fileName = "PlayerConfig")]
    public sealed class PlayerConfigItem : ScriptableObject
    {
        [Header("Vitality")]
        [Range(1, 10000)]
        public ushort MaxHp = 100;

        [Tooltip("Subtracted from each incoming hit. A hit always does at least 1, so " +
                 "no amount of defence makes a player invulnerable.")]
        [Range(0, 500)]
        public ushort Defense;

        [Tooltip("Health per second. Fractions accumulate, so 0.5 heals a point every " +
                 "two seconds rather than nothing.")]
        [Range(0f, 100f)]
        public float HpRegenPerSec;

        [Header("Movement")]
        [Tooltip("Tiles per second.")]
        [Range(0.5f, 40f)]
        public float Speed = 5f;

        [Header("Offence")]
        [Tooltip("Attack speed. 1 is the weapon's own rate, 2 is twice as fast.")]
        [Range(0.1f, 10f)]
        public float Dexterity = 1f;

        [Tooltip("Chance for a shot to crit, rolled per projectile — so a shotgun's " +
                 "pellets crit independently rather than all together.")]
        [Range(0f, 1f)]
        public float CritChance;

        [Range(1f, 10f)]
        public float CritMultiplier = 1.5f;

        [Tooltip("Weapon every new character starts holding, placed straight into the " +
                 "equipped slot. Leave empty to start unarmed — which means unable to " +
                 "kill anything, and so unable to ever loot a weapon.")]
        public WeaponItem? StartingWeapon;

        /// <summary>Rough sustained damage multiplier from crits, for comparing setups.</summary>
        public float CritDamageMultiplier => 1f + CritChance * (CritMultiplier - 1f);
    }
}
