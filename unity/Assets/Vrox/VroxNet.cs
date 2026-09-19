using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// The connection to the server. One of these in the scene, nothing else.
    /// </summary>
    /// <remarks>
    /// The SDK exposes <c>FrameTick()</c> — process everything waiting, return
    /// without blocking — which is exactly a game loop's shape, so the connection
    /// is driven from <c>Update</c> and there is no background thread to reason
    /// about.
    /// </remarks>
    public sealed class VroxNet : MonoBehaviour
    {
        [Tooltip("SpacetimeDB host.")]
        public string Uri = "http://127.0.0.1:3000";

        [Tooltip("Database name.")]
        public string Database = "vrox";

        public static VroxNet? Instance { get; private set; }

        public DbConnection? Conn { get; private set; }
        public Identity? LocalIdentity { get; private set; }

        /// <summary>True once connected and the initial data has arrived.</summary>
        public bool Ready { get; private set; }

        /// <summary>
        /// Raised once the initial subscription has been applied.
        /// </summary>
        /// <remarks>
        /// Exists so editor tooling can push the authored catalogue the moment
        /// there is a connection to push it through, without this file knowing
        /// anything about the editor.
        /// </remarks>
        public static event System.Action<DbConnection>? Subscribed;

        /// <summary>
        /// Everything this client reads. Every table it touches must be here.
        /// </summary>
        /// <remarks>
        /// An unsubscribed table is not an error — it is simply always empty, and
        /// code that reads it concludes the rows do not exist. That has now cost
        /// two separate bugs: projectiles that never drew, and a weapon that could
        /// never be equipped because the catalogue looked empty. Adding a table on
        /// the server means adding a line here.
        /// </remarks>
        private static readonly string[] Queries =
        {
            // This client's own player row, wherever it is. Every other player is
            // per zone, but the zone is read from this row, so it cannot live
            // inside the subscription it decides.
            "SELECT * FROM player WHERE identity = :sender",

            "SELECT * FROM weapon_def",
            "SELECT * FROM enemy_def",
            "SELECT * FROM player_config",

            // The realm row is how the client learns the world's size and
            // whether terrain is generated, instead of duplicating constants
            // that then drift.
            "SELECT * FROM realm",
            "SELECT * FROM realm_config",
            "SELECT * FROM biome_def",
            "SELECT * FROM phase_def",

            // Which copies of the world exist, and which layout each uses. The
            // zone watcher needs the layout to know which terrain table to read.
            "SELECT * FROM zone",

            // Every layout's header, so an entrance can be coloured before anyone
            // has opened its dungeon. Their terrain is per zone.
            "SELECT * FROM dungeon_layout",

            // The catalogue every other item reference is resolved through. A
            // bag and, later, an inventory slot record an id and a count; this
            // is what turns that into something with a name and a colour.
            //
            // loot_entry is deliberately absent and is not a public table: drop
            // tables are what an enemy *might* give, which is design data the
            // client has no use for and no business reading. portal_drop and
            // layout_spawner are private for the same reason.
            "SELECT * FROM item_def",

            // Every player's inventory, not just this one's. A per-player filter
            // is the right thing once there is something worth hiding; today it
            // would be a subscription rule to maintain for no benefit.
            "SELECT * FROM inventory",

            // Banked items. Subscribed because a vault the client cannot read is
            // a vault the player cannot be shown.
            "SELECT * FROM vault",

            // The account's characters, for the selection screen.
            "SELECT * FROM character",
            "SELECT * FROM player_stat",

            // The live damage and debuff tallies are deliberately absent. They
            // are updated on every hit, so subscribing would replicate a row
            // change per bullet to every client — the exact per-hit traffic the
            // aggregate design exists to avoid. A fight summary belongs as a
            // one-shot at the end, not as a live stream.
        };

        /// <summary>
        /// Everything this client reads about the zone it is standing in.
        /// </summary>
        /// <remarks>
        /// Filtered on the server, so a client receives the bullets, enemies and
        /// movement of its own zone and nothing else — bandwidth that grows with
        /// the zone rather than with the whole server. The rule on
        /// <see cref="Queries"/> applies here too: a world table the game reads
        /// that is missing from this list is silently empty.
        /// </remarks>
        private static string[] ZoneQueries(uint zone, ushort layout) => new[]
        {
            $"SELECT * FROM player WHERE zone_id = {zone}",
            $"SELECT * FROM shot WHERE zone_id = {zone}",

            // Hitscan rays, drawn for a moment and dropped. Subscribed because a
            // tracer nobody can read is a gun that fires invisibly.
            $"SELECT * FROM tracer WHERE zone_id = {zone}",

            // One row per hit, never stored — an event table, so only the insert
            // callback fires. Subscribed because the enemy row collapses a whole
            // volley into one number, and a shotgun is eight of them.
            $"SELECT * FROM hit WHERE zone_id = {zone}",
            $"SELECT * FROM dummy WHERE zone_id = {zone}",
            $"SELECT * FROM enemy WHERE zone_id = {zone}",
            $"SELECT * FROM spawner WHERE zone_id = {zone}",
            $"SELECT * FROM loot_drop WHERE zone_id = {zone}",
            $"SELECT * FROM portal WHERE zone_id = {zone}",

            // The realm's map and a dungeon's live in different tables — see
            // LayoutChunk on the server for why they could not share one.
            layout == 0
                ? "SELECT * FROM terrain_chunk"
                : $"SELECT * FROM layout_chunk WHERE layout_id = {layout}",
        };

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            Conn = DbConnection.Builder()
                .WithUri(Uri)
                .WithDatabaseName(Database)
                .OnConnect(OnConnect)
                .OnConnectError(e => Debug.LogError($"[vrox] connect failed: {e.Message}"))
                .OnDisconnect((_, e) =>
                {
                    Ready = false;
                    ForgetZone();
                    Debug.LogWarning($"[vrox] disconnected: {e?.Message ?? "clean"}");
                })
                .Build();
        }

        private void OnConnect(DbConnection conn, Identity identity, string token)
        {
            LocalIdentity = identity;
            Debug.Log($"[vrox] connected as {identity}");

            conn.SubscriptionBuilder()
                .OnApplied(_ =>
                {
                    Ready = true;
                    Debug.Log($"[vrox] subscribed: {conn.Db.Player.Count} player(s)");
                    WarnAboutMissingRenderers();
                    Subscribed?.Invoke(conn);
                })
                .Subscribe(Queries);
        }

        /// <summary>
        /// Complains about replicated things nothing in the scene draws.
        /// </summary>
        /// <remarks>
        /// A renderer that is simply absent cannot warn about itself, and the
        /// result is that a working server looks like a broken one: loot dropped,
        /// the log said so, and the ground stayed empty with nothing anywhere
        /// saying why. That already cost a debugging session.
        ///
        /// Here rather than in the renderers because this component is the one
        /// thing guaranteed to exist, and once at subscription rather than per
        /// frame because a scene search is not free.
        ///
        /// Only renderers whose absence is silent belong in this list. Players,
        /// enemies and shots are missed immediately; a bag that appears a few
        /// times a minute is not.
        /// </remarks>
        private void WarnAboutMissingRenderers()
        {
            if (FindAnyObjectByType<VroxLoot>() == null)
            {
                Debug.LogWarning("[vrox] no VroxLoot in the scene, so dropped loot bags "
                               + "will never be drawn however well the loot tables are "
                               + "rolling. Add the component, or run Vrox > Create Scene.",
                                 this);
            }
            if (FindAnyObjectByType<VroxPortals>() == null)
            {
                Debug.LogWarning("[vrox] no VroxPortals in the scene, so dungeon portals will "
                               + "never be drawn or usable. Add the component, or run "
                               + "Vrox > Create Scene.", this);
            }
        }

        private SubscriptionHandle? _zoneHandle;
        private (uint zone, ushort layout)? _zoneKey;
        private (uint zone, ushort layout)? _pendingKey;
        private (uint zone, ushort layout)? _failedKey;

        /// <summary>
        /// The zone whose rows this client is receiving, or 0 before the first arrive.
        /// </summary>
        /// <remarks>
        /// The zone the <em>subscription</em> covers, not the zone the player row
        /// names. The two differ between the server moving the player and the new
        /// zone's rows landing, and anything drawing rows has to agree with what it
        /// has actually been sent.
        /// </remarks>
        public uint SubscribedZone => _zoneKey?.zone ?? 0;

        /// <summary>
        /// Keeps the zone subscription on whichever zone the server says we are in.
        /// </summary>
        /// <remarks>
        /// Driven by the replicated player row, never by having pressed a portal
        /// key: the reducer can refuse, and a client that switched on intent would
        /// sit in an empty zone it was never let into.
        ///
        /// The new zone is subscribed before the old one is dropped, so the world
        /// never goes blank between them. It waits for the zone row rather than
        /// assuming the realm, because a dungeon's terrain is in a different table
        /// and a guess would subscribe the wrong one.
        ///
        /// A zone whose subscription the server rejected is not retried every
        /// frame; the error is logged once and stands until the zone changes.
        /// </remarks>
        private void FollowZone()
        {
            if (!Ready || Conn is not { } conn || LocalPlayer is not { } player)
            {
                return;
            }
            if (conn.Db.Zone.Id.Find(player.ZoneId) is not { } zone)
            {
                return;
            }

            (uint zone, ushort layout) key = (player.ZoneId, zone.LayoutId);
            if (_zoneKey == key || _pendingKey == key || _failedKey == key)
            {
                return;
            }

            _pendingKey = key;
            var previous = _zoneHandle;
            SubscriptionHandle? handle = null;
            handle = conn.SubscriptionBuilder()
                .OnApplied(_ =>
                {
                    if (_pendingKey != key)
                    {
                        // Moved again before this one landed.
                        handle?.Unsubscribe();
                        return;
                    }
                    _zoneHandle = handle;
                    _zoneKey = key;
                    _pendingKey = null;
                    _failedKey = null;
                    previous?.Unsubscribe();
                    Debug.Log($"[vrox] receiving zone {key.zone} (layout {key.layout})");
                })
                .OnError((_, e) =>
                {
                    Debug.LogError($"[vrox] the server refused the subscription for zone {key.zone}: "
                                 + e.Message);
                    if (_pendingKey == key)
                    {
                        _pendingKey = null;
                    }
                    _failedKey = key;
                })
                .Subscribe(ZoneQueries(key.zone, key.layout));
        }

        /// <summary>Drops zone bookkeeping; the handles died with the connection.</summary>
        private void ForgetZone()
        {
            _zoneHandle = null;
            _zoneKey = null;
            _pendingKey = null;
            _failedKey = null;
        }

        private void Update()
        {
            Conn?.FrameTick();
            FollowZone();
            WarnIfStranded();
        }

        private bool _warnedStranded;

        /// <summary>
        /// Says why nothing is happening, when nothing is going to happen.
        /// </summary>
        /// <remarks>
        /// Connecting creates an account, not a character, and a player with no
        /// character cannot move, shoot or be shot at. That is correct, and from
        /// inside the game it looks exactly like a broken build — a connected
        /// client, a drawn world, and controls that do nothing.
        ///
        /// So the one case where nobody is handling it says so, once, with the
        /// two ways out. Checked only after Ready, or it would fire during the
        /// second before the subscription lands.
        /// </remarks>
        private void WarnIfStranded()
        {
            if (_warnedStranded || !Ready || LocalCharacterId != 0)
            {
                return;
            }
            if (FindAnyObjectByType<VroxGuestCharacter>() != null
                || FindAnyObjectByType<UI.CharacterSelectView>() != null)
            {
                return;
            }

            _warnedStranded = true;
            Debug.LogError("[vrox] connected with no character, and nothing in the scene "
                         + "chooses one — so movement and shooting will do nothing. Add a "
                         + "VroxGuestCharacter to drop straight in, or a CharacterSelectView "
                         + "for the real screen.", this);
        }

        /// <summary>
        /// The character this account is playing, or 0 at the character screen.
        /// </summary>
        /// <remarks>
        /// One definition, because an inventory is now keyed by character and
        /// several screens have to ask the same question. Reading it from the
        /// player row rather than remembering a selection keeps it the server's
        /// answer — a client that cached which character it chose would keep
        /// showing that character's pack after it died.
        /// </remarks>
        public ulong LocalCharacterId => LocalPlayer is { } player ? player.CharacterId : 0ul;

        /// <summary>The local player's row, or null before it arrives.</summary>
        public Player? LocalPlayer =>
            Conn is { } conn && LocalIdentity is { } id ? conn.Db.Player.Identity.Find(id) : null;

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            // Disconnect before the object goes away. Left open, the SDK reports
            // results for calls it no longer has a record of, from a background
            // thread, every time you stop playing.
            try
            {
                Conn?.Disconnect();
            }
            catch (System.Exception)
            {
                // Already gone.
            }
        }
    }
}
