using System.Collections.Generic;
using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Draws the server's spawners in the Scene view.
    /// </summary>
    /// <remarks>
    /// <see cref="VroxSpawner"/> already draws a gizmo for a spawner placed by
    /// hand. Almost none are: the generator makes one per biome region, so the
    /// spawners actually running exist only as rows and nothing in the scene
    /// represents them. This draws those.
    ///
    /// Two sources, deliberately. In play mode it reads the live rows, so what is
    /// drawn is what the server is running right now. Out of play mode there is
    /// no connection, so it draws a snapshot taken by <c>Vrox → Snapshot
    /// Spawners</c> — which matters because regenerating a realm no longer needs
    /// play mode, and looking at where the spawners landed is the reason to
    /// regenerate.
    ///
    /// A snapshot is explicitly a snapshot: it is stamped with when it was taken
    /// and is drawn differently from live data, because a stale picture of where
    /// enemies come from is worse than no picture.
    /// </remarks>
    public sealed class VroxSpawnerGizmos : MonoBehaviour
    {
        /// <summary>One spawner, as captured for drawing without a connection.</summary>
        [System.Serializable]
        public struct Captured
        {
            public int Id;
            public float X;
            public float Y;
            public float Radius;
            public int MaxAlive;

            /// <summary>Colour of the commonest enemy in the mix.</summary>
            public Color Tint;
        }

        [Tooltip("Draw the area each spawner fills, not just its centre.")]
        public bool ShowRadius = true;

        [Tooltip("Fill the discs as well as outlining them. Off is easier to read " +
                 "when spawners overlap.")]
        public bool Fill = true;

        [Range(0f, 0.5f)]
        public float FillAlpha = 0.12f;

        [Tooltip("Marker size at the spawner's centre, in tiles.")]
        [Range(0f, 2f)]
        public float CentreSize = 0.5f;

        [Header("Snapshot")]
        [Tooltip("Filled by Vrox > Snapshot Spawners. Used only when the game is not " +
                 "running; live rows win whenever there is a connection.")]
        public List<Captured> Snapshot = new();

        [Tooltip("When the snapshot was taken. Shown so a stale one is obvious.")]
        public string SnapshotTaken = "";

        /// <summary>
        /// The connection to read spawners from, or null to fall back to the
        /// snapshot.
        /// </summary>
        /// <remarks>
        /// One definition, because the discs and the labels are drawn by
        /// different objects and Unity does not promise which runs first. Deciding
        /// separately let the labels say "snapshot" over live discs for a frame.
        ///
        /// A connection with no spawner rows counts as not live: the realm has
        /// not been generated yet, and the snapshot is the more useful of two
        /// empty answers.
        /// </remarks>
        public static SpacetimeDB.Types.DbConnection? Live =>
            VroxNet.Instance?.Conn is { } conn && conn.Db.Spawner.Count > 0 ? conn : null;

        private void OnDrawGizmos()
        {
            if (Live is { } conn)
            {
                foreach (var spawner in conn.Db.Spawner.Iter())
                {
                    Draw(spawner.X, spawner.Y, spawner.Radius, TintFor(conn, spawner), live: true);
                }
                return;
            }

            foreach (var captured in Snapshot)
            {
                Draw(captured.X, captured.Y, captured.Radius, captured.Tint, live: false);
            }
        }

        /// <summary>
        /// The colour of whichever enemy this spawner makes most of.
        /// </summary>
        /// <remarks>
        /// By weight, so a region reads as the thing it mostly produces rather
        /// than whichever entry was authored first. Grey when the mix is empty or
        /// the archetype has not replicated — a spawner drawn in the colour of an
        /// enemy it does not make would be worse than one drawn in no colour.
        /// </remarks>
        private static Color TintFor(SpacetimeDB.Types.DbConnection conn,
                                     SpacetimeDB.Types.Spawner spawner)
        {
            ushort bestDef = 0;
            ushort bestWeight = 0;
            foreach (var entry in spawner.Composition)
            {
                if (entry.Weight > bestWeight)
                {
                    bestWeight = entry.Weight;
                    bestDef = entry.EnemyDefId;
                }
            }

            return bestDef != 0 && conn.Db.EnemyDef.Id.Find(bestDef) is { } def
                ? PackedColour.Unpack(def.Colour)
                : Color.grey;
        }

        private void Draw(float x, float y, float radius, Color colour, bool live)
        {
            var centre = new Vector3(x, y, 0f);

            // A snapshot is drawn hollow and dimmer. The difference has to be
            // visible at a glance: the whole failure this guards against is
            // trusting a picture of a realm that has since been regenerated.
            if (Fill && live)
            {
                Gizmos.color = new Color(colour.r, colour.g, colour.b, FillAlpha);
                Gizmos.DrawSphere(centre, radius);
            }

            if (ShowRadius)
            {
                Gizmos.color = new Color(colour.r, colour.g, colour.b, live ? 0.9f : 0.45f);
                Gizmos.DrawWireSphere(centre, radius);
            }

            if (CentreSize > 0f)
            {
                Gizmos.color = new Color(colour.r, colour.g, colour.b, live ? 1f : 0.6f);
                Gizmos.DrawWireCube(centre, Vector3.one * CentreSize);
            }
        }
    }
}
