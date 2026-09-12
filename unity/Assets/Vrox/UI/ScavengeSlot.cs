using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Vrox.UI
{
    /// <summary>
    /// One row of the scavenge screen: an item in the bag you have open.
    /// </summary>
    /// <remarks>
    /// A view and a click target, nothing more. What is in the bag lives on the
    /// server and is read by <see cref="ScavengeView"/>; a row that remembered
    /// its own item would be a second copy of a bag that another player can empty
    /// while you are looking at it.
    ///
    /// Clicking asks for that item. It does not remove it — the row goes away
    /// when the server says the bag no longer holds it, which is also what
    /// happens when somebody else took it first.
    /// </remarks>
    public sealed class ScavengeSlot : MonoBehaviour, IPointerClickHandler
    {
        [Tooltip("Child image showing the item.")]
        public Image? Icon;

        [Tooltip("Item name.")]
        public TMP_Text? Label;

        [Tooltip("How many. Hidden when the entry is a single item.")]
        public TMP_Text? Count;

        [Tooltip("Optional. Shown when this item would not fit in your pack.")]
        public GameObject? FullMarker;

        internal ScavengeView? Owner;
        internal ushort ItemId;

        /// <summary>Draws one bag entry, or hides the row when there is none.</summary>
        public void Render(Sprite? icon, Color tint, string name, int count, bool fits)
        {
            gameObject.SetActive(true);
            ItemId = 0;

            if (Icon != null)
            {
                // Disabled rather than given a null sprite: an Image with no
                // sprite still draws a white box, which reads as a mystery item.
                Icon.enabled = icon != null;
                if (icon != null)
                {
                    Icon.sprite = icon;
                    Icon.color = tint;
                }
            }
            if (Label != null)
            {
                Label.text = name;
            }
            if (Count != null)
            {
                bool show = count > 1;
                Count.enabled = show;
                if (show)
                {
                    Count.text = $"x{count}";
                }
            }
            if (FullMarker != null)
            {
                // Said before the click, not after. Reaching for something you
                // cannot carry while rooted in the open is the worst moment to
                // find out, and the server would simply refuse.
                FullMarker.SetActive(!fits);
            }
        }

        public void Hide() => gameObject.SetActive(false);

        public void OnPointerClick(PointerEventData eventData)
        {
            if (ItemId != 0)
            {
                Owner?.Take(ItemId);
            }
        }
    }
}
