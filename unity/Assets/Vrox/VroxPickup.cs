using UnityEngine;
using UnityEngine.InputSystem;

namespace Vrox
{
    /// <summary>
    /// Lets the player open the bag they are standing on.
    /// </summary>
    /// <remarks>
    /// The nearest bag within reach, not every bag in reach. Two overlapping bags
    /// would otherwise both be candidates for one keypress, and the player could
    /// not tell which they were about to open.
    ///
    /// The range here decides only what is <em>offered</em>. Whether an item may
    /// actually move is re-checked in the reducer against where the server thinks
    /// the player is standing — this number existing on the client is a
    /// convenience, not a permission.
    ///
    /// Opens the bag; it does not take anything. Opening is what roots the
    /// player on the server, and being rooted in a room that was a fight a moment
    /// ago is the cost of looting — so the keypress has to be a decision, not a
    /// thing that happens because you walked somewhere.
    /// </remarks>
    public sealed class VroxPickup : MonoBehaviour
    {
        [Tooltip("How close a bag must be to be offered, in tiles. The server checks " +
                 "this again on its own, so raising it here grants nothing.")]
        [Range(0.5f, 4f)]
        public float Range = 2f;

        [Tooltip("Key that opens the nearest bag. Opening roots you until you close it.")]
        public Key OpenKey = Key.F;

        /// <summary>The bag currently in reach, or 0. Read by the prompt.</summary>
        public ulong Offered { get; private set; }

        /// <summary>What that bag holds, for a prompt to describe.</summary>
        public int OfferedCount { get; private set; }

        private void Update()
        {
            Offered = 0;
            OfferedCount = 0;

            var net = VroxNet.Instance;
            if (net?.Conn is not { } conn || net.LocalPlayer is not { } player)
            {
                return;
            }

            // Measured against the server's position, not the transform. The
            // player object is eased toward where the server says it is, so at
            // the edge of the range the two disagree by a fraction of a tile —
            // and the reducer will answer using the server's number.
            float best = Range * Range;
            foreach (var bag in conn.Db.LootDrop.Iter())
            {
                float dx = player.X - bag.X;
                float dy = player.Y - bag.Y;
                float d = dx * dx + dy * dy;
                if (d <= best)
                {
                    best = d;
                    Offered = bag.Id;
                    OfferedCount = bag.Items.Count;
                }
            }

            // Nothing offered while a bag is already open: the screen owns the
            // keyboard then, and re-pressing the open key over a second bag
            // would silently move you from one to another.
            if (player.LootingBag != 0)
            {
                Offered = 0;
                OfferedCount = 0;
                return;
            }

            if (Offered != 0 && Keyboard.current is { } keyboard
                && keyboard[OpenKey].wasPressedThisFrame)
            {
                conn.Reducers.OpenBag(Offered);
            }
        }
    }
}
