#if !ODIN_INSPECTOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Vrox.Equipment;

namespace Vrox.Editor
{
    /// <summary>
    /// A concrete-type dropdown for the project's <c>[SerializeReference]</c> fields.
    /// </summary>
    /// <remarks>
    /// Unity serialises polymorphic fields but ships no UI for choosing the type,
    /// so a fresh field is stuck on whatever it was constructed with. This adds
    /// the picker.
    ///
    /// Compiled out when Odin is present — Odin draws <c>[SerializeReference]</c>
    /// fields with its own, better picker, and two drawers competing for one field
    /// is worse than either.
    /// </remarks>
    // Every polymorphic authored type needs a line here. Without one the field
    // draws as nothing at all — Unity ships no type picker for SerializeReference
    // — and the omission looks like the concrete classes never having been
    // written rather than a missing drawer.
    [CustomPropertyDrawer(typeof(ProjectilePattern), useForChildren: true)]
    [CustomPropertyDrawer(typeof(MovementBehaviour), useForChildren: true)]
    [CustomPropertyDrawer(typeof(PhaseTransition), useForChildren: true)]
    public sealed class VroxReferenceDrawer : PropertyDrawer
    {
        private static readonly Dictionary<Type, Type[]> Cache = new();

        /// <summary>Concrete subclasses of whichever base this field declares.</summary>
        private Type[] Types
        {
            get
            {
                var declared = fieldInfo.FieldType;
                if (Cache.TryGetValue(declared, out var cached))
                {
                    return cached;
                }
                var found = declared.Assembly
                    .GetTypes()
                    .Where(t => declared.IsAssignableFrom(t) && !t.IsAbstract)
                    .OrderBy(t => t.Name)
                    .ToArray();
                Cache[declared] = found;
                return found;
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            return line + EditorGUI.GetPropertyHeight(property, label, includeChildren: true);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight;
            var pickerRect = new Rect(position.x, position.y, position.width, line);

            var types = Types;
            int current = Array.FindIndex(types, t => t.FullName == TypeNameOf(property));
            var names = types.Select(t => ObjectNames.NicifyVariableName(t.Name)).ToArray();

            int picked = EditorGUI.Popup(pickerRect, label.text, current, names);
            if (picked != current && picked >= 0)
            {
                property.managedReferenceValue = Activator.CreateInstance(types[picked]);
                property.serializedObject.ApplyModifiedProperties();
            }

            var bodyRect = new Rect(
                position.x,
                position.y + line + EditorGUIUtility.standardVerticalSpacing,
                position.width,
                position.height - line);

            // Drawn without its own label: the picker above already names it, and
            // repeating it reads as two separate fields.
            EditorGUI.PropertyField(bodyRect, property, GUIContent.none, includeChildren: true);
        }

        /// <summary>
        /// The full type name inside a managed-reference id.
        /// </summary>
        /// <remarks>
        /// The string is "&lt;assembly&gt; &lt;namespace.Type&gt;", and is empty when the
        /// reference is null.
        /// </remarks>
        private static string TypeNameOf(SerializedProperty property)
        {
            string full = property.managedReferenceFullTypename;
            int space = full.IndexOf(' ');
            return space >= 0 ? full[(space + 1)..] : full;
        }
    }
}
#endif
