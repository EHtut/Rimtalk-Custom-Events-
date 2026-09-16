using Verse;

namespace RimTalkCustomEvents.Settings
{
    /// <summary>
    /// Persisted mod settings. Event definitions are NOT stored here — they live as JSON
    /// files in the config dir (see EventStore) so they survive a settings reset and can
    /// be shared as individual files.
    /// </summary>
    public class RTCESettings : ModSettings
    {
        /// <summary>Master switch. When false, nothing is scheduled and nothing fires.</summary>
        public bool enabled = true;

        /// <summary>Scales every event's trigger chance. 0 = never, 1 = as authored.</summary>
        public float frequencyMultiplier = 1f;

        /// <summary>Cap on simultaneously running events across the whole colony.</summary>
        public int maxConcurrentGlobal = 3;

        /// <summary>Cap per pawn. 1 means a pawn is never in two events at once.</summary>
        public int maxConcurrentPerPawn = 1;

        // Which pawns are eligible to be targeted.
        public bool allowColonists = true;
        public bool allowPrisoners = false;
        public bool allowSlaves = false;
        public bool allowGuests = false;
        public bool allowAnimals = false;

        /// <summary>
        /// Wraps each beat before it is handed to RimTalk. Supported tokens:
        /// {event} {phase} {index} {total} {text}
        /// </summary>
        public string promptWrapper = "[EVENT: {event} — {phase}]\n{text}";

        public bool debugLogging = false;

        public const string DefaultPromptWrapper = "[EVENT: {event} — {phase}]\n{text}";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref frequencyMultiplier, "frequencyMultiplier", 1f);
            Scribe_Values.Look(ref maxConcurrentGlobal, "maxConcurrentGlobal", 3);
            Scribe_Values.Look(ref maxConcurrentPerPawn, "maxConcurrentPerPawn", 1);
            Scribe_Values.Look(ref allowColonists, "allowColonists", true);
            Scribe_Values.Look(ref allowPrisoners, "allowPrisoners", false);
            Scribe_Values.Look(ref allowSlaves, "allowSlaves", false);
            Scribe_Values.Look(ref allowGuests, "allowGuests", false);
            Scribe_Values.Look(ref allowAnimals, "allowAnimals", false);
            Scribe_Values.Look(ref promptWrapper, "promptWrapper", DefaultPromptWrapper);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit && string.IsNullOrEmpty(promptWrapper))
            {
                promptWrapper = DefaultPromptWrapper;
            }
        }
    }
}
