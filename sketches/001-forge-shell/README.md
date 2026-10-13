# Variant: Forge shell

## Design stance
The direct read of the ask — Hephaestus's real content restyled into Argus's full layout
language, with Hephaestus's gold "forge" identity swapped in for Argus's teal. The queue
becomes per-item cards (Argus's vessel-row shape) with a 3px state stripe.

## Key choices
- **Layout**: 160px sidebar (CRAFTING / SOURCING / SYSTEM groups) + 88px identity header
  (gradient tinted gold, anvil mark, wordmark, subtitle "CRAFTING FORGE", status pill, detail
  line, cog) + page header (title / muted subtitle / hairline) + 4-stat tile strip.
- **Accent**: gold `#D9A633` (Hephaestus) replaces teal; surfaces keep Argus's dark ramp.
- **Queue**: card per item — accent stripe (gold = current, green = done, amber = flagged),
  inline progress bar, actions in the top-right.
- **Typography**: page title 19px / wordmark 21px / labels 10.5px caps / stat value 16px.

## Trade-offs
- Strong at: identity match, scannability, showing queue state at a glance.
- Weak at: row density — 5 items fill the fold; a long queue needs scrolling.

## Best for
The canonical Hephaestus look — the one that reads unmistakably "Hephaestus, but Argus-shaped".

## Width note
Argus's shell assumes a 160px sidebar + 88px header. Hephaestus's window is currently
**560×480** (tab bar, no sidebar); this mock is drawn at Argus's **860×640**. Adopting the
sidebar grows the window ~300px wide — that growth is a decision, not free.
