using UnityEngine;
using UnityEngine.InputSystem;

namespace Vrox
{
    /// <summary>
    /// WASD in, server position out.
    /// </summary>
    /// <remarks>
    /// Put this on a GameObject with something visible on it.
    ///
    /// There is no prediction here. The client sends a direction and draws where
    /// the server says it is — so what you see is the truth, and if it feels
    /// wrong the problem is real rather than a disagreement between two
    /// simulations. Prediction is the next layer, not this one.
    ///
    /// The one exception is <see cref="LeadWhileMoving"/>, and it is worth being
    /// precise about why it is not prediction. It leads by a velocity *the server
    /// already reported*, to cancel a lag the easing itself introduces, and it
    /// leads by exactly the amount that puts the sprite back on the last position
    /// the server sent. It never draws the player somewhere the server has not
    /// already put them, and it does not shorten the delay between a keypress and
    /// a response by a single millisecond.
    ///
    /// Input is sent on a fixed 20 Hz schedule, not per frame. One input equals
    /// one server step, so the send rate *is* the movement speed: sending twice
    /// as often would move twice as fast.
    /// </remarks>
    public sealed class VroxPlayer : MonoBehaviour
    {
        /// <summary>Must match the server's step rate.</summary>
        private const float SendInterval = 0.05f;

        [Tooltip("Ease toward the server position instead of snapping. The server " +
                 "reports at 20 Hz, so snapping looks stepped at higher frame rates. " +
                 "Turn off to see exactly what the server said.")]
        public bool Smooth = true;

        [Tooltip("Higher follows the server more tightly and looks less floaty.")]
        public float SmoothLambda = 20f;

        [Tooltip("Cancel the trail that easing leaves behind while walking. Off is the " +
                 "old behaviour, for comparing the two — the difference is most " +
                 "obvious grazing something at full speed.")]
        public bool LeadWhileMoving = true;

        [Tooltip("A jump larger than this is a spawn, a death or a teleport rather than " +
                 "a step, and is snapped to instead of eased toward.")]
        [Range(0.5f, 20f)]
        public float SnapDistance = 2f;

        [Tooltip("How much of each new speed measurement to believe. Low is steady but " +
                 "slow to notice a real change of pace; 1 is the old raw behaviour, " +
                 "which shivers whenever the update stream does.")]
        [Range(0.05f, 1f)]
        public float VelocityBlend = 0.25f;

        [Header("Feel")]
        [Tooltip("Rattle the view when this player is hurt. Undirected: the row says how " +
                 "much was taken but not which way it came from, and inventing a direction " +
                 "would point the flinch somewhere the server never said.")]
        public bool ShakeOnHurt = true;

        [Tooltip("Tiles of camera rattle at the damage below. Scaled linearly and capped, " +
                 "so a big hit is felt without a boss one-shot throwing the view off the " +
                 "map.")]
        [Range(0f, 1.5f)]
        public float HurtShake = 0.45f;

        [Tooltip("Damage that produces a full-strength rattle. Anything above is clamped.")]
        [Range(1, 500)]
        public int HurtShakeAt = 60;

        private float _sendTimer;
        private long _lastHitAt;
        private bool _seenFirstRow;

        /// <summary>Debuff expiries as of the last row, to notice a fresh application.</summary>
        private ulong _stunnedUntil, _slowedUntil, _armorBrokenUntil;
        private bool _seenFirstDebuffRow;

        /// <summary>The last distinct server position, and when it arrived.</summary>
        private Vector2 _lastTarget;
        private float _lastTargetAt;
        private bool _haveTarget;

        /// <summary>Whether <see cref="_serverVelocity"/> holds a real measurement yet.</summary>
        private bool _haveVelocity;

        /// <summary>Tiles per second, measured from the server's own positions.</summary>
        private Vector2 _serverVelocity;

        private void Update()
        {
            var net = VroxNet.Instance;
            if (net == null || !net.Ready || net.Conn is not { } conn)
            {
                return;
            }

            SendInput(conn);

            if (net.LocalPlayer is not { } player)
            {
                return;
            }

            RaiseDamageNumber(player);
            RaiseStatusText(player);

            var server = new Vector2(player.X, player.Y);
            TrackServerVelocity(server);

            var target = new Vector3(server.x, server.y, transform.position.z);
            if (!Smooth)
            {
                transform.position = target;
                return;
            }

            // A spawn, a death or a teleport is not something to ease across.
            if (((Vector2)transform.position - server).sqrMagnitude
                > SnapDistance * SnapDistance)
            {
                transform.position = target;
                return;
            }

            // Easing toward a *moving* target never reaches it. Each frame closes
            // a fraction of the gap while the target opens a fresh one, and the
            // two balance at a fixed distance behind: v / lambda tiles, which at
            // 5 tiles/s and lambda 20 is a quarter of a tile, held for as long as
            // the player walks and closed only when they stop. In a game about
            // grazing bullets that is the hitbox sitting a quarter tile ahead of
            // the sprite the player is dodging with.
            //
            // Leading by exactly v / lambda cancels it: the ease still absorbs the
            // 20 Hz stepping, but settles on the server position rather than
            // behind it. The velocity is measured from the server's own positions,
            // never from the input we sent — the two disagree the moment a wall,
            // an open loot bag or a heavy weapon is involved, and leading by
            // intent would slide the sprite into walls the server never let us
            // enter.
            //
            // This is a drawing correction and nothing more. It does not make the
            // character respond any sooner; the input still waits up to a send
            // interval and a round trip. It makes the sprite tell the truth about
            // where the server has already put it.
            if (LeadWhileMoving && SmoothLambda > 0f)
            {
                target += (Vector3)(_serverVelocity / SmoothLambda);
            }

            // 1 - e^(-lambda*dt) rather than a fixed fraction per frame: a fixed
            // fraction would ease several times faster at 300 fps than at 60, so
            // the feel would depend on the hardware.
            float alpha = 1f - Mathf.Exp(-SmoothLambda * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, target, alpha);
        }

        /// <summary>
        /// Measures how fast the server is actually moving this player.
        /// </summary>
        /// <remarks>
        /// Updated only when the position changes, because rows arrive more often
        /// than they differ: a frame that sees the same position twice says
        /// nothing about speed, and dividing a zero delta by the frame time would
        /// read as a dead stop every time the client outran the 20 Hz stream.
        ///
        /// Held rather than decayed when the player stops. A stop is not visible
        /// here — an unchanged position means "no news", which is the same thing
        /// the client sees between updates while walking — so the stale velocity
        /// is left in place and the ease absorbs it over its own time constant.
        /// The cost is the sprite overshooting by a fraction of a tile on the
        /// frame movement ends; the alternative is it stuttering on every frame
        /// that arrives between updates, which is visible constantly rather than
        /// once.
        /// </remarks>
        private void TrackServerVelocity(Vector2 server)
        {
            if (!_haveTarget)
            {
                _haveTarget = true;
                _lastTarget = server;
                _lastTargetAt = Time.time;
                return;
            }

            if (server == _lastTarget)
            {
                return;
            }

            float dt = Time.time - _lastTargetAt;
            var delta = server - _lastTarget;
            _lastTarget = server;
            _lastTargetAt = Time.time;

            // A teleport is not a velocity. Reported at 20 Hz, a real step is at
            // most a few tenths of a tile; anything larger is a respawn, and
            // turning it into a lead would fling the sprite across the map.
            if (dt <= 0f || delta.sqrMagnitude > SnapDistance * SnapDistance)
            {
                _serverVelocity = Vector2.zero;
                return;
            }

            // Clamped and then smoothed, because the raw quotient is far noisier
            // than the thing it is measuring.
            //
            // The server moves this player one fixed step per input at a fixed
            // rate, so the true speed barely varies while a key is held. But dt
            // is wall-clock measured between two *frames that happened to notice
            // different rows*, so it carries the jitter of the update stream and
            // of the frame rate on top of it. A tick that arrives late doubles
            // dt and halves the estimate; two rows seen a frame apart shrink it
            // toward zero and send the estimate to infinity.
            //
            // That matters here more than it looks, because the estimate is not
            // only observed — it is fed straight back into the drawn position as
            // the lead below. An estimate that swings by a factor of two swings
            // the sprite by half a lead, which is a visible shiver while walking
            // and nothing at all while standing still, since a stopped player
            // reports no new positions to measure.
            float expected = SendInterval;
            dt = Mathf.Clamp(dt, expected * 0.5f, expected * 4f);

            var sample = delta / dt;
            _serverVelocity = _haveVelocity
                ? Vector2.Lerp(_serverVelocity, sample, VelocityBlend)
                : sample;
            _haveVelocity = true;
        }

        /// <summary>
        /// Raises a number when this player takes a hit.
        /// </summary>
        /// <remarks>
        /// Triggered by the server's own hit timestamp changing, so the number
        /// appears for damage that was actually applied — and shows the amount
        /// after defence, which is what the player's health actually lost.
        ///
        /// The first row seen is skipped. On connect it carries whatever
        /// timestamp the last session ended on, and treating that as a fresh hit
        /// would greet every returning player with a damage number.
        /// </remarks>
        private void RaiseDamageNumber(SpacetimeDB.Types.Player player)
        {
            long hitAt = player.LastHitAt.MicrosecondsSinceUnixEpoch;
            if (!_seenFirstRow)
            {
                _seenFirstRow = true;
                _lastHitAt = hitAt;
                return;
            }
            if (hitAt == _lastHitAt)
            {
                return;
            }

            _lastHitAt = hitAt;
            // Raised at the drawn position rather than the server's, so it appears
            // on the character as seen rather than a smoothing step ahead of it.
            DamageNumbers.Show(transform.position.x, transform.position.y,
                               player.LastDamage, incoming: true);

            if (ShakeOnHurt && player.LastDamage > 0)
            {
                float weight = Mathf.Clamp01((float)player.LastDamage / Mathf.Max(1, HurtShakeAt));
                VroxCamera.Shake(HurtShake * weight, 0.16f);
            }
        }

        /// <summary>
        /// Calls out a debuff the moment the server applies one.
        /// </summary>
        /// <remarks>
        /// Driven by the expiry moving *later*, not by the debuff being active.
        /// Active would raise a word every frame for as long as it lasted; an
        /// expiry that grows is exactly one event per application, and it catches
        /// a refresh of a debuff already running — which is a real hit that
        /// deserves saying so.
        ///
        /// The first row is skipped for the same reason the damage number skips
        /// it: reconnecting mid-stun would otherwise announce a stun that started
        /// before this client was watching.
        /// </remarks>
        private void RaiseStatusText(SpacetimeDB.Types.Player player)
        {
            if (!_seenFirstDebuffRow)
            {
                _seenFirstDebuffRow = true;
                _stunnedUntil = player.StunnedUntilUs;
                _slowedUntil = player.SlowedUntilUs;
                _armorBrokenUntil = player.ArmorBrokenUntilUs;
                return;
            }

            float x = transform.position.x;
            float y = transform.position.y;

            if (player.StunnedUntilUs > _stunnedUntil)
            {
                DamageNumbers.ShowStatus(x, y, Vrox.Equipment.DebuffKind.Stun);
            }
            if (player.SlowedUntilUs > _slowedUntil)
            {
                DamageNumbers.ShowStatus(x, y, Vrox.Equipment.DebuffKind.Slow);
            }
            if (player.ArmorBrokenUntilUs > _armorBrokenUntil)
            {
                DamageNumbers.ShowStatus(x, y, Vrox.Equipment.DebuffKind.ArmorBreak);
            }

            _stunnedUntil = player.StunnedUntilUs;
            _slowedUntil = player.SlowedUntilUs;
            _armorBrokenUntil = player.ArmorBrokenUntilUs;
        }

        private void SendInput(SpacetimeDB.Types.DbConnection conn)
        {
            _sendTimer += Time.deltaTime;
            if (_sendTimer < SendInterval)
            {
                return;
            }
            // Subtract rather than zero, so a slow frame does not lose the
            // remainder and quietly slow the character down.
            _sendTimer -= SendInterval;

            var dir = ReadDirection();
            if (dir == Vector2.zero)
            {
                return;
            }

            // Rotated into world space before sending. Movement is relative to
            // what the player sees, so with the view turned, "forward" is not +Y.
            // The rotation happens once, here — the server only ever receives a
            // world-space direction and knows nothing about the camera.
            dir = VroxCamera.ScreenToWorld(dir);

            try
            {
                conn.Reducers.Move(dir.x, dir.y);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[vrox] move failed: {e.Message}");
            }
        }

        /// <summary>
        /// WASD, read straight from the keyboard device.
        /// </summary>
        /// <remarks>
        /// No actions asset yet — one device, four keys, and nothing to configure
        /// or to go missing. Rebindable input is worth adding once there is
        /// something to rebind.
        /// </remarks>
        private static Vector2 ReadDirection()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return Vector2.zero;
            }

            var dir = Vector2.zero;
            if (keyboard.wKey.isPressed) dir.y += 1f;
            if (keyboard.sKey.isPressed) dir.y -= 1f;
            if (keyboard.aKey.isPressed) dir.x -= 1f;
            if (keyboard.dKey.isPressed) dir.x += 1f;
            return dir;
        }
    }
}
