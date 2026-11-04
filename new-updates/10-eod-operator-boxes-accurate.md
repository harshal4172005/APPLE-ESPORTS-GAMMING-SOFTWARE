# Count 10 — EOD "What the operator sees": Cash / Online / Food must be the whole day's real figures

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** EOD page (server EodService + EodDashboardPage). Money display.

## What the owner said
In EOD, the box "What the operator sees" (Total / Cash / Online / Food) should be accurate: the whole day's cash from everywhere,
the same for online and food. Screenshot: Total ₹3325, Cash ₹2510, Online ₹815, Food ₹200 = **Adajan-240Hz, 3 Nov** (matched in HQ data).

## How the boxes are worked out now
- Cash = cash in bill payments + cash member top-ups. Online = the same for online.
- Food = food on bills completed that day (before discount, including food paid from the member food wallet).
- Total = Cash + Online − petty expenses + cash in − owner takeouts.

## What was found (Adajan, 3 Nov)
- Payments cash ₹2510, but the drawers recorded ₹2450 of cash sales. The ₹60 difference comes from **payment-method edits**:
  **25 cash↔online edits** that day, many flipping the same bill back and forth within minutes, and 6 made **after midnight** with
  reason "light issue", after the drawer was closed. EOD follows the edited payment; a closed drawer does not change. (Fix 3.1.130,
  on purpose, so an old bill doesn't move today's drawer.)
- **Not counted anywhere in the boxes:** reservation advance deposits (cash goes into the drawer, but they're not a payment).
  None in the last 30 days, so this is not today's cause, but it is a gap.
- Food counts only food on bills **completed** that day. Food ordered on a session still running at midnight shows the next day.

## Proposed
1. Each box adds up every source of that money for the day: bill payments, top-ups, credits paid off, member top-up credits paid off,
   reservation deposits, PS5 (already a bill). Tap a box to see what it is made of (e.g. Cash ₹2510 = bills ₹2390 + credits cleared ₹30 + …).
2. Show a warning line when payment methods were edited that day: "25 payment-method changes today (6 after the drawer closed)",
   so the owner sees why the drawer and the box differ.
3. Food: the definition needs the owner's answer (below).

## Question for the owner
Food box: is it **food sold today** (every food order delivered today, including members paying from the food wallet and food
still on a running session), or **food money received today** (only the food part of bills paid today)?

## Owner's answer on Food (4 Nov 2026)
Food = **only the food money received today**, cash or online. Cash food counts inside Cash, online food inside Online.
A member's food balance is counted **once, when they top up** (it is already in the top-up). Food paid from the food wallet is not counted again.

### So the Food box becomes
The food part of every cash/online payment received today (bills, walk-in food bills, credits paid off), shared between cash and online
by how the bill was paid. **Not** food paid from the member food wallet, and **not** food still on an unpaid bill.
Shown as "Food ₹200 (cash ₹160 + online ₹40)". It is already inside Cash and Online, not extra.
(Today's box counts food on completed bills, so it includes wallet-paid food and food before discount.)

## Built (4 Nov 2026)
- `EodService.BuildOperatorBoxesAsync` → `report.operatorBoxes`: Cash/Online each = bills (incl. PS5) + credits paid off + member top-ups
  (incl. top-up credits paid off) + reservation deposits. Food = the food share of every cash/online payment
  (food after its discount share ÷ bill total), split cash/online. Food paid from the wallet is not counted.
  Payment-method edits today, and how many changed cash but could not move a closed drawer.
- `BillingService` payment-method edit audit now records `CashChanged` / `DrawerMoved`.
- EOD page `OperatorBoxes.jsx`: tap a box to see its parts; Food shows "cash ₹x + online ₹y"; orange warning line for edits.
  `EodPaymentSummaryBar` uses the same Cash/Online. Old server → old formula.
- **Nothing else in EOD changed** (Revenue, PaymentMethods, Reconciliation untouched). The only difference from before is
  reservation deposits (now counted) and the Food definition (owner's).

## Test (real API): 9/9 PASS
Cash from bills, online from bills, credits paid off +400, Cash = sum of parts, food ₹100 split by how the bill was paid,
a payment-method edit counted (drawer open, so not flagged), food follows the edit to cash.
Not tested: an edit after the drawer closed (logic reviewed), and the screen in a browser.
