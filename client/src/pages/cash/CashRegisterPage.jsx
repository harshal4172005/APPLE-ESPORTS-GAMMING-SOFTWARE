import { useState, useEffect, useCallback, useMemo, useRef, Fragment } from 'react';
import { useNavigate } from 'react-router-dom';
import { Lock, AlertTriangle, Calculator, LogOut, ArrowLeft, Calendar, Download, History, ChevronDown, ChevronRight } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useBranch } from '../../contexts/BranchContext';
import api from '../../config/api';
import { getCashReconciliationReport } from '../../api/reports.api';
import PageHeader from '../../components/layout/PageHeader';
import DenominationCounter from '../../components/cash/DenominationCounter';
import { MissingStockCount, MissingStockPay } from '../../components/cash/MissingStockSteps';
import { generateIdempotencyKey } from '../../utils/idempotency';
import { format } from 'date-fns';
import { createReport, addTable, save } from '../../utils/pdfReport';

const todayIso = () => format(new Date(), 'yyyy-MM-dd');

// Same per-browser remembered range as Cash Desk/Food Orders history.
const readStoredDate = (key) => {
  try {
    return localStorage.getItem(key) || todayIso();
  } catch {
    return todayIso();
  }
};

// A register's shift-start and shift-end stock counts, under its note breakdown: who counted,
// when, and every item with what the system said - missing or extra in colour, like cash.
function StockCounts({ counts }) {
  if (!counts?.length) {
    return <div className="mt-2 text-text-3 italic font-sans">No stock count recorded for this shift.</div>;
  }
  return counts.map((c, i) => (
    <div key={i} className="mt-3 font-sans">
      <div className="text-[10px] text-text-3 font-bold uppercase tracking-wider mb-1">
        Stock {c.kind === 'opening' ? 'at shift start' : 'at shift end'} · counted by{' '}
        <span className="text-neon-blue">{c.operatorName}</span> at {format(new Date(c.countedAt), 'MMM d, hh:mm a')}
      </div>
      <div className="flex flex-wrap gap-x-5 gap-y-1 font-mono">
        {c.lines.map(l => {
          const diff = l.counted - l.system;
          return (
            <span key={l.inventoryId} className="text-text-2">
              {l.itemName} <span className="text-text font-bold">{l.counted}</span>
              {diff !== 0 && (
                <span className={diff < 0 ? 'text-neon-red font-bold' : 'text-neon-orange font-bold'}>
                  {' '}(system {l.system}, {diff < 0 ? `${-diff} missing` : `${diff} extra`})
                </span>
              )}
            </span>
          );
        })}
      </div>
      {c.shortage && <MissingStockLine s={c.shortage} />}
    </div>
  ));
}

// The missing-stock bill a count raised, as it was settled. Figures are never rewritten: a
// credit cleared on a later day only adds that note here - the money counts on that day.
function MissingStockLine({ s }) {
  const rs = (n) => `₹${Number(n).toFixed(2)}`;
  let how;
  if (!s.paid) how = `not paid yet (bill ${s.billNumber})`;
  else if (s.paidWith === 'Credit') {
    how = `${s.cashAmount > 0 ? `Cash ${rs(s.cashAmount)} + ` : ''}Credit ${rs(s.creditAmount)}`
      + (s.creditCleared ? ` - credit cleared ${s.creditClearedAt ? format(new Date(s.creditClearedAt), 'MMM d') : ''}` : ' - pending');
  } else if (s.paidWith === 'Split') how = `Cash ${rs(s.cashAmount)} + Online ${rs(s.onlineAmount)}`;
  else how = `${s.paidWith} ${rs(s.amount)}`;
  return (
    <div className={`mt-1 text-xs font-bold font-sans ${s.paid ? 'text-neon-green' : 'text-neon-red'}`}>
      Missing stock paid by {s.chargedTo}: {rs(s.amount)} ({s.items}) - {how}
    </div>
  );
}

export default function CashRegisterPage() {
  const navigate = useNavigate();
  const { isSuperAdmin, isTrueSuperAdmin, user, logout } = useAuth();
  const { activeBranch, switchBranch } = useBranch();

  const [register, setRegister] = useState(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState(null);

  const [isLocking, setIsLocking] = useState(false);
  const [isClosing, setIsClosing] = useState(false);

  // The one question the system cannot work out for itself: is anyone coming in after this
  // operator? It decides whether the day's takings are totalled and emailed now, and whether
  // the system going quiet tonight is the shop being shut or a fault. Off by default, because
  // a handover between shifts is the ordinary case and wrongly closing the day would send half
  // a day's figures as though they were the whole day's.
  const [closesTradingDay, setClosesTradingDay] = useState(false);

  // Only asked when closesTradingDay is ticked — a normal handover to the next shift needs
  // neither. Next-day float defaults to the branch's fixed setting; cover amount (what the
  // owner takes out) has no sensible default and starts empty.
  const [coverAmount, setCoverAmount] = useState('');
  const [nextDayFloat, setNextDayFloat] = useState('');
  const [nextDayFloatReason, setNextDayFloatReason] = useState('');

  // Stock is shown, not asserted. A tick box saying "I have checked the stock" with nothing to
  // check it against is decoration: it teaches staff to tick without looking, and then the one
  // night something really is missing, that gets ticked too.
  const [stockChecked, setStockChecked] = useState(false);
  const [inventory, setInventory] = useState(null);
  const [counted, setCounted] = useState({});
  const [stockBusy, setStockBusy] = useState(false);
  const [stockError, setStockError] = useState('');
  const [isCancelling, setIsCancelling] = useState(false);

  // Super Admin's per-branch switch (Branch.ChargeMissingStock). On: stock is counted first,
  // anything missing is billed to this operator and paid, and only then is the register
  // locked. Off - or not known yet, or not answerable - is exactly the old screen.
  const [missingStatus, setMissingStatus] = useState(null);
  const missingEnabled = !!missingStatus?.enabled;

  // Whether the branch's last close was a genuine day close (so "No Register Open" can say so
  // plainly) and, for Admin/Super Admin, a way to undo it if it was a mistake.
  const [dayClosed, setDayClosed] = useState(false);
  const [isReopening, setIsReopening] = useState(false);
  const [reopenError, setReopenError] = useState(null);

  const targetBranchId = isSuperAdmin ? activeBranch?.id : user?.branchId;

  // Read-only history — a look back at what an operator counted opening/closing on a past
  // day, separate from the live counter above. Same date-range-plus-print pattern already on
  // Cash Desk/Online Desk/Food Orders History, and the same reconciliation report Cash Desk's
  // own history already uses (opened/closed, expected vs counted, difference, denominations).
  const [historyFrom, setHistoryFrom] = useState(() => readStoredDate('cashRegister.historyFrom'));
  const [historyTo, setHistoryTo] = useState(() => readStoredDate('cashRegister.historyTo'));
  const [history, setHistory] = useState(null);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState(null);
  const [historyOperator, setHistoryOperator] = useState('all');
  const [expandedRegisterId, setExpandedRegisterId] = useState(null);

  useEffect(() => {
    try { localStorage.setItem('cashRegister.historyFrom', historyFrom); } catch { /* ignore */ }
  }, [historyFrom]);

  useEffect(() => {
    try { localStorage.setItem('cashRegister.historyTo', historyTo); } catch { /* ignore */ }
  }, [historyTo]);

  const fetchHistory = useCallback(async () => {
    if (isSuperAdmin && !targetBranchId) {
      setHistory(null);
      return;
    }
    setHistoryLoading(true);
    try {
      setHistoryError(null);
      const { data } = await getCashReconciliationReport({
        branchId: targetBranchId,
        startDate: historyFrom,
        endDate: historyTo,
      });
      setHistory(data.data || []);
    } catch (err) {
      setHistoryError(err.response?.data?.error || 'Failed to fetch cash register history.');
    } finally {
      setHistoryLoading(false);
    }
  }, [targetBranchId, isSuperAdmin, historyFrom, historyTo]);

  useEffect(() => { fetchHistory(); }, [fetchHistory]);

  const historyOperators = useMemo(
    () => Array.from(new Set((history || []).map(r => r.operatorName))).sort(),
    [history]
  );
  const filteredHistory = useMemo(
    () => (history || []).filter(r => historyOperator === 'all' || r.operatorName === historyOperator),
    [history, historyOperator]
  );

  const resetHistoryToToday = () => {
    setHistoryFrom(todayIso());
    setHistoryTo(todayIso());
    setHistoryOperator('all');
  };

  const handleDownloadHistoryPdf = () => {
    if (filteredHistory.length === 0) return;
    const rangeLabel = historyFrom === historyTo ? historyFrom : `${historyFrom} to ${historyTo}`;
    const operatorLabel = historyOperator === 'all' ? '' : `  •  ${historyOperator}`;
    const subtitle = `${activeBranch?.name || 'Branch'}  •  ${rangeLabel}${operatorLabel}`;
    const { doc } = createReport({ title: 'Cash Register History', subtitle });

    addTable(doc, 90, {
      title: 'Cash Register History', subtitle,
      head: ['Opened', 'Closed', 'Operator', 'Expected', 'Counted', 'Difference', 'Status'],
      body: filteredHistory.map(r => [
        format(new Date(r.openedAt), 'MMM d, hh:mm a'),
        r.closedAt ? format(new Date(r.closedAt), 'MMM d, hh:mm a') : 'Still open',
        r.operatorName,
        `Rs ${r.expectedDrawerCash.toFixed(2)}`,
        `Rs ${r.physicalCashCounted.toFixed(2)}`,
        `Rs ${r.difference.toFixed(2)}`,
        r.isVerified ? 'Verified' : 'Unverified',
      ]),
    });

    save(doc, `cash-register-history-${historyFrom}${historyFrom !== historyTo ? `_to_${historyTo}` : ''}.pdf`);
  };

  // True only until the very first fetch (success or failure) has completed. A cold app
  // launch on the kiosk PC can call this before WebView2's own persisted cookie store has
  // finished attaching to the request - the page believes it is logged in (from the cached
  // user in localStorage) a moment before the browser layer actually has the session cookie
  // to prove it, so the very first /cash/active call can 404 even though a shift genuinely
  // is open. Reported as "sometimes shows no active shift, fixed by closing and reopening the
  // app" - a full relaunch just gives that race more time to resolve before this page asks.
  // Retrying a couple of times, a moment apart, gives it that same time without the relaunch.
  const isFirstFetch = useRef(true);

  const fetchActiveRegister = useCallback(async (attempt = 1) => {
    if (isSuperAdmin && !targetBranchId) {
      setRegister(null);
      setIsLoading(false);
      isFirstFetch.current = false;
      return;
    }

    try {
      setError(null);
      const { data } = await api.get('/cash/active', { params: { branchId: targetBranchId } });
      setRegister(data.data);
      isFirstFetch.current = false;
    } catch (err) {
      if (err.response?.status === 404) {
        if (isFirstFetch.current && attempt < 3) {
          setTimeout(() => fetchActiveRegister(attempt + 1), 1500);
          return;   // still loading - not yet a real "no active shift" answer
        }
        setRegister(null);
        isFirstFetch.current = false;
      } else {
        setError(err.response?.data?.error || err.response?.data?.message || 'Failed to fetch cash register');
        isFirstFetch.current = false;
      }
    } finally {
      if (!isFirstFetch.current) setIsLoading(false);
    }
  }, [targetBranchId, isSuperAdmin]);

  useEffect(() => {
    setIsLoading(true);
    isFirstFetch.current = true;
    fetchActiveRegister();
  }, [fetchActiveRegister]);

  // Only asked for Admin/Super Admin, and only once there's confirmed to be nothing open - this
  // is what tells "the day was properly closed" apart from "nobody has opened the drawer yet",
  // and is also how the Undo button below knows there's a day close to undo.
  useEffect(() => {
    if (isLoading || register || !isSuperAdmin || !targetBranchId) {
      setDayClosed(false);
      return;
    }
    let alive = true;
    (async () => {
      try {
        const { data } = await api.get('/cash/opening', { params: { branchId: targetBranchId } });
        if (alive) setDayClosed(data?.data?.isFirstOfDay === true);
      } catch {
        if (alive) setDayClosed(false);
      }
    })();
    return () => { alive = false; };
  }, [isLoading, register, isSuperAdmin, targetBranchId]);

  const handleReopenDay = async () => {
    setIsReopening(true);
    setReopenError(null);
    try {
      await api.post('/cash-desk/reopen-day', {}, {
        headers: { 'X-Idempotency-Key': generateIdempotencyKey() }
      });
      setDayClosed(false);
      await fetchActiveRegister();
    } catch (err) {
      setReopenError(err.response?.data?.error || err.response?.data?.message || 'Failed to undo the day close.');
    } finally {
      setIsReopening(false);
    }
  };

  // Defense-in-depth for the live ForceLogout push (see SocketContext.jsx): a missed socket
  // event - a reconnect gap, the tab was backgrounded when it fired - must not leave this
  // screen showing a register as open indefinitely after a Super Admin has force-closed the
  // shift behind it. Refetching on focus/visibility catches up within one glance at the tab,
  // without polling constantly while nobody is looking at it.
  useEffect(() => {
    const onFocus = () => { if (document.visibilityState !== 'hidden') fetchActiveRegister(); };
    window.addEventListener('focus', onFocus);
    document.addEventListener('visibilitychange', onFocus);
    return () => {
      window.removeEventListener('focus', onFocus);
      document.removeEventListener('visibilitychange', onFocus);
    };
  }, [fetchActiveRegister]);

  const handleStartVerification = async () => {
    setIsLocking(true);
    try {
      await api.post('/cash-desk/verify-start', {}, {
        headers: { 'X-Idempotency-Key': generateIdempotencyKey() }
      });
      await fetchActiveRegister();
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Failed to lock register for verification.');
    } finally {
      setIsLocking(false);
    }
  };

  const fetchMissingStatus = useCallback(async () => {
    if (!register?.id || isTrueSuperAdmin) return;
    try {
      const { data } = await api.get('/inventory/missing-stock', { params: { cashRegisterId: register.id } });
      setMissingStatus(data?.data || { enabled: false, closingCountDone: false, pending: [] });
    } catch {
      setMissingStatus({ enabled: false, closingCountDone: false, pending: [] });
    }
  }, [register?.id, isTrueSuperAdmin]);

  useEffect(() => { fetchMissingStatus(); }, [fetchMissingStatus]);

  // Loaded once the cash is counted, which is when this screen reaches the stock step - or,
  // where missing stock is charged, at the start, because that count comes first.
  useEffect(() => {
    const wantsStock = register?.status === 'Verified'
      || (register?.status === 'Open' && missingEnabled && !missingStatus?.closingCountDone);
    if (!wantsStock || isTrueSuperAdmin || inventory !== null) return;
    let alive = true;
    (async () => {
      try {
        const { data } = await api.get('/inventory');
        const items = data?.data?.items || data?.data || [];
        if (!alive) return;
        setInventory(items);
        const seed = {};
        items.forEach((i) => { seed[i.id] = String(i.currentStock ?? i.CurrentStock ?? 0); });
        setCounted(seed);
      } catch (err) {
        if (alive) setStockError(err.response?.data?.error || 'Could not load the stock list.');
      }
    })();
    return () => { alive = false; };
  }, [register?.status, isTrueSuperAdmin, inventory, missingEnabled, missingStatus?.closingCountDone]);

  // The whole count is saved against this register - every item, including the ones that
  // matched - so Super Admin sees who counted what under the note breakdown in the history.
  const confirmStock = async () => {
    setStockBusy(true);
    setStockError('');
    try {
      const counts = (inventory || []).map(item => {
        const was = Number(item.currentStock ?? item.CurrentStock ?? 0);
        const now = Number(counted[item.id]);
        return { inventoryId: item.id, counted: Number.isNaN(now) || counted[item.id] === undefined || counted[item.id] === '' ? was : now };
      });
      if (counts.length > 0) {
        await api.post('/inventory/stock-count', { kind: 'closing', cashRegisterId: register?.id, counts });
      }
      setStockChecked(true);
    } catch (err) {
      setStockError(err.response?.data?.error || 'Could not save the stock counts.');
    } finally {
      setStockBusy(false);
    }
  };

  const coverAmountNum = Number(coverAmount) || 0;
  const nextDayFloatNum = Number(nextDayFloat) || 0;
  const dayCloseRemainder = (register?.physicalCashCounted ?? 0) - coverAmountNum - nextDayFloatNum;
  const dayCloseNeedsReason = closesTradingDay && coverAmount !== '' && nextDayFloat !== '' && dayCloseRemainder !== 0;

  const handleCloseShift = async () => {
    setIsClosing(true);
    try {
      await api.post(`/cash-desk/close/${register.id}`, closesTradingDay ? {
        closesTradingDay: true,
        coverAmount: coverAmountNum,
        nextDayOpeningBalance: nextDayFloatNum,
        reason: dayCloseRemainder !== 0 ? nextDayFloatReason.trim() : undefined,
      } : {}, {
        headers: { 'X-Idempotency-Key': generateIdempotencyKey() }
      });

      if (isTrueSuperAdmin) {
        // Only a true Super Admin has no personal shift to end — an Admin-mode operator
        // (Quick Admin Switch) is the same physical person who was trading, and still needs
        // to be logged out below like any other shift ending.
        switchBranch(null);
        navigate('/app/dashboard');
      } else {
        // Operator's shift is over — log them out, carrying their answer about the day so the
        // server can close the trading day and send its totals.
        logout({ closesTradingDay });
      }
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Failed to close shift.');
      setIsClosing(false);
    }
  };

  const handleBackButton = async () => {
    if (register?.status !== 'Verifying') {
      navigate(-1);
      return;
    }

    setIsCancelling(true);
    try {
      // Undo the lock — register goes back to Open, shift is untouched.
      await api.post(`/cash-desk/cancel-verification/${register.id}`, {}, {
        headers: { 'X-Idempotency-Key': generateIdempotencyKey() }
      });
      navigate(-1);
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Failed to cancel verification.');
      setIsCancelling(false);
    }
  };

  // Built once here rather than duplicated in every branch below — it is a past-day lookup,
  // not today's live drawer, so it renders the same regardless of whether a register happens
  // to be open right now (same reasoning as CashDeskPage's own historySection).
  const historySection = (
    <div className="mt-6 bg-bg-2 border border-border rounded-xl p-4 max-w-4xl mx-auto w-full">
      <h3 className="text-text font-bold uppercase tracking-wider text-sm mb-4 border-b border-border pb-3 flex items-center gap-2">
        <History className="w-4 h-4" /> Cash Register History
      </h3>

      <div className="flex flex-wrap items-center gap-3 mb-4">
        <Calendar className="w-4 h-4 text-text-3 shrink-0" />
        <div className="flex items-center gap-2">
          <label className="text-xs text-text-3 uppercase tracking-wider">From</label>
          <input
            type="date"
            value={historyFrom}
            max={historyTo}
            onChange={(e) => setHistoryFrom(e.target.value)}
            className="bg-bg-3 border border-border rounded-lg px-3 py-1.5 text-sm text-text"
          />
        </div>
        <div className="flex items-center gap-2">
          <label className="text-xs text-text-3 uppercase tracking-wider">To</label>
          <input
            type="date"
            value={historyTo}
            min={historyFrom}
            max={todayIso()}
            onChange={(e) => setHistoryTo(e.target.value)}
            className="bg-bg-3 border border-border rounded-lg px-3 py-1.5 text-sm text-text"
          />
        </div>
        <div className="flex items-center gap-2">
          <label className="text-xs text-text-3 uppercase tracking-wider">Operator</label>
          <select
            value={historyOperator}
            onChange={(e) => setHistoryOperator(e.target.value)}
            className="bg-bg-3 border border-border rounded-lg px-3 py-1.5 text-sm text-text"
          >
            <option value="all">All operators</option>
            {historyOperators.map(name => <option key={name} value={name}>{name}</option>)}
          </select>
        </div>
        <button
          onClick={resetHistoryToToday}
          className="text-xs font-medium text-accent hover:text-accent/80 transition-colors ml-auto"
        >
          Today
        </button>
        <button
          onClick={handleDownloadHistoryPdf}
          disabled={filteredHistory.length === 0}
          className="btn-secondary py-1.5 px-3 flex items-center gap-1.5 text-xs font-bold disabled:opacity-50 disabled:cursor-not-allowed"
        >
          <Download className="w-3.5 h-3.5" /> Download PDF
        </button>
      </div>

      {historyError && (
        <div className="bg-neon-red/10 border border-neon-red/30 text-neon-red p-3 rounded-xl mb-4 flex items-center gap-2 text-sm">
          <AlertTriangle className="w-4 h-4" /> {historyError}
        </div>
      )}

      {historyLoading ? (
        <div className="flex justify-center py-8">
          <div className="w-6 h-6 border-2 border-accent border-t-transparent rounded-full animate-spin" />
        </div>
      ) : filteredHistory.length === 0 ? (
        <div className="text-center text-text-3 text-xs italic py-8 border border-dashed border-border rounded-lg">
          No cash registers found in this range.
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
            <thead>
              <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                <th className="py-2 px-3"></th>
                <th className="py-2 px-3">Opened</th>
                <th className="py-2 px-3">Closed</th>
                <th className="py-2 px-3">Operator</th>
                <th className="py-2 px-3 text-right">Expected</th>
                <th className="py-2 px-3 text-right">Counted</th>
                <th className="py-2 px-3 text-right">Difference</th>
                <th className="py-2 px-3 text-center">Status</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/40 font-mono">
              {filteredHistory.map(r => {
                const isExpanded = expandedRegisterId === r.cashRegisterId;
                const denominations = [
                  ['₹2000', r.notes2000], ['₹500', r.notes500], ['₹200', r.notes200],
                  ['₹100', r.notes100], ['₹50', r.notes50], ['₹20', r.notes20], ['₹10', r.notes10],
                  ['₹5', r.coins5], ['₹2', r.coins2], ['₹1', r.coins1],
                ].filter(([, count]) => count > 0);
                return (
                  <Fragment key={r.cashRegisterId}>
                    <tr
                      className="cursor-pointer hover:bg-bg-3/50"
                      onClick={() => setExpandedRegisterId(isExpanded ? null : r.cashRegisterId)}
                    >
                      <td className="py-2 px-3 text-text-3">
                        {isExpanded ? <ChevronDown className="w-3.5 h-3.5" /> : <ChevronRight className="w-3.5 h-3.5" />}
                      </td>
                      <td className="py-2 px-3 text-text-2">{format(new Date(r.openedAt), 'MMM d, hh:mm a')}</td>
                      <td className="py-2 px-3 text-text-2">{r.closedAt ? format(new Date(r.closedAt), 'MMM d, hh:mm a') : 'Still open'}</td>
                      <td className="py-2 px-3 text-neon-blue font-bold font-sans">{r.operatorName}</td>
                      <td className="py-2 px-3 text-right text-text">₹{r.expectedDrawerCash.toFixed(2)}</td>
                      <td className="py-2 px-3 text-right text-text">₹{r.physicalCashCounted.toFixed(2)}</td>
                      <td className={`py-2 px-3 text-right font-bold ${r.difference === 0 ? 'text-text-2' : r.difference < 0 ? 'text-neon-red' : 'text-neon-orange'}`}>
                        ₹{r.difference.toFixed(2)}
                      </td>
                      <td className="py-2 px-3 text-center">
                        <span className={`text-[10px] px-1.5 py-0.5 rounded border uppercase tracking-wider font-bold ${
                          r.isVerified ? 'text-neon-green bg-neon-green/10 border-neon-green/20' : 'text-neon-orange bg-neon-orange/10 border-neon-orange/20'
                        }`}>
                          {r.isVerified ? 'Verified' : 'Unverified'}
                        </span>
                      </td>
                    </tr>
                    {isExpanded && (
                      <tr>
                        <td colSpan={8} className="py-2 px-3 bg-bg-3/30">
                          <div className="text-[10px] text-text-3 font-bold uppercase tracking-wider mb-1 font-sans">Cash</div>
                          {denominations.length === 0 ? (
                            <span className="text-text-3 italic">
                              {r.closedAt
                                ? `Counted ₹${r.physicalCashCounted.toFixed(2)} — breakdown not recorded.`
                                : 'No denomination count recorded.'}
                            </span>
                          ) : (
                            <div className="flex flex-wrap gap-x-5 gap-y-1">
                              {denominations.map(([label, count]) => (
                                <span key={label} className="text-text-2">
                                  {label} <span className="text-text font-bold">× {count}</span>
                                </span>
                              ))}
                            </div>
                          )}
                          <StockCounts counts={r.stockCounts} />
                        </td>
                      </tr>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );

  if (isSuperAdmin && !activeBranch) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
        <Lock className="w-12 h-12 text-text-3 mb-4" />
        <h2 className="text-xl font-heading font-bold text-text mb-2">Select a Branch</h2>
        <p className="text-text-2">You must select a branch to access the Cash Register.</p>
      </div>
    );
  }

  if (isLoading) {
    return (
      <div className="flex justify-center items-center min-h-[60vh]">
        <div className="w-8 h-8 rounded-full border-2 border-accent border-t-transparent animate-spin" />
      </div>
    );
  }

  // 1. If no active register, nothing to close — history still shows underneath.
  if (!register) {
    return (
      <>
        <div className="flex flex-col items-center justify-center min-h-[40vh] text-center">
          <AlertTriangle className="w-12 h-12 text-neon-orange mb-4" />
          <h2 className="text-xl font-heading font-bold text-text mb-2">
            {isSuperAdmin ? (dayClosed ? 'Day Closed' : 'No Register Open') : 'No Active Shift'}
          </h2>
          <p className="text-text-2">
            {isSuperAdmin
              ? (dayClosed
                ? 'The last shift closed the day. Next shift opens tomorrow.'
                : 'No cash register is currently open at this branch.')
              : 'There is no active cash register open for this shift.'}
          </p>
          {isSuperAdmin && dayClosed && (
            <div className="mt-5">
              {reopenError && <p className="text-neon-red text-xs mb-2">{reopenError}</p>}
              <button
                onClick={handleReopenDay}
                disabled={isReopening}
                className="btn-secondary py-2 px-4 text-xs font-bold uppercase tracking-wider disabled:opacity-50"
              >
                {isReopening ? 'Undoing...' : 'Undo — this was a mistake'}
              </button>
              <p className="text-text-3 text-[11px] mt-2 max-w-sm">
                Only use this if "last shift of the day" was ticked by accident. The drawer
                reopens exactly as it was left.
              </p>
            </div>
          )}
        </div>
        {historySection}
      </>
    );
  }

  // Super Admin: read-only, always — never the Lock/Count/Close flow below. Super Admin is
  // not physically at the branch and cannot count a real drawer (see EnsureNotSuperAdminForCashAsync
  // on the server, which refuses every mutation regardless of what this screen would try to do).
  // Deliberately isTrueSuperAdmin, not the loose isSuperAdmin - an operator who quick-switched to
  // Admin is still physically at the counter and must keep the real Lock/Count/Close flow below.
  if (isTrueSuperAdmin) {
    return (
      <>
        <div className="h-full flex flex-col max-w-4xl mx-auto">
          <div className="mb-6">
            <PageHeader
              title="Cash Register"
              subtitle="Read-only — Super Admin cannot open, count, or close a register"
              icon="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z"
              badge="VIEW ONLY"
            />
          </div>

          <div className="border border-border bg-bg-2 rounded-xl p-6">
            <div className="flex items-center justify-between mb-5">
              <span className="text-text-3 text-xs font-bold uppercase tracking-wider">Status</span>
              <span className="text-[11px] px-2 py-1 rounded border uppercase tracking-wider font-bold text-neon-blue bg-neon-blue/10 border-neon-blue/20">
                {register.status}
              </span>
            </div>
            <div className="space-y-3">
              <div className="flex justify-between items-center">
                <span className="text-text-3 text-xs font-bold uppercase tracking-wider">Opening Balance</span>
                <span className="text-text font-mono font-bold">₹{register.openingBalance}</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-text-3 text-xs font-bold uppercase tracking-wider">Expected Drawer</span>
                <span className="text-text font-mono font-bold">₹{register.expectedDrawerCash}</span>
              </div>
              {register.physicalCashCounted != null && (
                <div className="flex justify-between items-center border-t border-border pt-3">
                  <span className="text-text-3 text-xs font-bold uppercase tracking-wider">Physically Counted</span>
                  <span className="text-text font-mono font-bold">₹{register.physicalCashCounted}</span>
                </div>
              )}
            </div>
          </div>
        </div>
        {historySection}
      </>
    );
  }

  return (
    <>
    <div className="h-full flex flex-col max-w-4xl mx-auto">
      <div className="mb-6 flex items-center gap-4">
        {register.status !== 'Verified' && (
          <button
            onClick={handleBackButton}
            disabled={isCancelling}
            className="p-2 hover:bg-bg-3 rounded-lg transition-colors text-text-3 hover:text-text shrink-0"
            title={register.status === 'Verifying' ? 'Cancel verification and go back' : 'Go back'}
          >
            <ArrowLeft className="w-5 h-5" />
          </button>
        )}
        <div className="flex-1">
          <PageHeader
            title="Cash Register"
            subtitle="End of Shift Reconciliation"
            icon="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z"
            badge="SECURE"
          />
        </div>
      </div>

      {error && (
        <div className="bg-neon-red/10 border border-neon-red/30 text-neon-red p-3 rounded-xl mb-4 flex items-center gap-2 text-sm">
          <AlertTriangle className="w-4 h-4" /> {error}
        </div>
      )}

      {/* Missing stock charged at this branch: count first, then pay, then lock (below). */}
      {register.status === 'Open' && missingEnabled && !missingStatus.closingCountDone && (
        inventory === null
          ? <div className="flex-1 flex items-center justify-center text-text-3 text-sm">{stockError || 'Loading the stock list...'}</div>
          : <MissingStockCount inventory={inventory} registerId={register.id} onDone={fetchMissingStatus} />
      )}
      {register.status === 'Open' && missingEnabled && missingStatus.closingCountDone && missingStatus.pending.length > 0 && (
        <MissingStockPay bills={missingStatus.pending} onPaid={fetchMissingStatus} />
      )}

      {/* STEP 1: Lock Register */}
      {register.status === 'Open' && missingStatus !== null
        && (!missingEnabled || (missingStatus.closingCountDone && missingStatus.pending.length === 0)) && (
        <div className="flex-1 flex flex-col items-center justify-center border border-border bg-bg-2 rounded-xl p-8 text-center">
          <div className="w-20 h-20 bg-neon-orange/10 text-neon-orange rounded-full flex items-center justify-center mb-6">
            <Lock className="w-10 h-10" />
          </div>
          <h2 className="text-2xl font-heading font-bold uppercase tracking-widest text-text mb-3">
            Initiate Snapshot Lock
          </h2>
          <p className="text-text-2 text-sm max-w-md mb-8 leading-relaxed">
            Starting verification will lock the active Cash Register. New payments, inward cash, and adjustments will be temporarily blocked to prevent reconciliation drift.
          </p>
          <button
            onClick={handleStartVerification}
            disabled={isLocking}
            className="btn-primary w-full max-w-sm py-4 shadow-lg shadow-accent/20 flex justify-center items-center"
          >
            {isLocking ? (
              <div className="w-5 h-5 rounded-full border-2 border-current border-t-transparent animate-spin" />
            ) : (
              'Lock Register & Start Count'
            )}
          </button>
        </div>
      )}

      {/* STEP 2: Denomination Verification */}
      {register.status === 'Verifying' && (
        <div className="flex-1 flex flex-col min-h-0">
          <div className="bg-neon-orange/10 border border-neon-orange/30 text-neon-orange p-3 rounded-xl mb-4 flex items-center gap-2 text-xs font-bold uppercase tracking-wider">
            <Lock className="w-4 h-4 shrink-0" /> Snapshot Locked. Register is immune to external cash flow changes.
          </div>
          <DenominationCounter
            expectedTotal={register.expectedDrawerCash}
            onVerified={fetchActiveRegister}
          />
        </div>
      )}

      {/* STEP 3: Finalize Shift */}
      {register.status === 'Verified' && (
        <div className="flex-1 flex flex-col items-center justify-center border border-accent/30 bg-bg-2 rounded-xl p-8 text-center shadow-[0_0_30px_rgba(255,51,102,0.1)]">
          <div className="w-20 h-20 bg-accent/10 text-accent rounded-full flex items-center justify-center mb-6">
            <Calculator className="w-10 h-10" />
          </div>
          <h2 className="text-2xl font-heading font-bold uppercase tracking-widest text-text mb-3">
            Verification Complete
          </h2>
          <div className="bg-bg-3 border border-border rounded-xl p-6 w-full max-w-md mb-8 text-left">
            <div className="flex justify-between items-center mb-3">
              <span className="text-text-3 text-xs font-bold uppercase tracking-wider">Expected Drawer</span>
              <span className="text-text font-mono font-bold">₹{register.expectedDrawerCash}</span>
            </div>
            <div className="flex justify-between items-center mb-3">
              <span className="text-text-3 text-xs font-bold uppercase tracking-wider">Counted Total</span>
              <span className="text-text font-mono font-bold">₹{register.physicalCashCounted}</span>
            </div>
            <div className="flex justify-between items-center border-t border-border pt-3">
              <span className="text-text-3 text-xs font-bold uppercase tracking-wider">Discrepancy</span>
              <span className={`font-mono font-bold ${register.cashDifference === 0 ? 'text-neon-blue' : 'text-neon-red'}`}>
                ₹{register.cashDifference}
              </span>
            </div>
          </div>
          {/* Stock, then the day. Both asked here rather than on a later screen, because this
              is the last moment the operator is still standing at the counter. Not gated on
              isTrueSuperAdmin - by the time render reaches this point, the true-Super-Admin
              case has already returned its own read-only view above, so it can only ever be
              false here. */}
          <div className="w-full max-w-md mb-6 space-y-3 text-left">
              {!missingEnabled && (
              <div className="bg-bg-3 border border-border rounded-xl p-4">
                <div className="flex items-center justify-between mb-1">
                  <span className="text-text text-sm font-bold">Check the stock</span>
                  {stockChecked && <span className="text-neon-green text-[11px] font-bold">CONFIRMED</span>}
                </div>
                <p className="text-text-3 text-[11px] mb-3 leading-relaxed">
                  Count what is actually on the shelf. Change any number that does not match, then confirm.
                </p>

                {inventory === null && !stockError && (
                  <p className="text-text-3 text-xs">Loading the stock list...</p>
                )}

                {inventory !== null && inventory.length === 0 && (
                  <p className="text-text-3 text-xs">Nothing is stocked at this branch, so there is nothing to count.</p>
                )}

                {inventory !== null && inventory.length > 0 && (
                  <div className="max-h-56 overflow-auto -mx-1 px-1">
                    <table className="w-full text-xs">
                      <thead className="text-text-3 text-[10px] uppercase tracking-wider">
                        <tr>
                          <th className="text-left font-normal pb-2">Item</th>
                          <th className="text-right font-normal pb-2">System says</th>
                          <th className="text-right font-normal pb-2 w-24">On the shelf</th>
                        </tr>
                      </thead>
                      <tbody>
                        {inventory.map((item) => {
                          const was = Number(item.currentStock ?? item.CurrentStock ?? 0);
                          const now = Number(counted[item.id]);
                          const differs = !Number.isNaN(now) && now !== was;
                          return (
                            <tr key={item.id} className="border-t border-border/60">
                              <td className="py-2 text-text-2">{item.itemName || item.ItemName || item.name}</td>
                              <td className="py-2 text-right font-mono text-text-3">{was}</td>
                              <td className="py-2 text-right">
                                <input
                                  type="number"
                                  min="0"
                                  value={counted[item.id] ?? ''}
                                  onChange={(e) => { setCounted({ ...counted, [item.id]: e.target.value }); setStockChecked(false); }}
                                  className={differs
                                    ? 'w-20 bg-bg-2 border border-neon-orange text-neon-orange rounded px-2 py-1 text-right font-mono text-xs'
                                    : 'w-20 bg-bg-2 border border-border text-text rounded px-2 py-1 text-right font-mono text-xs'}
                                />
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}

                {stockError && <p className="text-neon-red text-[11px] mt-2">{stockError}</p>}

                {!stockChecked && inventory !== null && (
                  <button
                    onClick={confirmStock}
                    disabled={stockBusy}
                    className="w-full mt-3 py-2 rounded-lg text-xs font-bold uppercase tracking-wider bg-accent/10 border border-accent/40 text-accent hover:bg-accent/20 transition-colors disabled:opacity-50"
                  >
                    {stockBusy ? 'Saving...' : 'Confirm these counts'}
                  </button>
                )}
              </div>
              )}

              <label className="flex items-start gap-3 bg-bg-3 border border-border rounded-xl p-4 cursor-pointer hover:border-neon-red/40 transition-colors">
                <input
                  type="checkbox"
                  checked={closesTradingDay}
                  onChange={(e) => {
                    const checked = e.target.checked;
                    setClosesTradingDay(checked);
                    if (checked && nextDayFloat === '') {
                      setNextDayFloat(String(register.defaultOpeningFloat ?? 1000));
                    }
                  }}
                  className="mt-0.5 w-4 h-4 accent-neon-red cursor-pointer flex-shrink-0"
                />
                <span>
                  <span className="block text-text text-sm font-bold">This is the last shift of the day</span>
                  <span className="block text-text-3 text-[11px] mt-1 leading-relaxed">
                    Only tick this if the shop is closing now and nobody is taking over. The
                    day's totals are sent to the owner, and the system knows it has been shut
                    rather than gone wrong.
                  </span>
                </span>
              </label>

              {closesTradingDay && (
                <div className="bg-bg-3 border border-border rounded-xl p-4 space-y-3">
                  <div>
                    <label className="text-xs uppercase tracking-wider font-bold text-text-2 block mb-1.5">
                      Cover Amount (for the owner)
                    </label>
                    <input
                      type="number"
                      min="0"
                      step="0.01"
                      value={coverAmount}
                      onChange={(e) => setCoverAmount(e.target.value)}
                      placeholder="0.00"
                      className="w-full bg-bg-2 border border-border text-text font-mono rounded-lg py-2 px-3 focus:border-accent focus:ring-1 focus:ring-accent outline-none"
                    />
                    <p className="text-[10px] text-text-3 mt-1">What you're keeping aside for the owner to withdraw.</p>
                  </div>
                  <div>
                    <label className="text-xs uppercase tracking-wider font-bold text-text-2 block mb-1.5">
                      Next-Day Opening Float
                    </label>
                    <input
                      type="number"
                      min="0"
                      step="0.01"
                      value={nextDayFloat}
                      onChange={(e) => setNextDayFloat(e.target.value)}
                      placeholder="0.00"
                      className="w-full bg-bg-2 border border-border text-text font-mono rounded-lg py-2 px-3 focus:border-accent focus:ring-1 focus:ring-accent outline-none"
                    />
                    <p className="text-[10px] text-text-3 mt-1">What's left in the drawer for tomorrow's first shift.</p>
                  </div>
                  {coverAmount !== '' && nextDayFloat !== '' && (
                    <div className={`text-xs font-mono font-bold ${dayCloseRemainder === 0 ? 'text-neon-green' : 'text-neon-orange'}`}>
                      {dayCloseRemainder === 0
                        ? 'Cover amount + next-day float matches what was counted.'
                        : `₹${Math.abs(dayCloseRemainder).toFixed(2)} ${dayCloseRemainder > 0 ? 'left over' : 'short'} against the counted cash.`}
                    </div>
                  )}
                  {dayCloseNeedsReason && (
                    <div>
                      <label className="text-xs uppercase tracking-wider font-bold text-neon-orange block mb-1.5">
                        Why doesn't it add up?
                      </label>
                      <textarea
                        value={nextDayFloatReason}
                        onChange={(e) => setNextDayFloatReason(e.target.value)}
                        rows={2}
                        className="w-full bg-bg-2 border border-neon-orange/40 text-text text-sm rounded-lg p-2.5 focus:border-neon-orange outline-none resize-none"
                      />
                    </div>
                  )}
                </div>
              )}
            </div>

          <button
            onClick={handleCloseShift}
            disabled={
              isClosing ||
              (!missingEnabled && !stockChecked) ||
              (closesTradingDay && (coverAmount === '' || nextDayFloat === '')) ||
              (dayCloseNeedsReason && !nextDayFloatReason.trim())
            }
            className="w-full max-w-md py-4 rounded-xl font-bold uppercase tracking-widest text-sm transition-all bg-accent hover:bg-accent-hover text-white shadow-lg shadow-accent/20 flex justify-center items-center gap-2 disabled:opacity-40 disabled:cursor-not-allowed"
          >
            {isClosing ? (
              <div className="w-5 h-5 rounded-full border-2 border-white border-t-transparent animate-spin" />
            ) : (
              <><LogOut className="w-5 h-5" /> {closesTradingDay ? 'Close The Day & Log Out' : 'End Shift & Log Out'}</>
            )}
          </button>

          {!stockChecked && (
            <p className="text-text-3 text-[11px] mt-3">Confirm the stock counts before you can finish.</p>
          )}
        </div>
      )}
    </div>
    {historySection}
    </>
  );
}
