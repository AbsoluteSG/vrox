using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// A strip of a fight's phases, each as wide as the time the simulation spent in it.
    /// </summary>
    /// <remarks>
    /// Sized by simulated time rather than evenly, so the strip reads as pacing —
    /// a phase that takes a minute at this DPS looks like a minute. Phases the fight
    /// never reached get a narrow dimmed block, so they stay clickable.
    /// </remarks>
    internal static class PhaseTimeline
    {
        public const float Height = 52f;

        private static readonly List<int> Counts = new();
        private static GUIStyle? _label;

        /// <returns>The phase clicked, or -1.</returns>
        public static int Draw(Rect rect, EnemySimulation sim, int selected, int tick, out bool doubleClick)
        {
            doubleClick = false;
            int n = sim.Phases.Count;
            if (n == 0 || sim.Ticks.Count == 0)
            {
                return -1;
            }

            sim.CountTicks(Counts);
            float minimum = Mathf.Max(1f, sim.Ticks.Count * 0.06f);
            float total = 0f;
            for (int i = 0; i < n; i++)
            {
                total += Mathf.Max(Counts[i], minimum);
            }

            var ev = Event.current;
            int clicked = -1;
            float x = rect.x;
            _label ??= new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = false,
                clipping = TextClipping.Clip,
                richText = true,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) },
            };

            for (int i = 0; i < n; i++)
            {
                float width = rect.width * Mathf.Max(Counts[i], minimum) / total;
                var block = new Rect(x, rect.y, width - 1f, rect.height);
                x += width;
                var phase = sim.Phases[i];
                bool reached = sim.EnteredAt[i] >= 0;

                if (ev.type == EventType.MouseDown && ev.button == 0 && block.Contains(ev.mousePosition))
                {
                    clicked = phase.Source >= 0 ? phase.Source : i;
                    doubleClick = ev.clickCount >= 2;
                    ev.Use();
                }

                if (ev.type != EventType.Repaint)
                {
                    continue;
                }

                var fill = phase.Invulnerable ? new Color(0.45f, 0.38f, 0.15f)
                         : phase.Untargetable ? new Color(0.22f, 0.24f, 0.32f)
                         : new Color(0.2f, 0.22f, 0.24f);
                if (!reached)
                {
                    fill *= 0.6f;
                }
                EditorGUI.DrawRect(block, fill);
                if (phase.Source == selected)
                {
                    EditorGUI.DrawRect(new Rect(block.x, block.yMax - 3f, block.width, 3f), new Color(0.4f, 0.7f, 1f));
                }

                string weapon = phase.Weapon != null ? phase.Weapon.name : "no weapon";
                string enter = reached ? $"@{Clock(EnemySimulation.TimeOf(sim.EnteredAt[i]))}" : "not reached";
                string text = $"<b>{phase.Source}. {phase.Name}</b>  {enter}\n" +
                              $"{phase.Rules.Kind} · {weapon}\n" +
                              $"{Exit(phase)}{Flags(phase)}";
                GUI.Label(new Rect(block.x + 3f, block.y + 2f, block.width - 6f, block.height - 4f), text, _label);
            }

            if (ev.type == EventType.Repaint)
            {
                // Where "now" is: blocks run in phase order and phases only advance,
                // so a tick sits inside its own phase's block at its offset from entry.
                var now = sim.Ticks[Mathf.Clamp(tick, 0, sim.Ticks.Count - 1)];
                float bx = rect.x;
                for (int i = 0; i < now.Phase; i++)
                {
                    bx += rect.width * Mathf.Max(Counts[i], minimum) / total;
                }
                float bw = rect.width * Mathf.Max(Counts[now.Phase], minimum) / total;
                int entered = Mathf.Max(0, sim.EnteredAt[now.Phase]);
                float within = Counts[now.Phase] > 0 ? (tick - entered) / (float)Counts[now.Phase] : 0f;
                float mx = bx + bw * Mathf.Clamp01(within);
                EditorGUI.DrawRect(new Rect(mx - 1f, rect.y, 2f, rect.height), Color.white);
            }

            return clicked;
        }

        public static string Exit(EnemySimulation.Phase phase) => phase.Transition switch
        {
            TransitionKind.HealthBelowPercent => $"ends at HP ≤ {phase.TransitionValue:0}%",
            TransitionKind.AfterSeconds => $"ends after {phase.TransitionValue:0.#}s engaged",
            _ => "never ends",
        };

        private static string Flags(EnemySimulation.Phase phase)
        {
            string flags = "";
            if (phase.Invulnerable) flags += " · invulnerable";
            if (phase.Untargetable) flags += " · untargetable";
            if (!Mathf.Approximately(phase.DamageTakenPercent, 100f)) flags += $" · takes {phase.DamageTakenPercent:0}%";
            return flags;
        }

        private static string Clock(float seconds) => $"{(int)(seconds / 60f)}:{seconds % 60f:00}";
    }
}
