using SpacetimeDB;

/// <summary>
/// Procedural realm generation.
/// </summary>
/// <remarks>
/// The server generates the map and the client draws the rows that come out.
/// That split is the whole point: the seed and every knob below live here, so
/// two clients cannot disagree about what the world looks like, and the ground
/// the server collides against is by construction the ground you can see.
///
/// Nothing in this file may call <c>TileAt</c> or <c>Terrain</c>. Those read a
/// static cache that is only correct between transactions; generation works on
/// local arrays and invalidates once, at the end. Reading the cache halfway
/// through would see a mixture of the old map and the new one.
///
/// Plain C# on purpose — no Unity, no <c>System.Random</c>, no clock. A seed has
/// to reproduce its realm exactly or "regenerate with the same seed" is not a
/// debugging tool.
/// </remarks>
public static partial class Module
{
    // ---------------------------------------------------------------- tables

    /// <summary>What the current realm is. One row, id 1.</summary>
    /// <remarks>
    /// Public so the client can stop hard-coding the world size, and so the
    /// editor can tell a generated realm from a hand-painted one before
    /// helpfully overwriting it.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Realm", Public = true)]
    public partial struct Realm
    {
        [PrimaryKey]
        public byte Id;

        /// <summary>The seed that produced this map. Re-running it reproduces it exactly.</summary>
        public uint Seed;

        /// <summary>World span in tiles, on each axis.</summary>
        public uint Size;

        /// <summary>Tiles per chunk, per axis. Saves the client duplicating the constant.</summary>
        public uint ChunkTiles;

        /// <summary><see cref="ModeAuthored"/> or <see cref="ModeGenerated"/>.</summary>
        public byte Mode;

        /// <summary>How many seeds were rejected before this one was accepted.</summary>
        public uint Rejected;

        /// <summary>Fraction of open ground reachable from the spawn point.</summary>
        public float Reachable;

        /// <summary>Open tiles reachable from the spawn point.</summary>
        public uint OpenTiles;
    }

    /// <summary>Terrain came from a Unity tilemap and the editor may overwrite it.</summary>
    public const byte ModeAuthored = 0;

    /// <summary>Terrain was generated here and the editor must keep its hands off.</summary>
    public const byte ModeGenerated = 1;

    /// <summary>
    /// The generator's tuning, as a single row.
    /// </summary>
    /// <remarks>
    /// A table rather than constants so the numbers can be pushed from a Unity
    /// asset and re-rolled without republishing the module — the same authoring
    /// split already used for weapons, enemies and areas. The asset is the
    /// authoring format; this row is what actually generates.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "RealmConfig", Public = true)]
    public partial struct RealmConfig
    {
        [PrimaryKey]
        public byte Id;

        /// <summary>
        /// World span in tiles, on each axis. Must be a multiple of the chunk size.
        /// </summary>
        /// <remarks>
        /// The grid is square. Independent extents would mean reworking chunk
        /// keys, the terrain cache span and the generator, all of which index by a
        /// single span today.
        /// </remarks>
        public float WorldSize;

        /// <summary>Elevation below which ground is water. Raise it to drown the map.</summary>
        public float SeaLevel;

        /// <summary>Elevation band above the shore that becomes beach.</summary>
        public float BeachWidth;

        /// <summary>
        /// How sharply elevation falls off towards the edges.
        /// </summary>
        /// <remarks>
        /// This is what makes the realm an island rather than a square. Low
        /// values drown everything but the middle; high values push the coast
        /// out until the map is square again with a wet rim.
        /// </remarks>
        public float IslandFalloff;

        /// <summary>Landmass noise frequency, in cycles per tile. Smaller is smoother.</summary>
        public float LandFrequency;

        /// <summary>Octaves of landmass noise. More adds coastline detail, not shape.</summary>
        public byte LandOctaves;

        /// <summary>How many biome regions to seed.</summary>
        public byte SiteCount;

        /// <summary>
        /// Coarse domain warp strength, in tiles. Makes bays and peninsulas.
        /// </summary>
        /// <remarks>
        /// The frequency matters more than the amplitude, and not in the obvious
        /// direction: a warp whose wavelength is much larger than a biome region
        /// displaces the whole region instead of bending its border, so the
        /// edges stay just as straight and only move. Keep the wavelength near
        /// the region size — for 14 regions on a 128 map that is roughly 30
        /// tiles, so a frequency near 0.035.
        /// </remarks>
        public float WarpCoarseAmp;

        public float WarpCoarseFrequency;

        /// <summary>Fine domain warp strength, in tiles. Makes ragged borders.</summary>
        public float WarpFineAmp;

        public float WarpFineFrequency;

        /// <summary>Base share of land that becomes obstacle, before the biome multiplier.</summary>
        public float ObstacleDensity;

        public float ObstacleFrequency;

        /// <summary>Cellular-automaton smoothing passes over the obstacle mask.</summary>
        public byte SmoothingPasses;

        /// <summary>
        /// Least share of open ground that must be reachable from the spawn.
        /// </summary>
        /// <remarks>
        /// A map with a third of itself sealed behind rock is unplayable and
        /// cannot be spotted by looking at the seed, so it is measured and the
        /// seed is thrown away. Rejecting is far less code than carving
        /// corridors, and it is what everyone else does.
        /// </remarks>
        public float MinReachable;
    }

    /// <summary>Tuning, or the defaults if nothing has been pushed.</summary>
    /// <remarks>
    /// Defaults rather than a refusal to generate: a fresh database has no config
    /// until someone presses Play, and a server with no world at all is a worse
    /// failure than one running on numbers I picked.
    /// </remarks>
    private static RealmConfig Tuning(ReducerContext ctx) =>
        ctx.Db.RealmConfig.Id.Find((byte)1) ?? new RealmConfig
        {
            Id = 1,
            SeaLevel = 0.26f,
            BeachWidth = 0.035f,
            IslandFalloff = 3.2f,
            LandFrequency = 0.022f,
            LandOctaves = 4,
            SiteCount = 14,
            WarpCoarseAmp = 12f,
            WarpCoarseFrequency = 0.035f,
            WarpFineAmp = 5f,
            WarpFineFrequency = 0.120f,
            ObstacleDensity = 0.20f,
            ObstacleFrequency = 0.085f,
            SmoothingPasses = 3,
            MinReachable = 0.80f,
        };

    /// <summary>
    /// What a biome is: how it looks, how cluttered it gets, and what lives in it.
    /// </summary>
    /// <remarks>
    /// Unity authors this; the server decides where biomes go. Same split as
    /// weapons and enemies, and it is what lets an existing AreaConfig asset be
    /// attached to a biome rather than to a hand-placed spawner.
    ///
    /// Colours live here rather than on the client renderer because two clients
    /// inventing their own palettes would draw the same realm differently.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "BiomeDef", Public = true)]
    public partial struct BiomeDef
    {
        /// <summary>Tile biome id. 0 is water and 1 is beach; both are terrain, not regions.</summary>
        [PrimaryKey]
        public byte Id;

        public string DisplayName;

        /// <summary>Packed 0xRRGGBB. One column rather than three, because it is one idea.</summary>
        public uint FloorColour;

        public uint WallColour;

        /// <summary>Obstacle density multiplier against the realm's base density.</summary>
        public float Clutter;

        /// <summary>Per-tile spawn weight inside this biome. 0 forbids spawning.</summary>
        public byte SpawnWeight;

        /// <summary>
        /// Copies of this biome in the deck the generator deals regions from.
        /// </summary>
        /// <remarks>
        /// A count, not a weight. Weighted rolling does not guarantee a biome
        /// appears at all, and a realm silently missing half its biomes looks
        /// like a bug even when the weights are right. Dealing from a deck makes
        /// "at least two of these" expressible. 0 means never generated.
        /// </remarks>
        public byte DeckCount;

        /// <summary>The area's enemy mix. Empty means generated spawners skip this biome.</summary>
        public List<AreaEntry> Composition;

        /// <summary>
        /// Enemies alive at once in each region of this biome.
        /// </summary>
        /// <remarks>
        /// Per region, and a region gets exactly one spawner, so this is the
        /// number you actually see. It used to be per *spawner*, with a separate
        /// count of spawners per region multiplying it — an author who wrote 20
        /// got 20 times however many spawners the biome happened to place, and
        /// the number in the asset was never the number in the world.
        /// </remarks>
        public ushort MaxAlive;

        public ushort IntervalMs;

        /// <summary>
        /// Dead columns. Nothing reads either one.
        /// </summary>
        /// <remarks>
        /// A region has exactly one spawner now, sized to the region it covers,
        /// so neither a count nor a radius is the author's to choose. Both are
        /// gone from the asset and from <see cref="UpsertBiome"/>, so nothing can
        /// write anything but zero to them.
        ///
        /// Still here, under their original names, because SpacetimeDB treats
        /// dropping *or renaming* a column as a migration it will not perform
        /// without wiping the database — and the database holds characters.
        /// Three bytes per biome is the cheaper of the two prices. They go on the
        /// next deliberate wipe.
        /// </remarks>
        public byte SpawnersPerRegion;

        public float SpawnerRadius;
    }

    /// <summary>Inserts or replaces one biome. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertBiome(ReducerContext ctx, byte id, string displayName,
                                   uint floorColour, uint wallColour, float clutter,
                                   byte spawnWeight, byte deckCount,
                                   List<AreaEntry> composition, ushort maxAlive,
                                   ushort intervalMs)
    {
        var biome = new BiomeDef
        {
            Id = id,
            DisplayName = displayName,
            FloorColour = floorColour,
            WallColour = wallColour,
            Clutter = clutter < 0f ? 0f : clutter,
            SpawnWeight = spawnWeight,
            DeckCount = deckCount,
            Composition = composition,
            MaxAlive = Math.Clamp(maxAlive, (ushort)0, (ushort)200),
            IntervalMs = (ushort)Math.Clamp((int)intervalMs, 100, 60000),
        };

        if (ctx.Db.BiomeDef.Id.Find(id) is null)
        {
            ctx.Db.BiomeDef.Insert(biome);
        }
        else
        {
            ctx.Db.BiomeDef.Id.Update(biome);
        }
    }

    /// <summary>Empties the biome catalogue, so a removed asset stops generating.</summary>
    [SpacetimeDB.Reducer]
    public static void ClearBiomes(ReducerContext ctx)
    {
        foreach (byte id in ctx.Db.BiomeDef.Iter().Select(b => b.Id).ToList())
        {
            ctx.Db.BiomeDef.Id.Delete(id);
        }
    }

    // ---------------------------------------------------------------- biomes

    /// <summary>Water. Blocks bodies, not bullets.</summary>
    private const byte BiomeWater = 0;

    /// <summary>The shore. A real tile type rather than a blend, because tiles do not interpolate.</summary>
    private const byte BiomeBeach = 1;

    /// <summary>The biomes a region may be dealt when no catalogue has been pushed.</summary>
    /// <remarks>
    /// Fallbacks, not the real thing. A fresh database has no biome assets until
    /// somebody presses Play, and a server that generates a blank world until
    /// then is a worse failure than one generating on numbers I picked. Once
    /// biomes are pushed these are unused.
    /// </remarks>
    private static readonly byte[] FallbackDeck = { 2, 3, 4, 5, 6, 7 };

    private static readonly float[] FallbackClutter = { 0f, 0f, 0.7f, 1.9f, 0.35f, 1.0f, 2.3f, 0.8f };

    private static readonly byte[] FallbackSpawn = { 0, 1, 3, 4, 2, 4, 3, 2 };

    /// <summary>Everything generation needs to know about biomes, flattened by id.</summary>
    /// <remarks>
    /// Read once, up front. <see cref="Compose"/> must stay free of database
    /// access so it can be re-run eight times over rejected seeds without
    /// touching a table per tile.
    /// </remarks>
    private sealed class Palette
    {
        public byte[] Deck = FallbackDeck;
        public readonly float[] Clutter = new float[256];
        public readonly byte[] Spawn = new byte[256];
        public bool FromCatalogue;
    }

    private static Palette Biomes(ReducerContext ctx)
    {
        var palette = new Palette();
        var deck = new List<byte>();

        foreach (var biome in ctx.Db.BiomeDef.Iter())
        {
            palette.FromCatalogue = true;
            palette.Clutter[biome.Id] = biome.Clutter;
            palette.Spawn[biome.Id] = biome.SpawnWeight;
            for (int i = 0; i < biome.DeckCount; i++)
            {
                deck.Add(biome.Id);
            }
        }

        if (!palette.FromCatalogue)
        {
            for (int i = 0; i < FallbackClutter.Length; i++) palette.Clutter[i] = FallbackClutter[i];
            for (int i = 0; i < FallbackSpawn.Length; i++) palette.Spawn[i] = FallbackSpawn[i];
            return palette;
        }

        if (deck.Count == 0)
        {
            // A catalogue exists but nothing in it is generatable. Said out loud,
            // because the alternative is a realm of one biome and no explanation.
            Log.Warn("every biome has DeckCount 0, so regions fall back to the built-in set");
            return palette;
        }

        deck.Sort();   // ids arrive in table order, which is not stable
        palette.Deck = deck.ToArray();
        return palette;
    }

    // -------------------------------------------------------------- reducers

    /// <summary>Generates the realm from a given seed. The same seed always gives the same map.</summary>
    [SpacetimeDB.Reducer]
    public static void GenerateRealm(ReducerContext ctx, uint seed) => BuildRealm(ctx, seed);

    /// <summary>
    /// Runs generation but writes no terrain. For finding out where the time
    /// goes, since a module cannot time itself.
    /// </summary>
    /// <remarks>
    /// The difference between this and <c>GenerateRealm</c> is the cost of
    /// turning a finished tile array into chunk rows. That split matters because
    /// the two halves need opposite fixes — cheaper noise on one side, fewer or
    /// smaller rows on the other — and guessing which to attack wasted an
    /// optimisation pass already.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void BenchGenerate(ReducerContext ctx, uint seed)
    {
        var cfg = Tuning(ctx);
        var palette = Biomes(ctx);
        int span = (int)WorldSize;
        var attempt = Compose(cfg, palette, seed, span);
        Log.Info($"bench generate {span}x{span}: {(attempt.Ok ? $"{attempt.Land} land, not written" : attempt.Why)}");
    }

    /// <summary>Generates a fresh realm from an unpredictable seed.</summary>
    [SpacetimeDB.Reducer]
    public static void RerollRealm(ReducerContext ctx)
    {
        long us = ctx.Timestamp.MicrosecondsSinceUnixEpoch;
        BuildRealm(ctx, Mix((uint)us ^ (uint)(us >> 32)));
    }

    /// <summary>Pushes the generator's tuning. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertRealmConfig(
        ReducerContext ctx,
        float seaLevel, float beachWidth, float islandFalloff,
        float landFrequency, byte landOctaves, byte siteCount,
        float warpCoarseAmp, float warpCoarseFrequency,
        float warpFineAmp, float warpFineFrequency,
        float obstacleDensity, float obstacleFrequency,
        byte smoothingPasses, float minReachable, float worldSize)
    {
        // Refused, not rounded. A size that is not a whole number of chunks
        // leaves tiles past the end of the terrain array — they exist, read as
        // solid, and nothing can ever stand on them. Rounding to the nearest
        // chunk would silently hand back a different world than the one authored.
        int rounded = (int)MathF.Round(worldSize);
        if (rounded <= 0 || rounded % ChunkSize != 0)
        {
            throw new Exception(
                $"world size {worldSize} must be a positive multiple of {ChunkSize} "
                + $"— try {Math.Max(ChunkSize, rounded / ChunkSize * ChunkSize)}");
        }

        var cfg = new RealmConfig
        {
            Id = 1,
            SeaLevel = seaLevel,
            BeachWidth = beachWidth,
            IslandFalloff = islandFalloff,
            LandFrequency = landFrequency,
            LandOctaves = landOctaves < 1 ? (byte)1 : landOctaves,
            SiteCount = siteCount < 2 ? (byte)2 : siteCount,
            WarpCoarseAmp = warpCoarseAmp,
            WarpCoarseFrequency = warpCoarseFrequency,
            WarpFineAmp = warpFineAmp,
            WarpFineFrequency = warpFineFrequency,
            ObstacleDensity = obstacleDensity,
            ObstacleFrequency = obstacleFrequency,
            SmoothingPasses = smoothingPasses,
            MinReachable = minReachable,
            WorldSize = rounded,
        };

        if (ctx.Db.RealmConfig.Id.Find((byte)1) is null)
        {
            ctx.Db.RealmConfig.Insert(cfg);
        }
        else
        {
            ctx.Db.RealmConfig.Id.Update(cfg);
        }
        // Not applied here. A config change describes the *next* realm, and
        // adopting it now would move the world's edge out from under the terrain
        // and the spawners that are standing on it. BuildRealm adopts it when it
        // actually builds.
        Log.Info($"realm config updated: world {rounded}x{rounded} tiles, "
               + "which applies on the next generate");
    }

    /// <summary>
    /// Hands the world back to the Unity tilemap.
    /// </summary>
    /// <remarks>
    /// The editor pushes terrain on every connect, so without a mode flag a
    /// generated realm would be wiped by the first person to press Play with a
    /// tilemap in their scene. This is the deliberate way back.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void UseAuthoredTerrain(ReducerContext ctx)
    {
        WriteRealmMode(ctx, ModeAuthored);
        Log.Info("terrain is authored again; editor pushes will apply");
    }

    // -------------------------------------------------------------- the work

    /// <summary>Tries seeds until one produces a connected, walkable realm.</summary>
    private static void BuildRealm(ReducerContext ctx, uint seed)
    {
        var cfg = Tuning(ctx);
        var palette = Biomes(ctx);

        // Generation is the one caller that wants the *configured* size rather
        // than the size of the realm standing here — building a new world is
        // exactly the moment the author's number takes effect. Everywhere else
        // reads the realm row, because that is the ground that actually exists.
        // Conflating the two put ten of twelve spawners outside the map.
        AdoptConfiguredWorldSize(ctx);
        int span = (int)WorldSize;

        Attempt? accepted = null;
        uint used = seed;
        uint rejected = 0;

        // Bounded, because an impossible configuration (sea level above every
        // peak, say) would otherwise loop until the reducer is killed with no
        // clue as to why.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            used = attempt == 0 ? seed : Mix(seed + (uint)attempt);
            var candidate = Compose(cfg, palette, used, span);
            if (candidate.Ok)
            {
                accepted = candidate;
                break;
            }
            rejected++;
            Log.Warn($"realm seed {used} rejected: {candidate.Why}");
        }

        if (accepted is not { } realm)
        {
            // Loudly, and without writing anything. Half-generating would leave
            // a map that is worse than the one already there.
            Log.Error($"no usable realm from seed {seed} after 8 attempts; terrain unchanged");
            return;
        }

        WriteChunks(ctx, realm.Tiles, span);
        InvalidateTerrain();

        // After the invalidate, so the rescue below reads the new map rather
        // than the one it just replaced.
        SetSpawn(ctx, realm.SpawnX, realm.SpawnY);

        int spawners = PlaceSpawners(ctx, realm, used);

        var row = new Realm
        {
            Id = 1,
            Seed = used,
            Size = (uint)span,
            ChunkTiles = ChunkSize,
            Mode = ModeGenerated,
            Rejected = rejected,
            Reachable = realm.Reachable,
            OpenTiles = (uint)realm.Open,
        };
        if (ctx.Db.Realm.Id.Find((byte)1) is null)
        {
            ctx.Db.Realm.Insert(row);
        }
        else
        {
            ctx.Db.Realm.Id.Update(row);
        }

        Log.Info($"realm {used}: {span}x{span}, {realm.Land} land ({realm.Land * 100 / (span * span)}%), "
               + $"{realm.Solid} obstacles, {realm.Open} open, {realm.Reachable * 100f:0.0}% reachable, "
               + $"spawn ({realm.SpawnX:0.0}, {realm.SpawnY:0.0}), {spawners} spawner(s), "
               + $"{rejected} seed(s) rejected");
    }

    private readonly struct Attempt
    {
        public readonly bool Ok;
        public readonly string Why;
        public readonly TileData[] Tiles;
        public readonly float SpawnX, SpawnY;
        public readonly int Open, Land, Solid;
        public readonly float Reachable;

        /// <summary>Region index per tile, 255 for none. Used to place spawners.</summary>
        public readonly byte[] SiteOf;

        public Attempt(string why) : this()
        {
            Ok = false;
            Why = why;
            Tiles = System.Array.Empty<TileData>();
            SiteOf = System.Array.Empty<byte>();
        }

        public Attempt(TileData[] tiles, byte[] siteOf, float sx, float sy, int open, int land,
                       int solid, float reachable)
        {
            Ok = true;
            Why = "";
            Tiles = tiles;
            SiteOf = siteOf;
            SpawnX = sx;
            SpawnY = sy;
            Open = open;
            Land = land;
            Solid = solid;
            Reachable = reachable;
        }
    }

    /// <summary>Builds one candidate realm. Pure: no database, no cache, no clock.</summary>
    private static Attempt Compose(RealmConfig cfg, Palette palette, uint seed, int span)
    {
        int n = span * span;

        // Built once for the whole map instead of hashed per sample. See Lattice
        // for the measurement that motivated this.
        // Strides chosen against each field's own period, not uniformly. The
        // coarse warp repeats about every 28 tiles and the fine warp about every
        // 8, so they tolerate very different sampling.
        var landField = new Coarse(
            new Field(seed ^ 0xA1B2C3D4u, cfg.LandFrequency, cfg.LandOctaves, span), span, 2);
        var warpCoarseX = new Coarse(
            new Field(seed ^ 0x11110000u, cfg.WarpCoarseFrequency, 1, span), span, 4);
        var warpFineX = new Coarse(
            new Field(seed ^ 0x22220000u, cfg.WarpFineFrequency, 1, span), span, 2);
        var warpCoarseY = new Coarse(
            new Field(seed ^ 0x33330000u, cfg.WarpCoarseFrequency, 1, span), span, 4);
        var warpFineY = new Coarse(
            new Field(seed ^ 0x44440000u, cfg.WarpFineFrequency, 1, span), span, 2);

        // Clutter keeps a tighter stride: its top octave repeats every three
        // tiles, and the three smoothing passes below already remove detail at
        // that scale. Coarser than this and clutter stops being speckle.
        var obstacleField = new Coarse(
            new Field(seed ^ 0xC0FFEEu, cfg.ObstacleFrequency, 3, span), span, 2);

        // --- 1. Landmass. fbm shaped by a radial falloff, so the realm has a
        // silhouette instead of being a square with scenery on it.
        var water = new bool[n];
        var beach = new bool[n];
        int land = 0;
        float half = span / 2f;

        for (int y = 0; y < span; y++)
        {
            for (int x = 0; x < span; x++)
            {
                float e = landField.At(x, y) * 0.5f + 0.5f;

                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float mask = 1f - MathF.Pow(d < 0f ? 0f : d, cfg.IslandFalloff);
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
            return new Attempt($"only {land} land tiles of {n}");
        }

        // --- 2. Biome sites, placed on land by Mitchell's best candidate. A
        // plain uniform roll clumps: you get one blob and a handful of slivers,
        // which reads as a mistake rather than as a map.
        var rng = new Rng(seed ^ 0x5EED1234u);
        int want = cfg.SiteCount;
        var siteX = new float[want];
        var siteY = new float[want];
        var siteBiome = new byte[want];

        for (int s = 0; s < want; s++)
        {
            float bestX = 0f, bestY = 0f, bestScore = -1f;
            for (int c = 0; c < 12; c++)
            {
                int idx = rng.Next(n);
                // Walk forward to the next land tile rather than rejecting, so a
                // mostly-wet map cannot spin here.
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

        // --- 3. Deal biomes from a deck rather than rolling per site. Rolling
        // does not guarantee a biome appears at all, and a realm missing half
        // its biomes looks like a bug even when the weights are correct.
        for (int s = 0; s < want; s++)
        {
            // Dealt round-robin before shuffling, so every biome in the deck is
            // used before any is repeated. Rolling independently per site is
            // what leaves a realm missing biomes entirely.
            siteBiome[s] = palette.Deck[s % palette.Deck.Length];
        }
        for (int s = want - 1; s > 0; s--)
        {
            int j = rng.Next(s + 1);
            (siteBiome[s], siteBiome[j]) = (siteBiome[j], siteBiome[s]);
        }

        // --- 4. Biome regions: nearest site, but sampled through a two-octave
        // domain warp. Raw Voronoi gives straight polygon edges that no natural
        // border has; the warp is what turns them into coastline-shaped ones.
        var biome = new byte[n];

        // Which region owns each tile, so generated spawners can be placed one
        // region at a time. 255 means "no region": water and shoreline.
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

        // --- 5. Obstacles, thresholded per biome so clutter is a property of
        // the region rather than uniform noise laid over the whole map.
        //
        // The cut is taken at a quantile of the field's own values rather than
        // at a fixed level. fbm is a sum of octaves divided by their amplitudes,
        // so its values bunch tightly around the middle and almost never reach
        // the ends: a fixed cut at "1 - density" produced eleven obstacle tiles
        // on a map asking for a fifth of the land. Against the sorted values,
        // ObstacleDensity means the share of land it says it means.
        var noise = new float[n];
        var sample = new List<float>();
        for (int y = 0; y < span; y++)
        {
            for (int x = 0; x < span; x++)
            {
                int i = y * span + x;
                if (water[i] || beach[i]) continue;
                noise[i] = obstacleField.At(x, y);
                sample.Add(noise[i]);
            }
        }
        sample.Sort();

        var cut = new float[256];
        for (int b = 0; b < cut.Length; b++)
        {
            float density = cfg.ObstacleDensity * palette.Clutter[b];
            if (density <= 0f || sample.Count == 0)
            {
                cut[b] = float.MaxValue;   // never solid
                continue;
            }
            if (density > 0.9f) density = 0.9f;
            int at = (int)((1f - density) * (sample.Count - 1));
            cut[b] = sample[at];
        }

        var obstacle = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (water[i] || beach[i]) continue;
            obstacle[i] = noise[i] > cut[biome[i]];
        }

        // --- 6. Smooth. A threshold on noise leaves single-tile pillars and
        // single-tile holes; both read as speckle rather than terrain, and the
        // pillars snag a body sliding along them.
        for (int pass = 0; pass < cfg.SmoothingPasses; pass++)
        {
            var next = new bool[n];
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
            obstacle = next;
        }

        // --- 7. Reachability. 4-connected, matching the way Slide resolves X
        // then Y: an 8-connected fill calls a diagonal pinch reachable when no
        // body can actually get through it.
        //
        // "Island", not "region". These are connected components of walkable
        // ground — how many separate landmasses the map came out as — and they
        // have nothing to do with the biome regions dealt in step 4, which is
        // what "region" means everywhere else in this file and in the biome
        // assets. The two were both called region, one of them was always the
        // wrong one to be reading, and telling them apart cost more than the
        // rename does.
        var solid = new bool[n];
        int open = 0;
        for (int i = 0; i < n; i++)
        {
            solid[i] = water[i] || obstacle[i];
            if (!solid[i]) open++;
        }
        if (open == 0)
        {
            return new Attempt("no open ground at all");
        }

        var island = new int[n];
        for (int i = 0; i < n; i++) island[i] = -1;

        int bestIsland = -1, bestSize = 0, islands = 0;
        var stack = new Stack<int>();
        for (int start = 0; start < n; start++)
        {
            if (solid[start] || island[start] >= 0) continue;

            int id = islands++;
            int size = 0;
            stack.Push(start);
            island[start] = id;
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
            if (size > bestSize) { bestSize = size; bestIsland = id; }

            void Visit(int j, int rid)
            {
                if (solid[j] || island[j] >= 0) return;
                island[j] = rid;
                stack.Push(j);
            }
        }

        float reachable = (float)bestSize / open;
        if (reachable < cfg.MinReachable)
        {
            return new Attempt($"largest island is {reachable * 100f:0.0}% of open ground");
        }

        // --- 8. Spawn in the largest island, as near the middle as that island
        // gets. Spawning into a sealed pocket is unrecoverable: the player
        // cannot walk out of it in any direction.
        int spawnIdx = -1;
        float spawnDist = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (island[i] != bestIsland) continue;
            float dx = i % span + 0.5f - half, dy = i / span + 0.5f - half;
            float dist = dx * dx + dy * dy;
            if (dist < spawnDist) { spawnDist = dist; spawnIdx = i; }
        }
        if (spawnIdx < 0)
        {
            return new Attempt("largest island is empty");
        }

        // --- 9. Flatten to tiles.
        var tiles = new TileData[n];
        for (int i = 0; i < n; i++)
        {
            byte flags;
            if (obstacle[i])
            {
                flags = 0b11;           // rock and trees stop bullets too
            }
            else if (water[i])
            {
                flags = 0b01;           // wade-proof, but you can shoot across it
            }
            else
            {
                flags = 0;
            }

            // Unreachable ground is left visible and left solid-free — it is
            // scenery across the water — but nothing may spawn there, or the
            // realm quietly fills with enemies no one can ever fight.
            byte weight = 0;
            if (flags == 0 && island[i] == bestIsland)
            {
                weight = palette.Spawn[biome[i]];
            }

            tiles[i] = new TileData
            {
                Flags = flags,
                SpawnWeight = weight,
                Hazard = 0,
                Biome = biome[i],
            };
        }

        int obstacles = 0;
        for (int i = 0; i < n; i++)
        {
            if (obstacle[i]) obstacles++;
        }

        return new Attempt(tiles, siteOf, spawnIdx % span + 0.5f, spawnIdx / span + 0.5f,
                           open, land, obstacles, reachable);
    }

    /// <summary>
    /// Generated spawner ids start here, clear of the editor's.
    /// </summary>
    /// <remarks>
    /// The editor numbers its spawners from 1 by scene order, so the two sets
    /// would collide at low ids and each would silently overwrite the other's
    /// rows on push.
    /// </remarks>
    private const ushort GeneratedSpawnerBase = 10000;

    /// <summary>
    /// Puts one spawner in each region, using that biome's authored enemy mix.
    /// </summary>
    /// <remarks>
    /// This is what connects generation to the AreaConfig assets: a biome names
    /// an area, and every region of that biome is populated from it. Without it
    /// a generated realm is scenery.
    ///
    /// Exactly one spawner per region, by design and not by configuration. A
    /// region is the unit the author can actually see on the map, so it is the
    /// unit the population rules are written against: a biome's Max Alive is the
    /// number of enemies standing in one of its regions, full stop. The previous
    /// model — a per-spawner cap times a per-biome spawner count — made the
    /// authored number a factor of the real one rather than the real one, and
    /// left an author guessing at how many circles their number was about to be
    /// multiplied by.
    ///
    /// The cost is that a spawner must now cover a region rather than sit in one,
    /// which is why it carries a biome and a radius derived from the ground it
    /// owns instead of a radius somebody typed.
    /// </remarks>
    private static int PlaceSpawners(ReducerContext ctx, Attempt realm, uint seed)
    {
        // The generator owns its own spawners and replaces them wholesale: the
        // old ones point at ground that no longer exists.
        DropSpawners(ctx, SourceGenerated);

        var defs = new Dictionary<byte, BiomeDef>();
        foreach (var biome in ctx.Db.BiomeDef.Iter())
        {
            defs[biome.Id] = biome;
        }
        if (defs.Count == 0)
        {
            return 0;
        }

        // Indexed by region rather than collected into a dictionary, because
        // dictionary order is not part of the contract and this has to produce
        // the same realm every time the seed does.
        var byRegion = new List<int>?[256];
        for (int i = 0; i < realm.Tiles.Length; i++)
        {
            byte site = realm.SiteOf[i];
            if (site == 255 || realm.Tiles[i].SpawnWeight == 0)
            {
                continue;
            }
            (byRegion[site] ??= new List<int>()).Add(i);
        }

        int span = (int)WorldSize;
        ushort id = GeneratedSpawnerBase;
        int placed = 0;
        int mute = 0;

        for (int site = 0; site < byRegion.Length; site++)
        {
            if (byRegion[site] is not { Count: > 0 } candidates)
            {
                continue;
            }

            byte biomeId = realm.Tiles[candidates[0]].Biome;
            if (!defs.TryGetValue(biomeId, out var def) || def.Composition.Count == 0)
            {
                continue;
            }
            if (def.MaxAlive == 0)
            {
                // A spawner that holds a population of zero never spawns. Counted
                // and reported, because it looks exactly like a broken generator.
                mute++;
                continue;
            }

            var (cx, cy, radius) = CoverRegion(candidates, span);

            ctx.Db.Spawner.Insert(new Spawner
            {
                Id = id++,
                Composition = def.Composition,
                X = cx,
                Y = cy,
                Radius = radius,
                MaxAlive = def.MaxAlive,
                IntervalMs = def.IntervalMs,
                NextSpawnAt = ctx.Timestamp,
                Source = SourceGenerated,
                Biome = biomeId,
                ZoneId = RealmZone,
            });
            placed++;
        }

        if (mute > 0)
        {
            Log.Warn($"{mute} biome region(s) have an enemy mix but Max Alive 0, so they "
                   + "were left empty");
        }
        return placed;
    }

    /// <summary>
    /// A centre and radius that cover a region's spawnable ground.
    /// </summary>
    /// <remarks>
    /// The centre is the region's own tile nearest its mean position, not the
    /// mean itself. A region is a noise-warped Voronoi cell and can be bent
    /// enough that its average lies in a neighbour, or in a wall; picking the
    /// nearest real candidate keeps the centre on ground this region actually
    /// owns, which is what the rejection sampling at spawn time is measured from.
    ///
    /// The radius is that of a circle with the region's area, widened by a
    /// quarter. Area rather than the furthest tile, because one thin tendril
    /// reaching into a neighbour would otherwise set the radius for the whole
    /// region and every sample would land outside it. Widened, because a circle
    /// of exactly equal area covers only the middle of a non-circular shape and
    /// the edges of the region would never see an enemy.
    ///
    /// Overshoot is safe and undershoot is not: a sample outside the region is
    /// rejected by the biome and spawn-weight checks and costs one retry, while a
    /// radius too small silently confines a region's whole population to its
    /// middle — which is the shape of the bug this replaces.
    /// </remarks>
    private static (float x, float y, float radius) CoverRegion(List<int> candidates, int span)
    {
        double sumX = 0, sumY = 0;
        foreach (int tile in candidates)
        {
            sumX += tile % span;
            sumY += tile / span;
        }
        double meanX = sumX / candidates.Count;
        double meanY = sumY / candidates.Count;

        int nearest = candidates[0];
        double best = double.MaxValue;
        foreach (int tile in candidates)
        {
            double dx = tile % span - meanX;
            double dy = tile / span - meanY;
            double d = dx * dx + dy * dy;
            if (d < best)
            {
                best = d;
                nearest = tile;
            }
        }

        float radius = (float)(Math.Sqrt(candidates.Count / Math.PI) * 1.25);
        return (nearest % span + 0.5f, nearest / span + 0.5f, radius);
    }

    /// <summary>Writes the whole map as chunk rows. Caller invalidates once, afterwards.</summary>
    private static void WriteChunks(ReducerContext ctx, TileData[] tiles, int span)
    {
        int chunks = span / ChunkSize;
        for (int cy = 0; cy < chunks; cy++)
        {
            for (int cx = 0; cx < chunks; cx++)
            {
                var list = new List<TileData>(ChunkSize * ChunkSize);
                for (int ty = 0; ty < ChunkSize; ty++)
                {
                    for (int tx = 0; tx < ChunkSize; tx++)
                    {
                        int x = cx * ChunkSize + tx;
                        int y = cy * ChunkSize + ty;
                        list.Add(tiles[y * span + x]);
                    }
                }
                WriteChunkRow(ctx, (uint)((cx << 16) | cy), list);
            }
        }
    }

    /// <summary>Records whether terrain is generated or authored, creating the row if needed.</summary>
    private static void WriteRealmMode(ReducerContext ctx, byte mode)
    {
        if (ctx.Db.Realm.Id.Find((byte)1) is { } existing)
        {
            existing.Mode = mode;
            ctx.Db.Realm.Id.Update(existing);
            return;
        }
        ctx.Db.Realm.Insert(new Realm
        {
            Id = 1,
            Seed = 0,
            Size = (uint)WorldSize,
            ChunkTiles = ChunkSize,
            Mode = mode,
            Rejected = 0,
            Reachable = 0f,
            OpenTiles = 0,
        });
    }

    /// <summary>True when the editor must not overwrite terrain.</summary>
    private static bool TerrainIsGenerated(ReducerContext ctx) =>
        ctx.Db.Realm.Id.Find((byte)1) is { } realm && realm.Mode == ModeGenerated;

    // ------------------------------------------------------------ noise, rng

    /// <summary>
    /// xorshift64*, seeded explicitly.
    /// </summary>
    /// <remarks>
    /// Not <c>System.Random</c>: its sequence is an implementation detail that
    /// has changed between .NET versions, so a seed would stop reproducing its
    /// realm on an upgrade — silently, and only for maps generated before it.
    /// </remarks>
    private struct Rng
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
    }

    /// <summary>Avalanches a seed so nearby seeds give unrelated maps.</summary>
    private static uint Mix(uint v)
    {
        v ^= v >> 16;
        v *= 0x7FEB352Du;
        v ^= v >> 15;
        v *= 0x846CA68Bu;
        v ^= v >> 16;
        return v;
    }

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

    /// <summary>Dot product of a lattice point's gradient with the offset to the sample.</summary>
    private static float GradDot(int ix, int iy, float dx, float dy, uint seed)
    {
        // Eight directions rather than four: with only axis gradients the field
        // has visible plus-shaped artefacts, and biome borders kink along them.
        switch (HashCell(ix, iy, seed) & 7u)
        {
            case 0: return dx + dy;
            case 1: return dx - dy;
            case 2: return -dx + dy;
            case 3: return -dx - dy;
            case 4: return dx;
            case 5: return -dx;
            case 6: return dy;
            default: return -dy;
        }
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    /// <summary>
    /// One octave of gradient noise, with its lattice worked out up front.
    /// </summary>
    /// <remarks>
    /// The point of this class is that a noise field has far fewer lattice
    /// points than tiles. At the landmass frequency a 512-tile map has a lattice
    /// of about twelve by twelve — and the per-tile version hashed those same
    /// hundred-odd gradients a quarter of a million times.
    ///
    /// Measured: a Perlin evaluation cost ~2.7us here, eleven of them per tile,
    /// which was the whole of an eight second generate. Four of those microseconds
    /// were four <c>HashCell</c> calls that always returned the same answers.
    ///
    /// The output is identical to hashing per sample. This is not an
    /// approximation and the same seed still produces the same map — which
    /// matters, because the seed is documented as reproducing a realm exactly.
    /// </remarks>
    private sealed class Lattice
    {
        private readonly byte[] _grad;
        private readonly int _stride;
        private readonly float _freq;

        public Lattice(uint seed, float freq, int span)
        {
            _freq = freq;
            // +2 so the sample at the far edge can still read its x0+1 neighbour.
            _stride = (int)(span * freq) + 2;
            if (_stride < 2)
            {
                _stride = 2;
            }
            _grad = new byte[_stride * _stride];
            for (int gy = 0; gy < _stride; gy++)
            {
                for (int gx = 0; gx < _stride; gx++)
                {
                    _grad[gy * _stride + gx] = (byte)(HashCell(gx, gy, seed) & 7u);
                }
            }
        }

        /// <summary>Samples at a tile coordinate.</summary>
        public float At(float tx, float ty)
        {
            float x = tx * _freq;
            float y = ty * _freq;

            // Truncation rather than MathF.Floor: tile coordinates are never
            // negative here, so the two agree, and Floor was a call per sample.
            int x0 = (int)x;
            int y0 = (int)y;
            float fx = x - x0;
            float fy = y - y0;

            // Clamped rather than wrapped. A sample past the lattice would read
            // another row's gradients, which shows up as a seam down the map.
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

        /// <summary>The same eight directions <see cref="GradDot"/> uses.</summary>
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

    /// <summary>
    /// A field evaluated on a coarse grid and read back by interpolation.
    /// </summary>
    /// <remarks>
    /// Noise that varies slowly does not need a sample per tile. The coarse warp
    /// has a period of about twenty-eight tiles, so sampling it every four still
    /// gives seven samples across every feature and the difference is not
    /// visible — while costing a sixteenth of the evaluations.
    ///
    /// <b>This is an approximation, and it changes the map.</b> The same seed
    /// still produces the same map as itself, but not the map it produced before
    /// this existed. That is the trade for the speed; it is worth stating because
    /// the seed is documented as reproducing a realm exactly.
    ///
    /// The stride has to stay well under the field's period. Too coarse and the
    /// smallest octave turns into visible bilinear facets — straight-edged
    /// diamonds, which look like a bug rather than like terrain.
    /// </remarks>
    private sealed class Coarse
    {
        private readonly float[] _grid;
        private readonly int _w;
        private readonly float _inv;
        private readonly int _stride;

        public Coarse(Field source, int span, int stride)
        {
            _stride = stride < 1 ? 1 : stride;
            _inv = 1f / _stride;
            _w = span / _stride + 2;
            _grid = new float[_w * _w];

            for (int gy = 0; gy < _w; gy++)
            {
                for (int gx = 0; gx < _w; gx++)
                {
                    _grid[gy * _w + gx] = source.At(gx * _stride, gy * _stride);
                }
            }
        }

        public float At(float x, float y)
        {
            float gx = x * _inv;
            float gy = y * _inv;
            int x0 = (int)gx;
            int y0 = (int)gy;
            if (x0 > _w - 2) x0 = _w - 2;
            if (y0 > _w - 2) y0 = _w - 2;
            float fx = gx - x0;
            float fy = gy - y0;

            int i = y0 * _w + x0;
            float a = _grid[i] + fx * (_grid[i + 1] - _grid[i]);
            int j = i + _w;
            float b = _grid[j] + fx * (_grid[j + 1] - _grid[j]);
            return a + fy * (b - a);
        }
    }

    /// <summary>Summed octaves, each with its own prepared lattice.</summary>
    private sealed class Field
    {
        private readonly Lattice[] _octaves;
        private readonly float[] _amps;
        private readonly float _norm;

        public Field(uint seed, float freq, int octaves, int span)
        {
            int count = octaves < 1 ? 1 : octaves;
            _octaves = new Lattice[count];
            _amps = new float[count];

            float amp = 1f, f = freq, norm = 0f;
            for (int o = 0; o < count; o++)
            {
                // Same seed derivation as the per-sample version, so a given seed
                // still lands on the same gradients.
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

    /// <summary>
    /// Gradient noise in roughly [-1, 1].
    /// </summary>
    /// <remarks>
    /// Gradient rather than value noise: value noise has extrema pinned to the
    /// integer lattice, which shows up as a faint square grid. It is invisible in
    /// a heightmap and very visible once it is warping a biome border.
    /// </remarks>
    private static float Perlin(float x, float y, uint seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float u = Fade(fx), v = Fade(fy);

        float n00 = GradDot(x0, y0, fx, fy, seed);
        float n10 = GradDot(x0 + 1, y0, fx - 1f, fy, seed);
        float n01 = GradDot(x0, y0 + 1, fx, fy - 1f, seed);
        float n11 = GradDot(x0 + 1, y0 + 1, fx - 1f, fy - 1f, seed);

        float a = n00 + u * (n10 - n00);
        float b = n01 + u * (n11 - n01);
        return (a + v * (b - a)) * 1.4f;
    }

    /// <summary>Summed octaves, normalised back to roughly [-1, 1].</summary>
    private static float Fbm(float x, float y, uint seed, int octaves)
    {
        float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Perlin(x * freq, y * freq, seed + (uint)o * 0x9E3779B9u);
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
        }
        return norm > 0f ? sum / norm : 0f;
    }
}
