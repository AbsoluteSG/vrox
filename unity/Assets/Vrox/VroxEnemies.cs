using UnityEngine;
using Vrox.Equipment;

namespace Vrox
{
    /// <summary>
    /// Draws the server's enemies, with hit flashes, easing and a death tween.
    /// </summary>
    /// <remarks>
    /// Nothing here decides whether a hit happened, or whether an enemy died —
    /// the server does, and this only reacts to rows changing. A client that
    /// flashed on its own guess would flash for shots that missed.
    ///
    /// One mesh for all of them, like the projectiles. A GameObject each would be
    /// per-frame transform, culling and batching work for what is a coloured
    /// square, and a realm holds hundreds at once.
    ///
    /// Separate from <see cref="VroxDummies"/> on purpose. The two used to share
    /// a component, which meant the thing that drew every enemy in the world was
    /// called "dummies" — and a scene built by hand simply never got it, with no
    /// error and nothing on screen to say why.
    /// </remarks>
    public sealed class VroxEnemies : MonoBehaviour
    {
        /// <summary>Behind the projectiles, in front of the ground.</summary>
        /// <remarks>
        /// Terrain draws opaque at +0.25 and writes depth, so this is a real
        /// depth test rather than a sorting hint.
        /// </remarks>
        private const float Depth = -0.5f;

        [Tooltip("How long an enemy stays lit after being hit, in seconds.")]
        public float FlashSeconds = 0.12f;

        [Header("Movement")]
        [Tooltip("Ease enemies toward their server position. They only move on the " +
                 "server's 20 Hz tick, so drawing the raw value shows several " +
                 "identical frames and then a jump. Turn off to see exactly what " +
                 "the server said.")]
        public bool Smooth = true;

        [Tooltip("Higher follows the server more tightly. Matches the player's setting.")]
        public float SmoothLambda = 20f;

        [Tooltip("Jumps further than this are not eased across. A respawn or a teleport " +
                 "is not motion, and gliding through it tours the map on the way.")]
        public float SnapDistance = 6f;

        [Header("Death")]
        [Tooltip("How long an enemy takes to shrink away once killed.")]
        public float DeathSeconds = 0.22f;

        [Header("Damage numbers")]
        [Tooltip("Raise a number for every hit. Drawn by DamageNumbers, which must be in " +
                 "the scene. One number per pellet, so a shotgun reads as a shotgun.")]
        public bool ShowDamage = true;

        [Tooltip("How far above the hit a number starts, in tiles. A flat lift rather than " +
                 "the target's radius: a killing pellet deletes the enemy row inside the " +
                 "same call that reported the hit, so there is often no body left to " +
                 "measure by the time the number is drawn.")]
        [Range(0f, 3f)]
        public float NumberLift = 0.7f;

        [Header("Colour")]
        [Tooltip("Used only for an enemy whose definition has not reached this client yet. " +
                 "Every enemy's real colour is the Tint on its asset, pushed to the server, " +
                 "so all players see the same thing. If everything on screen is this " +
                 "colour, the catalogue has not been pushed.")]
        public Color FallbackColour = new Color(0.85f, 0.35f, 0.35f);

        public Color Flash = Color.white;

        [Header("Art")]
        [Tooltip("Supplies each enemy's sprite by def id. Leave empty and every enemy " +
                 "draws as a coloured square, which is how they have always drawn.")]
        public EnemyCatalogue? Catalogue;

        /// <summary>An enemy that has been removed and is still shrinking away.</summary>
        private struct Dying
        {
            public float X, Y, Radius, StartedAt;
            public Color Colour;

            /// <summary>Kept so a corpse shrinks as itself rather than as a box.</summary>
            public Sprite? Sprite;
        }

        private readonly System.Collections.Generic.Dictionary<ulong, Dying> _alive = new();

        /// <summary>The zone the bookkeeping below belongs to.</summary>
        private uint _zone;

        /// <summary>Where each enemy is currently drawn, as opposed to where it is.</summary>
        private readonly System.Collections.Generic.Dictionary<ulong, Vector2> _drawn = new();
        private readonly System.Collections.Generic.List<Dying> _dying = new();
        private readonly System.Collections.Generic.HashSet<ulong> _seen = new();

        /// <summary>Per-enemy debuff expiries, to notice a fresh application.</summary>
        /// <remarks>
        /// Keyed by enemy id and dropped with the enemy in <see cref="RetireMissing"/>.
        /// Without that it grows for the lifetime of the session — a realm churns
        /// through thousands of enemies, and a dictionary nobody empties is a leak
        /// that only shows up in a long session, which is the hardest kind to
        /// catch.
        /// </remarks>
        private readonly System.Collections.Generic.Dictionary<ulong, (long Stun, long Slow)>
            _debuffs = new();

        private Mesh? _mesh;
        private Material? _material;
        private readonly System.Collections.Generic.List<Vector3> _verts = new();
        private readonly System.Collections.Generic.List<Color> _colors = new();
        private readonly System.Collections.Generic.List<int> _tris = new();

        // A second mesh for the sprited ones. A mesh carries one texture, so
        // plain squares and sprites cannot share it — but two meshes is two draw
        // calls for the whole realm, where a GameObject each would be hundreds.
        private Mesh? _spriteMesh;
        private Material? _spriteMaterial;
        private readonly System.Collections.Generic.List<Vector3> _spriteVerts = new();
        private readonly System.Collections.Generic.List<Vector2> _spriteUvs = new();
        private readonly System.Collections.Generic.List<Color> _spriteColors = new();
        private readonly System.Collections.Generic.List<int> _spriteTris = new();

        private void Awake()
        {
            _mesh = new Mesh { name = "Vrox Enemies" };
            _mesh.MarkDynamic();

            // Vertex colours, because each enemy is tinted by its own health and
            // flash. A single material colour could not say that.
            _material = new Material(Shader.Find("Sprites/Default"));

            _spriteMesh = new Mesh { name = "Vrox Enemy Sprites" };
            _spriteMesh.MarkDynamic();
            _spriteMaterial = new Material(Shader.Find("Sprites/Default"));
        }

        /// <summary>The connection whose hit events are currently hooked.</summary>
        /// <remarks>
        /// Held so a reconnect re-hooks rather than going quiet. Comparing against
        /// the live connection each frame is reconciling against replicated state;
        /// a bool saying "subscribed once" would be a latch that survived exactly
        /// one disconnect.
        /// </remarks>
        private SpacetimeDB.Types.DbConnection? _hooked;

        /// <summary>
        /// Raises a number for one hit.
        /// </summary>
        /// <remarks>
        /// Driven by the <c>hit</c> event table rather than by watching the enemy
        /// row, because the enemy row cannot express a volley. Eight pellets land
        /// inside one reducer call and share one timestamp, so they overwrite the
        /// same LastDamage and LastHitAt and arrive as a single update carrying
        /// the last pellet's number. Reading that, a shotgun looked like a weak
        /// pistol — one small number for what was really eight.
        ///
        /// The position comes off the event rather than from the enemy, so a
        /// killing pellet still shows its damage: the row it refers to has already
        /// been deleted by the time this runs.
        /// </remarks>
        private void OnHit(SpacetimeDB.Types.EventContext ctx, SpacetimeDB.Types.Hit hit)
        {
            if (!ShowDamage || hit.Amount == 0)
            {
                return;
            }
            DamageNumbers.Show(hit.X, hit.Y + NumberLift, hit.Amount, incoming: false);
        }

        private void Hook(SpacetimeDB.Types.DbConnection conn)
        {
            if (ReferenceEquals(_hooked, conn))
            {
                return;
            }
            if (_hooked != null)
            {
                _hooked.Db.Hit.OnInsert -= OnHit;
            }
            conn.Db.Hit.OnInsert += OnHit;
            _hooked = conn;
        }

        private void OnDestroy()
        {
            if (_hooked != null)
            {
                _hooked.Db.Hit.OnInsert -= OnHit;
                _hooked = null;
            }
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
            if (_spriteMesh != null) Destroy(_spriteMesh);
            if (_spriteMaterial != null) Destroy(_spriteMaterial);
        }

        private void LateUpdate()
        {
            var net = VroxNet.Instance;
            if (net == null || !net.Ready || net.Conn is not { } conn
                || _mesh == null || _material == null
                || _spriteMesh == null || _spriteMaterial == null)
            {
                return;
            }

            Hook(conn);

            // A zone change removes every enemy at once. Retired normally, the whole
            // previous zone would shrink away on top of the new one. Rows from the
            // old zone can also linger for a frame after the switch, until the old
            // subscription is dropped, so they are skipped below rather than drawn.
            uint zone = net.SubscribedZone;
            if (zone != _zone)
            {
                _zone = zone;
                _alive.Clear();
                _dying.Clear();
                _drawn.Clear();
                _debuffs.Clear();
            }

            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

            _verts.Clear();
            _colors.Clear();
            _tris.Clear();
            _spriteVerts.Clear();
            _spriteUvs.Clear();
            _spriteColors.Clear();
            _spriteTris.Clear();
            _seen.Clear();

            // Resolved once a frame, not per enemy: the catalogue walks its whole
            // list to check the sprites share a sheet, and doing that per row
            // would be hundreds of scans a frame.
            _spriteMaterial.mainTexture = Catalogue != null ? Catalogue.SharedTexture() : null;

            foreach (var enemy in conn.Db.Enemy.Iter())
            {
                if (enemy.ZoneId != zone)
                {
                    continue;
                }
                var def = conn.Db.EnemyDef.Id.Find(enemy.DefId);

                // Drawn at a default size when the definition has not arrived
                // rather than skipped. "The client has not been told yet" and
                // "there is no such enemy" are indistinguishable here, and
                // skipping makes the first look like the second — an enemy that
                // shoots you from nowhere.
                float radius = def?.Radius ?? 0.5f;
                float max = def is { } d && d.MaxHp > 0 ? d.MaxHp : 1f;

                // The asset's colour, by way of the server. The component's own
                // is reached only when the definition row has not arrived — which
                // is a knowable state, unlike "the colour looks empty", so there
                // is no sentinel value here and an asset may legitimately author
                // black.
                var basis = def is { } withColour
                    ? PackedColour.Unpack(withColour.Colour)
                    : FallbackColour;

                // Darkened as it weakens rather than recoloured, so a wounded
                // enemy still reads as its own archetype.
                var colour = basis * Mathf.Lerp(0.45f, 1f, enemy.Hp / max);
                colour.a = 1f;

                _seen.Add(enemy.Id);

                var target = new Vector2(enemy.X, enemy.Y);
                var shown = Smoothed(enemy.Id, target);

                // Remembered every frame so that when the row disappears there is
                // still something to animate. The server deletes an enemy the
                // moment it dies; without this the client has nothing left to
                // shrink and they would simply blink out.
                //
                // The drawn position, not the true one, so the corpse starts
                // exactly where the enemy appeared to be rather than jumping
                // forward at the moment of death.
                // Null when the catalogue has no entry, which draws the square
                // this has always drawn. Not a fallback hiding a failure: an
                // unlisted enemy is a knowable state and looks exactly like the
                // enemy did before any art existed.
                var sprite = Catalogue != null ? Catalogue.For(enemy.DefId)?.Sprite : null;

                _alive[enemy.Id] = new Dying
                {
                    X = shown.x, Y = shown.y, Radius = radius, Colour = colour,
                    Sprite = sprite,
                };

                // LastHitAt still drives the flash, which is per-enemy-per-frame
                // state and so is correctly collapsed. The *numbers* come from
                // the hit event instead — see OnHit for why.
                long hitAt = enemy.LastHitAt.MicrosecondsSinceUnixEpoch;

                RaiseStatusText(enemy, shown.x, shown.y + NumberLift);

                Quad(shown.x, shown.y, radius, WithFlash(colour, hitAt, now), sprite);
            }

            RetireMissing();
            DrawDying();

            _mesh.Clear();
            if (_verts.Count > 0)
            {
                _mesh.SetVertices(_verts);
                _mesh.SetColors(_colors);
                _mesh.SetTriangles(_tris, 0, calculateBounds: true);
                Graphics.RenderMesh(new RenderParams(_material), _mesh, 0, Matrix4x4.identity);
            }

            _spriteMesh.Clear();
            if (_spriteVerts.Count > 0)
            {
                _spriteMesh.SetVertices(_spriteVerts);
                _spriteMesh.SetUVs(0, _spriteUvs);
                _spriteMesh.SetColors(_spriteColors);
                _spriteMesh.SetTriangles(_spriteTris, 0, calculateBounds: true);
                Graphics.RenderMesh(
                    new RenderParams(_spriteMaterial), _spriteMesh, 0, Matrix4x4.identity);
            }
        }

        /// <summary>
        /// Eases an enemy toward where the server says it is.
        /// </summary>
        /// <remarks>
        /// The same <c>1 - e^(-lambda*dt)</c> curve the player uses, and for the
        /// same reason: a fixed fraction per frame would ease several times faster
        /// at 300 fps than at 60, so how the game moved would depend on the
        /// hardware.
        ///
        /// This trails the true position slightly — about a fifth of a tile at
        /// walking pace — which costs nothing, because collision is decided by the
        /// server against the real position and never against what is drawn.
        /// </remarks>
        private Vector2 Smoothed(ulong id, Vector2 target)
        {
            if (!Smooth)
            {
                _drawn[id] = target;
                return target;
            }

            // First sighting starts where it is. Easing in from wherever the
            // dictionary happened to be empty would fly it in from the origin.
            if (!_drawn.TryGetValue(id, out var current))
            {
                _drawn[id] = target;
                return target;
            }

            if ((target - current).sqrMagnitude > SnapDistance * SnapDistance)
            {
                _drawn[id] = target;
                return target;
            }

            float alpha = 1f - Mathf.Exp(-SmoothLambda * Time.deltaTime);
            var eased = Vector2.Lerp(current, target, alpha);
            _drawn[id] = eased;
            return eased;
        }

        /// <summary>
        /// Calls out a debuff the moment this enemy takes one.
        /// </summary>
        /// <remarks>
        /// Driven by the expiry moving later, not by the debuff being active — the
        /// same rule the player's copy uses, and for the same reason: active would
        /// mean a word every frame for the whole duration.
        ///
        /// An enemy seen for the first time records its expiries without
        /// announcing them. Walking into range of something already stunned is not
        /// a stun landing, and saying so would put a word over every mob in a
        /// fight already in progress.
        ///
        /// Armour break is not among these. It strips flat defence, enemies have
        /// none, and the server refuses to apply it to them — so a word here would
        /// promise an effect that is not happening.
        /// </remarks>
        private void RaiseStatusText(SpacetimeDB.Types.Enemy enemy, float x, float y)
        {
            long stun = enemy.StunnedUntil.MicrosecondsSinceUnixEpoch;
            long slow = enemy.SlowedUntil.MicrosecondsSinceUnixEpoch;

            if (_debuffs.TryGetValue(enemy.Id, out var was))
            {
                if (stun > was.Stun)
                {
                    DamageNumbers.ShowStatus(x, y, Vrox.Equipment.DebuffKind.Stun);
                }
                if (slow > was.Slow)
                {
                    DamageNumbers.ShowStatus(x, y, Vrox.Equipment.DebuffKind.Slow);
                }
            }
            _debuffs[enemy.Id] = (stun, slow);
        }

        /// <summary>Moves anything the server stopped reporting into the dying list.</summary>
        private void RetireMissing()
        {
            if (_alive.Count == _seen.Count)
            {
                return;
            }

            // Collected before removing: mutating the dictionary while walking it
            // throws, and the ones that vanished are exactly what is wanted.
            var gone = new System.Collections.Generic.List<ulong>();
            foreach (var pair in _alive)
            {
                if (!_seen.Contains(pair.Key))
                {
                    gone.Add(pair.Key);
                }
            }
            foreach (ulong id in gone)
            {
                _debuffs.Remove(id);
                var record = _alive[id];
                record.StartedAt = Time.time;
                _dying.Add(record);
                _alive.Remove(id);
                // Dropped too, or an id reused by a later enemy would ease in from
                // wherever the previous one died.
                _drawn.Remove(id);
            }
        }

        /// <summary>
        /// Shrinks the recently dead away.
        /// </summary>
        /// <remarks>
        /// Eased so the collapse is quick at the start and settles at the end,
        /// which reads as a thing being destroyed rather than a thing being
        /// scaled. Everything here is client-side: the enemy is already gone
        /// server-side and nothing can be hit during it.
        /// </remarks>
        private void DrawDying()
        {
            for (int i = _dying.Count - 1; i >= 0; i--)
            {
                float progress = (Time.time - _dying[i].StartedAt) / Mathf.Max(0.01f, DeathSeconds);
                if (progress >= 1f)
                {
                    _dying.RemoveAt(i);
                    continue;
                }

                float scale = 1f - progress * progress;
                var colour = _dying[i].Colour;
                colour.a = 1f - progress;
                Quad(_dying[i].X, _dying[i].Y, _dying[i].Radius * scale, colour, _dying[i].Sprite);
            }
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

        /// <summary>
        /// Adds one enemy, sized to the collision radius so what you see is what
        /// you hit.
        /// </summary>
        /// <remarks>
        /// Goes into the plain mesh or the sprite mesh depending on whether there
        /// is art. The colour is multiplied into the sprite either way, so health
        /// darkening, the hit flash and the death fade behave identically whether
        /// an enemy has been given a sprite or not.
        ///
        /// The height stays the collision radius and only the width follows the
        /// sprite's aspect. Fitting the whole sprite inside the radius instead
        /// would make a tall sprite draw smaller than the thing it can be hit on,
        /// and the hitbox is the part that has to stay honest.
        /// </remarks>
        private void Quad(float x, float y, float radius, Color colour, Sprite? sprite)
        {
            // Screen axes rather than world ones, so an enemy keeps facing the
            // player as the view turns. Only the corners are rotated — the centre
            // is still exactly where the server says the enemy is, so what you
            // see remains what you hit.
            VroxCamera.ScreenAxes(out var right, out var up);

            if (sprite == null)
            {
                Vector2 rp = right * radius;
                Vector2 upp = up * radius;

                int v = _verts.Count;
                _verts.Add(new Vector3(x - rp.x - upp.x, y - rp.y - upp.y, Depth));
                _verts.Add(new Vector3(x - rp.x + upp.x, y - rp.y + upp.y, Depth));
                _verts.Add(new Vector3(x + rp.x + upp.x, y + rp.y + upp.y, Depth));
                _verts.Add(new Vector3(x + rp.x - upp.x, y + rp.y - upp.y, Depth));

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
                return;
            }

            var rect = sprite.textureRect;
            var texture = sprite.texture;
            float halfHeight = radius;
            float halfWidth = rect.height > 0f ? radius * (rect.width / rect.height) : radius;

            // From the texture rect rather than sprite.uv, whose vertex order
            // follows the sprite's own mesh and does not have to match the corners
            // emitted here.
            float uMin = rect.xMin / texture.width;
            float uMax = rect.xMax / texture.width;
            float vMin = rect.yMin / texture.height;
            float vMax = rect.yMax / texture.height;

            Vector2 rw = right * halfWidth;
            Vector2 uw = up * halfHeight;

            int s = _spriteVerts.Count;
            _spriteVerts.Add(new Vector3(x - rw.x - uw.x, y - rw.y - uw.y, Depth));
            _spriteVerts.Add(new Vector3(x - rw.x + uw.x, y - rw.y + uw.y, Depth));
            _spriteVerts.Add(new Vector3(x + rw.x + uw.x, y + rw.y + uw.y, Depth));
            _spriteVerts.Add(new Vector3(x + rw.x - uw.x, y + rw.y - uw.y, Depth));

            _spriteUvs.Add(new Vector2(uMin, vMin));
            _spriteUvs.Add(new Vector2(uMin, vMax));
            _spriteUvs.Add(new Vector2(uMax, vMax));
            _spriteUvs.Add(new Vector2(uMax, vMin));

            for (int i = 0; i < 4; i++)
            {
                _spriteColors.Add(colour);
            }

            _spriteTris.Add(s);
            _spriteTris.Add(s + 1);
            _spriteTris.Add(s + 2);
            _spriteTris.Add(s);
            _spriteTris.Add(s + 2);
            _spriteTris.Add(s + 3);
        }
    }
}
