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
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.VFX;
using VfxControl.EditorTools;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public partial class VfxControlWindow : EditorWindow
    {
        const string UssPath = "Assets/VfxControl/Editor/VfxControl.uss";

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

        // Tab tear-off: when non-null this window shows ONLY that tab (the strip is hidden and
        // _tab is pinned). Deliberately NOT [SerializeField] — a serialized solo flag bakes the lean
        // state into the saved window layout, so a torn-off window comes back lean every session and
        // strands the Asset field/transport. Session-only instead: a pop-out reverts to a full window
        // after a domain reload / restart (re-tear-off if wanted).
        string _soloTab;
        // persistent chrome containers: the search field is built ONCE (so typing never
        // loses focus); tabs/chips/rail/body are repopulated by PopulateActiveTab.
        ToolbarSearchField _searchField;
        VisualElement _chipsHost, _tabsHost, _railContainer, _tabBody;
        float _scrubT;

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
            // If the focused instance is a torn-off (solo) pop-out, restore it to a full window so
            // the menu always yields a complete inspector (the tab strip is otherwise hidden).
            if (w._soloTab != null) { w._soloTab = null; w.Rebuild(); }
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
            StopProfiling(); // release the VFX profiling registration we requested for timing readouts
            _readbackBuffer?.Dispose(); _readbackBuffer = null; // GPU readback buffer
            _readbackGenBuffer?.Dispose(); _readbackGenBuffer = null; // per-slot generation buffer
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

            // Pop-out (solo) windows are lean: just header + chrome + rail + the one tab's body.
            // The Asset field + transport bar are always BUILT (so they can never be stranded out of
            // the tree) and merely hidden in solo mode — the transport stays the main window's job
            // (see Tick, which doesn't advance the clock in a solo window).
            var meta = BuildMetaSection();
            var transport = BuildMiniTransport();
            var gap = MakeElement("vfx-section-gap");   // the intentional divider
            var leanDisplay = _soloTab == null ? DisplayStyle.Flex : DisplayStyle.None;
            meta.style.display = transport.style.display = gap.style.display = leanDisplay;
            root.Add(meta);
            root.Add(transport);
            root.Add(gap);

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
                Id = "debug", Label = "Debug", HasRail = true, Sections = DebugSections,
                Build = BuildDebugTab,
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

        // Debug sections: the live stat grid, per-system capacity bars, texture usage, visualizers.
        static List<SectionDef> DebugSections() => new List<SectionDef>
        {
            new SectionDef { Id = "live", Label = "Live statistics" },
            new SectionDef { Id = "systems", Label = "Systems" },
            new SectionDef { Id = "textures", Label = "Textures" },
            new SectionDef { Id = "particles", Label = "Particles" },
            new SectionDef { Id = "visualizers", Label = "Visualizers" },
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
            // Pop-out (solo) windows show a single pinned tab — hide the strip entirely.
            if (_soloTab != null) { _tabsHost.style.display = DisplayStyle.None; return; }
            _tabsHost.style.display = DisplayStyle.Flex;
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

        Button MakeTab(string id, string label, int count)
        {
            // Use child Labels (not the Button's intrinsic text) so the label and the
            // count badge flow as flex items left-to-right instead of overlapping.
            // ClickEvent (not the Button action) so Alt is observable: Alt+click folds/unfolds
            // the whole tab body in one go (like Alt+click on a category/struct header).
            var tab = new Button();
            tab.AddToClassList("vfx-tab");
            tab.tooltip = "Alt+click to expand/collapse all · right-click to open in a new window";
            // Tear-off: right-click any focused tab → pop it into its own dockable window. Excluded
            // for "All" (a solo All would host the Debug shortcut whose jump has no strip to land on).
            if (id != "all")
                tab.AddManipulator(new ContextualMenuManipulator(evt =>
                    evt.menu.AppendAction("Open in new window", _ => OpenSolo(id, label))));
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

        // Open the given tab in its own dockable VfxControlWindow ("solo" mode): a distinct
        // instance pinned to one tab, following scene selection like the main window. Per-asset
        // state (favorites/collapsed/payloads) is shared via EditorPrefs/SessionState.
        static void OpenSolo(string tabId, string label)
        {
            var w = CreateWindow<VfxControlWindow>();
            w._soloTab = tabId;
            w._tab = tabId; // OnEnable already ran (non-solo); pin then re-render in solo mode
            w.titleContent = new GUIContent("VFX · " + label);
            w.minSize = new Vector2(300, 300);
            w.Show();
            w.Rebuild();
        }

        // ------------------------------------------------------------------ properties tab

        // ------------------------------------------------------------------ playback tab

        // Does a field/section label match the current search query? (empty query = match all)
        bool SearchMatches(string label)
        {
            string q = _search.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(q) || (label != null && label.ToLowerInvariant().Contains(q));
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
            // Debug is NOT embedded here (it gets heavy): a lightweight live teaser row that
            // jumps to the Debug tab, or can be torn off via the tab's right-click menu.
            if (_effect != null) AddDebugShortcut(body);
        }

        // A non-folding All-tab row styled like the section dividers, but it JUMPS to the Debug
        // tab instead of expanding. Carries a live one-line teaser (_dbgTeaser, refreshed by
        // RefreshDebugStats) so the All tab still surfaces live stats at a glance.
        void AddDebugShortcut(VisualElement body)
        {
            _dbgTeaser = null;
            var head = MakeElement("vfx-allsection-head");
            head.AddToClassList("vfx-allsection-head--link");
            var title = new Label("Debug");
            title.AddToClassList("vfx-allsection-title");
            head.Add(title);
            _dbgTeaser = new Label();
            _dbgTeaser.AddToClassList("vfx-debug-teaser");
            head.Add(_dbgTeaser);
            var jump = new Label("→") { pickingMode = PickingMode.Ignore };
            jump.AddToClassList("vfx-allsection-jump");
            head.Add(jump);
            head.tooltip = "Open the Debug tab";
            head.RegisterCallback<ClickEvent>(e =>
            {
                _tab = "debug"; _state.Tab = "debug";
                PopulateActiveTab();
            });
            body.Add(head);
            RefreshDebugStats();
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

        // Playback rows built this populate (favorites copy + section copy), kept in sync on edit
        // via each field's `sync` action (they back live props, not SerializedProperties).
        readonly List<(VisualElement row, PField field, Action sync)> _playbackRows = new List<(VisualElement, PField, Action)>();

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

            // Only the main window drives the playback clock; a transport-less pop-out just
            // observes (otherwise multiple windows would fight over Reinit/pause on the shared effect).
            if (_soloTab == null && _effect != null && !_effect.pause && _duration > 0f)
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
            PumpReadback(); // keeps the readback globals bound; only requests data when the panel shows
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
            RefreshDebugStats(); // no-ops unless the Debug stat grid is the current body
            // Keep the Show Bounds checkbox reflecting the shared state when another window flips it.
            if (_showBoundsToggle?.panel != null && _showBoundsToggle.value != ShowBounds)
                _showBoundsToggle.SetValueWithoutNotify(ShowBounds);
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

    }
}
