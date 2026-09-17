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

        private string _selectedDefName;

        /// <summary>
        /// The event the dropdown is pointing at. Falls back to the first one so the tab is
        /// never showing actions for nothing.
        /// </summary>
        private CustomEvent Selected
        {
            get
            {
                var byName = EventStore.Get(_selectedDefName);
                if (byName != null) return byName;
                return EventStore.All.OrderBy(e => e.Label).FirstOrDefault();
            }
        }

        private void DrawEventsTab(Listing_Standard listing)
        {
            // --- utility row ---
            var tools = listing.GetRect(30f);
            var quarter = tools.width / 4f;

            if (Widgets.ButtonText(new Rect(tools.x, tools.y, quarter - 4f, tools.height), "New event"))
            {
                Find.WindowStack.Add(new UI.EventEditorWindow(null));
            }

            if (Widgets.ButtonText(new Rect(tools.x + quarter, tools.y, quarter - 4f, tools.height), "Reload"))
            {
                EventStore.Reload();
                Effects.HediffFactory.RegisterAll();
                Messages.Message($"Reloaded {EventStore.Count} event(s).",
                    EventStore.LoadProblems.Count > 0 ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.TaskCompletion,
                    false);
            }

            if (Widgets.ButtonText(new Rect(tools.x + quarter * 2f, tools.y, quarter - 4f, tools.height), "Diagnostics"))
            {
                Find.WindowStack.Add(new UI.DiagnosticsWindow());
            }

            if (Widgets.ButtonText(new Rect(tools.x + quarter * 3f, tools.y, quarter, tools.height), "Active events"))
            {
                Find.WindowStack.Add(new UI.ActiveEventsWindow());
            }

            listing.Gap(8f);

            foreach (var problem in EventStore.LoadProblems)
            {
                var previous = GUI.color;
                GUI.color = Color.red;
                listing.Label($"{problem.FileName} — {problem.Problem}");
                GUI.color = previous;
            }

            if (EventStore.Count == 0)
            {
                listing.GapLine();
                var previous = GUI.color;
                GUI.color = Color.yellow;
                listing.Label("No events are loaded.");
                GUI.color = previous;
                listing.Label("Events are JSON files in:" + System.Environment.NewLine + EventStore.EventsFolder);
                listing.Label("Use New event above, or drop a .json file in that folder and hit Reload. "
                              + "If you expected Frost to be here, open Diagnostics — it reports what went wrong.");

                if (listing.ButtonText("Open folder", null, 0.4f)) Application.OpenURL(EventStore.EventsFolder);
                return;
            }

            listing.GapLine();

            // --- pick an event ---
            var selected = Selected;
            var pickRow = listing.GetRect(32f);
            Widgets.Label(new Rect(pickRow.x, pickRow.y + 4f, 60f, 24f), "Event:");

            if (Widgets.ButtonText(new Rect(pickRow.x + 64f, pickRow.y, pickRow.width - 64f, 30f),
                    selected == null ? "(choose)" : DropdownLabel(selected)))
            {
                var options = new List<FloatMenuOption>();
                foreach (var e in EventStore.All.OrderBy(x => x.Label))
                {
                    var captured = e;
                    options.Add(new FloatMenuOption(DropdownLabel(captured),
                        () => _selectedDefName = captured.DefName));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }

            if (selected == null) return;

            listing.Gap(6f);

            Text.Font = GameFont.Tiny;
            var grey = GUI.color;
            GUI.color = new Color(0.75f, 0.75f, 0.75f);
            listing.Label(Summarise(selected));
            if (!string.IsNullOrEmpty(selected.Description)) listing.Label(selected.Description);
            GUI.color = grey;
            Text.Font = GameFont.Small;

            listing.Gap(6f);

            // --- actions for it ---
            var actions = listing.GetRect(34f);
            var third = actions.width / 3f;

            if (Widgets.ButtonText(new Rect(actions.x, actions.y, third - 6f, 32f), "Edit"))
            {
                Find.WindowStack.Add(new UI.EventEditorWindow(selected));
            }

            if (Widgets.ButtonText(new Rect(actions.x + third, actions.y, third - 6f, 32f), "Test fire"))
            {
                TestFire(selected);
            }

            if (Widgets.ButtonText(new Rect(actions.x + third * 2f, actions.y, third - 6f, 32f), "Delete"))
            {
                ConfirmDelete(selected);
            }

            listing.Gap(8f);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.6f, 0.6f, 0.6f);
            listing.Label("Test fire needs a map open and a pawn matching this event's target filters. "
                          + "Diagnostics lists how many qualify.");
            GUI.color = grey;
            Text.Font = GameFont.Small;
        }

        private static string DropdownLabel(CustomEvent e)
        {
            return e.Enabled ? e.Label : e.Label + "   (disabled)";
        }

        private static string Summarise(CustomEvent e)
        {
            string beats;
            if (!e.HasContinueText)
            {
                beats = "BEGINNING → END";
            }
            else if (e.Phases.Continue.Mode == ContinueMode.Modifier)
            {
                // A modifier has no beats of its own; saying "CONTINUE ×3" would be a lie.
                beats = "BEGINNING → END, modifier throughout";
            }
            else
            {
                var kind = e.Phases.Continue.Mode == ContinueMode.Beat ? "beats" : "CONTINUE";
                beats = $"BEGINNING → {kind} ×{e.Timing.ContinueCount} → END";
            }

            var trigger = e.Trigger.Mode == TriggerMode.Daily
                ? $"daily at {e.Trigger.DailyHour}:00"
                : e.Trigger.Mode == TriggerMode.Manual
                    ? "manual only"
                    : $"~every {e.Trigger.MtbDays:0.#} days";

            var effects = e.AllEffects().Count(x => !x.HasOneOf && !x.Nothing);
            var effectText = effects == 1 ? "1 effect" : $"{effects} effects";

            return $"{beats}   ·   {e.Timing.DurationHours:0.#}h   ·   {trigger}   ·   {effectText}";
        }

        /// <summary>
        /// Deleting removes the file, so it asks first and says which file it will remove.
        /// </summary>
        private static void ConfirmDelete(CustomEvent customEvent)
        {
            var fileName = string.IsNullOrEmpty(customEvent.SourcePath)
                ? customEvent.DefName + ".json"
                : System.IO.Path.GetFileName(customEvent.SourcePath);

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                $"Delete \"{customEvent.Label}\"?\n\nThis removes {fileName} from disk. It can't be undone.",
                () =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(customEvent.SourcePath) && System.IO.File.Exists(customEvent.SourcePath))
                        {
                            System.IO.File.Delete(customEvent.SourcePath);
                        }

                        EventStore.Reload();
                        Messages.Message($"Deleted \"{customEvent.Label}\".", MessageTypeDefOf.TaskCompletion, false);
                    }
                    catch (System.Exception ex)
                    {
                        Messages.Message($"Couldn't delete: {ex.Message}", MessageTypeDefOf.RejectInput, false);
                    }
                },
                destructive: true));
        }

        private static void TestFire(CustomEvent customEvent)
        {
            var component = CustomEventsGameComponent.Current;
            if (component == null)
            {
                Messages.Message("Load a save first — events run inside a game.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                Messages.Message("Open a map first.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var pawn = PawnSelector.TryPick(customEvent, map);
            if (pawn == null)
            {
                Messages.Message(
                    $"Nobody on this map matches \"{customEvent.Label}\"'s target filters.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            // Bypass limits: a test that silently does nothing is worse than one that
            // overlaps an existing event.
            if (component.TryStart(customEvent, pawn, out var reason, bypassLimits: true))
            {
                Messages.Message($"Started \"{customEvent.Label}\" on {pawn.LabelShort}.",
                    pawn, MessageTypeDefOf.NeutralEvent, false);
            }
            else
            {
                Messages.Message($"Couldn't start \"{customEvent.Label}\": {reason}",
                    MessageTypeDefOf.RejectInput, false);
            }
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
