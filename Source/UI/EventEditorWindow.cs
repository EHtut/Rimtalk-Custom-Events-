using System;
using System.Collections.Generic;
using System.Globalization;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimTalkCustomEvents.UI
{
    /// <summary>
    /// Edits one event and writes it back to its JSON file.
    ///
    /// Edits a working copy, so cancelling changes nothing on disk and a half-finished
    /// edit can't be picked up by the scheduler mid-event.
    /// </summary>
    public class EventEditorWindow : Window
    {
        private readonly CustomEvent _event;
        private readonly bool _isNew;
        private readonly bool _sourceHasComments;

        private Vector2 _scroll;
        private float _contentHeight = 1400f;

        // Numeric fields are edited as text so a half-typed value doesn't snap.
        private readonly Dictionary<string, string> _buffers = new Dictionary<string, string>();

        public EventEditorWindow(CustomEvent existing)
        {
            _isNew = existing == null;
            _event = existing != null ? Clone(existing) : NewEvent();
            _sourceHasComments = !_isNew && EventWriter.HasComments(_event.SourcePath);

            doCloseX = true;
            absorbInputAroundWindow = true;
            forcePause = true;
        }

        public override Vector2 InitialSize => new Vector2(760f, 800f);

        /// <summary>
        /// Deep copy, so the editor never touches the event the scheduler is reading.
        /// Without this, Cancel wouldn't undo anything and a running instance could pick up
        /// half-typed text mid-beat — it looks up its definition from the store every tick.
        ///
        /// Goes through the writer and parser rather than a hand-written copy, so it stays
        /// faithful automatically as the schema grows; the round-trip tests already prove
        /// that path preserves everything.
        /// </summary>
        private static CustomEvent Clone(CustomEvent source)
        {
            try
            {
                return CustomEvent.FromJson(Json.Parse(EventWriter.ToJson(source)), source.SourcePath);
            }
            catch (Exception ex)
            {
                RTCELog.Error(
                    $"Could not copy \"{source.DefName}\" for editing, so edits will apply live "
                    + $"and Cancel will not undo them: {ex.Message}");
                return source;
            }
        }

        private static CustomEvent NewEvent()
        {
            var e = new CustomEvent
            {
                DefName = "NewEvent",
                Label = "new event",
                Description = ""
            };

            e.Phases.Beginning.Text = "Something begins to happen to them.";
            e.Phases.Continue.Text = "It grows steadily worse as the day wears on.";
            e.Phases.End.Text = "It reaches its peak, and then passes.";
            e.Timing.ContinueCount = 3;
            return e;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var footer = 40f;
            var bodyRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - footer);
            var viewRect = new Rect(0f, 0f, bodyRect.width - 20f, _contentHeight);

            Widgets.BeginScrollView(bodyRect, ref _scroll, viewRect);

            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            DrawIdentity(listing);
            DrawPhases(listing);
            DrawPreview(listing);
            DrawTiming(listing);
            DrawTrigger(listing);
            DrawTarget(listing);

            _contentHeight = listing.CurHeight + 30f;
            listing.End();
            Widgets.EndScrollView();

            DrawFooter(new Rect(inRect.x, inRect.yMax - footer + 6f, inRect.width, footer - 6f));
        }

        // ------------------------------------------------------------- identity

        private void DrawIdentity(Listing_Standard listing)
        {
            Text.Font = GameFont.Medium;
            listing.Label(_isNew ? "New event" : $"Editing: {_event.Label}");
            Text.Font = GameFont.Small;

            if (_sourceHasComments)
            {
                var previous = GUI.color;
                GUI.color = Color.yellow;
                listing.Label("This file has comments. Saving rewrites it and the comments go — "
                              + "the original is kept alongside as a .bak.");
                GUI.color = previous;
            }

            listing.Label("Name (also the filename)");
            _event.DefName = listing.TextEntry(_event.DefName ?? "");

            listing.Label("Label — what you see in lists and prompts");
            _event.Label = listing.TextEntry(_event.Label ?? "");

            listing.Label("Description");
            _event.Description = listing.TextEntry(_event.Description ?? "", 2);

            var enabled = _event.Enabled;
            listing.CheckboxLabeled("Enabled", ref enabled);
            _event.Enabled = enabled;

            listing.GapLine();
        }

        // --------------------------------------------------------------- phases

        private void DrawPhases(Listing_Standard listing)
        {
            Text.Font = GameFont.Medium;
            listing.Label("Phases");
            Text.Font = GameFont.Small;

            DrawPhase(listing, "BEGINNING — fires once and starts the event", _event.Phases.Beginning);

            DrawContinueMode(listing);
            DrawPhase(listing, ContinueHeading(), _event.Phases.Continue);

            DrawPhase(listing, "END — the payoff", _event.Phases.End);

            listing.GapLine();
        }

        private string ContinueHeading()
        {
            switch (_event.Phases.Continue.Mode)
            {
                case ContinueMode.Modifier:
                    return "CONTINUE — folded into everything they say";
                case ContinueMode.Beat:
                    return "CONTINUE — pulses that grow stronger";
                default:
                    return "CONTINUE — replayed as its own line each beat";
            }
        }

        /// <summary>Mode dropdown, plus the intensity range when Beat mode needs it.</summary>
        private void DrawContinueMode(Listing_Standard listing)
        {
            var continuePhase = _event.Phases.Continue;

            listing.Gap(8f);

            var row = listing.GetRect(30f);
            Widgets.Label(new Rect(row.x, row.y + 4f, 110f, 24f), "CONTINUE is a:");

            if (Widgets.ButtonText(new Rect(row.x + 114f, row.y, row.width - 114f, 28f),
                    DescribeMode(continuePhase.Mode)))
            {
                var options = new List<FloatMenuOption>();
                foreach (ContinueMode mode in Enum.GetValues(typeof(ContinueMode)))
                {
                    var captured = mode;
                    options.Add(new FloatMenuOption(DescribeMode(captured), () =>
                    {
                        continuePhase.Mode = captured;

                        // Beat mode is pointless without a range, so give it one the first
                        // time it's selected rather than leaving it flat at 1.
                        if (captured == ContinueMode.Beat && continuePhase.IntensityFrom >= 1f
                                                          && continuePhase.IntensityTo >= 1f)
                        {
                            continuePhase.IntensityFrom = 0.2f;
                            continuePhase.IntensityTo = 1f;
                        }
                    }));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }

            Text.Font = GameFont.Tiny;
            var previous = GUI.color;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            listing.Label(ModeHelp(continuePhase.Mode));
            GUI.color = previous;
            Text.Font = GameFont.Small;

            if (continuePhase.Mode != ContinueMode.Beat) return;

            listing.Label($"Starts at: {continuePhase.IntensityFrom:0.00}  ({PromptBuilder.DescribeIntensity(continuePhase.IntensityFrom)})");
            continuePhase.IntensityFrom = listing.Slider(continuePhase.IntensityFrom, 0f, 1f);

            listing.Label($"Builds to: {continuePhase.IntensityTo:0.00}  ({PromptBuilder.DescribeIntensity(continuePhase.IntensityTo)})");
            continuePhase.IntensityTo = listing.Slider(continuePhase.IntensityTo, 0f, 1f);

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            listing.Label("Use {intensity} in the text to let the wording follow it, and tick "
                          + "\"scale with intensity\" on an effect to make its numbers follow too.");
            GUI.color = previous;
            Text.Font = GameFont.Small;
        }

        private static string DescribeMode(ContinueMode mode)
        {
            switch (mode)
            {
                case ContinueMode.Modifier: return "Modifier — colours all their dialogue";
                case ContinueMode.Beat: return "Beat — pulses, growing stronger";
                default: return "Prompt — a spoken line each beat";
            }
        }

        private static string ModeHelp(ContinueMode mode)
        {
            switch (mode)
            {
                case ContinueMode.Modifier:
                    return "No lines of its own. The text rides along with whatever the pawn was already "
                           + "saying, so it colours their ordinary chatter. Example: \"Your skin is cold.\"";
                case ContinueMode.Beat:
                    return "Discrete pulses that climb in intensity. Example: \"Your skin grows colder\", "
                           + "with the temperature penalty deepening each time.";
                default:
                    return "The pawn is prompted to speak about it, once per beat.";
            }
        }

        private void DrawPhase(Listing_Standard listing, string heading, PhaseSpec phase)
        {
            listing.Gap(6f);
            listing.Label(heading);
            phase.Text = listing.TextEntry(phase.Text ?? "", 4);

            foreach (var effect in new List<PhaseEffect>(phase.Effects))
            {
                var row = listing.GetRect(26f);
                var removeRect = new Rect(row.xMax - 26f, row.y, 24f, 24f);

                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 8f, row.y + 3f, row.width - 40f, row.height), DescribeEffect(effect));
                Text.Font = GameFont.Small;

                if (Widgets.ButtonText(removeRect, "×"))
                {
                    phase.Effects.Remove(effect);
                }
            }

            if (phase.Mode == ContinueMode.Beat && phase.Effects.Count > 0)
            {
                foreach (var effect in phase.Effects)
                {
                    if (effect.HasOneOf) continue;
                    var scale = effect.ScaleWithIntensity;
                    listing.CheckboxLabeled($"   scale \"{DescribeEffect(effect)}\" with intensity", ref scale);
                    effect.ScaleWithIntensity = scale;
                }
            }

            if (listing.ButtonText("Add effect…", null, 0.35f))
            {
                ShowAddEffectMenu(phase);
            }
        }

        private static string DescribeEffect(PhaseEffect fx)
        {
            if (fx.HasOneOf)
            {
                return $"weighted table, {fx.OneOf.Count} arms — edit this one in the file";
            }

            var bits = new List<string>();
            if (fx.Hediff != null) bits.Add($"hediff {fx.Hediff.Def} {(fx.Hediff.Mode == HediffApplyMode.Add ? "+" : "=")}{fx.Hediff.Severity:0.##}");
            if (!string.IsNullOrEmpty(fx.Thought)) bits.Add($"thought {fx.Thought}");
            if (fx.Need != null) bits.Add($"need {fx.Need.Def} {fx.Need.Offset:+0.##;-0.##}");
            if (!string.IsNullOrEmpty(fx.Incident)) bits.Add($"incident {fx.Incident}");
            if (!string.IsNullOrEmpty(fx.ChainEvent)) bits.Add($"chain {fx.ChainEvent}");
            if (!string.IsNullOrEmpty(fx.TraitDef)) bits.Add($"trait {fx.TraitDef}{(fx.TraitRemove ? " (remove)" : "")}");
            if (!string.IsNullOrEmpty(fx.SkillXpSkill)) bits.Add($"{fx.SkillXpAmount:0} {fx.SkillXpSkill} XP");
            foreach (var item in fx.Items) bits.Add($"{item.Count}x {item.Def}");
            if (!string.IsNullOrEmpty(fx.Message)) bits.Add("message");
            if (fx.Nothing) bits.Add("nothing");
            if (fx.Chance < 1f) bits.Add($"{fx.Chance:P0} chance");

            return bits.Count == 0 ? "(empty effect)" : string.Join(", ", bits.ToArray());
        }

        private void ShowAddEffectMenu(PhaseSpec phase)
        {
            var options = new List<FloatMenuOption>
            {
                Pick(phase, "Hediff", DefKind.Hediff, (fx, entry) =>
                    fx.Hediff = new EffectHediff { Def = entry.DefName, Severity = 0.2f }),

                Pick(phase, "Thought (mood)", DefKind.Thought, (fx, entry) => fx.Thought = entry.DefName),

                Pick(phase, "Need offset", DefKind.Need, (fx, entry) =>
                    fx.Need = new EffectNeed { Def = entry.DefName, Offset = -0.1f }),

                Pick(phase, "Incident", DefKind.Incident, (fx, entry) => fx.Incident = entry.DefName),

                Pick(phase, "Trait", DefKind.Trait, (fx, entry) => fx.TraitDef = entry.DefName),

                Pick(phase, "Skill XP", DefKind.Skill, (fx, entry) =>
                {
                    fx.SkillXpSkill = entry.DefName;
                    fx.SkillXpAmount = 1000f;
                }),

                Pick(phase, "Item reward", DefKind.Item, (fx, entry) =>
                    fx.Items.Add(new EffectItem { Def = entry.DefName, Count = 1 })),

                new FloatMenuOption("Chain another event", () => ShowChainMenu(phase))
            };

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private FloatMenuOption Pick(PhaseSpec phase, string label, DefKind kind, Action<PhaseEffect, DefEntry> apply)
        {
            return new FloatMenuOption(label, () =>
                Find.WindowStack.Add(new DefPickerWindow(kind, $"Choose a {label.ToLowerInvariant()}", entry =>
                {
                    var fx = new PhaseEffect();
                    apply(fx, entry);
                    phase.Effects.Add(fx);
                })));
        }

        private void ShowChainMenu(PhaseSpec phase)
        {
            var options = new List<FloatMenuOption>();

            foreach (var other in EventStore.All)
            {
                if (other.DefName == _event.DefName) continue; // no self-chaining
                var captured = other;
                options.Add(new FloatMenuOption(captured.Label, () =>
                    phase.Effects.Add(new PhaseEffect { ChainEvent = captured.DefName })));
            }

            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("(no other events to chain to)", null));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        // -------------------------------------------------------------- preview

        private bool _showPreview;

        /// <summary>
        /// Exactly what RimTalk receives, wrapper and all — built through the same
        /// PromptBuilder the scheduler uses, so this can't drift from the real thing.
        /// </summary>
        private void DrawPreview(Listing_Standard listing)
        {
            if (listing.ButtonText(_showPreview ? "Hide prompt preview" : "Show prompt preview", null, 0.45f))
            {
                _showPreview = !_showPreview;
            }

            if (!_showPreview)
            {
                listing.GapLine();
                return;
            }

            var wrapper = RimTalkCustomEventsMod.Settings?.promptWrapper;
            var label = string.IsNullOrEmpty(_event.Label) ? _event.DefName : _event.Label;
            var beats = _event.Timing.ContinueCount;

            var previous = GUI.color;
            GUI.color = new Color(0.7f, 0.85f, 0.7f);
            Text.Font = GameFont.Tiny;

            listing.Label("This is the text sent to RimTalk. \"Colonist\" stands in for the pawn's name.");

            Preview(listing, wrapper, label, "BEGINNING", 1, beats, _event.Phases.Beginning.Text);

            if (_event.HasContinueText && beats > 0)
            {
                // Show the middle beat: the index and total are what change between them.
                var middle = Math.Max(1, (beats + 1) / 2);
                Preview(listing, wrapper, label, "CONTINUE", middle, beats, _event.Phases.Continue.Text);
            }

            Preview(listing, wrapper, label, "END", 1, beats, _event.Phases.End.Text);

            Text.Font = GameFont.Small;
            GUI.color = previous;
            listing.GapLine();
        }

        private static void Preview(Listing_Standard listing, string wrapper, string eventLabel,
            string phaseName, int index, int total, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var phaseLabel = PromptBuilder.PhaseLabel(phaseName, index, total);
            listing.Label(PromptBuilder.Build(wrapper, eventLabel, phaseLabel, phaseName,
                index, total, "Colonist", text));
            listing.Gap(4f);
        }

        // --------------------------------------------------------------- timing

        private void DrawTiming(Listing_Standard listing)
        {
            Text.Font = GameFont.Medium;
            listing.Label("Timing");
            Text.Font = GameFont.Small;

            _event.Timing.DurationHours = NumberField(listing, "Duration (in-game hours)", "dur",
                _event.Timing.DurationHours, 0.5f, 240f);

            _event.Timing.ContinueCount = (int)NumberField(listing, "CONTINUE beats", "beats",
                _event.Timing.ContinueCount, 0f, 24f);

            if (listing.ButtonText($"Spacing: {_event.Timing.Spacing}", null, 0.4f))
            {
                _event.Timing.Spacing = _event.Timing.Spacing == ContinueSpacing.Even
                    ? ContinueSpacing.Random
                    : ContinueSpacing.Even;
            }

            if (_event.Timing.Spacing == ContinueSpacing.Even)
            {
                listing.Label($"Jitter: {_event.Timing.ContinueJitter:0.00}");
                _event.Timing.ContinueJitter = listing.Slider(_event.Timing.ContinueJitter, 0f, 1f);
            }

            if (listing.ButtonText($"When a pawn can't speak: {_event.Timing.OnBlocked}", null, 0.5f))
            {
                _event.Timing.OnBlocked = Cycle(_event.Timing.OnBlocked);
            }

            listing.GapLine();
        }

        private static BlockedPolicy Cycle(BlockedPolicy policy)
        {
            switch (policy)
            {
                case BlockedPolicy.Retry: return BlockedPolicy.Skip;
                case BlockedPolicy.Skip: return BlockedPolicy.Abort;
                default: return BlockedPolicy.Retry;
            }
        }

        // -------------------------------------------------------------- trigger

        private void DrawTrigger(Listing_Standard listing)
        {
            Text.Font = GameFont.Medium;
            listing.Label("Trigger");
            Text.Font = GameFont.Small;

            if (listing.ButtonText($"Mode: {_event.Trigger.Mode}", null, 0.5f))
            {
                var options = new List<FloatMenuOption>();
                foreach (TriggerMode mode in Enum.GetValues(typeof(TriggerMode)))
                {
                    var captured = mode;
                    options.Add(new FloatMenuOption(Describe(captured), () => _event.Trigger.Mode = captured));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }

            if (_event.Trigger.Mode == TriggerMode.Daily)
            {
                _event.Trigger.DailyHour = (int)NumberField(listing, "Hour of day (0-23)", "dailyHour",
                    _event.Trigger.DailyHour, 0f, 23f);
                _event.Trigger.DailyChance = NumberField(listing, "Chance each day (0-1)", "dailyChance",
                    _event.Trigger.DailyChance, 0f, 1f);
            }
            else if (_event.Trigger.Mode != TriggerMode.Manual)
            {
                _event.Trigger.MtbDays = NumberField(listing, "Average days between", "mtb",
                    _event.Trigger.MtbDays, 0.1f, 360f);
            }

            if (_event.Trigger.Mode != TriggerMode.Manual)
            {
                _event.Trigger.MinRefireDays = NumberField(listing, "Minimum days before it can repeat", "refire",
                    _event.Trigger.MinRefireDays, 0f, 360f);

                var hasWindow = _event.Trigger.StartHourMin.HasValue;
                var wantsWindow = hasWindow;
                listing.CheckboxLabeled("Only start within a time window", ref wantsWindow);

                if (wantsWindow && !hasWindow)
                {
                    _event.Trigger.StartHourMin = 6;
                    _event.Trigger.StartHourMax = 20;
                }
                else if (!wantsWindow && hasWindow)
                {
                    _event.Trigger.StartHourMin = null;
                    _event.Trigger.StartHourMax = null;
                }

                if (_event.Trigger.StartHourMin.HasValue)
                {
                    _event.Trigger.StartHourMin = (int)NumberField(listing, "From hour", "winMin",
                        _event.Trigger.StartHourMin.Value, 0f, 23f);
                    _event.Trigger.StartHourMax = (int)NumberField(listing, "To hour", "winMax",
                        _event.Trigger.StartHourMax.Value, 0f, 23f);
                }
            }

            listing.GapLine();
        }

        private static string Describe(TriggerMode mode)
        {
            switch (mode)
            {
                case TriggerMode.Daily: return "Daily — at a set hour";
                case TriggerMode.Occasionally: return "Occasionally — random intervals";
                default: return "Manual — only when you fire it";
            }
        }

        // --------------------------------------------------------------- target

        private void DrawTarget(Listing_Standard listing)
        {
            Text.Font = GameFont.Medium;
            listing.Label("Target");
            Text.Font = GameFont.Small;

            _event.Target.CooldownDays = NumberField(listing, "Days before it can hit the same pawn again", "cooldown",
                _event.Target.CooldownDays, 0f, 360f);

            listing.Label($"Required traits: {Join(_event.Target.RequiredTraits)}");
            DrawListButtons(listing, _event.Target.RequiredTraits, DefKind.Trait, "required trait");

            listing.Label($"Excluded traits: {Join(_event.Target.ExcludedTraits)}");
            DrawListButtons(listing, _event.Target.ExcludedTraits, DefKind.Trait, "excluded trait");

            listing.Label($"Blocked by hediffs: {Join(_event.Target.ExcludedHediffs)}");
            DrawListButtons(listing, _event.Target.ExcludedHediffs, DefKind.Hediff, "blocking hediff");

            listing.Gap(6f);
        }

        private static string Join(List<string> values)
        {
            return values.Count == 0 ? "(none)" : string.Join(", ", values.ToArray());
        }

        private void DrawListButtons(Listing_Standard listing, List<string> values, DefKind kind, string what)
        {
            var row = listing.GetRect(26f);
            var half = row.width * 0.25f;

            if (Widgets.ButtonText(new Rect(row.x, row.y, half, 24f), "Add"))
            {
                Find.WindowStack.Add(new DefPickerWindow(kind, $"Choose a {what}", entry =>
                {
                    if (!values.Contains(entry.DefName)) values.Add(entry.DefName);
                }));
            }

            if (values.Count > 0 && Widgets.ButtonText(new Rect(row.x + half + 6f, row.y, half, 24f), "Clear"))
            {
                values.Clear();
            }
        }

        // --------------------------------------------------------------- footer

        private void DrawFooter(Rect rect)
        {
            var third = rect.width / 3f;

            if (Widgets.ButtonText(new Rect(rect.x, rect.y, third - 6f, 32f), "Cancel"))
            {
                Close();
            }

            if (Widgets.ButtonText(new Rect(rect.x + third * 2f, rect.y, third, 32f), "Save"))
            {
                Save();
            }
        }

        private void Save()
        {
            var errors = _event.Validate(out var warnings);

            if (errors.Count > 0)
            {
                Messages.Message("Can't save: " + string.Join("; ", errors.ToArray()),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            foreach (var warning in warnings) RTCELog.Warning($"\"{_event.DefName}\": {warning}");

            var path = EventWriter.Save(_event, EventStore.EventsFolder, out var error);
            if (path == null)
            {
                Messages.Message($"Couldn't save: {error}", MessageTypeDefOf.RejectInput, false);
                return;
            }

            // Re-read from disk so what's running matches what's on the file, and so a
            // renamed event doesn't leave a stale entry behind.
            EventStore.Reload();
            Effects.HediffFactory.RegisterAll();

            Messages.Message($"Saved \"{_event.Label}\".", MessageTypeDefOf.TaskCompletion, false);
            Close();
        }

        /// <summary>
        /// A labelled numeric field backed by a text buffer, so typing "1" on the way to
        /// "12" doesn't immediately clamp and fight the user.
        /// </summary>
        private float NumberField(Listing_Standard listing, string label, string key, float value, float min, float max)
        {
            var row = listing.GetRect(26f);
            Widgets.Label(new Rect(row.x, row.y, row.width * 0.62f, row.height), label);

            var fieldRect = new Rect(row.x + row.width * 0.64f, row.y, row.width * 0.34f, 24f);

            if (!_buffers.TryGetValue(key, out var buffer))
            {
                buffer = value.ToString("0.###", CultureInfo.InvariantCulture);
            }

            var typed = Widgets.TextField(fieldRect, buffer);
            _buffers[key] = typed;

            if (float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return Mathf.Clamp(parsed, min, max);
            }

            // Unparseable (mid-typing, or empty) — keep the previous value.
            return value;
        }
    }
}
