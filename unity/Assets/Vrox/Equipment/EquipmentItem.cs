using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>What slot an item occupies.</summary>
    public enum EquipmentKind : byte
    {
        Weapon = 0,
        Armor = 1,
        Jewellery = 2,
        Consumable = 3,
    }

    /// <summary>
    /// Anything that can sit in an inventory slot.
    /// </summary>
    /// <remarks>
    /// These assets are the *authoring* format. The server keeps its own
    /// catalogue, replicated to clients, and that is what the game actually runs
    /// on — so an asset is a convenient way to write a row, not the row itself.
    /// The editor pushes them across; see <c>Vrox → Push Equipment</c>.
    ///
    /// That split is what answers "what about runtime modification": the server's
    /// row *is* the runtime version. Change it and every client sees the change on
    /// the next shot, without anyone rebuilding an asset.
    /// </remarks>
    public abstract class EquipmentItem : ScriptableObject
    {
        [Tooltip("Stable id. This is what the server stores and what saves refer to — " +
                 "renaming the asset is safe, changing this is not.")]
        public ushort Id = 1;

        [Tooltip("Shown to the player.")]
        public string DisplayName = "Unnamed";

        [TextArea]
        public string Description = "";

        public Sprite? Icon;

        public Color Tint = Color.white;

        [Tooltip("How many vault cells wide this is. The carried pack is one item " +
                 "per slot regardless; this is only the grid.")]
        [Range(1, 8)]
        public byte Width = 1;

        [Tooltip("How many vault cells tall this is.")]
        [Range(1, 8)]
        public byte Height = 1;

        [Tooltip("Rarity band. 0 is common. Colours this item's tile in bags and the " +
                 "inventory. It does not decide a bag's kind: that comes from the loot " +
                 "pool the bag dropped from.")]
        [Range(0, 5)]
        public byte Tier;

        public abstract EquipmentKind Kind { get; }

        /// <summary>Highest id a single kind can author. 14 bits.</summary>
        public const ushort MaxAuthoredId = 0x3FFF;

        /// <summary>
        /// The id the server catalogue stores: this item's kind packed above its
        /// authored id.
        /// </summary>
        /// <remarks>
        /// A bag and an inventory slot record an id and nothing else, so every
        /// kind has to be distinguishable from that id alone — otherwise a sword
        /// and a helmet both authored as 1 are the same item to everything
        /// downstream. Packing keeps each kind numbering from 1 independently,
        /// which is how they are already authored, rather than making someone
        /// hand out globally unique numbers and remember the convention.
        ///
        /// The same trick the module already uses for <c>PhaseDef.Key</c> and
        /// <c>LootEntry.Key</c>, both of which are an owner id shifted above an
        /// index.
        ///
        /// Weapons are kind 0, so a weapon's catalogue id <em>is</em> its authored
        /// id. That is deliberate and load-bearing: <c>Player.WeaponId</c>,
        /// <c>EnemyDef.WeaponId</c> and <c>PhaseDef.WeaponId</c> all carry raw
        /// weapon ids, and they keep working untouched.
        /// </remarks>
        public ushort CatalogueId => (ushort)(((int)Kind << 14) | (Id & MaxAuthoredId));

        /// <summary>How many of this fit in one inventory slot.</summary>
        /// <remarks>
        /// Lives here rather than only on the kinds that stack, because the
        /// inventory has to ask every item the same question and cannot know in
        /// advance which types answer it. One means "does not stack", which is
        /// the honest default for a weapon or a piece of armour.
        /// </remarks>
        public virtual int MaxStack => 1;
    }
}
