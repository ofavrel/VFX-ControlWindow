// VFX Control — read/write helpers for the VisualEffect component's serialized
// override sheet (m_PropertySheet).
//
// Every exposed property maps to an entry in m_PropertySheet.<sheetType>.m_Array,
// where each element is { m_Name : string, m_Value : <typed>, m_Overridden : bool }.
// An entry is "modified" when it exists AND m_Overridden == true; otherwise the
// runtime falls back to the graph default baked in the VisualEffectAsset.
//
// Going through SerializedObject (rather than the runtime Get*/Set* API) is what
// makes Undo, prefab overrides and multi-edit work — exactly how the stock
// VisualEffectEditor does it. The per-type value read/write mirrors
// VisualEffectEditor.GetObjectValue / SetObjectValue, keyed on propertyType.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VfxControl.EditorTools
{
    internal static class VfxPropertySheet
    {
        // The serialized field names of one m_PropertySheet.<sheetType>.m_Array element.
        private const string NameField = "m_Name";
        private const string ValueField = "m_Value";
        private const string OverriddenField = "m_Overridden";

        private static string ArrayPath(VfxExposedParam p) => $"m_PropertySheet.{p.SheetType}.m_Array";

        /// The serialized array element whose m_Name matches the property, or null
        /// if this property has never been touched on the component.
        public static SerializedProperty FindEntry(SerializedObject so, VfxExposedParam p)
        {
            var array = so.FindProperty(ArrayPath(p));
            if (array == null || !array.isArray) return null;
            for (int i = 0; i < array.arraySize; i++)
            {
                var element = array.GetArrayElementAtIndex(i);
                var nameProp = element.FindPropertyRelative(NameField);
                if (nameProp != null && nameProp.stringValue == p.Name)
                    return element;
            }
            return null;
        }

        public static bool IsOverridden(SerializedObject so, VfxExposedParam p)
        {
            var entry = FindEntry(so, p);
            var overridden = entry?.FindPropertyRelative(OverriddenField);
            return overridden != null && overridden.boolValue;
        }

        /// Current effective value: the override if present, else the graph default.
        public static object GetValue(SerializedObject so, VfxExposedParam p)
        {
            var entry = FindEntry(so, p);
            if (entry != null)
            {
                var valueProp = entry.FindPropertyRelative(ValueField);
                if (valueProp != null)
                    return ReadValue(valueProp);
            }
            return p.DefaultValue;
        }

        /// Write a value as an override, creating the entry if needed, and flag it
        /// overridden. Records Undo on the target object(s).
        public static void SetValue(SerializedObject so, VfxExposedParam p, object value)
        {
            so.Update();
            var array = so.FindProperty(ArrayPath(p));
            if (array == null || !array.isArray) return;

            var entry = FindEntry(so, p);
            if (entry == null)
            {
                int index = array.arraySize;
                array.InsertArrayElementAtIndex(index);
                entry = array.GetArrayElementAtIndex(index);
                entry.FindPropertyRelative(NameField).stringValue = p.Name;
            }

            var valueProp = entry.FindPropertyRelative(ValueField);
            if (valueProp != null)
                WriteValue(valueProp, value);
            entry.FindPropertyRelative(OverriddenField).boolValue = true;

            so.ApplyModifiedProperties();
        }

        /// Clear the override so the property reverts to the graph default.
        public static void Reset(SerializedObject so, VfxExposedParam p)
        {
            so.Update();
            var entry = FindEntry(so, p);
            if (entry == null) return;

            var overridden = entry.FindPropertyRelative(OverriddenField);
            if (overridden != null) overridden.boolValue = false;

            // Re-seat the stored value to the graph default so a later toggle-on
            // doesn't resurrect a stale override value.
            if (p.DefaultValue != null)
            {
                var valueProp = entry.FindPropertyRelative(ValueField);
                if (valueProp != null) WriteValue(valueProp, p.DefaultValue);
            }
            so.ApplyModifiedProperties();
        }

        /// True if any exposed property is currently overridden.
        public static int CountModified(SerializedObject so, System.Collections.Generic.IEnumerable<VfxExposedParam> ps)
        {
            int n = 0;
            foreach (var p in ps)
                if (IsOverridden(so, p)) n++;
            return n;
        }

        // --- per-type value bridge (mirrors VisualEffectEditor.Get/SetObjectValue) ---
        //
        // Each supported SerializedPropertyType is described once — its read + write — so adding
        // a type is a single entry. Unlisted types read as null and ignore writes.
        private static readonly Dictionary<SerializedPropertyType,
            (Func<SerializedProperty, object> Read, Action<SerializedProperty, object> Write)> s_TypeBridge = new()
        {
            { SerializedPropertyType.Float,           (p => p.floatValue,           (p, v) => p.floatValue = Convert.ToSingle(v)) },
            // uint round-trips through longValue (it overflows a signed int as a negative).
            { SerializedPropertyType.Integer,         (p => p.longValue,            (p, v) => p.longValue = v is uint u ? u : Convert.ToInt64(v)) },
            { SerializedPropertyType.Boolean,         (p => p.boolValue,            (p, v) => p.boolValue = (bool)v) },
            { SerializedPropertyType.Vector2,         (p => p.vector2Value,         (p, v) => p.vector2Value = (Vector2)v) },
            { SerializedPropertyType.Vector3,         (p => p.vector3Value,         (p, v) => p.vector3Value = (Vector3)v) },
            // Color is stored in a Vector4f sheet entry, so a Color value writes through as a Vector4.
            { SerializedPropertyType.Vector4,         (p => p.vector4Value,         (p, v) => p.vector4Value = v is Color c ? (Vector4)c : (Vector4)v) },
            { SerializedPropertyType.Color,           (p => p.colorValue,           (p, v) => p.colorValue = (Color)v) },
            { SerializedPropertyType.ObjectReference, (p => p.objectReferenceValue, (p, v) => p.objectReferenceValue = v as UnityEngine.Object) },
            { SerializedPropertyType.Gradient,        (p => p.gradientValue,        (p, v) => p.gradientValue = (Gradient)v) },
            { SerializedPropertyType.AnimationCurve,  (p => p.animationCurveValue,  (p, v) => p.animationCurveValue = (AnimationCurve)v) },
        };

        private static object ReadValue(SerializedProperty prop) =>
            s_TypeBridge.TryGetValue(prop.propertyType, out var b) ? b.Read(prop) : null;

        private static void WriteValue(SerializedProperty prop, object value)
        {
            if (s_TypeBridge.TryGetValue(prop.propertyType, out var b)) b.Write(prop, value);
        }
    }
}
