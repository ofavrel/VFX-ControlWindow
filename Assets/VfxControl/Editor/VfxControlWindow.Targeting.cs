// VFX Control — target resolution & multi-instance editing (partial of VfxControlWindow).
//
// Resolves the scene VisualEffect(s) to edit from the current selection (single or several
// sharing one asset), rejecting persistent assets/prefab-assets; binds a SerializedObject
// per instance; and routes all writes through SetValueAll/ResetAll so multi-edit stays
// index-safe. Also the per-tab rail section persistence. Split out of VfxControlWindow.cs —
// same class (partial), shared private state.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public partial class VfxControlWindow : EditorWindow
    {
        // --- target ---
        private VisualEffect _effect;        // primary (drives display + property enumeration)
        private SerializedObject _so;        // primary's serialized object (display reads)
        private readonly List<VisualEffect> _effects = new List<VisualEffect>();      // all edited instances (same asset)
        private readonly List<SerializedObject> _sos = new List<SerializedObject>();  // one per instance (writes apply to all)
        private List<VfxExposedParam> _params = new List<VfxExposedParam>();

        private string _selectionHint; // why there's no editable target (shown as placeholder)
        // ------------------------------------------------------------------ target

        // Resolve the *scene* VisualEffect component to edit. We deliberately reject
        // anything persistent: selecting a .vfx asset, or a prefab asset in the
        // Project (whose root GameObject Selection.activeGameObject exposes), must
        // NOT let you edit it as if it were a live instance. This window edits the
        // component's per-instance override sheet, which only makes sense on a
        // scene/prefab-instance object.
        private VisualEffect ResolveSelectedEffect(out string hint)
        {
            hint = null;

            var go = Selection.activeGameObject;
            var effect = go != null ? go.GetComponent<VisualEffect>() : Selection.activeObject as VisualEffect;

            if (effect != null)
            {
                if (EditorUtility.IsPersistent(effect))
                {
                    hint = "That Visual Effect lives on a prefab/asset in the Project.\n" +
                           "Drag it into a scene (or select a scene instance) to edit its instance properties.";
                    return null;
                }
                return effect; // a scene (non-persistent) instance — editable
            }

            if (Selection.activeObject is VisualEffectAsset)
                hint = "You selected a Visual Effect asset (.vfx).\n" +
                       "Select a GameObject with a Visual Effect component in the scene to edit its instance properties.";

            return null;
        }

        private void RefreshTarget()
        {
            var effect = ResolveSelectedEffect(out var hint);

            if (effect != null)
            {
                _selectionHint = null;
                var targets = GatherTargets(effect);
                if (effect != _effect || _so == null || !SameSet(targets, _effects))
                    SetTarget(effect, targets);
                return;
            }

            // The selection isn't a scene Visual Effect. Stay STICKY on the last selected instance so
            // clicking around (scene, Inspector, VFX Graph editor) to tweak values doesn't blank the
            // window. Only drop to guidance when there's no live effect to keep — nothing has been
            // selected yet this session, or the previous target was destroyed (`_effect` reads Unity-null).
            if (_effect != null) { _selectionHint = null; return; }
            _selectionHint = hint;
        }

        // All selected scene VisualEffects sharing the primary's asset (primary first),
        // so multi-edit applies to instances of the same VFX graph.
        private List<VisualEffect> GatherTargets(VisualEffect primary)
        {
            var list = new List<VisualEffect>();
            if (primary == null) return list;
            list.Add(primary);
            var asset = primary.visualEffectAsset;
            foreach (var go in Selection.gameObjects)
            {
                var ve = go != null ? go.GetComponent<VisualEffect>() : null;
                if (ve != null && ve != primary && !EditorUtility.IsPersistent(ve) && ve.visualEffectAsset == asset)
                    list.Add(ve);
            }
            return list;
        }

        private static bool SameSet(List<VisualEffect> a, List<VisualEffect> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Bind the window to a primary VisualEffect (+ any same-asset instances to edit)
        // and load its exposed properties + per-asset UI state.
        private void SetTarget(VisualEffect effect) => SetTarget(effect, GatherTargets(effect));

        private void SetTarget(VisualEffect effect, List<VisualEffect> targets)
        {
            _gizmoStruct = null; // gizmo target is invalid for a new component
            _recorders.Clear();  // marker names embed the old effect/system — drop stale Recorders
            _smoothNs.Clear();
            _effect = effect;

            _effects.Clear();
            _sos.Clear();
            if (_effect != null)
            {
                foreach (var ve in targets) { _effects.Add(ve); _sos.Add(new SerializedObject(ve)); }
                _so = _sos[0];
            }
            else _so = null;
            _readback.SetTarget(_effect, _effects); // feed the particle-readback subsystem the new selection

            var asset = _effect != null ? _effect.visualEffectAsset : null;
            _params = VfxGraphReflection.GetExposedParameters(asset);

            string guid = asset != null
                ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset))
                : "";
            // Switch to this asset's payload (per-asset scope); create its bucket on first use.
            if (!_payloadByAsset.TryGetValue(guid, out var payload))
            {
                payload = new List<EventAttr>();
                _payloadByAsset[guid] = payload;
            }
            _eventPayload = payload;

            _state = new VfxControlState(guid);
            _favorites = _state.LoadFavorites();
            MigrateFavorites();
            _collapsed = _state.LoadCollapsed();
            _constrained = _state.LoadConstrained();
            _tab = _state.Tab;
            if (IsSolo) _tab = _soloTab; // a pop-out window is pinned to its one tab
            _filter = _state.Filter;
            _search = _state.Search;
            LoadSections();
        }

        // Per-tab rail section, persisted as a packed "tab=section;..." session string.
        private void LoadSections()
        {
            _sections.Clear();
            var raw = _state.Sections;
            if (!string.IsNullOrEmpty(raw))
                foreach (var pair in raw.Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0) _sections[pair.Substring(0, eq)] = pair.Substring(eq + 1);
                }
            // migrate the pre-rail Properties category selection (one-time)
            if (!_sections.ContainsKey("props") && _state.Category != "all")
                _sections["props"] = _state.Category;
        }

        private void SaveSections() =>
            _state.Sections = string.Join(";", _sections.Select(kv => $"{kv.Key}={kv.Value}"));

        // The active tab's selected rail section ("all" when no rail / nothing chosen).
        private string CurrentSection()
        {
            var def = ActiveTabDef();
            if (def == null || !def.HasRail) return "all";
            return _sections.TryGetValue(_tab, out var s) ? s : "all";
        }

        private void SetSection(string id)
        {
            string cur = _sections.TryGetValue(_tab, out var s) ? s : "all";
            _sections[_tab] = (cur == id) ? "all" : id; // re-clicking the active section clears it
            SaveSections();
        }
        // ---- multi-instance writes (apply to every edited instance) ----

        private void SetValueAll(VfxExposedParam p, object value)
        {
            foreach (var so in _sos) VfxPropertySheet.SetValue(so, p, value);
        }

        private void ResetAll(VfxExposedParam p)
        {
            foreach (var so in _sos) VfxPropertySheet.Reset(so, p);
        }

        private void UpdateAllSos()
        {
            foreach (var so in _sos) so.Update();
        }

        // True when the instances hold different values for this property (→ show mixed).
        private bool IsMixed(VfxExposedParam p)
        {
            if (_sos.Count < 2) return false;
            object first = VfxPropertySheet.GetValue(_sos[0], p);
            for (int i = 1; i < _sos.Count; i++)
                if (!Equals(first, VfxPropertySheet.GetValue(_sos[i], p))) return true;
            return false;
        }
    }
}
