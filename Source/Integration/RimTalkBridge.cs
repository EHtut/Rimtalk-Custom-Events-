using System;
using System.Linq;
using RimTalk.Data;
using RimTalk.Source.Data;
using RimTalkCustomEvents.Util;
using RimWorld;
using Verse;
using RimTalkCache = RimTalk.Data.Cache;

namespace RimTalkCustomEvents.Integration
{
    public enum BeatStatus
    {
        /// <summary>Still sitting in the pawn's queue, waiting to be picked up.</summary>
        Pending,

        /// <summary>Handed to the LLM. See the note on Deliver about what this does and doesn't mean.</summary>
        Delivered,

        /// <summary>Expired or vanished without being used. Re-arm and try again.</summary>
        Lost
    }

    /// <summary>
    /// Every call into RimTalk goes through here, so an upstream API change breaks in one
    /// place rather than across the scheduler.
    /// </summary>
    public static class RimTalkBridge
    {
        /// <summary>
        /// True once RimTalk is tracking this pawn. Pawns are added to its cache lazily, so
        /// a freshly spawned pawn can be untracked for a while.
        /// </summary>
        public static bool IsTracked(Pawn pawn)
        {
            if (pawn == null) return false;
            try
            {
                return RimTalkCache.Get(pawn) != null;
            }
            catch (Exception ex)
            {
                RTCELog.WarnOnce($"RimTalk cache lookup failed: {ex.Message}", 0x5C0FF1);
                return false;
            }
        }

        /// <summary>Whether RimTalk would accept a new line from this pawn right now.</summary>
        public static bool CanSpeakNow(Pawn pawn)
        {
            if (pawn == null) return false;
            try
            {
                var state = RimTalkCache.Get(pawn);
                return state != null && state.CanGenerateTalk();
            }
            catch (Exception ex)
            {
                RTCELog.WarnOnce($"RimTalk readiness check failed: {ex.Message}", 0x5C0FF2);
                return false;
            }
        }

        /// <summary>
        /// Queues a beat as its own dedicated line, and returns the request so its progress
        /// can be followed.
        ///
        /// Beats go in as <c>TalkType.User</c>, not <c>TalkType.Event</c>. That is not a
        /// cosmetic choice — the two take completely different routes through RimTalk:
        ///
        /// - User requests are drained every second by their own loop in TickManagerPatch,
        ///   straight to GenerateTalk for this exact pawn.
        /// - Event requests only fire if RimTalk's own pawn selector happens to pick this
        ///   pawn, behind the talk-interval throttle and the "AI is busy" gate, and they
        ///   expire after roughly 20 real-time seconds.
        ///
        /// User requests also never expire, and skip the AllowMonologue check that would
        /// otherwise refuse a solo line. An event beat is an event, not idle chatter: it
        /// should land whatever else is going on. Ambient blending is what the Modifier part
        /// of CONTINUE is for, and that goes through a different channel entirely.
        ///
        /// RimTalk marks a request "spoken" when it is dispatched to the LLM, not when the
        /// pawn's line appears on screen.
        /// </summary>
        public static TalkRequest Deliver(Pawn pawn, string prompt, bool urgent = false)
        {
            if (pawn == null || string.IsNullOrEmpty(prompt)) return null;

            try
            {
                var state = RimTalkCache.Get(pawn);
                if (state == null) return null;

                // Queuing as User also clears the pawn's pending unspoken lines, which is
                // what makes a beat its own moment rather than a continuation of whatever
                // they were mid-way through saying.
                state.AddTalkRequest(prompt, null, TalkType.User);

                // User requests are queued with AddFirst, so ours is at the head. Verify
                // rather than assume, in case that ordering ever changes upstream.
                var head = state.TalkRequests.First?.Value;
                if (head != null && ReferenceEquals(head.RawPrompt, prompt))
                {
                    return head;
                }

                foreach (var request in state.TalkRequests)
                {
                    if (request.RawPrompt == prompt) return request;
                }

                RTCELog.Warning("Queued a beat but could not find it again in the pawn's request list.");
                return null;
            }
            catch (Exception ex)
            {
                RTCELog.Error($"Failed to queue a beat for {pawn.LabelShort}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Where a previously queued beat has got to.</summary>
        public static BeatStatus GetStatus(Pawn pawn, TalkRequest request)
        {
            if (pawn == null || request == null) return BeatStatus.Lost;

            try
            {
                var state = RimTalkCache.Get(pawn);
                if (state == null) return BeatStatus.Lost;

                foreach (var queued in state.TalkRequests)
                {
                    if (ReferenceEquals(queued, request)) return BeatStatus.Pending;
                }

                foreach (var past in TalkRequestPool.GetHistory())
                {
                    if (ReferenceEquals(past, request))
                    {
                        return past.Status == RequestStatus.Processed
                            ? BeatStatus.Delivered
                            : BeatStatus.Lost;
                    }
                }

                // Gone from both, which means RimTalk discarded it without recording an
                // outcome. Beats are queued as User requests, which never expire, so the
                // remaining cause is Cache.Refresh() evicting the whole PawnState when the
                // pawn stops being talk-eligible — anaesthetised for surgery, downed, or
                // despawned into a caravan — taking its queued requests with it.
                //
                // Treat that as lost, not delivered. Re-arming replays a line at worst;
                // assuming delivery would silently run the phase's effects and advance the
                // event for a beat nobody ever heard.
                return BeatStatus.Lost;
            }
            catch (Exception ex)
            {
                RTCELog.WarnOnce($"RimTalk status check failed: {ex.Message}", 0x5C0FF3);
                return BeatStatus.Lost;
            }
        }

        /// <summary>
        /// Takes the lines this pawn had queued but has not said yet, and clears them.
        ///
        /// RimTalk generates a conversation as several responses and displays them one at a
        /// time, so whatever is still in that list is precisely what the pawn was *about* to
        /// say. Reading it is better than estimating from elapsed time: it is exactly the
        /// thread the event is cutting off, and it can be handed to the model as context.
        ///
        /// Returns null when there was nothing pending.
        /// </summary>
        public static string PeekUnspokenLines(Pawn pawn)
        {
            if (pawn == null) return null;

            try
            {
                var state = RimTalkCache.Get(pawn);
                if (state == null || state.TalkResponses.Count == 0) return null;

                // Only the next couple matter; a whole queued conversation would swamp the
                // event's own prompt.
                var lines = state.TalkResponses
                    .Take(2)
                    .Select(r => r.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                return lines.Count == 0 ? null : string.Join(" ", lines.ToArray());
            }
            catch (Exception ex)
            {
                RTCELog.WarnOnce($"Could not read pending lines: {ex.Message}", 0x5C0FF4);
                return null;
            }
        }

        /// <summary>
        /// Discards the pawn's unspoken lines. Kept separate from reading them so a beat
        /// that fails to deliver does not destroy a conversation for nothing.
        /// </summary>
        public static void DropUnspokenLines(Pawn pawn)
        {
            if (pawn == null) return;

            try
            {
                RimTalkCache.Get(pawn)?.IgnoreAllTalkResponses();
            }
            catch (Exception ex)
            {
                RTCELog.WarnOnce($"Could not clear pending lines: {ex.Message}", 0x5C0FF5);
            }
        }

        /// <summary>
        /// Plain-English reason the pawn can't take a beat, for the dev overlay and logs.
        /// Returns null when the pawn is ready.
        /// </summary>
        public static string DescribeBlock(Pawn pawn)
        {
            if (pawn == null) return "pawn is gone";
            if (pawn.Dead) return "pawn is dead";
            if (!pawn.Spawned) return "pawn is not on the map";

            PawnState state;
            try
            {
                state = RimTalkCache.Get(pawn);
            }
            catch (Exception ex)
            {
                return $"RimTalk lookup failed: {ex.Message}";
            }

            if (state == null) return "RimTalk is not tracking this pawn yet";
            if (state.CanGenerateTalk()) return null;

            if (Find.CurrentMap == null || pawn.Map != Find.CurrentMap) return "pawn is not on the visible map";
            if (state.IsGeneratingTalk) return "RimTalk is already generating a line for this pawn";
            if (state.TalkResponses.Count > 0) return "pawn still has an unspoken line queued";
            if (!pawn.Awake()) return "pawn is asleep";
            if (pawn.Drafted) return "pawn is drafted";
            if (state.TalkInitiationWeight <= 0) return "pawn's talk weight is 0 (muted in RimTalk)";

            return "waiting for RimTalk's reply interval";
        }

        /// <summary>
        /// Describes RimTalk's active API configuration, or the reason there isn't one.
        ///
        /// Never returns the key itself — only whether one is present. An API key in a log
        /// or a screenshot is a leaked credential.
        /// </summary>
        public static string DescribeApiConfig(out bool usable)
        {
            usable = false;

            try
            {
                var config = RimTalk.Settings.Get()?.GetActiveConfig();
                if (config == null)
                {
                    return "No API configuration is active. Set one up in RimTalk's own mod "
                           + "settings — without it nothing can generate dialogue.";
                }

                var model = string.IsNullOrEmpty(config.CustomModelName)
                    ? config.SelectedModel
                    : config.CustomModelName;

                var local = config.Provider.ToString() == "Local";
                var hasKey = !string.IsNullOrWhiteSpace(config.ApiKey);

                usable = local || hasKey;

                var keyNote = local
                    ? "local provider, no key needed"
                    : hasKey ? "key is set" : "NO KEY SET";

                return $"{config.Provider} · model {model ?? "(none chosen)"} · {keyNote}";
            }
            catch (Exception ex)
            {
                return $"Could not read RimTalk's API settings: {ex.Message}";
            }
        }

        /// <summary>
        /// Sends a real one-line request through the same path a beat uses, so a test
        /// exercises the whole chain rather than just checking a key is present.
        /// Returns the pawn it was sent to, or null with the reason.
        /// </summary>
        public static Pawn SendTestLine(out string problem)
        {
            problem = null;

            var map = Find.CurrentMap;
            if (map == null)
            {
                problem = "No map open — open a save first.";
                return null;
            }

            Pawn target = null;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (!IsTracked(pawn)) continue;
                target = pawn;
                if (CanSpeakNow(pawn)) break;
            }

            if (target == null)
            {
                problem = "RimTalk is not tracking any colonist on this map.";
                return null;
            }

            var request = Deliver(target,
                "[RIMTALK CUSTOM EVENTS - CONNECTION TEST] Say one short line, in character, "
                + "remarking on the weather. This is a test of the dialogue connection.");

            if (request == null)
            {
                problem = $"RimTalk refused the request for {target.LabelShort}.";
                return null;
            }

            return target;
        }

        /// <summary>
        /// True when RimTalk would reject an ordinary solo line.
        ///
        /// This no longer blocks beats — they go in as User requests, which skip that check
        /// — but it still shapes the pawn's *other* dialogue, and therefore how often a
        /// CONTINUE modifier has anything to colour.
        /// </summary>
        public static bool MonologuesDisabled()
        {
            try
            {
                var settings = RimTalk.Settings.Get();
                return settings != null && !settings.AllowMonologue;
            }
            catch
            {
                return false;
            }
        }
    }
}
