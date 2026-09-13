using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// The shapes a volley can take. Must stay in step with the server's copy.
    /// </summary>
    /// <remarks>
    /// A closed set, not open-ended code. The server decides what was actually
    /// fired, so it has to evaluate the same pattern — which means a pattern
    /// cannot be arbitrary client behaviour. Adding one is a change in two
    /// places, here and in the module, and that is the price of the server being
    /// the authority on where bullets go.
    /// </remarks>
    public enum PatternKind : byte
    {
        Single = 0,

        /// <summary>Fanned across an arc, centred on the aim.</summary>
        Spread = 1,

        /// <summary>Evenly around a full circle. Combine with Spin for a spiral.</summary>
        Ring = 2,

        /// <summary>Same direction, spawn points spread sideways.</summary>
        Parallel = 3,

        /// <summary>
        /// Same direction, wave phases spread evenly so the shots braid.
        /// </summary>
        /// <remarks>
        /// Needs the weapon's wave amplitude and frequency set, or every shot
        /// travels the same straight line and they overlap into one.
        /// </remarks>
        Helix = 4,

        /// <summary>
        /// Several tight fans, spaced evenly around a circle.
        /// </summary>
        /// <remarks>
        /// The one shape that needs two counts — how many directions, and how
        /// many bullets down each. A Ring of eight is eight bullets 45 degrees
        /// apart; a Cluster of four by two is four pairs 90 degrees apart, and you
        /// dodge it by finding the gap between clusters rather than between shots.
        /// </remarks>
        Cluster = 5,
    }

    /// <summary>
    /// How a weapon lays out the shots in one volley.
    /// </summary>
    /// <remarks>
    /// Subclasses exist for authoring, not for behaviour: each one is a friendlier
    /// way to fill in <see cref="Kind"/>, <see cref="Count"/> and
    /// <see cref="SpreadDegrees"/>, which is all that travels to the server.
    /// Where those numbers put each projectile is <see cref="VolleyMath.Place"/>,
    /// the client's copy of the module's maths, which the weapon preview draws.
    ///
    /// Marked <c>[SerializeReference]</c> where it is used, so the concrete type
    /// is chosen per weapon and survives serialisation.
    /// </remarks>
    [System.Serializable]
    public abstract class ProjectilePattern
    {
        public abstract PatternKind Kind { get; }

        /// <summary>Projectiles in one volley.</summary>
        public abstract byte Count { get; }

        /// <summary>Total arc the volley covers, in degrees.</summary>
        public abstract float SpreadDegrees { get; }

        /// <summary>
        /// How many directions the shots divide between. Only Cluster uses it.
        /// </summary>
        /// <remarks>
        /// On the base class rather than cast for at the push, so adding a second
        /// two-count pattern later does not mean another special case in the
        /// editor. 0 means "not a grouped pattern", which every other kind is.
        /// </remarks>
        public virtual byte Groups => 0;
    }

    /// <summary>One projectile, straight ahead.</summary>
    [System.Serializable]
    public sealed class SingleShot : ProjectilePattern
    {
        public override PatternKind Kind => PatternKind.Single;
        public override byte Count => 1;
        public override float SpreadDegrees => 0f;
    }

    /// <summary>Several projectiles fanned across an arc, centred on the aim.</summary>
    [System.Serializable]
    public sealed class SpreadShot : ProjectilePattern
    {
        [Range(1, 24)]
        public byte Shots = 3;

        [Tooltip("Total arc covered, in degrees. The shots are spread evenly across it.")]
        [Range(0f, 180f)]
        public float Arc = 30f;

        public override PatternKind Kind => PatternKind.Spread;
        public override byte Count => Shots;
        public override float SpreadDegrees => Arc;
    }

    /// <summary>Projectiles evenly around a full circle. The aim direction sets the phase.</summary>
    [System.Serializable]
    public sealed class RingShot : ProjectilePattern
    {
        [Range(2, 32)]
        public byte Shots = 8;

        public override PatternKind Kind => PatternKind.Ring;
        public override byte Count => Shots;
        public override float SpreadDegrees => 360f;
    }

    /// <summary>
    /// Projectiles side by side, all travelling the same way.
    /// </summary>
    /// <remarks>
    /// The offsets are perpendicular to travel, so the line stays abreast however
    /// the player is facing.
    /// </remarks>
    [System.Serializable]
    public sealed class ParallelShot : ProjectilePattern
    {
        [Range(1, 12)]
        public byte Shots = 2;

        [Tooltip("Distance between the outermost projectiles, in tiles.")]
        [Range(0f, 6f)]
        public float Width = 1f;

        public override PatternKind Kind => PatternKind.Parallel;
        public override byte Count => Shots;
        public override float SpreadDegrees => Width;
    }

    /// <summary>
    /// Tight fans of projectiles, spaced evenly around a circle.
    /// </summary>
    /// <remarks>
    /// Two counts rather than one. <see cref="Directions"/> is how many ways it
    /// fires at once and <see cref="PerDirection"/> is how many bullets go each
    /// way, so four by two is the classic four-way pair volley. The aim direction
    /// sets the phase, exactly as it does for a ring.
    /// </remarks>
    [System.Serializable]
    public sealed class ClusterShot : ProjectilePattern
    {
        [Tooltip("How many ways it fires at once, spaced evenly around the circle.")]
        [Range(1, 16)]
        public byte Directions = 4;

        [Tooltip("Bullets down each direction. 1 is a plain ring.")]
        [Range(1, 8)]
        public byte PerDirection = 2;

        [Tooltip("Arc each individual fan covers, in degrees. Small keeps the " +
                 "cluster tight and the gaps between clusters wide.")]
        [Range(0f, 90f)]
        public float Arc = 8f;

        public override PatternKind Kind => PatternKind.Cluster;
        public override byte Count => (byte)Mathf.Clamp(Directions * PerDirection, 1, 255);
        public override float SpreadDegrees => Arc;
        public override byte Groups => Directions < 1 ? (byte)1 : Directions;
    }

    /// <summary>
    /// Projectiles that braid around the aim line.
    /// </summary>
    /// <remarks>
    /// All travel the same direction; only their positions in the wave differ, so
    /// they cross each other as they fly. Set the weapon's wave amplitude and
    /// frequency, or they all trace the same straight line.
    /// </remarks>
    [System.Serializable]
    public sealed class HelixShot : ProjectilePattern
    {
        [Range(2, 8)]
        public byte Strands = 2;

        public override PatternKind Kind => PatternKind.Helix;
        public override byte Count => Strands;
        public override float SpreadDegrees => 0f;
    }
}
