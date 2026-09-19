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
    ///
    /// The flattening is shared with <see cref="PushDungeons"/>, so a realm and a
    /// dungeon painted with the same tiles mean the same thing on the server.
    /// </remarks>
    public static class PushTerrain
    {
        /// <summary>Must match the server's ChunkSize.</summary>
        internal const int ChunkSize = 16;

        /// <summary>Fallback span, used only before the realm row has arrived.</summary>
        private const int DefaultWorldSize = 128;

        /// <summary>An area's tilemaps merged into per-tile arrays, row-major, span by span.</summary>
        internal sealed class FlatArea
        {
            public int Span;
            public byte[] Flags = System.Array.Empty<byte>();
            public byte[] Hazard = System.Array.Empty<byte>();
            public byte[] Biome = System.Array.Empty<byte>();
            public byte[] Weight = System.Array.Empty<byte>();

            /// <summary>Cells that had any tile painted, inside the span.</summary>
            public HashSet<Vector2Int> Seen = new();

            public int Painted;
            public int Foreign;
            public int Outside;
        }

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
            // The realm's area is the one that is not part of a dungeon layout.
            // Picking one of those up here would paint a dungeon over the realm.
            var area = Object.FindObjectsByType<VroxArea>(FindObjectsInactive.Exclude)
                .FirstOrDefault(a => a.GetComponentInParent<VroxDungeonLayout>() == null);
            if (area == null)
            {
                return;
            }

            int worldSize = WorldSizeFrom(conn);
            int span = Mathf.CeilToInt((float)worldSize / ChunkSize) * ChunkSize;
            var flat = Flatten(area, span);

            conn.Reducers.ClearTerrain();
            int chunks = 0;
            foreach (var (cell, tiles) in Chunks(flat))
            {
                conn.Reducers.UpsertTerrainChunk(cell, tiles);
                chunks++;
            }

            PushSpawnPoint(conn, area, flat.Seen, flat.Flags, span);
            Report(flat, area, "the realm");
            Debug.Log($"Vrox: pushed terrain — {flat.Painted} tile(s) across {chunks} chunk(s).");
        }

        /// <summary>Merges an area's layers into tile arrays, span tiles square from the Grid's origin.</summary>
        internal static FlatArea Flatten(VroxArea area, int span)
        {
            var flat = new FlatArea
            {
                Span = span,
                Flags = new byte[span * span],
                Hazard = new byte[span * span],
                Biome = new byte[span * span],
                Weight = new byte[span * span],
            };

            for (int i = 0; i < flat.Weight.Length; i++)
            {
                flat.Weight[i] = (byte)Mathf.Clamp(area.DefaultSpawnWeight, 0, 255);
            }

            // Everywhere starts out of bounds and painting carves the walkable
            // area out of it. Starting open and painting walls is the other way
            // round, and only suits a map with no edges.
            if (area.UnpaintedIsSolid)
            {
                for (int i = 0; i < flat.Flags.Length; i++)
                {
                    flat.Flags[i] = 0b11;
                }
            }

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
                        flat.Outside++;
                        continue;
                    }
                    flat.Seen.Add(new Vector2Int(x, y));

                    if (tile is not VroxTile vrox)
                    {
                        // Counted, not guessed at. Treating an unknown tile as
                        // floor silently lets players walk through scenery; as
                        // wall it silently blocks corridors. Either way the map
                        // would be wrong in a way nothing reports.
                        flat.Foreign++;
                        continue;
                    }

                    int index = y * span + x;
                    flat.Flags[index] = vrox.Flags;
                    flat.Hazard[index] = (byte)Mathf.Clamp(vrox.HazardDamage, 0, 255);
                    flat.Biome[index] = (byte)Mathf.Clamp(vrox.BiomeId, 0, 255);
                    flat.Painted++;
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
                        flat.Weight[y * span + x] = (byte)Mathf.Clamp(marker.SpawnWeight, 0, 255);
                    }
                }
            }

            // Solid ground is never spawnable, whatever the weight layer says.
            // Otherwise an enemy appears inside a wall and cannot move.
            for (int i = 0; i < flat.Flags.Length; i++)
            {
                if ((flat.Flags[i] & 1) != 0)
                {
                    flat.Weight[i] = 0;
                }
            }

            return flat;
        }

        /// <summary>The flattened area cut into chunk rows, keyed as the server packs them.</summary>
        internal static IEnumerable<(uint cell, List<SpacetimeDB.Types.TileData> tiles)> Chunks(FlatArea flat)
        {
            int span = flat.Span;
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
                                Flags = flat.Flags[index],
                                SpawnWeight = flat.Weight[index],
                                Hazard = flat.Hazard[index],
                                Biome = flat.Biome[index],
                            });
                        }
                    }
                    yield return ((uint)((cx << 16) | cy), tiles);
                }
            }
        }

        /// <summary>Says what did not make it into a flattened area.</summary>
        internal static void Report(FlatArea flat, Object context, string what)
        {
            if (flat.Foreign > 0)
            {
                Debug.LogWarning($"Vrox: {flat.Foreign} painted cell(s) in {what} use a plain Tile rather "
                               + "than a VroxTile, so their meaning is unknown and were left as they were. "
                               + "Create tiles via Assets > Create > Vrox > Tile.", context);
            }
            if (flat.Outside > 0)
            {
                Debug.LogError($"Vrox: {flat.Outside} painted cell(s) in {what} are outside "
                             + $"(0,0)-({flat.Span},{flat.Span}) and did not reach the server. "
                             + "Tilemap cell coordinates are measured from the Grid, so move the "
                             + "drawing into the positive quadrant.", context);
            }
            if (flat.Painted == 0)
            {
                Debug.LogError($"Vrox: no usable tiles were exported for {what}. With Unpainted Is "
                             + "Solid on, that leaves the whole of it impassable.", context);
            }
        }
    }
}
