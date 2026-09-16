using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Integration;
using RimTalkCustomEvents.Scheduling;
using RimWorld;
using Verse;

namespace RimTalkCustomEvents.UI
{
    /// <summary>
    /// Adds a dev-mode gizmo for firing and cancelling events by hand. This is the only way
    /// to start an event until the automatic trigger modes are implemented.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class PawnGizmoPatch
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Pawn __instance)
        {
            foreach (var gizmo in values)
            {
                yield return gizmo;
            }

            if (!Prefs.DevMode) yield break;
            if (__instance == null || !__instance.Spawned || __instance.Dead) yield break;
            if (__instance.Faction == null || !__instance.Faction.IsPlayer) yield break;

            yield return new Command_Action
            {
                defaultLabel = "Custom event",
                defaultDesc = "Dev only. Start or cancel a RimTalk custom event on this pawn.",
                icon = TexCommand.DesirePower,
                action = () => ShowMenuFor(__instance)
            };
        }

        private static void ShowMenuFor(Pawn pawn)
        {
            var options = new List<FloatMenuOption>();
            var component = CustomEventsGameComponent.Current;

            if (component == null)
            {
                Messages.Message("RimTalk Custom Events isn't running on this save.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var block = RimTalkBridge.DescribeBlock(pawn);
            if (block != null)
            {
                options.Add(new FloatMenuOption($"(RimTalk status: {block})", null));
            }

            var running = component.ActiveInstances.Where(i => i.Pawn == pawn).ToList();
            foreach (var instance in running)
            {
                var captured = instance;
                options.Add(new FloatMenuOption($"Cancel — {captured.DescribeStatus()}", () => captured.Cancel()));
            }

            if (EventStore.Count == 0)
            {
                options.Add(new FloatMenuOption("(no event files loaded)", null));
            }

            foreach (var def in EventStore.AllEnabled.OrderBy(e => e.Label))
            {
                var captured = def;
                options.Add(new FloatMenuOption($"Start \"{captured.Label}\"", () =>
                {
                    if (component.TryStart(captured, pawn, out var reason, bypassLimits: true))
                    {
                        Messages.Message($"Started \"{captured.Label}\" on {pawn.LabelShort}.",
                            pawn, MessageTypeDefOf.NeutralEvent, false);
                    }
                    else
                    {
                        Messages.Message($"Couldn't start \"{captured.Label}\": {reason}",
                            MessageTypeDefOf.RejectInput, false);
                    }
                }));
            }

            options.Add(new FloatMenuOption("Show active events…", () =>
                Find.WindowStack.Add(new ActiveEventsWindow())));

            options.Add(new FloatMenuOption("Reload event files from disk", () =>
            {
                EventStore.Reload();
                Effects.HediffFactory.RegisterAll();
                var problems = EventStore.LoadProblems.Count;
                var suffix = problems > 0 ? $" ({problems} file(s) had problems — see the log)" : "";
                Messages.Message($"Reloaded {EventStore.Count} event(s).{suffix}",
                    problems > 0 ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.TaskCompletion, false);
            }));

            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
