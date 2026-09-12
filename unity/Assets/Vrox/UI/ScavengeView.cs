using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Vrox.Equipment;

namespace Vrox.UI
{
    /// <summary>
    /// The scavenge screen: what is in the bag you are standing over.
    /// </summary>
    /// <remarks>
    /// Shown entirely off the server's answer to "which bag does this player have
    /// open". That field is also what roots the player, so the menu and the
    /// rooting can never disagree — a screen that opened itself would show you
    /// looting while you were still free to run.
    ///
    /// Being dangerous is the point and is not implemented here. Opening a bag
    /// stops the server accepting your movement; standing still in a room that
    /// was a fight a moment ago is the cost, and nothing in this file enforces or
    /// simulates it.
    ///
    /// Owns no layout and creates nothing. Rows are collected from the children of
    /// <see cref="Content"/>, so the screen is built by hand like the inventory.
    /// </remarks>
    public sealed class ScavengeView : MonoBehaviour
    {
        [Tooltip("Shown while a bag is open, hidden otherwise. Leave empty to use " +
                 "this object.")]
        public GameObject? Panel;

        [Tooltip("Rows are collected from this object's children, in hierarchy order. " +
                 "A bag with more entries than there are rows shows the first few.")]
        public RectTransform? Content;

        [Tooltip("Turns item ids into the assets that know what they look like.")]
        public ItemCatalogue? Catalogue;

        [Tooltip("Takes everything that fits.")]
        public Key TakeAllKey = Key.Space;

        [Tooltip("Closes the bag and frees the player to move again.")]
        public Key CloseKey = Key.Escape;

        private readonly List<ScavengeSlot> _rows = new();
        private bool _warnedNoCatalogue;

        /// <summary>The bag the server says is open, or 0.</summary>
        public ulong OpenBagId =>
            VroxNet.Instance?.LocalPlayer is { } player ? player.LootingBag : 0ul;

        private void Awake()
        {
            if (Content != null)
            {
                Content.GetComponentsInChildren(true, _rows);
                foreach (var row in _rows)
                {
                    row.Owner = this;
                }
            }
            else
            {
                Debug.LogError($"[vrox] ScavengeView on '{name}' has no Content assigned, "
                             + "so an opened bag will show nothing.", this);
            }
            Show(false);
        }

        private void Update()
        {
            ulong bagId = OpenBagId;
            Show(bagId != 0);
            if (bagId == 0)
            {
                return;
            }

            if (Keyboard.current is { } keyboard)
            {
                if (keyboard[CloseKey].wasPressedThisFrame)
                {
                    Close();
                    return;
                }
                if (keyboard[TakeAllKey].wasPressedThisFrame)
                {
                    VroxNet.Instance?.Conn?.Reducers.TakeAllFromBag(bagId);
                }
            }

            Draw(bagId);
        }

        /// <summary>Asks for one item. The row disappears when the server agrees.</summary>
        public void Take(ushort itemId)
        {
            if (OpenBagId is var bagId && bagId != 0)
            {
                VroxNet.Instance?.Conn?.Reducers.TakeFromBag(bagId, itemId);
            }
        }

        /// <summary>Closes the bag, which is what frees the player to move.</summary>
        public void Close() => VroxNet.Instance?.Conn?.Reducers.CloseBag();

        private void Show(bool on)
        {
            var panel = Panel != null ? Panel : gameObject;
            if (panel.activeSelf != on)
            {
                panel.SetActive(on);
            }
        }

        private void Draw(ulong bagId)
        {
            if (VroxNet.Instance?.Conn is not { } conn
                || conn.Db.LootDrop.Id.Find(bagId) is not { } bag)
            {
                // The bag went while the screen was up — emptied by someone else,
                // or expired. The server clears the rooting on its own tick, so
                // this only has to stop drawing a bag that is not there.
                foreach (var row in _rows)
                {
                    row.Hide();
                }
                return;
            }

            if (Catalogue == null && !_warnedNoCatalogue)
            {
                _warnedNoCatalogue = true;
                Debug.LogError($"[vrox] ScavengeView on '{name}' has no Item Catalogue, so "
                             + "bag contents will have no icons.", this);
            }

            int free = FreeBackpackSlots(conn);

            for (int i = 0; i < _rows.Count; i++)
            {
                if (i >= bag.Items.Count)
                {
                    _rows[i].Hide();
                    continue;
                }

                var entry = bag.Items[i];
                var asset = Catalogue != null ? Catalogue.For(entry.ItemId) : null;
                string name = conn.Db.ItemDef.Id.Find(entry.ItemId) is { } def
                    ? def.Name
                    : $"item {entry.ItemId}";

                // Room is judged crudely: whether there is any empty slot at all.
                // Working out whether a partial stack could absorb it would be the
                // server's stacking rules living in a second place, and the server
                // leaves what does not fit in the bag anyway.
                _rows[i].Render(asset?.Icon, asset != null ? asset.Tint : Color.white,
                                name, entry.Count, free > 0);
                _rows[i].ItemId = entry.ItemId;
            }

            if (bag.Items.Count > _rows.Count)
            {
                Debug.LogWarning($"[vrox] bag {bagId} holds {bag.Items.Count} entries but the "
                               + $"screen has {_rows.Count} rows; the rest cannot be reached "
                               + "one at a time. Take All still gets them.", this);
            }
        }

        /// <summary>How many backpack slots are empty.</summary>
        private static int FreeBackpackSlots(SpacetimeDB.Types.DbConnection conn)
        {
            if (VroxNet.Instance is not { LocalCharacterId: not 0 } net)
            {
                return 0;
            }

            // Six, matching the server. Duplicated because there is nothing
            // replicated that says how many slots a player has — the server
            // refuses an out-of-range one, so a wrong number here shows up as a
            // "no room" marker that is merely wrong, not as lost items.
            const int backpackSlots = 6;
            int used = 0;
            if (conn.Db.Inventory.CharacterId.Find(net.LocalCharacterId) is { } row)
            {
                foreach (var slot in row.Slots)
                {
                    if (slot.Container == 0)
                    {
                        used++;
                    }
                }
            }
            return backpackSlots - used;
        }
    }
}
