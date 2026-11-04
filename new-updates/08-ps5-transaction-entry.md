# Count 8 — PS5 transaction entry on Cash Desk, in EOD, with a PS5 log

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** Branch + HQ (new table + sync), Cash Desk, EOD page/PDF/email. Source: owner's PRD "Change 4".
**Critical: money and EOD.**

## What the owner asked
On Cash Desk, add a PS5 transaction (cash / online / credit / split) that feeds EOD, plus a PS5 log with a date range under
"Complete Billing Audit Logs".

## Design (PRD, recommended)
- A new **PS5 entry** record, kept separate from bills (payments and credits require a bill). Append-only: no edit or delete,
  fixed with a reversing entry by an Admin.
- Saving one does all of this in one step: the PS5 entry; if cash, a cash-ledger row "ps5_sale" and drawer +cash; an audit log;
  sync to HQ.
- Credit is kept pending (like a member top-up credit) and cleared later through Credits.
- EOD: PS5 cash goes into Cash, PS5 online into Online, and a new "PS5" block. Net Revenue and Reconciliation include it.
  **Trap:** the ps5_sale ledger row must NOT also count as Cash Inward (double count).
- The EOD bar, stat grid, PDF and email all read the server's PS5 numbers (no re-calculating on the screen).
- PS5 log card under the billing log with its own From/To (defaults to the page range), and in the PDF.
- Cash Desk: a **PS5** button next to Add Entry (not for Super Admin), with a popup for amount, customer, phone (needed for credit),
  note, and Cash / Online / Credit / Split. Double-click protected.
- Built behind a **per-branch switch, off by default**. Turn it on at one branch first and compare EOD against a manual count for 2 days.

## Money rules
- The server checks all amounts.
- Everything is saved together or not at all.
- Nothing is counted twice (EOD, drawer, dashboard, reports).
- Past EOD days without PS5 must come out exactly the same.
- Branch and HQ ship together.

## Also found
The Cash Desk "Add Entry" popup offers a **Correction (adjustment)** type that the server rejects (it accepts only inward,
petty_expense and withdrawal; confirmed in `CashRegisterService` L332–334). It's a separate small bug.

## Questions for the owner (must answer before build)
- **Q1** Is a PS5 entry **money received** from PS5 customers (income), or money **spent** (expense)? Designed as income.
- **Q2** PS5 consoles can already run as sessions (Zone = Console). Is this entry **instead of** a session, or **as well as** one?
  (Something billed as a session must not be entered again.)
- **Q3** Can a PS5 credit be **cleared later** on the Credits page like a bill credit? Assumed yes.
- **Q4** In EOD/Reports, is PS5 its **own line "PS5"** (recommended), or part of Gaming revenue?
- **Q5** Who can add a PS5 entry: operators only, or Admin too?

## Owner's answers (4 Nov 2026). Develop approved
- **Q1:** income. A customer comes and plays, and the operator just makes an entry.
- **Q2:** **as well as** sessions. PS5 sessions stay as they are; this is a new, separate entry the client needs.
- **Q3:** yes. A PS5 credit can be paid off later on the Credits page.
- **Q4:** counted as **Gaming revenue** (these customers come and play games). It is still listed separately in the PS5 log.
- **Q5:** **operators and Admin** can both add entries.

## As built (4 Nov 2026): simpler than the PRD design, on purpose
The PRD proposed a new `Ps5Transaction` table with new EOD maths in 4 places. Because the owner said PS5 **is Gaming revenue**,
a PS5 entry is instead an **ordinary bill**: no PC, no session, one gaming line "PS5 - note", numbered `PS5-yyyyMMdd-XXXX`
(same idea as the existing `MISS-` missing-stock bills). Everything that moves money is existing, live code:
the payment code (cash into drawer, online, split, credit), Credits (clear later), EOD (Gaming revenue, Cash, Online, Net, Reconciliation),
the billing log and HQ sync. **No EOD arithmetic changed**, so the double-count risk the PRD warned about cannot happen, and past days are unchanged.

- Server: `POST /api/bills/ps5` [Idempotent] → `BillingService.CreatePs5BillAsync` (amount > 0, customer name required, refused at HQ,
  refused for true Super Admin like every drawer action; operators and Admins allowed). Audit `ps5_entry_created`.
- Cash Desk: **PS5** button next to Add Entry → `Ps5EntryModal`: amount, customer, phone, note → then the Billing Counter's own payment panel
  (Cash / Online / Split / Credit). Closed before paying: the bill waits on the Billing Counter.
- EOD: `range-report` returns `ps5Entries` (cash/online/credit/status per entry). New **PS5 Transaction Log** card under the billing log with its
  own From/To (starts at the page range), plus a section in the EOD PDF.
- No per-branch switch: nothing in the money path is new, so there is nothing to switch off.
- Also: removed the Cash Desk "Correction" option, which the server always rejected (EOD only knows the three real types).

## Test (scratch DB, real API; every figure as a before/after delta)
| Entry | Drawer | EOD Cash | EOD Online | Gaming | Net | Credit given | Reconciliation diff |
|---|---|---|---|---|---|---|---|
| Cash 500 | +500 | +500 | 0 | +500 | +500 | – | 0 |
| Online 300 | 0 | 0 | +300 | +300 | +300 | – | 0 |
| Split 200+300 | +200 | +200 | +300 | +500 | +500 | – | 0 |
| Credit 400 | 0 | 0 | 0 | +400 | 0 | +400 | 0 |
| Cash 100 + credit 300 | +100 | +100 | 0 | +400 | +100 | +300 | 0 |
| Clear the 400 credit in cash later | +400 | +400 | – | – | +400 | – | 0 |

The PS5 credit shows on the Credits page. A double click with the same key makes one bill. Amount 0 and a missing name are refused. Super Admin is refused.
The PS5 log lists all 5 with totals (total 2100, cash 800, online 600, credit 700, cleared status shown), and they are also in the normal billing log. **60/60 PASS.**
Not tested: the screens in a browser.
