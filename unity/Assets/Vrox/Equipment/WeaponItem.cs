using System.Collections.Generic;
using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>A weapon: how fast it fires, how hard it hits, and the shape of a volley.</summary>
    [CreateAssetMenu(menuName = "Vrox/Weapon", fileName = "Weapon")]
    public sealed class WeaponItem : EquipmentItem
    {
        public override EquipmentKind Kind => EquipmentKind.Weapon;

        [Header("Rate")]
        [Tooltip("Milliseconds between volleys. Enforced by the server, so a client " +
                 "cannot fire faster by asking more often.")]
        [Range(50, 3000)]
        public ushort FireRateMs = 300;

        [Header("Damage")]
        [Tooltip("What kind of damage this deals. Tallied separately, so a fight " +
                 "report can show where a player's damage actually came from.")]
        public DamageElement Element = DamageElement.Physical;

        [Tooltip("Rolled per projectile, inclusive of both ends.")]
        public ushort DamageMin = 8;

        public ushort DamageMax = 12;

        [Header("Projectiles")]
        [Tooltip("Tiles per second.")]
        [Range(1f, 40f)]
        public float ProjectileSpeed = 14f;

        [Tooltip("How long a projectile lives before it is removed, in milliseconds.")]
        [Range(100, 5000)]
        public ushort ProjectileLifetimeMs = 1200;

        [Range(0.05f, 1.5f)]
        public float ProjectileSize = 0.3f;

        [Header("On Hit")]
        [Tooltip("A debuff applied to whatever it hits. Refreshes rather than stacks, " +
                 "so repeated hits extend the effect without ever chaining it forever.")]
        public DebuffKind Debuff = DebuffKind.None;

        [Tooltip("How long the debuff lasts, in seconds.")]
        [Range(0f, 30f)]
        public float DebuffSeconds = 1.5f;

        [Header("Gun")]
        [Tooltip("How far a hitscan ray reaches, in tiles. 0 makes this a projectile " +
                 "weapon instead, which is how every enemy weapon works.")]
        [Range(0f, 60f)]
        public float Range;

        [Tooltip("Targets a ray passes through after the first.")]
        [Range(0, 8)]
        public byte Pierce;

        [Tooltip("Damage at maximum range as a percentage of the muzzle. 100 is no " +
                 "falloff. Low values are what make a shotgun a close-range weapon " +
                 "rather than a slow rifle.")]
        [Range(0, 100)]
        public ushort FalloffPercent = 100;

        [Tooltip("On: Damage is one trigger pull, split across the rays, so a shotgun " +
                 "shell means what it says. Off: every ray deals full damage.")]
        public bool SplitDamage = true;

        [Header("Handling")]
        [Tooltip("Shots before reloading. 0 for a weapon that never reloads, which is " +
                 "how everything behaved before magazines existed.")]
        [Range(0, 200)]
        public ushort Magazine;

        [Tooltip("Reload time in milliseconds. With Reload Per Shell this is the time " +
                 "for one shell, not the whole magazine.")]
        [Range(0, 10000)]
        public ushort ReloadMs = 1200;

        [Tooltip("Load one shell at a time, and let the player break off and fire. " +
                 "This is what makes a shotgun a shotgun.")]
        public bool ReloadPerShell;

        [Tooltip("How far each shot shoves the shooter backwards, in tiles. Resolved " +
                 "through the same wall collision as walking.")]
        [Range(0f, 3f)]
        public float Kickback;

        [Tooltip("Walk speed while holding this, as a percentage of the character's " +
                 "own speed. 100 is no effect. Below 100 is weight you feel every " +
                 "second the gun is out, not only when you fire it; above 100 is what " +
                 "makes a sidearm worth switching to.")]
        [Range(25, 200)]
        public ushort MoveSpeedPercent = 100;

        [Header("Pattern")]
        [Tooltip("The shape of one volley. Pick a concrete pattern.")]
        [SerializeReference]
        public ProjectilePattern Pattern = new SingleShot();

        [Header("Motion")]
        [Tooltip("Rotates the whole volley over time, in degrees per second. " +
                 "Ring plus spin is the classic spiral. Zero holds still.")]
        [Range(-720f, 720f)]
        public float SpinDegreesPerSec;

        [Tooltip("How far each projectile weaves side to side, in tiles. " +
                 "Zero is a straight line. Required for Helix to look like one.")]
        [Range(0f, 5f)]
        public float WaveAmplitude;

        [Tooltip("Weaves per second.")]
        [Range(0f, 20f)]
        public float WaveFrequency = 2f;

        [Header("Bullet Mix")]
        [Tooltip("Bullet kinds to spread across the volley's projectiles. Leave empty " +
                 "and every projectile uses the Damage / On Hit / Projectiles fields " +
                 "above. Add entries and those fields stop being used.")]
        public List<BulletVariant> Mix = new();

        [Tooltip("How the variants are dealt out. Cycle alternates around the volley; " +
                 "Block gives contiguous runs.")]
        public SlotAssignment Assignment = SlotAssignment.Cycle;

        [Header("Art")]
        [Tooltip("Bullet art for this weapon's shots, as an index into the Bullet " +
                 "Sprite Catalogue. 0 draws the plain soft blob every weapon used " +
                 "before art existed.")]
        [Min(0)]
        public int BulletSpriteId;

        /// <summary>Projectiles per volley, or 1 when no pattern is set.</summary>
        public byte Shots => Pattern?.Count ?? 1;

        /// <summary>Directions a Cluster divides between; 0 for every other pattern.</summary>
        public byte PatternGroups => Pattern?.Groups ?? 0;

        /// <summary>True when the mix, not the flat fields, describes this weapon's bullets.</summary>
        public bool HasMix => Mix is { Count: > 0 };

        /// <summary>Rough sustained damage per second, for comparing weapons in the inspector.</summary>
        /// <remarks>
        /// Summed over the actual slots when there is a mix, rather than averaged
        /// over the variants. Those differ whenever the slot count is not a
        /// multiple of the variant count — six shots across four variants fires
        /// the first two twice — and a figure that quietly assumed an even split
        /// would be furthest from the truth exactly when a weapon is most
        /// complicated, which is when someone is most likely to trust it.
        /// </remarks>
        public float DamagePerSecond
        {
            get
            {
                float perVolley = 0f;
                if (HasMix)
                {
                    for (int slot = 0; slot < Shots; slot++)
                    {
                        var v = VariantAt(slot);
                        perVolley += (v.DamageMin + v.DamageMax) * 0.5f;
                    }
                }
                else
                {
                    perVolley = (DamageMin + DamageMax) * 0.5f * Shots;
                }
                return FireRateMs > 0 ? perVolley / (FireRateMs / 1000f) : 0f;
            }
        }

        /// <summary>
        /// Which variant fills a slot. Mirrors the server's <c>ProfileFor</c>.
        /// </summary>
        /// <remarks>
        /// Goes through <see cref="VolleyMath.VariantIndex"/>, the one client copy
        /// of that maths, and with its rule: if the two disagree, the server is
        /// right and this inspector is lying.
        /// </remarks>
        public BulletVariant VariantAt(int slot) =>
            Mix[VolleyMath.VariantIndex(Assignment, slot, Shots, Mix.Count, PatternGroups)];

        /// <summary>True when the pattern needs a wave but the weapon has none.</summary>
        public bool HelixWithoutWave => Pattern is HelixShot && WaveAmplitude <= 0f;

        private void OnValidate()
        {
            // Swapping these silently produces a weapon that always rolls its
            // minimum, which looks like a balance problem rather than a typo.
            if (DamageMax < DamageMin)
            {
                DamageMax = DamageMin;
            }
            Pattern ??= new SingleShot();

            // A helix with no wave is several projectiles on identical straight
            // lines, drawn on top of each other — it looks like one shot and
            // reads as a broken pattern rather than a missing setting.
            if (HelixWithoutWave)
            {
                Debug.LogWarning($"\"{name}\" uses a Helix pattern but Wave Amplitude is 0, "
                               + "so every strand travels the same straight line.", this);
            }

            if (!HasMix)
            {
                return;
            }

            foreach (var variant in Mix)
            {
                if (variant != null && variant.DamageMax < variant.DamageMin)
                {
                    variant.DamageMax = variant.DamageMin;
                }
            }

            // That the flat Damage / On Hit / Projectiles fields stop being used
            // is said in the Mix tooltip, not logged. It is true of every
            // correctly configured mixed weapon, and a warning that fires on
            // every edit of a working asset only teaches you to ignore warnings.
            // The two below fire on actual mistakes.
            if (Mix.Count == 1)
            {
                Debug.LogWarning($"\"{name}\" has a one-entry mix, which fires exactly "
                               + "what the flat fields would. Clear the mix or add a "
                               + "second variant.", this);
            }

            if (Mix.Count > Shots)
            {
                Debug.LogWarning($"\"{name}\" has {Mix.Count} variants but only {Shots} "
                               + "projectile(s), so some never fire.", this);
            }
        }
    }
}
