using UnityEngine;

namespace Vrox.Equipment
{
    /// <summary>
    /// A movement behaviour reduced to what the server stores, clamped as it clamps.
    /// </summary>
    /// <remarks>
    /// Mirrors the movement columns <c>UpsertEnemyDef</c> and <c>UpsertPhase</c>
    /// write: aggro 0-60, preferred 0-40, pivot 0-1. A preview built from the
    /// authored numbers instead would disagree with the game exactly when a
    /// designer typed something out of bounds.
    /// </remarks>
    public struct MoveRules
    {
        public BehaviourKind Kind;
        public float AggroRange;
        public float PreferredRange;
        public OrbitPivot Pivot;

        public static MoveRules From(MovementBehaviour movement) => new()
        {
            Kind = (byte)movement.Kind > 4 ? BehaviourKind.Static : movement.Kind,
            AggroRange = Mathf.Clamp(movement.AggroRange, 0f, 60f),
            PreferredRange = Mathf.Clamp(movement.PreferredRange, 0f, 40f),
            Pivot = (byte)movement.Pivot > 1 ? OrbitPivot.Player : movement.Pivot,
        };
    }

    /// <summary>
    /// The client's one copy of the server's enemy movement and phase maths.
    /// </summary>
    /// <remarks>
    /// Each method mirrors a named piece of <c>server/module/Module.cs</c>:
    /// <c>MoveEnemy</c>, <c>WanderDirection</c>, <c>Normalise</c>, the range test
    /// in <c>Nearest</c>, and the leave condition in <c>AdvancePhase</c>. Kept in
    /// step by hand, like <see cref="VolleyMath"/>; the editor's enemy designer
    /// runs it, and the game does not — nothing on the client predicts enemies.
    ///
    /// If this and the module disagree, the server is right and the designer is
    /// showing a fight that does not happen.
    /// </remarks>
    public static class EnemyMath
    {
        /// <summary>The server's fixed step, <c>TickSeconds</c>.</summary>
        public const float TickSeconds = 0.05f;

        /// <summary><see cref="TickSeconds"/> in microseconds, as the server's timers count.</summary>
        public const long TickMicros = 50_000;

        /// <summary>
        /// Where an enemy wants to move this tick, as a unit vector or zero. Mirrors <c>MoveEnemy</c>.
        /// </summary>
        /// <remarks>
        /// <paramref name="seed"/> is the enemy's spawn-time <c>Phase</c>, rolled
        /// from the server's RNG; <paramref name="time"/> is seconds on the server
        /// clock. The server passes a target of zero when there is none, and so
        /// should callers, because Chase and KeepDistance read the distance before
        /// they check.
        /// </remarks>
        public static Vector2 MoveDirection(in MoveRules rules, Vector2 enemy, Vector2 home, float seed,
                                            float time, bool hasTarget, Vector2 target)
        {
            float toX = target.x - enemy.x;
            float toY = target.y - enemy.y;
            float distance = Mathf.Sqrt(toX * toX + toY * toY);

            switch (rules.Kind)
            {
                case BehaviourKind.Wander:
                    return WanderDirection(rules.PreferredRange, enemy, home, seed, time);

                case BehaviourKind.Chase:
                    if (!hasTarget || distance < 0.05f)
                    {
                        return WanderDirection(rules.PreferredRange, enemy, home, seed, time);
                    }
                    return new Vector2(toX / distance, toY / distance);

                case BehaviourKind.Orbit:
                {
                    bool aroundHome = rules.Pivot == OrbitPivot.SpawnPoint;
                    float pivotX = aroundHome ? home.x : target.x;
                    float pivotY = aroundHome ? home.y : target.y;

                    if (!aroundHome && !hasTarget)
                    {
                        return WanderDirection(rules.PreferredRange, enemy, home, seed, time);
                    }

                    float px = pivotX - enemy.x;
                    float py = pivotY - enemy.y;
                    float pd = Mathf.Sqrt(px * px + py * py);
                    if (pd < 0.05f)
                    {
                        return new Vector2(1f, 0f);
                    }

                    float nx = px / pd;
                    float ny = py / pd;
                    float error = pd - rules.PreferredRange;
                    float pull = Mathf.Max(-1f, Mathf.Min(1f, error * 0.5f));
                    return Normalise(-ny + nx * pull, nx + ny * pull);
                }

                case BehaviourKind.KeepDistance:
                {
                    if (!hasTarget)
                    {
                        return WanderDirection(rules.PreferredRange, enemy, home, seed, time);
                    }
                    float error = distance - rules.PreferredRange;
                    if (Mathf.Abs(error) < 0.5f || distance < 0.05f)
                    {
                        return Vector2.zero;
                    }
                    float sign = error > 0f ? 1f : -1f;
                    return new Vector2(toX / distance * sign, toY / distance * sign);
                }

                default:
                    return Vector2.zero;
            }
        }

        /// <summary>A slow drift that stays near home. Mirrors <c>WanderDirection</c>.</summary>
        public static Vector2 WanderDirection(float preferredRange, Vector2 enemy, Vector2 home,
                                              float seed, float time)
        {
            float angle = seed + time * 0.7f;
            float dx = Mathf.Cos(angle);
            float dy = Mathf.Sin(angle);

            float homeX = home.x - enemy.x;
            float homeY = home.y - enemy.y;
            float fromHome = Mathf.Sqrt(homeX * homeX + homeY * homeY);
            float leash = preferredRange > 0f ? preferredRange : 4f;
            if (fromHome > leash)
            {
                float pull = Mathf.Min(1f, (fromHome - leash) / leash);
                dx += homeX / fromHome * pull * 2f;
                dy += homeY / fromHome * pull * 2f;
            }
            return Normalise(dx, dy);
        }

        /// <summary>The wander leash an enemy with this preferred range is pulled back inside.</summary>
        public static float Leash(float preferredRange) => preferredRange > 0f ? preferredRange : 4f;

        public static Vector2 Normalise(float x, float y)
        {
            float length = Mathf.Sqrt(x * x + y * y);
            return length <= 0.0001f ? Vector2.zero : new Vector2(x / length, y / length);
        }

        /// <summary>Whether <paramref name="other"/> counts as within range. Mirrors <c>Nearest</c>'s test.</summary>
        public static bool InRange(Vector2 from, Vector2 other, float range)
        {
            float dx = other.x - from.x;
            float dy = other.y - from.y;
            return dx * dx + dy * dy <= range * range;
        }

        /// <summary>Whether a phase's transition fires this tick. Mirrors <c>AdvancePhase</c>.</summary>
        /// <remarks>
        /// Timed phases count <paramref name="engagedMicros"/>: time a player spent
        /// inside Attack Range, not time elapsed.
        /// </remarks>
        public static bool LeavesPhase(TransitionKind kind, float value, ushort hp, ushort maxHp,
                                       ulong engagedMicros) => kind switch
        {
            TransitionKind.HealthBelowPercent => maxHp > 0 && hp * 100f / maxHp <= value,
            TransitionKind.AfterSeconds => engagedMicros / 1_000_000f >= value,
            _ => false,
        };
    }
}
