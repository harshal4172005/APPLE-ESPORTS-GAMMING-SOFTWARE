# Count 19 — Power cut: pause timer and money, resume on restart

**Status:** Built (5 Nov 2026). Backend + frontend fix.
**Where:** Branch + Gaming PC + Head Office.

## What the owner asked
"When power cut happens the system never stops counting money. System should stop. If PC restarts,
system should stop counting money and time should stop. When system starts again and they enter
password or it opens directly, resume time and start counting money again. Currently this is not
happening — when power goes it is counting money with time."

## What was wrong
The **server** already paused correctly: `SessionDowntimeRecovery` at startup detects the gap since
last heartbeat, credits `PausedSeconds` on the session, sets state to `Interrupted`, and
`SessionTimeCalculator.ElapsedMinutes` subtracts `PausedSeconds` when computing TotalAmount.

The **client** ignored all of this. `useElapsedTime()` in `PcDetailPanel.jsx` computed pure
wall-clock elapsed (`Date.now() − startTime`) and never subtracted `PausedSeconds`. The DTO did not
even carry `PausedSeconds` to the frontend.

## What was done
1. Added `PausedSeconds` to `PcStatusDto` (already existed on Session entity).
2. Populated `dto.PausedSeconds = session.PausedSeconds` in `PcStatusService` (branch path).
3. Added `CurrentSessionPausedSeconds` to `Pc` entity + EF migration, so the heartbeat snapshot
   carries it to Head Office.
4. `BranchHeartbeatService` populates `SessionPausedSeconds` from the real session.
5. `BranchHeartbeatController` copies it to `pc.CurrentSessionPausedSeconds`.
6. `PcStatusService` Head Office path uses `pc.CurrentSessionPausedSeconds` instead of `0`.
7. `useElapsedTime(startTimeIso, pausedSeconds)` now subtracts `pausedSeconds * 1000` from the
   wall-clock diff. Called with `pc.pausedSeconds`.

Result: after a power cut, the timer and money counter on screen show the correct reduced amount
matching the server's calculation, both on the branch and at Head Office.
