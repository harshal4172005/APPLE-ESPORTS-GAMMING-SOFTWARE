// ═══════════════════════════════════════════════════════════
// Gaming Café ERP — Shift Start Modal
// SOP §6.3: Operator shift start → cash register + inventory check
// Shown immediately after operator login — blocks entry until complete
// ═══════════════════════════════════════════════════════════

import { useState, useEffect } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import {
  Banknote, Package, CheckCircle2, AlertTriangle,
  ChevronRight, Loader2, ArrowRight, Box, ClipboardCheck
} from 'lucide-react';
import api from '../../config/api';

const STEPS = {
  CASH: 'cash',
  INVENTORY: 'inventory',
  DONE: 'done',
};

export default function ShiftStartModal({ onComplete }) {
  const [step, setStep] = useState(STEPS.CASH);

  // ── Cash Step ──
  const [openingBalance, setOpeningBalance] = useState('');
  const [cashLoading, setCashLoading] = useState(false);
  const [cashError, setCashError] = useState(null);
  const [cashAlreadyOpen, setCashAlreadyOpen] = useState(false);

  // Set only when the operator's own count disagreed with what the last shift left and no
  // reason has been given yet — the server refused to open the drawer and handed the difference
  // back instead. Cleared once a reason is submitted and the drawer actually opens.
  const [mismatch, setMismatch] = useState(null);
  const [reason, setReason] = useState('');

  // Which of the two opening questions this operator gets. A branch has one drawer and it runs
  // through the trading day: only the first shift puts money in, and every shift after it
  // inherits what the last one left. Asking a later shift for a float and then throwing the
  // answer away — which is exactly what the server does, correctly — had operators typing a
  // figure that quietly meant nothing.
  const [opening, setOpening] = useState(null);

  // ── Inventory Step ──
  const [inventory, setInventory] = useState([]);
  const [stockUpdates, setStockUpdates] = useState({});
  const [invLoading, setInvLoading] = useState(false);
  const [invFetching, setInvFetching] = useState(false);
  const [invError, setInvError] = useState(null);

  // Ask the server what to ask: a float, or nothing at all because the drawer carries over.
  useEffect(() => {
    const checkRegister = async () => {
      try {
        const { data } = await api.get('/cash/opening');
        const result = data.data;
        setOpening(result);
        if (result?.isFirstOfDay && result?.defaultOpeningFloat != null) {
          setOpeningBalance(String(result.defaultOpeningFloat));
        }
        if (result?.alreadyOpen) {
          // A drawer is already open for today — a refresh mid-shift, or a re-login. Nothing to
          // decide, so this step is done.
          setCashAlreadyOpen(true);
          setStep(STEPS.INVENTORY);
        }
      } catch {
        // Unreachable for any reason: fall back to asking for a float. The server still refuses
        // to open a second drawer for a day that already has one, so the worst case is a
        // question that did not need asking, not a duplicate register.
        setOpening({ isFirstOfDay: true, inheritedBalance: 0 });
      }
    };
    checkRegister();
  }, []);

  // Fetch inventory when on inventory step
  useEffect(() => {
    if (step !== STEPS.INVENTORY) return;
    const fetchInventory = async () => {
      setInvFetching(true);
      setInvError(null);
      try {
        const { data } = await api.get('/inventory');
        const items = data.data || data || [];
        setInventory(items);
        // Initialize updates with current stock
        const initUpdates = {};
        items.forEach(item => {
          initUpdates[item.id] = item.currentStock ?? item.CurrentStock ?? '';
        });
        setStockUpdates(initUpdates);
      } catch (err) {
        setInvError(err.response?.data?.error || 'Failed to load inventory.');
      } finally {
        setInvFetching(false);
      }
    };
    fetchInventory();
  }, [step]);

  // Until the server answers, neither question is asked. Defaulting to the float would flash the
  // wrong one on screen and invite an operator to start typing into a box about to disappear.
  const isFirstOfDay = opening?.isFirstOfDay === true;

  // ── Step 1: Open Cash Register ──
  // Always the operator's own count — never a figure the server or a previous shift supplied.
  // The first attempt is sent with no reason; if it disagrees with what was expected, the server
  // refuses to open the drawer and hands the difference back instead (see `mismatch` below), and
  // the second attempt carries the operator's explanation.
  const handleOpenCash = async () => {
    const amount = Number(openingBalance);
    if (isNaN(amount) || amount < 0) {
      setCashError('Please enter a valid opening balance (0 or greater).');
      return;
    }
    if (mismatch && !reason.trim()) {
      setCashError('Please explain the difference before continuing.');
      return;
    }
    setCashLoading(true);
    setCashError(null);
    try {
      const { data } = await api.post('/cash/open', {
        openingBalance: amount,
        reason: mismatch ? reason.trim() : undefined,
      });
      const result = data.data;
      if (result?.opened === false) {
        setMismatch({
          expected: result.expectedBalance,
          counted: result.countedBalance,
          difference: result.difference,
        });
        return;
      }
      setStep(STEPS.INVENTORY);
    } catch (err) {
      const msg = err.response?.data?.error || err.response?.data?.message || '';
      if (msg.toLowerCase().includes('already') || err.response?.status === 409) {
        // Register already open — skip to inventory
        setCashAlreadyOpen(true);
        setStep(STEPS.INVENTORY);
      } else {
        setCashError(msg || 'Failed to open cash register.');
      }
    } finally {
      setCashLoading(false);
    }
  };

  // Back out of the "explain the difference" screen to recount instead of explaining.
  const handleRecount = () => {
    setMismatch(null);
    setReason('');
    setCashError(null);
  };

  // ── Step 2: Confirm Inventory Stocks ──
  const handleConfirmInventory = async () => {
    setInvLoading(true);
    setInvError(null);
    try {
      // Update each item stock if changed
      const updates = inventory.map(item => ({
        id: item.id,
        currentStock: Number(stockUpdates[item.id] ?? item.currentStock ?? item.CurrentStock ?? 0),
      }));

      // The whole opening count, saved as one record (who counted, every item) - shown under
      // the register's note breakdown in the Cash Register history.
      if (updates.length > 0) {
        await api.post('/inventory/stock-count', {
          kind: 'opening',
          counts: updates.map(u => ({ inventoryId: u.id, counted: u.currentStock })),
        });
      }
      setStep(STEPS.DONE);
    } catch (err) {
      setInvError(err.response?.data?.error || 'Failed to update inventory. You can still proceed.');
    } finally {
      setInvLoading(false);
    }
  };


  const handleDone = () => {
    onComplete();
  };

  const getStepNum = () => {
    if (step === STEPS.CASH) return 1;
    if (step === STEPS.INVENTORY) return 2;
    return 3;
  };

  return (
    <div className="fixed inset-0 z-[100] flex items-center justify-center p-4 bg-black/80 backdrop-blur-lg">
      <motion.div
        initial={{ opacity: 0, scale: 0.92, y: 20 }}
        animate={{ opacity: 1, scale: 1, y: 0 }}
        transition={{ duration: 0.35, ease: 'easeOut' }}
        className="w-full max-w-lg bg-bg-2 border border-border rounded-2xl shadow-2xl shadow-black/60 overflow-hidden flex flex-col"
        style={{ maxHeight: '90vh' }}
      >
        {/* Header */}
        <div className="px-6 pt-6 pb-4 border-b border-border bg-bg-3 flex-shrink-0">
          <div className="flex items-center gap-3 mb-3">
            <div className="w-10 h-10 bg-accent/10 rounded-xl flex items-center justify-center border border-accent/30">
              <ClipboardCheck className="w-5 h-5 text-accent" />
            </div>
            <div>
              <h2 className="font-heading font-bold text-text text-lg tracking-wide">Shift Start Checklist</h2>
              <p className="text-[11px] text-text-3 font-mono">Complete all steps before entering the system</p>
            </div>
          </div>

          {/* Step Progress */}
          <div className="flex items-center gap-2">
            {[
              { num: 1, label: 'Cash Register', icon: Banknote },
              { num: 2, label: 'Inventory', icon: Package },
              { num: 3, label: 'Ready', icon: CheckCircle2 },
            ].map((s, idx) => (
              <div key={s.num} className="flex items-center gap-2 flex-1">
                <div className={`flex items-center gap-1.5 px-2.5 py-1 rounded-full text-[10px] font-bold uppercase tracking-wider transition-all ${
                  getStepNum() === s.num
                    ? 'bg-accent/15 border border-accent text-accent'
                    : getStepNum() > s.num
                    ? 'bg-neon-green/10 border border-neon-green/50 text-neon-green'
                    : 'bg-bg border border-border-2 text-text-3'
                }`}>
                  {getStepNum() > s.num ? (
                    <CheckCircle2 className="w-3 h-3" />
                  ) : (
                    <s.icon className="w-3 h-3" />
                  )}
                  <span className="hidden sm:inline">{s.label}</span>
                  <span className="sm:hidden">{s.num}</span>
                </div>
                {idx < 2 && <ChevronRight className="w-3 h-3 text-border flex-shrink-0" />}
              </div>
            ))}
          </div>
        </div>

        {/* Body */}
        <div className="flex-1 overflow-y-auto">
          <AnimatePresence mode="wait">
            {/* ── STEP 1: Cash Register ── */}
            {step === STEPS.CASH && (
              <motion.div
                key="cash"
                initial={{ opacity: 0, x: -20 }}
                animate={{ opacity: 1, x: 0 }}
                exit={{ opacity: 0, x: 20 }}
                transition={{ duration: 0.2 }}
                className="p-6"
              >
                <div className="flex items-center gap-3 mb-5">
                  <div className={`w-12 h-12 rounded-xl flex items-center justify-center border ${
                    mismatch ? 'bg-neon-orange/10 border-neon-orange/30' : 'bg-neon-green/10 border-neon-green/30'
                  }`}>
                    {mismatch ? (
                      <AlertTriangle className="w-6 h-6 text-neon-orange" />
                    ) : (
                      <Banknote className="w-6 h-6 text-neon-green" />
                    )}
                  </div>
                  <div>
                    <h3 className="font-bold text-text text-base">
                      {mismatch
                        ? "That doesn't match what was expected"
                        : isFirstOfDay ? 'Open the drawer for today' : 'Count the drawer'}
                    </h3>
                    <p className="text-text-3 text-xs">
                      {mismatch
                        ? 'Explain the difference before the drawer opens'
                        : isFirstOfDay
                        ? 'Nobody has opened it yet today — count what you put in and enter it'
                        : 'Count what is physically there and enter it — this gets checked against what the last shift left'}
                    </p>
                  </div>
                </div>

                {cashError && (
                  <div className="p-3 mb-4 bg-neon-red/10 border border-neon-red/20 rounded-lg text-neon-red text-xs flex items-start gap-2">
                    <AlertTriangle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                    <p>{cashError}</p>
                  </div>
                )}

                {opening === null ? (
                  <div className="flex items-center justify-center py-10 gap-3 text-text-3">
                    <Loader2 className="w-6 h-6 animate-spin" />
                    <span className="text-sm">Checking the drawer...</span>
                  </div>
                ) : mismatch ? (
                  <div className="space-y-3 mb-6">
                    <div className="p-4 bg-bg-3 rounded-xl border border-neon-orange/30 space-y-2">
                      <div className="flex items-center justify-between text-sm">
                        <span className="text-text-3">Expected in the drawer</span>
                        <span className="font-mono font-bold text-text">₹{Number(mismatch.expected || 0).toLocaleString('en-IN')}</span>
                      </div>
                      <div className="flex items-center justify-between text-sm">
                        <span className="text-text-3">You counted</span>
                        <span className="font-mono font-bold text-text">₹{Number(mismatch.counted || 0).toLocaleString('en-IN')}</span>
                      </div>
                      <div className="flex items-center justify-between text-sm pt-2 border-t border-border">
                        <span className="text-text-3">{mismatch.difference < 0 ? 'Missing' : 'Extra'}</span>
                        <span className={`font-mono font-bold ${mismatch.difference < 0 ? 'text-neon-red' : 'text-neon-orange'}`}>
                          ₹{Math.abs(Number(mismatch.difference || 0)).toLocaleString('en-IN')}
                        </span>
                      </div>
                    </div>
                    <div className="space-y-2">
                      <label className="text-xs uppercase tracking-wider font-bold text-text-2">
                        Why doesn't it match?
                      </label>
                      <textarea
                        value={reason}
                        onChange={e => setReason(e.target.value)}
                        placeholder="e.g. change was given out of the drawer earlier, or the amount handed over was wrong"
                        rows={3}
                        className="w-full bg-bg-3 border border-border text-text text-sm rounded-xl py-3 px-4 focus:border-accent focus:ring-1 focus:ring-accent transition-all outline-none resize-none"
                        autoFocus
                      />
                    </div>
                    <p className="text-[11px] text-text-3 italic">
                      This gets sent to the owner along with your name and branch. The drawer opens
                      with the amount you actually counted, not the expected figure.
                    </p>
                    <button
                      onClick={handleRecount}
                      className="text-xs text-text-3 hover:text-text underline"
                    >
                      Recount instead
                    </button>
                  </div>
                ) : (
                  <div className="space-y-2 mb-6">
                    {!isFirstOfDay && (
                      <div className="p-3 mb-1 bg-bg-3 rounded-lg border border-border flex items-center justify-between">
                        <span className="text-[11px] text-text-3">Last shift left</span>
                        <span className="font-mono text-sm text-text-2">
                          ₹{Number(opening.inheritedBalance || 0).toLocaleString('en-IN')}
                        </span>
                      </div>
                    )}
                    <label className="text-xs uppercase tracking-wider font-bold text-text-2">
                      {isFirstOfDay ? 'How much are you putting in the drawer?' : 'How much did you count?'}
                    </label>
                    <div className="relative">
                      <span className="absolute left-4 top-1/2 -translate-y-1/2 font-mono text-text-3 text-xl">₹</span>
                      <input
                        type="number"
                        min="0"
                        placeholder="0.00"
                        value={openingBalance}
                        onChange={e => setOpeningBalance(e.target.value)}
                        onKeyDown={e => e.key === 'Enter' && handleOpenCash()}
                        className="w-full bg-bg-3 border border-border text-text font-mono text-2xl rounded-xl py-4 pl-12 pr-4 focus:border-accent focus:ring-1 focus:ring-accent transition-all outline-none"
                        autoFocus
                      />
                    </div>
                    <p className="text-[11px] text-text-3 italic">
                      {isFirstOfDay
                        ? "This branch's fixed float is pre-filled below. Every shift after yours inherits the drawer rather than being asked again — there is one drawer and it runs through the trading day. Change the number only if what you're actually putting in differs."
                        : "Count what's physically in the drawer. If it doesn't match what the last shift left, you'll be asked why."}
                    </p>
                  </div>
                )}

                <button
                  onClick={handleOpenCash}
                  disabled={cashLoading || opening === null || openingBalance === '' || (mismatch && !reason.trim())}
                  className="w-full py-3.5 rounded-xl text-sm font-bold uppercase tracking-wider flex items-center justify-center gap-2 transition-all bg-accent/10 border border-accent text-accent hover:bg-accent/20 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {cashLoading ? (
                    <Loader2 className="w-5 h-5 animate-spin" />
                  ) : mismatch ? (
                    <>
                      <AlertTriangle className="w-4 h-4" />
                      Submit & Open Drawer
                      <ArrowRight className="w-4 h-4" />
                    </>
                  ) : (
                    <>
                      <Banknote className="w-4 h-4" />
                      {isFirstOfDay ? 'Open Shift Register' : 'Take the drawer over'}
                      <ArrowRight className="w-4 h-4" />
                    </>
                  )}
                </button>
              </motion.div>
            )}

            {/* ── STEP 2: Inventory Check ── */}
            {step === STEPS.INVENTORY && (
              <motion.div
                key="inventory"
                initial={{ opacity: 0, x: -20 }}
                animate={{ opacity: 1, x: 0 }}
                exit={{ opacity: 0, x: 20 }}
                transition={{ duration: 0.2 }}
                className="p-6"
              >
                <div className="flex items-center gap-3 mb-4">
                  <div className="w-12 h-12 bg-neon-purple/10 border border-neon-purple/30 rounded-xl flex items-center justify-center">
                    <Package className="w-6 h-6 text-neon-purple" />
                  </div>
                  <div>
                    <h3 className="font-bold text-text text-base">Inventory Stock Check</h3>
                    <p className="text-text-3 text-xs">Verify and update current stock levels before shift begins</p>
                  </div>
                </div>

                {cashAlreadyOpen && (
                  <div className="p-2.5 mb-4 bg-neon-blue/10 border border-neon-blue/20 rounded-lg text-neon-blue text-xs flex items-center gap-2">
                    <CheckCircle2 className="w-3.5 h-3.5 shrink-0" />
                    Cash register already open for this shift.
                  </div>
                )}

                {invError && (
                  <div className="p-3 mb-4 bg-neon-orange/10 border border-neon-orange/20 rounded-lg text-neon-orange text-xs flex items-start gap-2">
                    <AlertTriangle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                    <p>{invError}</p>
                  </div>
                )}

                {invFetching ? (
                  <div className="flex items-center justify-center py-10 gap-3 text-text-3">
                    <Loader2 className="w-6 h-6 animate-spin" />
                    <span className="text-sm">Loading inventory...</span>
                  </div>
                ) : inventory.length === 0 ? (
                  <div className="text-center py-8 text-text-3">
                    <Box className="w-10 h-10 mx-auto mb-3 opacity-40" />
                    <p className="text-sm">No inventory items found for your branch.</p>
                  </div>
                ) : (
                  <div className="space-y-2 mb-5 max-h-64 overflow-y-auto pr-1">
                    {inventory.map(item => {
                      const itemId = item.id;
                      const name = item.itemName || item.ItemName || item.name || 'Item';
                      const category = item.category || item.Category || '';
                      const minStock = item.minStockLimit ?? item.MinStockLimit ?? 0;
                      const currentVal = stockUpdates[itemId] ?? '';
                      const currentNum = Number(currentVal);
                      const isLow = currentVal !== '' && currentNum <= minStock;

                      return (
                        <div key={itemId} className={`flex items-center gap-3 p-3 rounded-lg border transition-all ${
                          isLow ? 'bg-neon-orange/5 border-neon-orange/30' : 'bg-bg-3 border-border'
                        }`}>
                          <div className="flex-1 min-w-0">
                            <div className="flex items-center gap-2">
                              <span className="text-sm font-semibold text-text truncate">{name}</span>
                              {isLow && (
                                <span className="text-[9px] bg-neon-orange/20 text-neon-orange px-1.5 py-0.5 rounded-full font-bold uppercase">
                                  Low Stock
                                </span>
                              )}
                            </div>
                            <div className="text-[10px] text-text-3 font-mono mt-0.5">
                              {category} · Min: {minStock}
                            </div>
                          </div>
                          <div className="flex items-center gap-2">
                            <span className="text-xs text-text-3 hidden sm:block">Qty:</span>
                            <input
                              type="number"
                              min="0"
                              value={currentVal}
                              onChange={e => setStockUpdates(prev => ({ ...prev, [itemId]: e.target.value }))}
                              className={`w-20 bg-bg border text-text font-mono text-sm rounded-lg py-1.5 px-2 text-center focus:outline-none focus:ring-1 transition-all ${
                                isLow
                                  ? 'border-neon-orange/50 focus:border-neon-orange focus:ring-neon-orange/30'
                                  : 'border-border focus:border-accent focus:ring-accent/30'
                              }`}
                            />
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}

                <div className="p-3 mb-4 bg-neon-orange/10 border border-neon-orange/20 rounded-lg text-neon-orange text-xs flex items-start gap-2">
                  <AlertTriangle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                  <p><strong>Mandatory:</strong> All stock quantities must be verified and confirmed before entering the system. This prevents off-record sales.</p>
                </div>
                <button
                  onClick={handleConfirmInventory}
                  disabled={invLoading || invFetching}
                  className="w-full py-3.5 rounded-xl text-sm font-bold uppercase tracking-wider flex items-center justify-center gap-2 transition-all bg-neon-purple/10 border border-neon-purple/50 text-neon-purple hover:bg-neon-purple/20 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {invLoading ? (
                    <Loader2 className="w-5 h-5 animate-spin" />
                  ) : (
                    <>
                      <Package className="w-4 h-4" />
                      Confirm Stock & Enter System
                      <ArrowRight className="w-4 h-4" />
                    </>
                  )}
                </button>
              </motion.div>
            )}

            {/* ── STEP 3: Ready ── */}
            {step === STEPS.DONE && (
              <motion.div
                key="done"
                initial={{ opacity: 0, scale: 0.9 }}
                animate={{ opacity: 1, scale: 1 }}
                transition={{ duration: 0.3, type: 'spring', stiffness: 200 }}
                className="p-8 flex flex-col items-center text-center"
              >
                <motion.div
                  initial={{ scale: 0 }}
                  animate={{ scale: 1 }}
                  transition={{ delay: 0.1, type: 'spring', stiffness: 300 }}
                  className="w-20 h-20 bg-neon-green/10 border-2 border-neon-green/40 rounded-full flex items-center justify-center mb-5"
                >
                  <CheckCircle2 className="w-10 h-10 text-neon-green" />
                </motion.div>

                <h3 className="font-heading font-bold text-text text-2xl mb-2">Shift Started!</h3>
                <p className="text-text-3 text-sm mb-2">
                  Cash register is open and inventory has been verified.
                </p>
                <p className="text-text-3 text-xs font-mono">
                  All systems ready. Have a great shift!
                </p>

                <button
                  onClick={handleDone}
                  className="mt-8 w-full max-w-xs py-3.5 rounded-xl text-sm font-bold uppercase tracking-wider flex items-center justify-center gap-2 bg-neon-green/10 border border-neon-green text-neon-green hover:bg-neon-green/20 transition-all"
                >
                  <CheckCircle2 className="w-4 h-4" />
                  Enter System
                </button>
              </motion.div>
            )}
          </AnimatePresence>
        </div>
      </motion.div>
    </div>
  );
}
