using System.Collections.Generic;
using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Draws the lines hitscan weapons leave behind.
    /// </summary>
    /// <remarks>
    /// A tracer is not a thing in the world — by the time it is drawn the shot has
    /// already hit or missed. So this reads a row saying a line existed between
    /// two points and animates it, rather than simulating anything.
    ///
    /// <b>The far end comes from the server.</b> Drawing to where the client
    /// thinks a ray ended would be the client answering "what did I hit", which is
    /// the question the server exists to settle — and the line would disagree with
    /// the damage on anyone else's screen. That end is never touched here.
    ///
    /// The near end is moved, and only for looks. The server writes a shot's
    /// origin at the shooter's centre, so drawn literally a tracer comes out of
    /// the player's chest; <see cref="VroxMuzzle"/> pushes it forward to the edge
    /// of the sprite so it leaves the gun instead. Nothing downstream reads it —
    /// the shot was resolved before the row was written — and it has to match what
    /// <see cref="VroxMuzzleFlash"/> does, or the flash and the line it belongs to
    /// start in different places.
    ///
    /// <b>The travel is a lie, and a deliberate one.</b> A hitscan round arrives
    /// instantly, so drawing the whole line at once and fading it is technically
    /// honest and reads as a flicker with no direction to it. Giving the streak a
    /// few hundredths of a second to cross gives the eye something to follow, and
    /// the shot reads as leaving the gun rather than as a line appearing. Nothing
    /// downstream depends on it: the damage was resolved server-side before the
    /// row was written.
    ///
    /// Pooled <see cref="LineRenderer"/>s rather than one mesh, unlike the
    /// projectile renderer. There are a handful of these at a time, each is two
    /// points, and a line's width and fade are what a LineRenderer is for.
    ///
    /// Each tracer is two lines: a wide, dim one in the weapon's colour and a
    /// thin, near-white one down the middle of it. That pairing is what reads as
    /// hot — a single line of one colour reads as a drawn stroke no matter how
    /// bright it is. Same construction as the bullets in <see cref="VroxShots"/>,
    /// so hitscan and projectile weapons look like they belong to one game.
    ///
    /// Alignment stays on View. This game's camera rotates, and a line aligned to
    /// its own transform would tilt with the world instead of facing the player.
    /// </remarks>
    public sealed class VroxTracers : MonoBehaviour
    {
        /// <summary>In front of the ground, behind actors.</summary>
        private const float Depth = -0.45f;

        [Header("Timing")]
        [Tooltip("Seconds the streak takes to cross from muzzle to impact. Small — this " +
                 "is a legibility lie, and a big value turns a bullet into a thrown rock.")]
        [Range(0f, 0.3f)]
        public float TravelSeconds = 0.045f;

        [Tooltip("How long the streak takes to collapse and fade once it has arrived. " +
                 "The server drops the row after about 0.15s, so anything longer keeps " +
                 "drawing a line whose row has gone — which is fine, and is why the fade " +
                 "is owned here.")]
        [Range(0.02f, 0.6f)]
        public float FadeSeconds = 0.09f;

        [Tooltip("Random variation in this tracer's timings, as a fraction. Eight pellets " +
                 "fading in lockstep look like one object; eight fading independently look " +
                 "like buckshot.")]
        [Range(0f, 0.8f)]
        public float Variance = 0.3f;

        [Header("Shape")]
        [Tooltip("Length of the visible streak, as a fraction of the whole path. 1 draws " +
                 "the entire line at once and gives up the travel entirely.")]
        [Range(0.05f, 1f)]
        public float StreakFraction = 0.45f;

        [Tooltip("Width of the coloured halo at the head, in tiles.")]
        [Range(0.01f, 0.5f)]
        public float Width = 0.1f;

        [Tooltip("Width of the white-hot filament, as a fraction of the halo. Thin: the " +
                 "halo is what you see and the core is what makes it look lit.")]
        [Range(0.05f, 1f)]
        public float CoreWidth = 0.3f;

        [Tooltip("Width at the tail, as a fraction of the width at the head. A streak that " +
                 "narrows behind itself reads as motion.")]
        [Range(0f, 1f)]
        public float TailWidth = 0.25f;

        [Header("Colour")]
        [Tooltip("How far the filament burns towards white. The halo keeps the weapon's " +
                 "authored colour; only the centre goes hot.")]
        [Range(0f, 1f)]
        public float CoreWhite = 0.8f;

        [Tooltip("Opacity of the coloured halo around the filament.")]
        [Range(0f, 1f)]
        public float HaloAlpha = 0.55f;

        [Header("Placement")]
        [Tooltip("How far down the barrel the line starts, as a multiple of the shooter's " +
                 "radius. Keep this and the muzzle flash's own Offset in step: they are " +
                 "drawing the same event, and a flash that floats off the end of its " +
                 "tracer is worse than either being slightly wrong.")]
        [Range(0f, 3f)]
        public float Offset = 1.05f;

        [Tooltip("Radius to use when there is no player sprite to measure, in tiles.")]
        [Range(0f, 3f)]
        public float FallbackRadius = 0.5f;

        [Header("Look")]
        [Tooltip("Optional. The soft-edged band the lines are drawn with. Wants to be " +
                 "soft top to bottom and even left to right — a LineRenderer runs U along " +
                 "the line and V across it, so anything that varies along U fades the " +
                 "tracer somewhere down its length. Leave empty and one is generated, " +
                 "which is the intended setup. Ignored when a Material is assigned.")]
        public Texture2D? StreakTexture;

        [Tooltip("Material for the lines. Leave empty to build an unlit one with a " +
                 "soft-edged streak texture, which is the intended setup.")]
        public Material? LineMaterial;

        /// <summary>The two renderers one tracer is drawn with.</summary>
        private struct Pair
        {
            public LineRenderer Halo;
            public LineRenderer Core;
        }

        /// <summary>A tracer being animated, whether or not its row still exists.</summary>
        private struct Live
        {
            public Pair Lines;
            public Vector3 Start;
            public Vector3 End;
            public Color Colour;
            public float StartedAt;
            public float Travel;
            public float Fade;
            public float Scale;
        }

        private readonly List<Live> _playing = new();
        private readonly HashSet<ulong> _started = new();
        private readonly HashSet<ulong> _present = new();
        private readonly Stack<Pair> _pool = new();
        private Material? _owned;
        private Texture2D? _ownedTexture;

        private void OnDestroy()
        {
            if (_owned != null) Destroy(_owned);
            if (_ownedTexture != null) Destroy(_ownedTexture);
        }

        private void LateUpdate()
        {
            if (VroxNet.Instance?.Conn is not { } conn)
            {
                return;
            }

            _present.Clear();

            foreach (var tracer in conn.Db.Tracer.Iter())
            {
                _present.Add(tracer.Id);
                if (!_started.Add(tracer.Id))
                {
                    continue;
                }

                // Read once, on the frame the row first appears, and then left to
                // play out. Re-reading it every frame would be re-reading a line
                // that cannot have moved — the shot is already over.
                var lines = Take();
                float scale = Variance <= 0f ? 1f : Random.Range(1f - Variance, 1f + Variance);

                // Every tracer row is a player's: hitscan is fired from
                // FireHitscan, which takes a Player. So the shooter's radius is
                // always the one measured off the player sprite.
                var muzzle = VroxMuzzle.Point(
                    new Vector2(tracer.X1, tracer.Y1),
                    new Vector2(tracer.X2 - tracer.X1, tracer.Y2 - tracer.Y1),
                    VroxMuzzle.PlayerRadius(FallbackRadius) * Offset);

                _playing.Add(new Live
                {
                    Lines = lines,
                    Start = new Vector3(muzzle.x, muzzle.y, Depth),
                    End = new Vector3(tracer.X2, tracer.Y2, Depth),
                    Colour = PackedColour.Unpack(tracer.Tint),
                    StartedAt = Time.time,
                    Travel = TravelSeconds * scale,
                    Fade = Mathf.Max(0.001f, FadeSeconds * scale),
                    Scale = scale,
                });
            }

            // The row going away does not end the animation. The server drops
            // tracers quickly to keep the table small, and the streak outlives
            // that on purpose — otherwise a fast weapon would strobe. Ids are not
            // reused, so forgetting the ones whose rows have gone is only
            // housekeeping: without it this set grows for the whole session.
            _started.IntersectWith(_present);

            Animate();
        }

        /// <summary>
        /// Moves every playing streak on by a frame.
        /// </summary>
        /// <remarks>
        /// While travelling, the head runs ahead and the tail follows a fixed
        /// fraction behind. Once the head arrives it stops and the tail keeps
        /// going, so the streak shortens *into* the impact point rather than
        /// fading in place — which reads as the round landing rather than as a
        /// light switching off.
        /// </remarks>
        private void Animate()
        {
            for (int i = _playing.Count - 1; i >= 0; i--)
            {
                var live = _playing[i];
                float age = Time.time - live.StartedAt;
                float lifetime = live.Travel + live.Fade;

                if (age >= lifetime || live.Lines.Halo == null || live.Lines.Core == null)
                {
                    if (live.Lines.Halo != null)
                    {
                        Release(live.Lines);
                    }
                    _playing.RemoveAt(i);
                    continue;
                }

                float headT;
                float tailT;
                float alpha;

                if (live.Travel > 0f && age < live.Travel)
                {
                    headT = age / live.Travel;
                    tailT = Mathf.Max(0f, headT - StreakFraction);
                    alpha = 1f;
                }
                else
                {
                    float fade = Mathf.Clamp01((age - live.Travel) / live.Fade);
                    headT = 1f;
                    tailT = Mathf.Lerp(Mathf.Max(0f, 1f - StreakFraction), 1f, fade);

                    // Falls away fast rather than linearly. A linear fade reads as
                    // a fading stick; this reads as a flash leaving an afterimage,
                    // which is what a tracer actually looks like.
                    alpha = (1f - fade) * (1f - fade);
                }

                var tail = Vector3.Lerp(live.Start, live.End, tailT);
                var head = Vector3.Lerp(live.Start, live.End, headT);

                float width = Width * live.Scale;
                var halo = live.Colour;
                halo.a = alpha * HaloAlpha;
                Apply(live.Lines.Halo, tail, head, width, halo);

                var core = Color.Lerp(live.Colour, Color.white, CoreWhite);
                core.a = alpha;
                Apply(live.Lines.Core, tail, head, width * CoreWidth, core);

                _playing[i] = live;
            }
        }

        /// <summary>
        /// Positions one line and sets its width and colour.
        /// </summary>
        /// <remarks>
        /// Position 0 is the tail and position 1 is the head, so the widths run
        /// thin-to-thick rather than the other way round. Tapering towards the
        /// impact would be tapering towards the end the round is travelling
        /// *into*, which reads backwards.
        /// </remarks>
        private void Apply(LineRenderer line, Vector3 tail, Vector3 head, float width, Color colour)
        {
            line.SetPosition(0, tail);
            line.SetPosition(1, head);
            line.startWidth = width * TailWidth;
            line.endWidth = width;
            line.startColor = new Color(colour.r, colour.g, colour.b, colour.a * 0.25f);
            line.endColor = colour;
        }

        private Pair Take()
        {
            if (_pool.Count > 0)
            {
                var pooled = _pool.Pop();
                pooled.Halo.gameObject.SetActive(true);
                pooled.Core.gameObject.SetActive(true);
                return pooled;
            }

            // The core is created second so it is drawn after the halo it sits
            // inside. Both are transparent and neither writes depth, so ordering
            // is what decides which is on top.
            return new Pair
            {
                Halo = NewLine("Tracer Halo", 0),
                Core = NewLine("Tracer Core", 1),
            };
        }

        private LineRenderer NewLine(string name, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, worldPositionStays: false);

            var line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.numCapVertices = 2;
            line.sortingOrder = order;

            // View alignment, because the camera turns. A line aligned to its own
            // transform would roll with the world and read as tilting.
            line.alignment = LineAlignment.View;

            // Stretch, so the streak texture spans the whole line however long it
            // is. Tile would repeat the soft band along a long shot and put a hard
            // seam wherever it wrapped.
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.material = Material();
            return line;
        }

        private void Release(Pair lines)
        {
            lines.Halo.gameObject.SetActive(false);
            lines.Core.gameObject.SetActive(false);
            _pool.Push(lines);
        }

        /// <summary>
        /// The line material, built if none was assigned.
        /// </summary>
        /// <remarks>
        /// Sprites/Default with vertex colour and the shared soft streak texture.
        /// The texture is the part that matters: an untextured LineRenderer is a
        /// hard-edged polygon strip a couple of pixels wide, which crawls and
        /// shimmers as it moves. Assign your own material to change any of this;
        /// this only exists so the component works out of the box instead of
        /// drawing magenta.
        /// </remarks>
        private Material Material()
        {
            if (LineMaterial != null)
            {
                return LineMaterial;
            }
            if (_owned == null)
            {
                _owned = new Material(Shader.Find("Sprites/Default"))
                {
                    name = "Vrox Tracer",
                    mainTexture = StreakTexture != null
                        ? StreakTexture
                        : (_ownedTexture = VroxGlow.BuildStreak()),
                };
            }
            return _owned;
        }
    }
}
