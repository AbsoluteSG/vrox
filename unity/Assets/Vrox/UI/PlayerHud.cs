using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Vrox.UI
{
    /// <summary>
    /// The local player's own bars. Health only, for now.
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="BossBar"/>, and for the same reason: driven by row
    /// callbacks from the SDK rather than by polling a helper, so the only source
    /// of "how much health do I have" is the replicated <c>player</c> row the
    /// server writes.
    ///
    /// Owns no layout and creates nothing — build the UI however you like and
    /// drop the pieces in.
    /// </remarks>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class PlayerHud : MonoBehaviour
    {
        [Header("Health")]
        [Tooltip("Filled image. Give it a material using Vrox/UI Liquid Fill and a " +
                 "LiquidFill component for the wavy surface.")]
        public Image? CurrentHpFill;

        [Tooltip("Filled image that lags behind, showing what was just lost.")]
        public Image? EffectHpFill;

        public TMP_Text? HpLabel;

        [Header("Name")]
        public TMP_Text? NameLabel;

        [Header("Fade")]
        public float FadeSeconds = 0.25f;

        [Header("Damage trail")]
        [Tooltip("How long the trailing bar holds before it starts draining.")]
        public float TrailDelay = 0.4f;

        [Tooltip("How fast it drains once it starts, in fractions of the bar per second.")]
        public float TrailSpeed = 0.7f;

        private CanvasGroup _group = null!;
        private DbConnection? _bound;
        private bool _visible;
        private bool _seenRow;
        private float _effect;
        private float _hpFraction;
        private float _prevFraction;
        private float _lastDropAt;
        private bool _dirty;

        private void Awake()
        {
            _group = GetComponent<CanvasGroup>();
            _group.alpha = 0f;
            SetInteractive(false);
        }

        private void OnDisable() => Unbind();

        private void Update()
        {
            // Bound late and re-bound after a reconnect, because the connection
            // does not exist when this awakes and is replaced when it drops.
            var conn = VroxNet.Instance?.Conn;
            if (conn != _bound)
            {
                Unbind();
                _bound = conn;
                if (conn != null)
                {
                    conn.Db.Player.OnInsert += OnPlayerChanged;
                    conn.Db.Player.OnUpdate += OnPlayerUpdated;
                    conn.Db.Player.OnDelete += OnPlayerChanged;
                    _dirty = true;
                }

                // A new connection is a new identity and a new row. Keeping the
                // trail would drain it from a health level this session never
                // had.
                _seenRow = false;
            }

            // Coalesced to one refresh per frame, for the same reason the boss
            // bar is: the callbacks fire per row, and every other player in the
            // realm moves twenty times a second.
            if (_dirty)
            {
                _dirty = false;
                Refresh();
            }

            Fade();
            DrainTrail();
        }

        private void Unbind()
        {
            if (_bound is not { } conn)
            {
                return;
            }
            conn.Db.Player.OnInsert -= OnPlayerChanged;
            conn.Db.Player.OnUpdate -= OnPlayerUpdated;
            conn.Db.Player.OnDelete -= OnPlayerChanged;
            _bound = null;
        }

        private void OnPlayerUpdated(EventContext ctx, Player oldRow, Player newRow)
        {
            if (IsMine(oldRow) || IsMine(newRow))
            {
                _dirty = true;
            }
        }

        private void OnPlayerChanged(EventContext ctx, Player row)
        {
            if (IsMine(row))
            {
                _dirty = true;
            }
        }

        private bool IsMine(Player row) =>
            VroxNet.Instance?.LocalIdentity is { } me && row.Identity == me;

        /// <summary>
        /// Reads the local player's row, if it has arrived.
        /// </summary>
        /// <remarks>
        /// A missing row means "not replicated yet", not "no health" — so the HUD
        /// hides rather than showing an empty bar. An empty bar is a claim about
        /// the player's state, and this is not in a position to make it.
        /// </remarks>
        private void Refresh()
        {
            var net = VroxNet.Instance;
            if (net?.Conn is not { } conn
                || net.LocalIdentity is not { } me
                || conn.Db.Player.Identity.Find(me) is not { } player)
            {
                _visible = false;
                return;
            }

            // The trail starts where the bar starts. Left at zero it would play a
            // full drain the first time the row arrives, which looks like taking
            // every point of damage at once on spawn.
            if (!_seenRow)
            {
                _seenRow = true;
                _effect = Fraction(player);
                _prevFraction = _effect;
            }

            _visible = true;
            Render(player);
        }

        /// <summary>
        /// The player's health as the server holds it, including the fraction.
        /// </summary>
        /// <remarks>
        /// <c>Hp</c> is an integer, so regeneration lands a whole point at a time
        /// however often the row updates and a bar drawn from it alone climbs in
        /// visible steps. The module carries the remainder in <c>RegenPool</c> and
        /// replicates it, which makes <c>Hp + RegenPool</c> the continuous value —
        /// and the server advances it every tick, twenty times a second.
        ///
        /// So the smoothness comes from the authoritative number, not from a rate
        /// invented here. An ease on top would be a second answer to "how much
        /// health do I have" that lags the first, and the client is not in a
        /// position to give one.
        /// </remarks>
        private static float Fraction(Player p) =>
            p.MaxHp > 0 ? Mathf.Clamp01((p.Hp + p.RegenPool) / p.MaxHp) : 0f;

        private void Render(Player player)
        {
            _hpFraction = Fraction(player);

            if (CurrentHpFill != null)
            {
                CurrentHpFill.fillAmount = _hpFraction;
            }

            // Whole points, not the fraction. The bar is the continuous readout;
            // a label reading "43.6" is a number the player cannot act on, and
            // the pool is not health they can spend until it lands.
            if (HpLabel != null)
            {
                HpLabel.text = $"{player.Hp} / {player.MaxHp}";
            }
            if (NameLabel != null)
            {
                NameLabel.text = player.Name;
            }

            // Healing overtakes the trail at once: a trailing bar below the real
            // one would read as damage that never happened.
            if (_hpFraction > _effect)
            {
                _effect = _hpFraction;
            }

            // The delay is re-armed by health actually *falling*, measured
            // against the last value seen — not by health merely sitting below
            // the trail.
            //
            // Comparing against the trail was why the trail never drained.
            // Regeneration writes this row twenty times a second for as long as
            // the player is below full, and every one of those updates is a
            // moment when health is under the trail. Each re-armed the delay, so
            // the countdown restarted before it could ever elapse and the
            // trailing bar sat where the hit left it until health reached max.
            if (_hpFraction < _prevFraction)
            {
                _lastDropAt = Time.time;
            }
            _prevFraction = _hpFraction;
        }

        /// <summary>
        /// Fades the group toward its target.
        /// </summary>
        /// <remarks>
        /// The alpha is animated rather than the object deactivated, so this keeps
        /// receiving row callbacks — a deactivated object would not, and would
        /// come back with a stale bar.
        /// </remarks>
        private void Fade()
        {
            float target = _visible ? 1f : 0f;
            if (Mathf.Approximately(_group.alpha, target))
            {
                return;
            }

            _group.alpha = FadeSeconds <= 0f
                ? target
                : Mathf.MoveTowards(_group.alpha, target, Time.deltaTime / FadeSeconds);

            // Only interactive while fully visible, or a faded HUD keeps
            // swallowing clicks meant for the world behind it.
            SetInteractive(_group.alpha > 0.99f);
        }

        private void SetInteractive(bool on)
        {
            _group.interactable = on;
            _group.blocksRaycasts = on;
        }

        /// <summary>
        /// Eases the trailing bar down to the real one, after a pause.
        /// </summary>
        /// <remarks>
        /// The pause makes a single hit legible. Draining at a constant rate
        /// rather than easing keeps the streak proportional to the damage — an
        /// ease makes every hit look the same size however hard it was.
        /// </remarks>
        private void DrainTrail()
        {
            if (EffectHpFill == null)
            {
                return;
            }
            if (_effect > _hpFraction && Time.time - _lastDropAt >= TrailDelay)
            {
                _effect = Mathf.MoveTowards(_effect, _hpFraction, TrailSpeed * Time.deltaTime);
            }
            EffectHpFill.fillAmount = _effect;
        }
    }
}
