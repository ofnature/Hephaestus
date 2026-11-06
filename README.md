# Hephaestus

A crafting plugin for FFXIV (Dalamud) built around a **live, adaptive solver** instead of a static
precomputed macro.

- **Hybrid solving.** A [Raphael](https://github.com/KonaeAkira/raphael-rs) rotation is the
  baseline; after every action the plugin re-scores the legal next actions against the real craft
  state — progress, quality, durability, CP, buffs, and the condition that actually rolled — and
  deviates whenever that beats the plan.
- **Auto or Assisted.** Let the plugin press the actions, or have it show you the next one. Set a
  global default and override it per character.
- **Craft queues.** Lists of items and quantities, made unattended across crafting jobs, with
  progress that survives reloads and restarts.
- **Material sourcing.** Inventory, FC chest, retainers, sub-crafts, then vendors — buying only
  what the queue is actually short.
- **Multibox-first.** Designed to run on several clients at once with as little hands-on
  interaction as possible.

## Status

Early. Working: settings, per-character queue editing and persistence, and a window that docks to
the crafting log with per-recipe options and **Craft All / Craft X** (never more than your
materials and crystals make — the button names the max) — Hephaestus starts each
craft, verifies the item arrived, retries failures up to a cap, pauses when gear needs repair, and
resumes where it stopped. Pressed on a recipe for another job, it changes to that job first (from
your saved gearset). During each craft the solver runs: a Raphael baseline (bundled
`raphael-cli`, cached on disk) re-evaluated against the real state after every step; in Auto
mode it presses the actions, in Assisted mode an overlay beside the Synthesis window shows the
next action and a tentative preview, and the action's slot pulses gold on your own hotbars. The simulator and evaluator are verified against Raphael's
known outcomes and measured against a fixed rotation under the same dice.

The queue works through a list of items unattended, changing job between them. An item it
cannot make (out of materials, no gearset, will not start, keeps failing) is flagged and
skipped; anything that needs you (gear repair, a cancelled craft) pauses the queue where it
is. Intermediates you are short of and can craft (ingots before the shield) are made first,
automatically — deepest first, on whichever job makes them, and only for crafts your materials can
see through to the end; the Queue page shows that plan — with a priced vendor shopping list for
whatever is still short — before you press Start. **Make buy list** / **Make gather list** turn
that into the Buy List and Gather List pages (needed vs in your bags), fetched by hand or with
**Auto purchase** (walks to NPC vendors, teleporting when none is near) and **Auto gather** (asks
Demeter). Per character, the queue can also do this by itself when it runs short — on by default
for free-trial accounts, off for paid ones. The market board is never automated. On paid accounts
the queue also draws on your company chest and your retainers: it learns what they hold (from
Charon's store when that plugin is loaded, else from any chest or retainer you open), takes whole
stacks from the chest at the estate and withdraws at a summoning bell before crafting. Raphael baselines for the queue's coming crafts are
solved in the background between syntheses, so the first craft of a recipe does not wait. Add to it from the crafting log ("+ Queue" above Synthesize), from the right-click menu of any
craftable item (`[H] Add to Hephaestus Queue`), or from another plugin over IPC. Not done yet:
auto-repair, food upkeep and materia extraction.

`/hephaestus` (or `/heph`) opens the window.

The window uses an Argus-style shell: a sidebar, a fire banner, and an **Overview** landing page
(live status, current craft, what is next, what is short). The landing page is configurable in
Settings → Window.

## Building

Requires the .NET 10 SDK and a Dalamud dev install (XIVLauncher).

```
dotnet build Hephaestus.sln -c Release
dotnet test Hephaestus.sln
```

The Raphael baseline solver is bundled as `raphael-cli`, built from source (raphael-rs publishes
no CLI binary). With Rust and the MSVC C++ build tools installed:

```
powershell -File tools/build-raphael-cli.ps1
```

builds the pinned raphael-rs tag and stages it so the next `dotnet build` packs it into the zip.
A build without it still succeeds; the plugin then reports the baseline solver as unavailable.

## Acknowledgements

- The craft simulator's arithmetic, potencies and effect ordering follow
  [raphael-rs](https://github.com/KonaeAkira/raphael-rs) (Apache-2.0) by KonaeAkira, and its test
  vectors are ported from raphael-sim's test suite.
- Expert-condition behaviour, success rates and the measured per-recipe condition odds follow
  [Artisan](https://github.com/PunishXIV/Artisan) by PunishXIV, which is also where the crafting-log
  interactions were studied.

## For other plugins

| IPC gate | Returns | Meaning |
|---|---|---|
| `Hephaestus.IsBusy` | `bool` | True while Hephaestus is driving the character (queue, craft run, a trip or gathering it asked for, a synthesis it presses in Auto), or still handing it back after `RequestStopAfterRun`. |
| `Hephaestus.CraftItem(uint recipeId, int amount)` | `bool` | Make `amount` of a recipe: changes job, solves, crafts, verifies. Same shape as `Artisan.CraftItem`. True = it began (`IsBusy` is already true); false = nothing started. |
| `Hephaestus.CraftList(string json)` | `bool` | `CraftItem` for a whole list you build: `{ "keepOrder": true, "items": [ { "recipeId": 35499, "quantity": 12 }, { "itemId": 5057 } ] }`. Entries name a `recipeId` or an `itemId`; `quantity` defaults to 1, `keepOrder` to true. One-off and unsaved. All or nothing: one bad entry and nothing starts. |
| `Hephaestus.AddToQueue(string json)` | `bool` | Same format, appended to the player's saved queue; nothing is started. |
| `Hephaestus.StartQueue` | `bool` | Start or resume the player's saved queue. |
| `Hephaestus.Stop` | — | Stop whatever is running. Safe when idle. |
| `Hephaestus.RequestStopAfterRun` | `bool` | Finish the synthesis in progress, then start nothing more and close the crafting log; `IsBusy` falls once the character is out of the crafting stance. Trips stop at once. True = was busy and will stop (or already is); false = idle, nothing changed. Idempotent; a fresh start clears it. |
| `Hephaestus.GetQueueStatusJson` | `string` | Active / canResume / status / current item / flagged items. `"{}"` on error. |
| `Hephaestus.GetVersion` | `string` | Plugin version. |

Gates are extend-only: a published gate never changes its signature or meaning.
