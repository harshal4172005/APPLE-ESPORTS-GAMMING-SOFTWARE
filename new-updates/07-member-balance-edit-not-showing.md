# Count 7 — Member balance edit doesn't show on the server screen or the customer's PC

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** HQ + Branch + gaming PC screen. Source: owner's PRD "Bug 3".
**Money: notification and display only, arithmetic not touched.**

## What the owner said
Changing a member's balance doesn't show in the server UI, or on the user's PC.

## Causes (checked 4 Nov)
1. The branch announces `MemberBalanceUpdated`, but **no screen listens to it** (confirmed: zero matches in `client/src`). Only the
   Members page's 15 s poll catches it. Wallet Desk, the session-start picker and the member panel stay stale.
2. The customer's PC is never told. It picks up a new balance only through its 10 s poll during a session, and not on the login
   screen or between sessions.
3. HQ shows "updated" at once, but its own number changes only after the branch applies it and syncs back. Failures are invisible.
4. Branch side uses `member.HomeBranchId ?? Guid.Empty` (confirmed, `BranchHeartbeatService` L782/L838). For a member with no home
   branch, the wallet entry gets an empty branch and the announcement goes to nobody. It should use this branch's own id.
5. HQ applies balance events last-one-wins, so an older event arriving late could roll a balance back.

## Proposed fix (PRD R3.1–R3.7)
- Every screen listens for the balance change and updates straight away (Members, Wallet Desk, session start, billing).
- The member's gaming PC gets the new balance pushed within a few seconds, and the low-balance warning clears.
  The idle/login screen re-reads the balance.
- HQ shows "Sending to branch… → Applied", or the failure reason.
- Use the branch's own id. Apply balance events in order (an older one can't overwrite a newer one).
- Top-up and bonus use the same announcement, so every balance change shows everywhere.
- Every path keeps its wallet Correction row (audit trail).

## Acceptance (PRD)
- A branch edit shows in < 2 s with no refresh.
- A playing member's PC updates in < 3 s.
- HQ goes Queued → Applied, and then HQ matches the branch.
- A member not yet at the branch shows "Waiting for member to reach the branch".
- Two quick edits end with the same final value on both sides.

## Built (4 Nov 2026)
- Branch uses its own id for HQ balance edits (not `HomeBranchId ?? Empty`).
- Top-up, bonus and wallet deduction now announce the balance change too (after commit; a failed push never fails the money).
- The announcement also goes to the member's **gaming PC** if they are playing.
- Screens listen: Members page, Wallet Desk, Start Session member picker, Payment popup (`hooks/useMemberBalanceUpdates.js`).
  Gaming PC: in-session balance updates at once; the "choose time" screen re-reads the balance every 15 s (plain fetch, so an expired
  member token can't throw the PC to the staff login).
- HQ balance edit popup: "Sending to branch…", then "Applied" or the reason.
- **Not done (deliberately):** an ordering guard on HQ's balance events. HQ's member row is also changed by other events, so a time-based
  guard could wrongly drop a real change. Risk > benefit; left as is.

## Test (live SignalR)
Admin edit to ₹1500: counter screens and the member's gaming PC both got 1500. Bonus ₹50: both got 1550. PASS

## Owner's note (4 Nov 2026)
Only Super Admin changes a member's balance value. It is just the value: nothing happens with billing.
- Confirmed: the edit writes the new value plus a "Correction" wallet entry for the record. EOD counts only Recharge, so no money moves.
  Count 7 changed only the display refresh.
- Open: an Admin with the `member_value_edit` permission can also edit today. Make it Super Admin only? (asked)

## Owner's requirement (4 Nov 2026)
"A member has 4000; Super Admin changes it to 3000. It should be done on the member's account and show on the member's gaming PC."

### Gap found and fixed (part of count 7)
A Super Admin edit did **not** stamp `BalanceAsOf`. HQ and the other branches only accept a balance that is *newer* than their own, so
the edit reached the home branch and stopped there. **Any other branch kept showing 4000**, on its counter and its gaming PCs.
- `MemberService.AdminEditValuesAsync`: stamps `BalanceAsOf` and sends `balanceAsOf` in `member.balance_adjusted`.
- HQ `ApplyMemberBalanceAdjustAsync`: applies the balance only if it is newer (`ApplyBalanceIfNewer`, the same rule as top-ups) and keeps its time,
  so the other branches take it on their next beat. An old branch build (no `balanceAsOf`) is applied as before.
  This also covers PRD R3.6 (an older event can't roll a newer balance back), which was skipped earlier.
- Other branches: when HQ's newer balance lands, they push it to their screens and the member's gaming PC.

### Test
Home branch: 4000 → 3000 done; Correction entry −1000 (4000 → 3000); `BalanceAsOf` stamped; the event to HQ carries `balanceAsOf`. PASS
Not tested end to end: HQ → a *second* branch (needs three servers). The HQ rule reused is the existing, live one for top-ups.

## Owner's answer (4 Nov 2026)
An Admin can edit a balance when Super Admin has given them the permission. That's the current behaviour: **no change.**
