# Count 9 — Upgrade/downgrade a running package (e.g. 4 hr → Full Day)

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** Branch (server + operator screen + gaming PC screen)

## What the owner said
A customer is on a 4 hr package and wants to move to Full Day. Give the operator controls, and let the gaming PC send an approval
request to the operator. Also downgrade (Full Day → something smaller).
Example: 4 hr running, 3 hr already played, upgrade approved → Full Day is 14 hr → 14 − 3 = **11 hr left**.

## Now
There's only "extend" (add minutes on top). There's no way to swap the package, so today it's 4 hr + 10 hr of extensions, priced
as two separate things.

## Real packages (HQ, 4 Nov)
Adajan-240Hz: 4 hr ₹200, Full Day 14 hr ₹700. Katargam 360Hz: 4 Hrs ₹300, Full Day 14 Hrs ₹900.

## Proposed
- **Change package** = the session now IS the new package from its start time. Price = the new package price; time = the new package
  length from when they started. 4 hr → Full Day after 3 hr: bill ₹700 (not ₹200 + ₹700), 11 hr left.
- **Downgrade** works the same way: Full Day → 4 hr after 3 hr played = ₹200 bill, 1 hr left.
  Refused if they have already played more than the smaller package (e.g. 5 hr played → can't go down to 4 hr).
- Gaming PC: a **"Change package"** list on the time screen → sends a request → the operator gets a card "PC-07 — Rahul: 4 hr → Full Day,
  ₹200 → ₹700, 11 hr left" → Approve / Decline. The operator can also change it directly from the PC panel on the Sessions page.
- Extra time added before (extensions) is replaced by the new package. Paused/outage minutes still don't count as played.
- Member: refused if the gaming wallet can't cover the new package price.
- Pay-As-You-Go sessions (no package) are not included.
- Audit log entry for every change (who, from, to, price).

## Owner asked: where will it show? (4 Nov 2026). Proposed screens

### Member / customer gaming PC (overlay)
- Bottom bar today: Home · Food · **Extend** · Call · Bill. The **Extend** tab becomes **"Time"**, with two sections:
  1. **Extend time** (as today)
  2. **Change package**: one card per package of this PC, e.g.
     `Full Day 14 hr  ₹700  →  11 hr left`  [Request]
     `2 hr  ₹120  →  ✗ already played 3 hr` (greyed, with the reason)
     The current package is marked "Current".
- Tap Request → "Request sent — the operator will approve shortly". Approved: the timer and bill update by themselves.
  Declined: "The operator declined".
- Same for walk-ins and members. A member whose wallet can't cover it sees the card greyed: "Gaming balance ₹500 — top up at the counter".
- The Home screen shows a small line under the timer: "Package: 4 hr · Change ›" that opens the same screen.

### Operator PC
1. **Approval card** (same place as the extra-time card, bottom of the screen):
   `PACKAGE CHANGE — PC-07 — Rahul` · `4 hr → Full Day` · `Bill ₹200 → ₹700` · `Played 3 hr · 11 hr left` · [Decline] [Approve]
2. **Sessions page → click the PC → panel buttons** today: Credit · Extend · Food · Discount… A new **"Change Package"** button opens a list of
   packages with the new price and time left; the operator picks one and confirms. No request is needed.

## Built (4 Nov 2026)
- Server: `SessionService.GetPackageOptionsAsync` (each package: price, time left, allowed or why not) + `ChangePackageAsync`
  (the session becomes that package from its own start: same fields as starting on it; bill gaming lines replaced by one; discount kept;
  agent/PC/screens told; audit `session_package_change`).
- Endpoints: `GET /api/sessions/{id}/package-options`, `POST /api/sessions/{id}/change-package` (refused at HQ),
  `GET /api/public/pcs/{pcId}/package-options`, `POST /api/public/pcs/{pcId}/decline-package-change`;
  hub `RequestPackageChange` → operator card `PackageChangeRequested`; PC gets `PackageChanged` / `PackageChangeDeclined`.
- Gaming PC: the bottom tab "Extend" → **"Time"** with a "Change Package" list; Home has "Change package ›".
- Operator: an approval card (package, bill ₹ → ₹, played / left) and a **Change Package** button on the PC panel (`ChangePackageModal`).

## Test (real API + SignalR, Adajan's packages 2 hr ₹120 / 4 hr ₹200 / Full Day ₹700): 22/22 PASS
4 hr after 3 hr → Full Day: card shows "4 hr → Full Day, ₹200 → ₹700, played 3 hr, 11 hr left"; approved, the bill is **₹700** with one line, ends 11 hr from now, PC told.
Full Day → 4 hr after 3 hr: ₹200, 1 hr left. 5 hr played: down to 4 hr refused. 2 hr + 1 hr extension (₹180) → 4 hr: ₹200 (extension replaced).
A member with ₹500 can't pick Full Day ₹700. A refused package can't be requested from the PC. Decline reaches the PC.

## Note for the owner (existing rule, not changed)
If the customer **leaves early** (well before the package ends, with no "package price from" set on that package), Stop bills the
**minutes played**. That's the 25 Oct rule, the same as for any package. Full Day stopped after 3 hr = ₹180, not ₹700. To always charge
Full Day once chosen, set "Package price from" on the Full Day package (e.g. from 1 min).
