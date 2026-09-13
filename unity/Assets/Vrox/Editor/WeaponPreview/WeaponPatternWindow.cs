using UnityEditor;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// The old entry point to the weapon designer, now the Pattern tab of <see cref="EnemyDesignerWindow"/>.
    /// </summary>
    /// <remarks>
    /// Kept so the menu item people already know still works. The window this
    /// used to be is gone: a tab of it left docked in a saved layout shows Unity's
    /// "failed to load" placeholder once and can be closed.
    /// </remarks>
    public static class WeaponPatternWindow
    {
        [MenuItem("Vrox/Weapon Pattern Designer")]
        private static void Menu() => EnemyDesignerWindow.ShowWeapon(null);

        public static void Open(WeaponItem? weapon) => EnemyDesignerWindow.ShowWeapon(weapon);
    }
}
