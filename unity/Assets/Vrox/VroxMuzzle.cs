using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Where a shot leaves its shooter, rather than where the shooter stands.
    /// </summary>
    /// <remarks>
    /// The server writes every shot's origin at the shooter's centre, because
    /// that is where the shooter *is* — a position, not a body with a gun sticking
    /// out of one side. Drawn literally, a muzzle flash goes off inside the
    /// player's chest.
    ///
    /// So the muzzle is worked out here, on the client, by pushing the origin
    /// forward along the firing direction by roughly the shooter's radius. This is
    /// cosmetic and only cosmetic: it moves where a flash is *drawn* and never
    /// where anything is resolved.
    ///
    /// <b>The bullets themselves are deliberately not moved.</b> A projectile's
    /// drawn position has to match the path the server evaluates for collision,
    /// so nudging it forward would draw bullets that pass through things they
    /// visibly missed. Making a projectile actually leave the barrel is a server
    /// change — spawn the shot forward of the shooter — and a gameplay one, since
    /// it shortens every shot's travel to its target.
    /// </remarks>
    public static class VroxMuzzle
    {
        private static SpriteRenderer? _sprite;

        /// <summary>
        /// How far it is from the local player's centre to the edge of its sprite,
        /// in tiles.
        /// </summary>
        /// <remarks>
        /// Measured from the sprite rather than configured, so changing the art or
        /// the scale moves the muzzle with it instead of leaving a number in an
        /// inspector that used to be right.
        ///
        /// The mean of the two half-extents, because a shot can leave in any
        /// direction and the sprite is only square-ish. Taken from the sprite's own
        /// bounds and the transform scale rather than from
        /// <see cref="Renderer.bounds"/>, which is an axis-aligned box around the
        /// *rotated* sprite and so grows and shrinks as the billboard turns — a
        /// muzzle that breathed with the camera would be worse than one in the
        /// wrong place.
        ///
        /// Recomputed on each call rather than cached. It is a handful of
        /// multiplies, it happens once per shot fired, and a cached radius is one
        /// more thing that would go on being believed after the thing it measured
        /// had changed.
        /// </remarks>
        public static float PlayerRadius(float fallback)
        {
            if (_sprite == null)
            {
                // Unity's fake-null covers the destroyed case too, so this also
                // recovers after a scene reload rather than holding a dead
                // reference for the rest of the session.
                _sprite = Object.FindAnyObjectByType<VroxPlayer>() is { } player
                    ? player.GetComponentInChildren<SpriteRenderer>()
                    : null;
            }

            if (_sprite == null || _sprite.sprite == null)
            {
                return fallback;
            }

            var extents = _sprite.sprite.bounds.extents;
            var scale = _sprite.transform.lossyScale;
            float radius = (Mathf.Abs(extents.x * scale.x) + Mathf.Abs(extents.y * scale.y)) * 0.5f;

            return radius > 0f ? radius : fallback;
        }

        /// <summary>Pushes an origin forward along the direction it fired in.</summary>
        /// <remarks>
        /// A zero-length direction is left where it is rather than pushed an
        /// arbitrary way. It should not happen — a shot always has a heading — and
        /// a flash in the wrong place is easier to notice than one that quietly
        /// drifted east.
        /// </remarks>
        public static Vector2 Point(Vector2 origin, Vector2 direction, float distance)
        {
            if (distance == 0f || direction.sqrMagnitude < 1e-8f)
            {
                return origin;
            }
            return origin + direction.normalized * distance;
        }
    }
}
