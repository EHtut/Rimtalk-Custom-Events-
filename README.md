# RimTalk Custom Events

A companion mod for [RimTalk](https://steamcommunity.com/sharedfiles/filedetails/?id=3551203752)
that lets you write your own multi-phase, LLM-driven pawn events.

Each event has three beats:

- **BEGINNING** — fires once and starts it
- **CONTINUE** — replayed a few times through the day, escalating
- **END** — the payoff

Any phase can also *do* something: apply a hediff, add a mood, shift a need, spawn
items, change a trait, grant skill XP, fire a real RimWorld incident, or chain into
another event.

## Requirements

- RimWorld 1.6
- [RimTalk](https://steamcommunity.com/sharedfiles/filedetails/?id=3551203752) (`cj.rimtalk`)
- Harmony (`brrainz.harmony`)

## Example — FROST

```json
{
  "defName": "Frost",
  "label": "frost",
  "phases": {
    "beginning": "A sudden, sharp chill races up their spine...",
    "continue": {
      "text": "They feel subtly colder and colder as the day progresses...",
      "effects": [
        { "hediff": { "def": "RTCE_FrostTouched", "severity": 0.2 } }
      ]
    },
    "end": {
      "text": "An intense, bone-deep freezing sensation...",
      "effects": [
        { "oneOf": [
            { "weight": 0.3, "incident": "ColdSnap" },
            { "weight": 0.7, "nothing": true } ] }
      ]
    }
  },
  "timing": { "durationHours": 12, "continueCount": 3 },
  "trigger": { "mode": "occasionally", "mtbDays": 6 }
}
```

Hediff severity **accumulates** across CONTINUE beats, so three beats of `0.2` leave
the pawn at `0.6` by the time END fires.

## Where events live

```
<RimWorld config>/RimTalkCustomEvents/Events/*.json
```

One file per event. `Frost.json` is installed there on first run. Edit them in
**Options → Mod Settings → RimTalk Custom Events → Events**, or in a text editor and
hit *Reload from disk* — no restart needed.

The JSON parser is lenient on purpose, since these files are mostly hand-written
prose: `//` and `/* */` comments, trailing commas, and raw line breaks inside strings
are all fine. Parse errors report a line and column.

Note that **saving from the in-game editor rewrites the file and drops its comments**.
The editor warns you first.

## Reaching modded content

Every def field — hediffs, thoughts, incidents, items, traits, needs, skills — opens a
searchable picker listing what *your* load order actually provides, filterable by
source mod. Nothing is hardcoded, so content from mods added later just shows up.

On load, every def an event names is checked against what's installed, and anything
missing is reported by name rather than silently doing nothing later.

## It does not touch the storyteller

This mod paces itself — mean time between occurrences, minimum refire delays, per-pawn
cooldowns and concurrency caps. It never registers incidents with RimWorld's
storyteller or draws on its threat budget.

## Building

Needs the .NET SDK. `RimTalk.dll` is located automatically from the Steam Workshop
folder, with a local `Mods/RimTalk` fallback.

```powershell
.\build.ps1
```

Output goes straight to `1.6/Assemblies/`.

## Design notes

[DESIGN.md](DESIGN.md) is the single source of truth for how this works and why —
including the RimTalk internals it depends on, which are load-bearing and not
obvious.
