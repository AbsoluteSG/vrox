using System.Collections.Generic;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox
{
    /// <summary>
    /// Draws the bags enemies leave behind.
    /// </summary>
    /// <remarks>
    /// A pooled <see cref="SpriteRenderer"/> per bag, unlike <see cref="VroxShots"/>
    /// which builds one mesh for everything. Bags need a different sprite each,
    /// and one mesh can only carry one texture — putting every bag in a single
    /// draw call would mean an atlas, which is a build step to save a handful of
    /// draw calls for objects there are rarely more than a dozen of.
    ///
    /// Which bag a drop is comes from the server as a byte. What that byte looks
    /// like is authored in <see cref="LootBagCatalogue"/> and never leaves the
    /// client, so a bag's art can change without a publish.
    ///
    /// Nothing here decides when a bag disappears. The server expires bags on its
    /// own tick and the row goes away; this only draws what is replicated. A
    /// client that faded bags out on a timer of its own would show empty ground
    /// where a bag still existed, and a player would walk over nothing.
    /// </remarks>
    public sealed class VroxLoot : MonoBehaviour
    {
        /// <summary>On the ground, behind players and projectiles.</summary>
        private const float Depth = -0.4f;

        [Tooltip("Maps a bag kind to its sprite. Without this, bags do not draw at all " +
                 "— deliberately, because a stand-in bag is worse than a missing one.")]
        public LootBagCatalogue? Catalogue;

        [Tooltip("Base size of a bag in tiles, before the catalogue entry's scale.")]
        [Range(0.1f, 3f)]
        public float Size = 0.6f;

        [Tooltip("Sorting layer order, so bags sit above the ground but under actors.")]
        public int SortingOrder = -1;

        [Header("Bob")]
        [Tooltip("Cycles per second. Zero holds still.")]
        [Range(0f, 4f)]
        public float BobSpeed = 1.1f;

        [Tooltip("How far it rises, in tiles.")]
        [Range(0f, 0.5f)]
        public float BobHeight = 0.08f;

        private readonly Dictionary<ulong, SpriteRenderer> _live = new();
        private readonly List<ulong> _gone = new();
        private readonly HashSet<ulong> _seen = new();
        private bool _warnedNoCatalogue;

        private void LateUpdate()
        {
            if (VroxNet.Instance?.Conn is not { } conn)
            {
                return;
            }

            if (Catalogue == null)
            {
                // Once, not every frame. A renderer with no catalogue draws
                // nothing at all, which is indistinguishable from loot never
                // dropping — and that is a server-side bug someone would go
                // looking for instead.
                if (!_warnedNoCatalogue)
                {
                    _warnedNoCatalogue = true;
                    Debug.LogError($"[vrox] VroxLoot on '{name}' has no Loot Bag Catalogue, "
                                 + "so no bag will ever be drawn.", this);
                }
                return;
            }

            _seen.Clear();

            // Both computed once and shared by every bag. The view cannot turn
            // between two of them, and bobbing in step is deliberate: bags
            // flickering out of phase read as several unrelated things rather
            // than one kind of thing worth walking to.
            var spin = Quaternion.Euler(0f, 0f, VroxCamera.Instance != null
                ? VroxCamera.Instance.Angle * Mathf.Rad2Deg
                : 0f);

            float bob = BobSpeed > 0f
                ? BobHeight * Mathf.Sin(Time.time * BobSpeed * Mathf.PI * 2f)
                : 0f;

            foreach (var bag in conn.Db.LootDrop.Iter())
            {
                _seen.Add(bag.Id);

                var entry = Catalogue.For(bag.BagKind);
                if (entry?.Sprite == null)
                {
                    // Nothing drawn for an unauthored kind, and the bag is still
                    // there and still walkable. Substituting some other bag's art
                    // would be worse: it would look like loot the player can
                    // identify, and it would be the wrong loot.
                    continue;
                }

                if (!_live.TryGetValue(bag.Id, out var renderer))
                {
                    renderer = NewRenderer();
                    _live[bag.Id] = renderer;
                }

                renderer.sprite = entry.Sprite;
                renderer.color = entry.Tint;
                renderer.transform.position = new Vector3(bag.X, bag.Y + bob, Depth);

                // Turned with the view so the bag keeps facing the player. A
                // SpriteRenderer draws along its own transform, so unlike the mesh
                // renderers this is a rotation rather than a rebuilt quad.
                renderer.transform.rotation = spin;
                renderer.transform.localScale = Vector3.one * (Size * entry.Scale);
            }

            // Collected before removing: mutating the dictionary while iterating
            // it throws, and the bags that vanished this frame are exactly the
            // ones being iterated.
            _gone.Clear();
            foreach (var id in _live.Keys)
            {
                if (!_seen.Contains(id))
                {
                    _gone.Add(id);
                }
            }
            foreach (var id in _gone)
            {
                if (_live.Remove(id, out var renderer) && renderer != null)
                {
                    Destroy(renderer.gameObject);
                }
            }
        }

        private SpriteRenderer NewRenderer()
        {
            var go = new GameObject("Loot Bag");
            go.transform.SetParent(transform, worldPositionStays: false);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sortingOrder = SortingOrder;
            return renderer;
        }
    }
}
