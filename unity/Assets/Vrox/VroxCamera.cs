using UnityEngine;
using UnityEngine.InputSystem;

namespace Vrox
{
    /// <summary>
    /// Rotates the view, and converts between screen and world directions.
    /// </summary>
    /// <remarks>
    /// Put this on the camera. The camera is a child of the player, so following
    /// needs no code at all — this only turns it.
    ///
    /// Rotation is client-only; the server never hears about it. That is what
    /// makes it safe to spin freely: it changes nothing about the simulation,
    /// only how input is interpreted before it is sent.
    ///
    /// Input is read in <c>Update</c> and the transform written in
    /// <c>LateUpdate</c>, ordered last. The player is rotated by a billboard that
    /// keeps its sprite upright, and this camera is that player's child — so
    /// writing the camera before the parent had settled left the view leading the
    /// world by a frame's worth of turn while a key was held.
    /// </remarks>
    [DefaultExecutionOrder(100)]
    public sealed class VroxCamera : MonoBehaviour
    {
        public static VroxCamera? Instance { get; private set; }

        [Tooltip("Radians per second while Q or E is held.")]
        public float RotateSpeed = 2.2f;

        [Header("Offset pivot")]
        [Tooltip("How far the view sits from the player when offset, in tiles. " +
                 "Positive Y pushes the view up the screen, so the player sits low " +
                 "and you see further ahead. X is measured in screen space, so it " +
                 "stays consistent as the view rotates.")]
        public Vector2 Offset = new Vector2(0f, 2.5f);

        [Tooltip("Start offset rather than centred.")]
        public bool Offsetting;

        [Tooltip("Seconds to slide between centred and offset. 0 snaps.")]
        public float ShiftSeconds = 0.25f;

        [Header("Shake")]
        [Tooltip("Scales every shake at once, for turning the whole effect down without " +
                 "retuning each thing that asks for one. 0 disables it.")]
        [Range(0f, 3f)]
        public float ShakeScale = 1f;

        [Tooltip("How much of a directional shake is a clean push along one axis rather " +
                 "than jitter. 1 is a pure kick, 0 is pure rattle. A gun reads as a kick; " +
                 "being hit reads as a rattle.")]
        [Range(0f, 1f)]
        public float KickBias = 0.7f;

        /// <summary>View rotation in radians, counter-clockwise.</summary>
        public float Angle { get; private set; }

        private float _shift;

        private Vector2 _shakeKick;
        private float _shakeStrength;
        private float _shakeAge;
        private float _shakeLife;

        /// <summary>
        /// How far in front of the player the camera sits, captured once.
        /// </summary>
        /// <remarks>
        /// Read from the authored local z rather than hard-coded, so moving the
        /// camera in the inspector still works. Kept because the position is now
        /// written in world space, which would otherwise lose the depth the scene
        /// was built with.
        /// </remarks>
        private float _depth;

        private void Awake()
        {
            Instance = this;
            _depth = transform.localPosition.z;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.xKey.wasPressedThisFrame)
            {
                Offsetting = !Offsetting;
            }

            float delta = 0f;
            if (keyboard.qKey.isPressed) delta += 1f;
            if (keyboard.eKey.isPressed) delta -= 1f;

            if (delta != 0f)
            {
                Angle += delta * RotateSpeed * Time.deltaTime;
                // Kept bounded so it cannot drift into float ranges where
                // precision degrades over a long session.
                Angle = Mathf.Repeat(Angle, Mathf.PI * 2f);
            }
        }

        private void LateUpdate()
        {
            // World rotation, not local. This was local, on the reasoning that
            // the player never rotates so the two agreed — and then the player
            // was given a billboard to keep its sprite upright, which rotates it
            // by exactly this angle. Local rotation then stacked on top of the
            // parent's, and the view turned at double speed.
            //
            // World space makes the camera independent of whatever its parent is
            // doing, which is the property that was being assumed rather than
            // stated.
            transform.rotation = Quaternion.Euler(0f, 0f, Angle * Mathf.Rad2Deg);
            ApplyOffset();
        }

        /// <summary>
        /// Slides the camera between centred and offset.
        /// </summary>
        /// <remarks>
        /// The offset is authored in screen space and rotated into the world, so
        /// "the player sits low on screen" stays true as the view turns. Applying
        /// it unrotated would swing the player around the screen every time you
        /// pressed Q or E.
        ///
        /// Eased rather than snapped, because a camera that jumps a couple of
        /// tiles reads as a glitch rather than a change of framing.
        /// </remarks>
        private void ApplyOffset()
        {
            float target = Offsetting ? 1f : 0f;
            _shift = ShiftSeconds <= 0f
                ? target
                : Mathf.MoveTowards(_shift, target, Time.deltaTime / ShiftSeconds);

            // The offset is worked out in world space and then converted back
            // into the parent's frame, rather than written as a world position.
            // Staying a child is what makes following the player cost no code and
            // never lag: a camera that set its own world position would be a frame
            // behind whoever moved the player last.
            //
            // The conversion is what the old code was missing. It assigned a world
            // offset straight to a local position, which agreed only while the
            // parent was unrotated — and the player is now rotated by the
            // billboard that keeps its sprite upright.
            var world = ScreenToWorld(Offset) * _shift;
            world += Shaken();

            var offset = new Vector3(world.x, world.y, 0f);
            if (transform.parent != null)
            {
                offset = transform.parent.InverseTransformVector(offset);
            }
            transform.localPosition = new Vector3(offset.x, offset.y, _depth);
        }

        /// <summary>
        /// Kicks the view. Call it when something happened, not every frame.
        /// </summary>
        /// <remarks>
        /// Static, so anything can ask without holding a reference — the same
        /// shape <see cref="DamageNumbers.Show"/> uses, and for the same reason:
        /// the things that cause a shake are scattered and none of them should
        /// have to find the camera.
        ///
        /// Purely local presentation. It moves where the view sits and nothing
        /// else — it does not touch <c>Time</c>, so it cannot disturb the fixed
        /// input cadence that <see cref="VroxPlayer"/> depends on. That matters:
        /// the send rate *is* the movement speed there, so anything that
        /// interfered with timing would turn a camera effect into a movement bug.
        ///
        /// A later shake replaces a weaker one rather than adding to it, so a
        /// fast weapon settles into a steady rattle instead of compounding into
        /// something unreadable.
        /// </remarks>
        /// <param name="strength">Peak displacement, in tiles.</param>
        /// <param name="seconds">How long it takes to die away.</param>
        /// <param name="from">
        /// The direction the force came *from*, in world space. The view is pushed
        /// the opposite way, so firing shoves the camera back down the barrel.
        /// Leave zero for a shake with no direction.
        /// </param>
        public static void Shake(float strength, float seconds, Vector2 from = default)
        {
            if (Instance is not { } camera || strength <= 0f || seconds <= 0f)
            {
                return;
            }

            float scaled = strength * camera.ShakeScale;
            if (scaled <= 0f)
            {
                return;
            }

            // Only if it would actually be felt. Without this a rapid weapon
            // restarts its own decay every shot and the shake never falls off.
            float remaining = camera._shakeLife <= 0f
                ? 0f
                : camera._shakeStrength * (1f - camera._shakeAge / camera._shakeLife);
            if (scaled < remaining)
            {
                return;
            }

            camera._shakeStrength = scaled;
            camera._shakeLife = seconds;
            camera._shakeAge = 0f;
            camera._shakeKick = from.sqrMagnitude < 1e-8f ? Vector2.zero : -from.normalized;
        }

        /// <summary>The shake's contribution to where the camera sits this frame.</summary>
        /// <remarks>
        /// Squared decay rather than linear: a shake that fades evenly reads as
        /// the camera being dragged, where one that drops away fast reads as an
        /// impact that is already over.
        ///
        /// The jitter is re-rolled per frame, which is honest for a rattle and
        /// would be wrong for the kick — so the kick keeps its direction for the
        /// whole life of the shake and only its magnitude decays.
        /// </remarks>
        private Vector2 Shaken()
        {
            if (_shakeLife <= 0f)
            {
                return Vector2.zero;
            }

            _shakeAge += Time.deltaTime;
            if (_shakeAge >= _shakeLife)
            {
                _shakeLife = 0f;
                _shakeStrength = 0f;
                return Vector2.zero;
            }

            float fall = 1f - _shakeAge / _shakeLife;
            float amount = _shakeStrength * fall * fall;

            var jitter = new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 1f));
            if (_shakeKick == Vector2.zero)
            {
                return jitter * amount;
            }
            return Vector2.Lerp(jitter, _shakeKick, KickBias) * amount;
        }

        /// <summary>
        /// Screen right and up, expressed as world directions.
        /// </summary>
        /// <remarks>
        /// What every renderer builds its quads from, so a sprite keeps facing the
        /// player as the view turns. Without it a quad is laid out along world X
        /// and Y, and rotating the camera rolls every enemy, bag and bullet on
        /// screen — which reads as the world tilting rather than the camera
        /// turning.
        ///
        /// The same rotation as <see cref="ScreenToWorld"/> and deliberately
        /// beside it: the directions the player expresses and the directions the
        /// client draws have to agree about which way is up, and two copies of
        /// that maths would eventually disagree by a sign.
        ///
        /// The terrain is the exception and must not use this. The ground *is*
        /// the world, so it is supposed to turn.
        /// </remarks>
        public static void ScreenAxes(out Vector2 right, out Vector2 up)
        {
            float angle = Instance != null ? Instance.Angle : 0f;
            float c = Mathf.Cos(angle);
            float s = Mathf.Sin(angle);
            right = new Vector2(c, s);
            up = new Vector2(-s, c);
        }

        /// <summary>
        /// Turns a screen-space direction into the world direction it represents.
        /// </summary>
        /// <remarks>
        /// With the view rotated, "up" on screen is no longer +Y in the world.
        /// Every direction the player expresses — WASD, and the vector to the
        /// cursor — has to come through here before it is sent, or the controls
        /// stop matching what they see.
        /// </remarks>
        public static Vector2 ScreenToWorld(Vector2 v)
        {
            float angle = Instance != null ? Instance.Angle : 0f;
            if (angle == 0f)
            {
                return v;
            }
            float c = Mathf.Cos(angle);
            float s = Mathf.Sin(angle);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }
    }
}
