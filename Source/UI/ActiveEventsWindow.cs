using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Integration;
using RimTalkCustomEvents.Scheduling;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimTalkCustomEvents.UI
{
    /// <summary>
    /// Live view of every running event: which pawn, which phase, when the next beat is
    /// due, and — when a beat is stuck — exactly what is blocking it.
    ///
    /// Beats only land when RimTalk will accept a line, so "nothing is happening" has
    /// many possible causes (asleep, drafted, mid-generation, reply interval, untracked
    /// pawn). Guessing between them from the log alone is slow; this names the reason.
    /// </summary>
    public class ActiveEventsWindow : Window
    {
        private Vector2 _scroll;

        public ActiveEventsWindow()
        {
            doCloseX = true;
            preventCameraMotion = false;
            draggable = true;
            resizeable = true;
        }

        public override Vector2 InitialSize => new Vector2(720f, 480f);

        public override void DoWindowContents(Rect inRect)
        {
            var y = inRect.y;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, inRect.width - 120f, 32f), "Active events");
            Text.Font = GameFont.Small;

            var component = CustomEventsGameComponent.Current;
            var instances = component?.ActiveInstances.ToList() ?? new List<EventInstance>();

            if (instances.Count > 0 && Widgets.ButtonText(new Rect(inRect.xMax - 110f, y, 110f, 28f), "Cancel all"))
            {
                component.CancelAll();
            }

            y += 38f;

            if (component == null)
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 60f),
                    "No game loaded — events run inside a save.");
                return;
            }

            if (instances.Count == 0)
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 60f),
                    "Nothing running right now.\n\nStart one from Mod Options → Events → Test fire, or from a pawn's dev-mode gizmo.");
                return;
            }

            var listRect = new Rect(inRect.x, y, inRect.width, inRect.height - y - 10f);
            const float rowHeight = 62f;
            var viewRect = new Rect(0f, 0f, listRect.width - 20f, instances.Count * rowHeight);

            Widgets.BeginScrollView(listRect, ref _scroll, viewRect);

            for (var i = 0; i < instances.Count; i++)
            {
                DrawRow(new Rect(0f, i * rowHeight, viewRect.width, rowHeight - 4f), instances[i]);
            }

            Widgets.EndScrollView();
        }

        private static void DrawRow(Rect rect, EventInstance instance)
        {
            Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.03f));

            var pawn = instance.Pawn;
            var def = instance.Def;
            var label = def?.Label ?? instance.EventDefName;

            var textRect = new Rect(rect.x + 8f, rect.y + 4f, rect.width - 190f, rect.height - 8f);

            Widgets.Label(new Rect(textRect.x, textRect.y, textRect.width, 22f),
                $"{pawn?.LabelShortCap ?? "(gone)"} — {label}");

            Text.Font = GameFont.Tiny;
            var block = pawn != null ? RimTalkBridge.DescribeBlock(pawn) : "pawn is gone";
            GUI.color = block == null ? new Color(0.6f, 0.85f, 0.6f) : new Color(0.9f, 0.8f, 0.5f);
            Widgets.Label(new Rect(textRect.x, textRect.y + 22f, textRect.width, 20f), instance.DescribeStatus());
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(textRect.x, textRect.y + 40f, textRect.width, 20f), DescribeProgress(instance));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            var buttonX = rect.xMax - 176f;

            if (pawn != null && pawn.Spawned &&
                Widgets.ButtonText(new Rect(buttonX, rect.y + 8f, 80f, 26f), "Go to"))
            {
                CameraJumper.TryJumpAndSelect(pawn);
            }

            if (Widgets.ButtonText(new Rect(buttonX + 88f, rect.y + 8f, 80f, 26f), "Cancel"))
            {
                instance.Cancel();
            }
        }

        private static string DescribeProgress(EventInstance instance)
        {
            var now = Find.TickManager.TicksGame;
            var elapsed = (now - instance.StartTick) / (float)GenDate.TicksPerHour;
            var total = (instance.EndTick - instance.StartTick) / (float)GenDate.TicksPerHour;
            var beats = instance.ContinueBeatTicks.Count;

            var beatText = beats == 0
                ? "no CONTINUE beats"
                : $"CONTINUE {Mathf.Min(instance.BeatIndex, beats)}/{beats} done";

            return $"{elapsed:0.#}h of {total:0.#}h   ·   {beatText}";
        }
    }

    /// <summary>Opens the window from a dev-mode toolbar action.</summary>
    public static class ActiveEventsLauncher
    {
        [DebugAction("RimTalk Custom Events", "Active events", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Open()
        {
            Find.WindowStack.Add(new ActiveEventsWindow());
        }
    }
}
