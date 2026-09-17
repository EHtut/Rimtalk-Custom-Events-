using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using RimTalkCustomEvents.Util;

namespace RimTalkCustomEvents.Data
{
    /// <summary>
    /// Serialises a CustomEvent back to JSON.
    ///
    /// Values matching their default are left out, so a file stays as small as what the
    /// author actually chose rather than exploding into every field the schema allows.
    /// Deliberately free of Verse/RimWorld types so it can be round-trip tested outside
    /// the game.
    /// </summary>
    public static class EventWriter
    {
        private const float Epsilon = 0.0001f;

        public static string ToJson(CustomEvent e)
        {
            var w = new JsonWriter();
            var timingDefaults = new EventTiming();
            var triggerDefaults = new EventTrigger();
            var targetDefaults = new EventTarget();

            w.BeginObject();

            w.Write("defName", e.DefName);
            if (!string.IsNullOrEmpty(e.Label) && e.Label != e.DefName) w.Write("label", e.Label);
            if (!string.IsNullOrEmpty(e.Description)) w.Write("description", e.Description);
            if (!e.Enabled) w.Write("enabled", false);

            // ---- phases ----
            w.BeginObject("phases");
            WritePhase(w, "beginning", e.Phases.Beginning);
            WritePhase(w, "continue", e.Phases.Continue);
            WritePhase(w, "end", e.Phases.End);
            w.EndObject();

            // ---- timing ----
            var timing = e.Timing;
            var derivedContinueCount = DerivedContinueCount(e);
            var timingChanged =
                Differs(timing.DurationHours, timingDefaults.DurationHours) ||
                timing.ContinueCount != derivedContinueCount ||
                timing.Spacing != timingDefaults.Spacing ||
                Differs(timing.ContinueJitter, timingDefaults.ContinueJitter) ||
                Differs(timing.BeatTimeoutHours, timingDefaults.BeatTimeoutHours) ||
                Differs(timing.MinBeatGapHours, timingDefaults.MinBeatGapHours) ||
                timing.OnBlocked != timingDefaults.OnBlocked;

            if (timingChanged)
            {
                w.BeginObject("timing");
                if (Differs(timing.DurationHours, timingDefaults.DurationHours)) w.Write("durationHours", timing.DurationHours);

                // Only written when it isn't what the duration would imply anyway.
                if (timing.ContinueCount != derivedContinueCount) w.Write("continueCount", timing.ContinueCount);

                if (timing.Spacing != timingDefaults.Spacing) w.Write("continueSpacing", timing.Spacing.ToString().ToLowerInvariant());
                if (Differs(timing.ContinueJitter, timingDefaults.ContinueJitter)) w.Write("continueJitter", timing.ContinueJitter);
                if (timing.OnBlocked != timingDefaults.OnBlocked) w.Write("onBlocked", timing.OnBlocked.ToString().ToLowerInvariant());
                if (Differs(timing.BeatTimeoutHours, timingDefaults.BeatTimeoutHours)) w.Write("beatTimeoutHours", timing.BeatTimeoutHours);
                if (Differs(timing.MinBeatGapHours, timingDefaults.MinBeatGapHours)) w.Write("minBeatGapHours", timing.MinBeatGapHours);
                w.EndObject();
            }

            // ---- trigger ----
            var trigger = e.Trigger;
            w.BeginObject("trigger");
            w.Write("mode", trigger.Mode.ToString().ToLowerInvariant());

            if (trigger.Mode == TriggerMode.Daily)
            {
                w.Write("dailyHour", trigger.DailyHour);
                if (Differs(trigger.DailyChance, triggerDefaults.DailyChance)) w.Write("dailyChance", trigger.DailyChance);
            }
            else if (trigger.Mode != TriggerMode.Manual)
            {
                w.Write("mtbDays", trigger.MtbDays);
            }

            if (Differs(trigger.MinRefireDays, triggerDefaults.MinRefireDays)) w.Write("minRefireDays", trigger.MinRefireDays);

            if (trigger.StartHourMin.HasValue && trigger.StartHourMax.HasValue)
            {
                w.BeginArray("allowedTimeOfDay");
                w.Write(null, trigger.StartHourMin.Value);
                w.Write(null, trigger.StartHourMax.Value);
                w.EndArray();
            }

            w.EndObject();

            // ---- target ----
            var target = e.Target;
            var targetChanged =
                target.PawnKinds.Count > 0 || target.RequiredTraits.Count > 0 || target.ExcludedTraits.Count > 0 ||
                target.RequiredHediffs.Count > 0 || target.ExcludedHediffs.Count > 0 ||
                target.WeightByTrait.Count > 0 || target.ExclusionTags.Count > 0 ||
                !string.IsNullOrEmpty(target.Gender) ||
                target.MinAge >= 0f || target.MaxAge >= 0f ||
                Differs(target.CooldownDays, targetDefaults.CooldownDays);

            if (targetChanged)
            {
                w.BeginObject("target");
                WriteStringList(w, "pawnKinds", target.PawnKinds);
                if (!string.IsNullOrEmpty(target.Gender)) w.Write("gender", target.Gender);
                if (target.MinAge >= 0f) w.Write("minAge", target.MinAge);
                if (target.MaxAge >= 0f) w.Write("maxAge", target.MaxAge);
                WriteStringList(w, "requireTrait", target.RequiredTraits);
                WriteStringList(w, "excludeTrait", target.ExcludedTraits);
                WriteStringList(w, "requireHediff", target.RequiredHediffs);
                WriteStringList(w, "excludeIfHediff", target.ExcludedHediffs);

                if (target.WeightByTrait.Count > 0)
                {
                    w.BeginObject("weightByTrait");
                    foreach (var pair in target.WeightByTrait) w.Write(pair.Key, pair.Value);
                    w.EndObject();
                }

                if (Differs(target.CooldownDays, targetDefaults.CooldownDays)) w.Write("cooldownDays", target.CooldownDays);
                WriteStringList(w, "exclusionTags", target.ExclusionTags);
                w.EndObject();
            }

            // ---- inline hediffs ----
            if (e.CustomHediffs.Count > 0)
            {
                w.BeginArray("customHediffs");
                foreach (var spec in e.CustomHediffs) WriteHediffSpec(w, spec);
                w.EndArray();
            }

            w.EndObject();
            return w.ToString();
        }

        private static int DerivedContinueCount(CustomEvent e)
        {
            if (!e.HasContinueText) return 0;
            var derived = (int)Math.Round(e.Timing.DurationHours / 4f);
            return Math.Max(1, Math.Min(6, derived));
        }

        /// <summary>Bare string when there are no effects, object form when there are.</summary>
        private static void WritePhase(JsonWriter w, string key, PhaseSpec phase)
        {
            if (phase == null) return;
            if (!phase.HasText && phase.Effects.Count == 0) return;

            // The bare-string form only works when there is nothing else to record.
            var plain = phase.Effects.Count == 0
                        && phase.Mode == ContinueMode.Prompt
                        && !HasIntensityRange(phase);

            if (plain)
            {
                w.Write(key, phase.Text);
                return;
            }

            w.BeginObject(key);
            w.Write("text", phase.Text ?? "");

            if (phase.Mode != ContinueMode.Prompt) w.Write("mode", phase.Mode.ToString().ToLowerInvariant());

            if (HasIntensityRange(phase))
            {
                w.BeginObject("intensity");
                w.Write("from", phase.IntensityFrom);
                w.Write("to", phase.IntensityTo);
                w.EndObject();
            }

            if (phase.Effects.Count == 0)
            {
                w.EndObject();
                return;
            }

            w.BeginArray("effects");
            foreach (var effect in phase.Effects) WriteEffect(w, effect);
            w.EndArray();
            w.EndObject();
        }

        /// <summary>True when the phase actually ramps, rather than sitting flat at 1.</summary>
        private static bool HasIntensityRange(PhaseSpec phase)
        {
            return Differs(phase.IntensityFrom, 1f) || Differs(phase.IntensityTo, 1f);
        }

        private static void WriteEffect(JsonWriter w, PhaseEffect fx)
        {
            w.BeginObject();

            if (Differs(fx.Weight, 1f)) w.Write("weight", fx.Weight);
            if (Differs(fx.Chance, 1f)) w.Write("chance", fx.Chance);
            if (fx.ScaleWithIntensity) w.Write("scaleWithIntensity", true);

            if (fx.HasOneOf)
            {
                w.BeginArray("oneOf");
                foreach (var arm in fx.OneOf) WriteEffect(w, arm);
                w.EndArray();
                w.EndObject();
                return;
            }

            if (fx.Nothing) w.Write("nothing", true);

            if (fx.Hediff != null)
            {
                w.BeginObject("hediff");
                w.Write("def", fx.Hediff.Def);
                w.Write("severity", fx.Hediff.Severity);
                if (!string.IsNullOrEmpty(fx.Hediff.BodyPart)) w.Write("bodyPart", fx.Hediff.BodyPart);
                if (fx.Hediff.Mode != HediffApplyMode.Add) w.Write("mode", fx.Hediff.Mode.ToString().ToLowerInvariant());
                w.EndObject();
            }

            if (!string.IsNullOrEmpty(fx.Thought)) w.Write("thought", fx.Thought);

            if (fx.Need != null)
            {
                w.BeginObject("need");
                w.Write("def", fx.Need.Def);
                w.Write("offset", fx.Need.Offset);
                w.EndObject();
            }

            if (!string.IsNullOrEmpty(fx.Incident)) w.Write("incident", fx.Incident);
            if (!string.IsNullOrEmpty(fx.ChainEvent)) w.Write("chainEvent", fx.ChainEvent);

            if (!string.IsNullOrEmpty(fx.TraitDef))
            {
                w.BeginObject("trait");
                w.Write("def", fx.TraitDef);
                if (fx.TraitDegree != 0) w.Write("degree", fx.TraitDegree);
                if (fx.TraitRemove) w.Write("remove", true);
                w.EndObject();
            }

            if (!string.IsNullOrEmpty(fx.SkillXpSkill))
            {
                w.BeginObject("skillXp");
                w.Write("skill", fx.SkillXpSkill);
                w.Write("amount", fx.SkillXpAmount);
                w.EndObject();
            }

            if (fx.Items.Count > 0)
            {
                w.BeginArray("items");
                foreach (var item in fx.Items)
                {
                    w.BeginObject();
                    w.Write("def", item.Def);
                    if (item.Count != 1) w.Write("count", item.Count);
                    w.EndObject();
                }

                w.EndArray();
            }

            if (!string.IsNullOrEmpty(fx.Message)) w.Write("message", fx.Message);

            w.EndObject();
        }

        private static void WriteHediffSpec(JsonWriter w, CustomHediffSpec spec)
        {
            var defaults = new CustomHediffSpec();

            w.BeginObject();
            w.Write("defName", spec.DefName);
            if (!string.IsNullOrEmpty(spec.Label)) w.Write("label", spec.Label);
            if (!string.IsNullOrEmpty(spec.Description)) w.Write("description", spec.Description);
            if (spec.IsBad != defaults.IsBad) w.Write("isBad", spec.IsBad);
            if (spec.Tendable != defaults.Tendable) w.Write("tendable", spec.Tendable);
            if (Differs(spec.MaxSeverity, defaults.MaxSeverity)) w.Write("maxSeverity", spec.MaxSeverity);
            if (Differs(spec.SeverityPerDay, defaults.SeverityPerDay)) w.Write("severityPerDay", spec.SeverityPerDay);
            if (spec.DisappearsAfterDays > 0f) w.Write("disappearsAfterDays", spec.DisappearsAfterDays);

            if (spec.Stages.Count > 0)
            {
                w.BeginArray("stages");
                foreach (var stage in spec.Stages)
                {
                    w.BeginObject();
                    w.Write("minSeverity", stage.MinSeverity);
                    if (!string.IsNullOrEmpty(stage.Label)) w.Write("label", stage.Label);
                    if (Differs(stage.PainOffset, 0f)) w.Write("painOffset", stage.PainOffset);

                    if (stage.StatOffsets.Count > 0)
                    {
                        w.BeginObject("statOffsets");
                        foreach (var pair in stage.StatOffsets) w.Write(pair.Key, pair.Value);
                        w.EndObject();
                    }

                    if (stage.CapMods.Count > 0)
                    {
                        w.BeginObject("capMods");
                        foreach (var pair in stage.CapMods) w.Write(pair.Key, pair.Value);
                        w.EndObject();
                    }

                    w.EndObject();
                }

                w.EndArray();
            }

            w.EndObject();
        }

        private static void WriteStringList(JsonWriter w, string key, List<string> values)
        {
            if (values == null || values.Count == 0) return;

            w.BeginArray(key);
            foreach (var value in values) w.WriteValue(value);
            w.EndArray();
        }

        private static bool Differs(float a, float b) => Math.Abs(a - b) > Epsilon;

        // ------------------------------------------------------------------ files

        /// <summary>
        /// Writes the event to disk. Returns the path written, or null on failure with the
        /// reason in <paramref name="error"/>.
        /// </summary>
        public static string Save(CustomEvent e, string folder, out string error)
        {
            error = null;

            try
            {
                var path = !string.IsNullOrEmpty(e.SourcePath)
                    ? e.SourcePath
                    : Path.Combine(folder, e.DefName + ".json");

                var text = ToJson(e);

                // Serialising goes through the typed model, so comments in the original
                // can't survive. Keep a copy rather than destroying hand-written notes —
                // these files are prose, and the comments are often the useful part.
                if (File.Exists(path) && HasComments(path))
                {
                    try
                    {
                        File.Copy(path, path + ".bak", true);
                    }
                    catch
                    {
                        // A failed backup shouldn't block the save; the warning in the
                        // editor already told them comments would go.
                    }
                }

                // Write beside the target then swap, so a failure mid-write can't leave a
                // half-written file where a working event used to be.
                var temp = path + ".tmp";
                File.WriteAllText(temp, text);

                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);

                e.SourcePath = path;
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Whether a file contains comments, which saving would discard. Used to warn before
        /// overwriting a hand-written file.
        /// </summary>
        public static bool HasComments(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

                // Strip strings first so a "//" inside prose doesn't count as a comment.
                var text = File.ReadAllText(path);
                var withoutStrings = Regex.Replace(text, "\"(\\\\.|[^\"\\\\])*\"", "\"\"");
                return withoutStrings.Contains("//") || withoutStrings.Contains("/*");
            }
            catch
            {
                return false;
            }
        }
    }
}
