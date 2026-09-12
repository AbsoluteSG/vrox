using System.Collections.Generic;
using UnityEngine;

namespace Vrox
{
    /// <summary>
    /// Floating damage numbers, for anything that wants one.
    /// </summary>
    /// <remarks>
    /// Its own component rather than part of the entity renderer, because both
    /// enemies and the player raise them and neither owns the other. Anything can
    /// call <see cref="Show"/> without knowing what is drawing.
    ///
    /// IMGUI, so there is no prefab, no canvas and no font asset — a number is a
    /// string at a screen position, and anything more is machinery for something
    /// that lasts under a second.
    /// </remarks>
    public sealed class DamageNumbers : MonoBehaviour
    {
        [Header("Status text")]
        [Tooltip("Colour of a stun call-out.")]
        public Color StunColour = new Color(1f, 0.85f, 0.25f);

        [Tooltip("Colour of a slow call-out.")]
        public Color SlowColour = new Color(0.5f, 0.8f, 1f);

        [Tooltip("Colour of an armour-break call-out.")]
        public Color ArmorBreakColour = new Color(1f, 0.5f, 0.2f);

        [Tooltip("Point size of status words. Larger than a damage number on purpose — " +
                 "a status changes what you can do, and is worth reading first.")]
        [Range(8, 48)]
        public int StatusSize = 20;

        [Tooltip("How long a status word stays up. Longer than a number, because it " +
                 "reports a state you are still in rather than a hit that has landed.")]
        [Range(0.2f, 4f)]
        public float StatusLifetime = 1.1f;

        [Tooltip("How long a number stays on screen, in seconds.")]
        public float Lifetime = 0.8f;

        [Tooltip("How far a number drifts upward over its life, in tiles.")]
        public float Rise = 1.2f;

        [Tooltip("Damage dealt by this player.")]
        public Color OutgoingColour = new Color(1f, 0.95f, 0.5f);

        [Tooltip("Damage taken by this player. Deliberately unlike the outgoing " +
                 "colour — mistaking damage you took for damage you dealt is the " +
                 "one confusion these numbers must not cause.")]
        public Color IncomingColour = new Color(1f, 0.35f, 0.3f);

        [Tooltip("Point size for damage taken. Larger than outgoing, because it is " +
                 "the number that should interrupt you.")]
        public int IncomingSize = 22;

        public int OutgoingSize = 15;

        private static DamageNumbers? _instance;

        private struct Entry
        {
            public float X, Y, StartedAt;
            public int Amount;
            public bool Incoming;

            /// <summary>Drawn instead of <see cref="Amount"/> when set.</summary>
            public string? Text;

            public Color Colour;
            public int Size;
            public float Lifetime;
        }

        private readonly List<Entry> _entries = new();
        private GUIStyle? _outgoing;
        private GUIStyle? _incoming;

        private void Awake() => _instance = this;

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>
        /// Raises a number at a world position.
        /// </summary>
        /// <remarks>
        /// Silently does nothing when no instance is in the scene. Damage numbers
        /// are decoration; a missing one should not throw inside a renderer's
        /// update loop.
        /// </remarks>
        public static void Show(float worldX, float worldY, int amount, bool incoming)
        {
            if (_instance == null || amount <= 0)
            {
                return;
            }
            _instance._entries.Add(new Entry
            {
                X = worldX,
                Y = worldY,
                Amount = amount,
                Incoming = incoming,
                Lifetime = _instance.Lifetime,
                StartedAt = Time.time,
            });
        }

        /// <summary>
        /// Raises a word at a world position — a status, rather than a number.
        /// </summary>
        /// <remarks>
        /// Shares the rise-and-fade of a damage number because it is the same
        /// gesture and should read as one family, but carries its own colour, size
        /// and lifetime: a status says what you can no longer do, and losing it in
        /// a column of numbers is exactly the failure worth avoiding.
        /// </remarks>
        public static void ShowStatus(float worldX, float worldY, Vrox.Equipment.DebuffKind kind)
        {
            if (_instance == null || kind == Vrox.Equipment.DebuffKind.None)
            {
                return;
            }

            var (text, colour) = kind switch
            {
                Vrox.Equipment.DebuffKind.Stun => ("STUNNED", _instance.StunColour),
                Vrox.Equipment.DebuffKind.Slow => ("SLOWED", _instance.SlowColour),
                Vrox.Equipment.DebuffKind.ArmorBreak => ("ARMOR BREAK", _instance.ArmorBreakColour),
                _ => (null, Color.white),
            };
            if (text == null)
            {
                return;
            }

            ShowText(worldX, worldY, text, colour, _instance.StatusSize, _instance.StatusLifetime);
        }

        /// <summary>Raises arbitrary text. Silently does nothing with no instance.</summary>
        public static void ShowText(float worldX, float worldY, string text, Color colour,
                                    int size, float lifetime)
        {
            if (_instance == null || string.IsNullOrEmpty(text))
            {
                return;
            }
            _instance._entries.Add(new Entry
            {
                X = worldX,
                Y = worldY,
                Text = text,
                Colour = colour,
                Size = size,
                Lifetime = lifetime,
                StartedAt = Time.time,
            });
        }

        private void OnGUI()
        {
            if (_entries.Count == 0)
            {
                return;
            }
            var camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            _outgoing ??= Style(OutgoingSize);
            _incoming ??= Style(IncomingSize);

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                float progress = (Time.time - _entries[i].StartedAt)
                                 / Mathf.Max(0.01f, _entries[i].Lifetime);
                if (progress >= 1f)
                {
                    _entries.RemoveAt(i);
                    continue;
                }

                // Rises quickly then slows, and only starts fading halfway. A
                // number that fades from the instant it appears is hard to read at
                // exactly the moment you want to read it.
                float rise = Rise * Mathf.Sqrt(progress);
                float alpha = progress < 0.5f ? 1f : 1f - (progress - 0.5f) * 2f;

                var screen = camera.WorldToScreenPoint(
                    new Vector3(_entries[i].X, _entries[i].Y + rise, 0f));
                if (screen.z < 0f)
                {
                    continue;
                }

                bool isText = _entries[i].Text != null;

                var colour = isText
                    ? _entries[i].Colour
                    : _entries[i].Incoming ? IncomingColour : OutgoingColour;
                colour.a = alpha;

                var style = isText
                    ? Sized(_entries[i].Size)
                    : _entries[i].Incoming ? _incoming : _outgoing;

                var previous = GUI.color;
                GUI.color = colour;
                // Unity's screen origin is bottom-left, IMGUI's is top-left.
                // Wider than a number: "ARMOR BREAK" does not fit in 100px and
                // IMGUI clips rather than overflowing.
                GUI.Label(new Rect(screen.x - 90f, Screen.height - screen.y - 14f, 180f, 28f),
                          _entries[i].Text ?? _entries[i].Amount.ToString(),
                          style);
                GUI.color = previous;
            }
        }

        /// <summary>A cached style for one point size.</summary>
        /// <remarks>
        /// Cached because OnGUI runs several times a frame and allocating a
        /// GUIStyle per label per pass is the kind of garbage that shows up as a
        /// stutter rather than as a number anybody looks at.
        /// </remarks>
        private GUIStyle Sized(int size)
        {
            if (!_sized.TryGetValue(size, out var style))
            {
                style = Style(size);
                _sized[size] = style;
            }
            return style;
        }

        private readonly Dictionary<int, GUIStyle> _sized = new();

        private static GUIStyle Style(int size) => new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = size,
            fontStyle = FontStyle.Bold,
        };
    }
}
