using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Scheduling;
using RimTalkCustomEvents.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimTalkCustomEvents.UI
{
    /// <summary>
    /// The events list and editor, drawn straight into the mod options page.
    ///
    /// Laid out like a task manager: a Name/Status row per event, each one expandable to
    /// reveal its editor underneath. Editing in place beats a separate window because the
    /// list stays visible, and there's no modal to lose your position in.
    ///
    /// An expanded row edits a **working copy**. Nothing reaches disk or the scheduler
    /// until Save, so collapsing or discarding leaves the running event untouched.
    /// </summary>
    public static class EventListEditor
    {
        private const float RowHeight = 28f;

        private static string _expandedDefName;
        private static CustomEvent _draft;
        private static bool _draftHasComments;

        /// <summary>Per-field text buffers so a half-typed number doesn't snap.</summary>
        private static readonly Dictionary<string, string> Buffers = new Dictionary<string, string>();

        // RimWorld draws UI on the same thread it ticks, so per-row work here competes with
        // the simulation. These are rebuilt once per Draw instead of once per row.
        private static readonly List<CustomEvent> SortedEvents = new List<CustomEvent>();
        private static readonly Dictionary<string, int> RunningCounts = new Dictionary<string, int>();
        private static int _cacheStamp = -1;

        /// <summary>
        /// Rebuilds the per-draw caches. Sorting and counting once beats doing either per
        /// row; with a long event list the difference lands squarely on the tick thread.
        /// </summary>
        private static void RefreshCaches()
        {
            // One stamp per frame: Draw runs once per frame, so this just avoids repeating
            // the work if it's ever called twice.
            if (_cacheStamp == Time.frameCount) return;
            _cacheStamp = Time.frameCount;

            SortedEvents.Clear();
            SortedEvents.AddRange(EventStore.All);
            SortedEvents.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));

            RunningCounts.Clear();
            var component = CustomEventsGameComponent.Current;
            if (component == null) return;

            foreach (var instance in component.ActiveInstances)
            {
                if (string.IsNullOrEmpty(instance.EventDefName)) continue;
                RunningCounts.TryGetValue(instance.EventDefName, out var count);
                RunningCounts[instance.EventDefName] = count + 1;
            }
        }

        public static void Draw(Listing_Standard listing)
        {
            RefreshCaches();
            DrawToolbar(listing);
            DrawProblems(listing);

            if (EventStore.Count == 0 && _draft == null)
            {
                DrawEmptyState(listing);
                return;
            }

            listing.Gap(4f);
            DrawHeaderRow(listing);

            foreach (var e in SortedEvents)
            {
                DrawEventRow(listing, e);
            }

            // A brand new event has no store entry yet, so it gets its own row at the end.
            if (_draft != null && EventStore.Get(_draft.DefName) == null)
            {
                DrawNewDraftRow(listing);
            }
        }

        // ------------------------------------------------------------- toolbar

        private static void DrawToolbar(Listing_Standard listing)
        {
            var row = listing.GetRect(30f);
            var quarter = row.width / 4f;

            if (Widgets.ButtonText(new Rect(row.x, row.y, quarter - 4f, row.height), "New event"))
            {
                BeginNewDraft();
            }

            if (Widgets.ButtonText(new Rect(row.x + quarter, row.y, quarter - 4f, row.height), "Reload"))
            {
                Collapse();
                EventStore.Reload();
                Effects.HediffFactory.RegisterAll();
                Messages.Message($"Reloaded {EventStore.Count} event(s).",
                    EventStore.LoadProblems.Count > 0 ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.TaskCompletion,
                    false);
            }

            if (Widgets.ButtonText(new Rect(row.x + quarter * 2f, row.y, quarter - 4f, row.height), "Diagnostics"))
            {
                Find.WindowStack.Add(new DiagnosticsWindow());
            }

            if (Widgets.ButtonText(new Rect(row.x + quarter * 3f, row.y, quarter, row.height), "Active events"))
            {
                Find.WindowStack.Add(new ActiveEventsWindow());
            }
        }

        private static void DrawProblems(Listing_Standard listing)
        {
            foreach (var problem in EventStore.LoadProblems)
            {
                var previous = GUI.color;
                GUI.color = Color.red;
                listing.Label($"{problem.FileName} — {problem.Problem}");
                GUI.color = previous;
            }
        }

        private static void DrawEmptyState(Listing_Standard listing)
        {
            listing.GapLine();
            var previous = GUI.color;
            GUI.color = Color.yellow;
            listing.Label("No events are loaded.");
            GUI.color = previous;

            listing.Label("Events are JSON files in:" + Environment.NewLine + EventStore.EventsFolder);
            listing.Label("Use New event above, or drop a .json file in that folder and hit Reload. "
                          + "Diagnostics reports why a file didn't load.");

            if (listing.ButtonText("Open folder", null, 0.4f)) Application.OpenURL(EventStore.EventsFolder);
        }

        // ---------------------------------------------------------------- rows

        private static void DrawHeaderRow(Listing_Standard listing)
        {
            var row = listing.GetRect(22f);
            var previous = GUI.color;
            GUI.color = new Color(0.6f, 0.6f, 0.6f);
            Text.Font = GameFont.Tiny;

            Widgets.Label(new Rect(row.x + 26f, row.y, row.width * 0.45f, row.height), "Name");
            Widgets.Label(new Rect(row.x + row.width * 0.5f, row.y, row.width * 0.5f, row.height), "Status");

            Text.Font = GameFont.Small;
            GUI.color = previous;
            Widgets.DrawLineHorizontal(row.x, row.yMax, row.width);
        }

        private static void DrawEventRow(Listing_Standard listing, CustomEvent e)
        {
            var expanded = _expandedDefName == e.DefName;
            var row = listing.GetRect(RowHeight);

            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

            // Expander arrow.
            Widgets.Label(new Rect(row.x + 6f, row.y + 3f, 20f, row.height), expanded ? "v" : ">");

            var previous = GUI.color;
            if (!e.Enabled) GUI.color = new Color(0.6f, 0.6f, 0.6f);
            Widgets.Label(new Rect(row.x + 26f, row.y + 3f, row.width * 0.45f, row.height), e.Label);
            GUI.color = previous;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(new Rect(row.x + row.width * 0.5f, row.y + 5f, row.width * 0.5f, row.height), StatusOf(e));
            GUI.color = previous;
            Text.Font = GameFont.Small;

            if (Widgets.ButtonInvisible(row))
            {
                if (expanded) Collapse();
                else Expand(e);
            }

            Widgets.DrawLineHorizontal(row.x, row.yMax, row.width);

            if (expanded && _draft != null) DrawEditor(listing, e);
        }

        private static void DrawNewDraftRow(Listing_Standard listing)
        {
            var row = listing.GetRect(RowHeight);
            Widgets.DrawHighlight(row);
            Widgets.Label(new Rect(row.x + 6f, row.y + 3f, 20f, row.height), "v");
            Widgets.Label(new Rect(row.x + 26f, row.y + 3f, row.width * 0.45f, row.height), _draft.Label);

            Text.Font = GameFont.Tiny;
            var previous = GUI.color;
            GUI.color = new Color(0.9f, 0.85f, 0.5f);
            Widgets.Label(new Rect(row.x + row.width * 0.5f, row.y + 5f, row.width * 0.5f, row.height), "unsaved");
            GUI.color = previous;
            Text.Font = GameFont.Small;

            Widgets.DrawLineHorizontal(row.x, row.yMax, row.width);
            DrawEditor(listing, null);
        }

        /// <summary>The Status column: what this event is and when it fires.</summary>
        private static string StatusOf(CustomEvent e)
        {
            var bits = new List<string> { e.Enabled ? "enabled" : "disabled" };

            switch (e.Trigger.Mode)
            {
                case TriggerMode.Daily:
                    bits.Add($"daily {e.Trigger.DailyHour}:00");
                    break;
                case TriggerMode.Monthly:
                    bits.Add($"monthly {e.Trigger.DailyHour}:00");
                    break;
                case TriggerMode.Yearly:
                    bits.Add($"yearly {e.Trigger.DailyHour}:00");
                    break;
                case TriggerMode.Occasionally:
                    bits.Add($"~{e.Trigger.MtbDays:0.#}d");
                    break;
                default:
                    bits.Add("manual");
                    break;
            }

            bits.Add($"{e.Timing.DurationHours:0.#}h");

            var parts = new List<string>();
            if (e.Phases.Continue.HasBeat) parts.Add($"beat ×{e.Timing.ContinueCount}");
            if (e.Phases.Continue.HasModifier) parts.Add("modifier");
            if (parts.Count > 0) bits.Add(string.Join(" + ", parts.ToArray()));

            if (e.Target.Count > 1)
            {
                bits.Add($"{e.Target.Count} pawns, {(e.Target.Group == GroupMode.Shared ? "shared" : "separate")}");
            }

            if (!string.IsNullOrEmpty(e.Target.SpecificPawnName)) bits.Add(e.Target.SpecificPawnName + " only");
            if (e.Priority) bits.Add("priority");
            if (e.HasRunLimit) bits.Add($"max {e.MaxRunsPerSave}/save");

            if (RunningCounts.TryGetValue(e.DefName, out var running) && running > 0)
            {
                bits.Add($"RUNNING ×{running}");
            }

            return string.Join("   ·   ", bits.ToArray());
        }

        // -------------------------------------------------------------- expand

        private static void Expand(CustomEvent e)
        {
            _expandedDefName = e.DefName;
            _draft = Clone(e);
            _draftHasComments = EventWriter.HasComments(e.SourcePath);
            Buffers.Clear();
        }

        private static void BeginNewDraft()
        {
            var e = new CustomEvent { DefName = "NewEvent", Label = "new event" };
            e.Phases.Beginning.Text = "Something begins to happen to them.";
            e.Phases.Continue.Beat.Text = "It grows steadily worse as the day wears on.";
            e.Phases.End.Text = "It reaches its peak, and then passes.";
            e.Timing.ContinueCount = 3;

            _draft = e;
            _expandedDefName = e.DefName;
            _draftHasComments = false;
            Buffers.Clear();
        }

        public static void Collapse()
        {
            _expandedDefName = null;
            _draft = null;
            Buffers.Clear();
        }

        /// <summary>
        /// Deep copy through the writer and parser, so the editor never touches the object
        /// the scheduler reads and Cancel genuinely cancels.
        /// </summary>
        private static CustomEvent Clone(CustomEvent source)
        {
            try
            {
                return CustomEvent.FromJson(Json.Parse(EventWriter.ToJson(source)), source.SourcePath);
            }
            catch (Exception ex)
            {
                RTCELog.Error($"Could not copy \"{source.DefName}\" for editing: {ex.Message}");
                return source;
            }
        }

        // -------------------------------------------------------------- editor

        private static void DrawEditor(Listing_Standard listing, CustomEvent original)
        {
            var e = _draft;
            listing.Gap(6f);

            if (_draftHasComments)
            {
                var previous = GUI.color;
                GUI.color = Color.yellow;
                listing.Label("   This file has comments. Saving rewrites it — the original is kept as a .bak.");
                GUI.color = previous;
            }

            Indented(listing, () =>
            {
                listing.Label("Name (also the filename)");
                e.DefName = listing.TextEntry(e.DefName ?? "");

                listing.Label("Label");
                e.Label = listing.TextEntry(e.Label ?? "");

                listing.Label("Description");
                e.Description = listing.TextEntry(e.Description ?? "", 2);

                var enabled = e.Enabled;
                listing.CheckboxLabeled("Enabled", ref enabled);
                e.Enabled = enabled;

                // Opt-in, so 0 unambiguously means "never" rather than "no limit".
                var capped = e.HasRunLimit;
                listing.CheckboxLabeled("Limit how many times this can happen per save", ref capped,
                    "Off means it can happen as often as its trigger allows.");

                if (capped && !e.HasRunLimit) e.MaxRunsPerSave = 1;
                else if (!capped && e.HasRunLimit) e.MaxRunsPerSave = -1;

                if (e.HasRunLimit)
                {
                    e.MaxRunsPerSave = (int)Number(listing, "   Times per save", "maxRuns", e.MaxRunsPerSave, 0f, 9999f);

                    var component = CustomEventsGameComponent.Current;
                    if (component != null)
                    {
                        Text.Font = GameFont.Tiny;
                        var used = GUI.color;
                        GUI.color = new Color(0.7f, 0.7f, 0.7f);
                        listing.Label($"   Used {component.RunCount(e)} of {e.MaxRunsPerSave} in this save.");
                        GUI.color = used;
                        Text.Font = GameFont.Small;
                    }
                }

                var priority = e.Priority;
                listing.CheckboxLabeled("Cut in over whatever they were saying", ref priority,
                    "Discards their unspoken queued lines and starts the event immediately. What they "
                    + "were about to say is fed into the opening prompt, so it picks up mid-thought.");
                e.Priority = priority;

                listing.GapLine();

                // ---- BEGINNING ----
                Heading(listing, "BEGINNING — fires once, starts the event");
                e.Phases.Beginning.Text = listing.TextEntry(e.Phases.Beginning.Text ?? "", 3);
                DrawEffects(listing, e.Phases.Beginning.Effects, false);

                listing.GapLine();

                // ---- CONTINUE, both parts ----
                Heading(listing, "CONTINUE — two parts, use either or both");
                DrawContinue(listing, e);

                listing.GapLine();

                // ---- END ----
                Heading(listing, "END — the payoff");
                e.Phases.End.Text = listing.TextEntry(e.Phases.End.Text ?? "", 3);
                DrawEffects(listing, e.Phases.End.Effects, false);

                listing.GapLine();

                DrawTiming(listing, e);
                DrawTrigger(listing, e);

                listing.GapLine();
                DrawTarget(listing, e);

                listing.GapLine();
                DrawActions(listing, original);
            });

            listing.Gap(10f);
        }

        private static void DrawContinue(Listing_Standard listing, CustomEvent e)
        {
            var c = e.Phases.Continue;

            // --- Beat ---
            Text.Font = GameFont.Tiny;
            var previous = GUI.color;
            GUI.color = new Color(0.75f, 0.85f, 0.95f);
            listing.Label("BEAT — what they say, in pulses through the event. Leave empty for none.");
            GUI.color = previous;
            Text.Font = GameFont.Small;

            c.Beat.Text = listing.TextEntry(c.Beat.Text ?? "", 3);

            if (c.HasBeat)
            {
                var ramps = c.Ramps;
                listing.CheckboxLabeled("   Build in intensity across the beats", ref ramps,
                    "Off: every beat hits equally. On: they climb, and effects ticked below climb with them.");

                if (ramps && !c.Ramps)
                {
                    c.Beat.IntensityFrom = 0.2f;
                    c.Beat.IntensityTo = 1f;
                }
                else if (!ramps && c.Ramps)
                {
                    c.Beat.IntensityFrom = 1f;
                    c.Beat.IntensityTo = 1f;
                }

                if (c.Ramps)
                {
                    listing.Label($"   Starts {PromptBuilder.DescribeIntensity(c.Beat.IntensityFrom)} ({c.Beat.IntensityFrom:0.00})");
                    c.Beat.IntensityFrom = listing.Slider(c.Beat.IntensityFrom, 0f, 1f);

                    listing.Label($"   Builds to {PromptBuilder.DescribeIntensity(c.Beat.IntensityTo)} ({c.Beat.IntensityTo:0.00})");
                    c.Beat.IntensityTo = listing.Slider(c.Beat.IntensityTo, 0f, 1f);

                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(0.7f, 0.7f, 0.7f);
                    listing.Label("   Put {intensity} in the beat text and it becomes a word: faintly, "
                                  + "noticeably, strongly, overwhelmingly.");
                    GUI.color = previous;
                    Text.Font = GameFont.Small;
                }

                DrawEffects(listing, c.Beat.Effects, c.Ramps);
            }

            listing.Gap(8f);

            // --- Modifier ---
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.75f, 0.85f, 0.95f);
            listing.Label("MODIFIER — never its own line. Folded into everything they say while the event runs.");
            GUI.color = previous;
            Text.Font = GameFont.Small;

            c.ModifierText = listing.TextEntry(c.ModifierText ?? "", 2);
        }

        private static void DrawEffects(Listing_Standard listing, List<PhaseEffect> effects, bool offerScaling)
        {
            foreach (var effect in new List<PhaseEffect>(effects))
            {
                if (effect.HasOneOf)
                {
                    DrawWeightedTable(listing, effects, effect, offerScaling);
                    continue;
                }

                var row = listing.GetRect(24f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 16f, row.y + 2f, row.width - 60f, row.height), "• " + Describe(effect));
                Text.Font = GameFont.Small;

                if (Widgets.ButtonText(new Rect(row.xMax - 28f, row.y, 24f, 22f), "×"))
                {
                    effects.Remove(effect);
                }

                if (offerScaling)
                {
                    var scale = effect.ScaleWithIntensity;
                    listing.CheckboxLabeled("      grows with intensity", ref scale);
                    effect.ScaleWithIntensity = scale;
                }
            }

            if (listing.ButtonText("Add effect…", null, 0.32f)) ShowAddEffectMenu(effects, offerScaling);
        }

        /// <summary>
        /// A weighted table: exactly one arm fires. Weights are relative, so the UI shows the
        /// resulting percentage too — "0.3" alongside two other arms means nothing on its own.
        /// </summary>
        private static void DrawWeightedTable(Listing_Standard listing, List<PhaseEffect> owner,
            PhaseEffect table, bool offerScaling)
        {
            var header = listing.GetRect(24f);
            Widgets.Label(new Rect(header.x + 8f, header.y + 2f, header.width - 60f, header.height),
                $"⚖ One of these ({table.OneOf.Count}):");

            if (Widgets.ButtonText(new Rect(header.xMax - 28f, header.y, 24f, 22f), "×"))
            {
                owner.Remove(table);
                return;
            }

            var total = 0f;
            foreach (var arm in table.OneOf) total += Math.Max(0f, arm.Weight);

            PendingWeights.Clear();

            foreach (var arm in new List<PhaseEffect>(table.OneOf))
            {
                var row = listing.GetRect(26f);

                var share = total > 0f ? Math.Max(0f, arm.Weight) / total : 0f;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 28f, row.y + 3f, row.width * 0.44f, row.height),
                    $"{share:P0}  {Describe(arm)}");
                Text.Font = GameFont.Small;

                // Key includes the arm's identity so two arms don't share a text buffer.
                var key = "w_" + table.GetHashCode() + "_" + table.OneOf.IndexOf(arm);
                arm.Weight = Number(listing, "", key, arm.Weight, 0f, 1000f, row, 0.76f);

                if (Widgets.ButtonText(new Rect(row.xMax - 28f, row.y, 24f, 22f), "×"))
                {
                    table.OneOf.Remove(arm);
                }
            }

            if (total <= 0f)
            {
                var previous = GUI.color;
                GUI.color = Color.yellow;
                Text.Font = GameFont.Tiny;
                listing.Label("      Every weight is zero — nothing will ever be picked.");
                Text.Font = GameFont.Small;
                GUI.color = previous;
            }

            if (listing.ButtonText("Add outcome…", null, 0.32f))
            {
                ShowAddEffectMenu(table.OneOf, offerScaling, includeNothing: true);
            }
        }

        private static string Describe(PhaseEffect fx)
        {
            if (fx.HasOneOf) return $"one of {fx.OneOf.Count}";

            var bits = new List<string>();
            if (fx.Hediff != null) bits.Add($"hediff {fx.Hediff.Def} {(fx.Hediff.Mode == HediffApplyMode.Add ? "+" : "=")}{fx.Hediff.Severity:0.##}");
            if (!string.IsNullOrEmpty(fx.Thought)) bits.Add($"thought {fx.Thought}");
            if (fx.Need != null) bits.Add($"need {fx.Need.Def} {fx.Need.Offset:+0.##;-0.##}");
            if (!string.IsNullOrEmpty(fx.Incident)) bits.Add($"incident {fx.Incident}");
            if (!string.IsNullOrEmpty(fx.ChainEvent)) bits.Add($"chain {fx.ChainEvent}");
            if (!string.IsNullOrEmpty(fx.TraitDef)) bits.Add($"trait {fx.TraitDef}{(fx.TraitRemove ? " (remove)" : "")}");
            if (!string.IsNullOrEmpty(fx.SkillXpSkill)) bits.Add($"{fx.SkillXpAmount:0} {fx.SkillXpSkill} XP");
            foreach (var item in fx.Items) bits.Add($"{item.Count}× {item.Def}");
            if (!string.IsNullOrEmpty(fx.Message)) bits.Add("message");
            if (fx.Nothing) bits.Add("nothing");
            if (fx.Chance < 1f) bits.Add($"{fx.Chance:P0} chance");

            return bits.Count == 0 ? "(empty)" : string.Join(", ", bits.ToArray());
        }

        private static void ShowAddEffectMenu(List<PhaseEffect> effects, bool offerScaling = false,
            bool includeNothing = false)
        {
            var options = new List<FloatMenuOption>
            {
                Pick(effects, "Hediff", DefKind.Hediff, (fx, d) => fx.Hediff = new EffectHediff { Def = d.DefName, Severity = 0.2f }),
                Pick(effects, "Thought (mood)", DefKind.Thought, (fx, d) => fx.Thought = d.DefName),
                Pick(effects, "Need offset", DefKind.Need, (fx, d) => fx.Need = new EffectNeed { Def = d.DefName, Offset = -0.1f }),
                Pick(effects, "Incident", DefKind.Incident, (fx, d) => fx.Incident = d.DefName),
                Pick(effects, "Trait", DefKind.Trait, (fx, d) => fx.TraitDef = d.DefName),
                Pick(effects, "Skill XP", DefKind.Skill, (fx, d) => { fx.SkillXpSkill = d.DefName; fx.SkillXpAmount = 1000f; }),
                Pick(effects, "Item reward", DefKind.Item, (fx, d) => fx.Items.Add(new EffectItem { Def = d.DefName, Count = 1 }))
            };

            foreach (var other in EventStore.All.Where(x => _draft == null || x.DefName != _draft.DefName))
            {
                var captured = other;
                options.Add(new FloatMenuOption($"Chain into \"{captured.Label}\"",
                    () => effects.Add(new PhaseEffect { ChainEvent = captured.DefName })));
            }

            // "nothing" only means something inside a table, as the arm where it fizzles.
            if (includeNothing)
            {
                options.Add(new FloatMenuOption("Nothing happens (a chance to fizzle)",
                    () => effects.Add(new PhaseEffect { Nothing = true })));
            }
            else
            {
                options.Add(new FloatMenuOption("One of several (weighted table)…",
                    () => effects.Add(new PhaseEffect { OneOf = new List<PhaseEffect>() })));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static FloatMenuOption Pick(List<PhaseEffect> effects, string label, DefKind kind,
            Action<PhaseEffect, DefEntry> apply)
        {
            return new FloatMenuOption(label, () =>
                Find.WindowStack.Add(new DefPickerWindow(kind, $"Choose a {label.ToLowerInvariant()}", entry =>
                {
                    var fx = new PhaseEffect();
                    apply(fx, entry);
                    effects.Add(fx);
                })));
        }

        // ------------------------------------------------------- timing/trigger

        private static void DrawTiming(Listing_Standard listing, CustomEvent e)
        {
            Heading(listing, "Timing");

            e.Timing.DurationHours = Number(listing, "Duration (in-game hours)", "dur", e.Timing.DurationHours, 0.5f, 240f);
            e.Timing.ContinueCount = (int)Number(listing, "How many beats", "beats", e.Timing.ContinueCount, 0f, 24f);

            if (listing.ButtonText($"Beat spacing: {e.Timing.Spacing}", null, 0.4f))
            {
                e.Timing.Spacing = e.Timing.Spacing == ContinueSpacing.Even
                    ? ContinueSpacing.Random
                    : ContinueSpacing.Even;
            }

            if (listing.ButtonText($"If the pawn can't speak: {e.Timing.OnBlocked}", null, 0.5f))
            {
                e.Timing.OnBlocked = e.Timing.OnBlocked == BlockedPolicy.Retry ? BlockedPolicy.Skip
                    : e.Timing.OnBlocked == BlockedPolicy.Skip ? BlockedPolicy.Abort
                    : BlockedPolicy.Retry;
            }
        }

        private static void DrawTrigger(Listing_Standard listing, CustomEvent e)
        {
            Heading(listing, "Trigger");

            if (listing.ButtonText($"Fires: {DescribeTrigger(e.Trigger.Mode)}", null, 0.6f))
            {
                var options = new List<FloatMenuOption>();
                foreach (TriggerMode mode in Enum.GetValues(typeof(TriggerMode)))
                {
                    var captured = mode;
                    options.Add(new FloatMenuOption(DescribeTrigger(captured), () => e.Trigger.Mode = captured));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }

            if (TriggerScheduler.IsCalendarMode(e.Trigger.Mode))
            {
                e.Trigger.DailyHour = (int)Number(listing, "Hour of day (0-23)", "hour", e.Trigger.DailyHour, 0f, 23f);
                e.Trigger.DailyChance = Number(listing, "Chance each time (0-1)", "chance", e.Trigger.DailyChance, 0f, 1f);
            }
            else if (e.Trigger.Mode == TriggerMode.Occasionally)
            {
                e.Trigger.MtbDays = Number(listing, "Average days between", "mtb", e.Trigger.MtbDays, 0.1f, 360f);
            }

            if (e.Trigger.Mode != TriggerMode.Manual)
            {
                e.Trigger.MinRefireDays = Number(listing, "Min days before repeating", "refire", e.Trigger.MinRefireDays, 0f, 360f);
            }
        }

        /// <summary>
        /// Who the event can happen to. These all work at runtime; they were only ever
        /// missing from the UI, which meant editing the file was the sole way to reach them.
        /// </summary>
        private static void DrawTarget(Listing_Standard listing, CustomEvent e)
        {
            Heading(listing, "Who it happens to");

            var t = e.Target;

            t.Count = (int)Number(listing, "How many pawns affected", "count", t.Count, 1f, 20f);

            if (t.Count > 1)
            {
                if (listing.ButtonText($"   They get: {DescribeGroup(t.Group)}", null, 0.62f))
                {
                    t.Group = t.Group == GroupMode.Independent ? GroupMode.Shared : GroupMode.Independent;
                }

                Text.Font = GameFont.Tiny;
                var groupHint = GUI.color;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                listing.Label(t.Group == GroupMode.Shared
                    ? "   One event they all take part in. Beats pass between them, effects land on everyone."
                    : "   A separate event each, on its own timing. They need not even overlap.");
                GUI.color = groupHint;
                Text.Font = GameFont.Small;
            }

            var named = !string.IsNullOrEmpty(t.SpecificPawnName);
            var wantsNamed = named;
            listing.CheckboxLabeled("Only one specific pawn", ref wantsNamed,
                "Matched by name. A name nobody on the map has simply means the event cannot fire, "
                + "so this will not carry across to a different colony.");

            if (!wantsNamed && named) t.SpecificPawnName = null;

            if (wantsNamed)
            {
                var pawnRow = listing.GetRect(26f);
                Widgets.Label(new Rect(pawnRow.x + 14f, pawnRow.y + 3f, pawnRow.width * 0.42f, pawnRow.height),
                    named ? t.SpecificPawnName : "(nobody chosen)");

                if (Widgets.ButtonText(
                        new Rect(pawnRow.x + pawnRow.width * 0.5f, pawnRow.y, pawnRow.width * 0.5f, 24f),
                        "Choose a colonist..."))
                {
                    ShowPawnMenu(t);
                }
            }

            t.CooldownDays = Number(listing, "Cooldown per pawn (days)", "cooldown", t.CooldownDays, 0f, 360f);

            // Gender cycles rather than opening a menu — three states isn't worth a popup.
            var genderLabel = string.IsNullOrEmpty(t.Gender) ? "anyone" : t.Gender;
            if (listing.ButtonText($"Gender: {genderLabel}", null, 0.45f))
            {
                t.Gender = string.IsNullOrEmpty(t.Gender) ? "male"
                    : t.Gender == "male" ? "female"
                    : null;
            }

            // -1 means "no limit", so the fields show blank rather than a misleading number.
            t.MinAge = OptionalNumber(listing, "Minimum age", "minAge", t.MinAge, 0f, 200f);
            t.MaxAge = OptionalNumber(listing, "Maximum age", "maxAge", t.MaxAge, 0f, 200f);

            DrawDefList(listing, "Pawn kinds", t.PawnKinds, DefKind.PawnKind, "pawn kind",
                "Broad keywords (Colonist, Prisoner, Slave, Guest, Animal) also work. Empty means anyone eligible.");

            DrawDefList(listing, "Must have trait", t.RequiredTraits, DefKind.Trait, "required trait", null);
            DrawDefList(listing, "Must NOT have trait", t.ExcludedTraits, DefKind.Trait, "excluded trait", null);
            DrawDefList(listing, "Must have hediff", t.RequiredHediffs, DefKind.Hediff, "required hediff", null);
            DrawDefList(listing, "Blocked by hediff", t.ExcludedHediffs, DefKind.Hediff, "blocking hediff", null);

            DrawTraitWeights(listing, t);
            DrawTagList(listing, e, t);
        }

        /// <summary>
        /// Weighted trait preferences. A weight above 1 makes matching pawns likelier to be
        /// picked; 0 excludes them outright.
        /// </summary>
        private static void DrawTraitWeights(Listing_Standard listing, EventTarget t)
        {
            listing.Label($"Prefer pawns with traits: {(t.WeightByTrait.Count == 0 ? "(no preference)" : "")}");

            string toRemove = null;
            foreach (var pair in t.WeightByTrait)
            {
                var row = listing.GetRect(26f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 16f, row.y + 3f, row.width * 0.5f, row.height), "• " + pair.Key);
                Text.Font = GameFont.Small;

                var weight = Number(listing, "", "w_" + pair.Key, pair.Value, 0f, 100f, row, 0.55f);
                if (Math.Abs(weight - pair.Value) > 0.0001f) PendingWeights[pair.Key] = weight;

                if (Widgets.ButtonText(new Rect(row.xMax - 28f, row.y, 24f, 22f), "×")) toRemove = pair.Key;
            }

            // Applied after the loop: mutating a dictionary while enumerating it throws.
            foreach (var pending in PendingWeights)
            {
                if (t.WeightByTrait.ContainsKey(pending.Key)) t.WeightByTrait[pending.Key] = pending.Value;
            }

            PendingWeights.Clear();

            if (toRemove != null) t.WeightByTrait.Remove(toRemove);

            if (listing.ButtonText("Add trait preference…", null, 0.4f))
            {
                Find.WindowStack.Add(new DefPickerWindow(DefKind.Trait, "Choose a trait to prefer",
                    entry =>
                    {
                        if (!t.WeightByTrait.ContainsKey(entry.DefName)) t.WeightByTrait[entry.DefName] = 2f;
                    }));
            }
        }

        private static readonly Dictionary<string, float> PendingWeights = new Dictionary<string, float>();

        /// <summary>
        /// Exclusion tags are free text, not defs — two events sharing a tag can't run on
        /// the same pawn at once. Offers tags already in use rather than a typing field.
        /// </summary>
        private static void DrawTagList(Listing_Standard listing, CustomEvent e, EventTarget t)
        {
            listing.Label($"Conflicts with (tags): {(t.ExclusionTags.Count == 0 ? "(none)" : string.Join(", ", t.ExclusionTags.ToArray()))}");

            var row = listing.GetRect(26f);
            var half = row.width * 0.25f;

            if (Widgets.ButtonText(new Rect(row.x, row.y, half, 24f), "Add tag"))
            {
                var options = new List<FloatMenuOption>();

                // Tags only matter when shared, so offer the ones other events already use.
                foreach (var tag in EventStore.All
                             .Where(other => other.DefName != e.DefName)
                             .SelectMany(other => other.Target.ExclusionTags)
                             .Distinct()
                             .OrderBy(x => x))
                {
                    var captured = tag;
                    options.Add(new FloatMenuOption(captured, () =>
                    {
                        if (!t.ExclusionTags.Contains(captured)) t.ExclusionTags.Add(captured);
                    }));
                }

                // A tag named after the event is the common case: "nothing else like this".
                var own = e.DefName;
                if (!string.IsNullOrEmpty(own) && !t.ExclusionTags.Contains(own))
                {
                    options.Add(new FloatMenuOption($"New tag: \"{own}\"", () => t.ExclusionTags.Add(own)));
                }

                if (options.Count == 0)
                {
                    options.Add(new FloatMenuOption("(no tags in use yet — name an event first)", null));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }

            if (t.ExclusionTags.Count > 0 && Widgets.ButtonText(new Rect(row.x + half + 6f, row.y, half, 24f), "Clear"))
            {
                t.ExclusionTags.Clear();
            }
        }

        private static void DrawDefList(Listing_Standard listing, string label, List<string> values,
            DefKind kind, string what, string hint)
        {
            listing.Label($"{label}: {(values.Count == 0 ? "(any)" : string.Join(", ", values.ToArray()))}");

            if (hint != null)
            {
                Text.Font = GameFont.Tiny;
                var previous = GUI.color;
                GUI.color = new Color(0.65f, 0.65f, 0.65f);
                listing.Label("   " + hint);
                GUI.color = previous;
                Text.Font = GameFont.Small;
            }

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

        private static string DescribeGroup(GroupMode mode)
        {
            return mode == GroupMode.Shared ? "one shared event" : "a separate event each";
        }

        /// <summary>
        /// Offers the pawns currently on the map. Typing a name freehand would let you pick
        /// somebody who does not exist, which then fails silently at trigger time.
        /// </summary>
        private static void ShowPawnMenu(EventTarget t)
        {
            var options = new List<FloatMenuOption>();
            var map = Find.CurrentMap;

            if (map != null)
            {
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                {
                    var captured = pawn;
                    options.Add(new FloatMenuOption(captured.LabelShortCap,
                        () => t.SpecificPawnName = captured.LabelShort));
                }
            }

            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("(no colonists - open a save first)", null));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static string DescribeTrigger(TriggerMode mode)
        {
            switch (mode)
            {
                case TriggerMode.Daily: return "Daily, at a set hour";
                case TriggerMode.Monthly: return "Monthly (once a quadrum), at a set hour";
                case TriggerMode.Yearly: return "Yearly, at a set hour";
                case TriggerMode.Occasionally: return "Occasionally, at random intervals";
                default: return "Manual — only when you fire it";
            }
        }

        // ------------------------------------------------------------- actions

        private static void DrawActions(Listing_Standard listing, CustomEvent original)
        {
            var row = listing.GetRect(34f);
            var quarter = row.width / 4f;

            if (Widgets.ButtonText(new Rect(row.x, row.y, quarter - 6f, 32f), "Save"))
            {
                Save();
            }

            if (Widgets.ButtonText(new Rect(row.x + quarter, row.y, quarter - 6f, 32f), "Discard"))
            {
                Collapse();
            }

            // Test fires what's on disk, not the draft — an unsaved edit isn't what would run.
            if (original != null && Widgets.ButtonText(new Rect(row.x + quarter * 2f, row.y, quarter - 6f, 32f), "Test fire"))
            {
                TestFire(original);
            }

            if (original != null && Widgets.ButtonText(new Rect(row.x + quarter * 3f, row.y, quarter - 6f, 32f), "Delete"))
            {
                ConfirmDelete(original);
            }
        }

        private static void Save()
        {
            var errors = _draft.Validate(out var warnings);
            if (errors.Count > 0)
            {
                Messages.Message("Can't save: " + string.Join("; ", errors.ToArray()), MessageTypeDefOf.RejectInput, false);
                return;
            }

            foreach (var warning in warnings) RTCELog.Warning($"\"{_draft.DefName}\": {warning}");

            var path = EventWriter.Save(_draft, EventStore.EventsFolder, out var error);
            if (path == null)
            {
                Messages.Message($"Couldn't save: {error}", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var label = _draft.Label;
            Collapse();

            EventStore.Reload();
            Effects.HediffFactory.RegisterAll();
            Messages.Message($"Saved \"{label}\".", MessageTypeDefOf.TaskCompletion, false);
        }

        private static void TestFire(CustomEvent e)
        {
            var component = CustomEventsGameComponent.Current;
            if (component == null)
            {
                Messages.Message("Load a save first — events run inside a game.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                Messages.Message("Open a map first.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var pawn = PawnSelector.TryPick(e, map);
            if (pawn == null)
            {
                Messages.Message($"Nobody on this map matches \"{e.Label}\"'s target filters.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (component.TryStart(e, pawn, out var reason, bypassLimits: true))
            {
                Messages.Message($"Started \"{e.Label}\" on {pawn.LabelShort}.", pawn, MessageTypeDefOf.NeutralEvent, false);
            }
            else
            {
                Messages.Message($"Couldn't start \"{e.Label}\": {reason}", MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void ConfirmDelete(CustomEvent e)
        {
            var fileName = string.IsNullOrEmpty(e.SourcePath)
                ? e.DefName + ".json"
                : System.IO.Path.GetFileName(e.SourcePath);

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                $"Delete \"{e.Label}\"?\n\nThis removes {fileName} from disk. It can't be undone.",
                () =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(e.SourcePath) && System.IO.File.Exists(e.SourcePath))
                        {
                            System.IO.File.Delete(e.SourcePath);
                        }

                        Collapse();
                        EventStore.Reload();
                        Messages.Message($"Deleted \"{e.Label}\".", MessageTypeDefOf.TaskCompletion, false);
                    }
                    catch (Exception ex)
                    {
                        Messages.Message($"Couldn't delete: {ex.Message}", MessageTypeDefOf.RejectInput, false);
                    }
                },
                destructive: true));
        }

        // -------------------------------------------------------------- helpers

        private static void Heading(Listing_Standard listing, string text)
        {
            var previous = GUI.color;
            GUI.color = new Color(0.9f, 0.9f, 0.9f);
            listing.Label(text);
            GUI.color = previous;
        }

        /// <summary>
        /// Listing_Standard has no indent, so an editor block is nudged in by shrinking the
        /// column width for its duration. Keeps the expanded body visually inside its row.
        /// </summary>
        private static void Indented(Listing_Standard listing, Action body)
        {
            const float indent = 14f;

            // Indent shifts the left edge; the column has to shrink by the same amount or
            // the block runs past the right edge of the page.
            listing.Indent(indent);
            listing.ColumnWidth -= indent;

            try
            {
                body();
            }
            finally
            {
                listing.ColumnWidth += indent;
                listing.Outdent(indent);
            }
        }

        /// <summary>
        /// A number field that treats a negative stored value as "unset", showing an empty
        /// box. Used for the age bounds, where -1 means no limit.
        /// </summary>
        private static float OptionalNumber(Listing_Standard listing, string label, string key,
            float value, float min, float max)
        {
            var row = listing.GetRect(26f);
            Widgets.Label(new Rect(row.x, row.y, row.width * 0.62f, row.height), label);

            if (!Buffers.TryGetValue(key, out var buffer))
            {
                buffer = value < 0f ? "" : value.ToString("0.###", CultureInfo.InvariantCulture);
            }

            var typed = Widgets.TextField(new Rect(row.x + row.width * 0.64f, row.y, row.width * 0.34f, 24f), buffer);
            Buffers[key] = typed;

            if (string.IsNullOrWhiteSpace(typed)) return -1f;

            return float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? Mathf.Clamp(parsed, min, max)
                : value;
        }

        private static float Number(Listing_Standard listing, string label, string key, float value,
            float min, float max, Rect? existingRow = null, float fieldStart = 0.64f)
        {
            var row = existingRow ?? listing.GetRect(26f);
            if (!string.IsNullOrEmpty(label))
            {
                Widgets.Label(new Rect(row.x, row.y, row.width * 0.62f, row.height), label);
            }

            if (!Buffers.TryGetValue(key, out var buffer))
            {
                buffer = value.ToString("0.###", CultureInfo.InvariantCulture);
            }

            var typed = Widgets.TextField(
                new Rect(row.x + row.width * fieldStart, row.y, row.width * 0.3f, 24f), buffer);
            Buffers[key] = typed;

            if (float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return Mathf.Clamp(parsed, min, max);
            }

            // Mid-typing or empty: keep what we had rather than snapping to a bound.
            return value;
        }
    }
}
