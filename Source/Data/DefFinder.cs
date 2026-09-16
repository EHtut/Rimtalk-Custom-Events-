using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimTalkCustomEvents.Data
{
    /// <summary>The kinds of def an event effect can reference.</summary>
    public enum DefKind
    {
        Hediff,
        Thought,
        Incident,
        Item,
        Trait,
        Need,
        Skill,
        BodyPart,
        Stat,
        PawnCapacity
    }

    /// <summary>One selectable def, with the mod it came from.</summary>
    public class DefEntry
    {
        public string DefName;
        public string Label;
        public string ModName;

        /// <summary>"label (defName)" when they differ, for a picker row.</summary>
        public string Display =>
            string.IsNullOrEmpty(Label) || string.Equals(Label, DefName, StringComparison.OrdinalIgnoreCase)
                ? DefName
                : $"{Label}  ({DefName})";

        public bool Matches(string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            return (DefName != null && DefName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (Label != null && Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    /// <summary>
    /// Enumerates every def actually loaded in this playthrough, so events can reach content
    /// from any mod in the load order rather than a hardcoded vanilla list.
    ///
    /// Everything is read from DefDatabase at call time — add a mod and its content simply
    /// appears, with no change here.
    /// </summary>
    public static class DefFinder
    {
        public static IEnumerable<DefEntry> All(DefKind kind)
        {
            switch (kind)
            {
                case DefKind.Hediff: return Wrap(DefDatabase<HediffDef>.AllDefs);

                // Memories only: situational thoughts are derived from world state and
                // can't be handed to a pawn directly.
                case DefKind.Thought: return Wrap(DefDatabase<ThoughtDef>.AllDefs.Where(d => !d.IsSituational));

                case DefKind.Incident: return Wrap(DefDatabase<IncidentDef>.AllDefs);

                // Items only — the full ThingDef list is mostly terrain, filth and corpses.
                case DefKind.Item: return Wrap(DefDatabase<ThingDef>.AllDefs.Where(d => d.category == ThingCategory.Item));

                case DefKind.Trait: return Wrap(DefDatabase<TraitDef>.AllDefs);
                case DefKind.Need: return Wrap(DefDatabase<NeedDef>.AllDefs);
                case DefKind.Skill: return Wrap(DefDatabase<SkillDef>.AllDefs);
                case DefKind.BodyPart: return Wrap(DefDatabase<BodyPartDef>.AllDefs);
                case DefKind.Stat: return Wrap(DefDatabase<StatDef>.AllDefs);
                case DefKind.PawnCapacity: return Wrap(DefDatabase<PawnCapacityDef>.AllDefs);

                default: return Enumerable.Empty<DefEntry>();
            }
        }

        private static IEnumerable<DefEntry> Wrap<T>(IEnumerable<T> defs) where T : Def
        {
            foreach (var def in defs)
            {
                yield return new DefEntry
                {
                    DefName = def.defName,
                    Label = def.label,
                    ModName = def.modContentPack?.Name ?? "Unknown"
                };
            }
        }

        /// <summary>
        /// Type-ahead over label and defName, optionally narrowed to one source mod.
        /// Ordered so exact-ish matches surface first, then alphabetically.
        /// </summary>
        public static List<DefEntry> Search(DefKind kind, string query, string modName = null, int limit = 200)
        {
            var results = All(kind);

            if (!string.IsNullOrEmpty(modName))
            {
                results = results.Where(e => e.ModName == modName);
            }

            if (!string.IsNullOrEmpty(query))
            {
                results = results.Where(e => e.Matches(query));
            }

            return results
                .OrderBy(e => Rank(e, query))
                .ThenBy(e => e.Label ?? e.DefName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private static int Rank(DefEntry entry, string query)
        {
            if (string.IsNullOrEmpty(query)) return 1;

            if (string.Equals(entry.DefName, query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (entry.Label != null && entry.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
            if (entry.DefName != null && entry.DefName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        /// <summary>Distinct source mods contributing defs of this kind, for a filter dropdown.</summary>
        public static List<string> SourceMods(DefKind kind)
        {
            return All(kind)
                .Select(e => e.ModName)
                .Distinct()
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool Exists(DefKind kind, string defName)
        {
            if (string.IsNullOrEmpty(defName)) return false;

            switch (kind)
            {
                case DefKind.Hediff: return DefDatabase<HediffDef>.GetNamedSilentFail(defName) != null;
                case DefKind.Thought: return DefDatabase<ThoughtDef>.GetNamedSilentFail(defName) != null;
                case DefKind.Incident: return DefDatabase<IncidentDef>.GetNamedSilentFail(defName) != null;
                case DefKind.Item: return DefDatabase<ThingDef>.GetNamedSilentFail(defName) != null;
                case DefKind.Trait: return DefDatabase<TraitDef>.GetNamedSilentFail(defName) != null;
                case DefKind.Need: return DefDatabase<NeedDef>.GetNamedSilentFail(defName) != null;
                case DefKind.Skill: return DefDatabase<SkillDef>.GetNamedSilentFail(defName) != null;
                case DefKind.BodyPart: return DefDatabase<BodyPartDef>.GetNamedSilentFail(defName) != null;
                case DefKind.Stat: return DefDatabase<StatDef>.GetNamedSilentFail(defName) != null;
                case DefKind.PawnCapacity: return DefDatabase<PawnCapacityDef>.GetNamedSilentFail(defName) != null;
                default: return false;
            }
        }

        /// <summary>
        /// Checks every def an event names against what's loaded.
        ///
        /// Returns warnings, not errors: an event file shared between two load orders should
        /// still run the parts that resolve, rather than refusing to load outright. The
        /// alternative — finding out at the moment the effect silently does nothing — is
        /// much harder to diagnose.
        /// </summary>
        public static List<string> ValidateReferences(CustomEvent customEvent)
        {
            var problems = new List<string>();

            // Hediffs the event defines itself are registered by HediffFactory, so they
            // count as present even before that has run.
            var ownHediffs = new HashSet<string>(
                customEvent.CustomHediffs.Where(h => !string.IsNullOrEmpty(h.DefName)).Select(h => h.DefName),
                StringComparer.OrdinalIgnoreCase);

            foreach (var effect in customEvent.AllEffects())
            {
                if (effect.Hediff != null && !string.IsNullOrEmpty(effect.Hediff.Def)
                    && !ownHediffs.Contains(effect.Hediff.Def)
                    && !Exists(DefKind.Hediff, effect.Hediff.Def))
                {
                    problems.Add(Missing("hediff", effect.Hediff.Def));
                }

                if (effect.Hediff != null && !string.IsNullOrEmpty(effect.Hediff.BodyPart)
                    && !Exists(DefKind.BodyPart, effect.Hediff.BodyPart))
                {
                    problems.Add(Missing("body part", effect.Hediff.BodyPart));
                }

                if (!string.IsNullOrEmpty(effect.Thought) && !Exists(DefKind.Thought, effect.Thought))
                {
                    problems.Add(Missing("thought", effect.Thought));
                }

                if (effect.Need != null && !string.IsNullOrEmpty(effect.Need.Def)
                    && !Exists(DefKind.Need, effect.Need.Def))
                {
                    problems.Add(Missing("need", effect.Need.Def));
                }

                if (!string.IsNullOrEmpty(effect.Incident) && !Exists(DefKind.Incident, effect.Incident))
                {
                    problems.Add(Missing("incident", effect.Incident));
                }

                if (!string.IsNullOrEmpty(effect.TraitDef) && !Exists(DefKind.Trait, effect.TraitDef))
                {
                    problems.Add(Missing("trait", effect.TraitDef));
                }

                if (!string.IsNullOrEmpty(effect.SkillXpSkill) && !Exists(DefKind.Skill, effect.SkillXpSkill))
                {
                    problems.Add(Missing("skill", effect.SkillXpSkill));
                }

                foreach (var item in effect.Items)
                {
                    if (!string.IsNullOrEmpty(item.Def) && !Exists(DefKind.Item, item.Def))
                    {
                        problems.Add(Missing("item", item.Def));
                    }
                }

                if (!string.IsNullOrEmpty(effect.ChainEvent) && EventStore.Get(effect.ChainEvent) == null)
                {
                    problems.Add($"chains to \"{effect.ChainEvent}\", which isn't one of your events");
                }
            }

            // Stats and capacities named by inline hediff stages.
            foreach (var spec in customEvent.CustomHediffs)
            {
                foreach (var stage in spec.Stages)
                {
                    foreach (var stat in stage.StatOffsets.Keys.Where(k => !Exists(DefKind.Stat, k)))
                    {
                        problems.Add(Missing("stat", stat));
                    }

                    foreach (var capacity in stage.CapMods.Keys.Where(k => !Exists(DefKind.PawnCapacity, k)))
                    {
                        problems.Add(Missing("pawn capacity", capacity));
                    }
                }
            }

            return problems.Distinct().ToList();
        }

        private static string Missing(string kind, string defName)
        {
            return $"references the {kind} \"{defName}\", which no loaded mod provides — that effect will do nothing";
        }
    }
}
