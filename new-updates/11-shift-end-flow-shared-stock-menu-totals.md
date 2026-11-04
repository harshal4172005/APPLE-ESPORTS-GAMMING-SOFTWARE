# Count 11 — Shift-end order, shared fridge / stock counting, Menu Editor totals

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** Branch (shift end + stock), HQ (shared fridge + Menu Editor). **Money and stock: highest care.**

## What the owner said
1. At shift end the cold-drinks count sometimes comes **before** the money count and sometimes **after**. And if staff count the
   food and then press **Back**, coming back takes them **straight to the money page** — the stock step is gone.
   "Make proper system."
2. Complaints about wrong stock: at Citylight on 31 Oct, 144Hz showed **"system 0, 14 extra"**, S1 and S2 showed **"system 13, 1 extra"**.
   Also "I counted 28 water bottles and it showed 27."
   "Make the shared stock and stock counting perfect."
3. Menu Editor should show, per item and in boxes: how much the admin **refilled**, how much was **sold**, and the **difference**
   ("900 sold this month, admin put 950 in, where did 50 go").

---

## Part 1 — Shift-end order and the Back button

### Why it happens (code, 4 Nov)
There are **two different stock counts**, run by two different parts of the system:
- **Shift end (Cash Register page):** the stock count appears **only when that branch's "Missing stock" switch is ON**
  (`CashRegisterPage` shows `MissingStockCount` only when `missingStatus.enabled`). Order: count stock → pay missing → lock → count money.
- **Shift start / takeover (login):** `ShiftTakeoverService` asks for cash **and** stock together at login, before the operator can trade.
That is why the question lands in a different place on different days: the two flows are not the same flow.

**The Back button:** on the stock step the register is still "Open", so Back just calls `navigate(-1)` and leaves the page.
The count was already submitted when they pressed Confirm, so `closingCountDone` is now true, and returning drops them straight on the
money step. Nothing is lost, but there is no way back and no indication of where they are.

### Proposed
- **One shift-end flow, always the same, with a visible step bar:**
  `1 Count stock → 2 Pay missing (only if the switch is on) → 3 Lock → 4 Count money → 5 Cover / float → 6 Close`.
- The stock count runs at every shift end **whether or not** the missing-stock switch is on (it is what the owner already sees in the
  shift history). The switch only decides whether a missing-stock **bill** is raised.
- Back goes to the **previous step**, not out of the page, and the stock step can be re-opened and re-counted until the register is locked.
- The step bar says which step they are on and what is left.

---

## Part 2 — Shared fridge and stock counting

### What actually happened at Citylight on 31 Oct (traced in HQ `shared_stock_requests`)
Diet Coke, one shared fridge, three counters:

| Time | Counter | What it sent | Shared count after |
|---|---|---|---|
| 16:55:53 | 144Hz | sold 1 | 14 |
| 16:57:28 | 144Hz | sold 1 | 13 |
| 16:57:46 | **S2** | **set 14** (its shift-end count) | **14** |
| 17:00:05 | **S1** | **set 0** | **0** |
| 17:05:07 | **144Hz** | **set 14** | **14** |

- S2 counted 14 when the system said 13 → **"system 13, 1 extra"**.
- S1's count landed as **0**.
- 144Hz counted 14 when the system said **0** (S1 had just set it) → **"system 0, 14 extra"**.

**Root cause:** every shift-end count is an **absolute "set"** on the one shared fridge. Three counters closed within 8 minutes, each
overwriting the previous one. The "extra/short" each operator was shown was measured against a number another counter had already
changed. The same thing explains **"I counted 28 water, it showed 27"**: another counter sold one between opening the count screen and saving.

Also found: on 1 Nov at 02:19 S2 was **refused a sale** — "Citylight - 144Hz has no internet, so the shared fridge is sold only there
until it is back." That rule is working, but staff get no warning before it bites.

### Proposed
1. **One fridge, one count, one time.** The shift-end stock count for a shared group is a **group count**: whoever counts sets the
   fridge, and the other counters are told "counted by X at 17:05 — nothing to count". No second or third overwrite.
2. **Count against a frozen number.** The screen records what the system said **when the count screen was opened**, and sends
   "counted 28, system was 27, sold 1 while counting" — so the operator is never judged against a number that moved under them.
3. **A count is never silently overwritten.** If another counter changed the fridge while this one was counting, the screen says so
   and asks for a recount instead of saving a wrong "set".
4. **Offline warning up front:** when this branch is not the chosen offline seller and the internet is down, the counter shows
   "fridge sales are at <branch> right now" **before** an operator tries to sell.
5. **One shared-fridge history page** (HQ): every take/add/set with branch, operator, time and the resulting count — so a disputed
   number can be read back in one place instead of being guessed at.

### To confirm with the owner
The fridge groups were **re-linked** after the 22 Oct incident (Adajan, Citylight x3, Katargam x3 are all grouped again at HQ now).
That was done on the shop's side, not by me, and the old stale rows were never cleared — HQ's shared counts only start from
**3 Nov 10:49**. If any group still looks wrong, it needs one clean fridge count per group after this update ships.

---

## Part 3 — Menu Editor: refilled vs sold vs difference

### Now
Menu Editor shows Price, Stock limit, Expected stock, Sold qty. There is **no refill total** and **no difference**.
`inventory.SoldQty` is a running total that is never reset, so "sold" is all-time, not this month.

**Blocker found:** refills and counts are written to `inventory_logs` **on the branch only**. HQ has almost none
(4 price changes, 2 discrepancies in total). So Head Office **cannot show refills today** — the branch has to send them first.

### Proposed
- Branch sends every stock movement to HQ: refill (admin added), sold, count correction, with who and when.
- Menu Editor gets a **date range** (default: this month) and per item:
  `Opening · Refilled +950 · Sold −900 · Counted correction · Should be · Actually is · Difference`
  with the difference in red when stock is missing.
- Four boxes at the top for the whole range: **Refilled**, **Sold**, **Missing**, **Value of missing** (at menu price).
- Click an item → every movement in that range (date, branch, operator, +/−, reason).

---

## Order of work (proposed)
Part 1 (flow) → Part 2 (shared fridge rules) → Part 3 (needs the branch→HQ stock history from Part 2).
Part 2 touches live stock at 7 grouped branches, so it ships with a backup and one clean fridge count per group.

---

## Built (4 Nov 2026)

### Part 1 — one shift-end flow
- `CashRegisterPage`: a step bar on every step — **Count stock › Pay missing › Lock & count money › Close shift** — showing where the
  operator is and what is done. Back no longer drops them past the stock step.
- The count screen can be re-opened and recounted until the register is locked ("Recount" reloads the live numbers).

### Part 2 — shared fridge and counting
**Owner's correction (4 Nov 2026): every branch counts the shared fridge at its own shift end. A count is never refused for being second.**
My first attempt refused the later counters; that was wrong and has been removed.

- `shared_stock_counts` records **who counted and when** (`CountedAt`, `CountedByBranchId`, `CountedByName`).
- **The real cause of the 31 Oct mess:** each counter compared the shelf against **its own copy**. 144Hz still held 0 while the fridge
  held 14, so a correct count of 14 was reported as "14 extra". Fixed at the source: the count screen asks Head Office for the
  **live fridge** (`SharedStockGate.LiveCountsAsync`), so "system says" is the real number every counter sees.
- Every count applies. If another counter counted the same item in the last 30 minutes **and got a different number**, the reply carries
  theirs (`previousCountBy/At/Count`) and the operator is shown both: "S1 counted 14 at 4:57 PM; you counted 12. The fridge is now 12 —
  check the shelf if that looks wrong." Agreeing counts say nothing.
- The count screen loads a **sheet** (`GET /inventory/stock-count/sheet`) listing **every** item to count, each with the live number and,
  where it applies, "S1 counted this at 4:57 PM". No line to Head Office: the screen says so and the count still goes ahead.
- A count now sends **what the screen said when it opened** (`systemWas`). The server reports what moved in between, so
  "1 sold while you were counting" replaces blaming the operator for an extra bottle. `stock_counts.OpenedAt` keeps it.

### Part 3 — Menu Editor totals
- `InventoryLog` is now synced branch → HQ (`inventory_log.changed`), which Head Office never had before.
- New `GET /inventory/stock-summary?from&to&branchId` (Admin/Super Admin): per item **opening, refilled, sold, corrections,
  should be, actually is, missing, value of missing**, plus range totals.
- Menu Editor: a **Stock in and out** panel with a date range (this month by default) and five boxes —
  **Was already there / Admin added / Total available / Sold / Missing (with its rupee value)** — written out in a sentence under them
  ("5 were already there and the admin added 10, so 15 were available; 9 sold, which leaves 6"), plus a per-item table:
  already there · admin added · available · sold · should be left · on the shelf · difference. (Owner, 4 Nov: what was carried over from
  the last refill must be as plain as what the admin put in.)

## Test (HQ + branch servers, real APIs and database): 25/25 PASS
**The 31 Oct Citylight case, replayed, with every branch counting:** sales take it to 18 → S2 counts 14 → S1 counts 14 (**also counts**,
nothing flagged because they agree) → 144Hz counts 12 (**also counts**, and is told "S1 counted 14 at ..."). Every counter counted;
no number was silently overwritten; and because "system says" is now Head Office's live fridge, **"system 0, 14 extra" cannot happen again.**
Selling after a count still works; a counter can correct itself; a later shift counts with nothing flagged; a replayed offline sale is never blocked.
**Counting while selling:** screen opened at 28, one sold, operator counted 28 → recorded as "1 sold while you were counting",
shelf ends at 28, open time kept.
**Menu Editor:** refilled 950, sold 900, **missing 50** (₹1,000), opening + refilled − sold = should be; an operator is refused (403).

## Not done / still open
- The **takeover (login)** screen is deliberately unchanged - the owner confirmed on 4 Nov that it is fine as it is.
- Not viewed in a browser.
- **On release day:** branch and HQ must ship together (HQ needs the new columns before a branch sends a count).
  After the release, ask each shared group for **one clean fridge count** so every counter starts from the same number.

---

## Owner's money rules for counting (4 Nov 2026) — built and tested
1. **Shared fridge, counted less than the last counter:** if sales explain the difference, nothing is owed. If nothing explains it,
   it is missing and the operator pays.
2. **Any short count:** find where it went and show it; charge only what cannot be explained.
3. **Menu Editor:** trace the missing — when it went and on whose count.

### What changed for these
- **Money bug found and fixed:** the count screen showed Head Office's live fridge number, but the missing-stock bill was still worked
  out from this branch's own row. A branch holding a stale 0 while the fridge held 14 would have billed the operator for 14 that were
  on the shelf. The live number is now written into the branch's row when the sheet opens (straight to the table, like the watcher, so
  it is never echoed back to Head Office).
- The count result returns a **trail**: every sale/refill of a disputed item since the last count (the count's own correction is
  excluded - that is not where the stock went). Shown on screen as "Where the short stock went, since the last count", with the note
  "Anything above is accounted for. Whatever is left over is billed to you at menu price."
- `stock-summary` returns **wentMissing**: date and time, item, should-have-been → found, how many, what they are worth, and who counted.
  Menu Editor shows it as a red "When the stock went missing" table.

### Test (second suite, real API + database): 13/13 PASS
- Shelf 14, screen says 14, **2 sold**, operator counts 12 → **no bill**, and the sale is listed ("Water: 2 sold at ...").
- Shelf 14, **nothing sold**, operator counts 12 → bill **MISS-… ₹40** (2 × ₹20, menu price), charged to the operator, nothing to show as
  "where it went".
- Menu Editor: lists that count — Water, 14 → 12, 2 missing, ₹40, counted by Karan — and the range totals agree (2 units, ₹40).

The first suite still passes in full (25/25) after these changes.
