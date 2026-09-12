using System.Collections.Generic;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.UI;
using Vrox.Equipment;

namespace Vrox.UI
{
    /// <summary>
    /// The character screen: pick one, or build a new one out of the vault.
    /// </summary>
    /// <remarks>
    /// Shown whenever the account is playing nobody. That is the server's answer,
    /// not a mode this keeps — a screen that decided for itself when it was open
    /// could show a character select while the player was standing in the world.
    ///
    /// Three states, and the middle one is why they are all in one component:
    /// choosing an existing character, building a new one, and nothing selected.
    /// The loadout being assembled only exists here, because until Play is pressed
    /// there is no character to hang it on and nothing to tell the server about.
    ///
    /// Owns no layout. Slots, the loadout row and the vault grid are all built by
    /// hand and handed in.
    /// </remarks>
    public sealed class CharacterSelectView : MonoBehaviour
    {
        [Tooltip("Shown while the account has no character in the world.")]
        public GameObject? Panel;

        [Header("Slots")]
        [Tooltip("One per character slot, in order. Collected from this object's children.")]
        public RectTransform? SlotRoot;

        [Header("Creation")]
        [Tooltip("Shown only while an empty slot is selected. The cascading loadout.")]
        public GameObject? CreationPanel;

        [Tooltip("Where the chosen loadout is shown, before the character exists.")]
        public LoadoutView? Loadout;

        [Tooltip("The name being typed for a new character.")]
        public TMPro.TMP_InputField? NameField;

        [Header("Vault")]
        public GridView? Vault;

        [Header("Play")]
        public Button? PlayButton;

        [Tooltip("Optional. Explains why Play is unavailable.")]
        public TMPro.TMP_Text? PlayHint;

        private readonly List<CharacterSlotView> _slots = new();
        private readonly List<VaultCell> _picked = new();

        /// <summary>Which slot index is selected, or -1.</summary>
        public int Selected { get; private set; } = -1;

        /// <summary>The character in the selected slot, or null for an empty one.</summary>
        public Character? SelectedCharacter { get; private set; }

        private void Awake()
        {
            if (SlotRoot != null)
            {
                SlotRoot.GetComponentsInChildren(true, _slots);
                for (int i = 0; i < _slots.Count; i++)
                {
                    _slots[i].Owner = this;
                    _slots[i].Index = i;
                }
            }
            else
            {
                Debug.LogError($"[vrox] CharacterSelectView on '{name}' has no Slot Root, "
                             + "so no character can be chosen.", this);
            }

            if (Vault != null)
            {
                Vault.CellClicked += OnVaultCellClicked;
            }
            if (PlayButton != null)
            {
                PlayButton.onClick.AddListener(Play);
            }
        }

        private void OnDestroy()
        {
            if (Vault != null)
            {
                Vault.CellClicked -= OnVaultCellClicked;
            }
        }

        private void Update()
        {
            bool atScreen = VroxNet.Instance is { LocalCharacterId: 0 } net && net.Conn != null;
            if (Panel != null && Panel.activeSelf != atScreen)
            {
                Panel.SetActive(atScreen);
            }
            if (!atScreen)
            {
                return;
            }

            var mine = Characters();
            for (int i = 0; i < _slots.Count; i++)
            {
                if (i < mine.Count)
                {
                    _slots[i].Render(mine[i], i == Selected);
                }
                else
                {
                    _slots[i].RenderEmpty(i == Selected);
                }
            }

            // The selection can outlive what it pointed at: a character dies while
            // this is open, and the slot beneath the highlight becomes a different
            // one. Re-read rather than remembered.
            SelectedCharacter = Selected >= 0 && Selected < mine.Count ? mine[Selected] : null;
            bool creating = Selected >= 0 && SelectedCharacter == null;

            if (CreationPanel != null && CreationPanel.activeSelf != creating)
            {
                CreationPanel.SetActive(creating);
            }
            if (Loadout != null && creating)
            {
                Loadout.Render(_picked);
            }

            UpdatePlay(creating);
        }

        /// <summary>The account's characters, oldest first so slots do not shuffle.</summary>
        /// <remarks>
        /// Sorted by id rather than left in table order, which is not stable.
        /// Without it a character could appear to swap slots between frames, and
        /// clicking a slot would select whoever happened to be there.
        /// </remarks>
        public List<Character> Characters()
        {
            var found = new List<Character>();
            if (VroxNet.Instance is { } net && net.Conn is { } conn
                && net.LocalIdentity is { } me)
            {
                foreach (var character in conn.Db.Character.Account.Filter(me))
                {
                    found.Add(character);
                }
                found.Sort((a, b) => a.Id.CompareTo(b.Id));
            }
            return found;
        }

        /// <summary>Called by a slot when it is clicked.</summary>
        public void Select(int index)
        {
            if (Selected != index)
            {
                // The loadout belongs to the slot being built, so moving away
                // clears it. Carrying it over would silently arm a different
                // character with choices made for another.
                _picked.Clear();
            }
            Selected = index;
        }

        private void OnVaultCellClicked(byte x, byte y)
        {
            if (SelectedCharacter != null || Selected < 0)
            {
                // Only meaningful while building. Clicking the vault with a living
                // character selected is a request this screen cannot honour —
                // withdrawing needs a character standing at the vault.
                return;
            }

            for (int i = 0; i < _picked.Count; i++)
            {
                if (_picked[i].X == x && _picked[i].Y == y)
                {
                    _picked.RemoveAt(i);
                    return;
                }
            }
            _picked.Add(new VaultCell { X = x, Y = y });
        }

        private void UpdatePlay(bool creating)
        {
            string reason = "";
            if (Selected < 0)
            {
                reason = "Choose a character";
            }
            else if (creating && string.IsNullOrWhiteSpace(NameField?.text))
            {
                reason = "Name your character";
            }

            if (PlayButton != null)
            {
                PlayButton.interactable = reason.Length == 0;
            }
            if (PlayHint != null)
            {
                PlayHint.text = reason;
            }
        }

        /// <summary>Enters the world, creating the character first if it is new.</summary>
        /// <remarks>
        /// Creation and selection are two calls, and deliberately not merged. A
        /// combined reducer would have to decide what to do when the name is taken
        /// or the vault has moved on, halfway through — whereas a creation that
        /// fails simply leaves the screen up with the loadout still chosen.
        /// </remarks>
        public void Play()
        {
            if (VroxNet.Instance?.Conn is not { } conn || Selected < 0)
            {
                return;
            }

            if (SelectedCharacter is { } existing)
            {
                conn.Reducers.SelectCharacter(existing.Id);
                return;
            }

            string chosen = NameField != null ? NameField.text.Trim() : "";
            if (chosen.Length == 0)
            {
                return;
            }

            // Selecting is left to the next frame: the character does not exist
            // until the server says so, and its id is only knowable from the row
            // that comes back.
            conn.Reducers.CreateCharacter(chosen, new List<VaultCell>(_picked));
            _picked.Clear();
            _pendingName = chosen;
        }

        private string _pendingName = "";

        private void LateUpdate()
        {
            // A character created this session and not yet entered. Matched by
            // name because the id is assigned by the server and arrives with the
            // row; this is the one frame where the client does not know it.
            if (_pendingName.Length == 0 || VroxNet.Instance?.Conn is not { } conn)
            {
                return;
            }
            foreach (var character in Characters())
            {
                if (character.Name == _pendingName)
                {
                    _pendingName = "";
                    conn.Reducers.SelectCharacter(character.Id);
                    return;
                }
            }
        }
    }
}
