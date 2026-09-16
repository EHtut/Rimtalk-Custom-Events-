using System.Text;

namespace RimTalkCustomEvents.Util
{
    /// <summary>
    /// Applies the settings wrapper to a phase's text.
    ///
    /// Shared by the scheduler and the editor's preview so the two can't drift — a preview
    /// that shows something other than what the model receives is worse than no preview.
    /// Kept free of Verse types so it can be checked outside the game.
    /// </summary>
    public static class PromptBuilder
    {
        public const string DefaultWrapper = "[EVENT: {event} — {phase}]\n{text}";

        /// <summary>
        /// Substitutes the wrapper tokens. Unknown tokens are left alone rather than blanked,
        /// so a typo in the template shows up in the output instead of silently vanishing.
        /// </summary>
        public static string Build(
            string wrapper,
            string eventLabel,
            string phaseLabel,
            string phaseName,
            int index,
            int total,
            string pawnName,
            string text)
        {
            if (string.IsNullOrEmpty(wrapper)) wrapper = DefaultWrapper;

            var sb = new StringBuilder(wrapper);
            sb.Replace("{event}", eventLabel ?? "");
            sb.Replace("{phase}", phaseLabel ?? "");
            sb.Replace("{phaseName}", phaseName ?? "");
            sb.Replace("{index}", index.ToString());
            sb.Replace("{total}", total.ToString());
            sb.Replace("{pawn}", pawnName ?? "");
            sb.Replace("{text}", text ?? "");
            return sb.ToString();
        }

        /// <summary>The "CONTINUE 2/3" style label shown in the prompt and the UI.</summary>
        public static string PhaseLabel(string phaseName, int index, int total)
        {
            return phaseName == "CONTINUE" && total > 0
                ? $"CONTINUE {index}/{total}"
                : phaseName;
        }
    }
}
