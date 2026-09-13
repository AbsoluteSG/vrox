using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// Compact editors for one movement or one phase, and the phase list's selection, add, duplicate and delete.
    /// </summary>
    /// <remarks>
    /// Drawn with plain <see cref="SerializedProperty"/> fields rather than Odin, so
    /// one phase can be edited on its own instead of inside the whole enemy's list.
    /// That also means Odin's type picker is not available here, so polymorphic
    /// fields get <see cref="TypePicker"/>. The enemy's full Odin inspector is
    /// still one foldout away.
    /// </remarks>
    internal static class EnemyPanels
    {
        private static readonly Dictionary<Type, Type[]> TypesByBase = new();

        /// <summary>A concrete-type dropdown for a <c>[SerializeReference]</c> field, then its fields.</summary>
        public static void TypePicker(SerializedProperty? property, Type baseType, string label)
        {
            if (property == null)
            {
                return;
            }

            if (!TypesByBase.TryGetValue(baseType, out var types))
            {
                types = TypeCache.GetTypesDerivedFrom(baseType)
                    .Where(t => !t.IsAbstract && !t.IsGenericType)
                    .OrderBy(t => t.Name)
                    .ToArray();
                TypesByBase[baseType] = types;
            }

            string current = property.managedReferenceFullTypename;
            int index = Array.FindIndex(types, t => current == $"{t.Assembly.GetName().Name} {t.FullName}");
            string suffix = baseType == typeof(MovementBehaviour) ? "Movement"
                          : baseType == typeof(PhaseTransition) ? "Transition" : "";
            var names = types.Select(t => ObjectNames.NicifyVariableName(
                suffix.Length > 0 && t.Name.EndsWith(suffix) ? t.Name[..^suffix.Length] : t.Name)).ToArray();

            int picked = EditorGUILayout.Popup(label, index, names);
            if (picked != index && picked >= 0)
            {
                property.managedReferenceValue = Activator.CreateInstance(types[picked]);
                return;
            }

            if (string.IsNullOrEmpty(current) || !property.hasVisibleChildren)
            {
                return;
            }

            EditorGUI.indentLevel++;
            var child = property.Copy();
            var end = property.GetEndProperty();
            bool enter = true;
            while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
            {
                EditorGUILayout.PropertyField(child, true);
                enter = false;
            }
            EditorGUI.indentLevel--;
        }

        /// <summary>The movement, speed, weapon and attack range for the enemy (phase -1) or one phase.</summary>
        /// <returns>True when "Edit weapon" was pressed.</returns>
        public static bool Movement(SerializedObject so, int phase)
        {
            string root = phase < 0 ? "" : $"Phases.Array.data[{phase}].";
            EditorGUILayout.LabelField(phase < 0 ? "Movement" : $"Phase {phase} movement", EditorStyles.boldLabel);
            TypePicker(so.FindProperty(root + "Movement"), typeof(MovementBehaviour), "Behaviour");
            Field(so, root + "Speed", phase < 0 ? "Speed" : "Speed (0 = inherit)");
            Field(so, root + "AttackRange", "Attack Range");
            return WeaponField(so, root);
        }

        /// <summary>Every field of one phase.</summary>
        /// <returns>True when "Edit weapon" was pressed.</returns>
        public static bool Phase(SerializedObject so, int index)
        {
            string root = $"Phases.Array.data[{index}].";
            EditorGUILayout.LabelField($"Phase {index}", EditorStyles.boldLabel);
            Field(so, root + "Name", "Name");

            EditorGUILayout.Space(4f);
            TypePicker(so.FindProperty(root + "Movement"), typeof(MovementBehaviour), "Movement");
            Field(so, root + "Speed", "Speed (0 = inherit)");

            EditorGUILayout.Space(4f);
            Field(so, root + "AttackRange", "Attack Range");
            bool edit = WeaponField(so, root);

            EditorGUILayout.Space(4f);
            Field(so, root + "Invulnerable", "Invulnerable");
            Field(so, root + "Untargetable", "Untargetable");
            Field(so, root + "DamageTakenPercent", "Damage Taken %");

            EditorGUILayout.Space(4f);
            TypePicker(so.FindProperty(root + "Transition"), typeof(PhaseTransition), "Ends");
            return edit;
        }

        /// <summary>Previous/next selection, and add, duplicate and delete buttons for the phase list.</summary>
        /// <returns>The phase selected afterwards.</returns>
        public static int PhaseOps(EnemyItem e, SerializedObject so, int selected)
        {
            var phases = e.Phases;
            bool valid = selected >= 0 && selected < phases.Count;
            int result = selected;
            bool changed = false;

            // These arrows only select. There are deliberately no reorder buttons:
            // arrows that moved the phase read as "go to the next phase", silently
            // reordered the fight, and left the panel showing the same phase at a new
            // index. Reorder phases in the full enemy inspector.
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!valid || selected == 0))
                {
                    if (GUILayout.Button(new GUIContent("◀ Prev", "Select the previous phase.")) && valid && selected > 0)
                    {
                        result = selected - 1;
                    }
                }
                GUILayout.Label(phases.Count == 0 ? "No phases" : $"Phase {Mathf.Max(0, selected)} of {phases.Count}",
                                EditorStyles.centeredGreyMiniLabel);
                using (new EditorGUI.DisabledScope(!valid || selected >= phases.Count - 1))
                {
                    if (GUILayout.Button(new GUIContent("Next ▶", "Select the next phase.")) && valid && selected < phases.Count - 1)
                    {
                        result = selected + 1;
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add"))
                {
                    int at = valid ? selected + 1 : phases.Count;
                    Change(e, "Add Phase", () => phases.Insert(at, new BossPhase { Name = $"Phase {phases.Count}" }));
                    result = at;
                    changed = true;
                }

                using (new EditorGUI.DisabledScope(!valid))
                {
                    if (GUILayout.Button("Duplicate") && valid)
                    {
                        Change(e, "Duplicate Phase", () => phases.Insert(selected + 1, Clone(phases[selected])));
                        result = selected + 1;
                        changed = true;
                    }

                    if (GUILayout.Button("Delete") && valid
                        && EditorUtility.DisplayDialog("Delete phase",
                            $"Delete phase {selected} \"{phases[selected].Name}\" from {e.name}? Undo can restore it.",
                            "Delete", "Cancel"))
                    {
                        Change(e, "Delete Phase", () => phases.RemoveAt(selected));
                        result = Mathf.Min(selected, phases.Count - 1);
                        changed = true;
                    }
                }
            }

            if (result != selected)
            {
                ReleaseFocus();
            }

            // Every change edits the list directly, so the serialized copy is stale
            // whether or not the selection moved.
            if (changed)
            {
                so.Update();
            }
            return result;
        }

        /// <summary>
        /// A deep copy of a phase.
        /// </summary>
        /// <remarks>
        /// Built by hand rather than by duplicating the serialized array element,
        /// because a duplicated <c>[SerializeReference]</c> element can share its
        /// movement and transition objects with the original — editing the copy
        /// would then silently edit both.
        /// </remarks>
        public static BossPhase Clone(BossPhase p) => new()
        {
            Name = p.Name + " copy",
            Movement = p.Movement != null ? CloneReference(p.Movement) : new StaticMovement(),
            Weapon = p.Weapon,
            AttackRange = p.AttackRange,
            Invulnerable = p.Invulnerable,
            Untargetable = p.Untargetable,
            DamageTakenPercent = p.DamageTakenPercent,
            Speed = p.Speed,
            Transition = p.Transition != null ? CloneReference(p.Transition) : new NeverTransition(),
        };

        private static T CloneReference<T>(T value) where T : class =>
            (T)JsonUtility.FromJson(JsonUtility.ToJson(value), value.GetType());

        /// <summary>
        /// Drops keyboard focus so fields show the newly selected phase.
        /// </summary>
        /// <remarks>
        /// IMGUI keeps drawing a focused text or number field from its own edit
        /// buffer, not from the property, so a field focused on one phase goes on
        /// showing that phase's value after the selection moves.
        /// </remarks>
        public static void ReleaseFocus()
        {
            GUIUtility.keyboardControl = 0;
            EditorGUIUtility.editingTextField = false;
        }

        private static void Change(EnemyItem e, string name, Action change)
        {
            Undo.RecordObject(e, name);
            change();
            EditorUtility.SetDirty(e);
        }

        private static void Field(SerializedObject so, string path, string label)
        {
            var property = so.FindProperty(path);
            if (property != null)
            {
                EditorGUILayout.PropertyField(property, new GUIContent(label), true);
            }
        }

        private static bool WeaponField(SerializedObject so, string root)
        {
            var weapon = so.FindProperty(root + "Weapon");
            if (weapon == null)
            {
                return false;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(weapon, new GUIContent("Weapon"));
                using (new EditorGUI.DisabledScope(weapon.objectReferenceValue == null))
                {
                    return GUILayout.Button(new GUIContent("Edit", "Open this weapon in the Pattern tab."),
                                            GUILayout.Width(40f));
                }
            }
        }
    }
}
