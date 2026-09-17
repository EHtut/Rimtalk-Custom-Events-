using System.Collections.Generic;
using RimTalkCustomEvents.Data;
using RimTalkCustomEvents.Util;
using RimWorld;
using Verse;

namespace RimTalkCustomEvents.Scheduling
{
    /// <summary>
    /// Decides when automatically triggered events fire.
    ///
    /// Checked once per in-game hour, which is plenty for triggers measured in hours and
    /// days, and keeps the per-tick cost at nothing.
    /// </summary>
    public static class TriggerScheduler
    {
        public const int CheckIntervalTicks = GenDate.TicksPerHour;

        /// <summary>
        /// Rolls every eligible event once. Called on the hour by the game component, which
        /// owns the per-event bookkeeping passed in here.
        /// </summary>
        public static void CheckAll(
            CustomEventsGameComponent component,
            Dictionary<string, int> lastAutoFireTick,
            Dictionary<string, int> lastDailyDay)
        {
            var settings = RimTalkCustomEventsMod.Settings;
            if (settings == null || !settings.enabled) return;
            if (settings.frequencyMultiplier <= 0f) return;

            var map = Find.CurrentMap;
            if (map == null) return;

            var now = Find.TickManager.TicksGame;
            var hour = GenLocalDate.HourOfDay(map);
            var day = GenDate.DaysPassed;

            foreach (var def in EventStore.AllEnabled)
            {
                var mode = def.Trigger.Mode;
                if (mode == TriggerMode.Manual) continue;

                if (!WithinStartWindow(def, hour)) continue;
                if (!PastRefireDelay(def, lastAutoFireTick, now)) continue;

                if (!ShouldFire(def, mode, hour, day, lastDailyDay, settings.frequencyMultiplier)) continue;

                Fire(component, def, map, lastAutoFireTick, lastDailyDay, now, day);
            }
        }

        private static bool ShouldFire(
            CustomEvent def,
            TriggerMode mode,
            int hour,
            int day,
            Dictionary<string, int> lastDailyDay,
            float frequencyMultiplier)
        {
            // Daily, Monthly and Yearly are the same rule at different scales: fire once
            // per period, at the chosen hour.
            if (IsCalendarMode(mode))
            {
                if (hour != def.Trigger.DailyHour) return false;

                var period = PeriodIndex(mode, day);
                if (lastDailyDay.TryGetValue(def.DefName, out var firedOn) && firedOn == period) return false;

                return Rand.Chance(def.Trigger.DailyChance * frequencyMultiplier);
            }

            // Occasionally: a mean-time-between roll of this mod's own. A higher frequency
            // multiplier shortens the mean time between occurrences.
            var mtb = def.Trigger.MtbDays / frequencyMultiplier;
            if (mtb <= 0f) return false;

            return Rand.MTBEventOccurs(mtb, GenDate.TicksPerDay, CheckIntervalTicks);
        }

        private static void Fire(
            CustomEventsGameComponent component,
            CustomEvent def,
            Map map,
            Dictionary<string, int> lastAutoFireTick,
            Dictionary<string, int> lastDailyDay,
            int now,
            int day)
        {
            if (!component.TryStartFor(def, map, out var reason))
            {
                RTCELog.Debug($"\"{def.Label}\" came up but didn't start: {reason}");
                return;
            }

            // Only record a fire that actually started, so a blocked roll can retry next hour.
            lastAutoFireTick[def.DefName] = now;

            if (IsCalendarMode(def.Trigger.Mode))
            {
                lastDailyDay[def.DefName] = PeriodIndex(def.Trigger.Mode, day);
            }
        }

        public static bool IsCalendarMode(TriggerMode mode)
        {
            return mode == TriggerMode.Daily || mode == TriggerMode.Monthly || mode == TriggerMode.Yearly;
        }

        /// <summary>
        /// Which period the given day falls in, so "once per period" works the same way at
        /// every scale. A quadrum is 15 days and a year is 4 of them.
        /// </summary>
        private static int PeriodIndex(TriggerMode mode, int day)
        {
            switch (mode)
            {
                case TriggerMode.Monthly: return day / GenDate.DaysPerQuadrum;
                case TriggerMode.Yearly: return day / (GenDate.DaysPerQuadrum * 4);
                default: return day;
            }
        }

        private static bool WithinStartWindow(CustomEvent def, int hour)
        {
            var min = def.Trigger.StartHourMin;
            var max = def.Trigger.StartHourMax;
            if (!min.HasValue || !max.HasValue) return true;

            // A window that wraps past midnight, e.g. 22 to 4.
            if (min.Value <= max.Value) return hour >= min.Value && hour <= max.Value;
            return hour >= min.Value || hour <= max.Value;
        }

        private static bool PastRefireDelay(CustomEvent def, Dictionary<string, int> lastAutoFireTick, int now)
        {
            if (def.Trigger.MinRefireDays <= 0f) return true;
            if (!lastAutoFireTick.TryGetValue(def.DefName, out var last)) return true;

            return now - last >= (int)(def.Trigger.MinRefireDays * GenDate.TicksPerDay);
        }
    }
}
