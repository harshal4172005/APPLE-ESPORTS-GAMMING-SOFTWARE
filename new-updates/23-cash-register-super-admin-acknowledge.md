# 23. Cash Register — Super Admin "Cash Received" Checkbox

## What the owner asked
In the cash register history, add a tick box for super admin to confirm "yes, I received this cash for this day."

## What changed
A new **Received** column appears in the cash register history table (only visible to super admin). Each closed register row gets a checkbox. Ticking it marks that register as acknowledged — the super admin confirming they physically received the cash. Unticking undoes it.

This is completely separate from the existing VERIFIED/UNVERIFIED status (which is automatic: cash counted = expected → Verified).

## Where it runs
- Branch + HQ (web dashboard, super admin only)

## Files touched
- `CashRegister.cs` — new `SuperAdminAcknowledged` (bool) + `AcknowledgedAt` columns
- `ReconciliationReportDtos.cs` — new `SuperAdminAcknowledged` field
- `ReportsService.cs` — maps the field
- `ReportsController.cs` — new `PATCH /api/reports/cash-register/{id}/acknowledge` endpoint (SuperAdmin only)
- `reports.api.js` — new `acknowledgeCashRegister` API call
- `CashRegisterPage.jsx` — checkbox column (super admin only), handler
- EF migration `AddCashRegisterSuperAdminAcknowledged`
