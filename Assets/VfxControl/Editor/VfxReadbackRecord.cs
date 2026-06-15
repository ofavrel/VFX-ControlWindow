// VFX Control — the particle readback record contract (mirror of Readback/VfxReadback.hlsl).
//
// The fixed per-particle float-offset packing + the curated attribute table, plus pure decoders
// over a read-back Vector4[] buffer. The Float offsets here MUST match VfxReadback.hlsl's record
// packing — the offset-contract unit test guards against drift. No Unity-object state, so this is
// unit-testable in isolation (VfxParticleReadback owns the buffers/table/overlay and calls these).

using System.Collections.Generic;
using UnityEngine;

namespace VfxControl.EditorTools
{
    internal static class VfxReadbackRecord
    {
        public const int Stride = 9; // float4 per particle record (matches the .hlsl)

        public enum Kind { Float, Color, Alive, Id }

        public readonly struct Attr
        {
            public readonly string Layout;  // representative stored-attribute name that marks presence
            public readonly string Title;   // column header
            public readonly int Float;      // first float index in the per-particle record (0..35)
            public readonly int Count;      // 1 or 3 components
            public readonly Kind Kind;
            public Attr(string layout, string title, int f, int count, Kind kind)
            { Layout = layout; Title = title; Float = f; Count = count; Kind = kind; }
        }

        // Order = column order; Float offsets must match VfxReadback.hlsl's record packing.
        public static readonly Attr[] Attrs =
        {
            new Attr("position",         "Position",     0,  3, Kind.Float),
            new Attr("age",              "Age",          3,  1, Kind.Float),
            new Attr("velocity",         "Velocity",     4,  3, Kind.Float),
            new Attr("lifetime",         "Lifetime",     7,  1, Kind.Float),
            new Attr("color",            "Color",        8,  3, Kind.Color),
            new Attr("alpha",            "Alpha",        11, 1, Kind.Float),
            new Attr("direction",        "Direction",    12, 3, Kind.Float),
            new Attr("size",             "Size",         15, 1, Kind.Float),
            new Attr("targetPosition",   "Target Pos",   16, 3, Kind.Float),
            new Attr("mass",             "Mass",         19, 1, Kind.Float),
            new Attr("scaleX",           "Scale",        20, 3, Kind.Float),
            new Attr("texIndex",         "Tex Index",    23, 1, Kind.Float),
            new Attr("angleX",           "Angle",        24, 3, Kind.Float),
            new Attr("alive",            "Alive",        27, 1, Kind.Alive),
            new Attr("angularVelocityX", "Angular Vel",  28, 3, Kind.Float),
            new Attr("particleId",       "Particle Id",  31, 1, Kind.Id),
            new Attr("pivotX",           "Pivot",        32, 3, Kind.Float),
        };

        // Read one float of a particle's record from the decoded buffer (stride Stride float4).
        public static float Val(Vector4[] data, int slot, int floatIndex)
        {
            int idx = slot * Stride + (floatIndex >> 2);
            if (data == null || idx < 0 || idx >= data.Length) return 0f;
            var v = data[idx];
            switch (floatIndex & 3) { case 0: return v.x; case 1: return v.y; case 2: return v.z; default: return v.w; }
        }

        // Text for a non-color attribute cell.
        public static string Format(Vector4[] data, Attr a, int slot)
        {
            switch (a.Kind)
            {
                case Kind.Alive: return Val(data, slot, a.Float) > 0.5f ? "yes" : "no";
                case Kind.Id: return ((uint)Mathf.Max(0f, Val(data, slot, a.Float))).ToString();
                default:
                    if (a.Count == 3)
                        return $"{Val(data, slot, a.Float):0.###}, {Val(data, slot, a.Float + 1):0.###}, {Val(data, slot, a.Float + 2):0.###}";
                    return $"{Val(data, slot, a.Float):0.###}";
            }
        }

        // Comparable sort key per column: 0 system · 1 instance · 2 particleId · 3.. the active attribute
        // columns (float3 → magnitude, Color → luminance, else the scalar). The slot packs
        // system/instance/particle: slot = (system*maxInstances + instance)*perInstance + particleId.
        public static double SortKey(Vector4[] data, IReadOnlyList<Attr> cols, int slot, int col, int perInstance, int maxInstances)
        {
            int combined = slot / perInstance;          // system*maxInstances + instance
            if (col == 0) return combined / maxInstances; // system
            if (col == 1) return combined % maxInstances; // instance
            if (col == 2) return slot % perInstance;      // particleId
            int ci = col - 3;
            if (ci < 0 || ci >= cols.Count) return slot;
            var a = cols[ci];
            if (a.Kind == Kind.Color)
                return 0.2126 * Val(data, slot, a.Float) + 0.7152 * Val(data, slot, a.Float + 1) + 0.0722 * Val(data, slot, a.Float + 2);
            if (a.Count == 3)
            {
                double x = Val(data, slot, a.Float), y = Val(data, slot, a.Float + 1), z = Val(data, slot, a.Float + 2);
                return System.Math.Sqrt(x * x + y * y + z * z);
            }
            return Val(data, slot, a.Float);
        }
    }
}
