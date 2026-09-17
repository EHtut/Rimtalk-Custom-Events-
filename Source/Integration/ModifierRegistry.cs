using System;
using System.Collections.Generic;
using System.Linq;
using RimTalk.API;
using RimTalkCustomEvents.Util;
using Verse;

namespace RimTalkCustomEvents.Integration
{
    /// <summary>
    /// Carries Modifier-mode CONTINUE text into every prompt RimTalk builds for a pawn.
    ///
    /// Unlike a beat, a modifier never becomes a line of its own — it rides along with
    /// whatever the pawn was going to say anyway, so a cold that "grows" shows up while
    /// they're arguing about dinner rather than as a separate announcement.
    ///
    /// The active set is rebuilt from live instances each tick rather than registered and
    /// unregistered as events start and stop. One hook, no lifecycle to get wrong, and it
    /// reconstructs itself correctly after a save is loaded.
    /// </summary>
    public static class ModifierRegistry
    {
        private const string SectionName = "RTCE_EventModifiers";

        private static readonly Dictionary<Pawn, List<string>> Active = new Dictionary<Pawn, List<string>>();

        private static bool _hooked;
        private static bool _hookFailed;

        /// <summary>
        /// Registers the single pawn-section hook with RimTalk. Safe to call repeatedly;
        /// only the first call does anything.
        /// </summary>
        public static void EnsureHooked()
        {
            if (_hooked || _hookFailed) return;

            try
            {
                RimTalkPromptAPI.InjectPawnSection(
                    RimTalkCustomEventsMod.ModId,
                    SectionName,
                    ContextCategories.Pawn.Health,
                    ContextHookRegistry.InjectPosition.After,
                    DescribeFor);

                _hooked = true;
                RTCELog.Debug("Registered the modifier hook with RimTalk.");
            }
            catch (Exception ex)
            {
                // Without this, Modifier-mode events simply have no effect on dialogue.
                // Say so once rather than leaving an author wondering.
                _hookFailed = true;
                RTCELog.Error(
                    "Could not register the prompt hook with RimTalk, so Modifier-mode events "
                    + $"will not affect dialogue: {ex.Message}");
            }
        }

        /// <summary>
        /// Replaces the active set with the modifiers of the instances given. Anything not
        /// in the list stops applying immediately.
        /// </summary>
        public static void Sync(IEnumerable<KeyValuePair<Pawn, string>> modifiers)
        {
            Active.Clear();

            foreach (var pair in modifiers)
            {
                if (pair.Key == null || string.IsNullOrWhiteSpace(pair.Value)) continue;

                if (!Active.TryGetValue(pair.Key, out var list))
                {
                    list = new List<string>();
                    Active[pair.Key] = list;
                }

                if (!list.Contains(pair.Value)) list.Add(pair.Value);
            }
        }

        public static void Clear()
        {
            Active.Clear();
        }

        public static bool HasAny => Active.Count > 0;

        /// <summary>
        /// Called by RimTalk while it assembles a pawn's context. Returns null when there's
        /// nothing to add, so no empty section is emitted.
        /// </summary>
        private static string DescribeFor(Pawn pawn)
        {
            try
            {
                if (pawn == null || !Active.TryGetValue(pawn, out var list) || list.Count == 0) return null;
                return string.Join(" ", list.ToArray());
            }
            catch (Exception ex)
            {
                // This runs inside RimTalk's prompt assembly; throwing here would break the
                // pawn's dialogue entirely.
                RTCELog.WarnOnce($"Modifier lookup failed: {ex.Message}", 0x4D0D01);
                return null;
            }
        }
    }
}
