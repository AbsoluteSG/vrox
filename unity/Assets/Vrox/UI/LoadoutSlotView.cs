using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Vrox.UI
{
    /// <summary>One box of the loadout row: weapon, armour, or a spare.</summary>
    /// <remarks>
    /// A view and nothing else. What is in it is decided by
    /// <see cref="LoadoutView"/> from the picks, and the empty state is a real
    /// state rather than an absence — the boxes exist before anything is chosen,
    /// which is what makes the row read as a shape to fill.
    /// </remarks>
    public sealed class LoadoutSlotView : MonoBehaviour
    {
        public Image? Icon;
        public TMP_Text? Label;
        public TMP_Text? Count;

        [Tooltip("Shown when nothing is in this box. The 'WEAPON' or 'ARMOR' caption.")]
        public GameObject? Placeholder;

        public void Render(Sprite? icon, Color tint, string name, int count)
        {
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
                Label.enabled = true;
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
            if (Placeholder != null)
            {
                Placeholder.SetActive(false);
            }
        }

        public void Clear()
        {
            if (Icon != null) Icon.enabled = false;
            if (Label != null) Label.enabled = false;
            if (Count != null) Count.enabled = false;
            if (Placeholder != null) Placeholder.SetActive(true);
        }
    }
}
