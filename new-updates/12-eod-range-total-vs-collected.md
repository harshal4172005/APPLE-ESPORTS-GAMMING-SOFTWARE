# Count 12 — EOD range "Total" does not match the cash the operators counted

**Status:** Built + tested locally (5 Nov 2026). Not shipped; waits for the next release.
**Where:** End of Day, date-range view (HQ + branch). Reporting only — no money changes hands.

## What the owner saw
End of Day, 22/10/2026 – 28/10/2026: **Total Net Revenue ₹18,820**.
Adding up the seven daily "What the operator sees" Totals by calculator: **₹24,290**. A gap of **₹5,470**.

## Verified independently (live HQ database, 5 Nov 2026)
The branch is **City light 240Hz - S2**. Both numbers reproduce exactly:

| Day | Billed | Cash in | Online in | Top-up cash | Top-up online | Petty | Operator Total |
|---|---|---|---|---|---|---|---|
| 22 Oct | 1,330 | 830 | 380 | – | – | – | **1,210** |
| 23 Oct | 1,350 | 970 | 360 | – | – | 40 | **1,290** |
| 24 Oct | 3,660 | 2,050 | 1,070 | 380 | 2,210 | – | **5,710** |
| 25 Oct | 5,180 | 2,410 | 2,070 | 500 | – | – | **4,980** |
| 26 Oct | 3,130 | 1,130 | 1,240 | – | – | – | **2,370** |
| 27 Oct | 2,820 | 880 | 990 | 5,500 | – | – | **7,370** |
| 28 Oct | 1,350 | 940 | 420 | – | – | – | **1,360** |
| **Total** | **18,820** | 9,210 | 6,530 | 6,380 | 2,210 | 40 | **24,290** |

So **neither figure is wrong**. They measure different things:
- **₹18,820** = what customers were **billed** for (gaming + food).
- **₹24,290** = money that actually **came in** (drawer + online).

### The ₹5,470 gap, item by item
| | |
|---|---|
| Wallet top-ups — money in, but not a sale (it is credit for later) | **+8,590** |
| Credit given this week — billed, customer has not paid | **−4,170** |
| Older credits paid off this week — money in, billed earlier | **+1,050** |
| Bills paid from a member's wallet — money came in at top-up time | **−50** |
| Petty expense out of the drawer | **−40** |
| Bills paid a few minutes either side of the week's edges | ≈ −90 |
| **Net** | **≈ +5,470** |

**Biggest single cause:** one **₹5,500 cash top-up on 27 Oct**. That alone is why 27 Oct shows a ₹7,370 Total against ₹2,820 of billing.

## Two errors in the PRD that was supplied (do not carry them forward)
1. It says top-ups were **₹9,390**. That is the amount **added to wallets**, which includes **₹800 of free bonus** the shop gave away. The money actually received was **₹8,590**.
2. It says the leftover **₹800 is "net petty expenses and owner withdrawals (inferred, not verified)"**. It is not — the ₹800 is exactly that free bonus. Real petty expenses for the week were **₹40**, and there were **no owner withdrawals**.

Its final number happens to come out right because the two mistakes cancel. The reasoning behind it is wrong, so it must not be used as the basis for the fix.

## Why it looks like an error on screen (the real fault)
1. **Only one total exists in range mode.** The range view deliberately skips the per-day cash figures, so there is no "money received" number anywhere — the owner had to add seven cards by hand.
2. **The label.** The one figure shown is called "Total Net Revenue", which reads as "everything we took".

## Proposed fix
- Range mode shows **two** figures side by side:
  - **Money received** — the same formula as the daily operator card, summed over the range. It can never disagree with the daily cards.
  - **Billed sales** — today's figure, renamed so it cannot be read as cash.
- Under them, one line explaining the difference for that range:
  *"Money received is ₹5,470 higher: +₹8,590 wallet top-ups, +₹1,050 old credits paid off, −₹4,170 credit given this week, −₹40 expenses."*
- The same two figures go into the **Download PDF**, which today prints only the billed figure.

### Not the way the supplied PRD proposes it
That design calls the server **once per day in the range** (about 30 calls for a month, repeated every 30 seconds) and hides the whole row if any single day fails. Instead the range totals are computed **once, on the server**, inside the existing `/eod/range-report` — one call, no partial failures, and the same arithmetic as `operatorBoxes` which already exists from count 10.

## Also worth knowing
Credit billed in one range and collected in another appears as money in the range where it was **paid**, not where it was billed. That is correct and deliberate, but it needs saying on the page.

---

## Built (5 Nov 2026) — owner's instruction: show money received only, not what customers bought

**Server** (`EodController.GetRangeReport`): the range report now also returns, from one pass over the
same window, `collected` (total, cash, online, cash inwards, paid out) and `collectedDaily` (one row per day).
Same formula as the single-day operator card, so the two can never disagree.
**Not** one `/eod/preview` call per day as the supplied PRD proposed — that was ~30 calls a month, every 30 s,
and hid the whole row if any single day failed.

**Screen** (`EodDashboardPage`, range mode only; single-day view untouched):
- The four cards are now **Money Received** (the headline), **Cash**, **Online**, **Paid Out**.
  The old "Total Net Revenue" billed figure is **gone from the range view**, as asked.
- "Daily Revenue Breakdown" is now **"Money Received Day by Day"**: per day Cash, Online, Paid Out, Received,
  with a TOTAL row — the exact figures the owner was adding on a calculator.
- Download PDF carries the same four figures (it printed only the billed total before).

## Test (local database rebuilt as the real Citylight S2 week, real API): 10/10 PASS
Money Received = **₹24,290**, the owner's calculator total, to the rupee.
Cash ₹15,590, Online ₹8,740, Paid out ₹40, and cash + online − paid out = the headline.
All seven days match their own daily figure and add up to the headline.
27 Oct shows ₹7,370 received against ₹1,870 billed — the ₹5,500 top-up, visible instead of confusing.
