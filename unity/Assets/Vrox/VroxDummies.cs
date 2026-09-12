using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Draws the practice targets, with hit feedback.
    /// </summary>
    /// <remarks>
    /// Dummies only. Enemies are <see cref="VroxEnemies"/> — the two used to
    /// share this component, which left the renderer for every enemy in the world
    /// called "dummies", and a scene missing it drew nothing with no error to say
    /// so.
    ///
    /// Nothing here decides whether a hit happened — the server does, and this
    /// only reacts to health changing. A client that flashed on its own guess
    /// would flash for shots that missed.
    ///
    /// Targets do not move, are never removed and are never many, so unlike
    /// <see cref="VroxEnemies"/> this needs no easing, no death tween and no
    /// per-id bookkeeping.
    /// </remarks>
    public sealed class VroxDummies : MonoBehaviour
    {
        /// <summary>Behind the projectiles, in front of the ground.</summary>
        /// <remarks>
        /// Terrain draws opaque at +0.25 and writes depth, so this is a real
        /// depth test rather than a sorting hint.
        /// </remarks>
        private const float Depth = -0.5f;

        [Tooltip("How long a target stays lit after being hit, in seconds.")]
        public float FlashSeconds = 0.12f;

        public Color Healthy = new Color(0.55f, 0.55f, 0.62f);
        public Color Hurt = new Color(0.75f, 0.25f, 0.25f);
        public Color Flash = Color.white;

        private Mesh? _mesh;
        private Material? _material;
        private readonly System.Collections.Generic.List<Vector3> _verts = new();
        private readonly System.Collections.Generic.List<Color> _colors = new();
        private readonly System.Collections.Generic.List<int> _tris = new();

        private void Awake()
        {
            _mesh = new Mesh { name = "Vrox Dummies" };
            _mesh.MarkDynamic();

            // Vertex colours, because each target is tinted by its own health and
            // flash. A single material colour could not say that.
            _material = new Material(Shader.Find("Sprites/Default"));
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }

        private void LateUpdate()
        {
            var net = VroxNet.Instance;
            if (net == null || !net.Ready || net.Conn is not { } conn
                || _mesh == null || _material == null)
            {
                return;
            }

            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

            _verts.Clear();
            _colors.Clear();
            _tris.Clear();

            foreach (var dummy in conn.Db.Dummy.Iter())
            {
                float health = dummy.MaxHp > 0 ? (float)dummy.Hp / dummy.MaxHp : 1f;
                var colour = Color.Lerp(Hurt, Healthy, health);
                Quad(dummy.X, dummy.Y, dummy.Radius,
                     WithFlash(colour, dummy.LastHitAt.MicrosecondsSinceUnixEpoch, now));
            }

            _mesh.Clear();
            if (_verts.Count == 0)
            {
                return;
            }

            _mesh.SetVertices(_verts);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_tris, 0, calculateBounds: true);
            Graphics.RenderMesh(new RenderParams(_material), _mesh, 0, Matrix4x4.identity);
        }

        /// <summary>Lightens a colour briefly after a hit.</summary>
        /// <remarks>
        /// Driven entirely by the server's timestamp. A client that flashed on its
        /// own guess would flash for shots that missed.
        /// </remarks>
        private Color WithFlash(Color colour, long lastHitUs, long nowUs)
        {
            float since = (nowUs - lastHitUs) / 1_000_000f;
            if (since < 0f || since >= FlashSeconds)
            {
                return colour;
            }
            // Eased out rather than switched off, so rapid fire reads as a
            // continuous glow instead of a strobe.
            return Color.Lerp(Flash, colour, since / FlashSeconds);
        }

        /// <summary>Adds one square, sized to the collision radius so what you see is what you hit.</summary>
        private void Quad(float x, float y, float radius, Color colour)
        {
            int v = _verts.Count;
            // Screen axes, so a target keeps facing the player as the view turns.
            VroxCamera.ScreenAxes(out var right, out var up);
            Vector2 rw = right * radius;
            Vector2 uw = up * radius;

            _verts.Add(new Vector3(x - rw.x - uw.x, y - rw.y - uw.y, Depth));
            _verts.Add(new Vector3(x - rw.x + uw.x, y - rw.y + uw.y, Depth));
            _verts.Add(new Vector3(x + rw.x + uw.x, y + rw.y + uw.y, Depth));
            _verts.Add(new Vector3(x + rw.x - uw.x, y + rw.y - uw.y, Depth));

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
