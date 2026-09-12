using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Draws every projectile in flight, as one mesh in one draw call.
    /// </summary>
    /// <remarks>
    /// The triangles are built through <see cref="VroxGlow.Batch"/>, which is
    /// where the reasoning about soft-edged quads and one-draw-call batching
    /// lives. This component's job is the part that is specific to a bullet:
    /// where it is, what it looks like, and the streak behind it.
    ///
    /// The streak is built here rather than by hanging a <see cref="LineRenderer"/>
    /// or a <c>TrailRenderer</c> off each shot. A LineRenderer is the right tool
    /// for the handful of hitscan lines <see cref="VroxTracers"/> draws. It is the
    /// wrong tool at bullet-hell counts, where it would mean one GameObject and
    /// one draw call per projectile.
    ///
    /// A shot's row is written once and never updated, so its position comes from
    /// how long it has been alive: <c>origin + dir * speed * t</c>. Every client
    /// evaluates the same function and gets the same answer, which is why a bullet
    /// costs one insert and one delete however far it flies. The streak is the
    /// same function sampled at earlier <c>t</c>, so it needs no history buffer
    /// and no per-shot state — and it follows a weaving shot around its curve,
    /// which a straight line between two points could not.
    /// </remarks>
    public sealed class VroxShots : MonoBehaviour
    {
        /// <summary>
        /// Slightly in front of the player plane, so bullets are never hidden
        /// behind the character that fired them.
        /// </summary>
        private const float Depth = -1f;

        [Header("Bullet")]
        [Tooltip("How far the glow reaches past the shot's actual size, as a multiplier. " +
                 "The glow is not the hitbox — the server decides that from Size alone — " +
                 "so this is free to be generous.")]
        [Range(1f, 6f)]
        public float GlowScale = 3.2f;

        [Tooltip("Opacity of the outer glow. This is the half that carries the weapon's " +
                 "colour, and so the half that says whose bullet it is — a saturated hue " +
                 "is always darker than white, so it can identify a shot but cannot be " +
                 "what makes it findable.")]
        [Range(0f, 1f)]
        public float GlowAlpha = 0.55f;

        [Tooltip("Size of the white-hot centre, as a fraction of the shot. Well under 1: " +
                 "a small hard point inside a wide soft halo is what reads as hot. A core " +
                 "the same size as the glow just reads as a bigger blob.")]
        [Range(0.1f, 1f)]
        public float CoreScale = 0.55f;

        [Tooltip("How far the centre burns towards white. Keep it high: against the muted " +
                 "terrain this is the whole reason a bullet can be picked out at a glance. " +
                 "White reaches roughly 8:1 luminance contrast on every biome, where the " +
                 "saturated hues manage 2-3:1. Dropping this to let the colour through is " +
                 "the one change that will make bullets hard to see again.\n\n" +
                 "A blend, not a brightness multiply, so it works on the ordinary " +
                 "alpha-blended material instead of silently clamping the way a multiply " +
                 "does.")]
        [Range(0f, 1f)]
        public float CoreWhite = 0.85f;

        [Header("Streak")]
        [Tooltip("How far behind a projectile its streak reaches, in seconds of travel. " +
                 "Seconds rather than tiles so a fast bullet draws a long streak and a " +
                 "slow one a short one, which is the difference the eye actually reads. " +
                 "Zero turns the streak off.")]
        [Range(0f, 0.5f)]
        public float TrailSeconds = 0.13f;

        [Tooltip("Samples along the streak. A straight shot only needs one segment; a " +
                 "weaving one needs enough to bend smoothly. Each costs two vertices in a " +
                 "mesh that is already being built — there is still one draw call however " +
                 "many bullets are on screen.")]
        [Range(1, 12)]
        public int TrailSegments = 7;

        [Tooltip("Width of the streak where it leaves the bullet, as a fraction of the " +
                 "bullet. Well below 1: a tracer reads as hot because it is thin and " +
                 "bright, not because it is thick. A wide streak reads as a tube the " +
                 "bullet is flying down.")]
        [Range(0.05f, 2f)]
        public float TrailWidth = 0.35f;

        [Tooltip("Width at the far end of the streak, as a fraction of its width at the " +
                 "bullet. A streak that narrows reads as motion; one of even width reads " +
                 "as a stick being dragged along behind.")]
        [Range(0f, 1f)]
        public float TailWidth = 0.1f;

        [Tooltip("Shape of the fade from bullet to tail. 1 is a straight ramp; higher " +
                 "keeps the streak solid near the bullet and drops it away sharply, which " +
                 "is what makes it read as a trail rather than as a gradient.")]
        [Range(0.5f, 4f)]
        public float TailFalloff = 2.2f;

        [Tooltip("Scales drawn bullet art against the bullet's authored Size. The " +
                 "collision radius is the server's and does not change with this — " +
                 "turn it up and bullets look bigger than they hit.")]
        [Range(0.5f, 6f)]
        public float SpriteScale = 2.2f;

        [Header("Look")]
        [Tooltip("Optional. The soft round blob every bullet and streak is drawn with. " +
                 "Leave empty and one is generated — that is the intended setup, and this " +
                 "is here so a nicer one can be dropped in without touching code. " +
                 "Ignored when a Bullet Sprite Catalogue is assigned, which carries its " +
                 "own blob on the sheet.")]
        public Texture2D? DotTexture;

        [Tooltip("Bullet art. Leave empty and every shot draws as the plain blob, which " +
                 "is exactly what they did before art existed.")]
        public Vrox.Equipment.BulletSpriteCatalogue? Sprites;

        /// <summary>Scratch for one streak's sample points. Reused, never grown.</summary>
        private readonly Vector2[] _path = new Vector2[13];

        private VroxGlow.Batch? _batch;
        private Mesh? _mesh;
        private Material? _material;
        private Texture2D? _owned;

        private void Awake()
        {
            _batch = new VroxGlow.Batch();
            _mesh = new Mesh { name = "Vrox Shots" };
            // Rebuilt every frame, so tell Unity to keep it in a buffer suited to
            // frequent rewrites rather than one optimised for static geometry.
            _mesh.MarkDynamic();

            // Vertex colours, so both sides' shots live in one mesh and one draw
            // call rather than one batch per faction.
            // The catalogue's sheet carries the blob as well as the bolts, so one
            // texture still serves every shot in the game and the single draw call
            // survives. Without a catalogue nothing has art and the generated blob
            // is the whole texture, exactly as before.
            var texture = Sprites != null && Sprites.Usable
                ? Sprites.Sheet
                : DotTexture != null ? DotTexture : (_owned = VroxGlow.BuildBlob());

            _material = new Material(Shader.Find("Sprites/Default"))
            {
                name = "Vrox Shots",
                mainTexture = texture,
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

            // A shot's age is measured against the timestamp the *server* stamped
            // on it, but compared here against the *local* clock. That is only
            // correct while both run on the same machine, which is true today and
            // will not be once there is a real server: the two clocks will differ
            // by an unknown offset and every projectile will be drawn ahead of or
            // behind where it should be. Estimating that offset is the next layer,
            // and deliberately not this one.
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

            _batch.Clear();

            // Once a frame: the view cannot turn between two shots of the same
            // volley, and two trig calls per bullet would be the most expensive
            // thing in this loop.
            VroxCamera.ScreenAxes(out var right, out var up);

            // Resolved once. A catalogue that is assigned but unusable draws
            // everything as plain blobs rather than as untextured white quads —
            // and because the sheet is only the material's texture when it *is*
            // usable, the two always agree about which texture is bound.
            var sprites = Sprites != null && Sprites.Usable ? Sprites : null;
            var blob = sprites != null ? sprites.BlobUv : new Rect(0f, 0f, 1f, 1f);

            foreach (var shot in conn.Db.Shot.Iter())
            {
                float t = (now - shot.SpawnedAt.MicrosecondsSinceUnixEpoch) / 1_000_000f;
                if (t < 0f)
                {
                    // Stamped slightly ahead of our clock; draw it at the barrel
                    // rather than behind it.
                    t = 0f;
                }

                var head = PositionAt(shot, t);
                float half = shot.Size * 0.5f;

                // Straight off the row, authored on the weapon — this component
                // owns no palette. Colouring by faction here, or by element from
                // a table living in the client, both meant a designer could not
                // change how a weapon looks without an edit to code.
                //
                // The cost is real and worth stating: telling your own fire from
                // incoming fire is now something the weapons have to be authored
                // to do, where before it was enforced here. Nothing checks it.
                var colour = PackedColour.Unpack(shot.Tint);

                // Art, when this bullet has been dressed. Drawn once, in its own
                // colours, pointed along travel — none of the blob's compositing
                // applies: the streak, the glow and the whitened core are how a
                // round dot is made to read as a bullet, and stacking them on a
                // drawn bolt muddies art that already has its own trail and core.
                var art = sprites?.For(shot.SpriteId);
                if (art != null)
                {
                    // Size is the bullet's own; the aspect stretches it back out so
                    // a long bolt stays long rather than being squashed square.
                    float halfLength = half * SpriteScale * art.Aspect;
                    float halfWidth = half * SpriteScale;
                    _batch.Sprite(head, new Vector2(shot.DirX, shot.DirY),
                                  halfLength, halfWidth, Depth, Color.white, art.UvRect);
                    continue;
                }

                // Streak, then glow, then core. All three sit on the same plane
                // and this mesh is drawn with depth writes off, so what is in
                // front is decided by the order triangles were submitted and
                // nothing else. A bullet drawing over its own tail is the whole
                // point; a bullet drawing over *another* bullet's tail is
                // arbitrary, and at these sizes invisible.
                AppendTrail(shot, t, half * TrailWidth, colour, blob);

                var glow = colour;
                glow.a = colour.a * GlowAlpha;
                _batch.Blob(head, half * GlowScale, right, up, Depth, glow, blob);

                var core = Color.Lerp(colour, Color.white, CoreWhite);
                core.a = colour.a;
                _batch.Blob(head, half * CoreScale, right, up, Depth, core, blob);
            }

            _batch.Flush(_mesh, _material);
        }

        /// <summary>
        /// Where a shot is, <paramref name="t"/> seconds after it was fired.
        /// </summary>
        /// <remarks>
        /// The single definition of a projectile's path on the client, called once
        /// for the bullet and once per streak sample. It has to agree with the
        /// server's own evaluation — that is what makes the thing you see the
        /// thing that collides — so if one changes, both change.
        ///
        /// Speed, size, element and wave travel on the shot itself rather than
        /// being read from the weapon, so a projectile stays correct even if the
        /// weapon is retuned — or unequipped — while it is still in flight. That
        /// is also what lets one volley contain several kinds of bullet: the
        /// weapon is asked once, at spawn.
        /// </remarks>
        private static Vector2 PositionAt(SpacetimeDB.Types.Shot shot, float t)
        {
            float x = shot.OriginX + shot.DirX * shot.Speed * t;
            float y = shot.OriginY + shot.DirY * shot.Speed * t;

            // Still a function of time: the row is never updated, the path is just
            // no longer a straight one. Sideways displacement is applied
            // perpendicular to travel, so the wave follows the shot's heading
            // rather than the world axes.
            if (shot.WaveAmplitude != 0f)
            {
                float lateral = shot.WaveAmplitude *
                    Mathf.Sin(t * shot.WaveFrequency * Mathf.PI * 2f + shot.WavePhase);
                x += -shot.DirY * lateral;
                y += shot.DirX * lateral;
            }

            return new Vector2(x, y);
        }

        /// <summary>
        /// The streak behind a bullet: the same path, sampled backwards in time.
        /// </summary>
        /// <remarks>
        /// Built as one connected ribbon rather than a quad per segment. Separate
        /// quads meet at an angle wherever the path bends and leave a notch on the
        /// outside of the curve, which a helix bullet would show off several times
        /// a second. Sharing the two vertices at each joint closes it.
        ///
        /// Across its width the ribbon samples the middle column of the blob
        /// texture, so its sides fall off softly instead of ending at a hard
        /// polygon edge. That is the difference between a streak and a smear, and
        /// it costs one extra UV per vertex.
        ///
        /// The ribbon is widened perpendicular to travel in world space, unlike
        /// the blobs. Its direction is a fact about the world — which way the
        /// bullet is going — so it has to turn with the world when the camera
        /// does.
        ///
        /// Nothing is drawn behind the muzzle: a bullet younger than the streak is
        /// long gets a short streak, so a volley grows its tails rather than
        /// appearing with them already trailing out of the gun.
        /// </remarks>
        private void AppendTrail(SpacetimeDB.Types.Shot shot, float t, float half, Color colour,
                                 Rect blob)
        {
            float span = Mathf.Min(TrailSeconds, t);
            int segments = Mathf.Clamp(TrailSegments, 1, _path.Length - 1);
            if (span <= 0.0001f || half <= 0f)
            {
                return;
            }

            int samples = segments + 1;
            for (int i = 0; i < samples; i++)
            {
                _path[i] = PositionAt(shot, t - span * i / segments);
            }

            int v = _batch!.VertexCount;
            for (int i = 0; i < samples; i++)
            {
                // The direction the ribbon runs *at this sample*, taken across the
                // joint rather than along one side of it, so a bend is mitred
                // instead of kinked.
                Vector2 along = _path[Mathf.Max(i - 1, 0)] - _path[Mathf.Min(i + 1, samples - 1)];
                if (along.sqrMagnitude < 1e-8f)
                {
                    // A stationary or barely-moved sample has no direction to be
                    // perpendicular to. The shot's own heading always does.
                    along = new Vector2(shot.DirX, shot.DirY);
                }
                along.Normalize();

                var perp = new Vector2(-along.y, along.x);
                float u = (float)i / segments;
                float w = Mathf.Lerp(half, half * TailWidth, u);

                var tint = colour;
                tint.a = colour.a * Mathf.Pow(1f - u, TailFalloff);

                // Straight across the middle of the blob: soft at both edges,
                // solid down the centre line. Measured inside the blob's region of
                // the sheet, not across the whole texture — a hard-coded 0.5 used
                // to mean the middle of the only texture there was, and now means
                // the middle of a sheet of unrelated bolts.
                float uMid = blob.center.x;
                _batch.Vertex(_path[i] - perp * w, Depth, new Vector2(uMid, blob.yMin), tint);
                _batch.Vertex(_path[i] + perp * w, Depth, new Vector2(uMid, blob.yMax), tint);
            }

            for (int i = 0; i < segments; i++)
            {
                int a = v + i * 2;
                _batch.Triangle(a, a + 1, a + 3);
                _batch.Triangle(a, a + 3, a + 2);
            }
        }
    }
}
