using UnityEngine;
using UnityEngine.Serialization;

namespace Vrox.Equipment
{
    /// <summary>
    /// What a biome is: how it looks, how cluttered it gets, and what lives in it.
    /// </summary>
    /// <remarks>
    /// Authoring data. Unity says what a biome *is*; the server decides where
    /// biomes go, and its rows are what actually generate and render. Pushed on
    /// Play like weapons and enemies.
    ///
    /// This is the asset that connects generation to content you have already
    /// authored: point <see cref="Population"/> at an existing Population Config
    /// and every region of this biome is populated from it.
    ///
    /// Pushing does not regenerate. Colours and clutter apply to the next realm;
    /// use Vrox > Regenerate Realm to see them.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Biome", fileName = "Biome")]
    public sealed class BiomeItem : ScriptableObject
    {
        [Tooltip("Tile biome id, and the primary key on the server. 0 is water and 1 is " +
                 "beach — both are terrain the generator produces itself, so give them " +
                 "a Deck Count of 0 and use them only to set colours. Regions use 2 and up.")]
        [Range(0, 255)]
        public int Id = 2;

        public string DisplayName = "Unnamed Biome";

        [Header("Appearance")]
        [Tooltip("Walkable ground.")]
        public Color Floor = new Color(0.28f, 0.45f, 0.24f);

        [Tooltip("Obstacles inside this biome — trees, boulders, cliffs.")]
        public Color Wall = new Color(0.11f, 0.21f, 0.13f);

        [Header("Generation")]
        [Tooltip("Copies of this biome in the deck the generator deals regions from. This " +
                 "is a count, not a weight: 2 means at least two regions of this biome " +
                 "whenever there are enough regions to go round. 0 never generates it.")]
        [Range(0, 16)]
        public int DeckCount = 2;

        [Tooltip("Obstacle density multiplier against the realm's base density. Above 1 is " +
                 "cluttered like forest or rock; below 1 is open like desert.")]
        [Range(0f, 4f)]
        public float Clutter = 1f;

        [Tooltip("Per-tile spawn weight inside this biome. 0 means nothing spawns here, " +
                 "which is how a safe region is made.")]
        [Range(0, 255)]
        public int SpawnWeight = 3;

        [Header("Population")]
        [Tooltip("The enemy mix, population cap and spawn interval for every region of " +
                 "this biome. Without one the regions generate as empty scenery.\n\n" +
                 "Each region of this biome gets exactly one spawner covering the whole " +
                 "region, so the Population's Max Alive is the number of enemies standing " +
                 "in one region — not a figure that gets multiplied by anything.")]
        [FormerlySerializedAs("Area")]
        public PopulationConfigItem? Population;

        private void OnValidate()
        {
            // Water and beach are produced by the landmass pass, not dealt as
            // regions. Dealing one would put an ocean in the middle of the island.
            if (Id <= 1 && DeckCount > 0)
            {
                Debug.LogWarning($"\"{name}\" has id {Id}, which is "
                               + (Id == 0 ? "water" : "beach")
                               + " — terrain the generator makes itself. Set Deck Count to 0 "
                               + "and use this asset only for its colours.", this);
            }
        }
    }
}
