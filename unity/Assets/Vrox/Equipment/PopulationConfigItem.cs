using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>One kind of enemy in a population's mix.</summary>
    [System.Serializable]
    public struct PopulationEntry
    {
        public EnemyItem? Enemy;

        [Tooltip("Relative likelihood against the other entries. 0 disables it. " +
                 "These are ratios, not percentages — 8 and 3 means 8 of one for every 3 of the other.")]
        [Range(0, 100)]
        public int Weight;

        [Tooltip("Most of this kind alive at once. 0 means no limit. Weights give " +
                 "ratios, not limits: without a cap a rare elite still accumulates " +
                 "into a crowd given enough time.")]
        [Range(0, 100)]
        public int MaxAlive;
    }

    /// <summary>
    /// What lives somewhere: the enemy mix, the cap and the spawn rate.
    /// </summary>
    /// <remarks>
    /// The split with <c>VroxSpawner</c> is placement versus content. A spawner
    /// says *where* and *how big*; this says *what* and *how many*. One
    /// population can drive several spawners and they stay consistent, which is
    /// the point of having it as an asset rather than fields on each object.
    ///
    /// Named for what it holds rather than for where it applies. It was
    /// <c>AreaConfigItem</c>, and "area" read as a synonym for
    /// <see cref="BiomeItem"/> — two assets that both sounded like "a place",
    /// where only one of them describes terrain. A biome is a place; this is its
    /// population, and a biome points at one.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Population Config", fileName = "Population")]
    public sealed class PopulationConfigItem : ScriptableObject
    {
        public string DisplayName = "Unnamed Population";

        [Header("Population")]
        [Tooltip("Enemies alive at once in each place using this population — one " +
                 "region for a biome, or one hand-placed spawner in a scene. This is " +
                 "the number you see standing there.")]
        [Range(0, 200)]
        public int MaxAlive = 8;

        [Tooltip("Milliseconds between spawns while below the cap. One at a time, so " +
                 "clearing a camp gives a lull rather than an instant refill.")]
        [Range(100, 30000)]
        public int IntervalMs = 2000;

        [Header("Mix")]
        public List<PopulationEntry> Enemies = new();

        /// <summary>Entries that will actually produce something.</summary>
        public IEnumerable<PopulationEntry> Usable =>
            Enemies.Where(e => e.Enemy != null && e.Weight > 0);

        /// <summary>Share of spawns an entry should take, for display in the inspector.</summary>
        public float ShareOf(PopulationEntry entry)
        {
            int total = Usable.Sum(e => e.Weight);
            return total > 0 ? (float)entry.Weight / total : 0f;
        }

        private void OnValidate()
        {
            // A population with nothing usable in it looks identical to a working
            // one, and the only symptom is a spawner that never produces anything.
            if (Enemies.Count > 0 && !Usable.Any())
            {
                Debug.LogWarning($"\"{name}\" has entries but none are usable — every one is "
                               + "missing an Enemy or has a weight of 0, so nothing will spawn.", this);
            }
        }
    }
}
