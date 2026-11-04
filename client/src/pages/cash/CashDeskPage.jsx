import { useState, useEffect, useCallback } from 'react';
import { Calculator, Plus, AlertTriangle, WifiOff, Calendar, Gamepad2 } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useBranch } from '../../contexts/BranchContext';
import { useSocket } from '../../contexts/SocketContext';
import api from '../../config/api';
import { format } from 'date-fns';
import PageHeader from '../../components/layout/PageHeader';
import OpenRegisterModal from '../../components/cash/OpenRegisterModal';
import AddTransactionModal from '../../components/cash/AddTransactionModal';
import Ps5EntryModal from '../../components/cash/Ps5EntryModal';
import TransactionFeed from '../../components/cash/TransactionFeed';

const todayIso = () => format(new Date(), 'yyyy-MM-dd');

// Remembered per-browser, same convention as Online Desk / Member Amount Desk, so reopening
// the ledger keeps whatever range was last being looked at instead of always snapping to today.
const readStoredDate = (key) => {
  try {
    return localStorage.getItem(key) || todayIso();
  } catch {
    return todayIso();
  }
};

export default function CashDeskPage() {
  const { isSuperAdmin, isTrueSuperAdmin, user } = useAuth();
  const { activeBranch } = useBranch();
  const { subscribe, connected, SIGNALR_HUBS } = useSocket();

  const [register, setRegister] = useState(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState(null);

  const [isAddTxModalOpen, setIsAddTxModalOpen] = useState(false);
  const [isPs5ModalOpen, setIsPs5ModalOpen] = useState(false);

  // The Ledger itself is date-filterable - a shift ending must never mean the cash it moved
  // becomes harder to find. Kept separate from `register`, which stays the live, right-now
  // drawer regardless of what range the Ledger below is currently showing.
  const [fromDate, setFromDate] = useState(() => readStoredDate('cashDesk.fromDate'));
  const [toDate, setToDate] = useState(() => readStoredDate('cashDesk.toDate'));
  const [ledger, setLedger] = useState(null);
  const [ledgerLoading, setLedgerLoading] = useState(true);
  const [ledgerError, setLedgerError] = useState(null);

  useEffect(() => {
    try { localStorage.setItem('cashDesk.fromDate', fromDate); } catch { /* ignore */ }
  }, [fromDate]);

  useEffect(() => {
    try { localStorage.setItem('cashDesk.toDate', toDate); } catch { /* ignore */ }
  }, [toDate]);

  const targetBranchId = isSuperAdmin ? activeBranch?.id : user?.branchId;

  const fetchLedger = useCallback(async () => {
    if (isSuperAdmin && !targetBranchId) {
      setLedger(null);
      setLedgerLoading(false);
      return;
    }
    setLedgerLoading(true);
    try {
      setLedgerError(null);
      const { data } = await api.get('/system-desks/cash/active', {
        params: { branchId: targetBranchId, fromDate, toDate },
      });
      setLedger(data.data);
    } catch (err) {
      setLedgerError(err.response?.data?.message || 'Failed to fetch cash desk ledger.');
    } finally {
      setLedgerLoading(false);
    }
  }, [targetBranchId, isSuperAdmin, fromDate, toDate]);

  useEffect(() => { fetchLedger(); }, [fetchLedger]);

  // Same live events the drawer above already reacts to - a correction or a new cash
  // transaction should appear in the Ledger immediately, not only after changing the date range.
  useEffect(() => {
    if (!connected || !targetBranchId) return;
    const unsubLedger = subscribe(SIGNALR_HUBS.CASH, 'CashRegisterUpdated', () => {
      fetchLedger();
    });
    return () => unsubLedger();
  }, [connected, subscribe, SIGNALR_HUBS.CASH, targetBranchId, fetchLedger]);

  const resetLedgerToToday = () => {
    setFromDate(todayIso());
    setToDate(todayIso());
  };

  const ledgerSection = (
    <div className="flex-1 bg-bg-2 border border-border rounded-xl p-4 flex flex-col min-h-0">
      <div className="flex flex-wrap items-center justify-between gap-3 mb-4 border-b border-border pb-3">
        <h3 className="text-text font-bold uppercase tracking-wider text-sm flex items-center gap-2">
          Shift Transaction Ledger
          <span className="bg-bg-3 px-2 py-0.5 rounded-full text-[10px] text-text-3 font-mono border border-border">Append Only</span>
        </h3>

        <div className="flex flex-wrap items-center gap-2">
          <Calendar className="w-4 h-4 text-text-3 shrink-0" />
          <input
            type="date"
            value={fromDate}
            max={toDate}
            onChange={(e) => setFromDate(e.target.value)}
            className="bg-bg-3 border border-border rounded-lg px-2.5 py-1 text-xs text-text"
          />
          <span className="text-text-3 text-xs">to</span>
          <input
            type="date"
            value={toDate}
            min={fromDate}
            max={todayIso()}
            onChange={(e) => setToDate(e.target.value)}
            className="bg-bg-3 border border-border rounded-lg px-2.5 py-1 text-xs text-text"
          />
          <button
            onClick={resetLedgerToToday}
            className="text-xs font-medium text-accent hover:text-accent/80 transition-colors"
          >
            Today
          </button>
        </div>
      </div>

      {ledgerError && (
        <div className="bg-neon-red/10 border border-neon-red/30 text-neon-red p-3 rounded-xl mb-4 flex items-center gap-2 text-sm">
          <AlertTriangle className="w-4 h-4" /> {ledgerError}
        </div>
      )}

      {ledgerLoading ? (
        <div className="flex justify-center py-8">
          <div className="w-6 h-6 border-2 border-accent border-t-transparent rounded-full animate-spin" />
        </div>
      ) : (
        <TransactionFeed transactions={ledger?.transactions || []} />
      )}
    </div>
  );

  const fetchActiveRegister = useCallback(async () => {
    if (isSuperAdmin && !targetBranchId) {
      setRegister(null);
      setIsLoading(false);
      return;
    }

    try {
      setError(null);
      const { data } = await api.get('/cash/active', { params: { branchId: targetBranchId } });
      setRegister(data.data);
    } catch (err) {
      if (err.response?.status === 404) {
        setRegister(null); // No active register => triggers OpenRegisterModal
      } else {
        setError(err.response?.data?.error || err.response?.data?.message || 'Failed to fetch cash register');
      }
    } finally {
      setIsLoading(false);
    }
  }, [targetBranchId, isSuperAdmin]);

  useEffect(() => {
    setIsLoading(true);
    fetchActiveRegister();
  }, [fetchActiveRegister]);

  // Real-Time SignalR Updates
  useEffect(() => {
    if (!connected || !targetBranchId) return;

    const unsubCash = subscribe(SIGNALR_HUBS.CASH, 'CashRegisterUpdated', () => {
      fetchActiveRegister(); // Re-fetch on any cash event (multi-operator sync)
    });

    return () => unsubCash();
  }, [connected, subscribe, SIGNALR_HUBS.CASH, targetBranchId, fetchActiveRegister]);

  if (isSuperAdmin && !activeBranch) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
        <Calculator className="w-12 h-12 text-text-3 mb-4" />
        <h2 className="text-xl font-heading font-bold text-text mb-2">Select a Branch</h2>
        <p className="text-text-2">You must select a branch to view the Cash Desk.</p>
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

  // 1. If no register is open: an operator is asked to open it. Only a true Super Admin is
  // never shown that form - they cannot physically count a drawer, and the server refuses the
  // request anyway (see EnsureNotSuperAdminForCashAsync) - just a plain read-only notice. An
  // Admin-mode operator (Quick Admin Switch) is physically at the counter and opens it like
  // anyone else. The Ledger still shows underneath either way, since it is a look back at cash
  // movements regardless of whether a drawer happens to be open right now.
  if (!register) {
    return (
      <div className="flex flex-col h-full gap-6">
        {isTrueSuperAdmin ? (
          <div className="flex flex-col items-center justify-center py-16 text-center border border-border bg-bg-2 rounded-xl">
            <Calculator className="w-10 h-10 text-text-3 mb-3" />
            <h2 className="text-text font-bold mb-1">No Register Open</h2>
            <p className="text-text-2 text-sm">No cash register is currently open at this branch.</p>
          </div>
        ) : (
          <OpenRegisterModal onRegisterOpened={fetchActiveRegister} />
        )}
        {ledgerSection}
      </div>
    );
  }

  // 2. Active Register Dashboard
  const { expectedDrawerCash, transactions } = register;

  // Financial Alerts (Enterprise Requirements)
  const isDrawerNegative = expectedDrawerCash < 0;
  const largeWithdrawalCount = transactions.filter(t => t.cashAmount < -10000).length;
  const recentAdjustments = transactions.filter(t => t.transactionType === 'adjustment').length;

  return (
    <div className="h-full flex flex-col">
      <div className="flex justify-between items-start mb-6">
        <PageHeader
          title="Cash Desk"
          subtitle="Live Cash Flow Control Center"
          icon="M9 7h6m0 10v-3m-3 3h.01M9 17h.01M9 14h.01M12 14h.01M15 11h.01M12 11h.01M9 11h.01M7 21h10a2 2 0 002-2V5a2 2 0 00-2-2H7a2 2 0 00-2 2v14a2 2 0 002 2z"
          badge="LIVE"
        />

        {!isTrueSuperAdmin && (
          <div className="flex items-center gap-2">
            <button
              onClick={() => setIsPs5ModalOpen(true)}
              className="btn-secondary flex items-center gap-2"
              title="A PS5 customer's play - paid cash, online, split or credit"
            >
              <Gamepad2 className="w-5 h-5" /> PS5
            </button>
            <button
              onClick={() => setIsAddTxModalOpen(true)}
              className="btn-primary flex items-center gap-2 shadow-lg shadow-accent/20"
            >
              <Plus className="w-5 h-5" /> Add Entry
            </button>
          </div>
        )}
      </div>

      {!connected && (
        <div className="bg-neon-orange/10 border border-neon-orange/30 text-neon-orange p-3 rounded-xl mb-4 flex items-center gap-2 text-sm font-bold uppercase tracking-wider">
          <WifiOff className="w-4 h-4" /> OFFLINE: Real-time synchronization is disconnected.
        </div>
      )}

      {error && (
        <div className="bg-neon-red/10 border border-neon-red/30 text-neon-red p-3 rounded-xl mb-4 flex items-center gap-2 text-sm">
          <AlertTriangle className="w-4 h-4" /> {error}
        </div>
      )}

      {/* Financial KPIs */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6 mb-6">

        {/* KPI 1: Expected Drawer (The Source of Truth) */}
        <div className={`col-span-1 lg:col-span-2 p-6 rounded-xl border flex flex-col justify-center ${
          isDrawerNegative ? 'bg-neon-red/5 border-neon-red' : 'bg-bg-2 border-border shadow-[0_0_20px_rgba(255,255,255,0.02)]'
        }`}>
          <div className="flex justify-between items-start mb-2">
            <h3 className="text-text-3 font-bold uppercase tracking-widest text-sm flex items-center gap-2">
              <Calculator className="w-4 h-4" /> Expected Drawer Total
            </h3>
            {isDrawerNegative && (
              <span className="bg-neon-red/20 text-neon-red text-[10px] px-2 py-1 rounded-full font-bold uppercase flex items-center gap-1">
                <AlertTriangle className="w-3 h-3" /> Negative Drawer
              </span>
            )}
          </div>
          <div className={`font-mono text-5xl font-bold tracking-tight ${isDrawerNegative ? 'text-neon-red' : 'text-accent drop-shadow-[0_0_12px_rgba(255,51,102,0.3)]'}`}>
            ₹{expectedDrawerCash}
          </div>
          <p className="text-text-3 text-xs mt-3 italic">
            Physical drawer cash must exactly match this total at EOD.
          </p>
        </div>

        {/* Alerts Panel */}
        <div className="p-4 rounded-xl border border-border bg-bg-2 flex flex-col gap-3">
          <h3 className="text-text-2 font-bold uppercase tracking-wider text-xs border-b border-border-2 pb-2">
            Financial Alerts
          </h3>
          {largeWithdrawalCount > 0 && (
            <div className="text-xs text-neon-orange flex gap-2">
              <AlertTriangle className="w-3.5 h-3.5 shrink-0" /> {largeWithdrawalCount} Large withdrawal(s) recorded this shift.
            </div>
          )}
          {recentAdjustments > 0 && (
            <div className="text-xs text-neon-purple flex gap-2">
              <AlertTriangle className="w-3.5 h-3.5 shrink-0" /> {recentAdjustments} Manual adjustment(s) recorded.
            </div>
          )}
          {isDrawerNegative && (
            <div className="text-xs text-neon-red flex gap-2">
              <AlertTriangle className="w-3.5 h-3.5 shrink-0" /> Cash drawer is mathematically negative. Check inwards.
            </div>
          )}
          {largeWithdrawalCount === 0 && recentAdjustments === 0 && !isDrawerNegative && (
            <div className="text-xs text-text-3 italic text-center py-4">
              All systems nominal.
            </div>
          )}
        </div>

      </div>

      {ledgerSection}

      {isPs5ModalOpen && (
        <Ps5EntryModal
          onClose={() => setIsPs5ModalOpen(false)}
          onDone={() => { fetchActiveRegister(); fetchLedger(); }}
        />
      )}

      {isAddTxModalOpen && (
        <AddTransactionModal
          register={register}
          onClose={() => setIsAddTxModalOpen(false)}
          onTransactionAdded={fetchActiveRegister}
        />
      )}
    </div>
  );
}
