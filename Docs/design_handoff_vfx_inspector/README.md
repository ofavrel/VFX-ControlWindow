# Handoff: Custom Inspector for the Visual Effect component (Variant C — "Bold")

## Overview

This package describes a **custom Unity Editor inspector for the `VisualEffect`
component** (the runtime component that plays a VFX Graph `.vfx` asset, from the
`com.unity.visualeffectgraph` package).

The goal is a denser, more controllable inspector than the stock one. It adds:

- **Search** across exposed properties
- **Category grouping** with collapsible foldouts (Spawn, Color & Lighting, Shape & Motion, Size & Lifetime, Texture & Render)
- **Favorites / pinned properties** surfaced in a quick-access tray at the top
- **Filter chips** — All / Favorites / Modified
- A **horizontal category rail** for jump-to-group filtering (wheel-scrollable)
- **Per-property reset to graph default** + a global Reset all
- A **persistent mini-transport** (play/pause, scrub, restart) that is always visible
- Three tabs — **Properties / Playback / Debug** — so playback controls and live
  stats get real room instead of being buried

> This is **Variant C** of three explored directions. It is the one chosen for
> implementation. The other two variants (A "Native+", B "Tabbed") are visible in
> the prototype HTML for context but are **not** part of this handoff.

---

## About the design files

The files in `prototype/` are a **design reference built in HTML/React** — a
clickable prototype that demonstrates the intended **look, layout, and behavior**.
They are **not** production code to port line-for-line.

The task is to **recreate this design as a real Unity Editor inspector in C#**,
using Unity's established editor-extension patterns. The recommended path is
**UI Toolkit** (UXML + USS + a `UnityEditor.Editor` subclass), which is how modern
Unity inspectors — including the VFX Graph's own — are built. IMGUI
(`OnInspectorGUI`) is a viable fallback but will make the grouping, chips, and
animation noticeably more painful; prefer UI Toolkit.

### How to open the prototype
Open `prototype/VFX Custom Inspector.html` in a browser. **Variant C is the
third (right-most) inspector.** Use the in-page **Tweaks** panel to switch
property layout (rows / cards / table) and density (compact / comfortable).

---

## Fidelity

**High-fidelity.** Colors, spacing, type, control styling, and interactions are
all intended to be matched. However — and this is the most important framing for
a Unity dev — **do not hand-author the colors**. The prototype's color tokens are
a faithful re-creation of Unity's own Editor USS variables. In the real Editor you
should bind to the built-in `--unity-*` USS variables (see **Design Tokens**
below) so the inspector tracks the user's Editor theme (dark/light) automatically.
Treat the prototype's hex values as "what the Unity dark theme resolves to," not
as values to paste.

---

## Target: what this component actually is

- **Component:** `UnityEngine.VFX.VisualEffect` (namespace `UnityEngine.VFX`).
- **Asset it plays:** `VisualEffectAsset` (the compiled `.vfx` graph), exposed via
  `VisualEffect.visualEffectAsset`.
- **Stock inspector being replaced/augmented:** `VisualEffectEditor` (internal to
  the VFX package). See implementation caveat below about overriding it.

### Custom-editor skeleton (UI Toolkit)

```csharp
using UnityEditor;
using UnityEngine.VFX;
using UnityEngine.UIElements;

[CustomEditor(typeof(VisualEffect))]
[CanEditMultipleObjects]
public class VfxBoldInspector : Editor
{
    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();
        // load UXML/USS, build header + mini-transport + tabs, bind properties…
        return root;
    }
}
```

> **Caveat — overriding the built-in editor:** the VFX package already ships
> `[CustomEditor(typeof(VisualEffect))]`. Two custom editors for the same type
> conflict; Unity picks one non-deterministically. Decide up front which of these
> you want, and tell the implementer:
> 1. **Replace** — your `VfxBoldInspector` wins (simplest, but you lose the stock
>    editor's niceties unless you reproduce them). May require the VFX package
>    source or assembly tricks since the built-in editor is in a sealed assembly.
> 2. **Augment via EditorWindow** — leave the component inspector alone and ship
>    this UI as a dockable **"VFX Control" EditorWindow** that tracks the current
>    selection. This sidesteps the conflict entirely and is the lowest-risk option
>    for a first pass. **Recommended unless replacing the inspector is a hard
>    requirement.**
> Either way the layout/behavior below is identical; only the host frame differs.

---

## Layout (top → bottom)

The inspector is a single fixed-width column (~360px, i.e. the normal Inspector
width). Regions, in order:

1. **Component title bar** — "Visual Effect" with the sparkle/VFX icon and a
   context-menu (⋯) affordance. (In a real inspector this is the standard
   component header; you don't draw it yourself unless this is an EditorWindow.)

2. **Asset section** (group 1 of 2):
   - **Asset Template** — an ObjectField bound to `VisualEffect.visualEffectAsset`
     (`VisualEffectAsset`).
   - **Initial Event** — a dropdown bound to the initial-event name (default
     `"OnPlay"`). Maps to `VisualEffect.initialEventName`.

3. **Mini-transport** (persistent, part of group 1) — a thin always-visible bar:
   play/pause toggle, restart button, a **scrub slider** (normalized 0→1 over the
   effect's nominal duration), a time readout (`0.00s`), and a live "N live"
   particle count. See **Playback** for API mapping.

4. **Section divider** — a recessed 9px band with hairline borders top and
   bottom, deliberately separating the *Asset/Playback* group (1) from the
   *Tabs/Properties* group (2). This separation is intentional and called out by
   the user — keep it.

5. **Tabs** — `Properties | Playback | Debug`. The Properties tab shows a count
   badge (number of exposed properties).

6. **Tab body** (scrollable) — see each tab below.

7. **Footer** — left: "{N} edited · seed {n}"; right: "Reset all" (disabled when
   nothing is overridden) and "Save preset" buttons.

---

## Tab: Properties

Top controls:
- **Search field** (pill, magnifier icon) — case-insensitive substring filter on
  property display names. While a query is active, **all groups force-expand** so
  matches are never hidden.
- **Filter chips** — `All (n)` · `★ (n favorites)` · `Modified (n)`. Mutually
  exclusive. "Modified" = property value differs from the graph default (i.e. is
  overridden on this component).
- **Horizontal category rail** — squared chips: `All`, then one per category
  (each with a colored dot). Selecting one filters to that category; selecting it
  again returns to All. **The rail scrolls horizontally on mouse-wheel when it
  overflows** (hover + wheel). Implement with a wheel handler that adds
  deltaY/deltaX to horizontal scroll, only intercepting when content overflows.

Body:
- **Pinned tray** (collapsible, only shown in the unfiltered All view when at
  least one favorite exists) — a 2-column grid of compact cards, each showing the
  property's category dot, label, an unpin (★) button on hover, and the value
  control. This is a convenience surface; the same properties still appear in
  their category group below.
- **Category groups** (collapsible foldouts) — header shows a twirl arrow (▾/▸),
  the category dot, the category name, and a count. Body is the list of property
  rows.

### Property row
`[label] [value control] [reset ↺] [favorite ★]`
- **Label** — left, fixed width. **Modified properties render bold + brighter**
  (no bullet/dot — the user explicitly removed the dot; bold only).
- **Value control** — varies by property type (see below).
- **Reset ↺** — appears on hover; resets that property to the graph default.
- **Favorite ★** — toggles pin; filled when pinned.

The row tools (reset/favorite) appear on hover; favorited rows keep ★ visible.

### Exposed-property model & API mapping

Exposed properties come from the VFX Graph's blackboard. Enumerate them and read
overrides from the component. Practical mapping:

| Prototype type | Unity exposed type | Read / write on `VisualEffect` |
|---|---|---|
| `float` (slider+field) | float | `HasFloat(name)`, `GetFloat`, `SetFloat` |
| `int` | int / uint | `HasInt`/`GetInt`/`SetInt` (and uint variants) |
| `bool` (checkbox) | bool | `HasBool`/`GetBool`/`SetBool` |
| `color` | Color/Vector4 | `HasVector4`/`GetVector4` (HDR color) |
| `gradient` | Gradient | `HasGradient`/`GetGradient`/`SetGradient` |
| `vector3` | Vector3 | `HasVector3`/`GetVector3`/`SetVector3` |
| `curve` | AnimationCurve | `HasAnimationCurve`/`GetAnimationCurve` |
| `object` (texture/mesh) | Texture/Mesh | `HasTexture`/`GetTexture`, `HasMesh`/… |
| `enum` | (graph-defined) | usually an int/enum exposed property |

- **The canonical place to read/write in the inspector is the serialized
  property sheet** (`m_PropertySheet`) via `serializedObject`, so undo, prefab
  overrides, and multi-edit work. The runtime `Get/Set*` methods are best for the
  live **mini-transport preview** while the user scrubs.
- **"Modified" / override state:** each exposed property has an **override toggle**
  in the property sheet. "Modified" in the design = override is on / value ≠ graph
  default. **Per-property reset** = clear the override (revert to the value baked
  in the `VisualEffectAsset`). Read the default from the asset's exposed-property
  defaults.
- **Categories:** VFX Graph blackboard parameters already support **categories** —
  use the parameter's category string to group rows. The five categories in the
  prototype (Spawn, Color & Lighting, Shape & Motion, Size & Lifetime, Texture &
  Render) are **sample data**; in the real inspector, build groups dynamically
  from whatever categories the bound graph defines. Properties with no category
  go in a default/"Uncategorized" group.

### Favorites, filters, collapse, search — persistence
These are **custom UI state, not part of the VFX asset**. Persist per-asset (keyed
by the `VisualEffectAsset` GUID) in **`EditorPrefs`** (or a small `ScriptableObject`
/ `SessionState` if you prefer session-only). Suggested keys:
`vfxbold.{guid}.favorites` (list of property names), `vfxbold.{guid}.collapsed`
(set of group ids), plus last-used tab/filter. Favorites are the only state worth
persisting across sessions; tab/filter/search can be `SessionState`.

---

## Tab: Playback

This is the richer playback surface. Top to bottom:

- **Timeline** — a scrubbable track with a playhead and tick marks; click/drag
  sets the time. Below it, current/duration readout.
- **Transport buttons** — restart, step-back, **play/pause** (primary), step-
  forward, loop toggle.
- **Playback options** (rows): Loop, Play On Awake, Reset On Play, **Playback
  Rate** (0–2×), Fixed Delta Time, **Random Seed** (+ Reseed button).
- **Live info** — the stat grid (see Debug).
- **Systems** — capacity bars per particle system.
- **Send event** — chips for each event name; clicking sends the event.

### API mapping (Playback)

| UI control | `VisualEffect` API |
|---|---|
| Play / Pause | `pause` (bool); also `Play()` / `Stop()` |
| Restart | `Reinit()` (resets the simulation) |
| Loop | property of the graph / your own re-trigger logic |
| Playback rate | `playRate` (float, 1 = normal) |
| Step forward | `Simulate(deltaTime, stepCount)` while paused |
| Reseed / Random Seed | `startSeed` (uint) + `resetSeedOnPlay`; `Reinit()` to apply |
| Play On Awake | serialized field on the component |
| Send event | `SendEvent(eventName)` or `SendEvent(eventNameId)`; payloads via `VFXEventAttribute` |
| Initial event | `initialEventName` |

> **Scrubbing caveat (important):** VFX Graph is a **GPU simulation**. There is no
> random-access "seek." Forward scrubbing works by `pause = true` then
> `Simulate(dt, steps)`. **Backward** scrubbing requires `Reinit()` and then
> simulating forward from 0 to the target time, which can be expensive for long/
> dense effects. Implement the timeline as: pause → on scrub, if target < current,
> Reinit + simulate up to target; else simulate the delta. Communicate this cost
> to the user (the design's smooth scrub is the *ideal*; a stepped/throttled
> implementation is acceptable). The mini-transport's scrub has the same caveat.

---

## Tab: Debug

- **Live statistics** — a 2-column stat grid:
  - **Alive particles** — `VisualEffect.aliveParticleCount` (total). Per-system
    counts via `GetParticleSystemInfo` / the VFX debug APIs where available.
  - **Spawn rate** — derived from the relevant exposed property.
  - **GPU time (ms)** — from the profiler/recorder; this is editor-only debug data
    and may require the VFX debug panel APIs. If not readily available, show "—".
  - **Draw calls**, **Texture mem**, **Bounds** — bounds from
    `VisualEffect.GetComputedBounds` / renderer bounds; others from profiler.
- **Systems** — per-system capacity bars (alive / capacity). Capacity = the
  system's allocated particle count.
- **Visualizers** — toggles: Show Bounds, Show Spawn Icons, Wireframe, Motion
  Vectors. These map to scene-gizmo/debug draw toggles (e.g. bounds via
  `VisualEffect.GetComputedBounds` drawn in `OnSceneGUI`; others to VFX debug
  visualizers where exposed).

> Several Debug values (GPU time, per-system stats) depend on VFX debug/profiler
> APIs that vary by Unity/package version. Where an API isn't available on the
> target version, degrade gracefully (hide the stat or show "—") rather than
> faking a number.

---

## Interactions & behavior summary

- **Tabs** — switching tabs swaps the body; mini-transport and asset section stay
  fixed above the divider.
- **Foldouts & pinned tray** — click header to toggle; persist collapsed state.
- **Search** — live filter; force-expands all groups; empty state when no match.
- **Filter chips** — All / Favorites / Modified; each has an appropriate empty
  state ("Nothing edited yet…", "No pinned properties…").
- **Category rail** — click to filter; click active to clear; **wheel scrolls
  horizontally** when overflowing.
- **Favorite ★** — toggles pin (persisted). **Reset ↺** — clears the override.
- **Reset all** (footer) — clears all overrides; disabled when none.
- **Hover affordances** — row tools fade in on hover; group headers get a hover
  wash.
- **Animation** — keep it Unity-flat: foldout expand/collapse and value drags
  animate; **no bounce, no decorative motion** (matches Editor Foundations).

---

## State management (custom UI state)

| State | Scope | Suggested store |
|---|---|---|
| Favorites (per asset) | persistent | `EditorPrefs`, keyed by asset GUID |
| Collapsed groups / pinned-tray | persistent or session | `EditorPrefs` / `SessionState` |
| Active tab, active filter chip, active category, search text | session | `SessionState` |
| Playback: playing, rate, time, loop | runtime (not persisted) | component + editor state |
| Property values & overrides | the asset/component | `serializedObject` / runtime `Set*` |

---

## Design tokens

**Do not hard-code these.** They are the Unity **dark** theme resolved values; in
UI Toolkit, bind to the built-in USS variables so the inspector follows the user's
theme. Mapping from the prototype's tokens (in `prototype/assets/colors_and_type.css`,
prefixed `--uc-*`) to Unity's built-in USS variables (`--unity-colors-*`):

| Prototype token | Resolved (dark) | Unity USS variable (approx.) |
|---|---|---|
| `--uc-window-bg` | `#383838` | `--unity-colors-window-background` |
| `--uc-default-bg` (recessed) | `#282828` | `--unity-colors-default-background` |
| `--uc-app_toolbar-bg` | `#191919` | `--unity-colors-toolbar-background` (darkest) |
| `--uc-toolbar-bg` | `#3c3c3c` | `--unity-colors-toolbar-background` |
| `--uc-default-border` | `#232323` | `--unity-colors-default-border` |
| `--uc-window-border` | `#242424` | `--unity-colors-window-border` |
| `--uc-default-text` | `#d2d2d2` | `--unity-colors-default-text` |
| `--uc-label-text` | `#c4c4c4` | `--unity-colors-label-text` |
| `--uc-label-text-focus` (selection blue) | `#81b4ff` | `--unity-colors-label-text-focus` |
| `--uc-highlight-bg` (selected) | `#2c5d87` | `--unity-colors-highlight-background` |
| `--uc-input_field-bg` | inset field | `--unity-colors-input_field-background` |
| Links / highlight text | `#4c7eff` | `--unity-colors-link-text` |
| Focus ring | `#7baefa` | `--unity-colors-highlight-background-hover` / focus |

(See the full token list in `colors_and_type.css`; Unity publishes the matching
USS variable names in the *Editor Foundations* docs and the USS UnityVariables
manual page.)

**Category accent dots** (Spawn `#c98a3a`, Color `#c95a4a`, Motion `#4a8ac9`,
Size `#7a9a4a`, Texture `#8a6ac9`) are a **custom, intentional** addition — they
are not Unity tokens. Keep them as a small custom palette (desaturated to sit
calmly against the gray UI, per Unity's "muted color" guidance), or let the graph
define category colors if you prefer.

**Spacing / radius / type:** 8px-based spacing; control radius **2px**, slightly
larger surfaces **3px**, search field is a pill. Type is **Inter** for UI / the
Editor default; **mono** (`--u-font-mono`) for numeric value fields, IDs, and
seeds. Compact density ≈ 20px rows; comfortable ≈ 26px. Minimum control text 11px
(matches Editor conventions).

---

## Iconography

The prototype uses simple inline SVG stand-ins for glyphs (search, star, reset,
play/pause/step/loop, gear, chevrons, bolt, cube, sparkle, chart, list). In Unity,
use the **built-in Editor icons** (`EditorGUIUtility.IconContent(...)`) where
equivalents exist (e.g. play/pause/step from the toolbar set, search, settings),
rather than shipping custom SVGs. The category dots are not icons — they're small
color chips.

---

## Files in this package

```
design_handoff_vfx_inspector/
├─ README.md                      ← this document
└─ prototype/
   ├─ VFX Custom Inspector.html    ← open this; Variant C is the right-most inspector
   ├─ inspector-bold.jsx           ← Variant C component (the design being implemented)
   ├─ inspector-shared.jsx         ← shared state hook, PropRow, Transport, StatGrid
   ├─ vfx-atoms.jsx                ← icons + per-type value controls (PropControl)
   ├─ vfx-data.jsx                 ← the sample VFX_Fireball model (props, categories, stats, events)
   ├─ vfx.css                      ← all inspector styling (tokens consumed here)
   ├─ assets/colors_and_type.css   ← Unity Editor design tokens (the --uc-* variables)
   ├─ design-canvas.jsx            ← prototype scaffolding (canvas host) — not part of the design
   └─ tweaks-panel.jsx             ← prototype scaffolding (tweaks panel) — not part of the design
```

**Read order for the implementer:** `vfx-data.jsx` (what a VFX instance looks
like) → `inspector-bold.jsx` (the Variant C structure & behavior) →
`inspector-shared.jsx` (row/transport/stat building blocks) → `vfx-atoms.jsx`
(per-type controls) → `vfx.css` (exact styling).

---

## Suggested implementation order

1. **Frame decision** — EditorWindow (recommended) vs. replacing `VisualEffectEditor`.
2. **Read-only Properties tab** — enumerate exposed properties from the bound
   graph, group by category, render typed value controls bound to
   `serializedObject`. Get reset-to-default + modified detection working.
3. **Search + collapse + favorites + filter chips + category rail** (custom UI
   state in EditorPrefs/SessionState).
4. **Playback tab** — wire `pause`/`playRate`/`Reinit`/`SendEvent`; implement the
   transport. Add the scrub last (mind the GPU-seek caveat).
5. **Debug tab** — alive counts + systems first; GPU/profiler stats and
   visualizers as available on the target Unity version.
6. **Polish** — divider, persistent mini-transport, footer, theme-variable
   binding, density toggle (optional; the prototype exposes compact/comfortable).
```
