using System.Collections.Generic;
using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>Where one projectile in a volley starts, relative to the aim.</summary>
    /// <remarks>
    /// The client twin of the module's <c>Placement</c>. Named differently because
    /// "Placement" is already a header in the tracer and muzzle-flash components,
    /// and a type sharing a word that general is how the Grid collision happened.
    /// </remarks>
    public struct VolleySlot
    {
        /// <summary>Rotation from the aim direction, in degrees.</summary>
        public float AngleDeg;

        /// <summary>Sideways offset of the spawn point, in tiles.</summary>
        public float Lateral;

        /// <summary>Starting point of this projectile's oscillation, in radians.</summary>
        public float Phase;
    }

    /// <summary>
    /// The client's one copy of the server's volley maths.
    /// </summary>
    /// <remarks>
    /// Each method mirrors a named piece of <c>server/module/Module.cs</c> —
    /// <c>PlaceShots</c>, the rotation in <c>FireVolley</c>, <c>ShotPositionAt</c>
    /// and <c>ProfileFor</c>. The server cannot share source with Unity, so this is
    /// kept in step by hand, and it is the only place on the client that is: the
    /// game's bullet drawing and the editor's weapon preview both call it.
    ///
    /// If this and the module ever disagree, the server is right. The game then
    /// draws bullets that are not where they collide, and the preview shows a
    /// pattern the weapon does not fire.
    /// </remarks>
    public static class VolleyMath
    {
        /// <summary>Lays out one volley into <paramref name="into"/>. Mirrors <c>PlaceShots</c>.</summary>
        /// <remarks>
        /// Fills a caller's list rather than yielding, because the preview lays out
        /// a volley for every one in flight on every repaint.
        /// </remarks>
        public static void Place(PatternKind kind, byte count, float spread, byte groups,
                                 List<VolleySlot> into)
        {
            into.Clear();
            int n = count < 1 ? 1 : count;

            switch (kind)
            {
                case PatternKind.Spread:
                    for (int i = 0; i < n; i++)
                    {
                        into.Add(new VolleySlot
                        {
                            AngleDeg = n == 1 ? 0f : -spread * 0.5f + spread * i / (n - 1),
                        });
                    }
                    break;

                case PatternKind.Ring:
                    for (int i = 0; i < n; i++)
                    {
                        into.Add(new VolleySlot { AngleDeg = 360f * i / n });
                    }
                    break;

                case PatternKind.Parallel:
                    for (int i = 0; i < n; i++)
                    {
                        into.Add(new VolleySlot
                        {
                            Lateral = n == 1 ? 0f : -spread * 0.5f + spread * i / (n - 1),
                        });
                    }
                    break;

                case PatternKind.Helix:
                    for (int i = 0; i < n; i++)
                    {
                        into.Add(new VolleySlot { Phase = Mathf.PI * 2f * i / n });
                    }
                    break;

                case PatternKind.Cluster:
                {
                    int g = groups < 1 ? 1 : groups;
                    if (g > n)
                    {
                        g = n;
                    }
                    int per = n / g;
                    int spare = n - per * g;
                    for (int d = 0; d < g; d++)
                    {
                        float baseDeg = 360f * d / g;
                        int here = per + (d < spare ? 1 : 0);
                        for (int j = 0; j < here; j++)
                        {
                            float off = here == 1 ? 0f : -spread * 0.5f + spread * j / (here - 1);
                            into.Add(new VolleySlot { AngleDeg = baseDeg + off });
                        }
                    }
                    break;
                }

                default:
                    for (int i = 0; i < n; i++)
                    {
                        into.Add(default);
                    }
                    break;
            }
        }

        /// <summary>
        /// Rotation applied to a whole volley fired at <paramref name="seconds"/>.
        /// </summary>
        /// <remarks>
        /// The server passes seconds since the Unix epoch, so the absolute angle a
        /// real volley starts at is arbitrary; only the rotation between volleys is
        /// meaningful, and that is what a preview starting from zero shows.
        /// </remarks>
        public static float SpinDegrees(double seconds, float spinDegreesPerSec) =>
            spinDegreesPerSec == 0f ? 0f : (float)((seconds * spinDegreesPerSec) % 360.0);

        /// <summary>The aim rotated by a slot's angle plus the volley's spin.</summary>
        public static Vector2 Direction(Vector2 aim, float angleDeg, float spinDeg)
        {
            float rad = (angleDeg + spinDeg) * Mathf.PI / 180f;
            float c = Mathf.Cos(rad);
            float s = Mathf.Sin(rad);
            return new Vector2(aim.x * c - aim.y * s, aim.x * s + aim.y * c);
        }

        /// <summary>Spawn point pushed sideways, perpendicular to travel.</summary>
        public static Vector2 Origin(Vector2 from, Vector2 dir, float lateral) =>
            new(from.x + -dir.y * lateral, from.y + dir.x * lateral);

        /// <summary>
        /// Where a projectile is, <paramref name="t"/> seconds after it was fired.
        /// Mirrors <c>ShotPositionAt</c>.
        /// </summary>
        /// <remarks>
        /// The wave is applied perpendicular to travel, so it follows the shot's
        /// heading rather than the world axes.
        /// </remarks>
        public static Vector2 PositionAt(Vector2 origin, Vector2 dir, float speed,
                                         float waveAmplitude, float waveFrequency,
                                         float wavePhase, float t)
        {
            float x = origin.x + dir.x * speed * t;
            float y = origin.y + dir.y * speed * t;

            if (waveAmplitude != 0f)
            {
                float lateral = waveAmplitude *
                    Mathf.Sin(t * waveFrequency * Mathf.PI * 2f + wavePhase);
                x += -dir.y * lateral;
                y += dir.x * lateral;
            }

            return new Vector2(x, y);
        }

        /// <summary>
        /// Which variant of a mix fills a slot. Mirrors <c>ProfileFor</c>.
        /// </summary>
        /// <remarks>
        /// Only meaningful when <paramref name="mixCount"/> is at least one; an
        /// empty mix means the weapon's flat fields describe every bullet.
        /// <paramref name="groups"/> is the pattern's fan count, 0 for everything
        /// but Cluster, and is what makes Edges mark the outside of each fan rather
        /// than only the two ends of the whole volley.
        /// </remarks>
        public static int VariantIndex(SlotAssignment assignment, int slot, byte shots,
                                       int mixCount, byte groups)
        {
            if (mixCount <= 1)
            {
                return 0;
            }

            int n = shots > 0 ? shots : 1;

            if (assignment == SlotAssignment.Block)
            {
                int index = slot * mixCount / n;
                return index >= mixCount ? mixCount - 1 : index;
            }

            if (assignment == SlotAssignment.Edges)
            {
                int g = groups > 0 ? groups : 1;
                int per = n / g;
                if (per < 1)
                {
                    per = n;
                }
                int within = slot % per;
                if (within == 0 || within == per - 1)
                {
                    return 0;
                }
                return 1 + (within - 1) % (mixCount - 1);
            }

            return slot % mixCount;
        }
    }
}
