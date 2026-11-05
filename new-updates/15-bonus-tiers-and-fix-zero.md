# Count 15 — Bonus tiers per amount + fix 0% bug

**Status:** Built (5 Nov 2026). Not shipped; waits for the next release.
**Where:** Branch + HQ — Settings > System Config > Member Amount Top-Up Settings + Top-Up Modal.

## What the owner asked
"I want a '+' button so super admin can add specific bonus for specific amount. And if I write
0% bonus then it should be 0% — right now it still gives 10%."

## Root cause of 0% bug
Both `WalletService.cs` and `WalletSettingsController.cs` had a hardcoded fallback constant
`DefaultBonusPercent = 10m`. When no `system_configs` row existed (fresh install or never saved),
the 10% fallback kicked in even though the admin intended 0%.

## What was built

### Backend
- **Fallback changed** from 10% to 0% in both `WalletService` and `WalletSettingsController`.
  Now: no config row = 0% bonus, as safe default.
- **`BonusTierDto`** added: `{ MinAmount, BonusPercent }`.
- **`WalletTopUpRulesDto.BonusTiers`** — nullable list of tiers, stored as JSON in the same
  `system_configs` row.
- **`ResolveBonusPercent(rules, amount)`** — picks the highest tier whose `MinAmount <= amount`,
  falls back to `DefaultBonusPercent` when no tier matches.
- **`TopUpWalletAsync`** now calls `ResolveBonusPercent` instead of using a flat default.
  Super Admin's per-top-up override still wins when set.

### Frontend — Settings card (`SystemConfigTab.jsx`)
- **Bonus Tiers section** with "Add Tier" button.
- Each tier row: "Min amount (₹)" → "Bonus %" with a delete button.
- Tiers are sorted by amount on save; empty/invalid rows are stripped.
- Example text: "₹500 → 5% and ₹1000 → 10% means a ₹700 top-up gets 5% bonus."

### Frontend — Top-Up Modal (`MembersPage.jsx`)
- Initial bonus % default changed from 10 to 0 (matches server).
- `useEffect` auto-picks the matching tier as the operator types the amount.
  Super Admin's custom bonus override still works as before.

## No new endpoints, no migrations
The tiers are stored inside the existing `WalletTopUpRulesDto` JSON in `system_configs`.
