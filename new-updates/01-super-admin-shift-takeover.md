# Count 1 — "Super Admin took the shift" at the counter

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** Branch exe (and HQ, so both behave the same)

## What the owner said
An operator's shift is open. The Super Admin looks at the branch from his own device, then closes it.
Later the cash register shows that Super Admin has taken the shift. Super Admin should never take a
shift. Staff then have to close it and reopen, every time.

## What is really happening (checked in HQ data)
- Whenever the **Super Admin** does anything at a branch, the system needs a shift to attach the work
  to. The same happens when a **member logs out on a gaming PC** (their bill is made automatically).
  So it opens a hidden shift called **"System Administrator"**
  (`ControllerExtensions.GetShiftIdAsync`).
- That hidden shift is never closed. It just stays "Active".
- After 2 hours with no activity, the morning operator's login sees an open shift that isn't theirs.
  It treats it as an abandoned real shift and forces a **takeover**: "count the drawer"
  (`ShiftTakeoverService.FindAbandonedAsync` does not skip System Administrator).
- **Proof:** in the last 15 days there were 79 shift takeovers, and **77 of them were closing a
  "System Administrator" shift**. It happens almost every morning at every branch, around 09:15–09:45.
- Last 10 days: 580 bills were put on System Administrator shifts. 576 are member wallet bills (no
  cash). 4 are walk-in bills, ₹40 cash and ₹210 online, which went to the hidden shift and not to the
  operator's drawer.

## Owner asked: why does this hidden shift exist? Is it useful?
No, it does nothing useful for the business. It is a technical placeholder: every bill, session and
wallet entry has a "which shift" field, and when a member or the Super Admin does something there
is no operator, so the code invents one. Bills, sessions and wallet entries can already be saved with
no shift, and "who did it" stays recorded through the operator name and the audit log.

**Owner's direction (under discussion):** remove the hidden shift instead of ignoring it.

## Proposed fix (v2: remove the hidden shift)
1. Stop creating the "System Administrator" shift at all. Member and Super Admin actions go on the
   operator shift that is open at that counter. If no operator shift is open, they are saved with no
   shift. "Done by Super Admin / member" is still shown in the audit log.
2. One-time cleanup: close the System Administrator shifts that are still open at every branch, so
   tomorrow morning has no takeover.
3. Takeover check: never treat System Administrator as an operator (a safety net for old data).

## Earlier proposal (v1, replaced)
1. The takeover check ignores the hidden System Administrator shift. It is never shown or asked for
   as "someone's shift". This is the main fix, and it stops the morning takeovers.
2. Money taken by the Super Admin or a member at a branch goes to the shift that is **open right
   now** at that counter (the operator's), so the cash register matches. The hidden shift is used
   only when no operator shift is open.
3. The cash register and shift lists never show "System Administrator" as an operator shift.

## Not changed
- Member login (do not touch)
- The cash register rule that Super Admin can't open, count or close a drawer

## Ships with
The combined release (all counts together), not by itself.

## What was built (4 Nov 2026, branch `phase3-user-exe`, not committed yet)
- `ControllerExtensions.GetShiftIdAsync`: Super Admin, Admin and member actions use the operator shift that is open at that branch. The placeholder is used only when nobody is on shift.
- `PublicController.MemberCheckout`: the member's logout payment uses the same rule.
- `ShiftTakeoverService.FindAbandonedAsync`: the placeholder is never offered for takeover or counting.
- `AuthService.LoginOperatorAsync`: an operator login closes any open placeholder shift at that branch. There's nothing to count.
- `BranchHeartbeatService`: the on-duty list sent to HQ leaves out the placeholder. HQ commands (start session, stock, maintenance) prefer the real operator.
- `SyncInboxController` and `DashboardService`: the "operators on duty" counts leave out the placeholder.

Owner approved the "remove it" direction. The as-built version keeps the placeholder only as a fallback for
when nobody is on shift (cash and wallet records need a shift), and closes it automatically at the next login.

## Test (local API against a scratch copy of the DB; sync pointed at a dead address)
1. Operator on shift, member plays and logs out: the session and the bill land on **the operator's shift**. No placeholder is created. PASS
2. Nobody on shift, member plays: the placeholder is used. Three hours later an operator logs in: **no takeover**, the placeholder is closed, and the operator gets their own shift. PASS

## On release day
- The same code must also go to HQ (`main`), so both sides agree.
- The first operator login at each branch closes any open placeholder by itself. No manual cleanup is needed.
