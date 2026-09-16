using System;
using System.Collections.Generic;
using System.Linq;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Util;
using RimWorld;
using Verse;

namespace RimTalkCustomEvents.Effects
{
    /// <summary>
    /// Turns inline <c>customHediffs</c> blocks into real HediffDefs and registers them in
    /// DefDatabase, so an event file can carry its own mechanical consequence without
    /// shipping XML.
    ///
    /// Runs after RimWorld has loaded every other def, so a def name that clashes with a
    /// real mod's is detectable and left alone.
    /// </summary>
    public static class HediffFactory
    {
        /// <summary>defNames this mod created, so a reload can update rather than duplicate.</summary>
        private static readonly HashSet<string> Created = new HashSet<string>();

        /// <summary>
        /// Rebuilds every inline hediff from the current event store. Safe to call again
        /// after a reload.
        /// </summary>
        public static void RegisterAll()
        {
            var specs = new Dictionary<string, CustomHediffSpec>();

            foreach (var customEvent in EventStore.All)
            {
                foreach (var spec in customEvent.CustomHediffs)
                {
                    if (string.IsNullOrEmpty(spec.DefName)) continue;

                    if (specs.ContainsKey(spec.DefName))
                    {
                        RTCELog.Warning(
                            $"Two events both define the hediff \"{spec.DefName}\" — using the first one found.");
                        continue;
                    }

                    specs[spec.DefName] = spec;
                }
            }

            var built = 0;
            var updated = 0;

            foreach (var pair in specs)
            {
                var existing = DefDatabase<HediffDef>.GetNamedSilentFail(pair.Key);

                if (existing != null && !Created.Contains(pair.Key))
                {
                    // Something else owns this name. Clobbering another mod's hediff would be
                    // far worse than the event referencing theirs, so leave it alone.
                    RTCELog.Warning(
                        $"\"{pair.Key}\" is already defined by {DescribeSource(existing)} — leaving it as-is. " +
                        "Rename the one in your event file if you meant a new hediff.");
                    continue;
                }

                try
                {
                    if (existing != null)
                    {
                        Populate(existing, pair.Value);
                        updated++;

                        // Comps are built into each Hediff when it's created, so a pawn who
                        // already carries this one keeps the old behaviour. Say so rather
                        // than leaving two pawns visibly different with no explanation.
                        if (AnyPawnHas(existing))
                        {
                            RTCELog.Warning(
                                $"\"{pair.Key}\" changed, but pawns already carrying it keep the previous " +
                                "severity decay and duration until the save is reloaded.");
                        }
                    }
                    else
                    {
                        var def = new HediffDef { defName = pair.Key };
                        Populate(def, pair.Value);
                        Register(def);
                        Created.Add(pair.Key);
                        built++;
                    }
                }
                catch (Exception ex)
                {
                    RTCELog.Error($"Could not build hediff \"{pair.Key}\": {ex.Message}");
                }
            }

            if (built > 0 || updated > 0)
            {
                RTCELog.Message($"Built {built} custom hediff(s), updated {updated}.");
            }
        }

        /// <summary>Whether any live pawn is currently carrying this hediff.</summary>
        private static bool AnyPawnHas(HediffDef def)
        {
            var maps = Verse.Current.Game?.Maps;
            if (maps == null) return false;

            foreach (var map in maps)
            {
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn.health?.hediffSet?.GetFirstHediffOfDef(def) != null) return true;
                }
            }

            return false;
        }

        private static string DescribeSource(Def def)
        {
            var mod = def.modContentPack?.Name;
            return string.IsNullOrEmpty(mod) ? "another mod" : mod;
        }

        private static void Populate(HediffDef def, CustomHediffSpec spec)
        {
            def.label = string.IsNullOrEmpty(spec.Label) ? spec.DefName : spec.Label;
            def.description = spec.Description ?? "";
            def.hediffClass = typeof(HediffWithComps);
            def.isBad = spec.IsBad;
            def.tendable = spec.Tendable;
            def.maxSeverity = spec.MaxSeverity > 0f ? spec.MaxSeverity : 1f;
            def.scenarioCanAdd = false;
            def.defaultLabelColor = spec.IsBad ? new UnityEngine.Color(0.8f, 0.4f, 0.4f) : new UnityEngine.Color(0.6f, 0.8f, 0.6f);

            def.stages = BuildStages(spec);
            def.comps = BuildComps(spec);
        }

        private static List<HediffStage> BuildStages(CustomHediffSpec spec)
        {
            if (spec.Stages == null || spec.Stages.Count == 0) return null;

            var stages = new List<HediffStage>();

            foreach (var stageSpec in spec.Stages.OrderBy(s => s.MinSeverity))
            {
                var stage = new HediffStage
                {
                    minSeverity = stageSpec.MinSeverity,
                    label = stageSpec.Label,
                    painOffset = stageSpec.PainOffset
                };

                foreach (var pair in stageSpec.StatOffsets)
                {
                    var stat = DefDatabase<StatDef>.GetNamedSilentFail(pair.Key);
                    if (stat == null)
                    {
                        RTCELog.Warning($"\"{spec.DefName}\": no stat named \"{pair.Key}\" is loaded — skipping that offset.");
                        continue;
                    }

                    stage.statOffsets = stage.statOffsets ?? new List<StatModifier>();
                    stage.statOffsets.Add(new StatModifier { stat = stat, value = pair.Value });
                }

                foreach (var pair in stageSpec.CapMods)
                {
                    var capacity = DefDatabase<PawnCapacityDef>.GetNamedSilentFail(pair.Key);
                    if (capacity == null)
                    {
                        RTCELog.Warning($"\"{spec.DefName}\": no pawn capacity named \"{pair.Key}\" is loaded — skipping that modifier.");
                        continue;
                    }

                    stage.capMods = stage.capMods ?? new List<PawnCapacityModifier>();
                    stage.capMods.Add(new PawnCapacityModifier { capacity = capacity, offset = pair.Value });
                }

                stages.Add(stage);
            }

            return stages;
        }

        private static List<HediffCompProperties> BuildComps(CustomHediffSpec spec)
        {
            var comps = new List<HediffCompProperties>();

            if (Math.Abs(spec.SeverityPerDay) > 0.0001f)
            {
                comps.Add(new HediffCompProperties_SeverityPerDay { severityPerDay = spec.SeverityPerDay });
            }

            if (spec.DisappearsAfterDays > 0f)
            {
                var ticks = (int)(spec.DisappearsAfterDays * GenDate.TicksPerDay);
                comps.Add(new HediffCompProperties_Disappears
                {
                    disappearsAfterTicks = new IntRange(ticks, ticks),
                    showRemainingTime = true
                });
            }

            return comps.Count > 0 ? comps : null;
        }

        /// <summary>
        /// Adds the def to the database with a short hash. Without a hash the def can't be
        /// saved or loaded and the game throws when a pawn carrying it is written out.
        /// </summary>
        private static void Register(HediffDef def)
        {
            def.modContentPack = RimTalkCustomEventsMod.Instance?.Content;
            DefDatabase<HediffDef>.Add(def);
            AssignShortHash(def);
            RTCELog.Debug($"Registered hediff \"{def.defName}\" ({def.label})");
        }

        /// <summary>
        /// Assigns a short hash the way RimWorld does internally.
        ///
        /// <c>ShortHashGiver.GiveShortHash</c> is private, so this goes through reflection.
        /// It takes the set of hashes already handed out for the def type, and records the
        /// new one in it — so we must pass RimWorld's own set rather than a copy, or a def
        /// registered later could be given the same hash.
        /// </summary>
        private static void AssignShortHash(HediffDef def)
        {
            var giver = HarmonyLib.AccessTools.TypeByName("Verse.ShortHashGiver");
            if (giver == null)
            {
                RTCELog.Error("Could not find Verse.ShortHashGiver — custom hediffs will not survive a save.");
                return;
            }

            var method = HarmonyLib.AccessTools.Method(giver, "GiveShortHash",
                new[] { typeof(Def), typeof(Type), typeof(HashSet<ushort>) });

            if (method == null)
            {
                RTCELog.Error("ShortHashGiver.GiveShortHash has changed shape — custom hediffs will not survive a save.");
                return;
            }

            try
            {
                method.Invoke(null, new object[] { def, typeof(HediffDef), TakenHashesFor(giver) });
            }
            catch (Exception ex)
            {
                RTCELog.Error($"Could not assign a short hash to \"{def.defName}\": {ex.Message}");
            }
        }

        private static HashSet<ushort> TakenHashesFor(Type giver)
        {
            var field = HarmonyLib.AccessTools.Field(giver, "takenHashesPerDeftype");
            var dictionary = field?.GetValue(null) as System.Collections.IDictionary;

            if (dictionary == null)
            {
                // Startup normally fills this in. Falling back to a set built from the
                // database keeps us collision-free against what exists right now.
                RTCELog.Warning("ShortHashGiver's hash table was unreadable; falling back to a snapshot.");
                return new HashSet<ushort>(DefDatabase<HediffDef>.AllDefs.Select(d => d.shortHash));
            }

            if (dictionary.Contains(typeof(HediffDef)))
            {
                return (HashSet<ushort>)dictionary[typeof(HediffDef)];
            }

            var created = new HashSet<ushort>();
            dictionary[typeof(HediffDef)] = created;
            return created;
        }
    }
}
