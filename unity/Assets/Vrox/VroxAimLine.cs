using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Shows where the equipped weapon reaches, before it is fired.
    /// </summary>
    /// <remarks>
    /// Five guns with ranges from seven tiles to thirty-four look identical while
    /// you are holding them. Reach is the stat that decides whether a shot is
    /// worth taking, and until now the only way to learn it was to fire and watch
    /// nothing happen. This draws it.
    ///
    /// The spread is drawn for the same reason. A shotgun's nine pellets across
    /// fifteen degrees is the whole character of the weapon, and it is invisible
    /// until the moment it stops mattering.
    ///
    /// <b>This is a preview, not a claim.</b> It is drawn from the client's aim
    /// and the weapon's authored range, and it deliberately does not trace
    /// terrain: working out where a ray actually stops is the server's job, and a
    /// client that answered it would be the client deciding what the server knows
    /// — the exact mistake that made a stale equip cache look authoritative. So
    /// the line runs its full reach and passes through walls. It says "this gun
    /// reaches this far", never "this shot will land there".
    ///
    /// Drawn from the muzzle rather than the player's centre, through the same
    /// <see cref="VroxMuzzle"/> the flash and the tracers use, so the preview
    /// starts where the shot will.
    /// </remarks>
    public sealed class VroxAimLine : MonoBehaviour
    {
        /// <summary>Behind the bullets and the flash, in front of the ground.</summary>
        private const float Depth = -0.5f;

        [Tooltip("Draw the line at all. Off leaves aiming entirely to the cursor.")]
        public bool Show = true;

        [Tooltip("How far the preview runs for a weapon with no range — a projectile gun, " +
                 "whose shots keep going until they time out. Reach is not a fixed number " +
                 "there, so this is a hint rather than a measurement.")]
        [Range(1f, 40f)]
        public float ProjectileLength = 12f;

        [Tooltip("Width of the line, in tiles.")]
        [Range(0.01f, 0.3f)]
        public float Width = 0.05f;

        [Tooltip("Opacity at the muzzle. The line fades to nothing at its far end, so this " +
                 "is the strongest it ever gets — keep it low, it is an aid and not a " +
                 "thing in the world.")]
        [Range(0f, 1f)]
        public float Alpha = 0.35f;

        [Tooltip("Draw the outer edges of the volley for a weapon that spreads. This is " +
                 "the cone the pellets are dealt across, not a prediction of any one of " +
                 "them.")]
        public bool ShowSpread = true;

        [Tooltip("Opacity of the spread edges, relative to the centre line.")]
        [Range(0f, 1f)]
        public float SpreadAlpha = 0.5f;

        [Tooltip("Optional. The soft band the lines are drawn with. Leave empty and one is " +
                 "generated, the same one the tracers use.")]
        public Texture2D? StreakTexture;

        private VroxShooter? _shooter;
        private VroxGlow.Batch? _batch;
        private Mesh? _mesh;
        private Material? _material;
        private Texture2D? _owned;

        private void Awake()
        {
            _shooter = GetComponent<VroxShooter>();
            _batch = new VroxGlow.Batch();
            _mesh = new Mesh { name = "Vrox Aim Line" };
            _mesh.MarkDynamic();

            _material = new Material(Shader.Find("Sprites/Default"))
            {
                name = "Vrox Aim Line",
                mainTexture = StreakTexture != null
                    ? StreakTexture
                    : (_owned = VroxGlow.BuildStreak()),
            };
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
            if (_owned != null) Destroy(_owned);
        }

        private void LateUpdate()
        {
            var net = VroxNet.Instance;
            if (!Show || _shooter == null || _batch == null || _mesh == null || _material == null
                || net == null || !net.Ready || net.Conn is not { } conn
                || net.LocalPlayer is not { } player)
            {
                return;
            }

            _batch.Clear();

            // Asked of the server's own catalogue every frame rather than cached.
            // A null here means one of two things — the player is unarmed, or this
            // client has not been told about the weapon yet — and they are
            // indistinguishable from here, so both draw nothing rather than
            // guessing at a reach.
            if (player.WeaponId == 0
                || conn.Db.WeaponDef.Id.Find(player.WeaponId) is not { } weapon)
            {
                _batch.Flush(_mesh, _material);
                return;
            }

            var aim = _shooter.Aim;
            if (aim.sqrMagnitude < 1e-8f)
            {
                _batch.Flush(_mesh, _material);
                return;
            }

            float reach = weapon.Range > 0f ? weapon.Range : ProjectileLength;
            var muzzle = VroxMuzzle.Point(
                new Vector2(player.X, player.Y), aim,
                VroxMuzzle.PlayerRadius(0.5f));

            var colour = PackedColour.Unpack(weapon.Tint);

            Line(muzzle, aim, reach, colour, Alpha);

            // The edges of the fan, not any one pellet. PlaceShots deals the
            // volley evenly across the arc, so the outermost two sit at exactly
            // half of it either side — which makes these two lines the honest
            // boundary of where the shell can go.
            if (ShowSpread && weapon.SpreadDegrees > 0f && weapon.Shots > 1
                && weapon.SpreadDegrees < 360f)
            {
                float half = weapon.SpreadDegrees * 0.5f;
                Line(muzzle, Rotate(aim, half), reach, colour, Alpha * SpreadAlpha);
                Line(muzzle, Rotate(aim, -half), reach, colour, Alpha * SpreadAlpha);
            }

            _batch.Flush(_mesh, _material);
        }

        /// <summary>One line, solid at the muzzle and gone by the far end.</summary>
        /// <remarks>
        /// Faded along its length rather than drawn at an even weight, so the eye
        /// reads it as reach falling away rather than as a wall the shot will stop
        /// at. A hard end would be making a promise about the far tile that this
        /// component is not in a position to keep.
        /// </remarks>
        private void Line(Vector2 from, Vector2 along, float length, Color colour, float alpha)
        {
            along = along.normalized;
            var perp = new Vector2(-along.y, along.x) * Width * 0.5f;
            var to = from + along * length;

            var near = colour; near.a = alpha;
            var far = colour; far.a = 0f;

            int v = _batch!.VertexCount;
            _batch.Vertex(from - perp, Depth, new Vector2(0.5f, 0f), near);
            _batch.Vertex(from + perp, Depth, new Vector2(0.5f, 1f), near);
            _batch.Vertex(to - perp, Depth, new Vector2(0.5f, 0f), far);
            _batch.Vertex(to + perp, Depth, new Vector2(0.5f, 1f), far);

            _batch.Triangle(v, v + 1, v + 3);
            _batch.Triangle(v, v + 3, v + 2);
        }

        private static Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(r);
            float s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }
    }
}
