using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimTalkCustomEvents.Util;
using Verse;

namespace RimTalkCustomEvents.Data
{
    /// <summary>One file that failed to load, kept so the settings UI can show it.</summary>
    public class EventLoadProblem
    {
        public string FileName;
        public string Problem;
    }

    /// <summary>
    /// Loads player-authored events from JSON files in the RimWorld config folder.
    ///
    /// Config dir rather than the mod folder so events survive a mod update or reinstall,
    /// and can be shared as individual files. On first run the starter events shipped in
    /// the mod's own Events/ folder are copied across.
    /// </summary>
    public static class EventStore
    {
        private static readonly Dictionary<string, CustomEvent> Events =
            new Dictionary<string, CustomEvent>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<EventLoadProblem> Problems = new List<EventLoadProblem>();

        public static IReadOnlyList<EventLoadProblem> LoadProblems => Problems;

        public static IEnumerable<CustomEvent> All => Events.Values;

        public static IEnumerable<CustomEvent> AllEnabled => Events.Values.Where(e => e.Enabled);

        public static int Count => Events.Count;

        /// <summary>&lt;Config&gt;/RimTalkCustomEvents/Events</summary>
        public static string EventsFolder =>
            Path.Combine(Path.Combine(GenFilePaths.ConfigFolderPath, "RimTalkCustomEvents"), "Events");

        public static CustomEvent Get(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return null;
            return Events.TryGetValue(defName, out var e) ? e : null;
        }

        /// <summary>
        /// Re-reads every event file from disk. Safe to call at any time — a file that fails
        /// to parse is reported and skipped, leaving the rest loaded.
        /// </summary>
        public static void Reload()
        {
            Events.Clear();
            Problems.Clear();

            string folder;
            try
            {
                folder = EventsFolder;
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                RTCELog.Error($"Could not create the events folder: {ex.Message}");
                return;
            }

            BootstrapStarterEvents(folder);

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                RTCELog.Error($"Could not list event files: {ex.Message}");
                return;
            }

            foreach (var path in files)
            {
                LoadFile(path);
            }

            // Second pass: def references are checked only once every event is loaded, so a
            // chainEvent pointing at a file later in the list isn't reported as missing.
            ValidateReferences();

            var problemSuffix = Problems.Count > 0 ? $", {Problems.Count} file(s) with problems" : "";
            RTCELog.Message($"Loaded {Events.Count} event(s){problemSuffix} from {folder}");
        }

        private static void LoadFile(string path)
        {
            var fileName = Path.GetFileName(path);

            try
            {
                var text = File.ReadAllText(path);
                var root = Json.Parse(text);

                if (root.Type != JsonType.Object)
                {
                    AddProblem(fileName, "the file must contain a single JSON object");
                    return;
                }

                var customEvent = CustomEvent.FromJson(root, path);
                var errors = customEvent.Validate(out var warnings);

                foreach (var warning in warnings)
                {
                    RTCELog.Warning($"{fileName}: {warning}");
                }

                if (errors.Count > 0)
                {
                    AddProblem(fileName, string.Join("; ", errors.ToArray()));
                    return;
                }

                if (Events.ContainsKey(customEvent.DefName))
                {
                    RTCELog.Warning(
                        $"{fileName}: another file already defines \"{customEvent.DefName}\" — overwriting it.");
                }

                Events[customEvent.DefName] = customEvent;
                RTCELog.Debug($"Loaded event \"{customEvent.DefName}\" from {fileName}");
            }
            catch (JsonParseException ex)
            {
                AddProblem(fileName, ex.Message);
            }
            catch (Exception ex)
            {
                AddProblem(fileName, ex.Message);
            }
        }

        /// <summary>
        /// Warns about defs an event names that no loaded mod provides. Warnings only — an
        /// event file shared between two different load orders should still run the parts
        /// that resolve.
        /// </summary>
        private static void ValidateReferences()
        {
            // Nothing to check against if the def database isn't up yet.
            if (DefDatabase<HediffDef>.DefCount == 0) return;

            foreach (var customEvent in Events.Values)
            {
                foreach (var problem in DefFinder.ValidateReferences(customEvent))
                {
                    RTCELog.Warning($"\"{customEvent.DefName}\" {problem}");
                }
            }
        }

        private static void AddProblem(string fileName, string problem)
        {
            Problems.Add(new EventLoadProblem { FileName = fileName, Problem = problem });
            RTCELog.Error($"Could not load {fileName} — {problem}");
        }

        /// <summary>
        /// Copies the events shipped with the mod into the config folder, skipping any the
        /// player already has. Only fills gaps, so a deleted starter event stays deleted
        /// unless its file is gone entirely and the folder is empty.
        /// </summary>
        private static void BootstrapStarterEvents(string targetFolder)
        {
            var content = RimTalkCustomEventsMod.Instance?.Content;
            if (content == null) return;

            var sourceFolder = Path.Combine(content.RootDir, "Events");
            if (!Directory.Exists(sourceFolder)) return;

            try
            {
                // Only seed when the player has no events at all. Otherwise a starter event
                // they deliberately deleted would reappear on every launch.
                if (Directory.GetFiles(targetFolder, "*.json", SearchOption.TopDirectoryOnly).Length > 0)
                {
                    return;
                }

                foreach (var source in Directory.GetFiles(sourceFolder, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var target = Path.Combine(targetFolder, Path.GetFileName(source));
                    File.Copy(source, target, false);
                    RTCELog.Message($"Installed starter event {Path.GetFileName(source)}");
                }
            }
            catch (Exception ex)
            {
                RTCELog.Warning($"Could not install starter events: {ex.Message}");
            }
        }
    }
}
