using System.Collections.Generic;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Vrox.Equipment;

namespace Vrox.UI
{
    /// <summary>
    /// One row of the character list: a name, a level, and what they are carrying.
    /// </summary>
    /// <remarks>
    /// The preview reads that character's inventory row directly. It is keyed by
    /// character and public, so a slot can show what a character holds without
    /// that character being the one being played — which is the whole point of a
    /// select screen.
    /// </remarks>
    public sealed class CharacterSlotView : MonoBehaviour, IPointerClickHandler
    {
        [Tooltip("Shown for an occupied slot.")]
        public GameObject? Filled;

        [Tooltip("Shown for an empty slot — the plus placeholder.")]
        public GameObject? Empty;

        [Tooltip("Shown while this slot is the selected one.")]
        public GameObject? Highlight;

        public TMP_Text? NameLabel;
        public TMP_Text? LevelLabel;

        [Tooltip("Small boxes previewing what the character carries, in order: " +
                 "equipped first, then the pack. Extra boxes are hidden.")]
        public List<Image> Preview = new();

        [Tooltip("Turns item ids into the assets that know what they look like.")]
        public ItemCatalogue? Catalogue;

        internal CharacterSelectView? Owner;
        internal int Index;

        public void Render(Character character, bool selected)
        {
            Toggle(Filled, true);
            Toggle(Empty, false);
            Toggle(Highlight, selected);

            if (NameLabel != null)
            {
                NameLabel.text = character.Name;
            }
            if (LevelLabel != null)
            {
                LevelLabel.text = $"LEVEL {character.Level}";
            }

            DrawPreview(character.Id);
        }

        public void RenderEmpty(bool selected)
        {
            Toggle(Filled, false);
            Toggle(Empty, true);
            Toggle(Highlight, selected);

            foreach (var box in Preview)
            {
                if (box != null)
                {
                    box.enabled = false;
                }
            }
        }

        /// <summary>
        /// Fills the preview boxes with what the character holds.
        /// </summary>
        /// <remarks>
        /// Equipped before carried, so the boxes read as "what they are wearing,
        /// then what they have on them" rather than in whatever order the server
        /// happened to store them.
        /// </remarks>
        private void DrawPreview(ulong characterId)
        {
            var order = new List<GridItem>();
            if (VroxNet.Instance?.Conn is { } conn
                && conn.Db.Inventory.CharacterId.Find(characterId) is { } row)
            {
                foreach (var slot in row.Slots)
                {
                    if (slot.Container == 1) order.Add(slot);
                }
                foreach (var slot in row.Slots)
                {
                    if (slot.Container != 1) order.Add(slot);
                }
            }

            for (int i = 0; i < Preview.Count; i++)
            {
                var box = Preview[i];
                if (box == null)
                {
                    continue;
                }
                if (i >= order.Count)
                {
                    box.enabled = false;
                    continue;
                }

                var asset = Catalogue != null ? Catalogue.For(order[i].ItemId) : null;
                // Left blank rather than filled with a stand-in when the catalogue
                // has no entry. A preview box showing the wrong item is worse than
                // one showing nothing, because it is read at a glance and believed.
                box.enabled = asset?.Icon != null;
                if (box.enabled)
                {
                    box.sprite = asset!.Icon;
                    box.color = asset.Tint;
                }
            }
        }

        private static void Toggle(GameObject? go, bool on)
        {
            if (go != null && go.activeSelf != on)
            {
                go.SetActive(on);
            }
        }

        public void OnPointerClick(PointerEventData eventData) => Owner?.Select(Index);
    }
}
