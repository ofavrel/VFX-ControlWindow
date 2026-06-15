// Unit tests for VfxReadbackRecord — the particle-record decode + the .hlsl offset contract.
// No GPU / no fixture: a hand-filled Vector4[] stands in for a read-back buffer.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VfxControl.EditorTools;
using Attr = VfxControl.EditorTools.VfxReadbackRecord.Attr;
using Kind = VfxControl.EditorTools.VfxReadbackRecord.Kind;

namespace VfxControl.EditorTools.Tests
{
    public class VfxReadbackRecordTests
    {
        // One particle in slot 0: float index i lives at data[i>>2][i&3]. Stride is 9 float4 = 36 floats.
        static Vector4[] OneSlot()
        {
            var d = new Vector4[VfxReadbackRecord.Stride];
            d[0] = new Vector4(1, 2, 3, 4);        // position xyz (0..2) + age (3)
            d[2] = new Vector4(0.5f, 0.6f, 0.7f, 0.8f); // color rgb (8..10) + alpha (11)
            d[6] = new Vector4(0, 0, 0, 1);        // alive (27) in .w
            d[7] = new Vector4(0, 0, 0, 5);        // particleId (31) in .w
            return d;
        }

        [Test]
        public void Val_ReadsByFloatIndexAndStride()
        {
            var d = OneSlot();
            Assert.That(VfxReadbackRecord.Val(d, 0, 0), Is.EqualTo(1f)); // position.x
            Assert.That(VfxReadbackRecord.Val(d, 0, 2), Is.EqualTo(3f)); // position.z
            Assert.That(VfxReadbackRecord.Val(d, 0, 3), Is.EqualTo(4f)); // age (.w of float4 0)
            Assert.That(VfxReadbackRecord.Val(d, 0, 8), Is.EqualTo(0.5f)); // color.r
            Assert.That(VfxReadbackRecord.Val(d, 0, 11), Is.EqualTo(0.8f)); // alpha
        }

        [Test]
        public void Val_OutOfRange_ReturnsZero()
        {
            Assert.That(VfxReadbackRecord.Val(null, 0, 0), Is.EqualTo(0f));
            Assert.That(VfxReadbackRecord.Val(OneSlot(), 99, 0), Is.EqualTo(0f));
        }

        [Test]
        public void Format_HandlesAliveIdAndNumbers()
        {
            var d = OneSlot();
            Assert.That(VfxReadbackRecord.Format(d, new Attr("alive", "Alive", 27, 1, Kind.Alive), 0), Is.EqualTo("yes"));
            d[6] = new Vector4(0, 0, 0, 0);
            Assert.That(VfxReadbackRecord.Format(d, new Attr("alive", "Alive", 27, 1, Kind.Alive), 0), Is.EqualTo("no"));
            Assert.That(VfxReadbackRecord.Format(d, new Attr("particleId", "Particle Id", 31, 1, Kind.Id), 0), Is.EqualTo("5"));
            Assert.That(VfxReadbackRecord.Format(d, new Attr("position", "Position", 0, 3, Kind.Float), 0), Is.EqualTo("1, 2, 3"));
            Assert.That(VfxReadbackRecord.Format(d, new Attr("age", "Age", 3, 1, Kind.Float), 0), Is.EqualTo("4"));
        }

        [Test]
        public void SortKey_SystemInstanceParticleIdAndAttributeColumns()
        {
            var d = OneSlot();
            var cols = new List<Attr> { new Attr("position", "Position", 0, 3, Kind.Float) };
            // slot 9004 with perInstance 256, maxInstances 16 → combined 35 → system 2, instance 3, particleId 44.
            Assert.That(VfxReadbackRecord.SortKey(d, cols, 9004, 0, 256, 16), Is.EqualTo(2));  // system
            Assert.That(VfxReadbackRecord.SortKey(d, cols, 9004, 1, 256, 16), Is.EqualTo(3));  // instance
            Assert.That(VfxReadbackRecord.SortKey(d, cols, 9004, 2, 256, 16), Is.EqualTo(44)); // particleId
            // col 3 → first attribute column: float3 sorts by magnitude = |(1,2,3)| = sqrt(14) (slot 0 data).
            Assert.That(VfxReadbackRecord.SortKey(d, cols, 0, 3, 256, 16), Is.EqualTo(Mathf.Sqrt(14f)).Within(1e-4));
        }

        [Test]
        public void SortKey_ColorColumn_UsesLuminance()
        {
            var d = OneSlot();
            var cols = new List<Attr> { new Attr("color", "Color", 8, 3, Kind.Color) };
            double expected = 0.2126 * 0.5 + 0.7152 * 0.6 + 0.0722 * 0.7;
            Assert.That(VfxReadbackRecord.SortKey(d, cols, 0, 3, 256, 16), Is.EqualTo(expected).Within(1e-4));
        }

        [Test]
        public void OffsetContract_AttrsAreNonOverlappingAndWithinStride()
        {
            // The float-offset packing must match VfxReadback.hlsl: each attribute occupies
            // [Float, Float+Count) floats, none overlap, all fit inside Stride*4 floats.
            int floats = VfxReadbackRecord.Stride * 4;
            var used = new bool[floats];
            foreach (var a in VfxReadbackRecord.Attrs)
                for (int i = a.Float; i < a.Float + a.Count; i++)
                {
                    Assert.That(i, Is.InRange(0, floats - 1), $"{a.Layout} float index {i} out of record");
                    Assert.That(used[i], Is.False, $"{a.Layout} overlaps another attribute at float {i}");
                    used[i] = true;
                }
            // a couple of anchor offsets that must not drift
            Assert.That(System.Array.Find(VfxReadbackRecord.Attrs, a => a.Layout == "position").Float, Is.EqualTo(0));
            Assert.That(System.Array.Find(VfxReadbackRecord.Attrs, a => a.Layout == "color").Float, Is.EqualTo(8));
        }
    }
}
