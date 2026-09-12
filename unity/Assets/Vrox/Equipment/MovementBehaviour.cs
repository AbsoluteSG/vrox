using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// How a mob decides where to move. Must stay in step with the server's copy.
    /// </summary>
    /// <remarks>
    /// There is no separate notion of a movement *pattern*. A pattern would be a
    /// behaviour that ignores the world, which is not a different kind of thing —
    /// only a behaviour that leaves an input unread. Two systems would force every
    /// archetype to pick a lane, and the interesting cases fit neither: orbiting a
    /// player is a pattern's shape driven by a behaviour's target.
    ///
    /// What genuinely differs between them is **what happens with no target in
    /// range**, so each behaviour answers that for itself rather than sharing a
    /// fallback.
    ///
    /// A closed set, like projectile patterns: the server decides where mobs
    /// actually are, so it must evaluate the same behaviour. Adding one is a
    /// change in two places.
    /// </remarks>
    public enum BehaviourKind : byte
    {
        /// <summary>Never moves. Ignores everything.</summary>
        Static = 0,

        /// <summary>Drifts near its spawn point. Never looks for a player.</summary>
        Wander = 1,

        /// <summary>Straight at the nearest player in range; wanders with nobody to chase.</summary>
        Chase = 2,

        /// <summary>Circles the player at a preferred range, spiralling onto it.</summary>
        Orbit = 3,

        /// <summary>Closes to a preferred range and backs off inside it.</summary>
        KeepDistance = 4,
    }

    /// <summary>
    /// One mob's movement. Subclasses exist for authoring, not for behaviour.
    /// </summary>
    /// <remarks>
    /// Each concrete class is a friendlier way to fill in the two or three fields
    /// that travel to the server, and to leave out the ones that would mean
    /// nothing for it — a wanderer has no preferred range, a static mob has no
    /// aggro range.
    /// </remarks>
    [System.Serializable]
    public abstract class MovementBehaviour
    {
        public abstract BehaviourKind Kind { get; }

        /// <summary>How far it notices a player, in tiles. 0 for behaviours that never look.</summary>
        public virtual float AggroRange => 0f;

        /// <summary>Range it tries to hold; doubles as the wander leash.</summary>
        public virtual float PreferredRange => 0f;

        /// <summary>What an orbit circles. Meaningless for everything else.</summary>
        public virtual OrbitPivot Pivot => OrbitPivot.Player;
    }

    /// <summary>What an orbiting mob circles.</summary>
    public enum OrbitPivot : byte
    {
        /// <summary>The nearest player. A mob that harries whoever it finds.</summary>
        Player = 0,

        /// <summary>
        /// The point it spawned at. A boss holding its arena.
        /// </summary>
        /// <remarks>
        /// Needs no player at all, so it keeps circling whether or not anyone is
        /// there — place the enemy at the centre of the room it should hold and
        /// the spawn point is the pivot.
        /// </remarks>
        SpawnPoint = 1,
    }

    /// <summary>Stands still.</summary>
    [System.Serializable]
    public sealed class StaticMovement : MovementBehaviour
    {
        public override BehaviourKind Kind => BehaviourKind.Static;
    }

    /// <summary>Drifts around its spawn point, ignoring players.</summary>
    [System.Serializable]
    public sealed class WanderMovement : MovementBehaviour
    {
        [Tooltip("How far it may stray from where it spawned before being pulled back.")]
        [Range(1f, 20f)]
        public float Leash = 4f;

        public override BehaviourKind Kind => BehaviourKind.Wander;
        public override float PreferredRange => Leash;
    }

    /// <summary>Runs straight at the nearest player. Wanders when there is none.</summary>
    [System.Serializable]
    public sealed class ChaseMovement : MovementBehaviour
    {
        [Range(1f, 60f)]
        public float NoticeRange = 12f;

        public override BehaviourKind Kind => BehaviourKind.Chase;
        public override float AggroRange => NoticeRange;
    }

    /// <summary>Circles a pivot at a fixed distance.</summary>
    [System.Serializable]
    public sealed class OrbitMovement : MovementBehaviour
    {
        [Tooltip("What it circles. Spawn Point is what a boss holding an arena wants — " +
                 "put the enemy at the centre of the room and it orbits that.")]
        public OrbitPivot Circles = OrbitPivot.Player;

        [Tooltip("How far it notices a player. Ignored when circling its spawn point, " +
                 "which needs no target.")]
        [Range(1f, 60f)]
        public float NoticeRange = 15f;

        [Tooltip("Radius it settles onto. It spirals in or out to reach this.")]
        [Range(1f, 30f)]
        public float Radius = 6f;

        public override BehaviourKind Kind => BehaviourKind.Orbit;
        public override float AggroRange => NoticeRange;
        public override float PreferredRange => Radius;
        public override OrbitPivot Pivot => Circles;
    }

    /// <summary>Holds a range: approaches from outside it, retreats from inside.</summary>
    [System.Serializable]
    public sealed class KeepDistanceMovement : MovementBehaviour
    {
        [Range(1f, 60f)]
        public float NoticeRange = 18f;

        [Tooltip("The range it wants. There is a small dead zone so it does not jitter.")]
        [Range(1f, 30f)]
        public float Range = 8f;

        public override BehaviourKind Kind => BehaviourKind.KeepDistance;
        public override float AggroRange => NoticeRange;
        public override float PreferredRange => Range;
    }
}
