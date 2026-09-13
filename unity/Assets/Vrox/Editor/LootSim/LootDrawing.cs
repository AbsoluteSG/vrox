using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Bags and the pack, drawn the way the in-game windows draw them.
    /// </summary>
    /// <remarks>
    /// Colours, bag names and the four-across bag layout are copied from
    /// <c>VroxLootWindow</c>, so a simulated bag looks like the one a player opens.
    /// That window's tier colours are serialized on its component and can be
    /// changed in a scene; these are its defaults.
    /// </remarks>
    internal static class LootDrawing
    {
        public static readonly Color[] TierColours =
        {
            new(0.72f, 0.72f, 0.72f),
            new(0.45f, 0.85f, 0.45f),
            new(0.40f, 0.65f, 1.00f),
            new(0.72f, 0.45f, 0.95f),
            new(1.00f, 0.60f, 0.25f),
            new(1.00f, 0.85f, 0.30f),
        };

        private static readonly Color PanelColour = new(0.07f, 0.08f, 0.10f, 0.92f);
        private static readonly Color CellColour = new(0.16f, 0.17f, 0.20f, 0.95f);
        private static readonly Color CellEdge = new(0.30f, 0.32f, 0.38f, 1f);

        public const int PerRow = 4;
        public const float Gap = 4f;
        private const float Header = 24f;
        private const float Pad = 8f;

        private static GUIStyle? _label;
        private static GUIStyle? _count;
        private static GUIStyle? _title;

        public static Color Tier(int tier) => TierColours[Mathf.Clamp(tier, 0, TierColours.Length - 1)];

        public static string BagName(byte kind) => kind switch
        {
            1 => "UNCOMMON",
            2 => "RARE",
            3 => "EPIC",
            4 => "LEGENDARY",
            5 => "BOSS",
            _ => "COMMON",
        };

        public static Vector2 BagSize(int stacks, float cell)
        {
            int rows = Mathf.Max(1, Mathf.CeilToInt(stacks / (float)PerRow));
            return new Vector2(Pad * 2f + PerRow * (cell + Gap) - Gap,
                               Header + rows * (cell + Gap) - Gap + Pad);
        }

        public static void Bag(Rect rect, byte kind, IReadOnlyList<BagStack> stacks, LootAssets lib,
                               float cell, string caption)
        {
            EnsureStyles();
            EditorGUI.DrawRect(rect, PanelColour);
            Outline(rect, Tier(kind));
            GUI.Label(new Rect(rect.x + Pad, rect.y + 3f, rect.width - Pad * 2f, 18f),
                      $"BAG — {BagName(kind)}  {caption}", _title);

            for (int i = 0; i < stacks.Count; i++)
            {
                var slot = new Rect(rect.x + Pad + (i % PerRow) * (cell + Gap),
                                    rect.y + Header + (i / PerRow) * (cell + Gap), cell, cell);
                EditorGUI.DrawRect(slot, CellColour);
                Outline(slot, CellEdge);
                Item(slot, stacks[i].ItemId, stacks[i].Count, lib.Item(stacks[i].ItemId));
            }
        }

        /// <summary>The 5x3 pack, each stack drawn across its footprint.</summary>
        public static void Pack(Rect rect, List<PackStack> pack, LootAssets lib, float cell)
        {
            EnsureStyles();
            EditorGUI.DrawRect(rect, PanelColour);
            for (int y = 0; y < LootMath.PackHeight; y++)
            {
                for (int x = 0; x < LootMath.PackWidth; x++)
                {
                    var slot = new Rect(rect.x + Pad + x * (cell + Gap), rect.y + Pad + y * (cell + Gap), cell, cell);
                    EditorGUI.DrawRect(slot, CellColour);
                    Outline(slot, CellEdge);
                }
            }
            foreach (var stack in pack)
            {
                var (w, h) = LootMath.Footprint(lib.Shape(stack.ItemId));
                var area = new Rect(rect.x + Pad + stack.X * (cell + Gap), rect.y + Pad + stack.Y * (cell + Gap),
                                    w * (cell + Gap) - Gap, h * (cell + Gap) - Gap);
                Item(area, stack.ItemId, stack.Count, lib.Item(stack.ItemId));
            }
        }

        public static Vector2 PackSize(float cell) => new(
            Pad * 2f + LootMath.PackWidth * (cell + Gap) - Gap,
            Pad * 2f + LootMath.PackHeight * (cell + Gap) - Gap);

        /// <summary>One item tile: tier colour, icon or name, and count. As <c>VroxLootWindow.DrawItem</c>.</summary>
        public static void Item(Rect rect, ushort itemId, ushort count, EquipmentItem? def)
        {
            EnsureStyles();
            var tier = def != null ? Tier(def.Tier) : Color.grey;
            EditorGUI.DrawRect(rect, new Color(tier.r, tier.g, tier.b, 0.22f));
            Outline(rect, tier);

            if (def != null && def.Icon != null && def.Icon.texture != null)
            {
                var icon = def.Icon;
                var tr = icon.textureRect;
                var uv = new Rect(tr.x / icon.texture.width, tr.y / icon.texture.height,
                                  tr.width / icon.texture.width, tr.height / icon.texture.height);
                GUI.DrawTextureWithTexCoords(Inset(rect, 4f), icon.texture, uv, true);
            }
            else
            {
                GUI.Label(Inset(rect, 3f), def != null ? def.DisplayName : $"#{itemId}", _label);
            }

            if (count > 1)
            {
                GUI.Label(Inset(rect, 2f), count.ToString(), _count);
            }
        }

        public static void Outline(Rect r, Color colour)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), colour);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), colour);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1f, r.height), colour);
            EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), colour);
        }

        private static Rect Inset(Rect r, float by) => new(r.x + by, r.y + by, r.width - by * 2f, r.height - by * 2f);

        private static void EnsureStyles()
        {
            _label ??= new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };
            _count ??= new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.LowerRight,
                normal = { textColor = Color.white },
            };
            _title ??= new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = new Color(0.92f, 0.92f, 0.92f) } };
        }
    }
}
