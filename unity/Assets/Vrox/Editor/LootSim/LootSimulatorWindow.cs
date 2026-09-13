using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Configures loot tables and shows what they really drop.
    /// </summary>
    /// <remarks>
    /// Four tabs:
    /// - All Enemies: every enemy's drop odds side by side, with weights editable inline.
    /// - Enemy: one drop table — weights, exact odds, a seeded batch of kills, and pack fit.
    /// - Pools: a pool's entries, what it yields, and which enemies roll it. New pools are made here.
    /// - Items: where an item drops from, and how many kills it takes.
    ///
    /// Everything is computed from the rows Push All would write (<see cref="LootAssets"/>),
    /// using the client copy of the server's roll (<see cref="LootMath"/>). Edits go to the
    /// assets with undo; the server only sees them after Push All.
    /// </remarks>
    public sealed class LootSimulatorWindow : EditorWindow
    {
        private enum Tab
        {
            Enemies,
            Enemy,
            Pools,
            Items,
        }

        private static readonly string[] TabNames = { "All Enemies", "Enemy", "Pools", "Items" };
        private const float Cell = 42f;
        private const float SideWidth = 360f;

        [SerializeField] private Tab _tab;
        [SerializeField] private EnemyItem? _enemy;
        [SerializeField] private LootPoolItem? _pool;
        [SerializeField] private EquipmentItem? _item;
        [SerializeField] private bool _locked;
        [SerializeField] private int _kills = 1000;
        [SerializeField] private int _seed = 1;
        [SerializeField] private int _packBags = 10;
        [SerializeField] private int _shownBags = 24;
        [SerializeField] private string _filter = "";
        [SerializeField] private Vector2 _scrollA;
        [SerializeField] private Vector2 _scrollB;

        private readonly LootRoller _roller = new();
        private int _rolledHash;
        private readonly List<string> _warnings = new();
        private readonly Dictionary<Object, SerializedObject> _serialized = new();
        private readonly List<PoolWeightRow> _hashRows = new();
        private readonly List<LootEntryRow> _hashEntries = new();

        private static GUIStyle?[] _tierStyles = new GUIStyle?[LootOdds.BagKinds];

        [MenuItem("Vrox/Loot Simulator")]
        private static void Menu()
        {
            var window = GetWindow<LootSimulatorWindow>();
            window.titleContent = new GUIContent("Loot Simulator");
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Loot Simulator");
            if (_enemy == null && _pool == null && _item == null)
            {
                OnSelectionChange();
            }
        }

        private void OnSelectionChange()
        {
            if (_locked)
            {
                return;
            }
            switch (Selection.activeObject)
            {
                case EnemyItem enemy:
                    _enemy = enemy;
                    _tab = Tab.Enemy;
                    break;
                case LootPoolItem pool:
                    _pool = pool;
                    _tab = Tab.Pools;
                    break;
                case EquipmentItem item:
                    _item = item;
                    _tab = Tab.Items;
                    break;
                default:
                    return;
            }
            Repaint();
        }

        private void OnGUI()
        {
            var lib = LootAssets.Shared;
            DrawToolbar();

            LootWarnings.Global(lib, _warnings);
            foreach (var warning in _warnings)
            {
                EditorGUILayout.HelpBox(warning, MessageType.Error);
            }

            switch (_tab)
            {
                case Tab.Enemies: EnemiesTab(lib); break;
                case Tab.Enemy: EnemyTab(lib); break;
                case Tab.Pools: PoolsTab(lib); break;
                case Tab.Items: ItemsTab(lib); break;
            }
        }

        private void DrawToolbar()
        {
            using var _ = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);
            _locked = GUILayout.Toggle(_locked, new GUIContent("Lock", "Keep this tab's asset while selecting others."),
                                       EditorStyles.toolbarButton, GUILayout.Width(40f));
            _tab = (Tab)GUILayout.Toolbar((int)_tab, TabNames, EditorStyles.toolbarButton, GUILayout.Width(340f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("Refresh", "Reload the asset lists."), EditorStyles.toolbarButton))
            {
                LootAssets.Reload();
            }
            if (GUILayout.Button(new GUIContent("Push All", "Vrox/Push All: send every authored catalogue to the server."),
                                 EditorStyles.toolbarButton))
            {
                PushEquipment.Push();
            }
        }

        // ── All Enemies ─────────────────────────────────────────────────────────

        private void EnemiesTab(LootAssets lib)
        {
            _filter = EditorGUILayout.TextField("Filter", _filter);
            _scrollA = EditorGUILayout.BeginScrollView(_scrollA);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Enemy", EditorStyles.boldLabel, GUILayout.Width(190f));
                GUILayout.Label(new GUIContent("No drop", "No Drop Weight."), EditorStyles.boldLabel, GUILayout.Width(64f));
                GUILayout.Label(new GUIContent("Bag", "Chance a kill drops a bag."), EditorStyles.boldLabel, GUILayout.Width(56f));
                for (int k = 0; k < LootOdds.BagKinds; k++)
                {
                    GUILayout.Label(LootDrawing.BagName((byte)k), TierStyle(k), GUILayout.Width(66f));
                }
                GUILayout.Label(new GUIContent("Stacks", "Average stacks per kill."), EditorStyles.boldLabel, GUILayout.Width(52f));
                GUILayout.Label("Pools and weights", EditorStyles.boldLabel);
            }

            foreach (var e in lib.Enemies)
            {
                if (_filter.Length > 0 && e.name.IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var odds = LootOdds.ForEnemy(e, lib);
                var so = Serialized(e);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(e.IsBoss ? $"★ {e.name}" : e.name, EditorStyles.label, GUILayout.Width(190f)))
                    {
                        _enemy = e;
                        _tab = Tab.Enemy;
                    }
                    WeightField(so.FindProperty("NoDropWeight"), 64f);
                    GUILayout.Label(LootOdds.Percent(odds.Bag), GUILayout.Width(56f));
                    for (int k = 0; k < LootOdds.BagKinds; k++)
                    {
                        GUILayout.Label(LootOdds.Percent(odds.ByKind[k]), GUILayout.Width(66f));
                    }
                    GUILayout.Label(odds.ExpectedStacks.ToString("0.00"), GUILayout.Width(52f));

                    var list = so.FindProperty("LootPools");
                    for (int i = 0; i < list.arraySize; i++)
                    {
                        var element = list.GetArrayElementAtIndex(i);
                        var pool = element.FindPropertyRelative("Pool").objectReferenceValue;
                        GUILayout.Label(pool != null ? pool.name : "(empty)", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                        WeightField(element.FindPropertyRelative("Weight"), 44f);
                    }
                }
                so.ApplyModifiedProperties();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("Percentages are exact, per kill. A stack of three salves counts as one stack.",
                                       EditorStyles.miniLabel);
        }

        // ── Enemy ───────────────────────────────────────────────────────────────

        private void EnemyTab(LootAssets lib)
        {
            _enemy = (EnemyItem?)EditorGUILayout.ObjectField("Enemy", _enemy, typeof(EnemyItem), false);
            if (_enemy == null)
            {
                EditorGUILayout.HelpBox("Select an Enemy, or pick one in All Enemies.", MessageType.Info);
                return;
            }

            var odds = LootOdds.ForEnemy(_enemy, lib);
            using var _ = new EditorGUILayout.HorizontalScope();

            // Left: the drop table.
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(SideWidth)))
            {
                _scrollA = EditorGUILayout.BeginScrollView(_scrollA);
                var so = Serialized(_enemy);
                EditorGUILayout.LabelField("Drop table", EditorStyles.boldLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("No Drop Weight", GUILayout.Width(172f));
                    var noDrop = so.FindProperty("NoDropWeight");
                    WeightField(noDrop, 60f);
                    GUILayout.Label(Share(noDrop.longValue, odds.TotalWeight), EditorStyles.miniLabel);
                }

                var list = so.FindProperty("LootPools");
                for (int i = 0; i < list.arraySize; i++)
                {
                    var element = list.GetArrayElementAtIndex(i);
                    var poolProp = element.FindPropertyRelative("Pool");
                    var weightProp = element.FindPropertyRelative("Weight");
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PropertyField(poolProp, GUIContent.none, GUILayout.Width(168f));
                        WeightField(weightProp, 60f);
                        bool pushed = poolProp.objectReferenceValue != null && weightProp.longValue > 0;
                        GUILayout.Label(pushed ? Share(weightProp.longValue, odds.TotalWeight) : "skipped", EditorStyles.miniLabel);
                        if (GUILayout.Button(new GUIContent("×", "Remove this pool from the table."), GUILayout.Width(20f)))
                        {
                            list.DeleteArrayElementAtIndex(i);
                            break;
                        }
                    }
                }
                if (GUILayout.Button("Add pool"))
                {
                    list.arraySize++;
                    var added = list.GetArrayElementAtIndex(list.arraySize - 1);
                    added.FindPropertyRelative("Pool").objectReferenceValue = null;
                    added.FindPropertyRelative("Weight").longValue = 1;
                }
                so.ApplyModifiedProperties();

                LootWarnings.Enemy(_enemy, lib, odds, _warnings);
                EditorGUILayout.Space(6f);
                foreach (var warning in _warnings)
                {
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
                EditorGUILayout.EndScrollView();
            }

            // Right: odds, a batch of kills, pack fit.
            using (new EditorGUILayout.VerticalScope())
            {
                _scrollB = EditorGUILayout.BeginScrollView(_scrollB);
                OddsSection(odds);
                EditorGUILayout.Space(10f);
                RollSection(_enemy, lib, odds);
                EditorGUILayout.EndScrollView();
            }
        }

        private void OddsSection(LootOdds.EnemyOdds odds)
        {
            EditorGUILayout.LabelField("Exact odds per kill", EditorStyles.boldLabel);
            string every = odds.Bag > 0.0 ? $"  ·  a bag every {1.0 / odds.Bag:0.0} kills on average" : "";
            EditorGUILayout.LabelField($"Bag {LootOdds.Percent(odds.Bag)}  ·  {odds.ExpectedStacks:0.00} stacks per kill{every}");

            // One bar: each bag kind, then nothing.
            var bar = GUILayoutUtility.GetRect(10f, 16f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                float x = bar.x;
                EditorGUI.DrawRect(bar, new Color(0.2f, 0.2f, 0.2f));
                for (int k = 0; k < LootOdds.BagKinds; k++)
                {
                    float w = bar.width * (float)odds.ByKind[k];
                    EditorGUI.DrawRect(new Rect(x, bar.y, w, bar.height), LootDrawing.Tier(k));
                    x += w;
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int k = 0; k < LootOdds.BagKinds; k++)
                {
                    if (odds.ByKind[k] > 0.0)
                    {
                        GUILayout.Label($"{LootDrawing.BagName((byte)k)} {LootOdds.Percent(odds.ByKind[k])}", TierStyle(k));
                    }
                }
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(4f);
            Row("Pool", "Picked", "Bag if picked", "Bag from it", bold: true);
            foreach (var p in odds.Pools)
            {
                string name = p.Pool != null ? $"{p.Pool.name} ({p.Pool.Bag})" : $"missing pool {p.PoolId}";
                Row(name, LootOdds.Percent(p.Share), LootOdds.Percent(p.BagIfChosen), LootOdds.Percent(p.Share * p.BagIfChosen));
            }

            EditorGUILayout.Space(4f);
            Row("Item", "Per kill", "Per 100 kills", "Kills for 50% / 90%", bold: true);
            foreach (var item in odds.Items)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(item.Item.name, TierStyle(item.Item.Tier), GUILayout.Width(200f)))
                    {
                        _item = item.Item;
                        _tab = Tab.Items;
                    }
                    GUILayout.Label(LootOdds.Percent(item.PerKill), GUILayout.Width(90f));
                    GUILayout.Label((item.ExpectedCount * 100.0).ToString("0.#"), GUILayout.Width(90f));
                    GUILayout.Label($"{LootOdds.Kills(LootOdds.KillsFor(item.PerKill, 0.5))} / {LootOdds.Kills(LootOdds.KillsFor(item.PerKill, 0.9))}");
                }
            }
        }

        private void RollSection(EnemyItem enemy, LootAssets lib, LootOdds.EnemyOdds odds)
        {
            EditorGUILayout.LabelField("Roll kills", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _kills = Mathf.Clamp(EditorGUILayout.IntField("Kills", _kills), 1, 200_000);
                _seed = EditorGUILayout.IntField("Seed", _seed);
                if (GUILayout.Button("New seed", GUILayout.Width(80f)))
                {
                    _seed = Random.Range(1, int.MaxValue);
                }
            }

            // Re-rolled only when something that decides the result changed.
            int hash = RollHash(enemy, lib);
            if (hash != _rolledHash)
            {
                _roller.Roll(enemy, lib, _kills, _seed);
                _rolledHash = hash;
            }

            double observed = _roller.Kills > 0 ? _roller.BagCount / (double)_roller.Kills : 0.0;
            EditorGUILayout.LabelField(
                $"{_roller.BagCount:N0} bags from {_roller.Kills:N0} kills ({LootOdds.Percent(observed)}, exact {LootOdds.Percent(odds.Bag)})  ·  " +
                $"longest run without a bag: {_roller.LongestDry:N0} kills");
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int k = 0; k < LootOdds.BagKinds; k++)
                {
                    if (_roller.KindCounts[k] > 0)
                    {
                        GUILayout.Label($"{LootDrawing.BagName((byte)k)} ×{_roller.KindCounts[k]:N0}", TierStyle(k));
                    }
                }
                GUILayout.FlexibleSpace();
            }

            Row("Item", "Kills with it", "Rolled", "Exact", bold: true);
            foreach (var item in odds.Items)
            {
                int hits = _roller.KillsWithItem.TryGetValue(item.Item.CatalogueId, out var h) ? h : 0;
                long total = _roller.ItemCount.TryGetValue(item.Item.CatalogueId, out var c) ? c : 0;
                Row(item.Item.name, $"{hits:N0} (×{total:N0})",
                    LootOdds.Percent(_roller.Kills > 0 ? hits / (double)_roller.Kills : 0.0),
                    LootOdds.Percent(item.PerKill));
            }

            // The bags themselves, flowed across the width.
            EditorGUILayout.Space(8f);
            _shownBags = EditorGUILayout.IntSlider("Bags shown", _shownBags, 0, Mathf.Min(LootRoller.KeptBags, Mathf.Max(1, _roller.Bags.Count)));
            FlowBags(lib, _roller.Bags.Take(_shownBags).ToList());

            // Pack fit.
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Pack fit", EditorStyles.boldLabel);
            if (_roller.Bags.Count == 0)
            {
                EditorGUILayout.HelpBox("No bags rolled.", MessageType.Info);
                return;
            }
            _packBags = EditorGUILayout.IntSlider("Take All from bags", _packBags, 1, _roller.Bags.Count);
            var fit = _roller.Fit(lib, _packBags);
            EditorGUILayout.LabelField(fit.FirstOverflow < 0
                ? $"All {fit.Looted} bags fit."
                : $"Pack overflows at bag {fit.FirstOverflow + 1} (kill {_roller.Bags[fit.FirstOverflow].Kill + 1:N0}); " +
                  $"{fit.Left.Count} stack(s) left behind in all.");

            var size = LootDrawing.PackSize(Cell);
            var packRect = GUILayoutUtility.GetRect(size.x, size.y, GUILayout.ExpandWidth(false));
            if (Event.current.type == EventType.Repaint)
            {
                LootDrawing.Pack(packRect, fit.Pack, lib, Cell);
            }
            foreach (var (bag, left) in fit.Left.Take(40))
            {
                var def = lib.Item(left.ItemId);
                EditorGUILayout.LabelField($"Bag {bag + 1}: left {left.Count}× {(def != null ? def.name : $"#{left.ItemId}")}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField("Starts from an empty 5x3 pack and uses Take All on each bag in order, as " +
                                       "TakeAllFromBag does. A real pack already holds things and fills sooner.",
                                       EditorStyles.wordWrappedMiniLabel);
        }

        private void FlowBags(LootAssets lib, List<LootRoller.Bag> bags)
        {
            if (bags.Count == 0)
            {
                return;
            }
            float width = Mathf.Max(200f, position.width - SideWidth - 40f);
            float x = 0f, y = 0f, rowHeight = 0f;
            var placed = new List<(Rect, LootRoller.Bag)>();
            foreach (var bag in bags)
            {
                var size = LootDrawing.BagSize(bag.Items.Length, Cell);
                if (x > 0f && x + size.x > width)
                {
                    x = 0f;
                    y += rowHeight + 6f;
                    rowHeight = 0f;
                }
                placed.Add((new Rect(x, y, size.x, size.y), bag));
                x += size.x + 6f;
                rowHeight = Mathf.Max(rowHeight, size.y);
            }

            var area = GUILayoutUtility.GetRect(width, y + rowHeight, GUILayout.ExpandWidth(false));
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            foreach (var (rect, bag) in placed)
            {
                var r = new Rect(area.x + rect.x, area.y + rect.y, rect.width, rect.height);
                LootDrawing.Bag(r, bag.Kind, bag.Items, lib, Cell, $"kill {bag.Kill + 1:N0}");
            }
        }

        private int RollHash(EnemyItem enemy, LootAssets lib)
        {
            unchecked
            {
                // Reference identity, not an instance id: GetInstanceID is obsolete
                // in this Unity, and only "is this the same asset object" matters here.
                int hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(enemy) * 31 + _kills;
                hash = hash * 31 + _seed;
                LootAssets.EnemyRows(enemy, _hashRows);
                foreach (var row in _hashRows)
                {
                    hash = hash * 31 + row.PoolId;
                    hash = hash * 31 + (int)row.Weight;
                    if (lib.ServerPool(row.PoolId) is { } pool && LootAssets.PoolRows(pool, _hashEntries))
                    {
                        hash = hash * 31 + (int)pool.Bag;
                        foreach (var entry in _hashEntries)
                        {
                            hash = hash * 31 + entry.ItemId;
                            hash = hash * 31 + entry.ChancePercent.GetHashCode();
                            hash = hash * 31 + entry.Count;
                        }
                    }
                }
                return hash;
            }
        }

        // ── Pools ───────────────────────────────────────────────────────────────

        private void PoolsTab(LootAssets lib)
        {
            using var _ = new EditorGUILayout.HorizontalScope();

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(260f)))
            {
                if (GUILayout.Button(new GUIContent("New pool", "Create a pool asset with the next free id, beside the existing pools.")))
                {
                    CreatePool(lib);
                }
                _scrollA = EditorGUILayout.BeginScrollView(_scrollA);
                foreach (var pool in lib.Pools.OrderBy(p => p.Id).ThenBy(p => p.name))
                {
                    var style = new GUIStyle(TierStyle((int)pool.Bag)) { fontStyle = pool == _pool ? FontStyle.Bold : FontStyle.Normal };
                    if (GUILayout.Button($"{pool.Id,4}   {pool.name}", style))
                    {
                        _pool = pool;
                    }
                }
                EditorGUILayout.EndScrollView();
            }

            using (new EditorGUILayout.VerticalScope())
            {
                _scrollB = EditorGUILayout.BeginScrollView(_scrollB);
                _pool = (LootPoolItem?)EditorGUILayout.ObjectField("Pool", _pool, typeof(LootPoolItem), false);
                if (_pool == null)
                {
                    EditorGUILayout.HelpBox("Select a pool from the list, or make a new one.", MessageType.Info);
                    EditorGUILayout.EndScrollView();
                    return;
                }

                var so = Serialized(_pool);
                EditorGUILayout.PropertyField(so.FindProperty("Id"));
                EditorGUILayout.PropertyField(so.FindProperty("Bag"));

                EditorGUILayout.Space(4f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Item", EditorStyles.boldLabel, GUILayout.Width(200f));
                    GUILayout.Label("Chance %", EditorStyles.boldLabel, GUILayout.Width(170f));
                    GUILayout.Label("Count", EditorStyles.boldLabel, GUILayout.Width(50f));
                    GUILayout.Label("Stack / size", EditorStyles.boldLabel);
                }

                var list = so.FindProperty("Items");
                for (int i = 0; i < list.arraySize; i++)
                {
                    var element = list.GetArrayElementAtIndex(i);
                    var itemProp = element.FindPropertyRelative("Item");
                    var chanceProp = element.FindPropertyRelative("ChancePercent");
                    var countProp = element.FindPropertyRelative("Count");
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PropertyField(itemProp, GUIContent.none, GUILayout.Width(200f));
                        chanceProp.floatValue = EditorGUILayout.Slider(chanceProp.floatValue, 0f, 100f, GUILayout.Width(170f));
                        countProp.intValue = Mathf.Clamp(EditorGUILayout.IntField(countProp.intValue, GUILayout.Width(50f)), 1, 99);

                        if (itemProp.objectReferenceValue is EquipmentItem item)
                        {
                            var (w, h) = LootMath.Footprint(lib.Shape(item.CatalogueId));
                            string capped = countProp.intValue > item.MaxStack ? $" → sends {item.MaxStack}" : "";
                            GUILayout.Label($"stacks {item.MaxStack}{capped}  ·  {w}x{h}", EditorStyles.miniLabel);
                        }
                        else
                        {
                            GUILayout.Label("no item: skipped", EditorStyles.miniLabel);
                        }
                        if (GUILayout.Button(new GUIContent("×", "Remove this entry."), GUILayout.Width(20f)))
                        {
                            list.DeleteArrayElementAtIndex(i);
                            break;
                        }
                    }
                }
                if (GUILayout.Button("Add entry", GUILayout.Width(120f)))
                {
                    list.arraySize++;
                    var added = list.GetArrayElementAtIndex(list.arraySize - 1);
                    added.FindPropertyRelative("Item").objectReferenceValue = null;
                    added.FindPropertyRelative("ChancePercent").floatValue = 25f;
                    added.FindPropertyRelative("Count").intValue = 1;
                }
                so.ApplyModifiedProperties();

                var yield = LootOdds.Yield(_pool, lib);
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("When picked", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Bag {LootOdds.Percent(yield.Bag)}  ·  {yield.ExpectedStacks:0.00} stacks on average");

                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Rolled by", EditorStyles.boldLabel);
                Row("Enemy", "Picks this pool", "Bag from it", "", bold: true);
                bool any = false;
                foreach (var e in lib.Enemies)
                {
                    var odds = LootOdds.ForEnemy(e, lib);
                    foreach (var share in odds.Pools.Where(p => p.Pool == _pool))
                    {
                        any = true;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button(e.name, EditorStyles.label, GUILayout.Width(200f)))
                            {
                                _enemy = e;
                                _tab = Tab.Enemy;
                            }
                            GUILayout.Label(LootOdds.Percent(share.Share), GUILayout.Width(90f));
                            GUILayout.Label(LootOdds.Percent(share.Share * share.BagIfChosen), GUILayout.Width(90f));
                        }
                    }
                }
                if (!any)
                {
                    EditorGUILayout.LabelField("No enemy rolls this pool.", EditorStyles.miniLabel);
                }

                LootWarnings.Pool(_pool, lib, yield, _warnings);
                EditorGUILayout.Space(6f);
                foreach (var warning in _warnings)
                {
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void CreatePool(LootAssets lib)
        {
            int next = lib.Pools.Select(p => (int)p.Id).DefaultIfEmpty(0).Max() + 1;
            if (next > ushort.MaxValue)
            {
                EditorUtility.DisplayDialog("New pool", "Every pool id is taken.", "OK");
                return;
            }

            string folder = lib.Pools.Count > 0
                ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(lib.Pools[0]))?.Replace('\\', '/') ?? "Assets"
                : "Assets/Loot Pool";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                folder = "Assets";
            }

            var pool = CreateInstance<LootPoolItem>();
            pool.Id = (ushort)next;
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/Pool_New.asset");
            AssetDatabase.CreateAsset(pool, path);
            AssetDatabase.SaveAssets();
            Undo.RegisterCreatedObjectUndo(pool, "Create Loot Pool");

            LootAssets.Reload();
            _pool = pool;
            EditorGUIUtility.PingObject(pool);
        }

        // ── Items ───────────────────────────────────────────────────────────────

        private void ItemsTab(LootAssets lib)
        {
            // Every enemy's odds once, then read per item.
            var all = lib.Enemies.Select(e => (Enemy: e, Odds: LootOdds.ForEnemy(e, lib))).ToList();

            using var _ = new EditorGUILayout.HorizontalScope();

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(260f)))
            {
                _filter = EditorGUILayout.TextField(_filter);
                _scrollA = EditorGUILayout.BeginScrollView(_scrollA);
                foreach (var group in lib.Items.GroupBy(i => i.Kind).OrderBy(g => g.Key))
                {
                    EditorGUILayout.LabelField(group.Key.ToString(), EditorStyles.boldLabel);
                    foreach (var item in group.OrderBy(i => i.Tier).ThenBy(i => i.name))
                    {
                        if (_filter.Length > 0 && item.name.IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        int sources = all.Count(a => a.Odds.Items.Any(i => i.Item == item));
                        var style = new GUIStyle(TierStyle(item.Tier)) { fontStyle = item == _item ? FontStyle.Bold : FontStyle.Normal };
                        if (GUILayout.Button(sources > 0 ? $"{item.name}   ({sources})" : $"{item.name}   —", style))
                        {
                            _item = item;
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
            }

            using (new EditorGUILayout.VerticalScope())
            {
                _scrollB = EditorGUILayout.BeginScrollView(_scrollB);
                _item = (EquipmentItem?)EditorGUILayout.ObjectField("Item", _item, typeof(EquipmentItem), false);
                if (_item == null)
                {
                    EditorGUILayout.HelpBox("Select an item to see everything that drops it.", MessageType.Info);
                    EditorGUILayout.EndScrollView();
                    return;
                }

                var (w, h) = LootMath.Footprint(lib.Shape(_item.CatalogueId));
                bool fits = w <= LootMath.PackWidth && h <= LootMath.PackHeight;
                EditorGUILayout.LabelField($"{_item.Kind}  ·  tier {_item.Tier}  ·  stacks {_item.MaxStack}  ·  {w}x{h}" +
                                           $"{(fits ? "" : "  ·  too big for the pack")}  ·  catalogue id {_item.CatalogueId}");

                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Dropped by", EditorStyles.boldLabel);
                Row("Enemy", "Per kill", "Per 100 kills", "Kills for 50% / 90%", bold: true);
                var sources = all
                    .Select(a => (a.Enemy, Chance: a.Odds.Items.FirstOrDefault(i => i.Item == _item)))
                    .Where(a => a.Chance != null)
                    .OrderByDescending(a => a.Chance!.PerKill)
                    .ToList();
                foreach (var (enemy, chance) in sources)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(enemy.name, EditorStyles.label, GUILayout.Width(200f)))
                        {
                            _enemy = enemy;
                            _tab = Tab.Enemy;
                        }
                        GUILayout.Label(LootOdds.Percent(chance!.PerKill), GUILayout.Width(90f));
                        GUILayout.Label((chance.ExpectedCount * 100.0).ToString("0.#"), GUILayout.Width(90f));
                        GUILayout.Label($"{LootOdds.Kills(LootOdds.KillsFor(chance.PerKill, 0.5))} / {LootOdds.Kills(LootOdds.KillsFor(chance.PerKill, 0.9))}");
                    }
                }
                if (sources.Count == 0)
                {
                    EditorGUILayout.LabelField("No enemy drops this.", EditorStyles.miniLabel);
                }

                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("In pools", EditorStyles.boldLabel);
                foreach (var pool in lib.Pools)
                {
                    if (pool.Items == null)
                    {
                        continue;
                    }
                    foreach (var roll in pool.Items.Where(r => r?.Item == _item))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button(pool.name, TierStyle((int)pool.Bag), GUILayout.Width(200f)))
                            {
                                _pool = pool;
                                _tab = Tab.Pools;
                            }
                            GUILayout.Label($"{roll.ChancePercent:0.#}%  ×{roll.Count}");
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private SerializedObject Serialized(Object target)
        {
            if (!_serialized.TryGetValue(target, out var so) || so.targetObject == null)
            {
                so = new SerializedObject(target);
                _serialized[target] = so;
            }
            so.Update();
            return so;
        }

        /// <summary>A uint weight, edited without letting it go negative.</summary>
        private static void WeightField(SerializedProperty? property, float width)
        {
            if (property == null)
            {
                return;
            }
            EditorGUI.BeginChangeCheck();
            long value = EditorGUILayout.LongField(property.longValue, GUILayout.Width(width));
            if (EditorGUI.EndChangeCheck())
            {
                property.longValue = System.Math.Clamp(value, 0L, uint.MaxValue);
            }
        }

        private static string Share(long weight, long total) =>
            total > 0 ? LootOdds.Percent(weight / (double)total) + " picked" : "";

        private static void Row(string a, string b, string c, string d, bool bold = false)
        {
            var style = bold ? EditorStyles.boldLabel : EditorStyles.label;
            using var _ = new EditorGUILayout.HorizontalScope();
            GUILayout.Label(a, style, GUILayout.Width(200f));
            GUILayout.Label(b, style, GUILayout.Width(90f));
            GUILayout.Label(c, style, GUILayout.Width(90f));
            GUILayout.Label(d, style);
        }

        private static GUIStyle TierStyle(int tier)
        {
            tier = Mathf.Clamp(tier, 0, LootOdds.BagKinds - 1);
            return _tierStyles[tier] ??= new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = LootDrawing.Tier(tier) },
                hover = { textColor = Color.white },
            };
        }
    }
}
