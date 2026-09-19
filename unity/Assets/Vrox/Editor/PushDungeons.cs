using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Vrox.Tiles;

namespace Vrox.Editor
{
    /// <summary>
    /// Sends every <see cref="VroxDungeonLayout"/> in the open scene to the server.
    /// </summary>
    /// <remarks>
    /// Each layout is replaced wholesale — row, terrain, spawner templates and
    /// portal drops — in that order, because the server refuses terrain and
    /// spawners for a layout it has not been told about.
    ///
    /// Runs already open keep what they were opened with. Re-pushing changes the
    /// next run, not the one somebody is standing in.
    /// </remarks>
    public static class PushDungeons
    {
        [MenuItem("Vrox/Push Dungeon Layouts")]
        public static void PushMenu() => EditorConnection.Run("push dungeon layouts", Push);

        public static void Push(SpacetimeDB.Types.DbConnection conn)
        {
            var layouts = Object.FindObjectsByType<VroxDungeonLayout>(FindObjectsInactive.Exclude);
            if (layouts.Length == 0)
            {
                return;
            }

            var duplicate = layouts.GroupBy(l => l.Id).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
            {
                // Refused rather than pushed: the second silently replaces the first.
                Debug.LogError($"Vrox: dungeon layout id {duplicate.Key} is used by "
                             + string.Join(" and ", duplicate.Select(l => l.name))
                             + ". Give them different ids.", duplicate.First());
                return;
            }

            int pushed = 0;
            foreach (var layout in layouts)
            {
                if (PushOne(conn, layout))
                {
                    pushed++;
                }
            }
            Debug.Log($"Vrox: pushed {pushed} of {layouts.Length} dungeon layout(s).");
        }

        private static bool PushOne(SpacetimeDB.Types.DbConnection conn, VroxDungeonLayout layout)
        {
            string name = string.IsNullOrWhiteSpace(layout.DisplayName) ? layout.name : layout.DisplayName;

            if (layout.Id == 0)
            {
                Debug.LogError($"Vrox: dungeon layout \"{name}\" has id 0, which is the realm's map.", layout);
                return false;
            }
            if (layout.Size <= 0 || layout.Size % PushTerrain.ChunkSize != 0 || layout.Size > 1024)
            {
                Debug.LogError($"Vrox: dungeon layout \"{name}\" is {layout.Size} tiles; it must be a "
                             + $"multiple of {PushTerrain.ChunkSize}, up to 1024.", layout);
                return false;
            }

            var area = layout.GetComponentInChildren<VroxArea>();
            if (area == null)
            {
                Debug.LogError($"Vrox: dungeon layout \"{name}\" has no VroxArea beneath it, so it has "
                             + "no terrain to push.", layout);
                return false;
            }
            if (layout.Exit == null)
            {
                // No default: an exit dropped somewhere guessed is a dungeon you
                // cannot leave except by dying.
                Debug.LogError($"Vrox: dungeon layout \"{name}\" has no Exit assigned.", layout);
                return false;
            }

            var flat = PushTerrain.Flatten(area, layout.Size);

            Vector2 spawn;
            if (area.PlayerSpawn != null)
            {
                spawn = area.transform.InverseTransformPoint(area.PlayerSpawn.position);
            }
            else if (flat.Seen.Count > 0)
            {
                spawn = new Vector2((float)flat.Seen.Average(p => p.x) + 0.5f,
                                    (float)flat.Seen.Average(p => p.y) + 0.5f);
            }
            else
            {
                Debug.LogError($"Vrox: dungeon layout \"{name}\" has nothing painted.", layout);
                return false;
            }
            Vector2 exit = area.transform.InverseTransformPoint(layout.Exit.position);

            // Refused rather than nudged. A realm spawn is rescued because the
            // realm is shared; a dungeon is authored for this, and a wrong marker
            // is better fixed than silently moved.
            if (!Walkable(flat, spawn) || !Walkable(flat, exit))
            {
                Debug.LogError($"Vrox: dungeon layout \"{name}\": the player spawn ({spawn.x:0.0}, {spawn.y:0.0}) "
                             + $"and exit ({exit.x:0.0}, {exit.y:0.0}) must both be on painted, walkable ground.",
                               layout);
                return false;
            }

            var drops = layout.DroppedBy
                .Where(d => d.Enemy != null && d.ChancePercent > 0f)
                .Select(d => new SpacetimeDB.Types.LayoutDrop
                {
                    EnemyDefId = d.Enemy!.Id,
                    ChancePercent = d.ChancePercent,
                })
                .ToList();

            conn.Reducers.UpsertDungeonLayout(
                layout.Id, name, (uint)layout.Size,
                spawn.x, spawn.y, exit.x, exit.y,
                PackedColour.Pack(layout.Tint), (uint)layout.LifetimeSeconds, drops);

            conn.Reducers.ClearLayout(layout.Id);
            int chunks = 0;
            foreach (var (cell, tiles) in PushTerrain.Chunks(flat))
            {
                conn.Reducers.UpsertLayoutChunk(layout.Id, cell, tiles);
                chunks++;
            }

            int spawners = 0;
            int skipped = 0;
            foreach (var spawner in layout.GetComponentsInChildren<VroxSpawner>(false))
            {
                var population = spawner.Population;
                var composition = population == null
                    ? new List<SpacetimeDB.Types.AreaEntry>()
                    : population.Usable
                        .Select(e => new SpacetimeDB.Types.AreaEntry
                        {
                            EnemyDefId = e.Enemy!.Id,
                            Weight = (ushort)Mathf.Clamp(e.Weight, 0, 1000),
                            MaxAlive = (ushort)Mathf.Clamp(e.MaxAlive, 0, 200),
                        })
                        .ToList();
                if (population == null || composition.Count == 0)
                {
                    skipped++;
                    continue;
                }

                Vector2 p = area.transform.InverseTransformPoint(spawner.transform.position);
                conn.Reducers.AddLayoutSpawner(
                    layout.Id, composition, p.x, p.y, spawner.Radius,
                    (ushort)Mathf.Clamp(population.MaxAlive, 0, 200),
                    (ushort)Mathf.Clamp(population.IntervalMs, 100, 30000));
                spawners++;
            }

            PushTerrain.Report(flat, area, $"dungeon layout \"{name}\"");
            if (skipped > 0)
            {
                Debug.LogWarning($"Vrox: {skipped} spawner(s) in \"{name}\" have no usable Population and "
                               + "were skipped.", layout);
            }
            if (drops.Count == 0)
            {
                Debug.LogWarning($"Vrox: nothing drops a portal to \"{name}\", so it can only be reached "
                               + "with debug_open_portal.", layout);
            }

            Debug.Log($"Vrox: pushed dungeon \"{name}\" (id {layout.Id}) — {flat.Painted} tile(s) across "
                    + $"{chunks} chunk(s), {spawners} spawner(s), {drops.Count} portal drop(s).", layout);
            return true;
        }

        private static bool Walkable(PushTerrain.FlatArea flat, Vector2 at)
        {
            int x = Mathf.FloorToInt(at.x);
            int y = Mathf.FloorToInt(at.y);
            return x >= 0 && y >= 0 && x < flat.Span && y < flat.Span
                && (flat.Flags[y * flat.Span + x] & 1) == 0
                && flat.Seen.Contains(new Vector2Int(x, y));
        }
    }
}
