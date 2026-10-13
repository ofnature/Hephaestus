# Variant: Workbench (dense)

## Design stance
Same fire shell, but tuned for the multibox-first power user: the queue is a compact table
(Argus's `table-layout` style) instead of cards, so 7 items fit where the card variant shows 5.
Three stat tiles instead of four; current job moves to an inline chip. Everything tighter.

## Key choices
- **Queue**: single-line table rows with an inline minibar, state shown by a 3px left-edge tint
  (gold = current, green = done, amber = flagged) + row wash.
- **Stats**: 3 tiles (In queue / Crafted / Short) at 50px; current job is a small chip in the
  control row.
- **Typography**: labels 10px, values 15px — one notch tighter than the Forge shell.

## Trade-offs
- Strong at: rows per screen, scanning a long queue, running on several clients at once.
- Weak at: each item has less room to breathe — the flagged reason is a second line inside the cell,
  and the per-item progress is a minibar rather than a full bar.

## Best for
A big queue left to run unattended across boxes; less ceremony, more list.

## Width note
Same 860×640 shell as the Forge variant (Hephaestus's window is 560×480 today) — the sidebar
growth is a shared decision across all three.
