// VFX Control — a dockable EditorWindow that augments the stock VisualEffect
// inspector with the "Bold" layout from the design handoff (Variant C).
//
// Hosting as a window (rather than [CustomEditor(typeof(VisualEffect))]) sidesteps
// the conflict with the VFX package's own AdvancedVisualEffectEditor. The window
// tracks the current selection and rebuilds when it changes.
//
// First pass: full chrome (header, persistent mini-transport, divider, tabs,
// footer) + a working Properties tab (search, filter chips, category rail,
// pinned tray, collapsible category groups, typed value controls bound through
// the serialized property sheet, per-property reset, favorites). Playback and
// Debug tabs are stubbed for a later pass.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.VFX;
using VfxControl.EditorTools;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public class VfxControlWindow : EditorWindow
    {
        const string UssPath = "Assets/VfxControl/Editor/VfxControl.uss";

        // configurable timeline/scrub window length (Playback tab); the play clock
        // fills the bar over this many seconds and then loops (or stops, if _loop is off).
        float _duration = 10f;
        bool _loop = true;
        double _lastTick;

        // --- target ---
        VisualEffect _effect;        // primary (drives display + property enumeration)
        SerializedObject _so;        // primary's serialized object (display reads)
        readonly List<VisualEffect> _effects = new List<VisualEffect>();      // all edited instances (same asset)
        readonly List<SerializedObject> _sos = new List<SerializedObject>();  // one per instance (writes apply to all)
        List<VfxExposedParam> _params = new List<VfxExposedParam>();
        string _selectionHint; // why there's no editable target (shown as placeholder)

        // --- ui state ---
        VfxControlState _state;
        HashSet<string> _favorites = new HashSet<string>();
        HashSet<string> _collapsed = new HashSet<string>();
        HashSet<string> _constrained = new HashSet<string>(); // proportional-edit vectors
        string _search = "";
        string _filter = "all";   // all | fav | mod
        string _tab = "all";      // all | props | play | debug | render
        // per-tab rail section ("all" or a section id); the All tab has no rail.
        readonly Dictionary<string, string> _sections = new Dictionary<string, string>();

        // tab descriptors (id/label/badge/rail sections/body builder), rebuilt each Rebuild.
        List<TabDef> _tabDefs;

        // --- live element refs ---
        VisualElement _miniFill;
        Label _timeLabel, _liveLabel, _footNote;
        Button _resetAllBtn, _playBtn, _loopBtn;
        Image _playIcon;
        Slider _rateSlider; // Play Rate strip under the transport (resynced by UpdateLive)
        // persistent chrome containers: the search field is built ONCE (so typing never
        // loses focus); tabs/chips/rail/body are repopulated by PopulateActiveTab.
        ToolbarSearchField _searchField;
        VisualElement _chipsHost, _tabsHost, _railContainer, _tabBody;
        float _scrubT;

        // scene-view edit gizmo (custom Handles) for spaceable Position/Direction/Box
        VfxExposedParam _gizmoStruct;
        string _gizmoType, _gizmoSpace;
        bool _gizmoWasCollapsed; // fold state before the gizmo auto-unfolded it (to restore)
        Quaternion _gizmoRotation = Quaternion.identity; // persistent handle rotation (avoids LookRotation flips)
        BoxBoundsHandle _boxHandle;

        // property name -> actions that re-read the value into each control showing it,
        // so a pinned card and its category row (etc.) stay in sync after any edit.
        readonly Dictionary<string, List<Action>> _refreshers = new Dictionary<string, List<Action>>();

        // category name -> accent color, assigned distinctly in order of appearance.
        readonly Dictionary<string, Color> _categoryColors = new Dictionary<string, Color>();

        // struct parent -> its descendant leaf properties (for pin-all / reset-all).
        readonly Dictionary<VfxExposedParam, List<VfxExposedParam>> _structLeaves =
            new Dictionary<VfxExposedParam, List<VfxExposedParam>>();

        // struct header rows + their leaves, so a child edit can re-bold/aggregate live.
        readonly List<(VisualElement header, List<VfxExposedParam> leaves)> _structHeaders =
            new List<(VisualElement, List<VfxExposedParam>)>();

        // single-element structs (e.g. spaceable Position/Direction) -> their one leaf;
        // these render as a normal row (label + control + space) instead of a card.
        readonly Dictionary<VfxExposedParam, VfxExposedParam> _flattenChild =
            new Dictionary<VfxExposedParam, VfxExposedParam>();

        // scalar-only structs (e.g. Flipbook X/Y) -> their leaves; rendered inline on
        // one row like a Vector2/3/4 instead of a multi-row card.
        readonly Dictionary<VfxExposedParam, List<VfxExposedParam>> _inlineStruct =
            new Dictionary<VfxExposedParam, List<VfxExposedParam>>();

        // A tab: its id/label, optional badge count, and (when it has a rail) the rail's
        // sections beyond "All". `Build` fills the body for the current search/section/filter.
        sealed class TabDef
        {
            public string Id;
            public string Label;
            public int Count = -1;                 // -1 => no badge
            public bool HasRail;
            public Func<List<SectionDef>> Sections; // extra sections (rail prepends "All")
            public Action<VisualElement> Build;
            public Func<(int leaf, int fav, int mod)> ChipCounts; // for the filter chip badges
        }

        // A rail entry. Dot is drawn only when HasDot (category accents); section tabs
        // like Probes/Additional render dot-less like the "All" button.
        sealed class SectionDef
        {
            public string Id;
            public string Label;
            public Color Dot;
            public bool HasDot;
        }

        [MenuItem("Window/VFX Control")]
        public static void Open()
        {
            var w = GetWindow<VfxControlWindow>();
            w.titleContent = new GUIContent("VFX Control");
            w.minSize = new Vector2(320, 360);
            w.Show();
        }

        // Logs exactly where exposed-property enumeration succeeds or fails for the
        // selected/target VFX. Run it, then share the Console output.
        // NOTE: kept off the "Window/VFX Control" path — a MenuItem that is a prefix
        // of another turns the shorter one into a submenu and hides its command.
        [MenuItem("Tools/VFX Control/Diagnose Target")]
        static void Diagnose()
        {
            var go = Selection.activeGameObject;
            var ve = go != null ? go.GetComponent<VisualEffect>() : Selection.activeObject as VisualEffect;
            var asset = ve != null ? ve.visualEffectAsset : Selection.activeObject as VisualEffectAsset;

            Debug.Log($"[VFX Control] Diagnose — component={(ve != null ? ve.name : "null")}, " +
                      $"persistent={(ve != null && EditorUtility.IsPersistent(ve))}, " +
                      $"asset={(asset != null ? asset.name : "null")}");
            Debug.Log($"[VFX Control] Binding: {VfxGraphReflection.DescribeBindingState()}");

            VfxGraphReflection.Verbose = true;
            try
            {
                var ps = VfxGraphReflection.GetExposedParameters(asset);
                Debug.Log($"[VFX Control] Enumerated {ps.Count} parameter(s): " +
                          string.Join(", ", ps.Select(p => $"{p.Name}[{p.SheetType}/{p.RealType}] cat='{p.Category}'")));

                var evts = VfxGraphReflection.GetEventNames(asset);
                Debug.Log($"[VFX Control] Custom events ({evts.Count}): {string.Join(", ", evts)}");

                var customs = VfxGraphReflection.GetCustomAttributes(asset);
                Debug.Log($"[VFX Control] Custom attributes ({customs.Count}): " +
                          string.Join(", ", customs.Select(c => $"{c.name}#{c.type}")));
            }
            finally { VfxGraphReflection.Verbose = false; }
        }

        void OnEnable()
        {
            _duration = VfxControlState.GetTimelineDuration();
            _loop = VfxControlState.GetLoop();
            _lastTick = EditorApplication.timeSinceStartup;
            LoadPayloads(); // restore per-asset payloads before SetTarget picks the active list
            RefreshTarget();
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.projectChanged += OnProjectChanged;
            SceneView.duringSceneGui += OnSceneGui;
            rootVisualElement.schedule.Execute(Tick).Every(33); // ~30fps clock + live stats
            Rebuild();
        }

        void OnDisable()
        {
            SavePayloads(); // OnDisable fires before a domain reload (and on close) — SessionState
                            // carries the payloads across recompiles, but drops them on editor restart.
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.projectChanged -= OnProjectChanged;
            SceneView.duringSceneGui -= OnSceneGui;
        }

        // Fired after assets are imported (e.g. a .vfx recompiled + saved). The graph
        // may now expose new properties/categories, so force a fresh parameter rebuild.
        void OnProjectChanged()
        {
            if (_effect == null) return; // also true if the component was destroyed
            if (_effect.visualEffectAsset != null)
                _params = VfxGraphReflection.GetExposedParameters(_effect.visualEffectAsset, forceRebuild: true);
            UpdateAllSos();
            Rebuild();
        }

        void OnSelectionChange()
        {
            var prev = _effect;
            var prevHint = _selectionHint;
            RefreshTarget();
            if (prev != _effect || prevHint != _selectionHint)
            {
                _search = _state?.Search ?? "";
                Rebuild();
            }
        }

        void OnUndoRedo()
        {
            UpdateAllSos();
            Rebuild();
        }

        // ------------------------------------------------------------------ target

        // Resolve the *scene* VisualEffect component to edit. We deliberately reject
        // anything persistent: selecting a .vfx asset, or a prefab asset in the
        // Project (whose root GameObject Selection.activeGameObject exposes), must
        // NOT let you edit it as if it were a live instance. This window edits the
        // component's per-instance override sheet, which only makes sense on a
        // scene/prefab-instance object.
        VisualEffect ResolveSelectedEffect(out string hint)
        {
            hint = null;

            var go = Selection.activeGameObject;
            var effect = go != null ? go.GetComponent<VisualEffect>() : Selection.activeObject as VisualEffect;

            if (effect != null)
            {
                if (EditorUtility.IsPersistent(effect))
                {
                    hint = "That Visual Effect lives on a prefab/asset in the Project.\n" +
                           "Drag it into a scene (or select a scene instance) to edit its instance properties.";
                    return null;
                }
                return effect; // a scene (non-persistent) instance — editable
            }

            if (Selection.activeObject is VisualEffectAsset)
                hint = "You selected a Visual Effect asset (.vfx).\n" +
                       "Select a GameObject with a Visual Effect component in the scene to edit its instance properties.";

            return null;
        }

        void RefreshTarget()
        {
            var effect = ResolveSelectedEffect(out var hint);

            if (effect != null)
            {
                _selectionHint = null;
                var targets = GatherTargets(effect);
                if (effect != _effect || _so == null || !SameSet(targets, _effects))
                    SetTarget(effect, targets);
                return;
            }

            // The selection isn't a scene Visual Effect. The window mirrors the
            // current selection (like an inspector), so drop the target and surface
            // guidance — there's no manual target field to fall back on.
            _selectionHint = hint;
            if (_effect != null) SetTarget(null);
        }

        // All selected scene VisualEffects sharing the primary's asset (primary first),
        // so multi-edit applies to instances of the same VFX graph.
        List<VisualEffect> GatherTargets(VisualEffect primary)
        {
            var list = new List<VisualEffect>();
            if (primary == null) return list;
            list.Add(primary);
            var asset = primary.visualEffectAsset;
            foreach (var go in Selection.gameObjects)
            {
                var ve = go != null ? go.GetComponent<VisualEffect>() : null;
                if (ve != null && ve != primary && !EditorUtility.IsPersistent(ve) && ve.visualEffectAsset == asset)
                    list.Add(ve);
            }
            return list;
        }

        static bool SameSet(List<VisualEffect> a, List<VisualEffect> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Bind the window to a primary VisualEffect (+ any same-asset instances to edit)
        // and load its exposed properties + per-asset UI state.
        void SetTarget(VisualEffect effect) => SetTarget(effect, GatherTargets(effect));

        void SetTarget(VisualEffect effect, List<VisualEffect> targets)
        {
            _gizmoStruct = null; // gizmo target is invalid for a new component
            _effect = effect;

            _effects.Clear();
            _sos.Clear();
            if (_effect != null)
            {
                foreach (var ve in targets) { _effects.Add(ve); _sos.Add(new SerializedObject(ve)); }
                _so = _sos[0];
            }
            else _so = null;

            var asset = _effect != null ? _effect.visualEffectAsset : null;
            _params = VfxGraphReflection.GetExposedParameters(asset);

            string guid = asset != null
                ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset))
                : "";
            // Switch to this asset's payload (per-asset scope); create its bucket on first use.
            if (!_payloadByAsset.TryGetValue(guid, out var payload))
            {
                payload = new List<EventAttr>();
                _payloadByAsset[guid] = payload;
            }
            _eventPayload = payload;

            _state = new VfxControlState(guid);
            _favorites = _state.LoadFavorites();
            MigrateFavorites();
            _collapsed = _state.LoadCollapsed();
            _constrained = _state.LoadConstrained();
            _tab = _state.Tab;
            _filter = _state.Filter;
            _search = _state.Search;
            LoadSections();
        }

        // Per-tab rail section, persisted as a packed "tab=section;..." session string.
        void LoadSections()
        {
            _sections.Clear();
            var raw = _state.Sections;
            if (!string.IsNullOrEmpty(raw))
                foreach (var pair in raw.Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0) _sections[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                }
            // migrate the pre-rail Properties category selection (one-time)
            if (!_sections.ContainsKey("props") && _state.Category != "all")
                _sections["props"] = _state.Category;
        }

        void SaveSections() =>
            _state.Sections = string.Join(";", _sections.Select(kv => $"{kv.Key}={kv.Value}"));

        // The active tab's selected rail section ("all" when no rail / nothing chosen).
        string CurrentSection()
        {
            var def = ActiveTabDef();
            if (def == null || !def.HasRail) return "all";
            return _sections.TryGetValue(_tab, out var s) ? s : "all";
        }

        void SetSection(string id)
        {
            string cur = _sections.TryGetValue(_tab, out var s) ? s : "all";
            _sections[_tab] = (cur == id) ? "all" : id; // re-clicking the active section clears it
            SaveSections();
        }

        // ------------------------------------------------------------------ build

        void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear();
            root.AddToClassList("vfx-root");

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null && !root.styleSheets.Contains(uss))
                root.styleSheets.Add(uss);

            root.Add(BuildHeader());

            if (_effect == null)
            {
                var ph = new Label(_selectionHint ??
                    "Select a Visual Effect in the Hierarchy to edit its instance properties.");
                ph.AddToClassList("vfx-placeholder");
                root.Add(ph);
                return;
            }

            if (_so == null) SetTarget(_effect); // recover after a domain reload
            UpdateAllSos();

            root.Add(BuildMetaSection());
            root.Add(BuildMiniTransport());
            root.Add(MakeElement("vfx-section-gap"));   // the intentional divider

            // Persistent chrome: search + chips ABOVE the tabs (shared across tabs), then
            // the tab strip, the per-tab section rail, and the body. Only the search field
            // is built once; chips/tabs/rail/body are repopulated by PopulateActiveTab so
            // typing never detaches (and unfocuses) the search field.
            _tabDefs = BuildTabDefs();
            BuildCategoryColorMap();        // rail dots + pinned cards need the color map
            root.Add(BuildChrome());        // search field + _chipsHost

            // Horizontal ScrollView so the tab strip scrolls (wheel/drag) when the window is
            // too narrow to show every tab, instead of clipping the trailing tabs.
            var tabsScroll = new ScrollView(ScrollViewMode.Horizontal);
            tabsScroll.AddToClassList("vfx-tabs");
            tabsScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            tabsScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            AttachHScroll(tabsScroll);
            _tabsHost = tabsScroll;
            root.Add(_tabsHost);

            _railContainer = MakeElement("vfx-rail-host");
            root.Add(_railContainer);

            _tabBody = new ScrollView { name = "body" };
            _tabBody.AddToClassList("vfx-scroll");
            root.Add(_tabBody);

            root.Add(BuildFooter());
            PopulateActiveTab();
            UpdateLive();
        }

        // The ordered tab set. Counts/sections read live state, so this is rebuilt each
        // structural Rebuild (cheap). The "All" tab opts out of the rail (HasRail=false).
        List<TabDef> BuildTabDefs() => new List<TabDef>
        {
            new TabDef { Id = "all", Label = "All", HasRail = false, Build = BuildAllTab, ChipCounts = AllChipCounts },
            new TabDef
            {
                Id = "props", Label = "Properties", Count = _params.Count(p => !p.IsStruct),
                HasRail = true, Sections = PropertySections,
                Build = body => { AddFavoriteGroup(body, includeProps: true, null); PopulateProperties(body); },
                ChipCounts = PropertyChipCounts,
            },
            new TabDef { Id = "play", Label = "Playback", HasRail = true, Sections = PlaybackSections, Build = BuildPlaybackTab, ChipCounts = PlaybackChipCounts },
            new TabDef { Id = "render", Label = "Renderer", HasRail = true, Sections = RendererSections, Build = BuildRendererTab, ChipCounts = RendererChipCounts },
            new TabDef
            {
                Id = "debug", Label = "Debug", HasRail = true, Sections = NoSections,
                Build = body => BuildPlaceholder(body, "Debug tab — coming in the next pass.\nLive stats, systems, visualizers."),
                ChipCounts = () => (0, 0, 0),
            },
        };

        (int leaf, int fav, int mod) PropertyChipCounts() => (
            _params.Count(p => !p.IsStruct),
            _params.Count(p => !p.IsStruct && IsFav(FavKeyOf(p))),
            VfxPropertySheet.CountModified(_so, _params));

        // The All tab aggregates properties + renderer (playback has no fav/mod model yet).
        (int leaf, int fav, int mod) AllChipCounts()
        {
            var p = PropertyChipCounts();
            var r = RendererChipCounts();
            return (p.leaf + r.leaf, p.fav + r.fav, p.mod + r.mod);
        }

        TabDef ActiveTabDef()
        {
            if (_tabDefs == null) return null;
            return _tabDefs.FirstOrDefault(t => t.Id == _tab) ?? _tabDefs[0];
        }

        static List<SectionDef> NoSections() => new List<SectionDef>();

        // Properties sections = the distinct categories, in graph order, each with its accent dot.
        List<SectionDef> PropertySections()
        {
            var cats = new List<string>();
            foreach (var p in _params)
            {
                var cat = CategoryOf(p);
                if (!cats.Contains(cat)) cats.Add(cat);
            }
            return cats.Select(c => new SectionDef { Id = c, Label = c, Dot = GetCategoryColor(c), HasDot = true }).ToList();
        }

        // Renderer sections mirror the two IMGUI foldouts.
        static List<SectionDef> RendererSections() => new List<SectionDef>
        {
            new SectionDef { Id = "probes", Label = "Probes" },
            new SectionDef { Id = "additional", Label = "Additional Settings" },
        };

        // Playback sections: the setting rows live under "Playback options"; the event controls
        // get their own "Send Event" section (same collapsible group + rail style as the rest).
        static List<SectionDef> PlaybackSections() => new List<SectionDef>
        {
            new SectionDef { Id = "options", Label = "Playback options" },
            new SectionDef { Id = "events", Label = "Send Event" },
        };

        // Search + chips chrome. Built once per Rebuild; the search field reference is kept
        // so PopulateActiveTab never recreates it (preserving focus while typing).
        VisualElement BuildChrome()
        {
            var subbar = MakeElement("vfx-subbar");

            _searchField = new ToolbarSearchField();
            _searchField.AddToClassList("vfx-search");
            _searchField.placeholderText = "Search…";
            _searchField.value = _search;
            _searchField.RegisterValueChangedCallback(e =>
            {
                _search = e.newValue ?? "";
                _state.Search = _search;
                PopulateActiveTab(); // filters the active tab; chrome (search field) untouched
            });
            subbar.Add(_searchField);

            _chipsHost = MakeElement("vfx-filterchips");
            subbar.Add(_chipsHost);
            return subbar;
        }

        // Rebuild only the parts that depend on the active tab / filter / search / section,
        // leaving the search field (and the rest of the chrome) intact.
        void PopulateActiveTab()
        {
            if (_tabBody == null) return;
            var def = ActiveTabDef();
            if (def == null) return;

            PopulateChips();
            PopulateTabs();

            _railContainer.Clear();
            if (def.HasRail) _railContainer.Add(BuildRail(def));

            _tabBody.Clear();
            _refreshers.Clear();    // controls about to be discarded
            _structHeaders.Clear();
            _playbackRows.Clear();
            def.Build(_tabBody);

            UpdateFooter();
        }

        void PopulateTabs()
        {
            if (_tabsHost == null) return;
            _tabsHost.Clear();
            foreach (var def in _tabDefs)
                _tabsHost.Add(MakeTab(def.Id, def.Label, def.Count));
        }

        void PopulateChips()
        {
            if (_chipsHost == null) return;
            _chipsHost.Clear();
            var def = ActiveTabDef();
            var (leafCount, favCount, modCount) = def?.ChipCounts != null ? def.ChipCounts() : (0, 0, 0);
            _chipsHost.Add(MakeChip("all", "All", leafCount));
            _chipsHost.Add(MakeChip("fav", "★", favCount));
            _chipsHost.Add(MakeChip("mod", "Modified", modCount));
        }

        VisualElement BuildHeader()
        {
            var header = MakeElement("vfx-header");
            var title = new Label("VFX Control");
            title.AddToClassList("vfx-title");
            header.Add(title);
            if (_effect != null)
            {
                var sub = new Label(_effects.Count > 1
                    ? $"{_effect.gameObject.name}  (+{_effects.Count - 1} more)"
                    : _effect.gameObject.name);
                sub.AddToClassList("vfx-header-sub");
                header.Add(sub);
            }
            return header;
        }

        // The window's target: an explicit picker for a Visual Effect component in
        // the scene Hierarchy (drag a GameObject in, or use the object picker).
        VisualElement BuildMetaSection()
        {
            var meta = MakeElement("vfx-meta");

            var assetRow = MakeElement("vfx-meta-row");
            var assetLabel = new Label("Asset");
            assetLabel.AddToClassList("vfx-mlabel");
            assetRow.Add(assetLabel);
            var assetField = new ObjectField { objectType = typeof(VisualEffectAsset), allowSceneObjects = false };
            assetField.AddToClassList("vfx-meta-field");
            assetField.tooltip = "The .vfx graph this component plays (an instance property).";
            assetField.value = _effect.visualEffectAsset;
            assetField.RegisterValueChangedCallback(e =>
            {
                Undo.RecordObjects(_effects.ToArray(), "Set VFX Asset");
                foreach (var ve in _effects) { ve.visualEffectAsset = e.newValue as VisualEffectAsset; EditorUtility.SetDirty(ve); }
                SetTarget(_effect); // reload exposed properties for the new asset
                Rebuild();
            });
            assetRow.Add(assetField);
            meta.Add(assetRow);

            // Initial Event lives in the Playback tab (a PField row), not here — keep the header
            // to just the asset.

            return meta;
        }

        // The persistent transport bar (always visible above the tabs). Two rows:
        //   row 1 — the full-width scrub bar + time + live count;
        //   row 2 — the transport buttons (restart · step-back · play/pause · step-forward · loop)
        //           followed by the Rate slider.
        // This is the single home for the transport (the Playback tab does not duplicate it).
        VisualElement BuildMiniTransport()
        {
            var wrap = MakeElement("vfx-transport-wrap"); // column: scrub row + controls row

            // ---- row 1: scrub bar (expanded) + time + live ----
            var top = MakeElement("vfx-transport-row");

            var scrub = MakeElement("vfx-mini-scrub");
            _miniFill = MakeElement("vfx-mini-fill");
            _miniFill.style.width = Length.Percent(_scrubT * 100f);
            scrub.Add(_miniFill);
            scrub.RegisterCallback<MouseDownEvent>(e => { scrub.CaptureMouse(); ScrubAt(scrub, e.localMousePosition.x); });
            scrub.RegisterCallback<MouseMoveEvent>(e => { if (scrub.HasMouseCapture()) ScrubAt(scrub, e.localMousePosition.x); });
            scrub.RegisterCallback<MouseUpEvent>(e => scrub.ReleaseMouse());
            top.Add(scrub);

            _timeLabel = new Label("0.00 / 0s");
            _timeLabel.AddToClassList("vfx-mini-time");
            top.Add(_timeLabel);

            _liveLabel = new Label("0 live");
            _liveLabel.AddToClassList("vfx-mini-live");
            top.Add(_liveLabel);

            wrap.Add(top);

            // ---- row 2: transport buttons + Rate ----
            var bottom = MakeElement("vfx-transport-row");

            bottom.Add(MakeTransportButton("Restart (Reinit)", null,
                () => { _effect.Reinit(); _scrubT = 0f; UpdateLive(); }, glyph: "↺"));

            // step-back uses the Step-Forward icon mirrored horizontally (a dedicated glyph read poorly).
            bottom.Add(MakeTransportButton("Step back one frame", "StepButton", () => StepFrame(-1), mirror: true));

            // primary play/pause; built-in icon drawn 1:1 at native size (no scaling → no
            // aliasing), kept in sync with the pause state by UpdateLive.
            _playBtn = MakeTransportButton("Play", null, () => { _effect.pause = !_effect.pause; UpdateLive(); });
            _playBtn.AddToClassList("vfx-tbtn--primary");
            _playIcon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            _playIcon.style.width = 16;
            _playIcon.style.height = 16;
            _playBtn.Add(_playIcon);
            bottom.Add(_playBtn);

            bottom.Add(MakeTransportButton("Step forward one frame", "StepButton", () => StepFrame(1)));

            _loopBtn = MakeTransportButton(_loop ? "Looping (click to stop at end)" : "Stop at end (click to loop)", null, () =>
            {
                _loop = !_loop;
                VfxControlState.SetLoop(_loop);
                _loopBtn.EnableInClassList("vfx-tbtn--on", _loop);
                _loopBtn.tooltip = _loop ? "Looping (click to stop at end)" : "Stop at end (click to loop)";
                UpdateLive();
            }, glyph: "∞");
            _loopBtn.EnableInClassList("vfx-tbtn--on", _loop);
            bottom.Add(_loopBtn);

            // Rate slider, right after the transport buttons (label · 0–10× slider · reset-to-1×);
            // _rateSlider is resynced by UpdateLive so undo/multi-select stay reflected.
            var rateLabel = new Label("Rate");
            rateLabel.AddToClassList("vfx-rate-label");
            bottom.Add(rateLabel);

            _rateSlider = new Slider(0f, 10f) { showInputField = true, value = _effect != null ? _effect.playRate : 1f };
            _rateSlider.AddToClassList("vfx-rate-slider");
            _rateSlider.showMixedValue = EffectsDiffer(ve => ve.playRate);
            _rateSlider.RegisterValueChangedCallback(e => SetPlayRate(e.newValue));
            bottom.Add(_rateSlider);

            var rateReset = MakeIconButton("↺", "Reset to 1×", () =>
            {
                SetPlayRate(1f);
                _rateSlider.SetValueWithoutNotify(1f);
            });
            rateReset.AddToClassList("vfx-rate-reset");
            bottom.Add(rateReset);

            wrap.Add(bottom);
            return wrap;
        }

        void ScrubAt(VisualElement scrub, float localX)
        {
            float w = scrub.layout.width;
            if (w <= 0) return;
            SeekTo(localX / w);
        }

        // GPU sim has no random-access seek: pause, Reinit, then simulate forward to the target
        // time. Best-effort and capped (see handoff "Scrubbing caveat"). Used by the scrub bar
        // and the transport's step-back.
        void SeekTo(float t)
        {
            if (_effect == null) return;
            _scrubT = Mathf.Clamp01(t);
            if (_miniFill != null) _miniFill.style.width = Length.Percent(_scrubT * 100f);

            float target = _scrubT * _duration;
            _effect.pause = true;
            _effect.Reinit();
            const float dt = 1f / 60f;
            int steps = Mathf.Clamp(Mathf.RoundToInt(target / dt), 0, 600);
            if (steps > 0) _effect.Simulate(dt, (uint)steps);
            UpdateLive();
        }

        Button MakeTab(string id, string label, int count)
        {
            // Use child Labels (not the Button's intrinsic text) so the label and the
            // count badge flow as flex items left-to-right instead of overlapping.
            // ClickEvent (not the Button action) so Alt is observable: Alt+click folds/unfolds
            // the whole tab body in one go (like Alt+click on a category/struct header).
            var tab = new Button();
            tab.AddToClassList("vfx-tab");
            tab.tooltip = "Alt+click to expand/collapse all";
            tab.RegisterCallback<ClickEvent>(e =>
            {
                if (e.altKey)
                {
                    var keys = TabCollapseKeys(id).ToList();
                    bool collapse = keys.Any(k => !_collapsed.Contains(k)); // any open → collapse all
                    SetCollapsedAll(keys, collapse);
                    _state.SaveCollapsed(_collapsed);
                }
                _tab = id; _state.Tab = id;
                PopulateActiveTab();
            });
            if (_tab == id) tab.AddToClassList("vfx-tab--active");
            tab.Add(new Label(label));
            if (count >= 0)
            {
                var badge = new Label(count.ToString());
                badge.AddToClassList("vfx-tabcount");
                tab.Add(badge);
            }
            return tab;
        }

        // ------------------------------------------------------------------ properties tab

        // Fill `container` with the filtered pinned tray + category groups. `showEmpty`
        // suppresses the "nothing matches" note when stacked under other blocks (All tab).
        // Category groups for the Properties content. The Favorites group is added separately
        // by the tab builder (AddFavoriteGroup), so this is purely the categorized list.
        void PopulateProperties(VisualElement container, bool showEmpty = true)
        {
            if (container == null) return;
            BuildStructLeavesMap();

            bool forceOpen = !string.IsNullOrEmpty(_search.Trim());

            // group all entries (structs + leaves) by category, preserving graph order
            var ordered = new List<string>();
            var byCat = new Dictionary<string, List<VfxExposedParam>>();
            foreach (var p in _params)
            {
                var cat = CategoryOf(p);
                if (!byCat.TryGetValue(cat, out var list)) { byCat[cat] = list = new List<VfxExposedParam>(); ordered.Add(cat); }
                list.Add(p);
            }

            int shownLeaves = 0;
            foreach (var cat in ordered)
            {
                var display = ComputeDisplay(byCat[cat]);
                if (display.Count == 0) continue;
                shownLeaves += display.Count(e => !e.IsStruct);
                // gate detected from the FULL category list (not the filtered display) so
                // the header enable toggle still shows even when search hides the bool
                var gate = FindCategoryGate(cat, byCat[cat]);
                container.Add(BuildGroup(cat, display, forceOpen, gate));
            }

            if (shownLeaves == 0 && showEmpty)
            {
                var empty = new Label(EmptyMessage());
                empty.AddToClassList("vfx-empty");
                container.Add(empty);
            }
        }

        string CategoryOf(VfxExposedParam p) => string.IsNullOrEmpty(p.Category) ? "Uncategorized" : p.Category;

        // For each struct parent, collect its descendant leaf properties (entries that
        // follow it with greater depth), used by the header's pin-all / reset-all.
        void BuildStructLeavesMap()
        {
            _structLeaves.Clear();
            _flattenChild.Clear();
            _inlineStruct.Clear();
            for (int i = 0; i < _params.Count; i++)
            {
                if (!_params[i].IsStruct) continue;
                int d = _params[i].Depth;
                var leaves = new List<VfxExposedParam>();
                int total = 0;
                for (int j = i + 1; j < _params.Count && _params[j].Depth > d; j++)
                {
                    total++;
                    if (!_params[j].IsStruct) leaves.Add(_params[j]);
                }
                _structLeaves[_params[i]] = leaves;

                bool allLeaves = leaves.Count == total;
                // single element → plain row, EXCEPT spaceable ones (Position/Direction):
                // those stay as a labelled header + value row so the header can carry the
                // space + edit-gizmo and the value row the constrain lock.
                if (total == 1 && leaves.Count == 1 && !_params[i].Spaceable)
                    _flattenChild[_params[i]] = leaves[0];
                else if (allLeaves && leaves.Count >= 2 && leaves.Count <= 4 && leaves.All(IsScalarLeaf))
                    _inlineStruct[_params[i]] = leaves;     // scalar components → inline like a vector
            }
        }

        static bool IsScalarLeaf(VfxExposedParam p) =>
            p.SheetType == "m_Float" || p.SheetType == "m_Int" || p.SheetType == "m_Uint";

        // Ordered entries to display: visible leaves plus any struct parent that has a
        // visible descendant (so a shown child still appears under its struct label).
        List<VfxExposedParam> ComputeDisplay(List<VfxExposedParam> entries)
        {
            int n = entries.Count;
            var show = new bool[n];
            for (int i = 0; i < n; i++)
                if (!entries[i].IsStruct) show[i] = Visible(entries[i]);
            for (int i = n - 1; i >= 0; i--)
                if (entries[i].IsStruct)
                {
                    int d = entries[i].Depth;
                    for (int j = i + 1; j < n && entries[j].Depth > d; j++)
                        if (show[j]) { show[i] = true; break; }
                }

            var list = new List<VfxExposedParam>();
            for (int i = 0; i < n; i++) if (show[i]) list.Add(entries[i]);
            return list;
        }

        // Like ComputeDisplay but keyed on favorites: favorited leaves + the struct parents that
        // contain them — so a pinned struct (e.g. Box) keeps its header row + Edit-Gizmo, not a
        // flat list of components. Operates over the whole param list (favorites span categories).
        List<VfxExposedParam> ComputeFavoriteDisplay()
        {
            int n = _params.Count;
            var show = new bool[n];
            for (int i = 0; i < n; i++)
                if (!_params[i].IsStruct) show[i] = IsFav(FavKeyOf(_params[i]));
            for (int i = n - 1; i >= 0; i--)
                if (_params[i].IsStruct)
                {
                    int d = _params[i].Depth;
                    for (int j = i + 1; j < n && _params[j].Depth > d; j++)
                        if (show[j]) { show[i] = true; break; }
                }

            var list = new List<VfxExposedParam>();
            for (int i = 0; i < n; i++) if (show[i]) list.Add(_params[i]);
            return list;
        }

        // ------------------------------------------------------------------ playback tab

        // Does a field/section label match the current search query? (empty query = match all)
        bool SearchMatches(string label)
        {
            string q = _search.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(q) || (label != null && label.ToLowerInvariant().Contains(q));
        }

        // A Playback setting, modelled like the renderer's RField but backed by live component
        // props / tool prefs rather than a SerializedProperty: each carries a fav key, modified
        // test, reset, and a control factory whose `sync` re-reads the value into the control
        // (so duplicate copies — favorites group + section row — stay coherent, like Duration did).
        sealed class PField
        {
            public string Id, Label, Tooltip;
            public string FavKey => "play:" + Id;
            public Func<bool> IsModified;
            public Action Reset;
            public Func<(VisualElement control, Action sync)> BuildControl;
        }

        // The playback settings, in display order. Rebuilt on demand (cheap — just descriptors);
        // closures capture _effect/_effects/_duration, not specific controls, so this is safe to
        // call for counts even with no target.
        List<PField> BuildPlaybackFields()
        {
            var list = new List<PField>
            {
                new PField
                {
                    Id = "duration", Label = "Duration (s)",
                    Tooltip = "Length of the play/scrub timeline window before it loops.",
                    IsModified = () => !Mathf.Approximately(_duration, kDefaultDuration),
                    Reset = () => { _duration = kDefaultDuration; VfxControlState.SetTimelineDuration(_duration); UpdateLive(); },
                    BuildControl = () =>
                    {
                        var f = new FloatField { value = _duration };
                        f.RegisterValueChangedCallback(e =>
                        {
                            _duration = Mathf.Max(0.1f, e.newValue);
                            VfxControlState.SetTimelineDuration(_duration);
                            UpdateLive();
                            RefreshPlaybackRows();
                        });
                        return (f, () => f.SetValueWithoutNotify(_duration));
                    },
                },
                new PField
                {
                    Id = "seed", Label = "Start Seed",
                    Tooltip = "Random seed for the simulation (VisualEffect.startSeed). Reseed randomizes it and reinitializes.",
                    IsModified = () => _effect != null && (EffectsDiffer(ve => ve.startSeed) || _effect.startSeed != 0),
                    Reset = () => SetStartSeed(0),
                    BuildControl = BuildStartSeedControl,
                },
                new PField
                {
                    Id = "reseedOnPlay", Label = "Reseed on Play",
                    Tooltip = "Pick a new random seed each time the effect (re)starts. VisualEffect.resetSeedOnPlay.",
                    IsModified = () => _effect != null && (EffectsDiffer(ve => ve.resetSeedOnPlay) || _effect.resetSeedOnPlay != true),
                    Reset = () => SetResetSeedOnPlay(true),
                    BuildControl = () =>
                    {
                        var t = new Toggle { value = _effect != null && _effect.resetSeedOnPlay };
                        t.showMixedValue = EffectsDiffer(ve => ve.resetSeedOnPlay);
                        t.RegisterValueChangedCallback(e => { SetResetSeedOnPlay(e.newValue); RefreshPlaybackRows(); });
                        return (t, () =>
                        {
                            if (_effect != null) t.SetValueWithoutNotify(_effect.resetSeedOnPlay);
                            t.showMixedValue = EffectsDiffer(ve => ve.resetSeedOnPlay);
                        });
                    },
                },
                new PField
                {
                    Id = "event", Label = "Initial Event",
                    Tooltip = "Event sent when the effect starts (VisualEffect.initialEventName); defaults to OnPlay.",
                    IsModified = () => _effect != null && (EffectsDiffer(ve => InitEventOf(ve)) || InitEventOf(_effect) != "OnPlay"),
                    Reset = () => SetInitialEvent("OnPlay"),
                    BuildControl = () =>
                    {
                        var f = new TextField { value = _effect != null ? InitEventOf(_effect) : "OnPlay" };
                        f.showMixedValue = EffectsDiffer(ve => InitEventOf(ve));
                        f.RegisterValueChangedCallback(e => { SetInitialEvent(e.newValue); RefreshPlaybackRows(); });
                        return (f, () =>
                        {
                            if (_effect != null) f.SetValueWithoutNotify(InitEventOf(_effect));
                            f.showMixedValue = EffectsDiffer(ve => InitEventOf(ve));
                        });
                    },
                },
            };
            return list;
        }

        // initialEventName is empty by default but behaves as "OnPlay"; normalize for display/compare.
        static string InitEventOf(VisualEffect ve) => string.IsNullOrEmpty(ve.initialEventName) ? "OnPlay" : ve.initialEventName;

        // Do the selected instances disagree on a value? (drives showMixedValue, like a multi-target SO.)
        bool EffectsDiffer<T>(Func<VisualEffect, T> get)
        {
            if (_effect == null || _effects.Count <= 1) return false;
            var first = get(_effect);
            foreach (var ve in _effects)
                if (ve != null && !EqualityComparer<T>.Default.Equals(get(ve), first)) return true;
            return false;
        }

        void BuildPlaybackTab(VisualElement body)
        {
            AddFavoriteGroup(body, includeProps: false, PlaybackFavoriteSettings());
            BuildPlaybackContent(body);
        }

        // The Playback content without the favorites group, so the All tab can stack it under one
        // unified favorites group. Two collapsible sections, both rail-filterable like the Renderer
        // tab's Probes/Additional: "Playback options" (the setting rows) and "Send Event" (the
        // event controls). The transport itself is NOT here — it lives once in the persistent top
        // bar (with the scrub).
        void BuildPlaybackContent(VisualElement body)
        {
            string section = CurrentSection();
            bool InSection(string id) => section == "all" || section == id;

            var fields = BuildPlaybackFields();
            bool Show(PField f) => InSection("options") && SearchMatches(f.Label) && PlaybackChipOk(f);
            int shown = AddPlaybackSection(body, "options", "Playback options", fields, Show);

            // Send Event is an action section (favoritable but never "modified"): show it under
            // "All"/its own rail section in the unfiltered view, or under the ★ filter when pinned.
            // The Modified filter never includes it.
            bool eventsChipOk = _filter == "all" || (_filter == "fav" && IsFav(kSendEventFavKey));
            bool showEvents = _effect != null && InSection("events")
                              && eventsChipOk && string.IsNullOrEmpty(_search.Trim());
            if (showEvents) shown += AddSendEventSection(body);

            if (shown == 0)
            {
                BuildPlaceholder(body,
                    !string.IsNullOrEmpty(_search.Trim()) ? $"No playback settings match “{_search}”."
                    : _filter == "fav" ? "No favorite playback settings."
                    : _filter == "mod" ? "No modified playback settings."
                    : "No playback settings available.");
            }
        }

        // A collapsible "Playback options" group (styled like the renderer's section groups),
        // containing the visible playback setting rows. Returns the number of rows shown.
        int AddPlaybackSection(VisualElement host, string id, string title, List<PField> fields, Func<PField, bool> show)
        {
            var visible = fields.Where(show).ToList();
            if (visible.Count == 0) return 0;

            string key = "play:" + id;
            bool forceOpen = !string.IsNullOrEmpty(_search.Trim());
            bool open = forceOpen || !_collapsed.Contains(key);

            var group = MakeElement("vfx-group");
            var header = MakeElement("vfx-group-header");
            var twirl = new Label(open ? "▾" : "▸") { pickingMode = PickingMode.Ignore };
            twirl.AddToClassList("vfx-group-twirl");
            header.Add(twirl);
            var titleLbl = new Label(title);
            titleLbl.AddToClassList("vfx-group-title");
            header.Add(titleLbl);
            var count = new Label(visible.Count.ToString());
            count.AddToClassList("vfx-group-count");
            header.Add(count);
            if (!forceOpen)
            {
                header.tooltip = "Click to expand/collapse";
                header.RegisterCallback<ClickEvent>(e =>
                {
                    if (_collapsed.Contains(key)) _collapsed.Remove(key); else _collapsed.Add(key);
                    _state.SaveCollapsed(_collapsed);
                    RebuildBodyOnly();
                });
            }
            group.Add(header);

            var content = MakeElement("vfx-group-content");
            content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var f in visible) content.Add(BuildPlaybackRow(f));
            group.Add(content);
            host.Add(group);
            return visible.Count;
        }

        bool PlaybackChipOk(PField f) =>
            _filter == "all" ||
            (_filter == "fav" && IsFav(f.FavKey)) ||
            (_filter == "mod" && f.IsModified());

        List<Setting> PlaybackFavoriteSettings()
        {
            var list = new List<Setting>();
            foreach (var f in BuildPlaybackFields())
                if (IsFav(f.FavKey))
                    list.Add(new Setting { FavKey = f.FavKey, BuildRow = () => BuildPlaybackRow(f) });
            if (IsFav(kSendEventFavKey)) // the Send Event section pins as one unit
                list.Add(new Setting { FavKey = kSendEventFavKey, BuildRow = BuildSendEventFavRow });
            return list;
        }

        // (leaf, fav, mod) counts for the Playback tab's filter chips. The Send Event section
        // counts as one extra leaf (favoritable, never "modified").
        (int leaf, int fav, int mod) PlaybackChipCounts()
        {
            var fields = BuildPlaybackFields();
            int fav = fields.Count(f => IsFav(f.FavKey)) + (IsFav(kSendEventFavKey) ? 1 : 0);
            return (fields.Count + 1, fav, fields.Count(f => f.IsModified()));
        }

        // A playback setting row, styled like any property/renderer row (label · control · hover ↺/★).
        // These back live component props / tool prefs (not SerializedProperties), so edits sync the
        // (possibly two) visible copies via RefreshPlaybackRows rather than binding.
        VisualElement BuildPlaybackRow(PField f)
        {
            var (control, sync) = f.BuildControl();

            var row = MakeElement("vfx-row");
            row.EnableInClassList("vfx-row--modified", f.IsModified());
            if (IsFav(f.FavKey)) row.AddToClassList("vfx-row--fav");

            var labelCol = MakeElement("vfx-label-col");
            var label = new Label(f.Label) { tooltip = f.Tooltip ?? f.Label };
            label.AddToClassList("vfx-plabel");
            labelCol.Add(label);
            row.Add(labelCol);

            row.Add(MakeElement("vfx-row-lock")); // align with the other rows' lock gutter

            control.AddToClassList("vfx-pcontrol");
            AttachLabelDragger(label, control); // drag the label to scrub numeric fields (no-op otherwise)
            row.Add(control);

            var tools = MakeElement("vfx-row-tools");
            var reset = MakeIconButton("↺", "Reset to default", () => { f.Reset(); RefreshPlaybackRows(); });
            reset.AddToClassList("vfx-tool-reset");
            tools.Add(reset);
            var star = MakeIconButton(IsFav(f.FavKey) ? "★" : "☆", IsFav(f.FavKey) ? "Unpin" : "Pin", () => ToggleFav(f.FavKey));
            star.AddToClassList("vfx-tool-fav");
            tools.Add(star);
            row.Add(tools);

            _playbackRows.Add((row, f, sync));
            return row;
        }

        // Start Seed is meaningless when Reseed-on-Play is on (the seed is re-randomized each
        // (re)start), so the control greys out to match. Mixed multi-edit → leave it editable
        // (ambiguous, like the category gate treats mixed as enabled).
        bool SeedLocked() => _effect != null && _effect.resetSeedOnPlay && !EffectsDiffer(ve => ve.resetSeedOnPlay);

        // Start Seed: an int field (clamped ≥ 0 → uint, like the uint property control) plus an
        // inline Reseed button that randomizes the seed and reinitializes the sim.
        (VisualElement control, Action sync) BuildStartSeedControl()
        {
            var wrap = MakeElement("vfx-seed-control");
            var field = new IntegerField { value = _effect != null ? (int)_effect.startSeed : 0 };
            field.AddToClassList("vfx-seed-int"); // marks it as the label-drag target (see AttachLabelDragger)
            field.showMixedValue = EffectsDiffer(ve => ve.startSeed);
            field.style.flexGrow = 1;
            field.RegisterValueChangedCallback(e =>
            {
                // Locked by Reseed on Play — ignore edits (incl. label-drag, whose drag zone is
                // the label outside this disabled wrap) and revert the display.
                if (SeedLocked()) { field.SetValueWithoutNotify(_effect != null ? (int)_effect.startSeed : 0); return; }
                SetStartSeed((uint)Mathf.Max(0, e.newValue));
                RefreshPlaybackRows();
            });
            wrap.Add(field);

            var reseed = MakeIconButton("⚄", "Reseed (randomize + reinitialize)", () => { Reseed(); RefreshPlaybackRows(); });
            reseed.AddToClassList("vfx-seed-reseed");
            wrap.Add(reseed);

            wrap.SetEnabled(!SeedLocked()); // grey out while Reseed on Play overrides the seed

            return (wrap, () =>
            {
                if (_effect != null) field.SetValueWithoutNotify((int)_effect.startSeed);
                field.showMixedValue = EffectsDiffer(ve => ve.startSeed);
                wrap.SetEnabled(!SeedLocked()); // re-evaluate live when Reseed on Play toggles
            });
        }

        // A transport button: either a built-in editor icon (iconName, optionally mirrored
        // horizontally) or a text glyph.
        Button MakeTransportButton(string tooltip, string iconName, Action onClick, string glyph = null, bool mirror = false)
        {
            var b = new Button(onClick) { tooltip = tooltip };
            b.AddToClassList("vfx-tbtn");
            if (!string.IsNullOrEmpty(iconName))
            {
                var img = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                img.style.width = 16; img.style.height = 16;
                img.image = EditorGUIUtility.IconContent(iconName).image;
                if (mirror) img.style.scale = new Scale(new Vector3(-1f, 1f, 1f)); // flip to point the other way
                b.Add(img);
            }
            else if (glyph != null)
            {
                b.text = glyph;
            }
            return b;
        }

        // Step one frame forward (simulate) or backward (reinit + resimulate — the GPU sim has no
        // backward step; see the scrubbing caveat). Pauses, like the mini-transport step.
        void StepFrame(int dir)
        {
            if (_effect == null) return;
            const float dt = 1f / 60f;
            if (dir > 0)
            {
                _effect.pause = true;
                _effect.Simulate(dt, 1);
                _scrubT = Mathf.Min(1f, _scrubT + dt / Mathf.Max(0.0001f, _duration));
                UpdateLive();
            }
            else
            {
                SeekTo(_scrubT - dt / Mathf.Max(0.0001f, _duration));
            }
        }

        // Favorite key for the whole Send Event section (it's an action surface, not a per-row
        // setting, so it pins as one unit into the Favorites group).
        const string kSendEventFavKey = "play:sendevent";

        // "Send Event": a collapsible section group (same .vfx-group chrome as "Playback options"
        // / the renderer sections), containing the quick-chips — OnPlay/OnStop + every custom Event
        // block in the graph. Its header carries a ★ pin (favorite) like a row. Returns 1.
        int AddSendEventSection(VisualElement host)
        {
            string key = "play:events";
            bool open = !_collapsed.Contains(key);

            var group = MakeElement("vfx-group");
            var header = MakeElement("vfx-group-header");
            var twirl = new Label(open ? "▾" : "▸") { pickingMode = PickingMode.Ignore };
            twirl.AddToClassList("vfx-group-twirl");
            header.Add(twirl);
            var titleLbl = new Label("Send Event");
            titleLbl.AddToClassList("vfx-group-title");
            header.Add(titleLbl);
            // ★ pin: toggles the section's favorite (StopPropagation so it doesn't also collapse).
            var star = MakeIconButton(IsFav(kSendEventFavKey) ? "★" : "☆",
                IsFav(kSendEventFavKey) ? "Unpin from Favorites" : "Pin to Favorites", () => ToggleFav(kSendEventFavKey));
            star.AddToClassList("vfx-group-pin");
            star.EnableInClassList("vfx-group-pin--on", IsFav(kSendEventFavKey));
            star.RegisterCallback<ClickEvent>(e => e.StopPropagation());
            header.Add(star);
            header.tooltip = "Click to expand/collapse";
            header.RegisterCallback<ClickEvent>(e =>
            {
                if (_collapsed.Contains(key)) _collapsed.Remove(key); else _collapsed.Add(key);
                _state.SaveCollapsed(_collapsed);
                RebuildBodyOnly();
            });
            group.Add(header);

            var content = MakeElement("vfx-group-content");
            content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            // event buttons on top, sitting on a dark band (like the rail / section bands)
            var band = MakeElement("vfx-sendevent-band");
            band.Add(BuildEventChips());
            content.Add(band);
            content.Add(BuildEventPayloadEditor());    // payload list below
            group.Add(content);

            host.Add(group);
            return 1;
        }

        // Event-payload editor: a reorderable ListView of named/typed attributes (name · type ·
        // value) with the **standard +/- footer** — the + opens the Built-in/Custom add menu, the -
        // deletes the selected row. Attributes are attached to whatever event a chip sends (via
        // VFXEventAttribute). Editing a value mutates the model in place; reorder mutates the list
        // order in place (cosmetic — the payload is keyed by name); add/remove/type/name-swap
        // rebuild the body.
        const float kPayloadRowHeight = 24f;
        const float kPayloadHeaderHeight = 24f;
        const float kPayloadFooterHeight = 26f;
        const float kPayloadChrome = 8f;    // border/padding slack so the last row isn't clipped
        const int kPayloadMaxRows = 12;     // cap the visible list height

        VisualElement BuildEventPayloadEditor()
        {
            var box = MakeElement("vfx-payload");

            // Snapshot the graph's current custom attributes so GraphCustom rows can flag staleness
            // (name renamed/deleted, or type changed) without each row hitting reflection.
            _graphCustomLookup.Clear();
            foreach (var (cname, ctypeIdx) in VfxGraphReflection.GetCustomAttributes(_effect != null ? _effect.visualEffectAsset : null))
                _graphCustomLookup[cname] = (EventAttrType)Mathf.Clamp(ctypeIdx, 0, (int)EventAttrType.Int);

            // A bordered ListView with an integrated foldout header + the standard +/- footer —
            // the VFX Event Tester look. The header replaces the separate "Event Attributes" label.
            var list = new ListView
            {
                itemsSource = _eventPayload,
                reorderable = true,
                reorderMode = ListViewReorderMode.Animated, // drag handle per row
                selectionType = SelectionType.Single,       // so the footer's - removes the selection
                showBorder = true,
                showFoldoutHeader = true,
                headerTitle = "Event Attributes",
                showBoundCollectionSize = false,            // no editable size field (manual count → null items)
                showAddRemoveFooter = true,                 // add/remove only via the +/- footer
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                fixedItemHeight = kPayloadRowHeight,
                makeItem = () => MakeElement("vfx-payload-itemhost"),
                bindItem = (el, i) =>
                {
                    el.Clear();
                    if (i < 0 || i >= _eventPayload.Count) return;
                    // a manually-grown collection can hold null entries — backfill with a default.
                    if (_eventPayload[i] == null)
                        _eventPayload[i] = new EventAttr { Name = "customAttribute", Type = EventAttrType.Float, Value = 0f, BuiltIn = false };
                    el.Add(BuildPayloadRow(_eventPayload[i]));
                },
                // shown when the list has no items (UIToolkit's empty-state element factory)
                makeNoneElement = () =>
                {
                    var empty = new Label("List is Empty");
                    empty.AddToClassList("vfx-payload-empty");
                    return empty;
                },
            };
            list.AddToClassList("vfx-payload-list");
            // + opens the Built-in/Custom menu (not a blank default item); - removes the selection.
            list.onAdd = _ => ShowAddAttributeMenu();
            list.onRemove = lv =>
            {
                int sel = lv.selectedIndex;
                if (sel < 0) sel = _eventPayload.Count - 1;
                if (sel >= 0 && sel < _eventPayload.Count) _eventPayload.RemoveAt(sel);
                RebuildBodyOnly();
            };

            // No scrollbar — hide both scrollers (content stays wheel/drag reachable on the rare
            // overflow). Capped at kPayloadMaxRows so the list never grows unbounded; ≥1 row so the
            // empty message has room.
            var sv = list.Q<ScrollView>();
            if (sv != null)
            {
                sv.verticalScrollerVisibility = ScrollerVisibility.Hidden;
                sv.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            }
            int rows = Mathf.Clamp(_eventPayload.Count, 1, kPayloadMaxRows);
            list.style.height = kPayloadHeaderHeight + rows * kPayloadRowHeight + kPayloadFooterHeight + kPayloadChrome;
            box.Add(list);

            return box;
        }

        // One payload-attribute row, with aligned columns: name · type · value · ✕. Built-in:
        // name is a dropdown of standard attributes (swap re-types the row); type is a grayed-out,
        // disabled dropdown (fixed by the attribute). Custom: editable name + a live type dropdown.
        VisualElement BuildPayloadRow(EventAttr a)
        {
            var row = MakeElement("vfx-payload-row");
            if (a == null) return row; // defensive: a manually-grown list can hold null entries

            if (a.BuiltIn)
            {
                // Name = dropdown into the grouped standard-attribute menu; type grayed (fixed).
                row.Add(MakeNameDropdown(a.Name, "Standard attribute (swap to another)", () => ShowBuiltinNameMenu(a)));
                row.Add(MakeAttrTypeControl(a, editable: false));
            }
            else if (a.GraphCustom)
            {
                // Name = dropdown into the graph's custom-attribute list; type grayed (fixed). Flag if
                // the blackboard attribute was since renamed/deleted (missing) or retyped (mismatch) —
                // the user reconciles it via the dropdown (we never silently change the row).
                bool found = _graphCustomLookup.TryGetValue(a.Name, out var graphType);
                bool mismatch = found && graphType != a.Type;
                bool stale = !found || mismatch;
                string tip = !found
                    ? $"“{a.Name}” is not a custom attribute in this graph — pick another or remove it"
                    : mismatch
                        ? $"Graph declares “{a.Name}” as {AttrTypeLabel(graphType)} (this row is {AttrTypeLabel(a.Type)})"
                        : "Graph custom attribute (swap to another)";
                row.Add(MakeNameDropdown(a.Name, tip, () => ShowGraphCustomNameMenu(a), warn: stale));
                row.Add(MakeAttrTypeControl(a, editable: false));
            }
            else
            {
                var nameField = new TextField { value = a.Name };
                nameField.AddToClassList("vfx-payload-name");
                nameField.RegisterValueChangedCallback(e => a.Name = e.newValue);
                row.Add(nameField);

                // Type = the blackboard type icon; click opens the type menu (re-types the row).
                row.Add(MakeAttrTypeControl(a, editable: true));
            }

            var value = BuildAttrValueControl(a);
            value.AddToClassList("vfx-payload-value");
            row.Add(value);

            return row;
        }

        // Short type labels (used in the type dropdowns + the add menu) to save horizontal space.
        static string AttrTypeLabel(EventAttrType t)
        {
            switch (t)
            {
                case EventAttrType.Vector2: return "Vec 2";
                case EventAttrType.Vector3: return "Vec 3";
                case EventAttrType.Vector4: return "Vec 4";
                case EventAttrType.Bool: return "Bool";
                case EventAttrType.Uint: return "Uint";
                case EventAttrType.Int: return "Int";
                default: return "Float";
            }
        }

        // Custom-attribute type choices, in dropdown order (Float, V2, V3, V4, Bool, Uint, Int).
        static readonly List<EventAttrType> s_AttrTypes = new List<EventAttrType>
        {
            EventAttrType.Float, EventAttrType.Vector2, EventAttrType.Vector3, EventAttrType.Vector4,
            EventAttrType.Bool, EventAttrType.Uint, EventAttrType.Int,
        };

        // Type control = the VFX-Graph blackboard **type icon** (Float/Vector2-4/Boolean/Integer).
        // Editable (custom) → a button that opens a type menu; non-editable (built-in) → a grayed,
        // disabled icon holder. Replaces the text PopupField.
        VisualElement MakeAttrTypeControl(EventAttr a, bool editable)
        {
            var icon = new Image { image = AttrTypeIcon(a.Type), scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            icon.AddToClassList("vfx-payload-typeicon");

            if (!editable)
            {
                var holder = MakeElement("vfx-payload-type");
                holder.tooltip = AttrTypeLabel(a.Type) + " (fixed for built-in attributes)";
                holder.Add(icon);
                holder.SetEnabled(false);
                return holder;
            }

            var btn = new Button(() => ShowTypeMenu(a)) { tooltip = AttrTypeLabel(a.Type) };
            btn.AddToClassList("vfx-payload-type");
            btn.AddToClassList("vfx-payload-typebtn");
            btn.Add(icon);
            return btn;
        }

        // Menu of the custom attribute types (icon shows on the row; the menu lists names).
        void ShowTypeMenu(EventAttr a)
        {
            var menu = new GenericMenu();
            foreach (var t in s_AttrTypes)
            {
                var tt = t;
                menu.AddItem(new GUIContent(AttrTypeLabel(tt)), a.Type == tt, () =>
                {
                    a.Type = tt;
                    a.Value = DefaultAttrValue(tt);
                    RebuildBodyOnly();
                });
            }
            menu.ShowAsContext();
        }

        // The blackboard type icon for a payload type (Uint/Int share "Integer"; Bool → "Boolean").
        static Texture2D AttrTypeIcon(EventAttrType t)
        {
            string name;
            switch (t)
            {
                case EventAttrType.Vector2: name = "Vector2"; break;
                case EventAttrType.Vector3: name = "Vector3"; break;
                case EventAttrType.Vector4: name = "Vector4"; break;
                case EventAttrType.Bool: name = "Boolean"; break;
                case EventAttrType.Uint:
                case EventAttrType.Int: name = "Integer"; break;
                default: name = "Float"; break;
            }
            const string dir = "Packages/com.unity.visualeffectgraph/Editor/UIResources/VFX/types/";
            var tex = EditorGUIUtility.isProSkin
                ? AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}d_{name}@2x.png")
                : null;
            return tex ?? AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}{name}@2x.png");
        }

        // The value editor for one attribute, bound to a.Value (in-place; no rebuild on edit).
        VisualElement BuildAttrValueControl(EventAttr a)
        {
            // The standard `color` attribute is a Vector3 (RGB) but reads best as a color swatch;
            // edit it with a ColorField while keeping the value a Vector3 (so it sends via SetVector3).
            if (a.BuiltIn && a.Name == "color")
            {
                var v = a.Value is Vector3 cv ? cv : Vector3.one;
                var f = new ColorField { value = new Color(v.x, v.y, v.z), hdr = true };
                f.RegisterValueChangedCallback(e => a.Value = new Vector3(e.newValue.r, e.newValue.g, e.newValue.b));
                return f;
            }

            switch (a.Type)
            {
                case EventAttrType.Bool:
                {
                    var f = new Toggle { value = a.Value is bool b && b };
                    f.RegisterValueChangedCallback(e => a.Value = e.newValue);
                    return f;
                }
                case EventAttrType.Int:
                {
                    var f = new IntegerField { value = a.Value is int i ? i : 0 };
                    f.RegisterValueChangedCallback(e => a.Value = e.newValue);
                    new FieldMouseDragger<int>(f).SetDragZone(f); // drag-scrub like the vector components
                    return f;
                }
                case EventAttrType.Uint:
                {
                    var f = new IntegerField { value = a.Value is uint u ? (int)u : 0 };
                    f.RegisterValueChangedCallback(e => a.Value = (uint)Mathf.Max(0, e.newValue));
                    new FieldMouseDragger<int>(f).SetDragZone(f);
                    return f;
                }
                case EventAttrType.Vector2:
                {
                    var f = new Vector2Field { value = a.Value is Vector2 v ? v : Vector2.zero };
                    f.RegisterValueChangedCallback(e => a.Value = e.newValue);
                    return f;
                }
                case EventAttrType.Vector3:
                {
                    var f = new Vector3Field { value = a.Value is Vector3 v ? v : Vector3.zero };
                    f.RegisterValueChangedCallback(e => a.Value = e.newValue);
                    return f;
                }
                case EventAttrType.Vector4:
                {
                    var f = new Vector4Field { value = a.Value is Vector4 v ? v : Vector4.zero };
                    f.RegisterValueChangedCallback(e => a.Value = e.newValue);
                    return f;
                }
                default: // Float
                {
                    var f = new FloatField { value = a.Value is float fl ? fl : 0f };
                    f.RegisterValueChangedCallback(e => a.Value = e.newValue);
                    new FieldMouseDragger<float>(f).SetDragZone(f); // drag-scrub like the vector components
                    return f;
                }
            }
        }

        static object DefaultAttrValue(EventAttrType t)
        {
            switch (t)
            {
                case EventAttrType.Bool: return true;
                case EventAttrType.Int: return 0;
                case EventAttrType.Uint: return 0u;
                case EventAttrType.Vector2: return Vector2.zero;
                case EventAttrType.Vector3: return Vector3.zero;
                case EventAttrType.Vector4: return Vector4.zero;
                default: return 0f;
            }
        }

        // Default value for a standard attribute (color starts white, not black).
        static object StdDefault(StdAttr s) => s.Name == "color" ? (object)Vector3.one : DefaultAttrValue(s.Type);

        // Populate a menu with every standard attribute under `root`, grouped by a grayed section
        // header (`AddDisabledItem`) + `AddSeparator` between groups, alphabetical within each
        // section. `checkedName` shows the radio dot on the current pick. Shared by the "+ Attribute"
        // add menu and the built-in row's name-swap menu.
        void AddStdAttrMenuItems(GenericMenu menu, string root, string checkedName, Action<StdAttr> onPick)
        {
            bool firstSection = true;
            foreach (var section in s_StdSections)
            {
                if (!firstSection) menu.AddSeparator(root); // divider between section groups
                firstSection = false;
                menu.AddDisabledItem(new GUIContent(root + section)); // section header (grayed label)

                foreach (var s in s_StdAttrs.Where(x => x.Section == section).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var std = s;
                    menu.AddItem(new GUIContent($"{root}{std.Name}  ({AttrTypeLabel(std.Type)})"), std.Name == checkedName, () => onPick(std));
                }
            }
        }

        // The "+ Attribute" dropdown: two entries — "Built-in Attribute" (the grouped standard list)
        // and "Custom Attribute" (a free name/type).
        void ShowAddAttributeMenu()
        {
            var menu = new GenericMenu();
            AddStdAttrMenuItems(menu, "Built-in Attribute/", null, std =>
            {
                _eventPayload.Add(new EventAttr { Name = std.Name, Type = std.Type, Value = StdDefault(std), BuiltIn = true });
                RebuildBodyOnly();
            });
            // "Custom Attribute": the graph's own custom attributes (the blackboard list, prefilled
            // name + type) plus a "New Custom Attribute" to add a blank one. When the graph has none,
            // it collapses to a single direct "Custom Attribute" item.
            void AddBlankCustom()
            {
                _eventPayload.Add(new EventAttr { Name = "customAttribute", Type = EventAttrType.Float, Value = 0f, BuiltIn = false });
                RebuildBodyOnly();
            }

            var graphCustoms = VfxGraphReflection.GetCustomAttributes(_effect != null ? _effect.visualEffectAsset : null)
                .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase).ToList();

            if (graphCustoms.Count == 0)
            {
                menu.AddItem(new GUIContent("Custom Attribute"), false, AddBlankCustom);
            }
            else
            {
                const string croot = "Custom Attribute/";
                foreach (var (cname, ctypeIdx) in graphCustoms)
                {
                    var nm = cname;
                    var tp = (EventAttrType)Mathf.Clamp(ctypeIdx, 0, (int)EventAttrType.Int); // Signature ordinal == EventAttrType
                    menu.AddItem(new GUIContent($"{croot}{nm}  ({AttrTypeLabel(tp)})"), false, () =>
                    {
                        _eventPayload.Add(new EventAttr { Name = nm, Type = tp, Value = DefaultAttrValue(tp), GraphCustom = true });
                        RebuildBodyOnly();
                    });
                }
                menu.AddSeparator(croot);
                menu.AddItem(new GUIContent(croot + "New Custom Attribute"), false, AddBlankCustom);
            }

            menu.ShowAsContext();
        }

        // A name field rendered as a dropdown button (left-aligned label + ▾ caret) — shared by the
        // built-in and graph-custom rows (their name is constrained to a known list, not free text).
        // `warn` (stale graph-custom) prefixes a ⚠ and tints the label; the tooltip carries the reason.
        Button MakeNameDropdown(string current, string tooltip, Action onClick, bool warn = false)
        {
            var btn = new Button(onClick) { tooltip = tooltip };
            btn.AddToClassList("vfx-payload-name");
            btn.AddToClassList("vfx-payload-namebtn");
            var lbl = new Label(warn ? "⚠ " + current : current) { pickingMode = PickingMode.Ignore };
            lbl.AddToClassList("vfx-payload-namebtn-label");
            if (warn) lbl.AddToClassList("vfx-payload-namebtn-label--warn");
            btn.Add(lbl);
            var caret = new Label("▾") { pickingMode = PickingMode.Ignore };
            caret.AddToClassList("vfx-payload-namebtn-caret");
            btn.Add(caret);
            return btn;
        }

        // The built-in row's name swap menu — the same grouped standard list (headers + separators),
        // top-level (no "Built-in Attribute/" prefix), with the current attribute checked.
        void ShowBuiltinNameMenu(EventAttr a)
        {
            var menu = new GenericMenu();
            AddStdAttrMenuItems(menu, "", a.Name, std =>
            {
                a.Name = std.Name;
                a.Type = std.Type;         // built-in type follows the attribute
                a.Value = StdDefault(std);
                RebuildBodyOnly();
            });
            menu.ShowAsContext();
        }

        // The graph-custom row's name swap menu — the graph's blackboard custom attributes, with the
        // current one checked; swapping re-types the row to that attribute's declared type.
        void ShowGraphCustomNameMenu(EventAttr a)
        {
            var menu = new GenericMenu();
            var customs = VfxGraphReflection.GetCustomAttributes(_effect != null ? _effect.visualEffectAsset : null)
                .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase);
            foreach (var (cname, ctypeIdx) in customs)
            {
                var nm = cname;
                var tp = (EventAttrType)Mathf.Clamp(ctypeIdx, 0, (int)EventAttrType.Int);
                menu.AddItem(new GUIContent($"{nm}  ({AttrTypeLabel(tp)})"), a.Name == nm, () =>
                {
                    a.Name = nm;
                    a.Type = tp;
                    a.Value = DefaultAttrValue(tp);
                    RebuildBodyOnly();
                });
            }
            menu.ShowAsContext();
        }

        // The left-aligned, wrapping row of event chips: the built-in OnPlay/OnStop plus every
        // custom Event block (VFXBasicEvent.eventName) declared in the graph (via VfxGraphReflection).
        // Clicking a chip SendEvents it to every selected instance.
        VisualElement BuildEventChips()
        {
            var chips = MakeElement("vfx-sendevent-chips");
            foreach (var n in EventChipNames())
            {
                // icon + label as child elements (a Button's intrinsic `text` isn't a flex item,
                // so it would overlap a leading glyph — see the conventions note).
                var name = n;
                var chip = new Button(() => SendEventToAll(name)) { tooltip = $"Send “{name}” to the selected effect(s)" };
                chip.AddToClassList("vfx-sendevent-chip");
                var bolt = new Label("⚡") { pickingMode = PickingMode.Ignore }; // ⚡ event bolt
                bolt.AddToClassList("vfx-sendevent-bolt");
                chip.Add(bolt);
                chip.Add(new Label(name) { pickingMode = PickingMode.Ignore });
                chips.Add(chip);
            }
            return chips;
        }

        // The Send Event section as a Favorites-group entry: a labelled chips row.
        VisualElement BuildSendEventFavRow()
        {
            var row = MakeElement("vfx-row");
            var labelCol = MakeElement("vfx-label-col");
            var label = new Label("Send Event") { tooltip = "Send a graph event to the selected effect(s)." };
            label.AddToClassList("vfx-plabel");
            labelCol.Add(label);
            row.Add(labelCol);
            row.Add(MakeElement("vfx-row-lock"));
            var chips = BuildEventChips();
            chips.AddToClassList("vfx-pcontrol");
            row.Add(chips);
            return row;
        }

        // The Send-Event chips: the built-in OnPlay/OnStop, then every custom Event block declared
        // in the graph (VFXBasicEvent.eventName), distinct and in graph order.
        List<string> EventChipNames()
        {
            var names = new List<string> { VisualEffectAsset.PlayEventName, VisualEffectAsset.StopEventName };
            var asset = _effect != null ? _effect.visualEffectAsset : null;
            foreach (var e in VfxGraphReflection.GetEventNames(asset))
                if (!names.Contains(e)) names.Add(e);
            return names;
        }

        // Send an event to every selected instance, attaching the payload attributes (if any) via
        // a per-instance VFXEventAttribute. OnPlay/OnStop route through Play()/Stop() like the
        // package's Event Tester so the attributes reach the right system.
        void SendEventToAll(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            foreach (var ve in _effects)
            {
                if (ve == null) continue;

                VFXEventAttribute attrib = _eventPayload.Count > 0 ? ve.CreateVFXEventAttribute() : null;
                if (attrib != null)
                {
                    foreach (var a in _eventPayload)
                    {
                        if (string.IsNullOrEmpty(a.Name)) continue;
                        switch (a.Type)
                        {
                            case EventAttrType.Bool:    attrib.SetBool(a.Name, a.Value is bool b && b); break;
                            case EventAttrType.Int:     attrib.SetInt(a.Name, a.Value is int i ? i : 0); break;
                            case EventAttrType.Uint:    attrib.SetUint(a.Name, a.Value is uint u ? u : 0u); break;
                            case EventAttrType.Float:   attrib.SetFloat(a.Name, a.Value is float f ? f : 0f); break;
                            case EventAttrType.Vector2: attrib.SetVector2(a.Name, a.Value is Vector2 v2 ? v2 : Vector2.zero); break;
                            case EventAttrType.Vector3: attrib.SetVector3(a.Name, a.Value is Vector3 v3 ? v3 : Vector3.zero); break;
                            case EventAttrType.Vector4: attrib.SetVector4(a.Name, a.Value is Vector4 v4 ? v4 : Vector4.zero); break;
                        }
                    }
                }

                if (attrib == null) ve.SendEvent(eventName);
                else if (eventName == VisualEffectAsset.PlayEventName) ve.Play(attrib);
                else if (eventName == VisualEffectAsset.StopEventName) ve.Stop(attrib);
                else ve.SendEvent(eventName, attrib);
            }
            UpdateLive();
        }

        // ---- payload persistence (SessionState: survives domain reload, cleared on editor restart) ----

        // EventAttr.Value is `object`, so serialize it into typed buckets (vec / boolVal / intVal).
        [Serializable] struct EventAttrDTO { public string name; public int type; public bool builtIn; public bool graphCustom; public Vector4 vec; public bool boolVal; public int intVal; }
        [Serializable] class AssetPayloadDTO { public string guid; public List<EventAttrDTO> items = new List<EventAttrDTO>(); }
        [Serializable] class PayloadStoreDTO { public List<AssetPayloadDTO> assets = new List<AssetPayloadDTO>(); }

        static EventAttrDTO ToDTO(EventAttr a)
        {
            var d = new EventAttrDTO { name = a.Name, type = (int)a.Type, builtIn = a.BuiltIn, graphCustom = a.GraphCustom };
            switch (a.Type)
            {
                case EventAttrType.Bool: d.boolVal = a.Value is bool b && b; break;
                case EventAttrType.Int: d.intVal = a.Value is int i ? i : 0; break;
                case EventAttrType.Uint: d.intVal = a.Value is uint u ? (int)u : 0; break;
                case EventAttrType.Vector2: { var v = a.Value is Vector2 v2 ? v2 : Vector2.zero; d.vec = new Vector4(v.x, v.y, 0, 0); break; }
                case EventAttrType.Vector3: { var v = a.Value is Vector3 v3 ? v3 : Vector3.zero; d.vec = new Vector4(v.x, v.y, v.z, 0); break; }
                case EventAttrType.Vector4: d.vec = a.Value is Vector4 v4 ? v4 : Vector4.zero; break;
                default: d.vec = new Vector4(a.Value is float f ? f : 0f, 0, 0, 0); break; // Float
            }
            return d;
        }

        static EventAttr FromDTO(EventAttrDTO d)
        {
            var t = (EventAttrType)Mathf.Clamp(d.type, 0, (int)EventAttrType.Int);
            object val;
            switch (t)
            {
                case EventAttrType.Bool: val = d.boolVal; break;
                case EventAttrType.Int: val = d.intVal; break;
                case EventAttrType.Uint: val = (uint)Mathf.Max(0, d.intVal); break;
                case EventAttrType.Vector2: val = new Vector2(d.vec.x, d.vec.y); break;
                case EventAttrType.Vector3: val = new Vector3(d.vec.x, d.vec.y, d.vec.z); break;
                case EventAttrType.Vector4: val = d.vec; break;
                default: val = d.vec.x; break; // Float
            }
            return new EventAttr { Name = d.name, Type = t, Value = val, BuiltIn = d.builtIn, GraphCustom = d.graphCustom };
        }

        void SavePayloads()
        {
            var store = new PayloadStoreDTO();
            foreach (var kv in _payloadByAsset)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null || kv.Value.Count == 0) continue;
                var ap = new AssetPayloadDTO { guid = kv.Key };
                foreach (var a in kv.Value) if (a != null) ap.items.Add(ToDTO(a));
                if (ap.items.Count > 0) store.assets.Add(ap);
            }
            SessionState.SetString(kPayloadSessionKey, store.assets.Count > 0 ? JsonUtility.ToJson(store) : "");
        }

        void LoadPayloads()
        {
            _payloadByAsset.Clear();
            var json = SessionState.GetString(kPayloadSessionKey, "");
            if (string.IsNullOrEmpty(json)) return;
            PayloadStoreDTO store;
            try { store = JsonUtility.FromJson<PayloadStoreDTO>(json); }
            catch { return; }
            if (store?.assets == null) return;
            foreach (var ap in store.assets)
            {
                if (ap == null || string.IsNullOrEmpty(ap.guid)) continue;
                var list = new List<EventAttr>();
                if (ap.items != null) foreach (var d in ap.items) list.Add(FromDTO(d));
                _payloadByAsset[ap.guid] = list;
            }
        }

        // ---- playback property setters (write to every selected instance, undo-tracked) ----

        void SetPlayRate(float v)
        {
            v = Mathf.Max(0f, v);
            Undo.RecordObjects(_effects.ToArray(), "Set Play Rate");
            foreach (var ve in _effects) if (ve != null) { ve.playRate = v; EditorUtility.SetDirty(ve); }
        }

        void SetStartSeed(uint v)
        {
            Undo.RecordObjects(_effects.ToArray(), "Set Start Seed");
            foreach (var ve in _effects) if (ve != null) { ve.startSeed = v; EditorUtility.SetDirty(ve); }
        }

        void SetResetSeedOnPlay(bool v)
        {
            Undo.RecordObjects(_effects.ToArray(), "Set Reset Seed On Play");
            foreach (var ve in _effects) if (ve != null) { ve.resetSeedOnPlay = v; EditorUtility.SetDirty(ve); }
        }

        void SetInitialEvent(string v)
        {
            Undo.RecordObjects(_effects.ToArray(), "Set Initial Event");
            foreach (var ve in _effects) if (ve != null) { ve.initialEventName = v; EditorUtility.SetDirty(ve); }
        }

        // Randomize the seed on every instance and reinitialize so it takes effect immediately.
        void Reseed()
        {
            Undo.RecordObjects(_effects.ToArray(), "Reseed VFX");
            foreach (var ve in _effects)
                if (ve != null)
                {
                    ve.startSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
                    ve.Reinit();
                    EditorUtility.SetDirty(ve);
                }
        }

        // Keep every visible playback row (favorites copy + section copy) + chrome in sync.
        void RefreshPlaybackRows()
        {
            foreach (var (row, f, sync) in _playbackRows)
            {
                sync();
                row.EnableInClassList("vfx-row--modified", f.IsModified());
            }
            PopulateChips();
            UpdateFooter();
        }

        // The "All" tab: a traditional inspector — properties, renderer, and playback stacked
        // in one scroll with no section rail. Search still filters each block (the section is
        // forced to "all" because this tab has no rail). Each block sits under a collapsible
        // top-level header (AddAllSection); a unified Favorites group sits above them.
        void BuildAllTab(VisualElement body)
        {
            // One renderer SerializedObject + fields, shared by the unified pinned group and the
            // Renderer section below (so both edit the same instance and stay in sync).
            var renderers = GetRenderers();
            SerializedObject rendererSo = null;
            List<RField> rendererFields = null;
            if (renderers.Length > 0)
            {
                rendererSo = new SerializedObject(renderers.Cast<Object>().ToArray());
                rendererFields = BuildRendererFields(rendererSo, renderers, GetRendererDefaults());
            }
            _rendererRows = new List<(VisualElement, RField)>(); // reset before any renderer row

            // Unified Favorites group: property favorites (struct-aware) + renderer + playback.
            var extraFavs = RendererFavoriteSettings(rendererSo, rendererFields);
            extraFavs.AddRange(PlaybackFavoriteSettings());
            AddFavoriteGroup(body, includeProps: true, extraFavs);

            AddAllSection(body, "Properties", c => PopulateProperties(c, showEmpty: false));
            AddAllSection(body, "Playback", BuildPlaybackContent); // favorites shown in the unified group above
            AddAllSection(body, "Renderer", c =>
            {
                if (renderers.Length == 0)
                    BuildPlaceholder(c, "This Visual Effect has no renderer component to configure.");
                else
                    c.Add(BuildRendererSections(rendererSo, rendererFields));
            });
            AddAllSection(body, "Debug", c =>
                BuildPlaceholder(c, "Debug tab — coming in the next pass.\nLive stats, systems, visualizers."));
        }

        // A collapsible top-level section on the All tab: a header (twirl + title) over a content
        // container whose display toggles. Collapse persists under "all:<title>" in _collapsed.
        void AddAllSection(VisualElement body, string title, Action<VisualElement> buildContent)
        {
            string key = "all:" + title;
            bool open = !_collapsed.Contains(key);

            var header = MakeElement("vfx-allsection-head");
            var twirl = new Label(open ? "▾" : "▸") { pickingMode = PickingMode.Ignore };
            twirl.AddToClassList("vfx-allsection-twirl");
            header.Add(twirl);
            var titleLbl = new Label(title);
            titleLbl.AddToClassList("vfx-allsection-title");
            header.Add(titleLbl);
            header.tooltip = "Click to expand/collapse · Alt+click for all nested";
            header.RegisterCallback<ClickEvent>(e =>
            {
                bool collapse = !_collapsed.Contains(key); // the section's new state
                if (collapse) _collapsed.Add(key); else _collapsed.Remove(key);
                if (e.altKey) // fold/unfold every group inside this section to match
                    SetCollapsedAll(AllSectionCollapseKeys(title), collapse);
                _state.SaveCollapsed(_collapsed);
                RebuildBodyOnly();
            });
            body.Add(header);

            var content = MakeElement("vfx-allsection-content");
            content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            buildContent(content);
            body.Add(content);
        }

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
        List<(VisualElement row, RField field)> _rendererRows;
        // Playback rows built this populate (favorites copy + section copy), kept in sync on edit
        // via each field's `sync` action (they back live props, not SerializedProperties).
        readonly List<(VisualElement row, PField field, Action sync)> _playbackRows = new List<(VisualElement, PField, Action)>();

        // Event payload (Send Event section): a list of named/typed attributes attached to the
        // event via VFXEventAttribute (modelled on the package's VFX Event Tester overlay). Lives
        // for the window session; survives body rebuilds (not a per-populate list).
        // Enum order = the Custom type dropdown order (Float, V2, V3, V4, Bool, Uint, Int).
        enum EventAttrType { Float, Vector2, Vector3, Vector4, Bool, Uint, Int }
        // BuiltIn = a standard attribute (name picked from a fixed dropdown, type fixed); otherwise
        // a custom attribute (name + type freely edited).
        // BuiltIn = standard attribute (name picked from the standard list, type fixed).
        // GraphCustom = a custom attribute declared in the graph's blackboard (name picked from the
        // graph's list, type fixed). Neither → a free custom attribute (name + type freely edited).
        sealed class EventAttr { public string Name; public EventAttrType Type; public object Value; public bool BuiltIn; public bool GraphCustom; }
        // Payload is scoped per VFX asset: _payloadByAsset[guid] holds each asset's rows; _eventPayload
        // points at the active asset's list (swapped in SetTarget). Persisted in SessionState (survives
        // domain reload, cleared on editor restart) via Save/LoadPayloads.
        readonly Dictionary<string, List<EventAttr>> _payloadByAsset = new Dictionary<string, List<EventAttr>>();
        List<EventAttr> _eventPayload = new List<EventAttr>();
        const string kPayloadSessionKey = "vfxctrl.payloads";
        // The graph's current blackboard custom attributes (name → type), refreshed each time the
        // payload editor builds; used to flag stale GraphCustom rows (renamed/retyped/deleted).
        readonly Dictionary<string, EventAttrType> _graphCustomLookup = new Dictionary<string, EventAttrType>(StringComparer.OrdinalIgnoreCase);

        // The standard attributes offered for built-in payload entries — name, type, and section,
        // restricted to the three settable sections (Basic/Advanced Simulation, Rendering; the
        // System/Collision/Strip categories are read-only outputs). Types from VFXAttributesManager;
        // grouping/ordering from the manual's Reference-Attributes page.
        struct StdAttr { public string Name; public EventAttrType Type; public string Section; }
        static readonly StdAttr[] s_StdAttrs =
        {
            // Basic Simulation
            new StdAttr { Name = "age",            Type = EventAttrType.Float,   Section = "Basic Simulation" },
            new StdAttr { Name = "alive",          Type = EventAttrType.Bool,    Section = "Basic Simulation" },
            new StdAttr { Name = "lifetime",       Type = EventAttrType.Float,   Section = "Basic Simulation" },
            new StdAttr { Name = "position",       Type = EventAttrType.Vector3, Section = "Basic Simulation" },
            new StdAttr { Name = "velocity",       Type = EventAttrType.Vector3, Section = "Basic Simulation" },
            // Advanced Simulation
            new StdAttr { Name = "angle",          Type = EventAttrType.Vector3, Section = "Advanced Simulation" },
            new StdAttr { Name = "angularVelocity",Type = EventAttrType.Vector3, Section = "Advanced Simulation" },
            new StdAttr { Name = "direction",      Type = EventAttrType.Vector3, Section = "Advanced Simulation" },
            new StdAttr { Name = "mass",           Type = EventAttrType.Float,   Section = "Advanced Simulation" },
            new StdAttr { Name = "oldPosition",    Type = EventAttrType.Vector3, Section = "Advanced Simulation" },
            new StdAttr { Name = "targetPosition", Type = EventAttrType.Vector3, Section = "Advanced Simulation" },
            // Rendering
            new StdAttr { Name = "alpha",          Type = EventAttrType.Float,   Section = "Rendering" },
            new StdAttr { Name = "axisX",          Type = EventAttrType.Vector3, Section = "Rendering" },
            new StdAttr { Name = "axisY",          Type = EventAttrType.Vector3, Section = "Rendering" },
            new StdAttr { Name = "axisZ",          Type = EventAttrType.Vector3, Section = "Rendering" },
            new StdAttr { Name = "color",          Type = EventAttrType.Vector3, Section = "Rendering" },
            new StdAttr { Name = "pivot",          Type = EventAttrType.Vector3, Section = "Rendering" },
            new StdAttr { Name = "scale",          Type = EventAttrType.Vector3, Section = "Rendering" },
            new StdAttr { Name = "size",           Type = EventAttrType.Float,   Section = "Rendering" },
            new StdAttr { Name = "texIndex",       Type = EventAttrType.Float,   Section = "Rendering" },
        };
        static readonly string[] s_StdSections = { "Basic Simulation", "Advanced Simulation", "Rendering" };

        // One renderer setting: rail section, label, favorite key, availability (SRP /
        // current-value gates), modified-vs-default test, reset, and a UIToolkit control
        // factory. Built fresh each populate so the closures capture the live SerializedObject.
        sealed class RField
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
        struct RendererDefaults
        {
            public int reflectionProbeUsage, lightProbeUsage, rendererPriority, sortingOrder, sortingLayerID;
            public uint renderingLayerMask;
        }
        static RendererDefaults? s_rendererDefaults;

        RendererDefaults GetRendererDefaults()
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

        VFXRenderer[] GetRenderers() => _effects
            .Where(ve => ve != null)
            .Select(ve => ve.GetComponent<VFXRenderer>())
            .Where(r => r != null)
            .ToArray();

        static bool RendererPropModified(SerializedProperty p, int def) =>
            p != null && (p.hasMultipleDifferentValues || p.intValue != def);

        void BuildRendererTab(VisualElement body)
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
            var fields = BuildRendererFields(so, renderers, GetRendererDefaults());
            _rendererRows = new List<(VisualElement, RField)>();
            AddFavoriteGroup(body, includeProps: false, RendererFavoriteSettings(so, fields)); // favorited renderer rows share `so`
            body.Add(BuildRendererSections(so, fields));
        }

        // The Probes/Additional section groups for a given renderer SO+fields, as a host element
        // (so the All tab can share one SO between the pinned group and these sections). Caller
        // resets _rendererRows first; rows accumulate into it for live marker refresh.
        VisualElement BuildRendererSections(SerializedObject so, List<RField> fields)
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
        List<Setting> RendererFavoriteSettings(SerializedObject so, List<RField> fields)
        {
            var list = new List<Setting>();
            if (fields == null) return list;
            foreach (var f in fields)
                if (f.Available && IsFav(f.FavKey))
                    list.Add(new Setting { FavKey = f.FavKey, BuildRow = () => BuildRendererRow(f, so) });
            return list;
        }

        // A collapsible section group (Probes / Additional Settings) styled like a category
        // group, containing the visible renderer rows. Returns the number of rows shown.
        int AddRendererSection(VisualElement host, SerializedObject so, string id, string title, List<RField> fields, Func<RField, bool> show)
        {
            var visible = fields.Where(f => f.Section == id && show(f)).ToList();
            if (visible.Count == 0) return 0;

            string key = "render:" + id;
            bool forceOpen = !string.IsNullOrEmpty(_search.Trim());
            bool open = forceOpen || !_collapsed.Contains(key);

            var group = MakeElement("vfx-group");
            var header = MakeElement("vfx-group-header");
            var twirl = new Label(open ? "▾" : "▸") { pickingMode = PickingMode.Ignore };
            twirl.AddToClassList("vfx-group-twirl");
            header.Add(twirl);
            var titleLbl = new Label(title);
            titleLbl.AddToClassList("vfx-group-title");
            header.Add(titleLbl);
            var count = new Label(visible.Count.ToString());
            count.AddToClassList("vfx-group-count");
            header.Add(count);
            if (!forceOpen)
            {
                header.tooltip = "Click to expand/collapse";
                header.RegisterCallback<ClickEvent>(e =>
                {
                    if (_collapsed.Contains(key)) _collapsed.Remove(key); else _collapsed.Add(key);
                    _state.SaveCollapsed(_collapsed);
                    RebuildBodyOnly();
                });
            }
            group.Add(header);

            var content = MakeElement("vfx-group-content");
            content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var f in visible) content.Add(BuildRendererRow(f, so));
            group.Add(content);
            host.Add(group);
            return visible.Count;
        }

        // A renderer setting as a property-style row: label column, control, hover ↺/★ tools,
        // modified marker. Reset/pin rebuild the body (re-reading the unbound mask/sorting fields).
        VisualElement BuildRendererRow(RField f, SerializedObject so)
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
        void RefreshRendererState()
        {
            if (_rendererRows != null)
                foreach (var (row, f) in _rendererRows)
                    row.EnableInClassList("vfx-row--modified", f.IsModified());
            PopulateChips();
            UpdateFooter();
        }

        // Rebuild the active tab body on the next tick (used when a value change toggles which
        // other rows are available, e.g. probe usage → Anchor/Proxy visibility).
        void DeferRebuildBody() => rootVisualElement.schedule.Execute(RebuildBodyOnly);

        // EnumField over an int-backed serialized property (m_*ProbeUsage). Manual write
        // (intValue + Apply) because BindProperty(EnumField) doesn't persist an int property.
        VisualElement MakeRendererEnum<T>(SerializedProperty prop, SerializedObject so, bool rebuildOnChange = false)
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
        VisualElement MakeRenderingLayerMaskField(SerializedObject so)
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

        const string kAddSortingLayer = "Add Sorting Layer…";

        VisualElement MakeSortingLayerPopup(SerializedProperty layerIdProp, SerializedObject so)
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
        List<RField> BuildRendererFields(SerializedObject so, VFXRenderer[] renderers, RendererDefaults d)
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
        bool ChipOk(RField f) =>
            _filter == "all" ||
            (_filter == "fav" && IsFav(f.FavKey)) ||
            (_filter == "mod" && f.IsModified());

        // (leaf, fav, mod) counts for the renderer tab's filter chips.
        (int leaf, int fav, int mod) RendererChipCounts()
        {
            var renderers = GetRenderers();
            if (renderers.Length == 0) return (0, 0, 0);
            var so = new SerializedObject(renderers.Cast<Object>().ToArray());
            so.Update();
            var fields = BuildRendererFields(so, renderers, GetRendererDefaults());
            int leaf = fields.Count(f => f.Available);
            int fav = fields.Count(f => f.Available && IsFav(f.FavKey));
            int mod = fields.Count(f => f.Available && f.IsModified());
            return (leaf, fav, mod);
        }

        // Reset every modified renderer field on the selected instances to the fresh-create default.
        void ResetRendererToDefaults()
        {
            var renderers = GetRenderers();
            if (renderers.Length == 0) return;
            var so = new SerializedObject(renderers.Cast<Object>().ToArray());
            so.Update();
            foreach (var f in BuildRendererFields(so, renderers, GetRendererDefaults()))
                if (f.Available && f.IsModified()) f.Reset();
            so.ApplyModifiedProperties();
        }

        string EmptyMessage()
        {
            if (_filter == "mod") return "Nothing edited yet — all properties match the graph defaults.";
            if (_filter == "fav") return "No favorite properties. Hover a row and tap ★ to add one.";
            if (!string.IsNullOrEmpty(_search.Trim())) return $"No properties match “{_search}”.";
            return "No properties exposed in this Visual Effect Graph.";
        }

        bool Visible(VfxExposedParam p)
        {
            string q = _search.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(q) &&
                !p.Name.ToLowerInvariant().Contains(q) &&
                !(p.Label != null && p.Label.ToLowerInvariant().Contains(q)))
                return false;
            string section = CurrentSection();
            if (section != "all" && CategoryOf(p) != section) return false;
            if (_filter == "fav" && !IsFav(FavKeyOf(p))) return false;
            if (_filter == "mod" && !VfxPropertySheet.IsOverridden(_so, p)) return false;
            return true;
        }

        Button MakeChip(string id, string label, int count)
        {
            var chip = new Button(() => { _filter = id; _state.Filter = id; RebuildBodyOnly(); });
            chip.AddToClassList("vfx-fchip");
            if (_filter == id) chip.AddToClassList("vfx-fchip--active");
            chip.Add(new Label(label));
            var n = new Label(count.ToString());
            n.AddToClassList("vfx-fchip-n");
            chip.Add(n);
            return chip;
        }

        // The active tab's section rail: an "All" button plus the tab's declared sections
        // (categories for Properties, Probes/Additional for Renderer, …). Selection is
        // per-tab (CurrentSection / SetSection).
        VisualElement BuildRail(TabDef def)
        {
            var rail = new ScrollView(ScrollViewMode.Horizontal);
            rail.AddToClassList("vfx-hrail");
            rail.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            rail.verticalScrollerVisibility = ScrollerVisibility.Hidden;

            rail.Add(MakeRailButton("all", "All", default, true));
            foreach (var s in def.Sections())
                rail.Add(MakeRailButton(s.Id, s.Label, s.Dot, !s.HasDot));

            AttachHScroll(rail);
            return rail;
        }

        // Make a horizontal ScrollView scroll on a vertical (or horizontal) wheel when its
        // content overflows. Shared by the tab strip and the section rail.
        static void AttachHScroll(ScrollView sv)
        {
            sv.RegisterCallback<WheelEvent>(e =>
            {
                float content = sv.contentContainer.layout.width;
                if (content <= sv.layout.width) return;
                float d = Mathf.Abs(e.delta.x) > Mathf.Abs(e.delta.y) ? e.delta.x : e.delta.y;
                if (Mathf.Approximately(d, 0)) return;
                sv.scrollOffset = new Vector2(sv.scrollOffset.x + d * 18f, sv.scrollOffset.y);
                e.StopPropagation();
            });
        }

        Button MakeRailButton(string id, string label, Color dot, bool isAll)
        {
            var btn = new Button(() =>
            {
                SetSection(id);
                PopulateActiveTab();
            });
            btn.AddToClassList("vfx-hrail-btn");
            if (CurrentSection() == id) btn.AddToClassList("vfx-hrail-btn--active");
            // dot + label as flex children so the dot sits to the left of the label
            // (the Button's intrinsic text isn't a flex item and would overlap).
            if (!isAll)
            {
                var d = MakeElement("vfx-rail-dot");
                d.style.backgroundColor = dot;
                btn.Add(d);
            }
            btn.Add(new Label(label));
            return btn;
        }

        // A favorite-able setting from any source (property, renderer, …) that knows how to
        // render its own row. Lets the Favorites group mix sources uniformly (the All tab).
        sealed class Setting
        {
            public string FavKey;
            public Func<VisualElement> BuildRow;
        }

        // Prepend a "Favorites" group to a tab body — but only when not already narrowing via the
        // rail section, a chip, or search (those isolate favorites themselves). `includeProps`
        // adds the property favorites (struct-aware); `rendererFavs` adds renderer rows.
        void AddFavoriteGroup(VisualElement body, bool includeProps, List<Setting> rendererFavs)
        {
            if (CurrentSection() != "all" || _filter != "all" || !string.IsNullOrEmpty(_search.Trim()))
                return;

            // property favorites keep their struct headers (label + space + Edit-Gizmo)
            if (includeProps) BuildStructLeavesMap(); // AddDisplayEntries needs the struct maps
            var propDisplay = includeProps ? ComputeFavoriteDisplay() : null;
            int propLeaves = propDisplay?.Count(e => !e.IsStruct) ?? 0;
            int total = propLeaves + (rendererFavs?.Count ?? 0);
            if (total == 0) return;

            body.Add(BuildFavoriteGroup(propDisplay, rendererFavs, total));
        }

        // Quick-access "Favorites" group: a collapsible header styled like a category. Property
        // favorites render through the same struct-aware path as categories (so a pinned Box
        // shows its header + Edit-Gizmo); renderer favorites render as rows.
        VisualElement BuildFavoriteGroup(List<VfxExposedParam> propDisplay, List<Setting> rendererFavs, int count)
        {
            const string key = "Favorites"; // collapse state lives in _collapsed like a category
            bool open = !_collapsed.Contains(key);

            var group = MakeElement("vfx-group");
            group.AddToClassList("vfx-pinned-group");

            var header = MakeElement("vfx-group-header");
            var twirl = new Label(open ? "▾" : "▸") { pickingMode = PickingMode.Ignore };
            twirl.AddToClassList("vfx-group-twirl");
            header.Add(twirl);
            var star = new Label("★") { pickingMode = PickingMode.Ignore };
            star.AddToClassList("vfx-group-star");
            header.Add(star);
            var title = new Label("Favorites");
            title.AddToClassList("vfx-group-title");
            header.Add(title);
            var countLabel = new Label(count.ToString());
            countLabel.AddToClassList("vfx-group-count");
            header.Add(countLabel);
            header.tooltip = "Click to expand/collapse";
            header.RegisterCallback<ClickEvent>(e =>
            {
                if (_collapsed.Contains(key)) _collapsed.Remove(key); else _collapsed.Add(key);
                _state.SaveCollapsed(_collapsed);
                RebuildBodyOnly();
            });
            group.Add(header);

            var content = MakeElement("vfx-group-content");
            content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            if (propDisplay != null && propDisplay.Count > 0)
                AddDisplayEntries(content, propDisplay, forceOpen: false); // structs keep their headers/gizmo
            if (rendererFavs != null)
                foreach (var s in rendererFavs) content.Add(s.BuildRow());
            group.Add(content);

            return group;
        }

        VisualElement BuildGroup(string category, List<VfxExposedParam> props, bool forceOpen, VfxExposedParam gate = null)
        {
            bool open = forceOpen || !_collapsed.Contains(category);

            // a gated category hoists its bool into the header as a master enable toggle;
            // its own row is dropped from the body to avoid duplication
            var entries = gate != null ? props.Where(p => p != gate).ToList() : props;

            // Custom collapsible (not a Foldout) so the header uses the same ClickEvent +
            // altKey path as struct headers — which reliably carries Alt/Option on macOS.
            var group = MakeElement("vfx-group");

            var header = MakeElement("vfx-group-header");
            var twirl = new Label(open ? "▾" : "▸") { pickingMode = PickingMode.Ignore };
            twirl.AddToClassList("vfx-group-twirl");
            header.Add(twirl);
            var dot = MakeElement("vfx-dot");
            dot.style.backgroundColor = GetCategoryColor(category);
            header.Add(dot);
            var title = new Label(category);
            title.AddToClassList("vfx-group-title");
            header.Add(title);
            BaseField<bool> gateToggle = null;
            if (gate != null)
            {
                // master enable toggle; StopPropagation so clicking it doesn't collapse
                gateToggle = Bind(new Toggle(), gate, null, v => v is bool b && b, v => v);
                gateToggle.AddToClassList("vfx-group-enable");
                gateToggle.tooltip = $"Enable “{category}” (drives the exposed “{gate.Label}” bool)";
                gateToggle.RegisterCallback<ClickEvent>(e => e.StopPropagation());
                header.Add(gateToggle);
            }
            var count = new Label(entries.Count(p => !p.IsStruct).ToString());
            count.AddToClassList("vfx-group-count");
            header.Add(count);

            if (!forceOpen)
            {
                header.tooltip = "Click to expand/collapse · Alt+click for all nested";
                header.RegisterCallback<ClickEvent>(e =>
                {
                    bool collapse = !_collapsed.Contains(category);
                    if (collapse) _collapsed.Add(category); else _collapsed.Remove(category);
                    if (e.altKey) // recurse to every struct in this category
                        foreach (var s in _params.Where(x => x.IsStruct && CategoryOf(x) == category))
                            ApplyCollapse(s, collapse);
                    _state.SaveCollapsed(_collapsed);
                    RebuildBodyOnly();
                });
            }
            group.Add(header);

            var content = MakeElement("vfx-group-content");
            content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            AddDisplayEntries(content, entries, forceOpen);
            group.Add(content);

            if (gate != null)
            {
                ApplyCategoryGate(group, content, gate);
                // re-grey live when the toggle flips (its Bind fires RefreshProperty(gate),
                // which invokes every refresher keyed to gate.Name)
                RegisterRefresher(gate.Name, () => ApplyCategoryGate(group, content, gate));
                // deactivating collapses the category to hide the now-irrelevant props;
                // activating re-opens it. This only drives the normal _collapsed state, so
                // the header twirl still works to expand a gated-off category and peek at
                // its greyed values. Fires on real user toggles only (not refresher syncs).
                if (!forceOpen)
                    gateToggle.RegisterValueChangedCallback(e =>
                    {
                        if (e.newValue) _collapsed.Remove(category); else _collapsed.Add(category);
                        _state.SaveCollapsed(_collapsed);
                        bool open2 = !_collapsed.Contains(category);
                        content.style.display = open2 ? DisplayStyle.Flex : DisplayStyle.None;
                        twirl.text = open2 ? "▾" : "▸";
                    });
            }

            return group;
        }

        // Grey-out + lock a category's body when its gate bool is off (block deactivated in
        // the graph → its parameters are irrelevant). Visual only — collapse is handled
        // separately so the user can still expand to peek. The toggle lives in the header,
        // so disabling the whole content is safe — it stays interactive. Ambiguous multi-edit
        // (mixed values) counts as enabled, so nothing is greyed when unsure.
        void ApplyCategoryGate(VisualElement group, VisualElement content, VfxExposedParam gate)
        {
            bool off = VfxPropertySheet.GetValue(_so, gate) is bool b && !b && !IsMixed(gate);
            content.SetEnabled(!off);                         // native disabled tint + blocks input
            group.EnableInClassList("vfx-group--gated", off); // dim the header (reads even when collapsed)
        }

        // Auto-detect a category's enable gate: a top-level bool leaf whose label matches
        // the category, or is "Enable <Category>" / "Use <Category>" (case/space-insensitive).
        VfxExposedParam FindCategoryGate(string category, List<VfxExposedParam> props)
        {
            if (category == "Uncategorized" || props.Count == 0) return null;
            int minDepth = props.Min(p => p.Depth);
            string cat = NormGate(category);
            VfxExposedParam fallback = null;
            foreach (var p in props)
            {
                if (p.SheetType != "m_Bool" || p.IsStruct || p.Depth != minDepth) continue;
                string n = NormGate(p.Label);
                if (n == cat) return p;                                   // exact name match wins
                if (fallback == null && (n == "enable" + cat || n == "use" + cat)) fallback = p;
            }
            return fallback;
        }

        static string NormGate(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace(" ", "").Replace("_", "").ToLowerInvariant();

        // Render the ordered (already-filtered) entries, nesting struct children inside
        // their collapsible struct parent so depth maps to real containment.
        void AddDisplayEntries(VisualElement parent, List<VfxExposedParam> entries, bool forceOpen)
        {
            var stack = new Stack<(int depth, VisualElement container)>();
            stack.Push((-1, parent));
            var skip = new HashSet<VfxExposedParam>();

            foreach (var p in entries)
            {
                if (skip.Contains(p)) continue; // child already folded into its flattened parent
                while (stack.Count > 1 && stack.Peek().depth >= p.Depth) stack.Pop();
                var container = stack.Peek().container;

                if (p.IsStruct)
                {
                    if (_flattenChild.TryGetValue(p, out var only))
                    {
                        // single-element struct: render as a normal row using the parent's
                        // label + space, the child's control. Skip the child entry.
                        container.Add(BuildRow(only, p.Label, p));
                        skip.Add(only);
                    }
                    else if (_inlineStruct.TryGetValue(p, out var comps))
                    {
                        // scalar components on one row, like a Vector2/3/4. Skip children.
                        container.Add(BuildInlineStructRow(p, comps));
                        foreach (var c in comps) skip.Add(c);
                    }
                    else
                    {
                        var content = MakeElement("vfx-struct-content");
                        container.Add(BuildStructGroup(p, content, forceOpen));
                        stack.Push((p.Depth, content));
                    }
                }
                else
                {
                    container.Add(BuildRow(p));
                }
            }
        }

        string StructKey(VfxExposedParam p) => "struct:" + p.Name;

        void ApplyCollapse(VfxExposedParam structParam, bool collapse)
        {
            if (collapse) _collapsed.Add(StructKey(structParam));
            else _collapsed.Remove(StructKey(structParam));
        }

        // All struct entries nested under a given struct (for Alt+click recurse-all).
        IEnumerable<VfxExposedParam> DescendantStructs(VfxExposedParam p)
        {
            int i = _params.IndexOf(p);
            if (i < 0) yield break;
            for (int j = i + 1; j < _params.Count && _params[j].Depth > p.Depth; j++)
                if (_params[j].IsStruct) yield return _params[j];
        }

        // ---- Alt+click "collapse/expand all" (All-tab sections + tab headers) ----
        // Section-group collapse keys per content area (categories + structs for Properties,
        // the fixed section-group keys for Playback/Renderer). Used to drive the whole
        // hierarchy from a single Alt+click, like Alt+click on a category/struct header.
        IEnumerable<string> PropertyCollapseKeys()
        {
            var seenCat = new HashSet<string>();
            foreach (var p in _params)
            {
                if (p.IsStruct) yield return StructKey(p);
                var c = CategoryOf(p);
                if (seenCat.Add(c)) yield return c;
            }
        }
        static readonly string[] PlaybackCollapseKeys = { "play:options", "play:events" };
        static readonly string[] RendererCollapseKeys = { "render:probes", "render:additional" };

        // The collapsible keys inside one All-tab section ("Properties"/"Playback"/"Renderer").
        IEnumerable<string> AllSectionCollapseKeys(string title)
        {
            switch (title)
            {
                case "Properties": return PropertyCollapseKeys();
                case "Playback": return PlaybackCollapseKeys;
                case "Renderer": return RendererCollapseKeys;
                default: return Enumerable.Empty<string>();
            }
        }

        // Every collapsible key inside a tab's body (for Alt+click on the tab). The All tab
        // also includes its own top-level section headers so the whole tree folds at once.
        IEnumerable<string> TabCollapseKeys(string tabId)
        {
            switch (tabId)
            {
                case "props": return PropertyCollapseKeys();
                case "play": return PlaybackCollapseKeys;
                case "render": return RendererCollapseKeys;
                case "all": return new[] { "all:Properties", "all:Playback", "all:Renderer", "all:Debug" }
                    .Concat(PropertyCollapseKeys()).Concat(PlaybackCollapseKeys).Concat(RendererCollapseKeys);
                default: return Enumerable.Empty<string>();
            }
        }

        void SetCollapsedAll(IEnumerable<string> keys, bool collapse)
        {
            foreach (var k in keys)
                if (collapse) _collapsed.Add(k); else _collapsed.Remove(k);
        }

        // A compound parent (e.g. AABox): a collapsible header with pin-all / reset-all
        // acting on every component; children are added into `content` by the caller.
        VisualElement BuildStructGroup(VfxExposedParam p, VisualElement content, bool forceOpen)
        {
            var container = MakeElement("vfx-struct");
            var leaves = _structLeaves.TryGetValue(p, out var l) ? l : new List<VfxExposedParam>();

            bool collapsed = _collapsed.Contains(StructKey(p));
            bool open = forceOpen || !collapsed;

            var header = MakeElement("vfx-row");
            header.AddToClassList("vfx-struct-row");
            header.EnableInClassList("vfx-row--modified", leaves.Any(c => VfxPropertySheet.IsOverridden(_so, c)));
            if (leaves.Any(c => IsFav(FavKeyOf(c)))) header.AddToClassList("vfx-row--fav");
            _structHeaders.Add((header, leaves)); // so a child edit re-bolds this header live

            // clickable label area toggles collapse; tools sit outside it. The twirl
            // is absolutely positioned in the indent to the left of the label, so it
            // doesn't shift the label — and only appears on hover (discoverability).
            var click = MakeElement("vfx-struct-click");
            var twirl = new Label(open ? "▾" : "▸") { pickingMode = PickingMode.Ignore };
            twirl.AddToClassList("vfx-struct-twirl");
            click.Add(twirl);
            var label = new Label(p.Label) { tooltip = string.IsNullOrEmpty(p.Tooltip) ? p.RealType : p.Tooltip };
            label.AddToClassList("vfx-plabel");
            label.AddToClassList("vfx-struct-label");
            click.Add(label);
            var structSpace = BuildSpaceIcon(p);
            if (structSpace != null) click.Add(structSpace);
            click.tooltip = "Click to expand/collapse · Alt+click for all nested";
            click.RegisterCallback<ClickEvent>(e =>
            {
                bool collapse = !_collapsed.Contains(StructKey(p)); // toggle this struct
                ApplyCollapse(p, collapse);
                if (e.altKey) // recurse to every nested struct, like the Hierarchy
                    foreach (var d in DescendantStructs(p)) ApplyCollapse(d, collapse);
                _state.SaveCollapsed(_collapsed);
                RebuildBodyOnly();
            });
            header.Add(click);
            var headerTools = BuildBulkTools(leaves);
            if (IsGizmoSupported(p) && _structLeaves.TryGetValue(p, out var glv) && glv.Count > 0)
            {
                headerTools.Insert(0, BuildGizmoButton(p, inline: true)); // left of reset/pin
                headerTools.style.width = StyleKeyword.Auto; // widen to fit the extra icon
            }
            header.Add(headerTools);

            content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;

            container.Add(header);
            container.Add(content);
            return container;
        }

        // reset-all / pin-all tools that act on every leaf of a struct (header or inline row)
        VisualElement BuildBulkTools(List<VfxExposedParam> leaves)
        {
            var tools = MakeElement("vfx-row-tools");
            var resetAll = MakeIconButton("↺", "Reset all components", () =>
            {
                foreach (var c in leaves)
                    ResetAll(c);
                RebuildBodyOnly();
            });
            resetAll.AddToClassList("vfx-tool-reset");
            tools.Add(resetAll);

            bool allFav = leaves.Count > 0 && leaves.All(c => IsFav(FavKeyOf(c)));
            var starAll = MakeIconButton(allFav ? "★" : "☆", allFav ? "Unpin all components" : "Pin all components", () =>
            {
                foreach (var c in leaves)
                    if (allFav) _favorites.Remove(FavKeyOf(c)); else _favorites.Add(FavKeyOf(c));
                _state.SaveFavorites(_favorites);
                RebuildBodyOnly();
            });
            starAll.AddToClassList("vfx-tool-fav");
            tools.Add(starAll);
            return tools;
        }

        // A scalar-only struct (e.g. Flipbook X/Y) rendered inline on one row: parent
        // label + space, then each component as a labeled mini field, like a Vector2.
        VisualElement BuildInlineStructRow(VfxExposedParam p, List<VfxExposedParam> comps)
        {
            var row = MakeElement("vfx-row");
            row.userData = p;
            row.EnableInClassList("vfx-row--modified", comps.Any(c => VfxPropertySheet.IsOverridden(_so, c)));
            if (comps.Any(c => IsFav(FavKeyOf(c)))) row.AddToClassList("vfx-row--fav");
            _structHeaders.Add((row, comps)); // live bold/reset aggregation on edit

            var labelCol = MakeElement("vfx-label-col");
            var label = new Label(p.Label) { tooltip = string.IsNullOrEmpty(p.Tooltip) ? p.RealType : p.Tooltip };
            label.AddToClassList("vfx-plabel");
            labelCol.Add(label);
            var spaceIcon = BuildSpaceIcon(p);
            if (spaceIcon != null) labelCol.Add(spaceIcon);
            row.Add(labelCol);

            row.Add(MakeElement("vfx-row-lock")); // reserved gutter (no proportional lock here)

            var controlHost = MakeElement("vfx-pcontrol");
            foreach (var c in comps)
            {
                var comp = MakeElement("vfx-vec-comp");
                var compLabel = new Label(c.Label) { tooltip = c.Tooltip };
                compLabel.AddToClassList("vfx-vec-comp-label");
                comp.Add(compLabel);
                var field = BuildControl(c, row);
                AttachLabelDragger(compLabel, field); // scrub via the X/Y mini label
                comp.Add(field);
                controlHost.Add(comp);
            }
            row.Add(controlHost);

            row.Add(BuildBulkTools(comps));
            return row;
        }

        // `labelText`/`spaceFrom` let a single-element struct render through this same
        // row using the parent's label + space while editing the one child leaf `p`.
        VisualElement BuildRow(VfxExposedParam p, string labelText = null, VfxExposedParam spaceFrom = null)
        {
            var row = MakeElement("vfx-row");
            row.userData = p;
            UpdateRowModifiedClass(row, p);
            if (IsFav(FavKeyOf(p))) row.AddToClassList("vfx-row--fav");

            // label column (fixed width): the label text + (optional) space icon hug
            // the left, so the space sits right after the label with a gap before the
            // lock/control — Label · Space  ⟶  Lock · Control.
            var labelCol = MakeElement("vfx-label-col");
            var label = new Label(labelText ?? p.Label) { tooltip = p.Tooltip };
            label.AddToClassList("vfx-plabel");
            AddCopyPasteMenu(label, p); // right-click to copy/paste the value (Inspector-compatible)
            labelCol.Add(label);
            var spaceIcon = BuildSpaceIcon(spaceFrom ?? p);
            if (spaceIcon != null) labelCol.Add(spaceIcon);
            row.Add(labelCol);

            // constrain-proportions lock, in its gutter just before the control
            // (reserved on every row so the control column stays aligned).
            var lockSlot = MakeElement("vfx-row-lock");
            if (IsMultiComponent(p))
                lockSlot.Add(BuildConstrainToggle(p));
            row.Add(lockSlot);

            var control = BuildControl(p, row);
            AttachLabelDragger(label, control); // scrub the value by dragging the label, like a native field
            var controlHost = MakeElement("vfx-pcontrol");
            controlHost.Add(control);
            row.Add(controlHost);

            var tools = MakeElement("vfx-row-tools");
            var reset = MakeIconButton("↺", "Reset to graph default", () =>
            {
                VfxPropertySheet.Reset(_so, p);
                RebuildBodyOnly();
            });
            reset.AddToClassList("vfx-tool-reset"); // visibility + dim driven by CSS (modified state)
            tools.Add(reset);
            var star = MakeIconButton(IsFav(FavKeyOf(p)) ? "★" : "☆", "Pin to favorites", () => ToggleFavorite(p));
            star.AddToClassList("vfx-tool-fav"); // shown on hover or when pinned
            tools.Add(star);
            row.Add(tools);

            return row;
        }

        void UpdateRowModifiedClass(VisualElement row, VfxExposedParam p)
        {
            row.EnableInClassList("vfx-row--modified", VfxPropertySheet.IsOverridden(_so, p));
        }

        // A small chain-link toggle (like the Transform scale lock): when on, editing
        // one component scales the others proportionally.
        VisualElement BuildConstrainToggle(VfxExposedParam p)
        {
            bool on = IsConstrained(p);
            var btn = new Button(() => ToggleConstrain(p))
            {
                tooltip = on ? "Constrain proportions (on)" : "Constrain proportions"
            };
            btn.AddToClassList("vfx-iconbtn");
            btn.AddToClassList("vfx-lock");
            if (on) btn.AddToClassList("vfx-lock--on");

            var tex = EditorGUIUtility.IconContent(on ? "Linked" : "Unlinked").image as Texture2D;
            if (tex != null)
            {
                var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                img.style.width = 16;
                img.style.height = 16;
                btn.Add(img);
            }
            else
            {
                btn.text = on ? "⛓" : "⛓"; // glyph fallback if the icon isn't available
            }
            return btn;
        }

        // Make the property label a drag-scrub zone for numeric controls, matching a
        // native FloatField/IntegerField (whose own label is the drag zone). Slider
        // and vector fields already have their own drag affordances.
        static void AttachLabelDragger(Label label, VisualElement control)
        {
            switch (control)
            {
                case FloatField f:
                    new FieldMouseDragger<float>(f).SetDragZone(label);
                    label.AddToClassList("vfx-plabel--drag");
                    break;
                case IntegerField i:
                    new FieldMouseDragger<int>(i).SetDragZone(label);
                    label.AddToClassList("vfx-plabel--drag");
                    break;
                default:
                    // composite controls that still want label-drag opt in by class (e.g. the
                    // Start Seed wrap = int field + reseed button) — scoped so vector/color
                    // composites keep their own affordances and don't get hijacked.
                    var seedInt = control.Q<IntegerField>(className: "vfx-seed-int");
                    if (seedInt != null)
                    {
                        new FieldMouseDragger<int>(seedInt).SetDragZone(label);
                        label.AddToClassList("vfx-plabel--drag");
                    }
                    break;
            }
        }

        // builds the typed value control; `row` may be null (pinned card). Every
        // control is wired through Bind so edits write to the sheet AND re-sync any
        // other control showing the same property (e.g. pinned card vs category row).
        VisualElement BuildControl(VfxExposedParam p, VisualElement row)
        {
            if (p.IsEnum)
                return Bind(new PopupField<string>(p.EnumValues, 0), p, row,
                    v => p.EnumValues[Mathf.Clamp(v != null ? Convert.ToInt32(v) : 0, 0, p.EnumValues.Count - 1)],
                    s => { int i = Mathf.Max(0, p.EnumValues.IndexOf(s)); return p.SheetType == "m_Uint" ? (object)(uint)i : i; });

            switch (p.SheetType)
            {
                case "m_Float":
                    return p.HasRange
                        ? Bind(new Slider(p.Min, p.Max) { showInputField = true }, p, row, ToFloat, v => v)
                        : Bind(new FloatField(), p, row, ToFloat, v => v);

                case "m_Int":
                    return p.HasRange
                        ? Bind(new SliderInt((int)p.Min, (int)p.Max) { showInputField = true }, p, row, ToInt, v => v)
                        : Bind(new IntegerField(), p, row, ToInt, v => v);

                case "m_Uint":
                    return p.HasRange
                        ? Bind(new SliderInt(Mathf.Max(0, (int)p.Min), (int)p.Max) { showInputField = true },
                               p, row, ToInt, v => (object)(uint)Mathf.Max(0, v))
                        : Bind(new IntegerField(), p, row, ToInt, v => (object)(uint)Mathf.Max(0, v));

                case "m_Bool":
                    return Bind(new Toggle(), p, row, v => v is bool b && b, v => v);

                case "m_Vector2f":
                    return Bind(new Vector2Field(), p, row, v => v is Vector2 x ? x : Vector2.zero, v => v, ConstrainVec2);

                case "m_Vector3f":
                    return Bind(new Vector3Field(), p, row, v => v is Vector3 x ? x : Vector3.zero, v => v, ConstrainVec3);

                case "m_Vector4f":
                    return p.RealType == "Color"
                        ? Bind(new ColorField { hdr = true, showAlpha = true }, p, row,
                               v => v is Color c ? c : (v is Vector4 v4 ? (Color)v4 : Color.white), v => v)
                        : Bind(new Vector4Field(), p, row, v => v is Vector4 x ? x : Vector4.zero, v => v, ConstrainVec4);

                case "m_Gradient":
                    // VFX gradients are linear + HDR (matches the graph's GradientPropertyRM)
                    return Bind(new GradientField { colorSpace = ColorSpace.Linear, hdr = true },
                                p, row, v => v as Gradient ?? new Gradient(), v => v);

                case "m_AnimationCurve":
                    return Bind(new CurveField(), p, row, v => v as AnimationCurve ?? AnimationCurve.Linear(0, 0, 1, 1), v => v);

                case "m_NamedObject":
                    return Bind(new ObjectField { objectType = ResolveObjectType(p.RealType), allowSceneObjects = false },
                                p, row, v => v as Object, v => v);

                default:
                    return new Label($"({p.RealType} — edit in graph)") { tooltip = "Unsupported type in this pass" };
            }
        }

        // Wire a field to a property: seed its value, write edits to the sheet, and
        // register a refresher so all controls for this property stay in sync.
        // `constrain` (if given) proportionally adjusts a multi-component value when
        // the property's "constrain proportions" toggle is on (previous -> next).
        BaseField<T> Bind<T>(BaseField<T> field, VfxExposedParam p, VisualElement row,
                             Func<object, T> toControl, Func<T, object> toModel,
                             Func<T, T, T> constrain = null)
        {
            field.SetValueWithoutNotify(toControl(VfxPropertySheet.GetValue(_so, p)));
            field.showMixedValue = IsMixed(p);
            field.RegisterValueChangedCallback(e =>
            {
                T val = e.newValue;
                if (constrain != null && IsConstrained(p))
                {
                    val = constrain(e.previousValue, e.newValue);
                    field.SetValueWithoutNotify(val);
                }
                SetValueAll(p, toModel(val)); // apply to every edited instance
                RefreshProperty(p);
            });
            RegisterRefresher(p.Name, () =>
            {
                field.SetValueWithoutNotify(toControl(VfxPropertySheet.GetValue(_so, p)));
                field.showMixedValue = IsMixed(p);
                if (row != null) UpdateRowModifiedClass(row, p);
            });
            return field;
        }

        // ---- constrain-proportions (like the Transform scale lock) ----

        bool IsMultiComponent(VfxExposedParam p) =>
            p.SheetType == "m_Vector2f" || p.SheetType == "m_Vector3f" ||
            (p.SheetType == "m_Vector4f" && p.RealType != "Color");

        bool IsConstrained(VfxExposedParam p) => _constrained.Contains(p.Name);

        void ToggleConstrain(VfxExposedParam p)
        {
            if (!_constrained.Remove(p.Name)) _constrained.Add(p.Name);
            _state.SaveConstrained(_constrained);
            RebuildBodyOnly();
        }

        static Vector2 ConstrainVec2(Vector2 a, Vector2 b)
        {
            var r = ConstrainComponents(new[] { a.x, a.y }, new[] { b.x, b.y });
            return new Vector2(r[0], r[1]);
        }

        static Vector3 ConstrainVec3(Vector3 a, Vector3 b)
        {
            var r = ConstrainComponents(new[] { a.x, a.y, a.z }, new[] { b.x, b.y, b.z });
            return new Vector3(r[0], r[1], r[2]);
        }

        static Vector4 ConstrainVec4(Vector4 a, Vector4 b)
        {
            var r = ConstrainComponents(new[] { a.x, a.y, a.z, a.w }, new[] { b.x, b.y, b.z, b.w });
            return new Vector4(r[0], r[1], r[2], r[3]);
        }

        // Scale all components by the ratio of the one the user changed (Unity's
        // constrained-proportions behavior). If the edited component was 0 (ratio
        // undefined), make every component equal to the new value.
        static float[] ConstrainComponents(float[] prev, float[] next)
        {
            int changed = -1;
            for (int i = 0; i < prev.Length; i++)
                if (!Mathf.Approximately(prev[i], next[i])) { changed = i; break; }
            if (changed < 0) return next;

            var result = (float[])next.Clone();
            if (Mathf.Approximately(prev[changed], 0f))
            {
                for (int i = 0; i < result.Length; i++) result[i] = next[changed];
            }
            else
            {
                float ratio = next[changed] / prev[changed];
                for (int i = 0; i < result.Length; i++)
                    // keep the edited component as typed; round the derived ones to 2
                    // decimals so the fields don't widen with long float tails.
                    result[i] = (i == changed) ? next[changed] : Mathf.Round(prev[i] * ratio * 100f) / 100f;
            }
            return result;
        }

        // ---- copy / paste (interops with the Inspector via UnityEditor.Clipboard) ----

        static bool IsCopyPasteSupported(VfxExposedParam p)
        {
            switch (p.SheetType)
            {
                case "m_Float":
                case "m_Vector2f":
                case "m_Vector3f":
                case "m_Vector4f":
                case "m_Gradient": return true;
                default: return false;
            }
        }

        void AddCopyPasteMenu(VisualElement target, VfxExposedParam p)
        {
            if (!IsCopyPasteSupported(p)) return;
            target.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Copy", _ => CopyValue(p));
                evt.menu.AppendAction("Paste", _ => PasteValue(p),
                    CanPaste(p) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            }));
        }

        void CopyValue(VfxExposedParam p)
        {
            object val = VfxPropertySheet.GetValue(_so, p);
            switch (p.SheetType)
            {
                case "m_Float": VfxClipboard.Set("floatValue", ToFloat(val)); break;
                case "m_Vector2f": VfxClipboard.Set("vector2Value", val is Vector2 v2 ? v2 : Vector2.zero); break;
                case "m_Vector3f": VfxClipboard.Set("vector3Value", val is Vector3 v3 ? v3 : Vector3.zero); break;
                case "m_Vector4f":
                    if (p.RealType == "Color")
                        VfxClipboard.Set("colorValue", val is Color c ? c : (val is Vector4 v4c ? (Color)v4c : Color.white));
                    else
                        VfxClipboard.Set("vector4Value", val is Vector4 v4 ? v4 : Vector4.zero);
                    break;
                case "m_Gradient": VfxClipboard.Set("gradientValue", val as Gradient ?? new Gradient()); break;
            }
        }

        bool CanPaste(VfxExposedParam p)
        {
            switch (p.SheetType)
            {
                case "m_Float": return VfxClipboard.Has("hasFloat");
                case "m_Vector2f": return VfxClipboard.Has("hasVector2");
                case "m_Vector3f": return VfxClipboard.Has("hasVector3");
                case "m_Vector4f": return VfxClipboard.Has(p.RealType == "Color" ? "hasColor" : "hasVector4");
                case "m_Gradient": return VfxClipboard.Has("hasGradient");
                default: return false;
            }
        }

        void PasteValue(VfxExposedParam p)
        {
            switch (p.SheetType)
            {
                case "m_Float": if (VfxClipboard.Has("hasFloat")) SetValueAll(p, (float)VfxClipboard.Get("floatValue")); break;
                case "m_Vector2f": if (VfxClipboard.Has("hasVector2")) SetValueAll(p, (Vector2)VfxClipboard.Get("vector2Value")); break;
                case "m_Vector3f": if (VfxClipboard.Has("hasVector3")) SetValueAll(p, (Vector3)VfxClipboard.Get("vector3Value")); break;
                case "m_Vector4f":
                    if (p.RealType == "Color") { if (VfxClipboard.Has("hasColor")) SetValueAll(p, (Color)VfxClipboard.Get("colorValue")); }
                    else { if (VfxClipboard.Has("hasVector4")) SetValueAll(p, (Vector4)VfxClipboard.Get("vector4Value")); }
                    break;
                case "m_Gradient": if (VfxClipboard.Has("hasGradient")) SetValueAll(p, (Gradient)VfxClipboard.Get("gradientValue")); break;
            }
            RefreshProperty(p);
        }

        void RegisterRefresher(string name, Action refresh)
        {
            if (!_refreshers.TryGetValue(name, out var list))
                _refreshers[name] = list = new List<Action>();
            list.Add(refresh);
        }

        // ---- multi-instance writes (apply to every edited instance) ----

        void SetValueAll(VfxExposedParam p, object value)
        {
            foreach (var so in _sos) VfxPropertySheet.SetValue(so, p, value);
        }

        void ResetAll(VfxExposedParam p)
        {
            foreach (var so in _sos) VfxPropertySheet.Reset(so, p);
        }

        void UpdateAllSos()
        {
            foreach (var so in _sos) so.Update();
        }

        // True when the instances hold different values for this property (→ show mixed).
        bool IsMixed(VfxExposedParam p)
        {
            if (_sos.Count < 2) return false;
            object first = VfxPropertySheet.GetValue(_sos[0], p);
            for (int i = 1; i < _sos.Count; i++)
                if (!Equals(first, VfxPropertySheet.GetValue(_sos[i], p))) return true;
            return false;
        }

        // Re-sync every control bound to this property (and the footer) after an edit.
        void RefreshProperty(VfxExposedParam p)
        {
            if (_refreshers.TryGetValue(p.Name, out var list))
                foreach (var refresh in list) refresh();
            // re-aggregate struct headers (bold + reset-all visibility) for live updates
            foreach (var (header, leaves) in _structHeaders)
                header.EnableInClassList("vfx-row--modified", leaves.Any(c => VfxPropertySheet.IsOverridden(_so, c)));
            UpdateFooter();
        }

        // ------------------------------------------------------------------ footer

        VisualElement BuildFooter()
        {
            var footer = MakeElement("vfx-footer");
            _footNote = new Label();
            _footNote.AddToClassList("vfx-foot-note");
            footer.Add(_footNote);

            _resetAllBtn = new Button(ResetActiveTab) { text = "Reset tab" };
            _resetAllBtn.tooltip = "Reset every modified setting on the active tab to its default.";
            _resetAllBtn.AddToClassList("vfx-btn");
            footer.Add(_resetAllBtn);

            var preset = new Button { text = "Save preset" };
            preset.AddToClassList("vfx-btn");
            preset.SetEnabled(false);
            preset.tooltip = "Presets — coming in a later pass.";
            footer.Add(preset);

            UpdateFooter();
            return footer;
        }

        // The play/scrub timeline window default (the value a freshly-set-up tool uses).
        const float kDefaultDuration = 10f;
        bool PlaybackModified() => PlaybackChipCounts().mod > 0;

        // Modified count for the active tab — drives the footer note + Reset button enabled state.
        int ActiveTabModifiedCount()
        {
            switch (_tab)
            {
                case "props": return VfxPropertySheet.CountModified(_so, _params);
                case "render": return RendererChipCounts().mod;
                case "play": return PlaybackChipCounts().mod;
                case "all": return VfxPropertySheet.CountModified(_so, _params) + RendererChipCounts().mod + PlaybackChipCounts().mod;
                default: return 0; // debug has no resettable settings yet
            }
        }

        // Reset only the active tab's modified settings (All resets every source).
        void ResetActiveTab()
        {
            switch (_tab)
            {
                case "props": ResetAllProperties(); break;
                case "render": ResetRendererToDefaults(); break;
                case "play": ResetPlayback(); break;
                case "all": ResetAllProperties(); ResetRendererToDefaults(); ResetPlayback(); break;
            }
            RebuildBodyOnly();
        }

        void ResetAllProperties()
        {
            foreach (var p in _params)
                if (VfxPropertySheet.IsOverridden(_so, p))
                    ResetAll(p);
        }

        void ResetPlayback()
        {
            foreach (var f in BuildPlaybackFields())
                if (f.IsModified()) f.Reset();
        }

        void UpdateFooter()
        {
            if (_footNote == null || _so == null) return;
            int mod = ActiveTabModifiedCount();
            uint seed = _effect != null ? _effect.startSeed : 0;
            _footNote.text = (mod > 0 ? $"{mod} edited" : "No overrides") + $" · seed {seed}";
            _resetAllBtn?.SetEnabled(mod > 0);
        }

        // ------------------------------------------------------------------ helpers

        // Repopulate tabs/chips/rail/body for the current state, keeping the chrome (and the
        // focused search field) intact. Used by chips, rail, favorites, reset, collapse, etc.
        void RebuildBodyOnly() => PopulateActiveTab();

        // Favorites are namespaced so properties, renderer fields, and meta can coexist in
        // one set: "prop:<name>", "renderer:<m_Field>", "meta:<id>", "play:<id>".
        static string FavKeyOf(VfxExposedParam p) => "prop:" + p.Name;
        bool IsFav(string key) => _favorites.Contains(key);

        void ToggleFav(string key)
        {
            if (!_favorites.Remove(key)) _favorites.Add(key);
            _state.SaveFavorites(_favorites);
            RebuildBodyOnly();
        }

        void ToggleFavorite(VfxExposedParam p) => ToggleFav(FavKeyOf(p));

        // One-time upgrade of pre-Phase-2 favorites (bare property names) to the "prop:" namespace.
        void MigrateFavorites()
        {
            bool changed = false;
            var migrated = new HashSet<string>();
            foreach (var k in _favorites)
            {
                if (k.StartsWith("prop:") || k.StartsWith("renderer:") || k.StartsWith("meta:") || k.StartsWith("play:"))
                    migrated.Add(k);
                else { migrated.Add("prop:" + k); changed = true; }
            }
            if (changed) { _favorites = migrated; _state.SaveFavorites(_favorites); }
        }

        // ~30fps clock: advances the scrub bar in real time while playing. At the end of the
        // window it loops (reset the sim) or — if Loop is off — stops on the last frame.
        void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Min((float)(now - _lastTick), 0.1f); // clamp so idle gaps don't jump
            _lastTick = now;

            if (_effect != null && !_effect.pause && _duration > 0f)
            {
                float rate = _effect.playRate <= 0f ? 1f : _effect.playRate;
                _scrubT += dt * rate / _duration;
                if (_scrubT >= 1f)
                {
                    if (_loop) { _scrubT = 0f; _effect.Reinit(); } // restart at the end of the window
                    else { _scrubT = 1f; _effect.pause = true; }   // hold on the last frame
                }
            }

            UpdateLive();
        }

        void UpdateLive()
        {
            if (_effect == null) return;
            if (_liveLabel != null) _liveLabel.text = $"{_effect.aliveParticleCount:N0} live";
            if (_timeLabel != null) _timeLabel.text = $"{_scrubT * _duration:0.00} / {_duration:0.##}s";
            // keep the progress fill in sync with the scrub position (covers restart/step
            // while paused, when Tick isn't advancing)
            if (_miniFill != null) _miniFill.style.width = Length.Percent(_scrubT * 100f);
            // keep the play/pause icon in sync with the actual pause state
            if (_playIcon != null)
            {
                _playIcon.image = EditorGUIUtility.IconContent(_effect.pause ? "PlayButton" : "PauseButton").image;
                _playBtn.tooltip = _effect.pause ? "Play" : "Pause";
            }
            // keep the Play Rate slider reflecting external changes (undo, multi-select); only
            // correct when out of sync so an in-progress drag isn't disturbed.
            if (_rateSlider != null && !Mathf.Approximately(_rateSlider.value, _effect.playRate))
                _rateSlider.SetValueWithoutNotify(_effect.playRate);
        }

        void BuildPlaceholder(VisualElement body, string msg)
        {
            var ph = new Label(msg);
            ph.AddToClassList("vfx-placeholder");
            body.Add(ph);
        }

        static VisualElement MakeElement(string cls)
        {
            var ve = new VisualElement();
            ve.AddToClassList(cls);
            return ve;
        }

        static Button MakeIconButton(string glyph, string tooltip, Action onClick)
        {
            var b = new Button(onClick) { text = glyph, tooltip = tooltip };
            b.AddToClassList("vfx-iconbtn");
            return b;
        }

        static float ToFloat(object o) => o == null ? 0f : Convert.ToSingle(o);
        static int ToInt(object o) => o == null ? 0 : (int)Convert.ToInt64(o);

        static Type ResolveObjectType(string realType)
        {
            switch (realType)
            {
                case "Texture": return typeof(Texture);
                case "Texture2D": return typeof(Texture2D);
                case "Texture2DArray": return typeof(Texture2DArray);
                case "Texture3D": return typeof(Texture3D);
                case "Cubemap": return typeof(Cubemap);
                case "CubemapArray": return typeof(CubemapArray);
                case "Mesh": return typeof(Mesh);
                default:
                    var t = typeof(Texture).Assembly.GetType("UnityEngine." + realType);
                    return t ?? typeof(Object);
            }
        }

        // Category accent dots — a small custom palette (handoff): desaturated to sit
        // calmly against the gray UI. Conventional category names get a themed color;
        // everything else is assigned a distinct palette color by order of appearance.
        static readonly (string key, Color color)[] s_CatPalette =
        {
            ("spawn",   Hex("#c98a3a")),
            ("color",   Hex("#c95a4a")),
            ("light",   Hex("#c95a4a")),
            ("motion",  Hex("#4a8ac9")),
            ("shape",   Hex("#4a8ac9")),
            ("size",    Hex("#7a9a4a")),
            ("life",    Hex("#7a9a4a")),
            ("texture", Hex("#8a6ac9")),
            ("render",  Hex("#8a6ac9")),
        };

        static readonly Color[] s_Fallback =
        {
            Hex("#c98a3a"), Hex("#c95a4a"), Hex("#4a8ac9"), Hex("#7a9a4a"),
            Hex("#8a6ac9"), Hex("#4aa39a"), Hex("#c08ac9"), Hex("#9a9a4a"),
        };

        // Assign each category a color once per build, in graph order, so unrecognized
        // names cycle through distinct palette colors instead of colliding via a hash.
        void BuildCategoryColorMap()
        {
            _categoryColors.Clear();
            int fallback = 0;
            foreach (var p in _params)
            {
                string cat = string.IsNullOrEmpty(p.Category) ? "Uncategorized" : p.Category;
                if (_categoryColors.ContainsKey(cat)) continue;

                string lc = cat.ToLowerInvariant();
                Color color = default;
                bool keyed = false;
                foreach (var (key, c) in s_CatPalette)
                    if (lc.Contains(key)) { color = c; keyed = true; break; }
                if (!keyed) color = s_Fallback[fallback++ % s_Fallback.Length];

                _categoryColors[cat] = color;
            }
        }

        Color GetCategoryColor(string category)
        {
            if (_categoryColors.Count == 0) BuildCategoryColorMap();
            return _categoryColors.TryGetValue(category, out var c) ? c : s_Fallback[0];
        }

        static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        // ---- scene-view edit gizmo (custom Handles) ----

        // Shape gizmos keyed by C# struct name (realType). They also appear nested (e.g.
        // the inner Cone of an ArcCone), where the sub-struct carries no space; we gate
        // them on p.Spaceable below so only the top-level one — whose frame is known —
        // offers the gizmo. See [[vfx-cone-arccone-layout]].
        static readonly HashSet<string> s_ShapeGizmoTypes = new()
        {
            "TCone", "TArcCone", "TSphere", "TArcSphere",
            "TCircle", "TArcCircle", "TTorus", "TArcTorus",
            // Transform/OrientedBox MUST stay spaceable-gated: Transform also appears as
            // the nested `transform` of every shape (no space there), so the gate keeps
            // the button on the top-level exposed one only.
            "OrientedBox", "Transform",
        };

        static bool IsGizmoSupported(VfxExposedParam p) =>
            p.RealType is "Position" or "DirectionType" or "Vector" or "AABox" or "Line" or "Plane" ||
            (s_ShapeGizmoTypes.Contains(p.RealType) && p.Spaceable);

        VisualElement BuildGizmoButton(VfxExposedParam structParam, bool inline = false)
        {
            bool on = _gizmoStruct != null && _gizmoStruct.Name == structParam.Name;
            var btn = new Button(() => ToggleGizmo(structParam))
            {
                tooltip = on ? "Stop editing in Scene view" : "Edit in Scene view"
            };
            btn.AddToClassList("vfx-gizmo-btn");
            if (inline) btn.AddToClassList("vfx-gizmo-btn--inline"); // in flow (struct header) vs left gutter
            if (on) btn.AddToClassList("vfx-gizmo-btn--on");
            btn.RegisterCallback<ClickEvent>(e => e.StopPropagation()); // don't toggle the struct's collapse

            var tex = EditorGUIUtility.IconContent("EditCollider").image as Texture2D;
            if (tex != null)
            {
                var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                img.style.width = 16; // native size → crisp
                img.style.height = 16;
                btn.Add(img);
            }
            else btn.text = "⛶";
            return btn;
        }

        void ToggleGizmo(VfxExposedParam structParam)
        {
            bool turningOff = _gizmoStruct != null && _gizmoStruct.Name == structParam.Name;

            // restore the fold state of the previously-active gizmo (it was auto-unfolded)
            if (_gizmoStruct != null && _gizmoWasCollapsed)
            {
                _collapsed.Add(StructKey(_gizmoStruct));
                _state.SaveCollapsed(_collapsed);
            }

            if (turningOff)
            {
                _gizmoStruct = null;
            }
            else
            {
                _gizmoStruct = structParam;
                _gizmoType = structParam.RealType;
                _gizmoSpace = structParam.Space;
                _gizmoRotation = Quaternion.identity; // realigned to the value on first draw
                // remember the current fold state, then unfold so the numeric field shows
                _gizmoWasCollapsed = _collapsed.Contains(StructKey(structParam));
                _collapsed.Remove(StructKey(structParam));
                _state.SaveCollapsed(_collapsed);
            }
            SceneView.RepaintAll();
            RebuildBodyOnly(); // refresh the button's active state
        }

        Vector3 GizmoVec(VfxExposedParam leaf) =>
            VfxPropertySheet.GetValue(_so, leaf) is Vector3 v ? v : Vector3.zero;

        GUIStyle _gizmoLabelStyle;
        Texture2D _gizmoLabelBg;

        // A rounded-rect texture with a 1px feathered edge, for a 9-sliced label background.
        static Texture2D MakeRoundedTexture(int size, int radius, Color fill)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // distance into the nearest corner (clamp to the inner rect first)
                    float px0 = x + 0.5f, py0 = y + 0.5f;
                    float cx = Mathf.Clamp(px0, radius, size - radius);
                    float cy = Mathf.Clamp(py0, radius, size - radius);
                    float dist = Mathf.Sqrt((px0 - cx) * (px0 - cx) + (py0 - cy) * (py0 - cy));
                    float a = Mathf.Clamp01(radius - dist + 0.5f); // 1px feather at the rounded edge
                    var c = fill;
                    c.a *= a;
                    px[y * size + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        // axis-colored components (X=red, Y=green, Z=blue) for rich-text scene labels
        static string FmtAxis(Vector3 v)
        {
            string x = ColorUtility.ToHtmlStringRGB(Handles.xAxisColor);
            string y = ColorUtility.ToHtmlStringRGB(Handles.yAxisColor);
            string z = ColorUtility.ToHtmlStringRGB(Handles.zAxisColor);
            return $"(<color=#{x}>{v.x:0.##}</color>, <color=#{y}>{v.y:0.##}</color>, <color=#{z}>{v.z:0.##}</color>)";
        }

        // Draw a readable text label at the top-right of the gizmo's screen-space box
        // (a 2D box of `worldRadius` around `worldCenter`, ≈ the rotation gizmo size).
        void GizmoLabel(Vector3 worldCenter, float worldRadius, string text)
        {
            if (Event.current.type != EventType.Repaint) return;
            const int radius = 6;
            if (_gizmoLabelBg == null)
                _gizmoLabelBg = MakeRoundedTexture(16, radius, new Color(0.1f, 0.1f, 0.1f, 0.4f));
            // fresh style (not a copy of helpBox) so richText reliably applies
            _gizmoLabelStyle ??= new GUIStyle
            {
                fontSize = 11,
                richText = true, // axis-colored components via <color> tags
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(6, 6, 4, 4),
                border = new RectOffset(radius, radius, radius, radius), // 9-slice keeps the corners
                normal = { textColor = Color.white, background = _gizmoLabelBg },
            };

            Handles.BeginGUI();
            Camera cam = Camera.current;
            Vector2 center = HandleUtility.WorldToGUIPoint(worldCenter);
            Vector2 edge = HandleUtility.WorldToGUIPoint(worldCenter + (cam != null ? cam.transform.right : Vector3.right) * worldRadius);
            float r = Mathf.Max(8f, Vector2.Distance(center, edge)); // gizmo's screen radius
            var content = new GUIContent(text);
            Vector2 sz = _gizmoLabelStyle.CalcSize(content);
            GUI.Label(new Rect(center.x + r, center.y - r, sz.x, sz.y), content, _gizmoLabelStyle);
            Handles.EndGUI();
        }

        // Keep the persistent handle rotation's forward aligned to the current direction
        // by the minimal rotation (preserves roll, stays continuous — unlike LookRotation,
        // whose up-vector flips and makes the direction jump).
        void AlignGizmoRotation(Vector3 worldDir)
        {
            if (worldDir.sqrMagnitude < 1e-6f) return;
            Vector3 cur = _gizmoRotation * Vector3.forward;
            if (Vector3.Dot(cur.normalized, worldDir.normalized) < 0.99999f)
                _gizmoRotation = Quaternion.FromToRotation(cur, worldDir) * _gizmoRotation;
        }

        // LookRotation that won't degenerate when the forward axis is parallel to up.
        static Quaternion SafeLook(Vector3 forward)
        {
            if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;
            Vector3 up = Mathf.Abs(Vector3.Dot(forward.normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(forward, up);
        }

        // Find the child leaf of the active gizmo struct whose label contains `key`.
        VfxExposedParam GizmoLeaf(List<VfxExposedParam> leaves, string key) =>
            leaves.FirstOrDefault(l => l.Label != null && l.Label.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);

        void OnSceneGui(SceneView sv)
        {
            if (_gizmoStruct == null || _effect == null || _so == null) return;
            if (!_structLeaves.TryGetValue(_gizmoStruct, out var leaves) || leaves.Count == 0) return;

            var t = _effect.transform;
            bool local = _gizmoSpace == "Local";

            if (_gizmoType == "Position")
            {
                var leaf = leaves[0];
                Vector3 v = GizmoVec(leaf);
                Vector3 world = local ? t.TransformPoint(v) : v;
                EditorGUI.BeginChangeCheck();
                Vector3 nw = Handles.PositionHandle(world, local ? t.rotation : Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                    CommitGizmo(leaf, local ? t.InverseTransformPoint(nw) : nw);
                GizmoLabel(world, HandleUtility.GetHandleSize(world), $"<b>{_gizmoStruct.Label}</b>  {FmtAxis(v)}");
            }
            else if (_gizmoType == "DirectionType")
            {
                var leaf = leaves[0];
                Vector3 v = GizmoVec(leaf);
                Vector3 worldDir = local ? t.TransformDirection(v) : v;
                if (worldDir.sqrMagnitude < 1e-6f) worldDir = Vector3.up;
                worldDir.Normalize();

                Vector3 anchor = t.position;
                float size = HandleUtility.GetHandleSize(anchor);

                // arrow for context — only draw cosmetics on Repaint (drawing caps on
                // other events corrupts GL state and causes pixel-block artifacts)
                if (Event.current.type == EventType.Repaint)
                {
                    Handles.color = Color.yellow;
                    Vector3 tip = anchor + worldDir * size * 1.5f;
                    Handles.DrawLine(anchor, tip);
                    Handles.ConeHandleCap(0, tip, SafeLook(worldDir), size * 0.18f, EventType.Repaint);
                }

                // standard rotation gizmo (supports rotation snapping with Ctrl/Cmd).
                // Use a persistent rotation realigned to the direction so it never flips.
                AlignGizmoRotation(worldDir);
                EditorGUI.BeginChangeCheck();
                Quaternion nq = Handles.RotationHandle(_gizmoRotation, anchor);
                if (EditorGUI.EndChangeCheck())
                {
                    _gizmoRotation = nq;
                    Vector3 nd = (nq * Vector3.forward).normalized;
                    CommitGizmo(leaf, local ? t.InverseTransformDirection(nd).normalized : nd);
                }
                GizmoLabel(anchor, size, $"<b>{_gizmoStruct.Label}</b>  {FmtAxis(v)}");
            }
            else if (_gizmoType == "Vector")
            {
                // direction via the standard rotation gizmo, magnitude via a scale handle
                var leaf = leaves[0];
                Vector3 v = GizmoVec(leaf);
                Vector3 worldVec = local ? t.TransformDirection(v) : v; // rotation preserves magnitude
                float mag = worldVec.magnitude; // actual value magnitude (not clamped)
                Vector3 dir = worldVec.sqrMagnitude > 1e-6f ? worldVec.normalized : Vector3.forward;
                Vector3 anchor = t.position;
                float hsize = HandleUtility.GetHandleSize(anchor);

                // direction via the rotation gizmo (persistent rotation)
                AlignGizmoRotation(dir);
                EditorGUI.BeginChangeCheck();
                Quaternion nq = Handles.RotationHandle(_gizmoRotation, anchor);
                bool rotChanged = EditorGUI.EndChangeCheck();
                Vector3 newDir = dir;
                if (rotChanged) { _gizmoRotation = nq; newDir = (nq * Vector3.forward).normalized; }

                // magnitude via a uniform-scale cube at the origin (like the Scale tool's
                // centre box). The value itself is NOT clamped.
                EditorGUI.BeginChangeCheck();
                float newMag = Handles.ScaleValueHandle(mag, anchor, SafeLook(newDir), hsize,
                    Handles.CubeHandleCap, EditorSnapSettings.scale);
                bool magChanged = EditorGUI.EndChangeCheck();
                newMag = Mathf.Max(0f, newMag);

                // arrow with a cone tip — only the drawn LENGTH is clamped to 1..10 so the
                // arrow stays a sensible on-screen size regardless of the actual magnitude.
                float visLen = Mathf.Clamp(newMag, 1f, 10f);
                Vector3 tip = anchor + newDir * visLen;
                if (Event.current.type == EventType.Repaint)
                {
                    Handles.color = Color.cyan;
                    Handles.DrawLine(anchor, tip);
                    Handles.ConeHandleCap(0, tip, SafeLook(newDir), hsize * 0.18f, EventType.Repaint);
                }

                if (rotChanged || magChanged)
                {
                    Vector3 nwv = newDir * newMag;
                    CommitGizmo(leaf, local ? t.InverseTransformDirection(nwv) : nwv);
                }
                GizmoLabel(anchor, hsize, $"<b>{_gizmoStruct.Label}</b>\ndir {FmtAxis(newDir)}\nscale {newMag:0.##}");
            }
            else if (_gizmoType == "AABox")
            {
                var centerLeaf = GizmoLeaf(leaves, "center") ?? leaves[0];
                var sizeLeaf = GizmoLeaf(leaves, "size") ?? (leaves.Count > 1 ? leaves[1] : null);
                if (sizeLeaf == null) return;

                _boxHandle ??= new BoxBoundsHandle { midpointHandleDrawFunction = DrawAxisHandle };
                _boxHandle.center = GizmoVec(centerLeaf);
                _boxHandle.size = GizmoVec(sizeLeaf);

                // draw in the property's space (local → component transform; world → identity)
                using (new Handles.DrawingScope(local ? t.localToWorldMatrix : Matrix4x4.identity))
                {
                    // resize via the axis-colored face handles
                    EditorGUI.BeginChangeCheck();
                    _boxHandle.DrawHandle();
                    if (EditorGUI.EndChangeCheck())
                    {
                        CommitGizmo(centerLeaf, _boxHandle.center);
                        CommitGizmo(sizeLeaf, _boxHandle.size);
                    }

                    // move the center directly with the standard (axis-colored) position handle
                    EditorGUI.BeginChangeCheck();
                    Vector3 nc = Handles.PositionHandle(_boxHandle.center, Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                        CommitGizmo(centerLeaf, nc);
                }
                // label outside the box's matrix scope, anchored to the box center on screen
                Vector3 boxWorld = local ? t.TransformPoint(_boxHandle.center) : _boxHandle.center;
                GizmoLabel(boxWorld, HandleUtility.GetHandleSize(boxWorld),
                    $"<b>{_gizmoStruct.Label}</b>\ncenter {FmtAxis(_boxHandle.center)}\nsize {FmtAxis(_boxHandle.size)}");
            }
            else if (_gizmoType == "TCone" || _gizmoType == "TArcCone")
            {
                DrawConeGizmo(leaves, t, local);
            }
            else if (_gizmoType == "TSphere" || _gizmoType == "TArcSphere")
            {
                DrawSphereGizmo(leaves, t, local);
            }
            else if (_gizmoType == "TCircle" || _gizmoType == "TArcCircle")
            {
                DrawCircleGizmo(leaves, t, local);
            }
            else if (_gizmoType == "TTorus" || _gizmoType == "TArcTorus")
            {
                DrawTorusGizmo(leaves, t, local);
            }
            else if (_gizmoType == "Line")
            {
                DrawLineGizmo(leaves, t, local);
            }
            else if (_gizmoType == "OrientedBox" || _gizmoType == "Transform")
            {
                DrawBoxGizmo(leaves, t, local);
            }
            else if (_gizmoType == "Plane")
            {
                DrawPlaneGizmo(leaves, t, local);
            }
        }

        // Mirrors the VFX package's VFXPlaneGizmo (internal): a position-spaceable point
        // plus a direction-spaceable normal, shown as a square quad in the plane + a normal
        // arrow. Tool-gated like the other gizmos — Move shows the position handle, Rotate
        // shows the normal rotation gizmo (persistent `_gizmoRotation`, like DirectionType,
        // so the normal never pole-flips). VFX draws a fixed huge quad; we make it
        // handle-size-relative so it stays a sensible on-screen size (VFX even notes this).
        void DrawPlaneGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
        {
            var posLeaf = GizmoLeaf(leaves, "position");
            var normLeaf = GizmoLeaf(leaves, "normal");
            if (posLeaf == null || normLeaf == null) return;

            Vector3 p = GizmoVec(posLeaf);
            Vector3 n = GizmoVec(normLeaf);

            Vector3 worldPos = local ? t.TransformPoint(p) : p;
            Vector3 worldNormal = local ? t.TransformDirection(n) : n;
            if (worldNormal.sqrMagnitude < 1e-6f) worldNormal = Vector3.up;
            worldNormal.Normalize();
            float size = HandleUtility.GetHandleSize(worldPos);

            // square quad in the plane + normal arrow (cosmetic → Repaint only, else GL
            // state corrupts and bleeds pixel-block artifacts)
            if (Event.current.type == EventType.Repaint)
                using (new Handles.DrawingScope(Matrix4x4.TRS(worldPos, Quaternion.FromToRotation(Vector3.forward, worldNormal), Vector3.one)))
                {
                    float h = 2.5f * size;
                    Handles.color = new Color(0.5f, 0.8f, 1f);
                    Handles.DrawAAPolyLine(new Vector3(h, h, 0), new Vector3(h, -h, 0),
                        new Vector3(-h, -h, 0), new Vector3(-h, h, 0), new Vector3(h, h, 0));
                    Handles.color = Color.yellow;
                    Handles.ArrowHandleCap(0, Vector3.zero, Quaternion.identity, size, EventType.Repaint);
                }

            if (Tools.current == Tool.Rotate)
            {
                // normal via the persistent rotation gizmo (avoids pole flips), like DirectionType
                AlignGizmoRotation(worldNormal);
                EditorGUI.BeginChangeCheck();
                Quaternion nrot = Handles.RotationHandle(_gizmoRotation, worldPos);
                if (EditorGUI.EndChangeCheck())
                {
                    _gizmoRotation = nrot;
                    Vector3 nd = (nrot * Vector3.forward).normalized;
                    CommitGizmo(normLeaf, local ? t.InverseTransformDirection(nd).normalized : nd);
                }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                Vector3 np = Handles.PositionHandle(worldPos, local ? t.rotation : Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                    CommitGizmo(posLeaf, local ? t.InverseTransformPoint(np) : np);
            }

            GizmoLabel(worldPos, size, $"<b>{_gizmoStruct.Label}</b>\nnormal {FmtAxis(n)}");
        }

        // OrientedBox and Transform are the same shape — a center/position, euler angles,
        // and a size/scale — so they share one gizmo: a wire cube in the oriented frame
        // (more legible than bare transform handles, per the VFX VFXOrientedBoxGizmo idea)
        // plus the tool-aware move/rotate/scale handle. The leaf names differ
        // (center/size vs position/scale), matched with fallbacks.
        void DrawBoxGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
        {
            var ctrLeaf  = GizmoLeaf(leaves, "center") ?? GizmoLeaf(leaves, "position");
            var angLeaf  = GizmoLeaf(leaves, "angle");
            var sizeLeaf = GizmoLeaf(leaves, "size") ?? GizmoLeaf(leaves, "scale");

            Vector3 center = ctrLeaf  != null ? GizmoVec(ctrLeaf)  : Vector3.zero;
            Vector3 angles = angLeaf  != null ? GizmoVec(angLeaf)  : Vector3.zero;
            Vector3 size   = sizeLeaf != null ? GizmoVec(sizeLeaf) : Vector3.one;

            Matrix4x4 baseMatrix = local ? t.localToWorldMatrix : Matrix4x4.identity;
            Quaternion rot = Quaternion.Euler(angles);

            // wire cube in the box's own oriented frame (cosmetic → Repaint only, else GL
            // state corrupts and bleeds pixel-block artifacts)
            if (Event.current.type == EventType.Repaint)
                using (new Handles.DrawingScope(baseMatrix * Matrix4x4.TRS(center, rot, Vector3.one)))
                {
                    Handles.color = new Color(0.5f, 0.8f, 1f);
                    Handles.DrawWireCube(Vector3.zero, size);
                }

            // size/scale leaf drives the ScaleHandle branch of the shared transform handle
            DrawSpaceTransformHandle(baseMatrix, center, rot, size, ctrLeaf, angLeaf, sizeLeaf);

            Vector3 wc = baseMatrix.MultiplyPoint(center);
            GizmoLabel(wc, HandleUtility.GetHandleSize(wc), $"<b>{_gizmoStruct.Label}</b>\nsize {FmtAxis(size)}");
        }

        // Mirrors the VFX package's VFXLineGizmo (internal): two position-spaceable
        // endpoints joined by a line, each with its own PositionHandle. No transform/TRS
        // frame — both points live directly in the param's space (component transform for
        // Local, identity for World), like the Position gizmo above.
        void DrawLineGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
        {
            var startLeaf = GizmoLeaf(leaves, "start");
            var endLeaf = GizmoLeaf(leaves, "end");
            if (startLeaf == null || endLeaf == null) return;

            Vector3 s = GizmoVec(startLeaf);
            Vector3 e = GizmoVec(endLeaf);
            Vector3 ws = local ? t.TransformPoint(s) : s;
            Vector3 we = local ? t.TransformPoint(e) : e;
            Quaternion handleRot = local ? t.rotation : Quaternion.identity;

            // connecting line — cosmetic, so guard on Repaint (drawing on other events
            // corrupts GL state and bleeds pixel-block artifacts)
            if (Event.current.type == EventType.Repaint)
            {
                Handles.color = Color.yellow;
                Handles.DrawLine(ws, we);
            }

            EditorGUI.BeginChangeCheck();
            Vector3 nws = Handles.PositionHandle(ws, handleRot);
            if (EditorGUI.EndChangeCheck())
                CommitGizmo(startLeaf, local ? t.InverseTransformPoint(nws) : nws);

            EditorGUI.BeginChangeCheck();
            Vector3 nwe = Handles.PositionHandle(we, handleRot);
            if (EditorGUI.EndChangeCheck())
                CommitGizmo(endLeaf, local ? t.InverseTransformPoint(nwe) : nwe);

            GizmoLabel(ws, HandleUtility.GetHandleSize(ws), $"<b>{_gizmoStruct.Label}</b>  start {FmtAxis(s)}");
            GizmoLabel(we, HandleUtility.GetHandleSize(we), $"<b>{_gizmoStruct.Label}</b>  end {FmtAxis(e)}");
        }

        // The cone/sphere shapes share a transform frame: their move/rotate/scale handle
        // runs in the base frame (component transform for Local, identity for World — like
        // VFXSpaceableGizmo's Handles.matrix), respecting the active tool, exactly as VFX's
        // TransformGizmo does (drawn outside the shape's own matrix).
        void DrawSpaceTransformHandle(Matrix4x4 baseMatrix, Vector3 pos, Quaternion rot, Vector3 scale,
            VfxExposedParam posLeaf, VfxExposedParam angLeaf, VfxExposedParam sclLeaf)
        {
            using (new Handles.DrawingScope(baseMatrix))
            {
                if (Tools.current == Tool.Rotate && angLeaf != null)
                {
                    EditorGUI.BeginChangeCheck();
                    Quaternion nr = Handles.RotationHandle(rot, pos);
                    if (EditorGUI.EndChangeCheck()) CommitGizmo(angLeaf, nr.eulerAngles);
                }
                else if (Tools.current == Tool.Scale && sclLeaf != null)
                {
                    EditorGUI.BeginChangeCheck();
                    Vector3 ns = Handles.ScaleHandle(scale, pos, rot, HandleUtility.GetHandleSize(pos));
                    if (EditorGUI.EndChangeCheck()) CommitGizmo(sclLeaf, ns);
                }
                else if (posLeaf != null)
                {
                    EditorGUI.BeginChangeCheck();
                    Vector3 np = Handles.PositionHandle(
                        pos, Tools.pivotRotation == PivotRotation.Local ? rot : Quaternion.identity);
                    if (EditorGUI.EndChangeCheck()) CommitGizmo(posLeaf, np);
                }
            }
        }

        // Radial directions for the three radius handles of a sphere (one per axis).
        static readonly Vector3[] s_SphereRadiusDirs = { Vector3.right, Vector3.up, Vector3.forward };

        // Mirrors the VFX package's VFXTSphereGizmo / VFXArcSphereGizmo (both internal) on
        // the public Handles API. Transform handle in the base frame; the sphere shell and
        // its radius/arc handles inside that frame × the sphere's own TRS. A plain Sphere
        // has no arc leaf, so it draws three full wire discs and skips the arc handle.
        void DrawSphereGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
        {
            var posLeaf = GizmoLeaf(leaves, "position");
            var angLeaf = GizmoLeaf(leaves, "angle");
            var sclLeaf = GizmoLeaf(leaves, "scale");
            var radLeaf = GizmoLeaf(leaves, "radius");
            var arcLeaf = GizmoLeaf(leaves, "arc"); // null for a plain Sphere

            Vector3 pos    = posLeaf != null ? GizmoVec(posLeaf) : Vector3.zero;
            Vector3 angles = angLeaf != null ? GizmoVec(angLeaf) : Vector3.zero;
            Vector3 scale  = sclLeaf != null ? GizmoVec(sclLeaf) : Vector3.one;
            if (scale.sqrMagnitude < 1e-9f) scale = Vector3.one;
            float radius   = radLeaf != null ? GizmoFloat(radLeaf) : 1f;
            bool fullArc   = arcLeaf == null;
            float arcDeg   = fullArc ? 360f : Mathf.Clamp(GizmoFloat(arcLeaf) * Mathf.Rad2Deg, 0f, 360f);

            Matrix4x4 baseMatrix = local ? t.localToWorldMatrix : Matrix4x4.identity;
            Quaternion rot = Quaternion.Euler(angles);

            DrawSpaceTransformHandle(baseMatrix, pos, rot, scale, posLeaf, angLeaf, sclLeaf);

            Matrix4x4 sphereMatrix = baseMatrix * Matrix4x4.TRS(pos, rot, scale);
            using (new Handles.DrawingScope(sphereMatrix))
            {
                // shell (cosmetic → Repaint only; drawing on other events corrupts GL state)
                if (Event.current.type == EventType.Repaint)
                {
                    if (fullArc)
                    {
                        Handles.DrawWireDisc(Vector3.zero, Vector3.forward, radius);
                        Handles.DrawWireDisc(Vector3.zero, Vector3.up, radius);
                        Handles.DrawWireDisc(Vector3.zero, Vector3.right, radius);
                    }
                    else
                    {
                        // longitudinal half-circles at every 90° up to the arc, plus one at
                        // the arc edge, plus the equator arc (mirrors VFXArcSphereGizmo)
                        for (int i = 0; i < 4; i++)
                        {
                            float a = i * 90f;
                            if (a <= arcDeg)
                                Handles.DrawWireArc(Vector3.zero,
                                    Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, a)) * Vector3.right,
                                    Vector3.forward, 180f, radius);
                        }
                        if (arcDeg < 360f)
                            Handles.DrawWireArc(Vector3.zero,
                                Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, arcDeg)) * Vector3.right,
                                Vector3.forward, 180f, radius);
                        Handles.DrawWireArc(Vector3.zero, -Vector3.forward, Vector3.up, arcDeg, radius);
                    }
                }

                // three radial cube radius handles (one per axis), like VFX
                if (radLeaf != null)
                {
                    foreach (var dir in s_SphereRadiusDirs)
                    {
                        float nr = RadialRadiusHandle(Vector3.zero, dir, radius, AxisColor(dir));
                        if (!Mathf.Approximately(nr, radius)) { CommitGizmoFloat(radLeaf, nr); radius = nr; }
                    }
                }

                // arc handle in the equator plane (VFX uses Euler(-90,0,0) so the sweep
                // axis maps to -forward, matching the equator arc drawn above)
                if (arcLeaf != null)
                    ArcHandle(arcLeaf, Vector3.zero, radius, arcDeg, Quaternion.Euler(-90f, 0f, 0f));
            }

            // label, anchored to the sphere centre on screen (outside the matrix scope)
            Vector3 worldCenter = sphereMatrix.MultiplyPoint(Vector3.zero);
            string txt = $"<b>{_gizmoStruct.Label}</b>\nradius {radius:0.##}";
            if (arcLeaf != null) txt += $"  arc {arcDeg:0}°";
            GizmoLabel(worldCenter, HandleUtility.GetHandleSize(worldCenter), txt);
        }

        // X=red, Y=green, Z=blue for a cardinal-ish direction.
        static Color AxisColor(Vector3 dir)
        {
            Vector3 a = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
            return (a.x >= a.y && a.x >= a.z) ? Handles.xAxisColor
                 : (a.y >= a.z) ? Handles.yAxisColor : Handles.zAxisColor;
        }

        // In-plane cardinal directions for a circle's radius handles (VFX order: the arc
        // sweeps from +up, so handle i sits at i×90° and is hidden past the arc).
        static readonly Vector3[] s_CircleRadiusDirs = { Vector3.up, Vector3.right, Vector3.down, Vector3.left };

        // Mirrors the VFX package's VFXCircleGizmo / VFXArcCircleGizmo (both internal). The
        // circle lies in the XY plane (normal -forward); a plain Circle has no arc leaf, so
        // it draws a full disc and all four radius handles.
        void DrawCircleGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
        {
            var posLeaf = GizmoLeaf(leaves, "position");
            var angLeaf = GizmoLeaf(leaves, "angle");
            var sclLeaf = GizmoLeaf(leaves, "scale");
            var radLeaf = GizmoLeaf(leaves, "radius");
            var arcLeaf = GizmoLeaf(leaves, "arc"); // null for a plain Circle

            Vector3 pos    = posLeaf != null ? GizmoVec(posLeaf) : Vector3.zero;
            Vector3 angles = angLeaf != null ? GizmoVec(angLeaf) : Vector3.zero;
            Vector3 scale  = sclLeaf != null ? GizmoVec(sclLeaf) : Vector3.one;
            if (scale.sqrMagnitude < 1e-9f) scale = Vector3.one;
            float radius   = radLeaf != null ? GizmoFloat(radLeaf) : 1f;
            bool fullArc   = arcLeaf == null;
            float arcDeg   = fullArc ? 360f : Mathf.Clamp(GizmoFloat(arcLeaf) * Mathf.Rad2Deg, 0f, 360f);

            Matrix4x4 baseMatrix = local ? t.localToWorldMatrix : Matrix4x4.identity;
            Quaternion rot = Quaternion.Euler(angles);
            DrawSpaceTransformHandle(baseMatrix, pos, rot, scale, posLeaf, angLeaf, sclLeaf);

            Matrix4x4 m = baseMatrix * Matrix4x4.TRS(pos, rot, scale);
            using (new Handles.DrawingScope(m))
            {
                if (Event.current.type == EventType.Repaint)
                {
                    if (fullArc) Handles.DrawWireDisc(Vector3.zero, -Vector3.forward, radius);
                    else Handles.DrawWireArc(Vector3.zero, -Vector3.forward, Vector3.up, arcDeg, radius);
                }

                if (radLeaf != null)
                    for (int i = 0; i < s_CircleRadiusDirs.Length; i++)
                    {
                        if (!fullArc && i * 90f > arcDeg) continue; // only handles within the arc (VFX countVisible)
                        var dir = s_CircleRadiusDirs[i];
                        float nr = RadialRadiusHandle(Vector3.zero, dir, radius, AxisColor(dir));
                        if (!Mathf.Approximately(nr, radius)) { CommitGizmoFloat(radLeaf, nr); radius = nr; }
                    }

                if (arcLeaf != null)
                    ArcHandle(arcLeaf, Vector3.zero, radius, arcDeg, Quaternion.Euler(-90f, 0f, 0f));
            }

            Vector3 wc = m.MultiplyPoint(Vector3.zero);
            string txt = $"<b>{_gizmoStruct.Label}</b>\nradius {radius:0.##}";
            if (arcLeaf != null) txt += $"  arc {arcDeg:0}°";
            GizmoLabel(wc, HandleUtility.GetHandleSize(wc), txt);
        }

        // Cardinal sweep angles at which a torus draws tube cross-sections.
        static readonly float[] s_TorusAngles = { 0f, 90f, 180f, 270f };

        // Mirrors the VFX package's VFXTorusGizmo / VFXArcTorusGizmo (both internal). The
        // ring lies in the XY plane (normal forward); the tube cross-sections sweep that
        // plane from +up around -forward. `majorRadius` is the ring radius, `minorRadius`
        // the tube thickness. A plain Torus has no arc leaf → full discs, no arc handle.
        void DrawTorusGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
        {
            var posLeaf = GizmoLeaf(leaves, "position");
            var angLeaf = GizmoLeaf(leaves, "angle");
            var sclLeaf = GizmoLeaf(leaves, "scale");
            var majLeaf = GizmoLeaf(leaves, "major");
            var minLeaf = GizmoLeaf(leaves, "minor");
            var arcLeaf = GizmoLeaf(leaves, "arc"); // null for a plain Torus

            Vector3 pos    = posLeaf != null ? GizmoVec(posLeaf) : Vector3.zero;
            Vector3 angles = angLeaf != null ? GizmoVec(angLeaf) : Vector3.zero;
            Vector3 scale  = sclLeaf != null ? GizmoVec(sclLeaf) : Vector3.one;
            if (scale.sqrMagnitude < 1e-9f) scale = Vector3.one;
            float major    = majLeaf != null ? GizmoFloat(majLeaf) : 1f;
            float minor    = minLeaf != null ? GizmoFloat(minLeaf) : 0.1f;
            bool fullArc   = arcLeaf == null;
            float arcDeg   = fullArc ? 360f : Mathf.Clamp(GizmoFloat(arcLeaf) * Mathf.Rad2Deg, 0f, 360f);

            Matrix4x4 baseMatrix = local ? t.localToWorldMatrix : Matrix4x4.identity;
            Quaternion rot = Quaternion.Euler(angles);
            DrawSpaceTransformHandle(baseMatrix, pos, rot, scale, posLeaf, angLeaf, sclLeaf);

            Matrix4x4 m = baseMatrix * Matrix4x4.TRS(pos, rot, scale);
            using (new Handles.DrawingScope(m))
            {
                if (Event.current.type == EventType.Repaint)
                {
                    // ring envelope: two side discs offset ±minor, plus outer/inner rings
                    if (fullArc)
                    {
                        Handles.DrawWireDisc(Vector3.forward * minor, Vector3.forward, major);
                        Handles.DrawWireDisc(Vector3.back * minor, Vector3.forward, major);
                        Handles.DrawWireDisc(Vector3.zero, Vector3.forward, major + minor);
                        Handles.DrawWireDisc(Vector3.zero, Vector3.forward, Mathf.Max(0f, major - minor));
                    }
                    else
                    {
                        Handles.DrawWireArc(Vector3.forward * minor, Vector3.back, Vector3.up, arcDeg, major);
                        Handles.DrawWireArc(Vector3.back * minor, Vector3.back, Vector3.up, arcDeg, major);
                        Handles.DrawWireArc(Vector3.zero, Vector3.back, Vector3.up, arcDeg, major + minor);
                        Handles.DrawWireArc(Vector3.zero, Vector3.back, Vector3.up, arcDeg, Mathf.Max(0f, major - minor));
                    }
                    // tube cross-sections at the cardinal sweep angles within the arc
                    foreach (var a in s_TorusAngles)
                    {
                        if (!fullArc && a > arcDeg) continue;
                        Quaternion ar = Quaternion.AngleAxis(a, Vector3.back);
                        Handles.DrawWireDisc(ar * Vector3.up * major, ar * Vector3.right, minor);
                    }
                }

                // major radius handle along +up (the angle-0 cross-section direction)
                if (majLeaf != null)
                {
                    float nm = RadialRadiusHandle(Vector3.zero, Vector3.up, major, Handles.yAxisColor);
                    if (!Mathf.Approximately(nm, major)) { CommitGizmoFloat(majLeaf, nm); major = nm; }
                }
                // minor radius (thickness) handle at the angle-0 cap, offset out of the ring plane
                if (minLeaf != null)
                {
                    float nt = RadialRadiusHandle(Vector3.up * major, Vector3.forward, minor, Handles.zAxisColor);
                    if (!Mathf.Approximately(nt, minor)) { CommitGizmoFloat(minLeaf, nt); minor = nt; }
                }

                if (arcLeaf != null)
                    ArcHandle(arcLeaf, Vector3.zero, major, arcDeg, Quaternion.Euler(-90f, 0f, 0f));
            }

            Vector3 wc = m.MultiplyPoint(Vector3.zero);
            string txt = $"<b>{_gizmoStruct.Label}</b>\nmajor {major:0.##}  minor {minor:0.##}";
            if (arcLeaf != null) txt += $"\narc {arcDeg:0}°";
            GizmoLabel(wc, HandleUtility.GetHandleSize(wc), txt);
        }

        // Radial directions for the side lines of a full (un-arc'd) cone outline.
        static readonly Vector3[] s_ConeDirs = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };

        // Mirrors the VFX package's VFXConeGizmo / VFXTArcConeGizmo (both internal) on the
        // public Handles API. The transform (move/rotate/scale) handle runs in the base
        // frame — component transform for Local space, identity for World, like
        // VFXSpaceableGizmo's Handles.matrix; the cone shape and its radius/height/arc
        // handles are drawn inside that frame × the cone's own TRS. A plain Cone has no
        // arc leaf, so the arc handle and the wedge edges are skipped.
        void DrawConeGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
        {
            var posLeaf    = GizmoLeaf(leaves, "position");
            var angLeaf    = GizmoLeaf(leaves, "angle");
            var sclLeaf    = GizmoLeaf(leaves, "scale");
            var baseLeaf   = GizmoLeaf(leaves, "base");
            var topLeaf    = GizmoLeaf(leaves, "top");
            var heightLeaf = GizmoLeaf(leaves, "height");
            var arcLeaf    = GizmoLeaf(leaves, "arc"); // null for a plain Cone

            Vector3 pos    = posLeaf != null ? GizmoVec(posLeaf) : Vector3.zero;
            Vector3 angles = angLeaf != null ? GizmoVec(angLeaf) : Vector3.zero;
            Vector3 scale  = sclLeaf != null ? GizmoVec(sclLeaf) : Vector3.one;
            if (scale.sqrMagnitude < 1e-9f) scale = Vector3.one; // avoid a degenerate matrix
            float baseR    = baseLeaf   != null ? GizmoFloat(baseLeaf)   : 1f;
            float topR     = topLeaf    != null ? GizmoFloat(topLeaf)    : 0f;
            float height   = heightLeaf != null ? GizmoFloat(heightLeaf) : 1f;
            bool fullArc   = arcLeaf == null;
            float arcDeg   = fullArc ? 360f : Mathf.Clamp(GizmoFloat(arcLeaf) * Mathf.Rad2Deg, 0f, 360f);

            Matrix4x4 baseMatrix = local ? t.localToWorldMatrix : Matrix4x4.identity;
            Quaternion rot = Quaternion.Euler(angles);

            DrawSpaceTransformHandle(baseMatrix, pos, rot, scale, posLeaf, angLeaf, sclLeaf);

            // ---- cone shape + radius/height/arc handles, in the cone's own frame ----
            Matrix4x4 coneMatrix = baseMatrix * Matrix4x4.TRS(pos, rot, scale);
            Vector3 bottomCap = Vector3.zero;
            Vector3 topCap = Vector3.up * height;

            using (new Handles.DrawingScope(coneMatrix))
            {
                // outline (cosmetic → Repaint only; drawing on other events corrupts GL state)
                if (Event.current.type == EventType.Repaint)
                {
                    if (fullArc)
                    {
                        Handles.DrawWireDisc(topCap, Vector3.up, topR);
                        Handles.DrawWireDisc(bottomCap, Vector3.up, baseR);
                        foreach (var d in s_ConeDirs)
                            Handles.DrawLine(topCap + d * topR, bottomCap + d * baseR);
                    }
                    else
                    {
                        Vector3 arcDir = Quaternion.AngleAxis(arcDeg, Vector3.up) * Vector3.forward;
                        Handles.DrawWireArc(topCap, Vector3.up, Vector3.forward, arcDeg, topR);
                        Handles.DrawWireArc(bottomCap, Vector3.up, Vector3.forward, arcDeg, baseR);
                        Handles.DrawLine(topCap, topCap + Vector3.forward * topR);
                        Handles.DrawLine(bottomCap, bottomCap + Vector3.forward * baseR);
                        Handles.DrawLine(topCap, topCap + arcDir * topR);
                        Handles.DrawLine(bottomCap, bottomCap + arcDir * baseR);
                        Handles.DrawLine(bottomCap + Vector3.forward * baseR, topCap + Vector3.forward * topR);
                        Handles.DrawLine(bottomCap + arcDir * baseR, topCap + arcDir * topR);
                    }
                }

                // radius handles (radial cube sliders at the +forward extremity of each cap)
                if (baseLeaf != null)
                {
                    float nb = RadialRadiusHandle(bottomCap, Vector3.forward, baseR, Handles.zAxisColor);
                    if (!Mathf.Approximately(nb, baseR)) CommitGizmoFloat(baseLeaf, nb);
                }
                if (topLeaf != null)
                {
                    float nt = RadialRadiusHandle(topCap, Vector3.forward, topR, Handles.zAxisColor);
                    if (!Mathf.Approximately(nt, topR)) CommitGizmoFloat(topLeaf, nt);
                }

                // height handle (slide the top cap along up)
                if (heightLeaf != null)
                {
                    Handles.color = Handles.yAxisColor;
                    EditorGUI.BeginChangeCheck();
                    Vector3 nh = Handles.Slider(topCap, Vector3.up,
                        HandleUtility.GetHandleSize(topCap) * 0.08f, Handles.CubeHandleCap, 0f);
                    if (EditorGUI.EndChangeCheck()) CommitGizmoFloat(heightLeaf, nh.y);
                }

                // arc handle (Slider2D in the cap plane, like VFXGizmo.ArcGizmo)
                if (arcLeaf != null)
                {
                    float arcRadius = Mathf.Max(baseR, topR);
                    Vector3 arcCenter = baseR >= topR ? bottomCap : topCap;
                    ArcHandle(arcLeaf, arcCenter, arcRadius, arcDeg, Quaternion.identity);
                }
            }

            // label, anchored to the cone base position on screen (outside the matrix scope)
            Vector3 worldBase = coneMatrix.MultiplyPoint(bottomCap);
            string txt = $"<b>{_gizmoStruct.Label}</b>\nbase {baseR:0.##}  top {topR:0.##}  h {height:0.##}";
            if (arcLeaf != null) txt += $"\narc {arcDeg:0}°";
            GizmoLabel(worldBase, HandleUtility.GetHandleSize(worldBase), txt);
        }

        // A radial cube slider `dir * radius` out from `center`; returns the new
        // (non-negative) radius. Must be called inside the shape's matrix scope.
        float RadialRadiusHandle(Vector3 center, Vector3 dir, float radius, Color color)
        {
            Vector3 hp = center + dir * radius;
            Handles.color = color;
            EditorGUI.BeginChangeCheck();
            Vector3 np = Handles.Slider(hp, dir,
                HandleUtility.GetHandleSize(hp) * 0.08f, Handles.CubeHandleCap, 0f);
            return EditorGUI.EndChangeCheck() ? Mathf.Max(0f, Vector3.Dot(np - center, dir)) : radius;
        }

        // Arc handle, mirroring VFXGizmo.ArcGizmo: a Slider2D whose angle around the local
        // +up axis (after `rotation`) sets the arc. Must be called inside the shape's matrix
        // scope. `rotation` orients the sweep plane (identity for cones, Euler(-90,0,0) so a
        // sphere sweeps around -forward).
        void ArcHandle(VfxExposedParam arcLeaf, Vector3 center, float radius, float arcDeg, Quaternion rotation)
        {
            if (radius < 1e-5f) return;
            using (new Handles.DrawingScope(Handles.matrix * Matrix4x4.Translate(center) * Matrix4x4.Rotate(rotation)))
            {
                Vector3 handlePos = Quaternion.AngleAxis(arcDeg, Vector3.up) * Vector3.forward * radius;
                if (!float.IsFinite(handlePos.sqrMagnitude)) return;
                Handles.color = Handles.centerColor;
                EditorGUI.BeginChangeCheck();
                Vector3 np = Handles.Slider2D(handlePos, Vector3.up, Vector3.forward, Vector3.right,
                    HandleUtility.GetHandleSize(handlePos) * 0.1f, Handles.SphereHandleCap, Vector2.zero);
                if (EditorGUI.EndChangeCheck())
                {
                    float newArc = Vector3.Angle(Vector3.forward, np) * Mathf.Sign(Vector3.Dot(Vector3.right, np));
                    arcDeg += Mathf.DeltaAngle(arcDeg, newArc);
                    arcDeg = Mathf.Repeat(arcDeg, 360f);
                    CommitGizmoFloat(arcLeaf, arcDeg * Mathf.Deg2Rad);
                }
            }
        }

        float GizmoFloat(VfxExposedParam leaf) => ToFloat(VfxPropertySheet.GetValue(_so, leaf));

        void CommitGizmoFloat(VfxExposedParam leaf, float value)
        {
            SetValueAll(leaf, value);
            RefreshProperty(leaf); // sync the bound field in the window
        }

        void CommitGizmo(VfxExposedParam leaf, Vector3 value)
        {
            SetValueAll(leaf, value);
            RefreshProperty(leaf); // sync the bound field in the window
        }

        // Box face handle drawn in its axis color (X=red, Y=green, Z=blue); the handle's
        // rotation faces along the face normal, which tells us the axis.
        static void DrawAxisHandle(int id, Vector3 pos, Quaternion rot, float size, EventType type)
        {
            Vector3 n = rot * Vector3.forward;
            Vector3 a = new Vector3(Mathf.Abs(n.x), Mathf.Abs(n.y), Mathf.Abs(n.z));
            Color c = (a.x >= a.y && a.x >= a.z) ? Handles.xAxisColor
                    : (a.y >= a.z) ? Handles.yAxisColor
                    : Handles.zAxisColor;
            Color prev = Handles.color;
            Handles.color = c;
            Handles.DotHandleCap(id, pos, rot, size, type);
            Handles.color = prev;
        }

        // ---- spaceable property space icon (display only) ----

        static readonly Dictionary<string, Texture2D> s_SpaceIcons = new Dictionary<string, Texture2D>();

        static Texture2D LoadSpaceTexture(string space)
        {
            if (string.IsNullOrEmpty(space)) space = "None";
            string skin = EditorGUIUtility.isProSkin ? "d_" : "";
            string key = skin + space;
            if (s_SpaceIcons.TryGetValue(key, out var cached) && cached != null) return cached;

            // EditorGUIUtility.LoadIcon is internal, so resolve the variants ourselves.
            // The blur came from displaying the 1x icon on a HiDPI screen — pick the
            // @2x asset there (downscaling the @2x on 1x screens is fine too).
            const string dir = "Packages/com.unity.visualeffectgraph/Editor/UIResources/VFX/";
            string n = space + "Space";
            Texture2D Hi() => AssetDatabase.LoadAssetAtPath<Texture2D>(dir + skin + n + "@2x.png")
                           ?? AssetDatabase.LoadAssetAtPath<Texture2D>(dir + n + "@2x.png");
            Texture2D Lo() => AssetDatabase.LoadAssetAtPath<Texture2D>(dir + skin + n + ".png")
                           ?? AssetDatabase.LoadAssetAtPath<Texture2D>(dir + n + ".png");

            bool hidpi = EditorGUIUtility.pixelsPerPoint > 1.5f;
            var tex = hidpi ? (Hi() ?? Lo()) : (Lo() ?? Hi());
            s_SpaceIcons[key] = tex;
            return tex;
        }

        // The property's coordinate space (World/Local/None), shown read-only to the
        // right of the label; it's authored in the VFX graph, not here.
        VisualElement BuildSpaceIcon(VfxExposedParam p)
        {
            if (!p.Spaceable) return null;
            var tex = LoadSpaceTexture(p.Space);
            if (tex == null) return null;
            // Pickable (not Ignore) so hovering shows the tooltip.
            var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit };
            img.AddToClassList("vfx-space-icon");
            string desc = p.Space == "None" ? "No space" : $"{p.Space} space";
            img.tooltip = $"{desc} — defined in the VFX graph (read-only)";
            return img;
        }
    }
}
