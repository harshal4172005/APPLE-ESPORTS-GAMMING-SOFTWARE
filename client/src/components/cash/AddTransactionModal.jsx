import { useState, useMemo } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { X, ArrowDownRight, ArrowUpRight, AlertTriangle, HelpCircle } from 'lucide-react';
import api from '../../config/api';

const TX_TYPES = {
  inward: { label: 'Cash In', icon: ArrowDownRight, color: 'text-neon-blue', polarity: 1 },
  petty_expense: { label: 'Expense', icon: ArrowUpRight, color: 'text-neon-orange', polarity: -1 },
  withdrawal: { label: 'Owner Takeout', icon: ArrowUpRight, color: 'text-neon-red', polarity: -1 },
  // No "Correction": the server only records these three (it decides the direction itself), and
  // End of Day only counts these three - the button only ever showed an error.
};

export default function AddTransactionModal({ register, onClose, onTransactionAdded }) {
  const [type, setType] = useState('petty_expense');
  const [amountStr, setAmountStr] = useState('');
  const [reason, setReason] = useState('');
  const [isAdjustmentPositive, setIsAdjustmentPositive] = useState(false);
  
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  const amount = Number(amountStr) || 0;

  // Transaction Safety UX: Compute final payload amount
  const finalAmount = useMemo(() => {
    if (type === 'adjustment') return isAdjustmentPositive ? amount : -amount;
    return amount * TX_TYPES[type].polarity;
  }, [type, amount, isAdjustmentPositive]);

  const newExpectedDrawer = register.expectedDrawerCash + finalAmount;

  const handleSubmit = async () => {
    if (amount <= 0) {
      setError("Amount must be greater than 0.");
      return;
    }
    if ((type === 'petty_expense' || type === 'adjustment') && !reason.trim()) {
      setError("A reason is mandatory for this transaction type.");
      return;
    }

    setLoading(true);
    setError(null);
    try {
      await api.post('/cash/transaction', {
        amount: finalAmount, // Backend applies it blindly
        transactionType: type,
        reason: reason.trim() || undefined
      });
      onTransactionAdded();
      onClose();
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Failed to add transaction.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <AnimatePresence>
      <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/70 backdrop-blur-md">
        <motion.div
          initial={{ opacity: 0, scale: 0.95, y: 10 }}
          animate={{ opacity: 1, scale: 1, y: 0 }}
          exit={{ opacity: 0, scale: 0.95, y: 10 }}
          className="w-full max-w-lg bg-bg-2 border border-border rounded-xl shadow-2xl overflow-hidden flex flex-col"
        >
          {/* Header */}
          <div className="p-4 border-b border-border bg-bg-3 flex justify-between items-center">
            <h2 className="font-heading font-bold text-text uppercase tracking-wider text-lg">
              New Transaction
            </h2>
            <button onClick={onClose} className="p-1 text-text-3 hover:text-text rounded-md transition-colors">
              <X className="w-5 h-5" />
            </button>
          </div>

          <div className="p-5 overflow-y-auto">
            {error && (
              <div className="p-3 mb-4 bg-neon-red/10 border border-neon-red/20 rounded-lg text-neon-red text-xs flex items-start gap-2">
                <AlertTriangle className="w-4 h-4 mt-0.5 shrink-0" />
                <p>{error}</p>
              </div>
            )}

            {/* Type Selector */}
            <div className="grid grid-cols-2 gap-2 mb-6">
              {Object.entries(TX_TYPES).map(([key, config]) => {
                const Icon = config.icon;
                const isSelected = type === key;
                return (
                  <button
                    key={key}
                    onClick={() => { setType(key); setError(null); }}
                    className={`p-3 rounded-lg border text-left transition-all flex items-center gap-2 ${
                      isSelected 
                        ? `bg-bg-3 border-${config.color.replace('text-', '')}/50 shadow-[0_0_10px_rgba(255,255,255,0.05)]` 
                        : 'bg-bg-2 border-border hover:border-text-3'
                    }`}
                  >
                    <Icon className={`w-4 h-4 ${isSelected ? config.color : 'text-text-3'}`} />
                    <span className={`text-xs font-bold uppercase tracking-wider ${isSelected ? 'text-text' : 'text-text-3'}`}>
                      {config.label}
                    </span>
                  </button>
                );
              })}
            </div>

            {/* Adjustment Toggle */}
            {type === 'adjustment' && (
              <div className="flex bg-bg-3 p-1 rounded-md border border-border mb-4">
                <button
                  onClick={() => setIsAdjustmentPositive(true)}
                  className={`flex-1 py-1.5 text-xs font-bold uppercase rounded ${isAdjustmentPositive ? 'bg-neon-blue/20 text-neon-blue' : 'text-text-3 hover:text-text'}`}
                >
                  (+) Cash Added
                </button>
                <button
                  onClick={() => setIsAdjustmentPositive(false)}
                  className={`flex-1 py-1.5 text-xs font-bold uppercase rounded ${!isAdjustmentPositive ? 'bg-neon-red/20 text-neon-red' : 'text-text-3 hover:text-text'}`}
                >
                  (-) Cash Removed
                </button>
              </div>
            )}

            {/* Amount Input (Safety UX: Always positive) */}
            <div className="mb-4">
              <label className="text-xs uppercase tracking-wider font-bold text-text-2 mb-1.5 block">
                Amount (Positive Value Only)
              </label>
              <div className="relative">
                <span className="absolute left-4 top-1/2 -translate-y-1/2 font-mono text-text-3 text-lg">₹</span>
                <input
                  type="number"
                  min="1"
                  placeholder="0.00"
                  value={amountStr}
                  onChange={e => setAmountStr(e.target.value)}
                  className={`w-full bg-bg-3 border text-text font-mono text-2xl rounded-md py-3 pl-10 pr-4 focus:outline-none focus:ring-1 transition-all ${
                    finalAmount < 0 ? 'border-neon-orange focus:border-neon-orange focus:ring-neon-orange' : 'border-neon-blue focus:border-neon-blue focus:ring-neon-blue'
                  }`}
                />
              </div>
            </div>

            {/* Reason */}
            <div className="mb-6">
              <label className="text-xs uppercase tracking-wider font-bold text-text-2 mb-1.5 block">
                Reason / Note <span className="text-neon-red">*</span>
              </label>
              <input
                type="text"
                placeholder="e.g. Printer paper, Milk for cafe"
                value={reason}
                onChange={e => setReason(e.target.value)}
                className="w-full bg-bg-3 border border-border text-text text-sm rounded-md p-3 focus:border-accent outline-none"
              />
            </div>

            {/* Realtime Drawer Preview */}
            <div className="p-4 bg-bg-3 rounded-lg border border-border">
              <div className="text-[10px] text-text-3 uppercase tracking-wider font-bold mb-3 flex items-center gap-1.5">
                <AlertTriangle className="w-3 h-3" /> Drawer Preview
              </div>
              <div className="flex justify-between items-center text-xs mb-1">
                <span className="text-text-2">Current Drawer:</span>
                <span className="font-mono text-text">₹{register.expectedDrawerCash}</span>
              </div>
              <div className="flex justify-between items-center text-xs mb-2">
                <span className="text-text-2">Transaction:</span>
                <span className={`font-mono font-bold ${finalAmount < 0 ? 'text-neon-orange' : 'text-neon-blue'}`}>
                  {finalAmount < 0 ? '-' : '+'}₹{Math.abs(finalAmount)}
                </span>
              </div>
              <div className="flex justify-between items-center pt-2 border-t border-border-2">
                <span className="text-text font-bold uppercase text-xs tracking-wider">New Expected Drawer:</span>
                <span className={`font-mono font-bold text-lg ${newExpectedDrawer < 0 ? 'text-neon-red' : 'text-accent'}`}>
                  ₹{newExpectedDrawer}
                </span>
              </div>
            </div>

          </div>

          {/* Footer */}
          <div className="p-4 border-t border-border bg-bg-3">
            <button
              onClick={handleSubmit}
              disabled={loading || amount <= 0 || !reason.trim()}
              className={`w-full py-3.5 rounded-lg text-sm font-bold uppercase tracking-wider flex items-center justify-center gap-2 transition-all ${
                amount > 0 && reason.trim() && !loading
                  ? 'bg-accent/10 border border-accent text-accent hover:bg-accent/20' 
                  : 'bg-bg-2 border border-border text-text-3 cursor-not-allowed'
              }`}
            >
              {loading ? (
                <div className="w-5 h-5 rounded-full border-2 border-current border-t-transparent animate-spin" />
              ) : (
                <>✓ Add Entry</>
              )}
            </button>
          </div>
        </motion.div>
      </div>
    </AnimatePresence>
  );
}
