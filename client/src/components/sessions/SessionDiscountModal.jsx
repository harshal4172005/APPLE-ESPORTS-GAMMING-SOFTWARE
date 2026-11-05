import React, { useState } from 'react';
import { createPortal } from 'react-dom';
import { motion, AnimatePresence } from 'framer-motion';
import { X, Tag } from 'lucide-react';
import { useToast } from '../ui/Toast';
import { applyDiscount } from '../../api/billing.api';
import { useAuth } from '../../contexts/AuthContext';

export default function SessionDiscountModal({ isOpen, onClose, pc, onRefresh }) {
  // A typed ₹ amount, not a percent: it stays exactly that amount however long they keep playing.
  const [amount, setAmount] = useState('');
  const [loading, setLoading] = useState(false);
  const toast = useToast();
  const { isTrueSuperAdmin } = useAuth();
  const value = Number(amount);
  const valid = amount !== '' && Number.isFinite(value) && value > 0;

  if (!isOpen || !pc) return null;

  // remove = true clears the discount (₹0).
  const handleApply = async (remove = false) => {
    if (!pc.activeBillId) {
      toast.error('No active bill found for this session.');
      return;
    }

    setLoading(true);
    try {
      const rupees = remove ? 0 : value;
      const result = await applyDiscount(pc.activeBillId, {
        discountType: 'Flat',
        discountValue: rupees,
        reason: `${isTrueSuperAdmin ? 'Super Admin' : 'Admin'} discount ₹${rupees}`,
      });
      toast.success(
        result?.queued
          ? (result.message || 'Sent to the branch. It applies this within a few seconds.')
          : remove ? 'Discount removed.' : `₹${rupees} discount applied.`
      );
      onRefresh?.();
      onClose();
    } catch (err) {
      console.error('Failed to apply discount', err);
      toast.error(err.response?.data?.error || err.response?.data?.message || 'Failed to apply discount');
    } finally {
      setLoading(false);
    }
  };

  return createPortal(
    <AnimatePresence>
      <div className="fixed inset-0 z-[9999] flex items-center justify-center p-4">
        <motion.div
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          className="absolute inset-0 bg-bg/80 backdrop-blur-sm"
          onClick={onClose}
        />
        <motion.div
          initial={{ opacity: 0, scale: 0.95, y: 20 }}
          animate={{ opacity: 1, scale: 1, y: 0 }}
          exit={{ opacity: 0, scale: 0.95, y: 20 }}
          className="relative w-full max-w-md bg-bg-2 border border-border rounded-lg shadow-2xl overflow-hidden"
        >
          {/* Header */}
          <div className="flex items-center justify-between p-4 border-b border-border bg-bg-3">
            <div className="flex items-center gap-2 text-text">
              <Tag className="w-5 h-5 text-neon-orange" />
              <h2 className="font-heading font-bold tracking-widest text-lg">APPLY DISCOUNT</h2>
            </div>
            <button onClick={onClose} className="p-1 hover:bg-bg/50 rounded-lg transition-colors text-text-3 hover:text-text">
              <X className="w-5 h-5" />
            </button>
          </div>

          <div className="p-6 space-y-5">
            <div className="text-sm text-text-2">
              Apply a discount to the active session on <span className="text-neon-orange font-bold font-mono">{pc.name}</span>.
            </div>

            {/* Current bill total */}
            <div className="flex items-center justify-between bg-bg rounded-lg border border-border px-4 py-3">
              <span className="text-xs text-text-3 uppercase tracking-widest font-bold">Current Bill</span>
              <span className="font-mono font-bold text-lg text-text">₹{Number(pc.totalAmount || 0).toFixed(0)}</span>
            </div>

            <label className="block">
              <span className="text-xs text-text-3 uppercase tracking-wider font-bold">Discount amount (₹)</span>
              <div className="mt-1.5 flex items-center gap-2 bg-bg border border-border rounded-lg px-3 focus-within:border-neon-orange transition-colors">
                <span className="text-neon-orange font-mono font-bold">₹</span>
                <input
                  type="number"
                  min="1"
                  step="1"
                  inputMode="numeric"
                  autoFocus
                  value={amount}
                  onChange={(e) => setAmount(e.target.value)}
                  onKeyDown={(e) => { if (e.key === 'Enter' && valid && !loading) handleApply(); }}
                  placeholder="e.g. 40"
                  className="w-full bg-transparent py-2.5 font-mono text-lg text-text outline-none"
                />
              </div>
              <span className="block mt-1.5 text-[11px] text-text-3">
                The customer pays this much less. It stays the same even if they keep playing.
              </span>
            </label>

            {valid && (
              <div className="flex items-center justify-between text-xs px-1">
                <span className="text-text-3">Customer pays</span>
                <span className="font-mono font-bold text-neon-green">₹{Math.max(0, Number(pc.totalAmount || 0) - value).toFixed(0)}</span>
              </div>
            )}

            <div className="flex gap-2">
              <button
                onClick={() => handleApply(true)}
                disabled={loading}
                className="flex-1 py-2.5 rounded-lg border border-border text-text-3 text-xs font-bold tracking-widest hover:bg-bg-3 disabled:opacity-50 uppercase transition-colors"
              >
                Remove
              </button>
              <button
                onClick={() => handleApply()}
                disabled={loading || !valid}
                className="flex-[2] py-2.5 rounded-lg border border-neon-orange/50 bg-neon-orange/10 text-neon-orange font-bold tracking-widest hover:bg-neon-orange/20 transition-all disabled:opacity-50 disabled:cursor-not-allowed uppercase"
              >
                {loading ? 'APPLYING...' : valid ? `GIVE ₹${value} OFF` : 'APPLY DISCOUNT'}
              </button>
            </div>
          </div>
        </motion.div>
      </div>
    </AnimatePresence>,
    document.body
  );
}
