using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Packs every PNG in the enemy art folder onto one sheet, and re-points the
    /// enemies that use them.
    /// </summary>
    /// <remarks>
    /// <see cref="EnemyCatalogue"/> demands that every enemy sprite share one
    /// texture, because <see cref="VroxEnemies"/> draws them all in a single mesh.
    /// Art arrives as one PNG per enemy, so this is the step between the two.
    ///
    /// Authoring stays natural: drag the source PNG onto an enemy's Sprite field,
    /// run this, and the field is swapped to the same-named sprite on the sheet.
    /// Matching is by file name, so renaming a source PNG orphans the enemy that
    /// used it — reported, not guessed at.
    ///
    /// Each image is trimmed to its visible pixels before packing. The renderer
    /// sizes a sprite by its rect height, so a photo that is two-thirds empty
    /// padding would draw its subject at a third of the hitbox.
    /// </remarks>
    public static class BuildEnemySheet
    {
        private const string SourceFolder = "Assets/Art/Enemy Sprites";

        /// <summary>Outside the source folder, or the next build would pack the sheet into itself.</summary>
        private const string SheetPath = "Assets/Art/EnemySheet.png";

        /// <summary>Longest side after trimming. An enemy is about a tile on screen.</summary>
        private const int MaxSide = 256;

        private const int Padding = 4;

        /// <summary>Alpha at or below this counts as empty when trimming.</summary>
        /// <remarks>Background removal leaves faint noise that would otherwise defeat the trim.</remarks>
        private const byte AlphaCutoff = 8;

        [MenuItem("Vrox/Build Enemy Sheet")]
        public static void Build()
        {
            var paths = Directory.GetFiles(SourceFolder, "*.png")
                .Select(p => p.Replace('\\', '/'))
                .OrderBy(p => p)
                .ToList();
            if (paths.Count == 0)
            {
                Debug.LogError($"Vrox: no PNGs in {SourceFolder}, so there is nothing to pack.");
                return;
            }

            var names = new List<string>();
            var pieces = new List<Texture2D>();
            foreach (var path in paths)
            {
                var raw = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                raw.LoadImage(File.ReadAllBytes(path));
                var trimmed = Trim(raw);
                Object.DestroyImmediate(raw);
                if (trimmed == null)
                {
                    Debug.LogWarning($"Vrox: {path} is fully transparent and was left off the sheet.");
                    continue;
                }
                names.Add(Path.GetFileNameWithoutExtension(path));
                pieces.Add(Shrink(trimmed));
            }

            var sheet = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var uvs = sheet.PackTextures(pieces.ToArray(), Padding, 4096);
            File.WriteAllBytes(SheetPath, sheet.EncodeToPNG());
            int sheetWidth = sheet.width;
            int sheetHeight = sheet.height;
            Object.DestroyImmediate(sheet);
            foreach (var piece in pieces)
            {
                Object.DestroyImmediate(piece);
            }

            AssetDatabase.ImportAsset(SheetPath);
            if (AssetImporter.GetAtPath(SheetPath) is not TextureImporter importer)
            {
                Debug.LogError($"Vrox: {SheetPath} did not import as a texture.");
                return;
            }
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.spritePixelsPerUnit = 100f;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = 4096;
            importer.SaveAndReimport();

            var factory = new SpriteDataProviderFactories();
            factory.Init();
            var provider = factory.GetSpriteEditorDataProviderFromObject(importer);
            provider.InitSpriteEditorDataProvider();

            // Reusing each name's previous id keeps its file id stable across
            // rebuilds, so references to it survive even without the re-point below.
            var previous = provider.GetSpriteRects()
                .GroupBy(r => r.name)
                .ToDictionary(g => g.Key, g => g.First().spriteID);

            var rects = new SpriteRect[names.Count];
            for (int i = 0; i < names.Count; i++)
            {
                var uv = uvs[i];
                rects[i] = new SpriteRect
                {
                    name = names[i],
                    rect = new Rect(
                        Mathf.Round(uv.x * sheetWidth), Mathf.Round(uv.y * sheetHeight),
                        Mathf.Round(uv.width * sheetWidth), Mathf.Round(uv.height * sheetHeight)),
                    alignment = SpriteAlignment.Center,
                    pivot = new Vector2(0.5f, 0.5f),
                    spriteID = previous.TryGetValue(names[i], out var id) ? id : GUID.Generate(),
                };
            }
            provider.SetSpriteRects(rects);
            provider.Apply();
            importer.SaveAndReimport();

            int repointed = Repoint();
            Debug.Log($"Vrox: packed {names.Count} enemy sprite(s) into {SheetPath} "
                    + $"({sheetWidth}x{sheetHeight}), re-pointed {repointed} enemy asset(s).");
        }

        /// <summary>Moves every enemy using a source PNG, or an older sheet sprite, onto the new sheet.</summary>
        private static int Repoint()
        {
            var onSheet = AssetDatabase.LoadAllAssetsAtPath(SheetPath)
                .OfType<Sprite>()
                .ToDictionary(s => s.name);

            int count = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:EnemyItem"))
            {
                var enemy = AssetDatabase.LoadAssetAtPath<EnemyItem>(AssetDatabase.GUIDToAssetPath(guid));
                if (enemy == null || enemy.Sprite == null)
                {
                    continue;
                }
                var from = AssetDatabase.GetAssetPath(enemy.Sprite);
                if (!from.StartsWith(SourceFolder + "/") && from != SheetPath)
                {
                    // Art from somewhere else entirely. Left alone; the catalogue
                    // reports the mismatched texture at runtime.
                    continue;
                }
                if (!onSheet.TryGetValue(enemy.Sprite.name, out var packed))
                {
                    Debug.LogError($"Vrox: \"{enemy.name}\" uses sprite \"{enemy.Sprite.name}\", "
                                 + $"which is no longer in {SourceFolder}.", enemy);
                    continue;
                }
                if (enemy.Sprite != packed)
                {
                    enemy.Sprite = packed;
                    EditorUtility.SetDirty(enemy);
                    count++;
                }
            }

            // The catalogue caches which texture its sprites share, and a sprite
            // changed underneath it does not trigger its OnValidate.
            foreach (var guid in AssetDatabase.FindAssets("t:EnemyCatalogue"))
            {
                AssetDatabase.LoadAssetAtPath<EnemyCatalogue>(AssetDatabase.GUIDToAssetPath(guid))
                    ?.Invalidate();
            }

            AssetDatabase.SaveAssets();
            return count;
        }

        /// <summary>A copy cropped to the visible pixels, or null if there are none.</summary>
        private static Texture2D? Trim(Texture2D source)
        {
            var pixels = source.GetPixels32();
            int w = source.width, h = source.height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (pixels[y * w + x].a <= AlphaCutoff)
                    {
                        continue;
                    }
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0)
            {
                return null;
            }

            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            var cropped = new Texture2D(cw, ch, TextureFormat.RGBA32, false);
            cropped.SetPixels(source.GetPixels(minX, minY, cw, ch));
            cropped.Apply();
            return cropped;
        }

        /// <summary>Scales down to <see cref="MaxSide"/>, halving in steps so detail is averaged rather than skipped.</summary>
        /// <remarks>Consumes <paramref name="source"/> when it has to scale.</remarks>
        private static Texture2D Shrink(Texture2D source)
        {
            var current = source;
            while (Mathf.Max(current.width, current.height) > MaxSide)
            {
                float step = Mathf.Max(0.5f, (float)MaxSide / Mathf.Max(current.width, current.height));
                int nw = Mathf.Max(1, Mathf.RoundToInt(current.width * step));
                int nh = Mathf.Max(1, Mathf.RoundToInt(current.height * step));

                var rt = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                current.filterMode = FilterMode.Bilinear;
                Graphics.Blit(current, rt);

                var previousActive = RenderTexture.active;
                RenderTexture.active = rt;
                var next = new Texture2D(nw, nh, TextureFormat.RGBA32, false);
                next.ReadPixels(new Rect(0, 0, nw, nh), 0, 0);
                next.Apply();
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);

                Object.DestroyImmediate(current);
                current = next;
            }
            return current;
        }
    }
}
