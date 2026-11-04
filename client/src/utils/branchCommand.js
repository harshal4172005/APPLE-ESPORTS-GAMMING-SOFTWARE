import api from '../config/api';

/**
 * Head Office sends an edit to the branch and gets back a command id, not a result. This waits
 * for the branch to say what happened - "succeeded", "failed" (with the branch's reason), or
 * still "pending" if the branch has not picked it up yet (offline, or shut for the night).
 */
export async function waitForBranchCommand(commandId, { timeoutMs = 45000, everyMs = 2000 } = {}) {
  if (!commandId) return { status: 'unknown' };
  const until = Date.now() + timeoutMs;
  let last = { status: 'pending' };
  while (Date.now() < until) {
    await new Promise(r => setTimeout(r, everyMs));
    try {
      const res = await api.get(`/branch-status/commands/${commandId}`);
      last = res.data?.data || last;
      if (last.status === 'succeeded' || last.status === 'failed') return last;
    } catch { /* keep waiting; a blip is not an answer */ }
  }
  return last;
}

/** Toasts the outcome of a queued branch command. Returns the final status. */
export async function reportBranchCommand(toast, result, { done, refresh } = {}) {
  const queued = result?.data?.queued ? result.data : result?.queued ? result : null;
  if (!queued) { toast.success(done); refresh?.(); return 'succeeded'; }

  toast.info(queued.branchIsReporting === false
    ? 'Queued — the branch is not reporting right now. It will be applied when it is back.'
    : 'Sending to branch…');
  refresh?.();
  const outcome = await waitForBranchCommand(queued.commandId);
  if (outcome.status === 'succeeded') toast.success(`${done} at the branch.`);
  else if (outcome.status === 'failed') toast.error(`Branch refused: ${outcome.message || 'no reason given'}`);
  else toast.info('Still waiting for the branch. It will be applied when the branch picks it up.');
  refresh?.();
  return outcome.status;
}
