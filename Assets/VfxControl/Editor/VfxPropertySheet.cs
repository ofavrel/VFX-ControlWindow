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

using UnityEditor;
using UnityEngine;

namespace VfxControl.EditorTools
{
    internal static class VfxPropertySheet
    {
        static string ArrayPath(VfxExposedParam p) => $"m_PropertySheet.{p.SheetType}.m_Array";

        /// The serialized array element whose m_Name matches the property, or null
        /// if this property has never been touched on the component.
        public static SerializedProperty FindEntry(SerializedObject so, VfxExposedParam p)
        {
            var array = so.FindProperty(ArrayPath(p));
            if (array == null || !array.isArray) return null;
            for (int i = 0; i < array.arraySize; i++)
            {
                var element = array.GetArrayElementAtIndex(i);
                var nameProp = element.FindPropertyRelative("m_Name");
                if (nameProp != null && nameProp.stringValue == p.Name)
                    return element;
            }
            return null;
        }

        public static bool IsOverridden(SerializedObject so, VfxExposedParam p)
        {
            var entry = FindEntry(so, p);
            var overridden = entry?.FindPropertyRelative("m_Overridden");
            return overridden != null && overridden.boolValue;
        }

        /// Current effective value: the override if present, else the graph default.
        public static object GetValue(SerializedObject so, VfxExposedParam p)
        {
            var entry = FindEntry(so, p);
            if (entry != null)
            {
                var valueProp = entry.FindPropertyRelative("m_Value");
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
                entry.FindPropertyRelative("m_Name").stringValue = p.Name;
            }

            var valueProp = entry.FindPropertyRelative("m_Value");
            if (valueProp != null)
                WriteValue(valueProp, value);
            entry.FindPropertyRelative("m_Overridden").boolValue = true;

            so.ApplyModifiedProperties();
        }

        /// Clear the override so the property reverts to the graph default.
        public static void Reset(SerializedObject so, VfxExposedParam p)
        {
            so.Update();
            var entry = FindEntry(so, p);
            if (entry == null) return;

            var overridden = entry.FindPropertyRelative("m_Overridden");
            if (overridden != null) overridden.boolValue = false;

            // Re-seat the stored value to the graph default so a later toggle-on
            // doesn't resurrect a stale override value.
            if (p.DefaultValue != null)
            {
                var valueProp = entry.FindPropertyRelative("m_Value");
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

        static object ReadValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Float: return prop.floatValue;
                case SerializedPropertyType.Integer: return prop.longValue;
                case SerializedPropertyType.Boolean: return prop.boolValue;
                case SerializedPropertyType.Vector2: return prop.vector2Value;
                case SerializedPropertyType.Vector3: return prop.vector3Value;
                case SerializedPropertyType.Vector4: return prop.vector4Value;
                case SerializedPropertyType.Color: return prop.colorValue;
                case SerializedPropertyType.ObjectReference: return prop.objectReferenceValue;
                case SerializedPropertyType.Gradient: return prop.gradientValue;
                case SerializedPropertyType.AnimationCurve: return prop.animationCurveValue;
                default: return null;
            }
        }

        static void WriteValue(SerializedProperty prop, object value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Float:
                    prop.floatValue = System.Convert.ToSingle(value);
                    break;
                case SerializedPropertyType.Integer:
                    if (value is uint u) prop.longValue = u;
                    else prop.longValue = System.Convert.ToInt64(value);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = (bool)value;
                    break;
                case SerializedPropertyType.Vector2:
                    prop.vector2Value = (Vector2)value;
                    break;
                case SerializedPropertyType.Vector3:
                    prop.vector3Value = (Vector3)value;
                    break;
                case SerializedPropertyType.Vector4:
                    prop.vector4Value = value is Color c ? (Vector4)c : (Vector4)value;
                    break;
                case SerializedPropertyType.Color:
                    prop.colorValue = (Color)value;
                    break;
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = value as Object;
                    break;
                case SerializedPropertyType.Gradient:
                    prop.gradientValue = (Gradient)value;
                    break;
                case SerializedPropertyType.AnimationCurve:
                    prop.animationCurveValue = (AnimationCurve)value;
                    break;
            }
        }
    }
}
