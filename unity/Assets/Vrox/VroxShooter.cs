using UnityEngine;
using UnityEngine.InputSystem;

namespace Vrox
{
    /// <summary>
    /// Aims at the cursor and fires. Put this on the player.
    /// </summary>
    /// <remarks>
    /// It calls the server every tick while firing; the server decides whether a
    /// shot actually happens. Rate of fire lives there, so calling faster buys
    /// nothing — and the client never invents a projectile of its own.
    /// </remarks>
    public sealed class VroxShooter : MonoBehaviour
    {
        /// <summary>Matches the movement send rate. The server rate-limits anyway.</summary>
        private const float SendInterval = 0.05f;

        [Tooltip("Fire continuously without holding the button.")]
        public bool AutoFire = true;

        [Tooltip("Press R to top up a partly-spent magazine. An empty one reloads on its " +
                 "own without this, so it is a convenience rather than a requirement.")]
        public bool ReloadKey = true;

        [Tooltip("Hold to fire when auto-fire is off. Left mouse button.")]
        public bool AllowManualFire = true;

        /// <summary>World-space aim direction, unit length.</summary>
        public Vector2 Aim { get; private set; } = Vector2.right;

        private float _timer;
        private ushort _reported = ushort.MaxValue;

        /// <summary>
        /// The catalogue row the server is actually firing from, or null.
        /// </summary>
        /// <remarks>
        /// Read from the player's server-side weapon id, not from the asset
        /// assigned here — those disagree whenever a push has not happened, and
        /// the server's answer is the true one.
        /// </remarks>
        public SpacetimeDB.Types.WeaponDef? ServerWeapon
        {
            get
            {
                var net = VroxNet.Instance;
                if (net?.Conn is not { } conn || net.LocalPlayer is not { } player || player.WeaponId == 0)
                {
                    return null;
                }
                return conn.Db.WeaponDef.Id.Find(player.WeaponId);
            }
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.iKey.wasPressedThisFrame)
            {
                AutoFire = !AutoFire;
                Debug.Log($"[vrox] auto-fire {(AutoFire ? "ON" : "OFF")}");
            }

            bool reloadPressed = ReloadKey && keyboard != null
                              && keyboard.rKey.wasPressedThisFrame;

            UpdateAim();

            var net = VroxNet.Instance;
            if (net == null || !net.Ready || net.Conn is not { } conn)
            {
                return;
            }

            WatchWeapon(conn);

            // An empty magazine reloads itself on the server, so this is only for
            // topping up a partial one before walking into something. Asked for
            // rather than decided here: the server owns whether a reload may
            // start, and refuses one that is already running or already full.
            if (reloadPressed)
            {
                try
                {
                    conn.Reducers.Reload();
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[vrox] reload failed: {e.Message}");
                }
            }

            bool firing = AutoFire
                       || (AllowManualFire && Mouse.current != null && Mouse.current.leftButton.isPressed);
            if (!firing)
            {
                return;
            }

            _timer += Time.deltaTime;
            if (_timer < SendInterval)
            {
                return;
            }
            _timer -= SendInterval;

            try
            {
                conn.Reducers.Shoot(Aim.x, Aim.y);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[vrox] shoot failed: {e.Message}");
            }
        }

        /// <summary>
        /// Tells the server which catalogue entry to fire, when it changes.
        /// </summary>
        /// <remarks>
        /// Only the id is sent. The client does not get to describe its own
        /// weapon — if it did, the stats would be whatever it felt like claiming.
        /// A weapon whose id is not in the catalogue is a push that has not
        /// happened yet, so it says so once rather than failing silently.
        /// </remarks>
        /// <summary>
        /// Says what changed, when the server changes what this player holds.
        /// </summary>
        /// <remarks>
        /// Watches, and no longer asks. What is held is decided by whatever is in
        /// the player's equipped weapon slot, which the server recomputes on every
        /// inventory change — so there is nothing for a client to equip.
        ///
        /// This used to push a designer-assigned asset onto the server every
        /// second until it stuck. That was correct while a weapon came from the
        /// scene and nowhere else; with an inventory it is a second answer to
        /// "what am I holding", and the two fought: a looted weapon was equipped,
        /// re-overwritten a second later, and could never be used.
        ///
        /// A player with nothing in that slot fires nothing, deliberately. An
        /// unarmed player who still shoots would make a broken equip look like a
        /// working one.
        /// </remarks>
        private void WatchWeapon(SpacetimeDB.Types.DbConnection conn)
        {
            ushort held = VroxNet.Instance?.LocalPlayer is { } player ? player.WeaponId : (ushort)0;
            if (_reported == held)
            {
                return;
            }
            _reported = held;
            Report(conn, held);
        }

        /// <summary>
        /// States what the server is now firing, once per change.
        /// </summary>
        /// <remarks>
        /// Reads the catalogue row rather than the asset, so it reports what is
        /// actually in force. Without this the only way to tell an equip apart
        /// from a silent failure is to squint at the bullets.
        /// </remarks>
        private static void Report(SpacetimeDB.Types.DbConnection conn, ushort id)
        {
            if (id == 0)
            {
                Debug.Log("[vrox] no weapon equipped — unarmed, so nothing fires.");
                return;
            }
            if (conn.Db.WeaponDef.Id.Find(id) is not { } def)
            {
                return;
            }
            Debug.Log($"[vrox] equipped {id} \"{def.Name}\": {def.Shots} shot(s), "
                    + $"{def.FireRateMs}ms, {def.DamageMin}-{def.DamageMax} dmg, "
                    + $"pattern {def.PatternKind}, spread {def.SpreadDegrees}deg");
        }

        /// <summary>
        /// Aim is the direction from the player to the cursor, in the world.
        /// </summary>
        /// <remarks>
        /// Projected through the camera rather than measured from the middle of
        /// the screen. The screen centre is only the player's position while the
        /// view is centred on them — the moment the camera takes an offset pivot,
        /// measuring from the centre aims at a point the player is not standing
        /// on, and every shot leans in one direction.
        ///
        /// Going through the camera also removes the need to rotate the result:
        /// the projection already accounts for the view's rotation.
        /// </remarks>
        private void UpdateAim()
        {
            var mouse = Mouse.current;
            var camera = Camera.main;
            if (mouse == null || camera == null)
            {
                return;
            }

            var screen = mouse.position.ReadValue();
            var world = camera.ScreenToWorldPoint(
                new Vector3(screen.x, screen.y, Mathf.Abs(camera.transform.position.z)));

            var toCursor = new Vector2(world.x - transform.position.x,
                                       world.y - transform.position.y);
            if (toCursor.sqrMagnitude < 0.0001f)
            {
                // Cursor sitting exactly on the player: keep the last direction
                // rather than snapping to an arbitrary one.
                return;
            }

            Aim = toCursor.normalized;
        }
    }
}
