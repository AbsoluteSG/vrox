using System.Collections.Generic;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Steps one enemy against a dummy player, tick by tick, as <c>AdvanceEnemies</c> does.
    /// </summary>
    /// <remarks>
    /// Re-run from zero on every repaint rather than advanced incrementally. The
    /// enemy's position is integrated, not a closed form of time, so scrubbing
    /// backwards needs the whole history; and a few thousand ticks of this is far
    /// cheaper than drawing them. Re-running also means an inspector edit changes
    /// the whole fight at once, which is the point of previewing it.
    ///
    /// Same order as the server within a tick: find targets from the pre-move
    /// position, move, count engaged time, fire at the target's pre-move position
    /// from the enemy's post-move one, then check the phase transition.
    ///
    /// Deliberately not reproduced:
    /// - Walls, <c>Slide</c> and world bounds: this is open ground.
    /// - Other players and enemies, dormancy, and stun.
    /// - How damage really arrives. The dummy deals <see cref="EnemyPreviewState.Dps"/>
    ///   as a steady drain at the start of each tick, where the server applies it
    ///   per shot, rounded per shot, in its own pass; health thresholds are crossed
    ///   at about the right time, not the exact tick.
    /// - The server clock. Time starts at 0, and the spawn-time random seed is a slider.
    /// </remarks>
    internal sealed class EnemySimulation
    {
        public struct Phase
        {
            public string Name;
            public MoveRules Rules;
            public float AttackRange;
            public WeaponItem? Weapon;

            /// <summary>0 inherits the enemy's base speed.</summary>
            public float Speed;

            public bool Invulnerable;
            public bool Untargetable;
            public float DamageTakenPercent;
            public TransitionKind Transition;
            public float TransitionValue;

            /// <summary>Index into <see cref="EnemyItem.Phases"/>, or -1 for an enemy with none.</summary>
            public int Source;
        }

        public struct Tick
        {
            public Vector2 Enemy;
            public Vector2 Dummy;
            public int Phase;
            public ushort Hp;
            public bool HasTarget;
            public bool InRange;
        }

        public struct Fire
        {
            public int Tick;
            public Vector2 Origin;
            public Vector2 Aim;
            public WeaponItem Weapon;
        }

        public readonly List<Phase> Phases = new();
        public readonly List<Tick> Ticks = new();
        public readonly List<Fire> Fires = new();

        /// <summary>Tick each phase was first entered, or -1 if the fight never got there.</summary>
        public readonly List<int> EnteredAt = new();

        public int DiedAt = -1;
        public ushort MaxHp;
        public float BaseSpeed;

        /// <summary>True when one phase is being run on its own, with its transition ignored.</summary>
        public bool Isolated;

        /// <summary>Largest range any phase draws, for fitting the view.</summary>
        public float LargestRange;

        public float Seconds => Ticks.Count * EnemyMath.TickSeconds;

        public static float TimeOf(int tick) => tick * EnemyMath.TickSeconds;

        public int TickAt(double seconds) =>
            Ticks.Count == 0 ? 0 : Mathf.Clamp((int)(seconds / EnemyMath.TickSeconds), 0, Ticks.Count - 1);

        /// <param name="isolatePhase">Run only this phase, forever. -1 runs the fight.</param>
        public void Run(EnemyItem e, EnemyPreviewState s, int isolatePhase)
        {
            Resolve(e, isolatePhase);
            Ticks.Clear();
            Fires.Clear();
            DiedAt = -1;

            var home = Vector2.zero;
            var enemy = home;
            var dummy = s.DummyAnchor;

            int phaseIndex = Isolated ? 0 : Mathf.Clamp(s.StartPhase, 0, Phases.Count - 1);
            EnteredAt[phaseIndex] = 0;
            float hp = MaxHp * Mathf.Clamp(Isolated ? 100f : s.StartHpPercent, 1f, 100f) / 100f;
            long nextShot = 0;
            ulong engaged = 0;

            int count = Mathf.CeilToInt(Mathf.Clamp(s.FightSeconds, 5f, 600f) / EnemyMath.TickSeconds);
            for (int i = 0; i < count; i++)
            {
                long now = i * EnemyMath.TickMicros;
                float time = i * EnemyMath.TickSeconds;
                if (i > 0)
                {
                    dummy = DummyPlayer.Step(s, time, dummy, enemy, home);
                }

                var phase = Phases[phaseIndex];

                if (s.Dps > 0f && !phase.Untargetable && !phase.Invulnerable
                    && EnemyMath.InRange(enemy, dummy, s.PlayerRange))
                {
                    hp -= s.Dps * EnemyMath.TickSeconds * phase.DamageTakenPercent / 100f;
                }
                ushort hpNow = (ushort)Mathf.Clamp(Mathf.CeilToInt(hp), 0, MaxHp);
                if (hpNow == 0)
                {
                    DiedAt = i;
                    Ticks.Add(new Tick { Enemy = enemy, Dummy = dummy, Phase = phaseIndex, Hp = 0 });
                    break;
                }

                bool hasTarget = EnemyMath.InRange(enemy, dummy, phase.Rules.AggroRange);
                bool inRange = EnemyMath.InRange(enemy, dummy, phase.AttackRange);

                var dir = EnemyMath.MoveDirection(phase.Rules, enemy, home, s.WanderSeed, time,
                                                  hasTarget, hasTarget ? dummy : Vector2.zero);
                if (dir.x != 0f || dir.y != 0f)
                {
                    float speed = phase.Speed > 0f ? phase.Speed : BaseSpeed;
                    if (s.Slowed)
                    {
                        speed *= 0.5f;
                    }
                    enemy = new Vector2(enemy.x + dir.x * speed * EnemyMath.TickSeconds,
                                        enemy.y + dir.y * speed * EnemyMath.TickSeconds);
                }

                if (inRange && phase.Transition == TransitionKind.AfterSeconds)
                {
                    engaged += (ulong)EnemyMath.TickMicros;
                }

                if (inRange && phase.Weapon != null && now >= nextShot)
                {
                    var aim = dummy - enemy;
                    float length = aim.magnitude;
                    if (length > 0.0001f)
                    {
                        Fires.Add(new Fire { Tick = i, Origin = enemy, Aim = aim / length, Weapon = phase.Weapon });
                        nextShot = now + Mathf.Clamp(phase.Weapon.FireRateMs, 50, 5000) * 1000L;
                    }
                }

                if (EnemyMath.LeavesPhase(phase.Transition, phase.TransitionValue, hpNow, MaxHp, engaged)
                    && phaseIndex + 1 < Phases.Count)
                {
                    phaseIndex++;
                    engaged = 0;
                    nextShot = now;
                    if (EnteredAt[phaseIndex] < 0)
                    {
                        EnteredAt[phaseIndex] = i + 1;
                    }
                }

                Ticks.Add(new Tick
                {
                    Enemy = enemy,
                    Dummy = dummy,
                    Phase = phaseIndex,
                    Hp = hpNow,
                    HasTarget = hasTarget,
                    InRange = inRange,
                });
            }
        }

        /// <summary>The phases the server would hold for this enemy, as <c>PushEquipment</c> and the upserts shape them.</summary>
        private void Resolve(EnemyItem e, int isolatePhase)
        {
            Phases.Clear();
            EnteredAt.Clear();
            MaxHp = (ushort)Mathf.Max(1, e.MaxHp);
            BaseSpeed = Mathf.Clamp(e.Speed, 0f, 20f);

            if (e.Phases == null || e.Phases.Count == 0)
            {
                // CurrentPhase's implicit phase 0: the archetype's own fields, forever.
                Phases.Add(new Phase
                {
                    Name = "Base",
                    Rules = MoveRules.From(e.Movement ?? new WanderMovement()),
                    AttackRange = Mathf.Clamp(e.AttackRange, 0f, 60f),
                    Weapon = e.Weapon,
                    Speed = 0f,
                    DamageTakenPercent = 100f,
                    Transition = TransitionKind.Never,
                    Source = -1,
                });
            }
            else
            {
                for (int i = 0; i < e.Phases.Count && i < 255; i++)
                {
                    var p = e.Phases[i] ?? new BossPhase();
                    var exit = p.Transition ?? new NeverTransition();
                    Phases.Add(new Phase
                    {
                        Name = string.IsNullOrWhiteSpace(p.Name) ? $"Phase {i}" : p.Name,
                        Rules = MoveRules.From(p.Movement ?? new StaticMovement()),
                        AttackRange = Mathf.Clamp(p.AttackRange, 0f, 60f),
                        Weapon = p.Weapon,
                        Speed = Mathf.Clamp(p.Speed, 0f, 20f),
                        Invulnerable = p.Invulnerable,
                        Untargetable = p.Untargetable,
                        DamageTakenPercent = Mathf.Clamp(p.DamageTakenPercent, 0f, 1000f),
                        Transition = exit.Kind,
                        TransitionValue = exit.Value,
                        Source = i,
                    });
                }
            }

            // Only a real phase can be isolated; the implicit one already never ends.
            Isolated = isolatePhase >= 0 && isolatePhase < Phases.Count && Phases[0].Source >= 0;
            if (Isolated)
            {
                var only = Phases[isolatePhase];
                only.Transition = TransitionKind.Never;
                Phases.Clear();
                Phases.Add(only);
            }

            LargestRange = 0f;
            foreach (var p in Phases)
            {
                EnteredAt.Add(-1);
                LargestRange = Mathf.Max(LargestRange,
                    Mathf.Max(p.AttackRange, Mathf.Max(p.Rules.AggroRange, p.Rules.PreferredRange)));
            }
        }

        /// <summary>Ticks spent in each phase.</summary>
        public void CountTicks(List<int> into)
        {
            into.Clear();
            for (int i = 0; i < Phases.Count; i++)
            {
                into.Add(0);
            }
            foreach (var t in Ticks)
            {
                into[t.Phase]++;
            }
        }
    }
}
