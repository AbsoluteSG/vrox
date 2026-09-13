using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// The stand-in player an enemy preview reacts to.
    /// </summary>
    /// <remarks>
    /// Every script is either a function of time or a step from the previous tick,
    /// so re-running the simulation from zero gives the same fight every time —
    /// which is what lets the preview be scrubbed.
    /// </remarks>
    internal static class DummyPlayer
    {
        /// <summary>The server's <c>PlayerRadius</c>.</summary>
        public const float Radius = 0.4f;

        public static Vector2 Step(EnemyPreviewState s, float time, Vector2 previous, Vector2 enemy,
                                   Vector2 home)
        {
            var offset = s.DummyAnchor - home;
            float distance = offset.magnitude;
            float speed = Mathf.Max(0.1f, s.DummySpeed);

            switch (s.Script)
            {
                case EnemyPreviewState.DummyScript.CircleStrafe:
                {
                    float r = Mathf.Max(distance, 0.5f);
                    float angle = Mathf.Atan2(offset.y, offset.x) + time * speed / r;
                    return home + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
                }

                case EnemyPreviewState.DummyScript.ApproachRetreat:
                {
                    float near = Mathf.Min(Mathf.Max(0f, s.ApproachNear), distance);
                    if (distance - near < 0.01f)
                    {
                        return s.DummyAnchor;
                    }
                    var dir = offset / distance;
                    float period = 2f * (distance - near) / speed;
                    float phase = time % period / period;
                    float inward = phase < 0.5f ? phase * 2f : 2f - phase * 2f;
                    return home + dir * Mathf.Lerp(distance, near, inward);
                }

                case EnemyPreviewState.DummyScript.Kite:
                {
                    float want = Mathf.Max(distance, 0.5f);
                    var away = previous - enemy;
                    float d = away.magnitude;
                    away = d < 0.001f ? Vector2.up : away / d;
                    float step = Mathf.Clamp(want - d, -speed * EnemyMath.TickSeconds,
                                             speed * EnemyMath.TickSeconds);
                    return previous + away * step;
                }

                default:
                    return s.DummyAnchor;
            }
        }
    }
}
