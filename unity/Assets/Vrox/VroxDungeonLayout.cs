using System.Collections.Generic;
using UnityEngine;
using Vrox.Equipment;
using Vrox.Tiles;

namespace Vrox
{
    /// <summary>
    /// Marks a hand-built dungeon: its tilemaps, spawners, entry and exit.
    /// </summary>
    /// <remarks>
    /// Put this on a parent object with a <see cref="VroxArea"/> (the Grid) and
    /// any number of <see cref="VroxSpawner"/>s beneath it. Vrox &gt; Push All
    /// sends it as a layout; nothing here runs in the game.
    ///
    /// Everything is measured from the Grid's origin, not from world zero, so
    /// several layouts can sit side by side in one scene without overlapping on
    /// the server. The realm's own tilemap and spawners are whatever is
    /// <em>not</em> under one of these — the realm push skips anything that is.
    /// </remarks>
    public sealed class VroxDungeonLayout : MonoBehaviour
    {
        [System.Serializable]
        public struct DropEntry
        {
            [Tooltip("An enemy that can drop a portal to this dungeon when it dies.")]
            public EnemyItem? Enemy;

            [Tooltip("Chance per kill, in percent. One portal at most per kill.")]
            [Range(0f, 100f)]
            public float ChancePercent;
        }

        [Tooltip("The server's primary key for this layout. 0 is the realm and is refused.")]
        public ushort Id = 1;

        [Tooltip("Shown in logs. Defaults to the object's name.")]
        public string DisplayName = "";

        [Tooltip("Span in tiles on each axis, from the Grid's origin. Must be a multiple of 16. " +
                 "Paint everything inside it; unpainted ground is solid.")]
        public int Size = 64;

        [Tooltip("Where the portal back to the realm stands. Must be on walkable ground.")]
        public Transform? Exit;

        [Tooltip("Colour of the portals that lead here.")]
        public Color Tint = new Color(0.6f, 0.3f, 1f);

        [Tooltip("A run closes on whoever is still inside after this many seconds. 0 uses the server default.")]
        [Min(0)]
        public int LifetimeSeconds = 900;

        [Tooltip("Which enemies drop an entrance, and how often.")]
        public List<DropEntry> DroppedBy = new();

        private void OnDrawGizmos()
        {
            // The layout's bounds, so a room painted past the edge is visible
            // before the push reports it as lost.
            var origin = GetComponentInChildren<VroxArea>() is { } area ? area.transform.position : transform.position;
            Gizmos.color = new Color(Tint.r, Tint.g, Tint.b, 0.8f);
            Gizmos.DrawWireCube(origin + new Vector3(Size / 2f, Size / 2f, 0f), new Vector3(Size, Size, 0f));

            if (Exit != null)
            {
                Gizmos.DrawWireSphere(Exit.position, 0.7f);
            }
        }
    }
}
