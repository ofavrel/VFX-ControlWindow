# VFX Control — project context

A custom Unity Editor tool that augments the stock `VisualEffect` inspector with a
denser, more controllable UI (the "Bold"/Variant C design from
`Docs/design_handoff_vfx_inspector/README.md`). It's a **dockable EditorWindow**, not
a `[CustomEditor]`, to avoid conflicting with the VFX package's own
`AdvancedVisualEffectEditor`.

- Unity **6000.6.0a2**, `com.unity.visualeffectgraph` **17.6.0**.
- Open via **`Window ▸ VFX Control`**. Diagnostic: **`Tools ▸ VFX Control ▸ Diagnose Target`**
  (logs how the selected/target VFX's exposed properties enumerate — keep for debugging).
- All code is editor-only under `Assets/VfxControl/Editor/` (no asmdef → compiles into
  `Assembly-CSharp-Editor`, which references the VFX runtime + editor assemblies).

## Files

- **`VfxControlWindow.cs`** — the window. Selection tracking, header, target picker,
  mini-transport + playback clock, tabs, Properties tab (search/chips/rail/pinned/
  groups/struct cards/rows), footer, copy-paste, scene-view gizmos, multi-edit.
- **`VfxGraphReflection.cs`** — reflection bridge to the editor-internal VFX graph;
  `GetExposedParameters(asset)` → `List<VfxExposedParam>`.
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

## VfxExposedParam model

`Name` (path / sheet key + favorites/constrained/refresher key), `Label` (nicified field
name for display, bold/`<b>` when used as a header), `SheetType`, `RealType`, `Category`,
`Tooltip`, `IsStruct`, `Depth`, `Spaceable`, `Space`, `HasRange`/`Min`/`Max`,
`EnumValues`, `DefaultValue`.

## UI structure & rendering

- **Header** → **Target picker** (`ObjectField<VisualEffect>`, scene objects; the window
  edits a *scene* instance, never an asset/prefab-asset — guarded by
  `EditorUtility.IsPersistent`) → **Asset/Initial Event** rows → **mini-transport** →
  recessed divider → **tabs** (Properties/Playback/Debug) → body → **footer**
  (`{n} edited · seed {n}` + Reset all).
- **Properties tab**: persistent search (`ToolbarSearchField`, placeholder) + filter chips
  (All/★/Modified) + wheel-scrollable category rail. The filtered list lives in a separate
  `_listContainer` so typing in search rebuilds only the list (keeps focus). `BuildGroup`
  is a **custom collapsible** (NOT a `Foldout`) so headers use a `ClickEvent`+`altKey`
  path that reliably catches Option/Alt on macOS — **Alt/Option+click = expand/collapse all
  nested** (works on category headers and struct headers).
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

- **Copy/Paste** (right-click label, category rows + pinned cards): float/Vec2/3/4/Color/
  Gradient, via `VfxClipboard` (reflection over internal `UnityEditor.Clipboard`) so values
  round-trip with the **Inspector** both ways.
- **Playback**: configurable timeline **duration** (default 10s, editable in the Playback
  tab, stored globally in `EditorPrefs`). A ~30fps `Tick` advances the scrub bar by real
  `dt × playRate / duration` while playing and loops (`Reinit` at the end). Mini-transport:
  play/pause (`pause`, built-in `PlayButton`/`PauseButton` icons via an `Image` for crisp
  rendering), step (`Simulate(1/60,1)`), restart (`Reinit`). The blue fill is updated in
  `UpdateLive` (so it also resets on restart-while-paused). Time readout = scrub × duration
  (NOT real sim time — GPU sim has no queryable playhead; see handoff scrub caveat).
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

- Playback tab is just the Duration field; Debug tab is a placeholder (live stats, systems,
  visualizers — see handoff).
- All standard VFX gizmo types implemented: Position, Direction, Vector, AABox, Line,
  Plane, Cone/Sphere/Circle/Torus (+ Arc variants), OrientedBox, Transform.
- Preset save (footer button is disabled).
- Density toggle (compact/comfortable), full per-row update without a body rebuild.
