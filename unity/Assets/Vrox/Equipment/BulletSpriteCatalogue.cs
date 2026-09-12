using System.Collections.Generic;
using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>One bullet's art: where it sits on the shared sheet.</summary>
    /// <remarks>
    /// A rectangle rather than a <c>Sprite</c> reference on purpose. Every shot in
    /// the game is drawn in one mesh with one texture, so the renderer needs a UV
    /// rectangle into that sheet and nothing else — and referencing the 66 source
    /// PNGs here would pull all of them into the build purely to be looked at in
    /// an inspector.
    /// </remarks>
    [System.Serializable]
    public sealed class BulletSprite
    {
        [Tooltip("The source file this came from, for finding it again. Not loaded at runtime.")]
        public string Name = "";

        [Tooltip("Normalised position on the sheet: x, y, width, height in 0..1.")]
        public Rect UvRect;

        [Tooltip("Width over height of the trimmed art. The quad is stretched to this " +
                 "so a long bolt stays long instead of being squashed into a square.")]
        public float Aspect = 1f;
    }

    /// <summary>
    /// The bullet art a running client can draw, and where each piece lives.
    /// </summary>
    /// <remarks>
    /// The same split every other catalogue here uses: the server sends an id and
    /// the client looks the art up. A sprite cannot travel through the database —
    /// it is not data the simulation has any use for, and shipping one would make
    /// a texture change a publish.
    ///
    /// One sheet, not 66 textures, because <c>VroxShots</c> draws every projectile
    /// in a single mesh with a single material. That is not an optimisation to be
    /// traded away: a realm holds hundreds of bullets at once, and a texture per
    /// bullet kind would be a draw call per bullet kind.
    ///
    /// Ids are 1-based. 0 means "no art", and draws the generated soft blob that
    /// every bullet used before this existed — which is the honest default for a
    /// weapon nobody has dressed yet, and what unauthored enemy fire still uses.
    /// </remarks>
    [CreateAssetMenu(menuName = "Vrox/Bullet Sprite Catalogue", fileName = "BulletSpriteCatalogue")]
    public sealed class BulletSpriteCatalogue : ScriptableObject
    {
        [Tooltip("The packed sheet every bullet is drawn from.")]
        public Texture2D? Sheet;

        [Tooltip("Indexed from 1. Order is what the weapon's Bullet Sprite Id refers to, " +
                 "so inserting into the middle renumbers every weapon after it.")]
        public List<BulletSprite> Sprites = new();

        [Tooltip("Where the plain soft blob sits on the sheet. Bullets with no art " +
                 "authored are drawn with this.")]
        public Rect BlobUv;

        /// <summary>The art for a bullet sprite id, or null for 0 and anything unknown.</summary>
        /// <remarks>
        /// Null for an id past the end rather than a clamp to the last entry. A
        /// weapon pointing at art that is not there should draw the plain blob and
        /// be obviously undressed, not silently borrow whatever sits at the end of
        /// the list — that reads as the wrong sprite being authored, which is a
        /// much harder thing to notice.
        /// </remarks>
        public BulletSprite? For(int id)
        {
            int index = id - 1;
            return index >= 0 && index < Sprites.Count ? Sprites[index] : null;
        }

        /// <summary>Highest id this catalogue can answer.</summary>
        public int Count => Sprites.Count;

        /// <summary>
        /// Whether this catalogue can be drawn from at all.
        /// </summary>
        /// <remarks>
        /// The sheet carries the fallback blob as well as the bolts, so a
        /// catalogue without one cannot draw even an undressed bullet. Checked
        /// rather than assumed, because the failure is a screen of untextured
        /// white quads rather than anything that names itself.
        /// </remarks>
        public bool Usable => Sheet != null && BlobUv.width > 0f;
    }
}
