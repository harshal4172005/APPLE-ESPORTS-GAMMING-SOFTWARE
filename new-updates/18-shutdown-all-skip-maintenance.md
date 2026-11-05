# Count 18 — Shutdown All skips maintenance PCs

**Status:** Built (5 Nov 2026). Not shipped; waits for the next release.
**Where:** Branch — Sessions page "Shut Down All PCs" button, server hub.

## What the owner asked
"When we do Shutdown All, maintenance PC should not get shutdown because maintenance work is
going on."

## Root cause
`SendShutdownAllCommand` in `PcStatusHub` excluded Active, AwaitingBilling, and AwaitingSetup
PCs but did NOT exclude `UnderMaintenance`. Maintenance PCs were treated as idle and shut down.

## What was built
- Added `PcState.UnderMaintenance` to the exclusion list in `SendShutdownAllCommand`.
- One server change, zero frontend changes.
