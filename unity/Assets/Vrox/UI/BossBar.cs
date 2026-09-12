using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Vrox.UI
{
    /// <summary>
    /// Shows the boss that is currently engaging you.
    /// </summary>
    /// <remarks>
    /// Driven by row callbacks from the SDK, not by polling and not by a helper
    /// object. The server writes which player each enemy has engaged, using the
    /// same range check that decides whether it fires — so "the boss has noticed
    /// me" has one definition, on the authoritative side, and this only reacts to
    /// it.
    ///
    /// Measuring the distance here instead would be a second answer to that
    /// question using a different number, and the two would disagree at the edges.
    ///
    /// Build the UI however you like and drop the pieces in. This owns no layout
    /// and creates nothing.
    /// </remarks>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class BossBar : MonoBehaviour
    {
        [Header("Health")]
        public Image? CurrentHpFill;

        [Tooltip("Filled image that lags behind, showing what was just lost.")]
        public Image? EffectHpFill;

        public TMP_Text? HpLabel;

        [Header("Energy")]
        public Image? CurrentEnergyFill;
        public TMP_Text? EnergyLabel;

        [Tooltip("Hidden for a boss whose Max Energy is 0. Leave empty to hide the " +
                 "fill and label individually.")]
        public GameObject? EnergyGroup;

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
        private ulong _shownId;
        private bool _visible;
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
                    conn.Db.Enemy.OnInsert += OnEnemyChanged;
                    conn.Db.Enemy.OnUpdate += OnEnemyUpdated;
                    conn.Db.Enemy.OnDelete += OnEnemyChanged;
                    _dirty = true;
                }
            }

            // Coalesced to one scan per frame. The callbacks fire per row, and a
            // populated realm updates every enemy the server simulates twenty
            // times a second — enough that rescanning inside the callback was
            // costing hundreds of thousands of row visits a second and showing up
            // as stuttering player movement, with the network entirely idle.
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
            conn.Db.Enemy.OnInsert -= OnEnemyChanged;
            conn.Db.Enemy.OnUpdate -= OnEnemyUpdated;
            conn.Db.Enemy.OnDelete -= OnEnemyChanged;
            _bound = null;
        }

        private void OnEnemyUpdated(EventContext ctx, Enemy oldRow, Enemy newRow)
        {
            if (Concerns(oldRow) || Concerns(newRow))
            {
                _dirty = true;
            }
        }

        private void OnEnemyChanged(EventContext ctx, Enemy row)
        {
            if (Concerns(row))
            {
                _dirty = true;
            }
        }

        /// <summary>Whether a changed row could alter what this bar shows.</summary>
        /// <remarks>
        /// Both sides of an update are tested, which is what keeps the full scan
        /// unnecessary: a boss disengaging still matches on the old row's target
        /// or on being the one currently shown, and a boss engaging matches on the
        /// new row's. An enemy that has nothing to do with this player cannot
        /// change what the bar displays, and there are hundreds of those moving
        /// every tick.
        /// </remarks>
        private bool Concerns(Enemy row) =>
            row.Id == _shownId
            || (VroxNet.Instance?.LocalIdentity is { } me && row.Target == me);

        /// <summary>
        /// Picks the boss engaging this player, if any.
        /// </summary>
        /// <remarks>
        /// A scan rather than a cached id, because the row that changed is not
        /// necessarily the one being shown — another boss disengaging is exactly
        /// as relevant as this one being hit.
        /// </remarks>
        private void Refresh()
        {
            var net = VroxNet.Instance;
            if (net?.Conn is not { } conn || net.LocalIdentity is not { } me)
            {
                _visible = false;
                return;
            }

            foreach (var enemy in conn.Db.Enemy.Iter())
            {
                if (enemy.Target != me
                    || conn.Db.EnemyDef.Id.Find(enemy.DefId) is not { IsBoss: true } def)
                {
                    continue;
                }

                // A different boss starts its trail where that boss is. Inherited,
                // it would drain from a level this bar never showed.
                if (enemy.Id != _shownId)
                {
                    _shownId = enemy.Id;
                    _effect = def.MaxHp > 0 ? (float)enemy.Hp / def.MaxHp : 0f;
                    _prevFraction = _effect;
                }

                _visible = true;
                Render(enemy, def);
                return;
            }

            _visible = false;
            _shownId = 0;
        }

        private void Render(Enemy enemy, EnemyDef def)
        {
            _hpFraction = def.MaxHp > 0 ? Mathf.Clamp01((float)enemy.Hp / def.MaxHp) : 0f;

            if (CurrentHpFill != null)
            {
                CurrentHpFill.fillAmount = _hpFraction;
            }
            if (HpLabel != null)
            {
                HpLabel.text = $"{enemy.Hp} / {def.MaxHp}";
            }
            if (NameLabel != null)
            {
                NameLabel.text = def.Name;
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
            // Comparing against the trail was why the trail never drained. This
            // row is rewritten twenty times a second by the boss simply moving,
            // and every one of those updates is a moment when health is under
            // the trail. Each re-armed the delay, so the countdown restarted
            // before it could ever elapse and the trailing bar sat where the hit
            // left it for the whole fight.
            if (_hpFraction < _prevFraction)
            {
                _lastDropAt = Time.time;
            }
            _prevFraction = _hpFraction;

            bool hasEnergy = def.MaxEnergy > 0;
            if (EnergyGroup != null)
            {
                if (EnergyGroup.activeSelf != hasEnergy)
                {
                    EnergyGroup.SetActive(hasEnergy);
                }
            }
            else
            {
                if (CurrentEnergyFill != null && CurrentEnergyFill.enabled != hasEnergy)
                {
                    CurrentEnergyFill.enabled = hasEnergy;
                }
                if (EnergyLabel != null && EnergyLabel.enabled != hasEnergy)
                {
                    EnergyLabel.enabled = hasEnergy;
                }
            }

            if (!hasEnergy)
            {
                return;
            }
            if (CurrentEnergyFill != null)
            {
                CurrentEnergyFill.fillAmount = Mathf.Clamp01((float)enemy.Energy / def.MaxEnergy);
            }
            if (EnergyLabel != null)
            {
                EnergyLabel.text = $"{enemy.Energy} / {def.MaxEnergy}";
            }
        }

        /// <summary>
        /// Fades the group toward its target.
        /// </summary>
        /// <remarks>
        /// The alpha is animated rather than the object deactivated, so the bar
        /// finishes draining as it disappears — and so this keeps receiving row
        /// callbacks, which a deactivated object would not.
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

            // Only interactive while fully visible, or a faded bar keeps
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
        /// The pause makes a single hit legible. Draining at a constant rate rather
        /// than easing keeps the streak proportional to the damage — an ease makes
        /// every hit look the same size however hard it was.
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
