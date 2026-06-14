// EditMode tests for VfxPropertySheet — read/write of the VisualEffect's m_PropertySheet overrides.
//
// Runs against Fixture 1 (VfxControl_Properties.vfx). A throwaway HideAndDontSave VisualEffect is
// bound to the asset in SetUp and destroyed in TearDown. Every test Assert.Ignore()s if the fixture
// (or an expected param) isn't authored yet, so the file is green until the .vfx lands.

using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;
using VfxControl.EditorTools;

namespace VfxControl.EditorTools.Tests
{
    public class VfxPropertySheetTests
    {
        const string FixturePath = "Assets/VfxControl/Editor/Tests/VfxControl_Properties.vfx";

        VisualEffectAsset _asset;
        GameObject _go;
        VisualEffect _vfx;
        SerializedObject _so;
        System.Collections.Generic.List<VfxExposedParam> _params;

        [SetUp]
        public void SetUp()
        {
            _asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(FixturePath);
            if (_asset == null) return; // tests Assert.Ignore individually

            _go = new GameObject("VfxPropertySheetTests") { hideFlags = HideFlags.HideAndDontSave };
            _vfx = _go.AddComponent<VisualEffect>();
            _vfx.visualEffectAsset = _asset;
            _so = new SerializedObject(_vfx);
            _params = VfxGraphReflection.GetExposedParameters(_asset);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            _so = null; _vfx = null; _go = null; _params = null; _asset = null;
        }

        VfxExposedParam Param(string name)
        {
            if (_asset == null) Assert.Ignore($"fixture not authored: {FixturePath}");
            var p = _params?.FirstOrDefault(x => x.Name == name);
            if (p == null) Assert.Ignore($"fixture '{FixturePath}' has no exposed param '{name}'");
            return p;
        }

        [Test]
        public void Float_SetGetReset_RoundTrips()
        {
            var p = Param("Mass");
            VfxPropertySheet.SetValue(_so, p, 3.5f);
            Assert.That(VfxPropertySheet.IsOverridden(_so, p), Is.True, "should be overridden after SetValue");
            Assert.That(System.Convert.ToSingle(VfxPropertySheet.GetValue(_so, p)), Is.EqualTo(3.5f).Within(1e-4f));

            VfxPropertySheet.Reset(_so, p);
            Assert.That(VfxPropertySheet.IsOverridden(_so, p), Is.False, "should not be overridden after Reset");
        }

        [Test]
        public void Bool_SetGet_RoundTrips()
        {
            var p = Param("Looping");
            VfxPropertySheet.SetValue(_so, p, true);
            Assert.That(VfxPropertySheet.GetValue(_so, p), Is.EqualTo(true));
            VfxPropertySheet.SetValue(_so, p, false);
            Assert.That(VfxPropertySheet.GetValue(_so, p), Is.EqualTo(false));
        }

        [Test]
        public void Color_StoresAsVector4_RoundTrips()
        {
            // Color exposes as a Vector4f sheet entry, so a Color value writes through and reads back
            // as a Vector4 (the SetValue Color→Vector4 path). The ColorField mapping is a display-layer
            // concern; the sheet itself round-trips the RGBA components.
            var p = Param("Tint");
            var c = new Color(0.1f, 0.2f, 0.3f, 1f);
            VfxPropertySheet.SetValue(_so, p, c);

            var v = VfxPropertySheet.GetValue(_so, p);
            Assert.That(v, Is.InstanceOf<Vector4>());
            var got = (Vector4)v;
            Assert.That(got.x, Is.EqualTo(c.r).Within(1e-4f));
            Assert.That(got.y, Is.EqualTo(c.g).Within(1e-4f));
            Assert.That(got.z, Is.EqualTo(c.b).Within(1e-4f));
        }

        [Test]
        public void CountModified_CountsOverriddenOnly()
        {
            var p = Param("Mass");
            int before = VfxPropertySheet.CountModified(_so, _params);
            VfxPropertySheet.SetValue(_so, p, 1.23f);
            int after = VfxPropertySheet.CountModified(_so, _params);
            Assert.That(after, Is.EqualTo(before + 1));

            VfxPropertySheet.Reset(_so, p);
            Assert.That(VfxPropertySheet.CountModified(_so, _params), Is.EqualTo(before));
        }
    }
}
