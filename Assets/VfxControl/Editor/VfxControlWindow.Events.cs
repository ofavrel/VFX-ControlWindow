// VFX Control — Send Event + event payload editor (partial of VfxControlWindow).
//
// The Playback tab's "Send Event" section: quick-chips (OnPlay/OnStop + every graph Event
// block) and a reorderable payload editor (name · type · value rows over a VFXEventAttribute,
// modelled on the package's Event Tester). Built-in, graph-custom, and free-custom row kinds;
// payload scoped per asset and persisted in SessionState. Split out of VfxControlWindow.cs —
// same class (partial), shared private state.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public partial class VfxControlWindow : EditorWindow
    {
        // Favorite key for the whole Send Event section (it's an action surface, not a per-row
        // setting, so it pins as one unit into the Favorites group).
        private const string kSendEventFavKey = "play:sendevent";

        // "Send Event": a collapsible section group (same .vfx-group chrome as "Playback options"
        // / the renderer sections), containing the quick-chips — OnPlay/OnStop + every custom Event
        // block in the graph. Its header carries a ★ pin (favorite) like a row. Returns 1.
        private int AddSendEventSection(VisualElement host)
        {
            var (header, content, _) = AddGroupShell(host, "play:events", "Send Event");
            // ★ pin: toggles the section's favorite (StopPropagation so it doesn't also collapse).
            var star = MakeIconButton(IsFav(kSendEventFavKey) ? "★" : "☆",
                IsFav(kSendEventFavKey) ? "Unpin from Favorites" : "Pin to Favorites", () => ToggleFav(kSendEventFavKey));
            star.AddToClassList("vfx-group-pin");
            star.EnableInClassList("vfx-group-pin--on", IsFav(kSendEventFavKey));
            star.RegisterCallback<ClickEvent>(e => e.StopPropagation());
            header.Add(star);

            // event buttons on top, sitting on a dark band (like the rail / section bands)
            var band = MakeElement("vfx-sendevent-band");
            band.Add(BuildEventChips());
            content.Add(band);
            content.Add(BuildEventPayloadEditor());    // payload list below
            return 1;
        }

        // Event-payload editor: a reorderable ListView of named/typed attributes (name · type ·
        // value) with the **standard +/- footer** — the + opens the Built-in/Custom add menu, the -
        // deletes the selected row. Attributes are attached to whatever event a chip sends (via
        // VFXEventAttribute). Editing a value mutates the model in place; reorder mutates the list
        // order in place (cosmetic — the payload is keyed by name); add/remove/type/name-swap
        // rebuild the body.
        private const float kPayloadRowHeight = 24f;
        private const float kPayloadHeaderHeight = 24f;
        private const float kPayloadFooterHeight = 26f;
        private const float kPayloadChrome = 8f;    // border/padding slack so the last row isn't clipped
        private const int kPayloadMaxRows = 12;     // cap the visible list height

        private VisualElement BuildEventPayloadEditor()
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
        private VisualElement BuildPayloadRow(EventAttr a)
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
        private static string AttrTypeLabel(EventAttrType t)
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
        private static readonly List<EventAttrType> s_AttrTypes = new List<EventAttrType>
        {
            EventAttrType.Float, EventAttrType.Vector2, EventAttrType.Vector3, EventAttrType.Vector4,
            EventAttrType.Bool, EventAttrType.Uint, EventAttrType.Int,
        };

        // Type control = the VFX-Graph blackboard **type icon** (Float/Vector2-4/Boolean/Integer).
        // Editable (custom) → a button that opens a type menu; non-editable (built-in) → a grayed,
        // disabled icon holder. Replaces the text PopupField.
        private VisualElement MakeAttrTypeControl(EventAttr a, bool editable)
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
        private void ShowTypeMenu(EventAttr a)
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
        private static Texture2D AttrTypeIcon(EventAttrType t)
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
        private VisualElement BuildAttrValueControl(EventAttr a)
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

        private static object DefaultAttrValue(EventAttrType t)
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
        private static object StdDefault(StdAttr s) => s.Name == "color" ? (object)Vector3.one : DefaultAttrValue(s.Type);

        // Populate a menu with every standard attribute under `root`, grouped by a grayed section
        // header (`AddDisabledItem`) + `AddSeparator` between groups, alphabetical within each
        // section. `checkedName` shows the radio dot on the current pick. Shared by the "+ Attribute"
        // add menu and the built-in row's name-swap menu.
        private void AddStdAttrMenuItems(GenericMenu menu, string root, string checkedName, Action<StdAttr> onPick)
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
        private void ShowAddAttributeMenu()
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
        private Button MakeNameDropdown(string current, string tooltip, Action onClick, bool warn = false)
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
        private void ShowBuiltinNameMenu(EventAttr a)
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
        private void ShowGraphCustomNameMenu(EventAttr a)
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
        private VisualElement BuildEventChips()
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
        private VisualElement BuildSendEventFavRow()
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
        private List<string> EventChipNames()
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
        private void SendEventToAll(string eventName)
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
        [Serializable]
        private struct EventAttrDTO { public string name; public int type; public bool builtIn; public bool graphCustom; public Vector4 vec; public bool boolVal; public int intVal; }
        [Serializable]
        private class AssetPayloadDTO { public string guid; public List<EventAttrDTO> items = new List<EventAttrDTO>(); }
        [Serializable]
        private class PayloadStoreDTO { public List<AssetPayloadDTO> assets = new List<AssetPayloadDTO>(); }

        private static EventAttrDTO ToDTO(EventAttr a)
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

        private static EventAttr FromDTO(EventAttrDTO d)
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

        private void SavePayloads()
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

        private void LoadPayloads()
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
        // Event payload (Send Event section): a list of named/typed attributes attached to the
        // event via VFXEventAttribute (modelled on the package's VFX Event Tester overlay). Lives
        // for the window session; survives body rebuilds (not a per-populate list).
        // Enum order = the Custom type dropdown order (Float, V2, V3, V4, Bool, Uint, Int).
        private enum EventAttrType { Float, Vector2, Vector3, Vector4, Bool, Uint, Int }
        // BuiltIn = a standard attribute (name picked from a fixed dropdown, type fixed); otherwise
        // a custom attribute (name + type freely edited).
        // BuiltIn = standard attribute (name picked from the standard list, type fixed).
        // GraphCustom = a custom attribute declared in the graph's blackboard (name picked from the
        // graph's list, type fixed). Neither → a free custom attribute (name + type freely edited).
        private sealed class EventAttr { public string Name; public EventAttrType Type; public object Value; public bool BuiltIn; public bool GraphCustom; }
        // Payload is scoped per VFX asset: _payloadByAsset[guid] holds each asset's rows; _eventPayload
        // points at the active asset's list (swapped in SetTarget). Persisted in SessionState (survives
        // domain reload, cleared on editor restart) via Save/LoadPayloads.
        private readonly Dictionary<string, List<EventAttr>> _payloadByAsset = new Dictionary<string, List<EventAttr>>();
        private List<EventAttr> _eventPayload = new List<EventAttr>();

        private const string kPayloadSessionKey = "vfxctrl.payloads";
        // The graph's current blackboard custom attributes (name → type), refreshed each time the
        // payload editor builds; used to flag stale GraphCustom rows (renamed/retyped/deleted).
        private readonly Dictionary<string, EventAttrType> _graphCustomLookup = new Dictionary<string, EventAttrType>(StringComparer.OrdinalIgnoreCase);

        // The standard attributes offered for built-in payload entries — name, type, and section,
        // restricted to the three settable sections (Basic/Advanced Simulation, Rendering; the
        // System/Collision/Strip categories are read-only outputs). Types from VFXAttributesManager;
        // grouping/ordering from the manual's Reference-Attributes page.
        private struct StdAttr { public string Name; public EventAttrType Type; public string Section; }

        private static readonly StdAttr[] s_StdAttrs =
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

        private static readonly string[] s_StdSections = { "Basic Simulation", "Advanced Simulation", "Rendering" };
    }
}
