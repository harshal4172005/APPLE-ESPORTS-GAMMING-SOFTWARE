import { useCallback, useEffect, useRef, useState } from 'react';
import { Wallet, GripHorizontal } from 'lucide-react';

const MIN_HEIGHT = 56;
const MAX_HEIGHT = 420;

const TABS = [
  { key: 'cash', label: 'Cash & Collection' },
];

// ── Fixed-to-viewport financial summary strip for the EOD dashboard, mirroring
// SessionActivityLog's pinned-bottom / drag-resizable pattern ──
//
// Same layout as the original two-column summary. What changed is only where the numbers come
// from: every total here used to be added up in this file, and three of those additions were
// wrong - wallet spending got counted as new money on top of the top-up that paid for it, a
// UPI top-up was shown as cash, and a promotional bonus was shown as if the customer had paid
// it. All of that arithmetic now happens once, on the server, next to the records it is adding
// up - this file only prints what it is told.
export default function EodPaymentSummaryBar({ report, targetDate, height, onHeightChange }) {
  const resizing = useRef(false);
  const [activeTab, setActiveTab] = useState('cash');

  const handlePointerDown = useCallback((e) => {
    e.preventDefault();
    resizing.current = true;
    document.body.style.cursor = 'ns-resize';
    document.body.style.userSelect = 'none';
  }, []);

  useEffect(() => {
    const handleMove = (e) => {
      if (!resizing.current) return;
      const clientY = e.touches ? e.touches[0].clientY : e.clientY;
      const next = Math.min(MAX_HEIGHT, Math.max(MIN_HEIGHT, window.innerHeight - clientY));
      onHeightChange?.(next);
    };
    const handleUp = () => {
      resizing.current = false;
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    window.addEventListener('mousemove', handleMove);
    window.addEventListener('mouseup', handleUp);
    window.addEventListener('touchmove', handleMove);
    window.addEventListener('touchend', handleUp);
    return () => {
      window.removeEventListener('mousemove', handleMove);
      window.removeEventListener('mouseup', handleUp);
      window.removeEventListener('touchmove', handleMove);
      window.removeEventListener('touchend', handleUp);
    };
  }, [onHeightChange]);

  if (!report) return null;

  const n = (v) => Number(v ?? 0);
  const pm = report.paymentMethods;

  // Cash and online each cover both what was paid at billing AND what was paid at the top-up
  // counter - two counters, one till. Top-ups are split by how they were actually paid (the
  // server tracks this per row) rather than lumped into cash regardless of method, which used
  // to overstate the drawer by the full value of every UPI top-up.
  const cashTotal = n(pm.totalCash) + n(pm.totalWalletTopUpsCash);
  const onlineTotal = n(pm.totalOnline) + n(pm.totalWalletTopUpsOnline);

  // Credit and Credit Cleared used to be shown as rows here too, unsummed - moved up to their
  // own two boxes on the stat grid above instead (EodDashboardPage.jsx), rather than sitting
  // next to a total that was never going to include them: both would double-count if they were
  // (a pending credit was never collected, so it was never in Cash/Online to begin with; a
  // cleared credit's payment already flows straight into Cash/Online the moment it's cleared).
  const grandTotal = cashTotal + onlineTotal
    - n(report.cash.totalPettyExpenses) + n(report.cash.totalCashInwards)
    - n(report.cash.totalOwnerWithdrawals);

  return (
    <div
      style={{ height }}
      className="fixed bottom-0 left-0 lg:left-[var(--sidebar-offset,240px)] right-0 z-30 bg-bg-2 border-t border-border flex flex-col shadow-[0_-4px_12px_rgba(0,0,0,0.25)] transition-[left] duration-200 ease-in-out"
    >
      {/* Drag handle */}
      <div
        onMouseDown={handlePointerDown}
        onTouchStart={handlePointerDown}
        className="flex items-center justify-center h-2.5 cursor-ns-resize hover:bg-bg-3 transition-colors flex-shrink-0"
      >
        <GripHorizontal className="w-4 h-4 text-text-3" />
      </div>

      <div className="px-3 py-1 border-b border-border bg-bg-3 flex items-center justify-between flex-shrink-0">
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-1.5 text-[10px] font-mono font-bold uppercase tracking-widest text-text-3">
            <Wallet className="w-3 h-3" />
            {targetDate}
          </div>
          <div className="flex items-center gap-1">
            {TABS.map(tab => (
              <button
                key={tab.key}
                onClick={() => setActiveTab(tab.key)}
                className={`px-2.5 py-1 rounded text-[10px] font-bold uppercase tracking-widest transition-colors ${
                  activeTab === tab.key
                    ? 'bg-accent text-white'
                    : 'text-text-3 hover:text-text-2 hover:bg-bg-2'
                }`}
              >
                {tab.label}
              </button>
            ))}
          </div>
        </div>
        <div className="font-mono text-xs font-bold text-neon-blue">
          End Total: ₹{grandTotal.toFixed(2)}
        </div>
      </div>

      <div className="flex-1 overflow-y-auto px-4 py-2">
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
          {/* Left column - the drawer. Exactly Opening Balance, Expected Drawer, Physically
              Counted, Cover Amount, plus the Difference pill (derived, not one of the owner's
              four, but kept - it's how a shortfall is actually seen). */}
          <div className="space-y-1.5 text-xs">
            <div className="flex justify-between items-center">
              <span className="text-text-2">Opening Balance</span>
              <span className="font-mono text-text">₹{report.cash.totalOpeningBalance}</span>
            </div>

            <div className="flex justify-between items-center border-t border-border pt-1.5">
              <span className="font-bold text-text">Expected Drawer</span>
              <span className="font-mono font-bold text-accent">₹{report.cash.expectedCashInDrawer}</span>
            </div>

            {/* "Not counted yet" rather than ₹0. Zero reads as an empty drawer - the whole day's
                takings gone - when the truth is that nobody has looked in it yet. */}
            <div className="flex justify-between items-center">
              <span className="font-bold text-text">Physically Counted</span>
              {report.cash.actualPhysicalCashCounted === null || report.cash.actualPhysicalCashCounted === undefined ? (
                <span className="text-text-3 text-[11px] italic">not counted yet</span>
              ) : (
                <span className="font-mono font-bold text-text">₹{report.cash.actualPhysicalCashCounted}</span>
              )}
            </div>

            {/* Only set once the last shift of the day closes - see CashRegister.CoverAmount. */}
            <div className="flex justify-between items-center">
              <span className="text-text-2">Cover Amount</span>
              {report.cash.coverAmount === null || report.cash.coverAmount === undefined ? (
                <span className="text-text-3 text-[11px] italic">not set yet</span>
              ) : (
                <span className="font-mono text-text">₹{report.cash.coverAmount}</span>
              )}
            </div>

            <div className="flex justify-between items-center bg-bg-3 px-3 py-1.5 rounded-lg border border-border mt-1">
              <span className="font-bold text-text uppercase tracking-widest text-[10px]">
                {report.cash.actualPhysicalCashCounted === null || report.cash.actualPhysicalCashCounted === undefined ? 'Difference' : 'Total Difference'}
              </span>
              {report.cash.totalDiscrepancy === null || report.cash.totalDiscrepancy === undefined ? (
                <span className="text-text-3 text-[11px] italic">unknown until it is counted</span>
              ) : (
                <span className={`font-mono font-bold ${Number(report.cash.totalDiscrepancy) === 0 ? 'text-neon-blue' : 'text-neon-red'}`}>
                  ₹{report.cash.totalDiscrepancy}
                </span>
              )}
            </div>
          </div>

          {/* Right column - the day's money: Cash, Online, Petty Expense, Cash Add, Owner
              Withdrawals, ending in Overall End Total. Credit and Credit Cleared moved up to
              their own boxes on the stat grid above (EodDashboardPage.jsx) - neither was ever
              summed into this total (both would double-count), so they no longer sit here
              unsummed next to it. "Cash" is labelled plainly on the owner's own instruction;
              the value underneath is unchanged - today's billing cash plus today's member
              wallet top-ups paid in cash. */}
          <div className="space-y-1.5 text-xs">
            <div className="flex justify-between items-center">
              <span className="text-text-2">Cash</span>
              <span className="font-mono text-neon-green">+ ₹{report.cash.totalCashSales}</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-text-2">Online</span>
              <span className="font-mono text-text">₹{onlineTotal.toFixed(2)}</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-text-2">Petty Expense</span>
              <span className="font-mono text-neon-red">- ₹{report.cash.totalPettyExpenses}</span>
            </div>
            <div className="flex justify-between items-center">
              <span className="text-text-2">Cash Add</span>
              <span className="font-mono text-neon-green">+ ₹{report.cash.totalCashInwards}</span>
            </div>
            {/* Shown only on a day something was actually withdrawn - most days this is zero,
                and a permanent "- ₹0" row here would just be noise. Still subtracted in
                grandTotal above either way, whether this row is visible or not. */}
            {n(report.cash.totalOwnerWithdrawals) > 0 && (
              <div className="flex justify-between items-center">
                <span className="text-text-2">Owner Withdrawals</span>
                <span className="font-mono text-neon-red">- ₹{report.cash.totalOwnerWithdrawals}</span>
              </div>
            )}
            <div className="flex justify-between items-center bg-neon-blue/10 px-3 py-1.5 rounded-lg border border-neon-blue/30 mt-1">
              <span className="font-bold text-neon-blue uppercase tracking-widest text-[10px]">Overall End Total</span>
              <span className="font-mono font-bold text-base text-neon-blue">₹{grandTotal.toFixed(2)}</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
