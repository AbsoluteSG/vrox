using System.Collections.Generic;
using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// The enemy assets a running client can see, so it can draw their art.
    /// </summary>
    /// <remarks>
    /// A sprite cannot travel through the database — it is not data the
    /// simulation has any use for, and shipping one would make a texture change a
    /// publish. So the server sends a def id and the client looks the art up
    /// here, the same split loot bags use.
    ///
    /// This exists because nothing else references <c>EnemyItem</c> assets at
    /// runtime: they are pushed to the server by the editor and then never
    /// touched, so Unity has no reason to include them in a build. A list someone
    /// has to keep current is the cost of that, and it is why a missing entry is
    /// reported rather than ignored.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Enemy Catalogue", fileName = "EnemyCatalogue")]
    public sealed class EnemyCatalogue : ScriptableObject
    {
        [Tooltip("Every enemy whose art should be available at runtime. Enemies left " +
                 "out still spawn and fight; they just draw as coloured squares.")]
        public List<EnemyItem> Enemies = new();

        private Dictionary<ushort, EnemyItem>? _byId;
        private Texture2D? _shared;
        private bool _sharedResolved;

        /// <summary>The asset for a def id, or null if it is not listed.</summary>
        public EnemyItem? For(ushort defId)
        {
            _byId ??= Build();
            return _byId.TryGetValue(defId, out var item) ? item : null;
        }

        /// <summary>
        /// The one texture every listed sprite must come from, or null if there
        /// are no sprites.
        /// </summary>
        /// <remarks>
        /// Every sprited enemy is drawn in a single mesh, and a mesh carries one
        /// texture — so the sprites have to share a sheet. Reported loudly rather
        /// than worked around: the alternative is a draw call per texture, and
        /// this component exists precisely because a realm holds hundreds of
        /// enemies at once.
        /// </remarks>
        public Texture2D? SharedTexture()
        {
            // Cached, because the renderer asks every frame and the answer only
            // changes when the asset does. Without this a catalogue with a
            // mismatched sheet reports it sixty times a second, which buries the
            // one line that says what is wrong.
            if (_sharedResolved)
            {
                return _shared;
            }
            _sharedResolved = true;

            Texture2D? shared = null;
            foreach (var enemy in Enemies)
            {
                if (enemy == null || enemy.Sprite == null)
                {
                    continue;
                }
                var texture = enemy.Sprite.texture;
                if (shared == null)
                {
                    shared = texture;
                }
                else if (shared != texture)
                {
                    Debug.LogError($"\"{name}\": {enemy.name} uses texture "
                                 + $"\"{texture.name}\" but the catalogue is already on "
                                 + $"\"{shared.name}\". Every enemy sprite must come from "
                                 + "one sheet, or they cannot share a draw call.", this);
                    _shared = shared;
                    return shared;
                }
            }
            _shared = shared;
            return shared;
        }

        public void Invalidate()
        {
            _byId = null;
            _sharedResolved = false;
            _shared = null;
        }

        private void OnValidate() => Invalidate();

        private Dictionary<ushort, EnemyItem> Build()
        {
            var map = new Dictionary<ushort, EnemyItem>();
            foreach (var enemy in Enemies)
            {
                if (enemy == null)
                {
                    continue;
                }
                if (!map.TryAdd(enemy.Id, enemy))
                {
                    Debug.LogError($"\"{name}\" lists id {enemy.Id} more than once "
                                 + $"({enemy.name}). Only the first is used.", this);
                }
            }
            return map;
        }
    }
}
