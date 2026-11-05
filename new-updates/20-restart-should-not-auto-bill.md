# Count 20 — PC restart should not auto-bill; pause and resume instead

**Status:** Built (5 Nov 2026). Same fix as count 19.
**Where:** Branch + Gaming PC + Head Office.

## What the owner asked
"Timer is on and I do restart, then PC automatically goes to billing — I don't want that. It
should not go to billing. Timer and money count should get paused, and when system starts again
it should resume."

## What was wrong
The **server** already handled this correctly:
- `SessionDowntimeRecovery` detects the gap, credits `PausedSeconds`, sets `Interrupted` state.
- `InterruptedSessionsBanner` shows Resume / End per session.
- The session does NOT auto-bill — it goes to `Interrupted`, not `AwaitingBilling`.

The **visible** problem was the same as count 19: the client's live timer kept climbing through the
downtime because `useElapsedTime()` ignored `PausedSeconds`. So it *looked* like billing continued
even though the server was correct.

## What was done
Same fix as count 19 — `useElapsedTime` now subtracts `PausedSeconds` from wall-clock elapsed.
After a restart, the Interrupted Sessions banner appears, and the timer shows the correct paused
amount.
