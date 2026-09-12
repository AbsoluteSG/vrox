using System.Collections.Generic;
using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// The drawing primitives every glowing thing in the game is made of.
    /// </summary>
    /// <remarks>
    /// Bullets, muzzle flares and the streaks behind them are all the same job:
    /// build some soft-edged triangles in world space, colour them per vertex,
    /// and submit the lot as one mesh. This is that job, factored out so the
    /// falloff texture has one definition rather than one per component — three
    /// slightly different blobs is how a game ends up looking like it was drawn
    /// by three people.
    ///
    /// <b>Everything is textured, and that is the whole reason it does not look
    /// like programmer art.</b> An untextured quad is a hard-edged square and an
    /// untextured ribbon is a hard-edged smear; both alias, both crawl as they
    /// move, and no amount of tuning fixes either. A generated radial falloff,
    /// sampled two ways, gives a bullet a soft round glow and a streak soft sides
    /// — from one texture on one material, so it is still one draw call.
    ///
    /// Nothing here touches the network or the clock. A caller works out where a
    /// thing is and how bright it should be; this only knows how to draw it.
    /// </remarks>
    public static class VroxGlow
    {
        /// <summary>
        /// The soft round blob most things are drawn with.
        /// </summary>
        /// <remarks>
        /// Generated rather than imported, the same reason <see cref="VroxTracers"/>
        /// builds its own material: a component that draws nothing until somebody
        /// remembers to assign a sprite is a component that will one day ship
        /// drawing nothing. Every caller exposes a texture field to override it.
        ///
        /// The falloff is a solid core with a soft shoulder rather than a straight
        /// linear ramp. A linear ramp has no centre — it reads as a smudge with
        /// nothing burning in the middle of it — and a pure gaussian has no edge.
        /// Squaring the shoulder keeps both.
        ///
        /// White, with the colour coming entirely from vertex colours, because one
        /// texture has to serve every weapon in the catalogue.
        /// </remarks>
        public static Texture2D BuildBlob(int size = 64, float core = 0.25f)
        {
            var tex = NewTexture(size, "Vrox Blob");
            var pixels = new Color32[size * size];
            float centre = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - centre) / centre;
                    float dy = (y - centre) / centre;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    float shoulder = 1f - Mathf.InverseLerp(core, 1f, d);
                    pixels[y * size + x] = Alpha(shoulder * shoulder);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        /// <summary>
        /// A four-pointed star: a hot centre with spikes along both axes.
        /// </summary>
        /// <remarks>
        /// What a muzzle flash is drawn with. A plain blob at the barrel reads as
        /// a glowing dot somebody stuck there; the spikes are what make it read as
        /// a flash, because a real one blows out the camera and a blown-out
        /// highlight streaks.
        ///
        /// The long axis is U, so a caller that stretches the quad along the
        /// firing direction gets the long spike pointing down the barrel and the
        /// short one across it. That asymmetry is deliberate — a symmetric star
        /// gives no clue which way the gun is pointing.
        /// </remarks>
        public static Texture2D BuildFlare(int size = 64)
        {
            var tex = NewTexture(size, "Vrox Flare");
            var pixels = new Color32[size * size];
            float centre = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - centre) / centre;
                    float dy = (y - centre) / centre;

                    // The burning middle. Squared so it has an edge rather than
                    // fading all the way to the corners of the quad.
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float core = Mathf.Clamp01(1f - d);
                    core *= core;

                    // Spikes: narrow across, long along, and faded towards the
                    // tips so they end rather than stopping at the quad edge.
                    float along = Mathf.Clamp01(1f - Mathf.Abs(dy) * 12f)
                                * Mathf.Clamp01(1f - Mathf.Abs(dx));
                    float across = Mathf.Clamp01(1f - Mathf.Abs(dx) * 14f)
                                 * Mathf.Clamp01(1f - Mathf.Abs(dy));

                    pixels[y * size + x] = Alpha(Mathf.Clamp01(core + along * 0.8f + across * 0.45f));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        /// <summary>
        /// A soft-edged band: solid down the middle, falling away to nothing at
        /// the top and bottom, and unchanging left to right.
        /// </summary>
        /// <remarks>
        /// What a <see cref="LineRenderer"/> wants. Its UVs run along the line in
        /// U and across it in V, so a texture that varies only in V gives a line
        /// with soft sides and no gradient along its length — leaving the length
        /// entirely to the colour and width the caller animates.
        ///
        /// The blob cannot do this job: sampled by a line it would fade out at
        /// both ends as well as both edges, and a tracer that dims at the muzzle
        /// is a tracer that looks like it started somewhere else.
        /// </remarks>
        public static Texture2D BuildStreak(int size = 64)
        {
            var tex = NewTexture(size, "Vrox Streak");
            var pixels = new Color32[size * size];
            float centre = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                float dy = Mathf.Abs((y - centre) / centre);

                // Squared, for the same reason the blob's shoulder is: a linear
                // ramp across a line reads as a smudge with no filament in it.
                float a = Mathf.Clamp01(1f - dy);
                a *= a;

                var c = Alpha(a);
                for (int x = 0; x < size; x++)
                {
                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        private static Texture2D NewTexture(int size, string name) =>
            new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true, linear: false)
            {
                name = name,
                // Clamp, or bilinear filtering wraps the far edge back round and
                // leaves a faint seam across the middle of every blob.
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

        private static Color32 Alpha(float a) => new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));

        /// <summary>
        /// One frame's worth of triangles, built up and submitted in a single
        /// draw call.
        /// </summary>
        /// <remarks>
        /// There are no GameObjects behind any of this, and nothing is
        /// instantiated. A GameObject per bullet is the obvious approach and the
        /// wrong one for this genre: pooling removes the allocation churn but not
        /// the rest of the cost — every bullet is still a transform to update, an
        /// object to cull and a renderer to batch. Hundreds of those is real
        /// per-frame work for something that is ultimately a coloured blob.
        ///
        /// The lists are cleared and refilled every frame rather than reallocated,
        /// and the mesh is marked dynamic, so a steady rate of fire settles into
        /// zero garbage.
        /// </remarks>
        public sealed class Batch
        {
            private readonly List<Vector3> _verts = new();
            private readonly List<Color> _colors = new();
            private readonly List<Vector2> _uvs = new();
            private readonly List<int> _tris = new();

            /// <summary>How many vertices are in the batch so far.</summary>
            /// <remarks>
            /// Read before adding a run of vertices, so triangles can be indexed
            /// relative to where that run started. This is what lets a caller
            /// build something the batch has no opinion about — a ribbon, say —
            /// without the batch needing to know what a ribbon is.
            /// </remarks>
            public int VertexCount => _verts.Count;

            public bool Empty => _verts.Count == 0;

            public void Clear()
            {
                _verts.Clear();
                _colors.Clear();
                _uvs.Clear();
                _tris.Clear();
            }

            public void Vertex(Vector2 at, float depth, Vector2 uv, Color colour)
            {
                _verts.Add(new Vector3(at.x, at.y, depth));
                _uvs.Add(uv);
                _colors.Add(colour);
            }

            public void Triangle(int a, int b, int c)
            {
                _tris.Add(a);
                _tris.Add(b);
                _tris.Add(c);
            }

            /// <summary>A soft round blob, laid out along the screen's axes.</summary>
            /// <remarks>
            /// Screen axes rather than world ones, so it does not skew or change
            /// size as the view turns. Where the thing *is* stays world space —
            /// that is the position the server evaluates for collision — and only
            /// the corners are rotated.
            /// </remarks>
            public void Blob(Vector2 at, float half, Vector2 right, Vector2 up, float depth, Color colour)
                => Blob(at, half, right, up, depth, colour, FullTexture);

            /// <summary>The same blob, taken from one region of a sheet.</summary>
            public void Blob(Vector2 at, float half, Vector2 right, Vector2 up, float depth,
                             Color colour, Rect uv)
            {
                Vector2 rw = right * half;
                Vector2 uw = up * half;
                Quad(at - rw - uw, at - rw + uw, at + rw + uw, at + rw - uw, depth, colour, uv);
            }

            /// <summary>
            /// A blob stretched along a direction: longer than it is wide, and
            /// turned to point somewhere.
            /// </summary>
            /// <remarks>
            /// World-oriented, not screen-oriented, because the direction is a
            /// fact about the world — which way the gun is pointing — and has to
            /// turn with it when the camera does.
            ///
            /// <paramref name="anchor"/> says how much of the length sits *behind*
            /// the point: 0.5 centres it, 0 makes the quad start there and run
            /// forward. A symmetric shape wants centring; a directional one — a
            /// flare that is bright at the barrel and fades down the barrel —
            /// wants anchoring, or half of it is drawn out of the back of the gun.
            ///
            /// The texture's U axis runs along <paramref name="along"/>, so a
            /// directional texture has to be authored, or rotated, with its bright
            /// end at u=0.
            /// </remarks>
            public void Oriented(Vector2 at, Vector2 along, float halfLength, float halfWidth,
                                 float depth, Color colour, float anchor = 0.5f)
            {
                if (along.sqrMagnitude < 1e-8f)
                {
                    along = Vector2.right;
                }
                along.Normalize();

                // At anchor 0.5 this is zero and the quad straddles the point; at
                // 0 it pushes the whole length forward of it.
                Vector2 centre = at + along * halfLength * (1f - 2f * Mathf.Clamp01(anchor));

                Vector2 l = along * halfLength;
                Vector2 w = new Vector2(-along.y, along.x) * halfWidth;
                Quad(centre - l - w, centre - l + w, centre + l + w, centre + l - w, depth, colour);
            }

            /// <summary>
            /// A blob drawn from one region of a sheet, turned to point somewhere.
            /// </summary>
            /// <remarks>
            /// The sheet exists because every projectile in the game is drawn in
            /// one mesh with one material, so picking art per bullet has to mean
            /// picking a rectangle rather than swapping a texture.
            ///
            /// Oriented in world space rather than along the screen axes, unlike
            /// <see cref="Blob"/>: a bolt is a picture of something travelling, and
            /// one that does not point where it is going reads as broken
            /// immediately. Symmetric art does not care, which is why the plain
            /// blob can keep using the cheaper screen-aligned path.
            /// </remarks>
            public void Sprite(Vector2 at, Vector2 along, float halfLength, float halfWidth,
                               float depth, Color colour, Rect uv, float anchor = 0.5f)
            {
                if (along.sqrMagnitude < 1e-8f)
                {
                    along = Vector2.right;
                }
                along.Normalize();

                Vector2 centre = at + along * halfLength * (1f - 2f * Mathf.Clamp01(anchor));
                Vector2 l = along * halfLength;
                Vector2 w = new Vector2(-along.y, along.x) * halfWidth;
                Quad(centre - l - w, centre - l + w, centre + l + w, centre + l - w,
                     depth, colour, uv);
            }

            /// <summary>
            /// Four corners, wound anticlockwise from the bottom-left of the
            /// texture. The material's shader has culling off, so the winding only
            /// has to be consistent, not correct for a particular facing.
            /// </summary>
            private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float depth, Color colour)
                => Quad(a, b, c, d, depth, colour, FullTexture);

            /// <summary>The whole texture, for callers that are not drawing from a sheet.</summary>
            private static readonly Rect FullTexture = new Rect(0f, 0f, 1f, 1f);

            private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float depth,
                              Color colour, Rect uv)
            {
                int v = _verts.Count;
                float u0 = uv.xMin, u1 = uv.xMax, v0 = uv.yMin, v1 = uv.yMax;
                Vertex(a, depth, new Vector2(u0, v0), colour);
                Vertex(b, depth, new Vector2(u0, v1), colour);
                Vertex(c, depth, new Vector2(u1, v1), colour);
                Vertex(d, depth, new Vector2(u1, v0), colour);

                Triangle(v, v + 1, v + 2);
                Triangle(v, v + 2, v + 3);
            }

            /// <summary>Hands the frame's triangles to the renderer.</summary>
            /// <remarks>
            /// The vertices are already in world space, so the transform is
            /// identity. Bounds are recalculated because the contents move every
            /// frame and a stale bounding box gets the whole mesh culled the
            /// moment the action leaves it.
            /// </remarks>
            public void Flush(Mesh mesh, Material material)
            {
                mesh.Clear();
                if (_verts.Count == 0)
                {
                    return;
                }

                mesh.SetVertices(_verts);
                mesh.SetColors(_colors);
                mesh.SetUVs(0, _uvs);
                mesh.SetTriangles(_tris, 0, calculateBounds: true);

                Graphics.RenderMesh(new RenderParams(material), mesh, 0, Matrix4x4.identity);
            }
        }
    }
}
