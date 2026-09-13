using UnityEditor;
using UnityEngine;

namespace Vrox.Editor
{
    /// <summary>World tiles to pixels, and the tile grid, shared by every designer preview.</summary>
    internal static class PreviewCanvas
    {
        public static readonly Color Background = new(0.11f, 0.11f, 0.12f);
        private static readonly Color GridColour = new(1f, 1f, 1f, 0.045f);
        private static readonly Color AxisColour = new(1f, 1f, 1f, 0.12f);

        /// <summary>A view onto the world, in the local coordinates of a clipped rectangle.</summary>
        public readonly struct View
        {
            public readonly Vector2 Size;
            public readonly Vector2 Centre;
            public readonly float Ppt;
            public readonly Vector2 Focus;

            /// <param name="halfTiles">Tiles from the centre to the nearer edge.</param>
            /// <param name="focus">World point drawn at the centre.</param>
            public View(Vector2 size, float halfTiles, Vector2 focus)
            {
                Size = size;
                Centre = size * 0.5f;
                Ppt = Mathf.Min(size.x, size.y) * 0.5f / Mathf.Max(halfTiles, 0.01f);
                Focus = focus;
            }

            public View(Vector2 size, float halfTiles) : this(size, halfTiles, Vector2.zero)
            {
            }

            public Vector3 ToScreen(Vector2 world) =>
                new(Centre.x + (world.x - Focus.x) * Ppt, Centre.y - (world.y - Focus.y) * Ppt, 0f);

            public Vector2 ToWorld(Vector2 local) =>
                new(Focus.x + (local.x - Centre.x) / Ppt, Focus.y - (local.y - Centre.y) / Ppt);

            public bool Visible(Vector3 p, float margin) =>
                p.x > -margin && p.y > -margin && p.x < Size.x + margin && p.y < Size.y + margin;
        }

        /// <summary>Tile lines, thinned out as the view zooms out; the axes through the world origin brighter.</summary>
        public static void DrawGrid(View view)
        {
            float step = view.Ppt >= 8f ? 1f : view.Ppt * 5f >= 8f ? 5f : 10f;
            float spacing = Mathf.Max(step * view.Ppt, 2f);
            var origin = view.ToScreen(Vector2.zero);

            int x0 = Mathf.FloorToInt(-origin.x / spacing);
            int x1 = Mathf.CeilToInt((view.Size.x - origin.x) / spacing);
            for (int i = x0; i <= x1; i++)
            {
                Handles.color = i == 0 ? AxisColour : GridColour;
                float x = origin.x + i * spacing;
                Handles.DrawLine(new Vector3(x, 0f), new Vector3(x, view.Size.y));
            }

            int y0 = Mathf.FloorToInt(-origin.y / spacing);
            int y1 = Mathf.CeilToInt((view.Size.y - origin.y) / spacing);
            for (int i = y0; i <= y1; i++)
            {
                Handles.color = i == 0 ? AxisColour : GridColour;
                float y = origin.y + i * spacing;
                Handles.DrawLine(new Vector3(0f, y), new Vector3(view.Size.x, y));
            }
        }
    }

    /// <summary>
    /// Repaints the inspectors at about 30 frames a second, for animated preview panes.
    /// </summary>
    /// <remarks>
    /// An <see cref="ObjectPreview"/> has no Repaint of its own, and the editor
    /// drawing the inspector is Odin's, so <c>RequiresConstantRepaint</c> is not
    /// available. The inspector window's type is internal, hence matching by name;
    /// if Unity renames it, previews still draw but only animate when something
    /// else repaints the inspector.
    ///
    /// Requested from a visible pane's repaint, so it stops by itself as soon as
    /// the pane is collapsed or the asset deselected.
    /// </remarks>
    internal static class InspectorRepaint
    {
        private static bool _pumping;
        private static double _next;

        public static void Request()
        {
            if (_pumping)
            {
                return;
            }
            _pumping = true;
            EditorApplication.update += Pump;
        }

        private static void Pump()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _next)
            {
                return;
            }
            EditorApplication.update -= Pump;
            _pumping = false;
            _next = now + 1.0 / 30.0;

            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window.GetType().Name == "InspectorWindow")
                {
                    window.Repaint();
                }
            }
        }
    }
}
