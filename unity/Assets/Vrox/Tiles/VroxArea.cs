using UnityEngine;
using UnityEngine.Tilemaps;

namespace Vrox.Tiles
{
    /// <summary>
    /// Names the tilemaps that make up an area, so the exporter knows what to read.
    /// </summary>
    /// <remarks>
    /// Put this on the Grid. Paint the visual layers with <see cref="VroxTile"/>
    /// assets and their meaning comes with them; the spawn-weight layer is the one
    /// exception, because "enemies may appear here" has no appearance and should
    /// not be visible in the game.
    ///
    /// Layers are combined per cell rather than kept apart: the server cares only
    /// what a position *is*, and merging at export means the runtime never carries
    /// the authoring structure around.
    /// </remarks>
    public sealed class VroxArea : MonoBehaviour
    {
        [Tooltip("Rendered layers, painted with VroxTile assets. Later maps win where " +
                 "they overlap, so order these back to front: ground, then walls, then decor.")]
        public Tilemap[] Layers = System.Array.Empty<Tilemap>();

        [Tooltip("Optional. Painted with marker tiles whose Spawn Weight is read and " +
                 "whose sprite is never shown in game. Hide its renderer.")]
        public Tilemap? SpawnWeights;

        [Header("Bounds")]
        [Tooltip("Anything you did not paint is out of bounds. This is what makes a " +
                 "hand-drawn area work: draw the floor, and everywhere else is wall. " +
                 "Uncheck only for an open field where the tilemap adds obstacles to " +
                 "otherwise-walkable ground.")]
        public bool UnpaintedIsSolid = true;

        [Tooltip("Where players appear. Leave empty to use the centre of the painted " +
                 "area. It must be somewhere walkable.")]
        public Transform? PlayerSpawn;

        [Tooltip("Spawn weight for ground the weight layer says nothing about. 1 allows " +
                 "spawning anywhere unpainted; 0 restricts spawning to painted areas only.")]
        [Range(0, 255)]
        public int DefaultSpawnWeight = 1;
    }
}
