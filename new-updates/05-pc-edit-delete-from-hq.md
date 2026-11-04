# Count 5 — Super Admin edits or deletes a PC, and the change doesn't show

**Status:** Built + tested locally (4 Nov 2026). Not shipped; waits for the combined release.
**Where:** HQ + Branch. Source: owner's PRD "Bug 1" (4 Nov 2026), baseline `main` @ d4fd199.

## What the owner said
Settings → Branches → Manage PC Fleet (video: Varachha). After an edit or delete, the server page doesn't change.
All PCs show AWAITING SETUP.

## How it works now
HQ doesn't change the PC itself. It queues a command (`update_pc` / `delete_pc`, HTTP 202), and the branch runs it on its next
heartbeat (`PcStatusController` Update L232 / Delete L285 → `BranchHeartbeatService` RunUpdatePcAsync / RunDeletePcAsync).

## Causes (PRD, checked against code and HQ data on 4 Nov)
1. HQ's own copy is never updated, and the heartbeat sends only PC *state*. Name, number, zone, specs and "deleted" never travel
   back, so HQ shows the old values even after the branch applied the change.
2. The screen says "PC updated/deleted successfully" straight away and refreshes before anything has happened. Failures are never shown.
3. A command fails for good if the branch is offline too long or on an old build. **Seen at HQ:** Varachha had 31 update/delete/add
   commands fail on 30 Oct ("branch did not pick this up… most likely running a version…"). 2 `update_pc` from 01:11 on 4 Nov
   are pending because the branch is shut for the night.
4. **A deleted PC keeps being re-added.** HQ's copy still says "not deleted", so every 15 min it queues `add_pc`, which fails with a
   duplicate key. **Seen at HQ: Katargam 360Hz has 226 failed `add_pc` (latest 3 Nov 23:39).** This is the PC 01 that shows at HQ
   but not at the counter.
5. Remote delete doesn't check for a running session (confirmed: `RunDeletePcAsync` has no Active/Reserved/AwaitingBilling check).
6. Pricing profile can't be changed from the edit form (the field isn't carried).
7. The branch screens don't refresh live after a PC change (they wait for the 20 s poll).

## Proposed fix (PRD R1.1–R1.8)
- HQ shows "Sending to branch…", then "Done", or the branch's reason it failed, on that PC row.
- When the branch reports success, HQ applies the same change to its own copy, so both sides match.
- PC changes made at the branch itself also reach HQ (lower priority).
- An old-build "unknown command" waits and retries instead of failing for good.
- Delete is refused while the PC is in use ("PC is in use").
- A deleted PC is never re-added by the 15-minute check. One-time cleanup: Katargam PC 01 at HQ.
- The pricing profile is carried through edit.
- Branch screens refresh live after a PC change.

## Acceptance (PRD)
1. A name edit shows at HQ and at the branch within ~10 s, without F5.
2. Deleting an idle PC removes it everywhere, and it stays gone after 30 min.
3. Deleting an active PC is refused, and HQ shows "PC is in use".
4. Branch offline: "Queued — branch not reporting", then it applies when the branch is back.
5. A duplicate number shows the rejection reason, and HQ's copy is unchanged.
6. Old build: stays pending, then applies after update.
7. A pricing profile change persists on both sides.

## Built (4 Nov 2026)
- New `Api/Services/PcFleetEdits.cs`: one set of rules (edit incl. pricing profile, "in use" check, delete **releases MachineId/Token**)
  used by the branch's own Settings, the branch running HQ's command, and HQ updating its own copy.
- Branch `RunUpdatePcAsync` / `RunDeletePcAsync`: shared rules. Delete is refused while Active/Reserved/AwaitingBilling. Both broadcast `PcManagementUpdated`.
- `PcsController`: pricing profile carried and checked to belong to the branch. HQ refuses deleting a PC the branch reports in use.
  `details` now returns `pricingProfileId` and `lastBranchCommand` (status + reason) per PC.
- HQ `CommandResult`: on success, HQ applies the same edit/delete to its own copy. An "unknown command" from an old build now waits
  (update/delete/add PC, member edit, bonus).
- **Real cause of Varachha's 31 failures:** HQ gave up on a PC command after **5 minutes** (the message said "0 hour(s)"), so anything sent
  while the branch was shut failed when it opened. Setup commands (PC add/edit/delete, member edit) now wait 48 h like member passwords.
- HQ's 15-minute "missing PC" check skips PCs with a delete sent, and marks a branch-confirmed delete as deleted at HQ (heals Katargam PC 01).
- New `GET /api/branch-status/commands/{id}` (Super Admin/Admin) and client `utils/branchCommand.js`: "Sending to branch…", then done or the refusal reason.
- Settings fleet list: a row badge for pending/refused, toasts follow the real outcome, and a live refresh on `PcManagementUpdated`.

## Test (scratch DBs; branch-mode and HQ-mode servers; sync isolated)
Branch: in-use delete refused · edit + pricing profile saved · other-branch profile refused · idle delete OK, MachineId released · screens told live. PASS
HQ: edit queued + row "sending" · old build waits · branch confirms, HQ copy updated (name + profile) · branch refusal leaves HQ unchanged + shows reason ·
in-use delete refused · delete gone from HQ only after confirm, machine released · a branch-confirmed deleted PC is not re-added while a truly missing one still is. PASS (all)
