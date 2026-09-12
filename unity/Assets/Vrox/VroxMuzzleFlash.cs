using System.Collections.Generic;
using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// The flash at the barrel when anything fires.
    /// </summary>
    /// <remarks>
    /// Driven off the rows the shot already writes rather than off the act of
    /// firing, which is what makes it work for everyone. The client that pulled
    /// the trigger, the four players watching it and every enemy in the level all
    /// see the same flashes, because all of them are reading the same two tables:
    /// <c>Shot</c> for projectile weapons and <c>Tracer</c> for hitscan ones. No
    /// schema change was needed — both rows already carry an origin, a direction
    /// and a colour, because the thing being drawn had to know those anyway.
    ///
    /// Hooking this to the local player's fire button instead would have been
    /// less code and wrong: nobody else's gun would ever flash.
    ///
    /// A flash is fired once, on the frame its row is first seen, and then lives
    /// here on its own. It is not a thing in the world and the server has no
    /// opinion about it — by the time it is drawn the shot has already happened.
    ///
    /// <b>Timed against the local clock, deliberately.</b> The rows carry server
    /// timestamps, and using those would mean the flash appeared at the moment
    /// the server says the shot happened — which, once the two clocks differ by
    /// an unknown offset, is a moment that has already passed or has not arrived.
    /// A flash is feedback, so it starts when the news does. The projectile
    /// renderer needs the opposite and pays for it; see <see cref="VroxShots"/>.
    /// </remarks>
    public sealed class VroxMuzzleFlash : MonoBehaviour
    {
        /// <summary>In front of the bullets, so a flash is never behind its own shot.</summary>
        private const float Depth = -1.1f;

        [Tooltip("How long a flash lasts, in seconds. Short: a muzzle flash that " +
                 "outlives the eye's ability to notice it stops reading as an impulse " +
                 "and starts reading as a lamp.")]
        [Range(0.01f, 0.4f)]
        public float Duration = 0.07f;

        [Tooltip("How far the flare reaches down the barrel, in tiles.")]
        [Range(0.05f, 3f)]
        public float Length = 0.85f;

        [Tooltip("How wide the flare is across the barrel, as a fraction of its length.")]
        [Range(0.05f, 1f)]
        public float Width = 0.42f;

        [Tooltip("Size of the round burst at the muzzle, in tiles. This is the part " +
                 "that reads as heat; the flare is what reads as direction.")]
        [Range(0f, 2f)]
        public float CoreSize = 0.42f;

        [Tooltip("How far the flash burns towards white. A flash is hotter than the " +
                 "round it launched — keeping the weapon's colour only in the outer " +
                 "flare is what sells that.")]
        [Range(0f, 1f)]
        public float White = 0.75f;

        [Tooltip("Random size variation per flash, as a fraction. A volley whose " +
                 "flashes are identical reads as one object leaving the barrel; the " +
                 "same trick a shotgun needs to read as buckshot.")]
        [Range(0f, 0.8f)]
        public float Variance = 0.3f;

        [Tooltip("Optional. The star the flare is drawn with. Leave empty and one is " +
                 "generated, which is the intended setup.")]
        public Texture2D? FlareTexture;

        [Header("Recoil")]
        [Tooltip("Kick the view when your own gun goes off. Driven from the weapon's " +
                 "authored Kickback, so a Deagle shoves and an LMG rattles without either " +
                 "being tuned twice.")]
        public bool ShakeOnFire = true;

        [Tooltip("Tiles of camera kick per unit of the weapon's Kickback.")]
        [Range(0f, 2f)]
        public float ShakePerKickback = 0.55f;

        [Tooltip("How long a firing kick takes to die away, in seconds.")]
        [Range(0.02f, 0.5f)]
        public float ShakeSeconds = 0.09f;

        [Header("Placement")]
        [Tooltip("How far down the barrel the flash sits, as a multiple of the shooter's " +
                 "radius. 1 puts it on the edge of the sprite; a little over 1 puts it " +
                 "just clear of the body, which usually reads better than touching.")]
        [Range(0f, 3f)]
        public float Offset = 1.05f;

        [Tooltip("Radius to use for the player when there is no sprite to measure, in " +
                 "tiles. Only reached before the player is drawn, or in a scene where it " +
                 "has no SpriteRenderer.")]
        [Range(0f, 3f)]
        public float FallbackRadius = 0.5f;

        [Tooltip("Radius used for enemy fire, in tiles. Enemies are not measured the way " +
                 "the player is: a shot row says which side fired it but not which enemy, " +
                 "so there is no body here to ask for its size.")]
        [Range(0f, 4f)]
        public float EnemyRadius = 0.5f;

        [Tooltip("How much of the flare sits behind the muzzle. 0 starts it at the barrel " +
                 "and runs it forward, which is what the wired MuzzleFlare texture wants — " +
                 "it is bright at one end and fades down its length. 0.5 centres it, which " +
                 "is what the generated fallback star wants, being symmetric.")]
        [Range(0f, 0.5f)]
        public float FlareAnchor;

        /// <summary>A flash that has been fired and is now burning down.</summary>
        private struct Flash
        {
            public Vector2 At;
            public Vector2 Dir;
            public Color Tint;
            public float StartedAt;
            public float Scale;
        }

        private readonly List<Flash> _flashes = new();
        private readonly HashSet<ulong> _seenShots = new();
        private readonly HashSet<ulong> _seenTracers = new();
        private readonly HashSet<ulong> _live = new();

        private VroxGlow.Batch? _batch;
        private Mesh? _mesh;
        private Material? _material;
        private Texture2D? _owned;

        /// <summary>
        /// Whether the tables have been read once without drawing anything.
        /// </summary>
        /// <remarks>
        /// Joining a game in progress delivers every row that already exists in
        /// one go. Without this, connecting mid-firefight would flash once for
        /// every bullet in the air — a wall of light, for shots that were fired
        /// before you arrived.
        /// </remarks>
        private bool _primed;

        private void Awake()
        {
            _batch = new VroxGlow.Batch();
            _mesh = new Mesh { name = "Vrox Muzzle Flashes" };
            _mesh.MarkDynamic();

            _material = new Material(Shader.Find("Sprites/Default"))
            {
                name = "Vrox Muzzle Flash",
                mainTexture = FlareTexture != null ? FlareTexture : (_owned = VroxGlow.BuildFlare()),
            };
        }

        private void OnDestroy()
        {
            // Created in code, so not owned by the scene and not cleaned up with
            // it. Without this, entering play mode repeatedly leaks one of each.
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
            if (_owned != null) Destroy(_owned);
        }

        private void LateUpdate()
        {
            var net = VroxNet.Instance;
            if (net == null || !net.Ready || net.Conn is not { } conn
                || _mesh == null || _material == null || _batch == null)
            {
                return;
            }

            Collect(conn);
            Draw();
        }

        /// <summary>Fires a flash for every row that has appeared since last frame.</summary>
        private void Collect(SpacetimeDB.Types.DbConnection conn)
        {
            _live.Clear();
            foreach (var shot in conn.Db.Shot.Iter())
            {
                _live.Add(shot.Id);
                if (_seenShots.Add(shot.Id) && _primed)
                {
                    // Faction 0 is the players. Everything else came out of
                    // something the client cannot measure, so it gets the
                    // configured radius instead of a sprite's.
                    float radius = shot.Faction == 0
                        ? VroxMuzzle.PlayerRadius(FallbackRadius)
                        : EnemyRadius;

                    var dir = new Vector2(shot.DirX, shot.DirY);
                    Fire(new Vector2(shot.OriginX, shot.OriginY), dir,
                         PackedColour.Unpack(shot.Tint), radius);
                    Recoil(conn, shot.Owner, dir);
                }
            }

            // Ids are not reused, but a set that only ever grows is a leak in a
            // session long enough to matter. Dropping the ones whose rows have
            // gone keeps it the size of what is actually in flight.
            _seenShots.IntersectWith(_live);

            _live.Clear();
            foreach (var tracer in conn.Db.Tracer.Iter())
            {
                _live.Add(tracer.Id);
                if (_seenTracers.Add(tracer.Id) && _primed)
                {
                    // The muzzle is the near end. Both ends come from the server,
                    // so the flash sits along the shot that was actually taken
                    // rather than along one this client worked out for itself.
                    //
                    // Always the player's radius: hitscan is fired from
                    // FireHitscan, which takes a Player, so every tracer row in
                    // the table was made by somebody holding a gun.
                    var aim = new Vector2(tracer.X2 - tracer.X1, tracer.Y2 - tracer.Y1);
                    Fire(new Vector2(tracer.X1, tracer.Y1), aim,
                         PackedColour.Unpack(tracer.Tint),
                         VroxMuzzle.PlayerRadius(FallbackRadius));
                    Recoil(conn, tracer.Owner, aim);
                }
            }
            _seenTracers.IntersectWith(_live);

            _primed = true;
        }

        /// <summary>
        /// Starts a flash at the muzzle, given where the shot was written from.
        /// </summary>
        /// <remarks>
        /// The origin the server wrote is the shooter's centre, so the flash is
        /// pushed forward to the edge of the body before it is stored. Done here
        /// rather than at each call site so there is one place where a flash can
        /// end up in the wrong spot.
        /// </remarks>
        private void Fire(Vector2 at, Vector2 dir, Color tint, float radius)
        {
            _flashes.Add(new Flash
            {
                At = VroxMuzzle.Point(at, dir, radius * Offset),
                Dir = dir,
                Tint = tint,
                StartedAt = Time.time,
                Scale = Variance <= 0f ? 1f : Random.Range(1f - Variance, 1f + Variance),
            });
        }

        /// <summary>Kicks the view, but only for the gun this player is holding.</summary>
        /// <remarks>
        /// Driven off the row rather than off the button, so the view kicks when
        /// the shot actually happened rather than when it was asked for. A dry
        /// magazine, a rate limit or a refused reducer all produce no row and so
        /// produce no kick — where shaking on the click would have made an empty
        /// gun feel exactly like a firing one.
        ///
        /// The strength comes from the weapon's own Kickback, which the server
        /// already uses to shove the player. One authored number, two effects
        /// that agree: the view moves because the character did.
        /// </remarks>
        private void Recoil(SpacetimeDB.Types.DbConnection conn,
                            SpacetimeDB.Identity owner, Vector2 direction)
        {
            var net = VroxNet.Instance;
            if (!ShakeOnFire || net == null || net.LocalIdentity is not { } me || owner != me)
            {
                return;
            }
            if (net.LocalPlayer is not { } player || player.WeaponId == 0
                || conn.Db.WeaponDef.Id.Find(player.WeaponId) is not { Kickback: > 0f } weapon)
            {
                return;
            }

            VroxCamera.Shake(weapon.Kickback * ShakePerKickback, ShakeSeconds, direction);
        }

        private void Draw()
        {
            VroxCamera.ScreenAxes(out var right, out var up);
            _batch!.Clear();

            float life = Mathf.Max(0.01f, Duration);

            for (int i = _flashes.Count - 1; i >= 0; i--)
            {
                var flash = _flashes[i];
                float age = (Time.time - flash.StartedAt) / life;
                if (age >= 1f)
                {
                    // Swapped with the last rather than removed in place: order
                    // does not matter here, and this keeps a burst of fire from
                    // shifting the whole list every frame.
                    _flashes[i] = _flashes[_flashes.Count - 1];
                    _flashes.RemoveAt(_flashes.Count - 1);
                    continue;
                }

                // Bright and full-size immediately, then collapsing. A flash that
                // grows into place reads as something inflating; the real thing is
                // already at its brightest by the time you notice it.
                float t = 1f - age;
                float shrink = Mathf.Lerp(0.45f, 1f, t);
                float scale = flash.Scale * shrink;

                var tint = Color.Lerp(flash.Tint, Color.white, White);
                tint.a = t * t;

                _batch.Oriented(flash.At, flash.Dir,
                                Length * scale * 0.5f, Length * Width * scale * 0.5f,
                                Depth, tint, FlareAnchor);

                if (CoreSize > 0f)
                {
                    var core = Color.Lerp(flash.Tint, Color.white, Mathf.Min(1f, White + 0.2f));
                    core.a = t;
                    _batch.Blob(flash.At, CoreSize * scale * 0.5f, right, up, Depth, core);
                }
            }

            _batch.Flush(_mesh!, _material!);
        }
    }
}
