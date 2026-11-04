import useMemberBalanceUpdates from '../../hooks/useMemberBalanceUpdates';
import { useState, useEffect, useCallback } from 'react';
import { Wallet, AlertTriangle, ArrowUpRight, ArrowDownRight, Calendar, Download } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useBranch } from '../../contexts/BranchContext';
import api from '../../config/api';
import PageHeader from '../../components/layout/PageHeader';
import { format } from 'date-fns';
import { createReport, addTable, save } from '../../utils/pdfReport';

const todayIso = () => format(new Date(), 'yyyy-MM-dd');

// Remembered per-browser so reopening the desk keeps whatever range an operator was last
// looking at, rather than always snapping back to today.
const readStoredDate = (key) => {
  try {
    return localStorage.getItem(key) || todayIso();
  } catch {
    return todayIso();
  }
};

export default function WalletDeskPage() {
  const { isSuperAdmin, user } = useAuth();
  const { activeBranch } = useBranch();

  const [data, setData] = useState(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState(null);
  const [fromDate, setFromDate] = useState(() => readStoredDate('walletDesk.fromDate'));
  const [toDate, setToDate] = useState(() => readStoredDate('walletDesk.toDate'));

  useEffect(() => {
    try { localStorage.setItem('walletDesk.fromDate', fromDate); } catch { /* ignore */ }
  }, [fromDate]);

  useEffect(() => {
    try { localStorage.setItem('walletDesk.toDate', toDate); } catch { /* ignore */ }
  }, [toDate]);

  const targetBranchId = isSuperAdmin ? activeBranch?.id : user?.branchId;

  const fetchWalletDesk = useCallback(async (silent = false) => {
    if (isSuperAdmin && !targetBranchId) {
      if (!silent) setData(null);
      if (!silent) setIsLoading(false);
      return;
    }

    if (!silent) setIsLoading(true);
    try {
      setError(null);
      const response = await api.get('/system-desks/wallet/active', {
        params: { branchId: targetBranchId, fromDate, toDate },
      });
      setData(response.data.data);
    } catch (err) {
      setError(err.response?.data?.message || 'Failed to fetch wallet desk summary');
    } finally {
      if (!silent) setIsLoading(false);
    }
  }, [targetBranchId, isSuperAdmin, fromDate, toDate]);

  useEffect(() => {
    fetchWalletDesk();
  }, [fetchWalletDesk]);

  // Safety net so the operator's totals never sit stale against the server's own ledger while
  // the page is left open across a shift - same reasoning and interval as SessionsPage.
  useMemberBalanceUpdates(() => fetchWalletDesk(true));

  useEffect(() => {
    const interval = setInterval(() => fetchWalletDesk(true), 20000);
    return () => clearInterval(interval);
  }, [fetchWalletDesk]);

  const resetToToday = () => {
    setFromDate(todayIso());
    setToDate(todayIso());
  };

  const handleDownloadPdf = () => {
    if (!data || data.transactions.length === 0) return;
    const rangeLabel = fromDate === toDate ? fromDate : `${fromDate} to ${toDate}`;
    const subtitle = `${activeBranch?.name || 'Branch'}  •  ${rangeLabel}`;
    const { doc } = createReport({ title: 'Member Amount Desk', subtitle });
    let y = 90;

    y = addTable(doc, y, {
      title: 'Member Amount Desk', subtitle,
      heading: `Total Top-Ups: Rs ${data.totalWalletTopUps.toFixed(2)}   |   Total Deductions: Rs ${data.totalWalletDeductions.toFixed(2)}`,
      head: ['Date & Time', 'Description', 'PC', 'Duration', 'Amount'],
      body: data.transactions.map(tx => {
        const isTopUp = tx.action.includes('Recharge');
        const match = tx.description.match(/^(.*) \((.*)\)$/);
        const mainText = match ? match[1] : tx.description;
        const customerName = match ? match[2] : null;
        return [
          format(new Date(tx.timestamp), 'MMM d, hh:mm a'),
          customerName ? `${customerName} - ${mainText}` : mainText,
          tx.pcName || '-',
          tx.durationMinutes != null ? `${tx.durationMinutes}m` : '-',
          `${isTopUp ? '+' : '-'}Rs ${tx.amount.toFixed(2)}`,
        ];
      }),
    });

    save(doc, `member-amount-desk-${fromDate}${fromDate !== toDate ? `_to_${toDate}` : ''}.pdf`);
  };

  if (isSuperAdmin && !activeBranch) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
        <Wallet className="w-12 h-12 text-text-3 mb-4" />
        <h2 className="text-xl font-heading font-bold text-text mb-2">Select a Branch</h2>
        <p className="text-text-2">You must select a branch to access the Member Amount Desk.</p>
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

  return (
    <div className="h-full flex flex-col max-w-4xl mx-auto">
      <div className="mb-6">
        <PageHeader
          title="Member Amount Desk"
          subtitle="Member Amount Top-ups and Deductions"
          icon="M3 10h18M7 15h1m4 0h1m-7 4h12a3 3 0 003-3V8a3 3 0 00-3-3H6a3 3 0 00-3 3v8a3 3 0 003 3z"
          badge="SYSTEM"
        />
      </div>

      <div className="flex flex-wrap items-center gap-3 mb-6 bg-bg-2 border border-border rounded-xl p-4">
        <Calendar className="w-4 h-4 text-text-3 shrink-0" />
        <div className="flex items-center gap-2">
          <label className="text-xs text-text-3 uppercase tracking-wider">From</label>
          <input
            type="date"
            value={fromDate}
            max={toDate}
            onChange={(e) => setFromDate(e.target.value)}
            className="bg-bg-3 border border-border rounded-lg px-3 py-1.5 text-sm text-text"
          />
        </div>
        <div className="flex items-center gap-2">
          <label className="text-xs text-text-3 uppercase tracking-wider">To</label>
          <input
            type="date"
            value={toDate}
            min={fromDate}
            max={todayIso()}
            onChange={(e) => setToDate(e.target.value)}
            className="bg-bg-3 border border-border rounded-lg px-3 py-1.5 text-sm text-text"
          />
        </div>
        <button
          onClick={resetToToday}
          className="text-xs font-medium text-accent hover:text-accent/80 transition-colors ml-auto"
        >
          Today
        </button>
        <button
          onClick={handleDownloadPdf}
          disabled={!data || data.transactions.length === 0}
          className="btn-secondary py-1.5 px-3 flex items-center gap-1.5 text-xs font-bold disabled:opacity-50 disabled:cursor-not-allowed"
        >
          <Download className="w-3.5 h-3.5" /> Download PDF
        </button>
      </div>

      {error && (
        <div className="bg-neon-red/10 border border-neon-red/30 text-neon-red p-3 rounded-xl mb-4 flex items-center gap-2 text-sm">
          <AlertTriangle className="w-4 h-4" /> {error}
        </div>
      )}

      {data && (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
          {/* Summary Cards */}
          <div className="col-span-1 space-y-4">
            <div className="border border-neon-blue/30 bg-bg-2 rounded-xl p-6 shadow-[0_0_30px_rgba(0,195,255,0.05)]">
              <h3 className="text-sm font-heading font-bold uppercase tracking-wider text-text-2 mb-4">Total Top-Ups</h3>
              <div className="text-3xl font-mono font-bold text-neon-blue mb-2">
                ₹{data.totalWalletTopUps.toFixed(2)}
              </div>
            </div>
            
            <div className="border border-neon-orange/30 bg-bg-2 rounded-xl p-6 shadow-[0_0_30px_rgba(255,102,0,0.05)]">
              <h3 className="text-sm font-heading font-bold uppercase tracking-wider text-text-2 mb-4">Total Deductions</h3>
              <div className="text-3xl font-mono font-bold text-neon-orange mb-2">
                ₹{data.totalWalletDeductions.toFixed(2)}
              </div>
            </div>
          </div>

          {/* Transactions List */}
          <div className="col-span-1 md:col-span-2 border border-border bg-bg-2 rounded-xl p-6 h-fit max-h-[60vh] flex flex-col">
            <h3 className="text-sm font-heading font-bold uppercase tracking-wider text-text mb-4">Member Amount Transactions History</h3>
            {data.transactions.length === 0 ? (
              <div className="flex-1 flex items-center justify-center text-text-3 text-sm py-8">
                No Member Amount transactions in this date range.
              </div>
            ) : (
              <div className="flex-1 overflow-y-auto pr-2 scrollbar-thin">
                <div className="space-y-3">
                  {data.transactions.map((tx) => {
                    const isTopUp = tx.action.includes('Recharge');
                    const match = tx.description.match(/^(.*) \((.*)\)$/);
                    const mainText = match ? match[1] : tx.description;
                    const customerName = match ? match[2] : null;
                    return (
                      <div key={tx.id} className="flex items-center justify-between p-3 rounded-lg bg-bg-3 border border-border">
                        <div className="flex items-center gap-3">
                          <div className={`w-8 h-8 rounded-full flex items-center justify-center ${isTopUp ? 'bg-neon-blue/10 text-neon-blue' : 'bg-neon-orange/10 text-neon-orange'}`}>
                            {isTopUp ? <ArrowUpRight className="w-4 h-4" /> : <ArrowDownRight className="w-4 h-4" />}
                          </div>
                          <div>
                            <div className="flex items-center gap-2">
                              <p className="text-sm font-medium text-text">
                                {customerName ? `${customerName} - ${mainText}` : mainText}
                              </p>
                            </div>
                            <p className="text-xs text-text-3">
                              {format(new Date(tx.timestamp), 'MMM d, hh:mm a')}
                              {tx.pcName && ` • ${tx.pcName}`}
                              {tx.durationMinutes != null && ` • ${tx.durationMinutes}m`}
                            </p>
                          </div>
                        </div>
                        <div className={`font-mono font-bold ${isTopUp ? 'text-neon-blue' : 'text-neon-orange'}`}>
                          {isTopUp ? '+' : '-'}₹{tx.amount.toFixed(2)}
                        </div>
                      </div>
                    );
                  })}
                </div>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
