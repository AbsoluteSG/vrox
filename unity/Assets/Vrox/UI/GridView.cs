using System.Collections.Generic;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.UI;
using Vrox.Equipment;

namespace Vrox.UI
{
    /// <summary>
    /// Any container, drawn as a grid where items take the room they occupy.
    /// </summary>
    /// <remarks>
    /// One component for the pack, the secure pocket and the vault, because they
    /// are the same thing at different sizes and with different owners. The only
    /// difference is which row the items come from: the pack and pocket belong to
    /// a character, the vault to the account.
    ///
    /// Positions come from the server. A cell is not a slot index — an item's
    /// footprint covers several cells and the row records only its top-left, so
    /// "what is at (5,1)" is a question the server answers and this only draws.
    ///
    /// Tiles are created here, unlike every other panel in this project, because
    /// there is one per item and items come and go. The <em>grid</em> is not:
    /// <see cref="CellSize"/> and the rect it sits in are yours to lay out, and
    /// this only positions tiles inside them.
    /// </remarks>
    public sealed class GridView : MonoBehaviour
    {
        /// <summary>0 pack, 1 equipped, 2 secure, 3 vault. Matches the server.</summary>
        [Tooltip("Which container this grid shows. 0 pack, 1 equipped, 2 secure, 3 vault.")]
        [Range(0, 3)]
        public int Container;

        /// <summary>Must match ContainerSize on the server.</summary>
        /// <remarks>
        /// Duplicated because nothing replicates the grid's size. A wrong number
        /// here draws items outside the frame or leaves dead space; it cannot
        /// lose anything, because every placement is decided server-side.
        /// </remarks>
        [Tooltip("Cells across. Server: pack 5, equipped 3, secure 2, vault 10.")]
        public int Width = 10;

        [Tooltip("Cells down. Server: pack 3, equipped 1, secure 2, vault 6.")]
        public int Height = 6;

        [Tooltip("Where tiles are parented. Its size divided by the grid is one cell.")]
        public RectTransform? Grid;

        [Tooltip("Prefab for one item tile: an Image, optionally with a VaultTile.")]
        public VaultTile? TilePrefab;

        [Tooltip("Turns item ids into the assets that know what they look like.")]
        public ItemCatalogue? Catalogue;

        [Tooltip("Gap between a tile and its cell edges, in pixels.")]
        [Range(0f, 12f)]
        public float Padding = 2f;

        /// <summary>Raised when a tile is clicked, with the cell it sits on.</summary>
        /// <remarks>
        /// The cell rather than the item, because everything the server offers for
        /// the vault is addressed by cell — a grid can hold the same item twice
        /// and an id would be ambiguous.
        /// </remarks>
        public event System.Action<byte, byte>? CellClicked;

        private readonly Dictionary<int, VaultTile> _tiles = new();
        private readonly List<int> _gone = new();
        private readonly HashSet<int> _seen = new();

        /// <summary>Cell size in pixels, from the grid rect.</summary>
        public Vector2 CellSize => Grid != null
            ? new Vector2(Grid.rect.width / Width, Grid.rect.height / Height)
            : Vector2.zero;

        private void Update() => Redraw();

        /// <summary>Rebuilds the tiles from the replicated vault row.</summary>
        public void Redraw()
        {
            if (Grid == null || TilePrefab == null)
            {
                return;
            }

            _seen.Clear();
            var items = Items();
            var cell = CellSize;

            foreach (var item in items)
            {
                // Keyed by cell, not by list order. The server rewrites the whole
                // list on every change, so an index would make every item a
                // different item the moment anything moved.
                int key = item.Y * Width + item.X;
                _seen.Add(key);

                if (!_tiles.TryGetValue(key, out var tile) || tile == null)
                {
                    tile = Instantiate(TilePrefab, Grid);
                    tile.Owner = this;
                    _tiles[key] = tile;
                }

                var asset = Catalogue != null ? Catalogue.For(item.ItemId) : null;
                // Equipped ignores footprints, matching the server: what you are
                // wearing is not in your pack, so it costs one cell whatever
                // shape it is.
                byte w = 1, h = 1;
                if (Container != 1 && VroxNet.Instance?.Conn is { } conn
                    && conn.Db.ItemDef.Id.Find(item.ItemId) is { } def)
                {
                    w = def.Width < 1 ? (byte)1 : def.Width;
                    h = def.Height < 1 ? (byte)1 : def.Height;
                }

                Place(tile, item.X, item.Y, w, h, cell);
                tile.Render(item.X, item.Y, asset?.Icon,
                            asset != null ? asset.Tint : Color.white,
                            NameOf(item.ItemId), item.Count);
            }

            _gone.Clear();
            foreach (int key in _tiles.Keys)
            {
                if (!_seen.Contains(key))
                {
                    _gone.Add(key);
                }
            }
            foreach (int key in _gone)
            {
                if (_tiles.Remove(key, out var tile) && tile != null)
                {
                    Destroy(tile.gameObject);
                }
            }
        }

        /// <summary>
        /// What is in this container.
        /// </summary>
        /// <remarks>
        /// The vault hangs off the account and everything else off the character
        /// being played, which is the one place those two identities are not
        /// interchangeable — a pack read by account would survive a death it
        /// should not have.
        /// </remarks>
        public IEnumerable<GridItem> Items()
        {
            if (VroxNet.Instance is not { } net || net.Conn is not { } conn)
            {
                return System.Array.Empty<GridItem>();
            }

            if (Container == 3)
            {
                return net.LocalIdentity is { } me
                    && conn.Db.Vault.Identity.Find(me) is { } vault
                        ? Filter(vault.Items)
                        : System.Array.Empty<GridItem>();
            }

            return net.LocalCharacterId != 0
                && conn.Db.Inventory.CharacterId.Find(net.LocalCharacterId) is { } pack
                    ? Filter(pack.Slots)
                    : System.Array.Empty<GridItem>();
        }

        private IEnumerable<GridItem> Filter(List<GridItem> all)
        {
            var mine = new List<GridItem>();
            foreach (var item in all)
            {
                if (item.Container == Container)
                {
                    mine.Add(item);
                }
            }
            return mine;
        }

        internal void Clicked(byte x, byte y) => CellClicked?.Invoke(x, y);

        /// <summary>
        /// Sizes and positions a tile over the cells it covers.
        /// </summary>
        /// <remarks>
        /// Anchored top-left with y growing downward, because a grid is read from
        /// the top and the server's y counts the same way. Unity's UI y grows
        /// upward, which is why this is a subtraction rather than a multiply.
        /// </remarks>
        private void Place(VaultTile tile, byte x, byte y, byte w, byte h, Vector2 cell)
        {
            var rect = (RectTransform)tile.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(cell.x * w - Padding * 2f, cell.y * h - Padding * 2f);
            rect.anchoredPosition = new Vector2(
                x * cell.x + Padding,
                -(y * cell.y) - Padding);
        }

        private static string NameOf(ushort itemId) =>
            VroxNet.Instance?.Conn is { } conn
            && conn.Db.ItemDef.Id.Find(itemId) is { } def
            && !string.IsNullOrWhiteSpace(def.Name)
                ? def.Name
                : $"item {itemId}";
    }
}
