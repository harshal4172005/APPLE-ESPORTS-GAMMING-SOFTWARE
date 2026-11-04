# Count 2 — Difference total for the date range + per-operator difference

**Status:** Built (4 Nov 2026), client build passes; not yet viewed in a browser. Waits for the combined release.
**Where:** Cash Register page → Cash Register History (branch + HQ, screen only, no money logic)

## What the owner asked
Show the total difference for the selected date range. Also show how much difference each operator has.

## Now
The history lists each register with its own difference (for example ₹880, ₹-190, ₹10), but there is no total anywhere,
and no per-operator summary.

## Proposed fix
1. A **Total row** under the table for the selected range (and the selected operator filter):
   - Short (all minus amounts added up), for example ₹-200
   - Extra (all plus amounts added up), for example ₹+1,200
   - Net (short + extra)
2. A **"Difference by operator"** box: one line per operator showing the number of registers, short,
   extra and net. For example: jigar, 4 registers, short ₹0, extra ₹1,200, net ₹+1,200.
3. Both are also added to the **Download PDF**.
4. A register that is **still open** is left out of the totals (its count is not final yet) and marked "not counted yet".

## Files
`client/src/pages/cash/CashRegisterPage.jsx` (history section + PDF). Server not changed.

## Built (4 Nov 2026)
- `CashRegisterPage.jsx`: the existing page is unchanged. Two boxes were added under the history table, "Total difference" (short / extra / net)
  and "Difference by operator" (registers / short / extra / net + TOTAL). Both follow the date range and operator filter.
  Still-open registers are left out ("N still open, not counted yet"). Both are also added to Download PDF.
- Client build passes. Screen-only; no server change.
