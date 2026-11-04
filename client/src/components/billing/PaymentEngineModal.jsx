import useMemberBalanceUpdates, { withFreshBalance } from '../../hooks/useMemberBalanceUpdates';
import { useState, useEffect } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { X, AlertTriangle, Receipt, Wallet, CreditCard, Banknote, Gamepad2, Coffee, KeyRound, ShieldCheck, ShieldAlert, Eye, EyeOff, Search } from 'lucide-react';
import { processPayment, getMemberById } from '../../api/billing.api';
import { memberLogin, getMembers } from '../../api/members.api';
import { useToast } from '../ui/Toast';
import { formatMoney } from '../../utils/money';

// Quick-tender presets for cash (common Indian denominations)
// No Rs 1000 or Rs 2000: neither note is in circulation in India any more.
const QUICK_TENDER = [10, 20, 50, 100, 200, 500];

/**
 * PaymentEngineModal — SOP §10.2 full split-payment engine
 * Supports: Full Cash | Full UPI | Split | Wallet | Member+Cash top-up
 * Props: bill, onClose, onPaymentSuccess
 */
export default function PaymentEngineModal({ bill, onClose, onPaymentSuccess }) {
  const toast = useToast();

  // Payment amounts
  const [cashAmount, setCashAmount] = useState(0);
  const [onlineAmount, setOnlineAmount] = useState(0);
  const [walletAmount, setWalletAmount] = useState(0);
  const [cashReceived, setCashReceived] = useState(0);

  // Member wallet info
  const [memberWallet, setMemberWallet] = useState(null);

  // Wallet authentication
  const [walletVerified, setWalletVerified] = useState(false);
  const [walletAuthUsername, setWalletAuthUsername] = useState('');
  const [walletAuthPassword, setWalletAuthPassword] = useState('');
  const [walletAuthError, setWalletAuthError] = useState(null);
  const [walletAuthLoading, setWalletAuthLoading] = useState(false);
  const [showWalletPassword, setShowWalletPassword] = useState(false);

  // Dynamic member search & select
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState([]);
  const [searching, setSearching] = useState(false);
  const [selectedMember, setSelectedMember] = useState(null);

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  // Reset all state whenever the bill changes
  useEffect(() => {
    setCashAmount(0);
    setOnlineAmount(0);
    setWalletAmount(0);
    setCashReceived(0);
    setError(null);
    setWalletVerified(false);
    setWalletAuthUsername('');
    setWalletAuthPassword('');
    setWalletAuthError(null);
    setSelectedMember(null);
    setMemberWallet(null);
  }, [bill?.id]);

  // Reset wallet verification when wallet amount is cleared
  useEffect(() => {
    if (walletAmount === 0) {
      setWalletVerified(false);
      setWalletAuthUsername('');
      setWalletAuthPassword('');
      setWalletAuthError(null);
    }
  }, [walletAmount]);

  // Fetch member wallet balance if bill has a linked member
  useEffect(() => {
    if (bill?.memberId) {
      getMemberById(bill.memberId)
        .then(m => {
          setMemberWallet(m);
          setSelectedMember(m);
        })
        .catch(() => {
          setMemberWallet(null);
          setSelectedMember(null);
        });
    } else {
      setMemberWallet(null);
      setSelectedMember(null);
    }
  }, [bill?.memberId]);

  useMemberBalanceUpdates((u) => {
    setMemberWallet(m => withFreshBalance(m, u));
    setSelectedMember(m => withFreshBalance(m, u));
    setSearchResults(list => list.map(m => withFreshBalance(m, u)));
  });

  if (!bill) return null;

  const total = bill.totalAmount;
  const totalInput = cashAmount + onlineAmount + walletAmount;
  const balanceDue = total - totalInput;
  const changeReturned = cashAmount > 0 && cashReceived > cashAmount
    ? cashReceived - cashAmount
    : 0;

  // Remaining balance unallocated to any method
  const remaining = Math.max(0, balanceDue);

  // Fill remaining into a specific payment method
  const fillRemaining = (setter) => {
    if (remaining > 0) setter(prev => +(prev + remaining).toFixed(2));
  };

  // Quick tender: set cashReceived to preset AND auto-fill cash amount if not set
  const quickTender = (amount) => {
    if (cashAmount === 0) setCashAmount(Math.min(amount, total - onlineAmount - walletAmount));
    setCashReceived(amount);
  };

  // Full payment shortcuts
  const payAll = (method) => {
    setCashAmount(method === 'cash' ? total : 0);
    setOnlineAmount(method === 'online' ? total : 0);
    setWalletAmount(method === 'wallet' ? total : 0);
    setCashReceived(method === 'cash' ? total : 0);
  };

  const handleSearchMember = async (query) => {
    setSearchQuery(query);
    if (query.trim().length < 2) {
      setSearchResults([]);
      return;
    }
    setSearching(true);
    try {
      const branchId = bill.branchId;
      const res = await getMembers(branchId, query, 1, 10);
      setSearchResults(res?.items ?? (Array.isArray(res) ? res : []));
    } catch {
      setSearchResults([]);
    } finally {
      setSearching(false);
    }
  };

  const selectMember = async (m) => {
    setSelectedMember(m);
    setSearchResults([]);
    setSearchQuery('');
    setWalletVerified(false);
    setWalletAuthUsername(m.username || '');
    setWalletAuthPassword('');
    try {
      const details = await getMemberById(m.id);
      setMemberWallet(details);
    } catch {
      setMemberWallet(m);
    }
  };

  const clearMember = () => {
    setSelectedMember(null);
    setMemberWallet(null);
    setWalletAmount(0);
    setWalletVerified(false);
    setWalletAuthUsername('');
    setWalletAuthPassword('');
    setWalletAuthError(null);
  };

  const handleWalletVerify = async () => {
    if (!walletAuthUsername.trim() || !walletAuthPassword) return;
    setWalletAuthError(null);
    setWalletAuthLoading(true);
    try {
      await memberLogin({ identifier: walletAuthUsername.trim(), password: walletAuthPassword });
      setWalletVerified(true);
    } catch {
      setWalletAuthError('Invalid username or password. Please try again.');
    } finally {
      setWalletAuthLoading(false);
    }
  };

  const handleProcess = async () => {
    if (Math.abs(totalInput - total) > 0.01) {
      setError('Total payment must exactly match the bill grand total.');
      return;
    }
    if (cashAmount > 0 && cashReceived < cashAmount) {
      setError('Cash received cannot be less than the cash amount applied.');
      return;
    }
    if (walletAmount > 0 && !walletVerified) {
      setError('Member credentials must be verified before using Member Amount payment.');
      return;
    }

    // Determine PaymentType string
    let pType = 'Split';
    if (cashAmount === total) pType = 'Cash';
    else if (onlineAmount === total) pType = 'Online';
    else if (walletAmount === total) pType = 'Wallet';

    setLoading(true);
    setError(null);
    try {
      const result = await processPayment(bill.id, {
        paymentType: pType,
        cashAmount,
        onlineAmount,
        walletAmount,
        cashReceived: cashAmount > 0 ? cashReceived : 0,
        memberId: selectedMember?.id,
      });
      if (result?.queued) {
        toast.success(result.message || 'Sent to the branch. It collects this payment within a few seconds.');
      } else {
        toast.success('Payment processed — PC released!');
      }
      onPaymentSuccess?.();
      onClose();
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Payment processing failed');
    } finally {
      setLoading(false);
    }
  };

  return (
    <AnimatePresence>
      <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/75 backdrop-blur-md">
        <motion.div
          initial={{ opacity: 0, scale: 0.95, y: 10 }}
          animate={{ opacity: 1, scale: 1, y: 0 }}
          exit={{ opacity: 0, scale: 0.95, y: 10 }}
          className="w-full max-w-lg bg-bg-2 border border-border rounded-xl shadow-2xl overflow-hidden flex flex-col max-h-[96vh]"
        >
          {/* ── Header ── */}
          <div className="p-4 border-b border-border bg-bg-3 flex items-center justify-between shrink-0">
            <div className="flex items-center gap-2">
              <Receipt className="w-5 h-5 text-accent" />
              <div>
                <h2 className="font-heading font-bold text-text uppercase tracking-wider text-base leading-none">
                  Process Payment
                </h2>
                <p className="text-[10px] text-text-3 font-mono mt-0.5">
                  {bill.billNumber} — {bill.pcNumber ? `Station ${bill.pcNumber}` : 'Walk-in'}
                </p>
              </div>
            </div>
            <button onClick={onClose} className="p-1 text-text-3 hover:text-text rounded transition-colors">
              <X className="w-5 h-5" />
            </button>
          </div>

          <div className="overflow-y-auto flex-1 p-5 space-y-4">
            {error && (
              <div className="flex items-start gap-2 p-3 bg-neon-red/10 border border-neon-red/20 rounded-lg text-neon-red text-sm">
                <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
                <p>{error}</p>
              </div>
            )}

            {/* ── Bill summary (§10.1) ── */}
            <div className="bg-bg-3 border border-border rounded-xl p-4 space-y-2">
              <div className="flex justify-between items-center text-xs text-text-2">
                <span className="flex items-center gap-1.5"><Gamepad2 className="w-3.5 h-3.5 text-neon-purple" /> Gaming</span>
                <span className="font-mono">₹{formatMoney(bill.gamingAmount)}</span>
              </div>
              <div className="flex justify-between items-center text-xs text-text-2">
                <span className="flex items-center gap-1.5"><Coffee className="w-3.5 h-3.5 text-neon-orange" /> Food & Drink</span>
                <span className="font-mono">₹{formatMoney(bill.foodAmount)}</span>
              </div>
              {bill.discountAmount > 0 && (
                <div className="flex justify-between items-center text-xs text-neon-purple">
                  <span>Discount</span>
                  <span className="font-mono">−₹{formatMoney(bill.discountAmount)}</span>
                </div>
              )}
              <div className="flex justify-between items-center pt-2 border-t border-border">
                <span className="font-bold text-text uppercase tracking-wider text-sm">Grand Total</span>
                <span className="font-mono font-bold text-2xl text-accent drop-shadow-[0_0_8px_rgba(255,51,102,0.3)]">
                  ₹{formatMoney(total)}
                </span>
              </div>
            </div>

            {/* ── Quick payment buttons ── */}
            <div>
              <div className="text-[10px] text-text-3 uppercase tracking-widest font-bold mb-2">Quick Pay</div>
              <div className="grid grid-cols-3 gap-1.5">
                <QuickBtn label="Full Cash" onClick={() => payAll('cash')} color="blue" />
                <QuickBtn label="Full UPI" onClick={() => payAll('online')} color="purple" />
                {selectedMember && <QuickBtn label="Full Member Amount" onClick={() => payAll('wallet')} color="accent" />}
              </div>
            </div>

            {/* ── Cash ── */}
            <div className="p-4 border border-border rounded-xl bg-bg-3 space-y-3">
              <div className="flex items-center justify-between">
                <label className="flex items-center gap-2 text-text font-bold uppercase tracking-wider text-sm">
                  <Banknote className="w-4 h-4 text-neon-blue" /> Cash
                </label>
                <button
                  onClick={() => fillRemaining(setCashAmount)}
                  className="text-[10px] text-neon-blue font-bold tracking-wider hover:underline uppercase"
                >
                  Fill Remaining
                </button>
              </div>

              <div className="grid grid-cols-2 gap-3">
                <div>
                  <span className="text-[10px] text-text-3 uppercase font-bold">Applied to Bill</span>
                  <input
                    type="number" min="0" max={total}
                    value={cashAmount || ''}
                    onChange={e => setCashAmount(Math.max(0, Number(e.target.value) || 0))}
                    className="w-full mt-1 bg-bg-2 border border-border text-text font-mono text-lg rounded-lg p-2.5 focus:border-neon-blue focus:ring-1 focus:ring-neon-blue transition-all"
                  />
                </div>
                <div>
                  <span className="text-[10px] text-text-3 uppercase font-bold">Cash Tendered</span>
                  <input
                    type="number" min="0"
                    value={cashReceived || ''}
                    onChange={e => setCashReceived(Math.max(0, Number(e.target.value) || 0))}
                    className="w-full mt-1 bg-bg-2 border border-border text-text font-mono text-lg rounded-lg p-2.5 focus:border-neon-blue focus:ring-1 focus:ring-neon-blue transition-all"
                  />
                </div>
              </div>

              {/* Quick tender denomination buttons (§10.3) */}
              {cashAmount > 0 && (
                <div>
                  <span className="text-[10px] text-text-3 uppercase font-bold block mb-1.5">Quick Tender</span>
                  <div className="flex flex-wrap gap-1.5">
                    {QUICK_TENDER.filter(d => d >= cashAmount).slice(0, 6).map(d => (
                      <button
                        key={d}
                        onClick={() => quickTender(d)}
                        className={`px-2.5 py-1 rounded border text-[11px] font-mono font-bold transition-colors ${
                          cashReceived === d
                            ? 'bg-neon-blue/20 border-neon-blue text-neon-blue'
                            : 'bg-bg-2 border-border text-text-2 hover:border-neon-blue/50'
                        }`}
                      >
                        ₹{d}
                      </button>
                    ))}
                  </div>
                </div>
              )}

              {/* Change display (§10.3) */}
              {changeReturned > 0 && (
                <div className="flex justify-between items-center pt-2 border-t border-border text-neon-orange font-bold">
                  <span className="text-sm uppercase tracking-wider">Return Change</span>
                  <span className="font-mono text-xl">₹{formatMoney(changeReturned)}</span>
                </div>
              )}
            </div>

            {/* ── Online / UPI ── */}
            <div className="p-4 border border-border rounded-xl bg-bg-3">
              <div className="flex items-center justify-between mb-3">
                <label className="flex items-center gap-2 text-text font-bold uppercase tracking-wider text-sm">
                  <CreditCard className="w-4 h-4 text-neon-purple" /> Online / UPI
                </label>
                <button
                  onClick={() => fillRemaining(setOnlineAmount)}
                  className="text-[10px] text-neon-purple font-bold tracking-wider hover:underline uppercase"
                >
                  Fill Remaining
                </button>
              </div>
              <input
                type="number" min="0" max={total}
                value={onlineAmount || ''}
                onChange={e => setOnlineAmount(Math.max(0, Number(e.target.value) || 0))}
                className="w-full bg-bg-2 border border-border text-text font-mono text-lg rounded-lg p-2.5 focus:border-neon-purple focus:ring-1 focus:ring-neon-purple transition-all"
              />
            </div>

            {/* ── Wallet ── */}
            <div className={`p-4 border rounded-xl bg-bg-3 ${selectedMember ? 'border-border' : 'border-border/40'}`}>
              <div className="flex items-center justify-between mb-3">
                <div>
                  <label className="flex items-center gap-2 text-text font-bold uppercase tracking-wider text-sm">
                    <Wallet className="w-4 h-4 text-accent" /> Member Amount
                  </label>
                  {selectedMember && memberWallet != null && (
                    <p className="text-[10px] text-text-3 font-mono mt-0.5">
                      G: <span className="text-neon-blue font-bold">₹{formatMoney(memberWallet.gamingBalance || 0)}</span> | F: <span className="text-neon-orange font-bold">₹{formatMoney(memberWallet.foodBalance || 0)}</span>
                    </p>
                  )}
                </div>
                {selectedMember ? (
                  <div className="flex items-center gap-2">
                    <button
                      onClick={() => fillRemaining(setWalletAmount)}
                      className="text-[10px] text-accent font-bold tracking-wider hover:underline uppercase"
                    >
                      Fill Remaining
                    </button>
                    {!bill.memberId && (
                      <button
                        onClick={clearMember}
                        className="text-[10px] text-text-3 font-bold tracking-wider hover:text-accent uppercase hover:underline"
                      >
                        Unlink
                      </button>
                    )}
                  </div>
                ) : (
                  <span className="text-[10px] text-text-3 uppercase tracking-wider">Members Only</span>
                )}
              </div>

              {selectedMember ? (
                <div className="space-y-3">
                  {/* Selected Member Info */}
                  {!bill.memberId && (
                    <div className="bg-accent/10 border border-accent/20 rounded px-2.5 py-1 text-xs text-accent font-bold">
                      Linked: {selectedMember.fullName} ({selectedMember.mobileNumber})
                    </div>
                  )}
                  <input
                    type="number" min="0" max={((memberWallet?.gamingBalance || 0) + (memberWallet?.foodBalance || 0))}
                    value={walletAmount || ''}
                    onChange={e => setWalletAmount(Math.max(0, Number(e.target.value) || 0))}
                    className="w-full bg-bg-2 border border-border text-text font-mono text-lg rounded-lg p-2.5 focus:border-accent focus:ring-1 focus:ring-accent transition-all"
                  />
                </div>
              ) : (
                <div className="space-y-2">
                  <p className="text-[10px] text-text-3">Search and link a member to pay via Member Amount:</p>
                  <div className="relative">
                    <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-text-3" />
                    <input
                      type="text"
                      placeholder="Search by name, phone..."
                      value={searchQuery}
                      onChange={e => handleSearchMember(e.target.value)}
                      className="w-full bg-bg-2 border border-border rounded pl-8 pr-3 py-2 text-xs text-text placeholder-text-3 focus:border-accent focus:outline-none transition-colors"
                    />
                    
                    {searching && (
                      <div className="absolute right-2.5 top-1/2 -translate-y-1/2">
                        <div className="w-3.5 h-3.5 rounded-full border border-accent border-t-transparent animate-spin" />
                      </div>
                    )}

                    {searchResults.length > 0 && (
                      <div className="absolute left-0 right-0 mt-1 max-h-40 overflow-y-auto bg-bg-2 border border-border rounded-lg shadow-xl z-20 divide-y divide-border">
                        {searchResults.map(m => (
                          <button
                            key={m.id}
                            type="button"
                            onClick={() => selectMember(m)}
                            className="w-full text-left px-3 py-2 text-xs text-text-2 hover:bg-bg-3 hover:text-text transition-colors flex justify-between items-center"
                          >
                            <span className="font-bold">{m.fullName}</span>
                            <span className="font-mono text-[10px] text-text-3">{m.mobileNumber}</span>
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
              )}

              {/* ── Wallet auth gate ── */}
              {walletAmount > 0 && selectedMember && (
                <div className="mt-3 pt-3 border-t border-border">
                  {walletVerified ? (
                    <div className="flex items-center gap-2 text-neon-green text-sm font-bold">
                      <ShieldCheck className="w-4 h-4" />
                      Member identity verified
                    </div>
                  ) : (
                    <div className="space-y-2">
                      <p className="text-[10px] text-text-3 uppercase tracking-widest font-bold flex items-center gap-1.5">
                        <KeyRound className="w-3 h-3" /> Member Login Required
                      </p>
                      {walletAuthError && (
                        <div className="flex items-center gap-1.5 text-neon-red text-xs">
                          <ShieldAlert className="w-3.5 h-3.5 shrink-0" />
                          {walletAuthError}
                        </div>
                      )}
                      <input
                        type="text"
                        autoComplete="off"
                        placeholder="Username"
                        value={walletAuthUsername}
                        onChange={e => setWalletAuthUsername(e.target.value.replace(/\s/g, '').toLowerCase())}
                        className="w-full bg-bg-2 border border-border text-text text-sm rounded-lg px-3 py-2 font-mono focus:border-accent focus:ring-1 focus:ring-accent transition-all placeholder:text-text-3"
                      />
                      <div className="relative">
                        <input
                          type={showWalletPassword ? 'text' : 'password'}
                          autoComplete="new-password"
                          placeholder="Password"
                          value={walletAuthPassword}
                          onChange={e => setWalletAuthPassword(e.target.value)}
                          onKeyDown={e => e.key === 'Enter' && handleWalletVerify()}
                          className="w-full bg-bg-2 border border-border text-text text-sm rounded-lg px-3 py-2 pr-9 font-mono focus:border-accent focus:ring-1 focus:ring-accent transition-all placeholder:text-text-3"
                        />
                        <button
                          type="button"
                          onClick={() => setShowWalletPassword(p => !p)}
                          className="absolute right-2.5 top-1/2 -translate-y-1/2 text-text-3 hover:text-text transition-colors"
                        >
                          {showWalletPassword ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />}
                        </button>
                      </div>
                      <button
                        onClick={handleWalletVerify}
                        disabled={walletAuthLoading || !walletAuthUsername || !walletAuthPassword}
                        className="w-full py-2 rounded-lg bg-accent/10 border border-accent/40 text-accent text-xs font-bold uppercase tracking-wider flex items-center justify-center gap-1.5 hover:bg-accent/20 transition-colors disabled:opacity-40"
                      >
                        {walletAuthLoading
                          ? <div className="w-3.5 h-3.5 rounded-full border-2 border-current border-t-transparent animate-spin" />
                          : <><ShieldCheck className="w-3.5 h-3.5" /> Verify Member</>
                        }
                      </button>
                    </div>
                  )}
                </div>
              )}
            </div>
          </div>

          {/* ── Footer ── */}
          <div className="p-4 border-t border-border bg-bg-3 space-y-3 shrink-0">
            {/* Running balance display */}
            <div className="flex justify-between items-center">
              <span className="text-xs text-text-3 uppercase tracking-wider font-bold">Balance Remaining</span>
              <span className={`font-mono font-bold text-lg transition-colors ${
                balanceDue > 0 ? 'text-neon-red' : balanceDue === 0 ? 'text-neon-blue' : 'text-neon-orange'
              }`}>
                {balanceDue === 0 ? '✓ Exact' : `₹${Math.abs(balanceDue).toFixed(0)} ${balanceDue > 0 ? 'short' : 'over'}`}
              </span>
            </div>

            <button
              onClick={handleProcess}
              disabled={loading || Math.abs(totalInput - total) > 0.01}
              className={`w-full py-3.5 rounded-lg text-sm font-bold uppercase tracking-wider flex items-center justify-center gap-2 transition-all ${
                Math.abs(totalInput - total) <= 0.01 && !loading
                  ? 'bg-accent/10 border border-accent text-accent hover:bg-accent/20 shadow-[0_0_12px_rgba(255,51,102,0.2)]'
                  : 'bg-bg-2 border border-border text-text-3 cursor-not-allowed'
              }`}
            >
              {loading
                ? <div className="w-5 h-5 rounded-full border-2 border-current border-t-transparent animate-spin" />
                : <><Receipt className="w-4 h-4" /> Complete Payment</>
              }
            </button>
          </div>
        </motion.div>
      </div>
    </AnimatePresence>
  );
}

// ── Quick button helper ───────────────────────────────────────────────────────
function QuickBtn({ label, onClick, color }) {
  const colorMap = {
    blue:   'border-neon-blue/40   bg-neon-blue/10   text-neon-blue   hover:bg-neon-blue/20',
    purple: 'border-neon-purple/40 bg-neon-purple/10 text-neon-purple hover:bg-neon-purple/20',
    accent: 'border-accent/40      bg-accent/10      text-accent      hover:bg-accent/20',
  };
  return (
    <button
      onClick={onClick}
      className={`py-1.5 rounded border text-[10px] font-bold uppercase tracking-wider transition-colors ${colorMap[color]}`}
    >
      {label}
    </button>
  );
}
