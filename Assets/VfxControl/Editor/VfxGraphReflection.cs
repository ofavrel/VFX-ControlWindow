// VFX Control — reflection bridge to the VFX Graph's exposed-property model.
//
// The authoritative list of exposed properties (with categories, defaults,
// ranges and enum values) lives in the editor-internal VFXGraph.m_ParameterInfo
// array — the exact same data the stock VisualEffectEditor draws from. Those
// types (VisualEffectResource, VFXGraph, VFXParameterInfo) are `internal` to the
// UnityEditor.VFX assembly, so we reach them through reflection and degrade
// gracefully (everything → "Uncategorized", no defaults) if the package layout
// ever shifts.
//
// Mirrors: Editor/Models/VFXParameterInfo.cs, Editor/Models/VFXGraph.cs and
// Editor/Inspector/VisualEffectEditor.cs (DrawParameters) in
// com.unity.visualeffectgraph.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;

namespace VfxControl.EditorTools
{
    /// One exposed, editable leaf property of a VisualEffectAsset.
    internal sealed class VfxExposedParam
    {
        public string Name;        // unique key / property-sheet entry name (the "path")
        public string Label;       // display label (field name, nicified)
        public string SheetType;   // e.g. "m_Float", "m_Vector4f", "m_NamedObject"
        public string RealType;    // e.g. "Single", "Color", "Texture2D", "AABox"
        public string Category;    // blackboard category, or "" for uncategorized
        public string Tooltip;

        public bool IsStruct;      // compound parent (e.g. AABox) — a label, no value control
        public int Depth;          // nesting level (struct children are deeper)

        public bool Spaceable;     // type carries a coordinate space (Box, ArcCone, …)
        public string Space;       // "World" | "Local" | "None" (display only)

        public bool HasRange;
        public float Min;
        public float Max;

        public List<string> EnumValues; // non-null => render as a dropdown
        public object DefaultValue;      // graph default (boxed), may be null

        public bool IsEnum => EnumValues != null && EnumValues.Count > 0;
    }

    internal static class VfxGraphReflection
    {
        // Cached reflection handles, resolved lazily once.
        static bool s_Resolved;
        static bool s_Available;
        static MethodInfo s_GetResource;       // static VisualEffectResource GetResource(VisualEffectObject)
        static MethodInfo s_GetOrCreateGraph;  // static VFXGraph GetOrCreateGraph(VisualEffectResource)
        static FieldInfo s_ParameterInfoField; // VFXParameterInfo[] VFXGraph.m_ParameterInfo
        static MethodInfo s_BuildParameterInfo; // void VFXGraph.BuildParameterInfo()

        // Field handles on the VFXParameterInfo struct.
        static FieldInfo s_fName, s_fPath, s_fSheetType, s_fRealType, s_fTooltip,
                         s_fMin, s_fMax, s_fEnumValues, s_fDescendantCount, s_fDefaultValue,
                         s_fSpace, s_fSpaceable;
        static MethodInfo s_SerializableGet; // object VFXSerializableObject.Get()

        // Event-block enumeration: VFXBasicEvent.eventName + VFXGraph.children.
        static Type s_BasicEventType;          // UnityEditor.VFX.VFXBasicEvent (a VFXContext)
        static FieldInfo s_fEventName;         // public string VFXBasicEvent.eventName
        static PropertyInfo s_ChildrenProp;    // IEnumerable<VFXModel> VFXModel.children

        /// When true, GetExposedParameters logs each resolution/enumeration step.
        internal static bool Verbose;

        static void Log(string msg)
        {
            if (Verbose) Debug.Log("[VFX Control] " + msg);
        }

        /// One-line summary of which reflection handles resolved (for diagnostics).
        internal static string DescribeBindingState()
        {
            Resolve();
            return $"available={s_Available}, paramInfoType={(s_fSheetType != null)}, " +
                   $"getResource={s_GetResource != null}, getOrCreateGraph={s_GetOrCreateGraph != null}, " +
                   $"paramInfoField={s_ParameterInfoField != null}, buildInfo={s_BuildParameterInfo != null}, " +
                   $"serializableGet={s_SerializableGet != null}";
        }

        static void Resolve()
        {
            if (s_Resolved) return;
            s_Resolved = true;
            try
            {
                var paramInfoType = Type.GetType("UnityEditor.VFX.VFXParameterInfo, Unity.VisualEffectGraph.Editor");
                if (paramInfoType == null)
                {
                    // Fall back to scanning loaded assemblies (assembly name can vary).
                    paramInfoType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("UnityEditor.VFX.VFXParameterInfo"))
                        .FirstOrDefault(t => t != null);
                }
                if (paramInfoType == null) return;

                var asm = paramInfoType.Assembly;
                var graphType = asm.GetType("UnityEditor.VFX.VFXGraph");
                if (graphType == null) return;
                // NOTE: VisualEffectResource is a built-in editor type (not in this
                // package assembly), so we must NOT try to resolve it here and must
                // NOT constrain the GetOrCreateGraph lookup to it — doing so used to
                // make the whole bridge unavailable and return zero properties.

                const BindingFlags pubStatic = BindingFlags.Public | BindingFlags.Static;
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance;

                // GetResource / GetOrCreateGraph are extension methods on
                // VisualEffectResourceExtensions in this assembly; match by name + arity.
                s_GetResource = asm.GetTypes()
                    .SelectMany(t => t.GetMethods(pubStatic))
                    .FirstOrDefault(m => m.Name == "GetResource" &&
                                         m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(VisualEffectAsset)));

                s_GetOrCreateGraph = asm.GetTypes()
                    .SelectMany(t => t.GetMethods(pubStatic))
                    .FirstOrDefault(m => m.Name == "GetOrCreateGraph" &&
                                         m.GetParameters().Length == 1 &&
                                         m.ReturnType == graphType);

                s_ParameterInfoField = graphType.GetField("m_ParameterInfo", any);
                // Use LINQ rather than GetMethod(..., Type.EmptyTypes, ...): the latter
                // throws AmbiguousMatchException when a non-generic and a generic
                // overload share an empty parameter list.
                s_BuildParameterInfo = FindParameterless(graphType, "BuildParameterInfo", any);

                s_fName = paramInfoType.GetField("name", any);
                s_fPath = paramInfoType.GetField("path", any);
                s_fSheetType = paramInfoType.GetField("sheetType", any);
                s_fRealType = paramInfoType.GetField("realType", any);
                s_fTooltip = paramInfoType.GetField("tooltip", any);
                s_fMin = paramInfoType.GetField("min", any);
                s_fMax = paramInfoType.GetField("max", any);
                s_fEnumValues = paramInfoType.GetField("enumValues", any);
                s_fDescendantCount = paramInfoType.GetField("descendantCount", any);
                s_fDefaultValue = paramInfoType.GetField("defaultValue", any);
                s_fSpace = paramInfoType.GetField("space", any);
                s_fSpaceable = paramInfoType.GetField("spaceable", any);

                var serializableType = asm.GetType("UnityEditor.VFX.VFXSerializableObject");
                if (serializableType != null)
                    // VFXSerializableObject has both Get() and Get<T>(); avoid the
                    // ambiguous GetMethod overload and take the non-generic one.
                    s_SerializableGet = FindParameterless(serializableType, "Get", any);

                // Event blocks: VFXBasicEvent.eventName, reachable via the graph's children
                // (mirrors VFXComponentBoard.RecurseGetEventNames). Optional — degrades to no
                // graph events if absent. `children` is a `new`-hidden property, so pick by name.
                s_BasicEventType = asm.GetType("UnityEditor.VFX.VFXBasicEvent");
                s_fEventName = s_BasicEventType?.GetField("eventName", any);
                s_ChildrenProp = graphType.GetProperties(any)
                    .FirstOrDefault(p => p.Name == "children" && p.GetIndexParameters().Length == 0);

                s_Available = s_GetResource != null && s_GetOrCreateGraph != null &&
                              s_ParameterInfoField != null && s_fSheetType != null &&
                              s_fRealType != null && s_fName != null && s_fPath != null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VFX Control] Could not bind to VFX Graph internals; " +
                                 $"properties will be uncategorized. ({e.Message})");
                s_Available = false;
            }
        }

        // A non-generic, parameterless method by name — tolerant of overloads that
        // would make Type.GetMethod throw AmbiguousMatchException.
        static MethodInfo FindParameterless(Type type, string name, BindingFlags flags)
        {
            return type.GetMethods(flags)
                       .FirstOrDefault(m => m.Name == name &&
                                            !m.IsGenericMethodDefinition &&
                                            m.GetParameters().Length == 0);
        }

        /// Enumerate the exposed leaf properties of an asset, in graph order, with
        /// their categories. Returns an empty list if the asset is null or the
        /// graph internals can't be reached.
        public static List<VfxExposedParam> GetExposedParameters(VisualEffectAsset asset, bool forceRebuild = false)
        {
            var result = new List<VfxExposedParam>();
            if (asset == null) { Log("asset is null"); return result; }

            Resolve();
            Log($"binding: {DescribeBindingState()}");
            if (!s_Available) { Log("bridge unavailable — returning empty"); return result; }

            try
            {
                var resource = s_GetResource.Invoke(null, new object[] { asset });
                Log($"resource = {(resource == null ? "null" : resource.GetType().Name)}");
                if (resource == null) return result; // e.g. asset inside an AssetBundle
                var graph = s_GetOrCreateGraph.Invoke(null, new[] { resource });
                Log($"graph = {(graph == null ? "null" : graph.GetType().Name)}");
                if (graph == null) return result;

                var infos = s_ParameterInfoField.GetValue(graph) as Array;
                Log($"m_ParameterInfo length (initial) = {(infos == null ? -1 : infos.Length)}");
                // Rebuild the cached info when it's missing/empty, or when the caller
                // forces it (e.g. the asset was just recompiled and may have new
                // properties/categories the stale array doesn't reflect).
                if (forceRebuild || infos == null || infos.Length == 0)
                {
                    if (s_BuildParameterInfo != null)
                    {
                        s_BuildParameterInfo.Invoke(graph, null);
                        infos = s_ParameterInfoField.GetValue(graph) as Array;
                        Log($"m_ParameterInfo length (after build) = {(infos == null ? -1 : infos.Length)}");
                    }
                }
                if (infos == null) return result;

                // Walk the flattened array tracking a descendant-count stack to recover
                // nesting depth (the same bookkeeping VisualEffectEditor.DrawParameters
                // uses). Category headers set the current category; compound parents
                // (e.g. AABox) become struct labels; leaves become editable rows.
                string currentCategory = "";
                var stack = new List<int>();
                int currentCount = infos.Length;

                foreach (var info in infos)
                {
                    int depth = stack.Count; // computed before this entry pushes its own children

                    --currentCount;
                    int descendantCount = s_fDescendantCount != null ? Convert.ToInt32(s_fDescendantCount.GetValue(info)) : 0;
                    if (descendantCount > 0) { stack.Add(currentCount); currentCount = descendantCount; }
                    while (currentCount == 0 && stack.Count > 0) { currentCount = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); }

                    string sheetType = s_fSheetType.GetValue(info) as string;
                    string realType = s_fRealType.GetValue(info) as string;
                    string name = s_fName.GetValue(info) as string;
                    string tooltip = s_fTooltip?.GetValue(info) as string;
                    bool spaceable = s_fSpaceable != null && s_fSpaceable.GetValue(info) is bool sb && sb;
                    string space = s_fSpace?.GetValue(info)?.ToString() ?? "None";
                    Log($"  d{depth} name='{name}' sheetType='{sheetType}' realType='{realType}' desc={descendantCount} space={(spaceable ? space : "-")}");

                    bool isLeaf = !string.IsNullOrEmpty(sheetType);
                    if (!isLeaf)
                    {
                        if (string.IsNullOrEmpty(name))
                            continue;
                        if (string.IsNullOrEmpty(realType)) // category header
                        {
                            currentCategory = name;
                            continue;
                        }
                        if (descendantCount > 0) // compound parent (struct), e.g. AABox
                        {
                            result.Add(new VfxExposedParam
                            {
                                Name = name,
                                Label = depth > 0 ? ObjectNames.NicifyVariableName(name) : name,
                                RealType = realType,
                                Category = currentCategory,
                                Tooltip = tooltip,
                                IsStruct = true,
                                Depth = depth,
                                Spaceable = spaceable,
                                Space = space,
                            });
                        }
                        continue;
                    }

                    var p = new VfxExposedParam
                    {
                        Name = (s_fPath.GetValue(info) as string) ?? name,
                        Label = depth > 0 ? ObjectNames.NicifyVariableName(name) : name,
                        SheetType = sheetType,
                        RealType = realType ?? "",
                        Category = currentCategory,
                        Tooltip = tooltip,
                        Depth = depth,
                        Spaceable = spaceable,
                        Space = space,
                        EnumValues = (s_fEnumValues?.GetValue(info) as IEnumerable<string>)?.ToList(),
                    };

                    if (s_fMin != null && s_fMax != null)
                    {
                        float min = Convert.ToSingle(s_fMin.GetValue(info));
                        float max = Convert.ToSingle(s_fMax.GetValue(info));
                        p.HasRange = !float.IsInfinity(min) && !float.IsInfinity(max) && max > min;
                        p.Min = min;
                        p.Max = max;
                    }

                    if (s_fDefaultValue != null && s_SerializableGet != null)
                    {
                        var serializable = s_fDefaultValue.GetValue(info);
                        if (serializable != null)
                        {
                            try { p.DefaultValue = s_SerializableGet.Invoke(serializable, null); }
                            catch { /* default stays null — control falls back to type default */ }
                        }
                    }

                    result.Add(p);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VFX Control] Failed to read exposed properties: {e.Message}");
                result.Clear();
            }

            return result;
        }

        /// The custom event names declared by Event blocks (VFXBasicEvent) in the asset's graph,
        /// in graph order, distinct. Does NOT include the built-in OnPlay/OnStop (the caller adds
        /// those) and does NOT recurse subgraphs yet. Empty if the asset is null or unreachable.
        public static List<string> GetEventNames(VisualEffectAsset asset)
        {
            var result = new List<string>();
            if (asset == null) return result;

            Resolve();
            if (!s_Available || s_BasicEventType == null || s_fEventName == null || s_ChildrenProp == null)
                return result;

            try
            {
                var resource = s_GetResource.Invoke(null, new object[] { asset });
                if (resource == null) return result;
                var graph = s_GetOrCreateGraph.Invoke(null, new[] { resource });
                if (graph == null) return result;

                if (s_ChildrenProp.GetValue(graph) is IEnumerable children)
                {
                    foreach (var child in children)
                    {
                        if (child == null || !s_BasicEventType.IsInstanceOfType(child)) continue;
                        var name = s_fEventName.GetValue(child) as string;
                        if (!string.IsNullOrEmpty(name) && !result.Contains(name))
                            result.Add(name);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VFX Control] Failed to read event names: {e.Message}");
            }

            return result;
        }
    }
}
