using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Vrox.Editor
{
    /// <summary>Builds the whole scene: a connection, a player, a camera.</summary>
    public static class SceneSetup
    {
        [MenuItem("Vrox/Create Scene")]
        public static void CreateScene()
        {
            // Tops up an existing scene rather than refusing to touch it. A scene
            // built by hand is missing whichever component was forgotten, and the
            // symptom is silent: nothing errors, that one thing simply never
            // draws. Running this again is how you find out.
            var net = Object.FindAnyObjectByType<VroxNet>() is { } existing
                ? existing.gameObject
                : new GameObject("Vrox");

            var added = new System.Collections.Generic.List<string>();
            Ensure<VroxNet>(net, added);
            Ensure<VroxShots>(net, added);
            Ensure<VroxTerrain>(net, added);
            Ensure<VroxEnemies>(net, added);
            Ensure<VroxDummies>(net, added);
            Ensure<VroxLoot>(net, added);
            Ensure<VroxTracers>(net, added);
            Ensure<VroxMuzzleFlash>(net, added);
            Ensure<VroxCursor>(net, added);
            Ensure<VroxSpawnerGizmos>(net, added);
            Ensure<VroxPortals>(net, added);

            // A generated scene is a test scene, so it drops straight in.
            Ensure<VroxGuestCharacter>(net, added);
            Ensure<DamageNumbers>(net, added);

            WireTextures(net, added);

            var player = Object.FindAnyObjectByType<VroxPlayer>() is { } known
                ? known.gameObject
                : new GameObject("Player");
            Ensure<VroxPlayer>(player, added);
            Ensure<VroxShooter>(player, added);
            Ensure<VroxPickup>(player, added);
            Ensure<VroxAimLine>(player, added);

            // The camera is parented to the player, so turning it leaves the
            // player's sprite standing still in the world and spinning on screen.
            Ensure<VroxBillboard>(player, added);
            if (player.GetComponent<SpriteRenderer>() == null)
            {
                var renderer = player.AddComponent<SpriteRenderer>();
                renderer.sprite = Quad();
                renderer.color = new Color(0.2f, 0.95f, 0.95f);
                player.transform.position = new Vector3(64f, 64f, 0f);
                added.Add(nameof(SpriteRenderer));
            }

            if (Camera.main == null)
            {
                var cam = new GameObject("Main Camera") { tag = "MainCamera" };
                var camera = cam.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = 10f;
                camera.backgroundColor = new Color(0.07f, 0.07f, 0.10f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                // Parented to the player, so the camera follows with no script
                // and nothing to get wrong. It stops being enough the moment the
                // camera needs to lead or lag, which is not today.
                cam.transform.SetParent(player.transform, false);
                cam.transform.localPosition = new Vector3(0f, 0f, -10f);
                cam.AddComponent<VroxCamera>();
            }

            Selection.activeObject = player;
            EditorSceneManager.MarkSceneDirty(player.scene);

            if (added.Count == 0)
            {
                Debug.Log("Vrox: this scene already has everything.", net);
                return;
            }

            Debug.Log($"Vrox: added {string.Join(", ", added)}. Start the server and publish, "
                    + "then press Play. WASD moves, Q/E rotate the view, the mouse aims, "
                    + "I toggles auto-fire.", player);
        }

        /// <summary>Adds a component if the object has not got one, recording what it added.</summary>
        private static void Ensure<T>(GameObject target, System.Collections.Generic.List<string> added)
            where T : Component
        {
            if (target.GetComponent<T>() != null)
            {
                return;
            }
            Undo.AddComponent<T>(target);
            added.Add(typeof(T).Name);
        }

        /// <summary>A 1x1 white sprite, saved so the placeholder survives a reload.</summary>

        /// <summary>
        /// Hands the shot renderers their textures, if nobody has yet.
        /// </summary>
        /// <remarks>
        /// Both components generate their own texture when the field is empty, so
        /// this is not what makes them work — it is what makes them look like the
        /// game rather than like a placeholder. Assigned from the asset path
        /// rather than left to be dragged in, because a field somebody has to
        /// remember to fill is a field that ships empty.
        ///
        /// Only ever fills a blank. Overwriting would undo a deliberate choice
        /// every time somebody ran this menu to top up an unrelated component,
        /// which is exactly the kind of silent stomp this whole file exists to
        /// avoid.
        /// </remarks>
        private static void WireTextures(GameObject net, System.Collections.Generic.List<string> added)
        {
            const string glow = "Assets/Vrox/Textures/ShotGlow.png";
            const string flare = "Assets/Vrox/Textures/MuzzleFlare.png";

            if (net.GetComponent<VroxShots>() is { } shots && Blank(shots.DotTexture)
                && AssetDatabase.LoadAssetAtPath<Texture2D>(glow) is { } glowTexture)
            {
                shots.DotTexture = glowTexture;
                EditorUtility.SetDirty(shots);
                added.Add("VroxShots.DotTexture");
            }

            if (net.GetComponent<VroxMuzzleFlash>() is { } flash && Blank(flash.FlareTexture)
                && AssetDatabase.LoadAssetAtPath<Texture2D>(flare) is { } flareTexture)
            {
                flash.FlareTexture = flareTexture;
                EditorUtility.SetDirty(flash);
                added.Add("VroxMuzzleFlash.FlareTexture");
            }
        }

        /// <summary>
        /// Whether a texture field is unset, or set to something that cannot have
        /// been meant.
        /// </summary>
        /// <remarks>
        /// Empty is the obvious case. The other one is a texture out of Unity's
        /// built-in resources, which is what an empty field tends to become: the
        /// picker offers the built-ins alongside the project's assets, and one of
        /// them gets chosen because the field has to say *something*. That is
        /// never a deliberate answer here — every texture these components want
        /// lives in Assets/Vrox/Textures — so it is treated as blank and filled
        /// in, where a real project asset is left alone.
        /// </remarks>
        private static bool Blank(Texture2D? texture) =>
            texture == null || !AssetDatabase.Contains(texture);

        private static Sprite Quad()
        {
            const string path = "Assets/Vrox/Quad.png";
            if (AssetDatabase.LoadAssetAtPath<Sprite>(path) is { } cached)
            {
                return cached;
            }

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);

            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spritePixelsPerUnit = 1f;
                importer.filterMode = FilterMode.Point;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }
    }
}
