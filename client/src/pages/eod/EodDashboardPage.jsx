import { useState, useEffect, useCallback, useMemo, useRef, Fragment } from 'react';
import { AlertTriangle, FileText, Lock, Monitor, Utensils, Clock, Printer, Download, Wrench, ZapOff, Clock as ClockIcon, Pencil, X } from 'lucide-react';
import { printBill } from '../../utils/printBill';
import { useAuth } from '../../contexts/AuthContext';
import { useBranch } from '../../contexts/BranchContext';
import api from '../../config/api';
import PageHeader from '../../components/layout/PageHeader';
import { useSocket } from '../../contexts/SocketContext';
import { useToast } from '../../components/ui/Toast';
import { editPaymentMethod } from '../../api/billing.api';
import { createReport, addStatGrid, addTable, save, ROW_TINT_RED, ROW_TINT_GREEN, ROW_TINT_NEUTRAL } from '../../utils/pdfReport';
import EodPaymentSummaryBar from '../../components/eod/EodPaymentSummaryBar';
import { getBranchMaintenanceLogs } from '../../api/maintenanceLogs.api';
import { currentTradingDayIst, tradingDayRangeIst, toIstDateString } from '../../utils/timeUtils';

/**
 * Buckets bill-like rows (bills, credit clearances, wallet top-ups - anything with a `date`)
 * into the shift whose [loginTime, logoutTime ?? now] window contains it, so the day's billing
 * log can be read shift by shift instead of as one undifferentiated list. A row that falls
 * outside every shift's window - the gap between one shift ending and the next beginning - lands
 * in a trailing "outside any shift" bucket rather than being silently dropped from the report.
 */
function groupBillsByShift(bills, shifts) {
  const sortedShifts = [...(shifts || [])].sort((a, b) => new Date(a.loginTime) - new Date(b.loginTime));
  const groups = sortedShifts.map(shift => ({
    key: shift.id,
    operatorName: shift.operatorName,
    loginTime: shift.loginTime,
    logoutTime: shift.logoutTime,
    bills: [],
  }));
  const unassigned = { key: 'unassigned', operatorName: null, loginTime: null, logoutTime: null, bills: [] };

  for (const bill of bills || []) {
    const billTime = new Date(bill.date).getTime();
    const match = groups.find(g => {
      const start = new Date(g.loginTime).getTime();
      const end = g.logoutTime ? new Date(g.logoutTime).getTime() : Infinity;
      return billTime >= start && billTime <= end;
    });
    (match || unassigned).bills.push(bill);
  }

  const result = groups.filter(g => g.bills.length > 0);
  if (unassigned.bills.length > 0) result.push(unassigned);
  return result;
}

function shiftGroupLabel(group) {
  if (!group.loginTime) return 'Outside any shift';
  const start = new Date(group.loginTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true });
  const end = group.logoutTime
    ? new Date(group.logoutTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true })
    : 'still on shift';
  return `${group.operatorName} — ${start} to ${end}`;
}

export default function EodDashboardPage() {
  const { isSuperAdmin, user, canCorrectPaymentMethod } = useAuth();
  const toast = useToast();

  // ── Payment-method correction, inline from an EOD entry ──
  // Reuses the same PATCH /billing/{id}/payment-method endpoint as Billing Counter's own
  // "Change payment method" control (BillDetailsPanel.jsx) - this is just a second place to
  // reach it, for someone reviewing a day's entries here rather than pulling up the bill at
  // the counter. The live BillUpdated subscription already wired below (fetchEodData) means
  // this table refreshes on its own once the correction is saved - no separate refetch needed.
  const [editingBillId, setEditingBillId] = useState(null);
  const [correctMethod, setCorrectMethod] = useState('cash');
  const [correctCash, setCorrectCash] = useState('');
  const [correctOnline, setCorrectOnline] = useState('');
  const [correctReason, setCorrectReason] = useState('');
  const [correctBusy, setCorrectBusy] = useState(false);
  const [correctError, setCorrectError] = useState(null);

  const openCorrectPayMethod = (bill) => {
    const total = bill.totalRevenue || 0;
    const method = (bill.paymentType || '').toLowerCase();
    setCorrectMethod(method === 'online' ? 'online' : method === 'split' ? 'split' : 'cash');
    setCorrectCash(method === 'online' ? '0' : String(total));
    setCorrectOnline(method === 'online' ? String(total) : '0');
    setCorrectReason('');
    setCorrectError(null);
    // realBillId, never billId - billId on this row is the human-readable bill NUMBER
    // (e.g. "BILL-20260917-XXXX"), and the correction endpoint needs the actual id.
    setEditingBillId(bill.realBillId);
  };

  const handleCorrectPayMethod = async (bill) => {
    const total = bill.totalRevenue || 0;
    let cashAmount = 0, onlineAmount = 0, newPaymentType;
    if (correctMethod === 'cash') { cashAmount = total; newPaymentType = 'Cash'; }
    else if (correctMethod === 'online') { onlineAmount = total; newPaymentType = 'Online'; }
    else {
      cashAmount = Number(correctCash) || 0;
      onlineAmount = Number(correctOnline) || 0;
      // The Split tab is for typing a custom cash/online breakdown, not a promise that the
      // result is actually split - confirmed live: a bill corrected here with Cash 0 /
      // Online 180 still read "SPLIT" everywhere, because this used to trust which tab was
      // open rather than what was actually typed into it. Same rule PaymentEngineModal
      // already uses for a fresh payment - whichever side got the whole total is what this
      // bill actually was.
      newPaymentType = cashAmount === total ? 'Cash' : onlineAmount === total ? 'Online' : 'Split';
    }

    if (Math.round((cashAmount + onlineAmount) * 100) !== Math.round(total * 100)) {
      setCorrectError(`Cash + Online must add up to ₹${total.toFixed(2)}.`);
      return;
    }
    if (!correctReason.trim()) {
      setCorrectError('A reason is required.');
      return;
    }

    setCorrectBusy(true);
    setCorrectError(null);
    try {
      // realBillId, never billId - see openCorrectPayMethod's comment.
      await editPaymentMethod(bill.realBillId, {
        newPaymentType, cashAmount, onlineAmount, reason: correctReason.trim(),
      });
      toast.success('Payment method corrected');
      setEditingBillId(null);
    } catch (err) {
      setCorrectError(err.response?.data?.error || err.message || 'Failed to correct payment method');
    } finally {
      setCorrectBusy(false);
    }
  };

  const canEditPayment = (bill) => {
    if (!canCorrectPaymentMethod()) return false;
    const method = (bill.paymentType || '').toLowerCase();
    return method === 'cash' || method === 'online' || method === 'split';
  };
  const { activeBranch } = useBranch();
  const { subscribe, connected, SIGNALR_HUBS } = useSocket();

  // The IST trading day (06:00-06:00), not the UTC calendar date - opening
  // this screen at 1am IST while closing up must still default to tonight's
  // report, not tomorrow's UTC date.
  const [targetDate, setTargetDate] = useState(currentTradingDayIst()); // YYYY-MM-DD
  // Same field, same default - a single day is just a range of one. Only once someone actually
  // picks a later end date does the page stop asking for one day's cash register (which only
  // ever means one shift's drawer) and switch to showing every day in between instead.
  const [endDate, setEndDate] = useState(currentTradingDayIst());
  const isRange = endDate > targetDate;
  const rangeLabel = isRange ? `${targetDate} to ${endDate}` : targetDate;
  const [rangeDaily, setRangeDaily] = useState([]);
  const [rangeCredits, setRangeCredits] = useState([]);
  // Compact by default on a browser that has never opened this page before - a shift-close
  // operator wants a glance at the totals, not the PC grid above covered up the moment EOD
  // opens. Remembered per-browser after that: dragging the bar persists across reloads and
  // future visits instead of snapping back to whatever the default was every single time.
  const [summaryBarHeight, setSummaryBarHeight] = useState(() => {
    try {
      const stored = localStorage.getItem('eodSummaryBar.height');
      return stored ? Number(stored) : 180;
    } catch {
      return 180;
    }
  });

  useEffect(() => {
    try { localStorage.setItem('eodSummaryBar.height', String(summaryBarHeight)); } catch { /* ignore */ }
  }, [summaryBarHeight]);
  const [report, setReport] = useState(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState(null);

  const [pcs, setPcs] = useState([]);
  const [allBills, setAllBills] = useState([]);
  // Every shift touching this window, so the billing log can be read shift by shift instead of
  // as one undifferentiated list for the whole trading day.
  const [shifts, setShifts] = useState([]);
  // Power cuts and lost connections for the day — shown beside the money because they
  // explain it. A thin evening reads very differently once you can see the branch was dark.
  const [downtime, setDowntime] = useState([]);
  const [maintenanceLogs, setMaintenanceLogs] = useState([]);
  const [selectedPcId, setSelectedPcId] = useState(null);
  const [lastUpdated, setLastUpdated] = useState(Date.now());
  const [isUpdating, setIsUpdating] = useState(false);

  const targetBranchId = isSuperAdmin ? activeBranch?.id : user?.branchId;
  const isFetchingRef = useRef(false);

  const fetchEodData = useCallback(async () => {
    if (isSuperAdmin && !targetBranchId) {
      setIsLoading(false);
      return;
    }

    if (isFetchingRef.current) return;
    isFetchingRef.current = true;

    setIsUpdating(true);
    setError(null);

    const rangeSelected = endDate > targetDate;

    try {
      // Window must be the same 06:00-06:00 IST trading day boundary /eod/preview uses, not
      // midnight-to-midnight UTC - otherwise this table and the revenue cards above it disagree
      // about which bills belong to "today". For a real range, this spans midnight IST on the
      // start date through to midnight IST the morning after the end date.
      const startIso = tradingDayRangeIst(targetDate).startIso;
      const endIso = tradingDayRangeIst(endDate).endIso;

      // A single business day still asks /eod/preview for its cash register and shift figures -
      // that concept genuinely only means one day's drawer, and there is nothing sensible to sum
      // it into across a range. A real range skips it entirely instead of calling it once per
      // day, which is what let this page reuse the exact same range-report call either way.
      const previewPromise = rangeSelected
        ? Promise.resolve(null)
        : api.get('/eod/preview', { params: { date: targetDate, branchId: targetBranchId } });

      const [previewData, pcsRes, billsRes] = await Promise.all([
        previewPromise,
        api.get('/pcs', { params: { branchId: targetBranchId } }),
        api.get('/eod/range-report', {
          params: { startDate: startIso, endDate: endIso, branchId: targetBranchId }
        })
      ]);

      setReport(previewData?.data?.data ?? null);
      setPcs(pcsRes.data?.data || []);
      setAllBills(billsRes.data?.data?.allBills || []);
      setDowntime(billsRes.data?.data?.downtime || []);
      setShifts(billsRes.data?.data?.shifts || []);
      setRangeDaily(billsRes.data?.data?.daily || []);
      setRangeCredits(billsRes.data?.data?.allCredits || []);

      // Fetch maintenance logs separately so it doesn't break EOD if it fails
      try {
        const maintenanceRes = await getBranchMaintenanceLogs(targetBranchId, 30);
        // Show maintenance logs active at any point between the start and end date - "on or
        // before the end date" and "not resolved, or resolved on/after the start date" - which
        // collapses to the exact same single-day check already here when start === end.
        const logsActiveInRange = (maintenanceRes.data || []).filter(log => {
          const markedDate = toIstDateString(log.markedAt);
          const resolvedDate = log.resolvedAt ? toIstDateString(log.resolvedAt) : null;

          const markedOnOrBefore = markedDate <= endDate;
          const notResolvedOrResolvedAfter = !resolvedDate || resolvedDate >= targetDate;

          return markedOnOrBefore && notResolvedOrResolvedAfter;
        });
        setMaintenanceLogs(logsActiveInRange);
      } catch (err) {
        console.error('Failed to fetch maintenance logs:', err);
        setMaintenanceLogs([]);
      }

      setLastUpdated(Date.now());

    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Failed to fetch EOD data.');
      // A failed poll or a date switch that stumbles must not leave the previous date's full
      // report sitting on screen under the error banner - that reads as live, current data
      // when it is neither. Clear everything so the screen actually goes blank on failure.
      setReport(null);
      setPcs([]);
      setAllBills([]);
      setShifts([]);
      setDowntime([]);
      setMaintenanceLogs([]);
      setRangeDaily([]);
      setRangeCredits([]);
    } finally {
      setIsLoading(false);
      setIsUpdating(false);
      isFetchingRef.current = false;
    }
  }, [targetDate, endDate, targetBranchId, isSuperAdmin]);

  useEffect(() => {
    fetchEodData();
  }, [fetchEodData]);

  // ── Keep the figures current ──────────────────────────────────────────────
  //
  // Polling is deliberately NOT gated on the live connection, and that is the fix.
  //
  // Everything below used to sit behind `if (!connected) return`, so when the SignalR hub
  // dropped - a restart, a flaky line, an update installing - the page stopped refreshing
  // entirely and went on displaying whatever it had loaded, with nothing on screen admitting
  // it. Money figures that are quietly minutes old are worse than none: they look live, so
  // nobody thinks to reload, and the drawer gets counted against a stale expectation.
  //
  // The live connection is now what makes it *instant*. The timer is what makes it *reliable*,
  // and it runs either way.
  //
  // This also matters at Head Office specifically. A branch's bill raises its SignalR event on
  // the branch's own machine, not here, so Head Office was never going to be pushed anything
  // about a branch - the timer is the only thing that was ever updating those figures.
  useEffect(() => {
    const REFRESH_EVERY_MS = 10000;

    const tick = () => {
      if (document.visibilityState !== 'visible') return;
      fetchEodData();
    };

    const pollInterval = setInterval(tick, REFRESH_EVERY_MS);

    // Come back to the tab and see today's figures, not the ones from when you left it.
    document.addEventListener('visibilitychange', tick);

    return () => {
      clearInterval(pollInterval);
      document.removeEventListener('visibilitychange', tick);
    };
  }, [fetchEodData]);

  // Instant refresh when something actually happens on this machine, on top of the timer above.
  useEffect(() => {
    if (!connected) return;

    const unsubCash = subscribe(SIGNALR_HUBS.CASH, 'CashRegisterUpdated', fetchEodData);
    const unsubBill = subscribe(SIGNALR_HUBS.BILLING, 'BillUpdated', fetchEodData);
    const unsubSession = subscribe(SIGNALR_HUBS.SESSIONS, 'SessionUpdated', fetchEodData);

    return () => {
      unsubCash();
      unsubBill();
      unsubSession();
    };
  }, [connected, subscribe, SIGNALR_HUBS.CASH, SIGNALR_HUBS.BILLING, SIGNALR_HUBS.SESSIONS, fetchEodData]);

  const handleDownloadPdf = () => {
    if (isRange ? rangeDaily.length === 0 : !report) return;
    const title = isRange ? 'EOD Range Report' : 'End of Day Report';
    const rangeLabel = isRange ? `${targetDate} to ${endDate}` : targetDate;
    const subtitle = `${activeBranch?.name || 'All Branches'}  •  ${rangeLabel}  •  Live Preview`;
    const { doc } = createReport({ title, subtitle });
    let y = 90;

    if (isRange) {
      // A cash register only ever means one shift's drawer - there is no sensible way to sum
      // "opening balance" or "physically counted" across several of them, so a range prints the
      // same daily revenue breakdown the screen shows instead of a Cash Lifecycle table.
      const rangeTotals = rangeDaily.reduce((acc, d) => ({
        gaming: acc.gaming + d.gamingRevenue,
        food: acc.food + d.foodRevenue,
        discount: acc.discount + (d.discountAmount || 0),
        net: acc.net + d.totalRevenue,
      }), { gaming: 0, food: 0, discount: 0, net: 0 });

      y = addStatGrid(doc, y, [
        { label: 'Total Net Revenue', value: `Rs ${rangeTotals.net.toFixed(2)}` },
        { label: 'Gaming Revenue', value: `Rs ${rangeTotals.gaming.toFixed(2)}` },
        { label: 'Food Revenue', value: `Rs ${rangeTotals.food.toFixed(2)}` },
        { label: 'Discounts Applied', value: `Rs ${rangeTotals.discount.toFixed(2)}` },
      ]);
      y += 10;

      y = addTable(doc, y, {
        title, subtitle,
        heading: 'Daily Revenue Breakdown',
        head: ['Date', 'Net Gaming', 'Net Food & Drink', 'Discount', 'Total'],
        body: rangeDaily.map(d => [
          d.date, `Rs ${d.gamingRevenue.toFixed(2)}`, `Rs ${d.foodRevenue.toFixed(2)}`,
          `Rs ${(d.discountAmount || 0).toFixed(2)}`, `Rs ${d.totalRevenue.toFixed(2)}`
        ]),
      });
    } else {
      // Real money the member actually paid - Cash + Online, deliberately not
      // paymentMethods.totalWalletTopUps, which bakes the promotional bonus straight into the
      // figure (confirmed live: a Rs 1000 top-up with a Rs 100 bonus stored as Amount: 1100).
      // The bonus is the shop's own cost of running the promotion, not money anyone gave us.
      const realMembershipAmount =
        Number(report.paymentMethods.totalWalletTopUpsCash) + Number(report.paymentMethods.totalWalletTopUpsOnline);

      y = addStatGrid(doc, y, [
        { label: 'Total Net Revenue', value: `Rs ${(Number(report.revenue.netRevenue) + realMembershipAmount).toFixed(2)}` },
        { label: 'Gaming Revenue', value: `Rs ${report.revenue.totalGamingRevenue}` },
        { label: 'Food Revenue', value: `Rs ${report.revenue.totalFoodRevenue}` },
        { label: 'Membership Amount', value: `Rs ${realMembershipAmount.toFixed(2)}` },
        { label: 'Credit Cleared', value: `Rs ${report.reconciliation.creditClearedToday}` },
      ]);
      y += 10;

      // Same left/right split, same labels and order as the on-screen panel
      // (EodPaymentSummaryBar.jsx) and the EOD email, so all three agree. Member Amount
      // Deductions/Top-Ups are deliberately not on the screen but kept here - the printed
      // report is the copy that gets kept and argued over later.
      y = addTable(doc, y, {
        title, subtitle,
        heading: 'The Drawer',
        head: ['Metric', 'Amount'],
        body: [
          ['Opening Balance', `Rs ${report.cash.totalOpeningBalance}`],
          ['Expected Drawer', `Rs ${report.cash.expectedCashInDrawer}`],
          ['Physically Counted', report.cash.actualPhysicalCashCounted == null
            ? 'Not counted yet'
            : `Rs ${report.cash.actualPhysicalCashCounted}`],
          ['Cover Amount', report.cash.coverAmount == null
            ? 'Not set yet'
            : `Rs ${report.cash.coverAmount}`],
          [report.cash.actualPhysicalCashCounted == null ? 'Difference' : 'Total Difference',
            report.cash.totalDiscrepancy == null
              ? 'Unknown until the drawer is counted'
              : `Rs ${report.cash.totalDiscrepancy}`],
        ],
      });

      const creditsPending = (report.creditLogs?.filter(c => c.status?.toLowerCase() === 'pending')
        .reduce((acc, c) => acc + c.creditAmount, 0) || 0).toFixed(2);

      // Exactly the screen's grandTotal: cash (net of petty expense and owner withdrawals) plus
      // online. Member deductions excluded - they are a balance already counted as income when
      // it was topped up, not money arriving today. This used to be computed separately here
      // and disagreed with the screen; now it is not recomputed, just reproduced the same way.
      const cashNet = report.paymentMethods.totalCash + report.paymentMethods.totalWalletTopUpsCash
        + report.cash.totalCashInwards - report.cash.totalPettyExpenses - report.cash.totalOwnerWithdrawals;
      const onlineTotal = report.paymentMethods.totalOnline + report.paymentMethods.totalWalletTopUpsOnline;
      const overallEndTotal = (cashNet + onlineTotal).toFixed(2);

      y = addTable(doc, y, {
        title, subtitle,
        heading: "The Day's Money",
        head: ['Metric', 'Value'],
        body: [
          ['Cash + Member', `Rs ${report.cash.totalCashSales}`],
          ['Online', `Rs ${onlineTotal.toFixed(2)}`],
          ['Credit', `-Rs ${creditsPending}`],
          ['Credit Cleared', `Rs ${report.reconciliation.creditClearedToday}`],
          ['Petty Expense', `-Rs ${report.cash.totalPettyExpenses}`],
          ['Cash Add', `Rs ${report.cash.totalCashInwards}`],
          ['Overall End Total', `Rs ${overallEndTotal}`],
          ['Member Amount Deductions (Gaming/Food)', `Rs ${report.paymentMethods.totalWalletDeductions}`],
          ['Member Amount Top-Ups (Cash Collected)', `Rs ${realMembershipAmount.toFixed(2)}`],
          ['Total Sessions', String(report.operations.totalSessions)],
          ['Total Food Orders', String(report.operations.totalFoodOrders)],
        ],
      });
    }

    const pcRows = (pcs || []).map(pc => {
      const pcBills = allBills?.filter(b => b.pcId === pc.id) || [];
      const total = pcBills.reduce((sum, b) => sum + (b.totalRevenue || 0), 0);
      return [pc.name || pc.pcName || pc.pcNumber, String(pcBills.length), `Rs ${total.toFixed(2)}`];
    }).filter(row => row[1] !== '0');
    if (pcRows.length) {
      y = addTable(doc, y, {
        title, subtitle,
        heading: 'PC-Wise Breakdown',
        head: ['PC', 'Bills', 'Total Revenue'],
        body: pcRows,
      });
    }

    // Grouped shift by shift rather than as one flat list for the whole trading day - each
    // shift gets a highlighted heading row (operator, login-to-logout window) ahead of its own
    // bills, so a printed report answers "whose shift was this" without cross-referencing
    // anything else.
    const billBody = [];
    const shiftHeaderRows = new Set();
    groupedBills.forEach(group => {
      shiftHeaderRows.add(billBody.length);
      billBody.push([shiftGroupLabel(group), '', '', '', '', '', '', '', '', '', '', '']);
      group.bills.forEach(b => billBody.push([
        new Date(b.date).toLocaleDateString(),
        b.pcName || '-',
        b.sessionStartTime ? new Date(b.sessionStartTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true }) : '-',
        b.sessionEndTime ? new Date(b.sessionEndTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true }) : '-',
        b.customer, b.paymentType,
        `Rs ${b.gamingRevenue.toFixed(2)}`, `Rs ${b.foodRevenue.toFixed(2)}`,
        b.discount > 0 ? `-Rs ${b.discount.toFixed(2)}` : '-', `Rs ${b.totalRevenue.toFixed(2)}`,
        b.sessionNotes || '-', b.operator
      ]));
    });

    y = addTable(doc, y, {
      title, subtitle,
      heading: `Complete Billing Audit Logs (${rangeLabel})`,
      head: ['Date', 'PC Number', 'Start Time', 'End Time', 'Customer', 'Payment', 'Gaming', 'Food', 'Discount', 'Total', 'Note', 'Operator'],
      body: billBody,
      rowColor: (rowIndex) => shiftHeaderRows.has(rowIndex) ? ROW_TINT_NEUTRAL : null,
    });

    const eodCreditRows = isRange ? rangeCredits : (report.creditLogs || []);
    y = addTable(doc, y, {
      title, subtitle,
      heading: `Credit Audit Logs (${rangeLabel})`,
      head: ['Date Created', 'Customer', 'PC', 'Original Bill', 'Initial Paid', 'Amount Due', 'Status', 'Date Cleared'],
      body: eodCreditRows.map(c => [
        new Date(c.createdAt).toLocaleString(), c.customerName, c.pcNumber,
        `Rs ${c.originalBillAmount.toFixed(2)}`, `Rs ${c.amountPaidInitially.toFixed(2)}`, `Rs ${c.creditAmount.toFixed(2)}`,
        c.status, c.clearedAt ? new Date(c.clearedAt).toLocaleString() : '-'
      ]),
      rowColor: (rowIndex) => eodCreditRows[rowIndex]?.status?.toLowerCase() === 'cleared' ? ROW_TINT_GREEN : ROW_TINT_RED,
    });

    const maintenanceRows = maintenanceLogs || [];
    if (maintenanceRows.length) {
      y = addTable(doc, y, {
        title, subtitle,
        heading: `Maintenance Logs (${rangeLabel})`,
        head: ['PC', 'Marked At', 'Marked By', 'Reason', 'Duration', 'Status', 'Resolved At', 'Resolution Notes'],
        body: maintenanceRows.map(log => [
          log.pcName || '-',
          log.markedAt ? new Date(log.markedAt).toLocaleString() : '-',
          log.operatorName || '-',
          log.reason || '-',
          log.durationMinutes > 0
            ? `${Math.floor(log.durationMinutes / 60)}h ${log.durationMinutes % 60}m`
            : (log.isResolved ? '< 1m' : '-'),
          log.isResolved ? 'Resolved' : 'Active',
          log.resolvedAt ? new Date(log.resolvedAt).toLocaleString() : '-',
          log.resolutionNotes || '-'
        ]),
        rowColor: (rowIndex) => maintenanceRows[rowIndex]?.isResolved ? ROW_TINT_GREEN : ROW_TINT_RED,
      });
    }

    const downtimeRows = downtime || [];
    if (downtimeRows.length) {
      y = addTable(doc, y, {
        title, subtitle,
        heading: `Power Cut / Downtime Logs (${rangeLabel})`,
        head: ['Kind', 'From', 'To', 'Minutes', 'Sessions Affected', 'Impact', 'Notes'],
        body: downtimeRows.map(d => [
          d.kind || '-',
          d.from || '-',
          d.to || '-',
          String(d.minutes ?? '-'),
          String(d.sessionsAffected ?? '-'),
          d.impact || '-',
          d.notes || '-'
        ]),
      });
    }

    save(doc, `Apple_Esports_EOD_${isRange ? `${targetDate}_to_${endDate}` : targetDate}.pdf`);
  };

  const groupedBills = useMemo(() => groupBillsByShift(allBills, shifts), [allBills, shifts]);
  // report.creditLogs only exists for a single day; a range sources the same rows from
  // range-report's own allCredits instead (see fetchEodData) - same shape either way.
  const creditRows = isRange ? rangeCredits : (report?.creditLogs || []);

  if (isSuperAdmin && !activeBranch) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
        <Lock className="w-12 h-12 text-text-3 mb-4" />
        <h2 className="text-xl font-heading font-bold text-text mb-2">Select a Branch</h2>
        <p className="text-text-2">You must select a branch to view EOD Reports.</p>
      </div>
    );
  }

  return (
    <>
    <div
      className="h-full flex flex-col max-w-6xl mx-auto space-y-6 overflow-y-auto"
      // The fixed bottom bar's actual rendered height is always exactly `summaryBarHeight`
      // (that value is what sets its CSS height, not a guess) - so this padding, driven by the
      // same variable plus a clearance margin, can never fall short of what it needs to clear,
      // whatever height the bar is dragged to.
      style={{ paddingBottom: report ? summaryBarHeight + 32 : 40 }}
    >
      <div className="flex justify-between items-center bg-bg-2 p-6 rounded-xl border border-border">
        <PageHeader
          title="End of Day Dashboard"
          subtitle="Live Preview & Real-Time Updates"
          icon="M9 17v-2m3 2v-4m3 4v-6m2 10H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
        />
        <div className="flex items-center gap-2">
          <input
            type="date"
            value={targetDate}
            onChange={(e) => {
              const next = e.target.value;
              setTargetDate(next);
              // Keeps the range the right way round with no extra click - picking a new start
              // date past the current end date just brings the end date along with it, rather
              // than silently swapping which field means what.
              if (next > endDate) setEndDate(next);
            }}
            className="bg-bg-3 border border-border rounded-lg px-4 py-2 text-text outline-none focus:border-accent"
          />
          <span className="text-text-3 text-xs">to</span>
          <input
            type="date"
            value={endDate}
            min={targetDate}
            onChange={(e) => setEndDate(e.target.value)}
            className="bg-bg-3 border border-border rounded-lg px-4 py-2 text-text outline-none focus:border-accent"
          />
          <button
            onClick={handleDownloadPdf}
            disabled={isLoading || (isRange ? rangeDaily.length === 0 : !report)}
            title={isRange ? 'Downloads the daily breakdown, billing, credit and maintenance logs for this range' : undefined}
            className="btn-secondary py-2 px-3 flex items-center gap-1.5 text-xs font-bold disabled:opacity-50 disabled:cursor-not-allowed"
          >
            <Download className="w-3.5 h-3.5" /> Download PDF
          </button>
        </div>
      </div>

      {/* PC-Wise Personalised Billing Section */}
      <div className="card bg-bg-2 border border-border p-6 rounded-xl">
        <h2 className="font-heading font-extrabold text-sm uppercase tracking-wider text-text flex items-center gap-2 mb-6">
          <Monitor className="w-4.5 h-4.5 text-neon-blue" />
          PC-Wise Personalised Billing
        </h2>

        {/* PC Grid */}
        <div className="grid grid-cols-3 md:grid-cols-4 lg:grid-cols-8 gap-3 mb-6">
          {pcs.map(pc => {
            const pcBills = allBills?.filter(b => b.pcId === pc.id) || [];
            const pcDayTotal = pcBills.reduce((sum, b) => sum + (b.totalRevenue || 0), 0);
            const pcBillCount = pcBills.length;
            const hasEarnings = pcDayTotal > 0;

            return (
              <button
                key={pc.id}
                onClick={() => setSelectedPcId(pc.id)}
                className={`p-3 rounded-lg border flex flex-col items-center justify-center transition-all relative ${
                  selectedPcId === pc.id 
                    ? 'bg-neon-blue/20 border-neon-blue shadow-[0_0_15px_rgba(0,240,255,0.3)]' 
                    : hasEarnings 
                      ? 'bg-bg-3 border-neon-green/30 hover:border-neon-green/60' 
                      : 'bg-bg-3 border-border hover:border-neon-blue/50'
                }`}
              >
                {pcBillCount > 0 && (
                  <span className="absolute -top-1.5 -right-1.5 bg-accent text-white text-[9px] font-bold w-5 h-5 rounded-full flex items-center justify-center shadow-md">
                    {pcBillCount}
                  </span>
                )}
                <div className={`w-8 h-8 rounded-full flex items-center justify-center mb-1.5 ${
                  selectedPcId === pc.id ? 'bg-neon-blue/20' : hasEarnings ? 'bg-neon-green/10' : 'bg-bg-2'
                }`}>
                  <Monitor className={`w-4 h-4 ${
                    selectedPcId === pc.id ? 'text-neon-blue' : hasEarnings ? 'text-neon-green' : 'text-text-3'
                  }`} />
                </div>
                <div className="font-heading font-bold text-[10px] text-text truncate w-full text-center mb-1">
                  {pc.name || pc.pcName || pc.pcNumber}
                </div>
                <div className={`font-mono font-bold text-xs ${
                  hasEarnings ? 'text-neon-green' : 'text-text-3'
                }`}>
                  ₹{pcDayTotal.toFixed(0)}
                </div>
              </button>
            );
          })}
        </div>

        {/* Selected PC Details */}
        {selectedPcId && (
          <div className="space-y-6 animate-fade-in border-t border-border/50 pt-6">
            {(() => {
              const pcBills = allBills?.filter(b => 
                selectedPcId === 'walkin' ? (!b.pcId) : (b.pcId === selectedPcId)
              ) || [];
              
              const pcTotalGaming = pcBills.reduce((sum, d) => sum + d.gamingRevenue, 0);
              const pcTotalFood = pcBills.reduce((sum, d) => sum + d.foodRevenue, 0);
              const pcTotalDiscount = pcBills.reduce((sum, d) => sum + d.discount, 0);
              const pcTotalNet = pcBills.reduce((sum, d) => sum + d.totalRevenue, 0);

              return (
                <>
                  <div className="grid grid-cols-2 md:grid-cols-5 gap-4">
                    <div className="bg-bg-3 p-4 rounded-lg border border-border flex flex-col justify-center">
                      <div className="text-[10px] text-text-3 font-bold uppercase tracking-wider">Total Bills</div>
                      <div className="text-xl font-mono font-bold text-text">{pcBills.length}</div>
                    </div>
                    <div className="bg-bg-3 p-4 rounded-lg border border-border flex flex-col justify-center">
                      <div className="text-[10px] text-text-3 font-bold uppercase tracking-wider">Net Gaming</div>
                      <div className="text-xl font-mono font-bold text-neon-blue">₹{pcTotalGaming.toFixed(2)}</div>
                    </div>
                    <div className="bg-bg-3 p-4 rounded-lg border border-border flex flex-col justify-center">
                      <div className="text-[10px] text-text-3 font-bold uppercase tracking-wider">Net Food</div>
                      <div className="text-xl font-mono font-bold text-accent">₹{pcTotalFood.toFixed(2)}</div>
                    </div>
                    <div className="bg-bg-3 p-4 rounded-lg border border-border flex flex-col justify-center">
                      <div className="text-[10px] text-text-3 font-bold uppercase tracking-wider">Discounts</div>
                      <div className="text-xl font-mono font-bold text-neon-orange">₹{pcTotalDiscount.toFixed(2)}</div>
                    </div>
                    <div className="bg-bg-3 p-4 rounded-lg border border-border flex flex-col justify-center relative overflow-hidden">
                      <div className="absolute top-0 right-0 w-16 h-16 bg-neon-green/10 rounded-full blur-xl" />
                      <div className="text-[10px] text-text-3 font-bold uppercase tracking-wider relative">Net Revenue</div>
                      <div className="text-xl font-mono font-bold text-neon-green relative">₹{pcTotalNet.toFixed(2)}</div>
                    </div>
                  </div>

                  <div className="overflow-x-auto mt-4">
                    {pcBills.length === 0 ? (
                      <div className="text-center text-text-3 text-xs italic py-8 border border-dashed border-border rounded-lg">
                        No billing records found for this PC.
                      </div>
                    ) : (
                      <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
                        <thead>
                          <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                            <th className="py-3 px-4">Date</th>
                            <th className="py-3 px-4">PC Number</th>
                            <th className="py-3 px-4">Start Time</th>
                            <th className="py-3 px-4">End Time</th>
                            <th className="py-3 px-4">Customer</th>
                            <th className="py-3 px-4 text-center">Payment</th>
                            <th className="py-3 px-4 text-right">Gaming</th>
                            <th className="py-3 px-4 text-right">Food</th>
                            <th className="py-3 px-4 text-right">Discount</th>
                            <th className="py-3 px-4 text-right">Total</th>
                            <th className="py-3 px-4">Note</th>
                            <th className="py-3 px-4">Operator</th>
                            <th className="py-3 px-4 text-center">Print</th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-border/40 font-mono">
                          {pcBills.map(bill => {
                            const startStr = bill.sessionStartTime ? new Date(bill.sessionStartTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true }) : '-';
                            const endStr = bill.sessionEndTime ? new Date(bill.sessionEndTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true }) : '-';

                            const thisBillId = bill.billId || bill.id;
                            // editingBillId is set from realBillId (see openCorrectPayMethod) -
                            // compared against the same field here, not the display number.
                            const isEditingThis = editingBillId === bill.realBillId;

                            return (
                              <Fragment key={thisBillId}>
                              <tr className="hover:bg-bg-3/40 transition-colors">
                                <td className="py-3 px-4 text-text-2">{new Date(bill.date).toLocaleDateString()}</td>
                                <td className="py-3 px-4 text-text font-bold">{bill.pcName || '-'}</td>
                                <td className="py-3 px-4 text-text-2">{startStr}</td>
                                <td className="py-3 px-4 text-text-2">{endStr}</td>
                                <td className="py-3 px-4 text-text-2 font-sans">{bill.customer}</td>
                                <td className="py-3 px-4 text-center text-text-3 uppercase">
                                  <span className="inline-flex items-center gap-1.5">
                                    {bill.paymentType}
                                    {canEditPayment(bill) && (
                                      <button
                                        type="button"
                                        onClick={() => openCorrectPayMethod(bill)}
                                        title="Change payment method"
                                        className="text-text-3 hover:text-accent transition-colors"
                                      >
                                        <Pencil className="w-3 h-3" />
                                      </button>
                                    )}
                                  </span>
                                </td>
                                <td className="py-3 px-4 text-right text-text">₹{bill.gamingRevenue.toFixed(2)}</td>
                                <td className="py-3 px-4 text-right text-text">₹{bill.foodRevenue.toFixed(2)}</td>
                                <td className="py-3 px-4 text-right text-neon-red">{bill.discount > 0 ? `-₹${bill.discount.toFixed(2)}` : '-'}</td>
                                <td className="py-3 px-4 text-right text-neon-green font-bold">₹{bill.totalRevenue.toFixed(2)}</td>
                                <td className="py-3 px-4 text-text-3 text-[10px] whitespace-pre-wrap">{bill.sessionNotes || '-'}</td>
                                <td className="py-3 px-4 text-text-2">{bill.operator}</td>
                                <td className="py-3 px-4 text-center">
                                  <button
                                    onClick={() => printBill(bill.billId || bill.id, bill)}
                                    className="p-1.5 bg-bg-3 hover:bg-accent hover:text-bg transition-colors rounded-lg text-text-2 tooltip-trigger"
                                    title="Print Bill"
                                  >
                                    <Printer className="w-4 h-4" />
                                  </button>
                                </td>
                              </tr>
                              {isEditingThis && (
                                <tr>
                                  <td colSpan={13} className="bg-bg-3/40 px-4 py-3">
                                    <div className="max-w-md space-y-2.5 font-sans normal-case">
                                      <div className="flex items-center justify-between">
                                        <div className="text-[10px] font-bold text-accent uppercase tracking-widest">
                                          Correct Payment Method — {bill.customer}, ₹{bill.totalRevenue.toFixed(2)}
                                        </div>
                                        <button type="button" onClick={() => setEditingBillId(null)} className="text-text-3 hover:text-text">
                                          <X className="w-4 h-4" />
                                        </button>
                                      </div>
                                      <div className="grid grid-cols-3 gap-1.5">
                                        {['cash', 'online', 'split'].map(m => (
                                          <button
                                            key={m}
                                            type="button"
                                            onClick={() => setCorrectMethod(m)}
                                            className={`py-1.5 rounded border text-[11px] font-bold uppercase tracking-wider transition-all ${
                                              correctMethod === m
                                                ? 'bg-accent/20 border-accent text-accent'
                                                : 'bg-bg-2 border-border text-text-3 hover:border-accent/50'
                                            }`}
                                          >
                                            {m}
                                          </button>
                                        ))}
                                      </div>
                                      {correctMethod === 'split' && (
                                        <div className="grid grid-cols-2 gap-2">
                                          <div>
                                            <label className="text-[10px] text-text-3 uppercase tracking-wider">Cash</label>
                                            <input type="number" value={correctCash} onChange={e => setCorrectCash(e.target.value)}
                                              className="w-full bg-bg-2 border border-border rounded px-2 py-1.5 text-sm font-mono text-text" />
                                          </div>
                                          <div>
                                            <label className="text-[10px] text-text-3 uppercase tracking-wider">Online</label>
                                            <input type="number" value={correctOnline} onChange={e => setCorrectOnline(e.target.value)}
                                              className="w-full bg-bg-2 border border-border rounded px-2 py-1.5 text-sm font-mono text-text" />
                                          </div>
                                        </div>
                                      )}
                                      <div>
                                        <label className="text-[10px] text-text-3 uppercase tracking-wider">Reason (required)</label>
                                        <input type="text" value={correctReason} onChange={e => setCorrectReason(e.target.value)}
                                          placeholder="e.g. bank declined the online payment, customer paid cash instead"
                                          className="w-full bg-bg-2 border border-border rounded px-2 py-1.5 text-xs text-text" />
                                      </div>
                                      {correctError && <div className="text-[11px] text-neon-red">{correctError}</div>}
                                      <div className="flex gap-2 pt-1">
                                        <button type="button" disabled={correctBusy} onClick={() => setEditingBillId(null)}
                                          className="flex-1 py-1.5 rounded border border-border text-text-3 text-xs font-bold uppercase tracking-wider hover:bg-bg-2 disabled:opacity-50">
                                          Cancel
                                        </button>
                                        <button type="button" disabled={correctBusy} onClick={() => handleCorrectPayMethod(bill)}
                                          className="flex-1 py-1.5 rounded border border-accent bg-accent/10 text-accent text-xs font-bold uppercase tracking-wider hover:bg-accent/20 disabled:opacity-50">
                                          {correctBusy ? 'Saving…' : 'Save Correction'}
                                        </button>
                                      </div>
                                    </div>
                                  </td>
                                </tr>
                              )}
                              </Fragment>
                            );
                          })}
                        </tbody>
                      </table>
                    )}
                  </div>
                </>
              );
            })()}
          </div>
        )}
      </div>

      {error && (
        <div className="bg-neon-red/10 border border-neon-red/30 text-neon-red p-4 rounded-xl flex items-center gap-3">
          <AlertTriangle className="w-5 h-5 shrink-0" />
          <p>{error}</p>
        </div>
      )}

      {isLoading ? (
        <div className="flex justify-center items-center min-h-[40vh]">
          <div className="w-8 h-8 rounded-full border-2 border-accent border-t-transparent animate-spin" />
        </div>
      ) : (report || isRange) ? (
        <>
          {!isRange && report && (() => {
            // Real money the member actually paid - Cash + Online, deliberately not
            // paymentMethods.totalWalletTopUps, which bakes the promotional bonus straight into
            // the figure (confirmed live: a Rs 1000 top-up with a Rs 100 bonus stored as
            // Amount: 1100). The bonus is the shop's own cost of running the promotion, not
            // money anyone gave us.
            const realMembershipAmount =
              Number(report.paymentMethods.totalWalletTopUpsCash) + Number(report.paymentMethods.totalWalletTopUpsOnline);

            return (
            <>
              {/* Revenue & Operations Summary Grid */}
              <div className="grid grid-cols-1 md:grid-cols-5 gap-4">
                <div className="bg-bg-2 p-5 rounded-xl border border-border shadow-lg">
                  <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">Total Net Revenue</div>
                  {/* Includes today's membership top-ups on the owner's own instruction - not
                      "earned" revenue in the strict sense (the member hasn't spent it yet), but
                      real money collected today, which is what this figure is meant to answer
                      here. Gaming/Food Revenue below stay exactly as earned, so nothing else
                      reads this split differently than before. */}
                  <div className="text-3xl font-mono font-bold text-accent">
                    ₹{(Number(report.revenue.netRevenue) + realMembershipAmount).toFixed(2)}
                  </div>
                </div>
                <div className="bg-bg-2 p-5 rounded-xl border border-border shadow-lg">
                  <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">Gaming Revenue</div>
                  <div className="text-2xl font-mono font-bold text-text">₹{report.revenue.totalGamingRevenue}</div>
                </div>
                <div className="bg-bg-2 p-5 rounded-xl border border-border shadow-lg">
                  <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">Food Revenue</div>
                  <div className="text-2xl font-mono font-bold text-text">₹{report.revenue.totalFoodRevenue}</div>
                </div>
                <div className="bg-bg-2 p-5 rounded-xl border border-border shadow-lg">
                  <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">Membership Amount</div>
                  <div className="text-2xl font-mono font-bold text-text">₹{realMembershipAmount.toFixed(2)}</div>
                </div>
                <div className="bg-bg-2 p-5 rounded-xl border border-border shadow-lg">
                  <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">Credit Cleared</div>
                  <div className="text-2xl font-mono font-bold text-text">₹{report.reconciliation.creditClearedToday}</div>
                </div>
              </div>

              {/* Operations Overview */}
              <div className="bg-bg-2 rounded-xl border border-border shadow-lg p-6">
                <h3 className="text-sm uppercase font-bold text-text-2 tracking-widest mb-6 border-b border-border pb-3 flex items-center gap-2">
                  <FileText className="w-4 h-4" /> Operations Overview
                </h3>
                <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                  <div className="bg-bg-3 p-3 rounded-lg border border-border text-center">
                    <div className="text-2xl font-bold text-text">{report.operations.totalSessions}</div>
                    <div className="text-[10px] uppercase font-bold text-text-3 tracking-widest mt-1">Sessions</div>
                  </div>
                  <div className="bg-bg-3 p-3 rounded-lg border border-border text-center">
                    <div className="text-2xl font-bold text-text">{report.operations.totalFoodOrders}</div>
                    <div className="text-[10px] uppercase font-bold text-text-3 tracking-widest mt-1">Food Orders</div>
                  </div>
                </div>
              </div>
            </>
            );
          })()}

          {isRange && (() => {
            // A cash register only ever means one shift's drawer - summing "opening balance" or
            // "physically counted" across several of them would not describe anything real, so
            // a range shows the same daily revenue breakdown Reports already uses instead of the
            // single-day Cash Lifecycle / Operations cards above.
            const rangeTotals = rangeDaily.reduce((acc, d) => ({
              gaming: acc.gaming + d.gamingRevenue,
              food: acc.food + d.foodRevenue,
              discount: acc.discount + (d.discountAmount || 0),
              net: acc.net + d.totalRevenue,
            }), { gaming: 0, food: 0, discount: 0, net: 0 });

            return (
              <>
                <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
                  <div className="bg-bg-2 p-5 rounded-xl border border-border shadow-lg">
                    <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">Total Net Revenue</div>
                    <div className="text-3xl font-mono font-bold text-accent">₹{rangeTotals.net.toFixed(2)}</div>
                  </div>
                  <div className="bg-bg-2 p-5 rounded-xl border border-border shadow-lg">
                    <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">Gaming Revenue</div>
                    <div className="text-2xl font-mono font-bold text-text">₹{rangeTotals.gaming.toFixed(2)}</div>
                  </div>
                  <div className="bg-bg-2 p-5 rounded-xl border border-border shadow-lg">
                    <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">Food Revenue</div>
                    <div className="text-2xl font-mono font-bold text-text">₹{rangeTotals.food.toFixed(2)}</div>
                  </div>
                  <div className="bg-bg-2 p-5 rounded-xl border border-border shadow-lg">
                    <div className="text-text-3 text-xs uppercase font-bold tracking-widest mb-1">Discounts Applied</div>
                    <div className="text-2xl font-mono font-bold text-text">₹{rangeTotals.discount.toFixed(2)}</div>
                  </div>
                </div>

                <div className="card bg-bg-2 border border-border p-6 rounded-xl shadow-lg">
                  <h2 className="font-heading font-extrabold text-sm uppercase tracking-wider text-text flex items-center gap-2 mb-6">
                    <FileText className="w-4.5 h-4.5 text-accent" />
                    Daily Revenue Breakdown ({rangeLabel})
                  </h2>
                  <div className="overflow-x-auto">
                    {rangeDaily.length === 0 ? (
                      <div className="text-center text-text-3 text-xs italic py-8 border border-dashed border-border rounded-lg">
                        No billing records found in this range.
                      </div>
                    ) : (
                      <table className="w-full text-left border-collapse text-xs">
                        <thead>
                          <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                            <th className="py-2.5 px-4">Date</th>
                            <th className="py-2.5 px-4 text-right">Net Gaming</th>
                            <th className="py-2.5 px-4 text-right">Net Food & Drink</th>
                            <th className="py-2.5 px-4 text-right">Discount</th>
                            <th className="py-2.5 px-4 text-right">Total</th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-border/40 font-mono">
                          {rangeDaily.map(day => (
                            <tr key={day.date} className="hover:bg-bg-3/40 transition-colors">
                              <td className="py-2 px-4 text-text-2">{day.date}</td>
                              <td className="py-2 px-4 text-right text-text">₹{day.gamingRevenue.toFixed(2)}</td>
                              <td className="py-2 px-4 text-right text-text">₹{day.foodRevenue.toFixed(2)}</td>
                              <td className="py-2 px-4 text-right text-neon-red">{day.discountAmount > 0 ? `-₹${day.discountAmount.toFixed(2)}` : '-'}</td>
                              <td className="py-2 px-4 text-right text-neon-green font-bold">₹{day.totalRevenue.toFixed(2)}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    )}
                  </div>
                </div>
              </>
            );
          })()}

          {/* ── Power cuts & connection losses ──
              Deliberately above the billing log: it is the context for the numbers below. */}
          <div className="card bg-bg-2 border border-border p-6 rounded-xl shadow-lg">
            <div className="flex justify-between items-center mb-4">
              <h2 className="font-heading font-extrabold text-sm uppercase tracking-wider text-text flex items-center gap-2">
                <ZapOff className="w-4.5 h-4.5 text-accent" />
                Power &amp; Connection Interruptions ({rangeLabel})
              </h2>
              {downtime.length > 0 && (
                <span className="text-xs font-mono text-neon-orange">
                  {downtime.reduce((sum, d) => sum + (d.minutes || 0), 0)} min total
                </span>
              )}
            </div>

            {downtime.length === 0 ? (
              <div className="text-center text-text-3 text-xs italic py-6 border border-dashed border-border rounded-lg">
                No power cuts or connection losses recorded on this day.
              </div>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
                  <thead>
                    <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                      <th className="py-3 px-4">What happened</th>
                      <th className="py-3 px-4">From</th>
                      <th className="py-3 px-4">Back at</th>
                      <th className="py-3 px-4 text-right">Minutes</th>
                      <th className="py-3 px-4 text-center">PCs affected</th>
                      <th className="py-3 px-4">Effect on customers</th>
                    </tr>
                  </thead>
                  <tbody>
                    {downtime.map((d) => {
                      // Three kinds now share this table: a real power cut (sessions genuinely
                      // credited time back), a lost link to Head Office (nobody's play was
                      // touched), and an app problem the operator reported themselves (neither
                      // claim is true of it, so it gets its own middle badge rather than being
                      // folded into "everything that isn't internet" as a power cut).
                      const isPowerCut = d.kind === 'Power cut / restart';
                      const isAppFault = d.kind === 'App problem';
                      return (
                        <tr key={d.id} className="border-b border-border/50 hover:bg-bg-3/50">
                          <td className="py-2.5 px-4">
                            <span className={`badge ${isPowerCut ? 'badge-offline' : isAppFault ? 'badge-reserved' : 'badge-awaiting'}`}>
                              {d.kind}
                            </span>
                          </td>
                          <td className="py-2.5 px-4 font-mono">{d.from}</td>
                          <td className="py-2.5 px-4 font-mono">{d.to}</td>
                          <td className="py-2.5 px-4 text-right font-mono font-bold">{d.minutes}</td>
                          <td className="py-2.5 px-4 text-center font-mono">
                            {isPowerCut ? d.sessionsAffected : '—'}
                          </td>
                          <td className="py-2.5 px-4 text-text-2">{d.impact}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          {/* ── Complete Billing Audit Logs ── */}
          <div className="card bg-bg-2 border border-border p-6 rounded-xl shadow-lg">
            <div className="flex justify-between items-center mb-6">
              <h2 className="font-heading font-extrabold text-sm uppercase tracking-wider text-text flex items-center gap-2">
                <Clock className="w-4.5 h-4.5 text-accent" />
                Complete Billing Audit Logs ({rangeLabel})
              </h2>
            </div>

            <div className="overflow-x-auto">
              {!allBills || allBills.length === 0 ? (
                <div className="text-center text-text-3 text-xs italic py-8 border border-dashed border-border rounded-lg">
                  No bills found for the selected {isRange ? 'range' : 'date'}.
                </div>
              ) : (
                <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
                  <thead>
                    <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                      <th className="py-3 px-4">Date</th>
                      <th className="py-3 px-4">PC Number</th>
                      <th className="py-3 px-4">Start Time</th>
                      <th className="py-3 px-4">End Time</th>
                      <th className="py-3 px-4">Customer</th>
                      <th className="py-3 px-4 text-center">Payment</th>
                      <th className="py-3 px-4 text-right">Gaming</th>
                      <th className="py-3 px-4 text-right">Food</th>
                      <th className="py-3 px-4 text-right">Discount</th>
                      <th className="py-3 px-4 text-right">Total</th>
                      <th className="py-3 px-4">Note</th>
                      <th className="py-3 px-4">Operator</th>
                      <th className="py-3 px-4 text-center">Print</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-border/40 font-mono">
                    {groupedBills.map(group => (
                      <Fragment key={group.key}>
                        <tr className="bg-bg-3/70">
                          <td colSpan={13} className="py-2 px-4 text-[10px] font-bold uppercase tracking-wider text-text-2 font-sans">
                            {shiftGroupLabel(group)}
                            <span className="text-text-3 font-normal normal-case ml-2">
                              — ₹{group.bills.reduce((sum, b) => sum + (b.totalRevenue || 0), 0).toFixed(2)} across {group.bills.length} entr{group.bills.length === 1 ? 'y' : 'ies'}
                            </span>
                          </td>
                        </tr>
                        {group.bills.map(bill => {
                        const auditBillId = bill.billId || bill.id;
                        // editingBillId is set from realBillId (see openCorrectPayMethod) -
                        // compared against the same field here, not the display number.
                        const isEditingThisAudit = editingBillId === bill.realBillId;
                        return (
                      <Fragment key={auditBillId}>
                      <tr className="hover:bg-bg-3/40 transition-colors">
                        <td className="py-3 px-4 text-text-2">
                          {new Date(bill.date).toLocaleDateString()}
                        </td>
                        <td className="py-3 px-4 text-text font-bold">{bill.pcName || '-'}</td>
                        <td className="py-3 px-4 text-text-2">
                          {bill.sessionStartTime ? new Date(bill.sessionStartTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true }) : '-'}
                        </td>
                        <td className="py-3 px-4 text-text-2">
                          {bill.sessionEndTime ? new Date(bill.sessionEndTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: true }) : '-'}
                        </td>
                        <td className="py-3 px-4 text-text-2 font-sans">{bill.customer}</td>
                        <td className="py-3 px-4 text-center">
                          {bill.paymentType?.toUpperCase() === 'CREDIT' ? (
                            <div className="flex flex-col items-center gap-1">
                              <span className="text-text-3 font-bold uppercase text-[10px]">Credit</span>
                              {bill.creditStatus?.toLowerCase() === 'cleared' ? (
                                <>
                                  <span className="text-neon-green text-[9px] bg-neon-green/10 px-1.5 py-0.5 rounded border border-neon-green/20 uppercase tracking-wider font-bold">Cleared</span>
                                  <span className="text-text-3 text-[9px]">Total Paid: ₹{bill.totalRevenue.toFixed(2)}</span>
                                </>
                              ) : (
                                <>
                                  <span className="text-neon-red text-[9px] bg-neon-red/10 px-1.5 py-0.5 rounded border border-neon-red/20 uppercase tracking-wider font-bold">
                                    ₹{(bill.creditAmount || 0).toFixed(2)} Pending
                                  </span>
                                  <span className="text-text-3 text-[9px]">Upfront: ₹{(bill.amountPaidInitially || 0).toFixed(2)}</span>
                                </>
                              )}
                            </div>
                          ) : bill.paymentType?.toUpperCase() === 'CREDIT SETTLED' ? (
                            // The synthetic "a credit was cleared" row EodController adds for
                            // this date range - same green the CREDIT branch above uses for
                            // "Cleared", so a settled credit reads the same way wherever it
                            // shows up. How it was actually paid (cash/online/split) comes from
                            // the Payment row ClearCreditAsync already writes - CustomerCredit
                            // itself never recorded this, so without it every settled credit
                            // looked identical regardless of how the money actually came in.
                            <div className="flex flex-col items-center gap-1">
                              <span className="text-neon-green text-[9px] bg-neon-green/10 px-1.5 py-0.5 rounded border border-neon-green/20 uppercase tracking-wider font-bold">
                                {bill.paymentType}
                              </span>
                              <span className="text-text-3 text-[9px]">
                                {bill.clearedCashAmount > 0 && bill.clearedOnlineAmount > 0
                                  ? `Split: Cash ₹${bill.clearedCashAmount.toFixed(2)} + Online ₹${bill.clearedOnlineAmount.toFixed(2)}`
                                  : bill.clearedOnlineAmount > 0
                                  ? `Online ₹${bill.clearedOnlineAmount.toFixed(2)}`
                                  : bill.clearedCashAmount > 0
                                  ? `Cash ₹${bill.clearedCashAmount.toFixed(2)}`
                                  : (bill.clearedPaymentType || 'Method not recorded')}
                              </span>
                            </div>
                          ) : (
                            <span className="inline-flex items-center gap-1.5">
                              <span className="text-text-3 uppercase">{bill.paymentType}</span>
                              {canEditPayment(bill) && (
                                <button
                                  type="button"
                                  onClick={() => openCorrectPayMethod(bill)}
                                  title="Change payment method"
                                  className="text-text-3 hover:text-accent transition-colors"
                                >
                                  <Pencil className="w-3 h-3" />
                                </button>
                              )}
                            </span>
                          )}
                        </td>
                        <td className="py-3 px-4 text-right text-text">₹{bill.gamingRevenue.toFixed(2)}</td>
                        <td className="py-3 px-4 text-right text-text">₹{bill.foodRevenue.toFixed(2)}</td>
                        <td className="py-3 px-4 text-right text-neon-red">{bill.discount > 0 ? `-₹${bill.discount.toFixed(2)}` : '-'}</td>
                        <td className="py-3 px-4 text-right text-neon-green font-bold">₹{bill.totalRevenue.toFixed(2)}</td>
                        <td className="py-3 px-4 text-text-3 text-[10px] whitespace-pre-wrap">{bill.sessionNotes || '-'}</td>
                        <td className="py-3 px-4 text-neon-blue font-bold">{bill.operator}</td>
                        <td className="py-3 px-4 text-center">
                          <button
                            onClick={() => printBill(bill.billId || bill.id, bill)}
                            className="p-1.5 bg-bg-3 hover:bg-accent hover:text-bg transition-colors rounded-lg text-text-2 tooltip-trigger"
                            title="Print Bill"
                          >
                            <Printer className="w-4 h-4" />
                          </button>
                        </td>
                      </tr>
                      {isEditingThisAudit && (
                        <tr>
                          <td colSpan={13} className="bg-bg-3/40 px-4 py-3">
                            <div className="max-w-md space-y-2.5 font-sans normal-case">
                              <div className="flex items-center justify-between">
                                <div className="text-[10px] font-bold text-accent uppercase tracking-widest">
                                  Correct Payment Method — {bill.customer}, ₹{bill.totalRevenue.toFixed(2)}
                                </div>
                                <button type="button" onClick={() => setEditingBillId(null)} className="text-text-3 hover:text-text">
                                  <X className="w-4 h-4" />
                                </button>
                              </div>
                              <div className="grid grid-cols-3 gap-1.5">
                                {['cash', 'online', 'split'].map(m => (
                                  <button
                                    key={m}
                                    type="button"
                                    onClick={() => setCorrectMethod(m)}
                                    className={`py-1.5 rounded border text-[11px] font-bold uppercase tracking-wider transition-all ${
                                      correctMethod === m
                                        ? 'bg-accent/20 border-accent text-accent'
                                        : 'bg-bg-2 border-border text-text-3 hover:border-accent/50'
                                    }`}
                                  >
                                    {m}
                                  </button>
                                ))}
                              </div>
                              {correctMethod === 'split' && (
                                <div className="grid grid-cols-2 gap-2">
                                  <div>
                                    <label className="text-[10px] text-text-3 uppercase tracking-wider">Cash</label>
                                    <input type="number" value={correctCash} onChange={e => setCorrectCash(e.target.value)}
                                      className="w-full bg-bg-2 border border-border rounded px-2 py-1.5 text-sm font-mono text-text" />
                                  </div>
                                  <div>
                                    <label className="text-[10px] text-text-3 uppercase tracking-wider">Online</label>
                                    <input type="number" value={correctOnline} onChange={e => setCorrectOnline(e.target.value)}
                                      className="w-full bg-bg-2 border border-border rounded px-2 py-1.5 text-sm font-mono text-text" />
                                  </div>
                                </div>
                              )}
                              <div>
                                <label className="text-[10px] text-text-3 uppercase tracking-wider">Reason (required)</label>
                                <input type="text" value={correctReason} onChange={e => setCorrectReason(e.target.value)}
                                  placeholder="e.g. bank declined the online payment, customer paid cash instead"
                                  className="w-full bg-bg-2 border border-border rounded px-2 py-1.5 text-xs text-text" />
                              </div>
                              {correctError && <div className="text-[11px] text-neon-red">{correctError}</div>}
                              <div className="flex gap-2 pt-1">
                                <button type="button" disabled={correctBusy} onClick={() => setEditingBillId(null)}
                                  className="flex-1 py-1.5 rounded border border-border text-text-3 text-xs font-bold uppercase tracking-wider hover:bg-bg-2 disabled:opacity-50">
                                  Cancel
                                </button>
                                <button type="button" disabled={correctBusy} onClick={() => handleCorrectPayMethod(bill)}
                                  className="flex-1 py-1.5 rounded border border-accent bg-accent/10 text-accent text-xs font-bold uppercase tracking-wider hover:bg-accent/20 disabled:opacity-50">
                                  {correctBusy ? 'Saving…' : 'Save Correction'}
                                </button>
                              </div>
                            </div>
                          </td>
                        </tr>
                      )}
                      </Fragment>
                        );
                        })}
                      </Fragment>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          </div>

          {/* ── Credit Audit Logs ── */}
          <div className="card bg-bg-2 border border-border p-6 rounded-xl shadow-lg">
            <div className="flex justify-between items-center mb-6">
              <h2 className="font-heading font-extrabold text-sm uppercase tracking-wider text-text flex items-center gap-2">
                <Clock className="w-4.5 h-4.5 text-accent" />
                Credit Audit Logs ({rangeLabel})
              </h2>
            </div>

            <div className="overflow-x-auto">
              {creditRows.length === 0 ? (
                <div className="text-center text-text-3 text-xs italic py-8 border border-dashed border-border rounded-lg">
                  No credit records found for the selected {isRange ? 'range' : 'date'}.
                </div>
              ) : (
                <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
                  <thead>
                    <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                      <th className="py-3 px-4">Date Created</th>
                      <th className="py-3 px-4">Customer</th>
                      <th className="py-3 px-4">PC</th>
                      <th className="py-3 px-4 text-right">Original Bill</th>
                      <th className="py-3 px-4 text-right">Initial Paid</th>
                      <th className="py-3 px-4 text-right">Amount Due</th>
                      <th className="py-3 px-4 text-center">Status</th>
                      <th className="py-3 px-4">Date Cleared</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-border/40 font-mono">
                    {creditRows.map(credit => (
                      <tr key={credit.creditId} className="hover:bg-bg-3/40 transition-colors">
                        <td className="py-3 px-4 text-text-2 flex items-center gap-1">
                          {new Date(credit.createdAt).toLocaleString()}
                        </td>
                        <td className="py-3 px-4 text-neon-blue font-bold">
                          {credit.customerName}
                          <div className="text-[10px] text-text-3 font-sans font-normal">{credit.customerPhone}</div>
                        </td>
                        <td className="py-3 px-4 text-text-2">{credit.pcNumber}</td>
                        <td className="py-3 px-4 text-right text-text">₹{credit.originalBillAmount.toFixed(2)}</td>
                        <td className="py-3 px-4 text-right text-text">₹{credit.amountPaidInitially.toFixed(2)}</td>
                        <td className="py-3 px-4 text-right text-neon-red font-bold">₹{credit.creditAmount.toFixed(2)}</td>
                        <td className="py-3 px-4 text-center">
                          {credit.status.toLowerCase() === 'cleared' ? (
                            <span className="text-neon-green font-bold uppercase tracking-wider text-[10px] bg-neon-green/10 px-2 py-1 rounded border border-neon-green/20">Cleared</span>
                          ) : (
                            <span className="text-neon-red font-bold uppercase tracking-wider text-[10px] bg-neon-red/10 px-2 py-1 rounded border border-neon-red/20">Pending</span>
                          )}
                        </td>
                        <td className="py-3 px-4 text-text-3">
                          {credit.clearedAt ? new Date(credit.clearedAt).toLocaleString() : '-'}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          </div>

          {/* ── Maintenance Audit Logs ── */}
          <div className="card bg-bg-2 border border-border p-6 rounded-xl shadow-lg">
            <div className="flex justify-between items-center mb-6">
              <h2 className="font-heading font-extrabold text-sm uppercase tracking-wider text-text flex items-center gap-2">
                <Wrench className="w-4.5 h-4.5 text-neon-orange" />
                Maintenance Logs ({rangeLabel})
              </h2>
            </div>

            <div className="overflow-x-auto">
              {!maintenanceLogs || maintenanceLogs.length === 0 ? (
                <div className="text-center text-text-3 text-xs italic py-8 border border-dashed border-border rounded-lg">
                  No maintenance records found.
                </div>
              ) : (
                <table className="w-full text-left border-collapse text-xs whitespace-nowrap">
                  <thead>
                    <tr className="border-b border-border text-text-3 uppercase tracking-wider font-bold text-[10px]">
                      <th className="py-3 px-4">PC</th>
                      <th className="py-3 px-4">Marked Date & Time</th>
                      <th className="py-3 px-4">Marked By</th>
                      <th className="py-3 px-4">Reason</th>
                      <th className="py-3 px-4">Duration</th>
                      <th className="py-3 px-4">Status</th>
                      <th className="py-3 px-4">Resolved Date & Time</th>
                      <th className="py-3 px-4">Resolution Notes</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-border/40 font-mono">
                    {maintenanceLogs.map(log => (
                      <tr key={log.id} className={`hover:bg-bg-3/40 transition-colors ${log.isResolved ? 'opacity-75' : ''}`}>
                        <td className="py-3 px-4 text-text font-bold">{log.pcName}</td>
                        <td className="py-3 px-4 text-text-2">
                          {log.markedAt ? new Date(log.markedAt).toLocaleString() : '-'}
                        </td>
                        <td className="py-3 px-4 text-neon-blue font-bold">{log.operatorName}</td>
                        <td className="py-3 px-4 text-text-2">{log.reason}</td>
                        <td className="py-3 px-4 text-text-2">
                          {log.durationMinutes > 0
                            ? `${Math.floor(log.durationMinutes / 60)}h ${log.durationMinutes % 60}m`
                            : (log.isResolved ? '< 1m' : '-')}
                        </td>
                        <td className="py-3 px-4 text-center">
                          {log.isResolved ? (
                            <span className="text-neon-green font-bold uppercase tracking-wider text-[10px] bg-neon-green/10 px-2 py-1 rounded border border-neon-green/20">Resolved</span>
                          ) : (
                            <span className="text-neon-orange font-bold uppercase tracking-wider text-[10px] bg-neon-orange/10 px-2 py-1 rounded border border-neon-orange/20">Active</span>
                          )}
                        </td>
                        <td className="py-3 px-4 text-text-3">
                          {log.resolvedAt ? new Date(log.resolvedAt).toLocaleString() : '-'}
                        </td>
                        <td className="py-3 px-4 text-text-3 text-[10px]">{log.resolutionNotes || '-'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          </div>

        </>
      ) : null}
    </div>
    {report && (
      <EodPaymentSummaryBar
        report={report}
        targetDate={targetDate}
        height={summaryBarHeight}
        onHeightChange={setSummaryBarHeight}
      />
    )}
    </>
  );
}
