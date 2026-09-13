using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// The live pattern preview in the inspector's preview pane, for a selected weapon.
    /// </summary>
    /// <remarks>
    /// An <see cref="ObjectPreview"/> rather than a custom inspector on purpose:
    /// Odin draws the weapon inspector, including its <c>[SerializeReference]</c>
    /// pattern picker, and a <c>CustomEditor</c> for <see cref="WeaponItem"/> would
    /// replace that. A preview is added alongside whatever editor is drawing.
    ///
    /// The bigger view with aim, zoom and scrubbing is the Pattern tab of
    /// <see cref="EnemyDesignerWindow"/>; the Designer button opens it.
    /// </remarks>
    [CustomPreview(typeof(WeaponItem))]
    public sealed class WeaponItemPreview : ObjectPreview
    {
        private static readonly GUIContent Title = new("Pattern");

        private readonly PreviewState _state = new();

        public override bool HasPreviewGUI() => target is WeaponItem;

        public override GUIContent GetPreviewTitle() => Title;

        public override void OnPreviewSettings()
        {
            _state.Playing = GUILayout.Toggle(_state.Playing, _state.Playing ? "Pause" : "Play",
                                              EditorStyles.toolbarButton);
            _state.Mode = (PreviewState.ViewMode)EditorGUILayout.EnumPopup(
                _state.Mode, EditorStyles.toolbarPopup, GUILayout.Width(64f));
            if (GUILayout.Button("Designer", EditorStyles.toolbarButton) && target is WeaponItem w)
            {
                EnemyDesignerWindow.ShowWeapon(w);
            }
        }

        public override void OnPreviewGUI(Rect r, GUIStyle background)
        {
            if (target is not WeaponItem w)
            {
                return;
            }

            _state.Tick();
            WeaponPreviewRenderer.Draw(r, w, _state);

            if (_state.Playing && Event.current.type == EventType.Repaint)
            {
                InspectorRepaint.Request();
            }
        }
    }
}
