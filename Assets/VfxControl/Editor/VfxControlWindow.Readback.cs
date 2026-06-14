// VFX Control — particle attribute readback (partial of VfxControlWindow).
//
// Opt-in per-particle attribute spreadsheet (Debug ▸ Particles) + the scene-view
// overlay. VFX particles are GPU-only with no managed readback, so the graph is
// instrumented with a Custom HLSL block (Readback/VfxReadback.hlsl) that writes a fixed
// record into a shared global GraphicsBuffer; we AsyncGPUReadback it and tabulate the
// live particles. Split out of VfxControlWindow.cs — same class (partial), shared state.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public partial class VfxControlWindow : EditorWindow
    {
        // --- Particle attribute readback (opt-in; see Assets/VfxControl/Readback/VfxReadback.hlsl) ---
        // The graph is instrumented (Custom HLSL block, one `instanceId` input) so each particle writes
        // its position+color into a STABLE slot = instanceId*256 + particleId%256, plus a per-frame
        // generation stamp. The window auto-assigns each VisualEffect of the asset a distinct instanceId
        // via SetInt on the exposed `VfxReadbackInstanceId` property (if wired), so instances land in
        // separate regions. We bump the generation each frame, AsyncGPUReadback the gen+data buffers, and
        // show the slots stamped with the latest generation present (= live particles this frame),
        // grouped by instance. Dead particles stop re-stamping and drop out.
        const int kReadbackPerInstance = 256;    // particle slots per instance (matches the .hlsl)
        const int kReadbackMaxInstances = 16;    // instance regions (matches the .hlsl)
        const int kReadbackStride = 9;           // float4 per particle record (matches the .hlsl)
        const int kReadbackCap = kReadbackPerInstance * kReadbackMaxInstances; // total slots
        const int kReadbackFloat4s = kReadbackCap * kReadbackStride; // float4 buffer length
        const string kReadbackInstanceProp = "VfxReadbackInstanceId"; // exposed Int the user wires to the block
        static readonly int kReadbackBufferId = Shader.PropertyToID("_VfxReadbackBuffer");
        static readonly int kReadbackGenId = Shader.PropertyToID("_VfxReadbackGen");
        static readonly int kReadbackGenerationId = Shader.PropertyToID("_VfxReadbackGeneration");
        GraphicsBuffer _readbackBuffer;          // reusable, created lazily, disposed in OnDisable
        GraphicsBuffer _readbackGenBuffer;       // per-slot generation stamp
        Vector4[] _readbackData;                 // last decoded raw contents (length kReadbackFloat4s)
        uint[] _readbackGen;                     // last decoded per-slot generation stamps (length kReadbackCap)
        uint _readbackMaxGen;                    // latest generation present in the last gen readback
        int _readbackGeneration = 1;             // current frame id pushed to the shader (>=1; 0 = unwritten)
        readonly string[] _readbackInstanceNames = new string[kReadbackMaxInstances]; // instanceId → GameObject name
        readonly List<VisualEffect> _readbackSelected = new List<VisualEffect>(); // selected instances → ids 0..K-1
        double _lastInstanceAssign;              // throttle for the SetInt instance-id assignment
        readonly List<int> _readbackRows = new List<int>(); // slots stamped with _readbackMaxGen → table rows
        int _readbackInstanceCount;              // distinct instances present in the last readback
        bool _readbackPending;                   // an AsyncGPUReadback is in flight

        // Decoded columns: which attributes the instrumented system(s) actually use (from the graph
        // layout), mapped to fixed float offsets in the .hlsl record. Built per body in BuildDebugParticles.
        enum RbKind { Float, Color, Alive, Id }
        readonly struct RbAttr
        {
            public readonly string Layout;  // representative stored-attribute name that marks presence
            public readonly string Title;   // column header
            public readonly int Float;      // first float index in the per-particle record (0..35)
            public readonly int Count;      // 1 or 3 components
            public readonly RbKind Kind;
            public RbAttr(string layout, string title, int f, int count, RbKind kind)
            { Layout = layout; Title = title; Float = f; Count = count; Kind = kind; }
        }
        // Order = column order; Float offsets must match VfxReadback.hlsl's record packing.
        static readonly RbAttr[] kReadbackAttrs =
        {
            new RbAttr("position",         "Position",     0,  3, RbKind.Float),
            new RbAttr("age",              "Age",          3,  1, RbKind.Float),
            new RbAttr("velocity",         "Velocity",     4,  3, RbKind.Float),
            new RbAttr("lifetime",         "Lifetime",     7,  1, RbKind.Float),
            new RbAttr("color",            "Color",        8,  3, RbKind.Color),
            new RbAttr("alpha",            "Alpha",        11, 1, RbKind.Float),
            new RbAttr("direction",        "Direction",    12, 3, RbKind.Float),
            new RbAttr("size",             "Size",         15, 1, RbKind.Float),
            new RbAttr("targetPosition",   "Target Pos",   16, 3, RbKind.Float),
            new RbAttr("mass",             "Mass",         19, 1, RbKind.Float),
            new RbAttr("scaleX",           "Scale",        20, 3, RbKind.Float),
            new RbAttr("texIndex",         "Tex Index",    23, 1, RbKind.Float),
            new RbAttr("angleX",           "Angle",        24, 3, RbKind.Float),
            new RbAttr("alive",            "Alive",        27, 1, RbKind.Alive),
            new RbAttr("angularVelocityX", "Angular Vel",  28, 3, RbKind.Float),
            new RbAttr("particleId",       "Particle Id",  31, 1, RbKind.Id),
            new RbAttr("pivotX",           "Pivot",        32, 3, RbKind.Float),
        };
        // Columns shown when the graph layout isn't available yet (not compiled): a sensible default.
        static readonly string[] kReadbackDefaultCols = { "position", "age", "color", "alpha" };
        readonly List<RbAttr> _readbackCols = new List<RbAttr>(); // active columns for the current asset
        bool _readbackAuto = true;               // continuous capture while the section is shown
        bool _readbackCaptureOnce;               // a manual Capture was requested
        double _lastReadbackReq;                 // throttle (~6 Hz)
        MultiColumnListView _particleTable;
        Label _readbackCountLabel;
        VisualElement _readbackHelp;

        // Scene overlay (Debug ▸ Particles → Scene view): per-attribute "eye" toggles + a selected
        // particle drive a point + value box drawn at the particle's world position.
        readonly HashSet<string> _particleEyes = new HashSet<string>(); // eye-ON attributes, by RbAttr.Layout
        readonly List<int> _particleSelSlots = new List<int>(); // selected particle SLOTs (stable; drives the overlay)
        const int kMaxDebugParticles = 24;       // cap on simultaneously-overlaid particles (perf/clutter)
        int _readbackPosSpace;                   // sim space of the position-bearing system: 1 Local, else World/none
        VisualEffectAsset _readbackBufferAsset;  // asset whose data is currently in the shared buffer (wipe on change)
        const string kParticleEyesKeyPrefix = "vfxctrl.particleEyes."; // SessionState, per asset GUID
        // ---- Particle attribute readback spreadsheet (opt-in) ------------------------------
        // Requires the graph to be instrumented with a Custom HLSL block pointing at VfxReadback.hlsl
        // (writes a fixed superset record per particle into a shared global buffer). We bind the buffers,
        // AsyncGPUReadback them, and tabulate the live particles. Columns are driven by each system's
        // actual attribute layout, so only the attributes the system really uses are shown.
        void BuildDebugParticles(VisualElement host)
        {
            _particleTable = null;
            _readbackCountLabel = null;
            _readbackHelp = null;

            // Columns = the curated attributes the asset's systems actually store (union across systems);
            // fall back to a small default set if the graph layout isn't available yet (not compiled).
            BuildReadbackColumns();

            // Controls: Capture · Auto · row count.
            var bar = MakeElement("vfx-particles-bar");
            var capture = new Button(() => _readbackCaptureOnce = true) { text = "Capture" };
            capture.AddToClassList("vfx-particles-capture");
            bar.Add(capture);
            var auto = new Toggle("Auto") { value = _readbackAuto };
            auto.AddToClassList("vfx-particles-auto");
            auto.RegisterValueChangedCallback(e => _readbackAuto = e.newValue);
            bar.Add(auto);
            _readbackCountLabel = new Label();
            _readbackCountLabel.AddToClassList("vfx-particles-count");
            bar.Add(_readbackCountLabel);
            host.Add(bar);

            // The spreadsheet. Clicking a column header sorts by it (toggles asc/desc); the sort is
            // re-applied on every readback so it sticks as the data refreshes. Multi-row selection drives
            // the Scene overlay (tracked by stable slots, see OnParticleSelectionChanged; capped at
            // kMaxDebugParticles). Ctrl/Shift-click to select several particles.
            var table = new MultiColumnListView { showBoundCollectionSize = false, sortingMode = ColumnSortingMode.Custom };
            table.columnSortingChanged += () => { SortReadbackRows(); table.RefreshItems(); };
            table.selectionType = SelectionType.Multiple;
            table.selectionChanged += _ => OnParticleSelectionChanged();
            table.AddToClassList("vfx-particles-table");
            table.columns.Add(new Column
            {
                title = "Instance", width = 110, makeCell = () => MakeCell(),
                bindCell = (e, i) =>
                {
                    int inst = _readbackRows[i] / kReadbackPerInstance;
                    string nm = inst < _readbackInstanceNames.Length ? _readbackInstanceNames[inst] : null;
                    ((Label)e).text = nm ?? inst.ToString();
                }
            });
            table.columns.Add(new Column { title = "#", width = 44, makeCell = () => MakeCell(), bindCell = (e, i) => ((Label)e).text = (_readbackRows[i] % kReadbackPerInstance).ToString() });
            foreach (var a in _readbackCols)
            {
                var attr = a; // capture per-iteration
                if (attr.Kind == RbKind.Color)
                    table.columns.Add(new Column
                    {
                        title = attr.Title, width = 150,
                        makeHeader = () => MakeAttrHeader(attr), bindHeader = e => UpdateEyeVisual(e, attr),
                        makeCell = MakeColorCell,
                        bindCell = (e, i) =>
                        {
                            int s = _readbackRows[i];
                            float r = RbVal(s, attr.Float), g = RbVal(s, attr.Float + 1), b2 = RbVal(s, attr.Float + 2);
                            // Swatch gamma-corrected so it matches the particle on screen; text stays raw linear.
                            e.Q(className: "vfx-particles-swatch").style.backgroundColor = new Color(r, g, b2, 1f).gamma;
                            e.Q<Label>().text = $"{r:0.##}, {g:0.##}, {b2:0.##}";
                        }
                    });
                else
                    table.columns.Add(new Column
                    {
                        title = attr.Title, width = attr.Count == 3 ? 170 : 70,
                        makeHeader = () => MakeAttrHeader(attr), bindHeader = e => UpdateEyeVisual(e, attr),
                        makeCell = () => MakeCell(),
                        bindCell = (e, i) => ((Label)e).text = FormatRbCell(_readbackRows[i], attr)
                    });
            }
            table.itemsSource = _readbackRows;
            _particleTable = table;
            host.Add(table);

            // Empty / not-instrumented state.
            _readbackHelp = MakeElement("vfx-helpbox");
            _readbackHelp.Add(new Label(
                "No readback data. Add a Custom HLSL block (function VfxReadback) pointing at " +
                "Assets/VfxControl/Readback/VfxReadback.hlsl in this system's Update or Output context. For " +
                "separate per-instance rows, expose an Int property named VfxReadbackInstanceId and wire it to " +
                "the block's instanceId input (the window auto-assigns ids). Only public APIs — see the docs."));
            host.Add(_readbackHelp);

            RefreshParticleTable();
        }

        // Reorder _readbackRows by the column the user clicked (MultiColumnListView in Custom sorting
        // mode just reports the selected columns; we do the actual sort over our row→slot list).
        void SortReadbackRows()
        {
            if (_particleTable == null || _readbackRows.Count < 2) return;
            SortColumnDescription sort = null;
            foreach (var s in _particleTable.sortedColumns) { sort = s; break; } // primary column only
            if (sort == null) return;
            int col = sort.columnIndex;
            bool asc = sort.direction == SortDirection.Ascending;
            _readbackRows.Sort((a, b) =>
            {
                int cmp = ParticleSortKey(a, col).CompareTo(ParticleSortKey(b, col));
                return asc ? cmp : -cmp;
            });
        }

        // Comparable key per column: 0 Instance · 1 # (particleId) · 2.. the active attribute columns
        // (float3 → magnitude, Color → luminance, else the scalar). Guards against a short data buffer.
        double ParticleSortKey(int slot, int col)
        {
            if (col == 0) return slot / kReadbackPerInstance;
            if (col == 1) return slot % kReadbackPerInstance;
            int ci = col - 2;
            if (ci < 0 || ci >= _readbackCols.Count) return slot;
            var a = _readbackCols[ci];
            if (a.Kind == RbKind.Color)
                return 0.2126 * RbVal(slot, a.Float) + 0.7152 * RbVal(slot, a.Float + 1) + 0.0722 * RbVal(slot, a.Float + 2);
            if (a.Count == 3)
            {
                double x = RbVal(slot, a.Float), y = RbVal(slot, a.Float + 1), z = RbVal(slot, a.Float + 2);
                return Math.Sqrt(x * x + y * y + z * z);
            }
            return RbVal(slot, a.Float);
        }

        // Pick the active columns: the curated attributes the asset's systems actually store (union of
        // GetSystemAttributeLayout across systems); falls back to a default set when the layout is empty
        // (graph not compiled this session). Order follows kReadbackAttrs.
        void BuildReadbackColumns()
        {
            _readbackCols.Clear();
            var asset = _effect != null ? _effect.visualEffectAsset : null;
            var present = new HashSet<string>();
            if (asset != null)
                foreach (var kv in VfxGraphReflection.GetSystemAttributeLayout(asset))
                    foreach (var f in kv.Value) present.Add(f.Name);

            foreach (var a in kReadbackAttrs)
            {
                bool show = present.Count > 0 ? present.Contains(a.Layout)
                                              : System.Array.IndexOf(kReadbackDefaultCols, a.Layout) >= 0;
                if (show) _readbackCols.Add(a);
            }

            // World position needs the position-bearing system's sim space (Local → transform by the
            // owning instance; World/unknown → use as-is). Use the first system that stores `position`;
            // multi-system assets with mixed spaces aren't disambiguated (the readback slot doesn't
            // record its source system). Default World when unresolved (avoids a wrong double-transform).
            _readbackPosSpace = 2; // World
            if (asset != null)
            {
                var layout = VfxGraphReflection.GetSystemAttributeLayout(asset);
                var spaces = VfxGraphReflection.GetSystemSpaces(asset);
                foreach (var kv in layout)
                    if (kv.Value.Exists(f => f.Name == "position") && spaces.TryGetValue(kv.Key, out int sp))
                    { _readbackPosSpace = sp; break; }
            }

            LoadParticleEyes(asset);
        }

        // Eye state is persisted per asset GUID in SessionState (survives recompiles), default empty.
        void LoadParticleEyes(VisualEffectAsset asset)
        {
            _particleEyes.Clear();
            string guid = asset != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)) : null;
            if (string.IsNullOrEmpty(guid)) return;
            string csv = SessionState.GetString(kParticleEyesKeyPrefix + guid, "");
            if (csv.Length == 0) return;
            foreach (var s in csv.Split(',')) if (s.Length > 0) _particleEyes.Add(s);
        }

        void SaveParticleEyes()
        {
            var asset = _effect != null ? _effect.visualEffectAsset : null;
            string guid = asset != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)) : null;
            if (string.IsNullOrEmpty(guid)) return;
            SessionState.SetString(kParticleEyesKeyPrefix + guid, string.Join(",", _particleEyes));
        }

        // Column header = attribute name + an "eye" toggle. When the eye is on, the selected particle's
        // value for this attribute is drawn in the Scene overlay. The eye stops pointer propagation so it
        // doesn't trigger the header's column sort.
        VisualElement MakeAttrHeader(RbAttr attr)
        {
            var h = MakeElement("vfx-particles-header");
            var name = new Label(attr.Title);
            name.AddToClassList("vfx-particles-header-label");
            h.Add(name);

            var eye = new VisualElement { tooltip = "Show this attribute for the selected particle in the Scene view" };
            eye.AddToClassList("vfx-iconbtn");
            eye.AddToClassList("vfx-eye");
            var ico = EditorGUIUtility.IconContent("animationvisibilitytoggleon")?.image;
            if (ico != null)
            {
                var img = new Image { image = ico, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                img.style.width = 14; img.style.height = 14;
                eye.Add(img);
            }
            else
            {
                var g = new Label("◉") { pickingMode = PickingMode.Ignore }; // ◉ fallback glyph
                eye.Add(g);
            }
            eye.RegisterCallback<PointerDownEvent>(evt =>
            {
                evt.StopPropagation(); // don't sort the column
                if (!_particleEyes.Remove(attr.Layout)) _particleEyes.Add(attr.Layout);
                UpdateEyeVisual(h, attr);
                SaveParticleEyes();
                SceneView.RepaintAll();
            });
            h.Add(eye);
            UpdateEyeVisual(h, attr);
            return h;
        }

        void UpdateEyeVisual(VisualElement header, RbAttr attr)
        {
            var eye = header.Q(className: "vfx-eye");
            eye?.EnableInClassList("vfx-eye--on", _particleEyes.Contains(attr.Layout));
        }

        // Track the selection by stable SLOTs (not row indices): rows reorder on sort/refresh, so the
        // slots keep the overlay pinned to the same particles. Capped at kMaxDebugParticles.
        void OnParticleSelectionChanged()
        {
            _particleSelSlots.Clear();
            if (_particleTable != null)
                foreach (int i in _particleTable.selectedIndices)
                {
                    if (i >= 0 && i < _readbackRows.Count) _particleSelSlots.Add(_readbackRows[i]);
                    if (_particleSelSlots.Count >= kMaxDebugParticles) break;
                }
            SceneView.RepaintAll();
        }

        // Read one float of a particle's record from the decoded buffer (stride kReadbackStride float4).
        float RbVal(int slot, int floatIndex)
        {
            int idx = slot * kReadbackStride + (floatIndex >> 2);
            if (_readbackData == null || idx < 0 || idx >= _readbackData.Length) return 0f;
            var v = _readbackData[idx];
            switch (floatIndex & 3) { case 0: return v.x; case 1: return v.y; case 2: return v.z; default: return v.w; }
        }

        // Text for a non-color attribute cell.
        string FormatRbCell(int slot, RbAttr a)
        {
            switch (a.Kind)
            {
                case RbKind.Alive: return RbVal(slot, a.Float) > 0.5f ? "yes" : "no";
                case RbKind.Id: return ((uint)Mathf.Max(0f, RbVal(slot, a.Float))).ToString();
                default:
                    if (a.Count == 3)
                        return $"{RbVal(slot, a.Float):0.###}, {RbVal(slot, a.Float + 1):0.###}, {RbVal(slot, a.Float + 2):0.###}";
                    return $"{RbVal(slot, a.Float):0.###}";
            }
        }

        static Label MakeCell()
        {
            var l = new Label();
            l.AddToClassList("vfx-particles-cell");
            return l;
        }

        static VisualElement MakeColorCell()
        {
            var row = MakeElement("vfx-particles-colorcell");
            row.Add(MakeElement("vfx-particles-swatch"));
            var l = new Label();
            l.AddToClassList("vfx-particles-cell");
            row.Add(l);
            return row;
        }

        // Lazily (re)create the data + generation buffers, both zero-initialised (gen 0 = "never written").
        void EnsureReadbackBuffer()
        {
            if (_readbackBuffer == null || !_readbackBuffer.IsValid())
            {
                _readbackBuffer?.Dispose();
                _readbackBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kReadbackFloat4s, 16);
                _readbackBuffer.SetData(new Vector4[kReadbackFloat4s]);
            }
            if (_readbackGenBuffer == null || !_readbackGenBuffer.IsValid())
            {
                _readbackGenBuffer?.Dispose();
                _readbackGenBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kReadbackCap, sizeof(uint));
                _readbackGenBuffer.SetData(new uint[kReadbackCap]);
            }
        }

        // Only the SELECTED instances (_effects) get readable ids 0..K-1 via the exposed
        // `VfxReadbackInstanceId` Int — every other VisualEffect of the asset is pushed out of range
        // (id == kReadbackMaxInstances) so the instrumented block skips it and it never pollutes the
        // regions we read. Select one effect → see only it; select two → see both. SetInt persists, so
        // this is throttled (~2 Hz; forced to re-run on selection change via _lastInstanceAssign = 0).
        // Stable id per effect by GetEntityId. Components without the property wired (HasInt false) can't
        // be steered — they fall back to the port default (0); wire the property for the selection filter.
        void AssignReadbackInstanceIds()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastInstanceAssign < 0.5 && _readbackInstanceNames[0] != null) return;
            _lastInstanceAssign = now;
            System.Array.Clear(_readbackInstanceNames, 0, _readbackInstanceNames.Length);

            var asset = _effect.visualEffectAsset;
            if (asset == null) return;

            // Selected instances, sorted by entity id for a stable id assignment.
            _readbackSelected.Clear();
            foreach (var ve in _effects) if (ve != null) _readbackSelected.Add(ve);
            if (_readbackSelected.Count == 0) _readbackSelected.Add(_effect);
            _readbackSelected.Sort((a, b) => a.GetEntityId().CompareTo(b.GetEntityId()));

            var idOf = new Dictionary<VisualEffect, int>();
            for (int i = 0; i < _readbackSelected.Count && i < kReadbackMaxInstances; i++)
            {
                idOf[_readbackSelected[i]] = i;
                _readbackInstanceNames[i] = _readbackSelected[i].name;
            }

            // Steer EVERY instrumented instance in the scene (any asset, not just the current one): the
            // readback buffer is a scene-global resource, so a different asset's instance left at a low id
            // would keep writing into the regions we read and mix into the list. Selected → its id;
            // everything else (including other assets) → out of range so its block skips the write.
            foreach (var v in Object.FindObjectsByType<VisualEffect>(FindObjectsSortMode.None))
            {
                if (v == null || !v.HasInt(kReadbackInstanceProp)) continue;
                v.SetInt(kReadbackInstanceProp, idOf.TryGetValue(v, out var id) ? id : kReadbackMaxInstances);
            }
        }

        // Driven by Tick: bump the generation, keep the globals bound while the window is open, and
        // issue throttled readbacks (continuous when Auto, or once per Capture click). particleId
        // addressing + the generation stamp make the readback stable and independent of sim timing.
        void PumpReadback()
        {
            if (_effect == null) return;
            // Bind unconditionally while the window is open: the instrumented graph references these
            // globals every time it simulates (the Custom HLSL block's buffers are declared in its
            // kernels), so leaving them unbound when the Particles panel isn't showing triggers
            // "Property (_VfxReadbackGen) ... is not set" warnings on dispatch. Binding is cheap.
            EnsureReadbackBuffer();

            // Switching to a different asset: the shared buffer still holds the previous asset's records
            // and generation stamps. Wipe the gen buffer + decoded caches so nothing from the old asset
            // lingers in the list while the new instances start writing.
            var asset = _effect.visualEffectAsset;
            if (asset != _readbackBufferAsset)
            {
                _readbackBufferAsset = asset;
                _readbackGenBuffer.SetData(new uint[kReadbackCap]);
                _readbackMaxGen = 0; _readbackGen = null; _readbackData = null;
                _particleSelSlots.Clear(); _readbackRows.Clear();
                if (_particleTable != null) { _particleTable.ClearSelection(); _particleTable.RefreshItems(); }
            }

            if (++_readbackGeneration <= 0) _readbackGeneration = 1; // stay positive (0 = unwritten)
            Shader.SetGlobalBuffer(kReadbackBufferId, _readbackBuffer);
            Shader.SetGlobalBuffer(kReadbackGenId, _readbackGenBuffer);
            Shader.SetGlobalInt(kReadbackGenerationId, _readbackGeneration);

            // Only the readback REQUEST needs the spreadsheet on screen.
            if (_particleTable?.panel == null) return;
            AssignReadbackInstanceIds();
            double now = EditorApplication.timeSinceStartup;
            bool due = _readbackAuto && (now - _lastReadbackReq) > 0.15; // ~6 Hz
            if ((_readbackCaptureOnce || due) && !_readbackPending)
            {
                _readbackCaptureOnce = false;
                _lastReadbackReq = now;
                _readbackPending = true;
                AsyncGPUReadback.Request(_readbackGenBuffer, OnReadbackGen); // gen first, then data
            }
        }

        // Decode the per-slot generation stamps, find the latest generation present, then chain the
        // data readback. The latest generation = the most recently simulated frame's particles.
        void OnReadbackGen(AsyncGPUReadbackRequest req)
        {
            if (req.hasError || _readbackGenBuffer == null || _readbackBuffer == null) { _readbackPending = false; return; }
            var gen = req.GetData<uint>();
            if (_readbackGen == null || _readbackGen.Length != gen.Length)
                _readbackGen = new uint[gen.Length];
            gen.CopyTo(_readbackGen);
            _readbackMaxGen = 0;
            for (int i = 0; i < _readbackGen.Length; i++)
                if (_readbackGen[i] > _readbackMaxGen) _readbackMaxGen = _readbackGen[i];
            AsyncGPUReadback.Request(_readbackBuffer, OnReadback);
        }

        void OnReadback(AsyncGPUReadbackRequest req)
        {
            _readbackPending = false;
            if (req.hasError || _readbackBuffer == null) return;
            var data = req.GetData<Vector4>();
            if (_readbackData == null || _readbackData.Length != data.Length)
                _readbackData = new Vector4[data.Length];
            data.CopyTo(_readbackData);
            RefreshParticleTable();
        }

        // Rows = the slots stamped with the latest generation present (the most recently simulated
        // frame's particles); slots from older frames or never written are ignored. Iterating slots
        // ascending yields rows already grouped instance-major then particleId.
        void RefreshParticleTable()
        {
            if (_particleTable?.panel == null) return;
            _readbackRows.Clear();
            int prevInstance = -1;
            _readbackInstanceCount = 0;
            int cap = _readbackData != null ? Mathf.Min(kReadbackCap, _readbackData.Length / kReadbackStride) : 0;
            if (_readbackGen != null && _readbackMaxGen != 0)
                for (int s = 0; s < cap && s < _readbackGen.Length; s++)
                    if (_readbackGen[s] == _readbackMaxGen)
                    {
                        _readbackRows.Add(s);
                        int instance = s / kReadbackPerInstance;
                        if (instance != prevInstance) { _readbackInstanceCount++; prevInstance = instance; }
                    }
            SortReadbackRows(); // keep the user's chosen column sort applied across refreshes
            _particleTable.RefreshItems();

            // Re-pin the selection highlight to the same particles (by slot) after rows reorder; drop any
            // that died, and keep the Scene overlay live while a selection+eye is active.
            if (_particleSelSlots.Count > 0)
            {
                var rows = new List<int>();
                var alive = new List<int>();
                foreach (var slot in _particleSelSlots)
                {
                    int row = _readbackRows.IndexOf(slot);
                    if (row >= 0) { rows.Add(row); alive.Add(slot); }
                }
                _particleSelSlots.Clear();
                _particleSelSlots.AddRange(alive);
                _particleTable.SetSelectionWithoutNotify(rows); // empty → clears the highlight
                if (_particleEyes.Count > 0) SceneView.RepaintAll();
            }

            bool empty = _readbackRows.Count == 0;
            if (_readbackCountLabel != null)
                _readbackCountLabel.text = $"{_readbackRows.Count} · {_readbackInstanceCount} instance{(_readbackInstanceCount == 1 ? "" : "s")}";
            if (_readbackHelp != null) _readbackHelp.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            _particleTable.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;
        }
        // World position of a particle slot: read its stored position, transform by the OWNING instance's
        // transform when the system simulates in Local space, else use as-is. Owner = the selected instance
        // that produced this slot's id; falls back to the primary effect.
        bool TryGetParticleWorld(int slot, out Vector3 world)
        {
            world = default;
            if (_readbackData == null) return false;
            var p = new Vector3(RbVal(slot, 0), RbVal(slot, 1), RbVal(slot, 2));
            int inst = slot / kReadbackPerInstance;
            var owner = inst >= 0 && inst < _readbackSelected.Count ? _readbackSelected[inst] : _effect;
            world = (_readbackPosSpace == 1 && owner != null) // 1 = Local
                ? owner.transform.localToWorldMatrix.MultiplyPoint3x4(p)
                : p;
            return true;
        }

        // Scene overlay: for each selected (live) particle, when ≥1 attribute "eye" is on, draw a dot at
        // the particle's world position + a translucent box listing the eye-ON attributes' values.
        void DrawParticleOverlay()
        {
            if (_particleSelSlots.Count == 0 || _particleEyes.Count == 0 || _readbackData == null) return;
            foreach (var slot in _particleSelSlots)
                if (_readbackRows.Contains(slot)) // still live this generation
                    DrawParticleMarker(slot);
        }

        void DrawParticleMarker(int slot)
        {
            if (!TryGetParticleWorld(slot, out var world)) return;

            // Half-extent from the particle's own size · scale (defaults size≈0.1, scale=1 when unused),
            // and the camera's right/up so the quad faces the viewer and the box hugs its corner.
            float pSize = 0.1f, sx = 1f, sy = 1f, sz = 1f;
            for (int k = 0; k < kReadbackAttrs.Length; k++)
            {
                var a = kReadbackAttrs[k];
                if (a.Layout == "size") pSize = RbVal(slot, a.Float);
                else if (a.Layout == "scaleX")
                { sx = RbVal(slot, a.Float); sy = RbVal(slot, a.Float + 1); sz = RbVal(slot, a.Float + 2); }
            }
            float half = 0.5f * Mathf.Abs(pSize) * Mathf.Max(Mathf.Abs(sx), Mathf.Max(Mathf.Abs(sy), Mathf.Abs(sz)));
            Camera cam = Camera.current;
            Vector3 cr = cam != null ? cam.transform.right : Vector3.right;
            Vector3 cu = cam != null ? cam.transform.up : Vector3.up;

            if (Event.current.type == EventType.Repaint)
            {
                var prev = Handles.color;
                // camera-facing wireframe quad sized by size·scale
                Handles.color = new Color(1f, 1f, 1f, 0.6f);
                Vector3 r = cr * half, u = cu * half;
                Handles.DrawPolyLine(world - r - u, world + r - u, world + r + u, world - r + u, world - r - u);
                // center dot, constant screen size for visibility
                float handle = HandleUtility.GetHandleSize(world);
                Handles.color = Color.white;
                Handles.DotHandleCap(0, world, Quaternion.identity, handle * 0.04f, EventType.Repaint);
                Handles.color = prev;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var a in _readbackCols)
            {
                if (!_particleEyes.Contains(a.Layout)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(a.Title).Append(": ").Append(FormatRbCell(slot, a));
            }
            if (sb.Length == 0) return; // eyes are on attributes not present this asset

            // Anchor the box's bottom-left to the quad's upper-right corner.
            Vector2 corner = HandleUtility.WorldToGUIPoint(world + (cr + cu) * half);
            DrawLabelBoxScreen(corner, sb.ToString(), new Color(0.15f, 0.15f, 0.15f, 0.55f), bottomLeft: true);
        }
    }
}
