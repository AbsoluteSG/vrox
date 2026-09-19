using SpacetimeDB;

/// <summary>
/// The smallest thing that is still a multiplayer game: players that move.
/// </summary>
/// <remarks>
/// The server is authoritative. The client sends a direction; the server decides
/// where that puts you. It never accepts a position from a client.
///
/// Movement is a fixed step per input rather than one scaled by elapsed time.
/// That is the single most important decision here: a fixed step means the same
/// input always produces the same displacement, which is what makes it possible
/// to later predict the result on the client and have it agree exactly. Scaling
/// by measured time is the version that cannot be predicted.
/// </remarks>
public static partial class Module
{
    /// <summary>Fallback speed, used only before a config has been pushed.</summary>
    private const float Speed = 5f;

    /// <summary>Seconds of simulation one input represents. The client sends at this rate.</summary>
    private const float StepSeconds = 0.05f;

    /// <summary>Seconds between server ticks. Must match the ShotCleanup interval.</summary>
    private const float TickSeconds = 0.05f;

    /// <summary>How long a dropped bag stays on the ground, in seconds.</summary>
    private const float BagLifetimeSeconds = 120f;

    /// <summary>How long a tracer row survives, in milliseconds.</summary>
    private const float TracerLifetimeMs = 150f;

    /// <summary>
    /// How much of a player's defence armour break removes, as a percentage.
    /// </summary>
    /// <remarks>
    /// A module constant rather than a per-bullet strength, deliberately. Carrying
    /// it on the bullet means another column on <c>Shot</c>, and every authored
    /// weapon would have to answer a question none of them currently has an
    /// opinion about. One number now; when a weapon actually needs its own, that
    /// is the moment the column earns its place.
    /// </remarks>
    private const ushort ArmorBreakPercentDefault = 50;

    /// <summary>Movement multiplier while slowed. Shared by players and enemies.</summary>
    /// <remarks>
    /// One constant rather than a literal in each place, because the client
    /// predicts the player's slowed step and has to agree with the server exactly.
    /// Two copies of 0.5f is two things to change and one of them gets missed.
    /// </remarks>
    private const float SlowFactor = 0.5f;

    /// <summary>How close a player must be to take from a bag, in tiles.</summary>
    /// <remarks>
    /// Checked on the server on every take, not once when the panel opened. The
    /// client decides when to *show* a bag, which is only a UI question; whether
    /// an item may actually move is not something a client gets to answer.
    /// </remarks>
    private const float PickupRange = 2f;

    /// <summary>
    /// Slots per container. Must match the inventory panel in the scene.
    /// </summary>
    /// <remarks>
    /// The server is the one that refuses an out-of-range slot, so a UI with more
    /// slots than these simply has some that never accept anything. Stated here
    /// because there is no way for the scene to ask.
    /// </remarks>
    private const byte BackpackSlots = 6;
    private const byte EquippedSlots = 3;

    /// <summary>
    /// Slots that survive death.
    /// </summary>
    /// <remarks>
    /// The pressure valve on a loop that otherwise takes everything. Small on
    /// purpose: enough to bring home the one thing that mattered, not enough to
    /// make dying cheap. This is the dial to turn if permadeath stops being worth
    /// playing.
    /// </remarks>
    /// <summary>How many characters one account may have at once.</summary>
    private const int CharacterSlots = 4;

    public const byte ContainerPack = 0;
    public const byte ContainerEquipped = 1;
    public const byte ContainerSecure = 2;
    public const byte ContainerVault = 3;

    /// <summary>
    /// How big each container is, in cells.
    /// </summary>
    /// <remarks>
    /// The pack is deliberately tight: a rifle that costs eight cells of fifteen
    /// is the decision the grid exists to create, and a pack that swallowed
    /// everything would make carrying free again.
    ///
    /// Equipped is 3x1 and ignores footprints — see <see cref="GridItem"/>.
    /// </remarks>
    private static (byte w, byte h) ContainerSize(byte container) => container switch
    {
        ContainerEquipped => (EquippedSlots, (byte)1),
        ContainerSecure => (2, 2),
        ContainerVault => (10, 6),
        _ => (5, 3),
    };

    /// <summary>
    /// How close to the spawn point a player must be to use their vault.
    /// </summary>
    /// <remarks>
    /// Spawn is the base until extraction points exist. Banking has to cost
    /// something — walking back — or the whole risk of carrying loot evaporates
    /// and you would simply deposit after every kill.
    /// </remarks>
    private const float VaultRange = 6f;

    /// <summary>Size used until a realm config says otherwise.</summary>
    public const float DefaultWorldSize = 128f;

    /// <summary>
    /// The world is a square from 0 to this, in tiles.
    /// </summary>
    /// <remarks>
    /// Authored on <c>RealmConfig</c> and cached here, because <see cref="Clamp"/>
    /// and the generator are called from places that have no reducer context to
    /// read a row with. Refreshed at the top of every tick and whenever the config
    /// is written, so a change takes effect within one tick rather than at the
    /// next restart.
    ///
    /// Must divide by <see cref="ChunkSize"/>, so the terrain array is exactly the
    /// world and there is no unreachable padding. At 40 it was not: tiles 40..47
    /// existed, were solid, and nothing could ever stand on them. The upsert
    /// refuses a size that would bring that back.
    /// </remarks>
    private static float WorldSize = DefaultWorldSize;

    /// <summary>
    /// Re-reads the world size, and drops the terrain cache if it moved.
    /// </summary>
    /// <remarks>
    /// The cache is indexed by a span derived from the size, so a stale one after
    /// a resize would read tiles from the wrong row — walls in the wrong places
    /// rather than an obvious failure.
    /// </remarks>
    private static void RefreshWorldSize(ReducerContext ctx)
    {
        // The generated realm wins over the config. They are two different
        // numbers wearing one name: the config is how big the *next* realm will
        // be, the realm row is how big the one that exists actually is. Reading
        // the config while standing on older terrain puts the world's edge, and
        // everything Clamp places against it, somewhere the ground is not.
        //
        // That is not hypothetical. A config of 512 against a realm generated at
        // 128 left ten of twelve spawners outside the map and 33 of 38 living
        // enemies unreachable, with nothing anywhere reporting a problem.
        float size;
        if (ctx.Db.Realm.Id.Find((byte)1) is { Mode: ModeGenerated, Size: > 0 } realm)
        {
            size = realm.Size;
        }
        else
        {
            size = ctx.Db.RealmConfig.Id.Find(1) is { WorldSize: > 0 } cfg
                ? cfg.WorldSize
                : DefaultWorldSize;
        }

        if (size != WorldSize)
        {
            WorldSize = size;
            InvalidateTerrain();
        }
    }

    /// <summary>Takes the world size from the config, for building a new realm.</summary>
    /// <remarks>
    /// The counterpart to <see cref="RefreshWorldSize"/>, and the only place the
    /// configured size is allowed to win. Split out rather than expressed as a
    /// flag on that method so the two intentions are named where they are called:
    /// "how big is the world" and "how big should the next one be" are different
    /// questions, and the bug this fixes came from one function answering both.
    /// </remarks>
    private static void AdoptConfiguredWorldSize(ReducerContext ctx)
    {
        float size = ctx.Db.RealmConfig.Id.Find(1) is { WorldSize: > 0 } cfg
            ? cfg.WorldSize
            : DefaultWorldSize;

        if (size != WorldSize)
        {
            WorldSize = size;
            InvalidateTerrain();
        }
    }

    /// <summary>
    /// Points tried before a spawner gives up on an interval.
    /// </summary>
    /// <remarks>
    /// Raised from 12 when one spawner grew to cover a whole region. A circle
    /// sized to a region overhangs its neighbours and includes their walls, so a
    /// larger share of samples is rejected than when the circle was a small
    /// clearing — and a spawner that runs out of attempts does not spawn, which
    /// looks like a dead region rather than an unlucky roll.
    /// </remarks>
    private const int SpawnAttempts = 32;

    /// <summary>Collision radius of a player, in tiles.</summary>
    private const float PlayerRadius = 0.4f;

    /// <summary>
    /// How far from a player an enemy still gets simulated, in tiles.
    /// </summary>
    /// <remarks>
    /// Comfortably wider than a screen. An enemy nobody can see does not need to
    /// move: nothing observes it, and it cannot reach anyone before coming back
    /// into range and resuming.
    ///
    /// It has to exceed the visible area rather than merely match it, or enemies
    /// visibly start moving as they come on screen — which is far more noticeable
    /// than them having been still.
    /// </remarks>
    private const float SimulationRadius = 28f;

    /// <summary>
    /// Width of a spatial cell, in tiles.
    /// </summary>
    /// <remarks>
    /// Comfortably larger than a tile and smaller than the simulation radius, so
    /// a player pulls in a handful of cells rather than one huge one or hundreds
    /// of tiny ones. Bigger cells read more enemies than needed; smaller ones
    /// cost more index lookups per player.
    /// </remarks>
    private const float CellSize = 32f;

    /// <summary>The cell a point falls in.</summary>
    private static uint CellOf(float x, float y)
    {
        uint cx = (uint)Math.Max(0, (int)(x / CellSize));
        uint cy = (uint)Math.Max(0, (int)(y / CellSize));
        return (cy << 16) | (cx & 0xFFFF);
    }

    /// <summary>Moves an enemy, keeping its cell in step.</summary>
    /// <remarks>
    /// The only place an enemy's position is assigned. Setting X and Y directly
    /// would leave <see cref="Enemy.Cell"/> pointing at where it used to be, and
    /// the tick would stop simulating it as soon as it walked into a new cell.
    /// </remarks>
    private static void Reposition(ref Enemy enemy, float x, float y)
    {
        enemy.X = x;
        enemy.Y = y;
        enemy.Cell = CellOf(x, y);
    }

    private const ushort PlayerMaxHp = 100;

    // There is deliberately no default weapon. Bare hands do not shoot.
    //
    // A fallback here was worse than useless: with nothing equipped the server
    // still fired a plausible single shot, so "my weapon is not equipped" and
    // "my weapon fires one projectile" looked exactly the same on screen and the
    // real failure was invisible.

    /// <summary>
    /// One separate copy of the world: the realm, or later a dungeon.
    /// </summary>
    /// <remarks>
    /// Every world-scoped row — players, enemies, shots, spawners, bags, tracers,
    /// dummies — carries a <c>ZoneId</c>, and the tick simulates each zone only
    /// against its own rows. That one column is the whole separation, which is
    /// also what keeps this shard-ready: a zone never refers to another database.
    ///
    /// Not <c>AutoInc</c>. The realm is a fixed id that column defaults point at,
    /// and inserting an explicit value into an auto-increment column leaves the
    /// sequence free to hand the same id out again later.
    ///
    /// Terrain is still the single realm map. Dungeon layouts, and a terrain
    /// cache per layout, arrive with the first authored dungeon — until then a
    /// second zone would share the realm's walls.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Zone", Public = true)]
    public partial struct Zone
    {
        [PrimaryKey]
        public uint Id;

        /// <summary><see cref="ZoneRealm"/> or <see cref="ZoneDungeon"/>.</summary>
        public byte Kind;

        /// <summary>Which terrain this zone uses. 0 is the realm map.</summary>
        public ushort LayoutId;

        public Timestamp CreatedAt;

        /// <summary>Where a player leaving this zone lands in the realm.</summary>
        /// <remarks>The entrance portal's position, copied here because the portal expires long before the dungeon does.</remarks>
        [SpacetimeDB.Default(0f)]
        public float ReturnX;

        [SpacetimeDB.Default(0f)]
        public float ReturnY;

        /// <summary>When the last player left, in microseconds. 0 while anyone is inside.</summary>
        [SpacetimeDB.Default(0ul)]
        public ulong EmptySinceUs;

        /// <summary>When this zone closes whoever is inside, in microseconds. 0 never — the realm.</summary>
        [SpacetimeDB.Default(0ul)]
        public ulong ClosesAtUs;
    }

    /// <summary>
    /// A dungeon's shape, authored as a Unity tilemap and pushed by the editor.
    /// </summary>
    /// <remarks>
    /// A template, not an instance. Every run of a dungeon shares this row and its
    /// chunks, so opening one writes no terrain at all — ten parties in the same
    /// dungeon cost one copy of its map.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "DungeonLayout", Public = true)]
    public partial struct DungeonLayout
    {
        [PrimaryKey]
        public ushort Id;

        public string Name;

        /// <summary>Span in tiles on each axis. A whole number of chunks.</summary>
        public uint Size;

        public float SpawnX;
        public float SpawnY;

        /// <summary>Where the portal back to the realm stands.</summary>
        public float ExitX;
        public float ExitY;

        /// <summary>Packed 0xRRGGBB, for the portal that leads here.</summary>
        public uint Tint;

        /// <summary>How long a run may last before it closes on whoever is still inside.</summary>
        public uint LifetimeSeconds;
    }

    /// <summary>One chunk of a dungeon layout's terrain.</summary>
    /// <remarks>
    /// A table of its own rather than a <c>LayoutId</c> on <see cref="TerrainChunk"/>,
    /// whose primary key is the cell alone. Changing a primary key cannot be
    /// migrated, and a shared table would also mean a realm push could clear
    /// dungeon ground.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "LayoutChunk", Public = true)]
    public partial struct LayoutChunk
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        [SpacetimeDB.Index.BTree]
        public ushort LayoutId;

        public uint Cell;

        /// <summary>ChunkSize * ChunkSize tiles, row-major from the chunk's bottom-left.</summary>
        public List<TileData> Tiles;
    }

    /// <summary>A spawner as authored in a layout, copied into each run of it.</summary>
    /// <remarks>
    /// Private: this is what a dungeon <em>will</em> contain, the same kind of
    /// design data as a loot table. A run that is already open keeps the spawners
    /// it was opened with; re-pushing only changes runs opened afterwards.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "LayoutSpawner")]
    public partial struct LayoutSpawner
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        [SpacetimeDB.Index.BTree]
        public ushort LayoutId;

        public List<AreaEntry> Composition;
        public float X;
        public float Y;
        public float Radius;
        public ushort MaxAlive;
        public ushort IntervalMs;
    }

    /// <summary>Which enemies can drop a portal to which dungeon. Private, like loot tables.</summary>
    [SpacetimeDB.Table(Accessor = "PortalDrop")]
    public partial struct PortalDrop
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        [SpacetimeDB.Index.BTree]
        public ushort EnemyDefId;

        [SpacetimeDB.Index.BTree]
        public ushort LayoutId;

        public float ChancePercent;
    }

    /// <summary>One "dropped by" entry, as the editor sends it.</summary>
    [SpacetimeDB.Type]
    public partial struct LayoutDrop
    {
        public ushort EnemyDefId;
        public float ChancePercent;
    }

    /// <summary>
    /// A doorway between zones.
    /// </summary>
    /// <remarks>
    /// Public by design: anyone standing next to an entrance may use it, and
    /// everyone who does lands in the same run. The run is opened by the first
    /// person through, not when the portal drops, so a portal nobody takes costs
    /// one row.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Portal", Public = true)]
    public partial struct Portal
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        /// <summary>The zone the portal stands in.</summary>
        [SpacetimeDB.Index.BTree]
        public uint ZoneId;

        public float X;
        public float Y;

        /// <summary>The dungeon an entrance leads to. 0 for an exit.</summary>
        public ushort LayoutId;

        /// <summary>The zone it opens into. 0 until somebody walks through an entrance.</summary>
        [SpacetimeDB.Index.BTree]
        public uint TargetZoneId;

        /// <summary><see cref="PortalEntrance"/> or <see cref="PortalExit"/>.</summary>
        public byte Kind;

        /// <summary>When it disappears, in microseconds. 0 never.</summary>
        public ulong ExpiresAtUs;
    }

    public const byte PortalEntrance = 0;
    public const byte PortalExit = 1;

    // --- Dungeons -----------------------------------------------------------
    //
    // An entrance drops in some zone. The first player through opens a run: a
    // new Zone row pointing at the layout, the layout's spawners copied in with
    // that zone id, and an exit portal. Everyone else who uses the same entrance
    // lands in the same run. Leaving — by the exit, by dying, by disconnecting or
    // by leaving the character — puts you back in the realm at the entrance.
    //
    // A run with nobody in it stops simulating immediately and is deleted after a
    // grace period, row by row through the zone indexes. One that outlives its
    // layout's lifetime is closed on whoever is still inside.

    /// <summary>How close a player must stand to use a portal, in tiles.</summary>
    /// <remarks>Checked here against the server's position; the client's range only decides what it offers.</remarks>
    private const float PortalReach = 1.5f;

    /// <summary>How long a dropped entrance stays open, in seconds.</summary>
    private const float PortalLifetimeSeconds = 30f;

    /// <summary>How long a run survives with nobody inside, in seconds.</summary>
    /// <remarks>
    /// Death, disconnecting and leaving all return you to the realm, so this is not
    /// a reconnect window. It is what lets a player who stepped out walk back in
    /// through an entrance that is still open, instead of finding a fresh run.
    /// </remarks>
    private const float EmptyZoneGraceSeconds = 60f;

    /// <summary>Lifetime for a layout that did not author one.</summary>
    private const uint DefaultDungeonLifetimeSeconds = 900;

    /// <summary>
    /// First spawner id handed to a dungeon run.
    /// </summary>
    /// <remarks>
    /// Spawner ids are a <c>ushort</c> primary key shared with editor spawners
    /// (1 upward) and generated ones (from 10000), so runs take the top of the
    /// range. That caps concurrent runs at roughly 25,000 spawners in total —
    /// thousands of dungeons — and <see cref="FreeSpawnerId"/> refuses loudly when
    /// it runs out rather than overwriting somebody's camp.
    /// </remarks>
    private const int DungeonSpawnerBase = 40000;

    /// <summary>A spawner copied into a dungeon run. Never cleared by an editor push.</summary>
    public const byte SourceDungeon = 2;

    /// <summary>Rolls this enemy's portal drops, opening at most one entrance.</summary>
    private static void TrySpawnPortal(ReducerContext ctx, Enemy enemy)
    {
        foreach (var drop in ctx.Db.PortalDrop.EnemyDefId.Filter(enemy.DefId).ToList())
        {
            if (ctx.Rng.NextDouble() * 100.0 >= drop.ChancePercent)
            {
                continue;
            }
            if (ctx.Db.DungeonLayout.Id.Find(drop.LayoutId) is not { } layout)
            {
                // Said out loud: a drop that can never open looks exactly like an
                // unlucky roll otherwise.
                Log.Warn($"enemy {enemy.DefId} drops a portal to layout {drop.LayoutId}, "
                       + "which has not been pushed");
                continue;
            }

            long expires = ctx.Timestamp.MicrosecondsSinceUnixEpoch
                         + (long)(PortalLifetimeSeconds * 1_000_000f);
            ctx.Db.Portal.Insert(new Portal
            {
                Id = 0,
                ZoneId = enemy.ZoneId,
                X = enemy.X,
                Y = enemy.Y,
                LayoutId = layout.Id,
                TargetZoneId = 0,
                Kind = PortalEntrance,
                ExpiresAtUs = (ulong)expires,
            });
            Log.Info($"a portal to {layout.Name} opened at ({enemy.X:0.0}, {enemy.Y:0.0}) "
                   + $"in zone {enemy.ZoneId}");
            return;
        }
    }

    /// <summary>Walks the caller through a portal next to them.</summary>
    [SpacetimeDB.Reducer]
    public static void EnterPortal(ReducerContext ctx, ulong portalId)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            throw new Exception("no character is being played");
        }
        if (ctx.Db.Portal.Id.Find(portalId) is not { } portal)
        {
            throw new Exception("that portal has closed");
        }
        if (portal.ZoneId != player.ZoneId)
        {
            throw new Exception("that portal is in another zone");
        }
        if (player.LootingBag != 0)
        {
            throw new Exception("close the bag first");
        }

        float dx = player.X - portal.X;
        float dy = player.Y - portal.Y;
        if (dx * dx + dy * dy > PortalReach * PortalReach)
        {
            throw new Exception("too far from that portal");
        }

        if (portal.Kind == PortalExit)
        {
            uint from = player.ZoneId;
            LeaveZone(ctx, ref player);
            ctx.Db.Player.Identity.Update(player);
            Log.Info($"{player.Name} left zone {from}");
            return;
        }

        if (ctx.Db.DungeonLayout.Id.Find(portal.LayoutId) is not { } layout)
        {
            throw new Exception($"dungeon layout {portal.LayoutId} is not on the server");
        }

        uint target = portal.TargetZoneId;
        if (target != 0 && ctx.Db.Zone.Id.Find(target) is null)
        {
            // Closing a run deletes the entrances that point at it, so reaching
            // here means that did not happen. Refused rather than quietly opening
            // a second run behind the same door.
            throw new Exception("that dungeon has already closed");
        }
        if (target == 0)
        {
            target = OpenDungeon(ctx, portal, layout);
            portal.TargetZoneId = target;
            ctx.Db.Portal.Id.Update(portal);
        }

        player.ZoneId = target;
        player.X = layout.SpawnX;
        player.Y = layout.SpawnY;
        ctx.Db.Player.Identity.Update(player);
        Log.Info($"{player.Name} entered {layout.Name} (zone {target})");
    }

    /// <summary>Creates a run of a dungeon behind an entrance. Returns its zone id.</summary>
    private static uint OpenDungeon(ReducerContext ctx, Portal portal, DungeonLayout layout)
    {
        // Refused, not opened into solid rock: a layout with no chunks is all wall,
        // and the player would arrive unable to move with nothing saying why.
        if (!ctx.Db.LayoutChunk.LayoutId.Filter(layout.Id).Any())
        {
            throw new Exception($"{layout.Name} has no terrain pushed");
        }

        EnsureRealmZone(ctx);

        // One past the highest id in use. An id can come back after the newest run
        // closes; clients key their subscription on zone and layout together, and a
        // closed run has no rows left to confuse the new one with.
        uint id = ctx.Db.Zone.Iter().Max(z => z.Id) + 1;
        long nowUs = ctx.Timestamp.MicrosecondsSinceUnixEpoch;
        uint lifetime = layout.LifetimeSeconds > 0 ? layout.LifetimeSeconds : DefaultDungeonLifetimeSeconds;

        ctx.Db.Zone.Insert(new Zone
        {
            Id = id,
            Kind = ZoneDungeon,
            LayoutId = layout.Id,
            CreatedAt = ctx.Timestamp,
            ReturnX = portal.X,
            ReturnY = portal.Y,
            EmptySinceUs = 0,
            ClosesAtUs = (ulong)(nowUs + lifetime * 1_000_000L),
        });

        int spawners = 0;
        foreach (var template in ctx.Db.LayoutSpawner.LayoutId.Filter(layout.Id).ToList())
        {
            ctx.Db.Spawner.Insert(new Spawner
            {
                Id = FreeSpawnerId(ctx),
                Composition = template.Composition,
                X = template.X,
                Y = template.Y,
                Radius = template.Radius,
                MaxAlive = template.MaxAlive,
                IntervalMs = template.IntervalMs,
                NextSpawnAt = ctx.Timestamp,
                Source = SourceDungeon,
                Biome = AnyBiome,
                ZoneId = id,
            });
            spawners++;
        }

        ctx.Db.Portal.Insert(new Portal
        {
            Id = 0,
            ZoneId = id,
            X = layout.ExitX,
            Y = layout.ExitY,
            LayoutId = 0,
            TargetZoneId = RealmZone,
            Kind = PortalExit,
            ExpiresAtUs = 0,
        });

        Log.Info($"opened {layout.Name} as zone {id}: {spawners} spawner(s), "
               + $"closes in {lifetime}s");
        return id;
    }

    /// <summary>The lowest spawner id free for a dungeon run.</summary>
    /// <remarks>
    /// A linear probe from the base. Cheap while runs are counted in dozens; if
    /// thousands are ever open at once this wants a free list.
    /// </remarks>
    private static ushort FreeSpawnerId(ReducerContext ctx)
    {
        for (int id = DungeonSpawnerBase; id <= ushort.MaxValue; id++)
        {
            if (ctx.Db.Spawner.Id.Find((ushort)id) is null)
            {
                return (ushort)id;
            }
        }
        throw new Exception("no spawner ids left for another dungeon run");
    }

    /// <summary>
    /// Puts a player back in the realm, at the entrance of the zone they were in.
    /// </summary>
    /// <remarks>
    /// Does not write the row; callers are all mid-update. Falls back to the realm
    /// spawn when the entrance point is now solid, because arriving inside a wall
    /// is unrecoverable.
    /// </remarks>
    private static void LeaveZone(ReducerContext ctx, ref Player player)
    {
        float x, y;
        if (ctx.Db.Zone.Id.Find(player.ZoneId) is { Kind: ZoneDungeon } zone)
        {
            x = zone.ReturnX;
            y = zone.ReturnY;
        }
        else
        {
            (x, y) = SpawnPoint(ctx);
        }
        if (GroundOf(ctx, RealmLayout).Blocked(x, y))
        {
            (x, y) = SpawnPoint(ctx);
        }

        player.ZoneId = RealmZone;
        player.X = x;
        player.Y = y;
        player.LootingBag = 0;
    }

    /// <summary>Deletes a run and everything in it, returning anyone inside to the realm.</summary>
    private static void CloseZone(ReducerContext ctx, Zone zone, string why)
    {
        if (zone.Id == RealmZone)
        {
            Log.Error($"refused to close the realm ({why})");
            return;
        }

        int evicted = 0;
        foreach (var inside in ctx.Db.Player.ZoneId.Filter(zone.Id).ToList())
        {
            var moved = inside;
            LeaveZone(ctx, ref moved);
            ctx.Db.Player.Identity.Update(moved);
            evicted++;
        }

        int enemies = 0;
        foreach (var enemyId in ctx.Db.Enemy.ZoneId.Filter(zone.Id).Select(e => e.Id).ToList())
        {
            // Tallies go with their enemy, as they do on a kill. Left behind they
            // are rows nothing will ever read or delete.
            foreach (var t in ctx.Db.DamageTally.EnemyId.Filter(enemyId).Select(t => t.Id).ToList())
            {
                ctx.Db.DamageTally.Id.Delete(t);
            }
            foreach (var t in ctx.Db.DebuffTally.EnemyId.Filter(enemyId).Select(t => t.Id).ToList())
            {
                ctx.Db.DebuffTally.Id.Delete(t);
            }
            ctx.Db.Enemy.Id.Delete(enemyId);
            enemies++;
        }

        foreach (var id in ctx.Db.Shot.ZoneId.Filter(zone.Id).Select(r => r.Id).ToList())
        {
            ctx.Db.Shot.Id.Delete(id);
        }
        foreach (var id in ctx.Db.Spawner.ZoneId.Filter(zone.Id).Select(r => r.Id).ToList())
        {
            ctx.Db.Spawner.Id.Delete(id);
        }
        foreach (var id in ctx.Db.LootDrop.ZoneId.Filter(zone.Id).Select(r => r.Id).ToList())
        {
            ctx.Db.LootDrop.Id.Delete(id);
        }
        foreach (var id in ctx.Db.Tracer.ZoneId.Filter(zone.Id).Select(r => r.Id).ToList())
        {
            ctx.Db.Tracer.Id.Delete(id);
        }
        foreach (var id in ctx.Db.Dummy.ZoneId.Filter(zone.Id).Select(r => r.Id).ToList())
        {
            ctx.Db.Dummy.Id.Delete(id);
        }
        foreach (var id in ctx.Db.Portal.ZoneId.Filter(zone.Id).Select(r => r.Id).ToList())
        {
            ctx.Db.Portal.Id.Delete(id);
        }
        // Entrances in other zones that still lead here. Otherwise the next person
        // through would be refused by a door that looks open.
        foreach (var id in ctx.Db.Portal.TargetZoneId.Filter(zone.Id).Select(r => r.Id).ToList())
        {
            ctx.Db.Portal.Id.Delete(id);
        }

        ctx.Db.Zone.Id.Delete(zone.Id);
        Log.Info($"closed zone {zone.Id} (layout {zone.LayoutId}) — {why}: "
               + $"{evicted} player(s) returned, {enemies} enemy row(s) removed");
    }

    /// <summary>Removes entrances whose time is up. Exits never expire.</summary>
    private static void ExpirePortals(ReducerContext ctx, long nowUs)
    {
        var stale = new List<ulong>();
        foreach (var portal in ctx.Db.Portal.Iter())
        {
            if (portal.ExpiresAtUs != 0 && (ulong)nowUs >= portal.ExpiresAtUs)
            {
                stale.Add(portal.Id);
            }
        }
        foreach (ulong id in stale)
        {
            ctx.Db.Portal.Id.Delete(id);
        }
    }

    /// <summary>Opens an entrance to a layout at the caller's feet. Development only.</summary>
    /// <remarks>
    /// Like <c>SpawnEnemy</c>: a way to reach a dungeon without farming a drop,
    /// and the only way to test the whole path from a terminal.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void DebugOpenPortal(ReducerContext ctx, ushort layoutId)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { } player)
        {
            throw new Exception("no player row for the caller");
        }
        if (ctx.Db.DungeonLayout.Id.Find(layoutId) is not { } layout)
        {
            throw new Exception($"no dungeon layout {layoutId}");
        }

        long expires = ctx.Timestamp.MicrosecondsSinceUnixEpoch + (long)(PortalLifetimeSeconds * 1_000_000f);
        var portal = ctx.Db.Portal.Insert(new Portal
        {
            Id = 0,
            ZoneId = player.ZoneId,
            X = player.X,
            Y = player.Y,
            LayoutId = layout.Id,
            TargetZoneId = 0,
            Kind = PortalEntrance,
            ExpiresAtUs = (ulong)expires,
        });
        Log.Info($"debug portal {portal.Id} to {layout.Name} at ({player.X:0.0}, {player.Y:0.0}) "
               + $"in zone {player.ZoneId}");
    }

    /// <summary>Inserts or replaces a dungeon layout and its portal drops. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertDungeonLayout(ReducerContext ctx, ushort id, string name, uint size,
                                           float spawnX, float spawnY, float exitX, float exitY,
                                           uint tint, uint lifetimeSeconds, List<LayoutDrop> drops)
    {
        if (id == RealmLayout)
        {
            throw new Exception("layout id 0 is the realm's own map");
        }
        // Refused, not rounded, for the same reason as the realm: a size that is
        // not a whole number of chunks leaves tiles nothing can stand on.
        if (size == 0 || size % (uint)ChunkSize != 0 || size > 1024)
        {
            throw new Exception($"layout size {size} must be a multiple of {ChunkSize}, up to 1024");
        }
        if (spawnX < 0f || spawnY < 0f || spawnX > size || spawnY > size
            || exitX < 0f || exitY < 0f || exitX > size || exitY > size)
        {
            throw new Exception($"{name}: spawn and exit must both lie inside 0..{size}");
        }

        var row = new DungeonLayout
        {
            Id = id,
            Name = name,
            Size = size,
            SpawnX = spawnX,
            SpawnY = spawnY,
            ExitX = exitX,
            ExitY = exitY,
            Tint = tint,
            LifetimeSeconds = lifetimeSeconds,
        };
        if (ctx.Db.DungeonLayout.Id.Find(id) is null)
        {
            ctx.Db.DungeonLayout.Insert(row);
        }
        else
        {
            ctx.Db.DungeonLayout.Id.Update(row);
        }

        // Replaced wholesale, so removing an enemy from the list really stops it
        // dropping this portal.
        foreach (var old in ctx.Db.PortalDrop.LayoutId.Filter(id).Select(d => d.Id).ToList())
        {
            ctx.Db.PortalDrop.Id.Delete(old);
        }
        int kept = 0;
        foreach (var drop in drops)
        {
            if (drop.EnemyDefId == 0 || drop.ChancePercent <= 0f)
            {
                continue;
            }
            ctx.Db.PortalDrop.Insert(new PortalDrop
            {
                Id = 0,
                EnemyDefId = drop.EnemyDefId,
                LayoutId = id,
                ChancePercent = Math.Clamp(drop.ChancePercent, 0f, 100f),
            });
            kept++;
        }

        InvalidateLayout(id);
        Log.Info($"dungeon layout {id} \"{name}\": {size}x{size}, {kept} portal drop(s)");
    }

    /// <summary>Removes a layout's terrain and spawner templates, before the editor re-pushes them.</summary>
    [SpacetimeDB.Reducer]
    public static void ClearLayout(ReducerContext ctx, ushort layoutId)
    {
        foreach (var id in ctx.Db.LayoutChunk.LayoutId.Filter(layoutId).Select(c => c.Id).ToList())
        {
            ctx.Db.LayoutChunk.Id.Delete(id);
        }
        foreach (var id in ctx.Db.LayoutSpawner.LayoutId.Filter(layoutId).Select(s => s.Id).ToList())
        {
            ctx.Db.LayoutSpawner.Id.Delete(id);
        }
        InvalidateLayout(layoutId);
    }

    /// <summary>Replaces one chunk of a dungeon layout's terrain. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertLayoutChunk(ReducerContext ctx, ushort layoutId, uint cell, List<TileData> tiles)
    {
        if (ctx.Db.DungeonLayout.Id.Find(layoutId) is null)
        {
            throw new Exception($"push dungeon layout {layoutId} before its terrain");
        }

        foreach (var existing in ctx.Db.LayoutChunk.LayoutId.Filter(layoutId))
        {
            if (existing.Cell == cell)
            {
                var updated = existing;
                updated.Tiles = tiles;
                ctx.Db.LayoutChunk.Id.Update(updated);
                InvalidateLayout(layoutId);
                return;
            }
        }
        ctx.Db.LayoutChunk.Insert(new LayoutChunk { Id = 0, LayoutId = layoutId, Cell = cell, Tiles = tiles });
        InvalidateLayout(layoutId);
    }

    /// <summary>Adds one spawner template to a layout. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void AddLayoutSpawner(ReducerContext ctx, ushort layoutId, List<AreaEntry> composition,
                                        float x, float y, float radius,
                                        ushort maxAlive, ushort intervalMs)
    {
        if (ctx.Db.DungeonLayout.Id.Find(layoutId) is not { } layout)
        {
            throw new Exception($"push dungeon layout {layoutId} before its spawners");
        }
        ctx.Db.LayoutSpawner.Insert(new LayoutSpawner
        {
            Id = 0,
            LayoutId = layoutId,
            Composition = composition,
            X = Math.Clamp(x, 0f, layout.Size),
            Y = Math.Clamp(y, 0f, layout.Size),
            Radius = Math.Clamp(radius, 0f, layout.Size),
            MaxAlive = Math.Clamp(maxAlive, (ushort)0, (ushort)200),
            // Floored for the same reason as realm spawners: no interval is an
            // enemy every tick.
            IntervalMs = (ushort)Math.Clamp((int)intervalMs, 100, 60000),
        });
    }

    public const byte ZoneRealm = 0;
    public const byte ZoneDungeon = 1;

    /// <summary>The realm's zone. Every <c>ZoneId</c> column defaults to it.</summary>
    public const uint RealmZone = 1;

    /// <summary>Creates the realm's zone row if this database predates zones.</summary>
    /// <remarks>
    /// Called from the tick as well as <c>Init</c>: republishing an existing
    /// database does not run <c>Init</c> again, and a world with no zone rows
    /// simulates nothing — every enemy frozen, every shot immortal.
    /// </remarks>
    private static void EnsureRealmZone(ReducerContext ctx)
    {
        if (ctx.Db.Zone.Id.Find(RealmZone) is null)
        {
            ctx.Db.Zone.Insert(new Zone
            {
                Id = RealmZone,
                Kind = ZoneRealm,
                LayoutId = 0,
                CreatedAt = ctx.Timestamp,
            });
            Log.Info("realm zone created");
        }
    }

    [SpacetimeDB.Table(Accessor = "Player", Public = true)]
    public partial struct Player
    {
        [PrimaryKey]
        public Identity Identity;

        public float X;
        public float Y;

        /// <summary>False once they disconnect. The row is kept so a reconnect returns the same character.</summary>
        public bool Online;

        /// <summary>Earliest time this player may fire again.</summary>
        public Timestamp NextShotAt;

        /// <summary>Catalogue id of the equipped weapon. 0 means bare hands.</summary>
        public ushort WeaponId;

        public ushort Hp;
        public ushort MaxHp;

        /// <summary>Shown in kill logs. Set by the client, defaulted on first connect.</summary>
        public string Name;

        public Timestamp LastHitAt;

        /// <summary>
        /// Damage from the last hit taken, after defence.
        /// </summary>
        /// <remarks>
        /// The number applied, not the number rolled. A player reading "12" while
        /// losing 8 health would conclude their defence was broken, when it was
        /// working exactly as intended.
        /// </remarks>
        public ushort LastDamage;

        /// <summary>
        /// Fractional health waiting to become a whole point.
        /// </summary>
        /// <remarks>
        /// Health is an integer, and regeneration is usually less than one point
        /// per tick. Rounding each tick would either regenerate nothing or a point
        /// every tick; carrying the remainder makes the rate mean what it says.
        ///
        /// Replicated, and part of the player's health rather than bookkeeping
        /// behind it: <c>Hp + RegenPool</c> is this module's only continuous
        /// health value, and it is what a health bar should be drawn from. Read
        /// <c>Hp</c> alone and the bar advances a whole point at a time however
        /// often the row updates. Anything that stops the pool from being spent
        /// therefore has to clear it, or the sum exceeds the health the player
        /// actually has.
        /// </remarks>
        public float RegenPool;

        /// <summary>
        /// The bag this player has open, or 0.
        /// </summary>
        /// <remarks>
        /// Scavenging is meant to be dangerous because it roots you, so the root
        /// has to be a server rule. Held purely in the client's UI it would be a
        /// suggestion: a client that ignored its own menu could strafe and loot at
        /// once, which is exactly the risk the design is built on.
        ///
        /// Firing is deliberately still allowed. You can defend yourself, you just
        /// cannot dodge — and in a game made of bullets, not dodging is the whole
        /// cost.
        /// </remarks>
        [SpacetimeDB.Default(0ul)]
        public ulong LootingBag;

        /// <summary>
        /// Which character this account is playing, or 0 for none.
        /// </summary>
        /// <remarks>
        /// 0 means "at the character screen". The row still exists so the account
        /// keeps its place in the world's tables, but nothing should simulate a
        /// player who is not playing anybody — movement, shots and enemy
        /// targeting all check this first.
        /// </remarks>
        [SpacetimeDB.Default(0ul)]
        public ulong CharacterId;

        /// <summary>Rounds left in the magazine.</summary>
        /// <remarks>
        /// On the player, not the weapon: two characters carrying the same
        /// archetype have their own magazines, and a weapon row is a catalogue
        /// entry shared by everyone holding one.
        /// </remarks>
        [SpacetimeDB.Default((ushort)0)]
        public ushort Ammo;

        /// <summary>When the reload in progress finishes. Past means idle.</summary>
        [SpacetimeDB.Default(0ul)]
        public ulong ReloadAtUs;

        // Debuffs an enemy bullet has applied. Appended to the end of the row on
        // purpose: a client with stale bindings misreads every column after a new
        // one, so the new ones go where there is nothing after them to shift.
        //
        // Replicated so the client can *show* them. There is nothing to keep in
        // step: VroxPlayer does not predict movement, it draws the position the
        // server reports, so a slow needs no client-side arithmetic to match.
        // What the client cannot do without these columns is tell the player why
        // they suddenly walk slower, which is the whole feedback the debuff owes
        // them.

        /// <summary>Cannot move or fire until this time.</summary>
        [SpacetimeDB.Default(0ul)]
        public ulong StunnedUntilUs;

        /// <summary>Moves at <see cref="SlowFactor"/> speed until this time.</summary>
        [SpacetimeDB.Default(0ul)]
        public ulong SlowedUntilUs;

        /// <summary>Armour is reduced until this time.</summary>
        [SpacetimeDB.Default(0ul)]
        public ulong ArmorBrokenUntilUs;

        /// <summary>
        /// How much defence armour break removes, as a percentage of it.
        /// </summary>
        /// <remarks>
        /// A share rather than a flat number so one debuff reads the same against
        /// a starting character and a geared one. Carried on the player rather
        /// than looked up from whatever last hit them: the bullet that applied it
        /// is long deleted by the time the next hit is resolved.
        /// </remarks>
        [SpacetimeDB.Default((ushort)0)]
        public ushort ArmorBreakPercent;

        /// <summary>Which zone this row belongs to. See <see cref="Zone"/>.</summary>
        [SpacetimeDB.Default(1u)]
        [SpacetimeDB.Index.BTree]
        public uint ZoneId;
    }

    /// <summary>
    /// The weapon catalogue: what the server actually fires from.
    /// </summary>
    /// <remarks>
    /// Authored as ScriptableObjects in Unity and pushed here; these rows are the
    /// runtime version. Editing one changes every player's weapon on the next
    /// shot with nobody rebuilding anything, which is what makes tuning during
    /// play possible.
    ///
    /// Public, so clients can read the stats they need to draw and describe a
    /// weapon — but only the server ever fires one.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "WeaponDef", Public = true)]
    public partial struct WeaponDef
    {
        [PrimaryKey]
        public ushort Id;

        public string Name;

        public ushort FireRateMs;
        public ushort DamageMin;
        public ushort DamageMax;

        public float ProjectileSpeed;
        public ushort ProjectileLifetimeMs;
        public float ProjectileSize;

        /// <summary>0 physical, 1 fire, 2 ice, 3 poison, 4 arcane.</summary>
        public byte Element;

        /// <summary>0 none, 1 stun, 2 slow.</summary>
        public byte DebuffKind;

        public float DebuffSeconds;

        /// <summary>Mirrors the client's PatternKind. See PlaceShots.</summary>
        public byte PatternKind;

        public byte Shots;

        /// <summary>Total arc for a spread, in degrees. Perpendicular width for parallel.</summary>
        public float SpreadDegrees;

        /// <summary>
        /// Rotates the whole volley over time, in degrees per second.
        /// </summary>
        /// <remarks>
        /// Derived from the spawn timestamp rather than a counter, so it needs no
        /// per-player state and every volley lands where the clock says it should
        /// even if shots are missed. Ring plus spin is the classic spiral.
        /// </remarks>
        public float SpinDegreesPerSec;

        /// <summary>Sideways travel of each projectile, in tiles. 0 is a straight line.</summary>
        public float WaveAmplitude;

        /// <summary>Oscillations per second.</summary>
        public float WaveFrequency;

        /// <summary>
        /// Projectile colour, packed 0xRRGGBB, as authored on the weapon.
        /// </summary>
        /// <remarks>
        /// Authored rather than derived. Colouring by element instead would mean
        /// the palette lives in the client and a designer cannot change a
        /// weapon's look without editing code — and enemies already carry their
        /// own <c>Tint</c> this way, so this is the convention, not a new idea.
        /// </remarks>
        [SpacetimeDB.Default(0u)]
        public uint Tint;


        /// <summary>
        /// The bullet kinds this weapon's volley is made of. Empty means every
        /// projectile uses the flat columns above.
        /// </summary>
        /// <remarks>
        /// The fourth knob. The first three place a projectile — rotate it, move
        /// where it starts, shift where it is in its wave — and this one says what
        /// that projectile <em>is</em>. Keeping it orthogonal is what stops the
        /// pattern list from having to grow a case per combination.
        ///
        /// Empty is authored intent, not a fallback for a failure: there is no way
        /// for this list to arrive empty by accident. A client whose bindings are
        /// stale does not silently lose the column, it shifts every field after it
        /// and the SDK throws — which is exactly why the bindings are regenerated
        /// on every publish.
        /// </remarks>
        public List<BulletProfile> Profiles;

        /// <summary>
        /// How projectile slots map onto <see cref="Profiles"/>. 0 cycle, 1 block.
        /// </summary>
        /// <remarks>
        /// Cycle alternates around the volley — a ring of eight with two profiles
        /// gives ABABABAB. Block gives contiguous runs — AAAABBBB — which is a
        /// visibly different weapon rather than a tuning difference.
        ///
        /// Both are index arithmetic against the slot count, so neither can go out
        /// of step when the shot count changes. An explicit per-slot index list
        /// would be more general and would silently mis-assign the moment someone
        /// edited the count without editing the list.
        /// </remarks>
        public byte ProfileAssignment;
        /// <summary>
        /// Shots before a reload. 0 means no magazine at all.
        /// </summary>
        /// <remarks>
        /// Zero is how every weapon authored before this behaves: fire forever at
        /// its rate and never reload. That is the neutral case, not a special one,
        /// so adding magazines cannot silently break an existing catalogue.
        /// </remarks>
        [SpacetimeDB.Default((ushort)0)]
        public ushort Magazine;

        /// <summary>How long a reload takes, in milliseconds.</summary>
        [SpacetimeDB.Default((ushort)0)]
        public ushort ReloadMs;

        /// <summary>
        /// Reload one shell at a time rather than the whole magazine.
        /// </summary>
        /// <remarks>
        /// What makes a shotgun a shotgun. Each <see cref="ReloadMs"/> adds one
        /// shell, so topping up from nearly full is quick and you can break off
        /// and fire the moment you have anything chambered — the decision being
        /// whether one more shell is worth the second it costs.
        /// </remarks>
        [SpacetimeDB.Default(false)]
        public bool ReloadPerShell;

        /// <summary>
        /// How hard firing shoves the shooter backwards, in tiles.
        /// </summary>
        /// <remarks>
        /// Applied to the shooter, not the target, and server-side because
        /// position is. It is a cost and a tool at once: a shotgun that pushes you
        /// out of a crowd is doing something a damage number cannot express.
        /// </remarks>
        [SpacetimeDB.Default(0f)]
        public float Kickback;

        /// <summary>
        /// How far a hitscan ray reaches, in tiles. 0 falls back to projectile fire.
        /// </summary>
        /// <remarks>
        /// This is what makes a weapon a gun rather than a bullet emitter. A
        /// weapon with no range still fires travelling projectiles through the
        /// old path, which is how every enemy weapon works and stays working.
        /// </remarks>
        [SpacetimeDB.Default(0f)]
        public float Range;

        /// <summary>Targets a ray passes through after the first.</summary>
        [SpacetimeDB.Default((byte)0)]
        public byte Pierce;

        /// <summary>
        /// Damage multiplier at maximum range, as a percentage of the muzzle.
        /// </summary>
        /// <remarks>
        /// 100 is no falloff. A shotgun sets this low so it is devastating up
        /// close and pointless across a room, which is the whole reason to carry
        /// one alongside something else — the archetypes have to disagree about
        /// distance or they are the same gun with different numbers.
        /// </remarks>
        [SpacetimeDB.Default((ushort)100)]
        public ushort FalloffPercent;

        /// <summary>
        /// Split one trigger pull's damage and kickback across its rays.
        /// </summary>
        /// <remarks>
        /// On, a shotgun's Damage is the damage of a whole shell and eight
        /// pellets each carry an eighth — so a fan that only clips with three
        /// deals and shoves three-eighths, with no special case. Off, every ray
        /// deals full damage, which is what a multi-barrel weapon wants.
        /// </remarks>
        [SpacetimeDB.Default(true)]
        public bool SplitDamage;

        /// <summary>
        /// How fast the holder walks, as a percentage of their own speed.
        /// </summary>
        /// <remarks>
        /// 100 is no effect, and is what a weapon pushed before this column
        /// existed decodes as. This is a weapon's weight expressed as the only
        /// thing that is felt every second it is held, rather than only when the
        /// trigger is pulled: an LMG that walks at 70 is a different object from
        /// a pistol that walks at 115, even before either fires.
        ///
        /// A percentage rather than a multiplier because the whole catalogue is
        /// authored in whole numbers, and because 0 has to be distinguishable
        /// from "unset" — a clamp floor of 25 means a mis-authored zero is a slow
        /// weapon rather than a player nailed to the floor.
        ///
        /// Last in the struct rather than beside Kickback where it belongs by
        /// meaning: SpacetimeDB reads an inserted column as a reordering of the
        /// table and refuses to publish without a manual migration, so a new
        /// column goes on the end or the world gets wiped.
        /// </remarks>
        [SpacetimeDB.Default((ushort)100)]
        public ushort MoveSpeedPercent;

        /// <summary>
        /// How many directions a Cluster pattern splits its shots between.
        /// </summary>
        /// <remarks>
        /// Only Cluster reads it. Every other pattern already says everything it
        /// needs with a count and an arc; a cluster is the one shape that needs
        /// two counts — how many directions, and how many bullets down each — and
        /// <see cref="Shots"/> is the total, so this is what divides it.
        ///
        /// On the end for the same reason MoveSpeedPercent is: an inserted column
        /// is a reordering and will not publish.
        /// </remarks>
        [SpacetimeDB.Default((byte)0)]
        public byte PatternGroups;

        /// <summary>
        /// Which bullet art this weapon's shots are drawn with. 0 is the plain blob.
        /// </summary>
        /// <remarks>
        /// An id, not a sprite. Art cannot travel through the database — it is not
        /// data the simulation has any use for, and shipping a texture would make
        /// changing one a publish. The client resolves it against
        /// <c>BulletSpriteCatalogue</c>, the same split enemies and loot bags use.
        ///
        /// Appended rather than grouped with Tint, which is where it belongs by
        /// meaning: inserting a column between two existing ones is a reorder to
        /// the database and needs a hand-written migration, while appending one
        /// with a default does not.
        /// </remarks>
        [SpacetimeDB.Default(0)]
        public byte BulletSpriteId;
    }

    /// <summary>
    /// One instance of something being hurt, broadcast rather than stored.
    /// </summary>
    /// <remarks>
    /// The enemy row already carries <c>LastDamage</c> and <c>LastHitAt</c>, and
    /// for a single bullet that is enough. It is not enough for a shotgun. Eight
    /// pellets land inside one reducer call, so they share one
    /// <see cref="ReducerContext.Timestamp"/> and each overwrite the same two
    /// columns; the client sees one row update carrying the *last* pellet's
    /// number and draws one small hit for what was really eight. The volley is
    /// invisible in exactly the case it matters most.
    ///
    /// An event table fixes that by construction: one row per hit, never stored,
    /// delivered to every subscriber's insert callback. Eight pellets are eight
    /// rows and eight numbers.
    ///
    /// It carries its own position rather than an enemy id alone, because the
    /// thing it is describing may already be dead — a killing pellet deletes the
    /// enemy row inside the same call, and a number that cannot be placed is a
    /// number that never appears on the shot that mattered.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Hit", Public = true, Event = true)]
    public partial struct Hit
    {
        /// <summary>Who was hurt. Zero for a player.</summary>
        public ulong EnemyId;

        /// <summary>Where to draw the number, in tiles.</summary>
        public float X;
        public float Y;

        /// <summary>Damage after resistance and phase mitigation — what was really taken.</summary>
        public ushort Amount;

        public bool Crit;
        public byte Element;

        /// <summary>Which zone it happened in, so a client can ignore other zones' numbers.</summary>
        [SpacetimeDB.Default(1u)]
        public uint ZoneId;
    }

    /// <summary>
    /// A projectile: written once, never updated.
    /// </summary>
    /// <remarks>
    /// The row records where and when a shot started and which way it went.
    /// Position is a function of elapsed time — <c>origin + dir * speed * t</c> —
    /// evaluated independently by every client, so a bullet costs one insert and
    /// one delete no matter how long it flies. Moving projectiles by updating
    /// rows every tick is what makes bullet-hell games expensive to network.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Shot", Public = true)]
    public partial struct Shot
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        /// <summary>Who fired it. Meaningless for enemy shots; read Faction first.</summary>
        public Identity Owner;

        /// <summary>0 fired by a player, 1 fired by an enemy. Decides what it can hit.</summary>
        public byte Faction;

        public float OriginX;
        public float OriginY;

        /// <summary>Unit length, normalised when the shot was created.</summary>
        public float DirX;
        public float DirY;

        public Timestamp SpawnedAt;

        /// <summary>Tiles per second, copied from the weapon so the shot is self-contained.</summary>
        public float Speed;

        /// <summary>Rolled at spawn. Nothing takes damage yet; this is what will be applied.</summary>
        public ushort Damage;

        /// <summary>0 physical, 1 fire, 2 ice, 3 poison, 4 arcane.</summary>
        public byte Element;

        /// <summary>0 none, 1 stun, 2 slow. Applied on hit.</summary>
        public byte DebuffKind;

        /// <summary>How long the debuff lasts, in seconds.</summary>
        public float DebuffSeconds;

        public float Size;

        /// <summary>Milliseconds before this shot is removed.</summary>
        public ushort LifetimeMs;

        /// <summary>Rolled a critical. Carried so the client can show it differently.</summary>
        public bool Crit;

        // The path is still a function of time — these only change its shape, not
        // the fact that the row is written once and never updated.

        /// <summary>Sideways travel, in tiles. 0 is a straight line.</summary>
        public float WaveAmplitude;

        /// <summary>Oscillations per second.</summary>
        public float WaveFrequency;

        /// <summary>Where in its oscillation this projectile starts, in radians.</summary>
        public float WavePhase;

        /// <summary>Colour to draw it, packed 0xRRGGBB.</summary>
        /// <remarks>
        /// On the row like speed, size and element, so a projectile keeps the
        /// look it was fired with even if the weapon is retuned mid-flight — and
        /// so one volley can contain bullets of different colours.
        /// </remarks>
        [SpacetimeDB.Default(0u)]
        public uint Tint;


        /// <summary>Bullet art id, resolved client-side. 0 is the plain blob.</summary>
        [SpacetimeDB.Default(0)]
        public byte SpriteId;

        /// <summary>Which zone this row belongs to. See <see cref="Zone"/>.</summary>
        [SpacetimeDB.Default(1u)]
        [SpacetimeDB.Index.BTree]
        public uint ZoneId;
    }

    /// <summary>
    /// A target that does nothing but take hits.
    /// </summary>
    /// <remarks>
    /// Deliberately not an enemy: no movement, no AI, no death. It exists to make
    /// collision observable on its own, before anything else can be blamed for
    /// what it looks like.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Dummy", Public = true)]
    public partial struct Dummy
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        public float X;
        public float Y;

        /// <summary>Collision radius, in tiles.</summary>
        public float Radius;

        public ushort Hp;
        public ushort MaxHp;

        /// <summary>When it was last hit, so clients can flash it. Never cleared.</summary>
        public Timestamp LastHitAt;

        /// <summary>Damage taken from the last hit, for a number to float up.</summary>
        public ushort LastDamage;

        /// <summary>Which zone this row belongs to. See <see cref="Zone"/>.</summary>
        [SpacetimeDB.Default(1u)]
        [SpacetimeDB.Index.BTree]
        public uint ZoneId;
    }

    /// <summary>
    /// An enemy archetype. Authored in Unity, pushed here.
    /// </summary>
    /// <remarks>
    /// Weapons are referenced by catalogue id rather than duplicated, so an enemy
    /// fires through exactly the same code and patterns a player does. A separate
    /// enemy-weapon system would be a second place for spread and helix to be
    /// implemented, and a second place for them to drift.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "EnemyDef", Public = true)]
    public partial struct EnemyDef
    {
        [PrimaryKey]
        public ushort Id;

        public string Name;

        public ushort MaxHp;
        public float Radius;

        /// <summary>Tiles per second at full tilt.</summary>
        public float Speed;

        /// <summary>See MoveEnemy. 0 static, 1 wander, 2 chase, 3 orbit, 4 keep-distance.</summary>
        public byte Behaviour;

        /// <summary>How far it can notice a player, in tiles.</summary>
        public float AggroRange;

        /// <summary>Range it tries to hold, for orbit and keep-distance.</summary>
        public float PreferredRange;

        /// <summary>
        /// What an orbit circles: 0 the player, 1 the point it spawned at.
        /// </summary>
        /// <remarks>
        /// A parameter rather than a second behaviour, because the motion is
        /// identical and only the anchor differs. Circling its spawn point is what
        /// a boss holding an arena does; circling the player is what a harrying
        /// mob does, and they are the same maths.
        /// </remarks>
        public byte OrbitPivot;

        /// <summary>
        /// How close a player must be before it opens fire, in tiles.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="AggroRange"/>, which is about movement. A
        /// turret has no aggro range at all — it never moves — but still needs to
        /// know when to shoot, and tying the two together made every armed mob
        /// need a movement behaviour that chases.
        ///
        /// It lives here rather than on the weapon because a weapon is shared with
        /// players, for whom "range at which I start shooting" means nothing.
        /// </remarks>
        public float AttackRange;

        /// <summary>Weapon catalogue id, or 0 for a melee-only enemy that never fires.</summary>
        public ushort WeaponId;

        /// <summary>Contact damage per second while overlapping a player. 0 for none.</summary>
        public ushort TouchDamage;

        /// <summary>Worth a health bar on screen. Grunts are not.</summary>
        public bool IsBoss;

        /// <summary>
        /// Packed 0xRRGGBB, as authored on the enemy asset.
        /// </summary>
        /// <remarks>
        /// Held here rather than left to the client so that every player sees the
        /// same enemy. A colour each client picked for itself would be a second
        /// answer to "what is this thing", and the two would diverge the moment
        /// anyone edited their scene.
        ///
        /// There is no "unset" value: black is a real colour an asset may ask
        /// for. The renderer falls back only when the row itself has not arrived,
        /// which is a different thing and is knowable.
        /// </remarks>
        public uint Colour;

        /// <summary>
        /// A second resource, shown as its own bar. 0 hides it.
        /// </summary>
        /// <remarks>
        /// Replicated and authorable, but nothing spends or regenerates it yet —
        /// a boss sits at full energy. It exists so the bar binds to a real value
        /// rather than a placeholder, and so whatever mechanic eventually uses it
        /// has somewhere to live.
        /// </remarks>
        public ushort MaxEnergy;
        /// <summary>
        /// Damage multiplier per element, as a percentage. 100 is neutral.
        /// </summary>
        /// <remarks>
        /// Named by element rather than positional, so a row dump says what it
        /// means and an entry cannot silently shift when a new element is added.
        /// Until now that byte was pure bookkeeping — it fed the stats tables and
        /// changed no damage at all, so an ice gun and a fire gun were the same
        /// gun with different numbers on a chart.
        ///
        /// A percentage rather than a float so the intent reads at a glance in a
        /// row dump: 50 is resistant, 200 is weak. An empty list is neutral to
        /// everything, which is what every enemy authored before this was.
        /// </remarks>
        public List<Resistance> Resist;

    }

    /// <summary>
    /// The stats every player starts with. One row, id 1.
    /// </summary>
    /// <remarks>
    /// Read at the point of use rather than copied onto each player, so retuning
    /// during play changes everyone immediately — the same live-tuning the weapon
    /// catalogue gives. The exception is <see cref="MaxHp"/>, which is mirrored
    /// onto the player row so a client can draw a health bar without joining two
    /// tables, and is kept in step by the tick.
    ///
    /// Every one of these has to live here rather than on the client: the server
    /// decides movement, damage and rate of fire, so a client-side speed or
    /// defence would be a number nothing reads.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "PlayerConfig", Public = true)]
    public partial struct PlayerConfig
    {
        [PrimaryKey]
        public byte Id;

        public ushort MaxHp;

        /// <summary>Subtracted from each incoming hit, down to a floor of 1.</summary>
        public ushort Defense;

        /// <summary>Tiles per second.</summary>
        public float Speed;

        /// <summary>Attack speed. 1 is the weapon's own rate, 2 is twice as fast.</summary>
        public float Dexterity;

        /// <summary>Health per second, accumulated fractionally.</summary>
        public float HpRegenPerSec;

        /// <summary>0 to 1.</summary>
        public float CritChance;

        /// <summary>Damage multiplier on a crit.</summary>
        public float CritMultiplier;

        /// <summary>
        /// Weapon every new character starts holding. 0 for none.
        /// </summary>
        /// <remarks>
        /// A player with an empty inventory holds nothing and therefore fires
        /// nothing, which with permadeath means a fresh character cannot kill
        /// anything, cannot loot, and cannot ever obtain a weapon. Granting one is
        /// what makes the first run possible at all.
        /// </remarks>
        [SpacetimeDB.Default(0u)]
        public ushort StartingWeaponId;
    }

    /// <summary>
    /// A region that keeps itself populated.
    /// </summary>
    /// <remarks>
    /// Authored as a scene object in Unity and pushed here, because spawning has
    /// to be the server's decision: a client that spawned its own enemies would
    /// see a different world from everyone else, and could make as many as it
    /// liked.
    ///
    /// <see cref="NextSpawnAt"/> is runtime state living on a configuration row.
    /// That is deliberate — one row per spawner is simpler to reason about than a
    /// config table shadowed by a state table, and the editor overwrites it on
    /// every push anyway.
    /// </remarks>
    /// <summary>Tiles per chunk, per axis.</summary>
    private const int ChunkSize = 16;

    /// <summary>What one tile is.</summary>
    /// <remarks>
    /// Three bytes per tile, so a 16x16 chunk is 768 bytes and a whole 40x40 area
    /// is nine rows. Storing a row per tile would be 1,600 rows for an area this
    /// small and would not survive a real map.
    /// </remarks>
    [SpacetimeDB.Type]
    public partial struct TileData
    {
        /// <summary>Bit 0 blocks movement, bit 1 blocks projectiles.</summary>
        public byte Flags;

        /// <summary>Relative likelihood of an enemy appearing here. 0 forbids it.</summary>
        public byte SpawnWeight;

        /// <summary>Damage per second while standing on it. 0 is safe.</summary>
        public byte Hazard;

        /// <summary>
        /// Which biome this tile belongs to.
        /// </summary>
        /// <remarks>
        /// Carried per tile rather than derived, because the client needs
        /// something to colour by and the spawner needs to know which biome a
        /// position is in. Without it a renderer can only draw "solid or not",
        /// which is not a map.
        /// </remarks>
        public byte Biome;
    }

    /// <summary>Where players appear. One row, id 1.</summary>
    /// <remarks>
    /// Pushed with the terrain rather than with the player stat block, because it
    /// is a property of the map: a spawn point only means anything relative to
    /// the ground it stands on.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "WorldSpawn", Public = true)]
    public partial struct WorldSpawn
    {
        [PrimaryKey]
        public byte Id;

        public float X;
        public float Y;
    }

    /// <summary>A square of terrain, keyed by packed chunk coordinates.</summary>
    [SpacetimeDB.Table(Accessor = "TerrainChunk", Public = true)]
    public partial struct TerrainChunk
    {
        [PrimaryKey]
        public uint Cell;

        /// <summary>ChunkSize * ChunkSize tiles, row-major from the chunk's bottom-left.</summary>
        public List<TileData> Tiles;
    }

    /// <summary>One entry in an area's enemy mix.</summary>
    /// <summary>
    /// One bullet's identity, so a single volley can contain several kinds.
    /// </summary>
    /// <remarks>
    /// A profile is <em>what the bullet is</em> — element, on-hit effect, damage,
    /// speed, size, lifetime. Wave and spin stay on the weapon because they are
    /// <em>how the volley moves</em>: every projectile in a volley shares them, and
    /// a per-slot wave would make the client's helix-without-wave warning a lie.
    ///
    /// This costs the <c>Shot</c> table nothing. A shot row already carries every
    /// one of these fields per projectile, because a shot has always been written
    /// once and never read back against its weapon — so mixing bullet types is
    /// only a question of what <c>FireVolley</c> writes into each row.
    /// </remarks>
    [SpacetimeDB.Type]
    public partial struct BulletProfile
    {
        /// <summary>0 physical, 1 fire, 2 ice, 3 poison, 4 arcane.</summary>
        public byte Element;

        /// <summary>0 none, 1 stun, 2 slow.</summary>
        public byte DebuffKind;

        public float DebuffSeconds;

        public ushort DamageMin;
        public ushort DamageMax;

        public float Speed;
        public float Size;
        public ushort LifetimeMs;

        /// <summary>
        /// Colour and bullet art together, packed 0xSSRRGGBB.
        /// </summary>
        /// <remarks>
        /// The low 24 bits are the colour, as everywhere else. The top 8 are this
        /// bullet's sprite id, and 0 there means "use the weapon's" — the one
        /// thing in a profile that inherits, because most mixes want one look for
        /// the whole volley and making every variant restate it would mean a
        /// weapon could not be re-dressed in one place.
        ///
        /// Two values in one column because this struct is stored *inside* the
        /// <c>profiles</c> column of <c>weapon_def</c>. A nested type cannot gain
        /// a field with a default the way a table can: the database sees it as
        /// changing a column's type and demands a hand-written migration or a
        /// wipe of every character in the world. The high byte was already unused
        /// and already masked off on the way in, so nothing had to be given up to
        /// find the room.
        ///
        /// Unpacked at the moment a shot is fired. <c>Shot</c> carries a real
        /// <c>SpriteId</c> column and a clean <c>Tint</c>, so this packing stops
        /// here and nothing downstream has to know about it.
        ///
        /// The colour itself is still not inherited, for the same reason no other
        /// field here falls back: "not filled in" and "deliberately dark" have to
        /// look different.
        /// </remarks>
        public uint Tint;

        /// <summary>The colour half of <see cref="Tint"/>.</summary>
        public uint Colour => Tint & 0xFFFFFFu;

        /// <summary>The bullet art half of <see cref="Tint"/>. 0 inherits.</summary>
        public byte SpriteId => (byte)(Tint >> 24);
    }

    [SpacetimeDB.Type]
    public partial struct AreaEntry
    {
        public ushort EnemyDefId;

        /// <summary>Relative likelihood against the other entries. 0 disables it.</summary>
        public ushort Weight;

        /// <summary>
        /// Cap on how many of this one may be alive at once. 0 means no cap.
        /// </summary>
        /// <remarks>
        /// Weights alone give ratios, not limits — over time a 1-in-20 elite still
        /// ends up as a crowd of elites if nothing stops it. This is what makes
        /// "mostly grunts, at most one champion" expressible.
        /// </remarks>
        public ushort MaxAlive;
    }

    [SpacetimeDB.Table(Accessor = "Spawner", Public = true)]
    public partial struct Spawner
    {
        [PrimaryKey]
        public ushort Id;

        /// <summary>The area's enemy mix. Empty means this spawner does nothing.</summary>
        public List<AreaEntry> Composition;

        public float X;
        public float Y;

        /// <summary>Enemies appear at a random point within this, in tiles.</summary>
        public float Radius;

        /// <summary>Population this spawner maintains.</summary>
        public ushort MaxAlive;

        /// <summary>Milliseconds between spawns while below the cap.</summary>
        public ushort IntervalMs;

        public Timestamp NextSpawnAt;

        /// <summary>
        /// <see cref="SourceEditor"/> or <see cref="SourceGenerated"/>.
        /// </summary>
        /// <remarks>
        /// Without this the editor's push-on-connect deletes the realm's whole
        /// population every time anybody presses Play, because it clears every
        /// spawner before rewriting the ones in the open scene. The two kinds
        /// have to be told apart to coexist.
        /// </remarks>
        public byte Source;

        /// <summary>
        /// The biome this spawner belongs to, or 255 for "anywhere".
        /// </summary>
        /// <remarks>
        /// A generated spawner now covers a whole region rather than a small
        /// circle inside one, and a circle big enough to cover a region overlaps
        /// its neighbours. Without this a forest would seed its wolves into the
        /// tundra next door, which reads as the biome mix being broken rather
        /// than as a radius being generous.
        ///
        /// It confines by biome, not by region: two adjacent regions of the same
        /// biome can still borrow each other's ground. That is deliberate — they
        /// have the same mix and the same rules, so the only thing it changes is
        /// which of two identical spawners a given enemy is counted against.
        ///
        /// 255 rather than 0 for "unset": 0 is water, a real biome id, and an
        /// editor-placed spawner that meant "anywhere" would confine itself to
        /// the sea.
        /// </remarks>
        [SpacetimeDB.Default((byte)255)]
        public byte Biome;

        /// <summary>Which zone this row belongs to. See <see cref="Zone"/>.</summary>
        [SpacetimeDB.Default(1u)]
        [SpacetimeDB.Index.BTree]
        public uint ZoneId;
    }

    /// <summary>A spawner not tied to any biome.</summary>
    public const byte AnyBiome = 255;

    /// <summary>Pushed from a scene object, and owned by the editor.</summary>
    public const byte SourceEditor = 0;

    /// <summary>Placed by realm generation, and owned by the generator.</summary>
    public const byte SourceGenerated = 1;

    /// <summary>
    /// Damage dealt to one live target, split by who did it and how.
    /// </summary>
    /// <remarks>
    /// Aggregated per hit rather than logged per hit. A full event stream would
    /// be the more flexible thing, but every insert here replicates to every
    /// subscriber, and a bullet-hell fight produces thousands of hits a minute —
    /// enough that the analytics would cost more bandwidth than the game.
    /// Totals answer the questions actually being asked ("who did what, with
    /// what") at a few rows per fight.
    ///
    /// Rows live only as long as their target and are deleted with it, after
    /// being rolled into the lifetime totals. That keeps the live table
    /// proportional to what is currently being fought rather than to everything
    /// that has ever been fought.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "DamageTally", Public = true)]
    public partial struct DamageTally
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        [SpacetimeDB.Index.BTree]
        public ulong EnemyId;

        public Identity Attacker;

        /// <summary>0 physical, 1 fire, 2 ice, 3 poison, 4 arcane.</summary>
        public byte Element;

        public ulong Damage;
        public ulong Hits;
        public ulong Crits;

        /// <summary>Largest single hit, which is the number players actually quote.</summary>
        public ushort Best;
    }

    /// <summary>
    /// How long a target spent under a debuff, and who is responsible.
    /// </summary>
    /// <remarks>
    /// Contribution is measured in *seconds actually applied*, not in times
    /// applied. Re-stunning an already-stunned target adds nothing, and counting
    /// applications would credit the player who fired into an existing stun
    /// exactly as much as the one who started it.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "DebuffTally", Public = true)]
    public partial struct DebuffTally
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        [SpacetimeDB.Index.BTree]
        public ulong EnemyId;

        public Identity Attacker;

        /// <summary>0 stun, 1 slow.</summary>
        public byte Kind;

        public float Seconds;
        public ulong Applications;
    }

    /// <summary>A player's lifetime totals, one row per element.</summary>
    [SpacetimeDB.Table(Accessor = "PlayerStat", Public = true)]
    public partial struct PlayerStat
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        [SpacetimeDB.Index.BTree]
        public Identity Identity;

        public byte Element;

        public ulong Damage;
        public ulong Hits;
        public ulong Crits;
        public ulong Kills;
        public ushort Best;

        /// <summary>Seconds of debuff this player has applied, all kinds.</summary>
        public float DebuffSeconds;
    }

    /// <summary>
    /// One thing an enemy may drop, and how often.
    /// </summary>
    /// <remarks>
    /// Chances are rolled independently rather than as one weighted pick, so a
    /// kill can drop nothing, one thing, or everything. A weighted table always
    /// drops exactly one item, which is a different and much duller feel.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "ItemDef", Public = true)]
    public partial struct ItemDef
    {
        [PrimaryKey]
        public ushort Id;

        public string Name;

        /// <summary>
        /// 0 weapon, 1 armor, 2 jewellery, 3 consumable.
        /// </summary>
        /// <remarks>
        /// Also the top two bits of <see cref="Id"/>, which is what keeps the four
        /// kinds from colliding while each numbers itself from 1. Kept as a column
        /// as well so nothing downstream has to know the packing to ask what kind
        /// an item is; the upsert refuses a row where the two disagree.
        /// </remarks>
        public byte Kind;

        /// <summary>How many fit in one inventory slot. 1 does not stack.</summary>
        public ushort MaxStack;

        /// <summary>
        /// Footprint in vault cells.
        /// </summary>
        /// <remarks>
        /// The vault is a grid and things take up the room they look like they
        /// take up. The carried pack is still one item per slot — a backpack
        /// where a sword costs four slots and a ring costs one is a different
        /// game from the six-slot one that was chosen, and mixing the two would
        /// mean two answers to "does this fit".
        ///
        /// Not rotatable. Rotation is a flag per placement and a swap of these
        /// two numbers; it is left out until the grid is something people have
        /// actually packed.
        /// </remarks>
        [SpacetimeDB.Default((byte)1)]
        public byte Width;

        [SpacetimeDB.Default((byte)1)]
        public byte Height;

        /// <summary>Display colour, packed 0xRRGGBB.</summary>
        public uint Tint;

        /// <summary>
        /// Rarity band. 0 is common.
        /// </summary>
        /// <remarks>
        /// Colours the item's tile wherever it is drawn. It does not decide a bag's
        /// kind — <c>LootDrop.BagKind</c> comes from the pool that rolled it, in
        /// <c>RecordKill</c> — though a later rule restricting who may open a bag
        /// could be based on the tiers inside it. It is authored per item rather than derived from stats,
        /// because "how rare is this" and "how strong is this" are different
        /// questions and a strong common is a thing a designer should be able to
        /// make.
        /// </remarks>
        public byte Tier;
    }

    /// <summary>One item sitting in a bag.</summary>
    [SpacetimeDB.Type]
    public partial struct BagItem
    {
        public ushort ItemId;
        public ushort Count;
    }

    /// <summary>A named pool of possible items, and the bag they arrive in.</summary>
    /// <remarks>
    /// Shared between enemies rather than owned by one, because "the common bag"
    /// is the same thing whichever enemy drops it, and a per-enemy copy would
    /// drift the moment one was edited.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "LootPoolDef", Public = false)]
    public partial struct LootPoolDef
    {
        [PrimaryKey]
        public ushort Id;

        public string Name;

        /// <summary>Which bag the client draws for a drop from this pool.</summary>
        /// <remarks>
        /// The server carries a number and nothing else. What that bag looks like
        /// is authored on the client, because a sprite is not something the
        /// simulation has any use for — and shipping one through the database
        /// would mean a texture change needed a publish.
        /// </remarks>
        public byte BagKind;
    }

    /// <summary>One possible item inside a pool.</summary>
    /// <remarks>
    /// Rolled independently of the other entries in its pool, so the chances do
    /// not have to add up to anything and adding an entry cannot quietly make the
    /// others rarer. The weighted choice happens one level up, between pools.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "LootEntry", Public = false)]
    public partial struct LootEntry
    {
        /// <summary>(PoolId &lt;&lt; 8) | Index.</summary>
        [PrimaryKey]
        public uint Key;

        [SpacetimeDB.Index.BTree]
        public ushort PoolId;

        public ushort ItemId;

        /// <summary>0 to 100.</summary>
        public float ChancePercent;

        /// <summary>How many drop when this entry hits.</summary>
        public ushort Count;
    }

    /// <summary>One pool an enemy can roll, and how likely it is against the others.</summary>
    /// <remarks>
    /// A weight rather than a percentage, so the entries are read against each
    /// other and cannot add up to more than certainty. <see cref="PoolId"/> 0 is
    /// the "nothing drops" outcome, which lives in the same table on purpose:
    /// with a separate drop-chance knob, working out how often a red bag actually
    /// appears means multiplying two numbers that are edited in different places.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "EnemyLoot", Public = false)]
    public partial struct EnemyLoot
    {
        /// <summary>(EnemyDefId &lt;&lt; 8) | Index.</summary>
        [PrimaryKey]
        public uint Key;

        [SpacetimeDB.Index.BTree]
        public ushort EnemyDefId;

        /// <summary>0 means this slice of the roll drops nothing.</summary>
        public ushort PoolId;

        public uint Weight;
    }

    /// <summary>
    /// One of an account's characters. This is the thing that dies.
    /// </summary>
    /// <remarks>
    /// Separate from <c>Player</c>, which is the avatar standing in the world.
    /// An account has several characters and plays one at a time, so the two are
    /// different questions: <c>Player</c> answers "where is this account right
    /// now", and this answers "who are they playing and what have they earned".
    ///
    /// Level, the passive tree and affinity belong here when they exist, because
    /// they die with the character. Anything that should outlive a death belongs
    /// on the account instead — the vault and the lifetime stats.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Character", Public = true)]
    public partial struct Character
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        [SpacetimeDB.Index.BTree]
        public Identity Account;

        public string Name;
        public Timestamp CreatedAt;

        /// <summary>
        /// Shown on the character screen. Nothing raises it yet.
        /// </summary>
        /// <remarks>
        /// Here rather than waiting for the levelling layer, because the select
        /// screen has to draw something and a hard-coded "1" in the UI would be a
        /// lie the day levels arrive.
        /// </remarks>
        [SpacetimeDB.Default((byte)1)]
        public byte Level;
    }

    /// <summary>
    /// One hitscan ray, drawn and then forgotten.
    /// </summary>
    /// <remarks>
    /// A hitscan shot has no position over time — it has already happened. So
    /// what replicates is not a thing in the world but a record that a line
    /// existed between two points, which clients draw for a moment and drop.
    ///
    /// The endpoint is where the ray actually stopped, decided by the server. A
    /// client drawing to where it thinks the ray ended would be drawing its own
    /// answer to what was hit, which is the question the server exists to settle.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Tracer", Public = true)]
    public partial struct Tracer
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        public Identity Owner;
        public float X1;
        public float Y1;
        public float X2;
        public float Y2;

        /// <summary>Packed 0xRRGGBB, from the weapon.</summary>
        public uint Tint;

        /// <summary>True when the ray stopped on something rather than in the air.</summary>
        public bool Hit;

        public Timestamp FiredAt;

        /// <summary>Which zone this row belongs to. See <see cref="Zone"/>.</summary>
        [SpacetimeDB.Default(1u)]
        [SpacetimeDB.Index.BTree]
        public uint ZoneId;
    }

    /// <summary>How much damage of one element a thing takes, as a percentage.</summary>
    /// <remarks>
    /// A declared type rather than a bare number in a positional list, because a
    /// list of primitives is not something this module's bindings carry — every
    /// list column here holds one of these. Naming the element is the better
    /// shape regardless: an entry cannot shift meaning when a new element is
    /// added.
    /// </remarks>
    [SpacetimeDB.Type]
    public partial struct Resistance
    {
        /// <summary>0 physical, 1 fire, 2 ice, 3 poison, 4 arcane.</summary>
        public byte Element;

        /// <summary>100 is neutral. 50 resists, 200 is a weakness.</summary>
        public ushort Percent;
    }

    /// <summary>
    /// One stack sitting at a position in some grid.
    /// </summary>
    /// <remarks>
    /// The same shape for every container there is — pack, secure pocket and
    /// vault are all grids and differ only in their dimensions, so they are all
    /// this. One placement implementation means an item cannot fit in one
    /// container by rules that another container disagrees with.
    ///
    /// Equipped is the exception and is a grid only in the sense of being
    /// addressed the same way: it is 3x1, <see cref="X"/> is the kind, and
    /// footprints are ignored. What you are wearing does not take up pack space,
    /// so a four-cell rifle in your hands costs nothing to carry.
    /// </remarks>
    [SpacetimeDB.Type]
    public partial struct GridItem
    {
        /// <summary>0 backpack, 1 equipped, 2 secure, 3 vault.</summary>
        public byte Container;

        /// <summary>Top-left cell. The footprint extends right and down.</summary>
        public byte X;
        public byte Y;

        public ushort ItemId;
        public ushort Count;
    }

    /// <summary>
    /// What one player is carrying.
    /// </summary>
    /// <remarks>
    /// One row per player holding every slot, rather than a row per slot. A slot
    /// move is then a single atomic update — swapping two slots as two row writes
    /// has an instant in between where an item exists twice or not at all, and a
    /// client subscribed to it would see that.
    ///
    /// Only occupied slots are stored. An empty slot is the absence of an entry,
    /// which means the list never has to be resized to match a UI that changed.
    ///
    /// Keyed by character, not by account. What you are carrying dies when you
    /// do, so it has to hang off the thing that dies — keyed by account it would
    /// quietly survive into the next character, which is the one property
    /// permadeath cannot have.
    ///
    /// Public. It replicates to everyone, which is a deliberate simplification:
    /// nothing here is secret yet, and a per-player subscription filter is a
    /// change to make when there is something worth hiding.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Inventory", Public = true)]
    public partial struct Inventory
    {
        [PrimaryKey]
        public ulong CharacterId;

        public List<GridItem> Slots;
    }

    /// <summary>
    /// What an account has banked, across every character it has had.
    /// </summary>
    /// <remarks>
    /// Keyed by identity, not by character. That is the whole point: characters
    /// die and the vault is what survives them, so it is the only progression a
    /// bad run cannot take away.
    ///
    /// A grid, not a pile. Where a thing sits is part of what the vault is: a
    /// bounded space you have to pack, so that keeping something has a cost
    /// measured in room rather than in a slot count. An earlier version of this
    /// was a pile, and the comment here said position meant nothing — it does now.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Vault", Public = true)]
    public partial struct Vault
    {
        [PrimaryKey]
        public Identity Identity;

        public List<GridItem> Items;
    }

    /// <summary>A cell in the vault grid, for addressing what is there.</summary>
    [SpacetimeDB.Type]
    public partial struct VaultCell
    {
        public byte X;
        public byte Y;
    }

    /// <summary>
    /// A bag on the ground: everything one kill dropped, in one place.
    /// </summary>
    /// <remarks>
    /// One bag per kill rather than one row per item. Several rows at identical
    /// coordinates cannot be walked over separately, so they would read as a
    /// single pile that only ever gives up one thing — and the player would have
    /// no way to see what else was in it.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "LootDrop", Public = true)]
    public partial struct LootDrop
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        public float X;
        public float Y;

        /// <summary>When it dropped. Expiry is measured from this.</summary>
        public Timestamp DroppedAt;

        /// <summary>
        /// Which bag this is, taken from the pool that produced it.
        /// </summary>
        /// <remarks>
        /// Comes from the pool rather than being worked out from the contents.
        /// Deriving it from the best item inside would mean a designer could not
        /// make a plain-looking bag that happens to hold something good, and the
        /// bag's appearance would change if an item's rarity were ever retuned.
        ///
        /// Also the field a later rule about who may open a bag will read.
        /// </remarks>
        public byte BagKind;

        public List<BagItem> Items;

        /// <summary>Which zone this row belongs to. See <see cref="Zone"/>.</summary>
        [SpacetimeDB.Default(1u)]
        [SpacetimeDB.Index.BTree]
        public uint ZoneId;
    }

    /// <summary>
    /// One stage of a phased enemy's fight.
    /// </summary>
    /// <remarks>
    /// A phase overrides how the enemy moves and what it fires. An archetype with
    /// no phases uses its own flat fields, so ordinary enemies are unaffected and
    /// a boss is the same kind of thing with a list attached rather than a
    /// separate concept.
    ///
    /// Deliberately data the server interprets, not code. This shape covers the
    /// reusable verbs — move like this, fire that, change when something is true.
    /// A boss that genuinely cannot be expressed here wants a script, and trying
    /// to bend this into one would produce a programming language in an inspector.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "PhaseDef", Public = true)]
    public partial struct PhaseDef
    {
        /// <summary>(EnemyDefId &lt;&lt; 8) | Index.</summary>
        [PrimaryKey]
        public uint Key;

        [SpacetimeDB.Index.BTree]
        public ushort EnemyDefId;

        public byte Index;

        public byte Behaviour;
        public float AggroRange;
        public float PreferredRange;
        public float AttackRange;
        public ushort WeaponId;

        /// <summary>What an orbit circles: 0 the player, 1 the point it spawned at.</summary>
        public byte OrbitPivot;

        /// <summary>
        /// Movement speed for this phase. 0 inherits the archetype's.
        /// </summary>
        /// <remarks>
        /// 0 rather than a nullable: a phase that simply does not care about speed
        /// should not have to restate it, and every real speed is above zero
        /// anyway. A genuinely motionless phase uses the Static behaviour.
        /// </remarks>
        public float Speed;

        /// <summary>Bit 0 invulnerable, bit 1 untargetable.</summary>
        public byte Flags;

        /// <summary>
        /// Damage this phase takes, as a percentage. 100 is normal, 50 is armoured.
        /// </summary>
        /// <remarks>
        /// A dial alongside the invulnerable flag rather than instead of it.
        /// Invulnerable says what a transition phase *is*; a percentage says how
        /// tough a fighting phase is, and reading "Invulnerable" in an inspector
        /// is clearer than spotting a zero in a number field.
        /// </remarks>
        public float DamageTakenPercent;

        /// <summary>0 never leaves, 1 health below Value percent, 2 after Value seconds.</summary>
        public byte TransitionKind;

        public float TransitionValue;
    }

    /// <summary>A live enemy.</summary>
    /// <remarks>
    /// Cells are looked up per zone. Two zones share cell coordinates, so a
    /// lookup by cell alone would hand a realm bullet the monster standing at the
    /// same spot in a dungeon.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "Enemy", Public = true)]
    [SpacetimeDB.Index.BTree(Accessor = "ZoneCell", Columns = new[] { nameof(ZoneId), nameof(Cell) })]
    public partial struct Enemy
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;

        public ushort DefId;

        /// <summary>Which spawner made it, or 0 if it was placed by hand.</summary>
        [SpacetimeDB.Index.BTree]
        public ushort SpawnerId;

        /// <summary>
        /// Coarse grid cell, so a tick can read only the enemies near somebody.
        /// </summary>
        /// <remarks>
        /// Packed as <c>(cellY &lt;&lt; 16) | cellX</c> rather than an index into a
        /// grid, so it does not have to be recomputed when the world is resized.
        ///
        /// Kept in step with X and Y by <see cref="Reposition"/>. A stale cell is
        /// the dangerous failure here: the enemy is still in the table, still
        /// collides, and is simply never simulated again — a monster frozen
        /// forever with nothing logged. Every write to a position goes through
        /// that helper for exactly that reason.
        /// </remarks>
        [SpacetimeDB.Index.BTree]
        public uint Cell;

        /// <summary>Cannot move or fire until this time.</summary>
        public Timestamp StunnedUntil;

        /// <summary>Moves at half speed until this time.</summary>
        public Timestamp SlowedUntil;

        /// <summary>Current phase, 0 when the archetype has none.</summary>
        public byte PhaseIndex;

        public Timestamp PhaseStartedAt;

        public float X;
        public float Y;

        /// <summary>Where it was placed. Orbit and wander are measured from here.</summary>
        public float HomeX;
        public float HomeY;

        public ushort Hp;

        public Timestamp NextShotAt;
        public Timestamp LastHitAt;
        public ushort LastDamage;

        /// <summary>Current energy. Nothing changes it yet.</summary>
        public ushort Energy;

        /// <summary>
        /// The player this enemy is currently engaging, or null.
        /// </summary>
        /// <remarks>
        /// Written by the server from the same range check that decides whether to
        /// fire, so "the boss has noticed me" has exactly one definition. A client
        /// measuring its own distance would be a second answer to that question,
        /// using a different number, and the two would disagree at the edges — a
        /// health bar appearing before the boss reacts, or a boss shooting with no
        /// bar on screen.
        /// </remarks>
        public Identity? Target;

        /// <summary>
        /// Its own phase, so identical enemies do not move in lockstep.
        /// </summary>
        /// <remarks>
        /// Rolled once at spawn. Without it a group of the same archetype orbits
        /// and wanders in perfect unison, which reads as one object rather than
        /// several.
        /// </remarks>
        public float Phase;

        /// <summary>
        /// Microseconds this enemy has spent actually engaged in its current phase.
        /// </summary>
        /// <remarks>
        /// Accumulated rather than measured from a start time, because neither
        /// wall-clock reading works. Elapsed-since-entry runs while the region is
        /// empty, so a boss nobody has visited wakes with every timed phase
        /// already expired and skips them one per tick. Restarting the clock each
        /// time a player re-engages fixes that but hands the player a way to keep
        /// a boss in its opening phase forever by stepping in and out of range.
        ///
        /// Accumulating only while engaged is the reading an author actually
        /// means by "five seconds": five seconds of being fought. Stepping away
        /// pauses it and does not rewind it.
        ///
        /// Only advanced for phases that end on a timer, so an ordinary enemy —
        /// which has no phases at all — never writes a row for this and the tick
        /// keeps its "write only what changed" property.
        /// </remarks>
        [SpacetimeDB.Default(0ul)]
        public ulong PhaseEngagedUs;

        /// <summary>Which zone this row belongs to. See <see cref="Zone"/>.</summary>
        [SpacetimeDB.Default(1u)]
        [SpacetimeDB.Index.BTree]
        public uint ZoneId;
    }

    /// <summary>Deletes expired shots. Nothing else is scheduled.</summary>
    /// <summary>
    /// How long the world tick is taking.
    /// </summary>
    /// <remarks>
    /// Private, so measuring does not itself replicate a row to every client
    /// twenty times a second — which would be the measurement changing the thing
    /// being measured. Read it with <c>scripts/vrox tick</c>.
    ///
    /// Totals as well as a maximum, because an average alone hides the tick that
    /// took 40ms and a maximum alone cannot tell a one-off from a trend.
    /// </remarks>
    [SpacetimeDB.Table(Accessor = "TickMetric", Public = false)]
    public partial struct TickMetric
    {
        [PrimaryKey]
        public byte Id;

        /// <summary>Microseconds since the previous tick began.</summary>
        /// <remarks>
        /// The interval, not the duration. There is no wall clock inside a
        /// module — <c>Stopwatch</c> is not monotonic here and <c>DateTime.UtcNow</c>
        /// is frozen for determinism, both verified — so a tick cannot time
        /// itself. What it can do is notice that the next one started late.
        ///
        /// That is enough to answer the question that matters: the scheduler asks
        /// for a tick on a fixed interval, so intervals that stay at the requested
        /// value mean the work fits inside it, and intervals that grow mean it
        /// does not. Drive the requested interval down with <c>SetTickInterval</c>
        /// and the point where they start growing is the tick's real cost.
        /// </remarks>
        public uint LastMicros;
        public uint MaxMicros;
        public ulong Ticks;
        public ulong TotalMicros;

        /// <summary>What the last tick was carrying, so a time has a size beside it.</summary>
        public int Enemies;
        public int Active;
        public int Shots;
        public int Players;

        /// <summary>Zones open when the metric was written, the realm included.</summary>
        [SpacetimeDB.Default(0)]
        public int Zones;

        /// <summary>Players in the most crowded zone — the one a single tick pass pays for.</summary>
        [SpacetimeDB.Default(0)]
        public int BusiestZone;
    }

    [SpacetimeDB.Table(Accessor = "ShotCleanup", Scheduled = nameof(CleanUpShots), ScheduledAt = nameof(ScheduledAt))]
    public partial struct ShotCleanup
    {
        [PrimaryKey]
        [AutoInc]
        public ulong ScheduledId;

        public ScheduleAt ScheduledAt;
    }

    [SpacetimeDB.Reducer(ReducerKind.Init)]
    public static void Init(ReducerContext ctx)
    {
        // Collision and cleanup share one timer. Two would mean two places where
        // the world advances, and two chances for them to disagree about when.
        ctx.Db.ShotCleanup.Insert(new ShotCleanup
        {
            ScheduledId = 0,
            ScheduledAt = new ScheduleAt.Interval(TimeSpan.FromMilliseconds(50)),
        });

        // A world exists from the first moment the module runs. Without this a
        // fresh database is an empty void until somebody remembers to call a
        // reducer, and "the map did not load" and "nobody generated one" look
        // identical from the client.
        EnsureRealmZone(ctx);
        BuildRealm(ctx, 1337u);

        // Dummies are placed against the map that was just generated, not at a
        // fixed offset from the centre: the centre of a generated realm is quite
        // often a lake, and a dummy inside solid ground cannot be shot.
        var (sx, sy) = SpawnPoint(ctx);
        int placed = 0;
        for (int i = 0; i < 5 && placed < 5; i++)
        {
            float radius = 0.4f + i * 0.15f;
            float x = sx - 8f + i * 4f;
            float y = sy + 8f;
            if (GroundOf(ctx, RealmLayout).Blocked(x, y))
            {
                continue;
            }
            ctx.Db.Dummy.Insert(new Dummy
            {
                Id = 0,
                X = x,
                Y = y,
                Radius = radius,
                Hp = 200,
                MaxHp = 200,
                LastHitAt = ctx.Timestamp,
                LastDamage = 0,
                ZoneId = RealmZone,
            });
            placed++;
        }
        Log.Info($"{placed} dummies placed");
    }

    /// <summary>
    /// Advances collision and removes finished shots.
    /// </summary>
    /// <remarks>
    /// Projectile rows are still written once and never updated — this reads
    /// them, it does not move them. What it does is close the gap that existed
    /// while only the client evaluated a path: the server now computes where a
    /// bullet is, and is therefore the authority on what it hit.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void CleanUpShots(ReducerContext ctx, ShotCleanup timer)
    {
        // Named for what it originally did. It is the world tick now — players,
        // enemies, spawners, bags and shots — and deliberately still the only
        // scheduled thing in the module, because a second timer would need a
        // defined order against this one and there is no way to express that.
        //
        // Logged when the picture changes, not on a timer. A heartbeat at twenty
        // lines a second is unreadable, and one every ten seconds misses anything
        // shorter than ten seconds — which is most of what you want to catch.
        int online = ctx.Db.Player.Iter().Count(p => p.Online);
        var snapshot = (ActiveEnemies, DormantEnemies, online);
        if (snapshot != _lastReported)
        {
            _lastReported = snapshot;
            Log.Info($"enemies: {ActiveEnemies} simulated, {DormantEnemies} dormant "
                   + $"| players {online}");
        }

        RefreshWorldSize(ctx);

        long nowUs = ctx.Timestamp.MicrosecondsSinceUnixEpoch;
        float step = TickSeconds;

        // Ids are collected before deleting: removing rows while iterating the
        // table that produced them skips entries.
        var finished = new List<ulong>();
        // CharacterId 0 is somebody sitting at the character screen. They have a
        // row and a position, but nobody is playing them — so nothing should
        // shoot at them, and they should not wake enemies.
        var players = ctx.Db.Player.Iter()
            .Where(p => p.Online && p.Hp > 0 && p.CharacterId != 0)
            .ToList();

        var config = Config(ctx);
        var catalogue = Catalogue.Read(ctx);

        AdvancePlayers(ctx, config);
        ExpireBags(ctx, nowUs);
        ExpireTracers(ctx, nowUs);
        ExpirePortals(ctx, nowUs);

        // One pass per zone. Enemies, dummies, spawners and shots are all read
        // through that zone's index, so a dungeon's bullets cannot touch the
        // realm's monsters.
        //
        // The realm is simulated whether or not anyone is in it, as the single
        // world always was: spawners refill and shots keep flying. A dungeon with
        // nobody inside is not simulated at all, and is closed after a grace
        // period — so an abandoned run costs one skipped iteration, then nothing.
        EnsureRealmZone(ctx);
        var byZone = players.GroupBy(p => p.ZoneId).ToDictionary(g => g.Key, g => g.ToList());
        int simulated = 0, active = 0, dormant = 0, shotsSeen = 0, playersSeen = 0;
        int zones = 0, busiest = 0;
        foreach (var zone in ctx.Db.Zone.Iter().ToList())
        {
            var here = byZone.TryGetValue(zone.Id, out var inZone) ? inZone : new List<Player>();
            playersSeen += here.Count;

            if (zone.Kind == ZoneDungeon)
            {
                var row = zone;
                if (here.Count > 0 && row.EmptySinceUs != 0)
                {
                    row.EmptySinceUs = 0;
                    ctx.Db.Zone.Id.Update(row);
                }
                else if (here.Count == 0 && row.EmptySinceUs == 0)
                {
                    row.EmptySinceUs = (ulong)nowUs;
                    ctx.Db.Zone.Id.Update(row);
                }

                bool expired = row.ClosesAtUs != 0 && (ulong)nowUs >= row.ClosesAtUs;
                bool abandoned = here.Count == 0 && row.EmptySinceUs != 0
                    && nowUs - (long)row.EmptySinceUs >= (long)(EmptyZoneGraceSeconds * 1_000_000f);
                if (expired || abandoned)
                {
                    CloseZone(ctx, row, expired ? "its time ran out" : "nobody came back");
                    continue;
                }
                if (here.Count == 0)
                {
                    // Counted, or the orphan check below would report this run's
                    // leftover shots as belonging to no zone.
                    shotsSeen += ctx.Db.Shot.ZoneId.Filter(zone.Id).Count();
                    zones++;
                    continue;
                }
            }

            zones++;
            busiest = Math.Max(busiest, here.Count);

            // Only the enemies anybody could interact with this tick. Everything past
            // that would have been read, skipped as dormant, and thrown away — which
            // measured at ~29us each, so a populated map spent most of the tick
            // deciding to do nothing.
            var enemies = NearbyEnemies(ctx, zone.Id, here, catalogue);
            var (zoneActive, zoneDormant) = AdvanceEnemies(ctx, enemies, here, nowUs, catalogue, ZoneGround(ctx, zone.Id));
            active += zoneActive;
            dormant += zoneDormant;
            RunSpawners(ctx, zone.Id, enemies, nowUs);

            var dummies = ctx.Db.Dummy.ZoneId.Filter(zone.Id).ToList();
            shotsSeen += CollideShots(ctx, zone.Id, catalogue, enemies, here, dummies,
                                      nowUs, step, finished);
            simulated += enemies.Count;
        }

        ActiveEnemies = active;
        ZonesLive = zones;
        BusiestZonePlayers = busiest;

        // Everything the cell filter never read counts as dormant too, otherwise
        // the saving this exists for would not show up in the number that
        // measures it.
        DormantEnemies = dormant + ((int)ctx.Db.Enemy.Count - simulated);

        // A row whose zone has no Zone row is never simulated: a shot that never
        // expires, a player nothing can hit. Counted and said out loud, because
        // from the client it would look like the game had quietly stopped.
        var orphans = ((int)ctx.Db.Shot.Count - shotsSeen, players.Count - playersSeen);
        if (orphans != _lastOrphans)
        {
            _lastOrphans = orphans;
            if (orphans != (0, 0))
            {
                Log.Warn($"{orphans.Item1} shot(s) and {orphans.Item2} player(s) are in a "
                       + "zone that does not exist, and are not being simulated");
            }
        }

        foreach (ulong id in finished)
        {
            ctx.Db.Shot.Id.Delete(id);
        }

        RecordTick(ctx, nowUs, simulated, players.Count);
    }

    private static (int, int) _lastOrphans;

    /// <summary>Zones open, and players in the busiest, as of the last tick. For the metric row.</summary>
    private static int ZonesLive, BusiestZonePlayers;

    /// <summary>
    /// Resolves one zone's shots against that zone's targets.
    /// </summary>
    /// <remarks>
    /// Split out of the tick when zones arrived, otherwise unchanged. Every list
    /// passed in must belong to <paramref name="zone"/>; mixing them is exactly
    /// how a realm bullet would hit something standing in a dungeon.
    ///
    /// Returns how many shots it looked at, so the tick can notice rows that no
    /// zone ever reached.
    /// </remarks>
    private static int CollideShots(ReducerContext ctx, uint zone, Catalogue catalogue,
                                    List<Enemy> enemies, List<Player> players,
                                    List<Dummy> dummies, long nowUs, float step,
                                    List<ulong> finished)
    {
        // Hoisted out of the shot loop below. Both are per-enemy and constant for
        // the whole tick, but they sat in the innermost scope — recomputed once
        // per shot per candidate enemy, so their cost grew with the product of
        // the two rather than with either.
        //
        // The radius was the worse of the pair: a database index lookup, which
        // is the exact thing the comment on the phase check argues against, for
        // a def the catalogue snapshot already had in hand. A ring weapon firing
        // into a crowd made that thousands of index finds a tick, and a tick that
        // overruns is what the player actually feels — position is replicated,
        // not predicted, so a stretched tick arrives as a stutter in their own
        // movement.
        int enemyCount = enemies.Count;
        var enemyRadius = new float[enemyCount];
        var enemyUntargetable = new bool[enemyCount];
        for (int i = 0; i < enemyCount; i++)
        {
            if (catalogue.Enemies.TryGetValue(enemies[i].DefId, out var ed))
            {
                enemyRadius[i] = ed.Radius;
                // Untargetable phases let shots pass through entirely, so a boss
                // can retreat behind its minions without soaking every bullet
                // aimed at them.
                enemyUntargetable[i] = (CurrentPhase(catalogue, enemies[i], ed).Flags & 2) != 0;
            }
            else
            {
                enemyRadius[i] = 0.5f;
            }
        }

        int seen = 0;
        var ground = ZoneGround(ctx, zone);
        foreach (var shot in ctx.Db.Shot.ZoneId.Filter(zone).ToList())
        {
            seen++;
            float age = (nowUs - shot.SpawnedAt.MicrosecondsSinceUnixEpoch) / 1_000_000f;
            if (age * 1000f > shot.LifetimeMs)
            {
                finished.Add(shot.Id);
                continue;
            }

            // The segment covered since the last tick, not the point it is at
            // now. At 14 tiles a second a shot moves 0.7 tiles per tick, so a
            // point test would pass straight through anything smaller than that
            // and hits would look random.
            float previous = age - step;
            if (previous < 0f)
            {
                previous = 0f;
            }
            var (ax, ay) = ShotPositionAt(shot, previous);
            var (bx, by) = ShotPositionAt(shot, age);

            // Walls first. A shot that has already buried itself in rock should
            // not go on to hit whatever is standing on the other side of it.
            if (ground.HitsWall(ax, ay, bx, by))
            {
                finished.Add(shot.Id);
                continue;
            }

            // A shot only hits the other side. Without this an enemy volley
            // wipes out the enemies standing behind it.
            if (shot.Faction == 0)
            {
                bool hit = false;
                foreach (var dummy in dummies)
                {
                    if (SegmentHitsCircle(ax, ay, bx, by, dummy.X, dummy.Y, dummy.Radius + shot.Size * 0.5f))
                    {
                        DamageDummy(ctx, dummy, shot.Damage);
                        hit = true;
                        break;
                    }
                }
                if (!hit)
                {
                    for (int i = 0; i < enemyCount; i++)
                    {
                        var enemy = enemies[i];
                        if (enemy.Hp == 0 || enemyUntargetable[i])
                        {
                            continue;
                        }
                        float radius = enemyRadius[i];
                        if (SegmentHitsCircle(ax, ay, bx, by, enemy.X, enemy.Y, radius + shot.Size * 0.5f))
                        {
                            // The local copy is updated too: several shots can
                            // land in one tick, and re-reading a stale list would
                            // let a dead enemy absorb the rest of the volley.
                            enemies[i] = DamageEnemy(ctx, catalogue, enemy, shot, shot.Owner);
                            hit = true;
                            break;
                        }
                    }
                }
                if (hit)
                {
                    // One projectile hits one thing. Without this a shot passing
                    // through overlapping targets would damage each of them.
                    finished.Add(shot.Id);
                }
            }
            else
            {
                foreach (var player in players)
                {
                    if (SegmentHitsCircle(ax, ay, bx, by, player.X, player.Y, PlayerRadius + shot.Size * 0.5f))
                    {
                        DamagePlayer(ctx, player, shot.Damage,
                                     shot.DebuffKind, shot.DebuffSeconds);
                        finished.Add(shot.Id);
                        break;
                    }
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Banks how long the tick took.
    /// </summary>
    /// <remarks>
    /// Measured around the work and written afterwards, so the write is not
    /// inside the span it reports. The row write is the one part of the tick this
    /// cannot account for, and it is a single small upsert.
    ///
    /// Shots are counted from the table rather than carried through the tick,
    /// because the interesting number is what was there when the work happened,
    /// and the ones deleted this tick were part of that work.
    /// </remarks>
    private static long _prevStampUs;
    private static uint _pendingMax;
    private static ulong _pendingTicks;
    private static ulong _pendingMicros;

    /// <summary>How many ticks are accumulated before the metric row is written.</summary>
    /// <remarks>
    /// The row write is not free, and at a benchmarking interval of 1ms it was a
    /// thousand writes a second spent measuring rather than simulating — the
    /// instrument changing what it measured. Accumulating in statics and flushing
    /// twice a second costs the same information and almost none of the time.
    ///
    /// Statics survive between reducer calls in the same module instance, which
    /// is how <c>ActiveEnemies</c> already works. A restart loses at most half a
    /// second of samples.
    /// </remarks>
    private const int MetricFlushTicks = 20;

    private static void RecordTick(ReducerContext ctx, long nowUs, int enemies, int players)
    {
        if (_prevStampUs != 0)
        {
            uint micros = (uint)Math.Clamp(nowUs - _prevStampUs, 0, uint.MaxValue);
            _pendingTicks += 1;
            _pendingMicros += micros;
            if (micros > _pendingMax)
            {
                _pendingMax = micros;
            }
        }
        _prevStampUs = nowUs;

        if (_pendingTicks < MetricFlushTicks)
        {
            return;
        }

        var metric = ctx.Db.TickMetric.Id.Find(1);
        var row = metric ?? new TickMetric { Id = 1 };

        row.LastMicros = (uint)(_pendingMicros / _pendingTicks);
        row.MaxMicros = _pendingMax > row.MaxMicros ? _pendingMax : row.MaxMicros;
        row.Ticks += _pendingTicks;
        row.TotalMicros += _pendingMicros;
        row.Enemies = enemies;
        row.Active = ActiveEnemies;
        row.Shots = (int)ctx.Db.Shot.Count;
        row.Players = players;
        row.Zones = ZonesLive;
        row.BusiestZone = BusiestZonePlayers;

        if (metric is null)
        {
            ctx.Db.TickMetric.Insert(row);
        }
        else
        {
            ctx.Db.TickMetric.Id.Update(row);
        }

        _pendingTicks = 0;
        _pendingMicros = 0;
        _pendingMax = 0;
    }

    /// <summary>
    /// Changes how often the world tick is asked to run. For benchmarking.
    /// </summary>
    /// <remarks>
    /// The only way to find out how long the tick takes, given a module cannot
    /// read a clock: ask for it more often until it can no longer keep up.
    ///
    /// <b>This changes how fast the game runs.</b> Movement, fire rates and
    /// regeneration are all per-tick, so a shorter interval is a faster world,
    /// not a smoother one. Put it back to 50 when the measurement is done.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void SetTickInterval(ReducerContext ctx, uint millis)
    {
        uint ms = Math.Clamp(millis, 1u, 1000u);
        foreach (var timer in ctx.Db.ShotCleanup.Iter().ToList())
        {
            var updated = timer;
            updated.ScheduledAt = new ScheduleAt.Interval(TimeSpan.FromMilliseconds(ms));
            ctx.Db.ShotCleanup.ScheduledId.Update(updated);
        }
        Log.Info($"tick interval set to {ms}ms");
    }

    /// <summary>Clears the tick statistics, so a measurement starts from now.</summary>
    [SpacetimeDB.Reducer]
    public static void ResetTickMetrics(ReducerContext ctx)
    {
        if (ctx.Db.TickMetric.Id.Find(1) is not null)
        {
            ctx.Db.TickMetric.Id.Delete(1);
        }
        _prevStampUs = 0;
        _pendingTicks = 0;
        _pendingMicros = 0;
        _pendingMax = 0;
        Log.Info("tick metrics reset");
    }

    /// <summary>
    /// Spawns many enemies at once, for load testing.
    /// </summary>
    /// <remarks>
    /// One reducer rather than a loop of calls from a shell: two hundred separate
    /// reducer invocations are two hundred transactions, and the spawn cost would
    /// swamp the tick cost being measured.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void SpawnEnemies(ReducerContext ctx, ushort defId, uint count,
                                    float x, float y, float radius)
    {
        if (ctx.Db.EnemyDef.Id.Find(defId) is null)
        {
            throw new Exception($"no enemy def {defId}");
        }

        uint capped = Math.Min(count, 2000u);
        for (uint i = 0; i < capped; i++)
        {
            double angle = ctx.Rng.NextDouble() * Math.Tau;
            double r = radius * Math.Sqrt(ctx.Rng.NextDouble());
            SpawnEnemy(ctx, defId,
                       x + (float)(Math.Cos(angle) * r),
                       y + (float)(Math.Sin(angle) * r));
        }
        Log.Info($"spawned {capped} of def {defId} around ({x}, {y})");
    }

    /// <summary>
    /// Where a shot is, <paramref name="t"/> seconds after it was fired.
    /// </summary>
    /// <remarks>
    /// Must stay identical to the client's copy in <c>VroxShots</c>. If the two
    /// disagree, bullets are drawn somewhere other than where they hit, and the
    /// game looks like it is cheating.
    /// </remarks>
    private static (float x, float y) ShotPositionAt(Shot shot, float t)
    {
        float x = shot.OriginX + shot.DirX * shot.Speed * t;
        float y = shot.OriginY + shot.DirY * shot.Speed * t;

        if (shot.WaveAmplitude != 0f)
        {
            float lateral = shot.WaveAmplitude *
                MathF.Sin(t * shot.WaveFrequency * MathF.Tau + shot.WavePhase);
            x += -shot.DirY * lateral;
            y += shot.DirX * lateral;
        }
        return (x, y);
    }

    /// <summary>
    /// Where an enemy wants to move this tick, as a unit vector or zero.
    /// </summary>
    /// <remarks>
    /// One interface for every kind of movement, including the ones that ignore
    /// the world entirely. A "pattern" is just a behaviour that does not look at
    /// <paramref name="hasTarget"/>; splitting the two into separate systems would
    /// mean every archetype picking a lane, and the interesting cases — orbit the
    /// player, back away while firing — fit neither.
    ///
    /// The distinction that does matter is what happens with no target in range,
    /// and each behaviour answers that for itself rather than sharing a fallback.
    ///
    /// Mirrored by <c>EnemyMath.MoveDirection</c> on the client, which the
    /// editor's enemy designer runs. Change both.
    /// </remarks>
    private static (float x, float y) MoveEnemy(Enemy enemy, PhaseDef def, float time,
                                                bool hasTarget, float targetX, float targetY)
    {
        float toX = targetX - enemy.X;
        float toY = targetY - enemy.Y;
        float distance = MathF.Sqrt(toX * toX + toY * toY);

        switch (def.Behaviour)
        {
            case 1: // Wander — a slow drift around home, never far from it.
                return WanderDirection(enemy, def, time);

            case 2: // Chase — straight at the player; wanders with nobody to chase.
                if (!hasTarget || distance < 0.05f)
                {
                    return WanderDirection(enemy, def, time);
                }
                return (toX / distance, toY / distance);

            case 3: // Orbit — circles a pivot at PreferredRange, closing if too far.
                {
                    // Around its spawn point, an orbit needs no player at all: the
                    // pivot is fixed, so it keeps holding its arena whether or not
                    // anyone is there to see it.
                    float pivotX = def.OrbitPivot == 1 ? enemy.HomeX : targetX;
                    float pivotY = def.OrbitPivot == 1 ? enemy.HomeY : targetY;

                    if (def.OrbitPivot != 1 && !hasTarget)
                    {
                        return WanderDirection(enemy, def, time);
                    }

                    float px = pivotX - enemy.X;
                    float py = pivotY - enemy.Y;
                    float pd = MathF.Sqrt(px * px + py * py);
                    if (pd < 0.05f)
                    {
                        // Sitting exactly on the pivot has no tangent to follow.
                        // Stepping outward gives the next tick something to circle.
                        return (1f, 0f);
                    }

                    float nx = px / pd;
                    float ny = py / pd;
                    distance = pd;
                    // Tangent plus a correction toward the preferred radius, so it
                    // spirals onto the ring instead of circling at whatever
                    // distance it happened to arrive at.
                    float error = distance - def.PreferredRange;
                    float pull = MathF.Max(-1f, MathF.Min(1f, error * 0.5f));
                    float dx = -ny + nx * pull;
                    float dy = nx + ny * pull;
                    return Normalise(dx, dy);
                }

            case 4: // Keep distance — approaches to PreferredRange, backs off inside it.
                if (!hasTarget)
                {
                    return WanderDirection(enemy, def, time);
                }
                {
                    float error = distance - def.PreferredRange;
                    // A dead zone, or it jitters back and forth across the exact
                    // preferred range forever.
                    if (MathF.Abs(error) < 0.5f || distance < 0.05f)
                    {
                        return (0f, 0f);
                    }
                    float sign = error > 0f ? 1f : -1f;
                    return (toX / distance * sign, toY / distance * sign);
                }

            default: // Static.
                return (0f, 0f);
        }
    }

    /// <summary>
    /// A slow drift that stays near home.
    /// </summary>
    /// <remarks>
    /// Derived from the enemy's own phase and the clock rather than stored state,
    /// so it needs no extra columns and every enemy of an archetype moves
    /// differently. Trigonometry is fine here: no client predicts enemy movement
    /// at runtime. The editor's enemy designer does run a copy,
    /// <c>EnemyMath.WanderDirection</c>, so a change here is a change there.
    /// </remarks>
    private static (float x, float y) WanderDirection(Enemy enemy, PhaseDef def, float time)
    {
        float angle = enemy.Phase + time * 0.7f;
        float dx = MathF.Cos(angle);
        float dy = MathF.Sin(angle);

        // Steered back when it strays, so a wanderer patrols its post instead of
        // drifting across the map over a few minutes.
        float homeX = enemy.HomeX - enemy.X;
        float homeY = enemy.HomeY - enemy.Y;
        float fromHome = MathF.Sqrt(homeX * homeX + homeY * homeY);
        float leash = def.PreferredRange > 0f ? def.PreferredRange : 4f;
        if (fromHome > leash)
        {
            float pull = MathF.Min(1f, (fromHome - leash) / leash);
            dx += homeX / fromHome * pull * 2f;
            dy += homeY / fromHome * pull * 2f;
        }
        return Normalise(dx, dy);
    }

    private static (float x, float y) Normalise(float x, float y)
    {
        float length = MathF.Sqrt(x * x + y * y);
        return length <= 0.0001f ? (0f, 0f) : (x / length, y / length);
    }

    /// <summary>Whether the segment a-b passes within <paramref name="radius"/> of a point.</summary>
    private static bool SegmentHitsCircle(float ax, float ay, float bx, float by,
                                          float cx, float cy, float radius)
    {
        float dx = bx - ax;
        float dy = by - ay;
        float lengthSq = dx * dx + dy * dy;

        // Closest point on the segment, clamped to its ends so a target behind
        // the shot is not counted as hit.
        float t = lengthSq <= 0f ? 0f : ((cx - ax) * dx + (cy - ay) * dy) / lengthSq;
        t = t < 0f ? 0f : (t > 1f ? 1f : t);

        float px = ax + dx * t - cx;
        float py = ay + dy * t - cy;
        return px * px + py * py <= radius * radius;
    }

    /// <summary>
    /// Moves every enemy, and fires the ones that can.
    /// </summary>
    /// <remarks>
    /// Enemies are the one thing here the server steps every tick. Projectiles
    /// are still analytic and players are still driven by their own input; this is
    /// the only per-tick simulation in the module, which is worth keeping true.
    /// </remarks>
    private static (int active, int dormant) AdvanceEnemies(ReducerContext ctx, List<Enemy> enemies,
                                       List<Player> players, long nowUs,
                                       Catalogue catalogue, Ground ground)
    {
        float time = (float)(nowUs / 1_000_000.0 % 3600.0);

        // Nothing to observe anything, so nothing needs to move. This is the
        // common case for an empty server and it costs one branch to skip.
        if (players.Count == 0)
        {
            // Nobody is here, so nothing is engaged with anybody.
            for (int i = 0; i < enemies.Count; i++)
            {
                if (enemies[i].Target is not null)
                {
                    var cleared = enemies[i];
                    cleared.Target = null;
                    ctx.Db.Enemy.Id.Update(cleared);
                    enemies[i] = cleared;
                }
            }
            return (0, 0);
        }

        int active = 0;
        int dormant = 0;

        for (int i = 0; i < enemies.Count; i++)
        {
            var enemy = enemies[i];
            if (!catalogue.Enemies.TryGetValue(enemy.DefId, out var def))
            {
                continue;
            }

            // A phase overrides how this enemy moves and shoots. Resolved before
            // anything reads a range, so a boss that only notices players in its
            // second phase is not woken by the first phase's numbers.
            var phase = CurrentPhase(catalogue, enemy, def);

            float wake = MathF.Max(SimulationRadius, MathF.Max(phase.AggroRange, phase.AttackRange));
            if (!AnyPlayerWithin(players, enemy.X, enemy.Y, wake))
            {
                // Left exactly as it is rather than paused or flagged. Movement
                // and wander are derived from the clock and the enemy's own
                // phase, not from accumulated state, so resuming needs nothing
                // restored — it simply starts moving again.
                // Dormant enemies disengage. Leaving a stale target would keep a
                // health bar up for a boss on the far side of the map.
                if (enemy.Target is not null)
                {
                    enemy.Target = null;
                    ctx.Db.Enemy.Id.Update(enemy);
                    enemies[i] = enemy;
                }
                dormant++;
                continue;
            }

            active++;

            // Two separate questions with two separate ranges: what it moves
            // towards, and what it is willing to shoot at.
            var (hasTarget, target) = Nearest(players, enemy.X, enemy.Y, phase.AggroRange);
            var (inRange, victim) = Nearest(players, enemy.X, enemy.Y, phase.AttackRange);

            bool stunned = nowUs < enemy.StunnedUntil.MicrosecondsSinceUnixEpoch;
            bool slowed = nowUs < enemy.SlowedUntil.MicrosecondsSinceUnixEpoch;

            var (dx, dy) = stunned
                ? (0f, 0f)
                : MoveEnemy(enemy, phase, time, hasTarget,
                            hasTarget ? target.X : 0f, hasTarget ? target.Y : 0f);
            if (dx != 0f || dy != 0f)
            {
                float speed = phase.Speed > 0f ? phase.Speed : def.Speed;
                if (slowed)
                {
                    speed *= 0.5f;
                }
                var (ex, ey) = ground.Slide(enemy.X, enemy.Y,
                                     dx * speed * TickSeconds,
                                     dy * speed * TickSeconds);
                Reposition(ref enemy, ex, ey);
            }

            // Recorded whether or not it can shoot: engagement is about having
            // noticed a player, and a boss winding up or reloading has still
            // noticed you.
            var engaged = inRange ? victim.Identity : (Identity?)null;

            // A timed phase's clock only runs while somebody is being fought.
            // Guarded on the transition kind so the overwhelming majority of
            // enemies — everything without phases — never touch this field and
            // are still only written when something about them actually changed.
            if (engaged is not null && phase.TransitionKind == 2)
            {
                enemy.PhaseEngagedUs += (ulong)(TickSeconds * 1_000_000f);
            }
            enemy.Target = engaged;

            if (!stunned && inRange && phase.WeaponId != 0
                && nowUs >= enemy.NextShotAt.MicrosecondsSinceUnixEpoch
                && catalogue.Weapons.TryGetValue(phase.WeaponId, out var weapon))
            {
                float aimX = victim.X - enemy.X;
                float aimY = victim.Y - enemy.Y;
                float length = MathF.Sqrt(aimX * aimX + aimY * aimY);
                if (length > 0.0001f)
                {
                    FireVolley(ctx, enemy.ZoneId, weapon, 1, enemy.X, enemy.Y, aimX / length, aimY / length);
                    enemy.NextShotAt = ctx.Timestamp + new TimeDuration(weapon.FireRateMs * 1000L);
                }
            }

            AdvancePhase(ctx, ref enemy, def, phase);

            // Written only when something actually changed.
            //
            // Unconditionally, this rewrote every simulated enemy twenty times a
            // second whether or not anything about it was different. Measured
            // against a realm of stationary enemies that was 2,357 row events a
            // second arriving at each client, all of them saying nothing, and the
            // client spends the frame applying them — which looks like stuttering
            // movement while the network is idle.
            //
            // Every field of Enemy is a value type, so this compares the whole
            // row. Listing the fields the loop touches would be faster and would
            // silently stop replicating one the day a field was added.
            if (!enemy.Equals(enemies[i]))
            {
                enemies[i] = enemy;
                ctx.Db.Enemy.Id.Update(enemy);
            }
        }

        // Summed across zones by the tick, which is the only place that knows
        // how many enemies no zone read at all.
        return (active, dormant);
    }

    /// <summary>
    /// The enemies close enough to anybody to matter this tick.
    /// </summary>
    /// <remarks>
    /// Live shots are included alongside players, not just players. A shot can
    /// outrun the simulation radius — five seconds at sixty tiles a second is
    /// three hundred tiles — and an enemy the tick never read is an enemy the
    /// collision pass cannot hit. Bullets would silently pass through anything
    /// far from a player.
    ///
    /// The radius is the widest wake distance any archetype in the catalogue
    /// asks for, not just <see cref="SimulationRadius"/>: an enemy with a longer
    /// aggro range than that has to be woken by a player it can see before the
    /// player can see it.
    ///
    /// Duplicate cells are collapsed through a set, so players standing together
    /// cost one read rather than one each.
    /// </remarks>
    private static List<Enemy> NearbyEnemies(ReducerContext ctx, uint zone, List<Player> players,
                                             Catalogue catalogue)
    {
        float reach = SimulationRadius;
        foreach (var def in catalogue.Enemies.Values)
        {
            reach = MathF.Max(reach, MathF.Max(def.AggroRange, def.AttackRange));
        }
        foreach (var phase in catalogue.Phases.Values)
        {
            reach = MathF.Max(reach, MathF.Max(phase.AggroRange, phase.AttackRange));
        }

        var cells = new HashSet<uint>();
        foreach (var player in players)
        {
            AddCells(cells, player.X, player.Y, reach);
        }
        foreach (var shot in ctx.Db.Shot.ZoneId.Filter(zone))
        {
            // The origin, not where it is now: the collision pass sweeps the
            // segment travelled since the last tick, so both ends have to be
            // covered and the origin is the one this cannot recompute cheaply.
            AddCells(cells, shot.OriginX, shot.OriginY, shot.Size + CellSize);
        }

        var found = new List<Enemy>();
        foreach (uint cell in cells)
        {
            foreach (var enemy in ctx.Db.Enemy.ZoneCell.Filter((zone, cell)))
            {
                found.Add(enemy);
            }
        }
        return found;
    }

    /// <summary>Adds every cell overlapping a square around a point.</summary>
    private static void AddCells(HashSet<uint> cells, float x, float y, float reach)
    {
        int minX = Math.Max(0, (int)((x - reach) / CellSize));
        int maxX = Math.Max(0, (int)((x + reach) / CellSize));
        int minY = Math.Max(0, (int)((y - reach) / CellSize));
        int maxY = Math.Max(0, (int)((y + reach) / CellSize));

        for (int cy = minY; cy <= maxY; cy++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                cells.Add(((uint)cy << 16) | ((uint)cx & 0xFFFF));
            }
        }
    }

    /// <summary>Whether any player is within <paramref name="range"/> of a point.</summary>
    private static bool AnyPlayerWithin(List<Player> players, float x, float y, float range)
    {
        float rangeSq = range * range;
        foreach (var player in players)
        {
            float dx = player.X - x;
            float dy = player.Y - y;
            if (dx * dx + dy * dy <= rangeSq)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Enemies simulated and skipped on the last tick.
    /// </summary>
    /// <remarks>
    /// Exposed so the saving is measurable rather than assumed. An optimisation
    /// nobody can see the effect of is indistinguishable from one that is not
    /// working.
    /// </remarks>
    public static int ActiveEnemies;
    public static int DormantEnemies;

    private static (int active, int dormant, int players) _lastReported = (-1, -1, -1);

    /// <summary>
    /// Tops each spawner back up to its population.
    /// </summary>
    /// <remarks>
    /// One enemy per interval rather than filling instantly, so clearing a camp
    /// gives a lull instead of the whole group reappearing at once.
    ///
    /// Population is counted from the enemies that actually exist rather than
    /// tracked in a counter. A counter drifts the moment an enemy is removed by
    /// anything that forgets to decrement it, and then the spawner either stops
    /// forever or never stops.
    /// </remarks>
    private static void RunSpawners(ReducerContext ctx, uint zone, List<Enemy> enemies, long nowUs)
    {
        var ground = ZoneGround(ctx, zone);
        foreach (var spawner in ctx.Db.Spawner.ZoneId.Filter(zone).ToList())
        {
            if (nowUs < spawner.NextSpawnAt.MicrosecondsSinceUnixEpoch)
            {
                continue;
            }

            // Counted through the SpawnerId index, not the tick's enemy list.
            // That list is now only the enemies near a player, so counting from
            // it would report a spawner far from anybody as empty and it would
            // refill forever.
            //
            // Affordable because this line is only reached when a spawner is
            // actually due, which is once per its interval rather than per tick.
            var mine = ctx.Db.Enemy.SpawnerId.Filter(spawner.Id).ToList();
            int alive = mine.Count;

            var updated = spawner;
            updated.NextSpawnAt = ctx.Timestamp + new TimeDuration(spawner.IntervalMs * 1000L);
            ctx.Db.Spawner.Id.Update(updated);

            if (alive >= spawner.MaxAlive)
            {
                continue;
            }

            ushort defId = PickEnemy(ctx, spawner, mine);
            if (defId == 0 || ctx.Db.EnemyDef.Id.Find(defId) is not { } def)
            {
                continue;
            }

            // A uniform point in the disc: the square root is what stops them
            // clustering in the middle, which is where a naive random radius puts
            // most of them.
            //
            // Retried rather than nudged when the spot is unusable. Nudging an
            // enemy out of a wall drops it at the nearest opening, which piles
            // every rejected spawn against the same few tiles.
            //
            // The biome test is what lets a generated spawner's circle be as big
            // as its region: the circle overhangs the neighbours, and the test
            // discards the overhang. Without it a region-sized radius would seed
            // the biome next door with the wrong enemies.
            float x = 0f, y = 0f;
            bool placed = false;
            for (int attempt = 0; attempt < SpawnAttempts && !placed; attempt++)
            {
                double angle = ctx.Rng.NextDouble() * Math.Tau;
                double distance = spawner.Radius * Math.Sqrt(ctx.Rng.NextDouble());
                x = ground.Clamp(spawner.X + (float)(Math.Cos(angle) * distance));
                y = ground.Clamp(spawner.Y + (float)(Math.Sin(angle) * distance));

                var tile = ground.TileAt(x, y);
                placed = tile.SpawnWeight > 0
                      && (spawner.Biome == AnyBiome || tile.Biome == spawner.Biome)
                      && !ground.Blocked(x, y);
            }
            if (!placed)
            {
                // Every attempt landed somewhere unusable. Skipping this interval
                // is right: the area is walled off or unspawnable, and forcing an
                // enemy into it would put it inside geometry.
                continue;
            }

            var spawned = ctx.Db.Enemy.Insert(new Enemy
            {
                Id = 0,
                DefId = defId,
                SpawnerId = spawner.Id,
                PhaseIndex = 0,
                PhaseStartedAt = ctx.Timestamp,
                X = x,
                Y = y,
                Cell = CellOf(x, y),
                HomeX = x,
                HomeY = y,
                Hp = def.MaxHp,
                Energy = def.MaxEnergy,
                NextShotAt = ctx.Timestamp,
                LastHitAt = ctx.Timestamp,
                LastDamage = 0,
                Phase = (float)(ctx.Rng.NextDouble() * Math.Tau),
                ZoneId = spawner.ZoneId,
            });

            // The local list is kept current, so a second spawner in the same tick
            // counts this one and the cap is respected within the tick as well as
            // across ticks.
            enemies.Add(spawned);
        }
    }

    /// <summary>
    /// Chooses what to spawn next from the area's mix.
    /// </summary>
    /// <remarks>
    /// Entries already at their own cap are removed before the roll rather than
    /// rolled and rejected. Rejecting after the fact would silently reduce the
    /// spawn rate whenever a capped entry came up, and the area would fill more
    /// slowly the more it filled.
    /// </remarks>
    private static ushort PickEnemy(ReducerContext ctx, Spawner spawner, List<Enemy> enemies)
    {
        int total = 0;
        var eligible = new List<AreaEntry>();

        foreach (var entry in spawner.Composition)
        {
            if (entry.Weight == 0 || entry.EnemyDefId == 0)
            {
                continue;
            }

            if (entry.MaxAlive > 0)
            {
                int ofThisKind = 0;
                foreach (var enemy in enemies)
                {
                    if (enemy.SpawnerId == spawner.Id && enemy.DefId == entry.EnemyDefId)
                    {
                        ofThisKind++;
                    }
                }
                if (ofThisKind >= entry.MaxAlive)
                {
                    continue;
                }
            }

            eligible.Add(entry);
            total += entry.Weight;
        }

        if (total <= 0)
        {
            return 0;
        }

        int roll = ctx.Rng.Next(0, total);
        foreach (var entry in eligible)
        {
            roll -= entry.Weight;
            if (roll < 0)
            {
                return entry.EnemyDefId;
            }
        }
        return eligible[^1].EnemyDefId;
    }

    /// <summary>Inserts or replaces a spawner. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertSpawner(ReducerContext ctx, ushort id, List<AreaEntry> composition,
                                     float x, float y, float radius,
                                     ushort maxAlive, ushort intervalMs, byte source)
    {
        if (id == 0)
        {
            throw new Exception("spawner id 0 is reserved");
        }

        var spawner = new Spawner
        {
            Id = id,
            Composition = composition,
            X = Clamp(x),
            Y = Clamp(y),
            Radius = Math.Clamp(radius, 0f, WorldSize),
            MaxAlive = Math.Clamp(maxAlive, (ushort)0, (ushort)200),
            // Floored well above zero: a spawner with no interval would insert an
            // enemy every tick until it hit its cap, twenty times a second.
            IntervalMs = (ushort)Math.Clamp((int)intervalMs, 100, 60000),
            NextSpawnAt = ctx.Timestamp,
            Source = source > SourceGenerated ? SourceEditor : source,
            // An editor-placed spawner is a hand-placed camp, not a region, so it
            // spawns on whatever ground its radius reaches. Confining it to the
            // biome under its centre would silently shrink camps that were
            // deliberately placed on a border.
            Biome = AnyBiome,
            ZoneId = RealmZone,
        };

        if (ctx.Db.Spawner.Id.Find(id) is null)
        {
            ctx.Db.Spawner.Insert(spawner);
        }
        else
        {
            ctx.Db.Spawner.Id.Update(spawner);
        }
    }

    /// <summary>
    /// Removes the scene's spawners, and the enemies they made.
    /// </summary>
    /// <remarks>
    /// The editor calls this before pushing, because spawner ids are assigned by
    /// scene order and change whenever objects are added or removed. Leaving the
    /// old enemies would strand them under ids that now mean something else, and
    /// their spawner would never count them.
    ///
    /// Generated spawners are deliberately left alone. This runs on every
    /// connect, so clearing them here would empty the realm each time anyone
    /// pressed Play. <see cref="ClearGeneratedSpawners"/> is the generator's own.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void ClearSpawners(ReducerContext ctx, byte source)
    {
        DropSpawners(ctx, source > SourceGenerated ? SourceEditor : source);
    }

    /// <summary>Removes spawners of one provenance, and the enemies they made.</summary>
    private static void DropSpawners(ReducerContext ctx, byte source)
    {
        var ids = ctx.Db.Spawner.Iter().Where(s => s.Source == source).Select(s => s.Id).ToHashSet();
        foreach (ushort id in ids)
        {
            ctx.Db.Spawner.Id.Delete(id);
        }
        foreach (ulong id in ctx.Db.Enemy.Iter()
                     .Where(e => e.SpawnerId != 0 && ids.Contains(e.SpawnerId))
                     .Select(e => e.Id).ToList())
        {
            ctx.Db.Enemy.Id.Delete(id);
        }
    }

    // --- Terrain ------------------------------------------------------------
    //
    // Movement resolves collision several times per input, and every projectile
    // is tested each tick. Reading chunk rows from the database each time would
    // make walking the most expensive thing the module does, so terrain is
    // flattened into a plain array once and kept in the module instance.
    //
    // A static, because modules run single-threaded in one long-lived instance.
    // If that ever stops being true this has to become thread-local.

    /// <summary>
    /// One layout's terrain, flattened for collision.
    /// </summary>
    /// <remarks>
    /// A class that callers must ask for by layout, rather than the global array
    /// this used to be. With one global map a dungeon's walls would silently be
    /// the realm's walls — players walking through rock the client draws, with
    /// nothing failing anywhere. Now every wall test names whose ground it means.
    /// </remarks>
    private sealed class Ground
    {
        public readonly TileData[] Tiles;
        public readonly int Span;

        /// <summary>The playable square, 0 to this, in tiles.</summary>
        public readonly float Size;

        public Ground(TileData[] tiles, int span, float size)
        {
            Tiles = tiles;
            Span = span;
            Size = size;
        }

        /// <summary>The tile at a world position. Outside the map counts as solid.</summary>
        public TileData TileAt(float worldX, float worldY)
        {
            int x = (int)MathF.Floor(worldX);
            int y = (int)MathF.Floor(worldY);
            if (x < 0 || y < 0 || x >= Span || y >= Span)
            {
                return new TileData { Flags = 0b11, SpawnWeight = 0, Hazard = 0, Biome = 0 };
            }
            return Tiles[y * Span + x];
        }

        public bool BlocksMovement(float x, float y) => (TileAt(x, y).Flags & 1) != 0;

        public bool BlocksProjectiles(float x, float y) => (TileAt(x, y).Flags & 2) != 0;

        /// <summary>
        /// Whether a body centred here would overlap solid ground.
        /// </summary>
        /// <remarks>
        /// Four corners of the body's box, not its centre. Testing the centre alone
        /// lets half a body sink into a wall before anything notices, and at a
        /// player radius of 0.4 that is nearly half a tile.
        /// </remarks>
        public bool Blocked(float x, float y)
        {
            const float r = PlayerRadius;
            return BlocksMovement(x - r, y - r)
                || BlocksMovement(x + r, y - r)
                || BlocksMovement(x - r, y + r)
                || BlocksMovement(x + r, y + r);
        }

        /// <summary>Whether the segment a-b crosses anything that stops a projectile.</summary>
        public bool HitsWall(float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            float distance = MathF.Sqrt(dx * dx + dy * dy);

            // A sample every half tile: close enough that nothing thinner than half a
            // tile can be missed, and tiles are never thinner than one.
            int steps = (int)(distance / 0.5f) + 1;
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                if (BlocksProjectiles(ax + dx * t, ay + dy * t))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Moves a body, one axis at a time so it slides along walls.</summary>
        public (float x, float y) Slide(float x, float y, float dx, float dy)
        {
            float nextX = Clamp(x + dx);
            if (!Blocked(nextX, y))
            {
                x = nextX;
            }

            float nextY = Clamp(y + dy);
            if (!Blocked(x, nextY))
            {
                y = nextY;
            }

            return (x, y);
        }

        public float Clamp(float v) => v < 0f ? 0f : (v > Size ? Size : v);
    }

    /// <summary>Layout id of the realm's own map, which lives in <see cref="TerrainChunk"/>.</summary>
    public const ushort RealmLayout = 0;

    private static readonly Dictionary<ushort, Ground> _grounds = new();

    /// <summary>
    /// Ground with no tiles, so every position is solid.
    /// </summary>
    /// <remarks>
    /// What a player standing in a zone that does not exist gets: they cannot
    /// move, and the tick reports them as orphaned. Borrowing the realm's ground
    /// instead would let them walk around a map nobody else is on.
    /// </remarks>
    private static readonly Ground Nowhere = new(Array.Empty<TileData>(), 0, 0f);

    /// <summary>Drops the realm's cached terrain so the next read rebuilds it.</summary>
    private static void InvalidateTerrain() => _grounds.Remove(RealmLayout);

    /// <summary>Drops one dungeon layout's cached terrain.</summary>
    private static void InvalidateLayout(ushort layoutId) => _grounds.Remove(layoutId);

    /// <summary>A layout's terrain, loading it from chunk rows if needed.</summary>
    private static Ground GroundOf(ReducerContext ctx, ushort layoutId)
    {
        if (_grounds.TryGetValue(layoutId, out var cached))
        {
            return cached;
        }

        Ground built;
        if (layoutId == RealmLayout)
        {
            built = BuildRealmGround(ctx);
        }
        else if (ctx.Db.DungeonLayout.Id.Find(layoutId) is { } layout)
        {
            built = BuildLayoutGround(ctx, layout);
        }
        else
        {
            // Not cached, so pushing the layout later is picked up immediately.
            Log.Warn($"terrain asked for layout {layoutId}, which has not been pushed");
            return Nowhere;
        }

        _grounds[layoutId] = built;
        return built;
    }

    /// <summary>The terrain of whichever layout a zone uses.</summary>
    private static Ground ZoneGround(ReducerContext ctx, uint zoneId) =>
        ctx.Db.Zone.Id.Find(zoneId) is { } zone ? GroundOf(ctx, zone.LayoutId) : Nowhere;

    private static Ground BuildRealmGround(ReducerContext ctx)
    {
        int span = (int)Math.Ceiling(WorldSize / ChunkSize) * ChunkSize;
        var tiles = new TileData[span * span];

        // Untouched ground is open and spawnable. An area with no terrain pushed
        // yet behaves exactly as it did before terrain existed, rather than
        // becoming a solid block nobody can enter.
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new TileData { Flags = 0, SpawnWeight = 1, Hazard = 0, Biome = 0 };
        }

        foreach (var chunk in ctx.Db.TerrainChunk.Iter())
        {
            CopyChunk(tiles, span, chunk.Cell, chunk.Tiles);
        }
        return new Ground(tiles, span, WorldSize);
    }

    private static Ground BuildLayoutGround(ReducerContext ctx, DungeonLayout layout)
    {
        int span = (int)Math.Ceiling(layout.Size / (float)ChunkSize) * ChunkSize;
        var tiles = new TileData[span * span];

        // Solid where nothing was pushed, the opposite of the realm. A dungeon
        // missing a chunk should have a wall there, not a hole into open ground
        // that leads off the edge of the authored rooms.
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new TileData { Flags = 0b11, SpawnWeight = 0, Hazard = 0, Biome = 0 };
        }

        foreach (var chunk in ctx.Db.LayoutChunk.LayoutId.Filter(layout.Id))
        {
            CopyChunk(tiles, span, chunk.Cell, chunk.Tiles);
        }
        return new Ground(tiles, span, layout.Size);
    }

    private static void CopyChunk(TileData[] tiles, int span, uint cell, List<TileData> chunk)
    {
        int cx = (int)(cell >> 16);
        int cy = (int)(cell & 0xFFFF);
        for (int i = 0; i < chunk.Count && i < ChunkSize * ChunkSize; i++)
        {
            int x = cx * ChunkSize + i % ChunkSize;
            int y = cy * ChunkSize + i / ChunkSize;
            if (x < span && y < span)
            {
                tiles[y * span + x] = chunk[i];
            }
        }
    }

    /// <summary>Where a player arriving in a zone appears.</summary>
    private static (float x, float y) ZoneSpawn(ReducerContext ctx, uint zoneId)
    {
        if (ctx.Db.Zone.Id.Find(zoneId) is { Kind: ZoneDungeon } zone
            && ctx.Db.DungeonLayout.Id.Find(zone.LayoutId) is { } layout)
        {
            return (layout.SpawnX, layout.SpawnY);
        }
        return SpawnPoint(ctx);
    }


    /// <summary>
    /// Writes one chunk row without touching the cache.
    /// </summary>
    /// <remarks>
    /// Separate from the reducer so a generator can write all 64 chunks and
    /// invalidate once. Invalidating per chunk costs a full array rebuild each
    /// time — 64 rebuilds of a 16k array for one map.
    /// </remarks>
    private static void WriteChunkRow(ReducerContext ctx, uint cell, List<TileData> tiles)
    {
        var chunk = new TerrainChunk { Cell = cell, Tiles = tiles };
        if (ctx.Db.TerrainChunk.Cell.Find(cell) is null)
        {
            ctx.Db.TerrainChunk.Insert(chunk);
        }
        else
        {
            ctx.Db.TerrainChunk.Cell.Update(chunk);
        }
    }

    /// <summary>
    /// Replaces one chunk of terrain. Called by the Unity editor.
    /// </summary>
    /// <remarks>
    /// Refused while the realm is generated. The editor pushes terrain on every
    /// connect, so otherwise the first person to press Play with a tilemap in
    /// their scene silently replaces the generated realm with their lobby — and
    /// the only symptom is that the world looks wrong to everybody else.
    /// Call <c>UseAuthoredTerrain</c> to hand it back deliberately.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void UpsertTerrainChunk(ReducerContext ctx, uint cell, List<TileData> tiles)
    {
        if (TerrainIsGenerated(ctx))
        {
            Log.Warn($"refused a terrain push for chunk {cell}: the realm is generated. "
                   + "Call use_authored_terrain first if that is what you meant.");
            return;
        }
        WriteChunkRow(ctx, cell, tiles);
        InvalidateTerrain();
    }

    /// <summary>
    /// Sets where players appear, and rescues anyone the terrain has trapped.
    /// </summary>
    /// <remarks>
    /// Only players standing in solid ground are moved. Teleporting everyone on
    /// every push would yank people out of wherever they were testing, but
    /// leaving someone sealed inside a wall is unrecoverable — they cannot walk
    /// out of it in any direction.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void SetSpawn(ReducerContext ctx, float x, float y)
    {
        var spawn = new WorldSpawn { Id = 1, X = Clamp(x), Y = Clamp(y) };
        if (ctx.Db.WorldSpawn.Id.Find(1) is null)
        {
            ctx.Db.WorldSpawn.Insert(spawn);
        }
        else
        {
            ctx.Db.WorldSpawn.Id.Update(spawn);
        }

        foreach (var player in ctx.Db.Player.Iter().ToList())
        {
            if (player.ZoneId == RealmZone && GroundOf(ctx, RealmLayout).Blocked(player.X, player.Y))
            {
                var moved = player;
                moved.X = spawn.X;
                moved.Y = spawn.Y;
                ctx.Db.Player.Identity.Update(moved);
            }
        }
        Log.Info($"spawn point set to ({spawn.X}, {spawn.Y})");
    }

    /// <summary>Where a player should appear.</summary>
    private static (float x, float y) SpawnPoint(ReducerContext ctx) =>
        ctx.Db.WorldSpawn.Id.Find(1) is { } s ? (s.X, s.Y) : (WorldSize / 2f, WorldSize / 2f);

    /// <summary>Removes all terrain, returning the area to open ground.</summary>
    /// <remarks>Refused while the realm is generated, for the reason in
    /// <see cref="UpsertTerrainChunk"/>.</remarks>
    [SpacetimeDB.Reducer]
    public static void ClearTerrain(ReducerContext ctx)
    {
        if (TerrainIsGenerated(ctx))
        {
            Log.Warn("refused to clear terrain: the realm is generated.");
            return;
        }

        foreach (uint cell in ctx.Db.TerrainChunk.Iter().Select(c => c.Cell).ToList())
        {
            ctx.Db.TerrainChunk.Cell.Delete(cell);
        }
        InvalidateTerrain();
    }

    /// <summary>
    /// The player stat block, or sane defaults when none has been pushed.
    /// </summary>
    /// <remarks>
    /// Falling back rather than refusing to run: a fresh database has no config
    /// until someone presses Play, and a server where nobody can move until an
    /// editor connects is a worse failure than one running on defaults.
    /// </remarks>
    private static PlayerConfig Config(ReducerContext ctx) =>
        ctx.Db.PlayerConfig.Id.Find(1) ?? new PlayerConfig
        {
            Id = 1,
            MaxHp = PlayerMaxHp,
            Defense = 0,
            Speed = 5f,
            Dexterity = 1f,
            HpRegenPerSec = 0f,
            CritChance = 0f,
            CritMultiplier = 1.5f,
            StartingWeaponId = 0,
        };

    /// <summary>Inserts or replaces the player stat block. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertPlayerConfig(ReducerContext ctx, ushort maxHp, ushort defense,
                                          float speed, float dexterity, float hpRegenPerSec,
                                          float critChance, float critMultiplier,
                                          ushort startingWeaponId)
    {
        var config = new PlayerConfig
        {
            Id = 1,
            MaxHp = maxHp < 1 ? (ushort)1 : maxHp,
            Defense = defense,
            Speed = Math.Clamp(speed, 0.5f, 40f),
            Dexterity = Math.Clamp(dexterity, 0.1f, 10f),
            HpRegenPerSec = Math.Clamp(hpRegenPerSec, 0f, 1000f),
            StartingWeaponId = startingWeaponId,
            CritChance = Math.Clamp(critChance, 0f, 1f),
            CritMultiplier = Math.Clamp(critMultiplier, 1f, 10f),
        };

        if (ctx.Db.PlayerConfig.Id.Find(1) is null)
        {
            ctx.Db.PlayerConfig.Insert(config);
        }
        else
        {
            ctx.Db.PlayerConfig.Id.Update(config);
        }
        Log.Info($"player config: {config.MaxHp}hp, def {config.Defense}, speed {config.Speed}, "
               + $"dex {config.Dexterity}, regen {config.HpRegenPerSec}/s, "
               + $"crit {config.CritChance:P0} x{config.CritMultiplier}");
    }

    /// <summary>
    /// Applies regeneration and keeps each player's max health in step with the config.
    /// </summary>
    private static void AdvancePlayers(ReducerContext ctx, PlayerConfig config)
    {
        long nowUs = ctx.Timestamp.MicrosecondsSinceUnixEpoch;

        foreach (var player in ctx.Db.Player.Iter().Where(p => p.Online).ToList())
        {
            var updated = player;
            bool changed = false;

            if (updated.MaxHp != config.MaxHp)
            {
                updated.MaxHp = config.MaxHp;
                if (updated.Hp > config.MaxHp)
                {
                    updated.Hp = config.MaxHp;
                }
                changed = true;
            }

            // Terrain can change under a standing player, and a body sealed in
            // rock cannot walk out in any direction. Checked every tick rather
            // than only on push, because a spawner or a later feature could move
            // someone into geometry just as easily.
            if (ZoneGround(ctx, updated.ZoneId).Blocked(updated.X, updated.Y))
            {
                var (sx, sy) = ZoneSpawn(ctx, updated.ZoneId);
                updated.X = sx;
                updated.Y = sy;
                changed = true;
            }

            // Reloads are settled on the tick rather than when the player next
            // fires, so a weapon is ready the instant it should be and a client
            // can show that honestly. One lookup serves both halves: finishing a
            // reload, and noticing that one ought to start.
            if (ctx.Db.WeaponDef.Id.Find(updated.WeaponId) is { Magazine: > 0 } gun)
            {
                if (updated.ReloadAtUs != 0 && (long)updated.ReloadAtUs <= nowUs)
                {
                    if (gun.ReloadPerShell)
                    {
                        updated.Ammo += 1;
                        // Another shell if there is room. Break off by firing.
                        updated.ReloadAtUs = updated.Ammo < gun.Magazine
                            ? (ulong)(nowUs + gun.ReloadMs * 1000L)
                            : 0ul;
                    }
                    else
                    {
                        updated.Ammo = gun.Magazine;
                        updated.ReloadAtUs = 0ul;
                    }
                    changed = true;
                }
                else if (updated.ReloadAtUs == 0 && updated.Ammo == 0)
                {
                    // An empty magazine reloads itself. Firing dry already starts
                    // one, so holding the trigger has always ended in a reload —
                    // but letting go did not, and the gun then sat empty until
                    // something was clicked at. Nothing about that was a decision
                    // the player was making on purpose.
                    //
                    // Here rather than keyed to the client's auto-fire, which is a
                    // local display setting the server cannot see and should not
                    // be told about: the rule is "an empty gun reloads", which is
                    // true for a bot and another player's character as well.
                    //
                    // Weapons are drawn loaded, so this cannot fire on equipping
                    // one — only after the magazine has genuinely been emptied.
                    updated.ReloadAtUs = (ulong)(nowUs + gun.ReloadMs * 1000L);
                    changed = true;
                }
            }

            // A bag can empty under a player, or expire while they stand over
            // it. Left set, the player stays rooted with nothing to close —
            // stuck in place with no menu and no explanation.
            if (updated.LootingBag != 0 && ctx.Db.LootDrop.Id.Find(updated.LootingBag) is null)
            {
                updated.LootingBag = 0;
                changed = true;
            }

            if (config.HpRegenPerSec > 0f && updated.Hp > 0 && updated.Hp < updated.MaxHp)
            {
                updated.RegenPool += config.HpRegenPerSec * TickSeconds;
                if (updated.RegenPool >= 1f)
                {
                    int whole = (int)updated.RegenPool;
                    updated.RegenPool -= whole;
                    int healed = updated.Hp + whole;
                    updated.Hp = (ushort)Math.Min(healed, updated.MaxHp);
                }

                // Reaching the cap discards the remainder. The pool is part of
                // the player's health rather than a private accumulator —
                // clients read Hp + RegenPool, which is the only continuous
                // health value this module has — so a full player still holding
                // a fraction reads as more than full.
                if (updated.Hp >= updated.MaxHp)
                {
                    updated.RegenPool = 0f;
                }

                // Written every tick, not only when a whole point lands. The pool
                // lives on the row, so a tick that does not persist it discards
                // the fraction it just added — and at any rate below one point per
                // tick that means the pool never grows and nothing ever heals.
                changed = true;
            }
            else if (updated.RegenPool != 0f)
            {
                // Full, dead, or regen switched off, and holding a fraction with
                // nowhere to go. Left on the row it is health the player can see
                // but can never spend, and it would reappear as free healing the
                // instant they took a single point of damage.
                updated.RegenPool = 0f;
                changed = true;
            }

            if (changed)
            {
                ctx.Db.Player.Identity.Update(updated);
            }
        }
    }

    /// <summary>
    /// The phase an enemy is in, or the archetype's own settings as a phase.
    /// </summary>
    /// <remarks>
    /// Unphased enemies are expressed as a single implicit phase rather than
    /// handled separately. One code path means a boss and a grunt move and shoot
    /// through identical logic, and the phase system cannot break ordinary
    /// enemies by existing.
    /// </remarks>
    /// <summary>
    /// The catalogue rows a tick needs, read once instead of per enemy.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed. With 200 enemies the tick was resolving an
    /// <c>EnemyDef</c> and a <c>PhaseDef</c> for every one of them, every tick —
    /// 400 index lookups — and that was 10ms of a 25ms tick. The catalogue is a
    /// handful of rows that cannot change during a tick, so reading it once and
    /// handing it round costs three small dictionaries and removes all of it.
    ///
    /// Rebuilt per tick rather than cached across ticks on purpose: a cache that
    /// outlives the tick has to be invalidated when the editor pushes, and a
    /// stale catalogue would be a boss quietly fighting with last week's stats.
    /// </remarks>
    private sealed class Catalogue
    {
        public readonly Dictionary<ushort, EnemyDef> Enemies = new();
        public readonly Dictionary<uint, PhaseDef> Phases = new();
        public readonly Dictionary<ushort, WeaponDef> Weapons = new();

        public static Catalogue Read(ReducerContext ctx)
        {
            var c = new Catalogue();
            foreach (var def in ctx.Db.EnemyDef.Iter())
            {
                c.Enemies[def.Id] = def;
            }
            foreach (var phase in ctx.Db.PhaseDef.Iter())
            {
                c.Phases[phase.Key] = phase;
            }
            foreach (var weapon in ctx.Db.WeaponDef.Iter())
            {
                c.Weapons[weapon.Id] = weapon;
            }
            return c;
        }
    }

    private static PhaseDef CurrentPhase(Catalogue catalogue, Enemy enemy, EnemyDef def)
    {
        if (catalogue.Phases.TryGetValue(PhaseKey(def.Id, enemy.PhaseIndex), out var phase))
        {
            return phase;
        }

        return new PhaseDef
        {
            EnemyDefId = def.Id,
            Index = 0,
            Behaviour = def.Behaviour,
            AggroRange = def.AggroRange,
            PreferredRange = def.PreferredRange,
            AttackRange = def.AttackRange,
            WeaponId = def.WeaponId,
            OrbitPivot = def.OrbitPivot,
            Speed = def.Speed,
            Flags = 0,
            DamageTakenPercent = 100f,
            TransitionKind = 0,
            TransitionValue = 0f,
        };
    }

    private static uint PhaseKey(ushort defId, byte index) => ((uint)defId << 8) | index;

    /// <summary>
    /// Moves an enemy to its next phase when the current one's condition is met.
    /// </summary>
    /// <remarks>
    /// The shot timer is reset on entry. Without it a boss that has just switched
    /// to a slow weapon still fires immediately on the fast weapon's schedule,
    /// and the transition reads as a stutter rather than a change.
    ///
    /// The leave condition is mirrored by <c>EnemyMath.LeavesPhase</c> on the
    /// client, for the editor's enemy designer. Change both.
    /// </remarks>
    private static void AdvancePhase(ReducerContext ctx, ref Enemy enemy, EnemyDef def,
                                     PhaseDef phase)
    {
        bool leave = phase.TransitionKind switch
        {
            1 => def.MaxHp > 0 && enemy.Hp * 100f / def.MaxHp <= phase.TransitionValue,

            // Engaged time, not elapsed time. See Enemy.PhaseEngagedUs for why
            // wall-clock fails in both directions here.
            2 => enemy.PhaseEngagedUs / 1_000_000f >= phase.TransitionValue,
            _ => false,
        };

        if (!leave)
        {
            return;
        }

        byte next = (byte)(enemy.PhaseIndex + 1);
        if (ctx.Db.PhaseDef.Key.Find(PhaseKey(def.Id, next)) is null)
        {
            // The last phase stays put rather than wrapping. A boss looping back
            // to its opening at low health would be unkillable in the common case
            // where phase one is the defensive one.
            return;
        }

        enemy.PhaseIndex = next;
        enemy.PhaseStartedAt = ctx.Timestamp;
        enemy.PhaseEngagedUs = 0;
        enemy.NextShotAt = ctx.Timestamp;
        Log.Info($"enemy {enemy.Id} \"{def.Name}\" entered phase {next}");
    }

    /// <summary>Inserts or replaces one phase. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertPhase(ReducerContext ctx, ushort enemyDefId, byte index,
                                   byte behaviour, float aggroRange, float preferredRange,
                                   float attackRange, ushort weaponId, byte orbitPivot,
                                   float speed, byte flags, float damageTakenPercent,
                                   byte transitionKind, float transitionValue)
    {
        var phase = new PhaseDef
        {
            Key = PhaseKey(enemyDefId, index),
            EnemyDefId = enemyDefId,
            Index = index,
            Behaviour = behaviour > 4 ? (byte)0 : behaviour,
            AggroRange = Math.Clamp(aggroRange, 0f, 60f),
            PreferredRange = Math.Clamp(preferredRange, 0f, 40f),
            AttackRange = Math.Clamp(attackRange, 0f, 60f),
            WeaponId = weaponId,
            OrbitPivot = orbitPivot > 1 ? (byte)0 : orbitPivot,
            Speed = Math.Clamp(speed, 0f, 20f),
            Flags = flags,
            DamageTakenPercent = Math.Clamp(damageTakenPercent, 0f, 1000f),
            TransitionKind = transitionKind > 2 ? (byte)0 : transitionKind,
            TransitionValue = transitionValue,
        };

        if (ctx.Db.PhaseDef.Key.Find(phase.Key) is null)
        {
            ctx.Db.PhaseDef.Insert(phase);
        }
        else
        {
            ctx.Db.PhaseDef.Key.Update(phase);
        }
    }

    /// <summary>Removes every phase for one archetype.</summary>
    [SpacetimeDB.Reducer]
    public static void ClearPhases(ReducerContext ctx, ushort enemyDefId)
    {
        foreach (uint key in ctx.Db.PhaseDef.EnemyDefId.Filter(enemyDefId).Select(p => p.Key).ToList())
        {
            ctx.Db.PhaseDef.Key.Delete(key);
        }
    }

    /// <summary>Closest living player within <paramref name="range"/>, if any.</summary>
    private static (bool found, Player player) Nearest(List<Player> players, float x, float y, float range)
    {
        Player best = default;
        bool found = false;
        float bestSq = range * range;

        foreach (var player in players)
        {
            float dx = player.X - x;
            float dy = player.Y - y;
            float d = dx * dx + dy * dy;
            if (d <= bestSq)
            {
                bestSq = d;
                best = player;
                found = true;
            }
        }
        return (found, best);
    }

    /// <summary>Applies damage to an enemy, and removes it at zero.</summary>
    private static Enemy DamageEnemy(ReducerContext ctx, Catalogue catalogue, Enemy enemy,
                                     Shot shot, Identity? killer = null)
    {
        ushort amount = shot.Damage;
        if (catalogue.Enemies.TryGetValue(enemy.DefId, out var armourDef))
        {
            var phase = CurrentPhase(catalogue, enemy, armourDef);

            // Element before the phase's mitigation. Resistance is a property of
            // the creature; a phase percentage is a property of what it is doing
            // right now. Applying them in the other order would still multiply to
            // the same number, but reads backwards: an invulnerable phase would
            // be scaling something that had already been made bigger.
            amount = ApplyResistance(ctx, armourDef, shot.Element, amount);

            // Invulnerable still registers the hit — the flash and the number
            // tell the player their shots are landing and doing nothing, which is
            // the signal a transition phase exists to send. Silently swallowing
            // them reads as the boss being unhittable, or the game being broken.
            if ((phase.Flags & 1) != 0)
            {
                amount = 0;
            }
            else if (phase.DamageTakenPercent != 100f)
            {
                float scaled = amount * phase.DamageTakenPercent / 100f;
                amount = scaled <= 0f ? (ushort)0 : (ushort)MathF.Max(1f, scaled);
            }
        }

        // Credited before the health change, so a killing blow is still counted
        // against the target it killed rather than being lost with the row.
        if (killer is { } attacker)
        {
            CreditDamage(ctx, enemy.Id, attacker, shot.Element, amount, shot.Crit);
            ApplyDebuff(ctx, ref enemy, attacker, shot.DebuffKind, shot.DebuffSeconds);
        }

        // Broadcast before the health change, so a killing blow still announces
        // itself: the row it refers to is about to be deleted.
        ctx.Db.Hit.Insert(new Hit
        {
            EnemyId = enemy.Id,
            X = enemy.X,
            Y = enemy.Y,
            Amount = amount,
            Crit = shot.Crit,
            Element = shot.Element,
            ZoneId = enemy.ZoneId,
        });

        // Still written, and still the wrong thing to read for a volley. It is
        // what makes an enemy flash when hit, which is per-enemy-per-frame state
        // rather than per-hit, and so is correctly collapsed.
        enemy.Hp = amount >= enemy.Hp ? (ushort)0 : (ushort)(enemy.Hp - amount);
        enemy.LastHitAt = ctx.Timestamp;
        enemy.LastDamage = amount;

        if (enemy.Hp == 0)
        {
            if (ctx.Db.EnemyDef.Id.Find(enemy.DefId) is { } killedDef)
            {
                RecordKill(ctx, enemy, killedDef, killer);
                TrySpawnPortal(ctx, enemy);
            }
            ctx.Db.Enemy.Id.Delete(enemy.Id);
        }
        else
        {
            ctx.Db.Enemy.Id.Update(enemy);
        }
        return enemy;
    }

    /// <summary>
    /// Applies damage to a player, respawning them at zero.
    /// </summary>
    /// <remarks>
    /// No death state and no corpse: this exists so enemy fire has a consequence
    /// worth looking at, not to be a life system. That comes later, with something
    /// to lose.
    /// </remarks>
    private static void DamagePlayer(ReducerContext ctx, Player player, ushort amount,
                                     byte debuffKind = 0, float debuffSeconds = 0f)
    {
        player.LastHitAt = ctx.Timestamp;
        long nowUs = ctx.Timestamp.MicrosecondsSinceUnixEpoch;

        // Armour break is read before it is applied, so the bullet that breaks
        // the armour does not also benefit from having broken it. Otherwise the
        // first hit of a volley silently counts twice.
        ushort defense = Config(ctx).Defense;
        if ((long)player.ArmorBrokenUntilUs > nowUs && player.ArmorBreakPercent > 0)
        {
            int removed = defense * Math.Min((int)player.ArmorBreakPercent, 100) / 100;
            defense = (ushort)(defense - removed);
        }

        // Floored at 1, not 0. Defence high enough to zero every hit makes a
        // player invulnerable, which is a balance cliff rather than a stat.
        amount = amount > defense ? (ushort)(amount - defense) : (ushort)1;
        player.LastDamage = amount;

        ApplyPlayerDebuff(ref player, debuffKind, debuffSeconds, nowUs);

        if (amount >= player.Hp)
        {
            KillCharacter(ctx, ref player);
        }
        else
        {
            player.Hp = (ushort)(player.Hp - amount);
        }
        ctx.Db.Player.Identity.Update(player);
    }

    /// <summary>
    /// Applies one debuff carried by a bullet that hit a player.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="ApplyDebuff"/>, which does the same for enemies,
    /// and refreshes rather than stacks for the same reason: stacking is
    /// unbounded, and a room of shooters would hold a player stunned forever.
    ///
    /// Separate from the enemy version rather than shared, because the two write
    /// different rows and the shared part is two lines of arithmetic. A generic
    /// helper over both would take a ref to one of two structs, which C# cannot
    /// express without an interface neither table implements.
    ///
    /// A stun on a player is deliberately the harshest thing here and nothing
    /// authored uses it for long: the client stops accepting movement, which is
    /// indistinguishable from a frozen game if it outlasts a moment.
    /// </remarks>
    private static void ApplyPlayerDebuff(ref Player player, byte kind, float seconds,
                                          long nowUs)
    {
        if (kind == 0 || seconds <= 0f)
        {
            return;
        }

        long addUs = (long)(seconds * 1_000_000f);

        switch (kind)
        {
            case 1: // Stun
                player.StunnedUntilUs = Refreshed(player.StunnedUntilUs, nowUs, addUs);
                break;
            case 2: // Slow
                player.SlowedUntilUs = Refreshed(player.SlowedUntilUs, nowUs, addUs);
                break;
            case 3: // Armour break
                player.ArmorBrokenUntilUs = Refreshed(player.ArmorBrokenUntilUs, nowUs, addUs);
                // The strength is the bullet's, not accumulated: two sources take
                // the harsher of the two rather than adding up to no armour at all.
                if (player.ArmorBreakPercent < ArmorBreakPercentDefault)
                {
                    player.ArmorBreakPercent = ArmorBreakPercentDefault;
                }
                break;
        }
    }

    /// <summary>Extends an expiry to the later of what it was and now plus a span.</summary>
    private static ulong Refreshed(ulong currentUs, long nowUs, long addUs)
    {
        long active = (long)currentUs > nowUs ? (long)currentUs : nowUs;
        return (ulong)(active + addUs);
    }

    /// <summary>
    /// Applies damage to a dummy.
    /// </summary>
    /// <remarks>
    /// A dummy never dies — it refills once emptied, so it stays available to
    /// shoot at. That is the point of a dummy: it is a measuring instrument, and
    /// one that disappears stops measuring.
    /// </remarks>
    private static void DamageDummy(ReducerContext ctx, Dummy dummy, ushort amount)
    {
        dummy.Hp = amount >= dummy.Hp ? dummy.MaxHp : (ushort)(dummy.Hp - amount);
        dummy.LastHitAt = ctx.Timestamp;
        dummy.LastDamage = amount;
        ctx.Db.Dummy.Id.Update(dummy);
    }

    [SpacetimeDB.Reducer(ReducerKind.ClientConnected)]
    public static void ClientConnected(ReducerContext ctx)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is { } existing)
        {
            existing.Online = true;
            // A zone that closed while they were away — or a row from before
            // disconnecting returned people to the realm. Put back loudly rather
            // than left standing on ground that no longer exists.
            if (ctx.Db.Zone.Id.Find(existing.ZoneId) is null)
            {
                Log.Warn($"{existing.Name} reconnected into zone {existing.ZoneId}, which is gone");
                LeaveZone(ctx, ref existing);
            }
            ctx.Db.Player.Identity.Update(existing);
            Log.Info($"welcome back {ctx.Sender}");
            return;
        }

        ctx.Db.Player.Insert(new Player
        {
            Identity = ctx.Sender,
            X = SpawnPoint(ctx).x,
            Y = SpawnPoint(ctx).y,
            Online = true,
            Name = $"player-{ctx.Sender.ToString()[..6]}",
            NextShotAt = ctx.Timestamp,
            Hp = Config(ctx).MaxHp,
            MaxHp = Config(ctx).MaxHp,
            LastHitAt = ctx.Timestamp,
            RegenPool = 0f,
            ZoneId = RealmZone,
        });
        // No character, and none made here. Connecting means arriving at the
        // character screen; who you play is a choice, and a client that was
        // handed one on connect could never be shown the choice.
        Log.Info($"new account {ctx.Sender}");
    }

    [SpacetimeDB.Reducer(ReducerKind.ClientDisconnected)]
    public static void ClientDisconnected(ReducerContext ctx)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is { } player)
        {
            // Or they reconnect rooted to a bag that is long gone.
            // Out of any dungeon, so an empty run can close and nobody reconnects
            // into one that already has.
            if (player.ZoneId != RealmZone)
            {
                LeaveZone(ctx, ref player);
            }
            player.LootingBag = 0;
            player.Online = false;
            ctx.Db.Player.Identity.Update(player);
        }
    }

    /// <summary>
    /// Moves the caller one step along <paramref name="dirX"/>, <paramref name="dirY"/>.
    /// </summary>
    /// <remarks>
    /// The direction is normalised server-side. A client sending (100, 100)
    /// travels exactly as far as one sending (1, 1) — otherwise the length of the
    /// vector is a speed multiplier anyone can set.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void Move(ReducerContext ctx, float dirX, float dirY)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }

        // Rooted while a bag is open. Refused silently rather than thrown: the
        // client sends movement twenty times a second, and a throw per input
        // would bury the log for as long as the menu was up.
        if (player.LootingBag != 0)
        {
            return;
        }

        // Refused silently for the same reason the bag check is: this arrives
        // twenty times a second and a throw per input would bury the log.
        long nowUs = ctx.Timestamp.MicrosecondsSinceUnixEpoch;
        if ((long)player.StunnedUntilUs > nowUs)
        {
            return;
        }

        float length = MathF.Sqrt(dirX * dirX + dirY * dirY);
        if (length > 0.0001f)
        {
            // Reciprocal-then-multiply, so this matches a client that predicts
            // the same step later. Dividing each component separately rounds
            // differently and the two would disagree in the last bit.
            float inv = 1f / length;

            // The weapon in hand scales the step. Read from the replicated def
            // rather than remembered per player, so retuning a weapon's weight
            // in the editor changes how it walks on the very next input with
            // nobody re-equipping anything.
            //
            // A missing def is 100, not a refusal to move: WeaponId is set by
            // SyncWeapon and the row could in principle lag it, and a player
            // frozen in place is a far worse failure than one who walks at their
            // base speed for a tick.
            float handling = 1f;
            if (player.WeaponId != 0
                && ctx.Db.WeaponDef.Id.Find(player.WeaponId) is { } held)
            {
                handling = held.MoveSpeedPercent / 100f;
            }

            float step = Config(ctx).Speed * handling * StepSeconds;

            // Applied here and nowhere else. One input is one step, so halving
            // the step halves the speed exactly; the client neither predicts nor
            // rate-limits on its own, so there is no second copy to disagree.
            if ((long)player.SlowedUntilUs > nowUs)
            {
                step *= SlowFactor;
            }

            var (x, y) = ZoneGround(ctx, player.ZoneId).Slide(player.X, player.Y, dirX * inv * step, dirY * inv * step);
            player.X = x;
            player.Y = y;
            ctx.Db.Player.Identity.Update(player);
        }
    }

    /// <summary>
    /// Fires along <paramref name="aimX"/>, <paramref name="aimY"/> if the cooldown has elapsed.
    /// </summary>
    /// <remarks>
    /// The client calls this every tick while the fire button is held and passes
    /// only a direction. Everything else — rate, count, spread, damage — is read
    /// from the catalogue here, so a client cannot fire faster, wider or harder
    /// than its equipped weapon allows by changing what it sends.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void Shoot(ReducerContext ctx, float aimX, float aimY)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }
        if (ctx.Timestamp.MicrosecondsSinceUnixEpoch < player.NextShotAt.MicrosecondsSinceUnixEpoch)
        {
            return;
        }

        // Stun stops the trigger as well as the feet, matching what it does to an
        // enemy. Checked before the aim so a stunned player firing at nothing is
        // refused for the reason that is actually true.
        if ((long)player.StunnedUntilUs > ctx.Timestamp.MicrosecondsSinceUnixEpoch)
        {
            return;
        }

        float length = MathF.Sqrt(aimX * aimX + aimY * aimY);
        if (length <= 0.0001f)
        {
            return;
        }
        float inv = 1f / length;
        float dirX = aimX * inv;
        float dirY = aimY * inv;

        // No weapon, no shot. Not an error — an unarmed player simply cannot
        // fire, and the client calls this every tick regardless.
        if (player.WeaponId == 0 || ctx.Db.WeaponDef.Id.Find(player.WeaponId) is not { } weapon)
        {
            return;
        }

        var config = Config(ctx);
        long nowUs = ctx.Timestamp.MicrosecondsSinceUnixEpoch;

        if (weapon.Magazine > 0)
        {
            // Firing cancels a reload in progress. For a magazine weapon that
            // wastes the whole thing, which is the cost of panicking; for a
            // shell-at-a-time weapon it keeps whatever was already chambered,
            // which is the point of loading them one by one.
            if (player.ReloadAtUs != 0 && (long)player.ReloadAtUs > nowUs)
            {
                if (player.Ammo == 0)
                {
                    return;
                }
                player.ReloadAtUs = 0;
            }

            if (player.Ammo == 0)
            {
                // Started here rather than refused outright. A dry weapon that
                // needed a separate keypress to reload would mean every fight
                // ends with a player mashing fire at nothing.
                BeginReload(ctx, ref player, weapon, nowUs);
                ctx.Db.Player.Identity.Update(player);
                return;
            }

            player.Ammo -= 1;
        }

        if (weapon.Kickback > 0f)
        {
            // Shoved along the reverse of the aim, through the same Slide the
            // player walks with — so recoil cannot push anybody through a wall.
            var (kx, ky) = ZoneGround(ctx, player.ZoneId).Slide(player.X, player.Y,
                                 -dirX * weapon.Kickback, -dirY * weapon.Kickback);
            player.X = kx;
            player.Y = ky;
        }

        if (weapon.Range > 0f)
        {
            // A gun. Resolved instantly, with a line recorded for the client to
            // draw — see FireHitscan for why the two halves of the game fire
            // differently on purpose.
            FireHitscan(ctx, Catalogue.Read(ctx), weapon, ref player, dirX, dirY,
                        config.CritChance, config.CritMultiplier);
            player.NextShotAt = ctx.Timestamp + new TimeDuration(
                (long)(Math.Clamp(weapon.FireRateMs / config.Dexterity, 20f, 60000f) * 1000L));
            ctx.Db.Player.Identity.Update(player);
            return;
        }

        // The same path enemy fire takes. A separate implementation for players
        // would be a second place for patterns, spin and waves to be got wrong.
        FireVolley(ctx, player.ZoneId, weapon, 0, player.X, player.Y, dirX, dirY,
                   config.CritChance, config.CritMultiplier);

        // Dexterity divides: 2 is twice as fast, which is the way round a player
        // expects a stat called "attack speed" to work.
        float dex = config.Dexterity > 0.01f ? config.Dexterity : 1f;
        var fireRate = (ushort)Math.Clamp((int)(weapon.FireRateMs / dex), 20, 60000);

        player.NextShotAt = ctx.Timestamp + new TimeDuration(fireRate * 1000L);
        ctx.Db.Player.Identity.Update(player);
    }

    /// <summary>
    /// Fires a volley from anywhere, on any side.
    /// </summary>
    /// <remarks>
    /// The same code a player's shot goes through, so an enemy with a spread or a
    /// helix behaves identically to a player holding that weapon. A parallel
    /// implementation for enemies would be a second place to get patterns wrong.
    /// </remarks>
    private static void FireVolley(ReducerContext ctx, uint zone, WeaponDef weapon, byte faction,
                                   float x, float y, float dirX, float dirY,
                                   float critChance = 0f, float critMultiplier = 1f)
    {
        // Sampled once for the whole volley, deliberately. Every projectile is
        // rotated by the same amount, which is what keeps a spinning ring a ring.
        // Sampling it per projectile — or per group, if this ever grows sub-volleys
        // with their own patterns — tears the volley apart along the group
        // boundaries, and does it more the faster the weapon spins.
        float spinDeg = 0f;
        if (weapon.SpinDegreesPerSec != 0f)
        {
            double seconds = ctx.Timestamp.MicrosecondsSinceUnixEpoch / 1_000_000.0;
            spinDeg = (float)((seconds * weapon.SpinDegreesPerSec) % 360.0);
        }

        byte shots = weapon.Shots > 0 ? weapon.Shots : (byte)1;

        int slot = 0;
        foreach (var place in PlaceShots(weapon.PatternKind, shots, weapon.SpreadDegrees,
                                         weapon.PatternGroups))
        {
            // The slot is the placement's index, so it lines up with whichever
            // knob this pattern actually uses — angle for a ring, lateral offset
            // for a parallel, wave phase for a helix.
            var bullet = ProfileFor(weapon, slot, shots);
            slot++;

            float rad = (place.AngleDeg + spinDeg) * MathF.PI / 180f;
            float c = MathF.Cos(rad);
            float sn = MathF.Sin(rad);

            float sdx = dirX * c - dirY * sn;
            float sdy = dirX * sn + dirY * c;

            // Rolled per projectile, not per volley: a shotgun where every pellet
            // crits together is a different weapon from one where each pellet has
            // its own chance.
            //
            // Crit chance stays a property of the weapon while the damage range is
            // a property of the bullet, so the multiplier applies to whatever this
            // slot's profile happened to roll.
            ushort damage = RollDamage(ctx, bullet.DamageMin, bullet.DamageMax);
            bool crit = critChance > 0f && ctx.Rng.NextDouble() < critChance;
            if (crit)
            {
                float boosted = damage * critMultiplier;
                damage = boosted >= ushort.MaxValue ? ushort.MaxValue : (ushort)boosted;
            }

            ctx.Db.Shot.Insert(new Shot
            {
                Id = 0,
                Owner = ctx.Sender,
                Faction = faction,
                ZoneId = zone,
                OriginX = x + -sdy * place.Lateral,
                OriginY = y + sdx * place.Lateral,
                DirX = sdx,
                DirY = sdy,
                SpawnedAt = ctx.Timestamp,
                Speed = bullet.Speed,
                Damage = damage,
                Crit = crit,
                Element = bullet.Element,
                DebuffKind = bullet.DebuffKind,
                DebuffSeconds = bullet.DebuffSeconds,
                Size = bullet.Size,
                LifetimeMs = bullet.LifetimeMs,
                WaveAmplitude = weapon.WaveAmplitude,
                WaveFrequency = weapon.WaveFrequency,
                WavePhase = place.Phase,
                Tint = bullet.Colour,

                // The variant's art if it has its own, else the weapon's. The one
                // field in a profile that inherits; see BulletProfile.Tint.
                SpriteId = bullet.SpriteId != 0 ? bullet.SpriteId : weapon.BulletSpriteId,
            });
        }
    }

    /// <summary>Where one projectile in a volley starts, relative to the aim.</summary>
    public struct Placement
    {
        /// <summary>Rotation from the aim direction, in degrees.</summary>
        public float AngleDeg;

        /// <summary>Sideways offset of the spawn point, in tiles.</summary>
        public float Lateral;

        /// <summary>Starting point of this projectile's oscillation, in radians.</summary>
        public float Phase;
    }

    /// <summary>
    /// Lays out one volley.
    /// </summary>
    /// <remarks>
    /// Three independent knobs, which is what lets a handful of kinds cover most
    /// of the genre: rotate a shot, move where it starts, or shift where it is in
    /// its wave. A helix is not a special path — it is several waving shots whose
    /// phases are spread evenly, so they braid around each other.
    ///
    /// Mirrored by <c>VolleyMath.Place</c> on the client, which the editor's weapon
    /// preview draws. If the two disagree the server wins, and the preview shows a
    /// pattern the weapon does not fire.
    /// </remarks>
    private static IEnumerable<Placement> PlaceShots(byte kind, byte count, float spread,
                                                    byte groups = 0)
    {
        int n = count < 1 ? 1 : count;

        switch (kind)
        {
            case 1: // Spread — fanned across an arc, centred on the aim.
                for (int i = 0; i < n; i++)
                {
                    yield return new Placement
                    {
                        AngleDeg = n == 1 ? 0f : -spread * 0.5f + spread * i / (n - 1),
                    };
                }
                break;

            case 2: // Ring — evenly around a circle, the aim setting the phase.
                for (int i = 0; i < n; i++)
                {
                    yield return new Placement { AngleDeg = 360f * i / n };
                }
                break;

            case 3: // Parallel — same direction, spawn points spread sideways.
                for (int i = 0; i < n; i++)
                {
                    yield return new Placement
                    {
                        Lateral = n == 1 ? 0f : -spread * 0.5f + spread * i / (n - 1),
                    };
                }
                break;

            case 4: // Helix — same direction, phases spread evenly so they braid.
                for (int i = 0; i < n; i++)
                {
                    yield return new Placement { Phase = MathF.Tau * i / n };
                }
                break;

            case 5: // Cluster — several tight fans, spaced evenly around a circle.
            {
                // Two counts, not one: g directions with n/g bullets down each.
                // A Ring of 8 puts eight bullets 45 degrees apart; this puts four
                // pairs 90 degrees apart, which is a different thing to dodge —
                // you look for the gap between clusters rather than between shots.
                int g = groups < 1 ? 1 : groups;
                if (g > n)
                {
                    // More directions than bullets would emit empty clusters and
                    // silently fire fewer shots than the weapon says it does.
                    g = n;
                }
                int per = n / g;
                int spare = n - per * g;

                for (int d = 0; d < g; d++)
                {
                    float baseDeg = 360f * d / g;

                    // The remainder goes one extra bullet onto the first clusters
                    // rather than being dropped, so the volley always fires
                    // exactly Shots bullets however the count divides.
                    int here = per + (d < spare ? 1 : 0);
                    for (int j = 0; j < here; j++)
                    {
                        float off = here == 1
                            ? 0f
                            : -spread * 0.5f + spread * j / (here - 1);
                        yield return new Placement { AngleDeg = baseDeg + off };
                    }
                }
                break;
            }

            default: // Single, and anything unrecognised.
                for (int i = 0; i < n; i++)
                {
                    yield return default;
                }
                break;
        }
    }

    /// <summary>Rolls damage inclusive of both ends, using the reducer's own RNG.</summary>
    private static ushort RollDamage(ReducerContext ctx, ushort min, ushort max) =>
        max <= min ? min : (ushort)ctx.Rng.Next(min, max + 1);

    /// <summary>
    /// Which bullet fills a given slot of a volley.
    /// </summary>
    /// <remarks>
    /// An empty profile list means the weapon is a single-bullet weapon and the
    /// flat columns describe it. That is the authored meaning of empty, not a
    /// stand-in for a failure: the list cannot arrive empty by accident, because
    /// a client with stale bindings does not lose the column quietly — it
    /// misreads every column after it and throws.
    ///
    /// Both assignments are arithmetic over the slot count rather than a stored
    /// per-slot table, so changing a weapon's shot count can never leave the
    /// assignment half-updated and firing the wrong bullets.
    ///
    /// Mirrored by <c>VolleyMath.VariantIndex</c> on the client. Change both.
    /// </remarks>
    private static BulletProfile ProfileFor(WeaponDef weapon, int slot, byte shots)
    {
        var profiles = weapon.Profiles;
        if (profiles is null || profiles.Count == 0)
        {
            return new BulletProfile
            {
                Element = weapon.Element,
                DebuffKind = weapon.DebuffKind,
                DebuffSeconds = weapon.DebuffSeconds,
                DamageMin = weapon.DamageMin,
                DamageMax = weapon.DamageMax,
                Speed = weapon.ProjectileSpeed,
                Size = weapon.ProjectileSize,
                LifetimeMs = weapon.ProjectileLifetimeMs,
                Tint = (weapon.Tint & 0xFFFFFFu) | ((uint)weapon.BulletSpriteId << 24),
            };
        }

        if (weapon.ProfileAssignment == 1)
        {
            // Block — contiguous runs, so half a ring is one bullet and half the
            // other. Clamped because integer division reaches Count exactly when
            // slot is the last of a volley whose count divides evenly.
            int n = shots > 0 ? shots : 1;
            int index = slot * profiles.Count / n;
            return profiles[index >= profiles.Count ? profiles.Count - 1 : index];
        }

        if (weapon.ProfileAssignment == 2)
        {
            // Edges — the two outermost slots take the first variant, everything
            // between them takes the rest.
            //
            // The one shape Cycle and Block cannot express, and the one a shotgun
            // wants: heavy flankers with a weaker core, so where you stand in the
            // cone decides what hits you. Cycle would scatter the heavy pellets
            // through the fan and Block would put them all down one side.
            //
            // Meaningful for the patterns whose slots are ordered across an arc —
            // Spread, Parallel, and each fan of a Cluster. On a Ring "outermost"
            // has no meaning: slot 0 and slot n-1 are simply adjacent, and this
            // degrades to two marked bullets rather than to something wrong.
            //
            // Measured within a fan, not across the volley. A Cluster fires
            // several fans from one volley, so the outermost slots of the *whole*
            // list are the first bullet of the first fan and the last of the last
            // — which leaves every fan between them with no flankers, and no fan
            // at all with the pair this is for. PatternGroups is the fan count and
            // is 0 for every other pattern, so they divide by one and are
            // unchanged.
            int n = shots > 0 ? shots : 1;
            int groups = weapon.PatternGroups > 0 ? weapon.PatternGroups : 1;
            int per = groups > 0 ? n / groups : n;
            if (per < 1)
            {
                per = n;
            }

            // With one or two bullets to a fan every slot is an edge, which is
            // the honest answer rather than a special case: a pair has no middle.
            int within = per > 0 ? slot % per : slot;
            if (within == 0 || within == per - 1)
            {
                return profiles[0];
            }
            if (profiles.Count == 1)
            {
                return profiles[0];
            }
            // Interior slots cycle through whatever is left, so a three-variant
            // mix is edges plus an alternating core rather than a silent drop.
            return profiles[1 + (within - 1) % (profiles.Count - 1)];
        }

        // Cycle — alternating around the volley.
        return profiles[slot % profiles.Count];
    }

    /// <summary>Inserts or replaces an enemy archetype. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertEnemyDef(ReducerContext ctx, ushort id, string name,
                                      ushort maxHp, float radius, float speed,
                                      byte behaviour, float aggroRange, float preferredRange,
                                      float attackRange, ushort weaponId, ushort touchDamage,
                                      byte orbitPivot, bool isBoss, ushort maxEnergy,
                                      uint colour, List<Resistance> resist)
    {
        if (id == 0)
        {
            throw new Exception("enemy id 0 is reserved");
        }

        var def = new EnemyDef
        {
            Id = id,
            Name = name,
            MaxHp = maxHp < 1 ? (ushort)1 : maxHp,
            Radius = Math.Clamp(radius, 0.1f, 5f),
            Speed = Math.Clamp(speed, 0f, 20f),
            Behaviour = behaviour > 4 ? (byte)0 : behaviour,
            AggroRange = Math.Clamp(aggroRange, 0f, 60f),
            PreferredRange = Math.Clamp(preferredRange, 0f, 40f),
            AttackRange = Math.Clamp(attackRange, 0f, 60f),
            WeaponId = weaponId,
            TouchDamage = touchDamage,
            OrbitPivot = orbitPivot > 1 ? (byte)0 : orbitPivot,
            IsBoss = isBoss,
            Colour = colour,
            MaxEnergy = maxEnergy,
            // Never left null. A null list column throws on serialisation, which
            // surfaces as an opaque ArgumentNull from inside the bindings rather
            // than as anything naming this reducer.
            Resist = resist ?? new List<Resistance>(),
        };

        if (ctx.Db.EnemyDef.Id.Find(id) is null)
        {
            ctx.Db.EnemyDef.Insert(def);
        }
        else
        {
            ctx.Db.EnemyDef.Id.Update(def);
        }
        Log.Info($"enemy {id} \"{name}\": {def.MaxHp}hp, behaviour {def.Behaviour}, weapon {def.WeaponId}");
    }

    /// <summary>Spawns one enemy. A development tool, not a spawner.</summary>
    [SpacetimeDB.Reducer]
    public static void SpawnEnemy(ReducerContext ctx, ushort defId, float x, float y)
    {
        if (ctx.Db.EnemyDef.Id.Find(defId) is not { } def)
        {
            throw new Exception($"no enemy {defId} in the catalogue");
        }

        ctx.Db.Enemy.Insert(new Enemy
        {
            Id = 0,
            DefId = defId,
            X = Clamp(x),
            Y = Clamp(y),
            Cell = CellOf(Clamp(x), Clamp(y)),
            HomeX = Clamp(x),
            HomeY = Clamp(y),
            SpawnerId = 0,
            PhaseIndex = 0,
            PhaseStartedAt = ctx.Timestamp,
            Hp = def.MaxHp,
            Energy = def.MaxEnergy,
            NextShotAt = ctx.Timestamp,
            LastHitAt = ctx.Timestamp,
            LastDamage = 0,
            // Its own phase, so a group does not move in lockstep.
            Phase = (float)(ctx.Rng.NextDouble() * Math.Tau),
            ZoneId = RealmZone,
        });
    }

    /// <summary>Removes every live enemy. Leaves the catalogue alone.</summary>
    [SpacetimeDB.Reducer]
    public static void ClearEnemies(ReducerContext ctx)
    {
        var ids = ctx.Db.Enemy.Iter().Select(e => e.Id).ToList();
        foreach (ulong id in ids)
        {
            ctx.Db.Enemy.Id.Delete(id);
        }
    }

    /// <summary>Sets the caller's display name.</summary>
    [SpacetimeDB.Reducer]
    public static void SetName(ReducerContext ctx, string name)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { } player)
        {
            return;
        }

        string trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }
        // Capped rather than rejected: a long name is a nuisance in a log line,
        // not an attack, and refusing it outright would leave the player unnamed.
        player.Name = trimmed.Length > 24 ? trimmed[..24] : trimmed;
        ctx.Db.Player.Identity.Update(player);
    }

    /// <summary>Inserts or replaces a catalogue item. Called by the Unity editor.</summary>
    /// <remarks>
    /// Every authored equipment asset lands here, whatever its kind. Weapons keep
    /// their own row in <c>WeaponDef</c> for the things only a weapon has; this is
    /// the row that answers "what is item 7" for anything that has to hold, move
    /// or draw one without caring what kind it is.
    ///
    /// Ids are one space across all four kinds, because a bag and an inventory
    /// slot address items by id alone. A sword and a helmet both called 1 would be
    /// the same item to everything downstream — the editor push refuses that
    /// before it gets here.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void UpsertItemDef(ReducerContext ctx, ushort id, string name,
                                     byte kind, ushort maxStack, uint tint, byte tier,
                                     byte width, byte height)
    {
        if (id == 0)
        {
            throw new Exception("item id 0 is reserved for \"no item\"");
        }

        // The top two bits of the id are the kind. Checked rather than trusted,
        // because the client packs them and the two halves arriving inconsistent
        // would mean a bag holding an id that resolves to a row claiming to be a
        // different kind — which an equipment slot's kind filter would then be
        // deciding on wrong information.
        byte packedKind = (byte)(id >> 14);
        if (packedKind != kind)
        {
            throw new Exception($"item {id} says kind {kind} but its id encodes "
                              + $"kind {packedKind}");
        }

        var def = new ItemDef
        {
            Id = id,
            Name = name,
            Kind = kind > 3 ? (byte)0 : kind,
            MaxStack = maxStack < 1 ? (ushort)1 : maxStack,
            Tint = tint & 0xFFFFFFu,
            Tier = tier,
            // Clamped to the grid: an item taller than the vault could never be
            // stored anywhere, and would look like a vault that refuses loot.
            // Clamped to the vault, the largest grid there is. Anything bigger
            // could never be stored anywhere and would read as a vault refusing
            // loot rather than as an item authored wrong.
            Width = (byte)Math.Clamp((int)width, 1, ContainerSize(ContainerVault).w),
            Height = (byte)Math.Clamp((int)height, 1, ContainerSize(ContainerVault).h),
        };

        if (ctx.Db.ItemDef.Id.Find(id) is null)
        {
            ctx.Db.ItemDef.Insert(def);
        }
        else
        {
            ctx.Db.ItemDef.Id.Update(def);
        }
    }

    /// <summary>Inserts or replaces one loot table entry. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertLoot(ReducerContext ctx, ushort poolId, byte index,
                                  ushort itemId, float chancePercent, ushort count)
    {
        var entry = new LootEntry
        {
            Key = ((uint)poolId << 8) | index,
            PoolId = poolId,
            ItemId = itemId,
            ChancePercent = Math.Clamp(chancePercent, 0f, 100f),
            Count = count < 1 ? (ushort)1 : count,
        };

        if (ctx.Db.LootEntry.Key.Find(entry.Key) is null)
        {
            ctx.Db.LootEntry.Insert(entry);
        }
        else
        {
            ctx.Db.LootEntry.Key.Update(entry);
        }
    }

    /// <summary>Removes every entry in one pool.</summary>
    [SpacetimeDB.Reducer]
    public static void ClearLoot(ReducerContext ctx, ushort poolId)
    {
        foreach (uint key in ctx.Db.LootEntry.PoolId.Filter(poolId).Select(e => e.Key).ToList())
        {
            ctx.Db.LootEntry.Key.Delete(key);
        }
    }

    /// <summary>Inserts or replaces a loot pool. Called by the Unity editor.</summary>
    [SpacetimeDB.Reducer]
    public static void UpsertLootPool(ReducerContext ctx, ushort poolId, string name, byte bagKind)
    {
        if (poolId == 0)
        {
            throw new Exception("pool id 0 is reserved for \"drops nothing\"");
        }

        var pool = new LootPoolDef { Id = poolId, Name = name, BagKind = bagKind };
        if (ctx.Db.LootPoolDef.Id.Find(poolId) is null)
        {
            ctx.Db.LootPoolDef.Insert(pool);
        }
        else
        {
            ctx.Db.LootPoolDef.Id.Update(pool);
        }
    }

    /// <summary>Gives an enemy one weighted chance at a pool. Called by the Unity editor.</summary>
    /// <remarks>
    /// A <paramref name="poolId"/> of 0 is the "nothing drops" slice of the roll,
    /// and is the only id allowed to name no pool.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void UpsertEnemyLoot(ReducerContext ctx, ushort enemyDefId, byte index,
                                       ushort poolId, uint weight)
    {
        var row = new EnemyLoot
        {
            Key = ((uint)enemyDefId << 8) | index,
            EnemyDefId = enemyDefId,
            PoolId = poolId,
            Weight = weight,
        };

        if (ctx.Db.EnemyLoot.Key.Find(row.Key) is null)
        {
            ctx.Db.EnemyLoot.Insert(row);
        }
        else
        {
            ctx.Db.EnemyLoot.Key.Update(row);
        }
    }

    /// <summary>Removes every pool weighting for one archetype.</summary>
    [SpacetimeDB.Reducer]
    public static void ClearEnemyLoot(ReducerContext ctx, ushort enemyDefId)
    {
        foreach (uint key in ctx.Db.EnemyLoot.EnemyDefId.Filter(enemyDefId).Select(e => e.Key).ToList())
        {
            ctx.Db.EnemyLoot.Key.Delete(key);
        }
    }

    private static readonly string[] ElementNames = { "physical", "fire", "ice", "poison", "arcane" };
    private static readonly string[] DebuffNames = { "stun", "slow" };

    private static string ElementName(byte e) => e < ElementNames.Length ? ElementNames[e] : "unknown";

    /// <summary>
    /// Credits damage to whoever dealt it.
    /// </summary>
    /// <remarks>
    /// Read-modify-write on a row per (target, attacker, element). The row count
    /// is bounded by who is actually fighting what, not by how long the fight
    /// lasts, which is what keeps this affordable at bullet-hell fire rates.
    /// </remarks>
    private static void CreditDamage(ReducerContext ctx, ulong enemyId, Identity attacker,
                                     byte element, ushort amount, bool crit)
    {
        foreach (var row in ctx.Db.DamageTally.EnemyId.Filter(enemyId))
        {
            if (row.Attacker != attacker || row.Element != element)
            {
                continue;
            }
            var updated = row;
            updated.Damage += amount;
            updated.Hits += 1;
            updated.Crits += crit ? 1ul : 0ul;
            updated.Best = amount > row.Best ? amount : row.Best;
            ctx.Db.DamageTally.Id.Update(updated);
            return;
        }

        ctx.Db.DamageTally.Insert(new DamageTally
        {
            Id = 0,
            EnemyId = enemyId,
            Attacker = attacker,
            Element = element,
            Damage = amount,
            Hits = 1,
            Crits = crit ? 1ul : 0ul,
            Best = amount,
        });
    }

    /// <summary>
    /// Applies a debuff and credits only the time it actually added.
    /// </summary>
    /// <remarks>
    /// Extending a stun from 1.0s to 1.4s remaining credits 0.4s, not 1.4s.
    /// Crediting the full duration would let a second player firing into an
    /// existing stun claim as much as the one who landed it, and stacking
    /// attackers would report more stun time than the fight contained.
    /// </remarks>
    private static void ApplyDebuff(ReducerContext ctx, ref Enemy enemy, Identity attacker,
                                    byte kind, float seconds)
    {
        if (kind == 0 || seconds <= 0f)
        {
            return;
        }

        // Anything that is not stun or slow means nothing to an enemy. Armour
        // break in particular is a player-only effect — enemies subtract no flat
        // defence, so there is nothing for it to strip.
        //
        // Tested for rather than left to the ternary below, which sorted kinds
        // into "stun" and "everything else" and would quietly turn an armour-break
        // bullet into a slow the moment a third kind existed.
        if (kind != 1 && kind != 2)
        {
            return;
        }

        long nowUs = ctx.Timestamp.MicrosecondsSinceUnixEpoch;
        long addUs = (long)(seconds * 1_000_000f);

        long current = kind == 1
            ? enemy.StunnedUntil.MicrosecondsSinceUnixEpoch
            : enemy.SlowedUntil.MicrosecondsSinceUnixEpoch;

        // Refreshed to the longer of the two, not stacked on the end. Stacking
        // is unbounded: a few players with stun weapons would hold a boss
        // permanently, and each would be credited in full for doing it.
        long active = current > nowUs ? current : nowUs;
        long until = Math.Max(current, nowUs + addUs);

        // Only the time this hit actually added. Firing into an existing stun
        // buys the difference, which is often nothing — otherwise the second
        // player to land one is credited as much as the one who started it, and
        // the totals report more stun than the fight contained.
        float credited = (until - active) / 1_000_000f;

        if (kind == 1)
        {
            enemy.StunnedUntil = new Timestamp(until);
        }
        else
        {
            enemy.SlowedUntil = new Timestamp(until);
        }

        byte tallyKind = (byte)(kind - 1);
        foreach (var row in ctx.Db.DebuffTally.EnemyId.Filter(enemy.Id))
        {
            if (row.Attacker != attacker || row.Kind != tallyKind)
            {
                continue;
            }
            var updated = row;
            updated.Seconds += credited;
            updated.Applications += 1;
            ctx.Db.DebuffTally.Id.Update(updated);
            return;
        }

        ctx.Db.DebuffTally.Insert(new DebuffTally
        {
            Id = 0,
            EnemyId = enemy.Id,
            Attacker = attacker,
            Kind = tallyKind,
            Seconds = credited,
            Applications = 1,
        });
    }

    /// <summary>Folds a finished fight's tallies into the players' lifetime totals.</summary>
    private static void BankStats(ReducerContext ctx, ulong enemyId, Identity? killer)
    {
        foreach (var tally in ctx.Db.DamageTally.EnemyId.Filter(enemyId).ToList())
        {
            var stat = FindStat(ctx, tally.Attacker, tally.Element);
            stat.Damage += tally.Damage;
            stat.Hits += tally.Hits;
            stat.Crits += tally.Crits;
            stat.Best = tally.Best > stat.Best ? tally.Best : stat.Best;
            if (killer is { } k && k == tally.Attacker)
            {
                stat.Kills += 1;
            }
            ctx.Db.PlayerStat.Id.Update(stat);
        }

        foreach (var tally in ctx.Db.DebuffTally.EnemyId.Filter(enemyId).ToList())
        {
            // Banked against physical, because debuff time is not damage and
            // giving it its own element row would put a zero-damage entry in
            // every per-element breakdown.
            var stat = FindStat(ctx, tally.Attacker, 0);
            stat.DebuffSeconds += tally.Seconds;
            ctx.Db.PlayerStat.Id.Update(stat);
        }
    }

    private static PlayerStat FindStat(ReducerContext ctx, Identity identity, byte element)
    {
        foreach (var row in ctx.Db.PlayerStat.Identity.Filter(identity))
        {
            if (row.Element == element)
            {
                return row;
            }
        }
        return ctx.Db.PlayerStat.Insert(new PlayerStat
        {
            Id = 0,
            Identity = identity,
            Element = element,
        });
    }

    /// <summary>
    /// Rolls an enemy's loot, drops it, and records the kill.
    /// </summary>
    /// <remarks>
    /// The log line is written from what was actually inserted, not from what was
    /// rolled. A log that reports drops the world does not contain is worse than
    /// no log, because it is the thing you would trust when the two disagree.
    /// </remarks>
    /// <summary>
    /// Picks one of an enemy's loot pools, or 0 for nothing.
    /// </summary>
    /// <remarks>
    /// A running total rather than building a list: the table is walked once and
    /// nothing is allocated. Weights are unsigned and summed into a long, so a
    /// designer cannot overflow the total into a negative and make the whole roll
    /// silently pick the first entry every time.
    ///
    /// An enemy with no rows at all drops nothing, which is the right default —
    /// it is also what every enemy authored before loot existed will do.
    ///
    /// Mirrored by <c>LootMath.RollPool</c> on the client, which the editor's loot
    /// simulator runs. Change both.
    /// </remarks>
    private static ushort RollPool(ReducerContext ctx, ushort enemyDefId)
    {
        long total = 0;
        foreach (var row in ctx.Db.EnemyLoot.EnemyDefId.Filter(enemyDefId))
        {
            total += row.Weight;
        }
        if (total <= 0)
        {
            return 0;
        }

        long pick = (long)(ctx.Rng.NextDouble() * total);
        foreach (var row in ctx.Db.EnemyLoot.EnemyDefId.Filter(enemyDefId))
        {
            pick -= row.Weight;
            if (pick < 0)
            {
                return row.PoolId;
            }
        }

        // Only reachable if NextDouble returned exactly 1.0, which its contract
        // says it will not. Falling through to "nothing" rather than throwing:
        // a missed drop is a far better failure than a reducer that aborts the
        // kill it was recording.
        return 0;
    }

    /// <summary>
    /// Ends a character. The account survives; the character does not.
    /// </summary>
    /// <remarks>
    /// Carried and equipped items are destroyed. The secure pocket is banked
    /// instead — its contents go to the vault, which is what a secure pocket is
    /// for: the one thing you get to bring home from a run that went wrong.
    ///
    /// The character row is deleted, not reset. It is the thing that dies, and a
    /// reset one would keep its id, its name, and any progression later hung off
    /// it — a new character wearing an old character's history.
    ///
    /// The player row survives with no character, which is the character screen.
    /// It is keyed by account and the account is still connected; deleting it
    /// would log someone out of their own game for dying.
    /// </remarks>
    private static void KillCharacter(ReducerContext ctx, ref Player player)
    {
        ulong characterId = player.CharacterId;
        string who = ctx.Db.Character.Id.Find(characterId) is { } character
            ? character.Name
            : player.Name;

        var slots = SlotsOf(ctx, characterId);
        var vault = VaultOf(ctx, player.Identity);

        int lost = 0;
        int banked = 0;
        foreach (var slot in slots)
        {
            if (slot.Container != 2)
            {
                lost++;
                continue;
            }

            if (PutIn(ctx, vault, ContainerVault, slot.ItemId, slot.Count) >= slot.Count)
            {
                banked++;
            }
            else
            {
                // No room in the grid. Said out loud rather than silently, so a
                // player who tucked something away and did not get it can be told
                // why instead of assuming the pocket does not work.
                Log.Warn($"{who}'s vault has no room — secure item {slot.ItemId} was lost");
                lost++;
            }
        }

        SaveVault(ctx, player.Identity, vault);

        if (ctx.Db.Inventory.CharacterId.Find(characterId) is not null)
        {
            ctx.Db.Inventory.CharacterId.Delete(characterId);
        }
        if (characterId != 0)
        {
            ctx.Db.Character.Id.Delete(characterId);
        }

        var (sx, sy) = SpawnPoint(ctx);
        player.CharacterId = 0;
        player.Hp = player.MaxHp;
        player.X = sx;
        player.Y = sy;
        // Death always sends you back to the realm, wherever you fell.
        player.ZoneId = RealmZone;
        player.LootingBag = 0;
        player.RegenPool = 0f;
        player.WeaponId = 0;
        ctx.Db.Player.Identity.Update(player);

        Log.Info($"{who} died — {lost} item(s) lost, {banked} banked from the secure pocket");
    }

    // ---- Inventory --------------------------------------------------------

    /// <summary>Reads a player's slots, or an empty list if they have none yet.</summary>
    private static List<GridItem> SlotsOf(ReducerContext ctx, ulong characterId) =>
        ctx.Db.Inventory.CharacterId.Find(characterId) is { } row
            ? row.Slots
            : new List<GridItem>();

    /// <summary>
    /// Re-places anything sitting outside its container.
    /// </summary>
    /// <remarks>
    /// A grid shrinking, or an item growing, strands whatever no longer fits: it
    /// is still owned and still replicated, but every reducer that could move it
    /// refuses because its cell is out of bounds. It becomes permanently stuck,
    /// and the only symptom is a cell the panel cannot draw.
    ///
    /// Re-placed rather than dropped. An item that cannot be reached is a bug; an
    /// item the server deleted to tidy up is lost property. With nowhere to put
    /// it, it stays where it is and stays stuck — recoverable the moment room
    /// appears.
    /// </remarks>
    private static void Rehome(ReducerContext ctx, List<GridItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var entry = items[i];
            var (w, h) = FootprintIn(ctx, entry.Container, entry.ItemId);
            if (FitsAt(ctx, items, entry.Container, entry.X, entry.Y, w, h, i))
            {
                continue;
            }

            if (FindSpot(ctx, items, entry.Container, w, h, out byte x, out byte y))
            {
                Log.Info($"item {entry.ItemId} no longer fits at container "
                       + $"{entry.Container} ({entry.X},{entry.Y}) — moved to ({x},{y})");
                entry.X = x;
                entry.Y = y;
                items[i] = entry;
            }
            else if (entry.Container != ContainerPack
                     && PutIn(ctx, items, ContainerPack, entry.ItemId, entry.Count) >= entry.Count)
            {
                Log.Info($"item {entry.ItemId} no longer fits in container "
                       + $"{entry.Container} — moved to the pack");
                items.RemoveAt(i);
                i--;
            }
        }
    }

    /// <summary>Writes a player's slots back, inserting the row the first time.</summary>
    private static void SaveSlots(ReducerContext ctx, ulong characterId, List<GridItem> slots)
    {
        Rehome(ctx, slots);

        var row = new Inventory { CharacterId = characterId, Slots = slots };
        if (ctx.Db.Inventory.CharacterId.Find(characterId) is null)
        {
            ctx.Db.Inventory.Insert(row);
        }
        else
        {
            ctx.Db.Inventory.CharacterId.Update(row);
        }
    }

    /// <summary>An item's footprint, defaulting to one cell.</summary>
    /// <remarks>
    /// One cell for anything the catalogue has not described, rather than
    /// refusing to place it. A thing with no authored size is still a thing the
    /// player owns, and the smallest footprint cannot fail to fit somewhere.
    /// </remarks>
    /// <summary>An item's footprint, defaulting to one cell.</summary>
    /// <remarks>
    /// One cell for anything the catalogue has not described, rather than
    /// refusing to place it. A thing with no authored size is still a thing the
    /// player owns, and the smallest footprint cannot fail to fit somewhere.
    /// </remarks>
    private static (byte w, byte h) FootprintOf(ReducerContext ctx, ushort itemId)
    {
        if (ctx.Db.ItemDef.Id.Find(itemId) is not { } def)
        {
            return (1, 1);
        }
        return (def.Width < 1 ? (byte)1 : def.Width, def.Height < 1 ? (byte)1 : def.Height);
    }

    /// <summary>The footprint an item takes in a given container.</summary>
    /// <remarks>
    /// Always one cell when equipped. A rifle in your hands is not in your pack,
    /// so it costs no room — without this the three equipped cells could not hold
    /// anything bigger than a ring.
    /// </remarks>
    private static (byte w, byte h) FootprintIn(ReducerContext ctx, byte container, ushort itemId) =>
        container == ContainerEquipped ? ((byte)1, (byte)1) : FootprintOf(ctx, itemId);

    /// <summary>Whether two rectangles share any cell.</summary>
    private static bool Overlaps(int ax, int ay, int aw, int ah,
                                 int bx, int by, int bw, int bh) =>
        !(ax + aw <= bx || bx + bw <= ax || ay + ah <= by || by + bh <= ay);

    /// <summary>
    /// Whether a footprint fits at a cell of one container.
    /// </summary>
    /// <remarks>
    /// <paramref name="ignore"/> is an index to disregard, so a move can test a
    /// destination overlapping where the item already is. Without it, nudging
    /// something one cell fails against itself.
    /// </remarks>
    private static bool FitsAt(ReducerContext ctx, List<GridItem> items, byte container,
                               byte x, byte y, byte w, byte h, int ignore)
    {
        var (cw, ch) = ContainerSize(container);
        if (x + w > cw || y + h > ch)
        {
            return false;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (i == ignore || items[i].Container != container)
            {
                continue;
            }
            var other = items[i];
            var (ow, oh) = FootprintIn(ctx, container, other.ItemId);
            if (Overlaps(x, y, w, h, other.X, other.Y, ow, oh))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The first cell a footprint fits in, scanning rows then columns.</summary>
    /// <remarks>
    /// Reading order, so a container always packs the same way and an item put
    /// back lands where it came from when nothing else moved.
    /// </remarks>
    private static bool FindSpot(ReducerContext ctx, List<GridItem> items, byte container,
                                 byte w, byte h, out byte fx, out byte fy)
    {
        var (cw, ch) = ContainerSize(container);
        for (byte y = 0; y + h <= ch; y++)
        {
            for (byte x = 0; x + w <= cw; x++)
            {
                if (FitsAt(ctx, items, container, x, y, w, h, -1))
                {
                    fx = x;
                    fy = y;
                    return true;
                }
            }
        }
        fx = 0;
        fy = 0;
        return false;
    }

    /// <summary>Index of whatever occupies a cell of a container, or -1.</summary>
    private static int AtCell(ReducerContext ctx, List<GridItem> items,
                              byte container, byte x, byte y)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Container != container)
            {
                continue;
            }
            var (w, h) = FootprintIn(ctx, container, item.ItemId);
            if (x >= item.X && x < item.X + w && y >= item.Y && y < item.Y + h)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Puts a stack into a container, merging onto a matching stack first.
    /// </summary>
    /// <remarks>
    /// Returns how many actually fitted, which the caller must respect: a partial
    /// take has to leave the remainder where it was. Silently dropping the
    /// overflow would destroy loot on a full pack, and the player could not tell
    /// that from a bad drop roll.
    ///
    /// Mirrored for the pack, with <c>FitsAt</c>, <c>FindSpot</c> and
    /// <c>TakeAllFromBag</c>, by <c>LootMath</c> on the client for the editor's
    /// loot simulator. Change both.
    /// </remarks>
    private static ushort PutIn(ReducerContext ctx, List<GridItem> items, byte container,
                                ushort itemId, ushort count)
    {
        if (count == 0)
        {
            return 0;
        }

        ushort max = ctx.Db.ItemDef.Id.Find(itemId) is { } def && def.MaxStack > 1
            ? def.MaxStack
            : (ushort)1;
        ushort placed = 0;

        if (max > 1)
        {
            for (int i = 0; i < items.Count && placed < count; i++)
            {
                if (items[i].Container != container || items[i].ItemId != itemId
                    || items[i].Count >= max)
                {
                    continue;
                }
                var stack = items[i];
                ushort take = (ushort)Math.Min((ushort)(max - stack.Count), (ushort)(count - placed));
                stack.Count += take;
                items[i] = stack;
                placed += take;
            }
        }

        var (fw, fh) = FootprintIn(ctx, container, itemId);
        while (placed < count)
        {
            if (!FindSpot(ctx, items, container, fw, fh, out byte x, out byte y))
            {
                break;
            }
            ushort take = Math.Min((ushort)(count - placed), max);
            items.Add(new GridItem
            {
                Container = container,
                X = x,
                Y = y,
                ItemId = itemId,
                Count = take,
            });
            placed += take;
        }
        return placed;
    }

    /// <summary>Puts items in the pack. Returns how many fitted.</summary>
    private static ushort AddToBackpack(ReducerContext ctx, List<GridItem> slots,
                                        ushort itemId, ushort count) =>
        PutIn(ctx, slots, ContainerPack, itemId, count);

    /// <summary>
    /// Keeps the player's live weapon in step with what is in their weapon slot.
    /// </summary>
    /// <remarks>
    /// Without this the equipped panel is decoration: you could loot a weapon,
    /// put it in the slot, and still be firing the old one. Recomputed from the
    /// slots after every change rather than tracked alongside them, so there is
    /// one answer to "what am I holding" and it is the inventory.
    ///
    /// Weapon ids and catalogue ids are the same number for kind 0, which is what
    /// makes this a direct assignment rather than a lookup.
    /// </remarks>
    private static void SyncWeapon(ReducerContext ctx, Identity who, List<GridItem> slots)
    {
        if (ctx.Db.Player.Identity.Find(who) is not { } player)
        {
            return;
        }

        ushort weapon = 0;
        foreach (var slot in slots)
        {
            if (slot.Container != 1 || slot.Count == 0)
            {
                continue;
            }
            if (ctx.Db.ItemDef.Id.Find(slot.ItemId) is { Kind: 0 })
            {
                weapon = slot.ItemId;
                break;
            }
        }

        if (player.WeaponId != weapon)
        {
            player.WeaponId = weapon;

            // Drawn loaded. A character that spawned with an empty magazine spent
            // its first trigger pull reloading, which reads as the gun being
            // broken — and the same happened every time you swapped weapons.
            player.Ammo = ctx.Db.WeaponDef.Id.Find(weapon) is { Magazine: > 0 } drawn
                ? drawn.Magazine
                : (ushort)0;
            player.ReloadAtUs = 0ul;

            ctx.Db.Player.Identity.Update(player);
            Log.Info($"{player.Name} is now holding weapon {weapon}");
        }
    }

    /// <summary>
    /// Opens a bag, rooting the player until it is closed.
    /// </summary>
    /// <remarks>
    /// Opening is what costs something, not each item taken. Once open, taking is
    /// instant and free — the danger is that you are standing still in a place
    /// where a moment ago there was a fight, and the decision is how long you are
    /// willing to stay there.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void OpenBag(ReducerContext ctx, ulong bagId)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }
        if (ctx.Db.LootDrop.Id.Find(bagId) is not { } bag)
        {
            throw new Exception("that bag is gone");
        }

        float dx = player.X - bag.X;
        float dy = player.Y - bag.Y;
        if (bag.ZoneId != player.ZoneId || dx * dx + dy * dy > PickupRange * PickupRange)
        {
            throw new Exception("too far from that bag");
        }

        player.LootingBag = bagId;
        ctx.Db.Player.Identity.Update(player);
    }

    /// <summary>Closes whatever bag is open, freeing the player to move.</summary>
    /// <remarks>
    /// Takes no argument on purpose. A client closing "the bag it thinks it has
    /// open" could disagree with the server about which that is, and the failure
    /// would be a player rooted forever with no menu to close.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void CloseBag(ReducerContext ctx)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { LootingBag: not 0 } player)
        {
            return;
        }
        player.LootingBag = 0;
        ctx.Db.Player.Identity.Update(player);
    }

    /// <summary>
    /// Takes one entry out of a bag on the ground.
    /// </summary>
    /// <remarks>
    /// Addressed by item id rather than by index into the bag's list. Two clients
    /// reaching into the same bag would otherwise race: the first take shifts
    /// every later index, and the second player takes something they did not
    /// click.
    ///
    /// The range is re-checked here even though the client only shows a bag it
    /// thinks is in reach. Where the player is standing is a server fact.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void TakeFromBag(ReducerContext ctx, ulong bagId, ushort itemId)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }
        if (ctx.Db.LootDrop.Id.Find(bagId) is not { } bag)
        {
            // Already emptied or expired. Not an error: two players can reach the
            // same bag, and losing the race is an ordinary outcome.
            return;
        }

        if (player.LootingBag != bagId)
        {
            // Taking requires having opened it, because opening is what roots
            // you. Without this a client could skip the menu entirely and loot
            // at a run, which is the one thing the whole design rests on.
            throw new Exception("open the bag first");
        }

        float dx = player.X - bag.X;
        float dy = player.Y - bag.Y;
        if (bag.ZoneId != player.ZoneId || dx * dx + dy * dy > PickupRange * PickupRange)
        {
            throw new Exception("too far from that bag");
        }

        int at = -1;
        for (int i = 0; i < bag.Items.Count; i++)
        {
            if (bag.Items[i].ItemId == itemId) { at = i; break; }
        }
        if (at < 0)
        {
            return;
        }

        var entry = bag.Items[at];
        var slots = SlotsOf(ctx, player.CharacterId);
        ushort placed = AddToBackpack(ctx, slots, entry.ItemId, entry.Count);
        if (placed == 0)
        {
            throw new Exception("no room for that");
        }

        // The remainder stays in the bag rather than vanishing, so a full
        // inventory costs the player nothing they can't come back for.
        if (placed >= entry.Count)
        {
            bag.Items.RemoveAt(at);
        }
        else
        {
            entry.Count -= placed;
            bag.Items[at] = entry;
        }

        SaveSlots(ctx, player.CharacterId, slots);
        SyncWeapon(ctx, ctx.Sender, slots);

        // What moved, not what was asked for. A partial take is the interesting
        // case and the one a player would otherwise misread as a whole one.
        string name = ctx.Db.ItemDef.Id.Find(entry.ItemId) is { } taken
            ? taken.Name : $"item {entry.ItemId}";
        Log.Info($"{player.Name} took {placed}x {name}"
               + (placed < entry.Count ? $" ({entry.Count - placed} left, no room)" : ""));

        // An empty bag is deleted rather than left as a husk to walk over.
        if (bag.Items.Count == 0)
        {
            ctx.Db.LootDrop.Id.Delete(bagId);
        }
        else
        {
            ctx.Db.LootDrop.Id.Update(bag);
        }
    }

    /// <summary>Takes everything a bag holds that will fit.</summary>
    /// <remarks>
    /// The common case, and one call rather than one per item — reaching into a
    /// bag six times is six transactions and six chances to race another player.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void TakeAllFromBag(ReducerContext ctx, ulong bagId)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player
            || ctx.Db.LootDrop.Id.Find(bagId) is not { } bag)
        {
            return;
        }

        if (player.LootingBag != bagId)
        {
            // Taking requires having opened it, because opening is what roots
            // you. Without this a client could skip the menu entirely and loot
            // at a run, which is the one thing the whole design rests on.
            throw new Exception("open the bag first");
        }

        float dx = player.X - bag.X;
        float dy = player.Y - bag.Y;
        if (bag.ZoneId != player.ZoneId || dx * dx + dy * dy > PickupRange * PickupRange)
        {
            throw new Exception("too far from that bag");
        }

        var slots = SlotsOf(ctx, player.CharacterId);
        var left = new List<BagItem>();
        var took = new List<string>();
        foreach (var entry in bag.Items)
        {
            ushort placed = AddToBackpack(ctx, slots, entry.ItemId, entry.Count);
            if (placed > 0)
            {
                string name = ctx.Db.ItemDef.Id.Find(entry.ItemId) is { } def
                    ? def.Name : $"item {entry.ItemId}";
                took.Add(placed > 1 ? $"{placed}x {name}" : name);
            }
            if (placed < entry.Count)
            {
                left.Add(new BagItem { ItemId = entry.ItemId, Count = (ushort)(entry.Count - placed) });
            }
        }

        SaveSlots(ctx, player.CharacterId, slots);
        SyncWeapon(ctx, ctx.Sender, slots);

        // One line however many items, and it says when something was left —
        // "took nothing" and "took two of three" look identical otherwise.
        Log.Info($"{player.Name} took {(took.Count > 0 ? string.Join(", ", took) : "nothing")}"
               + (left.Count > 0 ? $" — {left.Count} left in the bag, no room" : ""));

        if (left.Count == 0)
        {
            ctx.Db.LootDrop.Id.Delete(bagId);
        }
        else
        {
            bag.Items = left;
            ctx.Db.LootDrop.Id.Update(bag);
        }
    }

    /// <summary>
    /// Moves, merges or swaps two inventory slots.
    /// </summary>
    /// <remarks>
    /// The same rules the UI already applied locally, moved to where they belong.
    /// The client now asks and redraws from the answer, so a refused move shows as
    /// the item going back rather than as a client and server that disagree.
    ///
    /// Equipped slots accept only the kind they are for, by position: slot 0 takes
    /// a weapon, 1 armour, 2 jewellery. Position rather than an authored filter,
    /// because the server has no scene to read a filter from.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void MoveItem(ReducerContext ctx, byte fromContainer, byte fromX, byte fromY,
                                byte toContainer, byte toX, byte toY)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }
        if (fromContainer > ContainerSecure || toContainer > ContainerSecure)
        {
            // The vault is not reachable from here. It is a different row, keyed
            // to the account rather than the character, and moving between the
            // two is depositing or withdrawing — which check that you are
            // standing at it.
            throw new Exception("no such container");
        }

        var slots = SlotsOf(ctx, player.CharacterId);
        int from = AtCell(ctx, slots, fromContainer, fromX, fromY);
        if (from < 0)
        {
            return;
        }

        var moving = slots[from];
        var (w, h) = FootprintIn(ctx, toContainer, moving.ItemId);
        if (!Accepts(ctx, toContainer, toX, moving.ItemId))
        {
            throw new Exception("that does not go there");
        }

        int to = AtCell(ctx, slots, toContainer, toX, toY);
        if (to == from)
        {
            return;
        }

        if (to < 0)
        {
            if (!FitsAt(ctx, slots, toContainer, toX, toY, w, h, from))
            {
                throw new Exception("it does not fit there");
            }
            moving.Container = toContainer;
            moving.X = toX;
            moving.Y = toY;
            slots[from] = moving;
        }
        else
        {
            var sitting = slots[to];
            ushort max = ctx.Db.ItemDef.Id.Find(moving.ItemId) is { } def
                ? (def.MaxStack < 1 ? (ushort)1 : def.MaxStack) : (ushort)1;

            if (sitting.ItemId == moving.ItemId && max > 1 && sitting.Count < max)
            {
                ushort take = (ushort)Math.Min((ushort)(max - sitting.Count), moving.Count);
                sitting.Count += take;
                moving.Count -= take;
                slots[to] = sitting;
                if (moving.Count == 0)
                {
                    slots.RemoveAt(from);
                }
                else
                {
                    slots[from] = moving;
                }
            }
            else
            {
                // A swap has to be legal both ways, and both items have to fit
                // where the other was. Two differently shaped things cannot
                // simply trade places.
                if (!Accepts(ctx, fromContainer, fromX, sitting.ItemId))
                {
                    throw new Exception("that does not go there");
                }
                var (sw, sh) = FootprintIn(ctx, fromContainer, sitting.ItemId);
                if (!FitsAt(ctx, slots, toContainer, toX, toY, w, h, to)
                    || !FitsAt(ctx, slots, fromContainer, moving.X, moving.Y, sw, sh, from))
                {
                    throw new Exception("they do not fit swapped");
                }

                (moving.Container, sitting.Container) = (sitting.Container, moving.Container);
                (moving.X, sitting.X) = (sitting.X, moving.X);
                (moving.Y, sitting.Y) = (sitting.Y, moving.Y);
                slots[from] = moving;
                slots[to] = sitting;
            }
        }

        SaveSlots(ctx, player.CharacterId, slots);
        SyncWeapon(ctx, ctx.Sender, slots);
    }

    /// <summary>Whether a cell will hold a given item.</summary>
    /// <remarks>
    /// Only the equipped row cares. Its cells are typed by position — x 0 takes a
    /// weapon, 1 armour, 2 jewellery — because the server has no scene to read a
    /// filter from. Every other container is a grid and takes anything that fits.
    ///
    /// The secure pocket takes anything on purpose: it is a pocket, not a slot
    /// for a kind of thing, and restricting it would decide the one item you keep
    /// by its type rather than by what you value.
    /// </remarks>
    private static bool Accepts(ReducerContext ctx, byte container, byte x, ushort itemId)
    {
        if (container != ContainerEquipped)
        {
            return true;
        }
        return ctx.Db.ItemDef.Id.Find(itemId) is { } def && def.Kind == x;
    }

    /// <summary>
    /// Makes a character and takes a loadout out of the vault for it.
    /// </summary>
    /// <remarks>
    /// The loadout is withdrawn as part of creation rather than afterwards,
    /// because a character created empty and then equipped is two states a client
    /// can be interrupted between — and the interrupted one is a character
    /// standing in the world with nothing, next to a vault it may not be able to
    /// reach.
    ///
    /// Anything the vault does not actually hold is skipped rather than granted.
    /// A creation screen showing what you own is a convenience; the vault is the
    /// authority on what you own.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void CreateCharacter(ReducerContext ctx, string name, List<VaultCell> loadout)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { } player)
        {
            return;
        }

        string trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new Exception("a character needs a name");
        }
        if (trimmed.Length > 24)
        {
            trimmed = trimmed[..24];
        }

        int alive = 0;
        foreach (var _ in ctx.Db.Character.Account.Filter(ctx.Sender))
        {
            alive++;
        }
        if (alive >= CharacterSlots)
        {
            throw new Exception($"all {CharacterSlots} character slots are full");
        }

        var created = ctx.Db.Character.Insert(new Character
        {
            Id = 0,
            Account = ctx.Sender,
            Name = trimmed,
            CreatedAt = ctx.Timestamp,
            // Set here, not left to the column default. A default only fills in
            // rows that already existed when the column arrived; a fresh insert
            // that omits it writes zero, and every new character would read
            // "LEVEL 0" on the select screen.
            Level = 1,
        });

        var slots = new List<GridItem>();
        var vault = VaultOf(ctx, ctx.Sender);
        var taken = new List<string>();

        foreach (var cell in loadout)
        {
            int at = AtCell(ctx, vault, ContainerVault, cell.X, cell.Y);
            if (at < 0)
            {
                // The cell is empty, or the client is looking at a vault that has
                // moved on. Skipped rather than refused: one stale cell should not
                // cost you the whole character you were making.
                continue;
            }

            var stack = vault[at];
            ushort placed = Equip(ctx, slots, stack.ItemId, stack.Count);
            if (placed == 0)
            {
                continue;
            }

            if (placed >= stack.Count)
            {
                vault.RemoveAt(at);
            }
            else
            {
                stack.Count -= placed;
                vault[at] = stack;
            }

            string label = ctx.Db.ItemDef.Id.Find(stack.ItemId) is { } named
                ? named.Name : $"item {stack.ItemId}";
            taken.Add(placed > 1 ? $"{placed}x {label}" : label);
        }

        // The configured starter, on top of whatever came from the vault. A first
        // character has an empty vault, and one that cannot shoot cannot ever
        // fill it.
        // Only when the loadout left the weapon slot empty. Granting it anyway
        // would push a chosen weapon into the pack and arm the character with the
        // default instead — the opposite of what picking one meant.
        ushort starting = Config(ctx).StartingWeaponId;
        if (starting != 0 && AtCell(ctx, slots, ContainerEquipped, 0, 0) < 0
            && ctx.Db.ItemDef.Id.Find(starting) is { Kind: 0 })
        {
            slots.Add(new GridItem { Container = ContainerEquipped, X = 0, Y = 0, ItemId = starting, Count = 1 });
        }

        SaveVault(ctx, ctx.Sender, vault);
        SaveSlots(ctx, created.Id, slots);

        Log.Info($"{ctx.Sender} created \"{trimmed}\" (character {created.Id})"
               + (taken.Count > 0 ? $" with {string.Join(", ", taken)} from the vault" : ""));
    }

    /// <summary>
    /// Puts a starting item where it belongs: its equipped slot if that is free,
    /// the pack otherwise.
    /// </summary>
    /// <remarks>
    /// The creation screen lets you pick a weapon, some armour and a few
    /// consumables; it does not ask which slot each goes in, because for
    /// everything except a duplicate there is only one sensible answer. Working
    /// it out here keeps that out of the client, where it would be a second copy
    /// of the kind rules.
    /// </remarks>
    private static ushort Equip(ReducerContext ctx, List<GridItem> slots,
                                ushort itemId, ushort count)
    {
        if (ctx.Db.ItemDef.Id.Find(itemId) is { } def && def.Kind < EquippedSlots
            && AtCell(ctx, slots, ContainerEquipped, def.Kind, 0) < 0)
        {
            slots.Add(new GridItem
            {
                Container = ContainerEquipped,
                X = def.Kind,
                Y = 0,
                ItemId = itemId,
                Count = 1,
            });
            return 1;
        }
        return AddToBackpack(ctx, slots, itemId, count);
    }

    /// <summary>Enters the world as one of the account's characters.</summary>
    /// <remarks>
    /// Ownership is checked against the character's account rather than trusted
    /// from the caller. Without it, any client could name any id and play
    /// somebody else's character.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void SelectCharacter(ReducerContext ctx, ulong characterId)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { } player)
        {
            return;
        }
        if (ctx.Db.Character.Id.Find(characterId) is not { } character)
        {
            throw new Exception("no such character");
        }
        if (character.Account != ctx.Sender)
        {
            throw new Exception("that is not your character");
        }

        var (sx, sy) = SpawnPoint(ctx);
        player.CharacterId = characterId;
        player.Name = character.Name;
        player.Hp = player.MaxHp;
        player.X = sx;
        player.Y = sy;
        player.ZoneId = RealmZone;
        player.LootingBag = 0;
        ctx.Db.Player.Identity.Update(player);

        SyncWeapon(ctx, ctx.Sender, SlotsOf(ctx, characterId));
        Log.Info($"{ctx.Sender} is playing \"{character.Name}\"");
    }

    /// <summary>Leaves the world for the character screen, keeping the character.</summary>
    [SpacetimeDB.Reducer]
    public static void LeaveCharacter(ReducerContext ctx)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }
        if (player.ZoneId != RealmZone)
        {
            LeaveZone(ctx, ref player);
        }
        player.CharacterId = 0;
        player.LootingBag = 0;
        player.WeaponId = 0;
        ctx.Db.Player.Identity.Update(player);
    }

    /// <summary>Puts an item in the caller's own pack. Development only.</summary>
    /// <remarks>
    /// Self only, like <see cref="KillMe"/> — it takes no identity and acts on the
    /// sender. It exists because looting through a bag needs a connection held
    /// open across two calls, which a terminal cannot do, and a grid nobody can
    /// put anything into is a grid nobody can check.
    ///
    /// <b>This is a cheat and it is not authorised.</b> Like every other editor
    /// and admin reducer here, any connected client may call it. That gap is
    /// known and unfixed; this makes it one item worse, and should go when the
    /// admin reducers get an identity check.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void GiveTestItem(ReducerContext ctx, ushort itemId, ushort count)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }
        var slots = SlotsOf(ctx, player.CharacterId);
        ushort placed = AddToBackpack(ctx, slots, itemId, count < 1 ? (ushort)1 : count);
        SaveSlots(ctx, player.CharacterId, slots);
        SyncWeapon(ctx, ctx.Sender, slots);
        Log.Info($"gave {placed}x item {itemId} to {player.Name}");
    }

    /// <summary>Ends the calling player's character. For testing permadeath.</summary>
    /// <remarks>
    /// Self only — it takes no identity and acts on the sender, so the worst a
    /// client can do with it is kill itself. Dying is otherwise unreachable
    /// without standing in front of something, which a terminal cannot do, and a
    /// loop built on death that nobody can trigger on demand is a loop nobody can
    /// check.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void KillMe(ReducerContext ctx)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { } player)
        {
            return;
        }
        KillCharacter(ctx, ref player);
    }

    // ---- Vault ------------------------------------------------------------

    private static List<GridItem> VaultOf(ReducerContext ctx, Identity who) =>
        ctx.Db.Vault.Identity.Find(who) is { } row ? row.Items : new List<GridItem>();

    private static void SaveVault(ReducerContext ctx, Identity who, List<GridItem> items)
    {
        var row = new Vault { Identity = who, Items = items };
        if (ctx.Db.Vault.Identity.Find(who) is null)
        {
            ctx.Db.Vault.Insert(row);
        }
        else
        {
            ctx.Db.Vault.Identity.Update(row);
        }
    }

    /// <summary>Whether a player is standing somewhere they can bank.</summary>
    /// <remarks>
    /// Spawn, until extraction points exist. Checked here rather than trusted
    /// from the client for the usual reason: where a player is standing is a
    /// server fact, and "am I safe" is exactly the claim a client would want to
    /// lie about.
    /// </remarks>
    private static bool AtVault(ReducerContext ctx, Player player)
    {
        var (sx, sy) = SpawnPoint(ctx);
        float dx = player.X - sx;
        float dy = player.Y - sy;
        // The vault stands at the realm's spawn. The same coordinates in a
        // dungeon are somewhere else entirely.
        return player.ZoneId == RealmZone && dx * dx + dy * dy <= VaultRange * VaultRange;
    }

    /// <summary>Banks one carried item. It then survives death.</summary>
    [SpacetimeDB.Reducer]
    public static void DepositToVault(ReducerContext ctx, byte container, byte x, byte y)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }
        if (!AtVault(ctx, player))
        {
            throw new Exception("too far from the vault");
        }

        var slots = SlotsOf(ctx, player.CharacterId);
        int at = AtCell(ctx, slots, container, x, y);
        if (at < 0)
        {
            return;
        }

        var entry = slots[at];
        var vault = VaultOf(ctx, ctx.Sender);

        if (PutIn(ctx, vault, ContainerVault, entry.ItemId, entry.Count) < entry.Count)
        {
            throw new Exception("no room in the vault for that");
        }

        slots.RemoveAt(at);
        SaveVault(ctx, ctx.Sender, vault);
        SaveSlots(ctx, player.CharacterId, slots);
        SyncWeapon(ctx, ctx.Sender, slots);
    }

    /// <summary>Takes what is in a vault cell back into the pack.</summary>
    /// <remarks>
    /// Addressed by cell, not by item id. A grid can hold the same item in two
    /// places, and an id would take whichever the server found first rather than
    /// the one the player pointed at.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void WithdrawFromVault(ReducerContext ctx, byte x, byte y, ushort count)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }
        if (!AtVault(ctx, player))
        {
            throw new Exception("too far from the vault");
        }

        var vault = VaultOf(ctx, ctx.Sender);
        int at = AtCell(ctx, vault, ContainerVault, x, y);
        if (at < 0)
        {
            throw new Exception("nothing there");
        }

        var stack = vault[at];
        ushort want = count == 0 || count > stack.Count ? stack.Count : count;

        var slots = SlotsOf(ctx, player.CharacterId);
        ushort placed = AddToBackpack(ctx, slots, stack.ItemId, want);
        if (placed == 0)
        {
            throw new Exception("no room in the pack");
        }

        // Only what fitted leaves the vault. Taking the whole stack out and
        // dropping the remainder would destroy the part that could not be
        // carried, which is the one thing a vault must never do.
        if (placed >= stack.Count)
        {
            vault.RemoveAt(at);
        }
        else
        {
            stack.Count -= placed;
            vault[at] = stack;
        }

        SaveVault(ctx, ctx.Sender, vault);
        SaveSlots(ctx, player.CharacterId, slots);
        SyncWeapon(ctx, ctx.Sender, slots);
    }

    /// <summary>Moves a stack to another cell of the vault.</summary>
    /// <remarks>
    /// Packing the grid is the point of having one, so rearranging has to be
    /// possible without taking things out and putting them back — which would
    /// need a free pack slot to shuffle through and would fail exactly when the
    /// vault was full enough to be worth tidying.
    ///
    /// Refused rather than nudged when the destination is occupied. A move that
    /// silently lands somewhere else is a move the player has to go and find.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void MoveVaultItem(ReducerContext ctx, byte fromX, byte fromY,
                                     byte toX, byte toY)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }
        if (!AtVault(ctx, player))
        {
            throw new Exception("too far from the vault");
        }

        var vault = VaultOf(ctx, ctx.Sender);
        int at = AtCell(ctx, vault, ContainerVault, fromX, fromY);
        if (at < 0)
        {
            return;
        }

        var moving = vault[at];
        var (w, h) = FootprintOf(ctx, moving.ItemId);
        if (!FitsAt(ctx, vault, ContainerVault, toX, toY, w, h, at))
        {
            throw new Exception("it does not fit there");
        }

        moving.X = toX;
        moving.Y = toY;
        vault[at] = moving;
        SaveVault(ctx, ctx.Sender, vault);
    }

    /// <summary>
    /// Scales damage by what the target is weak or resistant to.
    /// </summary>
    /// <remarks>
    /// Floored at 1 rather than 0 whenever any damage was going to land. A
    /// resistance that reduced a hit to nothing would read as a broken weapon —
    /// the shot connects, the number is zero, and there is no way to tell that
    /// apart from a bug. Immunity belongs in the invulnerable flag, which says so.
    /// </remarks>
    private static ushort ApplyResistance(ReducerContext ctx, EnemyDef def,
                                          byte element, ushort amount)
    {
        if (amount == 0 || def.Resist is not { Count: > 0 } table)
        {
            return amount;
        }

        ushort percent = 100;
        foreach (var entry in table)
        {
            if (entry.Element == element)
            {
                percent = entry.Percent;
                break;
            }
        }
        if (percent == 100)
        {
            return amount;
        }

        float scaled = amount * percent / 100f;
        return scaled <= 0f ? (ushort)1 : (ushort)MathF.Max(1f, scaled);
    }

    /// <summary>
    /// Resolves one trigger pull as instant rays, and records them for drawing.
    /// </summary>
    /// <remarks>
    /// The rays come from the same <see cref="PlaceShots"/> the projectile path
    /// uses, so a shotgun is a Spread of eight and a rifle is a Single — the
    /// pattern vocabulary already existed and did not need a second one.
    ///
    /// Each ray is marched rather than solved analytically. A step of a quarter
    /// tile is finer than any enemy is small, and it lets walls and bodies be
    /// tested by the same walk instead of by two kinds of intersection maths that
    /// could disagree about a corner.
    ///
    /// Damage runs through the ordinary <see cref="DamageEnemy"/> path using a
    /// Shot that is never inserted. That keeps crits, elements, resistances,
    /// debuffs and stat crediting in one place — a second damage path is how the
    /// two quietly stop agreeing.
    /// </remarks>
    private static void FireHitscan(ReducerContext ctx, Catalogue catalogue, WeaponDef weapon,
                                    ref Player player, float dirX, float dirY,
                                    float critChance, float critMultiplier)
    {
        byte rays = weapon.Shots > 0 ? weapon.Shots : (byte)1;
        float share = weapon.SplitDamage && rays > 1 ? 1f / rays : 1f;
        float range = weapon.Range;

        var enemies = ctx.Db.Enemy.ZoneId.Filter(player.ZoneId).ToList();
        var ground = ZoneGround(ctx, player.ZoneId);
        float pushX = 0f;
        float pushY = 0f;

        foreach (var place in PlaceShots(weapon.PatternKind, rays, weapon.SpreadDegrees,
                                         weapon.PatternGroups))
        {
            float rad = place.AngleDeg * MathF.PI / 180f;
            float c = MathF.Cos(rad);
            float sn = MathF.Sin(rad);
            float rx = dirX * c - dirY * sn;
            float ry = dirX * sn + dirY * c;

            float endX = player.X + rx * range;
            float endY = player.Y + ry * range;
            bool hit = false;
            int pierced = 0;

            // Which enemies this ray has already gone through. The march samples
            // every quarter tile and a body is a whole tile across, so without
            // this a piercing ray hits the same target at four consecutive steps
            // and a rifle deals double its damage to one enemy.
            var struck = new List<ulong>();

            const float step = 0.25f;
            for (float t = step; t <= range; t += step)
            {
                float px = player.X + rx * t;
                float py = player.Y + ry * t;

                // Walls stop a ray outright, and stop it before anything standing
                // behind them can be hit.
                if (ground.Blocked(px, py))
                {
                    endX = px;
                    endY = py;
                    hit = true;
                    break;
                }

                int found = -1;
                for (int i = 0; i < enemies.Count; i++)
                {
                    if (enemies[i].Hp == 0)
                    {
                        continue;
                    }
                    float radius = catalogue.Enemies.TryGetValue(enemies[i].DefId, out var def)
                        ? def.Radius : 0.5f;
                    if (struck.Contains(enemies[i].Id))
                    {
                        continue;
                    }
                    float dx = enemies[i].X - px;
                    float dy = enemies[i].Y - py;
                    if (dx * dx + dy * dy <= radius * radius)
                    {
                        found = i;
                        break;
                    }
                }
                if (found < 0)
                {
                    continue;
                }

                // Falloff by how far along the ray the target was, not by the
                // weapon's whole range — a pellet that connects at arm's length
                // should hit like one, whatever the gun could theoretically reach.
                float reach = range <= 0f ? 1f : t / range;
                float falloff = 1f + (weapon.FalloffPercent / 100f - 1f) * reach;
                float pierceLoss = pierced == 0 ? 1f : MathF.Pow(0.6f, pierced);

                ushort rolled = RollDamage(ctx, weapon.DamageMin, weapon.DamageMax);
                float scaled = rolled * share * falloff * pierceLoss;
                bool crit = critChance > 0f && ctx.Rng.NextDouble() < critChance;
                if (crit)
                {
                    scaled *= critMultiplier;
                }

                var pretend = new Shot
                {
                    Owner = player.Identity,
                    Faction = 0,
                    Damage = scaled < 1f ? (ushort)1 : (ushort)scaled,
                    Element = weapon.Element,
                    DebuffKind = weapon.DebuffKind,
                    DebuffSeconds = weapon.DebuffSeconds,
                    Crit = crit,
                };
                struck.Add(enemies[found].Id);
                enemies[found] = DamageEnemy(ctx, catalogue, enemies[found], pretend,
                                             player.Identity);

                endX = px;
                endY = py;
                hit = true;

                if (pierced >= weapon.Pierce)
                {
                    break;
                }
                pierced++;
                hit = false;
            }

            // The shove is shared too, so eight pellets add up to one shell and a
            // fan that half misses shoves proportionally less.
            pushX -= rx * weapon.Kickback * share;
            pushY -= ry * weapon.Kickback * share;

            ctx.Db.Tracer.Insert(new Tracer
            {
                Id = 0,
                Owner = player.Identity,
                X1 = player.X,
                Y1 = player.Y,
                X2 = endX,
                Y2 = endY,
                Tint = weapon.Tint,
                Hit = hit,
                FiredAt = ctx.Timestamp,
                ZoneId = player.ZoneId,
            });
        }

        if (pushX != 0f || pushY != 0f)
        {
            var (kx, ky) = ground.Slide(player.X, player.Y, pushX, pushY);
            player.X = kx;
            player.Y = ky;
        }
    }

    /// <summary>Starts a reload, if one is not already running.</summary>
    /// <remarks>
    /// Records when it finishes rather than counting down, so it survives a tick
    /// being late and needs no per-player timer. <see cref="AdvancePlayers"/>
    /// notices the moment it has passed.
    /// </remarks>
    private static void BeginReload(ReducerContext ctx, ref Player player,
                                    WeaponDef weapon, long nowUs)
    {
        if (weapon.Magazine == 0 || player.Ammo >= weapon.Magazine)
        {
            return;
        }
        if (player.ReloadAtUs != 0 && (long)player.ReloadAtUs > nowUs)
        {
            return;
        }
        player.ReloadAtUs = (ulong)(nowUs + weapon.ReloadMs * 1000L);
    }

    /// <summary>Asks to reload early, before the magazine is dry.</summary>
    [SpacetimeDB.Reducer]
    public static void Reload(ReducerContext ctx)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player
            || ctx.Db.WeaponDef.Id.Find(player.WeaponId) is not { } weapon)
        {
            return;
        }
        BeginReload(ctx, ref player, weapon, ctx.Timestamp.MicrosecondsSinceUnixEpoch);
        ctx.Db.Player.Identity.Update(player);
    }

    /// <summary>Drops tracers once every client has had time to draw them.</summary>
    /// <remarks>
    /// Short, because a tracer is a record that something happened rather than a
    /// thing in the world. Long enough that a client running at any sane frame
    /// rate sees it at least once; the fade is the client's business.
    /// </remarks>
    private static void ExpireTracers(ReducerContext ctx, long nowUs)
    {
        var stale = new List<ulong>();
        foreach (var tracer in ctx.Db.Tracer.Iter())
        {
            if ((nowUs - tracer.FiredAt.MicrosecondsSinceUnixEpoch) / 1000f > TracerLifetimeMs)
            {
                stale.Add(tracer.Id);
            }
        }
        foreach (ulong id in stale)
        {
            ctx.Db.Tracer.Id.Delete(id);
        }
    }

    /// <summary>Removes bags nobody came for.</summary>
    /// <remarks>
    /// Without this, drops accumulate for the life of the database — they were
    /// never deleted by anything, and every client subscribes to the table. A
    /// long session would end up replicating a field of bags from kills nobody
    /// remembers.
    ///
    /// Long enough to walk back for, short enough that a cleared area looks
    /// cleared.
    /// </remarks>
    private static void ExpireBags(ReducerContext ctx, long nowUs)
    {
        var stale = new List<ulong>();
        foreach (var bag in ctx.Db.LootDrop.Iter())
        {
            float age = (nowUs - bag.DroppedAt.MicrosecondsSinceUnixEpoch) / 1_000_000f;
            if (age > BagLifetimeSeconds)
            {
                stale.Add(bag.Id);
            }
        }
        foreach (ulong id in stale)
        {
            ctx.Db.LootDrop.Id.Delete(id);
        }
    }

    private static void RecordKill(ReducerContext ctx, Enemy enemy, EnemyDef def, Identity? killer)
    {
        var contents = new List<BagItem>();
        var dropped = new List<string>();

        // Which pool, before which items. One weighted choice across every
        // outcome including "nothing", so the entries are directly comparable:
        // a pool with weight 1 against a no-drop weight of 9 drops one time in
        // ten, and that stays true however many other pools are added.
        //
        // This pick and the entry loop below are mirrored, random draws in the same
        // order, by LootMath.RollPool and LootMath.RollEntries for the editor's loot
        // simulator. Change both.
        ushort poolId = RollPool(ctx, def.Id);
        byte bagKind = 0;

        if (poolId != 0 && ctx.Db.LootPoolDef.Id.Find(poolId) is { } pool)
        {
            bagKind = pool.BagKind;

            foreach (var entry in ctx.Db.LootEntry.PoolId.Filter(poolId))
            {
                if (entry.ItemId == 0 || entry.Count == 0
                    || ctx.Rng.NextDouble() * 100.0 >= entry.ChancePercent)
                {
                    continue;
                }

                // An entry naming an item that is not in the catalogue is skipped
                // and said out loud. Silently dropping it would look identical to
                // the roll simply failing, and a loot table that never pays out is
                // the bug hardest to tell from bad luck.
                if (ctx.Db.ItemDef.Id.Find(entry.ItemId) is not { } item)
                {
                    Log.Warn($"pool {pool.Name} names item {entry.ItemId}, which is not "
                           + "in the catalogue — push equipment before killing things");
                    continue;
                }

                contents.Add(new BagItem { ItemId = entry.ItemId, Count = entry.Count });
                dropped.Add(entry.Count > 1 ? $"{item.Name} x{entry.Count}" : item.Name);
            }
        }

        // No bag when nothing rolled, even though a pool was picked. An empty bag
        // is a promise of loot that is not there, and walking over one to find
        // nothing is worse than there having been no bag to walk to.
        if (contents.Count > 0)
        {
            ctx.Db.LootDrop.Insert(new LootDrop
            {
                Id = 0,
                X = enemy.X,
                Y = enemy.Y,
                DroppedAt = ctx.Timestamp,
                BagKind = bagKind,
                Items = contents,
                ZoneId = enemy.ZoneId,
            });
        }

        string by = killer is { } id && ctx.Db.Player.Identity.Find(id) is { } p ? p.Name : "something";
        string items = dropped.Count > 0 ? string.Join(", ", dropped) : "nothing";
        Log.Info($"{def.Name} killed by {by} — dropped {items}");

        ReportFight(ctx, enemy, def);
        BankStats(ctx, enemy.Id, killer);

        // Cleared after banking. The live tables track what is being fought, not
        // what has been fought, and leaving rows behind would grow them without
        // bound across a session.
        foreach (ulong tallyId in ctx.Db.DamageTally.EnemyId.Filter(enemy.Id).Select(t => t.Id).ToList())
        {
            ctx.Db.DamageTally.Id.Delete(tallyId);
        }
        foreach (ulong tallyId in ctx.Db.DebuffTally.EnemyId.Filter(enemy.Id).Select(t => t.Id).ToList())
        {
            ctx.Db.DebuffTally.Id.Delete(tallyId);
        }
    }

    /// <summary>Writes the per-contributor breakdown of a finished fight.</summary>
    private static void ReportFight(ReducerContext ctx, Enemy enemy, EnemyDef def)
    {
        var tallies = ctx.Db.DamageTally.EnemyId.Filter(enemy.Id).ToList();
        ulong total = 0;
        foreach (var t in tallies)
        {
            total += t.Damage;
        }
        if (total == 0)
        {
            return;
        }

        // Grouped by player, because "who contributed what" is the question, and
        // a flat list of element rows makes the reader do the addition.
        foreach (var group in tallies.GroupBy(t => t.Attacker))
        {
            string name = ctx.Db.Player.Identity.Find(group.Key) is { } p ? p.Name : "unknown";
            ulong sum = 0, hits = 0, crits = 0;
            ushort best = 0;
            foreach (var t in group)
            {
                sum += t.Damage;
                hits += t.Hits;
                crits += t.Crits;
                best = t.Best > best ? t.Best : best;
            }

            string byElement = string.Join(", ", group
                .Where(t => t.Damage > 0)
                .OrderByDescending(t => t.Damage)
                .Select(t => $"{t.Damage} {ElementName(t.Element)}"));

            string debuffs = string.Join(", ", ctx.Db.DebuffTally.EnemyId.Filter(enemy.Id)
                .Where(d => d.Attacker == group.Key && d.Seconds > 0f)
                .Select(d => $"{d.Seconds:0.0}s {DebuffNames[d.Kind]}"));

            Log.Info($"  {def.Name} — {name}: {sum} dmg ({sum * 100 / total}%) "
                   + $"[{byElement}] {hits} hits, {crits} crits, best {best}"
                   + (debuffs.Length > 0 ? $" | {debuffs}" : ""));
        }
    }

    /// <summary>Equips a weapon from the catalogue. 0 clears it.</summary>
    [SpacetimeDB.Reducer]
    public static void EquipWeapon(ReducerContext ctx, ushort weaponId)
    {
        if (ctx.Db.Player.Identity.Find(ctx.Sender) is not { CharacterId: not 0 } player)
        {
            return;
        }

        var slots = SlotsOf(ctx, player.CharacterId);
        int held = AtCell(ctx, slots, ContainerEquipped, 0, 0);

        if (weaponId == 0)
        {
            // Unequip: back to the pack, not deleted.
            if (held >= 0)
            {
                var entry = slots[held];
                slots.RemoveAt(held);
                if (AddToBackpack(ctx, slots, entry.ItemId, entry.Count) == 0)
                {
                    throw new Exception("no room in the pack to unequip that");
                }
            }
            SaveSlots(ctx, player.CharacterId, slots);
            SyncWeapon(ctx, ctx.Sender, slots);
            return;
        }

        int carrying = -1;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].ItemId == weaponId && slots[i].Container == 0)
            {
                carrying = i;
                break;
            }
        }
        if (carrying < 0)
        {
            // Refused rather than granted. This used to write the weapon id
            // straight onto the player, which meant a client could hand itself
            // any weapon in the catalogue — and the next inventory change would
            // undo it anyway, because what is held is recomputed from the slots.
            throw new Exception($"you are not carrying weapon {weaponId}");
        }
        if (ctx.Db.ItemDef.Id.Find(weaponId) is not { Kind: 0 })
        {
            throw new Exception($"item {weaponId} is not a weapon");
        }

        var moving = slots[carrying];
        if (held < 0)
        {
            moving.Container = ContainerEquipped;
            moving.X = 0;
            moving.Y = 0;
            slots[carrying] = moving;
        }
        else
        {
            var sitting = slots[held];
            (moving.Container, sitting.Container) = (sitting.Container, moving.Container);
            (moving.X, sitting.X) = (sitting.X, moving.X);
            (moving.Y, sitting.Y) = (sitting.Y, moving.Y);
            slots[carrying] = moving;
            slots[held] = sitting;
        }

        SaveSlots(ctx, player.CharacterId, slots);
        SyncWeapon(ctx, ctx.Sender, slots);
    }

    /// <summary>
    /// Inserts or replaces a catalogue entry. Called by the Unity editor.
    /// </summary>
    /// <remarks>
    /// Unauthenticated on purpose, and only safe because this is a development
    /// build: anyone who can reach the server can rewrite any weapon. Before this
    /// is exposed anywhere real it needs an owner check, or the catalogue needs to
    /// be baked into the module the way levels were.
    /// </remarks>
    [SpacetimeDB.Reducer]
    public static void UpsertWeapon(ReducerContext ctx, ushort id, string name,
                                    ushort fireRateMs, ushort damageMin, ushort damageMax,
                                    float projectileSpeed, ushort projectileLifetimeMs,
                                    float projectileSize, byte patternKind, byte shots,
                                    float spreadDegrees, float spinDegreesPerSec,
                                    float waveAmplitude, float waveFrequency,
                                    byte element, byte debuffKind, float debuffSeconds,
                                    uint tint, List<BulletProfile> profiles,
                                    byte profileAssignment,
                                    ushort magazine, ushort reloadMs, bool reloadPerShell,
                                    float kickback, float range, byte pierce,
                                    ushort falloffPercent, bool splitDamage,
                                    ushort moveSpeedPercent, byte patternGroups,
                                    byte bulletSpriteId)
    {
        if (id == 0)
        {
            throw new Exception("weapon id 0 is reserved for \"no weapon\"");
        }

        // Clamped to the same bounds the flat columns get, and nothing more. A
        // zero field is never swapped for the weapon's flat value: doing that
        // would make "the designer left this blank" and "the designer wanted a
        // tiny fast bullet" produce identical rows, and the first of those is a
        // mistake worth seeing. A clamped zero size becomes a visible 0.05
        // bullet, which is wrong in a way you can point at.
        var mix = new List<BulletProfile>();
        foreach (var p in profiles ?? new List<BulletProfile>())
        {
            mix.Add(new BulletProfile
            {
                Element = p.Element > 4 ? (byte)0 : p.Element,
                // 3 is armour break. This read > 2 while DebuffKind 3 existed and
                // was fully handled downstream, so every authored armour-break
                // bullet arrived as no debuff at all — and silently, which is the
                // worst way for it to fail: the weapon fires, the hit lands, and
                // only the effect is missing.
                DebuffKind = p.DebuffKind > 3 ? (byte)0 : p.DebuffKind,
                DebuffSeconds = Math.Clamp(p.DebuffSeconds, 0f, 30f),
                DamageMin = p.DamageMin,
                DamageMax = p.DamageMax < p.DamageMin ? p.DamageMin : p.DamageMax,
                Speed = Math.Clamp(p.Speed, 1f, 60f),
                Size = Math.Clamp(p.Size, 0.05f, 2f),
                LifetimeMs = (ushort)Math.Clamp((int)p.LifetimeMs, 100, 5000),
                Tint = p.Tint & 0xFFFFFFu,
            });
        }

        var def = new WeaponDef
        {
            Id = id,
            Name = name,
            // Clamped here as well as in the inspector: the inspector is a
            // convenience, this is the boundary. A zero fire rate would spawn a
            // volley every tick, per player.
            FireRateMs = (ushort)Math.Clamp((int)fireRateMs, 50, 5000),
            DamageMin = damageMin,
            DamageMax = damageMax < damageMin ? damageMin : damageMax,
            ProjectileSpeed = Math.Clamp(projectileSpeed, 1f, 60f),
            ProjectileLifetimeMs = (ushort)Math.Clamp((int)projectileLifetimeMs, 100, 5000),
            ProjectileSize = Math.Clamp(projectileSize, 0.05f, 2f),
            Element = element > 4 ? (byte)0 : element,
            DebuffKind = debuffKind > 3 ? (byte)0 : debuffKind,
            DebuffSeconds = Math.Clamp(debuffSeconds, 0f, 30f),
            PatternKind = patternKind > 5 ? (byte)0 : patternKind,
            Shots = (byte)Math.Clamp((int)shots, 1, 32),
            SpreadDegrees = Math.Clamp(spreadDegrees, 0f, 360f),
            SpinDegreesPerSec = Math.Clamp(spinDegreesPerSec, -720f, 720f),
            WaveAmplitude = Math.Clamp(waveAmplitude, 0f, 5f),
            WaveFrequency = Math.Clamp(waveFrequency, 0f, 20f),
            Tint = tint & 0xFFFFFFu,
            Magazine = magazine,
            ReloadMs = reloadMs,
            ReloadPerShell = reloadPerShell,
            // Clamped: a kickback bigger than a tile per shot would teleport the
            // shooter across the room, which reads as a bug rather than as recoil.
            Kickback = Math.Clamp(kickback, 0f, 3f),
            // Floor of 25 rather than 0: a weapon that stops the holder dead is
            // indistinguishable from a stuck client, and a mis-authored 0 would
            // read as a movement bug rather than as a heavy gun.
            MoveSpeedPercent = (ushort)Math.Clamp((int)moveSpeedPercent, 25, 200),
            // Clamped to the shot count by PlaceShots rather than here: the two
            // arrive as separate arguments and either could be the one that is
            // wrong, so the decision belongs where both are in hand.
            PatternGroups = patternGroups,
            BulletSpriteId = bulletSpriteId,
            Range = Math.Clamp(range, 0f, 60f),
            Pierce = pierce,
            FalloffPercent = falloffPercent,
            SplitDamage = splitDamage,
            Profiles = mix,
            ProfileAssignment = profileAssignment > 2 ? (byte)0 : profileAssignment,
        };

        if (ctx.Db.WeaponDef.Id.Find(id) is null)
        {
            ctx.Db.WeaponDef.Insert(def);
        }
        else
        {
            ctx.Db.WeaponDef.Id.Update(def);
        }
        // The mix is named because it is the one part of a weapon that is invisible
        // both in the inspector preview and on screen until bullets are tinted —
        // this log is the only place a push proves it carried the list.
        string mixText = def.Profiles.Count == 0
            ? "flat"
            : $"{def.Profiles.Count} profile(s) {(def.ProfileAssignment == 1 ? "block" : "cycle")}";
        Log.Info($"weapon {id} \"{name}\": {def.Shots} shot(s), {def.FireRateMs}ms, {def.DamageMin}-{def.DamageMax}, {mixText}");
    }

    private static float Clamp(float v) => v < 0f ? 0f : (v > WorldSize ? WorldSize : v);
}
