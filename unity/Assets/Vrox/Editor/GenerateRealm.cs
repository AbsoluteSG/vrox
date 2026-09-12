using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Runs <see cref="RealmGenerator"/> and uploads the result.
    /// </summary>
    /// <remarks>
    /// Generation moved out of the database module because a module runs
    /// interpreted — see RealmGenerator for the measurement. This is the half
    /// that talks to the server: it reads the authored configuration, computes a
    /// realm natively, and writes it through the same reducers a hand-painted
    /// tilemap already used.
    ///
    /// The server remains the only thing that owns terrain. Nothing here is sent
    /// to players; it is sent to the database, and players read it from there.
    /// </remarks>
    public static class GenerateRealm
    {
        /// <summary>
        /// Generated spawner ids start here, clear of the editor's.
        /// </summary>
        /// <remarks>
        /// Scene spawners are numbered from 1 by scene order, so the two sets
        /// would collide at low ids and silently overwrite each other on push.
        /// </remarks>
        private const ushort GeneratedSpawnerBase = 10000;

        private const int ChunkSize = 16;
        private const byte SourceGenerated = 1;

        [MenuItem("Vrox/Generate Realm From Seed")]
        public static void FromSeed()
        {
            var cfg = LoadConfig();
            Run(cfg.settings, cfg.seed, cfg.biomes);
        }

        [MenuItem("Vrox/Regenerate Realm")]
        public static void Reroll()
        {
            var cfg = LoadConfig();
            // Unpredictable, unlike the authored seed. Rerolling with the same
            // number would produce the same realm, which is the opposite of what
            // the command is for.
            uint seed = (uint)System.DateTime.Now.Ticks;
            Run(cfg.settings, seed, cfg.biomes);
        }

        private static void Run(RealmGenerator.Settings settings, uint seed, List<BiomeItem> biomes)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var realm = RealmGenerator.Build(settings, seed);
            long generated = watch.ElapsedMilliseconds;

            if (!realm.Ok)
            {
                Debug.LogError($"Vrox: {realm.Why}. Terrain left as it was.");
                return;
            }

            int span = settings.WorldSize;
            Debug.Log($"Vrox: generated {span}x{span} from seed {realm.Seed} in {generated}ms — "
                    + $"{realm.Land} land, {realm.Solid} obstacles, {realm.Open} open, "
                    + $"{realm.Reachable * 100f:0.0}% reachable, {realm.Rejected} seed(s) rejected. "
                    + "Uploading...");

            EditorConnection.Run("generate realm", conn =>
            {
                // Terrain pushes are refused while the server holds a generated
                // realm, so this has to claim authored mode first. It is honest:
                // the terrain now *is* authored, just by a generator rather than
                // by hand.
                conn.Reducers.UseAuthoredTerrain();
                conn.Reducers.ClearTerrain();

                int chunks = span / ChunkSize;
                for (int cy = 0; cy < chunks; cy++)
                {
                    for (int cx = 0; cx < chunks; cx++)
                    {
                        var list = new List<SpacetimeDB.Types.TileData>(ChunkSize * ChunkSize);
                        for (int ty = 0; ty < ChunkSize; ty++)
                        {
                            for (int tx = 0; tx < ChunkSize; tx++)
                            {
                                var tile = realm.Tiles[(cy * ChunkSize + ty) * span + cx * ChunkSize + tx];
                                list.Add(new SpacetimeDB.Types.TileData
                                {
                                    Flags = tile.Flags,
                                    SpawnWeight = tile.SpawnWeight,
                                    Hazard = tile.Hazard,
                                    Biome = tile.Biome,
                                });
                            }
                        }
                        conn.Reducers.UpsertTerrainChunk((uint)((cx << 16) | cy), list);
                    }
                }

                conn.Reducers.SetSpawn(realm.SpawnX, realm.SpawnY);

                int placed = PushSpawners(conn, realm, span, seed, biomes);
                Debug.Log($"Vrox: uploaded {chunks * chunks} chunk(s), spawn "
                        + $"({realm.SpawnX:0.0}, {realm.SpawnY:0.0}), {placed} spawner(s).");
            });
        }

        /// <summary>
        /// Places one set of spawners per biome region.
        /// </summary>
        /// <remarks>
        /// Regions are walked by index rather than gathered into a dictionary,
        /// because dictionary order is not part of the contract and the same seed
        /// has to place the same spawners every time.
        /// </remarks>
        private static int PushSpawners(SpacetimeDB.Types.DbConnection conn,
                                        RealmGenerator.Result realm, int span, uint seed,
                                        List<BiomeItem> biomes)
        {
            // The generator owns its spawners and replaces them wholesale: the old
            // ones point at ground that no longer exists.
            conn.Reducers.ClearSpawners(SourceGenerated);

            var byId = new Dictionary<byte, BiomeItem>();
            foreach (var biome in biomes)
            {
                byId[(byte)biome.Id] = biome;
            }
            if (byId.Count == 0)
            {
                return 0;
            }

            var byRegion = new List<int>?[256];
            for (int i = 0; i < realm.Tiles.Length; i++)
            {
                byte site = realm.SiteOf[i];
                if (site == 255 || realm.Tiles[i].SpawnWeight == 0)
                {
                    continue;
                }
                (byRegion[site] ??= new List<int>()).Add(i);
            }

            ushort id = GeneratedSpawnerBase;
            int placed = 0, mute = 0;

            for (int site = 0; site < byRegion.Length; site++)
            {
                if (byRegion[site] is not { Count: > 0 } candidates)
                {
                    continue;
                }

                byte biomeId = realm.Tiles[candidates[0]].Biome;
                if (!byId.TryGetValue(biomeId, out var biome) || biome.Population == null)
                {
                    continue;
                }

                var composition = biome.Population.Usable
                    .Where(e => e.Enemy != null)
                    .Select(e => new SpacetimeDB.Types.AreaEntry
                    {
                        EnemyDefId = e.Enemy!.Id,
                        Weight = (ushort)Mathf.Max(0, e.Weight),
                        MaxAlive = (ushort)Mathf.Max(0, e.MaxAlive),
                    })
                    .ToList();
                if (composition.Count == 0)
                {
                    continue;
                }
                if (biome.Population.MaxAlive == 0)
                {
                    // A spawner holding a population of zero never spawns. Counted
                    // and reported, because it looks exactly like a broken
                    // generator.
                    mute++;
                    continue;
                }

                // One spawner, covering the region. Mirrors the server's
                // PlaceSpawners and CoverRegion — and with the same rule as every
                // other mirror in this project: if the two disagree, the server is
                // right and this is the copy that is wrong.
                var (cx, cy, radius) = CoverRegion(candidates, span);
                conn.Reducers.UpsertSpawner(
                    id++,
                    composition,
                    cx,
                    cy,
                    radius,
                    (ushort)Mathf.Max(0, biome.Population.MaxAlive),
                    (ushort)Mathf.Clamp(biome.Population.IntervalMs, 100, 60000),
                    SourceGenerated);
                placed++;
            }

            if (mute > 0)
            {
                Debug.LogWarning($"Vrox: {mute} region(s) got no spawner because their area "
                               + "holds a population of zero.");
            }
            return placed;
        }

        /// <summary>Reads the authored configuration into the generator's own shape.</summary>
        private static (RealmGenerator.Settings settings, uint seed, List<BiomeItem> biomes) LoadConfig()
        {
            var settings = new RealmGenerator.Settings();
            uint seed = 1337;

            var configs = AssetDatabase.FindAssets($"t:{nameof(RealmConfigItem)}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<RealmConfigItem>)
                .Where(a => a != null)
                .ToList();

            if (configs.Count > 0)
            {
                var c = configs[0];
                settings.WorldSize = c.WorldSize;
                settings.SeaLevel = c.SeaLevel;
                settings.BeachWidth = c.BeachWidth;
                settings.IslandFalloff = c.IslandFalloff;
                settings.LandFrequency = c.LandFrequency;
                settings.LandOctaves = c.LandOctaves;
                settings.SiteCount = c.SiteCount;
                settings.WarpCoarseAmp = c.WarpCoarseAmp;
                settings.WarpCoarseFrequency = c.WarpCoarseFrequency;
                settings.WarpFineAmp = c.WarpFineAmp;
                settings.WarpFineFrequency = c.WarpFineFrequency;
                settings.ObstacleDensity = c.ObstacleDensity;
                settings.ObstacleFrequency = c.ObstacleFrequency;
                settings.SmoothingPasses = c.SmoothingPasses;
                settings.MinReachable = c.MinReachable;
                seed = c.Seed;
            }

            var biomes = AssetDatabase.FindAssets($"t:{nameof(BiomeItem)}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<BiomeItem>)
                .Where(a => a != null)
                .ToList();

            var deck = new List<byte>();
            foreach (var biome in biomes)
            {
                byte bid = (byte)biome.Id;
                settings.Clutter[bid] = biome.Clutter;
                settings.Spawn[bid] = (byte)Mathf.Clamp(biome.SpawnWeight, 0, 255);
                for (int i = 0; i < biome.DeckCount; i++)
                {
                    deck.Add(bid);
                }
            }

            if (deck.Count > 0)
            {
                // Sorted because asset order is not stable, and an unstable deck
                // means the same seed deals different biomes on different machines.
                deck.Sort();
                settings.Deck = deck.ToArray();
            }
            else if (biomes.Count > 0)
            {
                Debug.LogWarning("Vrox: every biome has Deck Count 0, so regions fall back to "
                               + "the built-in set.");
            }

            return (settings, seed, biomes);
        }

        /// <summary>
        /// A centre and radius covering a region's spawnable ground.
        /// </summary>
        /// <remarks>
        /// A mirror of the server's <c>CoverRegion</c>, and wrong if it ever
        /// disagrees with it. Duplicated rather than shared because this editor
        /// path talks to the server through reducers and cannot call into the
        /// module — the same reason the pattern maths is duplicated.
        /// </remarks>
        private static (float x, float y, float radius) CoverRegion(List<int> candidates, int span)
        {
            double sumX = 0, sumY = 0;
            foreach (int tile in candidates)
            {
                sumX += tile % span;
                sumY += tile / span;
            }
            double meanX = sumX / candidates.Count;
            double meanY = sumY / candidates.Count;

            int nearest = candidates[0];
            double best = double.MaxValue;
            foreach (int tile in candidates)
            {
                double dx = tile % span - meanX;
                double dy = tile / span - meanY;
                double d = dx * dx + dy * dy;
                if (d < best)
                {
                    best = d;
                    nearest = tile;
                }
            }

            float radius = (float)(System.Math.Sqrt(candidates.Count / System.Math.PI) * 1.25);
            return (nearest % span + 0.5f, nearest / span + 0.5f, radius);
        }
    }
}
