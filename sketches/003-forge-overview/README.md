# Variant: Overview landing (picked)

## Design stance
Same fire shell, with an **Overview** page as the default landing — live status + big Start, stat
tiles, the current craft ("Now"), the next three items, and a sourcing summary — and the full
Queue as a secondary page.

## Decisions locked in
- **Option 3** selected: Overview landing + card queue.
- **Landing page is configurable** — Settings → Window → "Landing page" (Overview / Queue / Buy
  List / Gather List / Settings / Debug), default **Overview**. Maps to a new config field
  (`HephaestusConfig.LandingPage`, an enum; default Overview) read on open.
- **Window grows** from 560×480 to Argus's 860×640 to fit the 160px sidebar + 88px header.
- **Embers up** — 7 embers (was 5), some 4px, brighter glow. "A little more" as asked.
- **Crafting-time UI approved** as mocked in `004-crafting-ui`: dock + assist overlay restyled;
  the Craft All overlay stays game-matched.

## Trade-offs
- Strong at: a one-glance status for "is it working / what does it need from me".
- Weak at: the Overview partly duplicates the Queue page, so it only fully earns its place once
  there is live craft state to surface — which is why the landing page is now user-selectable.

## Width note
860×640 (grown from 560×480) — confirmed as the target.
