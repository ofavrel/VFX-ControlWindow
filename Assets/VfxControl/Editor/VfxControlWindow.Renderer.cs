// VFX Control — Renderer tab (partial of VfxControlWindow).
//
// Exposes the sibling VFXRenderer's settings (the stock inspector's "Renderer" section):
// Probes (Reflection/Light Probes + Proxy/Anchor overrides) and Additional Settings
// (Rendering Layer Mask, Priority, Sorting Layer/Order). Built as UIToolkit rows sharing
// the property tab's chrome, bound to a multi-renderer SerializedObject. Split out of
// VfxControlWindow.cs — same class (partial), shared private state.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public partial class VfxControlWindow : EditorWindow
    {
        // ------------------------------------------------------------------ renderer tab

        // The VisualEffect renders through a sibling VFXRenderer component; its settings
        // (Probes, Rendering Layer Mask, Priority, Sorting) are what the stock VFX inspector
        // exposes under "Renderer". Built as UIToolkit rows (no IMGUI) sharing the property
        // tab's row chrome, so the look + favorite/reset/modified affordances are unified.
        // Multi-edit: all selected instances' renderers bind to one SerializedObject (writes
        // apply to every instance). The two controls without a stock UIToolkit field —
        // Rendering Layer Mask and Sorting Layer — are built from public SRP/SortingLayer
        // APIs (so they stay correct under HDRP and URP).

        // Rows built this populate, so a value/undo change can re-evaluate the modified marker.
        private List<(VisualElement row, RField field)> _rendererRows;
        // One renderer setting: rail section, label, favorite key, availability (SRP /
        // current-value gates), modified-vs-default test, reset, and a UIToolkit control
        // factory. Built fresh each populate so the closures capture the live SerializedObject.
        private sealed class RField
        {
            public string Label;
            public string Section;     // "probes" | "additional"
            public string FavKey;      // "renderer:<m_Field>"
            public bool Available;
            public Func<bool> IsModified;
            public Action Reset;
            public Func<VisualElement> BuildControl;
        }

        // Field defaults = the values on a freshly-created VFX component. Snapshotted once per
        // domain from a throwaway hidden GameObject, so "modified" means "differs from a new
        // component" exactly as the user expects.
        private struct RendererDefaults
        {
            public int reflectionProbeUsage, lightProbeUsage, rendererPriority, sortingOrder, sortingLayerID;
            public uint renderingLayerMask;
        }

        private static RendererDefaults? s_rendererDefaults;

        private RendererDefaults GetRendererDefaults()
        {
            if (s_rendererDefaults.HasValue) return s_rendererDefaults.Value;
            var d = new RendererDefaults();
            var go = EditorUtility.CreateGameObjectWithHideFlags("__VFXControlDefaults", HideFlags.HideAndDontSave, typeof(VisualEffect));
            try
            {
                var r = go.GetComponent<VFXRenderer>(); // auto-added by VisualEffect's RequireComponent
                if (r != null)
                {
                    var so = new SerializedObject(r);
                    d.reflectionProbeUsage = so.FindProperty("m_ReflectionProbeUsage")?.intValue ?? 0;
                    d.lightProbeUsage = so.FindProperty("m_LightProbeUsage")?.intValue ?? 0;
                    d.rendererPriority = so.FindProperty("m_RendererPriority")?.intValue ?? 0;
                    d.sortingOrder = so.FindProperty("m_SortingOrder")?.intValue ?? 0;
                    d.sortingLayerID = so.FindProperty("m_SortingLayerID")?.intValue ?? 0;
                    d.renderingLayerMask = r.renderingLayerMask;
                }
            }
            finally { Object.DestroyImmediate(go); }
            s_rendererDefaults = d;
            return d;
        }

        private VFXRenderer[] GetRenderers() => _effects
            .Where(ve => ve != null)
            .Select(ve => ve.GetComponent<VFXRenderer>())
            .Where(r => r != null)
            .ToArray();

        private static bool RendererPropModified(SerializedProperty p, int def) =>
            p != null && (p.hasMultipleDifferentValues || p.intValue != def);

        private void BuildRendererTab(VisualElement body)
        {
            var renderers = GetRenderers();
            if (renderers.Length == 0)
            {
                BuildPlaceholder(body, "This Visual Effect has no renderer component to configure.");
                return;
            }

            // One SerializedObject over every selected renderer (writes apply to all); it lives
            // for the lifetime of the rows that bind to it (a fresh one each populate).
            var so = new SerializedObject(renderers.Cast<Object>().ToArray());
            var fields = BuildRendererFields(so, GetRendererDefaults());
            _rendererRows = new List<(VisualElement, RField)>();
            AddFavoriteGroup(body, includeProps: false, RendererFavoriteSettings(so, fields)); // favorited renderer rows share `so`
            body.Add(BuildRendererSections(so, fields));
        }

        // The Probes/Additional section groups for a given renderer SO+fields, as a host element
        // (so the All tab can share one SO between the pinned group and these sections). Caller
        // resets _rendererRows first; rows accumulate into it for live marker refresh.
        private VisualElement BuildRendererSections(SerializedObject so, List<RField> fields)
        {
            string section = CurrentSection();
            bool InSection(string id) => section == "all" || section == id;
            bool Show(RField f) => f.Available && InSection(f.Section) && SearchMatches(f.Label) && ChipOk(f);

            var host = MakeElement("vfx-renderer-host");
            int shown = 0;
            shown += AddRendererSection(host, so, "probes", "Probes", fields, Show);
            shown += AddRendererSection(host, so, "additional", "Additional Settings", fields, Show);

            if (shown == 0)
            {
                var empty = new Label(
                    !string.IsNullOrEmpty(_search.Trim()) ? $"No renderer settings match “{_search}”."
                    : _filter == "fav" ? "No favorite renderer settings."
                    : _filter == "mod" ? "No modified renderer settings."
                    : "No renderer settings available.");
                empty.AddToClassList("vfx-empty");
                host.Add(empty);
            }
            else
            {
                // keep the modified markers + chip/footer counts live as values (or undo) change.
                // Registered on `host` so it's discarded when the body is repopulated (no leak).
                host.TrackSerializedObjectValue(so, _ => RefreshRendererState());
            }
            return host;
        }

        // Favorited (and available) renderer fields as Settings, sharing the caller's SO.
        private List<Setting> RendererFavoriteSettings(SerializedObject so, List<RField> fields)
        {
            var list = new List<Setting>();
            if (fields == null) return list;
            foreach (var f in fields)
                if (f.Available && IsFav(f.FavKey))
                    list.Add(new Setting { BuildRow = () => BuildRendererRow(f, so) });
            return list;
        }

        // A collapsible section group (Probes / Additional Settings) styled like a category
        // group, containing the visible renderer rows. Returns the number of rows shown.
        private int AddRendererSection(VisualElement host, SerializedObject so, string id, string heading, List<RField> fields, Func<RField, bool> show)
        {
            var visible = fields.Where(f => f.Section == id && show(f)).ToList();
            if (visible.Count == 0) return 0;

            bool forceOpen = !string.IsNullOrEmpty(_search.Trim());
            var (_, content, _) = AddGroupShell(host, "render:" + id, heading, visible.Count, forceOpen);
            foreach (var f in visible) content.Add(BuildRendererRow(f, so));
            return visible.Count;
        }

        // A renderer setting as a property-style row: label column, control, hover ↺/★ tools,
        // modified marker. Reset/pin rebuild the body (re-reading the unbound mask/sorting fields).
        private VisualElement BuildRendererRow(RField f, SerializedObject so)
        {
            var row = MakeElement("vfx-row");
            row.EnableInClassList("vfx-row--modified", f.IsModified());
            if (IsFav(f.FavKey)) row.AddToClassList("vfx-row--fav");

            var labelCol = MakeElement("vfx-label-col");
            var label = new Label(f.Label) { tooltip = f.Label };
            label.AddToClassList("vfx-plabel");
            labelCol.Add(label);
            row.Add(labelCol);

            row.Add(MakeElement("vfx-row-lock")); // align with property rows' lock gutter

            var control = f.BuildControl();
            control.AddToClassList("vfx-pcontrol");
            AttachLabelDragger(label, control); // scrub Priority/Order by dragging the label (no-op for non-numeric)
            row.Add(control);

            var tools = MakeElement("vfx-row-tools");
            var reset = MakeIconButton("↺", "Reset to default", () =>
            {
                f.Reset();
                so.ApplyModifiedProperties();
                RebuildBodyOnly();
            });
            reset.AddToClassList("vfx-tool-reset");
            tools.Add(reset);
            var star = MakeIconButton(IsFav(f.FavKey) ? "★" : "☆", IsFav(f.FavKey) ? "Unpin" : "Pin", () => ToggleFav(f.FavKey));
            star.AddToClassList("vfx-tool-fav");
            tools.Add(star);
            row.Add(tools);

            _rendererRows.Add((row, f));
            return row;
        }

        // Re-evaluate modified markers + chrome counts after a renderer value/undo change.
        private void RefreshRendererState()
        {
            if (_rendererRows != null)
                foreach (var (row, f) in _rendererRows)
                    row.EnableInClassList("vfx-row--modified", f.IsModified());
            PopulateChips();
            UpdateFooter();
        }

        // Rebuild the active tab body on the next tick (used when a value change toggles which
        // other rows are available, e.g. probe usage → Anchor/Proxy visibility).
        private void DeferRebuildBody() => rootVisualElement.schedule.Execute(RebuildBodyOnly);

        // EnumField over an int-backed serialized property (m_*ProbeUsage). Manual write
        // (intValue + Apply) because BindProperty(EnumField) doesn't persist an int property.
        private VisualElement MakeRendererEnum<T>(SerializedProperty prop, SerializedObject so, bool rebuildOnChange = false)
            where T : struct, Enum
        {
            var field = new EnumField((Enum)Enum.ToObject(typeof(T), prop.intValue));
            field.showMixedValue = prop.hasMultipleDifferentValues;
            field.RegisterValueChangedCallback(e =>
            {
                prop.intValue = Convert.ToInt32(e.newValue);
                so.ApplyModifiedProperties();
                if (rebuildOnChange) DeferRebuildBody(); // conditional rows (Anchor/Proxy) may change
                else RefreshRendererState();
            });
            return field;
        }

        // ---- the two controls with no stock UIToolkit field, built from public SRP APIs ----

        // Written through the serialized property (not the renderer's C# setter) so it shares
        // the one ApplyModifiedProperties with the other fields — mixing direct writes with an
        // open SerializedObject lets Apply clobber them (caused "Reset tab" to need two clicks).
        private VisualElement MakeRenderingLayerMaskField(SerializedObject so)
        {
            var names = RenderingLayerMask.GetDefinedRenderingLayerNames();
            var values = RenderingLayerMask.GetDefinedRenderingLayerValues();
            var maskProp = so.FindProperty("m_RenderingLayerMask");
            uint current = maskProp != null ? maskProp.uintValue : 0u;

            int bits = 0;
            for (int i = 0; i < values.Length; i++)
                if (((uint)values[i]) != 0 && (current & (uint)values[i]) == (uint)values[i]) bits |= 1 << i;

            var field = new MaskField(names.ToList(), bits);
            field.showMixedValue = maskProp != null && maskProp.hasMultipleDifferentValues;
            field.RegisterValueChangedCallback(e =>
            {
                if (maskProp == null) return;
                uint mask = 0;
                for (int i = 0; i < values.Length; i++)
                    if ((e.newValue & (1 << i)) != 0) mask |= (uint)values[i];
                maskProp.uintValue = mask;
                so.ApplyModifiedProperties();
                RefreshRendererState();
            });
            return field;
        }

        private const string kAddSortingLayer = "Add Sorting Layer…";

        private VisualElement MakeSortingLayerPopup(SerializedProperty layerIdProp, SerializedObject so)
        {
            var layers = SortingLayer.layers;
            var names = layers.Select(l => l.name).ToList();
            names.Add(kAddSortingLayer); // trailing entry opens Project Settings ▸ Tags and Layers

            int idx = System.Array.FindIndex(layers, l => l.id == layerIdProp.intValue);
            if (idx < 0) idx = 0;
            string currentName = layers.Length > 0 ? layers[Mathf.Clamp(idx, 0, layers.Length - 1)].name : "";

            var field = new PopupField<string>(names, idx);
            field.showMixedValue = layerIdProp.hasMultipleDifferentValues;
            field.RegisterValueChangedCallback(e =>
            {
                if (e.newValue == kAddSortingLayer)
                {
                    field.SetValueWithoutNotify(currentName); // revert the synthetic entry
                    SettingsService.OpenProjectSettings("Project/Tags and Layers");
                    return;
                }
                int i = names.IndexOf(e.newValue);
                if (i < 0 || i >= layers.Length) return;
                layerIdProp.intValue = layers[i].id;
                so.ApplyModifiedProperties();
                RefreshRendererState();
            });
            return field;
        }

        // The renderer settings as RField descriptors. Availability mirrors the stock VFX
        // inspector's SRP/usage gates; modified/reset compare against the fresh-create defaults.
        private List<RField> BuildRendererFields(SerializedObject so, RendererDefaults d)
        {
            var reflectionProbeUsage = so.FindProperty("m_ReflectionProbeUsage");
            var lightProbeUsage = so.FindProperty("m_LightProbeUsage");
            var lightProbeVolumeOverride = so.FindProperty("m_LightProbeVolumeOverride");
            var probeAnchor = so.FindProperty("m_ProbeAnchor");
            var renderingLayerMask = so.FindProperty("m_RenderingLayerMask");
            var rendererPriority = so.FindProperty("m_RendererPriority");
            var sortingOrder = so.FindProperty("m_SortingOrder");
            var sortingLayerID = so.FindProperty("m_SortingLayerID");

            bool showReflectionProbe = reflectionProbeUsage != null && SupportedRenderingFeatures.active.reflectionProbes;
            var srpType = GraphicsSettings.currentRenderPipelineAssetType;
            if (srpType != null && srpType.ToString().Contains("UniversalRenderPipeline"))
                showReflectionProbe = reflectionProbeUsage != null; // URP hides it in stock Renderers but VFX keeps it reachable

            bool reflectionOn = reflectionProbeUsage != null && !reflectionProbeUsage.hasMultipleDifferentValues &&
                                (ReflectionProbeUsage)reflectionProbeUsage.intValue != ReflectionProbeUsage.Off;
            bool lightOn = lightProbeUsage != null && !lightProbeUsage.hasMultipleDifferentValues &&
                           (LightProbeUsage)lightProbeUsage.intValue != LightProbeUsage.Off;
#pragma warning disable CS0618 // UseProxyVolume is obsolete in some configs but still the serialized enum value
            bool proxyOn = lightProbeUsage != null && !lightProbeUsage.hasMultipleDifferentValues &&
                           lightProbeUsage.intValue == (int)LightProbeUsage.UseProxyVolume;
#pragma warning restore CS0618

            return new List<RField>
            {
                new RField
                {
                    Label = "Reflection Probes", Section = "probes", FavKey = "renderer:m_ReflectionProbeUsage",
                    Available = showReflectionProbe,
                    IsModified = () => RendererPropModified(reflectionProbeUsage, d.reflectionProbeUsage),
                    Reset = () => { if (reflectionProbeUsage != null) reflectionProbeUsage.intValue = d.reflectionProbeUsage; },
                    // m_ReflectionProbeUsage is serialized as a plain int (the stock editor writes
                    // intValue), so BindProperty(EnumField) wouldn't persist — write it manually.
                    BuildControl = () => MakeRendererEnum<ReflectionProbeUsage>(reflectionProbeUsage, so, rebuildOnChange: true),
                },
                new RField
                {
                    Label = "Light Probes", Section = "probes", FavKey = "renderer:m_LightProbeUsage",
                    Available = lightProbeUsage != null,
                    IsModified = () => RendererPropModified(lightProbeUsage, d.lightProbeUsage),
                    Reset = () => { if (lightProbeUsage != null) lightProbeUsage.intValue = d.lightProbeUsage; },
                    // rebuild on change so Proxy Volume Override / Anchor Override appear/disappear
                    BuildControl = () => MakeRendererEnum<LightProbeUsage>(lightProbeUsage, so, rebuildOnChange: true),
                },
                new RField
                {
                    Label = "Proxy Volume Override", Section = "probes", FavKey = "renderer:m_LightProbeVolumeOverride",
                    Available = proxyOn,
                    IsModified = () => lightProbeVolumeOverride != null && (lightProbeVolumeOverride.hasMultipleDifferentValues || lightProbeVolumeOverride.objectReferenceValue != null),
                    Reset = () => { if (lightProbeVolumeOverride != null) lightProbeVolumeOverride.objectReferenceValue = null; },
                    BuildControl = () =>
                    {
#pragma warning disable CS0618 // LightProbeProxyVolume deprecated with the Built-In RP, but still the field's type
                        var f = new ObjectField { objectType = typeof(LightProbeProxyVolume), allowSceneObjects = true };
#pragma warning restore CS0618
                        f.BindProperty(lightProbeVolumeOverride);
                        return f;
                    },
                },
                new RField
                {
                    Label = "Anchor Override", Section = "probes", FavKey = "renderer:m_ProbeAnchor",
                    Available = (reflectionOn || lightOn) && probeAnchor != null,
                    IsModified = () => probeAnchor != null && (probeAnchor.hasMultipleDifferentValues || probeAnchor.objectReferenceValue != null),
                    Reset = () => { if (probeAnchor != null) probeAnchor.objectReferenceValue = null; },
                    BuildControl = () =>
                    {
                        var f = new ObjectField { objectType = typeof(Transform), allowSceneObjects = true };
                        f.BindProperty(probeAnchor);
                        return f;
                    },
                },
                new RField
                {
                    Label = "Rendering Layer Mask", Section = "additional", FavKey = "renderer:m_RenderingLayerMask",
                    Available = renderingLayerMask != null && GraphicsSettings.isScriptableRenderPipelineEnabled,
                    IsModified = () => renderingLayerMask != null && (renderingLayerMask.hasMultipleDifferentValues || renderingLayerMask.uintValue != d.renderingLayerMask),
                    Reset = () => { if (renderingLayerMask != null) renderingLayerMask.uintValue = d.renderingLayerMask; },
                    BuildControl = () => MakeRenderingLayerMaskField(so),
                },
                new RField
                {
                    Label = "Priority", Section = "additional", FavKey = "renderer:m_RendererPriority",
                    Available = rendererPriority != null && SupportedRenderingFeatures.active.rendererPriority,
                    IsModified = () => RendererPropModified(rendererPriority, d.rendererPriority),
                    Reset = () => { if (rendererPriority != null) rendererPriority.intValue = d.rendererPriority; },
                    BuildControl = () => { var f = new IntegerField(); f.BindProperty(rendererPriority); return f; },
                },
                new RField
                {
                    Label = "Sorting Layer", Section = "additional", FavKey = "renderer:m_SortingLayerID",
                    Available = sortingLayerID != null && sortingOrder != null,
                    IsModified = () => RendererPropModified(sortingLayerID, d.sortingLayerID),
                    Reset = () => { if (sortingLayerID != null) sortingLayerID.intValue = d.sortingLayerID; },
                    BuildControl = () => MakeSortingLayerPopup(sortingLayerID, so),
                },
                new RField
                {
                    Label = "Order in Layer", Section = "additional", FavKey = "renderer:m_SortingOrder",
                    Available = sortingLayerID != null && sortingOrder != null,
                    IsModified = () => RendererPropModified(sortingOrder, d.sortingOrder),
                    Reset = () => { if (sortingOrder != null) sortingOrder.intValue = d.sortingOrder; },
                    BuildControl = () => { var f = new IntegerField(); f.BindProperty(sortingOrder); return f; },
                },
            };
        }

        // Does an RField pass the active filter chip? (mirrors Visible's fav/mod logic)
        private bool ChipOk(RField f) =>
            _filter == "all" ||
            (_filter == "fav" && IsFav(f.FavKey)) ||
            (_filter == "mod" && f.IsModified());

        // (leaf, fav, mod) counts for the renderer tab's filter chips.
        private (int leaf, int fav, int mod) RendererChipCounts()
        {
            var renderers = GetRenderers();
            if (renderers.Length == 0) return (0, 0, 0);
            var so = new SerializedObject(renderers.Cast<Object>().ToArray());
            so.Update();
            var fields = BuildRendererFields(so, GetRendererDefaults());
            int leaf = fields.Count(f => f.Available);
            int fav = fields.Count(f => f.Available && IsFav(f.FavKey));
            int mod = fields.Count(f => f.Available && f.IsModified());
            return (leaf, fav, mod);
        }

        // Reset every modified renderer field on the selected instances to the fresh-create default.
        private void ResetRendererToDefaults()
        {
            var renderers = GetRenderers();
            if (renderers.Length == 0) return;
            var so = new SerializedObject(renderers.Cast<Object>().ToArray());
            so.Update();
            foreach (var f in BuildRendererFields(so, GetRendererDefaults()))
                if (f.Available && f.IsModified()) f.Reset();
            so.ApplyModifiedProperties();
        }
    }
}
