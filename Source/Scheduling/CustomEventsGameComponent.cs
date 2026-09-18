using System;
using System.Collections.Generic;
using System.Linq;
using RimTalkCustomEvents.Data;
using RimWorld;
using RimTalkCustomEvents.Integration;
using RimTalkCustomEvents.Util;
using Verse;

namespace RimTalkCustomEvents.Scheduling
{
    /// <summary>
    /// Owns every running event and drives their state machines.
    ///
    /// Ticks on a one-second cadence rather than every tick: beats are scheduled in hours,
    /// and the readiness check reaches into RimTalk's cache, which isn't worth doing 60
    /// times a second.
    /// </summary>
    public class CustomEventsGameComponent : GameComponent
    {
        private const int TickInterval = 60;

        private List<EventInstance> _instances = new List<EventInstance>();

        /// <summary>Keyed "&lt;pawnThingID&gt;|&lt;eventDefName&gt;" -> tick the event last fired.</summary>
        private Dictionary<string, int> _lastFired = new Dictionary<string, int>();

        /// <summary>Event defName -> tick it last fired automatically, for minRefireDays.</summary>
        private Dictionary<string, int> _lastAutoFireTick = new Dictionary<string, int>();

        /// <summary>Event defName -> period it last fired in, so a calendar event fires once per period.</summary>
        private Dictionary<string, int> _lastDailyDay = new Dictionary<string, int>();

        /// <summary>Event defName -> how many times it has fired in this save, for maxRunsPerSave.</summary>
        private Dictionary<string, int> _runCounts = new Dictionary<string, int>();

        private bool _warnedAboutMonologues;

        /// <summary>
        /// Chains requested by an effect, started on a later tick.
        ///
        /// A chainEvent usually fires from an END beat, at which point the instance that
        /// requested it is still active — so starting it inline would trip the per-pawn
        /// concurrency limit against the very event that's finishing. Deferring also means
        /// chains go through the normal limits, which is what stops A -> B -> A looping.
        /// </summary>
        private readonly List<PendingChain> _pendingChains = new List<PendingChain>();

        private class PendingChain
        {
            public string EventDefName;
            public Pawn Pawn;
        }

        public CustomEventsGameComponent(Game game)
        {
            // The registry is static and outlives a game, so loading a different save would
            // otherwise inherit whatever modifiers the previous one had running.
            ModifierRegistry.Clear();
        }

        public static CustomEventsGameComponent Current => Verse.Current.Game?.GetComponent<CustomEventsGameComponent>();

        public IEnumerable<EventInstance> ActiveInstances => _instances.Where(i => !i.IsFinished);

        public int ActiveCount => _instances.Count(i => !i.IsFinished);

        public override void GameComponentTick()
        {
            var settings = RimTalkCustomEventsMod.Settings;
            if (settings == null || !settings.enabled)
            {
                // Switching the mod off mid-event would otherwise leave modifier text glued
                // into every prompt for that pawn, with nothing left running to clear it.
                if (ModifierRegistry.HasAny) ModifierRegistry.Clear();
                return;
            }

            var ticks = Find.TickManager.TicksGame;

            // Trigger rolls happen on the hour, independently of whether anything is running.
            if (ticks % TriggerScheduler.CheckIntervalTicks == 0)
            {
                TriggerScheduler.CheckAll(this, _lastAutoFireTick, _lastDailyDay);
            }

            if (_instances.Count == 0 && _pendingChains.Count == 0) return;
            if (ticks % TickInterval != 0) return;

            for (var i = _instances.Count - 1; i >= 0; i--)
            {
                var instance = _instances[i];

                instance.Tick();

                if (instance.IsFinished)
                {
                    _instances.RemoveAt(i);
                }
            }

            StartPendingChains();
            SyncModifiers();
        }

        /// <summary>
        /// Refreshes the Modifier-mode text applying to each pawn. Rebuilt from live
        /// instances rather than registered and unregistered as events start and stop, so
        /// there is no lifecycle to get wrong and it restores itself after a load.
        /// </summary>
        private static readonly List<KeyValuePair<Pawn, string>> NoModifiers =
            new List<KeyValuePair<Pawn, string>>();

        private void SyncModifiers()
        {
            // Stays null until something is actually found, so an event with no modifier
            // costs no allocation on the tick thread — this runs once a second per event.
            List<KeyValuePair<Pawn, string>> active = null;

            foreach (var instance in _instances)
            {
                var text = instance.ActiveModifierText();
                if (text == null || instance.Pawn == null) continue;

                if (active == null) active = new List<KeyValuePair<Pawn, string>>();
                active.Add(new KeyValuePair<Pawn, string>(instance.Pawn, text));
            }

            // Nothing to apply and nothing left over: no work to do at all.
            if (active == null && !ModifierRegistry.HasAny) return;

            ModifierRegistry.EnsureHooked();
            ModifierRegistry.Sync(active ?? NoModifiers);
        }

        /// <summary>Queues a chained event. Started on a later tick — see _pendingChains.</summary>
        public void QueueChain(string eventDefName, Pawn pawn)
        {
            if (string.IsNullOrEmpty(eventDefName) || pawn == null) return;
            _pendingChains.Add(new PendingChain { EventDefName = eventDefName, Pawn = pawn });
        }

        private void StartPendingChains()
        {
            if (_pendingChains.Count == 0) return;

            // Copy and clear first: a chain that fails is dropped rather than retried
            // forever, and a chain started here can't re-enter this loop.
            var chains = _pendingChains.ToList();
            _pendingChains.Clear();

            foreach (var chain in chains)
            {
                var def = EventStore.Get(chain.EventDefName);
                if (def == null)
                {
                    RTCELog.Warning($"Chained event \"{chain.EventDefName}\" doesn't exist — skipping.");
                    continue;
                }

                if (!TryStart(def, chain.Pawn, out var reason))
                {
                    RTCELog.Debug($"Chained event \"{chain.EventDefName}\" didn't start: {reason}");
                }
            }
        }

        /// <summary>
        /// Starts an event on a pawn.
        /// </summary>
        /// <param name="def">The event to run.</param>
        /// <param name="pawn">Who it happens to.</param>
        /// <param name="reason">Set when the event can't start, for the caller to show.</param>
        /// <param name="bypassLimits">
        /// Skips concurrency and cooldown checks. Used by the dev trigger so testing an
        /// event never silently does nothing.
        /// </param>
        /// <param name="countsAsRun">
        /// Whether this start consumes one of the event's per-save runs. False when the
        /// caller is starting several pawns as part of a single occurrence and has already
        /// counted it.
        /// </param>
        public bool TryStart(CustomEvent def, Pawn pawn, out string reason, bool bypassLimits = false,
            List<Pawn> participants = null, bool countsAsRun = true)
        {
            reason = null;

            if (def == null)
            {
                reason = "no event given";
                return false;
            }

            if (pawn == null || pawn.Dead || !pawn.Spawned)
            {
                reason = "the pawn isn't available";
                return false;
            }

            var settings = RimTalkCustomEventsMod.Settings;
            if (settings == null || !settings.enabled)
            {
                reason = "custom events are switched off in mod settings";
                return false;
            }

            if (!RimTalkBridge.IsTracked(pawn))
            {
                reason = $"RimTalk isn't tracking {pawn.LabelShort} yet";
                return false;
            }

            // The run cap is checked even for a dev/test fire: it's a property of the save,
            // and quietly exceeding it would make the number meaningless.
            if (countsAsRun && def.HasRunLimit && RunCount(def) >= def.MaxRunsPerSave)
            {
                reason = def.MaxRunsPerSave == 0
                    ? $"\"{def.Label}\" is set to never fire (limit 0)"
                    : $"\"{def.Label}\" has already run {def.MaxRunsPerSave} time(s) this save";
                return false;
            }

            if (!bypassLimits)
            {
                if (ActiveCount >= settings.maxConcurrentGlobal)
                {
                    reason = $"already running {ActiveCount} events (colony limit)";
                    return false;
                }

                // Counts participants as well: being in a shared event still occupies a
                // pawn, so they shouldn't simultaneously be the focus of another.
                var onPawn = _instances.Count(i => !i.IsFinished && i.AllPawns.Contains(pawn));
                if (onPawn >= settings.maxConcurrentPerPawn)
                {
                    reason = $"{pawn.LabelShort} is already in an event";
                    return false;
                }

                if (HasExclusionClash(def, pawn))
                {
                    reason = $"{pawn.LabelShort} is in a conflicting event";
                    return false;
                }

                var cooldownTicks = (int)(def.Target.CooldownDays * GenDate.TicksPerDay);
                if (cooldownTicks > 0 && _lastFired.TryGetValue(CooldownKey(pawn, def), out var last))
                {
                    var elapsed = Find.TickManager.TicksGame - last;
                    if (elapsed < cooldownTicks)
                    {
                        var daysLeft = (cooldownTicks - elapsed) / (float)GenDate.TicksPerDay;
                        reason = $"on cooldown for {pawn.LabelShort} for another {daysLeft:0.#} days";
                        return false;
                    }
                }
            }

            WarnAboutMonologuesOnce();

            var instance = new EventInstance(def, pawn, participants);
            _instances.Add(instance);
            // Everyone taking part goes on cooldown, not just the primary — otherwise a
            // participant could be picked again immediately for the same event.
            var firedAt = Find.TickManager.TicksGame;
            foreach (var affected in instance.AllPawns)
            {
                _lastFired[CooldownKey(affected, def)] = firedAt;
            }

            if (countsAsRun) Bump(def);

            // Open immediately rather than waiting up to a second for the next scheduler
            // pass. Matters most for a test fire, where any delay reads as "nothing
            // happened" and sends you looking for a bug that is not there.
            instance.Tick();

            RTCELog.Message($"Started \"{def.Label}\" on {pawn.LabelShort}.");
            return true;
        }

        /// <summary>
        /// Picks who the event affects and starts it, honouring target.count and
        /// target.group. Independent means a separate event each; shared means one event
        /// they all take part in.
        /// </summary>
        public bool TryStartFor(CustomEvent def, Map map, out string reason, bool bypassLimits = false)
        {
            reason = null;
            if (def == null || map == null)
            {
                reason = "no event or map";
                return false;
            }

            // One occurrence, however many pawns it touches. Checking the cap per pawn
            // would let "max 1 per save" with "3 pawns affected" start one and silently
            // refuse the rest.
            if (def.HasRunLimit && RunCount(def) >= def.MaxRunsPerSave)
            {
                reason = def.MaxRunsPerSave == 0
                    ? $"\"{def.Label}\" is set to never fire (limit 0)"
                    : $"\"{def.Label}\" has already run {def.MaxRunsPerSave} time(s) this save";
                return false;
            }

            var wanted = Math.Max(1, def.Target.Count);
            var picked = PawnSelector.TryPickMany(def, map, wanted);

            if (picked.Count == 0)
            {
                reason = "nobody on the map matched its target filters";
                return false;
            }

            if (def.Target.Group == GroupMode.Shared && picked.Count > 1)
            {
                var ok = TryStart(def, picked[0], out reason, bypassLimits,
                    picked.GetRange(1, picked.Count - 1), countsAsRun: false);
                if (ok) Bump(def);
                return ok;
            }

            // Independent: a separate event each, but still one occurrence. Counts as
            // started if any of them did — partial success beats refusing the whole thing.
            var started = 0;
            string lastReason = null;

            foreach (var pawn in picked)
            {
                if (TryStart(def, pawn, out var why, bypassLimits, countsAsRun: false)) started++;
                else lastReason = why;
            }

            if (started > 0)
            {
                Bump(def);
                return true;
            }

            reason = lastReason ?? "no pawn could start it";
            return false;
        }

        /// <summary>How many times this event has fired in this save.</summary>
        public int RunCount(CustomEvent def)
        {
            return def != null && _runCounts.TryGetValue(def.DefName, out var count) ? count : 0;
        }

        private void Bump(CustomEvent def)
        {
            _runCounts.TryGetValue(def.DefName, out var count);
            _runCounts[def.DefName] = count + 1;
        }

        private bool HasExclusionClash(CustomEvent def, Pawn pawn)
        {
            if (def.Target.ExclusionTags.Count == 0) return false;

            foreach (var instance in _instances)
            {
                if (instance.IsFinished || instance.Pawn != pawn) continue;

                var otherDef = instance.Def;
                if (otherDef == null) continue;

                foreach (var tag in otherDef.Target.ExclusionTags)
                {
                    if (def.Target.ExclusionTags.Contains(tag)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Single-pawn events can't deliver at all when RimTalk has monologues switched off,
        /// so say it once rather than letting every beat fail quietly.
        /// </summary>
        private void WarnAboutMonologuesOnce()
        {
            if (_warnedAboutMonologues) return;
            _warnedAboutMonologues = true;

            if (RimTalkBridge.MonologuesDisabled())
            {
                RTCELog.Warning(
                    "RimTalk has \"allow monologue\" switched off. Event beats are unaffected — they " +
                    "are queued as their own dedicated lines — but a lone pawn will rarely say anything " +
                    "else, so a CONTINUE modifier will have little ordinary dialogue to colour.");
            }
        }

        private static string CooldownKey(Pawn pawn, CustomEvent def)
        {
            return pawn.ThingID + "|" + def.DefName;
        }

        public void CancelAll()
        {
            foreach (var instance in _instances.Where(i => !i.IsFinished))
            {
                instance.Cancel();
            }

            ModifierRegistry.Clear();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref _instances, "instances", LookMode.Deep);
            Scribe_Collections.Look(ref _lastFired, "lastFired", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _lastAutoFireTick, "lastAutoFireTick", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _lastDailyDay, "lastDailyDay", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _runCounts, "runCounts", LookMode.Value, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                _instances = _instances ?? new List<EventInstance>();
                _lastFired = _lastFired ?? new Dictionary<string, int>();
                _lastAutoFireTick = _lastAutoFireTick ?? new Dictionary<string, int>();
                _lastDailyDay = _lastDailyDay ?? new Dictionary<string, int>();
                _runCounts = _runCounts ?? new Dictionary<string, int>();

                // Drop anything that didn't survive the save: a deleted event file, or a
                // pawn reference that no longer resolves.
                var removed = _instances.RemoveAll(i => i == null || i.Pawn == null || i.Def == null);
                if (removed > 0)
                {
                    RTCELog.Message($"Dropped {removed} event(s) whose pawn or event file is gone.");
                }
            }
        }
    }
}
