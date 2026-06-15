// VFX Control — reflection bridge to UnityEditor.Clipboard (internal).
//
// The editor's Clipboard stores typed values in the system buffer using the same
// format the Inspector's right-click Copy/Paste uses, so values round-trip between
// this window and the Inspector. The class is internal, so we reach it by reflection
// and degrade to no-ops if a member isn't present on the running Unity version.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VfxControl.EditorTools
{
    internal static class VfxClipboard
    {
        private static readonly Type s_Type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEditor.Clipboard"))
            .FirstOrDefault(t => t != null);

        private static readonly Dictionary<string, PropertyInfo> s_Props = new Dictionary<string, PropertyInfo>();

        private static PropertyInfo Prop(string name)
        {
            if (s_Type == null) return null;
            if (!s_Props.TryGetValue(name, out var p))
                s_Props[name] = p = s_Type.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return p;
        }

        public static bool Has(string hasProp) => Prop(hasProp)?.GetValue(null) is bool b && b;

        public static object Get(string valueProp) => Prop(valueProp)?.GetValue(null);

        public static void Set(string valueProp, object value)
        {
            var p = Prop(valueProp);
            if (p != null && p.CanWrite) p.SetValue(null, value);
        }
    }
}
