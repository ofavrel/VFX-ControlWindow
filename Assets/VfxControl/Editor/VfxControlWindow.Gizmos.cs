// VFX Control — scene-view gizmos (partial of VfxControlWindow).
//
// Custom Handles-based editing for spaceable struct properties: Position, Direction,
// Vector, AABox, Line, Plane, plus the shape gizmos (Cone/Sphere/Circle/Torus + Arc
// variants, OrientedBox, Transform). VFX's own gizmos are internal/unusable, so these
// reimplement them on the public Handles API. Split out of VfxControlWindow.cs for
// navigability — same class (partial), shared private state.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public partial class VfxControlWindow : EditorWindow
    {
        // scene-view edit gizmo (custom Handles) for spaceable Position/Direction/Box
        private VfxExposedParam _gizmoStruct;
        private string _gizmoType, _gizmoSpace;
        private bool _gizmoWasCollapsed; // fold state before the gizmo auto-unfolded it (to restore)
        private Quaternion _gizmoRotation = Quaternion.identity; // persistent handle rotation (avoids LookRotation flips)

        private BoxBoundsHandle _boxHandle;
        // ---- scene-view edit gizmo (custom Handles) ----

        // Shape gizmos keyed by C# struct name (realType). They also appear nested (e.g.
        // the inner Cone of an ArcCone), where the sub-struct carries no space; we gate
        // them on p.Spaceable below so only the top-level one — whose frame is known —
        // offers the gizmo. See [[vfx-cone-arccone-layout]].
        private static readonly HashSet<string> s_ShapeGizmoTypes = new()
        {
            "TCone", "TArcCone", "TSphere", "TArcSphere",
            "TCircle", "TArcCircle", "TTorus", "TArcTorus",
            // Transform/OrientedBox MUST stay spaceable-gated: Transform also appears as
            // the nested `transform` of every shape (no space there), so the gate keeps
            // the button on the top-level exposed one only.
            "OrientedBox", "Transform",
        };

        private static bool IsGizmoSupported(VfxExposedParam p) =>
            p.RealType is "Position" or "DirectionType" or "Vector" or "AABox" or "Line" or "Plane" ||
            (s_ShapeGizmoTypes.Contains(p.RealType) && p.Spaceable);

        private VisualElement BuildGizmoButton(VfxExposedParam structParam, bool inline = false)
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

        private void ToggleGizmo(VfxExposedParam structParam)
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

        private Vector3 GizmoVec(VfxExposedParam leaf) =>
            VfxPropertySheet.GetValue(_so, leaf) is Vector3 v ? v : Vector3.zero;

        // axis-colored components (X=red, Y=green, Z=blue) for rich-text scene labels
        private static string FmtAxis(Vector3 v)
        {
            string x = ColorUtility.ToHtmlStringRGB(Handles.xAxisColor);
            string y = ColorUtility.ToHtmlStringRGB(Handles.yAxisColor);
            string z = ColorUtility.ToHtmlStringRGB(Handles.zAxisColor);
            return $"(<color=#{x}>{v.x:0.##}</color>, <color=#{y}>{v.y:0.##}</color>, <color=#{z}>{v.z:0.##}</color>)";
        }
        // Draw a readable text label at the top-right of the gizmo's screen-space box
        // (a 2D box of `worldRadius` around `worldCenter`, ≈ the rotation gizmo size).
        private void GizmoLabel(Vector3 worldCenter, float worldRadius, string text)
        {
            if (Event.current.type != EventType.Repaint) return;
            Camera cam = Camera.current;
            Vector2 center = HandleUtility.WorldToGUIPoint(worldCenter);
            Vector2 edge = HandleUtility.WorldToGUIPoint(worldCenter + (cam != null ? cam.transform.right : Vector3.right) * worldRadius);
            float r = Mathf.Max(8f, Vector2.Distance(center, edge)); // gizmo's screen radius
            // sits to the upper-right of the gizmo box, anchored by the label's top-left corner
            VfxSceneLabel.DrawBox(new Vector2(center.x + r, center.y - r), text, new Color(0.1f, 0.1f, 0.1f, 0.4f), bottomLeft: false);
        }

        // Keep the persistent handle rotation's forward aligned to the current direction
        // by the minimal rotation (preserves roll, stays continuous — unlike LookRotation,
        // whose up-vector flips and makes the direction jump).
        private void AlignGizmoRotation(Vector3 worldDir)
        {
            if (worldDir.sqrMagnitude < 1e-6f) return;
            Vector3 cur = _gizmoRotation * Vector3.forward;
            if (Vector3.Dot(cur.normalized, worldDir.normalized) < 0.99999f)
                _gizmoRotation = Quaternion.FromToRotation(cur, worldDir) * _gizmoRotation;
        }

        // LookRotation that won't degenerate when the forward axis is parallel to up.
        private static Quaternion SafeLook(Vector3 forward)
        {
            if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;
            Vector3 up = Mathf.Abs(Vector3.Dot(forward.normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(forward, up);
        }

        // Find the child leaf of the active gizmo struct whose label contains `key`.
        private VfxExposedParam GizmoLeaf(List<VfxExposedParam> leaves, string key) =>
            leaves.FirstOrDefault(l => l.Label != null && l.Label.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);

        private void OnSceneGui(SceneView _)
        {
            if (ShowBounds && _effect != null) DrawBoundsVisualizer();
            _readback.DrawOverlay(); // selected-particle attribute values (independent of the gizmo block below)

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
        private void DrawPlaneGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
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
        private void DrawBoxGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
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
        private void DrawLineGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
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
        private void DrawSpaceTransformHandle(Matrix4x4 baseMatrix, Vector3 pos, Quaternion rot, Vector3 scale,
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
        private static readonly Vector3[] s_SphereRadiusDirs = { Vector3.right, Vector3.up, Vector3.forward };

        // Mirrors the VFX package's VFXTSphereGizmo / VFXArcSphereGizmo (both internal) on
        // the public Handles API. Transform handle in the base frame; the sphere shell and
        // its radius/arc handles inside that frame × the sphere's own TRS. A plain Sphere
        // has no arc leaf, so it draws three full wire discs and skips the arc handle.
        private void DrawSphereGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
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
                        radius = RadiusHandleCommit(radLeaf, Vector3.zero, dir, radius, AxisColor(dir));
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
        private static Color AxisColor(Vector3 dir)
        {
            Vector3 a = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
            return (a.x >= a.y && a.x >= a.z) ? Handles.xAxisColor
                 : (a.y >= a.z) ? Handles.yAxisColor : Handles.zAxisColor;
        }

        // In-plane cardinal directions for a circle's radius handles (VFX order: the arc
        // sweeps from +up, so handle i sits at i×90° and is hidden past the arc).
        private static readonly Vector3[] s_CircleRadiusDirs = { Vector3.up, Vector3.right, Vector3.down, Vector3.left };

        // Mirrors the VFX package's VFXCircleGizmo / VFXArcCircleGizmo (both internal). The
        // circle lies in the XY plane (normal -forward); a plain Circle has no arc leaf, so
        // it draws a full disc and all four radius handles.
        private void DrawCircleGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
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
                        radius = RadiusHandleCommit(radLeaf, Vector3.zero, dir, radius, AxisColor(dir));
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
        private static readonly float[] s_TorusAngles = { 0f, 90f, 180f, 270f };

        // Mirrors the VFX package's VFXTorusGizmo / VFXArcTorusGizmo (both internal). The
        // ring lies in the XY plane (normal forward); the tube cross-sections sweep that
        // plane from +up around -forward. `majorRadius` is the ring radius, `minorRadius`
        // the tube thickness. A plain Torus has no arc leaf → full discs, no arc handle.
        private void DrawTorusGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
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
                    major = RadiusHandleCommit(majLeaf, Vector3.zero, Vector3.up, major, Handles.yAxisColor);
                // minor radius (thickness) handle at the angle-0 cap, offset out of the ring plane
                if (minLeaf != null)
                    minor = RadiusHandleCommit(minLeaf, Vector3.up * major, Vector3.forward, minor, Handles.zAxisColor);

                if (arcLeaf != null)
                    ArcHandle(arcLeaf, Vector3.zero, major, arcDeg, Quaternion.Euler(-90f, 0f, 0f));
            }

            Vector3 wc = m.MultiplyPoint(Vector3.zero);
            string txt = $"<b>{_gizmoStruct.Label}</b>\nmajor {major:0.##}  minor {minor:0.##}";
            if (arcLeaf != null) txt += $"\narc {arcDeg:0}°";
            GizmoLabel(wc, HandleUtility.GetHandleSize(wc), txt);
        }

        // Radial directions for the side lines of a full (un-arc'd) cone outline.
        private static readonly Vector3[] s_ConeDirs = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };

        // Mirrors the VFX package's VFXConeGizmo / VFXTArcConeGizmo (both internal) on the
        // public Handles API. The transform (move/rotate/scale) handle runs in the base
        // frame — component transform for Local space, identity for World, like
        // VFXSpaceableGizmo's Handles.matrix; the cone shape and its radius/height/arc
        // handles are drawn inside that frame × the cone's own TRS. A plain Cone has no
        // arc leaf, so the arc handle and the wedge edges are skipped.
        private void DrawConeGizmo(List<VfxExposedParam> leaves, UnityEngine.Transform t, bool local)
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
                    RadiusHandleCommit(baseLeaf, bottomCap, Vector3.forward, baseR, Handles.zAxisColor);
                if (topLeaf != null)
                    RadiusHandleCommit(topLeaf, topCap, Vector3.forward, topR, Handles.zAxisColor);

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
        private float RadialRadiusHandle(Vector3 center, Vector3 dir, float radius, Color color)
        {
            Vector3 hp = center + dir * radius;
            Handles.color = color;
            EditorGUI.BeginChangeCheck();
            Vector3 np = Handles.Slider(hp, dir,
                HandleUtility.GetHandleSize(hp) * 0.08f, Handles.CubeHandleCap, 0f);
            return EditorGUI.EndChangeCheck() ? Mathf.Max(0f, Vector3.Dot(np - center, dir)) : radius;
        }

        // RadialRadiusHandle + commit: draws the slider and, if it moved, writes the new radius to
        // `leaf`. Returns the (possibly new) radius so callers that reuse it can `radius = ...`.
        private float RadiusHandleCommit(VfxExposedParam leaf, Vector3 center, Vector3 dir, float radius, Color color)
        {
            float nr = RadialRadiusHandle(center, dir, radius, color);
            if (leaf != null && !Mathf.Approximately(nr, radius)) CommitGizmoFloat(leaf, nr);
            return nr;
        }

        // Arc handle, mirroring VFXGizmo.ArcGizmo: a Slider2D whose angle around the local
        // +up axis (after `rotation`) sets the arc. Must be called inside the shape's matrix
        // scope. `rotation` orients the sweep plane (identity for cones, Euler(-90,0,0) so a
        // sphere sweeps around -forward).
        private void ArcHandle(VfxExposedParam arcLeaf, Vector3 center, float radius, float arcDeg, Quaternion rotation)
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

        private float GizmoFloat(VfxExposedParam leaf) => ToFloat(VfxPropertySheet.GetValue(_so, leaf));

        private void CommitGizmoFloat(VfxExposedParam leaf, float value)
        {
            SetValueAll(leaf, value);
            RefreshProperty(leaf); // sync the bound field in the window
        }

        private void CommitGizmo(VfxExposedParam leaf, Vector3 value)
        {
            SetValueAll(leaf, value);
            RefreshProperty(leaf); // sync the bound field in the window
        }

        // Box face handle drawn in its axis color (X=red, Y=green, Z=blue); the handle's
        // rotation faces along the face normal, which tells us the axis.
        private static void DrawAxisHandle(int id, Vector3 pos, Quaternion rot, float size, EventType type)
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
    }
}
