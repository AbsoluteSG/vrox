using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Uploads authored weapons to the server's catalogue.
    /// </summary>
    /// <remarks>
    /// The assets are the authoring format; the server's rows are what the game
    /// runs on. This is the step between them, and it has to be run after editing
    /// a weapon — nothing watches the assets.
    ///
    /// It needs a live connection, which means play mode. That is a real
    /// constraint of pushing through a reducer rather than baking a catalogue
    /// into the module, and it is the right trade while weapons change every few
    /// minutes.
    /// </remarks>
    [InitializeOnLoad]
    public static class PushEquipment
    {
        /// <summary>
        /// Pushes automatically as soon as a play session connects.
        /// </summary>
        /// <remarks>
        /// Without this, editing a weapon and pressing Play silently keeps the
        /// previous values, because the server's catalogue is what actually
        /// fires and nothing had told it. Remembering a menu item every time is
        /// exactly the kind of step that gets skipped and then blamed on the code.
        /// </remarks>
        static PushEquipment()
        {
            // Removed before adding: with Domain Reload turned off, entering play
            // mode runs this again without clearing the previous subscription, and
            // the catalogue would be pushed once per play session ever started.
            Vrox.VroxNet.Subscribed -= OnSubscribed;
            Vrox.VroxNet.Subscribed += OnSubscribed;
        }

        private static void OnSubscribed(SpacetimeDB.Types.DbConnection conn) => Push();

        /// <summary>
        /// Uploads every authored catalogue: items, weapons, loot pools, enemies,
        /// phases, the realm, biomes, terrain, spawners and the player config.
        /// </summary>
        /// <remarks>
        /// One button on purpose. Splitting it meant remembering which pushes a
        /// given change needed, and a half-pushed catalogue is harder to reason
        /// about than one that was not pushed — an enemy naming a loot pool the
        /// server has not been told about looks exactly like a loot table that
        /// never rolls.
        ///
        /// Works with the game stopped; see <see cref="EditorConnection"/>.
        /// </remarks>
        [MenuItem("Vrox/Push All")]
        public static void Push() => EditorConnection.Run("push", PushAll);

        private static void PushAll(SpacetimeDB.Types.DbConnection conn)
        {
            var weapons = LoadAll<WeaponItem>();
            if (weapons.Count == 0)
            {
                Debug.LogWarning("Vrox: no WeaponItem assets found. Create one with Assets > Create > Vrox > Weapon.");
                return;
            }

            if (!Validate(LoadAll<EquipmentItem>()))
            {
                return;
            }

            // The catalogue goes first, and every kind goes in it. Loot entries
            // and, later, inventory rows address items by id alone, so the server
            // has to be able to answer "what is item 7" before anything can name
            // one. Pushing weapons first would leave a window where a loot table
            // referred to armour the server had never heard of.
            var catalogue = LoadAll<EquipmentItem>();
            foreach (var item in catalogue)
            {
                conn.Reducers.UpsertItemDef(
                    item.CatalogueId,
                    string.IsNullOrWhiteSpace(item.DisplayName) ? item.name : item.DisplayName,
                    (byte)item.Kind,
                    (ushort)item.MaxStack,
                    Vrox.PackedColour.Pack(item.Tint),
                    item.Tier,
                    item.Width,
                    item.Height);
            }
            Debug.Log($"Vrox: pushed {catalogue.Count} catalogue item(s).");

            foreach (var w in weapons)
            {
                var pattern = w.Pattern ?? new SingleShot();

                // Fully qualified because Vrox.Equipment is in scope and the
                // authoring type beside this one is BulletVariant — two names for
                // the same idea, deliberately different so neither shadows the
                // other.
                var mix = (w.Mix ?? new List<BulletVariant>())
                    .Where(v => v != null)
                    .Select(v => new SpacetimeDB.Types.BulletProfile
                    {
                        Element = (byte)v.Element,
                        DebuffKind = (byte)v.Debuff,
                        DebuffSeconds = v.DebuffSeconds,
                        DamageMin = v.DamageMin,
                        DamageMax = v.DamageMax,
                        Speed = v.Speed,
                        Size = v.Size,
                        LifetimeMs = v.LifetimeMs,

                        // Colour and art in one column, 0xSSRRGGBB. A nested type
                        // inside a column cannot gain a field without a manual
                        // migration, and the top byte of a packed colour was
                        // already spare. See BulletProfile.Tint on the server.
                        Tint = Vrox.PackedColour.Pack(v.Tint)
                               | ((uint)Mathf.Clamp(v.BulletSpriteId, 0, 255) << 24),
                    })
                    .ToList();

                conn.Reducers.UpsertWeapon(
                    w.Id,
                    string.IsNullOrWhiteSpace(w.DisplayName) ? w.name : w.DisplayName,
                    w.FireRateMs,
                    w.DamageMin,
                    w.DamageMax,
                    w.ProjectileSpeed,
                    w.ProjectileLifetimeMs,
                    w.ProjectileSize,
                    (byte)pattern.Kind,
                    pattern.Count,
                    pattern.SpreadDegrees,
                    w.SpinDegreesPerSec,
                    w.WaveAmplitude,
                    w.WaveFrequency,
                    (byte)w.Element,
                    (byte)w.Debuff,
                    w.DebuffSeconds,
                    Vrox.PackedColour.Pack(w.Tint),
                    mix,
                    (byte)w.Assignment,
                    w.Magazine, w.ReloadMs, w.ReloadPerShell, w.Kickback,
                    w.Range, w.Pierce, w.FalloffPercent, w.SplitDamage,
                    w.MoveSpeedPercent,
                    pattern.Groups,
                    (byte)Mathf.Clamp(w.BulletSpriteId, 0, 255));
            }

            // Before the enemies that name them, so an enemy weighting can never
            // point at a pool the server has not been told about yet.
            var pools = LoadAll<LootPoolItem>();
            foreach (var pool in pools)
            {
                if (pool.Id == 0)
                {
                    Debug.LogError($"Vrox: loot pool \"{pool.name}\" has id 0, which is "
                                 + "reserved for \"drops nothing\".", pool);
                    continue;
                }

                conn.Reducers.UpsertLootPool(pool.Id, pool.name, (byte)pool.Bag);
                conn.Reducers.ClearLoot(pool.Id);

                for (int i = 0; i < pool.Items.Count && i < 255; i++)
                {
                    var roll = pool.Items[i];
                    if (roll?.Item == null)
                    {
                        continue;
                    }
                    conn.Reducers.UpsertLoot(
                        pool.Id,
                        (byte)i,
                        roll.Item.CatalogueId,
                        roll.ChancePercent,
                        (ushort)Mathf.Min(roll.Count, roll.Item.MaxStack));
                }
            }
            Debug.Log($"Vrox: pushed {pools.Count} loot pool(s).");

            var enemies = LoadAll<EnemyItem>();
            foreach (var e in enemies)
            {
                var move = e.Movement ?? new WanderMovement();
                conn.Reducers.UpsertEnemyDef(
                    e.Id,
                    string.IsNullOrWhiteSpace(e.DisplayName) ? e.name : e.DisplayName,
                    e.MaxHp,
                    e.Radius,
                    e.Speed,
                    (byte)move.Kind,
                    move.AggroRange,
                    move.PreferredRange,
                    e.AttackRange,
                    e.Weapon != null ? e.Weapon.Id : (ushort)0,
                    e.TouchDamage,
                    (byte)move.Pivot,
                    e.IsBoss,
                    e.MaxEnergy,
                    Vrox.PackedColour.Pack(e.Tint),
                    e.Resist.ConvertAll(r => new SpacetimeDB.Types.Resistance
                    {
                        Element = (byte)r.Element,
                        Percent = r.Percent,
                    }));

                // Cleared first: phases are addressed by index, so shortening a
                // list would otherwise leave the old tail behind and the boss
                // would carry on into phases that no longer exist in the asset.
                conn.Reducers.ClearPhases(e.Id);
                for (int i = 0; i < e.Phases.Count && i < 255; i++)
                {
                    var phase = e.Phases[i];
                    var phaseMove = phase.Movement ?? new StaticMovement();
                    var exit = phase.Transition ?? new NeverTransition();

                    conn.Reducers.UpsertPhase(
                        e.Id,
                        (byte)i,
                        (byte)phaseMove.Kind,
                        phaseMove.AggroRange,
                        phaseMove.PreferredRange,
                        phase.AttackRange,
                        phase.Weapon != null ? phase.Weapon.Id : (ushort)0,
                        (byte)phaseMove.Pivot,
                        phase.Speed,
                        phase.Flags,
                        phase.DamageTakenPercent,
                        (byte)exit.Kind,
                        exit.Value);
                }

                // Cleared first for the same reason as phases: entries are
                // addressed by index, so removing a row from the asset would
                // otherwise leave the old one on the server and the enemy would
                // keep rolling a pool the designer deleted.
                conn.Reducers.ClearEnemyLoot(e.Id);

                // Index 0 is the no-drop slice, always written even when it is
                // zero. Skipping it when zero would leave a stale weight from a
                // previous push behind, and an enemy would quietly stop dropping.
                conn.Reducers.UpsertEnemyLoot(e.Id, 0, 0, e.NoDropWeight);

                for (int i = 0; i < e.LootPools.Count && i < 254; i++)
                {
                    var weighted = e.LootPools[i];
                    if (weighted?.Pool == null || weighted.Weight == 0)
                    {
                        continue;
                    }
                    conn.Reducers.UpsertEnemyLoot(
                        e.Id, (byte)(i + 1), weighted.Pool.Id, weighted.Weight);
                }
            }

            PushPlayerConfig(conn);
            PushRealm.Push(conn);

            // After the enemy catalogue above: a biome's mix names enemy def ids,
            // and pushing biomes first would reference enemies the server has not
            // been told about yet.
            PushRealm.PushBiomes(conn);

            // The scene's tilemap is not pushed over a generated realm. The
            // server refuses these writes anyway — it is the only thing that can
            // enforce it — but checking here keeps one connect from logging
            // sixty-four refusals.
            if (PushRealm.IsGenerated(conn))
            {
                Debug.Log("Vrox: the realm is generated, so the scene's tilemap was not "
                        + "pushed. Vrox > Use Authored Terrain hands the world back to "
                        + "hand-painted areas.");
            }
            else
            {
                PushTerrain.Push(conn);
            }
            PushSpawners(conn);

            Debug.Log($"Vrox: pushed {weapons.Count} weapon(s) and {enemies.Count} enemy type(s): "
                    + string.Join(", ", weapons.Select(w => $"{w.Id} {w.DisplayName}")
                        .Concat(enemies.Select(e => $"{e.Id} {e.DisplayName}"))));
        }

        /// <summary>
        /// Sends the player stat block, if there is one.
        /// </summary>
        /// <remarks>
        /// Exactly one asset is expected. More than one is refused rather than
        /// picked between: whichever won would depend on asset ordering, and the
        /// stats would silently change when a file was renamed.
        /// </remarks>
        private static void PushPlayerConfig(SpacetimeDB.Types.DbConnection conn)
        {
            var configs = LoadAll<PlayerConfigItem>();
            if (configs.Count == 0)
            {
                return;
            }
            if (configs.Count > 1)
            {
                Debug.LogError("Vrox: more than one Player Config asset — "
                             + string.Join(", ", configs.Select(c => c.name))
                             + ". Keep one; the server holds a single stat block.", configs[0]);
                return;
            }

            var c = configs[0];
            conn.Reducers.UpsertPlayerConfig(
                c.MaxHp, c.Defense, c.Speed, c.Dexterity,
                c.HpRegenPerSec, c.CritChance, c.CritMultiplier,
                c.StartingWeapon != null ? c.StartingWeapon.CatalogueId : (ushort)0);
            Debug.Log($"Vrox: pushed player config — {c.MaxHp}hp, def {c.Defense}, "
                    + $"speed {c.Speed}, dex {c.Dexterity}, regen {c.HpRegenPerSec}/s, "
                    + $"starting weapon {(c.StartingWeapon != null ? c.StartingWeapon.name : "none")}.");
        }

        /// <summary>
        /// Replaces the server's spawners with the ones in the open scene.
        /// </summary>
        /// <remarks>
        /// Cleared and rewritten wholesale rather than reconciled, because ids come
        /// from scene order and shift whenever an object is added or removed.
        /// Reconciling would need stable ids on every spawner, which is bookkeeping
        /// for the designer to do by hand and get wrong.
        ///
        /// Clearing also removes the enemies those spawners made, so a re-push does
        /// not strand a crowd under ids that now mean something else.
        /// </remarks>
        private static void PushSpawners(SpacetimeDB.Types.DbConnection conn)
        {
            var spawners = Object.FindObjectsByType<Vrox.VroxSpawner>(FindObjectsInactive.Exclude);

            conn.Reducers.ClearSpawners(0);

            ushort id = 0;
            int skipped = 0;
            foreach (var spawner in spawners)
            {
                var population = spawner.Population;
                if (population == null)
                {
                    skipped++;
                    continue;
                }

                var composition = population.Usable
                    .Select(e => new SpacetimeDB.Types.AreaEntry
                    {
                        EnemyDefId = e.Enemy!.Id,
                        Weight = (ushort)Mathf.Clamp(e.Weight, 0, 1000),
                        MaxAlive = (ushort)Mathf.Clamp(e.MaxAlive, 0, 200),
                    })
                    .ToList();

                if (composition.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var p = spawner.transform.position;
                conn.Reducers.UpsertSpawner(
                    ++id,
                    composition,
                    p.x,
                    p.y,
                    spawner.Radius,
                    (ushort)Mathf.Clamp(population.MaxAlive, 0, 200),
                    (ushort)Mathf.Clamp(population.IntervalMs, 100, 30000),
                    0);
            }

            if (skipped > 0)
            {
                Debug.LogWarning($"Vrox: {skipped} spawner(s) have no Population assigned, or a population "
                               + "with nothing usable in it, and were skipped.");
            }
            if (id > 0)
            {
                Debug.Log($"Vrox: pushed {id} spawner(s) from the scene.");
            }
        }

        /// <summary>
        /// Rejects a catalogue that would behave confusingly, rather than pushing it.
        /// </summary>
        /// <remarks>
        /// Duplicate ids are the one that matters: the second push silently
        /// overwrites the first, so a weapon you are testing turns into a
        /// different weapon and nothing says why.
        /// </remarks>
        /// <summary>
        /// Checks item ids before anything is uploaded.
        /// </summary>
        /// <remarks>
        /// Uniqueness is checked across <em>every</em> equipment kind, not within
        /// each. There is one catalogue and one id space, because a loot bag and
        /// an inventory slot record an id and nothing else — so a sword and a
        /// helmet both called 1 are the same item to everything downstream, and
        /// the bag would hand out whichever was pushed last.
        ///
        /// Refused before the first reducer call rather than reported after,
        /// because a half-pushed catalogue is harder to reason about than one
        /// that was not pushed at all.
        /// </remarks>
        private static bool Validate(List<EquipmentItem> items)
        {
            bool ok = true;

            foreach (var item in items.Where(i => i.Id == 0))
            {
                Debug.LogError($"Vrox: \"{item.name}\" has id 0, which is reserved for "
                             + "\"no item\".", item);
                ok = false;
            }

            foreach (var item in items.Where(i => i.Id > EquipmentItem.MaxAuthoredId))
            {
                Debug.LogError($"Vrox: \"{item.name}\" has id {item.Id}, above the maximum "
                             + $"of {EquipmentItem.MaxAuthoredId}. The top two bits of a "
                             + "catalogue id carry the item's kind.", item);
                ok = false;
            }

            // Grouped by catalogue id, not authored id. Two kinds may both use 1;
            // what must not happen is two items resolving to the same catalogue
            // row, and that is the number a bag actually records.
            foreach (var group in items.GroupBy(i => i.CatalogueId).Where(g => g.Count() > 1))
            {
                string names = string.Join(", ", group.Select(i => $"{i.name} ({i.Kind} {i.Id})"));
                Debug.LogError($"Vrox: catalogue id {group.Key} is claimed by more than one "
                             + $"item ({names}). Ids must be unique within a kind.",
                               group.First());
                ok = false;
            }

            return ok;
        }

        private static List<T> LoadAll<T>() where T : Object =>
            AssetDatabase.FindAssets($"t:{typeof(T).Name}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<T>)
                .Where(a => a != null)
                .ToList();
    }
}
