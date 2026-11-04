# Count 3 — Member can't log out when the operator logged them in from the counter

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** Branch server + gaming PC screen (overlay page served by the branch; no gaming PC exe change)

## What the owner said
If the operator logs a member in from the operator PC, the member can't log out by themselves.
They have to ask the operator to close it.

## Why (from the code)
- The **Logout** button on the gaming PC sends the member's own login ticket (`memberToken`).
- That ticket is saved on the gaming PC only when the **member logs in on that PC**.
- When the operator starts the member's session from the counter, the gaming PC never gets that ticket.
  The Logout button is still shown, but pressing it fails with "Not logged in", so the operator must end it.
  (`SessionInfoScreen.jsx` → `OverlaySocketContext.memberCheckout` → `/public/sessions/{id}/member-checkout` [Authorize]).

## Proposed fix
- A new logout path for **the PC the session is running on**: the gaming PC sends its PC id + the session id
  (the same way "PC was switched off" already ends a session), and the server ends that member session exactly like a normal
  member logout: wallet billing, gaming/food wallet split, and the bill on the operator's shift (Count 1).
- The gaming PC screen uses it when it has no member ticket. A member who logged in on the PC keeps the existing path.
- Safety: it only works for the session that is running **on that same PC**, and only for a **member** session
  (a walk-in still pays at the counter).

## Not changed
Member login itself (do not touch).

## Built (4 Nov 2026)
- `PublicController`: new `POST /api/public/pcs/{pcId}/sessions/{sessionId}/member-logout`. It only works for a member session that is active or interrupted
  **on that PC**. It shares one billing routine (`CheckoutMemberSessionAsync`) with the existing token checkout.
  If the hidden System Administrator account doesn't exist, it records the session's operator instead (this was found in testing).
- `OverlaySocketContext.memberCheckout`: the gaming PC's Logout always uses the PC path. A stale or missing member token can't block it.
  A 401 would have sent the overlay to the staff login page, so the token path is not used on the PC.
  The member portal (phone) keeps the token path.

## Test (scratch DB)
- Counter-started member session, 1 hour, Logout from the PC with no token: billed ₹60 from the gaming wallet, on the operator's shift, PC idle. PASS
- Wrong PC id, walk-in session, Logout pressed twice: all refused. PASS
