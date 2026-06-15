// VFX Control — particle attribute readback (Debug ▸ Particles spreadsheet + scene overlay).
//
// VFX particles are GPU-only with no managed readback, so the graph is instrumented with a
// Custom HLSL block (Readback/VfxReadback.hlsl) that writes a fixed record per particle into a
// shared global GraphicsBuffer; we AsyncGPUReadback it and tabulate the live particles.
//
// Self-contained subsystem (owns its buffers + decoded state + the table/overlay). The window
// feeds it the current selection via SetTarget and drives it: Build (Debug tab body), Pump
// (Tick), DrawOverlay (OnSceneGui), Dispose (OnDisable). Its only outward calls are the static
// VfxGraphReflection layout/space queries and VfxSceneLabel for the overlay value box.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    // Opt-in per-particle attribute spreadsheet + scene overlay. See Readback/VfxReadback.hlsl.
    // The graph is instrumented (Custom HLSL block, one `instanceId` input) so each particle writes
    // its record into a STABLE slot = instanceId*256 + particleId%256, plus a per-frame generation
    // stamp. The window auto-assigns each SELECTED VisualEffect a distinct instanceId via SetInt on
    // the exposed `VfxReadbackInstanceId` property (if wired), so instances land in separate regions.
    // We bump the generation each frame, AsyncGPUReadback the gen+data buffers, and show the slots
    // stamped with the latest generation present (= live particles this frame), grouped by instance.
    internal sealed class VfxParticleReadback : IDisposable
    {
        // Current target, pushed in by the window (SetTarget). _primary drives the asset + fallback
        // owner transform; _all is the selection that gets readable instance ids 0..K-1.
        private VisualEffect _primary;
        private IReadOnlyList<VisualEffect> _all = Array.Empty<VisualEffect>();

        private const int kReadbackPerInstance = 256;    // particle slots per instance (matches the .hlsl)
        private const int kReadbackMaxInstances = 16;    // instance regions per system (matches the .hlsl)
        private const int kReadbackMaxSystems = 8;       // system regions in the buffer (matches the .hlsl)
        private const int kReadbackCap = kReadbackMaxSystems * kReadbackMaxInstances * kReadbackPerInstance; // total slots
        private const int kReadbackFloat4s = kReadbackCap * VfxReadbackRecord.Stride; // float4 buffer length

        // slot = (systemId*kReadbackMaxInstances + instanceId)*kReadbackPerInstance + particleId%kReadbackPerInstance
        private static int SystemOf(int slot) => (slot / kReadbackPerInstance) / kReadbackMaxInstances;
        private static int InstanceOf(int slot) => (slot / kReadbackPerInstance) % kReadbackMaxInstances;
        private static int ParticleIdOf(int slot) => slot % kReadbackPerInstance;
        private static int CountBits(int m) { int c = 0; while (m != 0) { m &= m - 1; c++; } return c; }
        private const string kReadbackInstanceProp = "VfxReadbackInstanceId"; // exposed Int the user wires to the block
        private static readonly int kReadbackBufferId = Shader.PropertyToID("_VfxReadbackBuffer");
        private static readonly int kReadbackGenId = Shader.PropertyToID("_VfxReadbackGen");
        private static readonly int kReadbackGenerationId = Shader.PropertyToID("_VfxReadbackGeneration");
        private GraphicsBuffer _readbackBuffer;          // reusable, created lazily, disposed in Dispose
        private GraphicsBuffer _readbackGenBuffer;       // per-slot generation stamp
        private Vector4[] _readbackData;                 // last decoded raw contents (length kReadbackFloat4s)
        private uint[] _readbackGen;                     // last decoded per-slot generation stamps (length kReadbackCap)
        private uint _readbackMaxGen;                    // latest generation present in the last gen readback
        private int _readbackGeneration = 1;             // current frame id pushed to the shader (>=1; 0 = unwritten)
        private readonly string[] _readbackInstanceNames = new string[kReadbackMaxInstances]; // instanceId → GameObject name
        private readonly List<VisualEffect> _readbackSelected = new List<VisualEffect>(); // selected instances → ids 0..K-1
        private double _lastInstanceAssign;              // throttle for the SetInt instance-id assignment
        private readonly List<int> _readbackRows = new List<int>(); // slots stamped with _readbackMaxGen → table rows
        private bool _readbackPending;                   // an AsyncGPUReadback is in flight

        // Decoded columns: which curated attributes the instrumented system(s) actually use,
        // mapped to fixed float offsets in the .hlsl record — see VfxReadbackRecord (the contract).
        // Columns shown when the graph layout isn't available yet (not compiled): a sensible default.
        private static readonly string[] kReadbackDefaultCols = { "position", "age", "color", "alpha" };
        private readonly List<VfxReadbackRecord.Attr> _readbackCols = new List<VfxReadbackRecord.Attr>(); // active columns for the current asset
        private bool _readbackAuto = true;               // continuous capture while the section is shown
        private bool _readbackCaptureOnce;               // a manual Capture was requested
        private double _lastReadbackReq;                 // throttle (~6 Hz)
        private MultiColumnListView _particleTable;
        private readonly List<Column> _hideableColumns = new List<Column>(); // System/Instance/attrs — everything Show/Hide-all toggles (the # column stays as a row anchor)
        private Label _readbackCountLabel;
        private Label _systemLegend;                     // "systemId → name" hint (multi-system)
        private VisualElement _readbackHelp;

        // System dimension: systemId (wired per VfxReadback block) = index into _systemNames (graph order).
        private List<string> _systemNames = new List<string>();   // systemId → unique system name
        private int[] _systemSpaceById = Array.Empty<int>();      // systemId → sim space (1 Local, else World/none)

        // Scene overlay (Debug ▸ Particles → Scene view): per-attribute "eye" toggles + a selected
        // particle drive a point + value box drawn at the particle's world position.
        private readonly HashSet<string> _particleEyes = new HashSet<string>(); // eye-ON attributes, by VfxReadbackRecord.Attr.Layout
        private readonly List<int> _particleSelSlots = new List<int>(); // selected particle SLOTs (stable; drives the overlay)
        private const int kMaxDebugParticles = 24;       // cap on simultaneously-overlaid particles (perf/clutter)
        private const float kFrameMinSize = 0.5f;        // min world box when framing so a tiny particle doesn't over-zoom
        private VisualEffectAsset _readbackBufferAsset;  // asset whose data is currently in the shared buffer (wipe on change)
        private const string kParticleEyesKeyPrefix = "vfxctrl.particleEyes."; // SessionState, per asset GUID

        // The window pushes the current selection (primary + all instances) whenever it changes.
        public void SetTarget(VisualEffect primary, IReadOnlyList<VisualEffect> all)
        {
            _primary = primary;
            _all = all ?? Array.Empty<VisualEffect>();
            _lastInstanceAssign = 0; // reassign readback instance ids immediately for the new selection
        }

        // Release the GPU buffers (from the window's OnDisable / before a domain reload).
        public void Dispose()
        {
            _readbackBuffer?.Dispose(); _readbackBuffer = null;
            _readbackGenBuffer?.Dispose(); _readbackGenBuffer = null;
        }

        private static VisualElement MakeElement(string cls)
        {
            var ve = new VisualElement();
            ve.AddToClassList(cls);
            return ve;
        }

        // ---- Particle attribute readback spreadsheet (opt-in) ------------------------------
        // Requires the graph to be instrumented with a Custom HLSL block pointing at VfxReadback.hlsl
        // (writes a fixed superset record per particle into a shared global buffer). We bind the buffers,
        // AsyncGPUReadback them, and tabulate the live particles. Columns are driven by each system's
        // actual attribute layout, so only the attributes the system really uses are shown.
        public void Build(VisualElement host)
        {
            _particleTable = null;
            _hideableColumns.Clear();
            _readbackCountLabel = null;
            _systemLegend = null;
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

            // Multi-system legend: which systemId to wire into each block (shown only when >1 system).
            _systemLegend = new Label();
            _systemLegend.AddToClassList("vfx-particles-legend");
            host.Add(_systemLegend);
            UpdateSystemLegend();

            // The spreadsheet. Clicking a column header sorts by it (toggles asc/desc); the sort is
            // re-applied on every readback so it sticks as the data refreshes. Multi-row selection drives
            // the Scene overlay (tracked by stable slots, see OnParticleSelectionChanged; capped at
            // kMaxDebugParticles). Ctrl/Shift-click to select several particles; Alt+click frames the
            // selection in the Scene view.
            var table = new MultiColumnListView { showBoundCollectionSize = false, sortingMode = ColumnSortingMode.Custom };
            table.columnSortingChanged += () => { SortReadbackRows(); table.RefreshItems(); };
            table.selectionType = SelectionType.Multiple;
            table.selectionChanged += _ => OnParticleSelectionChanged();
            // Alt+click a row → frame it in the Scene view. Deferred so the ListView's selection has
            // settled first (and so re-Alt+clicking an already-selected row still frames it).
            table.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.altKey) table.schedule.Execute(FrameSelectedParticles).ExecuteLater(0);
            }, TrickleDown.TrickleDown);
            table.tooltip = "Alt+click a row to frame the particle in the Scene view.";
            table.AddToClassList("vfx-particles-table");
            // System/Instance/# carry the same right-click menu (Show/Hide all). The # column is never
            // hidden (it's the row anchor + always-reachable menu host); System/Instance/attrs are.
            var systemColumn = new Column
            {
                title = "System", width = 120, minWidth = 120, makeHeader = () => MakeMenuHeader("System"), makeCell = () => MakeCell(),
                bindCell = (e, i) =>
                {
                    int sys = SystemOf(_readbackRows[i]);
                    ((Label)e).text = sys < _systemNames.Count ? _systemNames[sys] : $"System {sys}";
                }
            };
            _hideableColumns.Add(systemColumn);
            table.columns.Add(systemColumn);
            var instanceColumn = new Column
            {
                title = "Instance", width = 110, minWidth = 110, makeHeader = () => MakeMenuHeader("Instance"), makeCell = () => MakeCell(),
                bindCell = (e, i) =>
                {
                    int inst = InstanceOf(_readbackRows[i]);
                    string nm = inst < _readbackInstanceNames.Length ? _readbackInstanceNames[inst] : null;
                    ((Label)e).text = nm ?? inst.ToString();
                }
            };
            _hideableColumns.Add(instanceColumn);
            table.columns.Add(instanceColumn);
            table.columns.Add(new Column { title = "#", width = 44, minWidth = 44, makeHeader = () => MakeMenuHeader("#"), makeCell = () => MakeCell(), bindCell = (e, i) => ((Label)e).text = ParticleIdOf(_readbackRows[i]).ToString() });
            foreach (var a in _readbackCols)
            {
                var attr = a; // capture per-iteration
                Column col;
                if (attr.Kind == VfxReadbackRecord.Kind.Color)
                    col = new Column
                    {
                        title = attr.Title, width = 150, minWidth = 150,
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
                    };
                else
                {
                    int w = attr.Count == 3 ? 170 : 70;
                    col = new Column
                    {
                        title = attr.Title, width = w, minWidth = w,
                        makeHeader = () => MakeAttrHeader(attr), bindHeader = e => UpdateEyeVisual(e, attr),
                        makeCell = () => MakeCell(),
                        bindCell = (e, i) => ((Label)e).text = FormatRbCell(_readbackRows[i], attr)
                    };
                }
                _hideableColumns.Add(col);
                table.columns.Add(col);
            }
            table.itemsSource = _readbackRows;
            _particleTable = table;
            host.Add(table);

            // Columns keep their (min)widths above, so when they're wider than the panel the table
            // overflows; show a horizontal scrollbar on the control's internal ScrollView so it can be
            // scrolled sideways (header + rows scroll together) instead of compressing the columns.
            var scroll = table.Q<ScrollView>();
            if (scroll != null)
            {
                scroll.mode = ScrollViewMode.VerticalAndHorizontal;
                scroll.horizontalScrollerVisibility = ScrollerVisibility.Auto;
            }

            // Empty / not-instrumented state.
            _readbackHelp = MakeElement("vfx-helpbox");
            _readbackHelp.Add(new Label(
                "No readback data. Add a Custom HLSL block (function VfxReadback) pointing at " +
                "Assets/VfxControl/Readback/VfxReadback.hlsl in this system's Update or Output context. For " +
                "separate per-instance rows, expose an Int property named VfxReadbackInstanceId and wire it to " +
                "the block's instanceId input (the window auto-assigns ids). To debug several systems at once, " +
                "put a block in each system and wire each block's systemId to a distinct constant per the legend. " +
                "Only public APIs — see the docs."));
            host.Add(_readbackHelp);

            RefreshParticleTable();
        }

        // Reorder _readbackRows by the column the user clicked (MultiColumnListView in Custom sorting
        // mode just reports the selected columns; we do the actual sort over our row→slot list).
        private void SortReadbackRows()
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
        private double ParticleSortKey(int slot, int col) => VfxReadbackRecord.SortKey(_readbackData, _readbackCols, slot, col, kReadbackPerInstance, kReadbackMaxInstances);

        // Pick the active columns: the curated attributes the asset's systems actually store (union of
        // GetSystemAttributeLayout across systems); falls back to a default set when the layout is empty
        // (graph not compiled this session). Order follows VfxReadbackRecord.Attrs.
        private void BuildReadbackColumns()
        {
            _readbackCols.Clear();
            var asset = _primary != null ? _primary.visualEffectAsset : null;
            var spaces = asset != null ? VfxGraphReflection.GetSystemSpaces(asset) : null;
            var layout = asset != null ? VfxGraphReflection.GetSystemAttributeLayout(asset) : null;

            // systemId (wired per block) = index into this ordered system list; it also drives the
            // System column name, the legend, and the per-system sim space. Source the order + space
            // from GetSystemSpaces, which reads the serialized per-system space and does NOT need the
            // graph to be compiled — so a Local-space system resolves correctly right after a domain
            // reload (the attribute layout is [NonSerialized] and empty until recompile, which used to
            // zero this list and make Local particles fall back to World until the .vfx was re-saved).
            // Fall back to the layout's system order (space unknown → World) only if spaces are absent.
            _systemNames = new List<string>();
            var spaceList = new List<int>();
            if (spaces != null && spaces.Count > 0)
                foreach (var kv in spaces) { _systemNames.Add(kv.Key); spaceList.Add(kv.Value); }
            else if (layout != null)
                foreach (var kv in layout) { _systemNames.Add(kv.Key); spaceList.Add(2); }
            _systemSpaceById = spaceList.ToArray();

            // Columns = attributes the systems actually store (needs the compiled layout); fall back to
            // a default set when it isn't available yet (graph not compiled this session).
            var present = new HashSet<string>();
            if (layout != null)
                foreach (var kv in layout)
                    foreach (var f in kv.Value) present.Add(f.Name);

            foreach (var a in VfxReadbackRecord.Attrs)
            {
                bool show = present.Count > 0 ? present.Contains(a.Layout)
                                              : Array.IndexOf(kReadbackDefaultCols, a.Layout) >= 0;
                if (show) _readbackCols.Add(a);
            }

            LoadParticleEyes(asset);
        }

        // "Wire each block's systemId — 0: Sparks · 1: Smoke" (hidden for 0/1 systems). Tells the user
        // which constant to type into each VfxReadback block so the System column reads the real name.
        private void UpdateSystemLegend()
        {
            if (_systemLegend == null) return;
            bool show = _systemNames.Count > 1;
            _systemLegend.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;
            var sb = new System.Text.StringBuilder("Wire each block's systemId — ");
            for (int i = 0; i < _systemNames.Count && i < kReadbackMaxSystems; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(i).Append(": ").Append(_systemNames[i]);
            }
            _systemLegend.text = sb.ToString();
        }

        // Eye state is persisted per asset GUID in SessionState (survives recompiles), default empty.
        private void LoadParticleEyes(VisualEffectAsset asset)
        {
            _particleEyes.Clear();
            string guid = asset != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)) : null;
            if (string.IsNullOrEmpty(guid)) return;
            string csv = SessionState.GetString(kParticleEyesKeyPrefix + guid, "");
            if (csv.Length == 0) return;
            foreach (var s in csv.Split(',')) if (s.Length > 0) _particleEyes.Add(s);
        }

        private void SaveParticleEyes()
        {
            var asset = _primary != null ? _primary.visualEffectAsset : null;
            string guid = asset != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)) : null;
            if (string.IsNullOrEmpty(guid)) return;
            SessionState.SetString(kParticleEyesKeyPrefix + guid, string.Join(",", _particleEyes));
        }

        // Column header = attribute name + an "eye" toggle. When the eye is on, the selected particle's
        // value for this attribute is drawn in the Scene overlay. The eye stops pointer propagation so it
        // doesn't trigger the header's column sort.
        private VisualElement MakeAttrHeader(VfxReadbackRecord.Attr attr)
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
            AttachColumnVisibilityMenu(h);
            UpdateEyeVisual(h, attr);
            return h;
        }

        private void UpdateEyeVisual(VisualElement header, VfxReadbackRecord.Attr attr)
        {
            var eye = header.Q(className: "vfx-eye");
            eye?.EnableInClassList("vfx-eye--on", _particleEyes.Contains(attr.Layout));
        }

        // A plain text header (Instance / #) that still carries the Show/Hide-all right-click menu.
        private VisualElement MakeMenuHeader(string title)
        {
            var l = new Label(title);
            l.AddToClassList("vfx-particles-header-label");
            AttachColumnVisibilityMenu(l);
            return l;
        }

        // Right-click a column title → show/hide every column at once (so isolating one or two doesn't
        // mean toggling many by hand). "Hide" keeps the # column as the row anchor. Composes with the
        // built-in MultiColumnListView header menu (per-column toggles + reorder), shown below these.
        private void AttachColumnVisibilityMenu(VisualElement header)
        {
            header.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Show All Columns", _ => SetAllColumnsVisible(true));
                evt.menu.AppendAction("Hide All Columns (keep #)", _ => SetAllColumnsVisible(false));
            }));
        }

        // Toggles System/Instance/attribute columns; the # column is intentionally excluded so the
        // table keeps a row anchor and the (always-visible) # header keeps hosting this menu.
        private void SetAllColumnsVisible(bool visible)
        {
            foreach (var c in _hideableColumns) c.visible = visible;
        }

        // Track the selection by stable SLOTs (not row indices): rows reorder on sort/refresh, so the
        // slots keep the overlay pinned to the same particles. Capped at kMaxDebugParticles.
        private void OnParticleSelectionChanged()
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

        // World-space AABB of a particle, sized from its own size · scale (same extent the overlay
        // draws). False for a slot whose position can't be read (dead/empty this generation).
        private bool TryGetParticleBounds(int slot, out Bounds bounds)
        {
            bounds = default;
            if (!TryGetParticleWorld(slot, out var world)) return false;
            bounds = new Bounds(world, Vector3.one * (2f * ParticleHalfExtent(slot)));
            return true;
        }

        // Frame the Scene View on the combined bounds of the selected particles (Alt+click a row).
        // A minimum box keeps a tiny particle from zooming to an extreme close-up; live slots only.
        private void FrameSelectedParticles()
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || _particleSelSlots.Count == 0) return;

            bool any = false; Bounds total = default;
            foreach (int slot in _particleSelSlots)
                if (TryGetParticleBounds(slot, out var b))
                {
                    if (!any) { total = b; any = true; }
                    else total.Encapsulate(b);
                }
            if (!any) return;

            total.size = Vector3.Max(total.size, Vector3.one * kFrameMinSize);
            sv.Frame(total, instant: false); // smooth pan/zoom; SceneView.Frame is public API
        }

        // Read one float of a particle's record (delegates to VfxReadbackRecord.Val over the buffer).
        private float RbVal(int slot, int floatIndex) => VfxReadbackRecord.Val(_readbackData, slot, floatIndex);

        // Text for a non-color attribute cell.
        private string FormatRbCell(int slot, VfxReadbackRecord.Attr a) => VfxReadbackRecord.Format(_readbackData, a, slot);

        private static Label MakeCell()
        {
            var l = new Label();
            l.AddToClassList("vfx-particles-cell");
            return l;
        }

        private static VisualElement MakeColorCell()
        {
            var row = MakeElement("vfx-particles-colorcell");
            row.Add(MakeElement("vfx-particles-swatch"));
            var l = new Label();
            l.AddToClassList("vfx-particles-cell");
            row.Add(l);
            return row;
        }

        // Lazily (re)create the data + generation buffers, both zero-initialised (gen 0 = "never written").
        private void EnsureReadbackBuffer()
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

        // Only the SELECTED instances (_all) get readable ids 0..K-1 via the exposed
        // `VfxReadbackInstanceId` Int — every other VisualEffect of the asset is pushed out of range
        // (id == kReadbackMaxInstances) so the instrumented block skips it and it never pollutes the
        // regions we read. Select one effect → see only it; select two → see both. SetInt persists, so
        // this is throttled (~2 Hz; forced to re-run on selection change via _lastInstanceAssign = 0).
        // Stable id per effect by GetEntityId. Components without the property wired (HasInt false) can't
        // be steered — they fall back to the port default (0); wire the property for the selection filter.
        private void AssignReadbackInstanceIds()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastInstanceAssign < 0.5 && _readbackInstanceNames[0] != null) return;
            _lastInstanceAssign = now;
            Array.Clear(_readbackInstanceNames, 0, _readbackInstanceNames.Length);

            var asset = _primary.visualEffectAsset;
            if (asset == null) return;

            // Selected instances, sorted by entity id for a stable id assignment.
            _readbackSelected.Clear();
            foreach (var ve in _all) if (ve != null) _readbackSelected.Add(ve);
            if (_readbackSelected.Count == 0) _readbackSelected.Add(_primary);
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
            foreach (var v in Object.FindObjectsByType<VisualEffect>(FindObjectsInactive.Exclude))
            {
                if (v == null || !v.HasInt(kReadbackInstanceProp)) continue;
                v.SetInt(kReadbackInstanceProp, idOf.TryGetValue(v, out var id) ? id : kReadbackMaxInstances);
            }
        }

        // Driven by Tick: bump the generation, keep the globals bound while the window is open, and
        // issue throttled readbacks (continuous when Auto, or once per Capture click). particleId
        // addressing + the generation stamp make the readback stable and independent of sim timing.
        public void Pump()
        {
            if (_primary == null) return;
            // Bind unconditionally while the window is open: the instrumented graph references these
            // globals every time it simulates (the Custom HLSL block's buffers are declared in its
            // kernels), so leaving them unbound when the Particles panel isn't showing triggers
            // "Property (_VfxReadbackGen) ... is not set" warnings on dispatch. Binding is cheap.
            EnsureReadbackBuffer();

            // Switching to a different asset: the shared buffer still holds the previous asset's records
            // and generation stamps. Wipe the gen buffer + decoded caches so nothing from the old asset
            // lingers in the list while the new instances start writing.
            var asset = _primary.visualEffectAsset;
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
        private void OnReadbackGen(AsyncGPUReadbackRequest req)
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

        private void OnReadback(AsyncGPUReadbackRequest req)
        {
            _readbackPending = false;
            if (req.hasError || _readbackBuffer == null) return;
            var data = req.GetData<Vector4>();
            if (_readbackData == null || _readbackData.Length != data.Length)
                _readbackData = new Vector4[data.Length];
            data.CopyTo(_readbackData);
            RefreshParticleTable();
        }

        // Total live particles across the selected instances (public runtime API) — the "all dead"
        // signal used to clear the table when a system fully empties.
        private int LiveAliveCount()
        {
            int n = 0;
            if (_all != null) foreach (var ve in _all) if (ve != null) n += ve.aliveParticleCount;
            if (n == 0 && _primary != null) n = _primary.aliveParticleCount;
            return n;
        }

        // Rows = the slots stamped with the latest generation present (the most recently simulated
        // frame's particles); slots from older frames or never written are ignored. Iterating slots
        // ascending yields rows already grouped instance-major then particleId.
        private void RefreshParticleTable()
        {
            if (_particleTable?.panel == null) return;
            _readbackRows.Clear();
            int cap = _readbackData != null ? Mathf.Min(kReadbackCap, _readbackData.Length / VfxReadbackRecord.Stride) : 0;
            if (_readbackGen != null && _readbackMaxGen != 0)
                for (int s = 0; s < cap && s < _readbackGen.Length; s++)
                    if (_readbackGen[s] == _readbackMaxGen)
                        _readbackRows.Add(s);

            // The generation stamp is "newest present in the buffer", so a system that has fully
            // emptied would otherwise freeze on its last live frame (no slot re-stamps a newer gen).
            // Public aliveParticleCount is a reliable "all dead" signal: drop the stale rows so the
            // table goes empty rather than showing dead particles.
            if (_readbackRows.Count > 0 && LiveAliveCount() == 0)
                _readbackRows.Clear();

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
            {
                int sysMask = 0, instMask = 0; // SystemOf ≤ 7, InstanceOf ≤ 15 → fit in an int bitmask
                foreach (var slot in _readbackRows) { sysMask |= 1 << SystemOf(slot); instMask |= 1 << InstanceOf(slot); }
                int sys = CountBits(sysMask), inst = CountBits(instMask);
                _readbackCountLabel.text = $"{_readbackRows.Count} · {sys} system{(sys == 1 ? "" : "s")} · {inst} instance{(inst == 1 ? "" : "s")}";
            }
            if (_readbackHelp != null) _readbackHelp.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            _particleTable.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // World position of a particle slot: read its stored position, transform by the OWNING instance's
        // transform when the system simulates in Local space, else use as-is. Owner = the selected instance
        // that produced this slot's id; falls back to the primary effect.
        private bool TryGetParticleWorld(int slot, out Vector3 world)
        {
            world = default;
            if (_readbackData == null) return false;
            var p = new Vector3(RbVal(slot, 0), RbVal(slot, 1), RbVal(slot, 2));
            int inst = InstanceOf(slot);
            var owner = inst >= 0 && inst < _readbackSelected.Count ? _readbackSelected[inst] : _primary;
            int sys = SystemOf(slot);
            int space = sys >= 0 && sys < _systemSpaceById.Length ? _systemSpaceById[sys] : 2; // World default
            world = (space == 1 && owner != null) // 1 = Local → transform by the owning instance
                ? owner.transform.localToWorldMatrix.MultiplyPoint3x4(p)
                : p;
            return true;
        }

        // Scene overlay: for each selected (live) particle, when ≥1 attribute "eye" is on, draw a dot at
        // the particle's world position + a translucent box listing the eye-ON attributes' values.
        public void DrawOverlay()
        {
            if (_particleSelSlots.Count == 0 || _particleEyes.Count == 0 || _readbackData == null) return;
            foreach (var slot in _particleSelSlots)
                if (_readbackRows.Contains(slot)) // still live this generation
                    DrawParticleMarker(slot);
        }

        // Half-extent of a particle from its own size · scale (defaults size≈0.1, scale=1 when unused).
        private float ParticleHalfExtent(int slot)
        {
            float pSize = 0.1f, sx = 1f, sy = 1f, sz = 1f;
            foreach (var a in VfxReadbackRecord.Attrs)
            {
                if (a.Layout == "size") pSize = RbVal(slot, a.Float);
                else if (a.Layout == "scaleX")
                { sx = RbVal(slot, a.Float); sy = RbVal(slot, a.Float + 1); sz = RbVal(slot, a.Float + 2); }
            }
            return 0.5f * Mathf.Abs(pSize) * Mathf.Max(Mathf.Abs(sx), Mathf.Max(Mathf.Abs(sy), Mathf.Abs(sz)));
        }

        private void DrawParticleMarker(int slot)
        {
            if (!TryGetParticleWorld(slot, out var world)) return;

            // Half-extent from the particle's own size · scale; the camera's right/up so the quad
            // faces the viewer and the box hugs its corner.
            float half = ParticleHalfExtent(slot);
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
            VfxSceneLabel.DrawBox(corner, sb.ToString(), new Color(0.15f, 0.15f, 0.15f, 0.55f), bottomLeft: true);
        }
    }
}
