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
        // fills the bar over this many seconds and then loops.
        float _duration = 10f;
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
        string _category = "all";
        string _tab = "props";    // props | play | debug

        // --- live element refs ---
        VisualElement _miniFill;
        Label _timeLabel, _liveLabel, _footNote;
        Button _resetAllBtn, _playBtn;
        Image _playIcon;
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

        // the filtered pinned-tray + groups list; rebuilt on search/filter without
        // recreating the search field (which would steal focus mid-typing).
        VisualElement _listContainer;

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
            }
            finally { VfxGraphReflection.Verbose = false; }
        }

        void OnEnable()
        {
            _duration = VfxControlState.GetTimelineDuration();
            _lastTick = EditorApplication.timeSinceStartup;
            RefreshTarget();
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.projectChanged += OnProjectChanged;
            SceneView.duringSceneGui += OnSceneGui;
            rootVisualElement.schedule.Execute(Tick).Every(33); // ~30fps clock + live stats
            Rebuild();
        }

        void OnDisable()
        {
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

            // The selection isn't a scene Visual Effect. Keep whatever target we
            // already have (e.g. one assigned via the target field) — clicking
            // around the scene shouldn't drop it. Only surface guidance when there
            // is no target at all.
            if (_effect != null)
            {
                _selectionHint = null;
                if (_so == null) SetTarget(_effect, GatherTargets(_effect)); // SerializedObject doesn't survive a domain reload
                return;
            }
            _selectionHint = hint;
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
            _state = new VfxControlState(guid);
            _favorites = _state.LoadFavorites();
            _collapsed = _state.LoadCollapsed();
            _constrained = _state.LoadConstrained();
            _tab = _state.Tab;
            _filter = _state.Filter;
            _category = _state.Category;
            _search = _state.Search;
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
            root.Add(BuildTargetPicker()); // always visible so a target can be assigned

            if (_effect == null)
            {
                var ph = new Label(_selectionHint ??
                    "Pick a Visual Effect from the scene above, or select one in the Hierarchy.");
                ph.AddToClassList("vfx-placeholder");
                root.Add(ph);
                return;
            }

            if (_so == null) SetTarget(_effect); // recover after a domain reload
            UpdateAllSos();

            root.Add(BuildMetaSection());
            root.Add(BuildMiniTransport());
            root.Add(MakeElement("vfx-section-gap"));   // the intentional divider
            root.Add(BuildTabs());

            var body = new ScrollView { name = "body" };
            body.AddToClassList("vfx-scroll");
            switch (_tab)
            {
                case "props": BuildPropertiesTab(body); break;
                case "play": BuildPlaybackTab(body); break;
                case "debug": BuildPlaceholder(body, "Debug tab — coming in the next pass.\nLive stats, systems, visualizers."); break;
            }
            root.Add(body);

            root.Add(BuildFooter());
            UpdateLive();
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
        VisualElement BuildTargetPicker()
        {
            var meta = MakeElement("vfx-meta");

            var row = MakeElement("vfx-meta-row");
            var label = new Label("Visual Effect");
            label.AddToClassList("vfx-mlabel");
            row.Add(label);

            var field = new ObjectField { objectType = typeof(VisualEffect), allowSceneObjects = true };
            field.AddToClassList("vfx-meta-field");
            field.tooltip = "The scene Visual Effect component this window edits.";
            field.SetValueWithoutNotify(_effect);
            field.RegisterValueChangedCallback(e =>
            {
                var ve = e.newValue as VisualEffect;
                if (ve != null && EditorUtility.IsPersistent(ve))
                {
                    _selectionHint = "That Visual Effect lives on a prefab/asset in the Project.\n" +
                                     "Use an instance that exists in an open scene.";
                    SetTarget(null);
                }
                else
                {
                    _selectionHint = null;
                    SetTarget(ve);
                }
                Rebuild();
            });
            row.Add(field);
            meta.Add(row);

            return meta;
        }

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

            var eventRow = MakeElement("vfx-meta-row");
            var eventLabel = new Label("Initial Event");
            eventLabel.AddToClassList("vfx-mlabel");
            eventRow.Add(eventLabel);
            var eventField = new TextField { value = string.IsNullOrEmpty(_effect.initialEventName) ? "OnPlay" : _effect.initialEventName };
            eventField.AddToClassList("vfx-meta-field");
            eventField.RegisterValueChangedCallback(e =>
            {
                Undo.RecordObjects(_effects.ToArray(), "Set Initial Event");
                foreach (var ve in _effects) { ve.initialEventName = e.newValue; EditorUtility.SetDirty(ve); }
            });
            eventRow.Add(eventField);
            meta.Add(eventRow);

            return meta;
        }

        VisualElement BuildMiniTransport()
        {
            var bar = MakeElement("vfx-sticky-transport");

            _playBtn = new Button(() =>
            {
                _effect.pause = !_effect.pause;
                UpdateLive(); // refreshes the icon from the new pause state
            });
            _playBtn.AddToClassList("vfx-tbtn");
            _playBtn.AddToClassList("vfx-tbtn--primary");
            // built-in icon drawn 1:1 at native size (no scaling → no aliasing);
            // centered by the button's flex alignment.
            _playIcon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            _playIcon.style.width = 16;
            _playIcon.style.height = 16;
            _playBtn.Add(_playIcon);
            bar.Add(_playBtn);

            // step one frame: pause, then simulate a single step (handoff API mapping)
            var step = new Button(() =>
            {
                _effect.pause = true;
                _effect.Simulate(1f / 60f, 1);
                _scrubT = Mathf.Min(1f, _scrubT + (1f / 60f) / _duration);
                UpdateLive();
            });
            step.AddToClassList("vfx-tbtn");
            step.tooltip = "Step one frame";
            var stepIcon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            stepIcon.style.width = 16;
            stepIcon.style.height = 16;
            stepIcon.image = EditorGUIUtility.IconContent("StepButton").image;
            step.Add(stepIcon);
            bar.Add(step);

            var restart = new Button(() => { _effect.Reinit(); _scrubT = 0; UpdateLive(); }) { text = "↺" };
            restart.AddToClassList("vfx-tbtn");
            restart.tooltip = "Restart (Reinit)";
            bar.Add(restart);

            var scrub = MakeElement("vfx-mini-scrub");
            _miniFill = MakeElement("vfx-mini-fill");
            _miniFill.style.width = Length.Percent(_scrubT * 100f);
            scrub.Add(_miniFill);
            scrub.RegisterCallback<MouseDownEvent>(e => { scrub.CaptureMouse(); ScrubAt(scrub, e.localMousePosition.x); });
            scrub.RegisterCallback<MouseMoveEvent>(e => { if (scrub.HasMouseCapture()) ScrubAt(scrub, e.localMousePosition.x); });
            scrub.RegisterCallback<MouseUpEvent>(e => scrub.ReleaseMouse());
            bar.Add(scrub);

            _timeLabel = new Label("0.00 / 0s");
            _timeLabel.AddToClassList("vfx-mini-time");
            bar.Add(_timeLabel);

            _liveLabel = new Label("0 live");
            _liveLabel.AddToClassList("vfx-mini-live");
            bar.Add(_liveLabel);

            return bar;
        }

        // GPU sim has no random-access seek: pause, Reinit, then simulate forward
        // to the target time. Best-effort and capped (see handoff "Scrubbing caveat").
        void ScrubAt(VisualElement scrub, float localX)
        {
            float w = scrub.layout.width;
            if (w <= 0) return;
            _scrubT = Mathf.Clamp01(localX / w);
            if (_miniFill != null) _miniFill.style.width = Length.Percent(_scrubT * 100f);

            float target = _scrubT * _duration;
            _effect.pause = true;
            _effect.Reinit();
            const float dt = 1f / 60f;
            int steps = Mathf.Clamp(Mathf.RoundToInt(target / dt), 0, 600);
            if (steps > 0) _effect.Simulate(dt, (uint)steps);
            UpdateLive();
        }

        VisualElement BuildTabs()
        {
            var tabs = MakeElement("vfx-tabs");
            tabs.Add(MakeTab("props", "Properties", _params.Count(p => !p.IsStruct)));
            tabs.Add(MakeTab("play", "Playback", -1));
            tabs.Add(MakeTab("debug", "Debug", -1));
            return tabs;
        }

        Button MakeTab(string id, string label, int count)
        {
            // Use child Labels (not the Button's intrinsic text) so the label and the
            // count badge flow as flex items left-to-right instead of overlapping.
            var tab = new Button(() => { _tab = id; _state.Tab = id; Rebuild(); });
            tab.AddToClassList("vfx-tab");
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

        void BuildPropertiesTab(VisualElement body)
        {
            BuildCategoryColorMap();

            // sub bar: search + filter chips (persist while typing in the search)
            var subbar = MakeElement("vfx-subbar");

            var search = new ToolbarSearchField();
            search.AddToClassList("vfx-search");
            search.placeholderText = "Search Properties…";
            search.value = _search;
            search.RegisterValueChangedCallback(e =>
            {
                _search = e.newValue ?? "";
                _state.Search = _search;
                PopulateList(); // only rebuild the list, keeping the search field focused
            });
            subbar.Add(search);

            int leafCount = _params.Count(p => !p.IsStruct);
            int favCount = _params.Count(p => !p.IsStruct && _favorites.Contains(p.Name));
            int modCount = VfxPropertySheet.CountModified(_so, _params);

            var chips = MakeElement("vfx-filterchips");
            chips.Add(MakeChip("all", $"All", leafCount));
            chips.Add(MakeChip("fav", "★", favCount));
            chips.Add(MakeChip("mod", "Modified", modCount));
            subbar.Add(chips);
            body.Add(subbar);

            // horizontal category rail (wheel scrolls horizontally)
            body.Add(BuildCategoryRail());

            // filtered list lives in its own container so search typing can rebuild
            // just this part without recreating (and unfocusing) the search field.
            _listContainer = new VisualElement();
            body.Add(_listContainer);
            PopulateList();
        }

        void PopulateList()
        {
            if (_listContainer == null) return;
            _listContainer.Clear();
            _refreshers.Clear(); // the controls we're about to discard are gone
            _structHeaders.Clear();
            BuildStructLeavesMap();

            int favCount = _params.Count(p => !p.IsStruct && _favorites.Contains(p.Name));
            bool forceOpen = !string.IsNullOrEmpty(_search.Trim());

            bool showTray = _category == "all" && _filter == "all" &&
                            string.IsNullOrEmpty(_search.Trim()) && favCount > 0;
            if (showTray)
                _listContainer.Add(BuildPinnedTray());

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
                _listContainer.Add(BuildGroup(cat, display, forceOpen, gate));
            }

            if (shownLeaves == 0)
            {
                var empty = new Label(EmptyMessage());
                empty.AddToClassList("vfx-empty");
                _listContainer.Add(empty);
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

        // ------------------------------------------------------------------ playback tab

        void BuildPlaybackTab(VisualElement body)
        {
            var section = MakeElement("vfx-meta");

            var row = MakeElement("vfx-meta-row");
            var label = new Label("Duration (s)") { tooltip = "Length of the play/scrub timeline window before it loops." };
            label.AddToClassList("vfx-mlabel");
            row.Add(label);

            var field = new FloatField { value = _duration };
            field.AddToClassList("vfx-meta-field");
            field.RegisterValueChangedCallback(e =>
            {
                _duration = Mathf.Max(0.1f, e.newValue);
                VfxControlState.SetTimelineDuration(_duration);
                field.SetValueWithoutNotify(_duration);
                UpdateLive();
            });
            row.Add(field);
            section.Add(row);
            body.Add(section);

            BuildPlaceholder(body, "More playback controls coming in the next pass.\nTransport, options, send-event.");
        }

        string EmptyMessage()
        {
            if (_filter == "mod") return "Nothing edited yet — all properties match the graph defaults.";
            if (_filter == "fav") return "No pinned properties. Hover a row and tap ★ to pin it here.";
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
            if (_category != "all" && CategoryOf(p) != _category) return false;
            if (_filter == "fav" && !_favorites.Contains(p.Name)) return false;
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

        VisualElement BuildCategoryRail()
        {
            var rail = new ScrollView(ScrollViewMode.Horizontal);
            rail.AddToClassList("vfx-hrail");
            rail.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            rail.verticalScrollerVisibility = ScrollerVisibility.Hidden;

            rail.Add(MakeRailButton("all", "All", default, true));

            var cats = new List<string>();
            foreach (var p in _params)
            {
                var cat = string.IsNullOrEmpty(p.Category) ? "Uncategorized" : p.Category;
                if (!cats.Contains(cat)) cats.Add(cat);
            }
            foreach (var cat in cats)
                rail.Add(MakeRailButton(cat, cat, GetCategoryColor(cat), false));

            // vertical wheel scrolls horizontally when overflowing
            rail.RegisterCallback<WheelEvent>(e =>
            {
                float content = rail.contentContainer.layout.width;
                if (content <= rail.layout.width) return;
                float d = Mathf.Abs(e.delta.x) > Mathf.Abs(e.delta.y) ? e.delta.x : e.delta.y;
                if (Mathf.Approximately(d, 0)) return;
                rail.scrollOffset = new Vector2(rail.scrollOffset.x + d * 18f, rail.scrollOffset.y);
                e.StopPropagation();
            });
            return rail;
        }

        Button MakeRailButton(string id, string label, Color dot, bool isAll)
        {
            var btn = new Button(() =>
            {
                _category = (_category == id) ? "all" : id;
                _state.Category = _category;
                RebuildBodyOnly();
            });
            btn.AddToClassList("vfx-hrail-btn");
            if (_category == id) btn.AddToClassList("vfx-hrail-btn--active");
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

        VisualElement BuildPinnedTray()
        {
            var fav = _params.Where(p => _favorites.Contains(p.Name)).ToList();
            var foldout = new Foldout { text = $"  ★ Pinned ({fav.Count})", value = true };
            foldout.AddToClassList("vfx-pinned");

            var grid = MakeElement("vfx-pinned-grid");
            foreach (var p in fav)
            {
                var card = MakeElement("vfx-pincard");
                UpdateRowModifiedClass(card, p); // reuses the modified marker for reset visibility
                var top = MakeElement("vfx-pincard-top");
                var dot = MakeElement("vfx-pincard-dot");
                dot.style.backgroundColor = GetCategoryColor(string.IsNullOrEmpty(p.Category) ? "Uncategorized" : p.Category);
                top.Add(dot);
                var lbl = new Label(p.Label) { tooltip = p.Tooltip };
                lbl.AddToClassList("vfx-pincard-label");
                AddCopyPasteMenu(lbl, p);
                top.Add(lbl);
                var reset = MakeIconButton("↺", "Reset to graph default", () =>
                {
                    ResetAll(p);
                    RebuildBodyOnly();
                });
                reset.AddToClassList("vfx-tool-reset"); // hover/modified-gated via CSS
                top.Add(reset);
                var unpin = MakeIconButton("★", "Unpin", () => ToggleFavorite(p));
                top.Add(unpin);
                card.Add(top);
                var control = BuildControl(p, card); // pass the card so its modified marker stays in sync
                AttachLabelDragger(lbl, control); // drag the card label to scrub numeric values
                card.Add(control);
                grid.Add(card);
            }
            foldout.Add(grid);
            return foldout;
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
            if (leaves.Any(c => _favorites.Contains(c.Name))) header.AddToClassList("vfx-row--fav");
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

            bool allFav = leaves.Count > 0 && leaves.All(c => _favorites.Contains(c.Name));
            var starAll = MakeIconButton(allFav ? "★" : "☆", allFav ? "Unpin all components" : "Pin all components", () =>
            {
                foreach (var c in leaves)
                    if (allFav) _favorites.Remove(c.Name); else _favorites.Add(c.Name);
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
            if (comps.Any(c => _favorites.Contains(c.Name))) row.AddToClassList("vfx-row--fav");
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
            if (_favorites.Contains(p.Name)) row.AddToClassList("vfx-row--fav");

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
            var star = MakeIconButton(_favorites.Contains(p.Name) ? "★" : "☆", "Pin to favorites", () => ToggleFavorite(p));
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

            _resetAllBtn = new Button(() =>
            {
                foreach (var p in _params)
                    if (VfxPropertySheet.IsOverridden(_so, p))
                        ResetAll(p);
                RebuildBodyOnly();
            }) { text = "Reset all" };
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

        void UpdateFooter()
        {
            if (_footNote == null || _so == null) return;
            int mod = VfxPropertySheet.CountModified(_so, _params);
            uint seed = _effect != null ? _effect.startSeed : 0;
            _footNote.text = (mod > 0 ? $"{mod} edited" : "No overrides") + $" · seed {seed}";
            _resetAllBtn?.SetEnabled(mod > 0);
        }

        // ------------------------------------------------------------------ helpers

        void RebuildBodyOnly() => Rebuild(); // simple + robust; preserves no transient focus

        void ToggleFavorite(VfxExposedParam p)
        {
            if (!_favorites.Remove(p.Name)) _favorites.Add(p.Name);
            _state.SaveFavorites(_favorites);
            RebuildBodyOnly();
        }

        // ~30fps clock: advances the scrub bar in real time while playing, looping
        // (and resetting the sim) at the end of the configured window.
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
                    _scrubT = 0f;
                    _effect.Reinit(); // reset playback at the end of the window
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
