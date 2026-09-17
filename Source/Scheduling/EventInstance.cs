using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimTalk.Data;
using RimWorld;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Effects;
using RimTalkCustomEvents.Integration;
using RimTalkCustomEvents.Util;
using Verse;

namespace RimTalkCustomEvents.Scheduling
{
    public enum EventPhaseState
    {
        Beginning,
        Continuing,
        Ending,
        Complete,
        Aborted
    }

    /// <summary>
    /// One running event on one pawn.
    ///
    /// Beats are pulled, not pushed: a beat becomes due at a scheduled tick but is only
    /// handed to RimTalk once the pawn can actually take it. A queued RimTalk request
    /// expires after roughly 20 real-time seconds, so firing on a timer alone would
    /// silently drop beats whenever the pawn was asleep, drafted or mid-generation.
    /// </summary>
    public class EventInstance : IExposable
    {
        private const int TicksPerHour = GenDate.TicksPerHour;

        public string EventDefName;

        /// <summary>Whose event this primarily is. BEGINNING and END are always theirs.</summary>
        public Pawn Pawn;

        /// <summary>
        /// The other pawns taking part, for a shared event. Empty for the usual single-pawn
        /// case. Effects land on everyone; CONTINUE beats rotate between them so the event
        /// reads as something they are going through together.
        /// </summary>
        public List<Pawn> Participants = new List<Pawn>();
        public EventPhaseState Phase = EventPhaseState.Beginning;

        public int StartTick;
        public int EndTick;

        /// <summary>Absolute ticks at which each CONTINUE beat becomes due.</summary>
        public List<int> ContinueBeatTicks = new List<int>();

        /// <summary>Which CONTINUE beat is next.</summary>
        public int BeatIndex;

        /// <summary>When the current beat first became due, for the blocked-beat timeout.</summary>
        public int BeatDueSinceTick = -1;

        /// <summary>Last tick a beat was actually handed over, for minBeatGapHours.</summary>
        public int LastDeliveredTick = -1;

        /// <summary>Why the instance stopped, shown in the dev overlay.</summary>
        public string AbortReason;

        // Transient: a TalkRequest reference can't be saved, so an in-flight beat is
        // re-armed after a load rather than followed.
        private TalkRequest _inFlight;
        private int _lastBlockLogTick = -1;

        public EventInstance()
        {
        }

        public EventInstance(CustomEvent def, Pawn pawn, List<Pawn> participants = null)
        {
            EventDefName = def.DefName;
            Pawn = pawn;
            if (participants != null) Participants.AddRange(participants.Where(p => p != null && p != pawn));
            StartTick = Find.TickManager.TicksGame;
            Phase = EventPhaseState.Beginning;
            BeatDueSinceTick = StartTick;
            ScheduleBeats(def);
        }

        public bool IsFinished => Phase == EventPhaseState.Complete || Phase == EventPhaseState.Aborted;

        public bool IsShared => Participants.Count > 0;

        /// <summary>Primary first, then the rest. Effects apply to all of these.</summary>
        public IEnumerable<Pawn> AllPawns
        {
            get
            {
                if (Pawn != null) yield return Pawn;
                foreach (var participant in Participants)
                {
                    if (participant != null && !participant.Dead) yield return participant;
                }
            }
        }

        /// <summary>
        /// Who speaks the beat about to fire. BEGINNING and END stay with the primary so the
        /// event opens and closes in one voice; CONTINUE beats rotate through the group.
        /// </summary>
        public Pawn CurrentSpeaker()
        {
            if (!IsShared || Phase != EventPhaseState.Continuing) return Pawn;

            var everyone = AllPawns.ToList();
            if (everyone.Count == 0) return Pawn;
            return everyone[BeatIndex % everyone.Count];
        }

        public CustomEvent Def => EventStore.Get(EventDefName);

        /// <summary>Lays out CONTINUE beats across the duration, with jitter, and sets END.</summary>
        private void ScheduleBeats(CustomEvent def)
        {
            var durationTicks = Math.Max(TicksPerHour, (int)(def.Timing.DurationHours * TicksPerHour));
            EndTick = StartTick + durationTicks;

            ContinueBeatTicks.Clear();

            var count = def.Timing.ContinueCount;
            if (count <= 0 || !def.ContinueUsesBeats) return;

            if (def.Timing.Spacing == ContinueSpacing.Random)
            {
                // Drawn freely across the window, so the escalation doesn't feel metronomic.
                for (var i = 0; i < count; i++)
                {
                    ContinueBeatTicks.Add(StartTick + Rand.Range(1, Math.Max(2, durationTicks - 1)));
                }
            }
            else
            {
                // Evenly spaced strictly between start and end: beat i sits at i/(count+1).
                var slot = durationTicks / (float)(count + 1);
                var jitter = Mathf01(def.Timing.ContinueJitter);

                for (var i = 0; i < count; i++)
                {
                    var centre = slot * (i + 1);
                    var wobble = jitter > 0f
                        ? Rand.Range(-slot * jitter * 0.5f, slot * jitter * 0.5f)
                        : 0f;

                    var tick = StartTick + (int)(centre + wobble);
                    tick = Math.Max(StartTick + 1, Math.Min(tick, EndTick - 1));
                    ContinueBeatTicks.Add(tick);
                }
            }

            ContinueBeatTicks.Sort();
        }

        private static float Mathf01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>Called on a slow tick. Advances the state machine at most one step.</summary>
        public void Tick()
        {
            if (IsFinished) return;

            var def = Def;
            if (def == null)
            {
                Abort("its event file no longer exists");
                return;
            }

            if (Pawn == null || Pawn.Dead || Pawn.Destroyed)
            {
                Abort("the pawn is gone");
                return;
            }

            var now = Find.TickManager.TicksGame;

            // A stuck instance shouldn't outlive its event by more than its own duration.
            if (now > EndTick + (EndTick - StartTick))
            {
                Abort("it ran far past its duration without finishing");
                return;
            }

            if (_inFlight != null)
            {
                FollowInFlightBeat(def, now);
                return;
            }

            if (!IsBeatDue(now)) return;

            // Respect the minimum gap so a long block doesn't dump several beats at once.
            var minGap = (int)(def.Timing.MinBeatGapHours * TicksPerHour);
            if (LastDeliveredTick >= 0 && now - LastDeliveredTick < minGap) return;

            // Start the blocked-beat clock only once the beat is genuinely waiting on the
            // pawn. Stamping it when the beat merely became due would charge the min-gap
            // wait against beatTimeoutHours, and skip beats that were never blocked.
            if (BeatDueSinceTick < 0) BeatDueSinceTick = now;

            if (!RimTalkBridge.CanSpeakNow(CurrentSpeaker()))
            {
                HandleBlockedBeat(def, now);
                return;
            }

            TryDeliverCurrentBeat(def, now);
        }

        private bool IsBeatDue(int now)
        {
            switch (Phase)
            {
                case EventPhaseState.Beginning:
                    return true;

                case EventPhaseState.Continuing:
                    if (now >= EndTick) return true; // fall through to END below
                    return BeatIndex < ContinueBeatTicks.Count && now >= ContinueBeatTicks[BeatIndex];

                case EventPhaseState.Ending:
                    return now >= EndTick;

                default:
                    return false;
            }
        }

        private void TryDeliverCurrentBeat(CustomEvent def, int now)
        {
            // Out of CONTINUE beats, or the duration ran out: move to END.
            if (Phase == EventPhaseState.Continuing &&
                (BeatIndex >= ContinueBeatTicks.Count || now >= EndTick))
            {
                Phase = EventPhaseState.Ending;
                BeatDueSinceTick = now;
                if (now < EndTick) return; // END isn't due yet
            }

            var text = CurrentPhaseSpec(def)?.Text;
            if (string.IsNullOrEmpty(text))
            {
                AdvanceAfterBeat(def, now);
                return;
            }

            var speaker = CurrentSpeaker();

            // A priority event cuts in. Read what they were about to say, so the opening
            // line can pick up from where the conversation broke off rather than arriving
            // out of nowhere. Only *read* here — see below.
            string interrupted = null;
            var urgent = def.Priority && Phase == EventPhaseState.Beginning;
            if (urgent) interrupted = RimTalkBridge.PeekUnspokenLines(speaker);

            var prompt = BuildPrompt(def, text);

            if (interrupted != null)
            {
                prompt += Environment.NewLine
                          + "(They were in the middle of saying: \"" + interrupted + "\" — this cuts them "
                          + "off mid-thought. Carry on from that moment.)";
            }

            var request = RimTalkBridge.Deliver(speaker, prompt, urgent);

            if (request == null)
            {
                // Nothing was discarded, so the pawn keeps whatever they were saying and
                // the retry can capture it again.
                HandleBlockedBeat(def, now);
                return;
            }

            // Only now that the beat is genuinely queued do we throw away what they were
            // going to say instead.
            if (urgent) RimTalkBridge.DropUnspokenLines(speaker);

            _inFlight = request;
            RTCELog.Debug($"{Pawn.LabelShort}: queued {PhaseLabel(def)} beat of \"{def.Label}\"");
        }

        private void FollowInFlightBeat(CustomEvent def, int now)
        {
            var status = RimTalkBridge.GetStatus(CurrentSpeaker(), _inFlight);

            switch (status)
            {
                case BeatStatus.Pending:
                    // RimTalk drops a request after ~20 real seconds; GetStatus will report
                    // Lost once that happens, so there's nothing to do but wait.
                    return;

                case BeatStatus.Delivered:
                    _inFlight = null;
                    LastDeliveredTick = now;
                    RTCELog.Debug($"{Pawn.LabelShort}: delivered {PhaseLabel(def)} beat of \"{def.Label}\"");

                    // Effects run on delivery, while Phase still points at the beat that
                    // just landed — AdvanceAfterBeat moves it on.
                    var effects = CurrentPhaseSpec(def)?.Effects;
                    var intensity = CurrentIntensity(def);
                    foreach (var affected in AllPawns.ToList())
                    {
                        EffectRunner.RunAll(effects, affected, def.Label ?? def.DefName, intensity);
                    }

                    AdvanceAfterBeat(def, now);
                    return;

                case BeatStatus.Lost:
                    _inFlight = null;
                    HandleBlockedBeat(def, now);
                    return;
            }
        }

        private void HandleBlockedBeat(CustomEvent def, int now)
        {
            if (BeatDueSinceTick < 0) BeatDueSinceTick = now;

            var waited = now - BeatDueSinceTick;
            var timeout = (int)(def.Timing.BeatTimeoutHours * TicksPerHour);

            // Log the reason at most once an hour so a long block doesn't flood the console.
            if (_lastBlockLogTick < 0 || now - _lastBlockLogTick > TicksPerHour)
            {
                _lastBlockLogTick = now;
                var reason = RimTalkBridge.DescribeBlock(Pawn) ?? "RimTalk did not accept the request";
                RTCELog.Debug($"{Pawn.LabelShort}: {PhaseLabel(def)} beat of \"{def.Label}\" is waiting — {reason}");
            }

            if (waited < timeout) return;

            switch (def.Timing.OnBlocked)
            {
                case BlockedPolicy.Skip:
                    RTCELog.Debug($"{Pawn.LabelShort}: skipping a blocked {PhaseLabel(def)} beat of \"{def.Label}\"");
                    BeatDueSinceTick = now;
                    AdvanceAfterBeat(def, now);
                    return;

                case BlockedPolicy.Abort:
                    Abort($"the {PhaseLabel(def)} beat stayed blocked for {def.Timing.BeatTimeoutHours:0.#}h");
                    return;

                case BlockedPolicy.Retry:
                default:
                    // Reset the window and keep trying. The overall staleness guard in Tick
                    // still ends the instance eventually.
                    BeatDueSinceTick = now;
                    return;
            }
        }

        private void AdvanceAfterBeat(CustomEvent def, int now)
        {
            BeatDueSinceTick = -1;
            _lastBlockLogTick = -1;

            switch (Phase)
            {
                case EventPhaseState.Beginning:
                    Phase = ContinueBeatTicks.Count > 0 ? EventPhaseState.Continuing : EventPhaseState.Ending;
                    BeatIndex = 0;
                    break;

                case EventPhaseState.Continuing:
                    BeatIndex++;
                    if (BeatIndex >= ContinueBeatTicks.Count) Phase = EventPhaseState.Ending;
                    break;

                case EventPhaseState.Ending:
                    Phase = EventPhaseState.Complete;
                    RTCELog.Debug($"{Pawn.LabelShort}: finished \"{def.Label}\"");
                    break;
            }
        }

        private void Abort(string reason)
        {
            Phase = EventPhaseState.Aborted;
            AbortReason = reason;
            _inFlight = null;
            var who = Pawn?.LabelShort ?? "a pawn";
            RTCELog.Debug($"{who}: aborted \"{EventDefName}\" — {reason}");
        }

        /// <summary>Ends the instance early at the player's request.</summary>
        public void Cancel()
        {
            Abort("cancelled");
        }

        /// <summary>
        /// Intensity of the beat about to fire. Only a Beat-mode CONTINUE ramps; everything
        /// else sits at 1 so it behaves exactly as it did before intensity existed.
        /// </summary>
        private float CurrentIntensity(CustomEvent def)
        {
            return Phase == EventPhaseState.Continuing
                ? def.Phases.Continue.Beat.IntensityAt(BeatIndex, ContinueBeatTicks.Count)
                : 1f;
        }

        /// <summary>
        /// The Modifier text this instance is contributing right now, or null. Only applies
        /// once the event is past BEGINNING — the modifier is the *continuation*, so it
        /// shouldn't colour dialogue before the event has actually started.
        /// </summary>
        public string ActiveModifierText()
        {
            if (IsFinished) return null;
            if (Phase == EventPhaseState.Beginning) return null;

            var def = Def;
            return def != null && def.Phases.Continue.HasModifier
                ? def.Phases.Continue.ModifierText
                : null;
        }

        private PhaseSpec CurrentPhaseSpec(CustomEvent def)
        {
            switch (Phase)
            {
                case EventPhaseState.Beginning: return def.Phases.Beginning;
                case EventPhaseState.Continuing: return def.Phases.Continue.Beat;
                case EventPhaseState.Ending: return def.Phases.End;
                default: return null;
            }
        }

        private string PhaseName()
        {
            switch (Phase)
            {
                case EventPhaseState.Beginning: return "BEGINNING";
                case EventPhaseState.Continuing: return "CONTINUE";
                case EventPhaseState.Ending: return "END";
                default: return Phase.ToString().ToUpperInvariant();
            }
        }

        private string PhaseLabel(CustomEvent def)
        {
            return PromptBuilder.PhaseLabel(PhaseName(), BeatIndex + 1, ContinueBeatTicks.Count);
        }

        /// <summary>
        /// Applies the settings wrapper. The phase marker gives the model the continuity cue
        /// it needs — RimTalk already supplies recent talk history, so CONTINUE and END land
        /// with the earlier beats in context.
        /// </summary>
        private string BuildPrompt(CustomEvent def, string text)
        {
            // A shared event needs the model to know the others are in it too, or each beat
            // reads as an unrelated solo moment.
            if (IsShared)
            {
                var others = AllPawns.Where(p => p != CurrentSpeaker()).Select(p => p.LabelShort).ToList();
                if (others.Count > 0)
                {
                    text = $"{text} (Also affected, going through the same thing: "
                           + string.Join(", ", others.ToArray()) + ".)";
                }
            }

            return PromptBuilder.Build(
                RimTalkCustomEventsMod.Settings?.promptWrapper,
                def.Label ?? def.DefName,
                PhaseLabel(def),
                PhaseName(),
                BeatIndex + 1,
                ContinueBeatTicks.Count,
                CurrentSpeaker()?.LabelShort,
                text,
                CurrentIntensity(def));
        }

        /// <summary>Short line for the dev overlay.</summary>
        public string DescribeStatus()
        {
            var def = Def;
            var label = def?.Label ?? EventDefName;

            if (Phase == EventPhaseState.Aborted) return $"{label}: aborted — {AbortReason}";
            if (Phase == EventPhaseState.Complete) return $"{label}: complete";

            var phase = def != null ? PhaseLabel(def) : Phase.ToString();
            if (_inFlight != null) return $"{label}: {phase} — queued with RimTalk";

            var block = RimTalkBridge.DescribeBlock(Pawn);
            if (block != null) return $"{label}: {phase} — waiting ({block})";

            var now = Find.TickManager.TicksGame;
            var dueTick = Phase == EventPhaseState.Continuing && BeatIndex < ContinueBeatTicks.Count
                ? ContinueBeatTicks[BeatIndex]
                : EndTick;
            var hours = Math.Max(0, (dueTick - now)) / (float)TicksPerHour;
            return $"{label}: {phase} — next beat in {hours:0.#}h";
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref EventDefName, "eventDefName");
            Scribe_References.Look(ref Pawn, "pawn");
            Scribe_Collections.Look(ref Participants, "participants", LookMode.Reference);
            Scribe_Values.Look(ref Phase, "phase", EventPhaseState.Beginning);
            Scribe_Values.Look(ref StartTick, "startTick");
            Scribe_Values.Look(ref EndTick, "endTick");
            Scribe_Collections.Look(ref ContinueBeatTicks, "continueBeatTicks", LookMode.Value);
            Scribe_Values.Look(ref BeatIndex, "beatIndex");
            Scribe_Values.Look(ref BeatDueSinceTick, "beatDueSinceTick", -1);
            Scribe_Values.Look(ref LastDeliveredTick, "lastDeliveredTick", -1);
            Scribe_Values.Look(ref AbortReason, "abortReason");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                ContinueBeatTicks = ContinueBeatTicks ?? new List<int>();
                Participants = Participants ?? new List<Pawn>();
                Participants.RemoveAll(p => p == null);
                // An in-flight request can't be saved; re-arm so the beat is retried.
                _inFlight = null;
                BeatDueSinceTick = -1;
            }
        }
    }
}
