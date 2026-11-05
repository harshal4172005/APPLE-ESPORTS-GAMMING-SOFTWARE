# 22. Short Session Slab Pricing

## What changed
Inside each Pricing Profile, super admin can now set a **Short Session Rates** table — fixed charges for sub-hour sessions instead of proportional hourly billing.

Example (₹60/hr profile):
| Time        | Charge |
|-------------|--------|
| 1-10 min    | ₹0 (buffer) |
| 11-20 min   | ₹20   |
| 21-30 min   | ₹40   |
| 31-40 min   | ₹40   |
| 41-50 min   | ₹50   |
| 51-60 min   | ₹60   |

After the last slab (or if no slab matches), normal hourly rate kicks in.

## Where it shows
- Gaming counter PC cards (live charge)
- Billing counter (live bill amount)
- Head Office remote view
- Customer-facing lock screen on the gaming PC
- Public display endpoint

## Settings
Settings > Pricing Profiles > Edit Profile > **Short Session Rates** section (below Buffer, above Refresh Rate). Add/remove rows, each row = From minutes, To minutes, Charge amount.

## Sync
Slabs sync from HQ to branches via heartbeat, same as packages.

## Files touched
- `PricingProfile.cs` — new `ShortSessionSlabs` jsonb column
- `SessionPricingCalculator.cs` — slab check before hourly rate
- `PcStatusService`, `BillingService`, `SessionService`, `PublicController` — pass slabs
- `PcDetailPanel.jsx` — client-side slab lookup
- `PricingProfilesTab.jsx` — slab editor UI
- `BranchConfigDto`, `BranchHeartbeatController`, `BranchHeartbeatService` — sync
- `HubNotificationService`, `IHubNotificationService` — slabs in unlock command
- `LockScreen.xaml.cs`, `SessionControlService.cs` — WinForms client slab support
- EF migration `AddShortSessionSlabs`
