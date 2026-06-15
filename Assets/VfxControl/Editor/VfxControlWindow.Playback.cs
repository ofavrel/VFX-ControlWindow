// VFX Control — Playback tab + transport (partial of VfxControlWindow).
//
// The persistent mini-transport (scrub bar, play/pause/step/loop, Rate slider) and the
// Playback tab's "Playback options" section: Duration, Start Seed (+ Reseed), Reseed on
// Play, Initial Event — modelled as PField descriptors over live component props / tool
// prefs. The "Send Event" section it hosts lives in the Events partial. Split out of
// VfxControlWindow.cs — same class (partial), shared private state.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.VFX;
using Object = UnityEngine.Object;

namespace VfxControl.EditorTools
{
    public partial class VfxControlWindow : EditorWindow
    {
        // configurable timeline/scrub window length (Playback tab); the play clock
        // fills the bar over this many seconds and then loops (or stops, if _loop is off).
        private float _duration = 10f;
        private bool _loop = true;

        private double _lastTick;
        // The persistent transport bar (always visible above the tabs). Two rows:
        //   row 1 — the full-width scrub bar + time + live count;
        //   row 2 — the transport buttons (restart · step-back · play/pause · step-forward · loop)
        //           followed by the Rate slider.
        // This is the single home for the transport (the Playback tab does not duplicate it).
        private VisualElement BuildMiniTransport()
        {
            var wrap = MakeElement("vfx-transport-wrap"); // column: scrub row + controls row

            // ---- row 1: scrub bar (expanded) + time + live ----
            var top = MakeElement("vfx-transport-row");

            var scrub = MakeElement("vfx-mini-scrub");
            _miniFill = MakeElement("vfx-mini-fill");
            _miniFill.style.width = Length.Percent(_scrubT * 100f);
            scrub.Add(_miniFill);
            scrub.RegisterCallback<MouseDownEvent>(e => { scrub.CaptureMouse(); ScrubAt(scrub, e.localMousePosition.x); });
            scrub.RegisterCallback<MouseMoveEvent>(e => { if (scrub.HasMouseCapture()) ScrubAt(scrub, e.localMousePosition.x); });
            scrub.RegisterCallback<MouseUpEvent>(_ => scrub.ReleaseMouse());
            top.Add(scrub);

            _timeLabel = new Label("0.00 / 0s");
            _timeLabel.AddToClassList("vfx-mini-time");
            top.Add(_timeLabel);

            _liveLabel = new Label("0 live");
            _liveLabel.AddToClassList("vfx-mini-live");
            top.Add(_liveLabel);

            wrap.Add(top);

            // ---- row 2: transport buttons + Rate ----
            var bottom = MakeElement("vfx-transport-row");

            bottom.Add(MakeTransportButton("Restart (Reinit)", null,
                () => { _effect.Reinit(); _scrubT = 0f; UpdateLive(); }, glyph: "↺"));

            // step-back uses the Step-Forward icon mirrored horizontally (a dedicated glyph read poorly).
            bottom.Add(MakeTransportButton("Step back one frame", "StepButton", () => StepFrame(-1), mirror: true));

            // primary play/pause; built-in icon drawn 1:1 at native size (no scaling → no
            // aliasing), kept in sync with the pause state by UpdateLive.
            _playBtn = MakeTransportButton("Play", null, () => { _effect.pause = !_effect.pause; UpdateLive(); });
            _playBtn.AddToClassList("vfx-tbtn--primary");
            _playIcon = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            _playIcon.style.width = 16;
            _playIcon.style.height = 16;
            _playBtn.Add(_playIcon);
            bottom.Add(_playBtn);

            bottom.Add(MakeTransportButton("Step forward one frame", "StepButton", () => StepFrame(1)));

            _loopBtn = MakeTransportButton(_loop ? "Looping (click to stop at end)" : "Stop at end (click to loop)", null, () =>
            {
                _loop = !_loop;
                VfxControlState.SetLoop(_loop);
                _loopBtn.EnableInClassList("vfx-tbtn--on", _loop);
                _loopBtn.tooltip = _loop ? "Looping (click to stop at end)" : "Stop at end (click to loop)";
                UpdateLive();
            }, glyph: "∞");
            _loopBtn.EnableInClassList("vfx-tbtn--on", _loop);
            bottom.Add(_loopBtn);

            // Rate slider, right after the transport buttons (label · 0–10× slider · reset-to-1×);
            // _rateSlider is resynced by UpdateLive so undo/multi-select stay reflected.
            var rateLabel = new Label("Rate");
            rateLabel.AddToClassList("vfx-rate-label");
            bottom.Add(rateLabel);

            _rateSlider = new Slider(0f, 10f) { showInputField = true, value = _effect != null ? _effect.playRate : 1f };
            _rateSlider.AddToClassList("vfx-rate-slider");
            _rateSlider.showMixedValue = EffectsDiffer(ve => ve.playRate);
            _rateSlider.RegisterValueChangedCallback(e => SetPlayRate(e.newValue));
            bottom.Add(_rateSlider);

            var rateReset = MakeIconButton("↺", "Reset to 1×", () =>
            {
                SetPlayRate(1f);
                _rateSlider.SetValueWithoutNotify(1f);
            });
            rateReset.AddToClassList("vfx-rate-reset");
            bottom.Add(rateReset);

            wrap.Add(bottom);
            return wrap;
        }

        private void ScrubAt(VisualElement scrub, float localX)
        {
            float w = scrub.layout.width;
            if (w <= 0) return;
            SeekTo(localX / w);
        }

        // GPU sim has no random-access seek: pause, Reinit, then simulate forward to the target
        // time. Best-effort and capped (see handoff "Scrubbing caveat"). Used by the scrub bar
        // and the transport's step-back.
        private void SeekTo(float t)
        {
            if (_effect == null) return;
            _scrubT = Mathf.Clamp01(t);
            if (_miniFill != null) _miniFill.style.width = Length.Percent(_scrubT * 100f);

            float target = _scrubT * _duration;
            _effect.pause = true;
            _effect.Reinit();
            const float dt = 1f / 60f;
            int steps = Mathf.Clamp(Mathf.RoundToInt(target / dt), 0, 600);
            if (steps > 0) _effect.Simulate(dt, (uint)steps);
            UpdateLive();
        }
        // A Playback setting, modelled like the renderer's RField but backed by live component
        // props / tool prefs rather than a SerializedProperty: each carries a fav key, modified
        // test, reset, and a control factory whose `sync` re-reads the value into the control
        // (so duplicate copies — favorites group + section row — stay coherent, like Duration did).
        private sealed class PField
        {
            public string Id, Label, Tooltip;
            public string FavKey => "play:" + Id;
            public Func<bool> IsModified;
            public Action Reset;
            public Func<(VisualElement control, Action sync)> BuildControl;
        }

        // The playback settings, in display order. Rebuilt on demand (cheap — just descriptors);
        // closures capture _effect/_effects/_duration, not specific controls, so this is safe to
        // call for counts even with no target.
        private List<PField> BuildPlaybackFields()
        {
            var list = new List<PField>
            {
                new PField
                {
                    Id = "duration", Label = "Duration (s)",
                    Tooltip = "Length of the play/scrub timeline window before it loops.",
                    IsModified = () => !Mathf.Approximately(_duration, kDefaultDuration),
                    Reset = () => { _duration = kDefaultDuration; VfxControlState.SetTimelineDuration(_duration); UpdateLive(); },
                    BuildControl = () =>
                    {
                        var f = new FloatField { value = _duration };
                        f.RegisterValueChangedCallback(e =>
                        {
                            _duration = Mathf.Max(0.1f, e.newValue);
                            VfxControlState.SetTimelineDuration(_duration);
                            UpdateLive();
                            RefreshPlaybackRows();
                        });
                        return (f, () => f.SetValueWithoutNotify(_duration));
                    },
                },
                new PField
                {
                    Id = "seed", Label = "Start Seed",
                    Tooltip = "Random seed for the simulation (VisualEffect.startSeed). Reseed randomizes it and reinitializes.",
                    IsModified = () => _effect != null && (EffectsDiffer(ve => ve.startSeed) || _effect.startSeed != 0),
                    Reset = () => SetStartSeed(0),
                    BuildControl = BuildStartSeedControl,
                },
                new PField
                {
                    Id = "reseedOnPlay", Label = "Reseed on Play",
                    Tooltip = "Pick a new random seed each time the effect (re)starts. VisualEffect.resetSeedOnPlay.",
                    IsModified = () => _effect != null && (EffectsDiffer(ve => ve.resetSeedOnPlay) || _effect.resetSeedOnPlay != true),
                    Reset = () => SetResetSeedOnPlay(true),
                    BuildControl = () =>
                    {
                        var t = new Toggle { value = _effect != null && _effect.resetSeedOnPlay };
                        t.showMixedValue = EffectsDiffer(ve => ve.resetSeedOnPlay);
                        t.RegisterValueChangedCallback(e => { SetResetSeedOnPlay(e.newValue); RefreshPlaybackRows(); });
                        return (t, () =>
                        {
                            if (_effect != null) t.SetValueWithoutNotify(_effect.resetSeedOnPlay);
                            t.showMixedValue = EffectsDiffer(ve => ve.resetSeedOnPlay);
                        });
                    },
                },
                new PField
                {
                    Id = "event", Label = "Initial Event",
                    Tooltip = "Event sent when the effect starts (VisualEffect.initialEventName); defaults to OnPlay.",
                    IsModified = () => _effect != null && (EffectsDiffer(ve => InitEventOf(ve)) || InitEventOf(_effect) != "OnPlay"),
                    Reset = () => SetInitialEvent("OnPlay"),
                    BuildControl = () =>
                    {
                        var f = new TextField { value = _effect != null ? InitEventOf(_effect) : "OnPlay" };
                        f.showMixedValue = EffectsDiffer(ve => InitEventOf(ve));
                        f.RegisterValueChangedCallback(e => { SetInitialEvent(e.newValue); RefreshPlaybackRows(); });
                        return (f, () =>
                        {
                            if (_effect != null) f.SetValueWithoutNotify(InitEventOf(_effect));
                            f.showMixedValue = EffectsDiffer(ve => InitEventOf(ve));
                        });
                    },
                },
            };
            return list;
        }

        // initialEventName is empty by default but behaves as "OnPlay"; normalize for display/compare.
        private static string InitEventOf(VisualEffect ve) => string.IsNullOrEmpty(ve.initialEventName) ? "OnPlay" : ve.initialEventName;

        // Do the selected instances disagree on a value? (drives showMixedValue, like a multi-target SO.)
        private bool EffectsDiffer<T>(Func<VisualEffect, T> get)
        {
            if (_effect == null || _effects.Count <= 1) return false;
            var first = get(_effect);
            foreach (var ve in _effects)
                if (ve != null && !EqualityComparer<T>.Default.Equals(get(ve), first)) return true;
            return false;
        }

        private void BuildPlaybackTab(VisualElement body)
        {
            AddFavoriteGroup(body, includeProps: false, PlaybackFavoriteSettings());
            BuildPlaybackContent(body);
        }

        // The Playback content without the favorites group, so the All tab can stack it under one
        // unified favorites group. Two collapsible sections, both rail-filterable like the Renderer
        // tab's Probes/Additional: "Playback options" (the setting rows) and "Send Event" (the
        // event controls). The transport itself is NOT here — it lives once in the persistent top
        // bar (with the scrub).
        private void BuildPlaybackContent(VisualElement body)
        {
            string section = CurrentSection();
            bool InSection(string id) => section == "all" || section == id;

            var fields = BuildPlaybackFields();
            bool Show(PField f) => InSection("options") && SearchMatches(f.Label) && PlaybackChipOk(f);
            int shown = AddPlaybackSection(body, "options", "Playback options", fields, Show);

            // Send Event is an action section (favoritable but never "modified"): show it under
            // "All"/its own rail section in the unfiltered view, or under the ★ filter when pinned.
            // The Modified filter never includes it.
            bool eventsChipOk = _filter == "all" || (_filter == "fav" && IsFav(kSendEventFavKey));
            bool showEvents = _effect != null && InSection("events")
                              && eventsChipOk && string.IsNullOrEmpty(_search.Trim());
            if (showEvents) shown += AddSendEventSection(body);

            if (shown == 0)
            {
                BuildPlaceholder(body,
                    !string.IsNullOrEmpty(_search.Trim()) ? $"No playback settings match “{_search}”."
                    : _filter == "fav" ? "No favorite playback settings."
                    : _filter == "mod" ? "No modified playback settings."
                    : "No playback settings available.");
            }
        }

        // A collapsible "Playback options" group (styled like the renderer's section groups),
        // containing the visible playback setting rows. Returns the number of rows shown.
        private int AddPlaybackSection(VisualElement host, string id, string heading, List<PField> fields, Func<PField, bool> show)
        {
            var visible = fields.Where(show).ToList();
            if (visible.Count == 0) return 0;

            bool forceOpen = !string.IsNullOrEmpty(_search.Trim());
            var (_, content, _) = AddGroupShell(host, "play:" + id, heading, visible.Count, forceOpen);
            foreach (var f in visible) content.Add(BuildPlaybackRow(f));
            return visible.Count;
        }


        private bool PlaybackChipOk(PField f) =>
            _filter == "all" ||
            (_filter == "fav" && IsFav(f.FavKey)) ||
            (_filter == "mod" && f.IsModified());

        private List<Setting> PlaybackFavoriteSettings()
        {
            var list = new List<Setting>();
            foreach (var f in BuildPlaybackFields())
                if (IsFav(f.FavKey))
                    list.Add(new Setting { BuildRow = () => BuildPlaybackRow(f) });
            if (IsFav(kSendEventFavKey)) // the Send Event section pins as one unit
                list.Add(new Setting { BuildRow = BuildSendEventFavRow });
            return list;
        }

        // (leaf, fav, mod) counts for the Playback tab's filter chips. The Send Event section
        // counts as one extra leaf (favoritable, never "modified").
        private (int leaf, int fav, int mod) PlaybackChipCounts()
        {
            var fields = BuildPlaybackFields();
            int fav = fields.Count(f => IsFav(f.FavKey)) + (IsFav(kSendEventFavKey) ? 1 : 0);
            return (fields.Count + 1, fav, fields.Count(f => f.IsModified()));
        }

        // A playback setting row, styled like any property/renderer row (label · control · hover ↺/★).
        // These back live component props / tool prefs (not SerializedProperties), so edits sync the
        // (possibly two) visible copies via RefreshPlaybackRows rather than binding.
        private VisualElement BuildPlaybackRow(PField f)
        {
            var (control, sync) = f.BuildControl();

            var row = MakeElement("vfx-row");
            row.EnableInClassList("vfx-row--modified", f.IsModified());
            if (IsFav(f.FavKey)) row.AddToClassList("vfx-row--fav");

            var labelCol = MakeElement("vfx-label-col");
            var label = new Label(f.Label) { tooltip = f.Tooltip ?? f.Label };
            label.AddToClassList("vfx-plabel");
            labelCol.Add(label);
            row.Add(labelCol);

            row.Add(MakeElement("vfx-row-lock")); // align with the other rows' lock gutter

            control.AddToClassList("vfx-pcontrol");
            AttachLabelDragger(label, control); // drag the label to scrub numeric fields (no-op otherwise)
            row.Add(control);

            var tools = MakeElement("vfx-row-tools");
            var reset = MakeIconButton("↺", "Reset to default", () => { f.Reset(); RefreshPlaybackRows(); });
            reset.AddToClassList("vfx-tool-reset");
            tools.Add(reset);
            var star = MakeIconButton(IsFav(f.FavKey) ? "★" : "☆", IsFav(f.FavKey) ? "Unpin" : "Pin", () => ToggleFav(f.FavKey));
            star.AddToClassList("vfx-tool-fav");
            tools.Add(star);
            row.Add(tools);

            _playbackRows.Add((row, f, sync));
            return row;
        }

        // Start Seed is meaningless when Reseed-on-Play is on (the seed is re-randomized each
        // (re)start), so the control greys out to match. Mixed multi-edit → leave it editable
        // (ambiguous, like the category gate treats mixed as enabled).
        private bool SeedLocked() => _effect != null && _effect.resetSeedOnPlay && !EffectsDiffer(ve => ve.resetSeedOnPlay);

        // Start Seed: an int field (clamped ≥ 0 → uint, like the uint property control) plus an
        // inline Reseed button that randomizes the seed and reinitializes the sim.
        private (VisualElement control, Action sync) BuildStartSeedControl()
        {
            var wrap = MakeElement("vfx-seed-control");
            var field = new IntegerField { value = _effect != null ? (int)_effect.startSeed : 0 };
            field.AddToClassList("vfx-seed-int"); // marks it as the label-drag target (see AttachLabelDragger)
            field.showMixedValue = EffectsDiffer(ve => ve.startSeed);
            field.style.flexGrow = 1;
            field.RegisterValueChangedCallback(e =>
            {
                // Locked by Reseed on Play — ignore edits (incl. label-drag, whose drag zone is
                // the label outside this disabled wrap) and revert the display.
                if (SeedLocked()) { field.SetValueWithoutNotify(_effect != null ? (int)_effect.startSeed : 0); return; }
                SetStartSeed((uint)Mathf.Max(0, e.newValue));
                RefreshPlaybackRows();
            });
            wrap.Add(field);

            var reseed = MakeIconButton("⚄", "Reseed (randomize + reinitialize)", () => { Reseed(); RefreshPlaybackRows(); });
            reseed.AddToClassList("vfx-seed-reseed");
            wrap.Add(reseed);

            wrap.SetEnabled(!SeedLocked()); // grey out while Reseed on Play overrides the seed

            return (wrap, () =>
            {
                if (_effect != null) field.SetValueWithoutNotify((int)_effect.startSeed);
                field.showMixedValue = EffectsDiffer(ve => ve.startSeed);
                wrap.SetEnabled(!SeedLocked()); // re-evaluate live when Reseed on Play toggles
            });
        }

        // A transport button: either a built-in editor icon (iconName, optionally mirrored
        // horizontally) or a text glyph.
        private Button MakeTransportButton(string tooltip, string iconName, Action onClick, string glyph = null, bool mirror = false)
        {
            var b = new Button(onClick) { tooltip = tooltip };
            b.AddToClassList("vfx-tbtn");
            if (!string.IsNullOrEmpty(iconName))
            {
                var img = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                img.style.width = 16; img.style.height = 16;
                img.image = EditorGUIUtility.IconContent(iconName).image;
                if (mirror) img.style.scale = new Scale(new Vector3(-1f, 1f, 1f)); // flip to point the other way
                b.Add(img);
            }
            else if (glyph != null)
            {
                b.text = glyph;
            }
            return b;
        }

        // Step one frame forward (simulate) or backward (reinit + resimulate — the GPU sim has no
        // backward step; see the scrubbing caveat). Pauses, like the mini-transport step.
        private void StepFrame(int dir)
        {
            if (_effect == null) return;
            const float dt = 1f / 60f;
            if (dir > 0)
            {
                _effect.pause = true;
                _effect.Simulate(dt, 1);
                _scrubT = Mathf.Min(1f, _scrubT + dt / Mathf.Max(0.0001f, _duration));
                UpdateLive();
            }
            else
            {
                SeekTo(_scrubT - dt / Mathf.Max(0.0001f, _duration));
            }
        }


        // ---- playback property setters (write to every selected instance, undo-tracked) ----

        private void SetPlayRate(float v)
        {
            v = Mathf.Max(0f, v);
            Undo.RecordObjects(_effects.ToArray(), "Set Play Rate");
            foreach (var ve in _effects) if (ve != null) { ve.playRate = v; EditorUtility.SetDirty(ve); }
        }

        private void SetStartSeed(uint v)
        {
            Undo.RecordObjects(_effects.ToArray(), "Set Start Seed");
            foreach (var ve in _effects) if (ve != null) { ve.startSeed = v; EditorUtility.SetDirty(ve); }
        }

        private void SetResetSeedOnPlay(bool v)
        {
            Undo.RecordObjects(_effects.ToArray(), "Set Reset Seed On Play");
            foreach (var ve in _effects) if (ve != null) { ve.resetSeedOnPlay = v; EditorUtility.SetDirty(ve); }
        }

        private void SetInitialEvent(string v)
        {
            Undo.RecordObjects(_effects.ToArray(), "Set Initial Event");
            foreach (var ve in _effects) if (ve != null) { ve.initialEventName = v; EditorUtility.SetDirty(ve); }
        }

        // Randomize the seed on every instance and reinitialize so it takes effect immediately.
        private void Reseed()
        {
            Undo.RecordObjects(_effects.ToArray(), "Reseed VFX");
            foreach (var ve in _effects)
                if (ve != null)
                {
                    ve.startSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
                    ve.Reinit();
                    EditorUtility.SetDirty(ve);
                }
        }

        // Keep every visible playback row (favorites copy + section copy) + chrome in sync.
        private void RefreshPlaybackRows()
        {
            foreach (var (row, f, sync) in _playbackRows)
            {
                sync();
                row.EnableInClassList("vfx-row--modified", f.IsModified());
            }
            PopulateChips();
            UpdateFooter();
        }
    }
}
