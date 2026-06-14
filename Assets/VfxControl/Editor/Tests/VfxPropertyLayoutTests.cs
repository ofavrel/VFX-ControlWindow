// Unit tests for VfxPropertyLayout — struct flatten/inline/card classification + category colors.
// Synthetic VfxExposedParam trees; no Unity object / no fixture.

using System.Collections.Generic;
using NUnit.Framework;
using VfxControl.EditorTools;

namespace VfxControl.EditorTools.Tests
{
    public class VfxPropertyLayoutTests
    {
        static VfxExposedParam Struct(string name, int depth, bool spaceable = false) =>
            new VfxExposedParam { Name = name, IsStruct = true, Depth = depth, Spaceable = spaceable };

        static VfxExposedParam Leaf(string name, int depth, string sheet = "m_Float") =>
            new VfxExposedParam { Name = name, IsStruct = false, Depth = depth, SheetType = sheet };

        [Test]
        public void IsScalarLeaf_TrueForFloatIntUint_FalseOtherwise()
        {
            Assert.That(VfxPropertyLayout.IsScalarLeaf(Leaf("a", 1, "m_Float")), Is.True);
            Assert.That(VfxPropertyLayout.IsScalarLeaf(Leaf("a", 1, "m_Uint")), Is.True);
            Assert.That(VfxPropertyLayout.IsScalarLeaf(Leaf("a", 1, "m_Vector3f")), Is.False);
        }

        [Test]
        public void ClassifyStructs_SingleNonSpaceableChild_Flattens()
        {
            var flat = new List<VfxExposedParam> { Struct("Wrap", 0), Leaf("Wrap.x", 1) };
            var m = VfxPropertyLayout.ClassifyStructs(flat);
            Assert.That(m.FlattenChild.ContainsKey(flat[0]), Is.True);
            Assert.That(m.InlineStruct.ContainsKey(flat[0]), Is.False);
        }

        [Test]
        public void ClassifyStructs_SingleSpaceableChild_StaysCard()
        {
            // spaceable single-element struct (Position/Direction) must keep its header → not flattened.
            var flat = new List<VfxExposedParam> { Struct("Pos", 0, spaceable: true), Leaf("Pos.v", 1, "m_Vector3f") };
            var m = VfxPropertyLayout.ClassifyStructs(flat);
            Assert.That(m.FlattenChild.ContainsKey(flat[0]), Is.False);
            Assert.That(m.Leaves[flat[0]].Count, Is.EqualTo(1));
        }

        [Test]
        public void ClassifyStructs_TwoToFourScalarLeaves_Inline()
        {
            var flat = new List<VfxExposedParam>
            {
                Struct("Flip", 0), Leaf("Flip.x", 1, "m_Int"), Leaf("Flip.y", 1, "m_Int"),
            };
            var m = VfxPropertyLayout.ClassifyStructs(flat);
            Assert.That(m.InlineStruct.ContainsKey(flat[0]), Is.True);
        }

        [Test]
        public void ClassifyStructs_NonScalarOrTooMany_Card()
        {
            // 3 leaves but one is a Vector (non-scalar) → not inline, not flatten (renders as a card).
            var flat = new List<VfxExposedParam>
            {
                Struct("Box", 0), Leaf("a", 1, "m_Float"), Leaf("b", 1, "m_Float"), Leaf("c", 1, "m_Vector3f"),
            };
            var m = VfxPropertyLayout.ClassifyStructs(flat);
            Assert.That(m.FlattenChild.ContainsKey(flat[0]), Is.False);
            Assert.That(m.InlineStruct.ContainsKey(flat[0]), Is.False);
            Assert.That(m.Leaves[flat[0]].Count, Is.EqualTo(3));
        }

        [Test]
        public void AssignCategoryColors_KeywordGetsThemed_UnknownsCycleDistinct()
        {
            var map = VfxPropertyLayout.AssignCategoryColors(new[] { "Color", "FooA", "FooB" });
            Assert.That(map["Color"], Is.EqualTo(VfxPropertyLayout.Hex("#c95a4a")), "keyword 'color' → themed palette");
            Assert.That(map["FooA"], Is.Not.EqualTo(map["FooB"]), "unknown categories get distinct fallbacks");
        }

        [Test]
        public void AssignCategoryColors_DeduplicatesByFirstAppearance()
        {
            var map = VfxPropertyLayout.AssignCategoryColors(new[] { "X", "X", "Y" });
            Assert.That(map.Count, Is.EqualTo(2));
        }
    }
}
