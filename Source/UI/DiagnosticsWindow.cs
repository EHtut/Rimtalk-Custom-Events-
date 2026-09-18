using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Integration;
using RimTalkCustomEvents.Scheduling;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimTalkCustomEvents.UI
{
    /// <summary>
    /// One-shot report answering "why is nothing happening?".
    ///
    /// Silence has many causes — the master toggle, no eligible pawns, RimTalk not
    /// tracking anyone, monologues disabled, a missing def, a cooldown — and most of
    /// them look identical from the outside. Checking them all in one pass is much
    /// cheaper than restarting the game to try another theory.
    /// </summary>
    public class DiagnosticsWindow : Window
    {
        private Vector2 _scroll;
        private readonly string _report;

        public DiagnosticsWindow()
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;
            _report = BuildReport();
        }

        public override Vector2 InitialSize => new Vector2(760f, 620f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 130f, 32f), "Diagnostics");
            Text.Font = GameFont.Small;

            if (Widgets.ButtonText(new Rect(inRect.xMax - 120f, inRect.y, 120f, 28f), "Copy to log"))
            {
                Log.Message(_report);
                Messages.Message("Written to the dev console.", MessageTypeDefOf.TaskCompletion, false);
            }

            if (Widgets.ButtonText(new Rect(inRect.xMax - 300f, inRect.y, 174f, 28f), "Test RimTalk connection"))
            {
                var pawn = RimTalkBridge.SendTestLine(out var problem);

                if (pawn == null)
                {
                    Messages.Message("Test failed: " + problem, MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    Messages.Message(
                        $"Test line queued for {pawn.LabelShortCap}. If the connection works they should "
                        + "speak within a few seconds — watch their speech bubble.",
                        pawn, MessageTypeDefOf.TaskCompletion, false);
                }
            }

            var bodyRect = new Rect(inRect.x, inRect.y + 38f, inRect.width, inRect.height - 44f);
            var height = Text.CalcHeight(_report, bodyRect.width - 20f);
            var viewRect = new Rect(0f, 0f, bodyRect.width - 20f, height);

            Widgets.BeginScrollView(bodyRect, ref _scroll, viewRect);
            Widgets.Label(viewRect, _report);
            Widgets.EndScrollView();
        }

        private static string BuildReport()
        {
            var sb = new StringBuilder();
            var settings = RimTalkCustomEventsMod.Settings;

            sb.AppendLine("— Mod —");
            if (settings == null)
            {
                sb.AppendLine("  PROBLEM: settings not loaded. The mod did not initialise.");
                return sb.ToString();
            }

            sb.AppendLine(settings.enabled
                ? "  Enabled."
                : "  PROBLEM: custom events are switched OFF in mod settings. Nothing will fire.");

            if (settings.frequencyMultiplier <= 0f)
                sb.AppendLine("  PROBLEM: event frequency is 0x. Automatic triggers are disabled.");
            else
                sb.AppendLine($"  Frequency {settings.frequencyMultiplier:0.00}x, "
                              + $"max {settings.maxConcurrentGlobal} at once ({settings.maxConcurrentPerPawn} per pawn).");

            var allowed = new List<string>();
            if (settings.allowColonists) allowed.Add("colonists");
            if (settings.allowPrisoners) allowed.Add("prisoners");
            if (settings.allowSlaves) allowed.Add("slaves");
            if (settings.allowGuests) allowed.Add("guests");
            if (settings.allowAnimals) allowed.Add("animals");
            sb.AppendLine(allowed.Count == 0
                ? "  PROBLEM: no pawn categories are enabled. Nothing can be targeted."
                : "  Can target: " + string.Join(", ", allowed.ToArray()));

            sb.AppendLine();
            sb.AppendLine("— RimTalk —");

            var apiDescription = RimTalkBridge.DescribeApiConfig(out var apiUsable);
            sb.AppendLine(apiUsable ? "  API: " + apiDescription : "  PROBLEM: " + apiDescription);

            if (RimTalkBridge.MonologuesDisabled())
            {
                sb.AppendLine("  Note: RimTalk has \"allow monologue\" switched OFF.");
                sb.AppendLine("    Beats are unaffected — they are queued as their own dedicated lines.");
                sb.AppendLine("    But a lone pawn will rarely say anything else, so a CONTINUE modifier");
                sb.AppendLine("    will have little ordinary dialogue to colour.");
            }
            else
            {
                sb.AppendLine("  Monologues allowed.");
            }

            var map = Find.CurrentMap;
            if (map == null)
            {
                sb.AppendLine("  No map open, so pawn tracking can't be checked.");
            }
            else
            {
                var spawned = map.mapPawns.AllPawnsSpawned.ToList();
                var tracked = spawned.Count(RimTalkBridge.IsTracked);
                sb.AppendLine($"  Tracking {tracked} of {spawned.Count} pawns on this map.");
                if (tracked == 0)
                    sb.AppendLine("    PROBLEM: RimTalk is tracking nobody. Check its API key and settings.");

                var ready = spawned.Count(p => RimTalkBridge.CanSpeakNow(p));
                sb.AppendLine($"  {ready} pawn(s) could take a beat right now.");
            }

            sb.AppendLine();
            sb.AppendLine("— Events —");
            sb.AppendLine($"  Folder: {EventStore.EventsFolder}");
            sb.AppendLine($"  {EventStore.Count} loaded, {EventStore.AllEnabled.Count()} enabled.");

            if (EventStore.Count == 0)
                sb.AppendLine("    PROBLEM: no events loaded. Check the folder above.");

            foreach (var problem in EventStore.LoadProblems)
                sb.AppendLine($"    PROBLEM: {problem.FileName} — {problem.Problem}");

            sb.AppendLine();

            foreach (var e in EventStore.All.OrderBy(e => e.Label))
            {
                sb.AppendLine($"  [{(e.Enabled ? "on" : "off")}] {e.Label} ({e.DefName})");
                sb.AppendLine($"       trigger: {DescribeTrigger(e)}");

                foreach (var missing in DefFinder.ValidateReferences(e))
                    sb.AppendLine($"       PROBLEM: {missing}");

                if (map != null && e.Enabled)
                {
                    var eligible = map.mapPawns.AllPawnsSpawned.Count(p => WouldTarget(e, p));
                    sb.AppendLine($"       {eligible} eligible pawn(s) on this map"
                                  + (eligible == 0 ? "   <- it can never fire here" : ""));
                }
            }

            sb.AppendLine();
            sb.AppendLine("— Running now —");
            var component = CustomEventsGameComponent.Current;

            if (component == null)
            {
                sb.AppendLine("  No game loaded.");
            }
            else
            {
                var active = component.ActiveInstances.ToList();
                if (active.Count == 0)
                {
                    sb.AppendLine("  Nothing running.");
                }
                else
                {
                    foreach (var instance in active)
                    {
                        sb.AppendLine($"  {instance.Pawn?.LabelShortCap ?? "(gone)"}: {instance.DescribeStatus()}");
                    }
                }
            }

            return sb.ToString();
        }

        private static string DescribeTrigger(CustomEvent e)
        {
            switch (e.Trigger.Mode)
            {
                case TriggerMode.Daily:
                    return $"daily at {e.Trigger.DailyHour}:00, {e.Trigger.DailyChance:P0} chance";
                case TriggerMode.Occasionally:
                    return $"roughly every {e.Trigger.MtbDays:0.#} days";
                default:
                    return "manual only — it will never fire on its own";
            }
        }

        /// <summary>
        /// Mirrors PawnSelector's eligibility by asking it directly, so the count here can't
        /// drift from what actually gets picked.
        /// </summary>
        private static bool WouldTarget(CustomEvent e, Pawn pawn)
        {
            return PawnSelector.IsEligible(e, pawn);
        }
    }

    public static class DiagnosticsLauncher
    {
        [LudeonTK.DebugAction("RimTalk Custom Events", "Diagnostics",
            allowedGameStates = LudeonTK.AllowedGameStates.PlayingOnMap)]
        private static void Open()
        {
            Find.WindowStack.Add(new DiagnosticsWindow());
        }
    }
}
