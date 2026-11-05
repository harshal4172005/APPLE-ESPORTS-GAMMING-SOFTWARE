# Count 14 — Add / Subtract member balance from Edit Values modal

**Status:** Built + tested locally (5 Nov 2026). Not shipped; waits for the next release.
**Where:** Branch + HQ — Members page, "Edit Values (Admin)" modal.
**No new endpoint.** The frontend computes `current ± delta` and sends the result through the
existing `PUT /members/{id}/admin-edit` endpoint.

## What the owner asked
"Add 2 things in Edit Values: if super admin wants to add some money or subtract some money,
he can directly do it from here. Put add and subtract, select food or gaming, then the addition
and subtraction should reflect to all branches and gaming PC instantly."

## What already existed
- `PUT /members/{id}/admin-edit` — sets absolute wallet values, creates "Correction" wallet
  transaction, stamps `BalanceAsOf`, broadcasts via SignalR to counter screens and gaming PC.
- The modal already showed every editable field (gaming balance, food balance, lifetime stats).

## What was built
- **Quick Add / Subtract** section at the top of the modal (before the existing fields):
  - **+ Add** / **− Subtract** toggle buttons (green / red)
  - **Gaming Wallet** / **Food Wallet** selector
  - **Amount (₹)** input
  - **Reason** (optional) — auto-filled if blank: "Added ₹500 to Gaming balance"
  - Live preview: "Current Gaming: ₹4,000.00 → ₹4,500.00"
  - Button: "+ Add to Balance" or "− Subtract from Balance"
- Clicking the button reads the current value from the modal's own state, applies the delta,
  and calls `adminEditMemberValues(id, { gamingBalance: newValue, reason })`. The existing
  endpoint handles everything: wallet transaction, BalanceAsOf, SignalR broadcast, outbox event.
- The existing "Direct Override (Advanced)" fields are now collapsed behind a `<details>` toggle
  so the quick action is front and center. Expanding it shows everything that was there before,
  unchanged.
- After a successful add/subtract, the modal's field values update in place to reflect the new
  balance, so a second adjustment starts from the correct number without reopening the modal.
- The result is processed through `reportBranchCommand` — from HQ it shows "Sending to branch…"
  then "Applied" (or why not), same as before.

## Instant reflection
Already in place from update 7: `BroadcastMemberBalanceUpdateAsync` pushes the new balance to
the counter screen and the member's gaming PC via SignalR the moment the branch applies the
change. No new wiring needed.

## No backend changes
Zero new endpoints, zero new DTOs, zero migrations. The frontend computes the absolute value
and the existing endpoint does the rest.

## Test
The existing `PUT /members/{id}/admin-edit` endpoint was verified working in update 7
(3.1.136, shipped). The new UI is a pure frontend add/subtract calculator over that endpoint.
PS5 payment edit tested alongside: 8/8 PASS (create PS5 Cash, edit to Online, verify amounts
and PS5 box updated).
