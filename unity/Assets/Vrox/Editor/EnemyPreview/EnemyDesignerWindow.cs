using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// One window for building a mob: its weapon patterns, its movement, its phases, and the whole fight.
    /// </summary>
    /// <remarks>
    /// Four tabs over the same enemy:
    /// - Pattern: a weapon's volley — a selected weapon, or the weapon of the
    ///   enemy's selected phase.
    /// - Movement: one movement on its own against the dummy — the enemy's, or one
    ///   phase's with its transition ignored.
    /// - Phases: the phase list with a timeline, editing one phase at a time.
    /// - Enemy: the full fight, phases advancing and bullets flying.
    ///
    /// Editing goes to the assets, with undo. Nothing reaches the server until Push
    /// All, as always. Every preview runs a client copy of the server's maths
    /// (<see cref="VolleyMath"/>, <see cref="EnemyMath"/>) on open ground; the
    /// canvas says so.
    /// </remarks>
    public sealed class EnemyDesignerWindow : EditorWindow
    {
        public enum Tab
        {
            Pattern,
            Movement,
            Phases,
            Enemy,
        }

        private const float ToolbarHeight = 21f;
        private const float Row = 20f;
        private static readonly string[] TabNames = { "Pattern", "Movement", "Phases", "Enemy" };

        [SerializeField] private WeaponItem? _weapon;
        [SerializeField] private EnemyItem? _enemy;
        [SerializeField] private bool _weaponFromEnemy;
        [SerializeField] private bool _locked;
        [SerializeField] private Tab _tab;
        [SerializeField] private PreviewState _weaponState = new();
        [SerializeField] private EnemyPreviewState _enemyState = new();
        [SerializeField] private Vector2 _scroll;
        [SerializeField] private bool _fullInspector;

        private UnityEditor.Editor? _weaponEditor;
        private UnityEditor.Editor? _enemyEditor;
        private SerializedObject? _so;
        private readonly EnemySimulation _sim = new();
        private readonly EnemyCanvas _canvas = new();
        private readonly List<string> _warnings = new();
        private double _nextRepaint;

        [MenuItem("Vrox/Enemy Designer")]
        private static void Menu() => Get();

        public static void ShowWeapon(WeaponItem? weapon)
        {
            var window = Get();
            if (weapon != null)
            {
                window.SetWeapon(weapon);
            }
            window._tab = Tab.Pattern;
        }

        public static void ShowEnemy(EnemyItem enemy) => Get().SetEnemy(enemy);

        private static EnemyDesignerWindow Get()
        {
            var window = GetWindow<EnemyDesignerWindow>();
            window.titleContent = new GUIContent("Enemy Designer");
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Enemy Designer");
            if (_weapon == null && _enemy == null)
            {
                OnSelectionChange();
            }
        }

        private void OnDisable()
        {
            if (_weaponEditor != null) DestroyImmediate(_weaponEditor);
            if (_enemyEditor != null) DestroyImmediate(_enemyEditor);
        }

        private void OnSelectionChange()
        {
            if (_locked)
            {
                return;
            }
            switch (Selection.activeObject)
            {
                case WeaponItem weapon:
                    SetWeapon(weapon);
                    break;
                case EnemyItem enemy:
                    SetEnemy(enemy);
                    break;
            }
        }

        private void SetWeapon(WeaponItem weapon)
        {
            if (weapon != _weapon)
            {
                _weapon = weapon;
                _weaponState.Time = 0;
                _weaponState.ZoomTiles = 0f;
            }
            _weaponFromEnemy = false;
            _tab = Tab.Pattern;
            Repaint();
        }

        private void SetEnemy(EnemyItem enemy)
        {
            if (enemy != _enemy)
            {
                _enemy = enemy;
                _so = null;
                _scroll = Vector2.zero;
                _enemyState.Restart(Mathf.Round(Mathf.Max(1, enemy.MaxHp) / 90f));
            }
            _weaponFromEnemy = true;
            _weaponState.AsEnemy = true;
            if (_tab == Tab.Pattern || _tab == Tab.Phases && enemy.Phases.Count == 0)
            {
                _tab = enemy.Phases.Count > 0 ? Tab.Enemy : Tab.Movement;
            }
            Repaint();
        }

        /// <summary>The weapon the Pattern tab shows: the selected weapon, or the enemy's (selected phase's).</summary>
        private WeaponItem? PatternWeapon
        {
            get
            {
                if (!_weaponFromEnemy || _enemy == null)
                {
                    return _weapon;
                }
                int i = _enemyState.SelectedPhase;
                return _enemy.Phases.Count > 0 && i >= 0 && i < _enemy.Phases.Count
                    ? _enemy.Phases[i].Weapon
                    : _enemy.Weapon;
            }
        }

        private void Update()
        {
            bool playing = _tab == Tab.Pattern ? _weaponState.Playing && PatternWeapon != null
                                               : _enemyState.Playing && _enemy != null;
            double now = EditorApplication.timeSinceStartup;
            if (playing && now >= _nextRepaint)
            {
                _nextRepaint = now + 1.0 / 60.0;
                Repaint();
            }
        }

        private void OnGUI()
        {
            DrawToolbar();

            var area = new Rect(0f, ToolbarHeight, position.width, position.height - ToolbarHeight);
            float leftWidth = Mathf.Clamp(area.width * 0.36f, 290f, 460f);
            var left = new Rect(area.x, area.y, leftWidth, area.height);
            var right = new Rect(left.xMax + 1f, area.y, area.width - leftWidth - 1f, area.height);

            if (_tab == Tab.Pattern)
            {
                PatternTab(area, left, right);
            }
            else
            {
                EnemyTabs(area, left, right);
            }
        }

        private void DrawToolbar()
        {
            using var _ = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);

            _locked = GUILayout.Toggle(_locked, new GUIContent("Lock", "Keep these assets while selecting others."),
                                       EditorStyles.toolbarButton, GUILayout.Width(40f));
            _tab = (Tab)GUILayout.Toolbar((int)_tab, TabNames, EditorStyles.toolbarButton, GUILayout.Width(280f));

            EditorGUI.BeginChangeCheck();
            var enemy = EditorGUILayout.ObjectField(_enemy, typeof(EnemyItem), false, GUILayout.Width(170f)) as EnemyItem;
            if (EditorGUI.EndChangeCheck() && enemy != null)
            {
                SetEnemy(enemy);
            }

            if (_tab == Tab.Pattern)
            {
                EditorGUI.BeginChangeCheck();
                var weapon = EditorGUILayout.ObjectField(PatternWeapon, typeof(WeaponItem), false, GUILayout.Width(170f)) as WeaponItem;
                if (EditorGUI.EndChangeCheck() && weapon != null)
                {
                    SetWeapon(weapon);
                }
                _weaponState.AsEnemy = GUILayout.Toggle(_weaponState.AsEnemy, new GUIContent("As Enemy",
                    "Draw it as an enemy fires it: always projectiles, even with a Range set."), EditorStyles.toolbarButton);
            }

            GUILayout.FlexibleSpace();

            if (_tab == Tab.Pattern)
            {
                _weaponState.Mode = (PreviewState.ViewMode)EditorGUILayout.EnumPopup(
                    _weaponState.Mode, EditorStyles.toolbarPopup, GUILayout.Width(64f));
                _weaponState.Trails = GUILayout.Toggle(_weaponState.Trails, "Trails", EditorStyles.toolbarButton);
                _weaponState.SlotNumbers = GUILayout.Toggle(_weaponState.SlotNumbers, new GUIContent("Slots",
                    "Number each slot, and its variant letter, in Volley mode and for hitscan."), EditorStyles.toolbarButton);
                if (GUILayout.Button("Fit", EditorStyles.toolbarButton))
                {
                    _weaponState.ZoomTiles = 0f;
                }
            }
            else
            {
                _enemyState.Ranges = GUILayout.Toggle(_enemyState.Ranges, "Ranges", EditorStyles.toolbarButton);
                if (_tab == Tab.Enemy)
                {
                    _enemyState.Bullets = GUILayout.Toggle(_enemyState.Bullets, "Bullets", EditorStyles.toolbarButton);
                    _enemyState.Trails = GUILayout.Toggle(_enemyState.Trails, "Trails", EditorStyles.toolbarButton);
                }
                if (GUILayout.Button("Fit", EditorStyles.toolbarButton))
                {
                    _enemyState.ZoomTiles = 0f;
                }
            }

            if (GUILayout.Button(new GUIContent("Push All", "Vrox/Push All: send every authored catalogue to the server."),
                                 EditorStyles.toolbarButton))
            {
                PushEquipment.Push();
            }
        }

        private void PatternTab(Rect area, Rect left, Rect right)
        {
            var weapon = PatternWeapon;
            if (weapon == null)
            {
                GUILayout.BeginArea(area);
                EditorGUILayout.HelpBox(_weaponFromEnemy && _enemy != null
                    ? $"{_enemy.name} has no weapon here. Pick one in the Phases or Movement tab, or select a Weapon."
                    : "Select a Weapon or an Enemy, or drop one into the fields above.", MessageType.Info);
                GUILayout.EndArea();
                return;
            }

            GUILayout.BeginArea(left);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_weaponFromEnemy && _enemy != null)
            {
                EditorGUILayout.HelpBox(_enemy.Phases.Count > 0
                    ? $"Phase {_enemyState.SelectedPhase} of {_enemy.name}. Edits change the weapon asset, which other enemies may share."
                    : $"{_enemy.name}'s weapon. Edits change the weapon asset, which other enemies may share.", MessageType.None);
            }
            UnityEditor.Editor.CreateCachedEditor(weapon, null, ref _weaponEditor);
            _weaponEditor!.OnInspectorGUI();
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();

            Divider(left, area);

            var canvas = new Rect(right.x, right.y, right.width, right.height - Row - 4f);
            HandleWeaponInput(canvas, weapon);
            _weaponState.Tick();
            WeaponPreviewRenderer.Draw(canvas, weapon, _weaponState);

            GUILayout.BeginArea(new Rect(right.x + 6f, canvas.yMax + 2f, right.width - 12f, Row));
            using (new EditorGUILayout.HorizontalScope())
            {
                Transport(ref _weaponState.Playing, ref _weaponState.TimeScale, () => _weaponState.Time = 0);
                EditorGUI.BeginChangeCheck();
                float t = Slider("Time", (float)_weaponState.Time, 0f, PreviewState.Loop, 36f);
                if (EditorGUI.EndChangeCheck())
                {
                    _weaponState.Time = t;
                    _weaponState.Playing = false;
                }
            }
            GUILayout.EndArea();
        }

        private void EnemyTabs(Rect area, Rect left, Rect right)
        {
            if (_enemy == null)
            {
                GUILayout.BeginArea(area);
                EditorGUILayout.HelpBox("Select an Enemy, or drop one into the field above.", MessageType.Info);
                GUILayout.EndArea();
                return;
            }

            if (_so == null || _so.targetObject != _enemy)
            {
                _so = new SerializedObject(_enemy);
            }
            _so.Update();

            int count = _enemy.Phases.Count;
            _enemyState.SelectedPhase = count == 0 ? 0 : Mathf.Clamp(_enemyState.SelectedPhase, 0, count - 1);
            int isolate = _tab == Tab.Movement && count > 0 ? _enemyState.SelectedPhase : -1;

            // Left: what is being edited on this tab.
            GUILayout.BeginArea(left);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField(_enemy.name, EditorStyles.largeLabel);

            bool editWeapon = false;
            switch (_tab)
            {
                case Tab.Movement:
                    if (count > 0)
                    {
                        int picked = EditorGUILayout.Popup("Phase", _enemyState.SelectedPhase, PhaseNames());
                        if (picked != _enemyState.SelectedPhase)
                        {
                            _enemyState.SelectedPhase = picked;
                            EnemyPanels.ReleaseFocus();
                        }
                        editWeapon = EnemyPanels.Movement(_so, _enemyState.SelectedPhase);
                    }
                    else
                    {
                        editWeapon = EnemyPanels.Movement(_so, -1);
                    }
                    break;

                case Tab.Phases:
                    _enemyState.SelectedPhase = EnemyPanels.PhaseOps(_enemy, _so, _enemyState.SelectedPhase);
                    count = _enemy.Phases.Count;
                    EditorGUILayout.Space(4f);
                    if (count > 0 && _enemyState.SelectedPhase >= 0 && _enemyState.SelectedPhase < count)
                    {
                        editWeapon = EnemyPanels.Phase(_so, _enemyState.SelectedPhase);
                        EditorGUILayout.Space(6f);
                        if (GUILayout.Button(new GUIContent("Start the fight here",
                                "Run the Phases and Enemy views from this phase, at the health the previous threshold leaves it.")))
                        {
                            StartHere(_enemyState.SelectedPhase);
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("No phases. With none, the enemy uses its top-level Movement, Weapon " +
                                                "and Attack Range forever. Add one to build a phased fight.", MessageType.Info);
                    }
                    break;
            }

            if (_tab == Tab.Enemy)
            {
                UnityEditor.Editor.CreateCachedEditor(_enemy, null, ref _enemyEditor);
                _enemyEditor!.OnInspectorGUI();
            }
            else
            {
                EditorGUILayout.Space(8f);
                _fullInspector = EditorGUILayout.Foldout(_fullInspector, "Full enemy inspector", true);
                if (_fullInspector)
                {
                    UnityEditor.Editor.CreateCachedEditor(_enemy, null, ref _enemyEditor);
                    _enemyEditor!.OnInspectorGUI();
                }
            }
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();

            _so.ApplyModifiedProperties();
            if (editWeapon)
            {
                _weaponFromEnemy = true;
                _weaponState.AsEnemy = true;
                _tab = Tab.Pattern;
                Repaint();
                return;
            }

            Divider(left, area);

            // Right: simulate, then draw.
            _sim.Run(_enemy, _enemyState, isolate);
            _enemyState.Tick(_sim.Seconds);
            int tick = _sim.TickAt(_enemyState.Time);

            float y = right.y;
            if (_tab != Tab.Movement && _enemy.Phases.Count > 0)
            {
                var strip = new Rect(right.x, y, right.width, PhaseTimeline.Height);
                int clicked = PhaseTimeline.Draw(strip, _sim, _enemyState.SelectedPhase, tick, out bool doubleClick);
                if (clicked >= 0)
                {
                    if (clicked != _enemyState.SelectedPhase)
                    {
                        EnemyPanels.ReleaseFocus();
                    }
                    _enemyState.SelectedPhase = clicked;
                    if (doubleClick)
                    {
                        StartHere(clicked);
                    }
                    Repaint();
                }
                y += PhaseTimeline.Height + 2f;
            }

            int rows = _tab == Tab.Movement ? 2 : 3;
            var canvas = new Rect(right.x, y, right.width, right.yMax - y - rows * Row - 4f);
            if (_canvas.HandleInput(canvas, _enemyState, _so))
            {
                Repaint();
            }
            EnemyWarnings.Collect(_enemy, _sim, _enemyState, _warnings);
            _canvas.Draw(canvas, _enemy, _enemyState, _sim, tick,
                         bullets: _tab == Tab.Enemy && _enemyState.Bullets,
                         playerRange: _tab != Tab.Movement,
                         warnings: _warnings);

            float ry = canvas.yMax + 2f;
            GUILayout.BeginArea(new Rect(right.x + 6f, ry, right.width - 12f, rows * Row));
            DummyRow();
            if (_tab != Tab.Movement)
            {
                FightRow();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                Transport(ref _enemyState.Playing, ref _enemyState.TimeScale, () => _enemyState.Time = 0);
                EditorGUI.BeginChangeCheck();
                float t = Slider("Time", (float)_enemyState.Time, 0f, Mathf.Max(0.1f, _sim.Seconds), 36f);
                if (EditorGUI.EndChangeCheck())
                {
                    _enemyState.Time = t;
                    _enemyState.Playing = false;
                }
            }
            GUILayout.EndArea();
        }

        private void DummyRow()
        {
            using var _ = new EditorGUILayout.HorizontalScope();
            GUILayout.Label("Dummy", EditorStyles.miniBoldLabel, GUILayout.Width(44f));
            _enemyState.Script = (EnemyPreviewState.DummyScript)EditorGUILayout.EnumPopup(
                _enemyState.Script, GUILayout.Width(110f));
            _enemyState.DummySpeed = Slider("Speed", _enemyState.DummySpeed, 0.5f, 15f, 40f);
            _enemyState.WanderSeed = Slider("Seed", _enemyState.WanderSeed, 0f, Mathf.PI * 2f, 34f);
            _enemyState.Slowed = GUILayout.Toggle(_enemyState.Slowed, new GUIContent("Slowed", "Enemy at half speed, as a slow debuff does."),
                                                  GUILayout.Width(62f));
        }

        private void FightRow()
        {
            using var _ = new EditorGUILayout.HorizontalScope();
            GUILayout.Label("Fight", EditorStyles.miniBoldLabel, GUILayout.Width(44f));
            float label = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 30f;
            _enemyState.Dps = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("DPS",
                "Damage per second the dummy deals while within its range. A steady drain, not per-shot."),
                _enemyState.Dps, GUILayout.Width(86f)));
            EditorGUIUtility.labelWidth = label;
            _enemyState.PlayerRange = Slider("Range", _enemyState.PlayerRange, 1f, 30f, 40f);
            _enemyState.StartHpPercent = Slider("HP %", _enemyState.StartHpPercent, 1f, 100f, 34f);
            _enemyState.FightSeconds = Slider("Length", _enemyState.FightSeconds, 10f, 600f, 42f);
        }

        /// <summary>Starts the fight at a phase, at the health the nearest earlier threshold leaves it.</summary>
        private void StartHere(int phase)
        {
            if (_enemy == null)
            {
                return;
            }
            _enemyState.StartPhase = phase;
            _enemyState.StartHpPercent = 100f;
            for (int i = phase - 1; i >= 0; i--)
            {
                if (_enemy.Phases[i]?.Transition is HealthBelowTransition health)
                {
                    _enemyState.StartHpPercent = health.Percent;
                    break;
                }
            }
            _enemyState.Time = 0;
            _enemyState.Playing = true;
        }

        private string[] PhaseNames()
        {
            var names = new string[_enemy!.Phases.Count];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = $"{i}. {_enemy.Phases[i]?.Name}";
            }
            return names;
        }

        private void Field(string path)
        {
            var property = _so?.FindProperty(path);
            if (property != null)
            {
                EditorGUILayout.PropertyField(property, true);
            }
        }

        private void HandleWeaponInput(Rect canvas, WeaponItem weapon)
        {
            var e = Event.current;
            if (!canvas.Contains(e.mousePosition))
            {
                return;
            }
            if (e.type == EventType.ScrollWheel)
            {
                float current = _weaponState.ZoomTiles > 0f
                    ? _weaponState.ZoomTiles
                    : WeaponPreviewRenderer.FitTiles(weapon, _weaponState);
                _weaponState.ZoomTiles = Mathf.Clamp(current * (1f + e.delta.y * 0.04f), 1f, 80f);
                e.Use();
                Repaint();
            }
            else if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0)
            {
                var v = e.mousePosition - canvas.center;
                if (v.sqrMagnitude > 4f)
                {
                    _weaponState.AimDegrees = Mathf.Atan2(-v.y, v.x) * Mathf.Rad2Deg;
                }
                e.Use();
                Repaint();
            }
        }

        private static void Transport(ref bool playing, ref float timeScale, System.Action restart)
        {
            if (GUILayout.Button(playing ? "Pause" : "Play", EditorStyles.miniButtonLeft, GUILayout.Width(46f)))
            {
                playing = !playing;
            }
            if (GUILayout.Button("Restart", EditorStyles.miniButtonRight, GUILayout.Width(52f)))
            {
                restart();
            }
            GUILayout.Label($"{timeScale:0.0}x", EditorStyles.miniLabel, GUILayout.Width(28f));
            timeScale = GUILayout.HorizontalSlider(timeScale, 0.1f, 4f, GUILayout.Width(60f));
        }

        private static float Slider(string label, float value, float min, float max, float labelWidth)
        {
            float saved = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = labelWidth;
            value = EditorGUILayout.Slider(label, value, min, max, GUILayout.MinWidth(120f));
            EditorGUIUtility.labelWidth = saved;
            return value;
        }

        private static void Divider(Rect left, Rect area) =>
            EditorGUI.DrawRect(new Rect(left.xMax, area.y, 1f, area.height), new Color(0f, 0f, 0f, 0.5f));
    }
}
