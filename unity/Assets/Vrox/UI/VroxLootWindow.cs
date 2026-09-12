using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Vrox.Equipment;

namespace Vrox.UI
{
    /// <summary>
    /// The pack, what is equipped, and whatever bag is open.
    /// </summary>
    /// <remarks>
    /// IMGUI, like <see cref="DamageNumbers"/> and for the same reason: it needs
    /// no canvas, no prefab and no event system, so the whole window is one
    /// component on one object and a scene carries nothing that can come
    /// unwired. The uGUI views beside this one (<c>GridView</c>,
    /// <c>ScavengeView</c>, <c>LoadoutView</c>) are a richer path that wants a
    /// designed canvas; they are left alone, and whichever survives is a decision
    /// for whoever builds that canvas.
    ///
    /// Nothing here decides anything. Every action is a reducer call and the
    /// window redraws from whatever the server replicates back — so a take that
    /// loses a race to another player simply does not change the grid, rather
    /// than showing an item that is not there. That is the same rule the rest of
    /// the client follows: reconcile against replicated state, never against a
    /// local record of intent.
    /// </remarks>
    public sealed class VroxLootWindow : MonoBehaviour
    {
        [Tooltip("Resolves an item id into something with a name, a colour and a footprint. " +
                 "Without it the window still works and every item draws as an unnamed " +
                 "grey tile, which is deliberately ugly rather than invisible.")]
        public ItemCatalogue? Items;

        [Tooltip("Shows and hides the pack. The bag panel ignores this — a bag you have " +
                 "opened is always drawn, because you are rooted until it closes.")]
        public Key ToggleKey = Key.Tab;

        [Tooltip("Open the pack whenever a bag is.")]
        public bool OpenPackWithBag = true;

        [Header("Layout")]
        [Range(24, 96)]
        public int CellSize = 46;

        [Range(0, 12)]
        public int CellGap = 4;

        [Range(0, 200)]
        public int MarginX = 16;

        [Range(0, 200)]
        public int MarginY = 16;

        [Header("Colour")]
        public Color PanelColour = new Color(0.07f, 0.08f, 0.10f, 0.92f);
        public Color CellColour = new Color(0.16f, 0.17f, 0.20f, 0.95f);
        public Color CellEdge = new Color(0.30f, 0.32f, 0.38f, 1f);
        public Color EquippedEdge = new Color(0.95f, 0.80f, 0.35f, 1f);

        [Tooltip("Tier colours, indexed 0..5. An item's tier picks its border, which is " +
                 "the whole reason a bag is worth crossing a room for.")]
        public Color[] TierColours =
        {
            new Color(0.72f, 0.72f, 0.72f),
            new Color(0.45f, 0.85f, 0.45f),
            new Color(0.40f, 0.65f, 1.00f),
            new Color(0.72f, 0.45f, 0.95f),
            new Color(1.00f, 0.60f, 0.25f),
            new Color(1.00f, 0.85f, 0.30f),
        };

        // Server container ids. Mirrored from the module rather than shared,
        // because the generated bindings expose reducers and rows but not the
        // module's own constants.
        private const byte ContainerPack = 0;
        private const byte ContainerEquipped = 1;

        private const byte PackWidth = 5;
        private const byte PackHeight = 3;
        private const byte EquippedSlots = 3;

        private bool _packOpen;

        /// <summary>Where a drag started, or null. Cleared on mouse up, always.</summary>
        private (byte container, byte x, byte y)? _dragFrom;

        private Texture2D? _white;
        private GUIStyle? _label;
        private GUIStyle? _title;
        private GUIStyle? _count;

        private readonly List<Rect> _cellRects = new();
        private readonly List<(byte container, byte x, byte y)> _cellKeys = new();

        private void Update()
        {
            if (Keyboard.current is { } keys && keys[ToggleKey].wasPressedThisFrame)
            {
                _packOpen = !_packOpen;
            }
        }

        private void OnGUI()
        {
            if (VroxNet.Instance is not { } net || net.Conn is not { } conn)
            {
                return;
            }
            if (net.LocalPlayer is not { } player || player.CharacterId == 0)
            {
                return;
            }

            _white ??= Solid(Color.white);
            _label ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter, fontSize = 9, wordWrap = true,
            };
            _title ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft, fontSize = 13, fontStyle = FontStyle.Bold,
            };
            _count ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.LowerRight, fontSize = 11, fontStyle = FontStyle.Bold,
            };

            bool bagOpen = player.LootingBag != 0;
            bool showPack = _packOpen || (bagOpen && OpenPackWithBag);

            _cellRects.Clear();
            _cellKeys.Clear();

            int step = CellSize + CellGap;
            int packW = PackWidth * step - CellGap;
            int y = Screen.height - MarginY;

            if (showPack)
            {
                int packH = PackHeight * step - CellGap;
                int equipH = step;
                int panelH = packH + equipH + 46;
                var panel = new Rect(MarginX, y - panelH, packW + 20, panelH);
                Fill(panel, PanelColour);

                GUI.Label(new Rect(panel.x + 10, panel.y + 4, 200, 18), "PACK", _title);

                DrawGrid(conn, player, ContainerEquipped, EquippedSlots, 1,
                         panel.x + 10, panel.y + 24, step, equipped: true);
                DrawGrid(conn, player, ContainerPack, PackWidth, PackHeight,
                         panel.x + 10, panel.y + 24 + equipH + 8, step, equipped: false);

                y -= panelH + 10;
            }

            if (bagOpen)
            {
                DrawBag(conn, player, MarginX, y, packW + 20, step);
            }

            HandleDrag(conn);
        }

        /// <summary>Draws one container's cells and whatever sits in them.</summary>
        private void DrawGrid(SpacetimeDB.Types.DbConnection conn,
                              SpacetimeDB.Types.Player player,
                              byte container, int width, int height,
                              float ox, float oy, int step, bool equipped)
        {
            var slots = Slots(conn, player);

            for (int gy = 0; gy < height; gy++)
            {
                for (int gx = 0; gx < width; gx++)
                {
                    var cell = new Rect(ox + gx * step, oy + gy * step, CellSize, CellSize);
                    Fill(cell, CellColour);
                    Outline(cell, equipped ? EquippedEdge : CellEdge);

                    // The equipped row is typed by position on the server — x 0
                    // takes a weapon, 1 armour, 2 jewellery — and a move into the
                    // wrong one is simply refused. Naming them is the difference
                    // between a rule and a mystery.
                    if (equipped)
                    {
                        var previous = GUI.color;
                        GUI.color = new Color(1f, 1f, 1f, 0.35f);
                        GUI.Label(cell, SlotName(gx), _label);
                        GUI.color = previous;
                    }

                    _cellRects.Add(cell);
                    _cellKeys.Add((container, (byte)gx, (byte)gy));
                }
            }

            // Items after every cell, so one spanning two cells is not drawn over
            // by the empty cell beside it.
            foreach (var slot in slots)
            {
                if (slot.Container != container || slot.ItemId == 0)
                {
                    continue;
                }
                var def = Items?.For(slot.ItemId);
                int w = equipped ? 1 : Mathf.Max(1, def?.Width ?? 1);
                int h = equipped ? 1 : Mathf.Max(1, def?.Height ?? 1);
                var rect = new Rect(ox + slot.X * step, oy + slot.Y * step,
                                    w * step - CellGap, h * step - CellGap);
                DrawItem(rect, slot.ItemId, slot.Count, def);
            }
        }

        /// <summary>Draws the open bag, with a take-all and a close.</summary>
        private void DrawBag(SpacetimeDB.Types.DbConnection conn,
                             SpacetimeDB.Types.Player player, float x, float y,
                             float width, int step)
        {
            var bag = conn.Db.LootDrop.Id.Find(player.LootingBag);

            // The row can be gone before the client hears about it — another
            // player emptied it, or it expired. Drawing an empty frame with a
            // close button is the honest thing: the player is still rooted until
            // the server says otherwise, and needs the way out.
            var contents = bag?.Items ?? new List<SpacetimeDB.Types.BagItem>();

            int rows = Mathf.Max(1, Mathf.CeilToInt(contents.Count / 4f));
            float panelH = rows * step + 62;
            var panel = new Rect(x, y - panelH, width, panelH);
            Fill(panel, PanelColour);

            byte kind = bag?.BagKind ?? (byte)0;
            Outline(panel, TierColour(kind));
            GUI.Label(new Rect(panel.x + 10, panel.y + 4, 220, 18),
                      bag == null ? "BAG — EMPTY" : $"BAG — {BagName(kind)}", _title);

            for (int i = 0; i < contents.Count; i++)
            {
                var cell = new Rect(panel.x + 10 + (i % 4) * step,
                                    panel.y + 26 + (i / 4) * step, CellSize, CellSize);
                Fill(cell, CellColour);
                Outline(cell, CellEdge);
                var def = Items?.For(contents[i].ItemId);
                DrawItem(cell, contents[i].ItemId, contents[i].Count, def);

                // One click takes one entry. Addressed by item id, which is what
                // the reducer wants — an index would be a different item by the
                // time a second player's take landed.
                if (Clicked(cell))
                {
                    conn.Reducers.TakeFromBag(player.LootingBag, contents[i].ItemId);
                }
            }

            var takeAll = new Rect(panel.x + 10, panel.yMax - 30, 90, 22);
            if (contents.Count > 0 && GUI.Button(takeAll, "Take All"))
            {
                conn.Reducers.TakeAllFromBag(player.LootingBag);
            }
            if (GUI.Button(new Rect(panel.x + 108, panel.yMax - 30, 70, 22), "Close"))
            {
                conn.Reducers.CloseBag();
            }
            GUI.Label(new Rect(panel.x + 186, panel.yMax - 29, 240, 20),
                      "click an item to take it", _label);
        }

        /// <summary>One item tile: its colour, its name, and how many.</summary>
        private void DrawItem(Rect rect, ushort itemId, ushort count, EquipmentItem? def)
        {
            var tier = def != null ? TierColour(def.Tier) : Color.grey;

            var body = tier;
            body.a = 0.22f;
            Fill(rect, body);
            Outline(rect, tier);

            if (def?.Icon is { } icon && icon.texture != null)
            {
                var tr = icon.textureRect;
                var uv = new Rect(tr.x / icon.texture.width, tr.y / icon.texture.height,
                                  tr.width / icon.texture.width, tr.height / icon.texture.height);
                GUI.DrawTextureWithTexCoords(Inset(rect, 4), icon.texture, uv, alphaBlend: true);
            }
            else
            {
                // No icon on most items yet. A name is more use than a blank
                // square, and reads as unfinished art rather than a missing item.
                var previous = GUI.color;
                GUI.color = Color.white;
                GUI.Label(Inset(rect, 3), def?.DisplayName ?? $"#{itemId}", _label);
                GUI.color = previous;
            }

            if (count > 1)
            {
                GUI.Label(Inset(rect, 2), count.ToString(), _count);
            }
        }

        /// <summary>
        /// Turns a press-then-release across two cells into one move.
        /// </summary>
        /// <remarks>
        /// Resolved on mouse *up* against whatever cell the cursor is over, not on
        /// every drag frame — a move is one reducer call, and firing one per frame
        /// would send a burst the server has to reject one at a time.
        ///
        /// The drag is always cleared, including when it ends on nothing. Leaving
        /// it set would make the next click anywhere finish a move the player
        /// abandoned, which is the sort of bug that loses somebody an item.
        /// </remarks>
        private void HandleDrag(SpacetimeDB.Types.DbConnection conn)
        {
            var e = Event.current;
            if (e == null)
            {
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                int i = CellAt(e.mousePosition);
                if (i >= 0)
                {
                    _dragFrom = _cellKeys[i];
                }
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                if (_dragFrom is { } from)
                {
                    int i = CellAt(e.mousePosition);
                    if (i >= 0)
                    {
                        var to = _cellKeys[i];
                        if (to != from)
                        {
                            conn.Reducers.MoveItem(from.container, from.x, from.y,
                                                   to.container, to.x, to.y);
                        }
                    }
                }
                _dragFrom = null;
            }
        }

        private int CellAt(Vector2 point)
        {
            for (int i = 0; i < _cellRects.Count; i++)
            {
                if (_cellRects[i].Contains(point))
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool Clicked(Rect rect)
        {
            var e = Event.current;
            if (e != null && e.type == EventType.MouseDown && e.button == 0
                && rect.Contains(e.mousePosition))
            {
                e.Use();
                return true;
            }
            return false;
        }

        private static IEnumerable<SpacetimeDB.Types.GridItem> Slots(
            SpacetimeDB.Types.DbConnection conn, SpacetimeDB.Types.Player player)
        {
            var pack = conn.Db.Inventory.CharacterId.Find(player.CharacterId);
            return pack?.Slots ?? new List<SpacetimeDB.Types.GridItem>();
        }

        private Color TierColour(int tier) =>
            TierColours != null && tier >= 0 && tier < TierColours.Length
                ? TierColours[tier]
                : Color.grey;

        private static string SlotName(int x) => x switch
        {
            0 => "WEAPON",
            1 => "ARMOUR",
            2 => "RING",
            _ => "",
        };

        private static string BagName(byte kind) => kind switch
        {
            1 => "UNCOMMON",
            2 => "RARE",
            3 => "EPIC",
            4 => "LEGENDARY",
            5 => "BOSS",
            _ => "COMMON",
        };

        private static Rect Inset(Rect r, float by) =>
            new Rect(r.x + by, r.y + by, r.width - by * 2f, r.height - by * 2f);

        private void Fill(Rect rect, Color colour)
        {
            var previous = GUI.color;
            GUI.color = colour;
            GUI.DrawTexture(rect, _white!);
            GUI.color = previous;
        }

        private void Outline(Rect rect, Color colour)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, 1f), colour);
            Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), colour);
            Fill(new Rect(rect.x, rect.y, 1f, rect.height), colour);
            Fill(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), colour);
        }

        private static Texture2D Solid(Color colour)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, colour);
            t.Apply();
            return t;
        }

        private void OnDestroy()
        {
            if (_white != null)
            {
                Destroy(_white);
            }
        }
    }
}
