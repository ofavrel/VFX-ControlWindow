// VFX Control — PROBE (temporary, deletable): a bare-minimum competing CustomEditor for VisualEffect.
//
// Purpose: empirically test whether OUR [CustomEditor(typeof(VisualEffect))] wins over the VFX
// package's AdvancedVisualEffectEditor, and how deterministic that is across domain reloads / restarts.
// It deliberately has NO VFX Control features — just a labelled placeholder + a diagnostic menu.
// Delete this whole file to fully revert (it touches nothing else).

using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    [CustomEditor(typeof(VisualEffect))]
    [CanEditMultipleObjects]
    internal sealed class VfxControlInspectorProbe : Editor
    {
        private void OnEnable() => Debug.Log($"[VFX Control PROBE] our inspector OnEnable for '{(target != null ? target.name : "null")}'");

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            root.Add(new HelpBox(
                "VFX Control custom inspector (PROBE). If you see this, OUR editor won over the VFX package's.",
                HelpBoxMessageType.Info));
            return root;
        }
    }

    internal static class VfxInspectorProbeDiagnostics
    {
        // Reports which editor Unity actually resolves for VisualEffect, plus every registered candidate,
        // so we can confirm the winner and see the conflict. Uses public APIs (Editor.CreateEditor /
        // TypeCache); the attribute's inspected type is read best-effort from a non-public field.
        [MenuItem("Tools/VFX Control/Probe: Diagnose VisualEffect Editor")]
        private static void Diagnose()
        {
            var go = EditorUtility.CreateGameObjectWithHideFlags("__VfxProbe", HideFlags.HideAndDontSave, typeof(VisualEffect));
            try
            {
                var ed = Editor.CreateEditor(go.GetComponent<VisualEffect>());
                if (ed != null)
                {
                    Debug.Log($"[VFX Control PROBE] WINNING editor = {ed.GetType().FullName} (asm {ed.GetType().Assembly.GetName().Name})");
                    Object.DestroyImmediate(ed);
                }
            }
            finally { Object.DestroyImmediate(go); }

            var field = typeof(CustomEditor).GetField("m_InspectedType", BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var t in TypeCache.GetTypesWithAttribute<CustomEditor>())
                foreach (var a in t.GetCustomAttributes(typeof(CustomEditor), false))
                    if (field?.GetValue(a) as Type == typeof(VisualEffect))
                        Debug.Log($"[VFX Control PROBE] candidate editor: {t.FullName} (asm {t.Assembly.GetName().Name})");
        }
    }
}
