// VFX Control — Properties tab (partial of VfxControlWindow).
//
// Renders the exposed-parameter list: category groups, struct cards (flatten/inline/card
// layouts), typed value controls bound through the property sheet, per-row reset/favorite/
// copy-paste, the constrain-proportions lock, category accent colors, the category enable
// gate, and the read-only space icon. The All tab reuses PopulateProperties. Split out of
// VfxControlWindow.cs — same class (partial), shared private state.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public partial class VfxControlWindow : EditorWindow
    {
        // property name -> actions that re-read the value into each control showing it,
        // so a pinned card and its category row (etc.) stay in sync after any edit.
        private readonly Dictionary<string, List<Action>> _refreshers = new Dictionary<string, List<Action>>();

        // category name -> accent color, assigned distinctly in order of appearance.
        private readonly Dictionary<string, Color> _categoryColors = new Dictionary<string, Color>();

        // struct parent -> its descendant leaf properties (for pin-all / reset-all).
        private readonly Dictionary<VfxExposedParam, List<VfxExposedParam>> _structLeaves =
            new Dictionary<VfxExposedParam, List<VfxExposedParam>>();

        // struct header rows + their leaves, so a child edit can re-bold/aggregate live.
        private readonly List<(VisualElement header, List<VfxExposedParam> leaves)> _structHeaders =
            new List<(VisualElement, List<VfxExposedParam>)>();

        // single-element structs (e.g. spaceable Position/Direction) -> their one leaf;
        // these render as a normal row (label + control + space) instead of a card.
        private readonly Dictionary<VfxExposedParam, VfxExposedParam> _flattenChild =
            new Dictionary<VfxExposedParam, VfxExposedParam>();

        // scalar-only structs (e.g. Flipbook X/Y) -> their leaves; rendered inline on
        // one row like a Vector2/3/4 instead of a multi-row card.
        private readonly Dictionary<VfxExposedParam, List<VfxExposedParam>> _inlineStruct =
            new Dictionary<VfxExposedParam, List<VfxExposedParam>>();
        // Fill `container` with the filtered pinned tray + category groups. `showEmpty`
        // suppresses the "nothing matches" note when stacked under other blocks (All tab).
        // Category groups for the Properties content. The Favorites group is added separately
        // by the tab builder (AddFavoriteGroup), so this is purely the categorized list.
        private void PopulateProperties(VisualElement container, bool showEmpty = true)
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

        private string CategoryOf(VfxExposedParam p) => string.IsNullOrEmpty(p.Category) ? "Uncategorized" : p.Category;

        // For each struct parent, collect its descendant leaf properties (entries that
        // follow it with greater depth), used by the header's pin-all / reset-all.
        // Classify struct rendering (delegated to the pure VfxPropertyLayout.ClassifyStructs), then
        // copy into the live dicts the body reads.
        private void BuildStructLeavesMap()
        {
            var m = VfxPropertyLayout.ClassifyStructs(_params);
            _structLeaves.Clear();
            foreach (var kv in m.Leaves) _structLeaves[kv.Key] = kv.Value;
            _flattenChild.Clear();
            foreach (var kv in m.FlattenChild) _flattenChild[kv.Key] = kv.Value;
            _inlineStruct.Clear();
            foreach (var kv in m.InlineStruct) _inlineStruct[kv.Key] = kv.Value;
        }

        // Ordered entries to display: visible leaves plus any struct parent that has a
        // visible descendant (so a shown child still appears under its struct label).
        private List<VfxExposedParam> ComputeDisplay(List<VfxExposedParam> entries)
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
        private List<VfxExposedParam> ComputeFavoriteDisplay()
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

        private VisualElement BuildGroup(string category, List<VfxExposedParam> props, bool forceOpen, VfxExposedParam gate = null)
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
            var titleLabel = new Label(category);
            titleLabel.AddToClassList("vfx-group-title");
            header.Add(titleLabel);
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
        private void ApplyCategoryGate(VisualElement group, VisualElement content, VfxExposedParam gate)
        {
            bool off = VfxPropertySheet.GetValue(_so, gate) is bool b && !b && !IsMixed(gate);
            content.SetEnabled(!off);                         // native disabled tint + blocks input
            group.EnableInClassList("vfx-group--gated", off); // dim the header (reads even when collapsed)
        }

        // Auto-detect a category's enable gate: a top-level bool leaf whose label matches
        // the category, or is "Enable <Category>" / "Use <Category>" (case/space-insensitive).
        private VfxExposedParam FindCategoryGate(string category, List<VfxExposedParam> props)
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

        private static string NormGate(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace(" ", "").Replace("_", "").ToLowerInvariant();

        // Render the ordered (already-filtered) entries, nesting struct children inside
        // their collapsible struct parent so depth maps to real containment.
        private void AddDisplayEntries(VisualElement parent, List<VfxExposedParam> entries, bool forceOpen)
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

        private string StructKey(VfxExposedParam p) => "struct:" + p.Name;

        private void ApplyCollapse(VfxExposedParam structParam, bool collapse)
        {
            if (collapse) _collapsed.Add(StructKey(structParam));
            else _collapsed.Remove(StructKey(structParam));
        }

        // All struct entries nested under a given struct (for Alt+click recurse-all).
        private IEnumerable<VfxExposedParam> DescendantStructs(VfxExposedParam p)
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
        private IEnumerable<string> PropertyCollapseKeys()
        {
            var seenCat = new HashSet<string>();
            foreach (var p in _params)
            {
                if (p.IsStruct) yield return StructKey(p);
                var c = CategoryOf(p);
                if (seenCat.Add(c)) yield return c;
            }
        }

        private static readonly string[] PlaybackCollapseKeys = { "play:options", "play:events" };
        private static readonly string[] RendererCollapseKeys = { "render:probes", "render:additional" };
        private static readonly string[] DebugCollapseKeys = { "debug:live", "debug:systems", "debug:textures", "debug:particles", "debug:visualizers" };

        // The collapsible keys inside one All-tab section ("Properties"/"Playback"/"Renderer").
        private IEnumerable<string> AllSectionCollapseKeys(string heading)
        {
            switch (heading)
            {
                case "Properties": return PropertyCollapseKeys();
                case "Playback": return PlaybackCollapseKeys;
                case "Renderer": return RendererCollapseKeys;
                default: return Enumerable.Empty<string>();
            }
        }

        // Every collapsible key inside a tab's body (for Alt+click on the tab). The All tab
        // also includes its own top-level section headers so the whole tree folds at once.
        private IEnumerable<string> TabCollapseKeys(string tabId)
        {
            switch (tabId)
            {
                case "props": return PropertyCollapseKeys();
                case "play": return PlaybackCollapseKeys;
                case "render": return RendererCollapseKeys;
                case "debug": return DebugCollapseKeys;
                case "all": return new[] { "all:Properties", "all:Playback", "all:Renderer" } // Debug is a jump-link, not a foldable section
                    .Concat(PropertyCollapseKeys()).Concat(PlaybackCollapseKeys).Concat(RendererCollapseKeys);
                default: return Enumerable.Empty<string>();
            }
        }

        private void SetCollapsedAll(IEnumerable<string> keys, bool collapse)
        {
            foreach (var k in keys)
                if (collapse) _collapsed.Add(k); else _collapsed.Remove(k);
        }

        // A compound parent (e.g. AABox): a collapsible header with pin-all / reset-all
        // acting on every component; children are added into `content` by the caller.
        private VisualElement BuildStructGroup(VfxExposedParam p, VisualElement content, bool forceOpen)
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
        private VisualElement BuildBulkTools(List<VfxExposedParam> leaves)
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
        private VisualElement BuildInlineStructRow(VfxExposedParam p, List<VfxExposedParam> comps)
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
        private VisualElement BuildRow(VfxExposedParam p, string labelText = null, VfxExposedParam spaceFrom = null)
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

        private void UpdateRowModifiedClass(VisualElement row, VfxExposedParam p)
        {
            row.EnableInClassList("vfx-row--modified", VfxPropertySheet.IsOverridden(_so, p));
        }

        // A small chain-link toggle (like the Transform scale lock): when on, editing
        // one component scales the others proportionally.
        private VisualElement BuildConstrainToggle(VfxExposedParam p)
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
        private static void AttachLabelDragger(Label label, VisualElement control)
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
        private VisualElement BuildControl(VfxExposedParam p, VisualElement row)
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
                    return Bind(new Vector2Field(), p, row, v => v is Vector2 x ? x : Vector2.zero, v => v, VfxConstrain.Vec2);

                case "m_Vector3f":
                    return Bind(new Vector3Field(), p, row, v => v is Vector3 x ? x : Vector3.zero, v => v, VfxConstrain.Vec3);

                case "m_Vector4f":
                    return p.RealType == "Color"
                        ? Bind(new ColorField { hdr = true, showAlpha = true }, p, row,
                               v => v is Color c ? c : (v is Vector4 v4 ? (Color)v4 : Color.white), v => v)
                        : Bind(new Vector4Field(), p, row, v => v is Vector4 x ? x : Vector4.zero, v => v, VfxConstrain.Vec4);

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
        private BaseField<T> Bind<T>(BaseField<T> field, VfxExposedParam p, VisualElement row,
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

        private bool IsMultiComponent(VfxExposedParam p) =>
            p.SheetType == "m_Vector2f" || p.SheetType == "m_Vector3f" ||
            (p.SheetType == "m_Vector4f" && p.RealType != "Color");

        private bool IsConstrained(VfxExposedParam p) => _constrained.Contains(p.Name);

        private void ToggleConstrain(VfxExposedParam p)
        {
            if (!_constrained.Remove(p.Name)) _constrained.Add(p.Name);
            _state.SaveConstrained(_constrained);
            RebuildBodyOnly();
        }

        // ---- copy / paste (interops with the Inspector via UnityEditor.Clipboard) ----

        private static bool IsCopyPasteSupported(VfxExposedParam p)
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

        private void AddCopyPasteMenu(VisualElement target, VfxExposedParam p)
        {
            if (!IsCopyPasteSupported(p)) return;
            target.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Copy", _ => CopyValue(p));
                evt.menu.AppendAction("Paste", _ => PasteValue(p),
                    CanPaste(p) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            }));
        }

        private void CopyValue(VfxExposedParam p)
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

        private bool CanPaste(VfxExposedParam p)
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

        private void PasteValue(VfxExposedParam p)
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

        private void RegisterRefresher(string name, Action refresh)
        {
            if (!_refreshers.TryGetValue(name, out var list))
                _refreshers[name] = list = new List<Action>();
            list.Add(refresh);
        }
        // Re-sync every control bound to this property (and the footer) after an edit.
        private void RefreshProperty(VfxExposedParam p)
        {
            if (_refreshers.TryGetValue(p.Name, out var list))
                foreach (var refresh in list) refresh();
            // re-aggregate struct headers (bold + reset-all visibility) for live updates
            foreach (var (header, leaves) in _structHeaders)
                header.EnableInClassList("vfx-row--modified", leaves.Any(c => VfxPropertySheet.IsOverridden(_so, c)));
            UpdateFooter();
        }
        // Assign each category its accent-dot color (delegated to VfxPropertyLayout.AssignCategoryColors,
        // keyword palette else distinct fallback), cached per build. Empty category → "Uncategorized".
        private void BuildCategoryColorMap()
        {
            _categoryColors.Clear();
            foreach (var kv in VfxPropertyLayout.AssignCategoryColors(
                         _params.Select(p => string.IsNullOrEmpty(p.Category) ? "Uncategorized" : p.Category)))
                _categoryColors[kv.Key] = kv.Value;
        }

        private Color GetCategoryColor(string category)
        {
            if (_categoryColors.Count == 0) BuildCategoryColorMap();
            return _categoryColors.TryGetValue(category, out var c) ? c : VfxPropertyLayout.DefaultDotColor;
        }
        // ---- spaceable property space icon (display only) ----

        private static readonly Dictionary<string, Texture2D> s_SpaceIcons = new Dictionary<string, Texture2D>();

        private static Texture2D LoadSpaceTexture(string space)
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
        private VisualElement BuildSpaceIcon(VfxExposedParam p)
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
