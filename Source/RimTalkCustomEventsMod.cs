using System.Collections.Generic;
using System.Linq;
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
            var viewRect = new Rect(0f, 0f, bodyRect.width - 20f, _contentHeight);

            Widgets.BeginScrollView(bodyRect, ref _scrollPosition, viewRect);

            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            if (_tab == Tab.Events) DrawEventsTab(listing);
            else DrawSettingsTab(listing);

            _contentHeight = listing.CurHeight + 24f;
            listing.End();

            Widgets.EndScrollView();
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
            listing.CheckboxLabeled("Enable custom events", ref _settings.enabled,
                "Master switch. When off, no events are scheduled and none fire.");

            listing.Gap();

            listing.Label($"Event frequency: {_settings.frequencyMultiplier:0.00}x");
            _settings.frequencyMultiplier = listing.Slider(_settings.frequencyMultiplier, 0f, 3f);

            listing.Label($"Max events running at once (colony): {_settings.maxConcurrentGlobal}");
            _settings.maxConcurrentGlobal = Mathf.RoundToInt(listing.Slider(_settings.maxConcurrentGlobal, 1, 10));

            listing.Label($"Max events running at once (per pawn): {_settings.maxConcurrentPerPawn}");
            _settings.maxConcurrentPerPawn = Mathf.RoundToInt(listing.Slider(_settings.maxConcurrentPerPawn, 1, 5));

            listing.GapLine();
            listing.Label("Who can events happen to?");
            listing.CheckboxLabeled("Colonists", ref _settings.allowColonists);
            listing.CheckboxLabeled("Prisoners", ref _settings.allowPrisoners);
            listing.CheckboxLabeled("Slaves", ref _settings.allowSlaves);
            listing.CheckboxLabeled("Guests and visitors", ref _settings.allowGuests);
            listing.CheckboxLabeled("Animals", ref _settings.allowAnimals,
                "Animals only speak if RimTalk's Vocal Link implant is installed.");

            listing.GapLine();
            listing.Label("Prompt wrapper — tokens: {event} {phase} {index} {total} {pawn} {text}");
            _settings.promptWrapper = listing.TextEntry(_settings.promptWrapper, 3);
            if (listing.ButtonText("Reset wrapper to default", null, 0.4f))
            {
                _settings.promptWrapper = RTCESettings.DefaultPromptWrapper;
            }

            listing.GapLine();
            listing.CheckboxLabeled("Verbose logging", ref _settings.debugLogging,
                "Logs scheduling and beat delivery to the dev console. Noisy.");
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
