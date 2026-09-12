using System.Collections.Generic;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Vrox.Equipment;

namespace Vrox.UI
{
    /// <summary>
    /// The loadout being assembled for a character that does not exist yet.
    /// </summary>
    /// <remarks>
    /// Draws vault cells the player has picked, sorted into the shape the
    /// creation screen shows: a weapon, a piece of armour, then everything else.
    ///
    /// It holds nothing. The picks live on <see cref="CharacterSelectView"/> and
    /// are handed in each frame, because until Play is pressed there is no
    /// character and no server row for any of this to be true of. Keeping a copy
    /// here would be a second answer to what is being taken.
    ///
    /// Which slot an item ends up in is decided by the server at creation, from
    /// the item's kind. This is a preview of that, not a second implementation —
    /// if the two ever disagree, the server is right.
    /// </remarks>
    public sealed class LoadoutView : MonoBehaviour
    {
        [Tooltip("The weapon box. Empty until a weapon is picked.")]
        public LoadoutSlotView? Weapon;

        [Tooltip("The armour box.")]
        public LoadoutSlotView? Armor;

        [Tooltip("The remaining boxes, filled in pick order — potions and anything else.")]
        public List<LoadoutSlotView> Others = new();

        [Tooltip("Turns item ids into the assets that know what they look like.")]
        public ItemCatalogue? Catalogue;

        [Tooltip("Optional. Says when more was picked than the boxes can show.")]
        public TMP_Text? Overflow;

        /// <summary>Draws the picks. Called every frame while creating.</summary>
        public void Render(IReadOnlyList<VaultCell> picked)
        {
            LoadoutSlotView? weapon = Weapon;
            LoadoutSlotView? armor = Armor;
            int other = 0;
            int shown = 0;

            Clear();

            if (VroxNet.Instance?.Conn is not { } conn)
            {
                return;
            }

            foreach (var cell in picked)
            {
                if (!ItemAt(conn, cell, out ushort itemId, out ushort count))
                {
                    // The cell no longer holds anything — another character was
                    // made from it, or the vault was rearranged. Skipped rather
                    // than drawn as blank, so the row shows what would actually
                    // be taken.
                    continue;
                }

                var def = conn.Db.ItemDef.Id.Find(itemId);
                var asset = Catalogue != null ? Catalogue.For(itemId) : null;
                string label = def is { } named && !string.IsNullOrWhiteSpace(named.Name)
                    ? named.Name
                    : $"item {itemId}";

                LoadoutSlotView? target = null;
                if (def is { Kind: 0 } && weapon != null)
                {
                    target = weapon;
                    weapon = null;
                }
                else if (def is { Kind: 1 } && armor != null)
                {
                    target = armor;
                    armor = null;
                }
                else if (other < Others.Count)
                {
                    target = Others[other++];
                }

                if (target == null)
                {
                    continue;
                }

                target.Render(asset?.Icon, asset != null ? asset.Tint : Color.white,
                              label, count);
                shown++;
            }

            if (Overflow != null)
            {
                int missed = picked.Count - shown;
                Overflow.enabled = missed > 0;
                if (missed > 0)
                {
                    Overflow.text = $"{missed} more picked than there is room to show";
                }
            }
        }

        /// <summary>What sits at a vault cell, accounting for footprints.</summary>
        private static bool ItemAt(DbConnection conn, VaultCell cell,
                                   out ushort itemId, out ushort count)
        {
            itemId = 0;
            count = 0;
            if (VroxNet.Instance?.LocalIdentity is not { } me
                || conn.Db.Vault.Identity.Find(me) is not { } vault)
            {
                return false;
            }

            foreach (var item in vault.Items)
            {
                byte w = 1, h = 1;
                if (conn.Db.ItemDef.Id.Find(item.ItemId) is { } def)
                {
                    w = def.Width < 1 ? (byte)1 : def.Width;
                    h = def.Height < 1 ? (byte)1 : def.Height;
                }
                if (cell.X >= item.X && cell.X < item.X + w
                    && cell.Y >= item.Y && cell.Y < item.Y + h)
                {
                    itemId = item.ItemId;
                    count = item.Count;
                    return true;
                }
            }
            return false;
        }

        private void Clear()
        {
            Weapon?.Clear();
            Armor?.Clear();
            foreach (var slot in Others)
            {
                if (slot != null)
                {
                    slot.Clear();
                }
            }
        }
    }
}
