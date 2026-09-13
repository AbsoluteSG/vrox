using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;
using View = Vrox.Editor.PreviewCanvas.View;

namespace Vrox.Editor
{
    /// <summary>
    /// Draws a weapon's firing pattern into a rectangle, as the server would fire it.
    /// </summary>
    /// <remarks>
    /// Shared by the inspector preview pane and the designer window.
    ///
    /// Everything positional goes through <see cref="VolleyMath"/>, the same code
    /// the game's bullet drawing uses, so the preview cannot drift from the game.
    /// Values are clamped to the bounds <c>UpsertWeapon</c> enforces rather than
    /// the inspector's, because the server's are the ones that fire — a Cluster
    /// asking for 128 shots is drawn as the 32 it really gets, with a warning.
    ///
    /// What it does not reproduce, deliberately:
    /// - Absolute spin phase. The server spins by seconds since the Unix epoch, so
    ///   a real volley starts at an arbitrary angle; the rotation between volleys
    ///   is what this shows.
    /// - Dexterity. A player's fire rate is divided by it; this draws the weapon's.
    /// - Exact bullet compositing. The glow and core are an approximation of
    ///   <c>VroxShots</c>, not its mesh.
    /// </remarks>
    internal static class WeaponPreviewRenderer
    {
        /// <summary><c>UpsertWeapon</c> clamps Shots to this.</summary>
        private const int ServerMaxShots = 32;

        /// <summary>
        /// The default of <c>VroxShots.SpriteScale</c>. A scene that overrides it
        /// draws art at a different size than this preview.
        /// </summary>
        private const float GameSpriteScale = 2.2f;

        /// <summary>Defaults of <c>VroxShots.TrailSeconds</c> and <c>TrailSegments</c>.</summary>
        private const float TrailSeconds = 0.13f;
        private const int TrailSegments = 7;

        private const int PathSamples = 24;
        private const float RayFlashSeconds = 0.12f;

        /// <summary>Above this many live bullets, trails are skipped to keep the editor responsive.</summary>
        private const int TrailBudget = 2500;

        private struct Bullet
        {
            public float Speed;
            public float Size;
            public float Life;
            public Color Colour;
            public int SpriteId;
            public int Variant;
        }

        private static readonly List<VolleySlot> Slots = new();
        private static readonly List<BulletVariant> Mix = new();
        private static readonly List<Bullet> Bullets = new();
        private static readonly List<string> WarningList = new();
        private static readonly Vector3[] Line = new Vector3[PathSamples + 1];
        private static readonly StringBuilder Text = new();

        private static BulletSpriteCatalogue? _catalogue;
        private static bool _searched;
        private static GUIStyle? _header;
        private static GUIStyle? _sub;
        private static GUIStyle? _warning;
        private static GUIStyle? _slot;

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.projectChanged -= ForgetCatalogue;
            EditorApplication.projectChanged += ForgetCatalogue;
        }

        private static void ForgetCatalogue()
        {
            _catalogue = null;
            _searched = false;
        }

        /// <summary>
        /// The first bullet sprite catalogue in the project, if it can be drawn from.
        /// </summary>
        /// <remarks>
        /// The game uses whichever catalogue is assigned on <c>VroxShots</c>. With
        /// one catalogue in the project those are the same; with two, this may show
        /// the other one's art.
        /// </remarks>
        private static BulletSpriteCatalogue? Catalogue
        {
            get
            {
                if (_catalogue == null && !_searched)
                {
                    _searched = true;
                    foreach (var guid in AssetDatabase.FindAssets("t:BulletSpriteCatalogue"))
                    {
                        _catalogue = AssetDatabase.LoadAssetAtPath<BulletSpriteCatalogue>(
                            AssetDatabase.GUIDToAssetPath(guid));
                        if (_catalogue != null)
                        {
                            break;
                        }
                    }
                }
                return _catalogue != null && _catalogue.Usable ? _catalogue : null;
            }
        }

        public static bool IsHitscan(WeaponItem w, PreviewState s) => w.Range > 0f && !s.AsEnemy;

        /// <summary>Half-extent in tiles that fits everything the weapon can reach.</summary>
        public static float FitTiles(WeaponItem w, PreviewState s)
        {
            Prepare(w);
            return Fit(w, IsHitscan(w, s));
        }

        public static void Draw(Rect rect, WeaponItem w, PreviewState s)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            byte shots = Prepare(w);
            bool hitscan = IsHitscan(w, s);
            float half = s.ZoomTiles > 0f ? s.ZoomTiles : Fit(w, hitscan);

            EditorGUI.DrawRect(rect, PreviewCanvas.Background);
            GUI.BeginClip(rect);
            var view = new View(rect.size, half);

            PreviewCanvas.DrawGrid(view);
            DrawReach(view, w, hitscan);
            DrawAim(view, s.Aim);

            if (hitscan)
            {
                DrawRays(view, w, s);
            }
            else if (s.Mode == PreviewState.ViewMode.Volley)
            {
                DrawVolley(view, w, s);
            }
            else
            {
                DrawStream(view, w, s);
            }

            GUI.EndClip();
            DrawText(rect, w, s, shots, hitscan);
        }

        /// <summary>Lays out one volley and resolves each slot's bullet, as the server will.</summary>
        private static byte Prepare(WeaponItem w)
        {
            var pattern = w.Pattern;
            byte shots = (byte)Mathf.Clamp(w.Shots, 1, ServerMaxShots);
            VolleyMath.Place(pattern?.Kind ?? PatternKind.Single, shots,
                             Mathf.Clamp(pattern?.SpreadDegrees ?? 0f, 0f, 360f),
                             w.PatternGroups, Slots);

            // Null entries are dropped by the push, so they are dropped here too;
            // otherwise the slot-to-variant mapping would differ from the server's.
            Mix.Clear();
            if (w.Mix != null)
            {
                foreach (var v in w.Mix)
                {
                    if (v != null)
                    {
                        Mix.Add(v);
                    }
                }
            }

            Bullets.Clear();
            int weaponSprite = Mathf.Clamp(w.BulletSpriteId, 0, 255);
            for (int slot = 0; slot < Slots.Count; slot++)
            {
                if (Mix.Count == 0)
                {
                    Bullets.Add(new Bullet
                    {
                        Speed = Mathf.Clamp(w.ProjectileSpeed, 1f, 60f),
                        Size = Mathf.Clamp(w.ProjectileSize, 0.05f, 2f),
                        Life = Mathf.Clamp(w.ProjectileLifetimeMs, 100, 5000) / 1000f,
                        Colour = Opaque(w.Tint),
                        SpriteId = weaponSprite,
                        Variant = -1,
                    });
                    continue;
                }

                int index = VolleyMath.VariantIndex(w.Assignment, slot, shots, Mix.Count,
                                                    w.PatternGroups);
                var v = Mix[index];
                int own = Mathf.Clamp(v.BulletSpriteId, 0, 255);
                Bullets.Add(new Bullet
                {
                    Speed = Mathf.Clamp(v.Speed, 1f, 60f),
                    Size = Mathf.Clamp(v.Size, 0.05f, 2f),
                    Life = Mathf.Clamp(v.LifetimeMs, 100, 5000) / 1000f,
                    Colour = Opaque(v.Tint),
                    SpriteId = own != 0 ? own : weaponSprite,
                    Variant = index,
                });
            }
            return shots;
        }

        private static float Fit(WeaponItem w, bool hitscan)
        {
            if (hitscan)
            {
                return Mathf.Max(2f, Mathf.Clamp(w.Range, 0f, 60f) * 1.1f);
            }
            float amp = Mathf.Clamp(w.WaveAmplitude, 0f, 5f);
            float reach = 0f;
            for (int i = 0; i < Slots.Count; i++)
            {
                reach = Mathf.Max(reach,
                    Bullets[i].Speed * Bullets[i].Life + Mathf.Abs(Slots[i].Lateral) + amp);
            }
            return Mathf.Max(2f, reach * 1.1f);
        }

        private static float MaxLife()
        {
            float life = 0.1f;
            foreach (var b in Bullets)
            {
                life = Mathf.Max(life, b.Life);
            }
            return life;
        }

        private static void DrawReach(View view, WeaponItem w, bool hitscan)
        {
            float reach = 0f;
            if (hitscan)
            {
                reach = Mathf.Clamp(w.Range, 0f, 60f);
            }
            else
            {
                foreach (var b in Bullets)
                {
                    reach = Mathf.Max(reach, b.Speed * b.Life);
                }
            }
            Handles.color = new Color(1f, 1f, 1f, 0.1f);
            Handles.DrawWireDisc(view.Centre, Vector3.forward, reach * view.Ppt);
        }

        private static void DrawAim(View view, Vector2 aim)
        {
            var from = (Vector3)view.Centre;
            var to = from + new Vector3(aim.x, -aim.y) * Mathf.Min(view.Size.x, view.Size.y) * 0.1f;
            Handles.color = new Color(1f, 1f, 1f, 0.35f);
            Handles.DrawAAPolyLine(2f, from, to);
            Handles.DrawSolidDisc(from, Vector3.forward, 4f);
        }

        /// <summary>Volleys fired on the fire rate, each spun by the time it was fired.</summary>
        private static void DrawStream(View view, WeaponItem w, PreviewState s)
        {
            float period = Mathf.Clamp(w.FireRateMs, 50, 5000) / 1000f;
            float spin = Mathf.Clamp(w.SpinDegreesPerSec, -720f, 720f);
            float amp = Mathf.Clamp(w.WaveAmplitude, 0f, 5f);
            float freq = Mathf.Clamp(w.WaveFrequency, 0f, 20f);
            double now = s.Time;

            long first = (long)System.Math.Max(0, System.Math.Floor((now - MaxLife()) / period));
            long last = (long)System.Math.Floor(now / period);
            bool trails = s.Trails && (last - first + 1) * Slots.Count <= TrailBudget;

            for (long k = first; k <= last; k++)
            {
                double t0 = k * period;
                float spinDeg = VolleyMath.SpinDegrees(t0, spin);
                float age = (float)(now - t0);
                for (int i = 0; i < Slots.Count; i++)
                {
                    var b = Bullets[i];
                    if (age > b.Life)
                    {
                        continue;
                    }
                    var slot = Slots[i];
                    var dir = VolleyMath.Direction(s.Aim, slot.AngleDeg, spinDeg);
                    var origin = VolleyMath.Origin(Vector2.zero, dir, slot.Lateral);
                    DrawBullet(view, b, origin, dir, amp, freq, slot.Phase, age, trails);
                }
            }
        }

        /// <summary>One unspun volley with each projectile's whole path, the heads looping along it.</summary>
        private static void DrawVolley(View view, WeaponItem w, PreviewState s)
        {
            float amp = Mathf.Clamp(w.WaveAmplitude, 0f, 5f);
            float freq = Mathf.Clamp(w.WaveFrequency, 0f, 20f);
            float age = (float)(s.Time % MaxLife());

            for (int i = 0; i < Slots.Count; i++)
            {
                var b = Bullets[i];
                var slot = Slots[i];
                var dir = VolleyMath.Direction(s.Aim, slot.AngleDeg, 0f);
                var origin = VolleyMath.Origin(Vector2.zero, dir, slot.Lateral);

                for (int j = 0; j <= PathSamples; j++)
                {
                    Line[j] = view.ToScreen(VolleyMath.PositionAt(origin, dir, b.Speed, amp, freq,
                                                                  slot.Phase, b.Life * j / PathSamples));
                }
                Handles.color = new Color(b.Colour.r, b.Colour.g, b.Colour.b, 0.3f);
                Handles.DrawAAPolyLine(1.5f, PathSamples + 1, Line);

                if (age <= b.Life)
                {
                    DrawBullet(view, b, origin, dir, amp, freq, slot.Phase, age, s.Trails);
                }

                if (s.SlotNumbers)
                {
                    var at = view.ToScreen(VolleyMath.PositionAt(origin, dir, b.Speed, amp, freq,
                                                                 slot.Phase, b.Life * 0.2f));
                    DrawSlotNumber(at, i, b.Variant);
                }
            }
        }

        /// <summary>
        /// Hitscan rays, flashing on each trigger pull.
        /// </summary>
        /// <remarks>
        /// Mirrors <c>FireHitscan</c>: rays use each slot's angle only, from the
        /// shooter — no spin, no lateral offset, no wave, and the flat damage
        /// fields rather than the mix.
        /// </remarks>
        private static void DrawRays(View view, WeaponItem w, PreviewState s)
        {
            float period = Mathf.Clamp(w.FireRateMs, 50, 5000) / 1000f;
            float range = Mathf.Clamp(w.Range, 0f, 60f);
            float since = (float)(s.Time % period);
            float flash = 1f - Mathf.Clamp01(since / Mathf.Min(RayFlashSeconds, period));
            var colour = Opaque(w.Tint);

            for (int i = 0; i < Slots.Count; i++)
            {
                var dir = VolleyMath.Direction(s.Aim, Slots[i].AngleDeg, 0f);
                Line[0] = view.Centre;
                Line[1] = view.ToScreen(dir * range);
                Handles.color = new Color(colour.r, colour.g, colour.b, Mathf.Lerp(0.2f, 1f, flash));
                Handles.DrawAAPolyLine(Mathf.Lerp(1.5f, 3f, flash), 2, Line);

                if (s.SlotNumbers)
                {
                    DrawSlotNumber(view.ToScreen(dir * range * 0.35f), i, -1);
                }
            }
        }

        /// <summary>
        /// One volley fired from a point in the world, <paramref name="age"/> seconds ago.
        /// </summary>
        /// <remarks>
        /// For the enemy designer, which records where and when an enemy fired and
        /// draws the bullets from that. Always projectiles: enemies never fire
        /// hitscan. Spin is sampled at <paramref name="firedAt"/>, once per volley,
        /// as <c>FireVolley</c> does.
        /// </remarks>
        internal static void DrawVolleyAt(View view, WeaponItem w, Vector2 origin, Vector2 aim,
                                          double firedAt, float age, bool trails)
        {
            Prepare(w);
            float spinDeg = VolleyMath.SpinDegrees(firedAt, Mathf.Clamp(w.SpinDegreesPerSec, -720f, 720f));
            float amp = Mathf.Clamp(w.WaveAmplitude, 0f, 5f);
            float freq = Mathf.Clamp(w.WaveFrequency, 0f, 20f);

            for (int i = 0; i < Slots.Count; i++)
            {
                var b = Bullets[i];
                if (age > b.Life)
                {
                    continue;
                }
                var slot = Slots[i];
                var dir = VolleyMath.Direction(aim, slot.AngleDeg, spinDeg);
                var from = VolleyMath.Origin(origin, dir, slot.Lateral);
                DrawBullet(view, b, from, dir, amp, freq, slot.Phase, age, trails);
            }
        }

        /// <summary>The longest-lived bullet this weapon fires, in seconds, after server clamps.</summary>
        internal static float MaxLife(WeaponItem w)
        {
            Prepare(w);
            return MaxLife();
        }

        private static void DrawBullet(View view, Bullet b, Vector2 origin, Vector2 dir,
                                       float amp, float freq, float phase, float age, bool trail)
        {
            var head = view.ToScreen(VolleyMath.PositionAt(origin, dir, b.Speed, amp, freq, phase, age));
            float radius = Mathf.Max(1.5f, b.Size * 0.5f * view.Ppt);
            if (!view.Visible(head, radius * GameSpriteScale * 2f))
            {
                return;
            }

            // Art is drawn alone, pointed along the shot's heading, as VroxShots does:
            // no streak or glow on a sprite that already has its own.
            var catalogue = b.SpriteId != 0 ? Catalogue : null;
            var art = catalogue?.For(b.SpriteId);
            if (catalogue != null && art != null)
            {
                DrawSprite(head, dir, b.Size * 0.5f * view.Ppt, art, catalogue.Sheet!);
                return;
            }

            if (trail)
            {
                float span = Mathf.Min(TrailSeconds, age);
                if (span > 0.0001f)
                {
                    for (int j = 0; j <= TrailSegments; j++)
                    {
                        Line[j] = view.ToScreen(VolleyMath.PositionAt(origin, dir, b.Speed, amp, freq,
                                                                      phase, age - span * j / TrailSegments));
                    }
                    Handles.color = new Color(b.Colour.r, b.Colour.g, b.Colour.b, 0.45f);
                    Handles.DrawAAPolyLine(radius * 1.2f, TrailSegments + 1, Line);
                }
            }

            Handles.color = new Color(b.Colour.r, b.Colour.g, b.Colour.b, 0.3f);
            Handles.DrawSolidDisc(head, Vector3.forward, radius * 1.8f);
            Handles.color = Color.Lerp(b.Colour, Color.white, 0.45f);
            Handles.DrawSolidDisc(head, Vector3.forward, radius);
        }

        private static void DrawSprite(Vector3 head, Vector2 dir, float halfPixels, BulletSprite art,
                                       Texture2D sheet)
        {
            float halfWidth = Mathf.Max(2f, halfPixels * GameSpriteScale);
            float halfLength = halfWidth * art.Aspect;
            var rect = new Rect(head.x - halfLength, head.y - halfWidth, halfLength * 2f, halfWidth * 2f);

            var saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg, head);
            GUI.DrawTextureWithTexCoords(rect, sheet, art.UvRect, true);
            GUI.matrix = saved;
        }

        private static void DrawSlotNumber(Vector3 at, int slot, int variant)
        {
            string label = variant < 0 ? slot.ToString() : $"{slot}:{Letter(variant)}";
            GUI.Label(new Rect(at.x + 4f, at.y - 8f, 40f, 16f), label, _slot);
        }

        private static void DrawText(Rect rect, WeaponItem w, PreviewState s, byte shots, bool hitscan)
        {
            EnsureStyles();
            var kind = w.Pattern?.Kind ?? PatternKind.Single;
            float x = rect.x + 6f;
            float width = rect.width - 12f;
            float y = rect.y + 4f;

            Text.Clear();
            Text.Append(kind).Append("  ·  ").Append(shots).Append(shots == 1 ? " shot" : " shots");
            if (kind == PatternKind.Cluster)
            {
                Text.Append(" in ").Append(Mathf.Clamp(w.PatternGroups, 1, shots)).Append(" fans");
            }
            Text.Append("  ·  ").Append((1000f / Mathf.Clamp(w.FireRateMs, 50, 5000)).ToString("0.#"))
                .Append(" volleys/s  ·  ≈").Append(w.DamagePerSecond.ToString("0")).Append(" DPS");
            GUI.Label(new Rect(x, y, width, 16f), Text.ToString(), _header);
            y += 16f;

            Text.Clear();
            if (hitscan)
            {
                Text.Append("Hitscan  ·  ").Append(Mathf.Clamp(w.Range, 0f, 60f).ToString("0.#"))
                    .Append(" tiles  ·  pierce ").Append(w.Pierce)
                    .Append("  ·  falloff to ").Append(w.FalloffPercent).Append("%  ·  ")
                    .Append(w.SplitDamage ? "damage split across rays" : "full damage per ray");
            }
            else
            {
                Text.Append(s.Mode).Append("  ·  t ").Append(s.Time.ToString("0.00")).Append("s");
                Text.Append(s.AsEnemy ? "  ·  as an enemy fires it" : "  ·  player rate before Dexterity");
            }
            GUI.Label(new Rect(x, y, width, 16f), Text.ToString(), _sub);
            y += 20f;

            foreach (var warning in Warnings(w, s, hitscan))
            {
                var content = new GUIContent("⚠ " + warning);
                float h = _warning!.CalcHeight(content, width);
                GUI.Label(new Rect(x, y, width, h), content, _warning);
                y += h;
            }

            if (hitscan || Mix.Count == 0)
            {
                return;
            }

            // Legend, bottom-up: which slots each variant fills.
            float ly = rect.yMax - 6f;
            for (int m = Mix.Count - 1; m >= 0; m--)
            {
                Text.Clear();
                Text.Append(Letter(m)).Append("  slots ");
                bool any = false;
                for (int i = 0; i < Bullets.Count; i++)
                {
                    if (Bullets[i].Variant != m)
                    {
                        continue;
                    }
                    Text.Append(any ? "," : "").Append(i);
                    any = true;
                }
                if (!any)
                {
                    Text.Append("none — never fires");
                }
                var v = Mix[m];
                Text.Append("  ·  ").Append(v.Speed.ToString("0.#")).Append(" t/s  ·  ")
                    .Append(v.DamageMin).Append('-').Append(v.DamageMax).Append(" dmg");

                ly -= 16f;
                EditorGUI.DrawRect(new Rect(x, ly + 4f, 8f, 8f), Opaque(v.Tint));
                GUI.Label(new Rect(x + 12f, ly, width - 12f, 16f), Text.ToString(), _sub);
            }
        }

        /// <summary>
        /// Things about this weapon that will not do what the inspector suggests.
        /// </summary>
        /// <remarks>
        /// Shown on the preview rather than logged: every one of these is a weapon
        /// that fires, just not the way it reads, and a console line scrolls away.
        /// </remarks>
        private static List<string> Warnings(WeaponItem w, PreviewState s, bool hitscan)
        {
            WarningList.Clear();

            if (w.Pattern == null)
            {
                WarningList.Add("No pattern set; the push sends Single.");
            }
            if (w.Shots > ServerMaxShots)
            {
                WarningList.Add($"Pattern asks for {w.Shots} shots; the server clamps to {ServerMaxShots}. " +
                                "Shown as the server fires it.");
            }
            if (w.Range > 0f && s.AsEnemy)
            {
                WarningList.Add("Enemies ignore Range and fire this as projectiles.");
            }
            if (hitscan)
            {
                if (w.SpinDegreesPerSec != 0f) WarningList.Add("Hitscan ignores Spin.");
                if (w.WaveAmplitude > 0f) WarningList.Add("Hitscan ignores Wave.");
                if (Mix.Count > 0) WarningList.Add("Hitscan ignores the Bullet Mix and uses the flat damage fields.");
                if (w.Pattern is ParallelShot) WarningList.Add("Hitscan ignores Parallel's width; every ray starts at the shooter.");
                return WarningList;
            }
            if (w.HelixWithoutWave)
            {
                WarningList.Add("Helix with Wave Amplitude 0: every strand travels the same line.");
            }
            if (w.Mix != null && Mix.Count < w.Mix.Count)
            {
                WarningList.Add("Mix has empty entries, which the push drops.");
            }
            if (Mix.Count == 1)
            {
                WarningList.Add("One-entry mix fires exactly what the flat fields would.");
            }
            if (Mix.Count > Slots.Count)
            {
                WarningList.Add($"{Mix.Count} variants but {Slots.Count} projectile(s); some never fire.");
            }

            var catalogue = Catalogue;
            if (catalogue != null)
            {
                foreach (var b in Bullets)
                {
                    if (b.SpriteId != 0 && catalogue.For(b.SpriteId) == null)
                    {
                        WarningList.Add($"Bullet sprite {b.SpriteId} is not in the catalogue; it draws as a plain blob.");
                        break;
                    }
                }
            }
            return WarningList;
        }

        private static void EnsureStyles()
        {
            _header ??= new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.92f, 0.92f, 0.92f) } };
            _sub ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.7f, 0.7f, 0.72f) } };
            _warning ??= new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                normal = { textColor = new Color(1f, 0.78f, 0.3f) },
            };
            _slot ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 1f, 1f, 0.8f) } };
        }

        private static string Letter(int variant) =>
            variant < 26 ? ((char)('A' + variant)).ToString() : "#" + variant;

        private static Color Opaque(Color c) => new(c.r, c.g, c.b, 1f);
    }
}
