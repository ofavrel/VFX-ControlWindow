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
        // ============================ VFX Graph package contract ============================
        // Everything below is the (internal) surface this bridge depends on by reflection. On a
        // VFX Graph package update, audit THIS region: the namespace/assembly, the type short-name
        // T_* consts, and the `s_*` handle declarations whose comments name each member + signature.
        // s_PackageVersion (vs AuthoredAgainstVersion) is logged on a binding failure to flag drift.
        private const string VfxNs = "UnityEditor.VFX.";          // namespace of every editor-internal VFX type
        private const string VfxAsm = "Unity.VisualEffectGraph.Editor"; // assembly they live in
        private const string T_ParameterInfo = "VFXParameterInfo"; // resolved both assembly-qualified + by scan
        private const string AuthoredAgainstVersion = "17.6.0";   // VFX Graph version this contract was written for
        private static string s_PackageVersion;                   // actual installed version (for diagnostics)

        // Cached reflection handles, resolved lazily once.
        private static bool s_Resolved;
        private static bool s_Available;
        private static MethodInfo s_GetResource;       // static VisualEffectResource GetResource(VisualEffectObject)
        private static MethodInfo s_GetOrCreateGraph;  // static VFXGraph GetOrCreateGraph(VisualEffectResource)
        private static FieldInfo s_ParameterInfoField; // VFXParameterInfo[] VFXGraph.m_ParameterInfo
        private static MethodInfo s_BuildParameterInfo; // void VFXGraph.BuildParameterInfo()

        // Field handles on the VFXParameterInfo struct.
        private static FieldInfo s_fName, s_fPath, s_fSheetType, s_fRealType, s_fTooltip,
                         s_fMin, s_fMax, s_fEnumValues, s_fDescendantCount, s_fDefaultValue,
                         s_fSpace, s_fSpaceable;

        private static MethodInfo s_SerializableGet; // object VFXSerializableObject.Get()

        // Event-block enumeration: VFXBasicEvent.eventName + VFXGraph.children.
        private static Type s_BasicEventType;          // UnityEditor.VFX.VFXBasicEvent (a VFXContext)
        private static FieldInfo s_fEventName;         // public string VFXBasicEvent.eventName
        private static PropertyInfo s_ChildrenProp;    // IEnumerable<VFXModel> VFXModel.children

        // Blackboard custom attributes: VFXGraph.customAttributes (VFXCustomAttributeDescriptor[]),
        // each with attributeName + type (CustomAttributeUtility.Signature, 0..6 = Float..Int).
        private static PropertyInfo s_CustomAttrsProp;

        // --- Debug-tab profiling extras (all optional; degrade to no data) ---
        // Attribute layout → per-system buffer size: VFXContext.GetData() → VFXDataParticle,
        // .GetCurrentAttributeLayout() → StructureOfArrayProvider.BucketInfo[] (each .size in dwords).
        // System name via VFXGraph.systemNames.GetUniqueSystemName(VFXData).
        private static Type s_ContextType, s_DataParticleType;
        private static MethodInfo s_GetData;             // VFXData VFXContext.GetData()
        private static MethodInfo s_GetCurrentLayout;    // BucketInfo[] VFXDataParticle.GetCurrentAttributeLayout()
        private static PropertyInfo s_DataSpaceProp;     // VFXSpace VFXDataParticle.space (None/Local/World)
        private static FieldInfo s_BucketSizeField;      // int BucketInfo.size (resolved from the returned element type)
        private static FieldInfo s_BucketAttribsField;   // VFXAttribute[] BucketInfo.attributes (per dword channel)
        private static FieldInfo s_AttrNameField;        // string VFXAttribute.name
        private static PropertyInfo s_AttrTypeProp;      // VFXValueType VFXAttribute.type
        private static PropertyInfo s_SystemNamesProp;   // VFXSystemNames VFXGraph.systemNames

        private static MethodInfo s_GetUniqueSystemName; // string VFXSystemNames.GetUniqueSystemName(VFXData)
        // Texture usage: walk slot containers' inputSlots + sub-slots, read VFXSlot.value as Texture
        // (slot members resolved by name per object via GetProp — no cached handles needed).
        // Component profiler markers (internal instance methods on the runtime VisualEffect).
        private static MethodInfo s_CpuEffectMarker;     // string GetCPUEffectMarkerName(VFXCPUEffectMarkers)
        private static object s_CpuEffectMarkerArg;      // VisualEffect.VFXCPUEffectMarkers.FullUpdate
        private static MethodInfo s_CpuSystemMarker;     // string GetCPUSystemMarkerName(string)

        private static MethodInfo s_GpuTaskMarker;       // string GetGPUTaskMarkerName(string, int)
        // Per-system CPU/GPU markers are only emitted while the component is registered for profiling.
        private static MethodInfo s_Register, s_Unregister, s_IsRegistered;

        /// When true, GetExposedParameters logs each resolution/enumeration step.
        internal static bool Verbose;

        private static void Log(string msg)
        {
            if (Verbose) Debug.Log("[VFX Control] " + msg);
        }

        /// One-line summary of which reflection handles resolved (for diagnostics) — the core path
        /// plus the optional/debug handles, so *Diagnose Target* shows exactly what degraded and
        /// against which package version.
        internal static string DescribeBindingState()
        {
            Resolve();
            return $"vfxPackage=({VersionNote()}), available={s_Available}, " +
                   // core path (s_Available gates on these)
                   $"getResource={s_GetResource != null}, getOrCreateGraph={s_GetOrCreateGraph != null}, " +
                   $"paramInfoField={s_ParameterInfoField != null}, paramInfoFields={(s_fSheetType != null)}, " +
                   $"buildInfo={s_BuildParameterInfo != null}, serializableGet={s_SerializableGet != null}, " +
                   // optional: events + blackboard custom attributes
                   $"basicEventType={s_BasicEventType != null}, eventName={s_fEventName != null}, " +
                   $"childrenProp={s_ChildrenProp != null}, customAttrsProp={s_CustomAttrsProp != null}, " +
                   // optional: debug-tab attribute layout + profiling markers
                   $"systemNames={s_SystemNamesProp != null}, getData={s_GetData != null}, " +
                   $"attrLayout={s_GetCurrentLayout != null}, dataSpace={s_DataSpaceProp != null}, " +
                   $"uniqueSystemName={s_GetUniqueSystemName != null}, " +
                   $"cpuMarker={s_CpuEffectMarker != null}, gpuMarker={s_GpuTaskMarker != null}, " +
                   $"profilingReg={s_Register != null}";
        }

        private static void Resolve()
        {
            if (s_Resolved) return;
            s_Resolved = true;
            try
            {
                var paramInfoType = Type.GetType($"{VfxNs}{T_ParameterInfo}, {VfxAsm}");
                if (paramInfoType == null)
                {
                    // Fall back to scanning loaded assemblies (assembly name can vary).
                    paramInfoType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType(VfxNs + T_ParameterInfo))
                        .FirstOrDefault(t => t != null);
                }
                if (paramInfoType == null) return;

                var asm = paramInfoType.Assembly;
                s_PackageVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(asm)?.version;
                Type VfxType(string shortName) => asm.GetType(VfxNs + shortName); // all VFX editor types share the namespace
                var graphType = VfxType("VFXGraph");
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

                var serializableType = VfxType("VFXSerializableObject");
                if (serializableType != null)
                    // VFXSerializableObject has both Get() and Get<T>(); avoid the
                    // ambiguous GetMethod overload and take the non-generic one.
                    s_SerializableGet = FindParameterless(serializableType, "Get", any);

                // Event blocks: VFXBasicEvent.eventName, reachable via the graph's children
                // (mirrors VFXComponentBoard.RecurseGetEventNames). Optional — degrades to no
                // graph events if absent. `children` is a `new`-hidden property, so pick by name.
                s_BasicEventType = VfxType("VFXBasicEvent");
                s_fEventName = s_BasicEventType?.GetField("eventName", any);
                s_ChildrenProp = graphType.GetProperties(any)
                    .FirstOrDefault(p => p.Name == "children" && p.GetIndexParameters().Length == 0);

                s_CustomAttrsProp = graphType.GetProperty("customAttributes", any);

                // Debug-tab extras — optional; resolution failures just disable that one readout.
                s_SystemNamesProp = graphType.GetProperty("systemNames", any);
                s_ContextType = VfxType("VFXContext");
                s_GetData = s_ContextType != null ? FindParameterless(s_ContextType, "GetData", any) : null;
                s_DataParticleType = VfxType("VFXDataParticle");
                s_GetCurrentLayout = s_DataParticleType?.GetMethods(any)
                    .FirstOrDefault(m => m.Name == "GetCurrentAttributeLayout" && m.GetParameters().Length == 0);
                s_DataSpaceProp = s_DataParticleType?.GetProperty("space", any); // VFXSpace (None/Local/World)
                var sysNamesType = VfxType("VFXSystemNames");
                s_GetUniqueSystemName = sysNamesType?.GetMethods(any)
                    .FirstOrDefault(m => m.Name == "GetUniqueSystemName" && m.GetParameters().Length == 1);

                // Component profiler markers on the runtime VisualEffect (internal instance methods).
                // NOTE: each has TWO overloads — a private (Int32 nameID, …) and the (string systemName, …)
                // one we want. Pin the parameter types exactly, or FirstOrDefault may grab the int overload
                // and Invoke(...) with a string throws → empty marker → "—".
                var veType = typeof(VisualEffect);
                // GetCPUEffectMarkerName has (Int32) and (VFXCPUEffectMarkers) overloads — take the enum
                // one and read FullUpdate (whole-effect CPU) from its parameter type.
                s_CpuEffectMarker = veType.GetMethods(any).FirstOrDefault(m =>
                    m.Name == "GetCPUEffectMarkerName" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsEnum);
                var cpuMarkerEnum = s_CpuEffectMarker?.GetParameters()[0].ParameterType;
                if (cpuMarkerEnum != null)
                    try { s_CpuEffectMarkerArg = Enum.Parse(cpuMarkerEnum, "FullUpdate"); } catch (Exception e) { if (Verbose) Debug.LogException(e); }
                s_CpuSystemMarker = veType.GetMethods(any).FirstOrDefault(m =>
                    m.Name == "GetCPUSystemMarkerName" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(string));
                s_GpuTaskMarker = veType.GetMethods(any).FirstOrDefault(m =>
                    m.Name == "GetGPUTaskMarkerName" && m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(string));
                s_Register = FindParameterless(veType, "RegisterForProfiling", any);
                s_Unregister = FindParameterless(veType, "UnregisterForProfiling", any);
                s_IsRegistered = FindParameterless(veType, "IsRegisteredForProfiling", any);

                s_Available = s_GetResource != null && s_GetOrCreateGraph != null &&
                              s_ParameterInfoField != null && s_fSheetType != null &&
                              s_fRealType != null && s_fName != null && s_fPath != null;

                // A core handle missing without an exception means the package layout drifted —
                // warn (with the version pair) so the blank Properties tab is diagnosable.
                if (!s_Available)
                    Debug.LogWarning($"[VFX Control] VFX Graph internals only partially resolved " +
                                     $"({VersionNote()}); properties may be uncategorized. {DescribeBindingState()}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VFX Control] Could not bind to VFX Graph internals " +
                                 $"({VersionNote()}); properties will be uncategorized. ({e.Message})");
                if (Verbose) Debug.LogException(e); // full stack when diagnosing a package break
                s_Available = false;
            }
        }

        // "installed 17.6.0, authored 17.6.0" — surfaces a package-version mismatch in warnings/diag.
        private static string VersionNote() => $"installed {s_PackageVersion ?? "?"}, authored {AuthoredAgainstVersion}";

        // A non-generic, parameterless method by name — tolerant of overloads that
        // would make Type.GetMethod throw AmbiguousMatchException.
        private static MethodInfo FindParameterless(Type type, string name, BindingFlags flags)
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
                if (Verbose) Debug.LogException(e); // full stack when diagnosing a package break
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
                if (Verbose) Debug.LogException(e); // full stack when diagnosing a package break
            }

            return result;
        }

        /// The custom attributes declared in the asset's graph (the blackboard's "Custom Attributes"),
        /// as (name, typeIndex) where typeIndex is the `CustomAttributeUtility.Signature` ordinal
        /// (0=Float,1=Vector2,2=Vector3,3=Vector4,4=Bool,5=Uint,6=Int). Empty if unreachable.
        public static List<(string name, int type)> GetCustomAttributes(VisualEffectAsset asset)
        {
            var result = new List<(string, int)>();
            if (asset == null) return result;

            Resolve();
            if (!s_Available || s_CustomAttrsProp == null) return result;

            try
            {
                var resource = s_GetResource.Invoke(null, new object[] { asset });
                if (resource == null) return result;
                var graph = s_GetOrCreateGraph.Invoke(null, new[] { resource });
                if (graph == null) return result;

                if (s_CustomAttrsProp.GetValue(graph) is IEnumerable items)
                {
                    foreach (var d in items)
                    {
                        if (d == null) continue;
                        var name = ReadMember(d, "attributeName", "m_AttributeName") as string;
                        var typeObj = ReadMember(d, "type", "m_Type");
                        if (string.IsNullOrEmpty(name) || typeObj == null) continue;
                        if (!result.Exists(x => x.Item1 == name)) result.Add((name, SignatureIndex(typeObj)));
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VFX Control] Failed to read custom attributes: {e.Message}");
                if (Verbose) Debug.LogException(e); // full stack when diagnosing a package break
            }

            return result;
        }

        // CustomAttributeUtility.Signature (boxed) → our payload type index (Float=0 … Int=6).
        // Mapped by enum NAME, not ordinal, so reordering/inserting Signature members in a future
        // package can't silently mistype an attribute (mirrors GetSystemSpaces' by-name space map).
        private static int SignatureIndex(object signature)
        {
            switch (signature.ToString())
            {
                case "Float":   return 0;
                case "Vector2": return 1;
                case "Vector3": return 2;
                case "Vector4": return 3;
                case "Bool":    return 4;
                case "Uint":    return 5;
                case "Int":     return 6;
                default:
                    Log($"unknown custom-attribute Signature '{signature}' — defaulting to Float");
                    return 0;
            }
        }

        // Read a member by property name (preferred) or serialized field name (fallback).
        private static object ReadMember(object obj, string prop, string field)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = obj.GetType();
            var p = t.GetProperty(prop, any);
            if (p != null) return p.GetValue(obj);
            var f = t.GetField(field, any);
            return f?.GetValue(obj);
        }

        // ---- Debug-tab profiling helpers ---------------------------------------------------

        private const BindingFlags InstanceAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static bool TryGetGraph(VisualEffectAsset asset, out object graph)
        {
            graph = null;
            var resource = s_GetResource.Invoke(null, new object[] { asset });
            if (resource == null) return false;
            graph = s_GetOrCreateGraph.Invoke(null, new[] { resource });
            return graph != null;
        }

        // A parameterless instance property by name (first match — tolerant of `new`-hidden overrides
        // like VFXModel.children / VFXGraph.children).
        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var p = obj.GetType().GetProperties(InstanceAny)
                    .FirstOrDefault(x => x.Name == name && x.GetIndexParameters().Length == 0);
                return p?.GetValue(obj);
            }
            catch { return null; }
        }

        /// Every distinct Texture wired into the asset's graph — exposed or not — by walking each
        /// node's (and its blocks') input slots and reading the slot value (mirrors the package
        /// profiler's CollectAllTextureSlotsRecursive). Empty if the graph can't be reached.
        public static List<Texture> GetTextureUsage(VisualEffectAsset asset)
        {
            var result = new List<Texture>();
            if (asset == null) return result;
            Resolve();
            if (!s_Available || s_ChildrenProp == null) return result;

            try
            {
                if (!TryGetGraph(asset, out var graph)) return result;
                var seen = new HashSet<Texture>();
                if (s_ChildrenProp.GetValue(graph) is IEnumerable children)
                    foreach (var child in children)
                    {
                        ScanContainer(child, result, seen);
                        if (GetProp(child, "children") is IEnumerable blocks) // a context's blocks
                            foreach (var block in blocks) ScanContainer(block, result, seen);
                    }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VFX Control] Failed to read texture usage: {e.Message}");
                if (Verbose) Debug.LogException(e); // full stack when diagnosing a package break
            }
            return result;
        }

        private static void ScanContainer(object container, List<Texture> result, HashSet<Texture> seen)
        {
            if (GetProp(container, "inputSlots") is IEnumerable slots)
                foreach (var slot in slots) ScanSlot(slot, result, seen);
        }

        private static void ScanSlot(object slot, List<Texture> result, HashSet<Texture> seen)
        {
            if (slot == null) return;
            if (GetProp(slot, "value") is Texture tex && tex != null && seen.Add(tex))
                result.Add(tex);
            if (GetProp(slot, "children") is IEnumerable subs) // compound slots (sub-slots)
                foreach (var s in subs) ScanSlot(s, result, seen);
        }

        /// Per-system attribute-buffer stride in 32-bit words (Σ of the current attribute layout's
        /// bucket sizes), keyed by unique system name. Bytes = words × capacity × 4. Mirrors the VFX
        /// inspector's "Current Attribute Layout" (VFXDataParticle.GetCurrentAttributeLayout). The
        /// layout is [NonSerialized] — populated when the graph compiles — so a system is OMITTED
        /// (→ caller shows "—") until the graph has been compiled/opened this session.
        // Enumerate each particle system's (buckets, uniqueName) once from the graph's current
        // attribute layout — the shared traversal behind GetSystemAttributeWords/Layout. Walks the
        // graph's children, keeps the particle-data contexts (deduped — contexts share one data per
        // system), yields each system's BucketInfo[] and resolved name. Yields nothing (degrades
        // silently) when the reflection handles or graph aren't available, or a system has no name.
        private static IEnumerable<(Array buckets, string name)> EnumerateSystemLayouts(VisualEffectAsset asset)
        {
            if (asset == null) yield break;
            Resolve();
            if (!s_Available || s_ChildrenProp == null || s_ContextType == null ||
                s_GetData == null || s_GetCurrentLayout == null) yield break;
            if (!TryGetGraph(asset, out var graph)) yield break;
            if (!(s_ChildrenProp.GetValue(graph) is IEnumerable children)) yield break;

            var systemNames = s_SystemNamesProp?.GetValue(graph);
            var seen = new HashSet<object>();
            foreach (var child in children)
            {
                if (child == null || !s_ContextType.IsInstanceOfType(child)) continue;
                object data;
                try { data = s_GetData.Invoke(child, null); } catch { continue; }
                if (data == null) continue;
                if (s_DataParticleType != null && !s_DataParticleType.IsInstanceOfType(data)) continue;
                if (!seen.Add(data)) continue; // contexts share one data per system

                if (!(s_GetCurrentLayout.Invoke(data, null) is Array buckets) || buckets.Length == 0) continue;

                string name = null;
                if (systemNames != null && s_GetUniqueSystemName != null)
                    try { name = s_GetUniqueSystemName.Invoke(systemNames, new[] { data }) as string; } catch (Exception e) { if (Verbose) Debug.LogException(e); }
                if (!string.IsNullOrEmpty(name)) yield return (buckets, name);
            }
        }

        public static Dictionary<string, int> GetSystemAttributeWords(VisualEffectAsset asset)
        {
            var result = new Dictionary<string, int>();
            try
            {
                foreach (var (buckets, name) in EnumerateSystemLayouts(asset))
                {
                    if (s_BucketSizeField == null)
                        s_BucketSizeField = buckets.GetType().GetElementType()?.GetField("size", InstanceAny);
                    if (s_BucketSizeField == null) continue;

                    int words = 0;
                    foreach (var b in buckets) words += Convert.ToInt32(s_BucketSizeField.GetValue(b));
                    result[name] = words;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VFX Control] Failed to read attribute layout: {e.Message}");
                if (Verbose) Debug.LogException(e); // full stack when diagnosing a package break
            }
            return result;
        }

        /// One stored attribute of a system: display name, friendly type, and its size in 32-bit words.
        public readonly struct VfxAttrField
        {
            public readonly string Name; public readonly string Type; public readonly int Words;
            public VfxAttrField(string name, string type, int words) { Name = name; Type = type; Words = words; }
        }

        /// The full per-system attribute layout (every stored attribute, in buffer order), keyed by
        /// unique system name. Same source as GetSystemAttributeWords (VFXDataParticle's current
        /// attribute layout, BucketInfo[].attributes) — the breakdown behind the "mem" stat. Empty for a
        /// system until its graph has been compiled/opened this session (the layout is [NonSerialized]).
        public static Dictionary<string, List<VfxAttrField>> GetSystemAttributeLayout(VisualEffectAsset asset)
        {
            var result = new Dictionary<string, List<VfxAttrField>>();
            try
            {
                foreach (var (buckets, name) in EnumerateSystemLayouts(asset))
                {
                    var elem = buckets.GetType().GetElementType();
                    if (s_BucketAttribsField == null) s_BucketAttribsField = elem?.GetField("attributes", InstanceAny);
                    if (s_BucketAttribsField == null) continue;

                    // Each bucket's "attributes" array has one entry per dword channel (the same
                    // VFXAttribute repeated across the channels it spans). Count channels per attribute,
                    // preserving first-seen buffer order, and skip null padding slots.
                    var fields = new List<VfxAttrField>();
                    var indexOf = new Dictionary<string, int>();
                    foreach (var b in buckets)
                    {
                        if (!(s_BucketAttribsField.GetValue(b) is Array attrs)) continue;
                        foreach (var a in attrs)
                        {
                            if (a == null) continue;
                            if (s_AttrNameField == null) s_AttrNameField = a.GetType().GetField("name", InstanceAny);
                            if (s_AttrTypeProp == null) s_AttrTypeProp = a.GetType().GetProperty("type", InstanceAny);
                            var nm = s_AttrNameField?.GetValue(a) as string;
                            if (string.IsNullOrEmpty(nm)) continue;
                            if (indexOf.TryGetValue(nm, out int at))
                                fields[at] = new VfxAttrField(nm, fields[at].Type, fields[at].Words + 1);
                            else
                            {
                                string ty = FriendlyAttrType(s_AttrTypeProp?.GetValue(a));
                                indexOf[nm] = fields.Count;
                                fields.Add(new VfxAttrField(nm, ty, 1));
                            }
                        }
                    }
                    if (fields.Count == 0) continue;
                    result[name] = fields;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VFX Control] Failed to read attribute layout detail: {e.Message}");
                if (Verbose) Debug.LogException(e); // full stack when diagnosing a package break
            }
            return result;
        }

        // VFXValueType enum (boxed) → a short friendly type name.
        private static string FriendlyAttrType(object valueType)
        {
            switch (valueType?.ToString())
            {
                case "Float": return "float";
                case "Float2": return "float2";
                case "Float3": return "float3";
                case "Float4": return "float4";
                case "Int32": return "int";
                case "Uint32": return "uint";
                case "Boolean": return "bool";
                default: return valueType?.ToString() ?? "?";
            }
        }

        /// Per-system simulation space, keyed by unique system name: 0 = None, 1 = Local, 2 = World
        /// (mirrors the VFXSpace enum). Same source/iteration as GetSystemAttributeLayout
        /// (VFXContext → GetData() → VFXDataParticle.space), so the keys align. Empty if the `space`
        /// member can't be reached or the graph isn't compiled.
        public static Dictionary<string, int> GetSystemSpaces(VisualEffectAsset asset)
        {
            var result = new Dictionary<string, int>();
            if (asset == null) return result;
            Resolve();
            if (!s_Available || s_ChildrenProp == null || s_ContextType == null ||
                s_GetData == null || s_DataSpaceProp == null) return result;

            try
            {
                if (!TryGetGraph(asset, out var graph)) return result;
                var systemNames = s_SystemNamesProp?.GetValue(graph);
                var seen = new HashSet<object>();
                if (!(s_ChildrenProp.GetValue(graph) is IEnumerable children)) return result;
                foreach (var child in children)
                {
                    if (child == null || !s_ContextType.IsInstanceOfType(child)) continue;
                    object data;
                    try { data = s_GetData.Invoke(child, null); } catch { continue; }
                    if (data == null) continue;
                    if (s_DataParticleType != null && !s_DataParticleType.IsInstanceOfType(data)) continue;
                    if (!seen.Add(data)) continue; // contexts share one data per system

                    int space; // map the VFXSpace enum name to an ordinal we can use without the VFX type
                    switch (s_DataSpaceProp.GetValue(data)?.ToString())
                    {
                        case "Local": space = 1; break;
                        case "World": space = 2; break;
                        default: space = 0; break; // None / unknown
                    }

                    string name = null;
                    if (systemNames != null && s_GetUniqueSystemName != null)
                        try { name = s_GetUniqueSystemName.Invoke(systemNames, new[] { data }) as string; } catch (Exception e) { if (Verbose) Debug.LogException(e); }
                    if (!string.IsNullOrEmpty(name)) result[name] = space;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VFX Control] Failed to read system spaces: {e.Message}");
                if (Verbose) Debug.LogException(e); // full stack when diagnosing a package break
            }
            return result;
        }

        // Profiler marker names on the runtime component (internal). Feed into UnityEngine.Profiling
        // Recorder.Get(name) for CPU/GPU timing. Null when unavailable (no recorder → "—").
        public static string CpuEffectMarker(VisualEffect ve)
        {
            Resolve();
            return s_CpuEffectMarkerArg == null ? null : InvokeStr(s_CpuEffectMarker, ve, new[] { s_CpuEffectMarkerArg });
        }
        public static string CpuSystemMarker(VisualEffect ve, string system) { Resolve(); return InvokeStr(s_CpuSystemMarker, ve, new object[] { system }); }
        public static string GpuTaskMarker(VisualEffect ve, string system, int taskIndex) { Resolve(); return InvokeStr(s_GpuTaskMarker, ve, new object[] { system, taskIndex }); }

        private static string InvokeStr(MethodInfo m, object target, object[] args)
        {
            if (m == null || target == null) return null;
            try { return m.Invoke(target, args) as string; } catch { return null; }
        }

        // Profiling registration — required for the per-system CPU/GPU markers to be emitted
        // (mirrors VFXProfilingBoard.Attach). No-ops if the methods can't be resolved.
        public static void RegisterForProfiling(VisualEffect ve) { Resolve(); try { s_Register?.Invoke(ve, null); } catch (Exception e) { if (Verbose) Debug.LogException(e); } }
        public static void UnregisterForProfiling(VisualEffect ve) { Resolve(); try { s_Unregister?.Invoke(ve, null); } catch (Exception e) { if (Verbose) Debug.LogException(e); } }
        public static bool IsRegisteredForProfiling(VisualEffect ve)
        {
            Resolve();
            if (s_IsRegistered == null || ve == null) return false;
            try { return s_IsRegistered.Invoke(ve, null) is bool b && b; } catch { return false; }
        }
    }
}
