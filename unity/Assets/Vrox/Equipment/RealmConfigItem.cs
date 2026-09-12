using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// How the server generates a realm.
    /// </summary>
    /// <remarks>
    /// One of these in the project. It is authoring data, exactly like the weapon
    /// and enemy assets: the numbers are pushed to the server on Play, and the
    /// server's row is what actually generates.
    ///
    /// The generation itself cannot live on the client. The server decides where
    /// bodies may stand, so if a client generated the map it would be drawing a
    /// world nothing collides with — and two clients would draw different ones.
    /// This asset is the knobs; the map is the server's.
    ///
    /// Pushing does *not* regenerate. A realm that re-rolled whenever anybody
    /// pressed Play would change under the feet of everyone already in it. Use
    /// Vrox > Regenerate Realm, or the seed field below with Vrox > Generate
    /// Realm From Seed.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Realm Config", fileName = "RealmConfig")]
    public sealed class RealmConfigItem : ScriptableObject
    {
        [Header("Grid")]
        [Tooltip("World span in tiles, on each axis. Must be a multiple of 16 — a " +
                 "size that is not a whole number of chunks leaves tiles past the end " +
                 "of the terrain array, which exist, read as solid, and can never be " +
                 "stood on. The server refuses those rather than rounding.")]
        public int WorldSize = 128;

        [Header("Landmass")]
        [Tooltip("Elevation below which ground is water. Raise it to drown the map.")]
        [Range(0f, 1f)]
        public float SeaLevel = 0.26f;

        [Tooltip("Elevation band above the shore that becomes beach. The shoreline is a " +
                 "third tile type rather than a blend, because tiles do not interpolate.")]
        [Range(0f, 0.3f)]
        public float BeachWidth = 0.035f;

        [Tooltip("How sharply elevation falls off towards the edges. This is what makes " +
                 "the realm an island instead of a square. Low values drown everything " +
                 "but the middle; high values push the coast out to the border.")]
        [Range(0.5f, 8f)]
        public float IslandFalloff = 3.2f;

        [Tooltip("Landmass noise frequency, in cycles per tile. Smaller is smoother and " +
                 "gives fewer, larger landmasses.")]
        [Range(0.002f, 0.2f)]
        public float LandFrequency = 0.022f;

        [Tooltip("Octaves of landmass noise. More octaves add coastline detail; they do " +
                 "not change the island's overall shape.")]
        [Range(1, 8)]
        public int LandOctaves = 4;

        [Header("Biome regions")]
        [Tooltip("How many biome regions to seed. Sites are placed by best-candidate " +
                 "sampling so regions come out comparable in size instead of one blob " +
                 "and a handful of slivers.")]
        [Range(2, 64)]
        public int SiteCount = 14;

        [Tooltip("Coarse warp strength, in tiles. This bends region borders into bays " +
                 "and peninsulas. At zero the borders are straight Voronoi polygons.")]
        [Range(0f, 30f)]
        public float WarpCoarseAmp = 12f;

        [Tooltip("Cycles per tile. This matters more than the amplitude: a warp whose " +
                 "wavelength is much bigger than a region slides the whole region sideways " +
                 "and leaves its edges just as straight. Keep 1/frequency near the region " +
                 "size — about 30 tiles for 14 regions on a 128 map.")]
        [Range(0.002f, 0.2f)]
        public float WarpCoarseFrequency = 0.035f;

        [Tooltip("Fine warp strength, in tiles. This is what makes a border ragged at " +
                 "arm's length rather than merely curved.")]
        [Range(0f, 10f)]
        public float WarpFineAmp = 5f;

        [Range(0.01f, 0.5f)]
        public float WarpFineFrequency = 0.120f;

        [Header("Obstacles")]
        [Tooltip("Share of land that becomes solid, before each biome's own multiplier. " +
                 "Measured against the noise field's own distribution, so this really is " +
                 "the fraction it says it is.")]
        [Range(0f, 0.6f)]
        public float ObstacleDensity = 0.20f;

        [Tooltip("Obstacle noise frequency. Smaller gives fewer, larger clumps.")]
        [Range(0.005f, 0.5f)]
        public float ObstacleFrequency = 0.085f;

        [Tooltip("Smoothing passes over the obstacle mask. Zero leaves single-tile " +
                 "pillars and single-tile holes, which read as speckle and snag a body " +
                 "sliding past them.")]
        [Range(0, 8)]
        public int SmoothingPasses = 3;

        [Header("Validation")]
        [Tooltip("Least share of open ground that must be reachable on foot from the " +
                 "spawn point. Below this the seed is thrown away and another is tried. " +
                 "A realm with a third of itself sealed off is unplayable and cannot be " +
                 "spotted by looking at the seed.")]
        [Range(0.3f, 1f)]
        public float MinReachable = 0.80f;

        [Header("Seed")]
        [Tooltip("Seed used by Vrox > Generate Realm From Seed. The same seed always " +
                 "reproduces the same realm, which is what makes a bad map reportable.")]
        public uint Seed = 1337;

        private void OnValidate()
        {
            // Said here as well as refused on the server, so the mistake is seen
            // while typing rather than at push time.
            if (WorldSize <= 0 || WorldSize % 16 != 0)
            {
                Debug.LogWarning($"\"{name}\" world size {WorldSize} is not a positive "
                               + "multiple of 16, and the push will refuse it.", this);
            }
        }
    }
}
