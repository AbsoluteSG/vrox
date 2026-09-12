using System.Linq;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Sends the generator's tuning to the server, and triggers generation.
    /// </summary>
    /// <remarks>
    /// Tuning is pushed automatically on connect, like every other asset.
    /// Generation is not: re-rolling the world whenever somebody presses Play
    /// would move the ground under everyone already standing on it. Rolling a
    /// new realm is a deliberate menu item.
    /// </remarks>
    public static class PushRealm
    {
        /// <summary>Uploads the tuning asset, if there is one.</summary>
        /// <remarks>
        /// Exactly one asset is expected. More than one is refused rather than
        /// picked between: whichever won would depend on asset ordering, and the
        /// world would silently change shape when a file was renamed.
        /// </remarks>
        public static void Push(SpacetimeDB.Types.DbConnection conn)
        {
            var configs = LoadAll();
            if (configs.Count == 0)
            {
                return;
            }
            if (configs.Count > 1)
            {
                Debug.LogError("Vrox: more than one Realm Config asset — "
                             + string.Join(", ", configs.Select(c => c.name))
                             + ". Keep one; the server holds a single generator config.",
                               configs[0]);
                return;
            }

            var c = configs[0];
            conn.Reducers.UpsertRealmConfig(
                c.SeaLevel, c.BeachWidth, c.IslandFalloff,
                c.LandFrequency, (byte)c.LandOctaves, (byte)c.SiteCount,
                c.WarpCoarseAmp, c.WarpCoarseFrequency,
                c.WarpFineAmp, c.WarpFineFrequency,
                c.ObstacleDensity, c.ObstacleFrequency,
                (byte)c.SmoothingPasses, c.MinReachable, c.WorldSize);

            Debug.Log($"Vrox: pushed realm config — world {c.WorldSize}x{c.WorldSize}, "
                    + $"sea {c.SeaLevel:0.00}, falloff "
                    + $"{c.IslandFalloff:0.0}, {c.SiteCount} biome regions, clutter "
                    + $"{c.ObstacleDensity:0.00}. Vrox > Regenerate Realm to apply it.");
        }

        /// <summary>
        /// Uploads the biome catalogue.
        /// </summary>
        /// <remarks>
        /// Cleared and rewritten wholesale rather than reconciled, so deleting an
        /// asset actually stops that biome generating. Reconciling would leave the
        /// server holding biomes with no asset behind them, and no way to tell
        /// which.
        ///
        /// Must run after the enemy catalogue: a biome's mix references enemy def
        /// ids, and pushing them first would name enemies that do not exist yet.
        /// </remarks>
        public static void PushBiomes(SpacetimeDB.Types.DbConnection conn)
        {
            var biomes = AssetDatabase.FindAssets($"t:{nameof(BiomeItem)}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<BiomeItem>)
                .Where(b => b != null)
                .OrderBy(b => b!.Id)
                .ToList();

            if (biomes.Count == 0)
            {
                return;
            }

            var duplicate = biomes.GroupBy(b => b!.Id).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
            {
                // Refused rather than pushed: the later asset silently overwrites
                // the earlier one, so a biome you are editing turns into a
                // different biome and nothing says why.
                Debug.LogError($"Vrox: biome id {duplicate.Key} is used by "
                             + string.Join(" and ", duplicate.Select(b => b!.name))
                             + ". Ids are the server's primary key; give them different ones.",
                               duplicate.First());
                return;
            }

            conn.Reducers.ClearBiomes();

            int populated = 0;
            foreach (var biome in biomes)
            {
                var composition = biome!.Population == null
                    ? new System.Collections.Generic.List<SpacetimeDB.Types.AreaEntry>()
                    : biome.Population.Usable
                        .Select(e => new SpacetimeDB.Types.AreaEntry
                        {
                            EnemyDefId = e.Enemy!.Id,
                            Weight = (ushort)Mathf.Clamp(e.Weight, 0, 1000),
                            MaxAlive = (ushort)Mathf.Clamp(e.MaxAlive, 0, 200),
                        })
                        .ToList();

                if (composition.Count > 0)
                {
                    populated++;
                }

                conn.Reducers.UpsertBiome(
                    (byte)biome.Id,
                    biome.DisplayName,
                    Vrox.PackedColour.Pack(biome.Floor),
                    Vrox.PackedColour.Pack(biome.Wall),
                    biome.Clutter,
                    (byte)biome.SpawnWeight,
                    // Water and beach are terrain, not regions. Forced here as
                    // well as warned about in the asset, because a dealt ocean
                    // region would be a lake in the middle of the island.
                    biome.Id <= 1 ? (byte)0 : (byte)biome.DeckCount,
                    composition,
                    (ushort)(biome.Population != null ? Mathf.Clamp(biome.Population.MaxAlive, 0, 200) : 0),
                    (ushort)(biome.Population != null ? Mathf.Clamp(biome.Population.IntervalMs, 100, 30000) : 2000));
            }

            int dealt = biomes.Count(b => b!.Id > 1 && b.DeckCount > 0);
            Debug.Log($"Vrox: pushed {biomes.Count} biome(s), {dealt} generatable, "
                    + $"{populated} with an enemy mix. Vrox > Regenerate Realm to apply.");

            if (dealt == 0)
            {
                Debug.LogWarning("Vrox: no biome has a Deck Count above 0, so generation "
                               + "falls back to its built-in regions and none of these "
                               + "assets will appear.", biomes[0]);
            }
        }

        // Vrox/Regenerate Realm and Vrox/Generate Realm From Seed used to live
        // here and called the module's GenerateRealm reducer. Generation now runs
        // natively in the editor — see GenerateRealm.cs — because a module is
        // interpreted and a 1024-tile map took 22 seconds inside it.
        //
        // Removed rather than left alongside: two commands on the same menu path
        // is not a choice between them, it is Unity picking one without saying
        // which.

        /// <summary>
        /// Hands terrain back to the scene's tilemap.
        /// </summary>
        /// <remarks>
        /// The server refuses terrain pushes while a realm is generated, so
        /// hand-authored areas need this first. It is a menu item rather than
        /// automatic because it throws the generated realm away.
        /// </remarks>
        [MenuItem("Vrox/Use Authored Terrain")]
        public static void UseAuthored() => EditorConnection.Run("use authored terrain", conn =>
        {
            conn.Reducers.UseAuthoredTerrain();
            Debug.Log("Vrox: terrain is authored again. The next push will apply, and "
                    + "the generated realm is gone.");
        });

        /// <summary>True when the server is holding a generated realm.</summary>
        /// <remarks>
        /// Read from the replicated row rather than remembered locally. What the
        /// editor last asked for and what the server actually has are different
        /// things, and only one of them is authoritative.
        /// </remarks>
        public static bool IsGenerated(SpacetimeDB.Types.DbConnection conn) =>
            conn.Db.Realm.Id.Find(1) is { } realm && realm.Mode == 1;

        private static System.Collections.Generic.List<RealmConfigItem> LoadAll() =>
            AssetDatabase.FindAssets($"t:{nameof(RealmConfigItem)}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<RealmConfigItem>)
                .Where(a => a != null)
                .ToList();

    }
}
