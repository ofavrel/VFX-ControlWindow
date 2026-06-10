// VFX Control — persistence of custom UI state (NOT part of the VFX asset).
//
// Favorites and collapsed groups persist across sessions in EditorPrefs, keyed
// by the VisualEffectAsset GUID (per the handoff). Tab / filter / category /
// search are session-only via SessionState.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VfxControl.EditorTools
{
    internal sealed class VfxControlState
    {
        const string Prefix = "vfxctrl";

        readonly string _guid; // asset GUID, or "none"

        public VfxControlState(string assetGuid)
        {
            _guid = string.IsNullOrEmpty(assetGuid) ? "none" : assetGuid;
        }

        // ---- global (EditorPrefs, not per-asset) ----

        // Timeline/scrub window length in seconds, editable in the Playback tab.
        public static float GetTimelineDuration() => EditorPrefs.GetFloat($"{Prefix}.timelineDuration", 10f);
        public static void SetTimelineDuration(float seconds) =>
            EditorPrefs.SetFloat($"{Prefix}.timelineDuration", Mathf.Max(0.1f, seconds));

        // ---- persistent (EditorPrefs, per asset) ----

        string FavKey => $"{Prefix}.{_guid}.favorites";
        string CollapsedKey => $"{Prefix}.{_guid}.collapsed";
        string ConstrainedKey => $"{Prefix}.{_guid}.constrained";

        public HashSet<string> LoadFavorites() => LoadSet(FavKey);
        public void SaveFavorites(HashSet<string> set) => SaveSet(FavKey, set);

        public HashSet<string> LoadCollapsed() => LoadSet(CollapsedKey);
        public void SaveCollapsed(HashSet<string> set) => SaveSet(CollapsedKey, set);

        // properties whose multi-component value edits scale proportionally
        public HashSet<string> LoadConstrained() => LoadSet(ConstrainedKey);
        public void SaveConstrained(HashSet<string> set) => SaveSet(ConstrainedKey, set);

        static HashSet<string> LoadSet(string key)
        {
            var raw = EditorPrefs.GetString(key, "");
            var set = new HashSet<string>();
            if (!string.IsNullOrEmpty(raw))
                foreach (var s in raw.Split('\n'))
                    if (!string.IsNullOrEmpty(s)) set.Add(s);
            return set;
        }

        static void SaveSet(string key, HashSet<string> set)
        {
            EditorPrefs.SetString(key, string.Join("\n", set));
        }

        // ---- session-only (SessionState) ----

        public string Tab
        {
            get => SessionState.GetString($"{Prefix}.tab", "props");
            set => SessionState.SetString($"{Prefix}.tab", value);
        }

        public string Filter // "all" | "fav" | "mod"
        {
            get => SessionState.GetString($"{Prefix}.filter", "all");
            set => SessionState.SetString($"{Prefix}.filter", value);
        }

        public string Category // "all" or a category name
        {
            get => SessionState.GetString($"{Prefix}.category", "all");
            set => SessionState.SetString($"{Prefix}.category", value);
        }

        public string Search
        {
            get => SessionState.GetString($"{Prefix}.search", "");
            set => SessionState.SetString($"{Prefix}.search", value ?? "");
        }
    }
}
