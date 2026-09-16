using System.Collections.Generic;
using System.Linq;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Integration;
using RimTalkCustomEvents.Util;
using RimWorld;
using Verse;

namespace RimTalkCustomEvents.Scheduling
{
    /// <summary>
    /// Chooses who an automatically triggered event happens to.
    ///
    /// Deliberately simple for v1: category eligibility from mod settings, the filters in
    /// the event's <c>target</c> block, and a weighted pick. Richer conditions (mood, job,
    /// relationships, backstory) can layer on later without changing the call site.
    /// </summary>
    public static class PawnSelector
    {
        public static Pawn TryPick(CustomEvent def, Map map)
        {
            if (def == null || map == null) return null;

            var candidates = map.mapPawns.AllPawnsSpawned
                .Where(p => IsEligible(def, p))
                .ToList();

            if (candidates.Count == 0) return null;

            var weights = candidates.Select(p => WeightFor(def, p)).ToList();
            var index = Weighted.PickIndex(weights, Rand.Value);
            return index < 0 ? null : candidates[index];
        }

        /// <summary>Public so diagnostics can report the same eligibility the picker uses.</summary>
        public static bool IsEligible(CustomEvent def, Pawn pawn)
        {
            if (pawn == null || pawn.Dead || !pawn.Spawned) return false;
            if (!CategoryAllowed(pawn)) return false;
            if (!MatchesPawnKinds(def, pawn)) return false;

            // RimTalk has to be tracking the pawn or the beats can never be delivered.
            if (!RimTalkBridge.IsTracked(pawn)) return false;

            var target = def.Target;

            if (!string.IsNullOrEmpty(target.Gender))
            {
                var wanted = target.Gender.ToLowerInvariant();
                if (wanted == "male" && pawn.gender != Gender.Male) return false;
                if (wanted == "female" && pawn.gender != Gender.Female) return false;
            }

            var age = pawn.ageTracker?.AgeBiologicalYearsFloat ?? -1f;
            if (target.MinAge >= 0f && age >= 0f && age < target.MinAge) return false;
            if (target.MaxAge >= 0f && age >= 0f && age > target.MaxAge) return false;

            var traits = pawn.story?.traits;
            foreach (var traitName in target.RequiredTraits)
            {
                var traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(traitName);
                if (traitDef == null || traits == null || !traits.HasTrait(traitDef)) return false;
            }

            foreach (var traitName in target.ExcludedTraits)
            {
                var traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(traitName);
                if (traitDef != null && traits != null && traits.HasTrait(traitDef)) return false;
            }

            foreach (var hediffName in target.RequiredHediffs)
            {
                if (!HasHediff(pawn, hediffName)) return false;
            }

            foreach (var hediffName in target.ExcludedHediffs)
            {
                if (HasHediff(pawn, hediffName)) return false;
            }

            return true;
        }

        private static bool HasHediff(Pawn pawn, string hediffDefName)
        {
            var def = DefDatabase<HediffDef>.GetNamedSilentFail(hediffDefName);
            if (def == null || pawn.health == null) return false;
            return pawn.health.hediffSet.GetFirstHediffOfDef(def) != null;
        }

        /// <summary>Gate on the eligibility checkboxes in mod settings.</summary>
        private static bool CategoryAllowed(Pawn pawn)
        {
            var settings = RimTalkCustomEventsMod.Settings;
            if (settings == null) return false;

            if (pawn.RaceProps != null && pawn.RaceProps.Animal) return settings.allowAnimals;
            if (pawn.IsPrisonerOfColony) return settings.allowPrisoners;
            if (pawn.IsSlaveOfColony) return settings.allowSlaves;
            if (pawn.IsColonist) return settings.allowColonists;

            // Anything else humanlike on the map that isn't hostile: visitors, guests.
            if (pawn.Faction != null && pawn.HostileTo(Faction.OfPlayer)) return false;
            return settings.allowGuests;
        }

        /// <summary>
        /// Matches the event's <c>pawnKinds</c> list. Entries are either a broad category
        /// keyword or a PawnKindDef defName; an empty list means "anyone eligible".
        /// </summary>
        private static bool MatchesPawnKinds(CustomEvent def, Pawn pawn)
        {
            var kinds = def.Target.PawnKinds;
            if (kinds == null || kinds.Count == 0) return true;

            foreach (var kind in kinds)
            {
                switch (kind.ToLowerInvariant())
                {
                    case "colonist":
                        if (pawn.IsColonist && !pawn.IsPrisonerOfColony && !pawn.IsSlaveOfColony) return true;
                        continue;
                    case "prisoner":
                        if (pawn.IsPrisonerOfColony) return true;
                        continue;
                    case "slave":
                        if (pawn.IsSlaveOfColony) return true;
                        continue;
                    case "animal":
                        if (pawn.RaceProps != null && pawn.RaceProps.Animal) return true;
                        continue;
                    case "guest":
                        if (pawn.Faction != Faction.OfPlayer) return true;
                        continue;
                    default:
                        if (pawn.kindDef != null &&
                            string.Equals(pawn.kindDef.defName, kind, System.StringComparison.OrdinalIgnoreCase))
                            return true;
                        continue;
                }
            }

            return false;
        }

        /// <summary>Base weight of 1, multiplied by every matching entry in weightByTrait.</summary>
        private static float WeightFor(CustomEvent def, Pawn pawn)
        {
            var weight = 1f;
            var traits = pawn.story?.traits;
            if (traits == null) return weight;

            foreach (var pair in def.Target.WeightByTrait)
            {
                var traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(pair.Key);

                // weightByTrait is keyed by label in the docs, so fall back to a label match
                // when the key isn't a defName.
                var has = traitDef != null
                    ? traits.HasTrait(traitDef)
                    : traits.allTraits.Any(t => string.Equals(t.Label, pair.Key, System.StringComparison.OrdinalIgnoreCase));

                if (has) weight *= pair.Value;
            }

            return Mathf01Min(weight);
        }

        private static float Mathf01Min(float weight) => weight < 0f ? 0f : weight;
    }
}
