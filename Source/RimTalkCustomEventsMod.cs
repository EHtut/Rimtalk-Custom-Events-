using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Scheduling;
using RimTalkCustomEvents.Settings;
using RimTalkCustomEvents.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimTalkCustomEvents
{
    public class RimTalkCustomEventsMod : Mod
    {
        public const string ModId = "ethan.rimtalkcustomevents";

        public static RimTalkCustomEventsMod Instance { get; private set; }

        /// <summary>Null before the mod is constructed; callers in early startup must null-check.</summary>
        public static RTCESettings Settings => Instance?._settings;

        private readonly RTCESettings _settings;

        private Vector2 _scrollPosition;
        private float _contentHeight = 600f;
        private Tab _tab = Tab.Events;

        private enum Tab
        {
            Events,
            Settings
        }

        public RimTalkCustomEventsMod(ModContentPack content) : base(content)
        {
            Instance = this;
            _settings = GetSettings<RTCESettings>();
        }

        public override string SettingsCategory() => "RimTalk Custom Events";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var tabRect = new Rect(inRect.x, inRect.y, inRect.width, 30f);
            DrawTabs(tabRect);

            var bodyRect = new Rect(inRect.x, inRect.y + 36f, inRect.width, inRect.height - 36f);
            var mode = _settings?.uiLayoutMode ?? 0;

            // Mode 1 skips the scroll view entirely. If the rows appear here but not in
            // mode 0, the scroll view's viewport is clipping them — which is the whole
            // question, and switching live answers it without another restart.
            if (mode == 1)
            {
                var plain = new Listing_Standard { maxOneColumn = true };
                plain.Begin(bodyRect);

                if (_tab == Tab.Events) DrawEventsTab(plain);
                else DrawSettingsTab(plain);

                _contentHeight = plain.CurHeight + 24f;
                plain.End();
                return;
            }

            // Mode 2 keeps the scroll view but gives it a viewport far taller than anything
            // could need, so a stale or undersized measurement cannot clip anything.
            var viewHeight = mode == 2
                ? Mathf.Max(_contentHeight, 6000f)
                : _contentHeight;

            var viewRect = new Rect(0f, 0f, bodyRect.width - 20f, viewHeight);

            Widgets.BeginScrollView(bodyRect, ref _scrollPosition, viewRect);

            // maxOneColumn is the fix for the vanishing list. Listing_Standard wraps into
            // a new column whenever content exceeds its rect height, and this rect is the
            // scroll viewport sized to *last frame's* measured height. When an event
            // expanded, the overflow wrapped into a second column off the right edge —
            // drawn correctly, visible to nobody. CurHeight then measured that short
            // second column, the viewport shrank to match, and the next frame wrapped
            // even earlier: the visible region collapsed a little more every frame.
            var listing = new Listing_Standard { maxOneColumn = true };
            listing.Begin(viewRect);

            if (_tab == Tab.Events) DrawEventsTab(listing);
            else DrawSettingsTab(listing);

            _contentHeight = listing.CurHeight + 24f;
            listing.End();

            Widgets.EndScrollView();
        }

        private static string DescribeLayoutMode(int mode)
        {
            switch (mode)
            {
                case 1: return "2 - no scroll view";
                case 2: return "3 - oversized viewport";
                default: return "1 - normal";
            }
        }

        private void DrawTabs(Rect rect)
        {
            var half = rect.width / 2f;

            if (Widgets.ButtonText(new Rect(rect.x, rect.y, half, rect.height),
                    _tab == Tab.Events ? "● Events" : "Events"))
            {
                _tab = Tab.Events;
            }

            if (Widgets.ButtonText(new Rect(rect.x + half, rect.y, half, rect.height),
                    _tab == Tab.Settings ? "● Settings" : "Settings"))
            {
                _tab = Tab.Settings;
            }
        }

        // ---------------------------------------------------------------- events

        private void DrawEventsTab(Listing_Standard listing)
        {
            UI.EventListEditor.Draw(listing);
        }

        // -------------------------------------------------------------- settings

        private void DrawSettingsTab(Listing_Standard listing)
        {
            Intro(listing,
                "These settings apply to every event. The events themselves — what they say "
                + "and do — live on the Events tab.");

            Heading(listing, "Master");

            listing.CheckboxLabeled("Enable custom events", ref _settings.enabled,
                "Master switch. When off, no events are scheduled and none fire.");

            Hint(listing, "Turning this off mid-event also stops any active CONTINUE modifier "
                          + "from colouring dialogue.");

            listing.Gap(8f);

            listing.Label($"Event frequency: {_settings.frequencyMultiplier:0.00}x");
            _settings.frequencyMultiplier = listing.Slider(_settings.frequencyMultiplier, 0f, 3f);
            Hint(listing, "Scales how often automatic events come up. 1x is as each event was "
                          + "written; 0x stops automatic events entirely, leaving only Test fire.");

            listing.Gap(8f);

            listing.Label($"Most events at once, colony-wide: {_settings.maxConcurrentGlobal}");
            _settings.maxConcurrentGlobal = Mathf.RoundToInt(listing.Slider(_settings.maxConcurrentGlobal, 1, 10));

            listing.Label($"Most events at once, per pawn: {_settings.maxConcurrentPerPawn}");
            _settings.maxConcurrentPerPawn = Mathf.RoundToInt(listing.Slider(_settings.maxConcurrentPerPawn, 1, 5));
            Hint(listing, "A pawn taking part in a shared event counts towards their own limit, "
                          + "so they will not also be the focus of another.");

            listing.GapLine();
            Heading(listing, "Who events can happen to");
            Hint(listing, "An event can narrow this further with its own target filters. "
                          + "Nothing outside these boxes is ever picked.");

            listing.CheckboxLabeled("Colonists", ref _settings.allowColonists);
            listing.CheckboxLabeled("Prisoners", ref _settings.allowPrisoners);
            listing.CheckboxLabeled("Slaves", ref _settings.allowSlaves);
            listing.CheckboxLabeled("Guests and visitors", ref _settings.allowGuests);
            listing.CheckboxLabeled("Animals", ref _settings.allowAnimals,
                "Animals only speak if RimTalk's Vocal Link implant is installed.");

            listing.GapLine();
            Heading(listing, "Prompt wrapper");
            Hint(listing, "Wraps every beat before it reaches RimTalk. The phase marker is what "
                          + "lets the model carry one beat on from the last, so keep {phase} and "
                          + "{text} unless you know what you are replacing them with.");
            Hint(listing, "Tokens:  {event}  {phase}  {phaseName}  {index}  {total}  {pawn}  "
                          + "{intensity}  {text}");

            _settings.promptWrapper = listing.TextEntry(_settings.promptWrapper, 3);
            if (listing.ButtonText("Reset wrapper to default", null, 0.4f))
            {
                _settings.promptWrapper = RTCESettings.DefaultPromptWrapper;
            }

            listing.GapLine();
            Heading(listing, "Troubleshooting");

            if (listing.ButtonText($"Page layout: {DescribeLayoutMode(_settings.uiLayoutMode)}", null, 0.6f))
            {
                _settings.uiLayoutMode = (_settings.uiLayoutMode + 1) % 3;
            }

            Hint(listing, "Only useful for diagnosing a display problem. If the event list looks "
                          + "empty on the Events tab, cycle this and see whether the rows appear — "
                          + "which one works says where the fault is. Leave it on 1 otherwise.");

            listing.Gap(6f);

            listing.CheckboxLabeled("Verbose logging", ref _settings.debugLogging,
                "Logs scheduling and beat delivery to the dev console. Noisy.");
            Hint(listing, "Worth turning on the first time you test an event: it reports every "
                          + "beat queuing, landing, or being held back, and why. It also adds a "
                          + "state readout to the Events tab.");

            listing.Gap(6f);
            Hint(listing, "If nothing is firing, the Diagnostics button on the Events tab checks "
                          + "everything at once — including whether RimTalk's API is configured.");
        }

        // ---------------------------------------------------------- text helpers

        private static void Heading(Listing_Standard listing, string text)
        {
            Text.Font = GameFont.Medium;
            listing.Label(text);
            Text.Font = GameFont.Small;
        }

        /// <summary>Small grey explanatory text under a control.</summary>
        private static void Hint(Listing_Standard listing, string text)
        {
            var previous = GUI.color;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.68f, 0.68f, 0.68f);
            listing.Label(text);
            GUI.color = previous;
            Text.Font = GameFont.Small;
        }

        private static void Intro(Listing_Standard listing, string text)
        {
            var previous = GUI.color;
            GUI.color = new Color(0.8f, 0.85f, 0.9f);
            listing.Label(text);
            GUI.color = previous;
            listing.GapLine();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            RTCELog.Debug("Settings written.");
        }
    }

    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            var harmony = new Harmony(RimTalkCustomEventsMod.ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            EventStore.Reload();
            Effects.HediffFactory.RegisterAll();
            RTCELog.Message("Loaded.");
        }
    }
}
