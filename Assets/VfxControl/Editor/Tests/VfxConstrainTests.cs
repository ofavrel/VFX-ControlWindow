// Unit tests for VfxConstrain — the constrained-proportions math. No Unity objects / no fixture.

using NUnit.Framework;
using UnityEngine;
using VfxControl.EditorTools;

namespace VfxControl.EditorTools.Tests
{
    public class VfxConstrainTests
    {
        [Test]
        public void Components_ScalesOthersByEditedRatio()
        {
            // changed index 0: 1 → 2 (ratio 2); derived 2→4, 3→6.
            var r = VfxConstrain.Components(new[] { 1f, 2f, 3f }, new[] { 2f, 2f, 3f });
            Assert.That(r, Is.EqualTo(new[] { 2f, 4f, 6f }).Within(1e-4f));
        }

        [Test]
        public void Components_EditedComponentKeptExact_DerivedRoundedTo2Decimals()
        {
            // changed index 0: 3 → 1 (ratio 1/3); derived 7 → 2.3333 → rounded 2.33.
            var r = VfxConstrain.Components(new[] { 3f, 7f }, new[] { 1f, 7f });
            Assert.That(r[0], Is.EqualTo(1f).Within(1e-4f), "edited component kept as typed");
            Assert.That(r[1], Is.EqualTo(2.33f).Within(1e-4f), "derived rounded to 2 decimals");
        }

        [Test]
        public void Components_EditedFromZero_EqualizesAll()
        {
            // prev edited component is 0 (ratio undefined) → every component becomes the new value.
            var r = VfxConstrain.Components(new[] { 0f, 5f, 9f }, new[] { 3f, 5f, 9f });
            Assert.That(r, Is.EqualTo(new[] { 3f, 3f, 3f }).Within(1e-4f));
        }

        [Test]
        public void Components_NoChange_ReturnsNextUnmodified()
        {
            var next = new[] { 4f, 4f };
            Assert.That(VfxConstrain.Components(new[] { 4f, 4f }, next), Is.SameAs(next));
        }

        [Test]
        public void Vec3_ScalesProportionally()
        {
            Assert.That(VfxConstrain.Vec3(new Vector3(1, 2, 3), new Vector3(2, 2, 3)),
                        Is.EqualTo(new Vector3(2, 4, 6)));
        }

        [Test]
        public void Vec2_And_Vec4_RoundTripDimensions()
        {
            Assert.That(VfxConstrain.Vec2(new Vector2(2, 4), new Vector2(4, 4)), Is.EqualTo(new Vector2(4, 8)));
            Assert.That(VfxConstrain.Vec4(new Vector4(1, 1, 1, 1), new Vector4(2, 1, 1, 1)),
                        Is.EqualTo(new Vector4(2, 2, 2, 2)));
        }
    }
}
