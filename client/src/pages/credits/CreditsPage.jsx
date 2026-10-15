import { useState, useEffect, useCallback } from 'react';
import { Clock, Search, CheckCircle2, User, Wallet, AlertCircle, Phone, X, Smartphone, Banknote, ArrowLeftRight, CalendarDays } from 'lucide-react';
import api from '../../config/api';
import PageHeader from '../../components/layout/PageHeader';
import { useToast } from '../../components/ui/Toast';
import { useAuth } from '../../contexts/AuthContext';
import { useBranch } from '../../contexts/BranchContext';
import { formatMoney } from '../../utils/money';

// No Rs 1000 or Rs 2000: neither note is in circulation in India any more.
const DENOMINATIONS = [10, 20, 50, 100, 200, 500];

// Local calendar day, not UTC. toISOString() would shift a credit taken late in the evening
// onto the following day for anyone east of Greenwich - which is every branch - so a shop
// searching the date they actually traded would not find their own row.
function toDateInputValue(value) {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '';
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

function formatDay(value) {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' });
}

function formatTime(value) {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', hour12: true });
}

export default function CreditsPage() {
  const { isSuperAdmin, user } = useAuth();
  const { activeBranch } = useBranch();
  const toast = useToast();
  
  const [credits, setCredits] = useState([]);
  const [isLoading, setIsLoading] = useState(true);
  const [filter, setFilter] = useState('pending'); // 'pending' | 'cleared' | 'all'
  const [searchQuery, setSearchQuery] = useState('');
  const [dateFilter, setDateFilter] = useState(''); // '' = every date
  
  const [selectedCredit, setSelectedCredit] = useState(null);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [payMethod, setPayMethod] = useState('cash'); // 'cash' | 'upi' | 'split'
  const [cashAmount, setCashAmount] = useState('');
  const [cashReceived, setCashReceived] = useState('');
  const [upiAmount, setUpiAmount] = useState('');
  const [notes, setNotes] = useState('');
  const [isClearing, setIsClearing] = useState(false);

  const targetBranchId = isSuperAdmin ? activeBranch?.id : user?.branchId;

  const fetchCredits = useCallback(async () => {
    if (!targetBranchId) return;
    try {
      setIsLoading(true);
      const [billRes, topUpRes] = await Promise.all([
        api.get('/credits', { params: { status: filter, page: 1, pageSize: 100 } }),
        api.get('/credits/member-topups', { params: { status: filter, page: 1, pageSize: 100 } }),
      ]);
      const billCredits = (billRes.data.data?.items || []).map(c => ({ ...c, source: 'bill' }));
      // Normalized to the exact shape the table/modal below already read, rather than teaching
      // either one a second shape - a member top-up credit has no bill or PC behind it (see
      // MemberTopUpCredit's own docstring), so those fields are filled with the member's own
      // identity and a label instead.
      const topUpCredits = (topUpRes.data.data?.items || []).map(c => ({
        id: c.id,
        customerName: c.memberName,
        customerPhone: c.memberUsername,
        pcNumber: 'Member Top-Up',
        originalBillAmount: c.creditAmount,
        amountPaidInitially: 0,
        creditAmount: c.creditAmount,
        status: c.status,
        createdAt: c.createdAt,
        clearedAt: c.clearedAt,
        source: 'topup',
      }));
      setCredits([...billCredits, ...topUpCredits]);
    } catch (err) {
      toast.error('Failed to fetch credits');
    } finally {
      setIsLoading(false);
    }
  }, [targetBranchId, filter]);

  useEffect(() => {
    fetchCredits();
  }, [fetchCredits]);

  const openClearModal = (credit) => {
    setSelectedCredit(credit);
    setPayMethod('cash');
    setCashAmount('0');
    setCashReceived('0');
    setUpiAmount('0');
    setNotes('');
    setIsModalOpen(true);
  };

  const handleClearCredit = async () => {
    try {
      setIsClearing(true);
      const due = Number(selectedCredit?.creditAmount) || 0;
      
      let payload = { notes };
      if (payMethod === 'cash') {
        payload.paymentType = 'Cash';
        payload.cashAmount = due;
        payload.cashReceived = parseFloat(cashReceived) || 0;
        payload.onlineAmount = 0;
        if (payload.cashReceived < due) {
          toast.error(`Cash received cannot be less than ₹${due}.`);
          return;
        }
      } else if (payMethod === 'upi') {
        payload.paymentType = 'Online';
        payload.cashAmount = 0;
        payload.cashReceived = 0;
        payload.onlineAmount = parseFloat(cashAmount) || 0; // Using cashAmount input as the single amount field for UPI
      } else {
        payload.paymentType = 'Split';
        payload.cashAmount = parseFloat(cashAmount) || 0;
        payload.cashReceived = parseFloat(cashAmount) || 0;
        payload.onlineAmount = parseFloat(upiAmount) || 0;
      }

      const endpoint = selectedCredit.source === 'topup'
        ? `/credits/member-topups/${selectedCredit.id}/clear`
        : `/credits/${selectedCredit.id}/clear`;
      await api.post(endpoint, payload);
      toast.success('Credit cleared successfully');
      setIsModalOpen(false);
      fetchCredits();
    } catch (err) {
      toast.error(err.response?.data?.error || 'Failed to clear credit');
    } finally {
      setIsClearing(false);
    }
  };

  if (isSuperAdmin && !activeBranch) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
        <Clock className="w-12 h-12 text-text-3 mb-4" />
        <h2 className="text-xl font-heading font-bold text-text mb-2">Select a Branch</h2>
        <p className="text-text-2">You must select a branch to view Credits.</p>
      </div>
    );
  }

  // A credit is dated by when it was taken. A cleared one is matched on either day - the day
  // it was given or the day the money finally came in - because both are days somebody would
  // reasonably look it up under, and being told "no credits found" for a date you know you
  // settled on is worse than showing the row twice across two searches.
  const matchesDate = (c) => {
    if (!dateFilter) return true;
    const onDay = (value) => value && toDateInputValue(value) === dateFilter;
    return onDay(c.createdAt) || onDay(c.clearedAt);
  };

  const filteredCredits = credits
    .filter(c =>
      (!searchQuery || (c.customerName || 'Walk-in').toLowerCase().includes(searchQuery.toLowerCase()))
      && matchesDate(c)
    )
    // Two sources merged above - newest first either way, same as the single-source list always was.
    .sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));

  const creditDue = Number(selectedCredit?.creditAmount) || 0;
  const cashTendered = Number(cashReceived) || 0;
  const cashChange = payMethod === 'cash' && cashTendered > creditDue ? cashTendered - creditDue : 0;

  return (
    <div className="h-full flex flex-col relative">
      <PageHeader
        title="Credit Dashboard"
        subtitle="Manage deferred payments and partial customer credits"
        icon="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z"
        badge="BETA"
      />

      {/* Tabs & Search */}
      <div className="flex items-center justify-between gap-4 mt-6 border-b border-border pb-2">
        <div className="flex items-center gap-2">
          <button
            onClick={() => setFilter('pending')}
            className={`px-4 py-2 text-sm font-bold uppercase tracking-wider rounded-t-lg border-b-2 transition-all ${
              filter === 'pending'
                ? 'border-neon-orange text-neon-orange bg-neon-orange/5'
                : 'border-transparent text-text-3 hover:text-text-2'
            }`}
          >
            Pending Credits
          </button>
          <button
            onClick={() => setFilter('cleared')}
            className={`px-4 py-2 text-sm font-bold uppercase tracking-wider rounded-t-lg border-b-2 transition-all ${
              filter === 'cleared'
                ? 'border-neon-blue text-neon-blue bg-neon-blue/5'
                : 'border-transparent text-text-3 hover:text-text-2'
            }`}
          >
            Cleared Credits
          </button>
        </div>
        <div className="flex items-center gap-2">
          {/* Native date input rather than a custom picker: it brings its own calendar, its own
              keyboard handling and the operator's own locale, and it is the one control on this
              page nobody has to be taught. */}
          <div className="relative">
            <CalendarDays className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-text-3 pointer-events-none" />
            <input
              type="date"
              value={dateFilter}
              onChange={(e) => setDateFilter(e.target.value)}
              className="pl-9 pr-3 py-1.5 text-sm bg-bg-3 border border-border rounded-lg text-text focus:outline-none focus:border-accent transition-colors [color-scheme:dark]"
              title="Show only credits from this date"
            />
          </div>

          {/* Only offered once a date is set, so the row does not carry a dead control all day. */}
          {dateFilter && (
            <button
              onClick={() => setDateFilter('')}
              className="px-3 py-1.5 text-xs font-bold uppercase tracking-wider text-text-3 hover:text-accent hover:bg-accent/10 border border-border rounded-lg transition-colors"
              title="Show every date again"
            >
              All dates
            </button>
          )}

          <div className="relative">
            <Search className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-text-3" />
            <input
              type="text"
              placeholder="Search customer name..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="pl-9 pr-4 py-1.5 text-sm bg-bg-3 border border-border rounded-lg text-text focus:outline-none focus:border-accent w-64 transition-colors"
            />
          </div>
        </div>
      </div>

      {/* List */}
      <div className="flex-1 mt-4 overflow-y-auto">
        {isLoading ? (
          <div className="flex items-center justify-center h-32">
            <span className="w-8 h-8 border-2 border-accent border-t-transparent rounded-full animate-spin"></span>
          </div>
        ) : filteredCredits.length === 0 ? (
          <div className="text-center text-text-3 py-12 bg-bg-2 rounded-xl border border-border/50">
            <CheckCircle2 className="w-12 h-12 mx-auto mb-3 opacity-20" />
            <p className="text-sm font-bold uppercase tracking-wider">No Credits Found</p>
          </div>
        ) : (
          <div className="bg-bg-2 border border-border rounded-xl overflow-x-auto shadow-sm pb-4">
            <table className="w-full text-left border-collapse whitespace-nowrap">
              <thead>
                <tr className="bg-bg-3/50 border-b border-border text-[10px] text-text-3 font-bold uppercase tracking-wider">
                  <th className="p-4 font-semibold">Date</th>
                  <th className="p-4 font-semibold">Customer</th>
                  <th className="p-4 font-semibold">PC</th>
                  <th className="p-4 font-semibold">Total Bill</th>
                  <th className="p-4 font-semibold">Paid Initially</th>
                  <th className="p-4 font-semibold">Amount Due</th>
                  <th className="p-4 font-semibold">Status</th>
                  <th className="p-4 text-right font-semibold">Action</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border/50">
                {filteredCredits.map(credit => (
                  <tr key={credit.id} className="hover:bg-bg-3/30 transition-colors group">
                    {/* When the credit was taken, with the time underneath - two credits for the
                        same customer on one day are otherwise indistinguishable. A cleared one
                        also says when the money actually came in, which is the question anybody
                        looking at a settled credit is really asking. */}
                    <td className="p-4 align-top">
                      <div className="flex flex-col">
                        <span className="text-sm font-mono text-text-2">{formatDay(credit.createdAt)}</span>
                        <span className="text-[10px] text-text-3 font-mono mt-1">{formatTime(credit.createdAt)}</span>
                        {credit.clearedAt && (
                          <span className="text-[10px] text-neon-blue font-mono mt-1">
                            cleared {formatDay(credit.clearedAt)}
                          </span>
                        )}
                      </div>
                    </td>
                    <td className="p-4">
                      <div className="flex flex-col">
                        <span className="font-heading font-bold text-text text-sm flex items-center gap-1.5">
                          <User className="w-3.5 h-3.5 text-accent opacity-70" />
                          {credit.customerName || 'Walk-in'}
                        </span>
                        {credit.customerPhone && credit.customerPhone !== 'N/A' && (
                          <span className="text-[10px] text-text-3 font-mono flex items-center gap-1 mt-1 ml-5">
                            <Phone className="w-2.5 h-2.5" />
                            {credit.customerPhone}
                          </span>
                        )}
                      </div>
                    </td>
                    <td className="p-4 text-sm font-mono text-text-2">
                      {credit.pcNumber || 'N/A'}
                    </td>
                    <td className="p-4 text-sm font-mono text-text-2">
                      ₹{credit.originalBillAmount}
                    </td>
                    <td className="p-4 text-sm font-mono text-text-2">
                      ₹{credit.amountPaidInitially}
                    </td>
                    <td className="p-4 text-base font-mono font-bold text-neon-orange">
                      ₹{credit.creditAmount}
                    </td>
                    <td className="p-4">
                      <span className={`text-[10px] font-bold font-mono px-2 py-1 rounded uppercase ${
                        credit.status?.toLowerCase() === 'cleared' 
                          ? 'bg-neon-blue/10 text-neon-blue border border-neon-blue/30'
                          : 'bg-neon-orange/10 text-neon-orange border border-neon-orange/30 animate-pulse'
                      }`}>
                        {credit.status}
                      </span>
                    </td>
                    <td className="p-4 text-right">
                      {credit.status?.toLowerCase() !== 'cleared' && (
                        <button
                          onClick={() => openClearModal(credit)}
                          className="px-4 py-1.5 bg-accent/10 hover:bg-accent text-accent hover:text-white border border-accent/30 rounded-lg text-[11px] font-bold uppercase tracking-wider transition-all"
                        >
                          Clear
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Clear Credit Modal */}
      {isModalOpen && selectedCredit && (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-bg/80 backdrop-blur-sm p-4">
          <div className="bg-bg-2 border border-border w-full max-w-md rounded-xl shadow-2xl overflow-hidden flex flex-col">
            <div className="px-5 py-4 border-b border-border bg-bg-3 flex items-center justify-between shrink-0">
              <h2 className="font-heading font-bold text-text uppercase tracking-wider text-sm flex items-center gap-2">
                <Wallet className="w-4 h-4 text-accent" />
                Clear Credit
              </h2>
              <button onClick={() => setIsModalOpen(false)} className="text-text-3 hover:text-text p-1">
                <X className="w-4 h-4" />
              </button>
            </div>
            
            <div className="p-5 space-y-4 overflow-y-auto">
              <div className="bg-bg-3/50 p-3 rounded-lg border border-border/50 text-center">
                <div className="text-[10px] text-text-3 font-bold uppercase tracking-widest mb-1">
                  Customer
                </div>
                <div className="font-bold text-text text-lg">
                  {selectedCredit.customerName || 'Walk-in'}
                </div>
                <div className="mt-2 text-xs text-text-2 font-mono">
                  Pending: <span className="text-neon-orange font-bold text-base">₹{selectedCredit.creditAmount}</span>
                </div>
              </div>

              {/* Payment Method */}
              <div>
                <label className="text-[10px] text-text-3 font-bold uppercase tracking-widest mb-2 block">
                  Payment Method
                </label>
                <div className="grid grid-cols-3 gap-2">
                  {[
                    { id: 'cash', label: 'Cash', Icon: Banknote },
                    { id: 'upi', label: 'UPI', Icon: Smartphone },
                    { id: 'split', label: 'Split', Icon: ArrowLeftRight },
                  ].map(({ id, label, Icon }) => (
                    <button
                      key={id}
                      onClick={() => setPayMethod(id)}
                      className={`py-2 flex items-center justify-center gap-1.5 rounded-lg border text-xs font-bold uppercase tracking-wider transition-all ${
                        payMethod === id
                          ? 'bg-accent/15 border-accent text-accent'
                          : 'bg-bg-3 border-border text-text-3 hover:border-accent/40 hover:text-text-2'
                      }`}
                    >
                      <Icon className="w-3.5 h-3.5" /> {label}
                    </button>
                  ))}
                </div>
              </div>

              {/* Amount Inputs */}
              {payMethod !== 'split' ? (
                <div>
                  <label className="text-[10px] text-text-3 font-bold uppercase tracking-widest mb-2 block">
                    {payMethod === 'cash' ? 'Cash Received from Customer (₹)' : 'Amount Paid (₹)'}
                  </label>
                  <input
                    type="number"
                    value={payMethod === 'cash' ? cashReceived : cashAmount}
                    onChange={e => {
                      if (payMethod === 'cash') {
                        setCashReceived(e.target.value);
                      } else {
                        setCashAmount(e.target.value);
                      }
                    }}
                    className="w-full bg-bg border border-border text-text font-mono text-lg rounded-lg p-2.5 focus:border-accent focus:ring-1 focus:ring-accent transition-all"
                  />
                  {payMethod === 'cash' && (
                    <div className="flex flex-wrap gap-1.5 mt-3">
                      {DENOMINATIONS.map(d => (
                        <button
                          key={d}
                          onClick={() => setCashReceived(String(d))}
                          className={`px-2.5 py-1 rounded border text-[11px] font-mono font-bold transition-colors ${
                            parseFloat(cashReceived) === d
                              ? 'bg-accent/15 border-accent text-accent'
                              : 'bg-bg-3 border-border text-text-2 hover:border-accent/40'
                          }`}
                        >
                          ₹{d}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              ) : (
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="text-[10px] text-text-3 font-bold uppercase tracking-widest mb-2 block">Cash</label>
                    <input
                      type="number" value={cashAmount} onChange={e => setCashAmount(e.target.value)}
                      className="w-full bg-bg border border-border text-text font-mono text-lg rounded-lg p-2.5 focus:border-neon-blue focus:ring-1 focus:ring-neon-blue transition-all"
                    />
                  </div>
                  <div>
                    <label className="text-[10px] text-text-3 font-bold uppercase tracking-widest mb-2 block">UPI</label>
                    <input
                      type="number" value={upiAmount} onChange={e => setUpiAmount(e.target.value)}
                      className="w-full bg-bg border border-border text-text font-mono text-lg rounded-lg p-2.5 focus:border-neon-purple focus:ring-1 focus:ring-neon-purple transition-all"
                    />
                  </div>
                </div>
              )}
              
              {payMethod === 'split' && (
                <div className="text-center text-xs font-mono">
                  Total: ₹{(parseFloat(cashAmount || 0) + parseFloat(upiAmount || 0))} 
                  <span className="text-text-3 ml-1">/ ₹{selectedCredit.creditAmount}</span>
                </div>
              )}

              {payMethod === 'cash' && cashChange > 0 && (
                <div className="flex justify-between items-center bg-neon-orange/10 border border-neon-orange/30 rounded-lg px-3 py-2.5">
                  <span className="text-xs font-bold uppercase tracking-wider text-neon-orange">Change Returned</span>
                  <span className="font-mono text-xl font-bold text-neon-orange">₹{formatMoney(cashChange)}</span>
                </div>
              )}

              <div>
                <label className="text-[10px] text-text-3 font-bold uppercase tracking-widest mb-2 block">
                  Notes (Optional)
                </label>
                <input
                  type="text"
                  value={notes}
                  onChange={e => setNotes(e.target.value)}
                  placeholder="e.g. Paid by friend"
                  className="w-full bg-bg border border-border text-text text-sm rounded-lg p-2.5 focus:border-accent transition-all"
                />
              </div>
            </div>

            <div className="p-4 border-t border-border bg-bg-3 shrink-0 flex justify-end gap-2">
              <button
                onClick={() => setIsModalOpen(false)}
                className="px-4 py-2 border border-border text-text-2 hover:bg-bg hover:text-text rounded-lg font-bold text-xs uppercase tracking-wider transition-colors"
                disabled={isClearing}
              >
                Cancel
              </button>
              <button
                onClick={handleClearCredit}
                disabled={isClearing}
                className="px-6 py-2 bg-accent hover:bg-accent-dark text-white shadow-[0_0_10px_rgba(220,38,38,0.3)] rounded-lg font-bold text-xs uppercase tracking-wider transition-colors flex items-center gap-2 disabled:opacity-50"
              >
                {isClearing ? 'Processing...' : 'Confirm Clear'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
