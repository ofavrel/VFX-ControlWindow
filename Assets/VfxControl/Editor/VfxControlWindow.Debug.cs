// VFX Control — Debug tab (partial of VfxControlWindow).
//
// Live runtime statistics (alive/efficiency/bounds/state), CPU/GPU profiling markers,
// per-system capacity bars + attribute-layout/memory, texture usage, and the Show Bounds
// visualizer. Live counts are public runtime API; the profiling/layout extras are reached
// by reflection (VfxGraphReflection) and degrade to "—". The particle spreadsheet lives in
// the Readback partial. Split out of VfxControlWindow.cs — same class (partial), shared state.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UIElements;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public partial class VfxControlWindow : EditorWindow
    {
        // --- Debug tab live stat refs (rebuilt with the body; refreshed in place by UpdateLive) ---
        private Label _dbgAlive, _dbgAliveCap, _dbgEff, _dbgSystems, _dbgBounds, _dbgState;
        private Label _dbgCpu, _dbgGpu, _dbgAttrMem; // effect CPU/GPU time (ms) + total attribute memory
        private Label _dbgTeaser; // All-tab "Debug" shortcut summary (one label, refreshed live)

        private readonly List<string> _dbgSysNames = new List<string>(); // reused buffer (no per-tick alloc)
        // Per-system capacity-bar rows (Systems section), refreshed in place; order matches _dbgSysNames.
        private readonly List<(string name, Label num, VisualElement fill, VisualElement row, Label detail)> _dbgSysRows
            = new List<(string, Label, VisualElement, VisualElement, Label)>();
        // Profiler Recorders keyed by marker name (created+enabled lazily); reset on target change.
        private readonly Dictionary<string, Recorder> _recorders = new Dictionary<string, Recorder>();
        // Light EMA per marker (nanoseconds) so the µs readouts don't flicker frame-to-frame.
        private readonly Dictionary<string, double> _smoothNs = new Dictionary<string, double>();

        private const double kTimerSmooth = 0.2; // EMA weight on the newest sample (~0.2s settle at 30fps)
        // The effect we've registered for profiling (per-system CPU/GPU markers need this); unregistered
        // on target change / window close. Registered on-demand while Debug timing is on screen.
        private VisualEffect _profilingEffect;
        // Per-system attribute-buffer stride in 32-bit words (Σ bucket sizes); from the graph layout.
        // Computed per body build (static per asset); empty entries → "—" (layout not yet compiled).
        private Dictionary<string, int> _attrWords = new Dictionary<string, int>();

        private Toggle _showBoundsToggle; // the Visualizers "Show Bounds" checkbox (resynced each tick)
        // Show Bounds is SHARED across all windows (read straight from SessionState, not cached) so a
        // torn-off Debug tab stays in sync with the main window — both the scene draw and the
        // checkbox (UpdateLive resyncs the toggle when another window flips it).
        private const string kShowBoundsKey = "vfxctrl.showBounds";

        private bool ShowBounds
        {
            get => SessionState.GetBool(kShowBoundsKey, false);
            set => SessionState.SetBool(kShowBoundsKey, value);
        }
        // ---- Debug tab (live runtime stats) -------------------------------------------------
        // Rail-filtered sections (handoff "Tab: Debug"): the live stat grid, per-system capacity
        // bars, and scene visualizers. All values are public runtime API on VisualEffect — no
        // reflection. Live values refresh in place off the ~30fps clock (RefreshDebugStats).
        private void BuildDebugTab(VisualElement body)
        {
            _dbgSysRows.Clear(); // discard refs from the prior body before rebuilding
            if (_effect == null) { BuildPlaceholder(body, "No Visual Effect selected."); return; }

            // Attribute layout is static per asset — read it once per body build (not per tick).
            _attrWords = VfxGraphReflection.GetSystemAttributeWords(_effect.visualEffectAsset);

            string section = CurrentSection();
            bool InSection(string id) => section == "all" || section == id;

            if (InSection("live"))
                AddDebugGroup(body, "live", "Live statistics", BuildDebugStatsGrid);
            if (InSection("systems"))
                AddDebugGroup(body, "systems", "Systems", BuildDebugSystems);
            if (InSection("textures"))
                AddDebugGroup(body, "textures", "Textures", BuildDebugTextures);
            if (InSection("particles"))
                AddDebugGroup(body, "particles", "Particles", _readback.Build);
            if (InSection("visualizers"))
                AddDebugGroup(body, "visualizers", "Visualizers", BuildDebugVisualizers);

            RefreshDebugStats(); // groups are now attached — fill values for a clean first paint
        }

        // A collapsible Debug section group (the shared .vfx-group chrome; collapse key debug:<id>).
        private void AddDebugGroup(VisualElement host, string id, string heading, Action<VisualElement> buildContent)
        {
            var (_, content, open) = AddGroupShell(host, "debug:" + id, heading);
            if (open) buildContent(content);
        }

        // The 2-column stat grid. Sets the _dbg* label refs (cleared first so a stale refresh from
        // a prior body can't touch detached labels), then fills them with one immediate read.
        private void BuildDebugStatsGrid(VisualElement host)
        {
            _dbgAlive = _dbgAliveCap = _dbgEff = _dbgSystems = _dbgBounds = _dbgState = null;
            _dbgCpu = _dbgGpu = _dbgAttrMem = null;

            var grid = MakeElement("vfx-stat-grid");
            _dbgAlive   = MakeStat(grid, "Alive particles", out _dbgAliveCap,
                                   "Living particles summed across all systems, over total capacity.");
            _dbgEff     = MakeStat(grid, "Efficiency", out _,
                                   "Alive / capacity across all systems — how much allocated budget is in use.");
            _dbgSystems = MakeStat(grid, "Systems", out _, "Number of particle systems in this effect.");
            _dbgBounds  = MakeStat(grid, "Bounds", out _, "World-space render bounds size (X × Y × Z) from the VFXRenderer.");
            _dbgState   = MakeStat(grid, "State", out _, "Whether the effect is playing, paused, or culled from rendering.");
            _dbgCpu     = MakeStat(grid, "CPU time", out _, "CPU evaluation time for the whole effect this frame (profiler Recorder).");
            _dbgGpu     = MakeStat(grid, "GPU time", out _,
                                   "GPU time across all systems this frame. Needs a GPU recorder (SystemInfo.supportsGpuRecorder); shows “—” where unsupported.");
            _dbgAttrMem = MakeStat(grid, "Attr memory", out _,
                                   "Total attribute-buffer memory across systems (capacity × per-particle stride). “—” until the graph is compiled/opened.");

            host.Add(grid);
            RefreshDebugStats();
        }

        // One stat cell: an uppercase key over a value (+ optional dim unit/suffix label).
        // Returns the value label; the unit label comes back via `unit`.
        private Label MakeStat(VisualElement grid, string key, out Label unit, string tooltip)
        {
            var cell = MakeElement("vfx-stat");
            cell.tooltip = tooltip;
            var k = new Label(key.ToUpperInvariant());
            k.AddToClassList("vfx-stat-k");
            cell.Add(k);
            var vrow = MakeElement("vfx-stat-vrow");
            var v = new Label("—");
            v.AddToClassList("vfx-stat-v");
            vrow.Add(v);
            unit = new Label();
            unit.AddToClassList("vfx-stat-u");
            vrow.Add(unit);
            cell.Add(vrow);
            grid.Add(cell);
            return v;
        }

        // Per-system capacity bars (mirrors the package's profiler occupancy section). Each row =
        // system name + alive/capacity numbers + a bar whose fill width is alive/capacity and whose
        // colour follows the VFX efficiency convention (red under-used → green well-utilised). Rows
        // are cached in _dbgSysRows and refreshed in place; systems are stable until the asset
        // recompiles (→ a full Rebuild), so the row set is built once here.
        private void BuildDebugSystems(VisualElement host)
        {
            _dbgSysRows.Clear();
            _effect.GetParticleSystemNames(_dbgSysNames);
            if (_dbgSysNames.Count == 0)
            {
                var empty = new Label("This effect has no particle systems.");
                empty.AddToClassList("vfx-sys-empty");
                host.Add(empty);
                return;
            }

            // Full attribute layout per system (static per compiled asset; the breakdown behind "mem").
            var layout = VfxGraphReflection.GetSystemAttributeLayout(_effect.visualEffectAsset);

            var list = MakeElement("vfx-syslist");
            foreach (var name in _dbgSysNames)
            {
                var row = MakeElement("vfx-sys");
                var top = MakeElement("vfx-sys-top");
                var nameLbl = new Label(name);
                nameLbl.AddToClassList("vfx-sys-name");
                top.Add(nameLbl);
                var num = new Label("—");
                num.AddToClassList("vfx-sys-num");
                top.Add(num);
                row.Add(top);

                var bar = MakeElement("vfx-sys-bar");
                var fill = MakeElement("vfx-sys-fill");
                bar.Add(fill);
                row.Add(bar);

                var detail = new Label();
                detail.AddToClassList("vfx-sys-detail");
                row.Add(detail);

                if (layout.TryGetValue(name, out var fields) && fields.Count > 0)
                    row.Add(BuildAttrLayoutFoldout(fields));

                list.Add(row);
                _dbgSysRows.Add((name, num, fill, row, detail));
            }
            host.Add(list);
            RefreshDebugStats();
        }

        // Collapsible per-system attribute layout: name · type · per-particle bytes, in buffer order,
        // with a header summarising the count and per-particle stride (matches the system's "mem"/cap).
        private static VisualElement BuildAttrLayoutFoldout(List<VfxGraphReflection.VfxAttrField> fields)
        {
            int words = 0;
            foreach (var f in fields) words += f.Words;
            var fold = new Foldout { value = false, text = $"Layout · {fields.Count} attrs · {words * 4} B/particle" };
            fold.AddToClassList("vfx-attr-fold");
            foreach (var f in fields)
            {
                var ar = MakeElement("vfx-attr-row");
                var an = new Label(f.Name); an.AddToClassList("vfx-attr-name");
                var at = new Label(f.Type); at.AddToClassList("vfx-attr-type");
                var ab = new Label($"{f.Words * 4} B"); ab.AddToClassList("vfx-attr-bytes");
                ar.Add(an); ar.Add(at); ar.Add(ab);
                fold.Add(ar);
            }
            return fold;
        }

        // Texture usage: every texture wired into the graph (exposed or not), via reflection over the
        // graph slots (mirrors the VFX profiler). Static per asset, so no live refresh. Rows are
        // name · resolution · size (public Profiler.GetRuntimeMemorySizeLong), biggest first, with a
        // total; clicking a row pings the texture asset.
        private void BuildDebugTextures(VisualElement host)
        {
            var textures = VfxGraphReflection.GetTextureUsage(_effect.visualEffectAsset);
            if (textures.Count == 0)
            {
                var empty = new Label("No textures found (or the graph isn’t reachable).");
                empty.AddToClassList("vfx-sys-empty");
                host.Add(empty);
                return;
            }

            var list = MakeElement("vfx-texlist");
            long total = 0;
            foreach (var tex in textures.OrderByDescending(Profiler.GetRuntimeMemorySizeLong))
            {
                long bytes = Profiler.GetRuntimeMemorySizeLong(tex);
                total += bytes;

                var row = MakeElement("vfx-tex");
                row.tooltip = "Click to ping the texture in the Project window.";
                var name = new Label(tex.name);
                name.AddToClassList("vfx-tex-name");
                var res = new Label(TextureResolution(tex));
                res.AddToClassList("vfx-tex-res");
                var size = new Label(EditorUtility.FormatBytes(bytes));
                size.AddToClassList("vfx-tex-size");
                row.Add(name); row.Add(res); row.Add(size);
                row.RegisterCallback<ClickEvent>(_ => EditorGUIUtility.PingObject(tex));
                list.Add(row);
            }
            host.Add(list);

            var totalRow = MakeElement("vfx-tex-total");
            var tl = new Label($"{textures.Count} texture{(textures.Count == 1 ? "" : "s")}");
            tl.AddToClassList("vfx-tex-name");
            var tv = new Label(EditorUtility.FormatBytes(total));
            tv.AddToClassList("vfx-tex-size");
            totalRow.Add(tl); totalRow.Add(tv);
            host.Add(totalRow);
        }

        private static string TextureResolution(Texture t)
        {
            switch (t)
            {
                case Texture3D t3: return $"{t3.width}×{t3.height}×{t3.depth}";
                case Cubemap c: return $"{c.width}×{c.height} cube";
                default: return $"{t.width}×{t.height}";
            }
        }

        // Visualizers: scene-view debug draws. "Show Bounds" draws the world-space render bounds
        // (the only one reachable via public API — spawn-icons/wireframe/motion-vectors map to VFX
        // debug visualizers that are internal, so they're omitted rather than faked).
        private void BuildDebugVisualizers(VisualElement host)
        {
            var row = MakeElement("vfx-toggle-row");
            var toggle = new Toggle { value = ShowBounds };
            _showBoundsToggle = toggle;
            toggle.AddToClassList("vfx-toggle-check");
            var col = MakeElement("vfx-toggle-text");
            var label = new Label("Show Bounds");
            label.AddToClassList("vfx-tg-label");
            col.Add(label);
            var sub = new Label("Draw the world-space render bounds in the Scene view.");
            sub.AddToClassList("vfx-tg-sub");
            col.Add(sub);
            row.Add(toggle);
            row.Add(col);
            // Click anywhere on the row toggles (the prototype affordance). Skip clicks within the
            // toggle itself — it flips on its own, so flipping again here would cancel it out.
            row.RegisterCallback<ClickEvent>(e =>
            {
                if (e.target is VisualElement t && (t == toggle || toggle.Contains(t))) return;
                toggle.value = !toggle.value;
            });
            toggle.RegisterValueChangedCallback(e =>
            {
                ShowBounds = e.newValue; // shared via SessionState — other windows resync in UpdateLive
                SceneView.RepaintAll();
            });
            host.Add(row);
        }


        // VFX efficiency convention (VFXAnchoredProfilerUI.ComputeEfficiencyColor): how well the
        // allocated capacity is used — under ~50% reads as over-allocated (hot/red), ~90%+ as
        // well-utilised (cold/green).
        private static Color EfficiencyColor(float efficiency) =>
            efficiency < 0.51f ? VfxPropertyLayout.Hex("#ff2e2e") :
            efficiency < 0.91f ? VfxPropertyLayout.Hex("#e3a98b") :
                                 VfxPropertyLayout.Hex("#00ff47");

        // Adaptive time format: ms for ≥1 ms, else microseconds (VFX per-system costs are usually a
        // few µs, which "0.00 ms" can't show). "—" for unavailable (NaN).
        private static string FmtMs(double ms)
        {
            if (double.IsNaN(ms)) return "—";
            if (ms >= 1.0) return $"{ms:0.00} ms";
            if (ms > 0.0) return $"{ms * 1000.0:0.#} µs";
            return "0 µs";
        }

        // Live refresh, driven by the ~30fps clock in UpdateLive. Each consumer (stat grid, All-tab
        // teaser, per-system bars) is guarded on its widgets being attached, so this no-ops for
        // whichever aren't the current body.
        private void RefreshDebugStats()
        {
            if (_effect == null) return;
            bool gridLive = _dbgAlive?.panel != null;     // Debug tab stat grid is the current body
            bool teaserLive = _dbgTeaser?.panel != null;  // All-tab Debug shortcut is the current body
            bool rowsLive = _dbgSysRows.Count > 0 && _dbgSysRows[0].fill?.panel != null; // Systems section
            if (!gridLive && !teaserLive && !rowsLive) return;

            // CPU/GPU per-system (and effect) markers are only emitted while the component is
            // registered for profiling — do it on demand while the timing readouts are on screen.
            if (gridLive || rowsLive) EnsureProfiling();

            bool gpuOk = SystemInfo.supportsGpuRecorder;
            _effect.GetParticleSystemNames(_dbgSysNames);
            int systems = _dbgSysNames.Count;
            long alive = 0, capacity = 0, attrBytesTotal = 0;
            double gpuMsTotal = 0; bool anyGpu = false, anyAttr = false;

            for (int i = 0; i < _dbgSysNames.Count; i++)
            {
                var name = _dbgSysNames[i];
                var info = _effect.GetParticleSystemInfo(name);
                alive += info.aliveCount;
                capacity += info.capacity;

                double cpuMs = MarkerMs(VfxGraphReflection.CpuSystemMarker(_effect, name), gpu: false);
                double gpuMs = gpuOk ? SumGpuMs(name) : double.NaN;
                if (!double.IsNaN(gpuMs)) { gpuMsTotal += gpuMs; anyGpu = true; }

                long attrBytes = -1;
                if (_attrWords.TryGetValue(name, out int words))
                {
                    attrBytes = (long)words * info.capacity * 4L;
                    attrBytesTotal += attrBytes;
                    anyAttr = true;
                }

                // Update the matching capacity-bar row (order mirrors _dbgSysNames; name-checked).
                if (rowsLive && i < _dbgSysRows.Count && _dbgSysRows[i].name == name)
                    UpdateSysRow(_dbgSysRows[i], info.aliveCount, info.capacity, info.sleeping, cpuMs, gpuMs, attrBytes);
            }
            // aliveParticleCount is the whole-effect async count (handoff): fall back to it when no
            // per-system readback is available (e.g. before the first system reports).
            if (systems == 0) alive = _effect.aliveParticleCount;

            if (teaserLive)
                _dbgTeaser.text = systems > 0
                    ? $"{alive:N0} live · {systems} system{(systems == 1 ? "" : "s")}"
                    : $"{alive:N0} live";

            if (!gridLive) return;

            _dbgAlive.text = alive.ToString("N0");
            _dbgAliveCap.text = capacity > 0 ? $"/ {capacity:N0}" : "";
            _dbgEff.text = capacity > 0 ? $"{(float)alive / capacity * 100f:0}%" : "—";
            _dbgSystems.text = systems.ToString();

            if (TryGetEffectBounds(out var size))
                _dbgBounds.text = $"{size.x:0.#} × {size.y:0.#} × {size.z:0.#}";
            else
                _dbgBounds.text = "—";

            _dbgState.text = _effect.culled ? "Culled" : _effect.pause ? "Paused" : "Playing";

            double cpuEffMs = MarkerMs(VfxGraphReflection.CpuEffectMarker(_effect), gpu: false);
            _dbgCpu.text = FmtMs(cpuEffMs);
            _dbgGpu.text = (!gpuOk || !anyGpu) ? "—" : FmtMs(gpuMsTotal);
            _dbgAttrMem.text = anyAttr ? EditorUtility.FormatBytes(attrBytesTotal) : "—";
        }

        // Update one capacity-bar row: alive/capacity numbers, fill (width + efficiency colour),
        // a dimmed "sleeping" treatment, and the per-system detail line (CPU/GPU ms + attr memory).
        private static void UpdateSysRow((string name, Label num, VisualElement fill, VisualElement row, Label detail) r,
                                 uint alive, uint capacity, bool sleeping, double cpuMs, double gpuMs, long attrBytes)
        {
            r.row.EnableInClassList("vfx-sys--sleeping", sleeping);
            if (sleeping)
            {
                r.num.text = "Sleeping";
                r.fill.style.width = Length.Percent(0f);
            }
            else
            {
                r.num.text = $"{alive:N0} / {capacity:N0}";
                float eff = capacity > 0 ? (float)alive / capacity : 0f;
                r.fill.style.width = Length.Percent(Mathf.Clamp01(eff) * 100f);
                r.fill.style.backgroundColor = EfficiencyColor(eff);
            }

            var parts = new List<string>();
            if (!double.IsNaN(cpuMs)) parts.Add($"cpu {FmtMs(cpuMs)}");
            if (!double.IsNaN(gpuMs)) parts.Add($"gpu {FmtMs(gpuMs)}");
            if (attrBytes >= 0) parts.Add($"mem {EditorUtility.FormatBytes(attrBytes)}");
            r.detail.text = string.Join("   ·   ", parts);
            r.detail.style.display = parts.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // Register the current effect for profiling (idempotent; re-registers if something else
        // unregistered it — self-heals across multiple Debug windows). Switches registration when
        // the target changes.
        private void EnsureProfiling()
        {
            if (_effect == null) return;
            if (_profilingEffect == _effect && VfxGraphReflection.IsRegisteredForProfiling(_effect)) return;
            if (_profilingEffect != null && _profilingEffect != _effect)
                VfxGraphReflection.UnregisterForProfiling(_profilingEffect);
            VfxGraphReflection.RegisterForProfiling(_effect);
            _profilingEffect = _effect;
        }

        private void StopProfiling()
        {
            if (_profilingEffect != null)
            {
                VfxGraphReflection.UnregisterForProfiling(_profilingEffect);
                _profilingEffect = null;
            }
        }

        // ---- profiler Recorder helpers (CPU/GPU timing) ----
        // Recorders are created+enabled lazily, cached by marker name, and dropped on target change.
        private Recorder GetRecorder(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return null;
            if (!_recorders.TryGetValue(marker, out var r))
            {
                r = Recorder.Get(marker);
                if (r != null && !r.enabled) r.enabled = true;
                _recorders[marker] = r;
            }
            return r;
        }

        // Milliseconds from a marker's Recorder, lightly smoothed (NaN when unavailable).
        private double MarkerMs(string marker, bool gpu)
        {
            var r = GetRecorder(marker);
            if (r == null) return double.NaN;
            long ns = gpu ? r.gpuElapsedNanoseconds : r.elapsedNanoseconds;
            return Smoothed(marker, ns) * 1e-6;
        }

        // Exponential moving average of a marker's nanoseconds (keyed by marker name).
        private double Smoothed(string marker, long rawNs)
        {
            double v = _smoothNs.TryGetValue(marker, out var prev)
                ? prev + (rawNs - prev) * kTimerSmooth
                : rawNs;
            _smoothNs[marker] = v;
            return v;
        }

        // Sum the GPU time of a system's tasks (marker probed by index until one comes back empty).
        // NaN when no task markers resolve (reflection unavailable).
        private double SumGpuMs(string system)
        {
            double sum = 0; bool any = false;
            for (int t = 0; t < 32; t++) // guard bound; real systems have a handful of tasks
            {
                string marker = VfxGraphReflection.GpuTaskMarker(_effect, system, t);
                if (string.IsNullOrEmpty(marker)) break;
                var r = GetRecorder(marker);
                if (r != null) { sum += Smoothed(marker, r.gpuElapsedNanoseconds) * 1e-6; any = true; }
            }
            return any ? sum : double.NaN;
        }

        // World-space render bounds from the sibling VFXRenderer (public Renderer.bounds). The
        // per-system GetComputedBounds is internal to the VFX editor assembly, so we use the
        // renderer's AABB — which is what's actually culled/drawn. Returns false (→ "—") with no
        // renderer or a degenerate (empty) box.
        private bool TryGetEffectBounds(out Vector3 size)
        {
            size = Vector3.zero;
            var r = _effect.GetComponent<Renderer>();
            if (r == null) return false;
            size = r.bounds.size;
            return size != Vector3.zero;
        }

        // "Show Bounds" visualizer: a world-space wire cube of the VFXRenderer's AABB. Cosmetic
        // Handles draws must be gated on Repaint or they corrupt GL state (see the gizmo notes).
        private void DrawBoundsVisualizer()
        {
            if (Event.current.type != EventType.Repaint) return;
            var r = _effect.GetComponent<Renderer>();
            if (r == null) return;
            var b = r.bounds;
            if (b.size == Vector3.zero) return;
            var prev = Handles.color;
            Handles.color = new Color(0.35f, 0.8f, 1f, 0.9f);
            Handles.DrawWireCube(b.center, b.size);
            Handles.color = prev;
        }
    }
}
