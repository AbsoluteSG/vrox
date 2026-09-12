using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>An enemy archetype: stats, how it moves, and what it fires.</summary>
    [CreateAssetMenu(menuName = "Vrox/Enemy", fileName = "Enemy")]
    public sealed class EnemyItem : ScriptableObject
    {
        [Tooltip("Stable id. This is what the server stores — renaming the asset is " +
                 "safe, changing this is not.")]
        public ushort Id = 1;

        public string DisplayName = "Unnamed";

        [Tooltip("The colour this enemy is drawn in, darkened as it loses health. Pushed " +
                 "to the server so every player sees the same enemy, rather than each " +
                 "client choosing for itself.")]
        public Color Tint = new Color(0.85f, 0.35f, 0.35f);

        [Tooltip("Drawn instead of a plain square. Leave empty and the enemy is a " +
                 "coloured box, which is exactly what it is today. Tint still applies " +
                 "on top, so health darkening and hit flashes work either way.")]
        public Sprite? Sprite;

        [Header("Body")]
        [Range(1, 10000)]
        public ushort MaxHp = 50;

        [Tooltip("Collision radius in tiles. Also its drawn size, so what you see is what you hit.")]
        [Range(0.1f, 5f)]
        public float Radius = 0.5f;

        [Tooltip("Tiles per second.")]
        [Range(0f, 20f)]
        public float Speed = 3f;

        [Header("Movement")]
        [Tooltip("How it decides where to go. Pick a concrete behaviour.")]
        [SerializeReference]
        public MovementBehaviour Movement = new WanderMovement();

        [Header("Attack")]
        [Tooltip("A weapon from the same catalogue players use, so enemies fire through " +
                 "identical patterns. Leave empty for a mob that never shoots.")]
        public WeaponItem? Weapon;

        [Tooltip("How close a player must get before it opens fire, in tiles. Separate " +
                 "from the movement behaviour's notice range — a turret never moves but " +
                 "still needs to know when to shoot.")]
        [Range(0f, 60f)]
        public float AttackRange = 10f;

        [Tooltip("Damage per second while overlapping a player. Not implemented yet.")]
        [Range(0, 500)]
        public ushort TouchDamage;

        [Header("Presentation")]
        [Tooltip("Gets a health bar on screen. Grunts should not.")]
        public bool IsBoss;

        [Tooltip("A second resource shown as its own bar. 0 hides it. Nothing spends " +
                 "or regenerates energy yet, so it sits at full.")]
        [Range(0, 10000)]
        public ushort MaxEnergy;

        [Header("Phases")]
        [Tooltip("Leave empty for an ordinary enemy: the fields above are used as they " +
                 "are. Add phases and they override movement, weapon and attack range " +
                 "in order, each running until its transition fires.")]
        public System.Collections.Generic.List<BossPhase> Phases = new();

        [Header("Resistance")]
        [Tooltip("Damage taken per element. 100 is neutral, 50 resistant, 200 weak. " +
                 "Elements left out are neutral, so an empty list is an enemy that " +
                 "does not care what hits it.")]
        public System.Collections.Generic.List<ElementResist> Resist = new();

        [Header("Loot")]
        [Tooltip("Weighted against each other and against No Drop Weight. One pool is " +
                 "chosen per kill, then its items are rolled to fill the bag.")]
        public System.Collections.Generic.List<LootPoolWeight> LootPools = new();

        [Tooltip("Weight of dropping nothing, against the pools above. In the same " +
                 "roll on purpose: a separate drop chance would mean working out how " +
                 "often a bag appears by multiplying two numbers edited in different " +
                 "places.")]
        [Min(0)]
        public uint NoDropWeight = 9;

        /// <summary>True when it carries a weapon it can never use.</summary>
        public bool ArmedButBlind => Weapon != null && AttackRange <= 0f;

        private void OnValidate()
        {
            Movement ??= new WanderMovement();

            // An armed mob whose behaviour never looks for a player will never
            // find one to shoot at, and reads as a broken weapon rather than a
            // mismatched pair.
            if (ArmedButBlind && Phases.Count == 0)
            {
                Debug.LogWarning($"\"{name}\" has a weapon but an Attack Range of 0, "
                               + "so it will never fire.", this);
            }

            for (int i = 0; i < Phases.Count; i++)
            {
                Phases[i].Movement ??= new StaticMovement();
                Phases[i].Transition ??= new NeverTransition();

                // A non-final phase that never ends strands the fight there, and
                // every phase after it is unreachable — which looks like the
                // later phases being broken rather than this one not leaving.
                // An invulnerable phase cannot end on health, because its health
                // never changes. The fight would sit there forever, which reads
                // as the boss being bugged rather than the transition being
                // impossible.
                if (Phases[i].Invulnerable
                    && Phases[i].Transition.Kind == TransitionKind.HealthBelowPercent)
                {
                    Debug.LogError($"\"{name}\" phase {i} (\"{Phases[i].Name}\") is invulnerable "
                                 + "but ends on health, which can never happen. Use a timed "
                                 + "transition.", this);
                }

                if (i < Phases.Count - 1 && Phases[i].Transition.Kind == TransitionKind.Never)
                {
                    Debug.LogWarning($"\"{name}\" phase {i} (\"{Phases[i].Name}\") never ends, "
                                   + $"so the {Phases.Count - i - 1} phase(s) after it are "
                                   + "unreachable.", this);
                }
            }
        }
    }
}
