using System.Collections.Generic;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Builds a realm from a seed, as plain native C#.
    /// </summary>
    /// <remarks>
    /// This used to run inside the database module. It was moved here for one
    /// measured reason: a module is compiled to <c>wasi-wasm</c> and executed by
    /// the Mono interpreter, which cost about 1.9us for a loop iteration doing a
    /// couple of array accesses — roughly a hundred times native. A 1024-tile map
    /// took 22 seconds there, and no algorithmic work was going to close a gap
    /// that size. The same code JIT-compiled in the editor is not slow.
    ///
    /// Nothing about authority changes. The generator only computes a tile array;
    /// the server still owns the rows, and every client reads terrain from those
    /// rows rather than generating its own. Two players cannot disagree about the
    /// map, because neither of them makes it.
    ///
    /// Deliberately free of Unity and SpacetimeDB types. It takes numbers and
    /// returns arrays, so it can be tested and so the pushing code has no
    /// generation logic in it.
    /// </remarks>
    public static class RealmGenerator
    {
        public const byte BiomeWater = 0;
        public const byte BiomeBeach = 1;

        /// <summary>Tuning, mirroring the fields of <see cref="RealmConfigItem"/>.</summary>
        public sealed class Settings
        {
            public int WorldSize = 128;
            public float SeaLevel = 0.26f;
            public float BeachWidth = 0.035f;
            public float IslandFalloff = 3.2f;
            public float LandFrequency = 0.022f;
            public int LandOctaves = 4;
            public int SiteCount = 14;
            public float WarpCoarseAmp = 12f;
            public float WarpCoarseFrequency = 0.035f;
            public float WarpFineAmp = 5f;
            public float WarpFineFrequency = 0.120f;
            public float ObstacleDensity = 0.20f;
            public float ObstacleFrequency = 0.085f;
            public int SmoothingPasses = 3;
            public float MinReachable = 0.80f;

            /// <summary>Biome ids to deal regions from, one entry per share.</summary>
            public byte[] Deck = { 2, 3, 4, 5, 6, 7 };

            /// <summary>Clutter multiplier per biome id.</summary>
            public float[] Clutter = new float[256];

            /// <summary>Spawn weight per biome id.</summary>
            public byte[] Spawn = new byte[256];
        }

        /// <summary>One tile, matching the server's TileData.</summary>
        public struct Tile
        {
            public byte Flags;
            public byte SpawnWeight;
            public byte Hazard;
            public byte Biome;
        }

        /// <summary>A finished realm, or the reason there isn't one.</summary>
        public sealed class Result
        {
            public bool Ok;
            public string Why = "";
            public Tile[] Tiles = System.Array.Empty<Tile>();

            /// <summary>Region index per tile, 255 for none. Places spawners.</summary>
            public byte[] SiteOf = System.Array.Empty<byte>();

            public float SpawnX, SpawnY;
            public int Open, Land, Solid;
            public float Reachable;
            public uint Seed;
            public uint Rejected;
        }

        /// <summary>
        /// Tries seeds until one produces a connected enough realm.
        /// </summary>
        /// <remarks>
        /// Eight attempts, the same as the module allowed. A realm whose open
        /// ground is mostly unreachable pockets is worse than no realm: players
        /// spawn into somewhere they cannot walk out of.
        /// </remarks>
        public static Result Build(Settings cfg, uint seed)
        {
            for (uint attempt = 0; attempt < 8; attempt++)
            {
                uint used = attempt == 0 ? seed : Mix(seed + attempt);
                var result = Compose(cfg, used);
                if (result.Ok)
                {
                    result.Seed = used;
                    result.Rejected = attempt;
                    return result;
                }
            }
            return new Result { Ok = false, Why = $"no usable realm from seed {seed} after 8 attempts" };
        }

        private static Result Compose(Settings cfg, uint seed)
        {
            int span = cfg.WorldSize;
            int n = span * span;
            float half = span / 2f;

            var landField = new Field(seed ^ 0xA1B2C3D4u, cfg.LandFrequency, cfg.LandOctaves, span);
            var warpCoarseX = new Lattice(seed ^ 0x11110000u, cfg.WarpCoarseFrequency, span);
            var warpFineX = new Lattice(seed ^ 0x22220000u, cfg.WarpFineFrequency, span);
            var warpCoarseY = new Lattice(seed ^ 0x33330000u, cfg.WarpCoarseFrequency, span);
            var warpFineY = new Lattice(seed ^ 0x44440000u, cfg.WarpFineFrequency, span);
            var obstacleField = new Field(seed ^ 0xC0FFEEu, cfg.ObstacleFrequency, 3, span);

            // --- 1. Landmass: fbm shaped by a radial falloff, so the realm has a
            // silhouette instead of being a square with scenery on it.
            var water = new bool[n];
            var beach = new bool[n];
            int land = 0;

            for (int y = 0; y < span; y++)
            {
                for (int x = 0; x < span; x++)
                {
                    float e = landField.At(x, y) * 0.5f + 0.5f;

                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float mask = 1f - Mathf.Pow(d < 0f ? 0f : d, cfg.IslandFalloff);
                    if (mask < 0f) mask = 0f;

                    float h = e * mask;
                    int i = y * span + x;
                    if (h < cfg.SeaLevel)
                    {
                        water[i] = true;
                    }
                    else
                    {
                        land++;
                        if (h < cfg.SeaLevel + cfg.BeachWidth)
                        {
                            beach[i] = true;
                        }
                    }
                }
            }

            if (land < n / 5)
            {
                return new Result { Ok = false, Why = $"only {land} land tiles of {n}" };
            }

            // --- 2. Biome sites by Mitchell's best candidate. A uniform roll
            // clumps: one blob and a handful of slivers, which reads as a mistake
            // rather than as a map.
            var rng = new Rng(seed ^ 0x5EED1234u);
            int want = cfg.SiteCount < 1 ? 1 : cfg.SiteCount;
            var siteX = new float[want];
            var siteY = new float[want];
            var siteBiome = new byte[want];

            for (int s = 0; s < want; s++)
            {
                float bestX = 0f, bestY = 0f, bestScore = -1f;
                for (int c = 0; c < 12; c++)
                {
                    int idx = rng.Next(n);
                    // Walk forward to land rather than rejecting, so a mostly-wet
                    // map cannot spin here.
                    for (int guard = 0; guard < n && water[idx]; guard++)
                    {
                        idx = idx + 1 == n ? 0 : idx + 1;
                    }
                    float cx = idx % span + 0.5f;
                    float cy = idx / span + 0.5f;

                    float nearest = float.MaxValue;
                    for (int p = 0; p < s; p++)
                    {
                        float ddx = cx - siteX[p], ddy = cy - siteY[p];
                        float dist = ddx * ddx + ddy * ddy;
                        if (dist < nearest) nearest = dist;
                    }
                    if (nearest > bestScore)
                    {
                        bestScore = nearest;
                        bestX = cx;
                        bestY = cy;
                    }
                }
                siteX[s] = bestX;
                siteY[s] = bestY;
            }

            // --- 3. Deal biomes from a deck. Rolling per site does not guarantee
            // a biome appears at all, and a realm missing half its biomes looks
            // like a bug even when the weights are right.
            var deck = cfg.Deck is { Length: > 0 } ? cfg.Deck : new byte[] { 2, 3, 4, 5, 6, 7 };
            for (int s = 0; s < want; s++)
            {
                siteBiome[s] = deck[s % deck.Length];
            }
            for (int s = want - 1; s > 0; s--)
            {
                int j = rng.Next(s + 1);
                (siteBiome[s], siteBiome[j]) = (siteBiome[j], siteBiome[s]);
            }

            // --- 4. Regions: nearest site, sampled through a two-octave domain
            // warp. Raw Voronoi gives straight polygon edges no natural border
            // has; the warp is what turns them into coastline-shaped ones.
            var biome = new byte[n];
            var siteOf = new byte[n];
            for (int i = 0; i < n; i++) siteOf[i] = 255;

            for (int y = 0; y < span; y++)
            {
                for (int x = 0; x < span; x++)
                {
                    int i = y * span + x;
                    if (water[i]) { biome[i] = BiomeWater; continue; }
                    if (beach[i]) { biome[i] = BiomeBeach; continue; }

                    float wx = x + cfg.WarpCoarseAmp * warpCoarseX.At(x, y)
                                 + cfg.WarpFineAmp * warpFineX.At(x, y);
                    float wy = y + cfg.WarpCoarseAmp * warpCoarseY.At(x, y)
                                 + cfg.WarpFineAmp * warpFineY.At(x, y);

                    int best = 0;
                    float bestDist = float.MaxValue;
                    for (int s = 0; s < want; s++)
                    {
                        float ddx = wx - siteX[s], ddy = wy - siteY[s];
                        float dist = ddx * ddx + ddy * ddy;
                        if (dist < bestDist) { bestDist = dist; best = s; }
                    }
                    biome[i] = siteBiome[best];
                    siteOf[i] = (byte)best;
                }
            }

            // --- 5. Obstacles, cut at a quantile of the field's own values. fbm
            // bunches around the middle and almost never reaches the ends, so a
            // fixed cut at "1 - density" produced eleven obstacles on a map asking
            // for a fifth of the land.
            var noise = new float[n];
            var sample = new List<float>(n / 2);
            for (int i = 0; i < n; i++)
            {
                if (water[i] || beach[i]) continue;
                int x = i % span, y = i / span;
                noise[i] = obstacleField.At(x, y);
                sample.Add(noise[i]);
            }
            sample.Sort();

            var cut = new float[256];
            for (int b = 0; b < cut.Length; b++)
            {
                float density = cfg.ObstacleDensity * cfg.Clutter[b];
                if (density <= 0f || sample.Count == 0)
                {
                    cut[b] = float.MaxValue;
                    continue;
                }
                if (density > 0.9f) density = 0.9f;
                cut[b] = sample[(int)((1f - density) * (sample.Count - 1))];
            }

            var obstacle = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (water[i] || beach[i]) continue;
                obstacle[i] = noise[i] > cut[biome[i]];
            }

            // --- 6. Smooth. A threshold on noise leaves single-tile pillars and
            // holes; both read as speckle, and the pillars snag a body sliding
            // along them.
            var next = new bool[n];
            for (int pass = 0; pass < cfg.SmoothingPasses; pass++)
            {
                for (int y = 0; y < span; y++)
                {
                    for (int x = 0; x < span; x++)
                    {
                        int i = y * span + x;
                        if (water[i] || beach[i]) { next[i] = false; continue; }

                        int count = 0;
                        for (int oy = -1; oy <= 1; oy++)
                        {
                            for (int ox = -1; ox <= 1; ox++)
                            {
                                if (ox == 0 && oy == 0) continue;
                                int nx = x + ox, ny = y + oy;
                                // Off-map counts as solid, so the rule does not
                                // erode the map's own rim.
                                if (nx < 0 || ny < 0 || nx >= span || ny >= span) { count++; continue; }
                                int j = ny * span + nx;
                                if (obstacle[j] || water[j]) count++;
                            }
                        }
                        // Above four fills, below four clears, exactly four keeps.
                        // The undecided band is what makes this stable instead of
                        // eroding every blob a ring per pass.
                        next[i] = count > 4 || (count == 4 && obstacle[i]);
                    }
                }
                (obstacle, next) = (next, obstacle);
            }

            // --- 7. Reachability, 4-connected to match the way Slide resolves X
            // then Y: an 8-connected fill calls a diagonal pinch reachable when no
            // body can get through it.
            var solid = new bool[n];
            int open = 0;
            for (int i = 0; i < n; i++)
            {
                solid[i] = water[i] || obstacle[i];
                if (!solid[i]) open++;
            }
            if (open == 0)
            {
                return new Result { Ok = false, Why = "no open ground at all" };
            }

            var region = new int[n];
            for (int i = 0; i < n; i++) region[i] = -1;

            int bestRegion = -1, bestSize = 0, regions = 0;
            var stack = new Stack<int>();
            for (int start = 0; start < n; start++)
            {
                if (solid[start] || region[start] >= 0) continue;

                int id = regions++;
                int size = 0;
                stack.Push(start);
                region[start] = id;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    size++;
                    int x = i % span, y = i / span;
                    if (x > 0) Visit(i - 1, id);
                    if (x < span - 1) Visit(i + 1, id);
                    if (y > 0) Visit(i - span, id);
                    if (y < span - 1) Visit(i + span, id);
                }
                if (size > bestSize) { bestSize = size; bestRegion = id; }

                void Visit(int j, int rid)
                {
                    if (solid[j] || region[j] >= 0) return;
                    region[j] = rid;
                    stack.Push(j);
                }
            }

            float reachable = (float)bestSize / open;
            if (reachable < cfg.MinReachable)
            {
                return new Result
                {
                    Ok = false,
                    Why = $"largest region is {reachable * 100f:0.0}% of open ground",
                };
            }

            // --- 8. Spawn in the largest region, as near the middle as it gets.
            // Spawning into a sealed pocket is unrecoverable.
            int spawnIdx = -1;
            float spawnDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (region[i] != bestRegion) continue;
                float dx = i % span + 0.5f - half, dy = i / span + 0.5f - half;
                float dist = dx * dx + dy * dy;
                if (dist < spawnDist) { spawnDist = dist; spawnIdx = i; }
            }
            if (spawnIdx < 0)
            {
                return new Result { Ok = false, Why = "largest region is empty" };
            }

            // --- 9. Flatten to tiles.
            var tiles = new Tile[n];
            int obstacles = 0;
            for (int i = 0; i < n; i++)
            {
                byte flags;
                if (obstacle[i]) { flags = 0b11; obstacles++; }   // stops bullets too
                else if (water[i]) { flags = 0b01; }              // wade-proof, shoot across
                else { flags = 0; }

                // Unreachable ground stays visible and stays clear — it is scenery
                // across the water — but nothing may spawn there, or the realm
                // quietly fills with enemies no one can ever fight.
                byte weight = flags == 0 && region[i] == bestRegion ? cfg.Spawn[biome[i]] : (byte)0;

                tiles[i] = new Tile
                {
                    Flags = flags,
                    SpawnWeight = weight,
                    Hazard = 0,
                    Biome = biome[i],
                };
            }

            return new Result
            {
                Ok = true,
                Tiles = tiles,
                SiteOf = siteOf,
                SpawnX = spawnIdx % span + 0.5f,
                SpawnY = spawnIdx / span + 0.5f,
                Open = open,
                Land = land,
                Solid = obstacles,
                Reachable = reachable,
            };
        }

        // ---- Noise ------------------------------------------------------------

        private static uint HashCell(int x, int y, uint seed)
        {
            uint h = seed + 0x9E3779B9u;
            h ^= (uint)x * 0x85EBCA6Bu;
            h ^= (uint)y * 0xC2B2AE35u;
            h ^= h >> 15;
            h *= 0x2545F491u;
            h ^= h >> 13;
            h *= 0x27D4EB2Fu;
            h ^= h >> 16;
            return h;
        }

        private static uint Mix(uint v)
        {
            v ^= v >> 16;
            v *= 0x7FEB352Du;
            v ^= v >> 15;
            v *= 0x846CA68Bu;
            v ^= v >> 16;
            return v;
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        /// <summary>
        /// One octave of gradient noise with its lattice worked out up front.
        /// </summary>
        /// <remarks>
        /// A field has far fewer lattice points than tiles — at the landmass
        /// frequency a 512-tile map's lattice is about twelve by twelve — so
        /// hashing per sample recomputed the same hundred gradients a quarter of a
        /// million times. Output is identical to hashing per sample.
        /// </remarks>
        private sealed class Lattice
        {
            private readonly byte[] _grad;
            private readonly int _stride;
            private readonly float _freq;

            public Lattice(uint seed, float freq, int span)
            {
                _freq = freq;
                _stride = Mathf.Max(2, (int)(span * freq) + 2);
                _grad = new byte[_stride * _stride];
                for (int gy = 0; gy < _stride; gy++)
                {
                    for (int gx = 0; gx < _stride; gx++)
                    {
                        _grad[gy * _stride + gx] = (byte)(HashCell(gx, gy, seed) & 7u);
                    }
                }
            }

            public float At(float tx, float ty)
            {
                float x = tx * _freq;
                float y = ty * _freq;
                int x0 = (int)x;
                int y0 = (int)y;
                float fx = x - x0;
                float fy = y - y0;

                if (x0 < 0) { x0 = 0; fx = 0f; }
                if (y0 < 0) { y0 = 0; fy = 0f; }
                if (x0 > _stride - 2) { x0 = _stride - 2; fx = 1f; }
                if (y0 > _stride - 2) { y0 = _stride - 2; fy = 1f; }

                int i00 = y0 * _stride + x0;
                int i01 = i00 + _stride;

                float n00 = Grad(_grad[i00], fx, fy);
                float n10 = Grad(_grad[i00 + 1], fx - 1f, fy);
                float n01 = Grad(_grad[i01], fx, fy - 1f);
                float n11 = Grad(_grad[i01 + 1], fx - 1f, fy - 1f);

                float u = Fade(fx);
                float v = Fade(fy);
                float a = n00 + u * (n10 - n00);
                float b = n01 + u * (n11 - n01);
                return (a + v * (b - a)) * 1.4f;
            }

            /// <summary>
            /// Eight directions rather than four: with only axis gradients the
            /// field has plus-shaped artefacts and biome borders kink along them.
            /// </summary>
            private static float Grad(byte g, float dx, float dy) => g switch
            {
                0 => dx + dy,
                1 => dx - dy,
                2 => -dx + dy,
                3 => -dx - dy,
                4 => dx,
                5 => -dx,
                6 => dy,
                _ => -dy,
            };
        }

        /// <summary>Summed octaves, each with its own lattice.</summary>
        private sealed class Field
        {
            private readonly Lattice[] _octaves;
            private readonly float[] _amps;
            private readonly float _norm;

            public Field(uint seed, float freq, int octaves, int span)
            {
                int count = Mathf.Max(1, octaves);
                _octaves = new Lattice[count];
                _amps = new float[count];

                float amp = 1f, f = freq, norm = 0f;
                for (int o = 0; o < count; o++)
                {
                    _octaves[o] = new Lattice(seed + (uint)o * 0x9E3779B9u, f, span);
                    _amps[o] = amp;
                    norm += amp;
                    amp *= 0.5f;
                    f *= 2f;
                }
                _norm = norm;
            }

            public float At(float x, float y)
            {
                float sum = 0f;
                for (int o = 0; o < _octaves.Length; o++)
                {
                    sum += _amps[o] * _octaves[o].At(x, y);
                }
                return _norm > 0f ? sum / _norm : 0f;
            }
        }

        /// <summary>xorshift, so a seed reproduces a realm exactly.</summary>
        public struct Rng
        {
            private ulong _state;

            public Rng(uint seed)
            {
                _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed * 0x9E3779B97F4A7C15UL + 1UL;
            }

            public uint NextU32()
            {
                _state ^= _state >> 12;
                _state ^= _state << 25;
                _state ^= _state >> 27;
                return (uint)((_state * 0x2545F4914F6CDD1DUL) >> 32);
            }

            /// <summary>Uniform in [0, max).</summary>
            public int Next(int max) => max <= 0 ? 0 : (int)(NextU32() % (uint)max);

            public double NextDouble() => NextU32() / 4294967296.0;
        }
    }
}
