# Count 13 — PS5 log: separate Person and Hours columns

**Status:** Built + tested locally (5 Nov 2026). Not shipped; waits for the next release.
**Where:** Branch + HQ — PS5 form, PS5 log on End of Day, EOD PDF. Source: owner's PRD of 5 Nov.
**No money changes.** Hours is information only.

## What the owner asked
"In the PS5 screen I want separate columns for the person and the hours."

## Checked against what shipped in 3.1.136
- **Person** — already its own column, headed **Customer**. Nothing to build; the heading becomes **Person**.
- **Hours** — **does not exist**. Today the operator types it into the free-text Note ("2 players, 1 hour"),
  so it cannot be read on its own, added up or sorted, and it sits mixed in with other notes.
- Confirmed in code: `CreatePs5BillDto` has Amount, CustomerName, CustomerPhone, Note — no hours.
  `Ps5LogCard` columns are Date/Time · Customer · Note · Payment · Cash · Online · Credit · Total · Operator.
  `Bill` has no minutes field.

## Proposed
1. **PS5 form:** a separate **Hours** box next to the person's name, required. Note stays optional and no longer
   needs to carry hours.
2. **Stored on its own** as minutes on the bill (1.5 hr = 90), nullable so older entries and older branches keep working.
   Travels to Head Office with the bill.
3. **PS5 log and PDF:** Date/Time · **Person** · **Hours** · Note · Payment · Cash · Online · Credit · Total · Operator —
   two separate cells, never "Rahul – 2h" in one. The totals row adds **total hours**.
4. **Entries made before this change** show "-" under Hours and are left out of the hours total. No guessing from notes.
5. **The amount is still whatever the operator types.** Hours never changes a price, the drawer, EOD or reconciliation.
   Past days' EOD figures must come out identical — that gets a regression check.

## Three questions for the owner
- **Q1 — How are hours entered?** Free number in half hours (0.5, 1, 1.5, 2 …), or fixed buttons (30 min / 1 hr / 2 hr / 3 hr)?
  *Proposed: free number in half hours, up to 24.*
- **Q2 — Where does total hours show?** Only at the bottom of the PS5 log, or also in the big boxes at the top of End of Day?
  *Proposed: bottom of the PS5 log only.*
- **Q3 — Does "Person" mean one name, or can it be several players?** The current note example is "2 players, 1 hour".
  If several people play on one entry, a separate **Players** number column is needed as well.
  *Proposed: Person is the one name that pays; add a Players count only if the owner wants it.*

## Ships with
Branch and Head Office together (Head Office must accept bills with no hours from older branches).

## Owner's answers (5 Nov 2026)
- **Q1 How hours are typed:** as the operator says it — "1 hr", "1 hr 30 min", "30 min". So the form has two boxes,
  **hr** and **min**, not a decimal like 1.5.
- **Q2 Total hours at the top of EOD:** not hours — instead the owner asked for a **PS5 money box** in the
  "What the operator sees" row, working exactly like the Food box (total, tap for cash and online).
- **Q3 Several players:** no. One customer name is enough; friends are not asked for. No Players column.

## Added: PS5 payment method edit (5 Nov 2026)
- PS5 bills can now have their payment method changed (Cash ↔ Online), same as regular bills.
- Backend already supported it. Only added `BillId` to the PS5 entry response in `EodController.cs`.
- `Ps5LogCard.jsx`: pencil icon on each row's Payment column, inline cash/online selector + reason.
- Same permission as regular bills — any logged-in role can correct.
- **Test: 8/8 PASS** — created ₹500 PS5 Cash, edited to Online, verified billId in response,
  payment type/amounts flipped, PS5 box online updated.

## Built (5 Nov 2026)
- `Bill.PlayMinutes` (nullable int, 90 = 1 hr 30 min) + column via `DbUpdater`, so it travels to HQ with the bill.
- `CreatePs5BillDto.PlayMinutes` required, 1–1440. Server refuses 0, blank or over a day, and still refuses a blank person.
- The bill line reads "PS5 - 1 hr 30 min - note" for the printed bill; the log strips that prefix by rebuilding the exact
  same text from PlayMinutes, so **the hours never leak into the Note column** (caught by the test, then fixed).
- PS5 form: **Person** (renamed from Customer) and two boxes, **hr** and **min**, with the note left optional.
- PS5 log and the EOD PDF: **Person** and **Hours** are two separate columns; the totals row adds the hours
  ("4 hr"). Older entries show "—" and are left out of that total.
- **New PS5 box** on the End of Day operator row, beside Food: total PS5 money, tap to see cash and online.
  Like Food, it is money already counted inside Cash and Online; the line under the boxes now says so.

## Test (real API + database): 19/19 PASS
Rahul 2 hr ₹400 cash: drawer moves exactly ₹400, hours stored on their own. 1 hr 30 min and 30 min both accepted.
Blank time, over a day, and a blank person are all refused. The log shows person and hours in separate cells, the note is
empty rather than carrying the time, an older entry shows no hours, and the hours total is 4 hr.
PS5 box reads ₹850 and splits into cash ₹550 + online ₹300, with that money also inside the Cash and Online boxes.
Money in the log is unchanged by any of it.
