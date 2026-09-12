using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// One kind of bullet inside a volley, so a single pattern can fire a mixture.
    /// </summary>
    /// <remarks>
    /// A variant is <em>what the bullet is</em>. Where it goes is still the
    /// pattern's job, and how the whole volley moves — spin and wave — is still
    /// the weapon's. Keeping those three separate is what stops the pattern list
    /// from needing a new entry for every combination of shape and bullet.
    ///
    /// A plain serialisable class rather than a <c>[SerializeReference]</c>
    /// hierarchy, because every variant has the same fields and there is nothing
    /// to subclass. That also keeps it out of <c>VroxReferenceDrawer</c>, which
    /// resolves its type list from the declared field type — for a list of
    /// references the declared type is the list, not the element, and the picker
    /// comes back empty.
    ///
    /// Named <c>BulletVariant</c> and not <c>BulletProfile</c> on purpose: the
    /// generated bindings define <c>SpacetimeDB.Types.BulletProfile</c>, and the
    /// editor push has both namespaces in scope.
    /// </remarks>
    [System.Serializable]
    public sealed class BulletVariant
    {
        [Tooltip("What kind of damage this bullet deals. Tallied separately, so a " +
                 "fight report can show where a player's damage actually came from.")]
        public DamageElement Element = DamageElement.Physical;

        [Tooltip("Rolled per projectile, inclusive of both ends.")]
        public ushort DamageMin = 8;

        public ushort DamageMax = 12;

        [Header("On Hit")]
        [Tooltip("A debuff applied to whatever it hits. Refreshes rather than stacks.")]
        public DebuffKind Debuff = DebuffKind.None;

        [Range(0f, 30f)]
        public float DebuffSeconds = 1.5f;

        [Header("Projectile")]
        [Tooltip("Tiles per second. Mixing speeds within one volley pulls the shape " +
                 "apart as it travels — a ring stops being a ring after a moment.")]
        [Range(1f, 40f)]
        public float Speed = 14f;

        [Range(0.05f, 1.5f)]
        public float Size = 0.3f;

        [Range(100, 5000)]
        public ushort LifetimeMs = 1200;

        [Tooltip("Colour this bullet is drawn in. Its own colour, not the weapon's — " +
                 "a mix whose variants all shared one tint would be invisible, which " +
                 "is most of the point of mixing them.")]
        public Color Tint = Color.white;

        [Tooltip("Bullet art for this variant, as an index into the Bullet Sprite " +
                 "Catalogue. Unlike Tint, 0 here inherits the weapon's — most mixes " +
                 "want one look for the whole volley, and an Edges weapon that wants " +
                 "two sets this only on the variant that differs.")]
        [Min(0)]
        public int BulletSpriteId;
    }

    /// <summary>How a volley's projectile slots map onto its bullet variants.</summary>
    /// <remarks>
    /// Both are arithmetic over the slot count rather than a stored per-slot
    /// table, so changing a pattern's shot count can never leave the assignment
    /// half-updated and firing the wrong bullets.
    /// </remarks>
    public enum SlotAssignment : byte
    {
        /// <summary>Alternating — a ring of eight with two variants gives ABABABAB.</summary>
        Cycle = 0,

        /// <summary>Contiguous runs — the same ring gives AAAABBBB.</summary>
        Block = 1,

        /// <summary>
        /// The outermost two slots take the first variant, the rest take the others.
        /// </summary>
        /// <remarks>
        /// For volleys whose slots are ordered across an arc — Spread, Parallel,
        /// and each fan of a Cluster — this is heavy flankers around a weaker
        /// core, so where you stand in the cone decides what hits you. Cycle
        /// scatters the heavy bullets through the fan and Block puts them all down
        /// one side; neither can mark the edges.
        ///
        /// On a Ring the ends are adjacent, so "outermost" means nothing and this
        /// is simply two marked bullets next to each other.
        /// </remarks>
        Edges = 2,
    }
}
