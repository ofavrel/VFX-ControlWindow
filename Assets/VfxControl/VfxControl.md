# VFX Control — project context

A custom Unity Editor tool that augments the stock `VisualEffect` inspector with a
denser, more controllable UI (the "Bold"/Variant C design from
`Docs/design_handoff_vfx_inspector/README.md`). It's a **dockable EditorWindow**, not
a `[CustomEditor]`, to avoid conflicting with the VFX package's own
`AdvancedVisualEffectEditor`.

- Unity **6000.6.0a2**, `com.unity.visualeffectgraph` **17.6.0**.th
- Open via **`Window ▸ VFX Control`**. Diagnostic: **`Tools ▸ VFX Control ▸ Diagnose Target`**
  (logs how the selected/target VFX's exposed properties enumerate — keep for debugging).
- All code is editor-only under `Assets/VfxControl/Editor/` (no asmdef → compiles into
  `Assembly-CSharp-Editor`, which references the VFX runtime + editor assemblies).

## Files

- **`VfxControlWindow.cs`** — the window. Selection tracking, header,
  mini-transport + playback clock, tabs, per-tab Favorites group, Properties tab
  (search/chips/rail/groups/struct cards/rows), Renderer tab (sibling VFXRenderer settings), footer,
  copy-paste, scene-view gizmos, multi-edit.
- **`VfxGraphReflection.cs`** — reflection bridge to the editor-internal VFX graph;
  `GetExposedParameters(asset)` → `List<VfxExposedParam>`, `GetEventNames(asset)` →
  custom Event-block names (`VFXBasicEvent.eventName` via `VFXGraph.children`), and
  `GetCustomAttributes(asset)` → the blackboard's custom attributes (`VFXGraph.customAttributes`).
- **`VfxPropertySheet.cs`** — read/write the component's `m_PropertySheet` via
  `SerializedObject` (undo/prefab/multi-edit safe).
- **`VfxControlState.cs`** — persistence: favorites/collapsed/constrained per asset GUID
  (`EditorPrefs`); tab/filter/category/search (`SessionState`); global timeline duration.
- **`VfxClipboard.cs`** — reflection wrapper over internal `UnityEditor.Clipboard` for
  Inspector-interop copy/paste.
- **`VfxControl.uss`** — styling, bound to built-in `--unity-*` theme variables (only the
  category accent dots are a custom palette, set inline from C#).

## Ground truth (verified against the VFX package source — do NOT re-guess)

Package source lives in `Library/PackageCache/com.unity.visualeffectgraph@*/Editor/`.
The whole graph/gizmo/types layer is **internal** to `Unity.VisualEffectGraph.Editor`,
so everything below is reached by **reflection** (see `VfxGraphReflection`/`VfxClipboard`)
and degrades gracefully (empty list / no-op) if a member shifts.

- **Exposed properties** come from `VFXGraph.m_ParameterInfo` (`VFXParameterInfo[]`),
  reached via `VisualEffectAsset.GetResource()` → `.GetOrCreateGraph()`
  (extension methods in `VisualEffectResourceExtensions`, *in* the package assembly;
  `GetOrCreateGraph` matched by name + arity + return type == `VFXGraph` — do NOT try to
  resolve `VisualEffectResource`, it's a built-in type and the lookup will fail/empty).
  Match `BuildParameterInfo()` (parameterless) and `VFXSerializableObject.Get()` with a
  LINQ "non-generic, zero-arg" lookup — `Type.GetMethod(..., Type.EmptyTypes)` throws
  `AmbiguousMatchException` on `Get()` vs `Get<T>()`.
- **`VFXParameterInfo` fields** (all read by reflection): `name`, `path`, `sheetType`,
  `realType`, `tooltip`, `min`, `max`, `enumValues`, `descendantCount`, `defaultValue`
  (`VFXSerializableObject.Get()`), `space` (`VFXSpace`), `spaceable`.
- **Flattened array → tree**: walk with a descendant-count **stack** (mirrors
  `VisualEffectEditor.DrawParameters`). A **category header** = empty `sheetType` AND empty
  `realType`. A **struct parent** (e.g. `AABox`) = empty `sheetType` + non-empty `realType`
  + `descendantCount > 0`; its children follow at greater depth. `descendantCount` is the
  number of **direct** children (not total flattened size).
- **`sheetType` → field name**: `m_Float`, `m_Int`, `m_Uint`, `m_Bool`,
  `m_Vector2f/3f/4f`, **Color→`m_Vector4f`**, `m_AnimationCurve`, `m_Gradient`,
  **Texture+Mesh→`m_NamedObject`**, `m_Matrix4x4f`.
- **Override sheet**: `m_PropertySheet.<sheetType>.m_Array`, each element
  `{ m_Name, m_Value, m_Overridden }`. "Modified" == an entry exists with
  `m_Overridden == true`; "reset" == clear the override. The entry key is the param's
  **`path`** (== name for top-level). Read/write `m_Value` by `propertyType` (mirrors
  `VisualEffectEditor.Get/SetObjectValue`); `m_NamedObject.m_Value` is an ObjectReference.
- **Space**: `VFXSpace` (`None`/`Local`/`World`; serialized `-1` = None). Spaceable
  space icons ship at `Packages/com.unity.visualeffectgraph/Editor/UIResources/VFX/`
  (`WorldSpace`/`LocalSpace`/`NoneSpace`, with `d_` dark + `@2x` HiDPI variants).
- **Event names**: custom events are `VFXBasicEvent` contexts in the graph — `eventName`
  (public string, default `OnPlay`). Enumerate by iterating `VFXGraph.children` (the `new`-hidden
  `VFXModel.children` property) and filtering to `VFXBasicEvent`; the built-in defaults are
  `VisualEffectAsset.PlayEventName`/`StopEventName` (= `OnPlay`/`OnStop`). The Component Board's
  `RecurseGetEventNames` also recurses `VFXSubgraphContext.subChildren` — we don't yet.
- **Custom attributes** (blackboard): `VFXGraph.customAttributes` → `VFXCustomAttributeDescriptor`s,
  each with `attributeName` (string) + `type` (`CustomAttributeUtility.Signature`). The Signature
  enum order is **exactly** our payload type order — `Float,Vector2,Vector3,Vector4,Bool,Uint,Int`
  (0..6) — so the ordinal maps 1:1 (`GetCustomAttributes` returns `(name, ordinal)`).
- **Type icons**: the blackboard's per-type icons live at
  `Packages/com.unity.visualeffectgraph/Editor/UIResources/VFX/types/<Name>@2x.png` —
  `Float`/`Vector2`/`Vector3`/`Vector4`/`Boolean`/`Integer` (+ `d_` dark variants; only `@2x` exists).
  Load via `AssetDatabase.LoadAssetAtPath<Texture2D>` (the path uses the package name, not the hashed
  PackageCache folder). Used for the event-payload type column (`AttrTypeIcon`).

## VfxExposedParam model

`Name` (path / sheet key + favorites/constrained/refresher key), `Label` (nicified field
name for display, bold/`<b>` when used as a header), `SheetType`, `RealType`, `Category`,
`Tooltip`, `IsStruct`, `Depth`, `Spaceable`, `Space`, `HasRange`/`Min`/`Max`,
`EnumValues`, `DefaultValue`.

## UI structure & rendering

- **Header** → **Asset** row (Initial Event moved to the Playback tab) → **mini-transport** → recessed divider →
  **chrome** (search + filter chips, shared across tabs) → **tabs** (All/Properties/
  Playback/Debug/Renderer) → **section rail** (per-tab) → body → **footer**
  (`{n} edited · seed {n}` + Reset all). The window is **selection-driven** like an
  inspector: `RefreshTarget` mirrors the current scene selection (single or multi-VFX
  sharing one asset) and drops the target when the selection isn't an editable scene
  VFX — it never edits an asset/prefab-asset (guarded by `EditorUtility.IsPersistent`).
  There is no manual target field.
- **Tabs / chrome / rail architecture**: each tab is a `TabDef` (id/label/badge/`HasRail`/
  `Sections`/`Build`), assembled by `BuildTabDefs`. `Rebuild` builds the chrome **once**
  (search field kept in `_searchField` so typing never loses focus) plus persistent
  `_tabsHost`/`_chipsHost`/`_railContainer`/`_tabBody`; every search/chip/rail/tab/favorite/
  reset interaction routes through **`PopulateActiveTab`** (= the new `RebuildBodyOnly`),
  which repopulates only tabs+chips+rail+body. **Search filters the active tab only**
  (`SearchMatches` for IMGUI/meta fields, `Visible` for properties). The **section rail**
  generalizes the old category rail: Properties→categories, Renderer→Probes/Additional,
  Playback→"Playback options"/"Send Event", Debug→just "All" for now; selection is **per-tab** in `_sections` (packed into
  `VfxControlState.Sections`, migrating the legacy `Category`). `CurrentSection()` returns
  "all" for tabs without a rail. The **All tab** (default) is a traditional inspector:
  Properties+Renderer+Playback stacked with no rail (`BuildAllTab`), each under a **collapsible**
  top-level header (`AddAllSection`, `.vfx-allsection-head` + `-title`/`-twirl`, collapse key
  `all:<title>`) that reads above the boxed category headers below it.
- **Renderer tab**: the `VisualEffect` renders through a sibling **`VFXRenderer`**; this
  tab exposes its settings (the stock inspector's "Renderer" section) — Probes (Reflection
  Probes, Light Probes + Proxy Volume Override + Anchor Override) and Additional Settings
  (Rendering Layer Mask, Priority, Sorting Layer/Order). **Built as UIToolkit `.vfx-row`s**
  (no IMGUI as of Phase 3a) sharing the property tab's row chrome, in collapsible section
  groups (`AddRendererSection`, `render:<id>` collapse keys). `ObjectField` (proxy/anchor) and
  `IntegerField` (priority/order) bind to a multi-renderer `SerializedObject` via `BindProperty`
  (undo + multi-edit *mixed values* + prefab bars for free); int `IntegerField`s also get the
  property-row `AttachLabelDragger` for drag-scrub. The **probe usages** are serialized as plain
  *int* (the stock editor writes `intValue`), so `BindProperty(EnumField)` wouldn't persist —
  they use `MakeRendererEnum<T>` (manual `intValue`+`ApplyModifiedProperties`) and rebuild the
  body on change so **Anchor/Proxy rows** appear/disappear. The two with no stock UIToolkit field
  are hand-built from public SRP APIs so HDRP/URP stay correct: **Rendering Layer Mask** →
  `MaskField` from `RenderingLayerMask.GetDefinedRenderingLayerNames/Values()`; **Sorting Layer**
  → `PopupField<string>` from `SortingLayer.layers` (mapped by `.id`, plus an "Add Sorting Layer…"
  entry → `SettingsService` Tags & Layers). `RefreshRendererState` (via `TrackSerializedObjectValue`
  on a per-build host) keeps modified markers + chip/footer counts live.
- **Properties tab**: filtered by the shared chrome search + chips (All/★/Modified) + the
  category section rail; `PopulateProperties(container, showEmpty)` fills the body (the All
  tab reuses it with `showEmpty:false`). `BuildGroup`
  is a **custom collapsible** (NOT a `Foldout`) so headers use a `ClickEvent`+`altKey`
  path that reliably catches Option/Alt on macOS — **Alt/Option+click = expand/collapse all
  nested** (works on category headers and struct headers).
- **Category enable-gate**: a category whose top-level bool leaf is named like the category
  (or `Enable/Use <Category>`) auto-promotes that bool to a **master enable toggle** in the
  group header (`FindCategoryGate`). When off, `ApplyCategoryGate` greys + locks the body
  (`content.SetEnabled(false)`) and adds `vfx-group--gated` (dims the header); the toggle
  stays live in the header. Re-applies live via a refresher keyed on the bool's `Name`.
  Deactivating also **collapses** the category to hide the now-irrelevant props (and
  activating re-opens it) — but this just drives the normal `_collapsed` state from the
  toggle's value-changed callback, so the header twirl still expands a gated-off category to
  **peek** at its greyed values. Purely a UI affordance — the bool is a normal
  exposed property the author wires to a block's **Activation** port in VFX Graph. Mixed
  multi-edit → treated as enabled (don't hide when ambiguous).
- **Rows**: fixed-width label column (label hugs left, space icon after it), constrain
  lock gutter, control, hover-revealed tools (reset ↺ + favorite ★). Tool visibility is
  CSS-driven by `.vfx-row--modified` / `.vfx-row--fav` (reset shows on hover or when
  modified; favorite on hover or when pinned; both dim-grey until active).
- **Typed controls** via a generic `Bind<T>(field, p, row, toControl, toModel, constrain)`:
  Slider/SliderInt (ranged float/int/uint) or FloatField/IntegerField; Toggle; Vector2/3/4;
  ColorField (hdr); GradientField (`colorSpace = Linear, hdr = true`); CurveField;
  ObjectField; PopupField (enum). `Bind` registers a **refresher** so all controls for a
  property stay in sync (e.g. pinned card vs category row), sets `showMixedValue`, and
  attaches a label `FieldMouseDragger` for scrub on numeric fields.
- **Structs**: a single-element non-spaceable struct flattens to one row; a single-element
  **spaceable** struct (Position/Direction/Vector) renders as a two-row card (header carries
  space icon + gizmo button, value row carries the constrain lock); a **scalar-only** 2–4
  field struct (e.g. Flipbook X/Y) renders inline like a vector; everything else is a
  collapsible **card** (lighter header + slightly-darker content, side+bottom border that
  matches the header fill; header bold only when a child is modified, children dimmer).
  Struct headers carry **reset-all / pin-all** (`BuildBulkTools`).
- **Constrain proportions** lock (chain icon) on multi-component values, like the Transform
  scale lock; derived components round to 2 decimals.
- **Category dot colors**: keyword map (spawn/color/motion/size/texture…) else distinct
  palette by order of appearance (`BuildCategoryColorMap`) — NOT a hash (hashing collapsed
  most names onto one color).

## Scene-view gizmos (custom — VFX's own are internal & unusable)

`SceneView.duringSceneGui`. An "edit in Scene" toggle on spaceable struct headers
(`IsGizmoSupported`: Position, DirectionType, Vector, AABox, Line, Plane + the shape set
`s_ShapeGizmoTypes` = **TCone/TArcCone, TSphere/TArcSphere, TCircle/TArcCircle,
TTorus/TArcTorus, OrientedBox, Transform** — note `realType` is the C# struct name, so
shapes carry the `T` prefix; the shape set is additionally gated on `p.Spaceable` to skip
the inner shape/`transform` nested in another type, which carries no space — see
[[vfx-cone-arccone-layout]]).
Activating unfolds the card (restored to prior fold state on deactivate). State:
`_gizmoStruct` + `_structLeaves`. All four shapes share helpers: `DrawSpaceTransformHandle`
(tool-aware move/rotate/scale in the base frame), `RadialRadiusHandle` (radial cube
slider), and `ArcHandle` (Slider2D arc, `rotation` orients the sweep plane). Each
`Draw*Gizmo` draws the full shape when its arc leaf is absent (so the non-Arc Cone/
Sphere/Circle/Torus variants work with no extra code).

- **Position** → `PositionHandle`. **Direction** → `RotationHandle` (persistent
  `_gizmoRotation` realigned via `FromToRotation` — rebuilding with `LookRotation` each
  frame caused pole flips). **Vector** → rotation gizmo (direction) + `ScaleValueHandle`
  cube at origin (magnitude, value unclamped; only the drawn arrow length clamps 1–10),
  arrow cone tip. **AABox** → `BoxBoundsHandle` (axis-colored face handles via
  `midpointHandleDrawFunction`) + a center `PositionHandle`. **TCone/TArcCone** →
  `DrawConeGizmo`, a public-`Handles` reimplementation of the package's internal
  `VFXConeGizmo`/`VFXTArcConeGizmo`: transform handle in the base frame (respects
  `Tools.current` — move/rotate/scale), then wire discs/arcs + radial cube radius
  sliders, an up-axis height slider, and an arc `Slider2D` (mirrors `VFXGizmo.ArcGizmo`)
  inside the cone's TRS frame. Leaves matched by label (`GizmoLeaf`); the arc leaf is
  absent on a plain Cone (skips the wedge edges + arc handle). **TSphere/TArcSphere** →
  `DrawSphereGizmo`, same pattern: transform handle, then three full wire discs (Sphere)
  or longitudinal half-circles + equator arc (ArcSphere), three per-axis radial radius
  sliders, and an equator arc handle. Arc leaf absent on a plain Sphere. **TCircle/
  TArcCircle** → `DrawCircleGizmo` (XY-plane disc/arc + cardinal radius sliders gated to
  the visible arc + arc handle). **TTorus/TArcTorus** → `DrawTorusGizmo` (ring envelope =
  two side discs ±minor + outer/inner rings, tube cross-sections at the cardinal sweep
  angles, a `majorRadius` slider along +up and a `minorRadius` slider out of plane, + arc
  handle). Torus radii matched by label `major`/`minor`; all others by `radius`. **Line**
  → `DrawLineGizmo`: two position-spaceable endpoints (`start`/`end`) joined by a line,
  each a `PositionHandle` — no TRS frame, same space handling as the Position gizmo.
  **OrientedBox/Transform** → `DrawBoxGizmo` (one method — they're the same shape): a
  `DrawWireCube` of size/scale in the oriented frame + the shared tool-aware
  move/rotate/scale handle. Leaves matched `center`→`position`, `size`→`scale` fallbacks;
  the size/scale leaf drives the `ScaleHandle` branch. **Plane** → `DrawPlaneGizmo`: a
  position-spaceable point + direction-spaceable `normal`, shown as a square quad + normal
  arrow; tool-gated (Move = position handle, Rotate = normal rotation gizmo, reusing the
  DirectionType persistent-rotation trick). Quad is handle-size-relative (VFX's is a fixed
  huge quad).
- Local/World via `component.transform` (`TransformPoint`/`TransformDirection`) or a
  `Handles.DrawingScope` matrix for the box.
- **Cosmetic draws (DrawLine/ConeHandleCap) must be guarded by
  `Event.current.type == EventType.Repaint`** — drawing caps on other events corrupts GL
  state and bleeds pixel-block artifacts across Scene view AND the window.
- **Labels**: screen-space (`Handles.BeginGUI` + `GUI.Label`) at the top-right of the
  gizmo's on-screen box; rich-text style (built fresh, NOT copied from `EditorStyles.helpBox`
  or richText is ignored) with a generated rounded translucent bg (alpha 0.4); property
  name `<b>bold</b>`, components axis-colored (`Handles.x/y/zAxisColor`).

## Other features

- **Copy/Paste** (right-click a property row label): float/Vec2/3/4/Color/
  Gradient, via `VfxClipboard` (reflection over internal `UnityEditor.Clipboard`) so values
  round-trip with the **Inspector** both ways.
- **Favorites group** (`BuildFavoriteGroup`, prepended by `AddFavoriteGroup(body, includeProps, rendererFavs)`):
  a collapsible group styled like a category (gold `vfx-group-star` in the dot slot) — *not* a
  card grid — at the **top of every main tab**. Property favorites render **struct-aware** through
  the same `ComputeFavoriteDisplay` + `AddDisplayEntries` path as categories, so a pinned compound
  (e.g. Box) keeps its **header row with the space icon + Edit-Gizmo**, not a flat list of leaves.
  Renderer favorites are `Setting`s (`{ FavKey, Func<VisualElement> BuildRow }`) from
  `RendererFavoriteSettings`, rendered as rows. Each tab prepends its own (Properties → property
  favs; Renderer → renderer favs, sharing the section's `SerializedObject`); the **All tab**
  prepends a *unified* group (properties **+** renderer favorites) sharing one renderer
  `SerializedObject` with `BuildRendererSections` so both stay in sync. Collapse persists under the
  `"Favorites"` key in `_collapsed`. Shown only when the rail **section = All**, filter = all, and
  search is empty (those narrow favorites themselves). Renderer rows reuse the per-build
  `_rendererRows`/`TrackSerializedObjectValue` so markers stay live.
- **Playback**: configurable timeline **duration** (default 10s, editable in the Playback
  tab, stored globally in `EditorPrefs`). A ~30fps `Tick` advances the scrub bar by real
  `dt × playRate / duration` while playing; at the end it loops (`Reinit`) or — if the
  **Loop** toggle is off (`_loop`, `EditorPrefs`) — holds on the last frame. The persistent
  top transport bar (`BuildMiniTransport`, always visible above the tabs) is the **single home
  for the transport**, laid out as a `.vfx-transport-wrap` **column of two `.vfx-transport-row`s**:
  **row 1** = the full-width scrub bar + time + live count; **row 2** = the transport buttons —
  restart (`Reinit`) · step-back · play/pause (`pause`, built-in `PlayButton`/`PauseButton`
  icons via an `Image` for crisp rendering) · step-forward (`Simulate(1/60,1)`) · **Loop**
  toggle (`_loopBtn`, `.vfx-tbtn--on`) — followed by the **Rate** slider ("Rate" label · 0–10×
  `Slider` filling the rest of the row · ↺ reset-to-1×; `_rateSlider` is a window-level ref
  resynced by `UpdateLive` so undo/multi-select stay reflected). The Playback tab does NOT
  duplicate any of this; buttons built via `MakeTransportButton`/`StepFrame`. **Step-back reuses
  the `StepButton` icon mirrored** (`MakeTransportButton(mirror:true)` → `style.scale = -1 x`);
  a dedicated glyph read poorly. The blue fill is updated in `UpdateLive` (so it also resets on
  restart-while-paused). Time readout = scrub × duration (NOT real sim time — GPU sim has no
  queryable playhead; see handoff scrub caveat). Scrub + step-back seek via **`SeekTo`** (pause
  → `Reinit` → `Simulate` forward to target).
- **Playback tab** (built out): settings are **`PField`** descriptors (`BuildPlaybackFields`),
  modelled like the renderer's `RField` but backed by live component props / tool prefs
  (NOT `SerializedProperty`s) — each has a fav key (`play:<id>`), `IsModified`, `Reset`, and a
  `BuildControl()` returning the control **+ a `sync` action** (re-reads the value into it).
  **Duration** (tool pref), **Start Seed** (`startSeed`, int→uint + inline ⚄ **Reseed** =
  randomize + `Reinit`; its int field is class `vfx-seed-int` so `AttachLabelDragger` makes the
  row label drag-scrub it like a plain int), **Reseed on Play** (`resetSeedOnPlay`), **Initial
  Event** (`initialEventName` — the sole home for it now). (Rate is NOT a `PField` — it lives in
  the transport strip, see Playback above.) All write
  to every selected instance with `Undo.RecordObjects` + `SetDirty`; `showMixedValue` via
  `EffectsDiffer`. Each is a property-style row (`BuildPlaybackRow`, hover ↺/★) filtered by
  search + chips (`PlaybackChipOk`/`PlaybackChipCounts`); the favorites group + section copies
  stay in sync via **`RefreshPlaybackRows`** (calls each row's `sync`), like the old
  `RefreshDurationRows`. The rows live in a collapsible **"Playback options"** section group
  (`AddPlaybackSection`, collapse key `play:options`). The events get their own **"Send Event"**
  section (`AddSendEventSection`, collapse key `play:events`), rendered with the **same
  `.vfx-group` chrome** — just a left-aligned, wrapping row of **quick-chips** (`BuildEventChips`:
  squared `border-radius:3px` buttons, each a leading orange ⚡ bolt Label + the event name; no
  manual text field — the graph is the source of truth). The chips are **OnPlay/OnStop**
  (`VisualEffectAsset.Play/StopEventName`) plus **every custom Event block in the graph** —
  `VfxGraphReflection.GetEventNames` walks `VFXGraph.children` for `VFXBasicEvent` and reads its
  public `eventName` (mirrors `VFXComponentBoard.RecurseGetEventNames`; top-level only, no
  subgraph recursion yet). Above the chips, an **event-payload editor** (`BuildEventPayloadEditor`)
  builds a `VFXEventAttribute` (modelled on the package's **VFX Event Tester overlay**, but reworked):
  `_eventPayload` is a list of `EventAttr` {name, type, value, **BuiltIn**} rendered as name · type ·
  value rows. **Event buttons (chips) sit on top, on a recessed dark band** (`vfx-sendevent-band`,
  like the rail); the attribute list below. Rows live in a **bordered, reorderable `ListView`** styled
  like the package's Event Tester — an **integrated foldout header** (`showFoldoutHeader` +
  `headerTitle = "Event Attributes"`, so the title reads as part of the list) and the **standard +/-
  footer** (`showAddRemoveFooter`, `reorderMode = Animated` drag handles, `selectionType = Single`,
  `itemsSource = _eventPayload`). Empty → **`makeNoneElement`** shows a "List is Empty" message. Both
  scrollers are **hidden** (no scrollbar; content stays wheel/drag-reachable) and the visible height is
  **capped at `kPayloadMaxRows` = 12** (height = header + clamp(count,1,12)·row + footer). Reorder is
  purely cosmetic (the payload is keyed by name). The
  footer **`+` (`onAdd`)** opens a `GenericMenu` with exactly two entries — **Built-in Attribute**
  (one submenu listing **all** standard attributes, grouped by a grayed section header
  (`AddDisabledItem`) + `AddSeparator` between groups — **Basic Simulation / Advanced Simulation /
  Rendering**, alphabetical within each — *not* three nested submenus; from `s_StdAttrs`/`s_StdSections`,
  name+type+section sourced from `VFXAttributesManager` and the manual's Reference-Attributes page;
  only those three settable sections, not System/Collision/Strip) and **Custom Attribute** (the
  grouped built-in list is built by the shared **`AddStdAttrMenuItems`**). The **Custom Attribute**
  entry lists the graph's own **blackboard custom attributes** (`VfxGraphReflection.GetCustomAttributes`,
  prefilling a custom row with the graph's name + type) followed by a separator + **"New Custom
  Attribute"** (a blank one); when the graph has none it collapses to a single direct "Custom
  Attribute" item. A picked **graph custom** attribute becomes a `GraphCustom` row that — like a
  built-in — uses a **name dropdown** (`ShowGraphCustomNameMenu`, the graph's custom list) + a
  **grayed type** (so the name always matches a real graph attribute and the type can't be
  mismatched); only the **New Custom Attribute** path gives a free text name + editable type. The
  name dropdown button is the shared `MakeNameDropdown` (used by built-in + graph-custom rows).
  A graph-custom row is **flagged stale** when the blackboard changes out from under it: each payload
  build snapshots the graph's customs into `_graphCustomLookup`, and a row whose name is gone
  (renamed/deleted) or whose type no longer matches gets a **⚠ + tinted name + explaining tooltip**
  (`MakeNameDropdown(warn:true)`); nothing is auto-changed — the user reconciles via the name dropdown
  (re-checked on the next section rebuild, since there's no live blackboard hook). The
  footer **`-` (`onRemove`)** deletes the selected row (no per-row ✕). So the three row kinds:
  **built-in** (`ShowBuiltinNameMenu`) and **graph-custom** (`ShowGraphCustomNameMenu`) both use a
  **name dropdown** + a **disabled grayed type icon** (the name is constrained to a real list and the
  type is fixed); a **free custom** row's name is a `TextField` and type a **clickable type icon**
  (`ShowTypeMenu`) over **`EventAttrType` = Float/Vector2/Vector3/Vector4/Bool/Uint/Int**. The type
  column shows the **VFX-Graph blackboard type icon** (`MakeAttrTypeControl`/`AttrTypeIcon` —
  `Float/Vector2-4/Boolean/Integer`; Uint+Int → Integer). All row kinds share a **fixed-width name
  column** (`vfx-payload-name`, 92px) + a 30px icon type column so they align, with 8px gaps between
  name/type/value. Value control is by type — Toggle / Vector2-3-4, and
  **Float/Int/Uint get a `FieldMouseDragger` so they drag-scrub** like the vector components; the
  built-in **`color`** (a Vector3) is edited with a **`ColorField`** (HDR) that reads/writes the
  Vector3 RGB, so it still sends via `SetVector3`. Clicking a chip sends
  with the payload: a per-instance `VFXEventAttribute` (`CreateVFXEventAttribute` +
  `SetBool/Int/Uint/Float/Vector2/Vector3/Vector4` by type), then `Play(attrib)` / `Stop(attrib)` for
  OnPlay/OnStop else `SendEvent(name, attrib)` — all **public runtime API, no reflection**. **Color
  gotcha**: the standard `color` attribute is a **Vector3 (RGB)**
  (`VFXAttributesManager.Color = VFXValue.Constant(Vector3.one)`) — it's typed Vector3 here and sent
  with `SetVector3`, so it passes; the package's own Event Tester uses `SetVector4` and therefore
  **silently fails to pass `color`** (matches the repro). The payload is **scoped per VFX asset**
  (`_payloadByAsset[guid]`; `SetTarget` swaps the active `_eventPayload` to the asset's list, so
  different assets keep independent payloads and same-asset instances share one). It's **persisted in
  `SessionState`** (`Save/LoadPayloads`, key `vfxctrl.payloads`, JSON via `EventAttrDTO`/`PayloadStoreDTO`
  since `Value` is `object`) — saved in `OnDisable`, restored in `OnEnable` — so it **survives domain
  reload/recompile but is cleared on editor restart**. Value edits mutate in place; add/remove/type/
  name-swap `RebuildBodyOnly`. The section header
  carries a **★ pin** (`vfx-group-pin`, fav key `play:sendevent`) — favoriting the whole section
  surfaces it in the **Favorites group** as a labelled chips row (`BuildSendEventFavRow`); it
  counts as one extra leaf in `PlaybackChipCounts` (favoritable, never "modified"), and shows
  under the ★ filter when pinned. Both sections are `PlaybackSections` rail entries,
  rail-filterable like the Renderer tab's Probes/Additional. The transport itself is NOT in the
  tab — it lives once in the persistent top bar (see Playback above).
- **Multi-instance edit**: select several scene VFX sharing the asset → `_effects` + one
  `SerializedObject` each (`_sos`). Display reads the primary; **all writes go through
  `SetValueAll`/`ResetAll`** (per-object, by `m_Name` — index-safe, unlike a single
  multi-target SerializedObject); fields show `showMixedValue` when instances differ; header
  shows `(+N more)`.

## Conventions / gotchas

- A `Button`'s intrinsic `text` is not a flex item — for icon+label/badge layouts, add child
  `Label`s instead (else they overlap). Affects tabs, chips, rail buttons.
- `pickingMode = Position` is required for tooltips/hover; `Ignore` disables them.
- Crisp editor icons: draw at native **16px** (downscaling aliases). `EditorGUIUtility.LoadIcon`
  is internal — for package icons, load `@2x` on HiDPI (`pixelsPerPoint > 1.5`) via
  `AssetDatabase.LoadAssetAtPath`.
- USS keyword for the scrub cursor is `cursor: slide-arrow;` (used by ShaderGraph/VFX).
- Flexbox: set `min-width: 0` on flex children so wide controls shrink instead of overflowing
  under the row tools.
- A `SerializedObject` does NOT survive a domain reload (but the `VisualEffect` reference may);
  `RefreshTarget`/`Rebuild` rebuild `_so`/`_sos` when null.

## Offline compile-check (no Unity needed)

See `~/.claude/projects/.../memory/offline-unity-compile-check.md`. Quick form:
`grep -o '<HintPath>[^<]*</HintPath>' Assembly-CSharp-Editor.csproj | sed -E 's#</?HintPath>##g' | sort -u`
→ emit each as `-r:"..."` into an rsp with `-target:library -nostdlib+ -langversion:9.0
-define:UNITY_EDITOR;UNITY_6000_0_OR_NEWER` + the `.cs` files, then run
`~/.dotnet/dotnet <sdk>/Roslyn/bincore/csc.dll @rsp`. Regenerate the rsp when adding files.

## Not done yet / ideas

- Playback tab is built out (two-row top transport + Rate, Duration, Seed + Reseed, Reseed on
  Play, Initial Event, Send Event section — see **Playback tab** above). Debug tab is a placeholder
  (live stats, systems, visualizers — see handoff). Renderer tab is implemented (VFXRenderer
  settings). Still TODO for Playback: a real scrubbable **timeline** widget (tick marks/
  playhead) and live info/systems (shared with Debug).
- **Generalized ★/Modified is implemented (Phase 2).** Filter chips work per active tab:
  favorites are **namespaced** (`prop:<name>` / `renderer:<m_Field>`; `IsFav`/`ToggleFav`/
  `FavKeyOf`, legacy bare keys migrated by `MigrateFavorites`). Renderer settings are
  modelled as `RField` descriptors (`BuildRendererFields`) with per-field availability,
  `IsModified`, `Reset`, and a UIToolkit `BuildControl` (Phase 3a); each row carries the same
  hover ↺/★ tools as property rows. **"Modified" = differs from a freshly-created VFX component**: `GetRendererDefaults`
  snapshots a throwaway `HideAndDontSave` `VisualEffect`+`VFXRenderer` once per domain.
  Chip counts are **tab-aware** (`TabDef.ChipCounts`); the footer button is **"Reset tab"**
  (`ResetActiveTab`, active-tab-scoped — All resets properties+renderer+playback). Still
  property-only: copy/paste. Favorites span sources — every tab prepends a **Favorites group**
  of its own favorites, and the All tab's lists property + renderer + playback favorites together
  (see Favorites group above). Playback settings are **`PField`** rows (`BuildPlaybackFields`,
  fav keys `play:<id>`): pinnable + ↺ reset + modified marker, counted by `PlaybackChipCounts`;
  since they back live props / tool prefs (not `SerializedProperty`s), copies sync via
  `RefreshPlaybackRows` rather than binding. `PlaybackModified`/`ResetPlayback` fold over the
  fields. `BuildPlaybackContent` is the favorites-less body the All tab reuses (the "Playback
  options" + "Send Event" section groups; the transport lives in the persistent top bar).
- All standard VFX gizmo types implemented: Position, Direction, Vector, AABox, Line,
  Plane, Cone/Sphere/Circle/Torus (+ Arc variants), OrientedBox, Transform.
- Preset save (footer button is disabled).
- Density toggle (compact/comfortable), full per-row update without a body rebuild.
- Meta (Asset) pin/modified; Debug-tab content + its fav/mod model
  once those tabs gain real component settings.
