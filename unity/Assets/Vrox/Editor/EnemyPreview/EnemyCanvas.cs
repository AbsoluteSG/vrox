using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;
using View = Vrox.Editor.PreviewCanvas.View;

namespace Vrox.Editor
{
    /// <summary>
    /// Draws a simulated fight — enemy, dummy, paths, ranges and bullets — and lets
    /// the ranges and the dummy be dragged.
    /// </summary>
    /// <remarks>
    /// One per preview surface: it remembers the last repaint's view and handle
    /// positions, because mouse events arrive between repaints and have to be
    /// matched against what was drawn.
    ///
    /// The world origin is the enemy's spawn point.
    /// </remarks>
    internal sealed class EnemyCanvas
    {
        private static readonly Color AggroColour = new(0.45f, 0.72f, 1f, 0.55f);
        private static readonly Color AttackColour = new(1f, 0.45f, 0.4f, 0.55f);
        private static readonly Color PreferredColour = new(0.55f, 1f, 0.55f, 0.55f);
        private static readonly Color LeashColour = new(1f, 1f, 1f, 0.18f);
        private static readonly Color PlayerRangeColour = new(1f, 1f, 1f, 0.12f);

        private struct Knob
        {
            public Vector2 Screen;
            public Vector2 Centre;
            public string Path;
            public float Min;
            public float Max;
        }

        private readonly List<Knob> _knobs = new();
        private Knob _dragging;
        private int _mode; // 0 idle, 1 knob, 2 dummy
        private View _view;
        private bool _hasView;
        private float _lastHalf = 10f;
        private Vector3[] _points = new Vector3[512];

        private static GUIStyle? _header;
        private static GUIStyle? _sub;
        private static GUIStyle? _warning;

        /// <param name="bullets">Draw the enemy's fire. Off for a movement-only view.</param>
        /// <param name="playerRange">Draw the range the dummy deals damage within.</param>
        public void Draw(Rect rect, EnemyItem e, EnemyPreviewState s, EnemySimulation sim, int tick,
                         bool bullets, bool playerRange, List<string>? warnings)
        {
            if (Event.current.type != EventType.Repaint || sim.Ticks.Count == 0)
            {
                return;
            }

            tick = Mathf.Clamp(tick, 0, sim.Ticks.Count - 1);
            var now = sim.Ticks[tick];
            var phase = sim.Phases[now.Phase];
            float half = s.ZoomTiles > 0f ? s.ZoomTiles : Fit(sim);
            _lastHalf = half;

            EditorGUI.DrawRect(rect, PreviewCanvas.Background);
            GUI.BeginClip(rect);
            _view = new View(rect.size, half, Vector2.zero);
            _hasView = true;
            PreviewCanvas.DrawGrid(_view);

            var enemy = _view.ToScreen(now.Enemy);
            var dummy = _view.ToScreen(now.Dummy);
            var home = _view.ToScreen(Vector2.zero);
            var tint = new Color(e.Tint.r, e.Tint.g, e.Tint.b, 1f);

            DrawPaths(sim, tick, tint);
            DrawPhaseMarkers(sim, tick);

            // Home.
            Handles.color = new Color(1f, 1f, 1f, 0.4f);
            Handles.DrawLine(home + new Vector3(-5f, 0f), home + new Vector3(5f, 0f));
            Handles.DrawLine(home + new Vector3(0f, -5f), home + new Vector3(0f, 5f));

            _knobs.Clear();
            if (s.Ranges)
            {
                DrawRanges(sim, phase, now, enemy, dummy, home);
            }
            if (playerRange)
            {
                Handles.color = PlayerRangeColour;
                Handles.DrawWireDisc(enemy, Vector3.forward, s.PlayerRange * _view.Ppt);
            }

            if (bullets)
            {
                DrawFire(sim, tick, s.Trails);
            }

            if (now.HasTarget)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.3f);
                Handles.DrawDottedLine(enemy, dummy, 3f);
            }

            // Dummy player.
            Handles.color = new Color(0.9f, 0.95f, 1f, 0.9f);
            Handles.DrawSolidDisc(dummy, Vector3.forward, Mathf.Max(3f, DummyPlayer.Radius * _view.Ppt));

            // Enemy, faded when untargetable and ringed in gold when invulnerable.
            float radius = Mathf.Max(3f, Mathf.Clamp(e.Radius, 0.1f, 5f) * _view.Ppt);
            Handles.color = phase.Untargetable ? new Color(tint.r, tint.g, tint.b, 0.35f) : tint;
            Handles.DrawSolidDisc(enemy, Vector3.forward, radius);
            if (phase.Invulnerable)
            {
                Handles.color = new Color(1f, 0.85f, 0.3f, 1f);
                Handles.DrawWireDisc(enemy, Vector3.forward, radius + 2f, 2f);
            }

            foreach (var knob in _knobs)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.9f);
                Handles.DrawSolidDisc(knob.Screen, Vector3.forward, 4.5f);
                Handles.color = new Color(0f, 0f, 0f, 0.8f);
                Handles.DrawWireDisc(knob.Screen, Vector3.forward, 4.5f);
            }

            GUI.EndClip();
            DrawHud(rect, e, sim, now, phase, tick, warnings);
        }

        /// <summary>Drag the dummy anywhere; drag a white handle to resize its ring; scroll to zoom.</summary>
        /// <returns>True when something changed and the view should repaint.</returns>
        public bool HandleInput(Rect rect, EnemyPreviewState s, SerializedObject so)
        {
            var ev = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (!_hasView)
            {
                return false;
            }
            var local = ev.mousePosition - rect.position;

            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseDown when ev.button == 0 && rect.Contains(ev.mousePosition):
                    _mode = 2;
                    foreach (var knob in _knobs)
                    {
                        if (Vector2.Distance(knob.Screen, local) <= 9f)
                        {
                            _dragging = knob;
                            _mode = 1;
                            s.Playing = false;
                            break;
                        }
                    }
                    GUIUtility.hotControl = id;
                    Apply(local, s, so);
                    ev.Use();
                    return true;

                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    Apply(local, s, so);
                    ev.Use();
                    return true;

                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    _mode = 0;
                    ev.Use();
                    return true;

                case EventType.ScrollWheel when rect.Contains(ev.mousePosition):
                    float current = s.ZoomTiles > 0f ? s.ZoomTiles : _lastHalf;
                    s.ZoomTiles = Mathf.Clamp(current * (1f + ev.delta.y * 0.04f), 2f, 120f);
                    ev.Use();
                    return true;
            }
            return false;
        }

        private void Apply(Vector2 local, EnemyPreviewState s, SerializedObject so)
        {
            if (_mode == 2)
            {
                var world = _view.ToWorld(local);
                s.DummyAnchor = new Vector2(Mathf.Round(world.x * 4f) / 4f, Mathf.Round(world.y * 4f) / 4f);
                return;
            }
            if (_mode != 1)
            {
                return;
            }

            float value = Mathf.Clamp(Vector2.Distance(local, _dragging.Centre) / _view.Ppt,
                                      _dragging.Min, _dragging.Max);
            so.Update();
            var property = so.FindProperty(_dragging.Path);
            if (property is { propertyType: SerializedPropertyType.Float })
            {
                property.floatValue = Mathf.Round(value * 4f) / 4f;
                so.ApplyModifiedProperties();
            }
        }

        private float Fit(EnemySimulation sim)
        {
            float extent = 4f;
            int stride = Mathf.Max(1, sim.Ticks.Count / 300);
            for (int i = 0; i < sim.Ticks.Count; i += stride)
            {
                var t = sim.Ticks[i];
                extent = Mathf.Max(extent, Mathf.Max(t.Enemy.magnitude, t.Dummy.magnitude));
            }
            return Mathf.Max(extent + 2f, sim.LargestRange * 0.75f) * 1.1f;
        }

        private void DrawPaths(EnemySimulation sim, int tick, Color tint)
        {
            // The whole fight, faint.
            int stride = Mathf.Max(1, sim.Ticks.Count / 400);
            int n = 0;
            EnsurePoints(sim.Ticks.Count / stride + 2);
            for (int i = 0; i < sim.Ticks.Count; i += stride)
            {
                _points[n++] = _view.ToScreen(sim.Ticks[i].Enemy);
            }
            if (n > 1)
            {
                Handles.color = new Color(tint.r, tint.g, tint.b, 0.14f);
                Handles.DrawAAPolyLine(1.5f, n, _points);
            }

            // The last two seconds, bright.
            int from = Mathf.Max(0, tick - 40);
            n = 0;
            for (int i = from; i <= tick; i++)
            {
                _points[n++] = _view.ToScreen(sim.Ticks[i].Enemy);
            }
            if (n > 1)
            {
                Handles.color = new Color(tint.r, tint.g, tint.b, 0.6f);
                Handles.DrawAAPolyLine(2.5f, n, _points);
            }
        }

        private void DrawPhaseMarkers(EnemySimulation sim, int tick)
        {
            if (sim.Isolated || sim.Phases.Count < 2)
            {
                return;
            }
            EnsureStyles();
            for (int p = 1; p < sim.Phases.Count; p++)
            {
                int at = sim.EnteredAt[p];
                if (at < 0 || at > tick || at >= sim.Ticks.Count)
                {
                    continue;
                }
                var pos = _view.ToScreen(sim.Ticks[Mathf.Max(0, at - 1)].Enemy);
                Handles.color = new Color(1f, 0.85f, 0.3f, 0.9f);
                Handles.DrawSolidDisc(pos, Vector3.forward, 3.5f);
                GUI.Label(new Rect(pos.x + 5f, pos.y - 8f, 30f, 16f), p.ToString(), _sub);
            }
        }

        private void DrawRanges(EnemySimulation sim, EnemySimulation.Phase phase, EnemySimulation.Tick now,
                                Vector3 enemy, Vector3 dummy, Vector3 home)
        {
            string root = phase.Source < 0 ? "" : $"Phases.Array.data[{phase.Source}].";
            var rules = phase.Rules;

            if (rules.AggroRange > 0f && rules.Kind != BehaviourKind.Wander && rules.Kind != BehaviourKind.Static)
            {
                Ring(enemy, rules.AggroRange, AggroColour, 35f, root + "Movement.NoticeRange", 1f, 60f);
            }
            Ring(enemy, phase.AttackRange, AttackColour, -35f, root + "AttackRange", 0f, 60f);

            switch (rules.Kind)
            {
                case BehaviourKind.Orbit:
                    Ring(rules.Pivot == OrbitPivot.SpawnPoint ? home : dummy, rules.PreferredRange,
                         PreferredColour, 90f, root + "Movement.Radius", 1f, 30f);
                    break;
                case BehaviourKind.KeepDistance:
                    Ring(dummy, rules.PreferredRange, PreferredColour, 90f, root + "Movement.Range", 1f, 30f);
                    break;
                case BehaviourKind.Wander:
                    Ring(home, EnemyMath.Leash(rules.PreferredRange), LeashColour, 135f,
                         root + "Movement.Leash", 1f, 20f);
                    break;
            }

            // Behaviours that fall back to wandering with no target are leashed too,
            // at the default of 4 tiles unless their preferred range says otherwise.
            if (!now.HasTarget && rules.Kind is BehaviourKind.Chase or BehaviourKind.KeepDistance
                || !now.HasTarget && rules.Kind == BehaviourKind.Orbit && rules.Pivot == OrbitPivot.Player)
            {
                Handles.color = LeashColour;
                Handles.DrawWireDisc(home, Vector3.forward, EnemyMath.Leash(rules.PreferredRange) * _view.Ppt);
            }
        }

        private void Ring(Vector3 centre, float tiles, Color colour, float knobDegrees, string path,
                          float min, float max)
        {
            if (tiles <= 0f)
            {
                return;
            }
            float r = tiles * _view.Ppt;
            Handles.color = colour;
            Handles.DrawWireDisc(centre, Vector3.forward, r, 1.5f);

            float rad = knobDegrees * Mathf.Deg2Rad;
            _knobs.Add(new Knob
            {
                Screen = new Vector2(centre.x + Mathf.Cos(rad) * r, centre.y - Mathf.Sin(rad) * r),
                Centre = centre,
                Path = path,
                Min = min,
                Max = max,
            });
        }

        private void DrawFire(EnemySimulation sim, int tick, bool trails)
        {
            double nowSeconds = EnemySimulation.TimeOf(tick);
            for (int i = sim.Fires.Count - 1; i >= 0; i--)
            {
                var fire = sim.Fires[i];
                if (fire.Tick > tick)
                {
                    continue;
                }
                double firedAt = EnemySimulation.TimeOf(fire.Tick);
                float age = (float)(nowSeconds - firedAt);
                if (age > 5.1f)
                {
                    // Server lifetimes are clamped to 5 s and fires are in tick order.
                    break;
                }
                if (fire.Weapon == null || age > WeaponPreviewRenderer.MaxLife(fire.Weapon))
                {
                    continue;
                }
                WeaponPreviewRenderer.DrawVolleyAt(_view, fire.Weapon, fire.Origin, fire.Aim, firedAt, age, trails);
            }
        }

        private static void DrawHud(Rect rect, EnemyItem e, EnemySimulation sim, EnemySimulation.Tick now,
                                    EnemySimulation.Phase phase, int tick, List<string>? warnings)
        {
            EnsureStyles();
            float x = rect.x + 6f;
            float width = rect.width - 12f;
            float y = rect.y + 4f;

            string weapon = phase.Weapon != null ? phase.Weapon.name : "no weapon";
            string title = phase.Source < 0
                ? $"{e.name}  ·  {phase.Rules.Kind}  ·  {weapon}"
                : sim.Isolated
                    ? $"Phase {phase.Source} \"{phase.Name}\" alone  ·  {phase.Rules.Kind}  ·  {weapon}"
                    : $"Phase {phase.Source} \"{phase.Name}\"  ·  {phase.Rules.Kind}  ·  {weapon}";
            GUI.Label(new Rect(x, y, width, 16f), title, _header);
            y += 16f;

            string state = now.HasTarget ? "target" : "no target";
            if (now.InRange)
            {
                state += "  ·  in attack range";
            }
            GUI.Label(new Rect(x, y, width, 16f),
                      $"t {EnemySimulation.TimeOf(tick):0.00}s  ·  HP {now.Hp}/{sim.MaxHp}  ·  {state}", _sub);
            y += 17f;

            var bar = new Rect(x, y, Mathf.Min(160f, width), 4f);
            EditorGUI.DrawRect(bar, new Color(1f, 1f, 1f, 0.12f));
            bar.width *= now.Hp / (float)Mathf.Max(1, sim.MaxHp);
            EditorGUI.DrawRect(bar, new Color(0.85f, 0.3f, 0.3f));
            y += 10f;

            if (warnings != null)
            {
                foreach (var warning in warnings)
                {
                    var content = new GUIContent("⚠ " + warning);
                    float h = _warning!.CalcHeight(content, width);
                    GUI.Label(new Rect(x, y, width, h), content, _warning);
                    y += h;
                }
            }

            GUI.Label(new Rect(x, rect.yMax - 16f, width, 14f),
                      "Open ground: no walls, one player, never dormant. Drag the dummy; drag white handles to resize.",
                      _sub);
        }

        private void EnsurePoints(int count)
        {
            if (_points.Length < count)
            {
                _points = new Vector3[Mathf.NextPowerOfTwo(count)];
            }
        }

        private static void EnsureStyles()
        {
            _header ??= new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.92f, 0.92f, 0.92f) } };
            _sub ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.7f, 0.7f, 0.72f) } };
            _warning ??= new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                normal = { textColor = new Color(1f, 0.78f, 0.3f) },
            };
        }
    }
}
