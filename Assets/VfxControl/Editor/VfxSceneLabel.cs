// VFX Control — shared scene-view text label (translucent rounded box + rich text).
//
// Used by both the gizmo labels (VfxControlWindow.Gizmos) and the particle overlay
// (VfxParticleReadback), so it lives here as a static helper owned by neither. State (the
// shared GUIStyle + one background texture per color) is static — one cache for all windows.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VfxControl.EditorTools
{
    internal static class VfxSceneLabel
    {
        private static GUIStyle s_style;
        private static readonly Dictionary<Color, Texture2D> s_bgCache = new Dictionary<Color, Texture2D>(); // one bg tex per color

        // A translucent rounded box (color `bg`) + rich text whose top-left (bottomLeft=false) or
        // bottom-left (bottomLeft=true → box grows upward) corner is placed at the GUI-space `anchor`.
        // Repaint-gated (drawing on other events corrupts GL state). Call inside an OnSceneGui pass.
        public static void DrawBox(Vector2 anchor, string text, Color bg, bool bottomLeft)
        {
            if (Event.current.type != EventType.Repaint) return;
            const int radius = 6;
            if (!s_bgCache.TryGetValue(bg, out var tex) || tex == null)
                s_bgCache[bg] = tex = MakeRoundedTexture(16, radius, bg);
            // fresh style (not a copy of helpBox) so richText reliably applies
            s_style ??= new GUIStyle
            {
                fontSize = 11,
                richText = true, // axis-colored components via <color> tags
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(6, 6, 4, 4),
                border = new RectOffset(radius, radius, radius, radius), // 9-slice keeps the corners
                normal = { textColor = Color.white },
            };
            s_style.normal.background = tex; // per-call: the style is shared across colors

            Handles.BeginGUI();
            var content = new GUIContent(text);
            Vector2 sz = s_style.CalcSize(content);
            float y = bottomLeft ? anchor.y - sz.y : anchor.y;
            GUI.Label(new Rect(anchor.x, y, sz.x, sz.y), content, s_style);
            Handles.EndGUI();
        }

        // A rounded-rect texture with a 1px feathered edge, for a 9-sliced label background.
        private static Texture2D MakeRoundedTexture(int size, int radius, Color fill)
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
    }
}
