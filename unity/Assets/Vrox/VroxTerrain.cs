using System.Collections.Generic;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vrox
{
    /// <summary>
    /// Draws the server's terrain.
    /// </summary>
    /// <remarks>
    /// Until this existed, the client could not see server terrain at all: the
    /// rows were subscribed and nothing read them, and what players saw was the
    /// scene's own Tilemap. Collision and visuals were two separate truths that
    /// agreed only because both came from the same editor push. Now the map on
    /// screen *is* the map the server collides against.
    ///
    /// Static geometry, so unlike <see cref="VroxShots"/> and
    /// <see cref="VroxDummies"/> this does not rebuild every frame — it rebuilds
    /// when terrain rows change, which is almost never.
    /// </remarks>
    public sealed class VroxTerrain : MonoBehaviour
    {
        /// <summary>
        /// Behind the entities, in front of the background.
        /// </summary>
        /// <remarks>
        /// Terrain draws opaque and writes depth, so this genuinely decides what
        /// covers what: shots at -1 and enemies at -0.5 are nearer than 0.25 and
        /// survive the depth test, and a background quad must sit further away
        /// than 0.25 or terrain will hide it.
        ///
        /// That is the point of using an opaque material here rather than the
        /// sprite one the entity meshes use. Sprites/Default has ZWrite off, so
        /// with everything in the transparent queue these depths meant nothing
        /// and order came from each mesh's bounds-centre distance to the camera —
        /// which for a mesh spanning the whole map is the middle of the island,
        /// and would have flipped as the player walked about.
        /// </remarks>
        private const float Depth = 0.25f;

        [Tooltip("Material for biomes the server has authored. Leave empty to build one " +
                 "from Vrox/Terrain Flat. Anything that reads vertex colour as the tile's " +
                 "biome colour will work.")]
        public Material? Ground;

        [Tooltip("Material for biomes with no BiomeDef row — the unfinished parts of the " +
                 "map. Leave empty to build one from Vrox/Terrain Checkerboard. This is " +
                 "the placeholder look; authored biomes deliberately do not get it.")]
        public Material? EmptyBiome;

        // Indices are the server's biome ids, in the order Generation.cs deals
        // them: 0 water, 1 beach, then the six land biomes. These are placeholder
        // colours living on the client only until the biome catalogue is pushed
        // like weapons and enemies are — at which point they come off this
        // component, because a colour the client invents is a colour two players
        // can disagree about.
        [Tooltip("Walkable ground, indexed by biome id: " +
                 "0 water, 1 beach, 2 grass, 3 forest, 4 desert, 5 swamp, 6 rock, 7 snow.")]
        public Color[] FloorColours =
        {
            new Color(0.09f, 0.16f, 0.30f),   // 0 water (never drawn as floor)
            new Color(0.76f, 0.70f, 0.48f),   // 1 beach
            new Color(0.28f, 0.45f, 0.24f),   // 2 grass
            new Color(0.17f, 0.32f, 0.20f),   // 3 forest
            new Color(0.72f, 0.62f, 0.38f),   // 4 desert
            new Color(0.24f, 0.30f, 0.22f),   // 5 swamp
            new Color(0.42f, 0.40f, 0.37f),   // 6 rock
            new Color(0.80f, 0.84f, 0.88f),   // 7 snow
        };

        [Tooltip("Solid ground, indexed by biome id. Index 0 is water, which blocks " +
                 "bodies but not bullets.")]
        public Color[] WallColours =
        {
            new Color(0.07f, 0.13f, 0.26f),   // 0 water
            new Color(0.60f, 0.55f, 0.38f),   // 1 beach (unused: beaches are clear)
            new Color(0.36f, 0.38f, 0.34f),   // 2 grass — boulders
            new Color(0.11f, 0.21f, 0.13f),   // 3 forest — trees
            new Color(0.60f, 0.48f, 0.30f),   // 4 desert — sandstone
            new Color(0.18f, 0.22f, 0.17f),   // 5 swamp — deadfall
            new Color(0.29f, 0.28f, 0.27f),   // 6 rock — cliffs
            new Color(0.62f, 0.70f, 0.78f),   // 7 snow — ice
        };

        // Resolved once per rebuild rather than per tile. Run merging compares
        // colours across every tile in a row, so a table lookup per comparison
        // would be tens of thousands of index probes per rebuild.
        private readonly Color[] _floor = new Color[256];
        private readonly Color[] _wall = new Color[256];

        /// <summary>Default shader for authored biomes.</summary>
        private const string GroundShaderName = "Vrox/Terrain Flat";

        /// <summary>Default shader for biomes nobody has authored.</summary>
        private const string EmptyShaderName = "Vrox/Terrain Checkerboard";

        /// <summary>
        /// Which biome ids the server has actually described.
        /// </summary>
        /// <remarks>
        /// Rebuilt with the colour tables, and consulted per run rather than per
        /// tile for the same reason those are: run merging compares neighbours
        /// across a whole row.
        /// </remarks>
        private readonly bool[] _authored = new bool[256];

        private Mesh? _mesh;
        private Material? _material;

        // A second mesh, because the two halves of the map are drawn with
        // different materials and a mesh carries one. Two draw calls for the
        // whole ground.
        private Mesh? _emptyMesh;
        private Material? _emptyMaterial;
        private bool _ownedEmpty;
        private readonly List<Vector3> _emptyVerts = new();
        private readonly List<Color> _emptyColors = new();
        private readonly List<int> _emptyTris = new();

        /// <summary>Whether this built <see cref="_material"/> and must destroy it.</summary>
        /// <remarks>
        /// An assigned material is a project asset. Destroying that on teardown
        /// deletes the user's asset, which does not come back.
        /// </remarks>
        private bool _owned;
        private DbConnection? _bound;
        private bool _dirty;

        private readonly List<Vector3> _verts = new();
        private readonly List<Color> _colors = new();
        private readonly List<int> _tris = new();

        private void Awake()
        {
            _mesh = new Mesh
            {
                name = "Vrox Terrain",
                // 128x128 as one quad per tile is 65,536 vertices against a
                // 16-bit limit of 65,535 — one over. Run merging below usually
                // keeps it far under, but relying on that to stay inside a hard
                // limit is exactly the kind of thing that breaks on one unusual
                // map, silently.
                indexFormat = IndexFormat.UInt32,
            };
            // Deliberately no MarkDynamic: this is static geometry rebuilt on
            // change, not per frame.

            _emptyMesh = new Mesh { name = "Vrox Terrain (empty biomes)", indexFormat = IndexFormat.UInt32 };

            (_material, _owned) = Resolve(Ground, GroundShaderName);
            (_emptyMaterial, _ownedEmpty) = Resolve(EmptyBiome, EmptyShaderName);

            // Ground used to mean "the material for all terrain", and the
            // checkerboard was the thing you put in it. It now means "authored
            // biomes", so a scene carrying the old assignment would paint the
            // finished parts of the map with the placeholder and leave the
            // unfinished parts plain — backwards, and easy to mistake for the
            // change simply not working.
            if (Ground != null && Ground.shader != null && Ground.shader.name == EmptyShaderName)
            {
                Debug.LogWarning($"[vrox] '{name}' has a {EmptyShaderName} material in Ground, "
                               + "which now draws only biomes the server HAS authored. Move it "
                               + "to Empty Biome and leave Ground empty for a flat colour.",
                                 this);
            }
        }

        /// <summary>
        /// Picks an assigned material, or builds one from a shader.
        /// </summary>
        /// <remarks>
        /// Returns whether this owns the result, because an assigned material is
        /// a project asset and destroying that on teardown deletes the user's
        /// asset, which does not come back.
        ///
        /// A missing shader draws nothing and says so. Quietly substituting some
        /// other material would look like a working renderer with bad colours,
        /// which is far worse to chase than an empty screen and an error naming
        /// the shader.
        /// </remarks>
        private (Material?, bool) Resolve(Material? assigned, string shaderName)
        {
            if (assigned != null)
            {
                return (assigned, false);
            }
            if (Shader.Find(shaderName) is { } shader)
            {
                return (new Material(shader), true);
            }
            Debug.LogError($"Vrox: shader \"{shaderName}\" is missing, so part of the terrain "
                         + "cannot draw. Check that it compiled.", this);
            return (null, false);
        }

        private void OnDestroy()
        {
            Unbind();
            if (_mesh != null) Destroy(_mesh);
            if (_emptyMesh != null) Destroy(_emptyMesh);
            if (_owned && _material != null) Destroy(_material);
            if (_ownedEmpty && _emptyMaterial != null) Destroy(_emptyMaterial);
        }

        private void Update()
        {
            // Bound late and re-bound after a reconnect: the connection does not
            // exist at Awake and is replaced when it drops.
            var conn = VroxNet.Instance?.Conn;
            if (conn != _bound)
            {
                Unbind();
                _bound = conn;
                if (conn != null)
                {
                    conn.Db.TerrainChunk.OnInsert += OnChunk;
                    conn.Db.TerrainChunk.OnDelete += OnChunk;
                    conn.Db.TerrainChunk.OnUpdate += OnChunkUpdated;
                    conn.Db.BiomeDef.OnInsert += OnBiome;
                    conn.Db.BiomeDef.OnDelete += OnBiome;
                    conn.Db.BiomeDef.OnUpdate += OnBiomeUpdated;
                    _dirty = true;
                }
            }

            if (_dirty)
            {
                Rebuild();
            }
            Draw();
        }

        private void Unbind()
        {
            if (_bound is not { } conn)
            {
                return;
            }
            conn.Db.TerrainChunk.OnInsert -= OnChunk;
            conn.Db.TerrainChunk.OnDelete -= OnChunk;
            conn.Db.TerrainChunk.OnUpdate -= OnChunkUpdated;
            conn.Db.BiomeDef.OnInsert -= OnBiome;
            conn.Db.BiomeDef.OnDelete -= OnBiome;
            conn.Db.BiomeDef.OnUpdate -= OnBiomeUpdated;
            _bound = null;
        }

        // A whole map arrives as dozens of callbacks in one transaction. They
        // set a flag rather than rebuilding, so the mesh is built once.
        private void OnChunk(EventContext ctx, TerrainChunk row) => _dirty = true;

        private void OnChunkUpdated(EventContext ctx, TerrainChunk oldRow, TerrainChunk newRow) =>
            _dirty = true;

        // Re-colouring a biome redraws the map without regenerating it, so
        // palette edits show up on the next push.
        private void OnBiome(EventContext ctx, BiomeDef row) => _dirty = true;

        private void OnBiomeUpdated(EventContext ctx, BiomeDef oldRow, BiomeDef newRow) =>
            _dirty = true;

        private void Rebuild()
        {
            if (_bound is not { } conn || _mesh == null)
            {
                return;
            }
            _dirty = false;
            ResolvePalette(conn);

            // Flattened first, because chunks arrive in no particular order and
            // run merging needs to walk whole rows.
            int span = 0;
            foreach (var chunk in conn.Db.TerrainChunk.Iter())
            {
                int cx = (int)(chunk.Cell >> 16);
                int cy = (int)(chunk.Cell & 0xFFFF);
                span = Mathf.Max(span, Mathf.Max(cx, cy) + 1);
            }
            if (span == 0)
            {
                _mesh.Clear();
                return;
            }
            span *= ChunkSize;

            var tiles = new TileData[span * span];
            var present = new bool[span * span];

            foreach (var chunk in conn.Db.TerrainChunk.Iter())
            {
                int cx = (int)(chunk.Cell >> 16);
                int cy = (int)(chunk.Cell & 0xFFFF);
                for (int i = 0; i < chunk.Tiles.Count && i < ChunkSize * ChunkSize; i++)
                {
                    int x = cx * ChunkSize + i % ChunkSize;
                    int y = cy * ChunkSize + i / ChunkSize;
                    if (x < span && y < span)
                    {
                        tiles[y * span + x] = chunk.Tiles[i];
                        present[y * span + x] = true;
                    }
                }
            }

            _verts.Clear();
            _colors.Clear();
            _tris.Clear();
            _emptyVerts.Clear();
            _emptyColors.Clear();
            _emptyTris.Clear();

            // Horizontal run merging. A biome map is mostly large flat regions,
            // so merging equal-coloured neighbours across a row turns tens of
            // thousands of quads into a couple of thousand — same single draw
            // call, a fraction of the geometry.
            for (int y = 0; y < span; y++)
            {
                int x = 0;
                while (x < span)
                {
                    if (!present[y * span + x])
                    {
                        x++;
                        continue;
                    }

                    var colour = ColourOf(tiles[y * span + x]);
                    bool authored = _authored[tiles[y * span + x].Biome];
                    int start = x;
                    x++;

                    // Authored-ness ends a run as surely as a colour change does.
                    // The two halves go into different meshes, and an unauthored
                    // tile that happened to share a colour with its authored
                    // neighbour would otherwise be merged into the wrong one and
                    // silently lose its checkerboard.
                    while (x < span && present[y * span + x]
                           && _authored[tiles[y * span + x].Biome] == authored
                           && ColourOf(tiles[y * span + x]) == colour)
                    {
                        x++;
                    }
                    Quad(start, y, x - start, colour, authored);
                }
            }

            _mesh.Clear();
            if (_verts.Count > 0)
            {
                _mesh.SetVertices(_verts);
                _mesh.SetColors(_colors);
                _mesh.SetTriangles(_tris, 0, calculateBounds: true);
            }

            if (_emptyMesh != null)
            {
                _emptyMesh.Clear();
                if (_emptyVerts.Count > 0)
                {
                    _emptyMesh.SetVertices(_emptyVerts);
                    _emptyMesh.SetColors(_emptyColors);
                    _emptyMesh.SetTriangles(_emptyTris, 0, calculateBounds: true);
                }
            }
        }

        private void Draw()
        {
            if (_mesh != null && _material != null && _mesh.vertexCount > 0)
            {
                Graphics.RenderMesh(new RenderParams(_material), _mesh, 0, Matrix4x4.identity);
            }
            if (_emptyMesh != null && _emptyMaterial != null && _emptyMesh.vertexCount > 0)
            {
                Graphics.RenderMesh(
                    new RenderParams(_emptyMaterial), _emptyMesh, 0, Matrix4x4.identity);
            }
        }

        /// <summary>
        /// Fills the colour tables from the server's biome catalogue.
        /// </summary>
        /// <remarks>
        /// The catalogue wins wherever it has a row. The serialized arrays below
        /// are only a fallback for a server with no biomes pushed yet — if the
        /// client kept its own palette as the real one, two players could draw
        /// the same realm in different colours.
        /// </remarks>
        private void ResolvePalette(DbConnection conn)
        {
            for (int i = 0; i < 256; i++)
            {
                _floor[i] = Fallback(FloorColours, i);
                _wall[i] = Fallback(WallColours, i);
                _authored[i] = false;
            }

            foreach (var biome in conn.Db.BiomeDef.Iter())
            {
                _floor[biome.Id] = PackedColour.Unpack(biome.FloorColour);
                _wall[biome.Id] = PackedColour.Unpack(biome.WallColour);

                // What decides which material a tile gets. A biome the server has
                // described is finished ground and is drawn flat in the colour it
                // was given; anything else is drawn as the checkerboard, which is
                // the whole signal that it is unfinished.
                _authored[biome.Id] = true;
            }
        }

        /// <summary>A stand-in colour for a biome the server has not described.</summary>
        /// <remarks>
        /// White with no palette, not magenta. Magenta was the marker back when
        /// every tile used one material and an unauthored biome had no other way
        /// to announce itself. It does now — unauthored ground is drawn with the
        /// checkerboard — and tinting that magenta would stop the placeholder
        /// material from ever looking the way it was authored to look.
        ///
        /// With a palette, the biome id still tints the checks, so two different
        /// unfinished biomes stay distinguishable from each other.
        /// </remarks>
        private static Color Fallback(Color[] palette, int biome)
        {
            if (palette.Length == 0)
            {
                return Color.white;
            }
            // Wrapped rather than clamped, so a biome nobody has authored is
            // visibly wrong in a way that repeats predictably instead of
            // silently reading as the last entry.
            return palette[biome % palette.Length];
        }

        private Color ColourOf(TileData tile) =>
            (tile.Flags & 1) != 0 ? _wall[tile.Biome] : _floor[tile.Biome];

        /// <summary>
        /// Adds one merged run as a single quad, <paramref name="width"/> tiles
        /// wide, into whichever mesh matches the run's biome.
        /// </summary>
        private void Quad(int x, int y, int width, Color colour, bool authored)
        {
            var verts = authored ? _verts : _emptyVerts;
            var colors = authored ? _colors : _emptyColors;
            var tris = authored ? _tris : _emptyTris;

            int v = verts.Count;
            verts.Add(new Vector3(x, y, Depth));
            verts.Add(new Vector3(x, y + 1, Depth));
            verts.Add(new Vector3(x + width, y + 1, Depth));
            verts.Add(new Vector3(x + width, y, Depth));

            for (int i = 0; i < 4; i++)
            {
                colors.Add(colour);
            }

            tris.Add(v);
            tris.Add(v + 1);
            tris.Add(v + 2);
            tris.Add(v);
            tris.Add(v + 2);
            tris.Add(v + 3);
        }

        /// <summary>Must match the server's ChunkSize.</summary>
        private const int ChunkSize = 16;
    }
}
