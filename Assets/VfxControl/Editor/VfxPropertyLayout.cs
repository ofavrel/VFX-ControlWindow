// VFX Control — pure layout/classification logic for the exposed-property list.
//
// Stateless decisions extracted from the Properties tab so they're unit-testable without a window
// or a live VFX asset: how a struct parent renders (flatten / inline / card), and the per-category
// accent-dot color assignment. No UI, no Unity-object state beyond Color.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VfxControl.EditorTools
{
    internal static class VfxPropertyLayout
    {
        // A leaf that renders as a plain number (so a 2–4 scalar struct can inline like a vector).
        public static bool IsScalarLeaf(VfxExposedParam p) =>
            p.SheetType == "m_Float" || p.SheetType == "m_Int" || p.SheetType == "m_Uint";

        // The struct-rendering classification for a flattened param list (mirrors the descendant-count
        // walk): every struct parent → its visible leaves; plus the two "render compactly" cases:
        //   FlattenChild — a single-element NON-spaceable struct → one plain row.
        //   InlineStruct — an all-leaves struct of 2–4 scalars → inline like a Vector2/3/4.
        // (Single-element SPACEABLE structs stay a card so the header can carry the space + gizmo.)
        public readonly struct StructMaps
        {
            public readonly Dictionary<VfxExposedParam, List<VfxExposedParam>> Leaves;
            public readonly Dictionary<VfxExposedParam, VfxExposedParam> FlattenChild;
            public readonly Dictionary<VfxExposedParam, List<VfxExposedParam>> InlineStruct;
            public StructMaps(Dictionary<VfxExposedParam, List<VfxExposedParam>> leaves,
                              Dictionary<VfxExposedParam, VfxExposedParam> flattenChild,
                              Dictionary<VfxExposedParam, List<VfxExposedParam>> inlineStruct)
            { Leaves = leaves; FlattenChild = flattenChild; InlineStruct = inlineStruct; }
        }

        public static StructMaps ClassifyStructs(IReadOnlyList<VfxExposedParam> flat)
        {
            var leavesMap = new Dictionary<VfxExposedParam, List<VfxExposedParam>>();
            var flattenChild = new Dictionary<VfxExposedParam, VfxExposedParam>();
            var inlineStruct = new Dictionary<VfxExposedParam, List<VfxExposedParam>>();

            for (int i = 0; i < flat.Count; i++)
            {
                if (!flat[i].IsStruct) continue;
                int d = flat[i].Depth;
                var leaves = new List<VfxExposedParam>();
                int total = 0;
                for (int j = i + 1; j < flat.Count && flat[j].Depth > d; j++)
                {
                    total++;
                    if (!flat[j].IsStruct) leaves.Add(flat[j]);
                }
                leavesMap[flat[i]] = leaves;

                bool allLeaves = leaves.Count == total;
                if (total == 1 && leaves.Count == 1 && !flat[i].Spaceable)
                    flattenChild[flat[i]] = leaves[0];
                else if (allLeaves && leaves.Count >= 2 && leaves.Count <= 4 && leaves.All(IsScalarLeaf))
                    inlineStruct[flat[i]] = leaves;
            }
            return new StructMaps(leavesMap, flattenChild, inlineStruct);
        }

        // Category accent dots — a small custom palette (handoff): desaturated to sit calmly against
        // the gray UI. Conventional category names get a themed color; everything else is assigned a
        // distinct palette color by order of appearance (NOT a hash, which collapsed names onto one).
        private static readonly (string key, Color color)[] s_CatPalette =
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

        private static readonly Color[] s_Fallback =
        {
            Hex("#c98a3a"), Hex("#c95a4a"), Hex("#4a8ac9"), Hex("#7a9a4a"),
            Hex("#8a6ac9"), Hex("#4aa39a"), Hex("#c08ac9"), Hex("#9a9a4a"),
        };

        // Color used when a category isn't in a built map (GetCategoryColor fallback).
        public static Color DefaultDotColor => s_Fallback[0];

        // Assign each distinct category (in first-appearance order) a color: a keyword match in
        // s_CatPalette, else the next distinct fallback. Empty names are skipped (the caller maps
        // them to "Uncategorized" first).
        public static Dictionary<string, Color> AssignCategoryColors(IEnumerable<string> orderedCategories)
        {
            var map = new Dictionary<string, Color>();
            int fallback = 0;
            foreach (var cat in orderedCategories)
            {
                if (string.IsNullOrEmpty(cat) || map.ContainsKey(cat)) continue;
                string lc = cat.ToLowerInvariant();
                Color color = default;
                bool keyed = false;
                foreach (var (key, c) in s_CatPalette)
                    if (lc.Contains(key)) { color = c; keyed = true; break; }
                if (!keyed) color = s_Fallback[fallback++ % s_Fallback.Length];
                map[cat] = color;
            }
            return map;
        }

        public static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }
    }
}
