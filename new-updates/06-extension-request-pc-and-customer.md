# Count 6 — Walk-in extra-time request shows no PC or customer

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** Branch (PcOverlayHub + operator popup). Source: owner's PRD "Bug 2".

## What the owner said
A walk-in adds extra time from their PC. The operator side shows only a bill number. It should show **PC number + customer name**.

## Causes (checked 4 Nov)
- `PcOverlayHub.RequestExtension` sends only `{PcId, BranchId, Duration, SessionId}`. It never reads the session, so there's no customer
  name (confirmed in code). The popup falls back to "Current User", and shows a raw GUID if the PC lookup fails.
- The popup's "Expected Bill" is worked out on the screen as minutes × hourly rate. It doesn't use the server's package/minutes
  pricing, so the operator is shown a wrong figure. (Branch 3.1.134 already bills extensions by minutes played. The fix must use that rule.)
- The bill line is already named from the package ("Extension - 60m") in branch code (`SessionService` L1031), so PRD R2.4 is mostly
  done. Check what the owner actually saw.
- A request from a PC with no session sends SessionId = null, and Approve would post to `/sessions/null/extend`.

## Proposed fix (PRD R2.1–R2.6)
- The request carries PC number, PC name, customer (walk-in name / member name / "Walk-in"), bill number, minutes and a
  **server-priced** amount.
- Card title: **"PC-07 — Rahul"**, with the bill number as a small second line.
- One shared pricing function for the card and the real charge, so they can't differ.
- Refuse a request from a PC with no running session. One pending card per PC.

## Question for the owner
Where did you see "only a bill number": the **extension popup**, the **Billing Counter list**, or the **EOD log**?

## Owner's answer (4 Nov 2026)
It was seen on the **extra-time popup**. Develop approved.

## Built (4 Nov 2026)
- `SessionPricingCalculator.ExtensionPrice`: one pricing function, used by both the card and `ExtendSessionAsync`.
- `PcOverlayHub.RequestExtension`: reads the running session and sends PC number/name, customer (walk-in name / member name / "Walk-in"),
  bill number, minutes and the server price. Refuses when no session is running (the gaming PC shows the reason).
- Operator popup: title **"KAT-PC-01 — Rahul"**, bill number small underneath, the real price, durations like "45 Min" / "4 Hr".
  Bill line "Extension - 240m".

## Test (live SignalR against the test server)
Walk-in Rahul: card shows KAT-PC-01, Rahul, bill no., ₹180 for 4 h (the 4-hour package, not 4×60). After Approve the bill gained exactly ₹180. PASS
Member: card shows member name; 30 min = ₹30 pro-rata. PASS. Idle PC: refused. PASS
