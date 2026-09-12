using UnityEngine;
using UnityEngine.Tilemaps;

namespace Vrox.Tiles
{
    /// <summary>
    /// A tile that carries what it means, not just how it looks.
    /// </summary>
    /// <remarks>
    /// Semantics live on the tile asset rather than on the Tilemap it is painted
    /// into. Deriving them from the layer — "anything on the Obstacles map is
    /// solid" — breaks the first time you want a decoration that is not solid, or
    /// a boulder painted onto the ground. A tile that carries its own meaning
    /// means the same thing wherever it is used, and layers stay a rendering and
    /// sorting concern.
    ///
    /// Create via Assets > Create > Vrox > Tile.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Tile", fileName = "VroxTile")]
    public sealed class VroxTile : Tile
    {
        [Header("Collision")]
        [Tooltip("Blocks movement. Players and enemies slide along it rather than sticking.")]
        public bool Solid;

        [Tooltip("Stops projectiles. Usually matches Solid — uncheck for a low wall you " +
                 "can shoot over, or a window you cannot walk through but can shoot through.")]
        public bool BlocksProjectiles = true;

        [Header("Gameplay")]
        [Tooltip("Damage per second while standing on it. 0 is safe ground.")]
        [Range(0, 255)]
        public int HazardDamage;

        [Tooltip("Relative likelihood an enemy spawns here. 0 forbids it entirely. " +
                 "Only read from the spawn-weight layer, which is not rendered at runtime.")]
        [Range(0, 255)]
        public int SpawnWeight = 1;

        [Tooltip("Which biome this tile belongs to. The client colours by it, and " +
                 "generated realms use it to place spawners. 0 is the default biome.")]
        [Range(0, 255)]
        public int BiomeId;

        /// <summary>Packed as the server stores it: bit 0 movement, bit 1 projectiles.</summary>
        public byte Flags => (byte)((Solid ? 1 : 0) | (Solid && BlocksProjectiles ? 2 : 0));

        private void OnValidate()
        {
            // A tile that stops bullets but not bodies would let a player stand
            // inside their own cover, which is confusing rather than clever.
            // Blocking is expressed as "solid, and optionally see-through".
            if (!Solid && !BlocksProjectiles)
            {
                return;
            }
            if (!Solid && BlocksProjectiles)
            {
                Debug.LogWarning($"\"{name}\" blocks projectiles but not movement, which is not "
                               + "modelled — projectiles pass through anything walkable.", this);
            }
        }
    }
}
