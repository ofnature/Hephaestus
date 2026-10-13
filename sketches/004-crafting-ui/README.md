# Variant: Crafting-time UI

## Design stance
The surfaces that matter *while* crafting — not the main window. Two Hephaestus panels restyled
into the fire/Argus language, drawn in their game context so the docking and the game-matched
button read honestly.

## What is shown
- **Crafting-log dock** (`Hephaestus — Crafting`): compact 34px fire header (flame + wordmark +
  "CRAFTING"), recipe name, Mode combo, Consumables / Raphael-baseline settings groups, and a
  Craft section with **Craft All** (primary) + **Add to queue** (ghost). Uses Argus's settings-row
  shape (label left / control right / hairline).
- **Assist overlay** (`Hephaestus — Next action`): 48px next-action tile + name + "on plan" pill
  (green) / "off plan" (yellow), a dimmed preview row, the status line, and a hotbar mock with the
  pulsing gold slot.
- **Craft All overlay** (`##HephaestusCraftAllOverlay`): shown *in the game's log*, directly above
  Synthesize — `+ Queue | count | Craft All` at the game button's own size.

## Trade-offs / caveats
- The **Craft All overlay is deliberately game-matched** (frameless, `NoDecoration`,
  `NoBackground`, sized to the Synthesize button, repositioned every frame). It should NOT get a
  banner or a card — its whole job is to look native to the game window. The only identity it
  carries is the gold button and the `+ Queue` ghost. Restyling it beyond that risks breaking the
  "sits over Synthesize at the same width" geometry.
- The Assist overlay is `AlwaysAutoResize` and small — the fire identity is compressed to a 34px
  header, a flame mark, and accent stripes/pills, not the 88px banner of the main window.

## Width note
Both Hephaestus panels here are *narrower than* the main window mockups (290px dock, 300px
assist) — they are floating docks that sit beside a game window, so width is a bigger cost than on
the main window. The dock's 290px vs the assist's 300px are illustrative; the real constraint is
the game window they ride.
