using System;
using System.Collections.Generic;
using System.Linq;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimTalkCustomEvents.Effects
{
    /// <summary>
    /// Applies a phase's effects when its beat is delivered.
    ///
    /// Every effect is independently guarded: one bad def name or missing need shouldn't
    /// stop the rest of the phase, and must never throw into RimWorld's tick loop.
    /// </summary>
    public static class EffectRunner
    {
        public static void RunAll(List<PhaseEffect> effects, Pawn pawn, string eventLabel)
        {
            if (effects == null || effects.Count == 0 || pawn == null) return;

            foreach (var effect in effects)
            {
                try
                {
                    Run(effect, pawn, eventLabel);
                }
                catch (Exception ex)
                {
                    RTCELog.Error($"\"{eventLabel}\": an effect threw and was skipped — {ex.Message}");
                }
            }
        }

        private static void Run(PhaseEffect effect, Pawn pawn, string eventLabel)
        {
            if (effect == null) return;

            // A weighted table picks exactly one arm, then that arm applies normally.
            if (effect.HasOneOf)
            {
                var chosen = RollOneOf(effect.OneOf);
                if (chosen != null) Run(chosen, pawn, eventLabel);
                return;
            }

            if (effect.Nothing) return;
            if (effect.Chance < 1f && !Rand.Chance(effect.Chance)) return;

            if (effect.Hediff != null) ApplyHediff(effect.Hediff, pawn, eventLabel);
            if (!string.IsNullOrEmpty(effect.Thought)) ApplyThought(effect.Thought, pawn, eventLabel);
            if (effect.Need != null) ApplyNeed(effect.Need, pawn, eventLabel);
            if (!string.IsNullOrEmpty(effect.Incident)) FireIncident(effect.Incident, pawn, eventLabel);
            if (!string.IsNullOrEmpty(effect.ChainEvent)) QueueChain(effect.ChainEvent, pawn, eventLabel);
            if (!string.IsNullOrEmpty(effect.TraitDef)) ApplyTrait(effect, pawn, eventLabel);
            if (!string.IsNullOrEmpty(effect.SkillXpSkill)) ApplySkillXp(effect, pawn, eventLabel);
            if (effect.Items.Count > 0) SpawnItems(effect.Items, pawn, eventLabel);
            if (!string.IsNullOrEmpty(effect.Message)) ShowMessage(effect.Message, pawn);
        }

        private static PhaseEffect RollOneOf(List<PhaseEffect> arms)
        {
            var weights = arms.Select(a => a.Weight).ToList();
            var index = Weighted.PickIndex(weights, Rand.Value);
            return index < 0 ? null : arms[index];
        }

        /// <summary>
        /// Adds to existing severity by default, which is how a CONTINUE beat escalates:
        /// three beats of +0.2 leave the pawn at 0.6 by END.
        /// </summary>
        private static void ApplyHediff(EffectHediff spec, Pawn pawn, string eventLabel)
        {
            if (string.IsNullOrEmpty(spec.Def)) return;

            var def = DefDatabase<HediffDef>.GetNamedSilentFail(spec.Def);
            if (def == null)
            {
                RTCELog.Warning($"\"{eventLabel}\": no hediff named \"{spec.Def}\" is loaded — skipping.");
                return;
            }

            if (pawn.health == null) return;

            BodyPartRecord part = null;
            if (!string.IsNullOrEmpty(spec.BodyPart))
            {
                var partDef = DefDatabase<BodyPartDef>.GetNamedSilentFail(spec.BodyPart);
                if (partDef == null)
                {
                    RTCELog.Warning($"\"{eventLabel}\": no body part named \"{spec.BodyPart}\" is loaded — applying to the whole pawn.");
                }
                else
                {
                    part = pawn.health.hediffSet.GetNotMissingParts().FirstOrDefault(p => p.def == partDef);
                    if (part == null)
                    {
                        RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort} has no {partDef.label} — applying to the whole pawn.");
                    }
                }
            }

            // Match on the exact part, so a whole-pawn effect (part == null) doesn't deepen
            // an injury that belongs to a specific limb.
            var existing = pawn.health.hediffSet.hediffs
                .FirstOrDefault(h => h.def == def && h.Part == part);

            if (existing != null)
            {
                var updated = spec.Mode == HediffApplyMode.Set
                    ? spec.Severity
                    : existing.Severity + spec.Severity;

                // Reaching zero removes it. Without this a negative severity could never
                // clear a hediff — which is how an event lifts a curse it applied earlier.
                if (updated <= 0f)
                {
                    pawn.health.RemoveHediff(existing);
                    RTCELog.Debug($"\"{eventLabel}\": removed {def.label} from {pawn.LabelShort}");
                    return;
                }

                existing.Severity = Math.Min(updated, def.maxSeverity);
                RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort}'s {def.label} is now {existing.Severity:0.##}");
                return;
            }

            // Nothing to reduce, so a negative or zero severity is a no-op rather than
            // a new hediff that starts out already gone.
            if (spec.Severity <= 0f) return;

            var hediff = HediffMaker.MakeHediff(def, pawn, part);
            hediff.Severity = Math.Min(spec.Severity, def.maxSeverity);
            pawn.health.AddHediff(hediff, part);
            RTCELog.Debug($"\"{eventLabel}\": gave {pawn.LabelShort} {def.label} at {hediff.Severity:0.##}");
        }

        private static void ApplyThought(string thoughtDefName, Pawn pawn, string eventLabel)
        {
            var def = DefDatabase<ThoughtDef>.GetNamedSilentFail(thoughtDefName);
            if (def == null)
            {
                RTCELog.Warning($"\"{eventLabel}\": no thought named \"{thoughtDefName}\" is loaded — skipping.");
                return;
            }

            var memories = pawn.needs?.mood?.thoughts?.memories;
            if (memories == null)
            {
                RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort} has no mood, so \"{thoughtDefName}\" was skipped.");
                return;
            }

            if (def.IsSituational)
            {
                RTCELog.Warning($"\"{eventLabel}\": \"{thoughtDefName}\" is a situational thought, which can't be given directly — use a memory thought.");
                return;
            }

            memories.TryGainMemory(def);
            RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort} gained the thought {def.defName}");
        }

        private static void ApplyNeed(EffectNeed spec, Pawn pawn, string eventLabel)
        {
            if (string.IsNullOrEmpty(spec.Def)) return;

            var def = DefDatabase<NeedDef>.GetNamedSilentFail(spec.Def);
            if (def == null)
            {
                RTCELog.Warning($"\"{eventLabel}\": no need named \"{spec.Def}\" is loaded — skipping.");
                return;
            }

            var need = pawn.needs?.TryGetNeed(def);
            if (need == null)
            {
                RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort} has no {def.label} need — skipping.");
                return;
            }

            need.CurLevel = Mathf.Clamp(need.CurLevel + spec.Offset, 0f, need.MaxLevel);
            RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort}'s {def.label} is now {need.CurLevel:0.##}");
        }

        /// <summary>
        /// Fires a real RimWorld incident on the pawn's map — the "massive event at the end".
        /// </summary>
        private static void FireIncident(string incidentDefName, Pawn pawn, string eventLabel)
        {
            var def = DefDatabase<IncidentDef>.GetNamedSilentFail(incidentDefName);
            if (def == null)
            {
                RTCELog.Warning($"\"{eventLabel}\": no incident named \"{incidentDefName}\" is loaded — skipping.");
                return;
            }

            var map = pawn.MapHeld;
            if (map == null)
            {
                RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort} isn't on a map, so \"{incidentDefName}\" was skipped.");
                return;
            }

            // DefaultParmsNow only *computes* sensible parameters (target map, and threat
            // points derived from colony wealth and difficulty). It does not register with
            // the storyteller, consume its budget, or affect its pacing — the mod still
            // decides entirely on its own when this fires. Building parms by hand instead
            // would leave points at zero and break any incident that scales with them.
            var parms = StorytellerUtility.DefaultParmsNow(def.category, map);

            if (!def.Worker.CanFireNow(parms))
            {
                // Normal and common: a cold snap can't fire during one that's already running.
                RTCELog.Debug($"\"{eventLabel}\": \"{incidentDefName}\" can't fire right now — skipping.");
                return;
            }

            if (def.Worker.TryExecute(parms))
            {
                RTCELog.Message($"\"{eventLabel}\": fired incident \"{incidentDefName}\".");
            }
            else
            {
                RTCELog.Debug($"\"{eventLabel}\": \"{incidentDefName}\" declined to execute.");
            }
        }

        private static void QueueChain(string chainDefName, Pawn pawn, string eventLabel)
        {
            if (EventStore.Get(chainDefName) == null)
            {
                RTCELog.Warning($"\"{eventLabel}\": chained event \"{chainDefName}\" doesn't exist — skipping.");
                return;
            }

            var component = Scheduling.CustomEventsGameComponent.Current;
            if (component == null) return;

            component.QueueChain(chainDefName, pawn);
            RTCELog.Debug($"\"{eventLabel}\": queued chained event \"{chainDefName}\" for {pawn.LabelShort}.");
        }

        private static void ApplyTrait(PhaseEffect effect, Pawn pawn, string eventLabel)
        {
            var def = DefDatabase<TraitDef>.GetNamedSilentFail(effect.TraitDef);
            if (def == null)
            {
                RTCELog.Warning($"\"{eventLabel}\": no trait named \"{effect.TraitDef}\" is loaded — skipping.");
                return;
            }

            var traits = pawn.story?.traits;
            if (traits == null)
            {
                RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort} has no traits to change.");
                return;
            }

            var existing = traits.allTraits.FirstOrDefault(t => t.def == def);

            if (effect.TraitRemove)
            {
                if (existing == null) return;
                traits.RemoveTrait(existing);
                RTCELog.Debug($"\"{eventLabel}\": removed {def.defName} from {pawn.LabelShort}.");
                return;
            }

            if (existing != null) return;

            traits.GainTrait(new Trait(def, effect.TraitDegree));
            RTCELog.Debug($"\"{eventLabel}\": gave {pawn.LabelShort} the trait {def.defName}.");
        }

        private static void ApplySkillXp(PhaseEffect effect, Pawn pawn, string eventLabel)
        {
            var def = DefDatabase<SkillDef>.GetNamedSilentFail(effect.SkillXpSkill);
            if (def == null)
            {
                RTCELog.Warning($"\"{eventLabel}\": no skill named \"{effect.SkillXpSkill}\" is loaded — skipping.");
                return;
            }

            var record = pawn.skills?.GetSkill(def);
            if (record == null || record.TotallyDisabled)
            {
                RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort} can't learn {def.defName}.");
                return;
            }

            // direct: true bypasses the daily learning-rate falloff, so a one-off event
            // grants what it says it grants.
            record.Learn(effect.SkillXpAmount, true);
            RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort} gained {effect.SkillXpAmount} {def.defName} XP.");
        }

        private static void SpawnItems(List<EffectItem> items, Pawn pawn, string eventLabel)
        {
            var map = pawn.MapHeld;
            if (map == null)
            {
                RTCELog.Debug($"\"{eventLabel}\": {pawn.LabelShort} isn't on a map, so no items were spawned.");
                return;
            }

            foreach (var item in items)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(item.Def);
                if (def == null)
                {
                    RTCELog.Warning($"\"{eventLabel}\": no item named \"{item.Def}\" is loaded — skipping.");
                    continue;
                }

                var remaining = Math.Max(1, item.Count);

                // Anything above a stack limit has to be placed as several stacks.
                while (remaining > 0)
                {
                    var stack = Math.Min(remaining, def.stackLimit > 0 ? def.stackLimit : remaining);
                    remaining -= stack;

                    var thing = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
                    thing.stackCount = stack;

                    if (!GenPlace.TryPlaceThing(thing, pawn.PositionHeld, map, ThingPlaceMode.Near))
                    {
                        RTCELog.Warning($"\"{eventLabel}\": couldn't find room to place {def.label}.");
                        thing.Destroy();
                        break;
                    }
                }

                RTCELog.Debug($"\"{eventLabel}\": spawned {item.Count}x {def.label} near {pawn.LabelShort}.");
            }
        }

        private static void ShowMessage(string text, Pawn pawn)
        {
            Messages.Message(text, pawn, MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
