# Count 4 — Super Admin discount as a typed amount (₹), not percent

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** Counter/HQ screen → PC → Discount popup (`SessionDiscountModal.jsx`)

## What the owner said
The Super Admin discount on a session shows only percent buttons (5/10/15/20%). The owner wants a box where Super Admin types
the money amount. For example, a ₹940 bill with ₹40 typed means the customer gets ₹40 off.
"This discount doesn't count anywhere or doesn't get minus from anywhere."

## Now
The popup offers only NONE / 5% / 10% / 15% / 20%. The server already supports a flat ₹ discount (`DiscountType.Flat`,
which the billing counter uses). Only this popup is missing it.

## Proposed fix
- Replace the percent buttons with a **₹ amount box** (keep a "Remove discount" button).
- Saved as a **Flat ₹ discount by Super Admin** on that session's bill. It stays exactly ₹40 when the session
  ends (a percent grows with time; a flat amount does not).
- The customer pays bill minus ₹40. The operator's drawer expects the lower amount, so **the operator is never short** for it.
- The discount can't be more than the bill.

## To confirm with the owner
"Doesn't count anywhere": understood as **not charged to the operator and not taken from the drawer**.
It will still be listed in the discount report as "Super Admin discount ₹40", so you can see what you gave.
If it should not appear in the report either, say so.

## Owner's answer (4 Nov 2026)
Keep it in the discount report as an entry. Yes.

## Built (4 Nov 2026)
- `SessionDiscountModal.jsx`: the % buttons are replaced by a ₹ box, plus "Remove discount". It's saved as a Flat discount with the reason
  "Super Admin discount ₹N" (or "Admin discount ₹N" for an admin with the discount permission).
- `BillingService.ApplyDiscountAsync`: a Flat discount on a **running** session may be more than the bill so far (it's meant for the
  final bill). Negative amounts are refused.
- `SessionService` stop: the recorded discount is capped at what the bill came to (₹100 given on a ₹30 session records ₹30, and the customer pays ₹0).
- **Existing bug fixed (found in testing, live today):** when a member's session ended, the wallet was charged
  GamingAmount + FoodAmount in full and **ignored the discount**. The bill showed the discount, but the wallet paid the sticker price.
  Now the wallet pays the discounted total: discount off gaming first, then food.

## Test (scratch DB)
- Walk-in: ₹40 at minute 0, played 2h10m: ₹130 − ₹40 = pays ₹90. PASS
- ₹100 on a ₹30 session: pays ₹0, discount recorded ₹30. PASS
- Negative amount: refused. PASS
- Member 1h, no discount: wallet −₹60. PASS
- Member 1h, ₹40 off: bill ₹20, wallet −₹20 (before the fix it was −₹60). PASS
- Member 1h + ₹50 food, ₹80 off: bill ₹30, gaming wallet −₹0, food wallet −₹30. PASS
