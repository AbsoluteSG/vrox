using UnityEngine;
using UnityEngine.InputSystem;

namespace Vrox
{
    /// <summary>
    /// Replaces the system pointer with a ring, and fills that ring while the
    /// weapon reloads.
    /// </summary>
    /// <remarks>
    /// The reload is the one piece of state that decides whether pulling the
    /// trigger does anything, and it was invisible: the gun simply stopped
    /// working for a second or three and started again. Putting it on the
    /// crosshair puts it where the eye already is — you are looking at what you
    /// are aiming at, not at a bar somewhere else on the screen.
    ///
    /// Drawn in the world rather than as UI, because it has to sit in the same
    /// mesh pipeline as everything else and because a world-space ring is the
    /// only kind that stays put relative to what it is pointing at. Its size is
    /// derived from the camera's orthographic size, so it stays the same fraction
    /// of the screen whatever the zoom.
    ///
    /// <b>Progress is measured against the local clock.</b> The row carries the
    /// server's absolute finish time, and this compares it to
    /// <c>DateTimeOffset.UtcNow</c> — correct only while both run on the same
    /// machine, which is true today. Once there is a real server the two clocks
    /// differ by an unknown offset and the fill will start or finish early by
    /// that much. It is the same debt <see cref="VroxShots"/> carries and it will
    /// be paid off in the same place.
    /// </remarks>
    public sealed class VroxCursor : MonoBehaviour
    {
        /// <summary>In front of everything. A crosshair behind a bullet is not a crosshair.</summary>
        private const float Depth = -2f;

        [Tooltip("Hide the system pointer while this is running. Off draws the ring on top " +
                 "of the arrow, which is useful when something has gone wrong and you want " +
                 "to see where the real pointer is.")]
        public bool HideSystemCursor = true;

        [Tooltip("Ring radius as a fraction of half the screen's height. Taken from the " +
                 "camera's orthographic size rather than fixed in tiles, so zooming does " +
                 "not resize the crosshair.")]
        [Range(0.005f, 0.2f)]
        public float Radius = 0.035f;

        [Tooltip("Ring thickness, as a fraction of its radius.")]
        [Range(0.05f, 1f)]
        public float Thickness = 0.22f;

        [Tooltip("Segments around the ring. Enough that it reads as a circle rather than " +
                 "a die, and no more — it is redrawn every frame.")]
        [Range(8, 64)]
        public int Segments = 40;

        [Tooltip("Opacity of the ring when nothing is happening.")]
        [Range(0f, 1f)]
        public float Alpha = 0.5f;

        [Tooltip("Opacity of the part that has filled in. Brighter than the ring, so the " +
                 "sweep is the thing you notice.")]
        [Range(0f, 1f)]
        public float FillAlpha = 0.95f;

        [Tooltip("Colour the ring by the equipped weapon instead of the colour below. The " +
                 "weapon palette already tells you whose fire is whose; this carries it " +
                 "onto the crosshair.")]
        public bool UseWeaponColour = true;

        [Tooltip("Used when there is no weapon, or when the weapon colour is turned off.")]
        public Color Colour = new Color(0.9f, 0.95f, 1f);

        [Tooltip("Optional. Leave empty and one is generated. Only the opaque middle of it " +
                 "is ever sampled, so this exists for a material swap rather than a look.")]
        public Texture2D? RingTexture;

        private VroxGlow.Batch? _batch;
        private Mesh? _mesh;
        private Material? _material;
        private Texture2D? _owned;
        private bool _hidden;

        private void Awake()
        {
            _batch = new VroxGlow.Batch();
            _mesh = new Mesh { name = "Vrox Cursor" };
            _mesh.MarkDynamic();

            _material = new Material(Shader.Find("Sprites/Default"))
            {
                name = "Vrox Cursor",
                mainTexture = RingTexture != null ? RingTexture : (_owned = VroxGlow.BuildBlob()),
            };
        }

        private void OnEnable()
        {
            if (HideSystemCursor)
            {
                Cursor.visible = false;
                _hidden = true;
            }
        }

        private void OnDisable()
        {
            // Put it back. A component that switched the pointer off and left it
            // off would make the editor unusable the moment it was disabled.
            if (_hidden)
            {
                Cursor.visible = true;
                _hidden = false;
            }
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
            if (_owned != null) Destroy(_owned);
        }

        private void LateUpdate()
        {
            var camera = Camera.main;
            var mouse = Mouse.current;
            if (_batch == null || _mesh == null || _material == null
                || camera == null || mouse == null)
            {
                return;
            }

            // Kept in step every frame rather than only at enable, so toggling the
            // field in the inspector during play does what it says.
            if (HideSystemCursor != _hidden)
            {
                Cursor.visible = !HideSystemCursor;
                _hidden = HideSystemCursor;
            }

            var screen = mouse.position.ReadValue();
            var world = camera.ScreenToWorldPoint(
                new Vector3(screen.x, screen.y, -camera.transform.position.z));
            var at = new Vector2(world.x, world.y);

            float outer = Radius * camera.orthographicSize;
            float inner = outer * (1f - Mathf.Clamp01(Thickness));

            var colour = Colour;
            float progress = 0f;

            if (VroxNet.Instance is { Ready: true } net && net.Conn is { } conn
                && net.LocalPlayer is { } player && player.WeaponId != 0
                && conn.Db.WeaponDef.Id.Find(player.WeaponId) is { } weapon)
            {
                if (UseWeaponColour)
                {
                    colour = PackedColour.Unpack(weapon.Tint);
                }
                progress = ReloadProgress(player, weapon);
            }

            _batch.Clear();

            var dim = colour; dim.a = Alpha;
            Arc(at, inner, outer, 0f, Mathf.PI * 2f, dim);

            if (progress > 0f)
            {
                var lit = colour; lit.a = FillAlpha;
                Arc(at, inner, outer, 0f, Mathf.PI * 2f * progress, lit);
            }

            _batch.Flush(_mesh, _material);
        }

        /// <summary>
        /// How far through a reload the weapon is, from 0 to 1.
        /// </summary>
        /// <remarks>
        /// Zero when nothing is reloading, which is also what a weapon with no
        /// magazine reports — it never reloads, so it is never partway through
        /// one.
        ///
        /// A shell-at-a-time reload sets the finish time one shell ahead, so the
        /// ring fills once per shell rather than once per magazine. That is the
        /// truth about what the gun is doing and it happens to read well: the
        /// shotgun ticks round six times and you can break off after any of them.
        /// </remarks>
        private static float ReloadProgress(SpacetimeDB.Types.Player player,
                                            SpacetimeDB.Types.WeaponDef weapon)
        {
            if (player.ReloadAtUs == 0 || weapon.ReloadMs == 0)
            {
                return 0f;
            }

            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            long remaining = (long)player.ReloadAtUs - now;
            if (remaining <= 0)
            {
                return 0f;
            }

            float total = weapon.ReloadMs * 1000f;
            return Mathf.Clamp01(1f - remaining / total);
        }

        /// <summary>
        /// A band between two radii, running from one angle to another.
        /// </summary>
        /// <remarks>
        /// Angles are measured from screen up and sweep clockwise, so the fill
        /// starts at the top and goes round the way a clock does however far the
        /// view has been rotated. Built on <see cref="VroxCamera.ScreenAxes"/> for
        /// that reason — in world space the sweep would start somewhere different
        /// every time the camera turned.
        ///
        /// Every vertex samples the middle of the blob, which is its opaque
        /// centre, so the geometry comes out flat and solid. That is what lets the
        /// ring share one material with the soft-edged things without needing a
        /// texture of its own.
        /// </remarks>
        private void Arc(Vector2 centre, float inner, float outer,
                         float from, float to, Color colour)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Segments * Mathf.Abs(to - from) / (Mathf.PI * 2f)));
            VroxCamera.ScreenAxes(out var right, out var up);

            int v = _batch!.VertexCount;
            var middle = new Vector2(0.5f, 0.5f);

            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(from, to, (float)i / steps);
                var dir = up * Mathf.Cos(a) + right * Mathf.Sin(a);

                _batch.Vertex(centre + dir * inner, Depth, middle, colour);
                _batch.Vertex(centre + dir * outer, Depth, middle, colour);
            }

            for (int i = 0; i < steps; i++)
            {
                int a = v + i * 2;
                _batch.Triangle(a, a + 1, a + 3);
                _batch.Triangle(a, a + 3, a + 2);
            }
        }
    }
}
