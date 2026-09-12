using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Puts you straight into the world with a throwaway character, for testing.
    /// </summary>
    /// <remarks>
    /// Connecting no longer creates a character — that is a choice the player
    /// makes at a screen. Which is correct, and useless when you only want to
    /// press Play and see whether bullets still come out.
    ///
    /// So this is opt-in by being in the scene at all, rather than a flag hidden
    /// on something else. A scene without it behaves the way the game does; a
    /// scene with it is a test scene, and that is visible from the hierarchy
    /// rather than from a checkbox somebody has to remember.
    ///
    /// It stands down when a <see cref="Vrox.UI.CharacterSelectView"/> is present
    /// and active, so leaving it in the real scene by accident cannot silently
    /// skip the screen. That is the failure worth guarding: a select screen that
    /// never appears because something else already chose.
    ///
    /// It prefers an existing character over making another, so repeated plays do
    /// not fill the account's four slots with identical guests.
    ///
    /// Death in this game is permanent — the server deletes the character row —
    /// so after dying there is nothing left to prefer and a fresh guest is made.
    /// Everything here therefore reconciles against replicated state each time
    /// rather than remembering what it already did, because every such memory is
    /// a latch that survives exactly one death and then stops working.
    /// </remarks>
    public sealed class VroxGuestCharacter : MonoBehaviour
    {
        [Tooltip("Name given to a character this creates. Existing characters are " +
                 "reused before a new one is made.")]
        public string GuestName = "Guest";

        [Tooltip("Handed to the guest if it ends up holding nothing. Granted through " +
                 "the development-only GiveTestItem reducer, which is why this lives " +
                 "on the test component and not in the game.")]
        public Vrox.Equipment.EquipmentItem? DefaultWeapon;

        [Tooltip("Also re-enter after dying. Off leaves you at the character screen, " +
                 "which is what a real player sees.")]
        public bool RejoinAfterDeath = true;

        private bool _warnedAboutScreen;
        private bool _announced;
        private float _retry;

        private void Update()
        {
            var net = VroxNet.Instance;
            if (net == null || !net.Ready || net.Conn is not { } conn)
            {
                return;
            }

            // Already playing somebody. Nothing to do, and the announcement resets
            // so a later death says its piece again — as does the creation
            // request, so the next death is free to make another guest
            // immediately instead of waiting out a timer from the last one.
            if (net.LocalCharacterId != 0)
            {
                _announced = false;
                _askedToCreateAt = float.NegativeInfinity;
                ArmIfEmptyHanded(conn, net);
                return;
            }

            if (StandDown())
            {
                return;
            }

            if (!_announced)
            {
                _announced = true;
                Debug.Log("[vrox] guest character active — this scene skips the character "
                        + "screen. Remove VroxGuestCharacter to test the real flow.", this);
            }

            if (!RejoinAfterDeath && _joinedOnce)
            {
                return;
            }

            // Rate-limited. Creating is a reducer call and the row takes a moment
            // to arrive; without this it would fire every frame until it did.
            _retry -= Time.deltaTime;
            if (_retry > 0f)
            {
                return;
            }
            _retry = 1f;

            if (net.LocalIdentity is not { } me)
            {
                return;
            }

            foreach (var character in conn.Db.Character.Account.Filter(me))
            {
                conn.Reducers.SelectCharacter(character.Id);
                _joinedOnce = true;
                return;
            }

            // None exist. Made with an empty loadout: the vault is the player's,
            // and quietly spending it to furnish a test character would cost them
            // something real.
            //
            // Asked again after a pause rather than once, because death here is
            // permanent: the server deletes the character row, so a guest that
            // asked once and remembered asking would sit at the character screen
            // for the rest of the session. The pause is what keeps that from
            // becoming a reducer call every frame.
            if (Time.time - _askedToCreateAt < RetryCreateSeconds)
            {
                return;
            }

            string name = string.IsNullOrWhiteSpace(GuestName) ? "Guest" : GuestName.Trim();
            _askedToCreateAt = Time.time;
            conn.Reducers.CreateCharacter(name, new System.Collections.Generic.List<
                SpacetimeDB.Types.VaultCell>());
        }

        /// <summary>How long to leave a creation request before asking again.</summary>
        /// <remarks>
        /// Long enough for the round trip and the row to replicate, short enough
        /// that a guest which genuinely failed to be made is retried rather than
        /// abandoned.
        /// </remarks>
        private const float RetryCreateSeconds = 3f;

        private bool _joinedOnce;
        private float _armRetry;
        private float _askedToCreateAt = float.NegativeInfinity;

        /// <summary>
        /// Puts a weapon in the guest's hands if the world left it unarmed.
        /// </summary>
        /// <remarks>
        /// A character is armed from the player config's starting weapon, which is
        /// only set once somebody has authored one and pushed it. Until then a
        /// guest spawns holding nothing and fires nothing — correct, and useless
        /// for the one thing this component exists to make quick.
        ///
        /// Granted rather than taken from the vault: a test character helping
        /// itself to the player's banked loot would cost them something real.
        /// </remarks>
        private void ArmIfEmptyHanded(SpacetimeDB.Types.DbConnection conn, VroxNet net)
        {
            if (DefaultWeapon == null || net.LocalPlayer is not { } player)
            {
                return;
            }

            // Answered from the replicated row every time rather than from a flag
            // saying it was handled once. The flag was wrong for the same reason
            // the old _equipped cache was: it recorded what this component had
            // done, not what the server ended up holding — so after a death took
            // the weapon away, it went on believing the guest was armed.
            if (player.WeaponId != 0)
            {
                return;
            }

            // Rate-limited: granting and equipping are two reducer calls and the
            // row takes a moment, so without this it fires every frame until it
            // lands.
            _armRetry -= Time.deltaTime;
            if (_armRetry > 0f)
            {
                return;
            }
            _armRetry = 1f;

            ushort id = DefaultWeapon.CatalogueId;
            conn.Reducers.GiveTestItem(id, 1);
            conn.Reducers.EquipWeapon(id);
            Debug.Log($"[vrox] guest armed with {DefaultWeapon.name} — granted, not taken "
                    + "from the vault.", this);
        }

        /// <summary>Whether a real character screen is handling this instead.</summary>
        private bool StandDown()
        {
            var screen = FindAnyObjectByType<Vrox.UI.CharacterSelectView>();
            if (screen == null || !screen.isActiveAndEnabled)
            {
                return false;
            }

            if (!_warnedAboutScreen)
            {
                _warnedAboutScreen = true;
                Debug.LogWarning("[vrox] a CharacterSelectView is in this scene, so "
                               + "VroxGuestCharacter is standing down. Remove one of them — "
                               + "with both, the screen would never get a chance to appear.",
                                 this);
            }
            return true;
        }
    }
}
