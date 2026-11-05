# Count 16 — Show total in Apply Discount modal + UI enhancement

**Status:** Built (5 Nov 2026). Not shipped; waits for the next release.
**Where:** Branch + HQ — Sessions page, Apply Discount modal.

## What the owner asked
"Show total value on which we are giving the discount. And enhance the UI."

## What was built
- **Current Bill** box at the top of the modal showing ₹{totalAmount}.
- **"Customer pays"** live preview below the input: `₹{total - discount}`, shown in green.
- **UI polish:** rounded-lg on input, side-by-side Remove/Apply buttons instead of stacked,
  neon-orange accent on the apply button to match the discount theme.
- No backend changes.
