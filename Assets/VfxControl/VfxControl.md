# VFX Control — project context

A custom Unity Editor tool that augments the stock `VisualEffect` inspector with a
denser, more controllable UI (the "Bold"/Variant C design from
`Docs/design_handoff_vfx_inspector/README.md`). It's a **dockable EditorWindow**, not
a `[CustomEditor]`, to avoid conflicting with the VFX package's own
`AdvancedVisualEffectEditor`.

- Unity **6000.6.0a2**, `com.unity.visualeffectgraph` **17.6.0**.th
- Open via **`Window ▸ VFX Control`**. Diagnostic: **`Tools ▸ VFX Control ▸ Diagnose Target`**
  (logs how the selected/target VFX's exposed properties enumerate — keep for debugging).
- All code is editor-only under `Assets/VfxControl/Editor/`, compiled by
  **`VfxControl.Editor.asmdef`** (`includePlatforms: ["Editor"]`, `references: []`). It needs no
  package references: the compile-time VFX types (`VisualEffect`/`VisualEffectAsset`/`VFXRenderer`/
  `VFXEventAttribute`) are in the **built-in** `UnityEngine.VFXModule`, and every *editor-internal*
  VFX type is reached by reflection strings (so the package editor assembly is loaded at runtime,
  never referenced at compile time). `RenderingLayerMask`/`SortingLayer` are built-in `CoreModule`.

## Files

- **`VfxControlWindow.cs`** + **`VfxControlWindow.<concern>.cs`** — the window, split into
  one `partial class VfxControlWindow` per concern (same class, shared private state — the
  split is purely for navigability, no behavior change). The core `VfxControlWindow.cs` holds
  lifecycle, `Rebuild`/`PopulateActiveTab`/`BuildChrome`, the tab/rail/chip/footer chrome, the
  All tab, favorites group, and small shared helpers. The concern partials:
  - **`.Targeting.cs`** — selection → editable scene VFX (single/multi sharing one asset),
    per-instance `SerializedObject`s, `SetValueAll`/`ResetAll` multi-edit, rail-section persistence.
  - **`.Properties.cs`** — Properties tab: category groups, struct cards (flatten/inline/card),
    typed value controls + `Bind`, constrain lock, copy-paste, category colors, enable gate, space icon.
  - **`.Playback.cs`** — persistent mini-transport (scrub/play/step/loop/Rate) + Playback tab
    `PField` options (Duration/Seed/Reseed/Initial Event).
  - **`.Events.cs`** — the Playback tab's "Send Event" section: chips + the `VFXEventAttribute`
    payload editor (built-in / graph-custom / free-custom rows), per-asset payload persistence.
  - **`.Debug.cs`** — Debug tab live stats grid, CPU/GPU profiling markers, per-system bars,
    texture usage, Show Bounds visualizer. (The Debug ▸ Particles spreadsheet is its own class —
    see `VfxParticleReadback` below — wired in here via `AddDebugGroup(..., _readback.Build)`.)
  - **`.Renderer.cs`** — Renderer tab: sibling `VFXRenderer` settings (`RField` rows).
  - **`.Gizmos.cs`** — custom scene-view edit gizmos for spaceable struct properties.
- **`VfxParticleReadback.cs`** — the opt-in per-particle attribute spreadsheet (GraphicsBuffer
  readback) + its scene-view overlay, extracted as a **self-contained subsystem** (not a partial):
  it owns its GPU buffers + decoded state + the table, with an `IDisposable` lifecycle. The window
  feeds it the selection (`SetTarget`) and drives it — `Build` (Debug tab body), `Pump` (Tick),
  `DrawOverlay` (OnSceneGui), `Dispose` (OnDisable). Its only outward calls are the static
  `VfxGraphReflection` layout/space queries and `VfxSceneLabel` for the overlay value box.
- **`VfxSceneLabel.cs`** — `DrawBox(anchor, text, bg, bottomLeft)`: the shared translucent
  rounded-box scene label (cached `GUIStyle` + one bg texture per color), used by both the gizmo
  labels and the particle overlay (owned by neither — Repaint-gated).
- **Pure-logic helpers** (stateless, unit-tested — extracted from the window/readback so they're
  testable without a window or a live VFX):
  - **`VfxConstrain.cs`** — constrained-proportions math (`Components`/`Vec2`/`Vec3`/`Vec4`); the
    property rows' chain-lock calls `VfxConstrain.VecN`.
  - **`VfxReadbackRecord.cs`** — the `VfxReadback.hlsl` record contract: `Kind`/`Attr`, the `Attrs`
    table + `Stride`, and pure decoders `Val`/`Format`/`SortKey` over a `Vector4[]`.
    `VfxParticleReadback` delegates its `RbVal`/`FormatRbCell`/`ParticleSortKey` here.
  - **`VfxPropertyLayout.cs`** — `IsScalarLeaf`, `ClassifyStructs` (struct→flatten/inline/card),
    `AssignCategoryColors` (keyword palette else distinct fallback) + the shared `Hex`. The
    Properties tab's `BuildStructLeavesMap`/`BuildCategoryColorMap` and Debug's `EfficiencyColor`
    call into it.
- **`VfxGraphReflection.cs`** — reflection bridge to the editor-internal VFX graph;
  `GetExposedParameters(asset)` → `List<VfxExposedParam>`, `GetEventNames(asset)` →
  custom Event-block names (`VFXBasicEvent.eventName` via `VFXGraph.children`), and
  `GetCustomAttributes(asset)` → the blackboard's custom attributes (`VFXGraph.customAttributes`).
  Debug-tab profiling extras: `GetTextureUsage(asset)` (graph texture slots), `GetSystemAttributeWords(asset)`
  (per-system attribute stride from `VFXDataParticle.GetCurrentAttributeLayout`), `GetSystemAttributeLayout(asset)`
  (the full per-system attribute list — name/type/words — from the same `BucketInfo[].attributes`),
  `GetSystemSpaces(asset)` (per-system sim space 0/1/2 = None/Local/World from `VFXDataParticle.space`,
  used to place the particle scene overlay), and the internal `VisualEffect` CPU/GPU profiler
  **marker-name** helpers (`CpuEffectMarker`/`CpuSystemMarker`/`GpuTaskMarker`).
  The package contract is centralized: `VfxNs`/`VfxAsm` consts + a `VfxType(shortName)` local in
  `Resolve()` are the one source of the namespace/assembly names. `GetSystemAttributeWords` and
  `GetSystemAttributeLayout` share one traversal (`EnumerateSystemLayouts` → per-system
  `(buckets, name)`); `GetSystemSpaces` keeps its own (it needs the data object, not a compiled layout).
- **`VfxPropertySheet.cs`** — read/write the component's `m_PropertySheet` via
  `SerializedObject` (undo/prefab/multi-edit safe). Field names are consts
  (`NameField`/`ValueField`/`OverriddenField`); the per-type read+write is one `s_TypeBridge`
  table (`SerializedPropertyType` → `(Read, Write)`), so each supported type is described once
  (Color→Vector4 and uint→long round-trips noted there).
- **`VfxControlState.cs`** — persistence: favorites/collapsed/constrained per asset GUID
  (`EditorPrefs`); tab/filter/category/search (`SessionState`); global timeline duration.
- **`VfxClipboard.cs`** — reflection wrapper over internal `UnityEditor.Clipboard` for
  Inspector-interop copy/paste.
- **`VfxControl.uss`** — styling, bound to built-in `--unity-*` theme variables. The few
  hand-authored colors are named custom properties on `.vfx-root` (`--vfx-star`/`--vfx-warn`/
  `--vfx-bolt`, inherited via `var()`); the category accent dots are a separate per-name palette
  set inline from C#.
- **`Readback/VfxReadback.hlsl`** — opt-in sample include for the Debug ▸ Particles spreadsheet: a
  Custom HLSL block points at it (`void VfxReadback(inout VFXAttributes, int instanceId, int systemId)`,
  Update or Output context) and each particle writes a **fixed superset record** into a shared global
  buffer at a **stable slot** keyed by `systemId`/`instanceId`/`particleId` (so multiple systems +
  instances never clobber each other); the window reads it back (see Debug tab below). Wire `instanceId`
  (auto-assigned) + a per-block `systemId` constant (per the in-panel legend).
- **`Editor/Tests/`** — EditMode test suite (UTF; `VfxControl.Editor.Tests.asmdef`, internals exposed
  via `Editor/AssemblyInfo.cs` → `InternalsVisibleTo`). `VfxControlStateTests` (prefs/set
  serialization, duration clamp — isolated via a throwaway GUID + save/restore), `VfxPropertySheetTests`
  (override set/get/reset + Color→Vector4, on a throwaway `VisualEffect`), and `VfxReflectionContractTests`
  — the **package-update canary** (`BindingResolves` asserts `available=True`; param types, event
  names, and by-name custom-attribute mapping checked against the fixtures). Plus **pure-logic unit
  tests** (no fixture): `VfxConstrainTests`, `VfxReadbackRecordTests` (incl. an offset-contract test
  guarding `.hlsl` drift), `VfxPropertyLayoutTests`. Fixture-dependent tests `Assert.Ignore` when
  their `.vfx` fixture is absent; the authored fixtures live alongside the tests
  (`Assets/VfxControl/Editor/Tests/VfxControl_Properties|Events|MultiSystem.vfx` — under `Editor/` so
  they stay out of builds). Run via **Window ▸ General ▸ Test Runner ▸ EditMode**.

## Ground truth (verified against the VFX package source — do NOT re-guess)

Package source lives in `Library/PackageCache/com.unity.visualeffectgraph@*/Editor/`.
The whole graph/gizmo/types layer is **internal** to `Unity.VisualEffectGraph.Editor`,
so everything below is reached by **reflection** (see `VfxGraphReflection`/`VfxClipboard`)
and degrades gracefully (empty list / no-op) if a member shifts. **On a package update, audit the
"VFX Graph package contract" region at the top of `VfxGraphReflection`** (namespace/assembly, the
`T_*` type short-names, and the `s_*` handle declarations whose comments name each member +
signature). `Resolve()` records the installed package version (`PackageInfo.FindForAssembly`); a
binding failure logs it next to `AuthoredAgainstVersion` (`VersionNote()`), and
`DescribeBindingState()` (→ *Diagnose Target*) reports the version + every core **and** optional
handle so it's clear exactly what failed to resolve.

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
    each with `attributeName` (string) + `type` (`CustomAttributeUtility.Signature` =
    `Float,Vector2,Vector3,Vector4,Bool,Uint,Int`). `GetCustomAttributes` returns `(name, index)`
    where the index is mapped from the Signature member **name** (`SignatureIndex`, a `switch` on
    `.ToString()`), **not its ordinal** — so a future package that reorders/inserts Signature members
    can't silently mistype an attribute (mirrors `GetSystemSpaces`' by-name space map; an unknown
    name logs and defaults to Float).
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
  inspector: `RefreshTarget` follows the current scene selection (single or multi-VFX
  sharing one asset) and never edits an asset/prefab-asset (guarded by
  `EditorUtility.IsPersistent`). It is **sticky** — when the selection isn't an editable
  scene VFX it KEEPS the last selected instance (so clicking around the scene/Inspector/VFX
  Graph editor doesn't blank the window); it only falls back to the guidance hint when no
  live effect exists yet or the previous target was destroyed (`_effect` reads Unity-null).
  There is no manual target field.
- **Tabs / chrome / rail architecture**: each tab is a `TabDef` (id/label/badge/`HasRail`/
  `Sections`/`Build`), assembled by `BuildTabDefs`. `Rebuild` builds the chrome **once**
  (search field kept in `_searchField` so typing never loses focus) plus persistent
  `_tabsHost`/`_chipsHost`/`_railContainer`/`_tabBody`; every search/chip/rail/tab/favorite/
  reset interaction routes through **`PopulateActiveTab`** (= the new `RebuildBodyOnly`),
  which repopulates only tabs+chips+rail+body. **Search filters the active tab only**
  (`SearchMatches` for IMGUI/meta fields, `Visible` for properties). The **section rail**
  generalizes the old category rail: Properties→categories, Renderer→Probes/Additional,
  Playback→"Playback options"/"Send Event"; Debug→"Live statistics"/"Systems"/"Visualizers"
  (`DebugSections`); selection is **per-tab** in `_sections` (packed into
  `VfxControlState.Sections`, migrating the legacy `Category`). `CurrentSection()` returns
  "all" for tabs without a rail. The Playback/Renderer/Debug/Send-Event sections share one
  collapsible `.vfx-group` builder — **`AddGroupShell(host, key, title, count, forceOpen)`** →
  `(header, content, open)` (the header twirl/title/optional-count + the `ToggleCollapse(key)`
  click); callers fill `content` themselves (Debug builds lazily on `open`, the rest build rows
  unconditionally, Send-Event appends its ★ pin to the returned header). The **All tab** (default) is a traditional inspector:
  Properties+Renderer+Playback stacked with no rail (`BuildAllTab`). **Tab tear-off**: right-clicking
  a focused tab (not "All") → **"Open in new window"** (`ContextualMenuManipulator` → `OpenSolo`)
  spawns a second dockable `VfxControlWindow` via `CreateWindow<>` pinned to that one tab — a
  **`[SerializeField]` `_soloTab`** so each window keeps its OWN pinned tab across a domain reload/restart
  (a torn-off Renderer window stays Renderer instead of syncing to the shared, SessionState main-tab).
  Unity serializes a null string as `""` and the main window never sets it, so every "is solo" test goes
  through **`IsSolo` (`!string.IsNullOrEmpty(_soloTab)`)** — treating null/empty as a *full* window, which
  is what keeps the main window from coming back lean (an earlier non-serialized `_soloTab` that survived
  a reload was the repro for the main window stranding its Asset/transport). A solo window hides the tab
  strip (`PopulateTabs` early-out) and forces `_tab` (clamped right after `_tab = _state.Tab` in
  `SetTarget`, so it follows selection without ever writing the shared `_state.Tab`). A pop-out is
  **lean** — `Rebuild` always builds the Asset (meta) row + transport bar + section gap but sets their
  `display` to `None` when `IsSolo` (so they can never be stranded out of the tree), keeping just header +
  chrome (search + chips) + rail + the one tab's body — and is a **passive observer**: `Tick` only
  advances the playback clock when `!IsSolo`, so multiple windows never fight over
  `Reinit`/`pause` on the shared effect (the main window stays the transport's home). `Open` (the
  menu) restores a solo instance to a full window if that's the only one focused. Each under a **collapsible**
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
slider) and its commit wrapper `RadiusHandleCommit(leaf, …)` (draws the slider, writes the
new radius to `leaf` if it moved, returns it — sphere/circle/torus reuse the return, cone
ignores it), and `ArcHandle` (Slider2D arc, `rotation` orients the sweep plane). Each
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
- **Debug tab** (`BuildDebugTab`): four rail-filtered, collapsible `vfx-group` sections
  (`AddDebugGroup`, collapse keys `debug:live`/`debug:systems`/`debug:textures`/`debug:visualizers`;
  `DebugCollapseKeys`). The live counts are public runtime API; the **profiling extras** (CPU/GPU
  timing markers, texture slots, attribute layout) are reached by reflection in `VfxGraphReflection`
  and degrade to "—" gracefully:
  - **Live statistics** — a 2-col `.vfx-stat-grid` of cells (`MakeStat` → uppercase `.vfx-stat-k` key +
    `.vfx-stat-v` value + dim `.vfx-stat-u` unit; gridlines = the container border colour through each
    cell's right/bottom border): **Alive particles** (Σ per-system `GetParticleSystemInfo(name).aliveCount`,
    `/ capacity` unit; falls back to `aliveParticleCount` when no systems report), **Efficiency**
    (alive/capacity %), **Systems** (`GetParticleSystemNames` count), **Bounds** (world-space
    `Renderer.bounds.size` — `GetComputedBounds` is internal to the VFX editor asm, unusable here),
    **State** (`culled`/`pause` → Culled/Paused/Playing), **CPU time** (whole-effect CPU eval ms),
    **GPU time** (Σ system GPU ms; "—" unless `SystemInfo.supportsGpuRecorder`), **Attr memory** (total
    attribute-buffer bytes; "—" until the graph layout is compiled).
  - **Timing** comes from the profiler **`Recorder`**: marker names via reflection
    (`VfxGraphReflection.CpuEffectMarker`/`CpuSystemMarker`/`GpuTaskMarker` → the *internal*
    `VisualEffect.GetCPU…/GetGPUTaskMarkerName`). Gotcha: `GetCPUEffectMarkerName` is **not**
    parameterless — it takes a `VisualEffect.VFXCPUEffectMarkers` enum; we pass **`FullUpdate`** (the
    whole-effect CPU update). Each marker method also has **two overloads** — a private `(Int32 nameID, …)`
    and the `(string systemName, …)` we want — so the reflection pins the parameter types exactly (grabbing
    the int overload and invoking with a string throws → empty marker → "—"). They're fed to public
    `Recorder.Get(name)` →
    `elapsedNanoseconds` (CPU) / `gpuElapsedNanoseconds` (GPU). Recorders are created+enabled lazily,
    cached in `_recorders` (keyed by marker, cleared on target change); GPU tasks are probed by index
    until a marker comes back empty (`SumGpuMs`). **Crucially, the per-system CPU/GPU markers are only
    emitted while the component is _registered for profiling_** (`VfxGraphReflection.RegisterForProfiling`,
    mirrors `VFXProfilingBoard.Attach`) — `EnsureProfiling` (called from `RefreshDebugStats` while the
    timing readouts are on screen) registers `_effect` on demand (idempotent, self-heals across windows,
    switches on target change), and `StopProfiling`/`OnDisable` unregisters. Markers also need the effect
    to be updating (edit mode works while it's playing/visible). Each marker's ns is lightly
    **EMA-smoothed** (`Smoothed`/`_smoothNs`, weight `kTimerSmooth`) so the readout doesn't flicker, then
    formatted by `FmtMs` (adaptive: ms ≥ 1 ms, else **µs** — per-system costs are usually a few µs,
    below "0.00 ms" precision).
  - **Attribute memory** = per-system stride × capacity × 4. The stride (words/particle) is
    `VfxGraphReflection.GetSystemAttributeWords` (Σ of `VFXDataParticle.GetCurrentAttributeLayout()`
    bucket sizes, mapped to system names via `VFXSystemNames.GetUniqueSystemName`) — the same layout
    the VFX inspector shows under Preferences ▸ VFX ▸ "extra debug info". It's `[NonSerialized]`, so a
    system is omitted (→ "—") until the graph compiles/opens. Cached in `_attrWords` per body build.
  - **Systems** — per-system capacity bars (`BuildDebugSystems`, `.vfx-syslist`/`.vfx-sys`): name +
    `alive / capacity` + a `.vfx-sys-fill` bar whose width = alive/capacity and whose colour follows the
    VFX efficiency convention (`EfficiencyColor`, mirrors `VFXAnchoredProfilerUI.ComputeEfficiencyColor`:
    `#ff2e2e` <51% under-used → `#e3a98b` <91% → `#00ff47` ≥91% well-utilised), plus a `.vfx-sys-detail`
    line (`cpu … ms · gpu … ms · mem …`). Sleeping systems show "Sleeping" + dim (`.vfx-sys--sleeping`).
    Rows cached in `_dbgSysRows` (order mirrors `_dbgSysNames`, name-checked) and refreshed in place.
    Each row also gets a collapsible **attribute-layout foldout** (`BuildAttrLayoutFoldout`) — the full
    stored attribute layout in buffer order (`name · type · per-particle bytes`, header `Layout · N attrs
    · X B/particle`), the breakdown behind the `mem` number. Source: `GetSystemAttributeLayout` walks the
    same `VFXDataParticle.GetCurrentAttributeLayout()` `BucketInfo[]` as the memory stat but reads each
    bucket's `attributes` (VFXAttribute[] per dword channel), counting channels per attribute for its
    word size and reading `VFXAttribute.name` / `.type`. Static per compiled asset (built once, no live
    refresh); empty until the graph is compiled/opened.
  - **Textures** — `BuildDebugTextures`: every texture wired into the graph (exposed or not), via
    `VfxGraphReflection.GetTextureUsage` (walks each node's + blocks' `inputSlots`/sub-slots, reads
    `VFXSlot.value as Texture`). Rows `name · resolution · size` (public `Profiler.GetRuntimeMemorySizeLong`),
    biggest first, with a total; clicking pings the asset. Static per asset → no live refresh.
  - **Particles** — `BuildDebugParticles`: an **opt-in per-particle attribute spreadsheet**
    (`MultiColumnListView`: System · Instance · # · then **one column per attribute the system actually uses**).
    Columns are **driven by the graph layout**: `BuildReadbackColumns` takes the curated `kReadbackAttrs`
    set (position/velocity/direction/color/age/lifetime/alpha/size/targetPosition/mass/scale/texIndex/
    angle/alive/angularVelocity/particleId/pivot — offsets matching the .hlsl record) and keeps only those
    present in the union of `GetSystemAttributeLayout` across systems (falls back to position/age/color/
    alpha when the graph isn't compiled). Headers are click-sortable (`ColumnSortingMode.Custom` →
    `SortReadbackRows`/`ParticleSortKey`; float3 sorts by magnitude, Color by luminance; re-applied on
    every readback) and **reorderable + individually hideable** via the built-in MultiColumnListView
    header menu. **Right-click any column title → "Show All Columns" / "Hide All Columns (keep #)"**
    (`AttachColumnVisibilityMenu`, a `ContextualMenuManipulator` that toggles `Column.visible` on the
    tracked `_hideableColumns` = System/Instance/attrs) so isolating one or two doesn't mean hiding the
    rest one by one; these compose with (sit above) the built-in per-column toggles. The **# column is
    never hidden** — it's the row anchor and (via `MakeMenuHeader`) keeps hosting this menu so "Show All"
    stays reachable after a Hide All.
    The Color swatch is `.gamma`-corrected to match the on-screen particle; numeric text
    stays raw linear. VFX particles are GPU-only with **no managed readback API**, so this uses the
    GraphicsBuffer readback pattern from Unity VFX dev Paul Demeulenaere's
    [`vfx-readback`](https://github.com/PaulDemeulenaere/vfx-readback): the user instruments the graph
    with a **Custom HLSL block** pointing at `Readback/VfxReadback.hlsl` (Update or Output context,
    function `VfxReadback(inout VFXAttributes, int instanceId, int systemId)`) which writes a **fixed
    superset record** (`kReadbackStride`=9 float4/particle — see the .hlsl packing) into
    `_VfxReadbackBuffer` at a **stable slot = `(systemId*kReadbackMaxInstances + instanceId)*256 +
    particleId%256`** **and stamps** `_VfxReadbackGen[slot]` with the current `_VfxReadbackGeneration`.
    Reading an attribute the system doesn't write yields its default and does
    NOT enter the stored layout, so unused attributes simply don't get a column (their buffer slots hold
    harmless defaults). Custom (user) attributes aren't captured — the record is a fixed standard set.
    **Multi-system, SELECTED-only:** put a block in **each** system and wire each block's `systemId` to a
    distinct constant (0,1,…); the panel shows a **legend** (`UpdateSystemLegend`, `systemId` = index into
    `_systemNames`) so you know which number maps to which system, and a **System column**
    (`SystemOf(slot)` → `_systemNames`). `systemId = 0` reduces the slot to the old single-system layout
    (backward compatible). World position resolves space **per system** (`_systemSpaceById[SystemOf(slot)]`).
    **Both `_systemNames` (order) and `_systemSpaceById` come from `GetSystemSpaces`** — which reads the
    serialized per-system space and does NOT need a compiled attribute layout — so a Local-space system
    resolves correctly **right after a domain reload** (sourcing them from `GetSystemAttributeLayout`,
    which is `[NonSerialized]`/empty until recompile, used to zero the space list → Local particles drawn
    in World until the .vfx was re-saved). `GetSystemAttributeLayout` is used only for the column set
    (with a default-column fallback when empty). So the overlay/Alt-click framing are
    correct for mixed-space multi-system graphs. **Per-instance
    separation, SELECTED-only:** the user exposes an Int property named `VfxReadbackInstanceId` and wires
    it to the block's `instanceId` input; `AssignReadbackInstanceIds` (~2 Hz — `SetInt` persists; forced
    on selection change) gives the **selected** instances (`_effects`, sorted by `GetEntityId()`) ids
    0..K-1, and `SetInt`s every *other* instrumented instance **in the scene (any asset, not just this
    one)** to `kReadbackMaxInstances` (out of range, so the block skips it) — select one effect → see only
    it, select two → see both. Steering all assets (not just the current one) is what prevents a
    previously-selected *different* asset's instances from leaking into the list (the buffer is a
    scene-global resource). On an asset switch `PumpReadback` also wipes the gen buffer + decoded caches
    (tracked via `_readbackBufferAsset`) so nothing from the old asset lingers. Names are cached for the
    **Instance** column. Unwired → the port defaults to 0 → one merged instance, no filtering (fine
    for a single effect). The tool bumps the generation each frame and binds both buffers + the int
    via `Shader.SetGlobalBuffer/Int` in `PumpReadback` — **bound for the whole window lifetime, not just
    when the panel shows**, because the instrumented graph references the globals on every sim dispatch,
    so leaving them unbound triggers `Property (_VfxReadbackGen) ... is not set` warnings. Only the
    readback request is gated on the panel: throttled `AsyncGPUReadback` (~6 Hz Auto, or a manual
    **Capture**) — gen buffer first (`OnReadbackGen` finds the latest generation present), then the data
    buffer — and lists the slots stamped with that latest generation = the live particles this frame,
    grouped by instance (dead particles stop re-stamping and drop out;
    `OnReadbackGen`→`OnReadback`→`RefreshParticleTable`; helpbox when uninstrumented; count shows
    `N · M instances`). An **Export CSV** toolbar button (`ExportCsv`) writes the current captured frame
    (`_readbackRows` in display order) to a user-picked `.csv` via `EditorUtility.SaveFilePanel`: header
    `System,Instance,ParticleId,…`, multi-component attributes split into X/Y/Z (Color → R/G/B) columns,
    all attributes regardless of column-hide state, invariant-culture (`G7`) values + RFC-style quoting
    (`Csv`) so names with commas are safe; refreshes AssetDatabase if saved under `Assets/`. Because the
    stamp is "newest present in the buffer", a system that **fully
    empties** would otherwise freeze on its last live frame; `RefreshParticleTable` guards that with
    `LiveAliveCount()` (Σ public `aliveParticleCount` over the selection) — 0 alive → the rows are
    cleared so the table goes empty. **Stable rows** (an atomic-append ring was tried first but its slots advance
    every frame → the list jumps and row counts are erratic; particleId is a stable address).
    **Scene overlay:** each attribute column header is name + an "eye" toggle (`MakeAttrHeader`/`UpdateEyeVisual`
    via `Column.makeHeader`/`bindHeader`; `.vfx-eye` dim→`.vfx-eye--on` bright; eye state persisted per-asset
    in `SessionState`, default all-off; the eye `StopPropagation`s so it doesn't sort). The table is
    `SelectionType.Multiple` (Ctrl/Shift-click several particles, capped at `kMaxDebugParticles`=24);
    selection is tracked by stable **slots** (`OnParticleSelectionChanged` → `_particleSelSlots`,
    re-pinned in `RefreshParticleTable` so each follows its particle across sort/refresh and drops when it
    dies). **Alt+click a row → frame the Scene view** on the selected particles: a trickle-down
    `PointerDownEvent` defers `FrameSelectedParticles` (so the selection has settled), which unions each
    slot's `TryGetParticleBounds` (world pos + `ParticleHalfExtent`, the same size·scale extent the overlay
    draws — shared helper) and calls `SceneView.lastActiveSceneView.Frame` (min box `kFrameMinSize` so a
    tiny particle doesn't over-zoom; works regardless of eye state). When ≥1 row is selected and ≥1 eye is on, `DrawParticleOverlay` (in `OnSceneGui`) loops the
    selected slots and per particle (`DrawParticleMarker`) draws a camera-facing **wireframe quad**
    (`Handles.DrawPolyLine`, sized by size·scale) + a center `Handles.DotHandleCap` (constant screen
    size) + a translucent grey box (`DrawLabelBoxScreen`,
    refactored out of `GizmoLabel`) listing each eye-ON attribute's value at the particle's **world**
    position. The box's **bottom-left** is anchored to the particle's **upper-right corner** — the corner
    is `world + (camRight+camUp)·half`, `half = 0.5·|size|·max(|scaleX|,|scaleY|,|scaleZ|)` (size·scale,
    defaults size≈0.1 / scale=1), so the box scales with the particle and sits just past its edge. Shared
    `DrawLabelBoxScreen(anchor, text, bg, bottomLeft)` places the box by a chosen corner (gizmo labels use
    top-left; the overlay uses bottom-left → box grows up-and-right). World position:
    `TryGetParticleWorld` reads the stored position and, when the system sims in **Local** space
    (`GetSystemSpaces`), transforms by the **owning instance's** `localToWorldMatrix` (owner =
    `_readbackSelected[slot/256]`); World/unknown → as-is. Mixed multi-system spaces aren't disambiguated. **No
    per-frame counter reset:** C#'s `PumpReadback` runs on `EditorApplication.update` while the VFX sim
    runs on **repaint** (decoupled in the editor), so a per-frame reset outran the sim and read back empty
    — the generation stamp is immune to that ordering. **The global-UAV path is CONFIRMED WORKING** in
    Unity (a global `RWStructuredBuffer` bound via `Shader.SetGlobalBuffer` *is* visible to VFX compute
    and writes persist) — so the exposed-buffer-parameter fallback (`VisualEffect.SetGraphicsBuffer`) is
    not needed, kept only as a note.
    **Why the instanceId must be wired (not automatic):** a Custom HLSL function receives only `att` + its
    wired input ports (`CustomHLSL.BuildSource`), never the simulation kernel's locals or thread index,
    and **no graph operator outputs a per-instance id** (`instanceActiveIndex` is computed from the
    dispatch index inside `VFXInitInstancing`, kernel-local; System Seed isn't exposed to HLSL either). So
    the only way to tell instances apart is the exposed-Int id set per-component via `SetInt`. The block
    works in **either the Update or the Output context** — it does plain `RWStructuredBuffer` writes (no
    UAV atomics, no kernel-only locals like `instanceActiveIndex`), which compile and run in both the
    compute kernel and the output vertex/fragment passes. **Do NOT re-add a `UNITY_COMPUTE_SHADER` guard**
    (an earlier version had one for the since-removed `instanceActiveIndex`/`InterlockedAdd`; it silently
    disabled the Output-context use — block in Output → body compiled out → list never updates). Avoid the
    **Initialize** context (fires only at particle birth → empty on frames with no spawn). Limits (debug
    snapshot): 8 systems × 16 instances × 256 particleIds (`kReadbackMaxSystems`/`kReadbackMaxInstances`/
    `kReadbackPerInstance`, kept in lockstep with the .hlsl defines); particleIds `≥ 256` wrap within an
    instance, and systemIds `≥ 8` / instanceIds `≥ 16` are skipped by the block. Buffers reused + disposed
    in `OnDisable`.
  - **Visualizers** — `BuildDebugVisualizers`: a **Show Bounds** toggle row (`.vfx-toggle-row`, whole-row
    clickable) → the `ShowBounds` property (read straight from `SessionState` `vfxctrl.showBounds`, **not
    cached**, so it's shared across all windows) drives `DrawBoundsVisualizer` in `OnSceneGui` (world-space
    `Renderer.bounds` wire cube, Repaint-gated like the gizmos). The checkbox (`_showBoundsToggle`)
    resyncs in `UpdateLive` when another window (e.g. a torn-off Debug tab) flips it.
    Spawn-icons/wireframe/motion-vectors are omitted (their VFX visualizers are internal).
  - Live values refresh **in place** off the ~30fps clock: `RefreshDebugStats` (from `UpdateLive`) reads
    each system once, feeding the grid, the per-system bars, **and** the All-tab teaser — each guarded on
    its widgets' `.panel` so it no-ops for whichever isn't the current body (reusing `_dbgSysNames`).
  - The **All tab** does NOT embed any of this (it gets heavy): `AddDebugShortcut` adds a non-folding
    `.vfx-allsection-head--link` row — title + a live `_dbgTeaser` summary (`N live · M systems`) + a `→`
    — that **jumps to the Debug tab** on click (so `all:Debug` is dropped from `TabCollapseKeys`).
  - No fav/mod model yet (`ChipCounts` = 0).
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
  Play, Initial Event, Send Event section — see **Playback tab** above). Debug tab is built out —
  live **statistics grid** (incl. CPU/GPU ms + attribute memory) + per-system **capacity bars**
  (with cpu/gpu/mem detail) + **Textures** usage + a **Show Bounds** visualizer (see **Debug tab**
  above); still TODO: more visualizers (spawn icons/wireframe/motion vectors are internal) and a
  fav/mod model. Renderer tab is implemented (VFXRenderer settings). Still TODO for Playback: a
  real scrubbable **timeline** widget (tick marks/playhead) and live info/systems (shared with Debug).
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
- Meta (Asset) pin/modified. Debug tab has stats grid (+ CPU/GPU/attr-memory) + per-system bars +
  Textures + opt-in particle-readback spreadsheet + Show Bounds; a fav/mod model and more visualizers
  (internal VFX ones) are still TODO. Particle readback **works** (global-UAV confirmed), shows a
  **curated standard-attribute set** (the `VfxReadbackRecord.Attrs` table — position/velocity/color/
  size/age/lifetime/…), clears an emptied system via `LiveAliveCount()`, and is **opt-in** (needs the
  Custom HLSL block). Likely next: a smoother authoring path (auto-insert the block / a subgraph) and
  custom (user) attribute capture.
