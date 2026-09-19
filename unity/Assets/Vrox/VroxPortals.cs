using UnityEngine;
using UnityEngine.InputSystem;

namespace Vrox
{
    /// <summary>
    /// Draws portals, and lets the player step through the nearest one.
    /// </summary>
    /// <remarks>
    /// Only portals in the player's own zone ever reach this client, because the
    /// portal subscription is filtered by zone — so there is no zone test here,
    /// and adding one would be a second copy of a rule the server already applies.
    ///
    /// Pressing the key only <em>asks</em>. Whether the player is close enough,
    /// in the right zone and not looting is decided by the reducer against the
    /// server's position, and the zone switch is noticed by <see cref="VroxNet"/>
    /// when the player row's zone changes — never by remembering that we pressed
    /// the key.
    /// </remarks>
    public sealed class VroxPortals : MonoBehaviour
    {
        /// <summary>Behind shots, beside enemies, in front of terrain.</summary>
        private const float Depth = -0.4f;

        [Tooltip("How close a portal must be to be offered, in tiles. The server checks " +
                 "its own, smaller-or-equal range, so raising this grants nothing.")]
        [Range(0.5f, 4f)]
        public float Range = 1.5f;

        [Tooltip("Key that steps through the nearest portal.")]
        public Key EnterKey = Key.G;

        [Tooltip("Half-width of a portal, in tiles.")]
        public float Radius = 0.7f;

        [Tooltip("Colour of an exit, which leads back to the realm.")]
        public Color ExitColour = new Color(0.85f, 0.95f, 1f);

        [Tooltip("Colour of an entrance whose dungeon layout has not replicated yet.")]
        public Color UnknownColour = Color.magenta;

        /// <summary>The portal currently in reach, or 0.</summary>
        public ulong Offered { get; private set; }

        private Mesh? _mesh;
        private Material? _material;
        private readonly System.Collections.Generic.List<Vector3> _verts = new();
        private readonly System.Collections.Generic.List<Color> _colors = new();
        private readonly System.Collections.Generic.List<int> _tris = new();

        private void Awake()
        {
            _mesh = new Mesh { name = "Vrox Portals" };
            _mesh.MarkDynamic();
            _material = new Material(Shader.Find("Sprites/Default"));
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }

        private void LateUpdate()
        {
            Offered = 0;
            var net = VroxNet.Instance;
            if (net == null || !net.Ready || net.Conn is not { } conn
                || _mesh == null || _material == null)
            {
                return;
            }

            var player = net.LocalPlayer;
            float best = Range * Range;
            foreach (var portal in conn.Db.Portal.Iter())
            {
                if (player is { } p)
                {
                    float dx = p.X - portal.X;
                    float dy = p.Y - portal.Y;
                    float d = dx * dx + dy * dy;
                    if (d <= best)
                    {
                        best = d;
                        Offered = portal.Id;
                    }
                }
            }

            _verts.Clear();
            _colors.Clear();
            _tris.Clear();

            float pulse = 0.85f + 0.15f * Mathf.Sin(Time.time * 4f);
            foreach (var portal in conn.Db.Portal.Iter())
            {
                // An entrance whose layout row has not arrived is drawn in a colour
                // nobody authors, rather than skipped: "not told yet" must not look
                // like "no portal here".
                Color colour = portal.Kind == 1
                    ? ExitColour
                    : conn.Db.DungeonLayout.Id.Find(portal.LayoutId) is { } layout
                        ? PackedColour.Unpack(layout.Tint)
                        : UnknownColour;
                colour.a = 1f;

                bool offered = portal.Id == Offered;
                float size = Radius * (offered ? 1.25f : 1f) * pulse;
                Diamond(portal.X, portal.Y, size, offered ? Color.Lerp(colour, Color.white, 0.35f) : colour);
            }

            _mesh.Clear();
            if (_verts.Count > 0)
            {
                _mesh.SetVertices(_verts);
                _mesh.SetColors(_colors);
                _mesh.SetTriangles(_tris, 0, calculateBounds: true);
                Graphics.RenderMesh(new RenderParams(_material), _mesh, 0, Matrix4x4.identity);
            }

            // Nothing offered while a bag is open: the loot screen owns the keyboard.
            if (Offered != 0 && player is { LootingBag: 0 }
                && Keyboard.current is { } keyboard && keyboard[EnterKey].wasPressedThisFrame)
            {
                conn.Reducers.EnterPortal(Offered);
            }
        }

        /// <summary>A square stood on its corner, in screen axes so it stays upright as the view turns.</summary>
        private void Diamond(float x, float y, float size, Color colour)
        {
            VroxCamera.ScreenAxes(out var right, out var up);
            Vector2 r = right * size;
            Vector2 u = up * size;

            int v = _verts.Count;
            _verts.Add(new Vector3(x - r.x, y - r.y, Depth));
            _verts.Add(new Vector3(x + u.x, y + u.y, Depth));
            _verts.Add(new Vector3(x + r.x, y + r.y, Depth));
            _verts.Add(new Vector3(x - u.x, y - u.y, Depth));
            for (int i = 0; i < 4; i++)
            {
                _colors.Add(colour);
            }
            _tris.Add(v);
            _tris.Add(v + 1);
            _tris.Add(v + 2);
            _tris.Add(v);
            _tris.Add(v + 2);
            _tris.Add(v + 3);
        }
    }
}
