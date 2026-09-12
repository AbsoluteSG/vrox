using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;
using Vrox.Tiles;

namespace Vrox.Editor
{
    /// <summary>
    /// Flattens the scene's tilemaps into terrain and sends it to the server.
    /// </summary>
    /// <remarks>
    /// The server has to hold this: it decides where bodies may stand and where
    /// projectiles stop, so a client-only tilemap would be scenery that nothing
    /// collides with.
    ///
    /// Sent as chunks rather than tiles. A 40x40 area is nine rows this way and
    /// 1,600 the other, and the difference only grows.
    /// </remarks>
    public static class PushTerrain
    {
        private const int ChunkSize = 16;

        /// <summary>Fallback span, used only before the realm row has arrived.</summary>
        private const int DefaultWorldSize = 128;

        /// <summary>
        /// The server's world span, read from the replicated realm row.
        /// </summary>
        /// <remarks>
        /// Was a duplicated constant, which was fine only while the size could
        /// never change. Now that it is authored on the realm config, a stale copy
        /// here would push a tilemap sized for the wrong world — chunks past the
        /// end silently dropped, or a short map padded with solid tiles.
        /// </remarks>
        private static int WorldSizeFrom(SpacetimeDB.Types.DbConnection conn) =>
            conn.Db.Realm.Id.Find(1) is { Size: > 0 } realm
                ? (int)realm.Size
                : DefaultWorldSize;

        /// <summary>
        /// Tells the server where to put arriving players.
        /// </summary>
        /// <remarks>
        /// Falls back to the middle of what was actually painted rather than the
        /// middle of the world. With unpainted ground solid, the world centre is
        /// almost never inside a hand-drawn area, and a player would arrive sealed
        /// in rock.
        /// </remarks>
        private static void PushSpawnPoint(SpacetimeDB.Types.DbConnection conn, VroxArea area,
                                           HashSet<Vector2Int> painted, byte[] flags, int span)
        {
            float x, y;

            if (area.PlayerSpawn != null)
            {
                x = area.PlayerSpawn.position.x;
                y = area.PlayerSpawn.position.y;
            }
            else if (painted.Count > 0)
            {
                x = (float)painted.Average(p => p.x) + 0.5f;
                y = (float)painted.Average(p => p.y) + 0.5f;
            }
            else
            {
                return;
            }

            int cx = Mathf.Clamp(Mathf.FloorToInt(x), 0, span - 1);
            int cy = Mathf.Clamp(Mathf.FloorToInt(y), 0, span - 1);
            if ((flags[cy * span + cx] & 1) != 0)
            {
                // Nearest walkable tile, searched outward. Arriving inside a wall
                // is unrecoverable, so this is nudged rather than trusted.
                var open = painted
                    .Where(p => (flags[p.y * span + p.x] & 1) == 0)
                    .OrderBy(p => (p.x + 0.5f - x) * (p.x + 0.5f - x) + (p.y + 0.5f - y) * (p.y + 0.5f - y))
                    .Cast<Vector2Int?>()
                    .FirstOrDefault();

                if (open is not { } found)
                {
                    Debug.LogError("Vrox: nothing walkable was painted, so there is nowhere to "
                                 + "spawn a player.", area);
                    return;
                }

                Debug.LogWarning($"Vrox: the spawn point at ({x:0.0}, {y:0.0}) is not walkable; "
                               + $"moved to ({found.x + 0.5f:0.0}, {found.y + 0.5f:0.0}).", area);
                x = found.x + 0.5f;
                y = found.y + 0.5f;
            }

            conn.Reducers.SetSpawn(x, y);
            Debug.Log($"Vrox: player spawn at ({x:0.0}, {y:0.0}).");
        }

        public static void Push(SpacetimeDB.Types.DbConnection conn)
        {
            var area = Object.FindAnyObjectByType<VroxArea>();
            if (area == null)
            {
                return;
            }

            int worldSize = WorldSizeFrom(conn);
            int span = Mathf.CeilToInt((float)worldSize / ChunkSize) * ChunkSize;
            var flags = new byte[span * span];
            var hazard = new byte[span * span];
            var biome = new byte[span * span];
            var weight = new byte[span * span];

            for (int i = 0; i < weight.Length; i++)
            {
                weight[i] = (byte)Mathf.Clamp(area.DefaultSpawnWeight, 0, 255);
            }

            // Everywhere starts out of bounds and painting carves the walkable
            // area out of it. Starting open and painting walls is the other way
            // round, and only suits a map with no edges.
            if (area.UnpaintedIsSolid)
            {
                for (int i = 0; i < flags.Length; i++)
                {
                    flags[i] = 0b11;
                }
            }

            int painted = 0;
            int foreign = 0;
            int outside = 0;
            var seen = new HashSet<Vector2Int>();

            foreach (var layer in area.Layers.Where(l => l != null))
            {
                var bounds = layer.cellBounds;
                foreach (var pos in bounds.allPositionsWithin)
                {
                    var tile = layer.GetTile(pos);
                    if (tile == null)
                    {
                        continue;
                    }

                    int x = pos.x;
                    int y = pos.y;
                    if (x < 0 || y < 0 || x >= span || y >= span)
                    {
                        // Counted rather than skipped quietly. A map drawn around
                        // the origin sits half in negative cells, and every one of
                        // them would vanish with no indication that most of the
                        // area never reached the server.
                        outside++;
                        continue;
                    }
                    seen.Add(new Vector2Int(x, y));

                    if (tile is not VroxTile vrox)
                    {
                        // Counted, not guessed at. Treating an unknown tile as
                        // floor silently lets players walk through scenery; as
                        // wall it silently blocks corridors. Either way the map
                        // would be wrong in a way nothing reports.
                        foreign++;
                        continue;
                    }

                    int index = y * span + x;
                    flags[index] = vrox.Flags;
                    hazard[index] = (byte)Mathf.Clamp(vrox.HazardDamage, 0, 255);
                    biome[index] = (byte)Mathf.Clamp(vrox.BiomeId, 0, 255);
                    painted++;
                }
            }

            if (area.SpawnWeights != null)
            {
                foreach (var pos in area.SpawnWeights.cellBounds.allPositionsWithin)
                {
                    if (area.SpawnWeights.GetTile(pos) is not VroxTile marker)
                    {
                        continue;
                    }
                    int x = pos.x, y = pos.y;
                    if (x >= 0 && y >= 0 && x < span && y < span)
                    {
                        weight[y * span + x] = (byte)Mathf.Clamp(marker.SpawnWeight, 0, 255);
                    }
                }
            }

            // Solid ground is never spawnable, whatever the weight layer says.
            // Otherwise an enemy appears inside a wall and cannot move.
            for (int i = 0; i < flags.Length; i++)
            {
                if ((flags[i] & 1) != 0)
                {
                    weight[i] = 0;
                }
            }

            conn.Reducers.ClearTerrain();

            int chunks = span / ChunkSize;
            for (int cy = 0; cy < chunks; cy++)
            {
                for (int cx = 0; cx < chunks; cx++)
                {
                    var tiles = new List<SpacetimeDB.Types.TileData>(ChunkSize * ChunkSize);
                    for (int ty = 0; ty < ChunkSize; ty++)
                    {
                        for (int tx = 0; tx < ChunkSize; tx++)
                        {
                            int index = (cy * ChunkSize + ty) * span + cx * ChunkSize + tx;
                            tiles.Add(new SpacetimeDB.Types.TileData
                            {
                                Flags = flags[index],
                                SpawnWeight = weight[index],
                                Hazard = hazard[index],
                                Biome = biome[index],
                            });
                        }
                    }
                    conn.Reducers.UpsertTerrainChunk((uint)((cx << 16) | cy), tiles);
                }
            }

            PushSpawnPoint(conn, area, seen, flags, span);

            if (foreign > 0)
            {
                Debug.LogWarning($"Vrox: {foreign} painted cell(s) use a plain Tile rather than a "
                               + "VroxTile, so their meaning is unknown and were left as they were. "
                               + "Create tiles via Assets > Create > Vrox > Tile.", area);
            }
            if (outside > 0)
            {
                Debug.LogError($"Vrox: {outside} painted cell(s) are outside the world "
                             + $"(0,0)-({worldSize},{worldSize}) and did not reach the server. "
                             + "Tilemap cell coordinates are world tile coordinates, so move the "
                             + "drawing into the positive quadrant.", area);
            }
            if (painted == 0)
            {
                Debug.LogError("Vrox: no usable tiles were exported. With Unpainted Is Solid on, "
                             + "that leaves the whole world impassable.", area);
            }
            Debug.Log($"Vrox: pushed terrain — {painted} tile(s) across {chunks * chunks} chunk(s).");
        }
    }
}
