using System;
using System.Collections.Generic;
using RimTalkCustomEvents.Util;

namespace RimTalkCustomEvents.Data
{
    /// <summary>
    /// How an event decides to fire.
    ///
    /// There is deliberately no storyteller mode. This mod is its own storyteller for
    /// narrative beats — it paces itself with mtbDays, cooldowns and concurrency caps —
    /// and stays out of RimWorld's incident pacing and threat budget entirely.
    /// </summary>
    public enum TriggerMode
    {
        Manual,
        Daily,
        Occasionally
    }

    /// <summary>What to do when a pawn stays unavailable past a beat's timeout.</summary>
    public enum BlockedPolicy
    {
        Retry,
        Skip,
        Abort
    }

    /// <summary>How CONTINUE beats are laid out across the event's duration.</summary>
    public enum ContinueSpacing
    {
        /// <summary>Evenly spread, nudged by continueJitter. Predictable escalation.</summary>
        Even,

        /// <summary>Beat times drawn at random across the window.</summary>
        Random
    }

    public enum HediffApplyMode
    {
        /// <summary>Add to any existing severity. This is what makes CONTINUE escalate.</summary>
        Add,

        /// <summary>Set severity outright, ignoring what's already there.</summary>
        Set
    }

    public class EffectHediff
    {
        public string Def;
        public float Severity = 0.1f;
        public string BodyPart;
        public HediffApplyMode Mode = HediffApplyMode.Add;
    }

    public class EffectNeed
    {
        public string Def;
        public float Offset;
    }

    public class EffectItem
    {
        public string Def;
        public int Count = 1;
    }

    /// <summary>
    /// One mechanical consequence attached to a phase. Fields are all optional; an effect
    /// applies whichever ones are set. An effect carrying <see cref="OneOf"/> instead picks
    /// exactly one child at random, weighted.
    /// </summary>
    public class PhaseEffect
    {
        /// <summary>0-1 probability this effect applies at all.</summary>
        public float Chance = 1f;

        /// <summary>Relative likelihood when this effect sits inside a OneOf table.</summary>
        public float Weight = 1f;

        /// <summary>Explicit no-op arm, so a weighted table can genuinely fizzle.</summary>
        public bool Nothing;

        public EffectHediff Hediff;
        public string Thought;
        public EffectNeed Need;

        // Applied from chunk C onwards. Parsed now so event files stay valid.
        public string Incident;
        public string ChainEvent;
        public string TraitDef;
        public int TraitDegree;
        public bool TraitRemove;
        public string SkillXpSkill;
        public float SkillXpAmount;
        public List<EffectItem> Items = new List<EffectItem>();
        public string Message;

        /// <summary>Weighted alternatives; exactly one is chosen.</summary>
        public List<PhaseEffect> OneOf;

        public bool HasOneOf => OneOf != null && OneOf.Count > 0;

        public static PhaseEffect FromJson(JsonValue v)
        {
            var e = new PhaseEffect
            {
                Chance = v.GetFloat("chance", 1f),
                Weight = v.GetFloat("weight", 1f),
                Nothing = v.GetBool("nothing"),
                Thought = v.GetString("thought"),
                Message = v.GetString("message")
            };

            var oneOf = v.Get("oneOf");
            if (oneOf != null && oneOf.Type == JsonType.Array)
            {
                e.OneOf = new List<PhaseEffect>();
                foreach (var child in oneOf.Array)
                {
                    e.OneOf.Add(FromJson(child));
                }
            }

            var hediff = v.Get("hediff");
            if (hediff != null && !hediff.IsNull)
            {
                e.Hediff = new EffectHediff
                {
                    Def = hediff.GetString("def"),
                    Severity = hediff.GetFloat("severity", 0.1f),
                    BodyPart = hediff.GetString("bodyPart"),
                    Mode = ParseEnum(hediff.GetString("mode"), HediffApplyMode.Add)
                };
            }

            var need = v.Get("need");
            if (need != null && !need.IsNull)
            {
                e.Need = new EffectNeed
                {
                    Def = need.GetString("def"),
                    Offset = need.GetFloat("offset")
                };
            }

            // "incident": "ColdSnap" or "incident": { "def": "ColdSnap" }
            var incident = v.Get("incident");
            if (incident != null && !incident.IsNull)
            {
                e.Incident = incident.Type == JsonType.Object
                    ? incident.GetString("def")
                    : incident.AsString();
            }

            e.ChainEvent = v.GetString("chainEvent");

            // "trait": "Nimble" or { "def": "Nimble", "degree": 0, "remove": false }
            var trait = v.Get("trait");
            if (trait != null && !trait.IsNull)
            {
                if (trait.Type == JsonType.String)
                {
                    e.TraitDef = trait.Str;
                }
                else
                {
                    e.TraitDef = trait.GetString("def");
                    e.TraitDegree = trait.GetInt("degree");
                    e.TraitRemove = trait.GetBool("remove");
                }
            }

            var skillXp = v.Get("skillXp");
            if (skillXp != null && !skillXp.IsNull)
            {
                e.SkillXpSkill = skillXp.GetString("skill");
                e.SkillXpAmount = skillXp.GetFloat("amount");
            }

            foreach (var item in v.GetArray("items"))
            {
                e.Items.Add(new EffectItem
                {
                    Def = item.GetString("def"),
                    Count = item.GetInt("count", 1)
                });
            }

            return e;
        }

        internal static T ParseEnum<T>(string text, T fallback) where T : struct
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            return Enum.TryParse<T>(text, true, out var parsed) ? parsed : fallback;
        }
    }

    /// <summary>
    /// One phase: what the pawn is prompted with, and what it does mechanically.
    /// Authored either as a bare string (text only) or an object with text plus effects.
    /// </summary>
    public class PhaseSpec
    {
        public string Text = "";
        public List<PhaseEffect> Effects = new List<PhaseEffect>();

        public bool HasText => !string.IsNullOrWhiteSpace(Text);

        public static PhaseSpec FromJson(JsonValue v)
        {
            var spec = new PhaseSpec();
            if (v == null || v.IsNull) return spec;

            if (v.Type == JsonType.String)
            {
                spec.Text = v.Str;
                return spec;
            }

            if (v.Type == JsonType.Array)
            {
                // An author who wrote a list of lines gets them joined rather than losing
                // all but the first.
                var parts = new List<string>();
                foreach (var item in v.Array)
                {
                    var s = item?.AsString();
                    if (!string.IsNullOrEmpty(s)) parts.Add(s);
                }

                spec.Text = string.Join(" ", parts.ToArray());
                return spec;
            }

            if (v.Type != JsonType.Object) return spec;

            spec.Text = v.GetString("text", "");
            foreach (var effect in v.GetArray("effects"))
            {
                spec.Effects.Add(PhaseEffect.FromJson(effect));
            }

            return spec;
        }
    }

    public class EventPhases
    {
        public PhaseSpec Beginning = new PhaseSpec();

        /// <summary>
        /// Replayed once per CONTINUE beat. How many beats there are, and when each lands,
        /// is worked out by the scheduler rather than the author.
        /// </summary>
        public PhaseSpec Continue = new PhaseSpec();

        public PhaseSpec End = new PhaseSpec();
    }

    public class EventTiming
    {
        /// <summary>In-game hours from BEGINNING to END.</summary>
        public float DurationHours = 12f;

        /// <summary>
        /// How many CONTINUE beats play between BEGINNING and END. Derived from the duration
        /// unless the author sets it explicitly.
        /// </summary>
        public int ContinueCount = 3;

        public ContinueSpacing Spacing = ContinueSpacing.Even;

        /// <summary>0-1. Randomises each CONTINUE beat's position. Even spacing only.</summary>
        public float ContinueJitter = 0.25f;

        /// <summary>Give up on a blocked beat after this many in-game hours.</summary>
        public float BeatTimeoutHours = 2f;

        /// <summary>Never fire two beats closer together than this.</summary>
        public float MinBeatGapHours = 0.5f;

        public BlockedPolicy OnBlocked = BlockedPolicy.Retry;
    }

    public class EventTrigger
    {
        public TriggerMode Mode = TriggerMode.Manual;

        /// <summary>Mean days between occurrences, for Occasionally.</summary>
        public float MtbDays = 10f;

        /// <summary>Hour 0-23 the Daily roll happens at.</summary>
        public int DailyHour = 8;

        /// <summary>Chance 0-1 that a Daily roll actually fires.</summary>
        public float DailyChance = 1f;

        /// <summary>Days before this event can fire again anywhere.</summary>
        public float MinRefireDays = 5f;

        /// <summary>Inclusive hour window the event may start in. Null means any time.</summary>
        public int? StartHourMin;
        public int? StartHourMax;
    }

    public class EventTarget
    {
        public List<string> PawnKinds = new List<string>();
        public List<string> RequiredTraits = new List<string>();
        public List<string> ExcludedTraits = new List<string>();
        public List<string> RequiredHediffs = new List<string>();
        public List<string> ExcludedHediffs = new List<string>();
        public Dictionary<string, float> WeightByTrait = new Dictionary<string, float>();

        /// <summary>"male", "female", or null for any.</summary>
        public string Gender;

        public float MinAge = -1f;
        public float MaxAge = -1f;

        /// <summary>Days before this event can hit the same pawn again.</summary>
        public float CooldownDays = 5f;

        /// <summary>Events sharing a tag can't run on the same pawn at once.</summary>
        public List<string> ExclusionTags = new List<string>();
    }

    public class HediffStageSpec
    {
        public float MinSeverity;
        public string Label;
        public float PainOffset;
        public Dictionary<string, float> StatOffsets = new Dictionary<string, float>();
        public Dictionary<string, float> CapMods = new Dictionary<string, float>();
    }

    /// <summary>An inline hediff definition, turned into a real HediffDef at load.</summary>
    public class CustomHediffSpec
    {
        public string DefName;
        public string Label = "";
        public string Description = "";
        public bool IsBad = true;
        public bool Tendable;
        public float MaxSeverity = 1f;
        public float SeverityPerDay;
        public float DisappearsAfterDays = -1f;
        public List<HediffStageSpec> Stages = new List<HediffStageSpec>();
    }

    /// <summary>
    /// One player-authored event, parsed from a JSON file. Every event targets exactly one
    /// pawn; the schema leaves room for a "roles" block if multi-pawn is added later.
    /// </summary>
    public class CustomEvent
    {
        public string DefName;
        public string Label;
        public string Description = "";
        public bool Enabled = true;

        public EventPhases Phases = new EventPhases();
        public EventTiming Timing = new EventTiming();
        public EventTrigger Trigger = new EventTrigger();
        public EventTarget Target = new EventTarget();
        public List<CustomHediffSpec> CustomHediffs = new List<CustomHediffSpec>();

        /// <summary>Absolute path this event was loaded from.</summary>
        public string SourcePath;

        public bool HasContinueText => Phases.Continue.HasText;

        public static CustomEvent FromJson(JsonValue root, string sourcePath)
        {
            var e = new CustomEvent
            {
                SourcePath = sourcePath,
                DefName = root.GetString("defName"),
                Description = root.GetString("description", ""),
                Enabled = root.GetBool("enabled", true)
            };

            e.Label = root.GetString("label", e.DefName);

            // ---- phases ----
            var phases = root.Get("phases");
            if (phases != null)
            {
                e.Phases.Beginning = PhaseSpec.FromJson(phases.Get("beginning"));
                e.Phases.Continue = PhaseSpec.FromJson(phases.Get("continue"));
                e.Phases.End = PhaseSpec.FromJson(phases.Get("end"));
            }

            // Older files put a weighted table at the top level; it meant "what happens at
            // the end", so fold it into END's effects when END doesn't define its own.
            var legacyOutcome = root.Get("outcome");
            if (legacyOutcome != null && legacyOutcome.Type == JsonType.Array && e.Phases.End.Effects.Count == 0)
            {
                var table = new PhaseEffect { OneOf = new List<PhaseEffect>() };
                foreach (var entry in legacyOutcome.Array)
                {
                    table.OneOf.Add(PhaseEffect.FromJson(entry));
                }

                if (table.OneOf.Count > 0) e.Phases.End.Effects.Add(table);
            }

            // ---- timing ----
            var timing = root.Get("timing");
            if (timing != null)
            {
                e.Timing.DurationHours = timing.GetFloat("durationHours", 12f);
                e.Timing.ContinueJitter = timing.GetFloat("continueJitter", 0.25f);
                e.Timing.BeatTimeoutHours = timing.GetFloat("beatTimeoutHours", 2f);
                e.Timing.MinBeatGapHours = timing.GetFloat("minBeatGapHours", 0.5f);
                e.Timing.ContinueCount = timing.GetInt("continueCount", DefaultContinueCount(e));
                e.Timing.OnBlocked = PhaseEffect.ParseEnum(timing.GetString("onBlocked"), BlockedPolicy.Retry);
                e.Timing.Spacing = PhaseEffect.ParseEnum(timing.GetString("continueSpacing"), ContinueSpacing.Even);
            }
            else
            {
                e.Timing.ContinueCount = DefaultContinueCount(e);
            }

            // ---- trigger ----
            var trigger = root.Get("trigger");
            if (trigger != null)
            {
                var mode = trigger.GetString("mode");

                // "storyteller" was a mode before this mod decided to stay out of RimWorld's
                // incident pacing. Map it to the nearest equivalent rather than silently
                // falling back to Manual, which would stop the event firing at all.
                e.Trigger.Mode = string.Equals(mode, "storyteller", StringComparison.OrdinalIgnoreCase)
                    ? TriggerMode.Occasionally
                    : PhaseEffect.ParseEnum(mode, TriggerMode.Manual);

                e.Trigger.MtbDays = trigger.GetFloat("mtbDays", 10f);
                e.Trigger.DailyHour = trigger.GetInt("dailyHour", 8);
                e.Trigger.DailyChance = trigger.GetFloat("dailyChance", 1f);
                e.Trigger.MinRefireDays = trigger.GetFloat("minRefireDays", 5f);

                var window = trigger.GetArray("allowedTimeOfDay");
                if (window.Count == 2)
                {
                    e.Trigger.StartHourMin = window[0].AsInt();
                    e.Trigger.StartHourMax = window[1].AsInt();
                }
            }

            // ---- target ----
            var target = root.Get("target");
            if (target != null)
            {
                e.Target.PawnKinds = target.GetStringList("pawnKinds");
                e.Target.RequiredTraits = target.GetStringList("requireTrait");
                e.Target.ExcludedTraits = target.GetStringList("excludeTrait");
                e.Target.RequiredHediffs = target.GetStringList("requireHediff");
                e.Target.ExcludedHediffs = target.GetStringList("excludeIfHediff");
                e.Target.WeightByTrait = target.GetFloatMap("weightByTrait");
                e.Target.Gender = target.GetString("gender");
                e.Target.MinAge = target.GetFloat("minAge", -1f);
                e.Target.MaxAge = target.GetFloat("maxAge", -1f);
                e.Target.CooldownDays = target.GetFloat("cooldownDays", 5f);
                e.Target.ExclusionTags = target.GetStringList("exclusionTags");
            }

            // ---- inline hediffs ----
            foreach (var entry in root.GetArray("customHediffs"))
            {
                e.CustomHediffs.Add(ParseHediffSpec(entry));
            }

            return e;
        }

        /// <summary>
        /// How many CONTINUE beats an event gets when the author doesn't say: roughly one
        /// every four hours, so a 12-hour event gets three. Zero when there's no CONTINUE
        /// text to replay.
        /// </summary>
        private static int DefaultContinueCount(CustomEvent e)
        {
            if (!e.HasContinueText) return 0;
            var derived = (int)Math.Round(e.Timing.DurationHours / 4f);
            return Math.Max(1, Math.Min(6, derived));
        }

        private static CustomHediffSpec ParseHediffSpec(JsonValue v)
        {
            var spec = new CustomHediffSpec
            {
                DefName = v.GetString("defName"),
                Label = v.GetString("label", ""),
                Description = v.GetString("description", ""),
                IsBad = v.GetBool("isBad", true),
                Tendable = v.GetBool("tendable"),
                MaxSeverity = v.GetFloat("maxSeverity", 1f),
                SeverityPerDay = v.GetFloat("severityPerDay"),
                DisappearsAfterDays = v.GetFloat("disappearsAfterDays", -1f)
            };

            foreach (var stageJson in v.GetArray("stages"))
            {
                spec.Stages.Add(new HediffStageSpec
                {
                    MinSeverity = stageJson.GetFloat("minSeverity"),
                    Label = stageJson.GetString("label"),
                    PainOffset = stageJson.GetFloat("painOffset"),
                    StatOffsets = stageJson.GetFloatMap("statOffsets"),
                    CapMods = stageJson.GetFloatMap("capMods")
                });
            }

            return spec;
        }

        /// <summary>All effects across all phases, for validation and def checking.</summary>
        public IEnumerable<PhaseEffect> AllEffects()
        {
            foreach (var effect in Flatten(Phases.Beginning.Effects)) yield return effect;
            foreach (var effect in Flatten(Phases.Continue.Effects)) yield return effect;
            foreach (var effect in Flatten(Phases.End.Effects)) yield return effect;
        }

        private static IEnumerable<PhaseEffect> Flatten(List<PhaseEffect> effects)
        {
            foreach (var effect in effects)
            {
                yield return effect;
                if (!effect.HasOneOf) continue;
                foreach (var child in Flatten(effect.OneOf)) yield return child;
            }
        }

        /// <summary>
        /// Returns problems that make the event unusable. An empty list means it can run.
        /// Cosmetic issues are reported through <paramref name="warnings"/> instead.
        /// </summary>
        public List<string> Validate(out List<string> warnings)
        {
            var errors = new List<string>();
            warnings = new List<string>();

            if (string.IsNullOrEmpty(DefName))
            {
                errors.Add("missing \"defName\"");
            }
            else if (DefName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                errors.Add($"\"defName\" contains characters that aren't valid in a filename: {DefName}");
            }

            if (!Phases.Beginning.HasText) errors.Add("missing \"phases.beginning\"");
            if (!Phases.End.HasText) errors.Add("missing \"phases.end\"");

            if (Timing.DurationHours <= 0f) errors.Add("\"timing.durationHours\" must be greater than 0");

            if (Timing.ContinueCount > 0 && !HasContinueText)
            {
                warnings.Add($"\"timing.continueCount\" is {Timing.ContinueCount} but \"phases.continue\" is empty — the event will go straight from BEGINNING to END");
            }

            if (Phases.Continue.Effects.Count > 0 && !HasContinueText)
            {
                warnings.Add("\"phases.continue\" has effects but no text — those effects will never run");
            }

            if (Timing.ContinueJitter < 0f || Timing.ContinueJitter > 1f)
            {
                warnings.Add("\"timing.continueJitter\" should be between 0 and 1; clamping");
                Timing.ContinueJitter = Math.Max(0f, Math.Min(1f, Timing.ContinueJitter));
            }

            foreach (var spec in CustomHediffs)
            {
                if (string.IsNullOrEmpty(spec.DefName))
                {
                    errors.Add("a \"customHediffs\" entry is missing \"defName\"");
                }
                else if (!spec.DefName.StartsWith("RTCE_"))
                {
                    warnings.Add($"custom hediff \"{spec.DefName}\" should start with RTCE_ to avoid clashing with other mods");
                }
            }

            foreach (var effect in AllEffects())
            {
                if (effect.HasOneOf)
                {
                    var total = 0f;
                    foreach (var arm in effect.OneOf) total += Math.Max(0f, arm.Weight);
                    if (total <= 0f) warnings.Add("a \"oneOf\" table has no positive weights — it will never pick anything");
                }

                if (effect.Hediff != null && string.IsNullOrEmpty(effect.Hediff.Def))
                {
                    warnings.Add("a \"hediff\" effect is missing \"def\"");
                }

                if (effect.Need != null && string.IsNullOrEmpty(effect.Need.Def))
                {
                    warnings.Add("a \"need\" effect is missing \"def\"");
                }
            }

            return errors;
        }
    }
}
