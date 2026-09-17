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
        /// Queues a beat and returns the request so its progress can be followed.
        ///
        /// RimTalk marks a request "spoken" when it is dispatched to the LLM, not when the
        /// pawn's line appears on screen. It may also be consumed as scene context for a
        /// nearby pawn's conversation rather than producing a dedicated line. Either way the
        /// prompt reached the model, which is what we count as delivery.
        /// </summary>
        public static TalkRequest Deliver(Pawn pawn, string prompt, bool urgent = false)
        {
            if (pawn == null || string.IsNullOrEmpty(prompt)) return null;

            try
            {
                var state = RimTalkCache.Get(pawn);
                if (state == null) return null;

                // Urgent clears the pawn's other pending requests, which is how RimTalk
                // itself handles something that has to be said now.
                state.AddTalkRequest(prompt, null, urgent ? TalkType.Urgent : TalkType.Event);

                // TalkType.Event is queued with AddFirst, so ours is at the head. Verify
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
                // outcome. The usual cause is Cache.Refresh() evicting the whole PawnState
                // when the pawn stops being talk-eligible — anaesthetised for surgery,
                // downed, or despawned into a caravan — taking its queued requests with it.
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
        /// True when RimTalk would reject a solo line. Single-pawn events can't deliver at
        /// all in that state, so it's worth telling the player rather than failing quietly.
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
