# RimTalk Custom Events — Design Spec

Companion mod for **RimTalk** (`cj.rimtalk`). Lets the player author multi-phase,
LLM-driven pawn events (BEGINNING → CONTINUE → END) that fire daily or at random
intervals, and can leave a hediff, a mood, a need change, an item or a full RimWorld
incident behind at any phase.

> This is the single source of truth for the design. Update it in the same session
> that changes the design; delete sections that stop describing anything real.

**Status:** v1 is feature-complete and compiles — events, per-phase effects, runtime
hediffs, self-paced triggers, targeting, the JSON writer, three CONTINUE modes, and the
Mod Options browser, editor and diagnostics. 102 checks pass outside the game.
**Nothing has been run inside RimWorld yet**; that is the one remaining gate.

---

## 1. Research findings (verified against RimTalk source)

Full RimTalk source is vendored at `<workshop>/294100/3642675329/ref/RimTalk`
(RimTalk Quests checked it in). These are read off the actual code, not assumed:

| Finding | Where | Why it matters |
|---|---|---|
| `Cache.Get(pawn)` → `PawnState`; `PawnState.AddTalkRequest(prompt, recipient, TalkType)` | `Data/PawnState.cs:26` | **The targeted injection point.** How we make *one specific pawn* speak to a prompt. |
| `TalkType.Event` requests are `AddFirst` — they jump the pawn's queue | `Data/PawnState.cs:57` | Our phases get priority over idle chitchat. Correct type to use. |
| `TalkRequestPool.Add(...)` **reassigns `Initiator` to whoever is picked** | `Data/TalkRequestPool.cs:38` | ⚠️ The global pool cannot target a pawn. We must use `PawnState`, not the pool. |
| `TalkRequest.IsExpired()` → non-urgent expires after `GetTicksForDuration(20)` | `Data/TalkRequest.cs:44` | |
| `GetTicksForDuration(s)` = **real-time seconds × tick rate** | `Util/CommonUtil.cs:15` | ⚠️ **A queued phase dies after ~20 real-time seconds.** Fire-and-forget silently drops beats. |
| `CanDisplayTalk()` false when drafted / asleep / off-map / dead (settings-dependent) | `Data/PawnState.cs:91` | Must gate before firing, not after. |
| `CanGenerateTalk()` also requires no in-flight generation + reply interval elapsed | `Data/PawnState.cs:106` | Our readiness check. |
| `RimTalkPromptAPI.InjectPawnSection(modId, name, anchor, position, provider)` | `API/RimTalkPromptAPI.cs` | Official extension surface. This is how Modifier-mode CONTINUE reaches every prompt for a pawn. Anchors are nested: `ContextCategories.Pawn.Health`, not `ContextCategories.Health`. |
| RimTalk templates run through **Scriban** | `Prompt/Parser/ScribanParser.cs` | Event text can support `{{pawn.name}}`-style variables. |
| `MarkRequestSpoken` runs when a request is **dispatched to the LLM**, not when the line appears | `Service/TalkService.cs:92` | "Delivered" means the prompt reached the model, nothing stronger. |
| Building scene context **consumes other pawns' queued requests** and folds them into the prompt | `Util/PawnUtil.cs:230-233` | A beat may surface as scene context for a nearby pawn's conversation rather than a dedicated line. Still counts as delivered. |
| `AllowMonologue` (default **on**) rejects solo lines before marking them spoken | `Service/TalkService.cs:76` | ⚠️ With it off, single-pawn events can only deliver when someone else is nearby. We warn once. |
| `.csproj` resolves `RimTalk.dll` from the workshop path with a local-Mods fallback | `RimTalkQuests.csproj` | Copy this pattern verbatim. |
| dotnet SDK **8.0.424** present; no msbuild on PATH | this machine | Build with `dotnet build`, SDK-style project targeting `net472`. |

**The single most important consequence:** because a queued phase expires in ~20
real seconds, the scheduler must be *pull-based* — hold the beat until the pawn is
actually ready, then inject. Never inject on a timer alone.

---

## 2. Architecture

```
RimTalkCustomEvents
├─ Store         JSON event files in the RimWorld config dir; load/save/validate
├─ Triggers      Daily / MTB "occasionally" / manual — self-paced, no storyteller
├─ Scheduler     GameComponent: owns active instances, ticks the state machine
├─ Instance      Per-pawn state machine: Beginning → Continuing×N → Ending
├─ Injection     PawnState.AddTalkRequest (beats) + InjectPawnSection (modifiers)
├─ Effects       Per phase: hediff / thought / need / incident / chain / items / trait
├─ HediffFactory Runtime-generated HediffDefs from inline event definitions
├─ Writer        CustomEvent → JSON, defaults omitted, round-trip verified
└─ UI            Mod Options: browser, editor, def pickers; dev gizmos
```

**Load order:** `loadAfter: cj.rimtalk`, `brrainz.harmony`. Hard `modDependencies`
on both.

---

## 3. Event storage and authoring

**Authoring surface is the Mod Options menu.** Players create and edit events in
`Options → Mod Settings → RimTalk Custom Events`, not by hand-editing files.

**Storage is one JSON file per event**, in the RimWorld config dir:

```
<Config>/RimTalkCustomEvents/Events/<defName>.json
```

(alongside the existing `Config/RimTalk/` folder). Rationale for a folder of JSON
rather than serialising into the mod settings XML:

- survives a settings reset or a mod reinstall
- shareable by sending one file; Import/Export buttons in the options menu
- still hand-editable for bulk prompt-writing, which is faster than a GUI when
  you're drafting a dozen events at once
- hot-reloadable — Reload button re-reads the folder without a game restart

The mod's own `Events/` folder ships **starter events** (FROST) that are copied
into the config dir on first run if it's empty. XML `Def` loading is **not** in
scope — one format, one code path.

### FROST, ported from your prompt

This mirrors the shipped `Events/Frost.json`, minus its comments.

```json
{
  "defName": "Frost",
  "label": "frost",
  "description": "A creeping supernatural chill that ends in a vision of the frozen dead.",

  "phases": {
    // A phase is either a bare string, or an object with "text" plus "effects".
    "beginning": "A sudden, sharp chill races up their spine, making their hair stand on end and breaking their rhythm for a moment.",

    "continue": {
      "text": "They feel subtly colder and colder as the day progresses; they might hunch their shoulders or rub their arms while working, but the cold is mostly internal.",
      "effects": [
        // Severity accumulates across beats: 3 beats leave them at 0.6 by END.
        { "hediff": { "def": "RTCE_FrostTouched", "severity": 0.2 } },
        { "need": { "def": "Comfort", "offset": -0.1 } }
      ]
    },

    "end": {
      "text": "An intense, bone-deep freezing sensation. They wrap themselves tightly in their clothes (if any) and shiver violently. Then, a spectral hand grips their throat and they see a vision of a thousand frozen corpses surrounding them; then it vanishes and the event ends.",
      "effects": [
        {
          "oneOf": [
            { "weight": 0.3, "incident": "ColdSnap" },
            { "weight": 0.7, "nothing": true }
          ]
        }
      ]
    }
  },

  "timing": {
    "durationHours": 12,
    "continueCount": 3,
    "continueSpacing": "even",
    "continueJitter": 0.25,
    "onBlocked": "retry",
    "beatTimeoutHours": 2,
    "minBeatGapHours": 0.5
  },

  "trigger": {
    "mode": "occasionally",
    "mtbDays": 6,
    "allowedTimeOfDay": [4, 20]
  },

  "target": {
    "pawnKinds": ["Colonist"],
    "excludeIfHediff": ["Frostbite"],
    "weightByTrait": { "Psychically sensitive": 2.0 },
    "cooldownDays": 15,
    "exclusionTags": ["supernatural-chill"]
  },

  "customHediffs": [
    {
      "defName": "RTCE_FrostTouched",
      "label": "frost-touched",
      "description": "Something cold looked back.",
      "severityPerDay": -0.1,
      "stages": [
        { "minSeverity": 0.0, "statOffsets": { "ComfyTemperatureMin": -8 } }
      ]
    }
  ]
}
```

### CONTINUE modes

CONTINUE can carry the event forward three ways. BEGINNING and END are always spoken
lines; only CONTINUE has a mode.

| Mode | What it does |
|---|---|
| `prompt` (default) | A spoken line of its own, once per beat. |
| `modifier` | No lines of its own — the text is folded into *every* prompt the pawn generates while the event runs, so it colours whatever they were already talking about. "Your skin is cold." |
| `beat` | Discrete pulses whose **intensity** climbs from `intensity.from` to `intensity.to`. Effects marked `scaleWithIntensity` scale with it, so the mechanical bite grows alongside the wording. "Your skin grows colder", and the temperature offset deepens. |

`{intensity}` in the text renders as a word — faintly / noticeably / strongly /
overwhelmingly — because a number means nothing to a model writing prose.

Modifier mode is delivered through `RimTalkPromptAPI.InjectPawnSection`, the ambient
channel originally deferred from v1. The active set is rebuilt from live instances
each tick rather than registered and unregistered as events start and stop: one hook,
no lifecycle to get wrong, and it restores itself after a save is loaded.

**Three texts, and the code handles the rest.** The author writes one BEGINNING, one
CONTINUE and one END, exactly as in the original prompt. The scheduler decides how
many CONTINUE beats there are and when each lands; CONTINUE is replayed for each
one. Omitting `continueCount` derives it from the duration — roughly one beat every
four hours, so 12h gives three. Omitting `phases.continue` entirely gives a
two-beat event that goes straight from BEGINNING to END.

Pronouns are they/them because an event can land on any pawn. If you want
per-gender phrasings later, add a `variants` block keyed by gender rather than
writing gendered text into the base fields.

**Single-pawn only.** Every event targets exactly one pawn. Multi-pawn events
(shared visions, mutual arguments) would need per-role phase text and cross-pawn
beat coordination — deliberately out of scope; the JSON shape leaves room to add a
`roles` block later without breaking existing files.

---

## 4. Phase state machine

```
Pending ──(trigger fires, pawn ready)──▶ Beginning
Beginning ──(beat spoken)──▶ Continuing
Continuing ──(beat spoken, n < continueCount)──▶ Continuing
Continuing ──(n == continueCount, or duration elapsed)──▶ Ending
Ending ──(beat spoken)──▶ Resolving ──▶ Complete   [apply outcome]
         │
         └──(pawn dead / downed / left map / cancelled)──▶ Aborted   [no outcome]
```

**Beat delivery (the ~20s expiry problem).** Each tick the scheduler walks active
instances. For any instance whose next beat is due:

1. `Cache.Get(pawn)` — if null, the pawn isn't RimTalk-tracked → pause.
2. `CanGenerateTalk()` — if false, **do not inject**; leave the beat due and retry
   next check. Rate-limit checks to ~1/sec of game time.
3. If ready: `AddTalkRequest(BuildPrompt(phase), null, TalkType.Event)`, mark the
   beat *injected* and record the tick.
4. Watch for the request leaving `PawnState.TalkRequests`. Left via
   `MarkRequestSpoken` → beat landed, advance. Expired → re-arm and retry.
5. `beatTimeoutHours` (default 2 in-game hours) — after that many failed retries,
   apply `onBlocked`: `retry` (default) | `skip` | `abort`.

**CONTINUE spacing.** `continueSpacing` picks the rhythm:

- `"even"` (default) — `continueCount` beats spread evenly across `durationHours`,
  nudged by `continueJitter`. Predictable escalation.
- `"random"` — beat times drawn at random across the window. Less metronomic, good
  for events that should feel like they're creeping up unpredictably.

END always lands at the end. If the pawn is unavailable for a long stretch, beats
compress rather than pile up — cap at one beat per `minBeatGapHours` (default 0.5).

---

## 5. Triggers

Rolled once per in-game hour, whether or not anything is running.

| Mode | Mechanism |
|---|---|
| `daily` | Fires at `dailyHour`, once per day, subject to `dailyChance` |
| `occasionally` | `Rand.MTBEventOccurs` on `mtbDays` |
| `manual` | Dev gizmo, or Test fire in mod options |

**This mod never touches RimWorld's storyteller.** Decided outright rather than left
open: the mod *is* its own storyteller for narrative beats, pacing itself with
`mtbDays`, `minRefireDays`, per-pawn cooldowns and concurrency caps. It does not
register `IncidentDef`s, enter the incident pool, or draw on the threat budget.

The one apparent exception is `StorytellerUtility.DefaultParmsNow`, used by the
`incident` effect. That call only *computes* parameters — target map, and threat
points from colony wealth and difficulty. It does not register with the storyteller
or consume its budget; the mod still decides entirely on its own when to fire.
Building parms by hand would leave points at zero and break any incident that scales
with them.

A file still saying `"mode": "storyteller"` loads as `occasionally` rather than
silently falling back to `manual` and never firing.

`allowedTimeOfDay` narrows the start window and may wrap midnight (`[22, 4]`).
`minRefireDays` throttles an event globally; `target.cooldownDays` throttles it per
pawn. The frequency slider in mod settings scales all of it, and 0 disables firing.

A roll that comes up but can't start — nobody matches, or a limit blocks it — is not
recorded as a fire, so it simply retries next hour.

**Targeting** (`target` block), implemented for v1: category eligibility from mod
settings (colonists / prisoners / slaves / guests / animals), `pawnKinds` (broad
keywords or a PawnKindDef name), gender, age range, required and excluded traits,
required and excluded hediffs, and `weightByTrait` for a weighted pick. The pawn must
also be tracked by RimTalk, or its beats could never be delivered.

Richer conditions — mood, current job, backstory, relationships, indoors/outdoors —
can layer into `PawnSelector.IsEligible` later without touching the call site.

**Concurrency guards:** global max active events, max per pawn (default 1),
`exclusionTags` so two "supernatural chill" events can't stack, and per-event
`cooldownDays` per pawn.

---

## 6. Per-phase effects

**Every phase can have mechanical consequences, not just END.** BEGINNING plants
something, each CONTINUE beat escalates it, and END pays it off. A phase's `effects`
list runs when that beat is delivered.

Effect kinds:

- `hediff` — `{ def, severity, bodyPart?, chance? }`. On a CONTINUE beat, severity
  is *added* to an existing hediff rather than replacing it, which is how escalation
  works: three beats of `+0.2` leave the pawn at 0.6 by END.
- `thought` — a memory `ThoughtDef`: the mood change.
- `need` — offsets a need directly (mood, rest, joy, comfort…). This is the
  colonist status-bar movement.
- `incident` — fires a real `IncidentDef` on the map. This is the "massive event at
  the end": FROST's END can set off a cold snap.
- `chainEvent` — starts another custom event, so events can cascade.
- `trait` — add or remove, with degree.
- `skillXp` — `{ skill, amount }`.
- `items` — ThingDefs and counts, spawned near the pawn: the reward.
- `relationship` — opinion or relation change toward another pawn.
- `message` — a letter or mote so the player notices.

Each effect takes an optional `chance` (0-1). A phase's effects can instead be a
**weighted table** via `oneOf`, to pick exactly one arm — that's how an END fizzles
70% of the time and pays off 30%, which is what the old event-level `outcome` block
did. `outcome` at the top level still parses and is treated as END's effects.

### Custom hediffs

Two levels, both supported:

1. **Reference an existing HediffDef** by `defName` — vanilla or from any mod.
2. **Define one inline** in the event file (`customHediffs`). At load the mod
   builds a real `HediffDef` at runtime and registers it in `DefDatabase`:
   label, description, `stages` with `statOffsets`/`capMods`/`painOffset`,
   `severityPerDay` (via `HediffComp_SeverityPerDay`), `disappearsAfterDays`
   (via `HediffComp_Disappears`), `isBad`, `tendable`, `maxSeverity`.

Inline hediffs are namespaced (`RTCE_` prefix enforced) to avoid colliding with
other mods. This is what makes an event self-contained: one file carries the
prompts *and* the mechanical consequence.

### The def finder — reaching modded content

Effects reference defs by name, and in a heavy load order the interesting ones come
from other mods. The finder enumerates everything actually loaded in *this*
playthrough, so an event can use any of it:

| Effect field | Source |
|---|---|
| `hediff.def` | `DefDatabase<HediffDef>.AllDefs` |
| `thought` | `DefDatabase<ThoughtDef>.AllDefs` (memories only) |
| `incident` | `DefDatabase<IncidentDef>.AllDefs` |
| `items[].def` | `DefDatabase<ThingDef>.AllDefs` |
| `trait.def` | `DefDatabase<TraitDef>.AllDefs` |
| `need` | `DefDatabase<NeedDef>.AllDefs` |
| `skillXp.skill` | `DefDatabase<SkillDef>.AllDefs` |

This is read at runtime, not baked in, so a mod added later simply shows up. Each
entry carries `def.modContentPack.Name`, so the picker can group and filter by
source mod — "show me only hediffs from Vanilla Races Expanded" — and label
collisions between mods stay distinguishable.

Two consumers:

- **Authoring.** The options-menu editor uses a searchable picker per field:
  type-ahead over label *and* defName, grouped by mod. This is how you discover
  what a heavily modded save actually offers, rather than knowing defNames by heart.
- **Validation.** On load, every def an event references is resolved. A name that
  isn't loaded becomes a warning naming the field and the event — not a silent
  no-op at the moment the effect fires, and not a hard failure either, so an event
  file shared between two different load orders still works for the parts that
  resolve.

---

## 7. Prompt integration

`AddTalkRequest(prompt, null, TalkType.Event)`. The prompt is wrapped so the LLM
keeps continuity across phases:

```
[EVENT: frost — CONTINUE 2/3]
They feel subtly colder and colder as the day progresses; ...
```

The wrapper is a configurable template in mod settings. RimTalk already feeds
recent talk history to the model, so the CONTINUE and END beats land with the
earlier lines in context — that's what makes "continue event X" work without
re-sending the whole story each time.

### Deferred: ambient context

`RimTalkPromptAPI.InjectPawnSection` would let an active event tint the pawn's
*ordinary* chitchat too — mentioning the cold while arguing about dinner. Cut from
v1 to keep prompt token cost predictable. Adding it later is additive: an
`ambientContext` string on the event plus register/unregister on instance
start/stop. No rework of anything else.

---

## 8. UI

- **Mod Options — Events tab.** Every loaded event with its beat shape, duration,
  trigger and effect count; load problems in red; New event, Open folder, Reload from
  disk, and Edit / Test fire / Delete per event. Delete asks first and names the file.
- **Event editor.** Edits a working copy — cancelling changes nothing on disk, and a
  half-finished edit can't be picked up by the scheduler mid-event. Covers identity,
  all three phase texts, per-phase effects, timing, trigger and the main target
  filters. Saving writes the JSON, then reloads from disk so what's running matches
  the file.

  Two honest limits: a `oneOf` weighted table shows as a summary and is edited in the
  file, and **saving discards comments**, because serialising goes through the typed
  model. The editor warns first, and the original is copied to `<name>.json.bak` so
  hand-written notes are never actually lost.
- **Def pickers.** Every def field opens a searchable list of what this playthrough
  actually loaded, filterable by source mod. This is the finder (§6) doing its job.
- **Prompt preview.** Shows the exact text RimTalk receives, wrapper applied. Built
  through the same `PromptBuilder` the scheduler uses, so it cannot drift — a preview
  showing something other than what the model gets is worse than none.
- **Active events window.** Live phase, elapsed vs total hours, beats done, and when a
  beat is stuck, the specific reason. Plus Go to and Cancel.
- **Diagnostics window.** One pass over everything that makes an event silent: master
  toggle, frequency at zero, no pawn categories enabled, RimTalk tracking nobody,
  monologues disabled, load failures, missing def references, and **eligible pawn count
  per event on this map**. Silence has many causes that look identical from outside;
  checking them one at a time costs a restart each.
- **Mod Options — Settings tab.** Done: master toggle, global frequency multiplier,
  concurrency caps, eligibility (prisoners / slaves / guests / animals), prompt
  wrapper template, verbose logging.
- **Active events view.** Live instances: pawn, event, phase, next beat ETA, and
  *why* a beat is blocked; Advance / Cancel. Starts as a dev-mode window, promoted
  to a proper tab if it earns it.
- **Dev gizmos.** Under dev mode, "Trigger custom event ▸" on any pawn.

---

## 9. Persistence

A `GameComponent` (`CustomEventsGameComponent`) holds active instances and
`ExposeData`s them: event defName, pawn reference (`Scribe_References`), phase,
tick markers, beat index, RNG seed, whether the outcome was already rolled. On
load, drop instances whose event file or pawn no longer exists. Per-pawn cooldowns
persist alongside.

Event *definitions* live in the config dir, not the save — so a save loads against
whatever events currently exist, and a missing one just ends that instance.

---

## 10. Build setup

- SDK-style `.csproj`, `TargetFramework=net472`, `LangVersion=latest`.
- `Krafs.Rimworld.Ref` + `Lib.Harmony.Ref` NuGet packages for CI-friendly refs.
- `RimTalk.dll` referenced from the workshop path with local-Mods fallback,
  `<Private>false</Private>` — copied from `RimTalkQuests.csproj`.
- Output to `1.6/Assemblies/`. `build.ps1` wrapper.
- Harmony only if needed — the public API may cover everything, which would make
  this patch-free and far more update-proof.

---

## 11. Resolved decisions

| Decision | Choice |
|---|---|
| Authoring surface | **Mod Options menu**, backed by one JSON file per event in the config dir. No XML Defs. |
| v1 scope | **Core loop first** — FROST plays BEGINNING → CONTINUE → END via dev trigger. |
| Ambient context | **Deferred** past v1 (§7). |
| Multi-pawn events | **Single-pawn only**; JSON leaves room for a `roles` block later. |
| Blocked-beat policy | Default `retry`, 2 in-game-hour timeout, then per-event `onBlocked`. |

---

## 12. Implementation order

**v1 — the core loop:**

| Phase | Deliverable | State |
|---|---|---|
| 1 | Skeleton: About.xml, csproj, build.ps1, mod class, settings | written, compiles |
| 2 | JSON event store: parser, model, loader, validation, config-dir bootstrap, FROST starter | written, parser and schema verified against the real FROST file outside the game |
| 3 | Scheduler GameComponent, instance state machine, beat injection with the readiness gate | written, compiles |
| 4 | Dev-mode trigger gizmo | written, compiles |

**Outstanding:** none of it has run inside RimWorld yet. The check that matters is
FROST playing BEGINNING → CONTINUE ×3 → END on a real colonist, and surviving a
save/load mid-event.

**v2 — the rest:**

| Phase | Deliverable |
|---|---|
| A | Per-phase effects: hediff (accumulating), thought, need, `oneOf` tables, per-effect `chance`, `continueSpacing` | **done** |
| B | Runtime HediffDef factory for inline `customHediffs` | **done** |
| C | World effects: incident, chainEvent, items, trait, skillXp, message | **done** |
| D | Def finder: runtime enumeration of all loaded defs + reference validation on load | **done** |
| E | Triggers (daily, occasionally) + pawn targeting | **done** — no storyteller coupling, see §5 |
| F | Mod Options: Events + Settings tabs, browser with Test fire and Reload | **done** |
| G | JSON writer, event editor, def pickers, New/Edit/Delete | **done** — writer round-trip verified on 11 shapes |
| H | Active-events window: live phase, next beat ETA, and why a beat is blocked | **done** |
| I | Storyteller coupling removed outright (§5); git repo initialised | **done** |
| — | **Run it in RimWorld** | the only thing left before v1 is real |
| J | Prompt preview, active-events window, diagnostics report, .bak on comment-losing save | **done** |
| K | CONTINUE modes (prompt / modifier / beat), intensity ramp, dropdown-driven Events tab | **done** |
| — | Comment-preserving save, and `oneOf` editing in the UI | |
| — | Translations | |

102 checks now run outside the game, covering the parser, the weighted-table roll, every
authoring form, and a full write → reparse → compare round trip.

### Second audit (editor, writer, modifiers)

Four more real bugs, all fixed:

- **The editor mutated the live event.** It held the same `CustomEvent` the store hands
  the scheduler, so Cancel undid nothing and a running instance — which re-reads its
  definition every tick — could pick up half-typed text mid-beat. It now edits a deep
  copy, cloned through the writer and parser so it stays faithful as the schema grows.
- **Modifier text could stay glued to a pawn's prompts forever.** Switching the mod off
  mid-event returned from the tick before the sync ran, leaving the injected text with
  nothing left running to clear it; loading a second save inherited the first one's
  modifiers, since the registry is static. Both now clear.
- **Renaming an event left it in the old filename**, so the folder stopped matching the
  events in it. Save now writes the new name and removes the old file afterwards.
- The Events tab described a Modifier event as "CONTINUE ×3" when it has no beats.

### First audit (scheduler, effects)

Five findings; three were real bugs, now fixed:

- `RimTalkBridge.GetStatus` treated "request found in neither the queue nor history"
  as **delivered**. RimTalk's `Cache.Refresh` evicts a pawn's whole `PawnState` when
  they stop being talk-eligible — anaesthetised for surgery, downed, despawned into a
  caravan — destroying queued requests without recording an outcome. Effects would
  then fire and the phase advance for a beat nobody heard. Now treated as lost and
  retried.
- The `0.001` severity floor made hediffs unremovable, so a negative-severity effect
  could never lift a curse the event applied. Severity reaching zero now removes.
- The blocked-beat timeout clock started during the deliberate `minBeatGapHours`
  wait, so a long gap plus `onBlocked: skip` dropped beats that were never blocked.
- Whole-pawn hediff effects could deepen a *part-specific* injury; matching is now
  exact on body part.
- Reloading events mutates a live `HediffDef`; pawns already carrying it keep the old
  comps. Can't be fixed without a save reload, so it now warns when it matters.

All of A–D compiles, and 37 checks pass against the parser, the weighted-table roll
and every authoring form. **None of it has run inside RimWorld yet** — that remains
the outstanding check, and it's where the hediff factory and incident firing are most
likely to need a round of debugging.

### Chain loop protection

`chainEvent` doesn't start the next event inline. An END beat's effects run while that
instance is still active, so an inline start would trip the per-pawn concurrency limit
against the very event that's finishing. Chains are queued and started on a later
tick, through the *normal* limits — which is also what stops A → B → A running away.
