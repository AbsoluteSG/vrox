using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// A compact live fight in the inspector's preview pane, for a selected enemy.
    /// </summary>
    /// <remarks>
    /// An <see cref="ObjectPreview"/> for the same reason as <see cref="WeaponItemPreview"/>:
    /// it sits beside Odin's inspector instead of replacing it. Dummy, DPS and
    /// ranges are the defaults; the Designer button opens the full controls.
    /// </remarks>
    [CustomPreview(typeof(EnemyItem))]
    public sealed class EnemyItemPreview : ObjectPreview
    {
        private static readonly GUIContent Title = new("Fight");

        private readonly EnemyPreviewState _state = new();
        private readonly EnemySimulation _sim = new();
        private readonly EnemyCanvas _canvas = new();
        private Object? _initialisedFor;

        public override bool HasPreviewGUI() => target is EnemyItem;

        public override GUIContent GetPreviewTitle() => Title;

        public override void OnPreviewSettings()
        {
            _state.Playing = GUILayout.Toggle(_state.Playing, _state.Playing ? "Pause" : "Play",
                                              EditorStyles.toolbarButton);
            if (GUILayout.Button("Designer", EditorStyles.toolbarButton) && target is EnemyItem e)
            {
                EnemyDesignerWindow.ShowEnemy(e);
            }
        }

        public override void OnPreviewGUI(Rect r, GUIStyle background)
        {
            if (target is not EnemyItem e)
            {
                return;
            }
            if (_initialisedFor != e)
            {
                _initialisedFor = e;
                _state.Restart(Mathf.Round(Mathf.Max(1, e.MaxHp) / 90f));
            }

            _sim.Run(e, _state, -1);
            _state.Tick(_sim.Seconds);
            _canvas.Draw(r, e, _state, _sim, _sim.TickAt(_state.Time), bullets: true, playerRange: false, warnings: null);

            if (_state.Playing && Event.current.type == EventType.Repaint)
            {
                InspectorRepaint.Request();
            }
        }
    }
}
