using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Converts between a Unity colour and the packed 0xRRGGBB the server stores.
    /// </summary>
    /// <remarks>
    /// One column rather than three float columns, because a colour is one idea
    /// and three that can disagree is a worse row.
    ///
    /// Alpha is deliberately not carried. Everything drawn from a server colour
    /// is opaque, and a transparency the server could set but nothing honoured
    /// would be a field that lies.
    /// </remarks>
    public static class PackedColour
    {
        public static uint Pack(Color colour)
        {
            var c = (Color32)colour;
            return ((uint)c.r << 16) | ((uint)c.g << 8) | c.b;
        }

        public static Color Unpack(uint packed) => new Color32(
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF),
            255);
    }
}
