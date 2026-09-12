using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Vrox.Editor
{
    /// <summary>
    /// Captures the server's spawners so they can be drawn without the game
    /// running, and labels them in the Scene view.
    /// </summary>
    /// <remarks>
    /// The generator makes spawners per biome region, so where they land is only
    /// knowable after a realm is built — and building one no longer needs play
    /// mode. Without this, seeing the result of a regenerate would mean pressing
    /// Play every time.
    /// </remarks>
    public static class SnapshotSpawners
    {
        [MenuItem("Vrox/Snapshot Spawners")]
        public static void Capture()
        {
            var target = Object.FindAnyObjectByType<VroxSpawnerGizmos>();
            if (target == null)
            {
                Debug.LogError("Vrox: no VroxSpawnerGizmos in the scene to snapshot into. "
                             + "Add the component, or run Vrox > Create Scene.");
                return;
            }

            EditorConnection.Run("snapshot spawners", conn =>
            {
                // Recorded rather than referenced: the enemy colour comes from a
                // replicated row that will not be there once the connection
                // closes, so it has to be resolved now or the snapshot draws grey.
                var captured = conn.Db.Spawner.Iter().Select(spawner =>
                {
                    ushort bestDef = 0, bestWeight = 0;
                    foreach (var entry in spawner.Composition)
                    {
                        if (entry.Weight > bestWeight)
                        {
                            bestWeight = entry.Weight;
                            bestDef = entry.EnemyDefId;
                        }
                    }

                    return new VroxSpawnerGizmos.Captured
                    {
                        Id = spawner.Id,
                        X = spawner.X,
                        Y = spawner.Y,
                        Radius = spawner.Radius,
                        MaxAlive = spawner.MaxAlive,
                        Tint = bestDef != 0 && conn.Db.EnemyDef.Id.Find(bestDef) is { } def
                            ? PackedColour.Unpack(def.Colour)
                            : Color.grey,
                    };
                }).ToList();

                Undo.RecordObject(target, "Snapshot Spawners");
                target.Snapshot = captured;
                target.SnapshotTaken = System.DateTime.Now.ToString("HH:mm:ss");
                EditorUtility.SetDirty(target);
                SceneView.RepaintAll();

                Debug.Log($"Vrox: captured {captured.Count} spawner(s). They draw hollow "
                        + "because a snapshot is not live — regenerate the realm and this "
                        + "is stale until you take another.", target);
            });
        }

        /// <summary>
        /// Labels each spawner with its id and cap.
        /// </summary>
        /// <remarks>
        /// Separate from the component's own gizmos because <c>Handles</c> lives
        /// in the editor assembly, and a runtime component cannot reference it
        /// without a compile guard in the middle of the drawing code.
        ///
        /// Only when the object is selected. A realm has dozens of spawners and
        /// dozens of overlapping labels is worse than none.
        /// </remarks>
        [DrawGizmo(GizmoType.Selected)]
        private static void DrawLabels(VroxSpawnerGizmos gizmos, GizmoType type)
        {
            var live = VroxSpawnerGizmos.Live;
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = live != null ? Color.white : new Color(1f, 1f, 1f, 0.6f) },
            };

            if (live is { } conn)
            {
                foreach (var spawner in conn.Db.Spawner.Iter())
                {
                    Handles.Label(new Vector3(spawner.X, spawner.Y + spawner.Radius, 0f),
                                  $"#{spawner.Id}  max {spawner.MaxAlive}", style);
                }
                return;
            }

            foreach (var captured in gizmos.Snapshot)
            {
                Handles.Label(new Vector3(captured.X, captured.Y + captured.Radius, 0f),
                              $"#{captured.Id}  max {captured.MaxAlive}  "
                            + $"(snapshot {gizmos.SnapshotTaken})", style);
            }
        }
    }
}
