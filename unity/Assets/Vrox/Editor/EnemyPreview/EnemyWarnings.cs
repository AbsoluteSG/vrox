using System.Collections.Generic;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Ways an enemy will not fight the way its inspector reads, drawn on the designer's canvas.
    /// </summary>
    /// <remarks>
    /// Includes the checks <see cref="EnemyItem"/>'s OnValidate logs, because a
    /// console line scrolls away while the fight is on screen, plus the ones only
    /// a simulated fight can see.
    /// </remarks>
    internal static class EnemyWarnings
    {
        public static void Collect(EnemyItem e, EnemySimulation sim, EnemyPreviewState s, List<string> into)
        {
            into.Clear();
            var phases = e.Phases;

            if (phases == null || phases.Count == 0)
            {
                if (e.ArmedButBlind)
                {
                    into.Add("Has a weapon but an Attack Range of 0, so it never fires.");
                }
                return;
            }

            if (e.Weapon != null || e.Movement is not null and not StaticMovement)
            {
                into.Add("Has phases, so the top-level Movement, Weapon and Attack Range are not used in the fight.");
            }

            float previousHealth = -1f;
            bool anyHealth = false;
            for (int i = 0; i < phases.Count; i++)
            {
                var p = phases[i];
                if (p == null)
                {
                    continue;
                }
                var kind = p.Transition?.Kind ?? TransitionKind.Never;
                string label = $"Phase {i} \"{p.Name}\"";

                if (p.Invulnerable && kind == TransitionKind.HealthBelowPercent)
                {
                    into.Add($"{label} is invulnerable but ends on health, which can never happen.");
                }
                if (i < phases.Count - 1 && kind == TransitionKind.Never)
                {
                    into.Add($"{label} never ends, so the {phases.Count - i - 1} phase(s) after it are unreachable.");
                }
                if (kind == TransitionKind.AfterSeconds && p.AttackRange <= 0f)
                {
                    into.Add($"{label} is timed, but its timer only runs while a player is within Attack Range, which is 0.");
                }
                if (p.Weapon != null && p.AttackRange <= 0f)
                {
                    into.Add($"{label} has a weapon but an Attack Range of 0, so it never fires.");
                }
                if (kind == TransitionKind.HealthBelowPercent)
                {
                    anyHealth = true;
                    float value = p.Transition!.Value;
                    if (previousHealth >= 0f && value >= previousHealth)
                    {
                        into.Add($"{label} ends below {value:0}%, but health is already below {previousHealth:0}% " +
                                 "when it starts, so it is skipped on its first tick.");
                    }
                    previousHealth = value;
                }
            }

            if (anyHealth && s.Dps <= 0f)
            {
                into.Add("DPS is 0, so health transitions never fire in this preview.");
            }

            if (!sim.Isolated && sim.Phases.Count > 1 && sim.EnteredAt[sim.Phases.Count - 1] < 0)
            {
                into.Add(sim.DiedAt >= 0
                    ? $"Dies at {EnemySimulation.TimeOf(sim.DiedAt):0.0}s without reaching the last phase."
                    : $"At {s.Dps:0} DPS the fight does not reach the last phase within {s.FightSeconds:0}s.");
            }
        }
    }
}
