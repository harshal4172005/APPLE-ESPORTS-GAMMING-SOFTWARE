# Count 21 — EOD: Gaming Revenue box (like Food box)

**Status:** Built (5 Nov 2026). Backend + frontend.
**Where:** EOD dashboard — "What the operator sees" section (branch + HQ).

## What the owner asked
"In EOD I want a Gaming Revenue box. Online + Cash should be shown and total also, like the
Food box. Just need to add a Gaming Revenue box of the whole day."

## What was done
1. Added `GamingCash`, `GamingOnline`, `Gaming` to `OperatorBoxesDto`.
2. In `EodService.BuildOperatorBoxesAsync`: `GamingCash = CashFromBills − FoodCash − Ps5Cash`,
   same for online. Gaming is bill revenue minus food and PS5 portions.
3. Added a 6th box "Gaming" to `OperatorBoxes.jsx` with cash/online sub-line and tap-to-expand
   breakdown. Grid changed to 3 cols on tablet, 6 on desktop.
