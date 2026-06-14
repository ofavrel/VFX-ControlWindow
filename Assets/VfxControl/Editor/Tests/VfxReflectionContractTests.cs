// EditMode tests for the VfxGraphReflection bridge — the "package-update canary".
//
// BindingResolves runs with NO fixture (it just confirms the bridge bound to the installed VFX
// package). The rest assert the exposed-property / event / custom-attribute contract against the
// authored fixtures and Assert.Ignore() when a fixture (or the [NonSerialized] attribute layout)
// isn't available, so the suite is green until the .vfx files are authored / the graph is compiled.

using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.VFX;
using VfxControl.EditorTools;

namespace VfxControl.EditorTools.Tests
{
    public class VfxReflectionContractTests
    {
        const string PropsPath = "Assets/VfxControl/Editor/Tests/VfxControl_Properties.vfx";
        const string EventsPath = "Assets/VfxControl/Editor/Tests/VfxControl_Events.vfx";
        const string MultiPath = "Assets/VfxControl/Editor/Tests/VfxControl_MultiSystem.vfx";

        static VisualEffectAsset Load(string path)
        {
            var a = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(path);
            if (a == null) Assert.Ignore($"fixture not authored: {path}");
            return a;
        }

        // No fixture needed: the single most important regression signal — did the reflection bridge
        // bind to the installed VFX Graph package at all? A package update that moves a core member
        // flips this to available=False.
        [Test]
        public void BindingResolves_AgainstInstalledPackage()
        {
            StringAssert.Contains("available=True", VfxGraphReflection.DescribeBindingState());
        }

        [Test]
        public void ExposedParameters_EnumerateWithExpectedTypes()
        {
            var asset = Load(PropsPath);
            var ps = VfxGraphReflection.GetExposedParameters(asset);
            Assert.That(ps, Is.Not.Empty, "binding resolved but no exposed params enumerated");

            VfxExposedParam Find(string n) => ps.FirstOrDefault(p => p.Name == n);
            Assert.That(Find("Mass")?.SheetType, Is.EqualTo("m_Float"));
            Assert.That(Find("Looping")?.SheetType, Is.EqualTo("m_Bool"));
            Assert.That(Find("Tint")?.RealType, Is.EqualTo("Color"));
        }

        [Test]
        public void ShapeParameter_IsSpaceableWithTPrefixedRealType()
        {
            var asset = Load(PropsPath);
            var ps = VfxGraphReflection.GetExposedParameters(asset);
            var cone = ps.FirstOrDefault(p => p.Name == "Cone");
            if (cone == null) Assert.Ignore($"fixture '{PropsPath}' has no exposed 'Cone' shape param");
            Assert.That(cone.RealType, Is.EqualTo("TCone"), "shape realType is the C# struct name (T-prefixed)");
            Assert.That(cone.Spaceable, Is.True, "an exposed shape is spaceable (gizmo + space icon)");
        }

        [Test]
        public void EventNames_AreTheGraphsCustomEventBlocksOnly()
        {
            // GetEventNames enumerates the graph's custom VFXBasicEvent blocks only; the OnPlay/OnStop
            // built-ins are layered in by the UI (VisualEffectAsset.Play/StopEventName), not here.
            var asset = Load(EventsPath);
            var events = VfxGraphReflection.GetEventNames(asset);
            Assert.That(events, Does.Contain("OnFoo"));
            Assert.That(events, Does.Contain("OnBurst"));
            Assert.That(events, Does.Not.Contain("OnPlay"), "built-in events are not returned by GetEventNames");
        }

        [Test]
        public void CustomAttributes_MapTypeByName_NotOrdinal()
        {
            var asset = Load(EventsPath);
            var attrs = VfxGraphReflection.GetCustomAttributes(asset);
            // index = our payload type order: Float=0, Vector2=1, Vector3=2, Vector4=3, Bool=4, Uint=5, Int=6.
            void Expect(string name, int index)
            {
                var hit = attrs.FirstOrDefault(a => a.name == name);
                Assert.That(hit.name, Is.EqualTo(name), $"custom attribute '{name}' missing from fixture");
                Assert.That(hit.type, Is.EqualTo(index), $"custom attribute '{name}' mapped to wrong type index");
            }
            Expect("attrFloat", 0);
            Expect("attrVec3", 2);
            Expect("attrInt", 6);
        }

        [Test]
        public void SystemSpaces_ReportLocalAndWorld()
        {
            var asset = Load(MultiPath);
            var spaces = VfxGraphReflection.GetSystemSpaces(asset);
            if (spaces == null || spaces.Count == 0)
                Assert.Ignore("system spaces unavailable (graph not compiled this session)");
            // 1 = Local, 2 = World — the fixture authors one of each.
            CollectionAssert.Contains(spaces.Values, 1);
            CollectionAssert.Contains(spaces.Values, 2);
        }
    }
}
