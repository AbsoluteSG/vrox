using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Vrox.UI
{
    /// <summary>One item drawn on the vault grid.</summary>
    /// <remarks>
    /// Sized and positioned by <see cref="GridView"/>, which knows the cell
    /// size; this only draws what it is handed and reports clicks by cell.
    ///
    /// It remembers its cell rather than its item because everything the vault
    /// offers is addressed by cell — the same item can sit in two places.
    /// </remarks>
    public sealed class VaultTile : MonoBehaviour, IPointerClickHandler
    {
        public Image? Icon;
        public TMP_Text? Label;
        public TMP_Text? Count;

        internal GridView? Owner;

        private byte _x;
        private byte _y;

        public void Render(byte x, byte y, Sprite? icon, Color tint, string name, int count)
        {
            _x = x;
            _y = y;

            if (Icon != null)
            {
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
        }

        public void OnPointerClick(PointerEventData eventData) => Owner?.Clicked(_x, _y);
    }
}
