using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>What ends a phase. A closed set, like every other authored rule.</summary>
    public enum TransitionKind : byte
    {
        /// <summary>Never leaves. Correct for the last phase.</summary>
        Never = 0,
        HealthBelowPercent = 1,
        AfterSeconds = 2,
    }

    /// <summary>When an enemy moves on to its next phase.</summary>
    [System.Serializable]
    public abstract class PhaseTransition
    {
        public abstract TransitionKind Kind { get; }
        public abstract float Value { get; }
    }

    /// <summary>Stays in this phase for the rest of the fight.</summary>
    [System.Serializable]
    public sealed class NeverTransition : PhaseTransition
    {
        public override TransitionKind Kind => TransitionKind.Never;
        public override float Value => 0f;
    }

    /// <summary>Leaves once health drops to or below a share of maximum.</summary>
    [System.Serializable]
    public sealed class HealthBelowTransition : PhaseTransition
    {
        [Range(1f, 99f)]
        public float Percent = 50f;

        public override TransitionKind Kind => TransitionKind.HealthBelowPercent;
        public override float Value => Percent;
    }

    /// <summary>Leaves after a fixed time in this phase.</summary>
    [System.Serializable]
    public sealed class TimedTransition : PhaseTransition
    {
        [Range(0.5f, 300f)]
        public float Seconds = 10f;

        public override TransitionKind Kind => TransitionKind.AfterSeconds;
        public override float Value => Seconds;
    }

    /// <summary>
    /// One stage of a fight: how it moves, what it fires, and when it stops.
    /// </summary>
    /// <remarks>
    /// A phase reuses the same movement behaviours and weapons ordinary enemies
    /// use. A boss is not a different kind of thing — it is an enemy with a list
    /// attached, which is why a grunt and a boss run through identical code.
    /// </remarks>
    [System.Serializable]
    public sealed class BossPhase
    {
        [Tooltip("Names the phase in the inspector. Not sent to the server.")]
        public string Name = "Phase";

        [Tooltip("How it moves during this phase.")]
        [SerializeReference]
        public MovementBehaviour Movement = new StaticMovement();

        [Tooltip("What it fires during this phase. Empty means it holds fire.")]
        public WeaponItem? Weapon;

        [Tooltip("How close a player must be before it opens fire, in tiles.")]
        [Range(0f, 60f)]
        public float AttackRange = 15f;

        [Header("State")]
        [Tooltip("Takes no damage at all. Hits still flash and show a 0, because a " +
                 "player needs to see their shots landing and doing nothing — that is " +
                 "the whole signal a transition phase sends.")]
        public bool Invulnerable;

        [Tooltip("Shots pass straight through, so it can retreat behind minions " +
                 "without soaking every bullet aimed at them.")]
        public bool Untargetable;

        [Tooltip("Damage taken, as a percentage. 100 is normal, 50 is armoured.")]
        [Range(0f, 300f)]
        public float DamageTakenPercent = 100f;

        [Tooltip("Movement speed during this phase. 0 keeps the archetype's.")]
        [Range(0f, 20f)]
        public float Speed;

        [Tooltip("What ends this phase. The last phase should be Never.")]
        [SerializeReference]
        public PhaseTransition Transition = new NeverTransition();

        /// <summary>Packed as the server stores it: bit 0 invulnerable, bit 1 untargetable.</summary>
        public byte Flags => (byte)((Invulnerable ? 1 : 0) | (Untargetable ? 2 : 0));
    }
}
