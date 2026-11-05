# Count 17 — Change Package button UI enhancement

**Status:** Built (5 Nov 2026). Not shipped; waits for the next release.
**Where:** Branch + HQ — Sessions page, PC detail panel.

## What the owner asked
"Enhance UI of the Change Package button in the session — looks odd to me. Working stays as it is."

## What was built
- All action buttons (Stop, Credit, Extend, Change Package, Food, Discount) now sit in a single
  `grid-cols-2` grid instead of being split into separate rows.
- Change Package no longer wraps alone onto a second row — it sits beside Extend, same size.
- Food and Discount are full-width buttons in the same grid, uniform style.
- No logic or backend changes.
