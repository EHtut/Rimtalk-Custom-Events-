using System;
using System.Collections.Generic;
using System.Linq;
using RimTalkCustomEvents.Data;
using UnityEngine;
using Verse;

namespace RimTalkCustomEvents.UI
{
    /// <summary>
    /// Searchable list of every def of one kind that this playthrough has loaded, grouped
    /// by the mod it came from. This is how an event reaches modded content without the
    /// author needing to know defNames by heart.
    /// </summary>
    public class DefPickerWindow : Window
    {
        private const string AnyMod = "(any mod)";

        private readonly DefKind _kind;
        private readonly string _title;
        private readonly Action<DefEntry> _onPick;

        private string _query = "";
        private string _modFilter = AnyMod;
        private Vector2 _scroll;
        private List<DefEntry> _results;
        private string _lastQuery;
        private string _lastModFilter;

        public DefPickerWindow(DefKind kind, string title, Action<DefEntry> onPick)
        {
            _kind = kind;
            _title = title;
            _onPick = onPick;

            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            forcePause = true;
        }

        public override Vector2 InitialSize => new Vector2(640f, 720f);

        public override void DoWindowContents(Rect inRect)
        {
            var y = inRect.y;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 32f), _title);
            Text.Font = GameFont.Small;
            y += 38f;

            // Search box, focused so you can type straight away.
            GUI.SetNextControlName("rtce_def_search");
            _query = Widgets.TextField(new Rect(inRect.x, y, inRect.width - 180f, 28f), _query);

            if (Widgets.ButtonText(new Rect(inRect.x + inRect.width - 174f, y, 174f, 28f), Truncate(_modFilter, 22)))
            {
                ShowModFilterMenu();
            }

            y += 34f;

            EnsureResults();

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 20f),
                _results.Count >= 200
                    ? "Showing the first 200 matches — keep typing to narrow it down."
                    : $"{_results.Count} match(es)");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 22f;

            var listRect = new Rect(inRect.x, y, inRect.width, inRect.height - y - 10f);
            var rowHeight = 30f;
            var viewRect = new Rect(0f, 0f, listRect.width - 20f, _results.Count * rowHeight);

            Widgets.BeginScrollView(listRect, ref _scroll, viewRect);

            for (var i = 0; i < _results.Count; i++)
            {
                var entry = _results[i];
                var rowRect = new Rect(0f, i * rowHeight, viewRect.width, rowHeight - 2f);

                if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

                var labelRect = new Rect(rowRect.x + 4f, rowRect.y, rowRect.width * 0.65f, rowRect.height);
                Widgets.Label(labelRect, Truncate(entry.Display, 48));

                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.65f, 0.65f, 0.65f);
                var modRect = new Rect(rowRect.x + rowRect.width * 0.66f, rowRect.y + 4f, rowRect.width * 0.33f, rowRect.height);
                Widgets.Label(modRect, Truncate(entry.ModName, 24));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                if (Widgets.ButtonInvisible(rowRect))
                {
                    _onPick?.Invoke(entry);
                    Close();
                    return;
                }
            }

            Widgets.EndScrollView();
        }

        /// <summary>Recomputes only when the query or filter changed — this scans every def.</summary>
        private void EnsureResults()
        {
            if (_results != null && _query == _lastQuery && _modFilter == _lastModFilter) return;

            _lastQuery = _query;
            _lastModFilter = _modFilter;
            _results = DefFinder.Search(_kind, _query, _modFilter == AnyMod ? null : _modFilter);
        }

        private void ShowModFilterMenu()
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption(AnyMod, () => _modFilter = AnyMod)
            };

            foreach (var mod in DefFinder.SourceMods(_kind))
            {
                var captured = mod;
                options.Add(new FloatMenuOption(captured, () => _modFilter = captured));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
        }
    }
}
